using System.Diagnostics;
using System.IO;

namespace SpotlightWindows.Core.Services;

/// <summary>
/// Lightweight logging service for diagnostics.
/// Writes to a rotating log file in the application data directory.
/// Designed to be low-overhead — only logs significant events, not keystrokes.
/// </summary>
public sealed class LoggingService
{
    private static readonly Lazy<LoggingService> _instance = new(() => new LoggingService());
    public static LoggingService Instance => _instance.Value;

    private readonly string _logFilePath;
    private readonly object _lock = new();

    private LoggingService()
    {
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpotlightWindows");

        Directory.CreateDirectory(appDataDir);

        _logFilePath = Path.Combine(appDataDir, "spotlight.log");

        // Rotate log if it exceeds 1 MB
        RotateLogIfNeeded(maxSizeBytes: 1_048_576);
    }

    public void Info(string message)
        => Write("INFO", message);

    public void Warning(string message)
        => Write("WARN", message);

    public void Error(string message, Exception? ex = null)
    {
        var detail = ex is not null
            ? $"{message} | {ex.GetType().Name}: {ex.Message}"
            : message;
        Write("ERROR", detail);
    }

    private void Write(string level, string message)
    {
        var entry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";

        try
        {
            lock (_lock)
            {
                File.AppendAllText(_logFilePath, entry + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never crash the application
            Debug.WriteLine($"[LOG WRITE FAILED] {entry}");
        }
    }

    private void RotateLogIfNeeded(long maxSizeBytes)
    {
        try
        {
            if (!File.Exists(_logFilePath)) return;

            var fileInfo = new FileInfo(_logFilePath);
            if (fileInfo.Length <= maxSizeBytes) return;

            var backupPath = Path.ChangeExtension(_logFilePath, ".prev.log");
            if (File.Exists(backupPath))
                File.Delete(backupPath);

            File.Move(_logFilePath, backupPath);
        }
        catch
        {
            // Ignore rotation failures
        }
    }
}
