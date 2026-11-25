namespace BEStart.Models;

public enum ServiceCategory
{
    SqlDatabase,
    NoSqlDatabase,
    PhpRuntime
}

public enum ServiceKind
{
    PostgreSql,
    MariaDb,
    MySql,
    SqlServer,
    MongoDb,
    Redis,
    Cassandra,
    Php,
    Composer
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
