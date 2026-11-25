using System.IO;
using System.Text.Json.Serialization;
using BEStart.Services;

namespace BEStart.Models;

public sealed class AppSettings
{
    public string BaseDirectory { get; set; } = GetDefaultBaseDirectory();
    public string ProjectsDirectory { get; set; } = Path.Combine(GetDefaultBaseDirectory(), "www");
    public string PhpHost { get; set; } = "127.0.0.1";

    public Dictionary<string, int> ServicePorts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ActiveVersions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool AutoConfigurePath { get; set; } = true;
    public bool AutoCreateDesktopShortcut { get; set; } = true;
    public bool AutoStartServicesOnLaunch { get; set; } = false;
    public bool InstallComposerWithPhp { get; set; } = true;

    [JsonIgnore]
    public string RuntimesDirectory => Path.Combine(BaseDirectory, "runtimes");

    [JsonIgnore]
    public string DataRootDirectory => Path.Combine(BaseDirectory, "data");

    [JsonIgnore]
    public string DownloadsDirectory => Path.Combine(BaseDirectory, "downloads");

    [JsonIgnore]
    public string JreDirectory => Path.Combine(RuntimesDirectory, "jre");

    public int GetPort(ServiceKind kind)
    {
        var key = kind.ToString();
        if (ServicePorts.TryGetValue(key, out var port) && port is >= 1 and <= 65535)
            return port;
        return ServiceDescriptor.Get(kind).DefaultPort;
    }

    public void SetPort(ServiceKind kind, int port)
    {
        var fallback = ServiceDescriptor.Get(kind).DefaultPort;
        ServicePorts[kind.ToString()] = port is >= 1 and <= 65535 ? port : fallback;
    }

    public string? GetActiveVersion(ServiceKind kind) =>
        ActiveVersions.TryGetValue(kind.ToString(), out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    public void SetActiveVersion(ServiceKind kind, string? version)
    {
        var key = kind.ToString();
        if (string.IsNullOrWhiteSpace(version))
            ActiveVersions.Remove(key);
        else
            ActiveVersions[key] = version;
    }

    public string GetRuntimeRoot(ServiceKind kind) =>
        Path.Combine(RuntimesDirectory, ServiceDescriptor.Get(kind).RuntimeFolder);

    public string GetInstallDirectory(ServiceKind kind, string version) =>
        Path.Combine(GetRuntimeRoot(kind), SanitizeFolderName(version));

    public string GetDataDirectory(ServiceKind kind)
    {
        var folder = ServiceDescriptor.Get(kind).DataFolder;
        return string.IsNullOrEmpty(folder) ? BaseDirectory : Path.Combine(DataRootDirectory, folder);
    }

    public string GetMariaDbSocketPath() =>
        Path.Combine(GetDataDirectory(ServiceKind.MariaDb), "mariadb.sock");

    public string GetMySqlSocketPath() =>
        Path.Combine(GetDataDirectory(ServiceKind.MySql), "mysql.sock");

    public static string GetDefaultBaseDirectory() =>
        PlatformHelper.GetDefaultBaseDirectory();

    public static string SanitizeFolderName(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(raw.Select(c => invalid.Contains(c) ? '_' : c));
    }
}
