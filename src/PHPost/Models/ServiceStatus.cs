namespace PHPost.Models;

public enum ServiceKind
{
    Php,
    MariaDb
}

public enum ServiceState
{
    NotInstalled,
    Stopped,
    Starting,
    Running,
    Stopping,
    Installing,
    Error
}

public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error
}
