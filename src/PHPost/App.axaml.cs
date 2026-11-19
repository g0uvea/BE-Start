using System.Net.Http;
using System.Net.Http.Headers;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PHPost.Services;
using PHPost.ViewModels;
using PHPost.Views;

namespace PHPost;

public partial class App : Application
{
    private HttpClient? _httpClient;
    private MainWindowViewModel? _mainViewModel;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = true };
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMinutes(15)
            };
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PHPost", "1.0"));

            var logService = new LogService();
            var settingsService = new SettingsService();
            var catalogService = new PackageCatalogService(_httpClient, logService);
            var installerService = new InstallerService(_httpClient, logService);
            var processManager = new ProcessManagerService(installerService, logService);
            var desktopSetup = new DesktopSetupService(logService);

            _mainViewModel = new MainWindowViewModel(
                settingsService,
                logService,
                catalogService,
                installerService,
                processManager,
                desktopSetup);

            desktop.MainWindow = new MainWindow
            {
                DataContext = _mainViewModel
            };

            desktop.Exit += (_, _) =>
            {
                _mainViewModel?.ShutdownServices();
                _mainViewModel?.Dispose();
                _httpClient?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
