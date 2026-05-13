using System;

namespace OctoFetch.Services
{
    public enum LogChannel
    {
        Downloader,
        Settings,
        Extractor,
        General,
        Mitm
    }

    public interface IAppLogger
    {
        event Action<LogChannel, string>? LogReceived;

        void Log(LogChannel channel, string message);
        void LogException(LogChannel channel, string contextMessage, Exception ex);
    }
}
