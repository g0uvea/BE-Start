namespace BEStart.Models;

public sealed record ServiceDescriptor(
    ServiceKind Kind,
    ServiceCategory Category,
    string Title,
    string ShortName,
    string BadgeText,
    string BadgeBgHex,
    string BadgeFgHex,
    int DefaultPort,
    string ConnectionHint,
    string RuntimeFolder,
    string DataFolder,
    string ConfigFileName,
    string? CliLabel,
    string LogSource)
{
    public static readonly IReadOnlyList<ServiceDescriptor> All =
    [
        new(ServiceKind.PostgreSql, ServiceCategory.SqlDatabase,
            "PostgreSQL", "PostgreSQL", "PG", "#14273E", "#60A5FA",
            5432, "Usuário: postgres | Sem senha",
            "postgresql", "postgresql", "postgresql.conf", "Console psql", "PostgreSQL"),

        new(ServiceKind.MariaDb, ServiceCategory.SqlDatabase,
            "MariaDB Server", "MariaDB", "MDB", "#0F293B", "#38BDF8",
            3306, "Usuário: root | Sem senha",
            "mariadb", "mariadb", "my.ini", "Console SQL", "MariaDB"),

        new(ServiceKind.MySql, ServiceCategory.SqlDatabase,
            "MySQL Server", "MySQL", "SQL", "#1D263B", "#F59E0B",
            3307, "Usuário: root | Sem senha",
            "mysql", "mysql", "my.ini", "Console MySQL", "MySQL"),

        new(ServiceKind.SqlServer, ServiceCategory.SqlDatabase,
            "Microsoft SQL Server", "SQL Server", "MS", "#2D192B", "#F87171",
            1433, "Instância LocalDB / Express",
            "sqlserver", "sqlserver", "sqlserver.info", "Console SQL", "SQLServer"),

        new(ServiceKind.MongoDb, ServiceCategory.NoSqlDatabase,
            "MongoDB", "MongoDB", "MGO", "#112D21", "#4ADE80",
            27017, "Sem autenticação local",
            "mongodb", "mongodb", "mongod.cfg", "Shell Mongo", "MongoDB"),

        new(ServiceKind.Redis, ServiceCategory.NoSqlDatabase,
            "Redis", "Redis", "RDS", "#33161B", "#FB7185",
            6379, "Protocolo RESP | Sem senha",
            "redis", "redis", "redis.conf", "Redis CLI", "Redis"),

        new(ServiceKind.Cassandra, ServiceCategory.NoSqlDatabase,
            "Apache Cassandra", "Cassandra", "CAS", "#1D233D", "#A78BFA",
            9042, "CQL Nativo (JVM leve)",
            "cassandra", "cassandra", "cassandra.yaml", "CQL Shell", "Cassandra"),

        new(ServiceKind.Php, ServiceCategory.PhpRuntime,
            "Servidor PHP", "PHP", "PHP", "#1E2346", "#818CF8",
            8000, "Servidor embutido (8.x)",
            "php", "", "php.ini", "Abrir Site", "PHP"),

        new(ServiceKind.Composer, ServiceCategory.PhpRuntime,
            "PHP Composer", "Composer", "CMP", "#292018", "#FBBF24",
            0, "Gerenciador de dependências PHP (2.11.x)",
            "composer", "", "composer.phar", null, "Composer")
    ];

    private static readonly Dictionary<ServiceKind, ServiceDescriptor> ByKind =
        All.ToDictionary(d => d.Kind);

    public static ServiceDescriptor Get(ServiceKind kind) => ByKind[kind];
}
