using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace SteamViewer.Common.Logging;

/// <summary>
/// Shared file logger that writes to logs/{prefix}-{MachineName}.log in project root.
/// Used by both client and server for multi-dev debugging.
/// </summary>
public class SharedFileLogger : IDisposable
{
    private readonly string _logFilePath;
    private readonly StreamWriter _writer;
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writeTask;
    private bool _disposed;

    private const long MaxLogSizeBytes = 1 * 1024 * 1024; // 1 MB
    private const long KeepTailBytes = 512 * 1024;        // Keep last 512 KB after rotation

    public string LogFilePath => _logFilePath;

    /// <summary>
    /// Creates a file logger that writes to logs/{prefix}-{MachineName}.log
    /// </summary>
    /// <param name="prefix">Log file prefix (e.g., "client" or "server")</param>
    /// <param name="baseDirectory">Base directory to find project root from (usually AppContext.BaseDirectory)</param>
    public SharedFileLogger(string prefix, string? baseDirectory = null)
    {
        var logDir = FindLogsDirectory(baseDirectory);
        Directory.CreateDirectory(logDir);

        var machineName = Environment.MachineName;
        _logFilePath = Path.Combine(logDir, $"{prefix}-{machineName}.log");

        RotateIfNeeded();

        // Write session header
        var header = $"\n=== SteamViewer {prefix} Log - {DateTime.Now:yyyy-MM-dd HH:mm:ss} - {machineName} ===\n";
        File.AppendAllText(_logFilePath, header);

        _writer = new StreamWriter(_logFilePath, append: true) { AutoFlush = true };
        _writeTask = Task.Run(WriteLoop);

        Log("INFO", "SharedFileLogger", $"Logging to: {_logFilePath}");
    }

    private void RotateIfNeeded()
    {
        try
        {
            if (!File.Exists(_logFilePath)) return;

            var fileInfo = new FileInfo(_logFilePath);
            if (fileInfo.Length <= MaxLogSizeBytes) return;

            // Read the last KeepTailBytes and find the first complete line
            byte[] tail;
            using (var fs = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var skipBytes = fs.Length - KeepTailBytes;
                fs.Seek(skipBytes, SeekOrigin.Begin);
                tail = new byte[fs.Length - skipBytes];
                fs.ReadExactly(tail);
            }

            // Find first newline to start on a complete line
            var text = System.Text.Encoding.UTF8.GetString(tail);
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0 && firstNewline < text.Length - 1)
                text = text[(firstNewline + 1)..];

            var oldPath = _logFilePath + ".old";
            if (File.Exists(oldPath)) File.Delete(oldPath);
            File.Move(_logFilePath, oldPath);
            File.WriteAllText(_logFilePath, $"[Log rotated at {DateTime.Now:yyyy-MM-dd HH:mm:ss} — previous content in .old file]\n{text}");
        }
        catch
        {
            // Don't let rotation failure prevent logging
        }
    }

    /// <summary>
    /// Finds the logs directory by looking for solution root markers.
    /// Falls back to a logs folder next to the executable.
    /// </summary>
    private static string FindLogsDirectory(string? baseDirectory)
    {
        var searchDir = baseDirectory ?? AppContext.BaseDirectory;

        // Walk up from base directory looking for solution root markers
        var dir = new DirectoryInfo(searchDir);
        while (dir != null)
        {
            // Look for solution file or CLAUDE.md as root markers
            if (dir.GetFiles("*.sln").Length > 0 || dir.GetFiles("CLAUDE.md").Length > 0)
            {
                return Path.Combine(dir.FullName, "logs");
            }
            dir = dir.Parent;
        }

        // Fallback: create logs folder next to executable
        return Path.Combine(searchDir, "logs");
    }

    public void Log(string level, string source, string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var line = $"[{timestamp}] [{level}] [{source}] {message}";
        _queue.Enqueue(line);
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
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        try { _writeTask.Wait(1000); } catch { }
        _writer.Dispose();
        _cts.Dispose();
    }
}

/// <summary>
/// ILogger provider that writes to SharedFileLogger
/// </summary>
public class SharedFileLoggerProvider : ILoggerProvider
{
    private readonly SharedFileLogger _fileLogger;

    public SharedFileLoggerProvider(SharedFileLogger fileLogger)
    {
        _fileLogger = fileLogger;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new SharedFileLoggerAdapter(_fileLogger, categoryName);
    }

    public void Dispose() { }
}

/// <summary>
/// ILogger adapter for SharedFileLogger
/// </summary>
public class SharedFileLoggerAdapter : ILogger
{
    private readonly SharedFileLogger _fileLogger;
    private readonly string _category;

    public SharedFileLoggerAdapter(SharedFileLogger fileLogger, string category)
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
