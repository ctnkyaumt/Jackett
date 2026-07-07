using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jackett.Common.Models.Config;
using Jackett.Common.Services.Interfaces;
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
    /// The solver is a small Python service (nd_service.py) shipped alongside Jackett as an
    /// embeddable-Python bundle in the "nodriver" folder. It needs a Chromium, which
    /// is downloaded once at runtime (the browser is too large to ship in the installer).
    /// </summary>
    public class NodriverManagerService : INodriverManagerService
    {
        // First launch downloads Chromium (~290 MB) and boots a browser, which can be slow.
        private const int ReadyTimeoutSeconds = 120;

        // Portable Chromium snapshot to download (matches the version nodriver was validated against).
        private const string ChromiumSnapshotRevision = "1522586";

        private static readonly HttpClient _healthClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        private readonly Logger _logger;
        private readonly IConfigurationService _configurationService;
        private readonly ServerConfig _serverConfig;
        private readonly SemaphoreSlim _startLock = new SemaphoreSlim(1, 1);

        private Process _process;
        private string _chromePath;
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
                StartServiceProcess();

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
        /// Ensures a Chromium is present for nodriver to drive. Downloads the portable snapshot once
        /// (the browser is too big to ship in the installer). Sets <see cref="_chromePath"/>.
        /// </summary>
        private async Task EnsureChromiumAsync()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;

            var chromiumDir = Path.Combine(InstallDir, "chromium");
            var chromeExe = Path.Combine(chromiumDir, "chrome-win", "chrome.exe");

            if (File.Exists(chromeExe))
            {
                _chromePath = chromeExe;
                return;
            }

            _logger.Info($"Downloading portable Chromium (snapshot {ChromiumSnapshotRevision}, ~290 MB) for the solver. This is a one-time download...");
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

        private void StartServiceProcess()
        {
            if (_process != null && !_process.HasExited)
                return;

            if (_process != null)
            {
                try { _process.Dispose(); } catch { }
                _process = null;
            }

            var pythonExe = Path.Combine(BundleDir, "python.exe");
            var scriptPath = Path.Combine(BundleDir, "nd_service.py");
            if (!File.Exists(pythonExe) || !File.Exists(scriptPath))
            {
                throw new Exception(
                    $"nodriver solver bundle not found at {BundleDir}. Expected python.exe and nd_service.py. " +
                    "Reinstall Jackett to restore the bundled solver.");
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
                WorkingDirectory = BundleDir
            };

            startInfo.EnvironmentVariables["ND_CHROME"] = _chromePath;
            startInfo.EnvironmentVariables["PORT"] = "8191";
            startInfo.EnvironmentVariables["HOST"] = "127.0.0.1";

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
