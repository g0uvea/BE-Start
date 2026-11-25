using BEStart.Models;

namespace BEStart.Services;

public sealed class LogService
{
    public event Action<LogEntry>? EntryAdded;

    public void Log(string source, string message, LogLevel level = LogLevel.Info)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        EntryAdded?.Invoke(new LogEntry
        {
            Timestamp = DateTime.Now,
            Source = source,
            Level = level,
            Message = message.TrimEnd()
        });
    }

    public void Info(string source, string message) => Log(source, message, LogLevel.Info);
    public void Success(string source, string message) => Log(source, message, LogLevel.Success);
    public void Warn(string source, string message) => Log(source, message, LogLevel.Warning);
    public void Error(string source, string message) => Log(source, message, LogLevel.Error);
}
