using System.Net.Http;
using System.Text.Json;
using PHPost.Models;

namespace PHPost.Services;

public sealed class PackageCatalogService
{
    private const string PhpReleasesUrl = "https://windows.php.net/downloads/releases/releases.json";
    private const string PhpDownloadBase = "https://windows.php.net/downloads/releases/";
    private const string MariaDbApiBase = "https://downloads.mariadb.org/rest-api/mariadb/";

    private readonly HttpClient _http;
    private readonly LogService _log;

    public PackageCatalogService(HttpClient http, LogService log)
    {
        _http = http;
        _log = log;
    }

    public async Task<IReadOnlyList<PackageRelease>> FetchPhpReleasesAsync(CancellationToken ct = default)
    {
        var results = new List<PackageRelease>();

        try
        {
            using var response = await _http.GetAsync(PhpReleasesUrl, ct);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var entries = new List<(Version ParsedSeries, string Series, JsonElement Element)>();

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (Version.TryParse(prop.Name, out var parsed))
                {
                    entries.Add((parsed, prop.Name, prop.Value));
                }
            }

            entries.Sort((a, b) => b.ParsedSeries.CompareTo(a.ParsedSeries));

            for (var i = 0; i < entries.Count; i++)
            {
                var (_, series, obj) = entries[i];
                var version = obj.TryGetProperty("version", out var vProp) ? vProp.GetString() ?? series : series;

                // Prefer Thread Safe x64 (ts-*-x64), then fallback to Non-Thread Safe x64 (nts-*-x64)
                JsonProperty? chosenVariant = null;
                foreach (var variant in obj.EnumerateObject())
                {
                    if (variant.Name.StartsWith("ts-", StringComparison.OrdinalIgnoreCase) &&
                        variant.Name.EndsWith("-x64", StringComparison.OrdinalIgnoreCase) &&
                        variant.Value.ValueKind == JsonValueKind.Object &&
                        variant.Value.TryGetProperty("zip", out _))
                    {
                        chosenVariant = variant;
                        break;
                    }
                }

                if (chosenVariant is null)
                {
                    foreach (var variant in obj.EnumerateObject())
                    {
                        if (variant.Name.StartsWith("nts-", StringComparison.OrdinalIgnoreCase) &&
                            variant.Name.EndsWith("-x64", StringComparison.OrdinalIgnoreCase) &&
                            variant.Value.ValueKind == JsonValueKind.Object &&
                            variant.Value.TryGetProperty("zip", out _))
                        {
                            chosenVariant = variant;
                            break;
                        }
                    }
                }

                if (chosenVariant is null)
                    continue;

                var zipObj = chosenVariant.Value.Value.GetProperty("zip");
                var fileName = zipObj.GetProperty("path").GetString();
                if (string.IsNullOrWhiteSpace(fileName))
                    continue;

                var sizeLabel = zipObj.TryGetProperty("size", out var sProp) ? sProp.GetString() ?? "" : "";
                var sha256 = zipObj.TryGetProperty("sha256", out var hProp) ? hProp.GetString() : null;

                results.Add(new PackageRelease
                {
                    Kind = ServiceKind.Php,
                    Version = version,
                    Series = series,
                    DisplayName = $"PHP {version}",
                    Subtitle = $"Windows x64 ({chosenVariant.Value.Name}) • Oficial windows.php.net",
                    DownloadUrl = PhpDownloadBase + fileName,
                    FileName = fileName,
                    SizeLabel = sizeLabel,
                    Sha256 = sha256,
                    IsRecommended = i == 0
                });
            }

            _log.Info("PHPost", $"Catálogo PHP atualizado: {results.Count} versões encontradas.");
        }
        catch (Exception ex)
        {
            _log.Error("PHPost", $"Falha ao consultar versões do PHP: {ex.Message}");
        }

        return results;
    }

    public async Task<IReadOnlyList<PackageRelease>> FetchMariaDbReleasesAsync(CancellationToken ct = default)
    {
        var results = new List<PackageRelease>();

        try
        {
            using var response = await _http.GetAsync(MariaDbApiBase, ct);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("major_releases", out var majors))
                return results;

            var stableSeries = new List<(string Id, bool IsLts, string SupportType)>();

            foreach (var item in majors.EnumerateArray())
            {
                var status = item.TryGetProperty("release_status", out var st) ? st.GetString() : null;
                if (!string.Equals(status, "Stable", StringComparison.OrdinalIgnoreCase))
                    continue;

                var id = item.TryGetProperty("release_id", out var idProp) ? idProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                var support = item.TryGetProperty("release_support_type", out var supProp) && supProp.ValueKind == JsonValueKind.String
                    ? supProp.GetString() ?? "Stable"
                    : "Stable";

                var isLts = support.Contains("Long Term", StringComparison.OrdinalIgnoreCase);
                stableSeries.Add((id, isLts, support));
            }

            // Prioritize LTS first, then take top 5 stable series to keep API calls snappy.
            var selectedSeries = stableSeries
                .OrderByDescending(x => x.IsLts)
                .ThenByDescending(x => Version.TryParse(x.Id, out var v) ? v : new Version(0, 0))
                .Take(5)
                .ToList();

            var tasks = selectedSeries.Select(s => FetchSingleMariaDbSeriesAsync(s.Id, s.IsLts, s.SupportType, ct));
            var fetched = await Task.WhenAll(tasks);

            var recommendedAssigned = false;
            foreach (var rel in fetched.Where(r => r is not null))
            {
                var item = rel!;
                if (!recommendedAssigned)
                {
                    item = new PackageRelease
                    {
                        Kind = item.Kind,
                        Version = item.Version,
                        Series = item.Series,
                        DisplayName = item.DisplayName,
                        Subtitle = item.Subtitle,
                        DownloadUrl = item.DownloadUrl,
                        FileName = item.FileName,
                        SizeLabel = item.SizeLabel,
                        Sha256 = item.Sha256,
                        IsRecommended = true
                    };
                    recommendedAssigned = true;
                }
                results.Add(item);
            }

            _log.Info("PHPost", $"Catálogo MariaDB atualizado: {results.Count} versões estáveis encontradas.");
        }
        catch (Exception ex)
        {
            _log.Error("PHPost", $"Falha ao consultar versões do MariaDB: {ex.Message}");
        }

        return results;
    }

    private async Task<PackageRelease?> FetchSingleMariaDbSeriesAsync(
        string seriesId,
        bool isLts,
        string supportType,
        CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync($"{MariaDbApiBase}{seriesId}/", ct);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("releases", out var releasesObj))
                return null;

            foreach (var relProp in releasesObj.EnumerateObject())
            {
                var version = relProp.Name;
                if (!relProp.Value.TryGetProperty("files", out var files))
                    continue;

                foreach (var file in files.EnumerateArray())
                {
                    var os = file.TryGetProperty("os", out var osProp) ? osProp.GetString() : null;
                    var cpu = file.TryGetProperty("cpu", out var cpuProp) ? cpuProp.GetString() : null;
                    var pkgType = file.TryGetProperty("package_type", out var ptProp) ? ptProp.GetString() : null;
                    var fileName = file.TryGetProperty("file_name", out var fnProp) ? fnProp.GetString() : null;
                    var url = file.TryGetProperty("file_download_url", out var urlProp) ? urlProp.GetString() : null;

                    if (!string.Equals(os, "Windows", StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(cpu, "x86_64", StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(pkgType, "ZIP file", StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrWhiteSpace(fileName) ||
                        string.IsNullOrWhiteSpace(url) ||
                        fileName.Contains("debugsymbols", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                        url = "https://" + url["http://".Length..];

                    string? sha256 = null;
                    if (file.TryGetProperty("checksum", out var chk) &&
                        chk.ValueKind == JsonValueKind.Object &&
                        chk.TryGetProperty("sha256sum", out var shaProp))
                    {
                        sha256 = shaProp.GetString();
                    }

                    var tag = isLts ? "LTS • Portátil ZIP" : $"{supportType} • Portátil ZIP";
                    return new PackageRelease
                    {
                        Kind = ServiceKind.MariaDb,
                        Version = version,
                        Series = seriesId,
                        DisplayName = $"MariaDB {version}",
                        Subtitle = $"Windows x64 ({tag}) • Oficial mariadb.org",
                        DownloadUrl = url,
                        FileName = fileName,
                        SizeLabel = "ZIP x64",
                        Sha256 = sha256,
                        IsRecommended = false
                    };
                }
            }
        }
        catch
        {
            // Skip failed individual series gracefully.
        }

        return null;
    }
}
