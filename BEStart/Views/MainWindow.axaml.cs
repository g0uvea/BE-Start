using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using BEStart.ViewModels;

namespace BEStart.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += OnWindowOpened;
        PropertyChanged += OnWindowPropertyChanged;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr procHandle, IntPtr min, IntPtr max);

    private async void OnWindowOpened(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            await vm.InitializeAsync();
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != WindowStateProperty)
            return;

        try
        {
            using var current = Process.GetCurrentProcess();
            if (WindowState == WindowState.Minimized)
            {
                // Yield CPU priority to active IDEs/compilers and trim unused working set RAM
                current.PriorityClass = ProcessPriorityClass.BelowNormal;
                GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
                if (OperatingSystem.IsWindows())
                    SetProcessWorkingSetSize(current.Handle, (IntPtr)(-1), (IntPtr)(-1));
            }
            else
            {
                current.PriorityClass = ProcessPriorityClass.Normal;
            }
        }
        catch
        {
            // Ignore OS permission restrictions on process priority/working set.
        }
    }

    private async void OnBrowseBaseDirClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Escolha a pasta base do BE-Start",
            AllowMultiple = false
        });

        var chosen = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(chosen))
            vm.BaseDirectory = chosen;
    }

    private async void OnBrowseProjectsDirClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

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
