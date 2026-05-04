using System;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace OctoFetch.Services
{
    public class AppLogger : IAppLogger
    {
        private readonly ILogger<AppLogger> _logger;
        private readonly string _logFilePath;
        private static readonly object _fileLock = new();

        public event Action<LogChannel, string>? LogReceived;

        public AppLogger(ILogger<AppLogger> logger)
        {
            _logger = logger;

            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OctoFetch", "logs");
            Directory.CreateDirectory(folder);
            _logFilePath = Path.Combine(folder, $"octofetch-{DateTime.UtcNow:yyyy-MM-dd}.log");
        }

        public void Log(LogChannel channel, string message)
        {
            try { LogReceived?.Invoke(channel, message); }
            catch (Exception ex) { _logger.LogWarning(ex, "Log dispatch failed"); }

            try
            {
                lock (_fileLock)
                {
                    File.AppendAllText(
                        _logFilePath,
                        $"{DateTime.UtcNow:O} [{Thread.CurrentThread.ManagedThreadId,-3}] [{channel}] {message}{Environment.NewLine}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Log file write failed");
            }

            _logger.LogInformation("[{Channel}] {Message}", channel, message);
        }

        public void LogException(LogChannel channel, string contextMessage, Exception ex)
        {
            Log(channel, $"❌ {contextMessage}: {ex.Message}");
            _logger.LogError(ex, "{Context}", contextMessage);
        }
    }
}
