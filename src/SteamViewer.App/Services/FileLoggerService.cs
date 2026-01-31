using Microsoft.Extensions.Logging;
using SteamViewer.Common.Logging;

namespace SteamViewer.App.Services;

/// <summary>
/// Logger that writes all logs to a file for easy debugging.
/// Log file location: logs/client-{MachineName}.log in project root
/// </summary>
public class FileLoggerService : IDisposable
{
    private readonly SharedFileLogger _sharedLogger;

    public string LogFilePath => _sharedLogger.LogFilePath;

    public FileLoggerService()
    {
        _sharedLogger = new SharedFileLogger("client");
    }

    public void Log(string level, string source, string message)
    {
        _sharedLogger.Log(level, source, message);
    }

    public void LogJS(string level, string message)
    {
        _sharedLogger.LogJS(level, message);
    }

    public void Dispose()
    {
        _sharedLogger.Dispose();
    }
}

/// <summary>
/// ILogger provider that writes to FileLoggerService
/// </summary>
public class FileLoggerProvider : ILoggerProvider
{
    private readonly FileLoggerService _fileLogger;

    public FileLoggerProvider(FileLoggerService fileLogger)
    {
        _fileLogger = fileLogger;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new FileLogger(_fileLogger, categoryName);
    }

    public void Dispose() { }
}

public class FileLogger : ILogger
{
    private readonly FileLoggerService _fileLogger;
    private readonly string _category;

    public FileLogger(FileLoggerService fileLogger, string category)
    {
        _fileLogger = fileLogger;
        _category = category;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
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
            _ => "???"
        };

        var message = formatter(state, exception);
        if (exception != null)
        {
            message += $"\n  Exception: {exception.Message}";
        }

        _fileLogger.Log(level, _category, message);
    }
}
