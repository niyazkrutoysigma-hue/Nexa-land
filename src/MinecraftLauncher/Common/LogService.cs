using System.Text;

namespace MinecraftLauncher.Common;

public enum LogLevel { Trace, Info, Warn, Error, Success }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message, string? Detail = null)
{
    public string TimeText => Time.ToString("HH:mm:ss");
}

/// <summary>Единый лог приложения: пишет в файл и в UI.</summary>
public sealed class LogService
{
    private static readonly object FileLock = new();
    private static string? _logFile;

    public static LogService Default { get; } = new();

    private LogService() { }

    public DispatcherCollection<LogEntry> Entries { get; } = new();

    private string LogFile
    {
        get
        {
            if (_logFile is not null && File.Exists(_logFile)) return _logFile;
            _logFile = Path.Combine(AppPaths.LogsDir, $"launcher-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
            return _logFile;
        }
    }

    public void Write(LogLevel level, string message, Exception? ex = null)
    {
        var entry = new LogEntry(DateTime.Now, level, message, ex?.ToString());
        Entries.AddSafe(entry);

        var sb = new StringBuilder();
        sb.Append('[').Append(entry.Time.ToString("HH:mm:ss")).Append("] [")
          .Append(level.ToString().ToUpperInvariant()).Append("] ").Append(message);
        if (ex is not null) sb.AppendLine().Append(ex);
        sb.AppendLine();

        try
        {
            lock (FileLock)
            {
                var path = LogFile;
                if (File.Exists(path) && new FileInfo(path).Length > 8 * 1024 * 1024)
                {
                    File.Delete(path);
                }
                File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // лог не должен ронять приложение
        }
    }

    public void Info(string message) => Write(LogLevel.Info, message);
    public void Warn(string message) => Write(LogLevel.Warn, message);
    public void Success(string message) => Write(LogLevel.Success, message);
    public void Error(string message, Exception? ex = null) => Write(LogLevel.Error, message, ex);
    public void Trace(string message) => Write(LogLevel.Trace, message);
}