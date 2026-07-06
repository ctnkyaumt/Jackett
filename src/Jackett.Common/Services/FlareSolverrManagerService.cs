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
    }
}

namespace Jackett.Common.Services
{
    public class FlareSolverrManagerService : IFlareSolverrManagerService
    {
        // First launch downloads/patches undetected_chromedriver and boots a real Chrome, which can be slow.
        private const int ReadyTimeoutSeconds = 90;

        private static readonly HttpClient _healthClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        private readonly Logger _logger;
        private readonly IConfigurationService _configurationService;
        private readonly ServerConfig _serverConfig;
        private readonly SemaphoreSlim _startLock = new SemaphoreSlim(1, 1);

        private Process _process;
        private string _executablePath;
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

            _logger.Info("FlareSolverr installed successfully.");
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

            // Fix FlareSolverr 3.6.6 Windows bug: a missing proxy_extension directory crashes the process.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var proxyExtensionDir = Path.Combine(Path.GetDirectoryName(_executablePath), "_internal", "flaresolverr", "proxy_extension");
                if (!Directory.Exists(proxyExtensionDir))
                {
                    _logger.Info("Applying fix for FlareSolverr 3.6.6 missing proxy_extension directory.");
                    Directory.CreateDirectory(proxyExtensionDir);
                }
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
