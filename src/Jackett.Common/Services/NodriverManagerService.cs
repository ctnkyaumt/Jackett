using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jackett.Common.Models.Config;
using Jackett.Common.Services.Interfaces;
using Microsoft.Win32;
using NLog;

namespace Jackett.Common.Services.Interfaces
{
    public interface INodriverManagerService : IDisposable
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
    /// <summary>
    /// Manages an embedded challenge-solver that speaks the FlareSolverr v1 API but is backed by
    /// nodriver (pure CDP, no chromedriver/Selenium). This solves Cloudflare challenges that
    /// FlareSolverr's undetected-chromedriver is detected on when running on Windows.
    ///
    /// The solver is a small Python service (nd_service.py) shipped alongside Jackett in the
    /// "nodriver" folder. At runtime it uses system Python if available (directly or via a venv);
    /// embeddable Python is downloaded on-demand only as a last resort. Chromium is resolved from
    /// the system first (installed Chrome, PATH, registry, user directories) and downloaded only
    /// if nothing is found.
    /// </summary>
    public class NodriverManagerService : INodriverManagerService
    {
        // First launch downloads Chromium (~290 MB) and boots a browser, which can be slow.
        private const int ReadyTimeoutSeconds = 120;

        // Portable Chromium snapshot to download (matches the version nodriver was validated against).
        private const string ChromiumSnapshotRevision = "1522586";

        // Embeddable Python version to download on-demand when no system Python is usable.
        private const string EmbeddablePythonVersion = "3.11.9";

        private static readonly HttpClient _healthClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        private readonly Logger _logger;
        private readonly IConfigurationService _configurationService;
        private readonly ServerConfig _serverConfig;
        private readonly SemaphoreSlim _startLock = new SemaphoreSlim(1, 1);

        private Process _process;
        private string _chromePath;
        private string _pythonPathPrepend; // base-Python DLL dirs to prepend when using a system-Python venv
        private volatile bool _ready;
        private readonly StringBuilder _lastLogs = new StringBuilder();

        public bool IsRunning => _process != null && !_process.HasExited;
        public string FlareSolverrUrl => "http://127.0.0.1:8191";

        public NodriverManagerService(IConfigurationService configurationService, ServerConfig serverConfig)
        {
            _logger = LogManager.GetCurrentClassLogger();
            _configurationService = configurationService;
            _serverConfig = serverConfig;
        }

        public void Start()
        {
            // Warm up in the background so the first indexer request doesn't pay the download/boot cost.
            // Failures here are non-fatal: EnsureReadyAsync retries lazily on demand.
            Task.Run(async () =>
            {
                try
                {
                    await EnsureReadyAsync();
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "nodriver-FlareSolverr warm-up failed; will retry on demand.");
                }
            });
        }

        public void Stop()
        {
            _logger.Info("Stopping embedded nodriver-FlareSolverr...");
            StopProcess();
        }

        // The solver ships with Jackett and updates with it, so there is nothing separate to update.
        public Task CheckForUpdateAsync() => Task.CompletedTask;

        /// <summary>
        /// Ensures the solver is reachable at <see cref="FlareSolverrUrl"/>. Self-healing: if the
        /// endpoint answers (even one we didn't launch) we're done; otherwise we (re)launch and poll.
        /// Failures are never cached, so a later call always gets a fresh attempt.
        /// </summary>
        public async Task EnsureReadyAsync()
        {
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
                if (await IsEndpointHealthyAsync())
                {
                    _ready = true;
                    return;
                }

                _ready = false;
                await EnsureChromiumAsync().ConfigureAwait(false);
                await StartServiceProcessAsync().ConfigureAwait(false);

                var deadline = DateTime.UtcNow.AddSeconds(ReadyTimeoutSeconds);
                while (DateTime.UtcNow < deadline)
                {
                    if (await IsEndpointHealthyAsync())
                    {
                        _ready = true;
                        _logger.Info("nodriver-FlareSolverr is ready.");
                        return;
                    }

                    if (_process != null && _process.HasExited)
                    {
                        try { _process.WaitForExit(2000); } catch { }
                        var logs = _lastLogs.ToString().Trim();
                        throw new Exception(
                            $"nodriver-FlareSolverr exited prematurely (exit code {SafeExitCode()})." +
                            (string.IsNullOrEmpty(logs) ? " No output was captured." : $" Output:{Environment.NewLine}{logs}"));
                    }

                    await Task.Delay(1000).ConfigureAwait(false);
                }

                var timeoutLogs = _lastLogs.ToString().Trim();
                throw new Exception(
                    $"nodriver-FlareSolverr did not become ready within {ReadyTimeoutSeconds} seconds." +
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

        private string InstallDir => Path.Combine(_configurationService.GetAppDataFolder(), "nodriver");

        /// <summary>Absolute path to the shipped nodriver bundle folder (next to the Jackett binaries).</summary>
        private string BundleDir => Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory,
            "nodriver");

        /// <summary>
        /// Ensures a Chrome/Chromium is available for nodriver. Prefers one already on the system
        /// (real Google Chrome, a user Chromium, or an explicit override), and only downloads the
        /// portable snapshot when nothing usable is found. Sets <see cref="_chromePath"/>.
        /// </summary>
        private async Task EnsureChromiumAsync()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;

            var systemChrome = ResolveSystemChrome();
            if (!string.IsNullOrEmpty(systemChrome))
            {
                _chromePath = systemChrome;
                _logger.Info($"Using system Chrome/Chromium at {systemChrome}");
                return;
            }

            var chromiumDir = Path.Combine(InstallDir, "chromium");
            var chromeExe = Path.Combine(chromiumDir, "chrome-win", "chrome.exe");

            if (File.Exists(chromeExe))
            {
                _chromePath = chromeExe;
                return;
            }

            _logger.Info($"No system Chrome/Chromium found. Downloading portable Chromium (snapshot {ChromiumSnapshotRevision}, ~290 MB). This is a one-time download...");
            Directory.CreateDirectory(chromiumDir);

            var url = $"https://commondatastorage.googleapis.com/chromium-browser-snapshots/Win_x64/{ChromiumSnapshotRevision}/chrome-win.zip";
            var tempZip = Path.Combine(InstallDir, "chrome-win.zip");

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
                _chromePath = chromeExe;
                _logger.Info($"Portable Chromium ready at {chromeExe}");
            }
            else
            {
                _logger.Warn("Portable Chromium archive did not contain chrome.exe at the expected path.");
            }
        }

        /// <summary>
        /// Finds a usable Chrome/Chromium already installed. Search order:
        ///   1. Explicit override: JACKETT_CHROME_PATH env-var or chrome_path.txt
        ///   2. Standard install locations (Program Files, LocalAppData)
        ///   3. Windows Registry (Chrome and Chromium install paths)
        ///   4. PATH lookup via where.exe
        ///   5. Recursive scan of well-known user directories (Documents, Desktop, Downloads)
        /// Returns null only if nothing usable is found anywhere.
        /// </summary>
        private string ResolveSystemChrome()
        {
            // --- 1. Explicit overrides -----------------------------------------------
            var env = Environment.GetEnvironmentVariable("JACKETT_CHROME_PATH");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env.Trim('"')))
                return env.Trim('"');

            try
            {
                var overrideFile = Path.Combine(InstallDir, "chrome_path.txt");
                if (File.Exists(overrideFile))
                {
                    var p = File.ReadAllText(overrideFile).Trim().Trim('"');
                    if (!string.IsNullOrWhiteSpace(p) && File.Exists(p))
                        return p;
                }
            }
            catch { }

            // --- 2. Standard well-known install locations ----------------------------
            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
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

            // --- 3. Windows Registry ------------------------------------------------
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var regResult = FindChromeInRegistry();
                if (regResult != null)
                    return regResult;
            }

            // --- 4. PATH lookup via where.exe ---------------------------------------
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                foreach (var exeName in new[] { "chrome.exe", "chromium.exe" })
                {
                    try
                    {
                        var output = RunCapture("where.exe", exeName, 5000, out var code);
                        if (code == 0)
                        {
                            var firstLine = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                            if (!string.IsNullOrWhiteSpace(firstLine) && File.Exists(firstLine.Trim()))
                                return firstLine.Trim();
                        }
                    }
                    catch { }
                }
            }

            // --- 5. Recursive scan of user directories ------------------------------
            // Scan specific subdirectories of the user profile rather than the whole
            // profile (which includes AppData — enormous and full of false positives).
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var scanDirs = new List<string>();
            if (!string.IsNullOrEmpty(userProfile) && Directory.Exists(userProfile))
            {
                var subNames = new[] { "Documents", "Desktop", "Downloads", "Apps", "Programs", "Tools", "Browsers" };
                foreach (var sub in subNames)
                {
                    var full = Path.Combine(userProfile, sub);
                    if (Directory.Exists(full))
                        scanDirs.Add(full);
                }
                // Also scan any direct chrome/chromium exe sitting in the profile root
                foreach (var name in new[] { "chrome.exe", "chromium.exe" })
                {
                    var candidate = Path.Combine(userProfile, name);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            foreach (var dir in scanDirs)
            {
                var found = FindChromeRecursive(dir, maxDepth: 6);
                if (found != null)
                    return found;
            }

            return null;
        }

        /// <summary>
        /// Checks the Windows Registry for Chrome/Chromium install locations.
        /// </summary>
        private string FindChromeInRegistry()
        {
            var regPaths = new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe",
                @"SOFTWARE\Chromium\BLBeacon"
            };
            foreach (var regPath in regPaths)
            {
                try
                {
                    using (var key = Registry.LocalMachine.OpenSubKey(regPath))
                    {
                        var val = key?.GetValue(null)?.ToString() ?? key?.GetValue("Path")?.ToString();
                        if (!string.IsNullOrEmpty(val))
                        {
                            // Value might be a direct exe path or a directory
                            if (File.Exists(val))
                                return val;
                            var asDir = Path.Combine(val, "chrome.exe");
                            if (File.Exists(asDir))
                                return asDir;
                        }
                    }
                }
                catch { }
                try
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(regPath))
                    {
                        var val = key?.GetValue(null)?.ToString() ?? key?.GetValue("Path")?.ToString();
                        if (!string.IsNullOrEmpty(val))
                        {
                            if (File.Exists(val))
                                return val;
                            var asDir = Path.Combine(val, "chrome.exe");
                            if (File.Exists(asDir))
                                return asDir;
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Recursively searches a directory for chrome.exe or chromium.exe, limited by depth.
        /// Skips hidden/system directories and common non-browser folders to keep the scan fast.
        /// </summary>
        private string FindChromeRecursive(string dir, int maxDepth)
        {
            if (maxDepth <= 0)
                return null;
            try
            {
                foreach (var name in new[] { "chrome.exe", "chromium.exe" })
                {
                    var candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate))
                        return candidate;
                }
                foreach (var subDir in Directory.EnumerateDirectories(dir))
                {
                    var dirName = Path.GetFileName(subDir);
                    // Skip obvious non-browser directories to keep the scan fast
                    if (dirName.StartsWith(".") ||
                        string.Equals(dirName, "node_modules", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(dirName, ".git", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(dirName, "__pycache__", StringComparison.OrdinalIgnoreCase))
                        continue;
                    try
                    {
                        var attr = new DirectoryInfo(subDir).Attributes;
                        if ((attr & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                            continue;
                    }
                    catch { continue; }

                    var found = FindChromeRecursive(subDir, maxDepth - 1);
                    if (found != null)
                        return found;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Chooses the Python interpreter to run the solver with. Preference order:
        ///   1. System Python directly (if nodriver + aiohttp + ssl all work without a venv)
        ///   2. System Python inside a venv (created once with the solver's deps)
        ///   3. On-demand embeddable Python downloaded to AppData (last resort)
        /// </summary>
        private async Task<string> ResolvePythonExeAsync()
        {
            try
            {
                if (SystemPythonAvailable())
                {
                    // --- Try system Python directly (no venv) ---
                    if (SystemPythonHasDeps())
                    {
                        var resolved = ResolveExeOnPath("python");
                        if (resolved != null)
                        {
                            _logger.Info($"Using system Python directly for the solver: {resolved}");
                            return resolved;
                        }
                    }

                    // --- Try system Python inside a venv ---
                    var venvPy = EnsureVenvPython();
                    if (venvPy != null)
                    {
                        _logger.Info($"Using system Python (venv) for the solver: {venvPy}");
                        return venvPy;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"System Python not usable: {ex.Message}");
            }

            // --- Last resort: download embeddable Python on-demand ---
            _logger.Info("No usable system Python found. Downloading embeddable Python (last resort)...");
            return await EnsureEmbeddablePythonAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Downloads and configures the embeddable CPython distribution on-demand. This is the
        /// last resort when no system Python is available or usable. The bundle is placed under
        /// InstallDir/python-embed so it persists across restarts and only downloads once.
        /// </summary>
        private async Task<string> EnsureEmbeddablePythonAsync()
        {
            var embedDir = Path.Combine(InstallDir, "python-embed");
            var embedPy = Path.Combine(embedDir, "python.exe");

            // Already downloaded — validate it still works.
            if (File.Exists(embedPy))
            {
                if (EmbeddedPythonHasDeps(embedPy))
                {
                    _logger.Info($"Using previously downloaded embeddable Python: {embedPy}");
                    return embedPy;
                }
                // Corrupt/broken — delete and re-download.
                _logger.Warn("Existing embeddable Python is broken; re-downloading...");
                try { Directory.Delete(embedDir, true); } catch { }
            }

            Directory.CreateDirectory(embedDir);

            // Download embeddable CPython.
            var url = $"https://www.python.org/ftp/python/{EmbeddablePythonVersion}/python-{EmbeddablePythonVersion}-embed-amd64.zip";
            var tempZip = Path.Combine(InstallDir, "python-embed.zip");
            _logger.Info($"Downloading embeddable Python {EmbeddablePythonVersion}...");

            using (var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            using (var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                using (var fileStream = File.Create(tempZip))
                using (var httpStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                {
                    await httpStream.CopyToAsync(fileStream).ConfigureAwait(false);
                }
            }

            ZipFile.ExtractToDirectory(tempZip, embedDir, true);
            File.Delete(tempZip);

            // Enable site-packages so pip-installed deps are importable.
            foreach (var pthFile in Directory.GetFiles(embedDir, "python*._pth"))
            {
                var lines = File.ReadAllText(pthFile)
                    .Replace("#import site", "import site");
                File.WriteAllText(pthFile, lines);
            }

            // Bootstrap pip.
            _logger.Info("Bootstrapping pip in embeddable Python...");
            var getPipPath = Path.Combine(InstallDir, "get-pip.py");
            using (var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            using (var response = await httpClient.GetAsync("https://bootstrap.pypa.io/get-pip.py").ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                File.WriteAllText(getPipPath, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            }
            RunCapture(embedPy, $"\"{getPipPath}\" --no-warn-script-location", 120000, out _);
            try { File.Delete(getPipPath); } catch { }

            // Install solver deps.
            var requirements = Path.Combine(BundleDir, "requirements.txt");
            if (File.Exists(requirements))
            {
                _logger.Info("Installing nodriver + aiohttp in embeddable Python...");
                RunCapture(embedPy, $"-m pip install --no-warn-script-location -r \"{requirements}\"", 600000, out _);
            }

            if (!EmbeddedPythonHasDeps(embedPy))
            {
                throw new Exception("Embeddable Python setup failed: required packages are missing after install.");
            }

            _logger.Info($"Embeddable Python ready at {embedPy}");
            return embedPy;
        }

        private bool EmbeddedPythonHasDeps(string embedPy)
        {
            try
            {
                RunCapture(embedPy, "-c \"import ssl, nodriver, aiohttp\"", 15000, out var code);
                return code == 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// Checks whether the system Python already has nodriver, aiohttp, and a working SSL stack
        /// installed globally (or in user site-packages), so we can skip the venv entirely.
        /// </summary>
        private bool SystemPythonHasDeps()
        {
            try
            {
                RunCapture("python", "-c \"import ssl, nodriver, aiohttp\"", 15000, out var code);
                return code == 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// Resolves a bare executable name (e.g. "python") to an absolute path via where.exe.
        /// Returns null if the executable is not found on PATH.
        /// </summary>
        private string ResolveExeOnPath(string exeName)
        {
            try
            {
                var output = RunCapture("where.exe", exeName, 5000, out var code);
                if (code == 0)
                {
                    var firstLine = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(firstLine) && File.Exists(firstLine.Trim()))
                        return firstLine.Trim();
                }
            }
            catch { }
            return null;
        }

        private bool SystemPythonAvailable()
        {
            try
            {
                var outp = RunCapture("python", "--version", 5000, out var code);
                return code == 0 && outp.IndexOf("Python 3", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        private string EnsureVenvPython()
        {
            var venvDir = Path.Combine(InstallDir, "venv");
            var venvPy = Path.Combine(venvDir, "Scripts", "python.exe");

            // The base Python's own directory + its DLLs folder must be on PATH when running so that
            // _ssl.pyd loads that Python's OpenSSL, not a different-version libssl/libcrypto found
            // elsewhere on the machine (the classic Windows "DLL load failed importing _ssl").
            var basePrefix = RunCapture("python", "-c \"import sys;print(sys.base_prefix)\"", 8000, out var bc).Trim();
            if (bc != 0 || string.IsNullOrEmpty(basePrefix) || !Directory.Exists(basePrefix))
                return null;
            var dllPrepend = basePrefix + ";" + Path.Combine(basePrefix, "DLLs");

            // If a venv already exists, use it only if it actually works under service conditions;
            // never rebuild a broken one (that would churn a slow pip install on every start) - just
            // fall back to the bundled Python.
            if (File.Exists(venvPy))
            {
                if (VenvHasDeps(venvPy, dllPrepend))
                {
                    _pythonPathPrepend = dllPrepend;
                    return venvPy;
                }
                return null;
            }

            var requirements = Path.Combine(BundleDir, "requirements.txt");
            if (!File.Exists(requirements))
                return null;

            _logger.Info("Setting up a Python venv for the solver (one-time)...");
            RunCapture("python", $"-m venv \"{venvDir}\"", 120000, out _);
            if (!File.Exists(venvPy))
                return null;

            RunCapture(venvPy, $"-m pip install --disable-pip-version-check --no-warn-script-location -r \"{requirements}\"", 600000, out _, dllPrepend);
            if (!VenvHasDeps(venvPy, dllPrepend))
                return null;

            _pythonPathPrepend = dllPrepend;
            return venvPy;
        }

        private bool VenvHasDeps(string pythonExe, string pathPrepend)
        {
            try
            {
                // Validate under the SAME conditions the service will run (own dir as cwd, base DLLs
                // on PATH), so a pass here reliably predicts the service will start.
                // Explicitly test 'import ssl' alongside the solver deps because the service imports
                // it at startup (via nodriver.core.util). A system Python with a broken/mismatched
                // OpenSSL ("DLL load failed importing _ssl") must be caught here, not at runtime.
                RunCapture(pythonExe, "-c \"import ssl, nodriver, aiohttp\"", 15000, out var code, pathPrepend);
                return code == 0;
            }
            catch { return false; }
        }

        private string RunCapture(string exe, string args, int timeoutMs, out int exitCode, string pathPrepend = null)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = File.Exists(exe) ? Path.GetDirectoryName(exe) : null
            };
            if (!string.IsNullOrEmpty(pathPrepend))
                psi.EnvironmentVariables["PATH"] = pathPrepend + ";" + (Environment.GetEnvironmentVariable("PATH") ?? "");
            using (var p = Process.Start(psi))
            {
                // Drain both streams concurrently to avoid a full-pipe deadlock.
                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    exitCode = -1;
                    return string.Empty;
                }
                exitCode = p.ExitCode;
                return (stdout.Result ?? "") + (stderr.Result ?? "");
            }
        }

        private async Task StartServiceProcessAsync()
        {
            if (_process != null && !_process.HasExited)
                return;

            if (_process != null)
            {
                try { _process.Dispose(); } catch { }
                _process = null;
            }

            var pythonExe = await ResolvePythonExeAsync().ConfigureAwait(false);
            var scriptPath = Path.Combine(BundleDir, "nd_service.py");
            if (!File.Exists(pythonExe) || !File.Exists(scriptPath))
            {
                throw new Exception(
                    $"nodriver solver not runnable. Python: {pythonExe} (exists={File.Exists(pythonExe)}); " +
                    $"script: {scriptPath} (exists={File.Exists(scriptPath)}). Reinstall Jackett to restore the bundled solver.");
            }
            if (string.IsNullOrEmpty(_chromePath) || !File.Exists(_chromePath))
            {
                throw new Exception("No Chromium available for the solver (download may have failed).");
            }

            _logger.Info($"Starting nodriver-FlareSolverr: {pythonExe} {scriptPath}");

            var startInfo = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = $"\"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // Use the interpreter's own folder as the working dir. The embeddable bundle ships
                // OpenSSL 3 DLLs (libssl-3.dll); if that folder were the cwd it would shadow a
                // system Python's OpenSSL and break `import _ssl`. The script path is absolute, so cwd
                // doesn't affect finding it.
                WorkingDirectory = Path.GetDirectoryName(pythonExe)
            };

            startInfo.EnvironmentVariables["ND_CHROME"] = _chromePath;
            startInfo.EnvironmentVariables["PORT"] = "8191";
            startInfo.EnvironmentVariables["HOST"] = "127.0.0.1";

            // When running a system-Python venv, put that Python's DLL dirs first so _ssl loads the
            // matching OpenSSL (see EnsureVenvPython). Not needed for the self-contained bundle.
            if (!string.IsNullOrEmpty(_pythonPathPrepend))
            {
                var existingPath = startInfo.EnvironmentVariables.ContainsKey("PATH") ? startInfo.EnvironmentVariables["PATH"] : Environment.GetEnvironmentVariable("PATH");
                startInfo.EnvironmentVariables["PATH"] = _pythonPathPrepend + ";" + (existingPath ?? "");
            }

            // Jackett can be launched with a stripped environment where the profile vars are wrong,
            // which breaks Chrome's temp/profile handling. Rewrite them from the known-folder API.
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
                if (e.Data == null) return;
                _logger.Debug($"[ND] {e.Data}");
                AppendLog(e.Data);
            };
            _process.ErrorDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                _logger.Warn($"[ND] {e.Data}");
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
                if (_lastLogs.Length > 8000)
                    _lastLogs.Remove(0, _lastLogs.Length - 4000);
                _lastLogs.AppendLine(line);
            }
        }

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
