using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using PHPost.Models;

namespace PHPost.Services;

public sealed class PackageCatalogService
{
    private const string WindowsPhpReleasesUrl = "https://windows.php.net/downloads/releases/releases.json";
    private const string WindowsPhpDownloadBase = "https://windows.php.net/downloads/releases/";
    private const string StaticPhpBulkApiUrl = "https://dl.static-php.dev/static-php-cli/bulk/?format=json";
    private const string StaticPhpBaseUrl = "https://dl.static-php.dev";
    private const string MariaDbApiBase = "https://downloads.mariadb.org/rest-api/mariadb/";
    private const string HomebrewFormulaApiBase = "https://formulae.brew.sh/api/formula/";

    private readonly HttpClient _http;
    private readonly LogService _log;

    public PackageCatalogService(HttpClient http, LogService log)
    {
        _http = http;
        _log = log;
    }

    public Task<IReadOnlyList<PackageRelease>> FetchPhpReleasesAsync(CancellationToken ct = default)
    {
        if (PlatformHelper.IsWindows)
            return FetchWindowsPhpReleasesAsync(ct);

        return FetchUnixStaticPhpReleasesAsync(ct);
    }

    public Task<IReadOnlyList<PackageRelease>> FetchMariaDbReleasesAsync(CancellationToken ct = default)
    {
        if (PlatformHelper.IsWindows)
            return FetchOfficialMariaDbArchiveReleasesAsync("Windows", "x86_64", "ZIP file", "ZIP x64", "Portátil ZIP", ct);

        if (PlatformHelper.IsLinux && !PlatformHelper.IsArm64)
            return FetchOfficialMariaDbArchiveReleasesAsync("Linux", "x86_64", "gzipped tar file", "TAR.GZ x64", "Portátil TAR.GZ", ct);

        // macOS (Apple Silicon / Intel) and Linux ARM64 use Homebrew formulae (official MariaDB recommendation on macOS)
        return FetchHomebrewMariaDbReleasesAsync(ct);
    }

    private async Task<IReadOnlyList<PackageRelease>> FetchWindowsPhpReleasesAsync(CancellationToken ct)
    {
        var results = new List<PackageRelease>();

        try
        {
            using var response = await _http.GetAsync(WindowsPhpReleasesUrl, ct);
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
                    InstallMethod = PackageInstallMethod.ArchiveDownload,
                    Version = version,
                    Series = series,
                    DisplayName = $"PHP {version}",
                    Subtitle = $"Windows x64 ({chosenVariant.Value.Name}) • Oficial windows.php.net",
                    DownloadUrl = WindowsPhpDownloadBase + fileName,
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

    private async Task<IReadOnlyList<PackageRelease>> FetchUnixStaticPhpReleasesAsync(CancellationToken ct)
    {
        var results = new List<PackageRelease>();

        try
        {
            var osToken = PlatformHelper.IsMacOS ? "macos" : "linux";
            var archToken = PlatformHelper.IsArm64 ? "aarch64" : "x86_64";
            var regex = new Regex(
                $@"^php-(\d+\.\d+\.\d+)-cli-{osToken}-{archToken}\.tar\.gz$",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

            using var response = await _http.GetAsync(StaticPhpBulkApiUrl, ct);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return results;

            // Keep the newest patch release for each major.minor series (e.g. 8.5, 8.4, 8.3, 8.2, 8.1)
            var latestPerSeries = new Dictionary<string, (Version Parsed, string Version, string FileName, string FullPath, string Size)>();

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("is_dir", out var isDirProp) && isDirProp.ValueKind == JsonValueKind.True)
                    continue;

                var name = item.TryGetProperty("name", out var nProp) ? nProp.GetString() : null;
                var fullPath = item.TryGetProperty("full_path", out var fpProp) ? fpProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(fullPath))
                    continue;

                var match = regex.Match(name);
                if (!match.Success)
                    continue;

                var verText = match.Groups[1].Value;
                if (!Version.TryParse(verText, out var parsed))
                    continue;

                var series = $"{parsed.Major}.{parsed.Minor}";
                var size = item.TryGetProperty("size", out var szProp) ? szProp.GetString() ?? "" : "";

                if (!latestPerSeries.TryGetValue(series, out var existing) || parsed > existing.Parsed)
                {
                    latestPerSeries[series] = (parsed, verText, name, fullPath, size);
                }
            }

            var ordered = latestPerSeries
                .OrderByDescending(kv => kv.Value.Parsed)
                .Take(6)
                .ToList();

            var platformLabel = $"{PlatformHelper.PlatformDisplayName} {PlatformHelper.ArchitectureDisplayName}";
            for (var i = 0; i < ordered.Count; i++)
            {
                var (series, info) = ordered[i];
                var url = info.FullPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? info.FullPath
                    : StaticPhpBaseUrl + info.FullPath;

                results.Add(new PackageRelease
                {
                    Kind = ServiceKind.Php,
                    InstallMethod = PackageInstallMethod.ArchiveDownload,
                    Version = info.Version,
                    Series = series,
                    DisplayName = $"PHP {info.Version}",
                    Subtitle = $"{platformLabel} (Static CLI completo: pdo_mysql, mysqli, gd, intl, zip) • dl.static-php.dev",
                    DownloadUrl = url,
                    FileName = info.FileName,
                    SizeLabel = string.IsNullOrWhiteSpace(info.Size) ? "TAR.GZ" : $"{info.Size} • TAR.GZ",
                    Sha256 = null,
                    IsRecommended = i == 0
                });
            }

            _log.Info("PHPost", $"Catálogo PHP ({platformLabel}) atualizado: {results.Count} versões encontradas.");
        }
        catch (Exception ex)
        {
            _log.Error("PHPost", $"Falha ao consultar versões do PHP: {ex.Message}");
        }

        return results;
    }

    private async Task<IReadOnlyList<PackageRelease>> FetchOfficialMariaDbArchiveReleasesAsync(
        string targetOs,
        string targetCpu,
        string targetPackageType,
        string sizeLabel,
        string packageTag,
        CancellationToken ct)
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

            var tasks = selectedSeries.Select(s =>
                FetchSingleMariaDbSeriesAsync(s.Id, s.IsLts, s.SupportType, targetOs, targetCpu, targetPackageType, sizeLabel, packageTag, ct));
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
                        InstallMethod = item.InstallMethod,
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

            _log.Info("PHPost", $"Catálogo MariaDB ({targetOs} {targetCpu}) atualizado: {results.Count} versões estáveis encontradas.");
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
        string targetOs,
        string targetCpu,
        string targetPackageType,
        string sizeLabel,
        string packageTag,
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

                    if (!string.Equals(os, targetOs, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(cpu, targetCpu, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(pkgType, targetPackageType, StringComparison.OrdinalIgnoreCase) ||
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

                    var tag = isLts ? $"LTS • {packageTag}" : $"{supportType} • {packageTag}";
                    return new PackageRelease
                    {
                        Kind = ServiceKind.MariaDb,
                        InstallMethod = PackageInstallMethod.ArchiveDownload,
                        Version = version,
                        Series = seriesId,
                        DisplayName = $"MariaDB {version}",
                        Subtitle = $"{targetOs} {targetCpu} ({tag}) • Oficial mariadb.org",
                        DownloadUrl = url,
                        FileName = fileName,
                        SizeLabel = sizeLabel,
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

    private async Task<IReadOnlyList<PackageRelease>> FetchHomebrewMariaDbReleasesAsync(CancellationToken ct)
    {
        var results = new List<PackageRelease>();
        var platformLabel = $"{PlatformHelper.PlatformDisplayName} {PlatformHelper.ArchitectureDisplayName}";

        (string Formula, string Series, bool IsLts)[] candidates =
        [
            ("mariadb@11.8", "11.8", true),
            ("mariadb@11.4", "11.4", true),
            ("mariadb", "latest", false),
            ("mariadb@10.11", "10.11", true)
        ];

        try
        {
            var tasks = candidates.Select(c => FetchSingleHomebrewMariaDbAsync(c.Formula, c.Series, c.IsLts, platformLabel, ct));
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
                        InstallMethod = item.InstallMethod,
                        FormulaName = item.FormulaName,
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

            _log.Info("PHPost", $"Catálogo MariaDB ({platformLabel} via Homebrew) atualizado: {results.Count} versões encontradas.");
        }
        catch (Exception ex)
        {
            _log.Error("PHPost", $"Falha ao consultar versões do MariaDB via Homebrew: {ex.Message}");
        }

        return results;
    }

    private async Task<PackageRelease?> FetchSingleHomebrewMariaDbAsync(
        string formulaName,
        string fallbackSeries,
        bool isLts,
        string platformLabel,
        CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync($"{HomebrewFormulaApiBase}{formulaName}.json", ct);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("versions", out var versions) ||
                !versions.TryGetProperty("stable", out var stableProp))
            {
                return null;
            }

            var version = stableProp.GetString();
            if (string.IsNullOrWhiteSpace(version))
                return null;

            var series = Version.TryParse(version, out var parsed)
                ? $"{parsed.Major}.{parsed.Minor}"
                : fallbackSeries;
            var tag = isLts ? "LTS • Homebrew" : "Estável • Homebrew";

            return new PackageRelease
            {
                Kind = ServiceKind.MariaDb,
                InstallMethod = PackageInstallMethod.HomebrewFormula,
                FormulaName = formulaName,
                Version = version,
                Series = series,
                DisplayName = $"MariaDB {version}",
                Subtitle = $"{platformLabel} ({tag}) • Oficial formulae.brew.sh",
                DownloadUrl = $"brew://{formulaName}",
                FileName = $"brew install {formulaName}",
                SizeLabel = "Homebrew",
                Sha256 = null,
                IsRecommended = false
            };
        }
        catch
        {
            return null;
        }
    }
}
