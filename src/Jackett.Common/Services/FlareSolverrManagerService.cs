using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jackett.Common.Models.Config;
using Jackett.Common.Services.Interfaces;
using Newtonsoft.Json.Linq;
using NLog;

namespace Jackett.Common.Services.Interfaces
{
    public interface IFlareSolverrManagerService : IDisposable
    {
        bool IsRunning { get; }
        string FlareSolverrUrl { get; }
        void Start();
        void Stop();
        Task EnsureReadyAsync();
        Task CheckForUpdateAsync();
    }
}

namespace Jackett.Common.Services
{
    public class FlareSolverrManagerService : IFlareSolverrManagerService
    {
        // First launch downloads/patches undetected_chromedriver and boots a real Chrome, which can be slow.
        private const int ReadyTimeoutSeconds = 90;

        // Portable Chromium snapshot FlareSolverr's build script bundles (build_package.py). Downloading
        // it gives a self-contained, headless browser so no system Chrome install is required.
        private const string ChromiumSnapshotRevision = "1522586";

        private static readonly HttpClient _healthClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        private readonly Logger _logger;
        private readonly IConfigurationService _configurationService;
        private readonly ServerConfig _serverConfig;
        private readonly SemaphoreSlim _startLock = new SemaphoreSlim(1, 1);

        private Process _process;
        private string _executablePath;
        private string _bundledChromePath;
        private volatile bool _ready;
        private readonly StringBuilder _lastLogs = new StringBuilder();

        public bool IsRunning => _process != null && !_process.HasExited;
        public string FlareSolverrUrl => "http://127.0.0.1:8191";

        public FlareSolverrManagerService(IConfigurationService configurationService, ServerConfig serverConfig)
        {
            _logger = LogManager.GetCurrentClassLogger();
            _configurationService = configurationService;
            _serverConfig = serverConfig;
        }

        public void Start()
        {
            // Warm up in the background so the first indexer request doesn't pay the download/boot cost.
            // Failures here are non-fatal: EnsureReadyAsync will retry lazily on demand.
            Task.Run(async () =>
            {
                try
                {
                    await EnsureReadyAsync();
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "FlareSolverr warm-up failed; will retry on demand.");
                }
            });
        }

        public void Stop()
        {
            _logger.Info("Stopping embedded FlareSolverr manager...");
            StopProcess();
        }

        /// <summary>
        /// Ensures a FlareSolverr instance is reachable at <see cref="FlareSolverrUrl"/>.
        /// Self-healing: if the endpoint answers (even one we didn't launch) we're done; otherwise we
        /// (re)launch the managed process and poll until it responds. Failures are never cached, so a
        /// later call always gets a fresh attempt instead of replaying an old error forever.
        /// </summary>
        public async Task EnsureReadyAsync()
        {
            // Fast path: our own process is alive and was already confirmed healthy.
            if (_ready && IsRunning)
                return;

            if (await IsEndpointHealthyAsync())
            {
                _ready = true;
                return;
            }

            await _startLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // Re-check inside the lock in case another caller just brought it up.
                if (await IsEndpointHealthyAsync())
                {
                    _ready = true;
                    return;
                }

                _ready = false;
                await EnsureFlareSolverrDownloaded().ConfigureAwait(false);
                await EnsureBundledChromiumAsync().ConfigureAwait(false);
                StartFlareSolverrProcess();

                var deadline = DateTime.UtcNow.AddSeconds(ReadyTimeoutSeconds);
                while (DateTime.UtcNow < deadline)
                {
                    if (await IsEndpointHealthyAsync())
                    {
                        _ready = true;
                        _logger.Info("FlareSolverr is ready.");
                        return;
                    }

                    if (_process != null && _process.HasExited)
                    {
                        // Let the async output readers flush before we read the captured logs.
                        try { _process.WaitForExit(2000); } catch { }
                        var exitCode = SafeExitCode();
                        var logs = _lastLogs.ToString().Trim();
                        throw new Exception(
                            $"FlareSolverr process exited prematurely (exit code {exitCode})." +
                            (string.IsNullOrEmpty(logs) ? " No output was captured." : $" Output:{Environment.NewLine}{logs}"));
                    }

                    await Task.Delay(1000).ConfigureAwait(false);
                }

                var timeoutLogs = _lastLogs.ToString().Trim();
                throw new Exception(
                    $"FlareSolverr did not become ready within {ReadyTimeoutSeconds} seconds." +
                    (string.IsNullOrEmpty(timeoutLogs) ? "" : $" Output:{Environment.NewLine}{timeoutLogs}"));
            }
            finally
            {
                _startLock.Release();
            }
        }

        private async Task<bool> IsEndpointHealthyAsync()
        {
            try
            {
                using (var resp = await _healthClient.GetAsync(FlareSolverrUrl + "/").ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode)
                        return false;
                    var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return body.IndexOf("FlareSolverr", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private int SafeExitCode()
        {
            try { return _process?.ExitCode ?? -1; }
            catch { return -1; }
        }

        private async Task EnsureFlareSolverrDownloaded()
        {
            string installDir = Path.Combine(_configurationService.GetAppDataFolder(), "FlareSolverr");
            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            string exeName = isWindows ? "flaresolverr.exe" : "flaresolverr";

            _executablePath = Path.Combine(installDir, "flaresolverr", exeName);

            if (File.Exists(_executablePath))
            {
                _logger.Info($"Found existing FlareSolverr at {_executablePath}");
                return;
            }

            _logger.Info("FlareSolverr not found locally. Downloading latest release from smeinecke/FlareSolverr...");
            Directory.CreateDirectory(installDir);

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Jackett-FlareSolverrManager");

            var response = await httpClient.GetStringAsync("https://api.github.com/repos/smeinecke/FlareSolverr/releases/latest");
            var release = JObject.Parse(response);
            var releaseTag = release["tag_name"]?.ToString();

            string targetAssetName = isWindows ? "flaresolverr_windows_x64.zip" : "flaresolverr_linux_x64.tar.gz";
            var asset = release["assets"]?.FirstOrDefault(a => a["name"]?.ToString() == targetAssetName);

            if (asset == null)
                throw new Exception($"Could not find release asset matching {targetAssetName}");

            string downloadUrl = asset["browser_download_url"].ToString();
            string tempFilePath = Path.Combine(installDir, targetAssetName);

            _logger.Info($"Downloading {downloadUrl}...");
            using (var responseStream = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                responseStream.EnsureSuccessStatusCode();
                using (var fileStream = File.Create(tempFilePath))
                using (var httpStream = await responseStream.Content.ReadAsStreamAsync())
                {
                    await httpStream.CopyToAsync(fileStream);
                }
            }

            _logger.Info($"Extracting {tempFilePath}...");
            if (isWindows)
            {
                ZipFile.ExtractToDirectory(tempFilePath, installDir, true);
            }
            else
            {
                var proc = Process.Start(new ProcessStartInfo("tar", $"-xzf {tempFilePath} -C {installDir}") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
                proc.WaitForExit();
            }

            File.Delete(tempFilePath);

            if (!File.Exists(_executablePath))
                throw new Exception($"Executable not found at expected path {_executablePath} after extraction.");

            if (!isWindows)
            {
                Process.Start(new ProcessStartInfo("chmod", $"+x {_executablePath}") { UseShellExecute = false })?.WaitForExit();
            }

            if (!string.IsNullOrEmpty(releaseTag))
            {
                try { File.WriteAllText(Path.Combine(installDir, "fs-version.txt"), releaseTag); } catch { }
            }

            _logger.Info($"FlareSolverr {releaseTag} installed successfully.");
        }

        private string InstallDir => Path.Combine(_configurationService.GetAppDataFolder(), "FlareSolverr");

        private string ReadInstalledFlareSolverrVersion()
        {
            try
            {
                var marker = Path.Combine(InstallDir, "fs-version.txt");
                return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Checks smeinecke/FlareSolverr for a newer release than the installed one and, if found,
        /// stops FlareSolverr, removes the old install, and re-downloads + restarts it. Called from the
        /// Jackett "Check for updates" flow so one button updates both Jackett and FlareSolverr.
        /// </summary>
        public async Task CheckForUpdateAsync()
        {
            try
            {
                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                httpClient.DefaultRequestHeaders.Add("User-Agent", "Jackett-FlareSolverrManager");
                var response = await httpClient.GetStringAsync("https://api.github.com/repos/smeinecke/FlareSolverr/releases/latest").ConfigureAwait(false);
                var latestTag = JObject.Parse(response)["tag_name"]?.ToString();
                var installedTag = ReadInstalledFlareSolverrVersion();

                if (string.IsNullOrEmpty(latestTag) || string.IsNullOrEmpty(installedTag))
                {
                    _logger.Debug($"FlareSolverr update check skipped (installed='{installedTag}', latest='{latestTag}').");
                    return;
                }
                if (latestTag == installedTag)
                {
                    _logger.Info($"FlareSolverr is up to date ({installedTag}).");
                    return;
                }

                _logger.Info($"FlareSolverr update available: {installedTag} -> {latestTag}. Updating...");
                await _startLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    StopProcess();
                    // Give Windows a moment to release file handles before deleting.
                    await Task.Delay(1500).ConfigureAwait(false);
                    var fsFolder = Path.Combine(InstallDir, "flaresolverr");
                    SafeDeleteDirectory(fsFolder);
                    _bundledChromePath = null;
                    _ready = false;
                }
                finally
                {
                    _startLock.Release();
                }

                await EnsureReadyAsync().ConfigureAwait(false);
                _logger.Info($"FlareSolverr updated to {latestTag}.");
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "FlareSolverr update check/update failed.");
            }
        }

        private void SafeDeleteDirectory(string dir)
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (Directory.Exists(dir))
                        Directory.Delete(dir, true);
                    return;
                }
                catch (Exception ex)
                {
                    if (attempt == 4)
                        throw;
                    _logger.Debug($"Retry deleting '{dir}' ({ex.Message})...");
                    Thread.Sleep(1000);
                }
            }
        }

        /// <summary>
        /// Ensures a self-contained headless browser is available so no system Chrome is required.
        /// FlareSolverr 3.7.0+ already bundles a Chromium (at _internal/chrome) - we use that as-is.
        /// Older builds don't, so we download the portable Chromium snapshot once. Either way
        /// <see cref="_bundledChromePath"/> points at the browser and <see cref="ResolveBrowserExecutable"/>
        /// prefers it. Best-effort: on failure FlareSolverr falls back to system Chrome detection.
        /// </summary>
        private async Task EnsureBundledChromiumAsync()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;

            // Prefer a Chromium that FlareSolverr already ships with (3.7.0+), avoiding a second download.
            if (!string.IsNullOrEmpty(_executablePath))
            {
                var fsBundled = Path.Combine(Path.GetDirectoryName(_executablePath), "_internal", "chrome", "chrome.exe");
                if (File.Exists(fsBundled))
                {
                    _bundledChromePath = fsBundled;
                    _logger.Info($"Using FlareSolverr's bundled Chromium at {fsBundled}");
                    return;
                }
            }

            var installDir = InstallDir;
            var chromiumDir = Path.Combine(installDir, "chromium");
            var chromeExe = Path.Combine(chromiumDir, "chrome-win", "chrome.exe");

            if (File.Exists(chromeExe))
            {
                _bundledChromePath = chromeExe;
                return;
            }

            try
            {
                _logger.Info($"Downloading portable Chromium (snapshot {ChromiumSnapshotRevision}, ~290 MB) for FlareSolverr. This is a one-time download...");
                Directory.CreateDirectory(chromiumDir);

                var url = $"https://commondatastorage.googleapis.com/chromium-browser-snapshots/Win_x64/{ChromiumSnapshotRevision}/chrome-win.zip";
                var tempZip = Path.Combine(installDir, "chrome-win.zip");

                using (var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(20) })
                using (var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    using (var fileStream = File.Create(tempZip))
                    using (var httpStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    {
                        await httpStream.CopyToAsync(fileStream).ConfigureAwait(false);
                    }
                }

                _logger.Info("Extracting portable Chromium...");
                ZipFile.ExtractToDirectory(tempZip, chromiumDir, true);
                File.Delete(tempZip);

                if (File.Exists(chromeExe))
                {
                    _bundledChromePath = chromeExe;
                    _logger.Info($"Portable Chromium ready at {chromeExe}");
                }
                else
                {
                    _logger.Warn("Portable Chromium archive did not contain chrome.exe at the expected path; FlareSolverr will fall back to system Chrome.");
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to download portable Chromium; FlareSolverr will fall back to system Chrome detection.");
            }
        }

        private void StartFlareSolverrProcess()
        {
            if (_process != null && !_process.HasExited)
                return;

            // Clean up a dead process handle from a previous attempt.
            if (_process != null)
            {
                try { _process.Dispose(); } catch { }
                _process = null;
            }

            _logger.Info($"Starting FlareSolverr process: {_executablePath}");

            // Fix FlareSolverr 3.6.6 Windows bug: it always loads its proxy_extension into Chrome
            // (--load-extension), but the Windows build ships that folder EMPTY. Chrome then fails
            // to load the extension ("manifest missing" dialog) and the browser hangs / challenge
            // times out. Write the real extension files if they're absent.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var proxyExtensionDir = Path.Combine(Path.GetDirectoryName(_executablePath), "_internal", "flaresolverr", "proxy_extension");
                EnsureProxyExtensionFiles(proxyExtensionDir);

                // FlareSolverr checks a bundled browser at _internal/flaresolverr/chrome/chrome.exe
                // FIRST, before its own detection. This lets an explicit override (JACKETT_CHROME_PATH
                // / chrome_path.txt) point FlareSolverr at a specific Chrome/Chromium build.
                EnsureBrowserLinked();
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = _executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(_executablePath)
            };

            // The child inherits Jackett's environment. If Jackett was launched with a stripped/wrong
            // environment (e.g. bad LOCALAPPDATA), FlareSolverr's own Chrome detection and
            // undetected_chromedriver's driver cache break. Rewrite the profile-derived vars from the
            // known-folder API so the child always gets correct paths.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                void SetFolder(string key, Environment.SpecialFolder folder)
                {
                    var value = Environment.GetFolderPath(folder);
                    if (!string.IsNullOrEmpty(value))
                        startInfo.EnvironmentVariables[key] = value;
                }

                SetFolder("LOCALAPPDATA", Environment.SpecialFolder.LocalApplicationData);
                SetFolder("APPDATA", Environment.SpecialFolder.ApplicationData);
                SetFolder("USERPROFILE", Environment.SpecialFolder.UserProfile);

                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(localAppData))
                {
                    var temp = Path.Combine(localAppData, "Temp");
                    try { Directory.CreateDirectory(temp); } catch { }
                    startInfo.EnvironmentVariables["TEMP"] = temp;
                    startInfo.EnvironmentVariables["TMP"] = temp;
                }
            }

            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            lock (_lastLogs)
                _lastLogs.Clear();

            _process.OutputDataReceived += (s, e) =>
            {
                if (e.Data == null)
                    return;
                _logger.Debug($"[FS] {e.Data}");
                AppendLog(e.Data);
            };

            _process.ErrorDataReceived += (s, e) =>
            {
                if (e.Data == null)
                    return;
                _logger.Warn($"[FS] {e.Data}");
                AppendLog(e.Data);
            };

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        private void AppendLog(string line)
        {
            lock (_lastLogs)
            {
                // Keep the buffer bounded so a long-running instance doesn't grow it forever.
                if (_lastLogs.Length > 8000)
                    _lastLogs.Remove(0, _lastLogs.Length - 4000);
                _lastLogs.AppendLine(line);
            }
        }

        /// <summary>
        /// Links a resolved Chrome/Chromium into the location FlareSolverr probes first
        /// (<c>_internal/flaresolverr/chrome/chrome.exe</c>) using a directory junction, so the
        /// bundled browser wins over FlareSolverr's account-scoped auto-detection. No-op if we
        /// can't resolve a browser (FlareSolverr then falls back to its own detection).
        /// </summary>
        private void EnsureBrowserLinked()
        {
            try
            {
                var linkDir = Path.Combine(Path.GetDirectoryName(_executablePath), "_internal", "flaresolverr", "chrome");
                var linkedExe = Path.Combine(linkDir, "chrome.exe");

                var browserExe = ResolveBrowserExecutable();
                if (string.IsNullOrEmpty(browserExe))
                {
                    // Nothing resolved: leave any existing link so a previous one keeps working.
                    if (!File.Exists(linkedExe))
                        _logger.Info("Jackett could not resolve a Chrome/Chromium binary; FlareSolverr will use its own detection. " +
                                     "If it reports 'Chrome not installed', set JACKETT_CHROME_PATH or create a chrome_path.txt (see logs).");
                    return;
                }

                var browserDir = Path.GetDirectoryName(browserExe);
                if (!File.Exists(Path.Combine(browserDir, "chrome.exe")))
                {
                    _logger.Warn($"Resolved browser '{browserExe}' but its folder has no chrome.exe; skipping browser link.");
                    return;
                }

                if (Directory.Exists(linkDir))
                {
                    // Only our own junction gets replaced (so we can re-point it when the resolved
                    // browser changes). A real directory that already has chrome.exe is left untouched.
                    var isJunction = (File.GetAttributes(linkDir) & FileAttributes.ReparsePoint) != 0;
                    if (!isJunction && File.Exists(linkedExe))
                    {
                        _logger.Debug($"A real bundled browser already exists at {linkDir}; leaving it.");
                        return;
                    }
                    try { Directory.Delete(linkDir, false); } catch { }
                }

                _logger.Info($"Linking FlareSolverr browser -> {browserDir}");
                var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkDir}\" \"{browserDir}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var proc = Process.Start(psi))
                {
                    proc.WaitForExit(5000);
                }

                if (!File.Exists(linkedExe))
                    _logger.Warn("Failed to create the FlareSolverr browser junction; FlareSolverr will fall back to its own detection.");
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Could not link a browser for FlareSolverr; falling back to its own detection.");
            }
        }

        /// <summary>
        /// Resolves a Chrome/Chromium executable, in priority order:
        /// 1. JACKETT_CHROME_PATH environment variable (full path to chrome.exe);
        /// 2. a chrome_path.txt override file in the Jackett app-data folder (single line, full path);
        /// 3. the portable Chromium we downloaded (<see cref="EnsureBundledChromiumAsync"/>) - the default;
        /// 4. standard Chrome/Chromium install directories.
        /// Folder roots come from <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/>
        /// (the profile/known-folder API) rather than %LOCALAPPDATA%/%PROGRAMFILES% env vars, because
        /// Jackett can be launched with a stripped environment where those vars are wrong/missing.
        /// </summary>
        private string ResolveBrowserExecutable()
        {
            var env = Environment.GetEnvironmentVariable("JACKETT_CHROME_PATH");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env.Trim('"')))
                return env.Trim('"');

            foreach (var overrideFile in new[]
                     {
                         Path.Combine(_configurationService.GetAppDataFolder(), "chrome_path.txt"),
                         Path.Combine(_configurationService.GetAppDataFolder(), "FlareSolverr", "chrome_path.txt")
                     })
            {
                try
                {
                    if (File.Exists(overrideFile))
                    {
                        var p = File.ReadAllText(overrideFile).Trim().Trim('"');
                        if (!string.IsNullOrWhiteSpace(p) && File.Exists(p))
                            return p;
                    }
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(_bundledChromePath) && File.Exists(_bundledChromePath))
                return _bundledChromePath;

            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };
            var relPaths = new[]
            {
                @"Google\Chrome\Application\chrome.exe",
                @"Chromium\Application\chrome.exe"
            };
            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root))
                    continue;
                foreach (var rel in relPaths)
                {
                    var candidate = Path.Combine(root, rel);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }

            return null;
        }

        /// <summary>
        /// Ensures the FlareSolverr proxy_extension directory contains its files. FlareSolverr copies
        /// this folder into a temp dir and loads it as a Chrome extension on every request; the Windows
        /// build ships it empty, which makes Chrome fail to load the extension and hang. Only writes when
        /// manifest.json is missing, so a future build that bundles the files properly is left untouched.
        /// </summary>
        private void EnsureProxyExtensionFiles(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var manifestPath = Path.Combine(dir, "manifest.json");
                if (File.Exists(manifestPath))
                    return;

                _logger.Info("Writing FlareSolverr proxy_extension files (missing in the Windows build).");
                File.WriteAllText(manifestPath, ProxyExtensionManifest);
                File.WriteAllText(Path.Combine(dir, "background.js"), ProxyExtensionBackground);
                File.WriteAllText(Path.Combine(dir, "proxy.html"), ProxyExtensionProxyHtml);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to write FlareSolverr proxy_extension files.");
            }
        }

        // Verbatim copies of FlareSolverr 3.6.6's src/flaresolverr/proxy_extension/* files.
        private const string ProxyExtensionManifest = @"{
    ""version"": ""1.0.0"",
    ""manifest_version"": 3,
    ""name"": ""FlareSolverr Proxy Manager"",
    ""permissions"": [
        ""proxy"",
        ""storage"",
        ""webRequest"",
        ""webRequestAuthProvider""
    ],
    ""host_permissions"": [
        ""<all_urls>""
    ],
    ""background"": {
        ""service_worker"": ""background.js""
    }
}
";

        private const string ProxyExtensionBackground = @"// Background service worker to manage Chrome proxy settings dynamically.
// Supports both authenticated and unauthenticated proxies via chrome.proxy API.

var FLARESOLVERR_PROXY_KEY = ""flaresolverrProxy"";
var currentAuth = null;

/**
 * Apply proxy settings to Chrome.
 * @param {Object} proxyConfig - The proxy configuration object.
 */
function applyProxyConfig(proxyConfig, callback) {
    chrome.proxy.settings.set(
        { value: proxyConfig, scope: ""regular"" },
        function() {
            if (chrome.runtime.lastError) {
                callback({ success: false, error: chrome.runtime.lastError.message });
            } else {
                callback({ success: true });
            }
        }
    );
}

/**
 * Restore proxy config from storage on startup.
 */
function restoreProxyFromStorage() {
    chrome.storage.local.get([FLARESOLVERR_PROXY_KEY, ""flaresolverrProxyAuth""], function(result) {
        var config = result[FLARESOLVERR_PROXY_KEY];
        currentAuth = result.flaresolverrProxyAuth || null;
        if (config) {
            applyProxyConfig(config, function() {});
        } else {
            // Default to direct (no proxy)
            applyProxyConfig({ mode: ""direct"" }, function() {});
        }
    });
}

// Restore on startup
restoreProxyFromStorage();

// Handle messages from content script or extension pages
chrome.runtime.onMessage.addListener(function(request, sender, sendResponse) {
    if (!request || !request.mode) {
        sendResponse({ success: false, error: ""Missing mode"" });
        return false;
    }

    try {
        if (request.mode === ""direct"") {
            applyProxyConfig({ mode: ""direct"" }, function(result) {
                if (result.success) {
                    currentAuth = null;
                    chrome.storage.local.remove([FLARESOLVERR_PROXY_KEY]);
                    chrome.storage.local.remove([""flaresolverrProxyAuth""]);
                }
                sendResponse(result);
            });
        } else if (request.mode === ""fixed_servers"") {
            var proxyConfig = {
                mode: ""fixed_servers"",
                rules: request.rules
            };
            var newAuth = (request.auth && request.auth.username) ? request.auth : null;
            applyProxyConfig(proxyConfig, function(result) {
                if (result.success) {
                    currentAuth = newAuth;
                    chrome.storage.local.set({ [FLARESOLVERR_PROXY_KEY]: proxyConfig });
                    if (currentAuth) {
                        chrome.storage.local.set({ flaresolverrProxyAuth: currentAuth });
                    } else {
                        chrome.storage.local.remove([""flaresolverrProxyAuth""]);
                    }
                }
                sendResponse(result);
            });
        } else {
            sendResponse({ success: false, error: ""Unknown mode: "" + request.mode });
        }
    } catch (err) {
        sendResponse({ success: false, error: err.message });
    }
    return true;
});

// Handle proxy authentication
chrome.webRequest.onAuthRequired.addListener(
    function(details, callbackFn) {
        // currentAuth is updated synchronously in onMessage so there is never
        // a race between ACK and the first onAuthRequired event.
        if (currentAuth && currentAuth.username) {
            callbackFn({
                authCredentials: {
                    username: currentAuth.username,
                    password: currentAuth.password || """"
                }
            });
            return;
        }
        // Fallback to storage (e.g. service worker restart).
        chrome.storage.local.get([""flaresolverrProxyAuth""], function(result) {
            var auth = result.flaresolverrProxyAuth;
            if (auth && auth.username) {
                currentAuth = auth;
                callbackFn({
                    authCredentials: {
                        username: auth.username,
                        password: auth.password || """"
                    }
                });
            } else {
                callbackFn();
            }
        });
    },
    { urls: [""<all_urls>""] },
    [""asyncBlocking""]
);
";

        private const string ProxyExtensionProxyHtml = @"<!DOCTYPE html>
<html>
<head>
    <title>FlareSolverr Proxy Manager</title>
</head>
<body>
<script>
// Stable extension page used as a command channel for proxy updates.
// The Python side navigates here and executes scripts that call
// chrome.runtime.sendMessage directly (extension pages have that API).
window.__FS_PROXY_RESULT = null;
</script>
</body>
</html>
";

        private void StopProcess()
        {
            _ready = false;
            if (_process != null && !_process.HasExited)
            {
                try
                {
                    _process.Kill(true);
                    _process.WaitForExit(3000);
                }
                catch { }
            }
        }

        public void Dispose()
        {
            StopProcess();
            _process?.Dispose();
            _startLock.Dispose();
        }
    }
}
