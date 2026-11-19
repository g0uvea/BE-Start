using System.IO;
using System.Text.Json;
using PHPost.Models;

namespace PHPost.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

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
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
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
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsFilePath, json);
    }

    public void EnsureDirectories(AppSettings settings)
    {
        Directory.CreateDirectory(settings.BaseDirectory);
        Directory.CreateDirectory(settings.ProjectsDirectory);
        Directory.CreateDirectory(settings.PhpRuntimesDirectory);
        Directory.CreateDirectory(settings.MariaDbRuntimesDirectory);
        Directory.CreateDirectory(settings.DownloadsDirectory);
    }

    private static void Normalize(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.BaseDirectory))
            settings.BaseDirectory = AppSettings.GetDefaultBaseDirectory();

        if (string.IsNullOrWhiteSpace(settings.ProjectsDirectory))
            settings.ProjectsDirectory = Path.Combine(settings.BaseDirectory, "www");

        if (string.IsNullOrWhiteSpace(settings.PhpHost))
            settings.PhpHost = "127.0.0.1";

        if (settings.PhpPort is < 1 or > 65535)
            settings.PhpPort = 8000;

        if (settings.MariaDbPort is < 1 or > 65535)
            settings.MariaDbPort = 3306;
    }
}
