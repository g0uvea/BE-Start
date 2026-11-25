namespace BEStart.Models;

public sealed class LogEntry
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Source { get; init; } = "BE-Start";
    public LogLevel Level { get; init; } = LogLevel.Info;
    public string Message { get; init; } = string.Empty;

    public string FormattedTime => Timestamp.ToString("HH:mm:ss");
}
