using System;
using System.Threading;
using System.Threading.Tasks;

namespace OctoFetch.Services
{
    public sealed class MitmHealthResult
    {
        public bool YouTubeOk { get; init; }
        public long YouTubeLatencyMs { get; init; }
        public string? YouTubeError { get; init; }

        public bool DriveOk { get; init; }
        public long DriveLatencyMs { get; init; }
        public string? DriveError { get; init; }

        public bool TcpPingOk { get; init; }
        public long TcpPingMs { get; init; }
        public string? TcpPingError { get; init; }

        public bool OverallOk => YouTubeOk && DriveOk;
    }

    public interface IMitmService
    {
        bool IsRunning { get; }
        bool IsCertificateInstalled { get; }
        bool BinariesPresent { get; }
        int DependentOperationsCount { get; }
        IDisposable RegisterDependentOperation(string reason);
        string? LastStartError { get; }

        string CoreFolder { get; }
        string ConfigPath { get; }
        string ProxyAddress { get; }   
        string ProxyUrl { get; }   
        
        event Action? StateChanged;

        Task<bool> ApplyEnabledStateAsync(CancellationToken ct = default);

        Task<bool> StartEngineAsync(CancellationToken ct = default);

        Task StopEngineAsync(CancellationToken ct = default);

        Task<IDisposable> AcquireAsync(string reason, CancellationToken ct = default);
        IDisposable EnableSystemProxyScope();
        Task<bool> InstallCertificateAsync(CancellationToken ct = default);
        Task<bool> UninstallCertificateAsync(CancellationToken ct = default);

        Task<MitmHealthResult> CheckHealthAsync(CancellationToken ct = default);
        string ReadConfig();
        bool WriteConfig(string json, out string? error);
        void RestoreDefaultConfig();
    }
}
