using System.Text.Json.Serialization;

namespace BEStart.Models;

public sealed class CatalogCacheEntry
{
    public ServiceKind Kind { get; set; }
    public PackageInstallMethod InstallMethod { get; set; }
    public string? FormulaName { get; set; }
    public string Version { get; set; } = string.Empty;
    public string Series { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string SizeLabel { get; set; } = string.Empty;
    public string? Sha256 { get; set; }
    public bool IsRecommended { get; set; }
}

public sealed class CatalogCacheData
{
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string PlatformKey { get; set; } = string.Empty;
    public Dictionary<string, List<CatalogCacheEntry>> Services { get; set; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(CatalogCacheData))]
internal partial class AppJsonContext : JsonSerializerContext
{
}
