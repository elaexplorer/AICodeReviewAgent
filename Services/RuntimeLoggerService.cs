using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeReviewAgent.Services;

/// <summary>
/// Service that manages runtime logging to a file with auto-cleanup on shutdown
/// </summary>
public class RuntimeLoggerService : IHostedService, IDisposable
{
    // NOTE: No ILogger<RuntimeLoggerService> dependency - would cause circular dependency:
    // ILoggerFactory → RuntimeFileLoggerProvider → RuntimeLoggerService → ILogger → ILoggerFactory
    private readonly string _logFilePath;
    private readonly Timer _flushTimer;
    private readonly ConcurrentQueue<LogEntry> _logQueue;
    private readonly SemaphoreSlim _writeSemaphore;
    private bool _isDisposed;

    public RuntimeLoggerService()
    {
        _logFilePath = Path.Combine(Directory.GetCurrentDirectory(), "runtime.log");
        _logQueue = new ConcurrentQueue<LogEntry>();
        _writeSemaphore = new SemaphoreSlim(1, 1);
        
        // Flush logs to file every 1 second
        _flushTimer = new Timer(FlushLogsToFile, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Clear any existing log file on startup
        try
        {
            if (File.Exists(_logFilePath))
            {
                File.Delete(_logFilePath);
            }
            
            // Create initial log file with header
            var header = new StringBuilder();
            header.AppendLine("═══════════════════════════════════════════════════════════════");
            header.AppendLine($"  CODE REVIEW AGENT - RUNTIME LOG");
            header.AppendLine($"  Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            header.AppendLine($"  Log File: {_logFilePath}");
            header.AppendLine("═══════════════════════════════════════════════════════════════");
            header.AppendLine();
            
            File.WriteAllText(_logFilePath, header.ToString());

            Console.WriteLine($"📝 Runtime logging started - logs will be written to: {_logFilePath}");
            Console.WriteLine("💡 Monitor with: tail -f runtime.log (Linux/macOS) or Get-Content runtime.log -Wait (PowerShell)");

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"❌ Failed to initialize runtime log file: {ex.Message}");
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("🛑 Runtime logging stopping - cleaning up log file...");

        // Final flush
        await FlushLogsToFileAsync();

        // Add shutdown footer
        try
        {
            var footer = new StringBuilder();
            footer.AppendLine();
            footer.AppendLine("═══════════════════════════════════════════════════════════════");
            footer.AppendLine($"  APPLICATION SHUTDOWN");
            footer.AppendLine($"  Stopped: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            footer.AppendLine("═══════════════════════════════════════════════════════════════");

            await File.AppendAllTextAsync(_logFilePath, footer.ToString(), cancellationToken);

            // Optional: Delete log file on shutdown for true "runtime only" behavior
            // Uncomment the next lines if you want logs to be completely cleaned up:
            // await Task.Delay(1000, cancellationToken); // Give time for final reads
            // File.Delete(_logFilePath);

            Console.WriteLine("✅ Runtime logging stopped successfully");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"❌ Error during runtime log cleanup: {ex.Message}");
        }
    }

    /// <summary>
    /// Queue a log entry to be written to file
    /// </summary>
    public void QueueLogEntry(LogLevel level, string category, string message, Exception? exception = null)
    {
        if (_isDisposed) return;
        
        var entry = new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = level,
            Category = category,
            Message = message,
            Exception = exception
        };
        
        _logQueue.Enqueue(entry);
    }

    private void FlushLogsToFile(object? state)
    {
        _ = FlushLogsToFileAsync();
    }

    private async Task FlushLogsToFileAsync()
    {
        if (_isDisposed || _logQueue.IsEmpty) return;

        await _writeSemaphore.WaitAsync();
        try
        {
            var entriesToWrite = new List<LogEntry>();
            
            // Dequeue all pending entries
            while (_logQueue.TryDequeue(out var entry))
            {
                entriesToWrite.Add(entry);
            }

            if (entriesToWrite.Count == 0) return;

            var sb = new StringBuilder();
            foreach (var entry in entriesToWrite)
            {
                sb.AppendLine(FormatLogEntry(entry));
            }

            await File.AppendAllTextAsync(_logFilePath, sb.ToString());
        }
        catch (Exception ex)
        {
            // Don't use _logger here to avoid infinite recursion
            Console.WriteLine($"❌ Error writing to runtime log: {ex.Message}");
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    private static string FormatLogEntry(LogEntry entry)
    {
        var level = entry.Level switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO ",
            LogLevel.Warning => "WARN ",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "CRIT ",
            _ => "UNKN "
        };

        var timestamp = entry.Timestamp.ToString("HH:mm:ss.fff");
        var category = entry.Category.Length > 30 ? entry.Category.Substring(entry.Category.Length - 30) : entry.Category;
        
        var logLine = $"[{timestamp}] {level} {category,-30} {entry.Message}";
        
        if (entry.Exception != null)
        {
            logLine += $"\n    Exception: {entry.Exception.GetType().Name}: {entry.Exception.Message}";
            if (!string.IsNullOrEmpty(entry.Exception.StackTrace))
            {
                logLine += $"\n    StackTrace: {entry.Exception.StackTrace}";
            }
        }

        return logLine;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        
        _isDisposed = true;
        _flushTimer?.Dispose();
        
        // Final flush
        _ = FlushLogsToFileAsync();
        
        _writeSemaphore?.Dispose();
    }

    private class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public LogLevel Level { get; set; }
        public string Category { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public Exception? Exception { get; set; }
    }
}

/// <summary>
/// Custom file logger provider that integrates with the runtime logger service
/// </summary>
public class RuntimeFileLoggerProvider : ILoggerProvider
{
    private readonly RuntimeLoggerService _runtimeLoggerService;
    private readonly ConcurrentDictionary<string, RuntimeFileLogger> _loggers = new();

    public RuntimeFileLoggerProvider(RuntimeLoggerService runtimeLoggerService)
    {
        _runtimeLoggerService = runtimeLoggerService;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new RuntimeFileLogger(name, _runtimeLoggerService));
    }

    public void Dispose()
    {
        _loggers.Clear();
    }
}

/// <summary>
/// Custom file logger that writes to the runtime log file
/// </summary>
public class RuntimeFileLogger : ILogger
{
    private readonly string _categoryName;
    private readonly RuntimeLoggerService _runtimeLoggerService;

    public RuntimeFileLogger(string categoryName, RuntimeLoggerService runtimeLoggerService)
    {
        _categoryName = categoryName;
        _runtimeLoggerService = runtimeLoggerService;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel >= LogLevel.Debug; // Log Debug and above to file
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var message = formatter(state, exception);
        _runtimeLoggerService.QueueLogEntry(logLevel, _categoryName, message, exception);
    }
}