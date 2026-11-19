using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PHPost.ViewModels;

namespace PHPost.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += OnWindowOpened;
    }

    private async void OnWindowOpened(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            await vm.InitializeAsync();
        }
    }

    private async void OnBrowseBaseDirClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Escolha a pasta base do PHPost",
            AllowMultiple = false
        });

        var chosen = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(chosen))
            vm.BaseDirectory = chosen;
    }

    private async void OnBrowseProjectsDirClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Escolha a pasta de projetos (www)",
            AllowMultiple = false
        });

        var chosen = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(chosen))
            vm.ProjectsDirectory = chosen;
    }
}
