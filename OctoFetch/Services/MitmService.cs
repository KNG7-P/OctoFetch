using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Authentication;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using OctoFetch.Models;

namespace OctoFetch.Services
{
    public sealed class MitmService : IMitmService, IDisposable
    {
        private const string ProxyListenAddress = "127.0.0.1:10808";
        private const string DefaultConfigResource = "OctoFetch.Resources.MITM-DomainFronting.json";
        private const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        private const int XrayShutdownTimeoutMs = 3_000;
        private const int XrayHealthCheckDelayMs = 2_000;
        private const int CertGenerationTimeoutMs = 15_000;

        [DllImport("wininet.dll")]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
        private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        private const int INTERNET_OPTION_REFRESH = 37;

        private readonly IAppLogger _logger;
        private readonly AppSettings _settings;

        private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
        private readonly object _stateLock = new();

        private Process? _xrayProcess;
        private bool _certificateInstalled;
        private bool _disposed;
        private string? _lastStartError;

        private SystemProxyScope? _primaryProxyScope;

        private readonly object _scopesLock = new();
        private readonly System.Collections.Generic.List<SystemProxyScope> _activeScopes = new();

        public MitmService(IAppLogger logger, AppSettings settings)
        {
            _logger = logger;
            _settings = settings;
            if (_settings.IsMitmEnabled)
            {
                _certificateInstalled = DetectCertificateInstalled();
            }
        }

        public string CoreFolder =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MitmCore");

        public string ConfigPath =>
            Path.Combine(CoreFolder, "MITM-DomainFronting.json");

        private string XrayPath => Path.Combine(CoreFolder, "xray.exe");
        private string CertPath => Path.Combine(CoreFolder, "mycert.crt");
        private string KeyPath => Path.Combine(CoreFolder, "mycert.key");

        public string ProxyAddress => ProxyListenAddress;
        public string ProxyUrl => "http://" + ProxyListenAddress;

        public bool BinariesPresent => File.Exists(XrayPath);

        public bool IsRunning
        {
            get
            {
                lock (_stateLock)
                {
                    return _xrayProcess is { HasExited: false };
                }
            }
        }

        public bool IsCertificateInstalled
        {
            get
            {
                lock (_stateLock) return _certificateInstalled;
            }
        }

        public string? LastStartError
        {
            get { lock (_stateLock) return _lastStartError; }
        }

        public event Action? StateChanged;

        private void RaiseStateChanged()
        {
            try { StateChanged?.Invoke(); }
            catch { }
        }

        // -------------------------------------------------------------------
        // Dependent-operation tracking
        // -------------------------------------------------------------------
        private int _dependentOperationsCount;

        public int DependentOperationsCount
            => System.Threading.Volatile.Read(ref _dependentOperationsCount);

        public IDisposable RegisterDependentOperation(string reason)
        {
            System.Threading.Interlocked.Increment(ref _dependentOperationsCount);
            return new DependentOperationLease(this, reason);
        }

        private void ReleaseDependentOperation()
        {
            System.Threading.Interlocked.Decrement(ref _dependentOperationsCount);
        }

        private sealed class DependentOperationLease : IDisposable
        {
            private readonly MitmService _owner;
            private readonly string _reason;
            private int _disposed;
            public DependentOperationLease(MitmService owner, string reason)
            {
                _owner = owner;
                _reason = reason;
            }
            public void Dispose()
            {
                if (System.Threading.Interlocked.Exchange(ref _disposed, 1) == 1) return;
                _owner.ReleaseDependentOperation();
            }
        }

        // -------------------------------------------------------------------
        // Persistent enable / disable lifecycle
        // -------------------------------------------------------------------
        
        public Task<bool> ApplyEnabledStateAsync(CancellationToken ct = default)
        {
            return _settings.IsMitmEnabled
                ? StartEngineAsync(ct)
                : StopEngineAsync(ct).ContinueWith(_ => true, ct);
        }

        public async Task<bool> StartEngineAsync(CancellationToken ct = default)
        {
            if (_disposed) return false;
            if (!_settings.IsMitmEnabled) return false;

            if (!BinariesPresent)
            {
                SetStartError("MITM binaries are missing from MitmCore/.");
                _logger.Log(LogChannel.Mitm,
                    "⚠ MITM binaries not found in MitmCore/. The engine will stay off.");
                RaiseStateChanged();
                return false;
            }

            await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (IsRunning) return true;

                _logger.Log(LogChannel.Mitm, "⛓ Starting MITM engine…");
                bool ok = await StartCoreAsync(ct).ConfigureAwait(false);
                if (!ok)
                {
                    var err = LastStartError;
                    _logger.Log(LogChannel.Mitm,
                        string.IsNullOrWhiteSpace(err)
                            ? "⚠ MITM engine failed to start; the app stays on the direct connection."
                            : $"⚠ MITM engine failed to start ({err}); the app stays on the direct connection.");
                    return false;
                }
                lock (_stateLock) { _lastStartError = null; }
                return true;
            }
            finally
            {
                _lifecycleLock.Release();
                RaiseStateChanged();
            }
        }

        public async Task StopEngineAsync(CancellationToken ct = default)
        {
            if (_disposed) return;

            await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!IsRunning) return;
                _logger.Log(LogChannel.Mitm, "⛓ Stopping MITM engine…");
                StopCoreInternal();
            }
            finally
            {
                CloseAllSystemProxyScopes();
                _lifecycleLock.Release();
                RaiseStateChanged();
            }
        }

        public async Task<IDisposable> AcquireAsync(string reason, CancellationToken ct = default)
        {
            if (_disposed) return NoOpDisposable.Instance;
            if (!_settings.IsMitmEnabled) return NoOpDisposable.Instance;
            if (!BinariesPresent) return NoOpDisposable.Instance;

            if (!IsRunning)
            {
                _logger.Log(LogChannel.Mitm, $"Engine not yet up for {reason}; starting on demand…");
                await StartEngineAsync(ct).ConfigureAwait(false);
            }

            return NoOpDisposable.Instance;
        }

        private sealed class NoOpDisposable : IDisposable
        {
            public static readonly NoOpDisposable Instance = new();
            public void Dispose() { }
        }

        // -------------------------------------------------------------------
        // xray-core process management
        // -------------------------------------------------------------------

        private Task<bool> StartCoreAsync(CancellationToken ct)
        {
            return Task.Run(() =>
            {
                try
                {
                    bool certReady = EnsureCertificateInstalled();
                    if (!certReady)
                    {
                        SetStartError("Certificate could not be installed in any trust store.");
                        _logger.Log(LogChannel.Mitm,
                            "Certificate not ready; cannot start engine.");
                        return false;
                    }

                    if (!File.Exists(ConfigPath))
                    {
                        Directory.CreateDirectory(CoreFolder);
                        File.WriteAllText(ConfigPath, ReadEmbeddedConfig());
                        _logger.Log(LogChannel.Mitm,
                            "📝 First-run init: wrote default MITM config to MitmCore/MITM-DomainFronting.json.");
                    }

                    StopCoreInternal();

                    var psi = new ProcessStartInfo
                    {
                        FileName = XrayPath,
                        Arguments = "-c MITM-DomainFronting.json",
                        WorkingDirectory = CoreFolder,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                    };

                    var proc = new Process { StartInfo = psi };
                    var captured = new StringBuilder();
                    var captureLock = new object();
                    int capturedBytes = 0;
                    const int captureLimit = 8 * 1024;

                    void OnLine(string? line)
                    {
                        if (string.IsNullOrEmpty(line)) return;
                        lock (captureLock)
                        {
                            if (capturedBytes >= captureLimit) return;
                            captured.AppendLine(line);
                            capturedBytes += line.Length + 1;
                        }
                    }

                    proc.OutputDataReceived += (_, e) => OnLine(e.Data);
                    proc.ErrorDataReceived += (_, e) => OnLine(e.Data);

                    proc.Start();
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                    lock (_stateLock) { _xrayProcess = proc; }

                    if (!ct.WaitHandle.WaitOne(XrayHealthCheckDelayMs) && proc.HasExited)
                    {
                        try { proc.WaitForExit(500); } catch { }

                        string output;
                        lock (captureLock) { output = captured.ToString().Trim(); }
                        int exitCode = -1;
                        try { exitCode = proc.ExitCode; } catch { }

                        string detail = string.IsNullOrEmpty(output)
                            ? $"exit code {exitCode}, no output"
                            : $"exit code {exitCode}: {Truncate(output, 600)}";
                        SetStartError(detail);
                        _logger.Log(LogChannel.Mitm,
                            $"Xray exited unexpectedly ({detail}). " +
                            "Check that 127.0.0.1:10808 is free, that MitmCore/MITM-DomainFronting.json is valid, " +
                            "and that xray.exe is recent enough for the bundled config.");
                        lock (_stateLock) { _xrayProcess = null; }
                        return false;
                    }

                    _logger.Log(LogChannel.Mitm,
                        $"Engine listening on {ProxyAddress}.");

                    try { ApplySystemProxy(); }
                    catch (Exception ex)
                    {
                        _logger.LogException(LogChannel.Mitm,
                            "Engine started but the system-wide proxy could not be applied; " +
                            "some apps (browser OAuth, yt-dlp) will still hit the direct connection",
                            ex);
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    SetStartError(ex.Message);
                    _logger.LogException(LogChannel.Mitm, "Failed to start xray", ex);
                    StopCoreInternal();
                    return false;
                }
            }, ct);
        }

        private void SetStartError(string? message)
        {
            lock (_stateLock) { _lastStartError = message; }
        }

        private static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max) return value;
            return value[..max] + "…";
        }

        private void StopCore() => StopCoreInternal();

        private void StopCoreInternal()
        {
            try { CloseAllSystemProxyScopes(); }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Mitm,
                    "Failed to restore system proxy on stop", ex);
            }

            Process? proc;
            lock (_stateLock)
            {
                proc = _xrayProcess;
                _xrayProcess = null;
            }
            if (proc == null) return;

            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill();
                    proc.WaitForExit(XrayShutdownTimeoutMs);
                }
            }
            catch (Exception ex)
            {
                _logger.Log(LogChannel.Mitm, $"Could not stop xray cleanly: {ex.Message}");
            }
            finally
            {
                try { proc.Dispose(); } catch { /* already disposed */ }
            }
        }

        // -------------------------------------------------------------------
        // System-wide proxy (engine-scoped)
        // -------------------------------------------------------------------

        private void ApplySystemProxy()
        {
            if (_primaryProxyScope != null) return;
            _primaryProxyScope = EnableSystemProxyScope() as SystemProxyScope;
        }

        // -------------------------------------------------------------------
        // Certificate management
        // -------------------------------------------------------------------

        public Task<bool> InstallCertificateAsync(CancellationToken ct = default) =>
            Task.Run(() => EnsureCertificateInstalled(), ct);

        private bool EnsureCertificateInstalled()
        {
            try
            {
                if (!File.Exists(XrayPath))
                {
                    _logger.Log(LogChannel.Mitm,
                        $"xray.exe not found at {XrayPath}; cannot generate certificate.");
                    return false;
                }

                if (!File.Exists(CertPath) || !File.Exists(KeyPath))
                {
                    _logger.Log(LogChannel.Mitm, "Generating new MITM root certificate…");

                    var psi = new ProcessStartInfo
                    {
                        FileName = XrayPath,
                        Arguments = "tls cert -ca -file=mycert",
                        WorkingDirectory = CoreFolder,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                    };

                    using var proc = Process.Start(psi);
                    if (proc == null)
                    {
                        _logger.Log(LogChannel.Mitm, "Failed to launch xray for cert generation.");
                        return false;
                    }

                    if (!proc.WaitForExit(CertGenerationTimeoutMs))
                    {
                        try { proc.Kill(); } catch { /* best effort */ }
                        _logger.Log(LogChannel.Mitm,
                            $"Cert generation timed out after {CertGenerationTimeoutMs / 1000}s.");
                        return false;
                    }
                    if (proc.ExitCode != 0)
                    {
                        _logger.Log(LogChannel.Mitm,
                            $"Cert generation failed (exit code {proc.ExitCode}).");
                        return false;
                    }
                }

                using var cert = new X509Certificate2(CertPath);

                bool inUser = IsCertificateInStore(StoreLocation.CurrentUser, cert);
                bool inMachine = IsCertificateInStore(StoreLocation.LocalMachine, cert);

                bool installedOk;
                if (inUser || inMachine)
                {
                    installedOk = true;
                    _logger.Log(LogChannel.Mitm, "Certificate already trusted; skipping reinstall.");
                }
                else
                {
                    installedOk = TryAddToStore(StoreLocation.CurrentUser, cert, logFailures: true);
                    if (installedOk)
                        _logger.Log(LogChannel.Mitm, "Certificate installed in Windows trust store (CurrentUser).");
                    else
                        _logger.Log(LogChannel.Mitm,
                            "Could not install certificate in CurrentUser\\Root; HttpClient/curl through the proxy will reject TLS.");
                }

                lock (_stateLock) _certificateInstalled = installedOk;
                RaiseStateChanged();
                return installedOk;
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Mitm, "Certificate setup failed", ex);
                return false;
            }
        }

        private bool TryAddToStore(StoreLocation location, X509Certificate2 cert, bool logFailures = true)
        {
            try
            {
                using var store = new X509Store(StoreName.Root, location);
                store.Open(OpenFlags.ReadWrite);
                if (!store.Certificates.Contains(cert))
                    store.Add(cert);
                store.Close();
                return true;
            }
            catch (Exception ex)
            {
                if (logFailures)
                {
                    _logger.Log(LogChannel.Mitm,
                        $"Could not write to {location}\\Root: {ex.Message}");
                }
                return false;
            }
        }

        private static bool IsCertificateInStore(StoreLocation location, X509Certificate2 cert)
        {
            try
            {
                using var store = new X509Store(StoreName.Root, location);
                store.Open(OpenFlags.ReadOnly);
                var matches = store.Certificates.Find(
                    X509FindType.FindByThumbprint, cert.Thumbprint, validOnly: false);
                return matches.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        public Task<bool> UninstallCertificateAsync(CancellationToken ct = default) =>
            Task.Run(() =>
            {
                bool removed = false;
                try
                {
                    if (File.Exists(CertPath))
                    {
                        using var cert = new X509Certificate2(CertPath);
                        removed |= RemoveFromStore(StoreLocation.LocalMachine, cert);
                        removed |= RemoveFromStore(StoreLocation.CurrentUser, cert);
                    }
                    SafeDelete(CertPath);
                    SafeDelete(KeyPath);

                    lock (_stateLock) _certificateInstalled = DetectCertificateInstalled();
                    RaiseStateChanged();

                    _logger.Log(LogChannel.Mitm, removed
                        ? "Certificate removed from Windows trust store."
                        : "Certificate was not present in any trust store.");
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogException(LogChannel.Mitm, "Certificate uninstall failed", ex);
                    return false;
                }
            }, ct);

        private bool RemoveFromStore(StoreLocation location, X509Certificate2 cert)
        {
            try
            {
                using var store = new X509Store(StoreName.Root, location);
                store.Open(OpenFlags.ReadWrite);
                var matches = store.Certificates.Find(
                    X509FindType.FindByThumbprint, cert.Thumbprint, validOnly: false);
                if (matches.Count == 0) return false;
                store.RemoveRange(matches);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Log(LogChannel.Mitm,
                    $"Could not access {location}\\Root: {ex.Message}");
                return false;
            }
        }

        private void SafeDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                _logger.Log(LogChannel.Mitm,
                    $"Could not delete {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        private bool DetectCertificateInstalled()
        {
            try
            {
                if (!File.Exists(CertPath)) return false;
                using var cert = new X509Certificate2(CertPath);
                foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
                {
                    try
                    {
                        using var store = new X509Store(StoreName.Root, location);
                        store.Open(OpenFlags.ReadOnly);
                        var matches = store.Certificates.Find(
                            X509FindType.FindByThumbprint, cert.Thumbprint, validOnly: false);
                        if (matches.Count > 0) return true;
                    }
                    catch { }
                }
                return false;
            }
            catch { return false; }
        }

        // -------------------------------------------------------------------
        // System-proxy scope (shared by the engine's primary scope and the
        // short-lived Drive-OAuth scope)
        // -------------------------------------------------------------------

        public IDisposable EnableSystemProxyScope()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true);
                if (key == null)
                {
                    _logger.Log(LogChannel.Mitm, "Could not open Internet Settings key.");
                    return NoOpDisposable.Instance;
                }

                var prev = new SavedProxy
                {
                    Enable = key.GetValue("ProxyEnable"),
                    Server = key.GetValue("ProxyServer"),
                    Override = key.GetValue("ProxyOverride"),
                };

                key.SetValue("ProxyEnable", 1);
                key.SetValue("ProxyServer", $"http={ProxyAddress};https={ProxyAddress}");
                key.SetValue("ProxyOverride", "<local>;127.0.0.1;localhost");

                FlushInternetSettings();
                _logger.Log(LogChannel.Mitm, $"System proxy set to {ProxyAddress} — all apps now route through MITM.");
                var scope = new SystemProxyScope(this, prev);
                lock (_scopesLock) _activeScopes.Add(scope);
                return scope;
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Mitm,
                    "Could not enable system proxy scope", ex);
                return NoOpDisposable.Instance;
            }
        }

        private void CloseAllSystemProxyScopes()
        {
            SystemProxyScope[] snapshot;
            lock (_scopesLock) snapshot = _activeScopes.ToArray();
            for (int i = snapshot.Length - 1; i >= 0; i--)
            {
                try { snapshot[i].Dispose(); } catch { }
            }
            _primaryProxyScope = null;
        }

        internal void Unregister(SystemProxyScope scope)
        {
            lock (_scopesLock) _activeScopes.Remove(scope);
        }

        internal sealed class SavedProxy
        {
            public object? Enable;
            public object? Server;
            public object? Override;
        }

        internal sealed class SystemProxyScope : IDisposable
        {
            private readonly MitmService _svc;
            private readonly SavedProxy _prev;
            private int _disposed;

            public SystemProxyScope(MitmService svc, SavedProxy prev)
            {
                _svc = svc;
                _prev = prev;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                _svc.Unregister(this);
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true);
                    if (key == null) return;

                    if (_prev.Enable != null) key.SetValue("ProxyEnable", _prev.Enable);
                    else key.SetValue("ProxyEnable", 0);

                    if (_prev.Server != null) key.SetValue("ProxyServer", _prev.Server);
                    else { try { key.DeleteValue("ProxyServer", false); } catch { } }

                    if (_prev.Override != null) key.SetValue("ProxyOverride", _prev.Override);
                    else { try { key.DeleteValue("ProxyOverride", false); } catch { } }

                    FlushInternetSettings();
                    _svc._logger.Log(LogChannel.Mitm, "System proxy restored to previous value.");
                }
                catch (Exception ex)
                {
                    _svc._logger.LogException(LogChannel.Mitm,
                        "Failed to restore system proxy", ex);
                }
            }
        }

        private static void FlushInternetSettings()
        {
            try
            {
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
            }
            catch { }
        }

        // -------------------------------------------------------------------
        // Health probes
        // -------------------------------------------------------------------

        public async Task<MitmHealthResult> CheckHealthAsync(CancellationToken ct = default)
        {
            await AcquireAsync("health-check", ct).ConfigureAwait(false);

            bool useProxy = IsRunning;

            var youTubeResult = await SingleProbeAsync(useProxy, "https://www.youtube.com/generate_204", ct).ConfigureAwait(false);
            var driveResult   = await SingleProbeAsync(useProxy, "https://clients4.google.com/generate_204", ct).ConfigureAwait(false);
            var pingResult    = await SingleProbeAsync(useProxy, "https://i.ytimg.com/generate_204", ct).ConfigureAwait(false);

            return new MitmHealthResult
            {
                YouTubeOk = youTubeResult.Ok,
                YouTubeLatencyMs = youTubeResult.LatencyMs,
                YouTubeError = youTubeResult.Error,
                DriveOk = driveResult.Ok,
                DriveLatencyMs = driveResult.LatencyMs,
                DriveError = driveResult.Error,
                TcpPingOk = pingResult.Ok,
                TcpPingMs = pingResult.LatencyMs,
                TcpPingError = pingResult.Error,
            };
        }

        private async Task<(bool Ok, long LatencyMs, string? Error)> SingleProbeAsync(
            bool useProxy, string url, CancellationToken ct)
        {
            var curlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GitCore", "curl.exe");
            if (!File.Exists(curlPath))
            {
                return (false, 0, "curl.exe not found in GitCore/");
            }

            var args = new StringBuilder();
            args.Append("-s -o NUL -w \"%{http_code} %{time_total}\" ");
            args.Append("--max-time 12 --connect-timeout 8 ");
            args.Append("--http1.1 ");
            if (useProxy)
            {
                args.Append($"-x \"{ProxyUrl}\" -k ");
            }
            args.Append("-A \"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 OctoFetch/1.0\" ");
            args.Append($"\"{url}\"");

            var psi = new ProcessStartInfo
            {
                FileName = curlPath,
                WorkingDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GitCore"),
                Arguments = args.ToString(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            var sw = Stopwatch.StartNew();
            try
            {
                using var proc = Process.Start(psi)
                    ?? throw new InvalidOperationException("Failed to launch curl.exe");
                try
                {
                    await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (!proc.HasExited)
                    {
                        try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                    }
                    throw;
                }

                var stdout = (await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false)).Trim();
                var stderr = (await proc.StandardError.ReadToEndAsync().ConfigureAwait(false)).Trim();
                sw.Stop();

                var parts = stdout.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 1 && int.TryParse(parts[0], out var code) && code > 0)
                {
                    long ms = sw.ElapsedMilliseconds;
                    if (parts.Length >= 2 &&
                        double.TryParse(parts[1],
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var seconds))
                    {
                        ms = (long)Math.Round(seconds * 1000);
                    }
                    return (code < 400, ms, null);
                }

                var msg = string.IsNullOrEmpty(stderr)
                    ? $"curl exit {proc.ExitCode}"
                    : $"curl exit {proc.ExitCode}: {stderr}";
                return (false, sw.ElapsedMilliseconds, msg);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                var msg = ex.Message;
                var inner = ex.InnerException;
                while (inner != null) { msg = inner.Message; inner = inner.InnerException; }
                return (false, sw.ElapsedMilliseconds, msg);
            }
        }

        // -------------------------------------------------------------------
        // Config I/O
        // -------------------------------------------------------------------

        public string ReadConfig()
        {
            try
            {
                if (File.Exists(ConfigPath))
                    return File.ReadAllText(ConfigPath);
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Mitm, "Could not read MITM config", ex);
            }
            return ReadEmbeddedConfig();
        }

        public bool WriteConfig(string json, out string? error)
        {
            error = null;
            try
            {
                using var _ = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                error = ex.Message;
                return false;
            }
            try
            {
                if (!Directory.Exists(CoreFolder)) Directory.CreateDirectory(CoreFolder);
                File.WriteAllText(ConfigPath, json);
                _logger.Log(LogChannel.Mitm, "MITM config saved.");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Mitm, "Failed to write MITM config", ex);
                error = ex.Message;
                return false;
            }
        }

        public void RestoreDefaultConfig()
        {
            try
            {
                if (!Directory.Exists(CoreFolder)) Directory.CreateDirectory(CoreFolder);
                File.WriteAllText(ConfigPath, ReadEmbeddedConfig());
                _logger.Log(LogChannel.Mitm, "MITM config restored to default.");
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Mitm, "Failed to restore default MITM config", ex);
            }
        }

        private static string ReadEmbeddedConfig()
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream(DefaultConfigResource)
                ?? throw new InvalidOperationException(
                    $"Embedded resource '{DefaultConfigResource}' was not found. " +
                    "Check the EmbeddedResource entry in the project file.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { StopCoreInternal(); } catch { }
            try { CloseAllSystemProxyScopes(); } catch { }
        }
    }
}
