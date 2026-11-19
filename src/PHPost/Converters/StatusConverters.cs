using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using PHPost.Models;

namespace PHPost.Converters;

public static class StatusConverters
{
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

    public static readonly IValueConverter StateToBrush = new FuncValueConverter<ServiceState, IBrush>(state => state switch
    {
        ServiceState.Running => new SolidColorBrush(Color.Parse("#22C55E")),
        ServiceState.Starting or ServiceState.Stopping or ServiceState.Installing => new SolidColorBrush(Color.Parse("#F59E0B")),
        ServiceState.Error => new SolidColorBrush(Color.Parse("#EF4444")),
        ServiceState.Stopped => new SolidColorBrush(Color.Parse("#64748B")),
        _ => new SolidColorBrush(Color.Parse("#475569"))
    });

    public static readonly IValueConverter StateToBadgeBackground = new FuncValueConverter<ServiceState, IBrush>(state => state switch
    {
        ServiceState.Running => new SolidColorBrush(Color.Parse("#1A22C55E")),
        ServiceState.Starting or ServiceState.Stopping or ServiceState.Installing => new SolidColorBrush(Color.Parse("#1AF59E0B")),
        ServiceState.Error => new SolidColorBrush(Color.Parse("#1AEF4444")),
        _ => new SolidColorBrush(Color.Parse("#1F94A3B8"))
    });

    public static readonly IValueConverter LogLevelToBrush = new FuncValueConverter<LogLevel, IBrush>(level => level switch
    {
        LogLevel.Success => new SolidColorBrush(Color.Parse("#4ADE80")),
        LogLevel.Warning => new SolidColorBrush(Color.Parse("#FBBF24")),
        LogLevel.Error => new SolidColorBrush(Color.Parse("#F87171")),
        _ => new SolidColorBrush(Color.Parse("#CBD5E1"))
    });

    public static readonly IValueConverter LogSourceToBrush = new FuncValueConverter<string?, IBrush>(src => src switch
    {
        "PHP" => new SolidColorBrush(Color.Parse("#818CF8")),
        "MariaDB" => new SolidColorBrush(Color.Parse("#38BDF8")),
        _ => new SolidColorBrush(Color.Parse("#A78BFA"))
    });

    public static readonly IValueConverter BoolToNavBackground = new FuncValueConverter<bool, IBrush>(active =>
        active ? new SolidColorBrush(Color.Parse("#1E293B")) : Brushes.Transparent);

    public static readonly IValueConverter BoolToNavForeground = new FuncValueConverter<bool, IBrush>(active =>
        active ? new SolidColorBrush(Color.Parse("#F8FAFC")) : new SolidColorBrush(Color.Parse("#94A3B8")));
}
