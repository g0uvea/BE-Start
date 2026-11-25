using System.IO;
using System.Text.Json;
using BEStart.Models;

namespace BEStart.Services;

public sealed class SettingsService
{
    public string SettingsFilePath { get; }

    public SettingsService()
    {
        var configDir = AppSettings.GetDefaultBaseDirectory();
        Directory.CreateDirectory(configDir);
        SettingsFilePath = Path.Combine(configDir, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                var loaded = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppSettings);
                if (loaded is not null)
                {
                    Normalize(loaded);
                    return loaded;
                }
            }
        }
        catch
        {
            // Fall back to defaults if settings file is corrupted.
        }

        var defaults = new AppSettings();
        Normalize(defaults);
        Save(defaults);
        return defaults;
    }

    public void Save(AppSettings settings)
    {
        Normalize(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath)!);
        var json = JsonSerializer.Serialize(settings, AppJsonContext.Default.AppSettings);
        File.WriteAllText(SettingsFilePath, json, PlatformHelper.Utf8NoBom);
    }

    public void EnsureDirectories(AppSettings settings)
    {
        Directory.CreateDirectory(settings.BaseDirectory);
        Directory.CreateDirectory(settings.ProjectsDirectory);
        Directory.CreateDirectory(settings.RuntimesDirectory);
        Directory.CreateDirectory(settings.DataRootDirectory);
        Directory.CreateDirectory(settings.DownloadsDirectory);

        foreach (var desc in ServiceDescriptor.All)
        {
            Directory.CreateDirectory(settings.GetRuntimeRoot(desc.Kind));
        }
    }

    private static void Normalize(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.BaseDirectory))
            settings.BaseDirectory = AppSettings.GetDefaultBaseDirectory();

        if (string.IsNullOrWhiteSpace(settings.ProjectsDirectory))
            settings.ProjectsDirectory = Path.Combine(settings.BaseDirectory, "www");

        if (string.IsNullOrWhiteSpace(settings.PhpHost))
            settings.PhpHost = "127.0.0.1";

        settings.ServicePorts ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        settings.ActiveVersions ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var desc in ServiceDescriptor.All)
        {
            if (desc.DefaultPort > 0)
                settings.SetPort(desc.Kind, settings.GetPort(desc.Kind));
        }
    }
}
