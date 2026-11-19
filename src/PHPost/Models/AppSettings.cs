using System.IO;
using System.Text.Json.Serialization;

namespace PHPost.Models;

public sealed class AppSettings
{
    public string BaseDirectory { get; set; } = GetDefaultBaseDirectory();
    public string ProjectsDirectory { get; set; } = Path.Combine(GetDefaultBaseDirectory(), "www");
    public string PhpHost { get; set; } = "127.0.0.1";
    public int PhpPort { get; set; } = 8000;
    public int MariaDbPort { get; set; } = 3306;
    public string? ActivePhpVersion { get; set; }
    public string? ActiveMariaDbVersion { get; set; }
    public bool AutoConfigurePath { get; set; } = true;
    public bool AutoCreateDesktopShortcut { get; set; } = true;
    public bool AutoStartServicesOnLaunch { get; set; } = false;

    [JsonIgnore]
    public string RuntimesDirectory => Path.Combine(BaseDirectory, "runtimes");

    [JsonIgnore]
    public string PhpRuntimesDirectory => Path.Combine(RuntimesDirectory, "php");

    [JsonIgnore]
    public string MariaDbRuntimesDirectory => Path.Combine(RuntimesDirectory, "mariadb");

    [JsonIgnore]
    public string MariaDbDataDirectory => Path.Combine(BaseDirectory, "data", "mariadb");

    [JsonIgnore]
    public string DownloadsDirectory => Path.Combine(BaseDirectory, "downloads");

    public string GetPhpInstallDirectory(string version) =>
        Path.Combine(PhpRuntimesDirectory, SanitizeFolderName(version));

    public string GetMariaDbInstallDirectory(string version) =>
        Path.Combine(MariaDbRuntimesDirectory, SanitizeFolderName(version));

    public static string GetDefaultBaseDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            localAppData = AppContext.BaseDirectory;

        return Path.Combine(localAppData, "PHPost");
    }

    public static string SanitizeFolderName(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(raw.Select(c => invalid.Contains(c) ? '_' : c));
    }
}
