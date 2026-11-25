using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using BEStart.Models;

namespace BEStart.Converters;

public static class StatusConverters
{
    private static readonly IBrush GreenBrush = new ImmutableSolidColorBrush(Color.Parse("#22C55E"));
    private static readonly IBrush AmberBrush = new ImmutableSolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush RedBrush = new ImmutableSolidColorBrush(Color.Parse("#EF4444"));
    private static readonly IBrush SlateBrush = new ImmutableSolidColorBrush(Color.Parse("#64748B"));
    private static readonly IBrush MutedSlateBrush = new ImmutableSolidColorBrush(Color.Parse("#475569"));

    private static readonly IBrush GreenBgBrush = new ImmutableSolidColorBrush(Color.Parse("#1A22C55E"));
    private static readonly IBrush AmberBgBrush = new ImmutableSolidColorBrush(Color.Parse("#1AF59E0B"));
    private static readonly IBrush RedBgBrush = new ImmutableSolidColorBrush(Color.Parse("#1AEF4444"));
    private static readonly IBrush NeutralBgBrush = new ImmutableSolidColorBrush(Color.Parse("#1F94A3B8"));

    private static readonly IBrush LogSuccessBrush = new ImmutableSolidColorBrush(Color.Parse("#4ADE80"));
    private static readonly IBrush LogWarnBrush = new ImmutableSolidColorBrush(Color.Parse("#FBBF24"));
    private static readonly IBrush LogErrorBrush = new ImmutableSolidColorBrush(Color.Parse("#F87171"));
    private static readonly IBrush LogInfoBrush = new ImmutableSolidColorBrush(Color.Parse("#CBD5E1"));

    private static readonly IBrush NavActiveBg = new ImmutableSolidColorBrush(Color.Parse("#1E293B"));
    private static readonly IBrush NavActiveFg = new ImmutableSolidColorBrush(Color.Parse("#F8FAFC"));
    private static readonly IBrush NavInactiveFg = new ImmutableSolidColorBrush(Color.Parse("#94A3B8"));

    private static readonly IBrush PhpLogBrush = new ImmutableSolidColorBrush(Color.Parse("#818CF8"));
    private static readonly IBrush MariaDbLogBrush = new ImmutableSolidColorBrush(Color.Parse("#38BDF8"));
    private static readonly IBrush PostgresLogBrush = new ImmutableSolidColorBrush(Color.Parse("#60A5FA"));
    private static readonly IBrush MySqlLogBrush = new ImmutableSolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush SqlServerLogBrush = new ImmutableSolidColorBrush(Color.Parse("#F87171"));
    private static readonly IBrush MongoLogBrush = new ImmutableSolidColorBrush(Color.Parse("#4ADE80"));
    private static readonly IBrush RedisLogBrush = new ImmutableSolidColorBrush(Color.Parse("#FB7185"));
    private static readonly IBrush CassandraLogBrush = new ImmutableSolidColorBrush(Color.Parse("#A78BFA"));
    private static readonly IBrush ComposerLogBrush = new ImmutableSolidColorBrush(Color.Parse("#FBBF24"));
    private static readonly IBrush DBeaverLogBrush = new ImmutableSolidColorBrush(Color.Parse("#38BDF8"));
    private static readonly IBrush DefaultLogBrush = new ImmutableSolidColorBrush(Color.Parse("#A5B4FC"));

    public static readonly IValueConverter StateToLabel = new FuncValueConverter<ServiceState, string>(state => state switch
    {
        ServiceState.NotInstalled => "Não instalado",
        ServiceState.Stopped => "Parado",
        ServiceState.Starting => "Iniciando...",
        ServiceState.Running => "Rodando",
        ServiceState.Stopping => "Parando...",
        ServiceState.Installing => "Instalando...",
        ServiceState.Error => "Erro",
        _ => "Desconhecido"
    });

    public static readonly IValueConverter StateToShortLabel = new FuncValueConverter<ServiceState, string>(state => state switch
    {
        ServiceState.NotInstalled => "Não inst.",
        ServiceState.Stopped => "Parado",
        ServiceState.Starting => "Iniciando",
        ServiceState.Running => "Rodando",
        ServiceState.Stopping => "Parando",
        ServiceState.Installing => "Instalando",
        ServiceState.Error => "Erro",
        _ => "-"
    });

    public static readonly IValueConverter StateToBrush = new FuncValueConverter<ServiceState, IBrush>(state => state switch
    {
        ServiceState.Running => GreenBrush,
        ServiceState.Starting or ServiceState.Stopping or ServiceState.Installing => AmberBrush,
        ServiceState.Error => RedBrush,
        ServiceState.Stopped => SlateBrush,
        _ => MutedSlateBrush
    });

    public static readonly IValueConverter StateToBadgeBackground = new FuncValueConverter<ServiceState, IBrush>(state => state switch
    {
        ServiceState.Running => GreenBgBrush,
        ServiceState.Starting or ServiceState.Stopping or ServiceState.Installing => AmberBgBrush,
        ServiceState.Error => RedBgBrush,
        _ => NeutralBgBrush
    });

    public static readonly IValueConverter LogLevelToBrush = new FuncValueConverter<LogLevel, IBrush>(level => level switch
    {
        LogLevel.Success => LogSuccessBrush,
        LogLevel.Warning => LogWarnBrush,
        LogLevel.Error => LogErrorBrush,
        _ => LogInfoBrush
    });

    public static readonly IValueConverter LogSourceToBrush = new FuncValueConverter<string?, IBrush>(src => src switch
    {
        "PHP" => PhpLogBrush,
        "Composer" => ComposerLogBrush,
        "DBeaver" => DBeaverLogBrush,
        "MariaDB" => MariaDbLogBrush,
        "PostgreSQL" => PostgresLogBrush,
        "MySQL" => MySqlLogBrush,
        "SQLServer" => SqlServerLogBrush,
        "MongoDB" => MongoLogBrush,
        "Redis" => RedisLogBrush,
        "Cassandra" => CassandraLogBrush,
        _ => DefaultLogBrush
    });

    private static readonly Dictionary<string, IBrush> HexBrushCache = new(StringComparer.OrdinalIgnoreCase);

    public static readonly IValueConverter HexToBrush = new FuncValueConverter<string?, IBrush>(hex =>
    {
        if (string.IsNullOrWhiteSpace(hex))
            return NavActiveBg;
        if (HexBrushCache.TryGetValue(hex, out var cached))
            return cached;
        if (Color.TryParse(hex, out var color))
        {
            var brush = new ImmutableSolidColorBrush(color);
            HexBrushCache[hex] = brush;
            return brush;
        }
        return NavActiveBg;
    });

    public static readonly IValueConverter BoolToNavBackground = new FuncValueConverter<bool, IBrush>(active =>
        active ? NavActiveBg : Brushes.Transparent);

    public static readonly IValueConverter BoolToNavForeground = new FuncValueConverter<bool, IBrush>(active =>
        active ? NavActiveFg : NavInactiveFg);
}
