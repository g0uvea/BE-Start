using CommunityToolkit.Mvvm.ComponentModel;

namespace PHPost.Models;

public partial class PackageRelease : ObservableObject
{
    public ServiceKind Kind { get; init; }
    public string Version { get; init; } = string.Empty;
    public string Series { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public string DownloadUrl { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string SizeLabel { get; init; } = string.Empty;
    public string? Sha256 { get; init; }
    public bool IsRecommended { get; init; }

    [ObservableProperty]
    private bool _isInstalled;

    [ObservableProperty]
    private bool _isActive;

    public string ActionLabel => IsActive ? "Ativa" : (IsInstalled ? "Ativar" : "Instalar");

    partial void OnIsInstalledChanged(bool value) => OnPropertyChanged(nameof(ActionLabel));
    partial void OnIsActiveChanged(bool value) => OnPropertyChanged(nameof(ActionLabel));
}
