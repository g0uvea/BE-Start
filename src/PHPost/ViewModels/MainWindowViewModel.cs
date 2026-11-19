using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PHPost.Models;
using PHPost.Services;

namespace PHPost.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private const int MaxLogEntries = 800;

    private readonly SettingsService _settingsService;
    private readonly LogService _logService;
    private readonly PackageCatalogService _catalogService;
    private readonly InstallerService _installerService;
    private readonly ProcessManagerService _processManager;
    private readonly DesktopSetupService _desktopSetup;

    private readonly List<LogEntry> _allLogs = [];
    private AppSettings _settings;

    // Navigation
    [ObservableProperty] private int _selectedTabIndex;
    public bool IsDashboardTab => SelectedTabIndex == 0;
    public bool IsPackagesTab => SelectedTabIndex == 1;
    public bool IsProjectsTab => SelectedTabIndex == 2;
    public bool IsLogsTab => SelectedTabIndex == 3;
    public bool IsSettingsTab => SelectedTabIndex == 4;

    // Banner / Global Activity
    [ObservableProperty] private string _statusMessage = "Pronto para configurar e iniciar seu ambiente local.";
    [ObservableProperty] private bool _isQuickSetupRunning;
    [ObservableProperty] private bool _isLoadingCatalog;

    // PHP Status
    [ObservableProperty] private ServiceState _phpState = ServiceState.NotInstalled;
    [ObservableProperty] private string _phpVersionDisplay = "Não instalado";
    [ObservableProperty] private int? _phpPid;
    [ObservableProperty] private double _phpInstallProgress;
    [ObservableProperty] private string _phpProgressText = "";
    [ObservableProperty] private bool _isPhpInstalling;

    // MariaDB Status
    [ObservableProperty] private ServiceState _mariaDbState = ServiceState.NotInstalled;
    [ObservableProperty] private string _mariaDbVersionDisplay = "Não instalado";
    [ObservableProperty] private int? _mariaDbPid;
    [ObservableProperty] private double _mariaDbInstallProgress;
    [ObservableProperty] private string _mariaDbProgressText = "";
    [ObservableProperty] private bool _isMariaDbInstalling;

    // Settings Form Properties
    [ObservableProperty] private string _baseDirectory = "";
    [ObservableProperty] private string _projectsDirectory = "";
    [ObservableProperty] private string _phpHost = "127.0.0.1";
    [ObservableProperty] private decimal? _phpPort = 8000;
    [ObservableProperty] private decimal? _mariaDbPort = 3306;
    [ObservableProperty] private bool _autoConfigurePath = true;
    [ObservableProperty] private bool _autoCreateDesktopShortcut = true;
    [ObservableProperty] private bool _autoStartServicesOnLaunch;
    [ObservableProperty] private string _pathStatusSummary = "Verificando PATH do Windows...";

    // Projects
    [ObservableProperty] private string _newProjectName = "";

    // Logs
    [ObservableProperty] private string _logFilter = "Todos";

    public ObservableCollection<PackageRelease> PhpReleases { get; } = [];
    public ObservableCollection<PackageRelease> MariaDbReleases { get; } = [];
    public ObservableCollection<ProjectInfo> Projects { get; } = [];
    public ObservableCollection<LogEntry> FilteredLogs { get; } = [];

    // Computed display helpers
    public bool IsPhpInstalled => !string.IsNullOrWhiteSpace(_settings.ActivePhpVersion) &&
                                  _installerService.IsVersionInstalled(_settings, ServiceKind.Php, _settings.ActivePhpVersion);
    public bool IsMariaDbInstalled => !string.IsNullOrWhiteSpace(_settings.ActiveMariaDbVersion) &&
                                      _installerService.IsVersionInstalled(_settings, ServiceKind.MariaDb, _settings.ActiveMariaDbVersion);

    public bool IsPhpRunning => PhpState == ServiceState.Running;
    public bool IsMariaDbRunning => MariaDbState == ServiceState.Running;
    public bool IsAnyServiceRunning => IsPhpRunning || IsMariaDbRunning;
    public bool IsBothInstalled => IsPhpInstalled && IsMariaDbInstalled;
    public bool NeedsInitialSetup => !IsBothInstalled;

    public string PhpUrl => $"http://{PhpHost}:{(int)(PhpPort ?? 8000)}";
    public string MariaDbEndpoint => $"127.0.0.1:{(int)(MariaDbPort ?? 3306)}";
    public string PhpPrimaryActionLabel => !IsPhpInstalled ? "Baixar PHP" : (IsPhpRunning ? "Parar PHP" : "Iniciar PHP");
    public string MariaDbPrimaryActionLabel => !IsMariaDbInstalled ? "Baixar MariaDB" : (IsMariaDbRunning ? "Parar MariaDB" : "Iniciar MariaDB");
    public string PhpPidLabel => PhpPid.HasValue ? $"PID {PhpPid.Value}" : "Offline";
    public string MariaDbPidLabel => MariaDbPid.HasValue ? $"PID {MariaDbPid.Value}" : "Offline";

    public bool IsFilterAll => LogFilter == "Todos";
    public bool IsFilterPhPost => LogFilter == "PHPost";
    public bool IsFilterPhp => LogFilter == "PHP";
    public bool IsFilterMariaDb => LogFilter == "MariaDB";

    public MainWindowViewModel(
        SettingsService settingsService,
        LogService logService,
        PackageCatalogService catalogService,
        InstallerService installerService,
        ProcessManagerService processManager,
        DesktopSetupService desktopSetup)
    {
        _settingsService = settingsService;
        _logService = logService;
        _catalogService = catalogService;
        _installerService = installerService;
        _processManager = processManager;
        _desktopSetup = desktopSetup;

        _settings = _settingsService.Load();
        _settingsService.EnsureDirectories(_settings);
        LoadSettingsIntoForm();

        _logService.EntryAdded += OnLogEntryAdded;
        _processManager.StateChanged += OnProcessStateChanged;

        DetectInstalledRuntimes();
        _desktopSetup.EnsureStarterWorkspace(_settings);
        RefreshProjects();
        RefreshPathStatus();

        _logService.Info("PHPost", "PHPost inicializado. Pronto para baixar e gerenciar PHP + MariaDB.");
    }

    public async Task InitializeAsync()
    {
        await RefreshCatalogAsync();

        if (_settings.AutoStartServicesOnLaunch && IsBothInstalled)
        {
            await StartAllAsync();
        }
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsDashboardTab));
        OnPropertyChanged(nameof(IsPackagesTab));
        OnPropertyChanged(nameof(IsProjectsTab));
        OnPropertyChanged(nameof(IsLogsTab));
        OnPropertyChanged(nameof(IsSettingsTab));

        if (value == 2)
            RefreshProjects();
        else if (value == 4)
            RefreshPathStatus();
    }

    partial void OnPhpStateChanged(ServiceState value)
    {
        OnPropertyChanged(nameof(IsPhpRunning));
        OnPropertyChanged(nameof(IsAnyServiceRunning));
        OnPropertyChanged(nameof(PhpPrimaryActionLabel));
    }

    partial void OnMariaDbStateChanged(ServiceState value)
    {
        OnPropertyChanged(nameof(IsMariaDbRunning));
        OnPropertyChanged(nameof(IsAnyServiceRunning));
        OnPropertyChanged(nameof(MariaDbPrimaryActionLabel));
    }

    partial void OnPhpPidChanged(int? value) => OnPropertyChanged(nameof(PhpPidLabel));
    partial void OnMariaDbPidChanged(int? value) => OnPropertyChanged(nameof(MariaDbPidLabel));
    partial void OnPhpHostChanged(string value) => OnPropertyChanged(nameof(PhpUrl));
    partial void OnPhpPortChanged(decimal? value) => OnPropertyChanged(nameof(PhpUrl));
    partial void OnMariaDbPortChanged(decimal? value) => OnPropertyChanged(nameof(MariaDbEndpoint));

    partial void OnLogFilterChanged(string value)
    {
        OnPropertyChanged(nameof(IsFilterAll));
        OnPropertyChanged(nameof(IsFilterPhPost));
        OnPropertyChanged(nameof(IsFilterPhp));
        OnPropertyChanged(nameof(IsFilterMariaDb));
        RebuildFilteredLogs();
    }

    [RelayCommand]
    private void SelectTab(string indexText)
    {
        if (int.TryParse(indexText, out var idx))
            SelectedTabIndex = idx;
    }

    [RelayCommand]
    private async Task RefreshCatalogAsync()
    {
        if (IsLoadingCatalog)
            return;

        IsLoadingCatalog = true;
        StatusMessage = "Consultando versões oficiais de PHP e MariaDB na web...";

        try
        {
            var phpTask = _catalogService.FetchPhpReleasesAsync();
            var mariaTask = _catalogService.FetchMariaDbReleasesAsync();
            await Task.WhenAll(phpTask, mariaTask);

            PhpReleases.Clear();
            foreach (var r in phpTask.Result)
                PhpReleases.Add(r);

            MariaDbReleases.Clear();
            foreach (var r in mariaTask.Result)
                MariaDbReleases.Add(r);

            SyncCatalogInstallationFlags();

            StatusMessage = PhpReleases.Count > 0 || MariaDbReleases.Count > 0
                ? $"Catálogo online carregado ({PhpReleases.Count} versões PHP, {MariaDbReleases.Count} versões MariaDB)."
                : "Não foi possível carregar o catálogo online. Verifique sua conexão com a internet.";
        }
        finally
        {
            IsLoadingCatalog = false;
        }
    }

    [RelayCommand]
    private async Task QuickSetupAllAsync()
    {
        if (IsQuickSetupRunning)
            return;

        IsQuickSetupRunning = true;
        StatusMessage = "Preparando instalação automática de PHP + MariaDB...";

        try
        {
            if (PhpReleases.Count == 0 || MariaDbReleases.Count == 0)
                await RefreshCatalogAsync();

            if (!IsPhpInstalled)
            {
                var phpRel = PhpReleases.FirstOrDefault(r => r.IsRecommended) ?? PhpReleases.FirstOrDefault();
                if (phpRel is null)
                {
                    StatusMessage = "Não foi possível localizar uma versão do PHP para baixar.";
                    return;
                }
                await InstallReleaseInternalAsync(phpRel);
            }

            if (!IsMariaDbInstalled)
            {
                var mariaRel = MariaDbReleases.FirstOrDefault(r => r.IsRecommended) ?? MariaDbReleases.FirstOrDefault();
                if (mariaRel is null)
                {
                    StatusMessage = "Não foi possível localizar uma versão do MariaDB para baixar.";
                    return;
                }
                await InstallReleaseInternalAsync(mariaRel);
            }

            if (_settings.AutoConfigurePath)
                _desktopSetup.ConfigureUserPath(_settings);

            if (_settings.AutoCreateDesktopShortcut)
                _desktopSetup.CreateDesktopShortcuts(_settings);

            _desktopSetup.EnsureStarterWorkspace(_settings);
            RefreshPathStatus();
            RefreshProjects();

            await StartAllAsync();
            StatusMessage = "Ambiente PHP + MariaDB instalado, configurado no desktop e rodando!";
        }
        catch (Exception ex)
        {
            _logService.Error("PHPost", $"Erro durante a instalação automática: {ex.Message}");
            StatusMessage = $"Erro na instalação automática: {ex.Message}";
        }
        finally
        {
            IsQuickSetupRunning = false;
        }
    }

    [RelayCommand]
    private async Task StartAllAsync()
    {
        if (!IsPhpInstalled && !IsMariaDbInstalled)
        {
            await QuickSetupAllAsync();
            return;
        }

        StatusMessage = "Iniciando serviços...";
        var tasks = new List<Task>();
        if (IsMariaDbInstalled && !IsMariaDbRunning)
            tasks.Add(_processManager.StartMariaDbAsync(_settings));
        if (IsPhpInstalled && !IsPhpRunning)
            tasks.Add(_processManager.StartPhpAsync(_settings));

        await Task.WhenAll(tasks);
        StatusMessage = "Serviços iniciados.";
    }

    [RelayCommand]
    private async Task StopAllAsync()
    {
        StatusMessage = "Parando serviços...";
        await _processManager.StopAllAsync(_settings);
        StatusMessage = "Todos os serviços foram parados.";
    }

    [RelayCommand]
    private async Task PrimaryPhpActionAsync()
    {
        if (IsPhpInstalling)
            return;

        if (!IsPhpInstalled)
        {
            if (PhpReleases.Count == 0)
                await RefreshCatalogAsync();

            var rel = PhpReleases.FirstOrDefault(r => r.IsRecommended) ?? PhpReleases.FirstOrDefault();
            if (rel is not null)
                await InstallReleaseInternalAsync(rel);
            else
                StatusMessage = "Nenhuma versão do PHP disponível no catálogo.";
            return;
        }

        if (IsPhpRunning)
            await _processManager.StopPhpAsync();
        else
            await _processManager.StartPhpAsync(_settings);
    }

    [RelayCommand]
    private async Task PrimaryMariaDbActionAsync()
    {
        if (IsMariaDbInstalling)
            return;

        if (!IsMariaDbInstalled)
        {
            if (MariaDbReleases.Count == 0)
                await RefreshCatalogAsync();

            var rel = MariaDbReleases.FirstOrDefault(r => r.IsRecommended) ?? MariaDbReleases.FirstOrDefault();
            if (rel is not null)
                await InstallReleaseInternalAsync(rel);
            else
                StatusMessage = "Nenhuma versão do MariaDB disponível no catálogo.";
            return;
        }

        if (IsMariaDbRunning)
            await _processManager.StopMariaDbAsync(_settings);
        else
            await _processManager.StartMariaDbAsync(_settings);
    }

    [RelayCommand]
    private async Task InstallOrActivateReleaseAsync(PackageRelease? release)
    {
        if (release is null)
            return;

        if (release.Kind == ServiceKind.Php && IsPhpInstalling)
            return;
        if (release.Kind == ServiceKind.MariaDb && IsMariaDbInstalling)
            return;

        if (release.IsInstalled)
        {
            await ActivateVersionAsync(release.Kind, release.Version);
            return;
        }

        await InstallReleaseInternalAsync(release);
    }

    [RelayCommand]
    private async Task UninstallReleaseAsync(PackageRelease? release)
    {
        if (release is null || !release.IsInstalled)
            return;

        if (release.Kind == ServiceKind.Php && release.IsActive && IsPhpRunning)
            await _processManager.StopPhpAsync();

        if (release.Kind == ServiceKind.MariaDb && release.IsActive && IsMariaDbRunning)
            await _processManager.StopMariaDbAsync(_settings);

        try
        {
            _installerService.UninstallVersion(_settings, release.Kind, release.Version);

            if (release.Kind == ServiceKind.Php && _settings.ActivePhpVersion == release.Version)
                _settings.ActivePhpVersion = _installerService.GetInstalledVersions(_settings, ServiceKind.Php).FirstOrDefault();
            else if (release.Kind == ServiceKind.MariaDb && _settings.ActiveMariaDbVersion == release.Version)
                _settings.ActiveMariaDbVersion = _installerService.GetInstalledVersions(_settings, ServiceKind.MariaDb).FirstOrDefault();

            _settingsService.Save(_settings);
            DetectInstalledRuntimes();
            SyncCatalogInstallationFlags();
            StatusMessage = $"{release.DisplayName} removido.";
        }
        catch (Exception ex)
        {
            _logService.Error("PHPost", $"Falha ao remover {release.DisplayName}: {ex.Message}");
            StatusMessage = $"Erro ao remover: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenPhpBrowser()
    {
        _desktopSetup.OpenInShell(PhpUrl);
    }

    [RelayCommand]
    private void OpenPhpIni()
    {
        if (string.IsNullOrWhiteSpace(_settings.ActivePhpVersion))
            return;

        var phpDir = _settings.GetPhpInstallDirectory(_settings.ActivePhpVersion);
        var iniPath = Path.Combine(phpDir, "php.ini");
        if (!File.Exists(iniPath) && Directory.Exists(phpDir))
            _installerService.ConfigurePhpIni(phpDir);

        if (File.Exists(iniPath))
            _desktopSetup.OpenInShell(iniPath);
    }

    [RelayCommand]
    private void OpenPhpFolder()
    {
        var dir = !string.IsNullOrWhiteSpace(_settings.ActivePhpVersion)
            ? _settings.GetPhpInstallDirectory(_settings.ActivePhpVersion)
            : _settings.PhpRuntimesDirectory;
        Directory.CreateDirectory(dir);
        _desktopSetup.OpenInShell(dir);
    }

    [RelayCommand]
    private void OpenMariaDbIni()
    {
        var iniPath = Path.Combine(_settings.MariaDbDataDirectory, "my.ini");
        if (File.Exists(iniPath))
            _desktopSetup.OpenInShell(iniPath);
        else
        {
            Directory.CreateDirectory(_settings.MariaDbDataDirectory);
            _desktopSetup.OpenInShell(_settings.MariaDbDataDirectory);
        }
    }

    [RelayCommand]
    private void OpenMariaDbFolder()
    {
        var dir = !string.IsNullOrWhiteSpace(_settings.ActiveMariaDbVersion)
            ? _settings.GetMariaDbInstallDirectory(_settings.ActiveMariaDbVersion)
            : _settings.MariaDbRuntimesDirectory;
        Directory.CreateDirectory(dir);
        _desktopSetup.OpenInShell(dir);
    }

    [RelayCommand]
    private void OpenMariaDbCli()
    {
        _desktopSetup.OpenMariaDbCli(_settings);
    }

    [RelayCommand]
    private void OpenProjectsFolder()
    {
        Directory.CreateDirectory(_settings.ProjectsDirectory);
        _desktopSetup.OpenInShell(_settings.ProjectsDirectory);
    }

    [RelayCommand]
    private void OpenTerminal()
    {
        _desktopSetup.OpenConfiguredTerminal(_settings);
    }

    [RelayCommand]
    private void CreateProject()
    {
        if (string.IsNullOrWhiteSpace(NewProjectName))
        {
            StatusMessage = "Digite um nome para o novo projeto (ex: meu-site).";
            return;
        }

        try
        {
            var project = _desktopSetup.CreateProject(_settings, NewProjectName);
            NewProjectName = "";
            RefreshProjects();
            StatusMessage = $"Projeto '{project.Name}' criado com sucesso!";

            if (IsPhpRunning)
                _desktopSetup.OpenInShell(project.Url);
            else
                _desktopSetup.OpenInShell(project.FullPath);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erro ao criar projeto: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RefreshProjects()
    {
        Projects.Clear();
        foreach (var p in _desktopSetup.ScanProjects(_settings))
            Projects.Add(p);
    }

    [RelayCommand]
    private void OpenProjectInBrowser(ProjectInfo? project)
    {
        if (project is null)
            return;
        _desktopSetup.OpenInShell(project.Url);
    }

    [RelayCommand]
    private void OpenProjectFolder(ProjectInfo? project)
    {
        if (project is null)
            return;
        _desktopSetup.OpenInShell(project.FullPath);
    }

    [RelayCommand]
    private void SetLogFilter(string filter)
    {
        LogFilter = filter;
    }

    [RelayCommand]
    private void ClearLogs()
    {
        _allLogs.Clear();
        FilteredLogs.Clear();
    }

    [RelayCommand]
    private void SaveSettings()
    {
        _settings.BaseDirectory = string.IsNullOrWhiteSpace(BaseDirectory)
            ? AppSettings.GetDefaultBaseDirectory()
            : BaseDirectory.Trim();
        _settings.ProjectsDirectory = string.IsNullOrWhiteSpace(ProjectsDirectory)
            ? Path.Combine(_settings.BaseDirectory, "www")
            : ProjectsDirectory.Trim();
        _settings.PhpHost = string.IsNullOrWhiteSpace(PhpHost) ? "127.0.0.1" : PhpHost.Trim();
        var phpPortInt = (int)(PhpPort ?? 8000);
        var mariaPortInt = (int)(MariaDbPort ?? 3306);
        _settings.PhpPort = phpPortInt is >= 1 and <= 65535 ? phpPortInt : 8000;
        _settings.MariaDbPort = mariaPortInt is >= 1 and <= 65535 ? mariaPortInt : 3306;
        PhpPort = _settings.PhpPort;
        MariaDbPort = _settings.MariaDbPort;
        _settings.AutoConfigurePath = AutoConfigurePath;
        _settings.AutoCreateDesktopShortcut = AutoCreateDesktopShortcut;
        _settings.AutoStartServicesOnLaunch = AutoStartServicesOnLaunch;

        _settingsService.Save(_settings);
        _settingsService.EnsureDirectories(_settings);
        _desktopSetup.EnsureStarterWorkspace(_settings);

        DetectInstalledRuntimes();
        SyncCatalogInstallationFlags();
        RefreshProjects();
        RefreshPathStatus();

        _logService.Success("PHPost", "Configurações salvas com sucesso.");
        StatusMessage = "Configurações salvas! Reinicie serviços ativos se alterou portas ou pasta www.";
    }

    [RelayCommand]
    private void ConfigureUserPath()
    {
        if (_desktopSetup.ConfigureUserPath(_settings))
        {
            RefreshPathStatus();
            StatusMessage = "PATH do usuário configurado! Novos terminais já reconhecem php e mariadb.";
        }
        else
        {
            StatusMessage = "Instale o PHP ou MariaDB primeiro para adicioná-los ao PATH.";
        }
    }

    [RelayCommand]
    private void CreateDesktopShortcuts()
    {
        if (_desktopSetup.CreateDesktopShortcuts(_settings))
            StatusMessage = "Atalhos criados na Área de Trabalho com sucesso!";
        else
            StatusMessage = "Não foi possível criar os atalhos na Área de Trabalho.";
    }

    [RelayCommand]
    private void OpenVcRedistDownload()
    {
        _desktopSetup.OpenInShell("https://aka.ms/vs/17/release/vc_redist.x64.exe");
    }

    public void ShutdownServices()
    {
        _processManager.StopAllSync(_settings);
    }

    private async Task InstallReleaseInternalAsync(PackageRelease release)
    {
        if (release.Kind == ServiceKind.Php)
        {
            if (IsPhpRunning)
                await _processManager.StopPhpAsync();

            IsPhpInstalling = true;
            PhpState = ServiceState.Installing;
            PhpInstallProgress = 0;
            PhpProgressText = $"Preparando {release.DisplayName}...";
        }
        else
        {
            if (IsMariaDbRunning)
                await _processManager.StopMariaDbAsync(_settings);

            IsMariaDbInstalling = true;
            MariaDbState = ServiceState.Installing;
            MariaDbInstallProgress = 0;
            MariaDbProgressText = $"Preparando {release.DisplayName}...";
        }

        var progress = new Progress<InstallProgress>(p =>
        {
            if (release.Kind == ServiceKind.Php)
            {
                PhpInstallProgress = p.Percentage;
                PhpProgressText = p.Stage;
            }
            else
            {
                MariaDbInstallProgress = p.Percentage;
                MariaDbProgressText = p.Stage;
            }
        });

        try
        {
            await _installerService.InstallReleaseAsync(release, _settings, progress);
            await ActivateVersionAsync(release.Kind, release.Version, quiet: true);

            if (_settings.AutoConfigurePath)
            {
                _desktopSetup.ConfigureUserPath(_settings);
                RefreshPathStatus();
            }

            StatusMessage = $"{release.DisplayName} instalado e pronto para iniciar!";
        }
        catch (Exception ex)
        {
            _logService.Error(release.Kind == ServiceKind.Php ? "PHP" : "MariaDB", $"Erro na instalação: {ex.Message}");
            StatusMessage = $"Falha ao instalar {release.DisplayName}: {ex.Message}";

            if (release.Kind == ServiceKind.Php)
                PhpState = IsPhpInstalled ? ServiceState.Stopped : ServiceState.Error;
            else
                MariaDbState = IsMariaDbInstalled ? ServiceState.Stopped : ServiceState.Error;
        }
        finally
        {
            if (release.Kind == ServiceKind.Php)
                IsPhpInstalling = false;
            else
                IsMariaDbInstalling = false;

            SyncCatalogInstallationFlags();
            NotifyInstallComputedProperties();
        }
    }

    private async Task ActivateVersionAsync(ServiceKind kind, string version, bool quiet = false)
    {
        if (kind == ServiceKind.Php)
        {
            if (IsPhpRunning)
                await _processManager.StopPhpAsync();
            _settings.ActivePhpVersion = version;
            PhpVersionDisplay = $"PHP {version}";
            PhpState = ServiceState.Stopped;
        }
        else
        {
            if (IsMariaDbRunning)
                await _processManager.StopMariaDbAsync(_settings);
            _settings.ActiveMariaDbVersion = version;
            MariaDbVersionDisplay = $"MariaDB {version}";
            MariaDbState = ServiceState.Stopped;
        }

        _settingsService.Save(_settings);

        if (_settings.AutoConfigurePath)
        {
            _desktopSetup.ConfigureUserPath(_settings);
            RefreshPathStatus();
        }

        SyncCatalogInstallationFlags();
        NotifyInstallComputedProperties();

        if (!quiet)
        {
            var label = kind == ServiceKind.Php ? $"PHP {version}" : $"MariaDB {version}";
            _logService.Success("PHPost", $"{label} definido como versão ativa.");
            StatusMessage = $"{label} ativado!";
        }
    }

    private void DetectInstalledRuntimes()
    {
        var phpVersions = _installerService.GetInstalledVersions(_settings, ServiceKind.Php);
        if (string.IsNullOrWhiteSpace(_settings.ActivePhpVersion) || !phpVersions.Contains(_settings.ActivePhpVersion))
            _settings.ActivePhpVersion = phpVersions.FirstOrDefault();

        var mariaVersions = _installerService.GetInstalledVersions(_settings, ServiceKind.MariaDb);
        if (string.IsNullOrWhiteSpace(_settings.ActiveMariaDbVersion) || !mariaVersions.Contains(_settings.ActiveMariaDbVersion))
            _settings.ActiveMariaDbVersion = mariaVersions.FirstOrDefault();

        _settingsService.Save(_settings);

        PhpVersionDisplay = !string.IsNullOrWhiteSpace(_settings.ActivePhpVersion)
            ? $"PHP {_settings.ActivePhpVersion}"
            : "Não instalado";
        PhpState = _processManager.IsPhpRunning
            ? ServiceState.Running
            : (IsPhpInstalled ? ServiceState.Stopped : ServiceState.NotInstalled);

        MariaDbVersionDisplay = !string.IsNullOrWhiteSpace(_settings.ActiveMariaDbVersion)
            ? $"MariaDB {_settings.ActiveMariaDbVersion}"
            : "Não instalado";
        MariaDbState = _processManager.IsMariaDbRunning
            ? ServiceState.Running
            : (IsMariaDbInstalled ? ServiceState.Stopped : ServiceState.NotInstalled);

        NotifyInstallComputedProperties();
    }

    private void SyncCatalogInstallationFlags()
    {
        foreach (var r in PhpReleases)
        {
            r.IsInstalled = _installerService.IsVersionInstalled(_settings, ServiceKind.Php, r.Version);
            r.IsActive = r.IsInstalled && string.Equals(_settings.ActivePhpVersion, r.Version, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var r in MariaDbReleases)
        {
            r.IsInstalled = _installerService.IsVersionInstalled(_settings, ServiceKind.MariaDb, r.Version);
            r.IsActive = r.IsInstalled && string.Equals(_settings.ActiveMariaDbVersion, r.Version, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void NotifyInstallComputedProperties()
    {
        OnPropertyChanged(nameof(IsPhpInstalled));
        OnPropertyChanged(nameof(IsMariaDbInstalled));
        OnPropertyChanged(nameof(IsBothInstalled));
        OnPropertyChanged(nameof(NeedsInitialSetup));
        OnPropertyChanged(nameof(PhpPrimaryActionLabel));
        OnPropertyChanged(nameof(MariaDbPrimaryActionLabel));
    }

    private void LoadSettingsIntoForm()
    {
        BaseDirectory = _settings.BaseDirectory;
        ProjectsDirectory = _settings.ProjectsDirectory;
        PhpHost = _settings.PhpHost;
        PhpPort = _settings.PhpPort;
        MariaDbPort = _settings.MariaDbPort;
        AutoConfigurePath = _settings.AutoConfigurePath;
        AutoCreateDesktopShortcut = _settings.AutoCreateDesktopShortcut;
        AutoStartServicesOnLaunch = _settings.AutoStartServicesOnLaunch;
    }

    private void RefreshPathStatus()
    {
        var (phpInPath, mariaInPath) = _desktopSetup.CheckUserPathStatus(_settings);
        PathStatusSummary = (phpInPath, mariaInPath) switch
        {
            (true, true) => "PHP e MariaDB estão configurados no PATH do Windows.",
            (true, false) => "Apenas PHP está no PATH do Windows.",
            (false, true) => "Apenas MariaDB está no PATH do Windows.",
            _ => "PHP e MariaDB ainda não estão no PATH do Windows."
        };
    }

    private void OnProcessStateChanged(ServiceKind kind, ServiceState state, int? pid)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (kind == ServiceKind.Php)
            {
                PhpState = state;
                PhpPid = pid;
            }
            else
            {
                MariaDbState = state;
                MariaDbPid = pid;
            }
        });
    }

    private void OnLogEntryAdded(LogEntry entry)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _allLogs.Add(entry);
            if (_allLogs.Count > MaxLogEntries)
                _allLogs.RemoveAt(0);

            if (MatchesFilter(entry))
            {
                FilteredLogs.Add(entry);
                if (FilteredLogs.Count > MaxLogEntries)
                    FilteredLogs.RemoveAt(0);
            }
        });
    }

    private bool MatchesFilter(LogEntry entry) =>
        LogFilter == "Todos" || string.Equals(entry.Source, LogFilter, StringComparison.OrdinalIgnoreCase);

    private void RebuildFilteredLogs()
    {
        FilteredLogs.Clear();
        foreach (var entry in _allLogs)
        {
            if (MatchesFilter(entry))
                FilteredLogs.Add(entry);
        }
    }

    public void Dispose()
    {
        _logService.EntryAdded -= OnLogEntryAdded;
        _processManager.StateChanged -= OnProcessStateChanged;
        _processManager.Dispose();
    }
}
