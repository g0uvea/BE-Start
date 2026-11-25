using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BEStart.Models;

namespace BEStart.ViewModels;

public partial class ServiceCardViewModel : ObservableObject
{
    private readonly Func<ServiceCardViewModel, Task> _onPrimaryAction;
    private readonly Func<ServiceCardViewModel, PackageRelease, Task> _onVersionSelected;
    private readonly Func<ServiceCardViewModel, Task> _onUninstallSelected;
    private readonly Action<ServiceCardViewModel> _onOpenCli;
    private readonly Action<ServiceCardViewModel> _onOpenConfig;
    private readonly Action<ServiceCardViewModel> _onOpenFolder;

    private bool _suppressSelectionCallback;

    public ServiceDescriptor Descriptor { get; }
    public ServiceKind Kind => Descriptor.Kind;
    public ServiceCategory Category => Descriptor.Category;
    public string Title => Descriptor.Title;
    public string ShortName => Descriptor.ShortName;
    public string BadgeText => Descriptor.BadgeText;
    public string BadgeBgHex => Descriptor.BadgeBgHex;
    public string BadgeFgHex => Descriptor.BadgeFgHex;
    public string ConnectionHint => Descriptor.ConnectionHint;
    public string ConfigFileName => Descriptor.ConfigFileName;
    public string? CliLabel => Descriptor.CliLabel;
    public bool HasCli => !string.IsNullOrWhiteSpace(CliLabel);

    [ObservableProperty] private string _host = "127.0.0.1";
    [ObservableProperty] private int _port;
    [ObservableProperty] private ServiceState _state = ServiceState.NotInstalled;
    [ObservableProperty] private int? _pid;
    [ObservableProperty] private string _activeVersionDisplay = "Não instalado";
    [ObservableProperty] private bool _isAnyVersionInstalled;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private double _installProgress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private PackageRelease? _selectedVersion;

    public ObservableCollection<PackageRelease> Versions { get; } = [];

    public bool IsRunning => State == ServiceState.Running;

    public string EndpointDisplay => Kind == ServiceKind.Php
        ? $"http://{Host}:{Port}"
        : $"{Host}:{Port}";

    public string PidLabel => Pid.HasValue
        ? $"PID {Pid.Value}"
        : (IsRunning ? "Ativo" : "Offline");

    public bool IsSelectedVersionInstalled =>
        SelectedVersion is { IsInstalled: true } || (SelectedVersion is null && IsAnyVersionInstalled);

    public string PrimaryActionLabel
    {
        get
        {
            if (IsInstalling) return "Instalando...";
            if (!IsSelectedVersionInstalled) return "Instalar";
            return IsRunning ? "Parar" : "Iniciar";
        }
    }

    public bool IsPrimaryButtonSuccess => IsSelectedVersionInstalled && !IsRunning && !IsInstalling;
    public bool IsPrimaryButtonDanger => IsSelectedVersionInstalled && IsRunning && !IsInstalling;
    public bool IsPrimaryButtonPrimary => !IsSelectedVersionInstalled || IsInstalling;

    public bool CanUninstallSelected =>
        SelectedVersion is { IsInstalled: true } &&
        !IsInstalling &&
        !(IsRunning && SelectedVersion.IsActive);

    public bool CanOpenCli => HasCli && (Kind == ServiceKind.Php ? IsRunning : IsRunning);
    public bool CanOpenConfig => IsAnyVersionInstalled;

    public ServiceCardViewModel(
        ServiceDescriptor descriptor,
        Func<ServiceCardViewModel, Task> onPrimaryAction,
        Func<ServiceCardViewModel, PackageRelease, Task> onVersionSelected,
        Func<ServiceCardViewModel, Task> onUninstallSelected,
        Action<ServiceCardViewModel> onOpenCli,
        Action<ServiceCardViewModel> onOpenConfig,
        Action<ServiceCardViewModel> onOpenFolder)
    {
        Descriptor = descriptor;
        _port = descriptor.DefaultPort;
        _onPrimaryAction = onPrimaryAction;
        _onVersionSelected = onVersionSelected;
        _onUninstallSelected = onUninstallSelected;
        _onOpenCli = onOpenCli;
        _onOpenConfig = onOpenConfig;
        _onOpenFolder = onOpenFolder;
    }

    public void SetSelectedVersionSilently(PackageRelease? release)
    {
        _suppressSelectionCallback = true;
        try
        {
            SelectedVersion = release;
        }
        finally
        {
            _suppressSelectionCallback = false;
        }
        NotifyActionProperties();
    }

    public void NotifyActionProperties()
    {
        OnPropertyChanged(nameof(IsSelectedVersionInstalled));
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(IsPrimaryButtonSuccess));
        OnPropertyChanged(nameof(IsPrimaryButtonDanger));
        OnPropertyChanged(nameof(IsPrimaryButtonPrimary));
        OnPropertyChanged(nameof(CanUninstallSelected));
        OnPropertyChanged(nameof(CanOpenCli));
        OnPropertyChanged(nameof(CanOpenConfig));
    }

    partial void OnHostChanged(string value) => OnPropertyChanged(nameof(EndpointDisplay));
    partial void OnPortChanged(int value) => OnPropertyChanged(nameof(EndpointDisplay));
    partial void OnPidChanged(int? value) => OnPropertyChanged(nameof(PidLabel));

    partial void OnStateChanged(ServiceState value)
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(PidLabel));
        NotifyActionProperties();
    }

    partial void OnIsAnyVersionInstalledChanged(bool value) => NotifyActionProperties();
    partial void OnIsInstallingChanged(bool value) => NotifyActionProperties();

    partial void OnSelectedVersionChanged(PackageRelease? value)
    {
        NotifyActionProperties();
        if (!_suppressSelectionCallback && value is not null && value.IsInstalled)
        {
            _ = _onVersionSelected(this, value);
        }
    }

    [RelayCommand]
    private Task PrimaryActionAsync() => _onPrimaryAction(this);

    [RelayCommand]
    private Task UninstallSelectedAsync() => _onUninstallSelected(this);

    [RelayCommand]
    private void OpenCli() => _onOpenCli(this);

    [RelayCommand]
    private void OpenConfig() => _onOpenConfig(this);

    [RelayCommand]
    private void OpenFolder() => _onOpenFolder(this);
}
