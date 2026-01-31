using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace SteamViewer.App.Services;

/// <summary>
/// Logger that writes all logs to a file for easy debugging.
/// Log file location: %TEMP%\SteamViewer\debug.log
/// </summary>
public class FileLoggerService : IDisposable
{
    private readonly string _logFilePath;
    private readonly StreamWriter _writer;
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writeTask;

    public string LogFilePath => _logFilePath;

    public FileLoggerService()
    {
        var logDir = Path.Combine(Path.GetTempPath(), "SteamViewer");
        Directory.CreateDirectory(logDir);
        _logFilePath = Path.Combine(logDir, "debug.log");

        // Clear previous log
        File.WriteAllText(_logFilePath, $"=== SteamViewer Debug Log - {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n\n");

        _writer = new StreamWriter(_logFilePath, append: true) { AutoFlush = true };
        _writeTask = Task.Run(WriteLoop);

        Log("INFO", "FileLogger", $"Logging to: {_logFilePath}");
    }

    public void Log(string level, string source, string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var line = $"[{timestamp}] [{level}] [{source}] {message}";
        _queue.Enqueue(line);
        Console.WriteLine(line); // Also write to console
    }

    public void LogJS(string level, string message)
    {
        Log(level, "JS", message);
    }

    private async Task WriteLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            while (_queue.TryDequeue(out var line))
            {
                try
                {
                    await _writer.WriteLineAsync(line);
                }
                catch { /* ignore write errors */ }
            }
            await Task.Delay(50);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _writeTask.Wait(1000);
        _writer.Dispose();
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
