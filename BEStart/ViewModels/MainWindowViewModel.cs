using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BEStart.Models;
using BEStart.Services;

namespace BEStart.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private const int MaxLogEntries = 250;

    private readonly SettingsService _settingsService;
    private readonly LogService _logService;
    private readonly PackageCatalogService _catalogService;
    private readonly InstallerService _installerService;
    private readonly ProcessManagerService _processManager;
    private readonly DesktopSetupService _desktopSetup;
    private readonly DBeaverService _dbeaverService;

    private readonly List<LogEntry> _allLogs = [];
    private readonly ConcurrentQueue<LogEntry> _pendingLogs = new();
    private int _logFlushScheduled;
    private readonly Dictionary<ServiceKind, ServiceCardViewModel> _cardsByKind = new();
    private AppSettings _settings;

    // Navigation: 0=SQL, 1=NoSQL, 2=PHP, 3=Logs, 4=Configurações
    [ObservableProperty] private int _selectedTabIndex;
    public bool IsSqlTab => SelectedTabIndex == 0;
    public bool IsNoSqlTab => SelectedTabIndex == 1;
    public bool IsPhpTab => SelectedTabIndex == 2;
    public bool IsLogsTab => SelectedTabIndex == 3;
    public bool IsSettingsTab => SelectedTabIndex == 4;

    // Header status
    [ObservableProperty] private string _statusMessage = "BE-Start pronto. Escolha os serviços para instalar ou iniciar.";
    [ObservableProperty] private bool _isLoadingCatalog;
    [ObservableProperty] private bool _isDBeaverBusy;
    [ObservableProperty] private double _dBeaverProgress;

    public string DBeaverButtonLabel => IsDBeaverBusy ? "Atualizando DBeaver..." : "Iniciar DBeaver";

    // Service Collections
    public ObservableCollection<ServiceCardViewModel> SqlServices { get; } = [];
    public ObservableCollection<ServiceCardViewModel> NoSqlServices { get; } = [];
    public ObservableCollection<ServiceCardViewModel> StatusBarServices { get; } = [];
    public ServiceCardViewModel PhpCard { get; }

    // Composer 2.11.x alongside PHP
    public ObservableCollection<PackageRelease> ComposerVersions { get; } = [];
    [ObservableProperty] private PackageRelease? _selectedComposerVersion;
    [ObservableProperty] private bool _installComposerWithPhp = true;
    [ObservableProperty] private bool _isComposerInstalling;
    [ObservableProperty] private double _composerInstallProgress;
    [ObservableProperty] private string _composerProgressText = "";
    [ObservableProperty] private string _composerStatusDisplay = "Não instalado";
    [ObservableProperty] private bool _isComposerInstalled;

    public string ComposerActionLabel => SelectedComposerVersion is { IsInstalled: true }
        ? "Reinstalar Composer"
        : "Instalar Composer";

    // Projects (inside PHP tab)
    [ObservableProperty] private string _newProjectName = "";
    public ObservableCollection<ProjectInfo> Projects { get; } = [];

    // Logs
    [ObservableProperty] private string _logFilter = "Todos";
    public ObservableCollection<LogEntry> FilteredLogs { get; } = [];
    public bool IsFilterAll => LogFilter == "Todos";
    public bool IsFilterApp => LogFilter == "BE-Start";
    public bool IsFilterSql => LogFilter == "SQL";
    public bool IsFilterNoSql => LogFilter == "NoSQL";
    public bool IsFilterPhp => LogFilter == "PHP";

    // Settings Form
    [ObservableProperty] private string _baseDirectory = "";
    [ObservableProperty] private string _projectsDirectory = "";
    [ObservableProperty] private string _phpHost = "127.0.0.1";
    [ObservableProperty] private decimal? _phpPort = 8000;
    [ObservableProperty] private decimal? _postgreSqlPort = 5432;
    [ObservableProperty] private decimal? _mariaDbPort = 3306;
    [ObservableProperty] private decimal? _mySqlPort = 3307;
    [ObservableProperty] private decimal? _sqlServerPort = 1433;
    [ObservableProperty] private decimal? _mongoDbPort = 27017;
    [ObservableProperty] private decimal? _redisPort = 6379;
    [ObservableProperty] private decimal? _cassandraPort = 9042;
    [ObservableProperty] private bool _autoConfigurePath = true;
    [ObservableProperty] private bool _autoCreateDesktopShortcut = true;
    [ObservableProperty] private bool _autoStartServicesOnLaunch;
    [ObservableProperty] private string _pathStatusSummary = $"Verificando PATH ({PlatformHelper.PlatformDisplayName})...";

    // Platform Display
    public bool IsWindows => PlatformHelper.IsWindows;
    public string DesktopIntegrationTitle => $"Configuração do Desktop & {PlatformHelper.PlatformDisplayName}";
    public string DesktopIntegrationDescription => PlatformHelper.IsWindows
        ? "Adiciona os runtimes instalados ao PATH do usuário (sem Admin) e cria atalhos na Área de Trabalho."
        : $"Adiciona os runtimes instalados ao PATH do terminal ({PlatformHelper.PlatformDisplayName}) e cria atalhos no Desktop.";

    public bool IsAnyServiceRunning => _processManager.IsAnyRunning;

    public MainWindowViewModel(
        SettingsService settingsService,
        LogService logService,
        PackageCatalogService catalogService,
        InstallerService installerService,
        ProcessManagerService processManager,
        DesktopSetupService desktopSetup,
        DBeaverService dbeaverService)
    {
        _settingsService = settingsService;
        _logService = logService;
        _catalogService = catalogService;
        _installerService = installerService;
        _processManager = processManager;
        _desktopSetup = desktopSetup;
        _dbeaverService = dbeaverService;

        _settings = _settingsService.Load();
        _settingsService.EnsureDirectories(_settings);

        // Build service cards from descriptor table
        foreach (var desc in ServiceDescriptor.All)
        {
            if (desc.Kind == ServiceKind.Composer)
                continue;

            var card = new ServiceCardViewModel(
                desc,
                OnCardPrimaryActionAsync,
                OnCardVersionSelectedAsync,
                OnCardUninstallAsync,
                OnCardOpenCli,
                OnCardOpenConfig,
                OnCardOpenFolder);

            _cardsByKind[desc.Kind] = card;
            StatusBarServices.Add(card);

            if (desc.Category == ServiceCategory.SqlDatabase)
                SqlServices.Add(card);
            else if (desc.Category == ServiceCategory.NoSqlDatabase)
                NoSqlServices.Add(card);
        }

        PhpCard = _cardsByKind[ServiceKind.Php];

        LoadSettingsIntoForm();

        _logService.EntryAdded += OnLogEntryAdded;
        _processManager.StateChanged += OnProcessStateChanged;

        DetectAllInstalledRuntimes();
        _desktopSetup.EnsureStarterWorkspace(_settings);
        RefreshProjects();
        RefreshPathStatus();

        _logService.Info("BE-Start", "BE-Start inicializado em modo baixo consumo.");
    }

    public async Task InitializeAsync()
    {
        await LoadCatalogsAsync(forceRefresh: false);

        if (_settings.AutoStartServicesOnLaunch)
        {
            await StartInstalledServicesAsync();
        }
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsSqlTab));
        OnPropertyChanged(nameof(IsNoSqlTab));
        OnPropertyChanged(nameof(IsPhpTab));
        OnPropertyChanged(nameof(IsLogsTab));
        OnPropertyChanged(nameof(IsSettingsTab));

        if (value == 2)
            RefreshProjects();
        else if (value == 4)
            RefreshPathStatus();
    }

    partial void OnSelectedComposerVersionChanged(PackageRelease? value) =>
        OnPropertyChanged(nameof(ComposerActionLabel));

    partial void OnInstallComposerWithPhpChanged(bool value)
    {
        _settings.InstallComposerWithPhp = value;
        _settingsService.Save(_settings);
    }

    partial void OnLogFilterChanged(string value)
    {
        OnPropertyChanged(nameof(IsFilterAll));
        OnPropertyChanged(nameof(IsFilterApp));
        OnPropertyChanged(nameof(IsFilterSql));
        OnPropertyChanged(nameof(IsFilterNoSql));
        OnPropertyChanged(nameof(IsFilterPhp));
        RebuildFilteredLogs();
    }

    [RelayCommand]
    private void SelectTab(string indexText)
    {
        if (int.TryParse(indexText, out var idx))
            SelectedTabIndex = idx;
    }

    [RelayCommand]
    private Task RefreshCatalogCommandActionAsync() => LoadCatalogsAsync(forceRefresh: true);

    private async Task LoadCatalogsAsync(bool forceRefresh)
    {
        if (IsLoadingCatalog)
            return;

        IsLoadingCatalog = true;
        StatusMessage = forceRefresh
            ? "Atualizando catálogo online de versões..."
            : "Carregando catálogo de versões...";

        try
        {
            var catalogs = await _catalogService.FetchAllCatalogsAsync(_settings, forceRefresh);

            foreach (var (kind, card) in _cardsByKind)
            {
                var list = catalogs.TryGetValue(kind, out var fetched) ? fetched : [];
                PopulateCardVersions(card, list);
            }

            var composerList = catalogs.TryGetValue(ServiceKind.Composer, out var comp) ? comp : [];
            PopulateComposerVersions(composerList);

            StatusMessage = "Catálogo pronto. Selecione a versão desejada em qualquer card.";
        }
        finally
        {
            IsLoadingCatalog = false;
        }
    }

    private void PopulateCardVersions(ServiceCardViewModel card, IReadOnlyList<PackageRelease> releases)
    {
        var installedLocal = _installerService.GetInstalledVersions(_settings, card.Kind);
        var activeVer = _settings.GetActiveVersion(card.Kind);

        card.Versions.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in releases)
        {
            r.IsInstalled = _installerService.IsVersionInstalled(_settings, card.Kind, r.Version);
            r.IsActive = r.IsInstalled && string.Equals(activeVer, r.Version, StringComparison.OrdinalIgnoreCase);
            card.Versions.Add(r);
            seen.Add(r.Version);
        }

        // Also include locally installed versions not present in online list
        foreach (var localVer in installedLocal)
        {
            if (seen.Contains(localVer))
                continue;

            card.Versions.Add(new PackageRelease
            {
                Kind = card.Kind,
                Version = localVer,
                DisplayName = $"{card.ShortName} {localVer}",
                Subtitle = "Instalada localmente",
                IsInstalled = true,
                IsActive = string.Equals(activeVer, localVer, StringComparison.OrdinalIgnoreCase)
            });
        }

        var selected = card.Versions.FirstOrDefault(v => v.IsActive)
                       ?? card.Versions.FirstOrDefault(v => v.IsInstalled)
                       ?? card.Versions.FirstOrDefault(v => v.IsRecommended)
                       ?? card.Versions.FirstOrDefault();

        card.SetSelectedVersionSilently(selected);
    }

    private void PopulateComposerVersions(IReadOnlyList<PackageRelease> releases)
    {
        var installedLocal = _installerService.GetInstalledVersions(_settings, ServiceKind.Composer);
        var activeVer = _settings.GetActiveVersion(ServiceKind.Composer);

        ComposerVersions.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in releases)
        {
            r.IsInstalled = _installerService.IsVersionInstalled(_settings, ServiceKind.Composer, r.Version);
            r.IsActive = r.IsInstalled && string.Equals(activeVer, r.Version, StringComparison.OrdinalIgnoreCase);
            ComposerVersions.Add(r);
            seen.Add(r.Version);
        }

        foreach (var localVer in installedLocal)
        {
            if (seen.Contains(localVer)) continue;
            ComposerVersions.Add(new PackageRelease
            {
                Kind = ServiceKind.Composer,
                InstallMethod = PackageInstallMethod.PharDownload,
                Version = localVer,
                DisplayName = $"Composer {localVer}",
                IsInstalled = true,
                IsActive = string.Equals(activeVer, localVer, StringComparison.OrdinalIgnoreCase)
            });
        }

        SelectedComposerVersion = ComposerVersions.FirstOrDefault(v => v.IsActive)
                                  ?? ComposerVersions.FirstOrDefault(v => v.IsInstalled)
                                  ?? ComposerVersions.FirstOrDefault();
    }

    private async Task OnCardPrimaryActionAsync(ServiceCardViewModel card)
    {
        if (card.IsInstalling)
            return;

        var selected = card.SelectedVersion;
        if (selected is not null && !selected.IsInstalled)
        {
            await InstallServiceReleaseAsync(card, selected);
            return;
        }

        if (!card.IsAnyVersionInstalled)
        {
            if (card.Versions.Count == 0)
                await LoadCatalogsAsync(forceRefresh: true);

            var fallback = card.SelectedVersion ?? card.Versions.FirstOrDefault();
            if (fallback is null)
            {
                StatusMessage = $"Nenhuma versão de {card.ShortName} disponível para este sistema.";
                return;
            }
            await InstallServiceReleaseAsync(card, fallback);
            return;
        }

        if (card.IsRunning)
            await _processManager.StopServiceAsync(card.Kind, _settings);
        else
            await _processManager.StartServiceAsync(card.Kind, _settings);

        OnPropertyChanged(nameof(IsAnyServiceRunning));
    }

    private async Task OnCardVersionSelectedAsync(ServiceCardViewModel card, PackageRelease release)
    {
        if (!release.IsInstalled)
            return;

        if (string.Equals(_settings.GetActiveVersion(card.Kind), release.Version, StringComparison.OrdinalIgnoreCase))
            return;

        await ActivateVersionAsync(card.Kind, release.Version, quiet: false);
    }

    private async Task OnCardUninstallAsync(ServiceCardViewModel card)
    {
        var release = card.SelectedVersion;
        if (release is null || !release.IsInstalled)
            return;

        if (card.IsRunning && release.IsActive)
            await _processManager.StopServiceAsync(card.Kind, _settings);

        try
        {
            _installerService.UninstallVersion(_settings, card.Kind, release.Version);
            if (string.Equals(_settings.GetActiveVersion(card.Kind), release.Version, StringComparison.OrdinalIgnoreCase))
            {
                var next = _installerService.GetInstalledVersions(_settings, card.Kind).FirstOrDefault();
                _settings.SetActiveVersion(card.Kind, next);
            }

            _settingsService.Save(_settings);
            DetectSingleRuntime(card.Kind);
            SyncCardFlags(card);
            StatusMessage = $"{release.DisplayName} removido.";
        }
        catch (Exception ex)
        {
            _logService.Error(card.Descriptor.LogSource, $"Erro ao remover: {ex.Message}");
            StatusMessage = $"Erro ao remover {release.DisplayName}: {ex.Message}";
        }
    }

    private async Task InstallServiceReleaseAsync(ServiceCardViewModel card, PackageRelease release)
    {
        if (card.IsRunning)
            await _processManager.StopServiceAsync(card.Kind, _settings);

        card.IsInstalling = true;
        card.State = ServiceState.Installing;
        card.InstallProgress = 0;
        card.ProgressText = $"Preparando {release.DisplayName}...";
        StatusMessage = $"Instalando {release.DisplayName}...";

        var progress = new Progress<InstallProgress>(p =>
        {
            card.InstallProgress = p.Percentage;
            card.ProgressText = p.Stage;
        });

        try
        {
            await _installerService.InstallReleaseAsync(release, _settings, progress);
            await ActivateVersionAsync(card.Kind, release.Version, quiet: true);

            // If PHP was installed and user opted to install Composer together:
            if (card.Kind == ServiceKind.Php && InstallComposerWithPhp)
            {
                await InstallComposerInternalAsync();
            }

            if (_settings.AutoConfigurePath)
            {
                _desktopSetup.ConfigureUserPath(_settings);
                RefreshPathStatus();
            }

            StatusMessage = $"{release.DisplayName} instalado e pronto para iniciar!";
        }
        catch (Exception ex)
        {
            _logService.Error(card.Descriptor.LogSource, $"Erro na instalação: {ex.Message}");
            StatusMessage = $"Falha ao instalar {release.DisplayName}: {ex.Message}";
            card.State = card.IsAnyVersionInstalled ? ServiceState.Stopped : ServiceState.Error;
        }
        finally
        {
            card.IsInstalling = false;
            DetectSingleRuntime(card.Kind);
            SyncCardFlags(card);
            card.SetSelectedVersionSilently(card.Versions.FirstOrDefault(v => v.Version == release.Version) ?? card.SelectedVersion);
        }
    }

    [RelayCommand]
    private Task InstallComposerAsync() => InstallComposerInternalAsync();

    private async Task InstallComposerInternalAsync()
    {
        if (IsComposerInstalling)
            return;

        if (ComposerVersions.Count == 0)
        {
            var fetched = await _catalogService.FetchComposerReleasesAsync();
            PopulateComposerVersions(fetched);
        }

        var release = SelectedComposerVersion ?? ComposerVersions.FirstOrDefault();
        if (release is null)
        {
            StatusMessage = "Nenhuma versão do Composer disponível no catálogo.";
            return;
        }

        IsComposerInstalling = true;
        ComposerInstallProgress = 0;
        ComposerProgressText = $"Instalando {release.DisplayName}...";

        var progress = new Progress<InstallProgress>(p =>
        {
            ComposerInstallProgress = p.Percentage;
            ComposerProgressText = p.Stage;
        });

        try
        {
            await _installerService.InstallReleaseAsync(release, _settings, progress);
            _settings.SetActiveVersion(ServiceKind.Composer, release.Version);
            _settingsService.Save(_settings);

            if (_settings.AutoConfigurePath)
            {
                _desktopSetup.ConfigureUserPath(_settings);
                RefreshPathStatus();
            }

            DetectComposerRuntime();
            foreach (var c in ComposerVersions)
            {
                c.IsInstalled = _installerService.IsVersionInstalled(_settings, ServiceKind.Composer, c.Version);
                c.IsActive = c.IsInstalled && string.Equals(_settings.GetActiveVersion(ServiceKind.Composer), c.Version, StringComparison.OrdinalIgnoreCase);
            }
            OnPropertyChanged(nameof(ComposerActionLabel));
            StatusMessage = $"{release.DisplayName} instalado com sucesso!";
        }
        catch (Exception ex)
        {
            _logService.Error("Composer", $"Erro ao instalar Composer: {ex.Message}");
            StatusMessage = $"Erro ao instalar Composer: {ex.Message}";
        }
        finally
        {
            IsComposerInstalling = false;
        }
    }

    [RelayCommand]
    private void RemoveComposer()
    {
        var active = _settings.GetActiveVersion(ServiceKind.Composer);
        if (string.IsNullOrWhiteSpace(active))
            return;

        _installerService.UninstallVersion(_settings, ServiceKind.Composer, active);
        var next = _installerService.GetInstalledVersions(_settings, ServiceKind.Composer).FirstOrDefault();
        _settings.SetActiveVersion(ServiceKind.Composer, next);
        _settingsService.Save(_settings);
        DetectComposerRuntime();
        foreach (var c in ComposerVersions)
        {
            c.IsInstalled = _installerService.IsVersionInstalled(_settings, ServiceKind.Composer, c.Version);
            c.IsActive = c.IsInstalled && string.Equals(next, c.Version, StringComparison.OrdinalIgnoreCase);
        }
        OnPropertyChanged(nameof(ComposerActionLabel));
        StatusMessage = "Composer removido.";
    }

    private async Task ActivateVersionAsync(ServiceKind kind, string version, bool quiet)
    {
        if (_processManager.IsRunning(kind))
            await _processManager.StopServiceAsync(kind, _settings);

        _settings.SetActiveVersion(kind, version);
        _settingsService.Save(_settings);

        if (_settings.AutoConfigurePath)
        {
            _desktopSetup.ConfigureUserPath(_settings);
            RefreshPathStatus();
        }

        DetectSingleRuntime(kind);
        if (_cardsByKind.TryGetValue(kind, out var card))
            SyncCardFlags(card);

        if (!quiet)
        {
            var shortName = ServiceDescriptor.Get(kind).ShortName;
            _logService.Success("BE-Start", $"{shortName} {version} definido como ativo.");
            StatusMessage = $"{shortName} {version} ativado!";
        }
    }

    private void OnCardOpenCli(ServiceCardViewModel card)
    {
        if (card.Kind == ServiceKind.Php)
        {
            _desktopSetup.OpenInShell(card.EndpointDisplay);
            return;
        }
        _desktopSetup.OpenServiceCli(card.Kind, _settings);
    }

    private void OnCardOpenConfig(ServiceCardViewModel card)
    {
        var ver = _settings.GetActiveVersion(card.Kind);
        if (string.IsNullOrWhiteSpace(ver)) return;

        if (card.Kind == ServiceKind.Php)
        {
            var phpDir = _settings.GetInstallDirectory(ServiceKind.Php, ver);
            var ini = Path.Combine(phpDir, "php.ini");
            if (!File.Exists(ini) && Directory.Exists(phpDir))
                _installerService.ConfigurePhpIni(phpDir);
            if (File.Exists(ini)) _desktopSetup.OpenInShell(ini);
            return;
        }

        var dataDir = _settings.GetDataDirectory(card.Kind);
        if (card.Kind is ServiceKind.MariaDb or ServiceKind.MySql)
        {
            var ini = PlatformHelper.ResolveMySqlStyleConfigPath(dataDir);
            if (File.Exists(ini)) _desktopSetup.OpenInShell(ini);
            else _desktopSetup.OpenInShell(dataDir);
            return;
        }

        if (card.Kind == ServiceKind.Cassandra)
        {
            var yaml = Path.Combine(InstallerService.ResolveEffectiveRuntimeDirectory(_settings.GetInstallDirectory(ServiceKind.Cassandra, ver)), "conf", "cassandra.yaml");
            if (File.Exists(yaml)) _desktopSetup.OpenInShell(yaml);
            return;
        }

        var conf = Path.Combine(dataDir, card.ConfigFileName);
        if (File.Exists(conf)) _desktopSetup.OpenInShell(conf);
        else
        {
            Directory.CreateDirectory(dataDir);
            _desktopSetup.OpenInShell(dataDir);
        }
    }

    private void OnCardOpenFolder(ServiceCardViewModel card)
    {
        var ver = _settings.GetActiveVersion(card.Kind);
        var dir = !string.IsNullOrWhiteSpace(ver)
            ? _settings.GetInstallDirectory(card.Kind, ver)
            : _settings.GetRuntimeRoot(card.Kind);
        Directory.CreateDirectory(dir);
        _desktopSetup.OpenInShell(dir);
    }

    partial void OnIsDBeaverBusyChanged(bool value) =>
        OnPropertyChanged(nameof(DBeaverButtonLabel));

    [RelayCommand]
    private async Task StartOrUpdateDBeaverAsync()
    {
        if (IsDBeaverBusy)
            return;

        IsDBeaverBusy = true;
        DBeaverProgress = 0;

        var progress = new Progress<InstallProgress>(p =>
        {
            DBeaverProgress = p.Percentage;
            StatusMessage = p.Stage;
        });

        try
        {
            StatusMessage = await _dbeaverService.LaunchOrUpdateAsync(_settings, progress);
        }
        catch (Exception ex)
        {
            _logService.Error("DBeaver", $"Erro ao iniciar/atualizar DBeaver: {ex.Message}");
            StatusMessage = $"Erro no DBeaver: {ex.Message}";
        }
        finally
        {
            IsDBeaverBusy = false;
        }
    }

    [RelayCommand]
    private async Task StartInstalledServicesAsync()
    {
        var tasks = _cardsByKind.Values
            .Where(c => c.IsAnyVersionInstalled && !c.IsRunning)
            .Select(c => _processManager.StartServiceAsync(c.Kind, _settings));
        await Task.WhenAll(tasks);
        OnPropertyChanged(nameof(IsAnyServiceRunning));
    }

    [RelayCommand]
    private async Task StopAllAsync()
    {
        StatusMessage = "Parando serviços em execução...";
        await _processManager.StopAllAsync(_settings);
        OnPropertyChanged(nameof(IsAnyServiceRunning));
        StatusMessage = "Todos os serviços foram parados.";
    }

    [RelayCommand]
    private void OpenTerminal() => _desktopSetup.OpenConfiguredTerminal(_settings);

    [RelayCommand]
    private void OpenProjectsFolder()
    {
        Directory.CreateDirectory(_settings.ProjectsDirectory);
        _desktopSetup.OpenInShell(_settings.ProjectsDirectory);
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
            StatusMessage = $"Projeto '{project.Name}' criado!";
            _desktopSetup.OpenInShell(PhpCard.IsRunning ? project.Url : project.FullPath);
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
        if (project is not null) _desktopSetup.OpenInShell(project.Url);
    }

    [RelayCommand]
    private void OpenProjectFolder(ProjectInfo? project)
    {
        if (project is not null) _desktopSetup.OpenInShell(project.FullPath);
    }

    [RelayCommand]
    private void SetLogFilter(string filter) => LogFilter = filter;

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

        _settings.SetPort(ServiceKind.Php, (int)(PhpPort ?? 8000));
        _settings.SetPort(ServiceKind.PostgreSql, (int)(PostgreSqlPort ?? 5432));
        _settings.SetPort(ServiceKind.MariaDb, (int)(MariaDbPort ?? 3306));
        _settings.SetPort(ServiceKind.MySql, (int)(MySqlPort ?? 3307));
        _settings.SetPort(ServiceKind.SqlServer, (int)(SqlServerPort ?? 1433));
        _settings.SetPort(ServiceKind.MongoDb, (int)(MongoDbPort ?? 27017));
        _settings.SetPort(ServiceKind.Redis, (int)(RedisPort ?? 6379));
        _settings.SetPort(ServiceKind.Cassandra, (int)(CassandraPort ?? 9042));

        _settings.AutoConfigurePath = AutoConfigurePath;
        _settings.AutoCreateDesktopShortcut = AutoCreateDesktopShortcut;
        _settings.AutoStartServicesOnLaunch = AutoStartServicesOnLaunch;

        _settingsService.Save(_settings);
        _settingsService.EnsureDirectories(_settings);
        _desktopSetup.EnsureStarterWorkspace(_settings);

        LoadSettingsIntoForm();
        DetectAllInstalledRuntimes();
        RefreshProjects();
        RefreshPathStatus();

        _logService.Success("BE-Start", "Configurações salvas.");
        StatusMessage = "Configurações salvas com sucesso!";
    }

    [RelayCommand]
    private void ConfigureUserPath()
    {
        if (_desktopSetup.ConfigureUserPath(_settings))
        {
            RefreshPathStatus();
            StatusMessage = "PATH do usuário atualizado com os runtimes instalados!";
        }
        else
        {
            StatusMessage = "Instale pelo menos um serviço antes de adicionar ao PATH.";
        }
    }

    [RelayCommand]
    private void CreateDesktopShortcuts()
    {
        StatusMessage = _desktopSetup.CreateDesktopShortcuts(_settings)
            ? "Atalhos criados na Área de Trabalho!"
            : "Não foi possível criar os atalhos na Área de Trabalho.";
    }

    [RelayCommand]
    private void OpenVcRedistDownload() =>
        _desktopSetup.OpenInShell("https://aka.ms/vs/17/release/vc_redist.x64.exe");

    public void ShutdownServices() => _processManager.StopAllSync(_settings);

    private void DetectAllInstalledRuntimes()
    {
        foreach (var kind in _cardsByKind.Keys)
            DetectSingleRuntime(kind);

        DetectComposerRuntime();
        _settingsService.Save(_settings);
    }

    private void DetectSingleRuntime(ServiceKind kind)
    {
        var installed = _installerService.GetInstalledVersions(_settings, kind);
        var active = _settings.GetActiveVersion(kind);
        if (string.IsNullOrWhiteSpace(active) || !installed.Contains(active))
        {
            active = installed.FirstOrDefault();
            _settings.SetActiveVersion(kind, active);
        }

        if (_cardsByKind.TryGetValue(kind, out var card))
        {
            card.Host = kind == ServiceKind.Php ? _settings.PhpHost : "127.0.0.1";
            card.Port = _settings.GetPort(kind);
            card.IsAnyVersionInstalled = !string.IsNullOrWhiteSpace(active);
            card.ActiveVersionDisplay = !string.IsNullOrWhiteSpace(active) ? $"{card.ShortName} {active}" : "Não instalado";
            card.State = _processManager.IsRunning(kind)
                ? ServiceState.Running
                : (card.IsAnyVersionInstalled ? ServiceState.Stopped : ServiceState.NotInstalled);
        }
    }

    private void DetectComposerRuntime()
    {
        var installed = _installerService.GetInstalledVersions(_settings, ServiceKind.Composer);
        var active = _settings.GetActiveVersion(ServiceKind.Composer);
        if (string.IsNullOrWhiteSpace(active) || !installed.Contains(active))
        {
            active = installed.FirstOrDefault();
            _settings.SetActiveVersion(ServiceKind.Composer, active);
        }

        IsComposerInstalled = !string.IsNullOrWhiteSpace(active);
        ComposerStatusDisplay = IsComposerInstalled ? $"Composer {active} instalado" : "Não instalado";
    }

    private void SyncCardFlags(ServiceCardViewModel card)
    {
        var active = _settings.GetActiveVersion(card.Kind);
        foreach (var r in card.Versions)
        {
            r.IsInstalled = _installerService.IsVersionInstalled(_settings, card.Kind, r.Version);
            r.IsActive = r.IsInstalled && string.Equals(active, r.Version, StringComparison.OrdinalIgnoreCase);
        }
        card.NotifyActionProperties();
    }

    private void LoadSettingsIntoForm()
    {
        BaseDirectory = _settings.BaseDirectory;
        ProjectsDirectory = _settings.ProjectsDirectory;
        PhpHost = _settings.PhpHost;
        PhpPort = _settings.GetPort(ServiceKind.Php);
        PostgreSqlPort = _settings.GetPort(ServiceKind.PostgreSql);
        MariaDbPort = _settings.GetPort(ServiceKind.MariaDb);
        MySqlPort = _settings.GetPort(ServiceKind.MySql);
        SqlServerPort = _settings.GetPort(ServiceKind.SqlServer);
        MongoDbPort = _settings.GetPort(ServiceKind.MongoDb);
        RedisPort = _settings.GetPort(ServiceKind.Redis);
        CassandraPort = _settings.GetPort(ServiceKind.Cassandra);
        AutoConfigurePath = _settings.AutoConfigurePath;
        AutoCreateDesktopShortcut = _settings.AutoCreateDesktopShortcut;
        AutoStartServicesOnLaunch = _settings.AutoStartServicesOnLaunch;
        InstallComposerWithPhp = _settings.InstallComposerWithPhp;
    }

    private void RefreshPathStatus() =>
        PathStatusSummary = _desktopSetup.GetPathStatusSummary(_settings);

    private void OnProcessStateChanged(ServiceKind kind, ServiceState state, int? pid)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_cardsByKind.TryGetValue(kind, out var card))
            {
                card.State = state;
                card.Pid = pid;
            }
            OnPropertyChanged(nameof(IsAnyServiceRunning));
        });
    }

    private void OnLogEntryAdded(LogEntry entry)
    {
        _pendingLogs.Enqueue(entry);
        if (Interlocked.Exchange(ref _logFlushScheduled, 1) == 0)
        {
            Dispatcher.UIThread.Post(FlushPendingLogs, DispatcherPriority.Background);
        }
    }

    private void FlushPendingLogs()
    {
        Interlocked.Exchange(ref _logFlushScheduled, 0);

        while (_pendingLogs.TryDequeue(out var entry))
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
        }
    }

    private bool MatchesFilter(LogEntry entry) => LogFilter switch
    {
        "Todos" => true,
        "BE-Start" => entry.Source == "BE-Start",
        "SQL" => entry.Source is "PostgreSQL" or "MariaDB" or "MySQL" or "SQLServer",
        "NoSQL" => entry.Source is "MongoDB" or "Redis" or "Cassandra",
        "PHP" => entry.Source is "PHP" or "Composer",
        _ => true
    };

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
