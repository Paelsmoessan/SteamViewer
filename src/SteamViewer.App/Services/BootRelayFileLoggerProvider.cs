using Microsoft.Extensions.Logging;
using SteamViewer.Platform.Windows.Elevation;

namespace SteamViewer.App.Services;

/// <summary>
/// File logging provider for boot relay mode. Routes all ILogger output to
/// the same debug files as BootRelayService.DebugLog (ProgramData + exe-local).
/// This captures HostSession, HostVideoPipeline, StreamTransport, etc. logs
/// that would otherwise go to an invisible console window.
/// </summary>
public sealed class BootRelayFileLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName)
    {
        // Shorten category: "SteamViewer.Client.Core.Video.HostVideoPipeline" -> "HostVideoPipeline"
        var shortName = categoryName;
        var lastDot = categoryName.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < categoryName.Length - 1)
            shortName = categoryName[(lastDot + 1)..];

        return new BootRelayFileLogger(shortName);
    }

    public void Dispose() { }

    private sealed class BootRelayFileLogger : ILogger
    {
        private readonly string _category;

        public BootRelayFileLogger(string category) => _category = category;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var level = logLevel switch
            {
                LogLevel.Trace => "TRACE",
                LogLevel.Debug => "DEBUG",
                LogLevel.Information => "INFO",
                LogLevel.Warning => "WARN",
                LogLevel.Error => "ERROR",
                LogLevel.Critical => "CRIT",
                _ => "NONE"
            };

            var message = formatter(state, exception);
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] [{_category}] {message}";
            if (exception != null)
                line += $"\n  {exception.GetType().Name}: {exception.Message}";

            BootRelayService.DebugLog(line);
        }
    }
}
