using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using BEStart.Models;

namespace BEStart.Services;

public sealed partial class PackageCatalogService
{
    private const string WindowsPhpReleasesUrl = "https://windows.php.net/downloads/releases/releases.json";
    private const string WindowsPhpDownloadBase = "https://windows.php.net/downloads/releases/";
    private const string StaticPhpBulkApiUrl = "https://dl.static-php.dev/static-php-cli/bulk/?format=json";
    private const string StaticPhpBaseUrl = "https://dl.static-php.dev";
    private const string ComposerVersionsUrl = "https://getcomposer.org/versions";
    private const string ComposerBaseUrl = "https://getcomposer.org";
    private const string MariaDbApiBase = "https://downloads.mariadb.org/rest-api/mariadb/";
    private const string PostgreSqlEolUrl = "https://endoflife.date/api/postgresql.json";
    private const string MySqlEolUrl = "https://endoflife.date/api/mysql.json";
    private const string MongoDbCurrentUrl = "https://downloads.mongodb.org/current.json";
    private const string RedisWindowsApiUrl = "https://api.github.com/repos/redis-windows/redis-windows/releases?per_page=8";
    private const string CassandraDownloadsUrl = "https://downloads.apache.org/cassandra/";
    private const string HomebrewFormulaApiBase = "https://formulae.brew.sh/api/formula/";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    private readonly HttpClient _http;
    private readonly LogService _log;

    public PackageCatalogService(HttpClient http, LogService log)
    {
        _http = http;
        _log = log;
    }

    public async Task<Dictionary<ServiceKind, IReadOnlyList<PackageRelease>>> FetchAllCatalogsAsync(
        AppSettings settings,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        var cachePath = Path.Combine(settings.BaseDirectory, "catalog-cache.json");

        if (!forceRefresh && TryLoadValidCache(cachePath, out var cached))
        {
            _log.Info("BE-Start", "Catálogo carregado do cache local (zero consumo de rede).");
            return cached;
        }

        var result = new Dictionary<ServiceKind, IReadOnlyList<PackageRelease>>();

        var tasks = new (ServiceKind Kind, Task<IReadOnlyList<PackageRelease>> Task)[]
        {
            (ServiceKind.Php, FetchPhpReleasesAsync(ct)),
            (ServiceKind.Composer, FetchComposerReleasesAsync(ct)),
            (ServiceKind.PostgreSql, FetchPostgreSqlReleasesAsync(ct)),
            (ServiceKind.MariaDb, FetchMariaDbReleasesAsync(ct)),
            (ServiceKind.MySql, FetchMySqlReleasesAsync(ct)),
            (ServiceKind.SqlServer, FetchSqlServerReleasesAsync(ct)),
            (ServiceKind.MongoDb, FetchMongoDbReleasesAsync(ct)),
            (ServiceKind.Redis, FetchRedisReleasesAsync(ct)),
            (ServiceKind.Cassandra, FetchCassandraReleasesAsync(ct))
        };

        await Task.WhenAll(tasks.Select(t => t.Task));

        foreach (var (kind, task) in tasks)
            result[kind] = task.Result;

        if (result.Values.Any(list => list.Count > 0))
            SaveCache(cachePath, result);

        return result;
    }

    // ==================== PHP 8.x ====================

    public Task<IReadOnlyList<PackageRelease>> FetchPhpReleasesAsync(CancellationToken ct = default) =>
        PlatformHelper.IsWindows
            ? FetchWindowsPhpReleasesAsync(ct)
            : FetchUnixStaticPhpReleasesAsync(ct);

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
                if (prop.Name.StartsWith("8.", StringComparison.Ordinal) &&
                    Version.TryParse(prop.Name, out var parsed))
                {
                    entries.Add((parsed, prop.Name, prop.Value));
                }
            }

            entries.Sort((a, b) => b.ParsedSeries.CompareTo(a.ParsedSeries));

            for (var i = 0; i < entries.Count; i++)
            {
                var (_, series, obj) = entries[i];
                var version = obj.TryGetProperty("version", out var vProp) ? vProp.GetString() ?? series : series;

                var chosenVariant = FindPhpZipVariant(obj, "ts-") ?? FindPhpZipVariant(obj, "nts-");
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

            _log.Info("PHP", $"Catálogo PHP 8.x atualizado: {results.Count} versões.");
        }
        catch (Exception ex)
        {
            _log.Error("PHP", $"Falha ao consultar versões PHP: {ex.Message}");
        }
        return results;
    }

    private static JsonProperty? FindPhpZipVariant(JsonElement obj, string prefix)
    {
        foreach (var variant in obj.EnumerateObject())
        {
            if (variant.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                variant.Name.EndsWith("-x64", StringComparison.OrdinalIgnoreCase) &&
                variant.Value.ValueKind == JsonValueKind.Object &&
                variant.Value.TryGetProperty("zip", out _))
            {
                return variant;
            }
        }
        return null;
    }

    private async Task<IReadOnlyList<PackageRelease>> FetchUnixStaticPhpReleasesAsync(CancellationToken ct)
    {
        var results = new List<PackageRelease>();
        try
        {
            var osToken = PlatformHelper.IsMacOS ? "macos" : "linux";
            var archToken = PlatformHelper.IsArm64 ? "aarch64" : "x86_64";
            var regex = new Regex($@"^php-(8\.\d+\.\d+)-cli-{osToken}-{archToken}\.tar\.gz$", RegexOptions.IgnoreCase);

            using var response = await _http.GetAsync(StaticPhpBulkApiUrl, ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return results;

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
                if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var parsed) || parsed.Major != 8)
                    continue;

                var series = $"{parsed.Major}.{parsed.Minor}";
                var size = item.TryGetProperty("size", out var szProp) ? szProp.GetString() ?? "" : "";
                if (!latestPerSeries.TryGetValue(series, out var existing) || parsed > existing.Parsed)
                    latestPerSeries[series] = (parsed, match.Groups[1].Value, name, fullPath, size);
            }

            var ordered = latestPerSeries.OrderByDescending(kv => kv.Value.Parsed).Take(6).ToList();
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
                    Subtitle = $"{platformLabel} (Static CLI) • dl.static-php.dev",
                    DownloadUrl = url,
                    FileName = info.FileName,
                    SizeLabel = string.IsNullOrWhiteSpace(info.Size) ? "TAR.GZ" : $"{info.Size} • TAR.GZ",
                    IsRecommended = i == 0
                });
            }

            _log.Info("PHP", $"Catálogo PHP 8.x ({platformLabel}) atualizado: {results.Count} versões.");
        }
        catch (Exception ex)
        {
            _log.Error("PHP", $"Falha ao consultar versões PHP: {ex.Message}");
        }
        return results;
    }

    // ==================== PHP COMPOSER 2.11.x ====================

    public async Task<IReadOnlyList<PackageRelease>> FetchComposerReleasesAsync(CancellationToken ct = default)
    {
        var results = new List<PackageRelease>();
        try
        {
            using var response = await _http.GetAsync(ComposerVersionsUrl, ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("stable", out var stableArr))
                return results;

            var candidates = new List<(string Version, string Path, bool Is211)>();
            foreach (var item in stableArr.EnumerateArray())
            {
                var ver = item.TryGetProperty("version", out var vProp) ? vProp.GetString() : null;
                var path = item.TryGetProperty("path", out var pProp) ? pProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(ver) || string.IsNullOrWhiteSpace(path))
                    continue;

                if (ver.StartsWith("2.", StringComparison.Ordinal))
                    candidates.Add((ver, path, ver.StartsWith("2.11.", StringComparison.Ordinal)));
            }

            var filtered = candidates.Where(c => c.Is211).ToList();
            if (filtered.Count == 0)
                filtered = candidates;

            for (var i = 0; i < filtered.Count; i++)
            {
                var c = filtered[i];
                results.Add(new PackageRelease
                {
                    Kind = ServiceKind.Composer,
                    InstallMethod = PackageInstallMethod.PharDownload,
                    Version = c.Version,
                    Series = "2.11",
                    DisplayName = $"Composer {c.Version}",
                    Subtitle = "Oficial getcomposer.org (composer.phar)",
                    DownloadUrl = ComposerBaseUrl + c.Path,
                    FileName = $"composer-{c.Version}.phar",
                    SizeLabel = "~3 MB",
                    IsRecommended = i == 0
                });
            }

            _log.Info("Composer", $"Catálogo Composer atualizado: {results.Count} versão(ões).");
        }
        catch (Exception ex)
        {
            _log.Error("Composer", $"Falha ao consultar versões do Composer: {ex.Message}");
        }
        return results;
    }

    // ==================== POSTGRESQL ====================

    public async Task<IReadOnlyList<PackageRelease>> FetchPostgreSqlReleasesAsync(CancellationToken ct = default)
    {
        if (!PlatformHelper.IsWindows)
            return await FetchHomebrewSeriesAsync(ServiceKind.PostgreSql, "PostgreSQL",
                [("postgresql@17", "17", true), ("postgresql@16", "16", false), ("postgresql@15", "15", false)], ct);

        var results = new List<PackageRelease>();
        try
        {
            using var response = await _http.GetAsync(PostgreSqlEolUrl, ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var cycles = new List<(int Major, string Cycle, string Latest)>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var cycle = item.TryGetProperty("cycle", out var cProp) ? cProp.GetString() : null;
                var latest = item.TryGetProperty("latest", out var lProp) ? lProp.GetString() : null;
                if (int.TryParse(cycle, out var major) && major is >= 14 and <= 18 && !string.IsNullOrWhiteSpace(latest))
                    cycles.Add((major, cycle!, latest!));
            }

            cycles.Sort((a, b) => b.Major.CompareTo(a.Major));
            var selected = cycles.Take(4).ToList();

            var checks = selected.Select(async c =>
            {
                var fileName = $"postgresql-{c.Latest}-1-windows-x64-binaries.zip";
                var url = $"https://get.enterprisedb.com/postgresql/{fileName}";
                var size = await ProbeContentLengthLabelAsync(url, ct);
                return (c, fileName, url, size);
            });

            var probed = await Task.WhenAll(checks);
            var recAssigned = false;
            foreach (var (c, fileName, url, size) in probed)
            {
                if (size is null)
                    continue;

                results.Add(new PackageRelease
                {
                    Kind = ServiceKind.PostgreSql,
                    InstallMethod = PackageInstallMethod.ArchiveDownload,
                    Version = c.Latest,
                    Series = c.Cycle,
                    DisplayName = $"PostgreSQL {c.Latest}",
                    Subtitle = "Windows x64 Portátil (EDB Binaries) • get.enterprisedb.com",
                    DownloadUrl = url,
                    FileName = fileName,
                    SizeLabel = size,
                    IsRecommended = !recAssigned
                });
                recAssigned = true;
            }

            _log.Info("PostgreSQL", $"Catálogo PostgreSQL atualizado: {results.Count} versões.");
        }
        catch (Exception ex)
        {
            _log.Error("PostgreSQL", $"Falha ao consultar versões PostgreSQL: {ex.Message}");
        }
        return results;
    }

    // ==================== MARIADB ====================

    public Task<IReadOnlyList<PackageRelease>> FetchMariaDbReleasesAsync(CancellationToken ct = default)
    {
        if (PlatformHelper.IsWindows)
            return FetchOfficialMariaDbArchiveReleasesAsync("Windows", "x86_64", "ZIP file", "ZIP x64", "Portátil ZIP", ct);

        if (PlatformHelper.IsLinux && !PlatformHelper.IsArm64)
            return FetchOfficialMariaDbArchiveReleasesAsync("Linux", "x86_64", "gzipped tar file", "TAR.GZ x64", "Portátil TAR.GZ", ct);

        return FetchHomebrewSeriesAsync(ServiceKind.MariaDb, "MariaDB",
            [("mariadb@11.8", "11.8", true), ("mariadb@11.4", "11.4", false), ("mariadb", "latest", false), ("mariadb@10.11", "10.11", false)], ct);
    }

    private async Task<IReadOnlyList<PackageRelease>> FetchOfficialMariaDbArchiveReleasesAsync(
        string targetOs, string targetCpu, string targetPackageType, string sizeLabel, string packageTag, CancellationToken ct)
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
                stableSeries.Add((id, support.Contains("Long Term", StringComparison.OrdinalIgnoreCase), support));
            }

            var selected = stableSeries
                .OrderByDescending(x => x.IsLts)
                .ThenByDescending(x => Version.TryParse(x.Id, out var v) ? v : new Version(0, 0))
                .Take(4)
                .ToList();

            var fetched = await Task.WhenAll(selected.Select(s =>
                FetchSingleMariaDbSeriesAsync(s.Id, s.IsLts, s.SupportType, targetOs, targetCpu, targetPackageType, sizeLabel, packageTag, ct)));

            var recAssigned = false;
            foreach (var item in fetched.Where(r => r is not null))
            {
                var rel = item!;
                if (!recAssigned)
                {
                    rel = CloneWithRecommended(rel, true);
                    recAssigned = true;
                }
                results.Add(rel);
            }

            _log.Info("MariaDB", $"Catálogo MariaDB atualizado: {results.Count} versões.");
        }
        catch (Exception ex)
        {
            _log.Error("MariaDB", $"Falha ao consultar versões MariaDB: {ex.Message}");
        }
        return results;
    }

    private async Task<PackageRelease?> FetchSingleMariaDbSeriesAsync(
        string seriesId, bool isLts, string supportType,
        string targetOs, string targetCpu, string targetPackageType, string sizeLabel, string packageTag, CancellationToken ct)
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
                        Version = relProp.Name,
                        Series = seriesId,
                        DisplayName = $"MariaDB {relProp.Name}",
                        Subtitle = $"{targetOs} {targetCpu} ({tag}) • Oficial mariadb.org",
                        DownloadUrl = url,
                        FileName = fileName,
                        SizeLabel = sizeLabel,
                        Sha256 = sha256
                    };
                }
            }
        }
        catch { }
        return null;
    }

    // ==================== MYSQL ====================

    public async Task<IReadOnlyList<PackageRelease>> FetchMySqlReleasesAsync(CancellationToken ct = default)
    {
        if (PlatformHelper.IsMacOS || (PlatformHelper.IsLinux && PlatformHelper.IsArm64))
            return await FetchHomebrewSeriesAsync(ServiceKind.MySql, "MySQL",
                [("mysql@8.4", "8.4", true), ("mysql", "latest", false), ("mysql@8.0", "8.0", false)], ct);

        var results = new List<PackageRelease>();
        try
        {
            using var response = await _http.GetAsync(MySqlEolUrl, ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var candidates = new List<(string Cycle, string Latest, bool IsLts)>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var cycle = item.TryGetProperty("cycle", out var cProp) ? cProp.GetString() : null;
                var latest = item.TryGetProperty("latest", out var lProp) ? lProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(cycle) || string.IsNullOrWhiteSpace(latest))
                    continue;

                if (cycle is "8.4" or "8.0" || cycle.StartsWith("9.", StringComparison.Ordinal))
                {
                    var isLts = item.TryGetProperty("lts", out var ltsProp) && ltsProp.ValueKind == JsonValueKind.True;
                    candidates.Add((cycle!, latest!, isLts || cycle == "8.4"));
                }
            }

            var ordered = candidates
                .OrderByDescending(c => c.IsLts)
                .ThenByDescending(c => Version.TryParse(c.Latest, out var v) ? v : new Version(0, 0))
                .Take(4)
                .ToList();

            var checks = ordered.Select(async c =>
            {
                var fileName = PlatformHelper.IsWindows
                    ? $"mysql-{c.Latest}-winx64.zip"
                    : $"mysql-{c.Latest}-linux-glibc2.28-x86_64.tar.xz";
                var url = $"https://cdn.mysql.com/Downloads/MySQL-{c.Cycle}/{fileName}";
                var size = await ProbeContentLengthLabelAsync(url, ct);
                return (c, fileName, url, size);
            });

            var probed = await Task.WhenAll(checks);
            var recAssigned = false;
            foreach (var (c, fileName, url, size) in probed)
            {
                if (size is null)
                    continue;

                results.Add(new PackageRelease
                {
                    Kind = ServiceKind.MySql,
                    InstallMethod = PackageInstallMethod.ArchiveDownload,
                    Version = c.Latest,
                    Series = c.Cycle,
                    DisplayName = $"MySQL {c.Latest}",
                    Subtitle = $"{(PlatformHelper.IsWindows ? "Windows x64 ZIP" : "Linux x64")} • Oficial cdn.mysql.com",
                    DownloadUrl = url,
                    FileName = fileName,
                    SizeLabel = size,
                    IsRecommended = !recAssigned
                });
                recAssigned = true;
            }

            _log.Info("MySQL", $"Catálogo MySQL atualizado: {results.Count} versões.");
        }
        catch (Exception ex)
        {
            _log.Error("MySQL", $"Falha ao consultar versões MySQL: {ex.Message}");
        }
        return results;
    }

    // ==================== MICROSOFT SQL SERVER ====================

    public Task<IReadOnlyList<PackageRelease>> FetchSqlServerReleasesAsync(CancellationToken ct = default)
    {
        if (!PlatformHelper.IsWindows)
            return Task.FromResult<IReadOnlyList<PackageRelease>>([]);

        IReadOnlyList<PackageRelease> list =
        [
            new PackageRelease
            {
                Kind = ServiceKind.SqlServer,
                InstallMethod = PackageInstallMethod.MsiInstaller,
                Version = "2022-LocalDB",
                Series = "2022",
                DisplayName = "SQL Server 2022 LocalDB",
                Subtitle = "Microsoft Oficial (Leve, ideal p/ dev local)",
                DownloadUrl = "https://download.microsoft.com/download/3/8/d/38de7036-2433-4207-8eae-06e247e17b25/SqlLocalDB.msi",
                FileName = "SqlLocalDB-2022.msi",
                SizeLabel = "63.5 MB",
                IsRecommended = true
            },
            new PackageRelease
            {
                Kind = ServiceKind.SqlServer,
                InstallMethod = PackageInstallMethod.MsiInstaller,
                Version = "2019-LocalDB",
                Series = "2019",
                DisplayName = "SQL Server 2019 LocalDB",
                Subtitle = "Microsoft Oficial (LocalDB MSI)",
                DownloadUrl = "https://download.microsoft.com/download/7/c/1/7c14e92e-bdcb-4f89-b7cf-93543e7112d1/SqlLocalDB.msi",
                FileName = "SqlLocalDB-2019.msi",
                SizeLabel = "55.7 MB"
            },
            new PackageRelease
            {
                Kind = ServiceKind.SqlServer,
                InstallMethod = PackageInstallMethod.ExeInstaller,
                Version = "2019-Express",
                Series = "2019",
                DisplayName = "SQL Server 2019 Express",
                Subtitle = "Engine Completo TCP 1433 (Instância BESTART)",
                DownloadUrl = "https://download.microsoft.com/download/7/c/1/7c14e92e-bdcb-4f89-b7cf-93543e7112d1/SQLEXPR_x64_ENU.exe",
                FileName = "SQLEXPR_2019_x64_ENU.exe",
                SizeLabel = "261 MB"
            }
        ];

        return Task.FromResult(list);
    }

    // ==================== MONGODB ====================

    public async Task<IReadOnlyList<PackageRelease>> FetchMongoDbReleasesAsync(CancellationToken ct = default)
    {
        var results = new List<PackageRelease>();
        try
        {
            using var response = await _http.GetAsync(MongoDbCurrentUrl, ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("versions", out var versionsArr))
                return results;

            var targetOs = PlatformHelper.IsWindows ? "windows" : PlatformHelper.IsMacOS ? "macos" : "ubuntu2404";
            var targetArch = PlatformHelper.IsArm64 ? "aarch64" : "x86_64";
            if (PlatformHelper.IsMacOS && PlatformHelper.IsArm64)
                targetArch = "arm64";

            var recAssigned = false;
            foreach (var verObj in versionsArr.EnumerateArray())
            {
                var ver = verObj.TryGetProperty("version", out var vProp) ? vProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(ver) || ver.Contains('-', StringComparison.Ordinal))
                    continue;

                if (!verObj.TryGetProperty("downloads", out var downloadsArr))
                    continue;

                JsonElement? match = null;
                foreach (var dl in downloadsArr.EnumerateArray())
                {
                    var target = dl.TryGetProperty("target", out var tProp) ? tProp.GetString() : null;
                    var arch = dl.TryGetProperty("arch", out var aProp) ? aProp.GetString() : null;
                    var edition = dl.TryGetProperty("edition", out var eProp) ? eProp.GetString() : null;

                    var validEdition = string.Equals(edition, "base", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(edition, "targeted", StringComparison.OrdinalIgnoreCase);

                    if (string.Equals(target, targetOs, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(arch, targetArch, StringComparison.OrdinalIgnoreCase) &&
                        validEdition)
                    {
                        match = dl;
                        break;
                    }
                }

                if (match is null || !match.Value.TryGetProperty("archive", out var archive))
                    continue;

                var url = archive.TryGetProperty("url", out var uProp) ? uProp.GetString() : null;
                var sha256 = archive.TryGetProperty("sha256", out var sProp) ? sProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                var fileName = Path.GetFileName(new Uri(url).AbsolutePath);
                var series = Version.TryParse(ver, out var pv) ? $"{pv.Major}.{pv.Minor}" : ver;

                results.Add(new PackageRelease
                {
                    Kind = ServiceKind.MongoDb,
                    InstallMethod = PackageInstallMethod.ArchiveDownload,
                    Version = ver,
                    Series = series,
                    DisplayName = $"MongoDB {ver}",
                    Subtitle = $"{PlatformHelper.PlatformDisplayName} • Oficial mongodb.org",
                    DownloadUrl = url,
                    FileName = fileName,
                    SizeLabel = PlatformHelper.IsWindows ? "ZIP x64" : "TGZ",
                    Sha256 = sha256,
                    IsRecommended = !recAssigned
                });
                recAssigned = true;

                if (results.Count >= 4)
                    break;
            }

            _log.Info("MongoDB", $"Catálogo MongoDB atualizado: {results.Count} versões.");
        }
        catch (Exception ex)
        {
            _log.Error("MongoDB", $"Falha ao consultar versões MongoDB: {ex.Message}");
        }
        return results;
    }

    // ==================== REDIS ====================

    public async Task<IReadOnlyList<PackageRelease>> FetchRedisReleasesAsync(CancellationToken ct = default)
    {
        if (!PlatformHelper.IsWindows)
            return await FetchHomebrewSeriesAsync(ServiceKind.Redis, "Redis", [("redis", "latest", true)], ct);

        var results = new List<PackageRelease>();
        try
        {
            using var response = await _http.GetAsync(RedisWindowsApiUrl, ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var rel in doc.RootElement.EnumerateArray())
                {
                    var tag = rel.TryGetProperty("tag_name", out var tProp) ? tProp.GetString() : null;
                    if (string.IsNullOrWhiteSpace(tag) || !rel.TryGetProperty("assets", out var assets))
                        continue;

                    string? zipUrl = null;
                    string? zipName = null;
                    long zipSize = 0;

                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = asset.TryGetProperty("name", out var nProp) ? nProp.GetString() : null;
                        if (string.IsNullOrWhiteSpace(name))
                            continue;

                        if (name.Contains("msys2.zip", StringComparison.OrdinalIgnoreCase) &&
                            !name.Contains("Service", StringComparison.OrdinalIgnoreCase))
                        {
                            zipName = name;
                            zipUrl = asset.TryGetProperty("browser_download_url", out var uProp) ? uProp.GetString() : null;
                            zipSize = asset.TryGetProperty("size", out var szProp) ? szProp.GetInt64() : 0;
                            break;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(zipUrl) || string.IsNullOrWhiteSpace(zipName))
                        continue;

                    var cleanVer = tag.TrimStart('v', 'V');
                    results.Add(new PackageRelease
                    {
                        Kind = ServiceKind.Redis,
                        InstallMethod = PackageInstallMethod.ArchiveDownload,
                        Version = cleanVer,
                        Series = cleanVer,
                        DisplayName = $"Redis {cleanVer}",
                        Subtitle = "Windows x64 Portátil (redis-windows)",
                        DownloadUrl = zipUrl,
                        FileName = zipName,
                        SizeLabel = zipSize > 0 ? $"{zipSize / (1024.0 * 1024.0):F1} MB" : "ZIP x64",
                        IsRecommended = results.Count == 0
                    });

                    if (results.Count >= 4)
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn("Redis", $"Falha ao consultar redis-windows: {ex.Message}");
        }

        // Legacy native Windows port fallback/option
        results.Add(new PackageRelease
        {
            Kind = ServiceKind.Redis,
            InstallMethod = PackageInstallMethod.ArchiveDownload,
            Version = "5.0.14.1",
            Series = "5.0",
            DisplayName = "Redis 5.0.14.1",
            Subtitle = "Windows x64 Nativo (tporadowski/redis)",
            DownloadUrl = "https://github.com/tporadowski/redis/releases/download/v5.0.14.1/Redis-x64-5.0.14.1.zip",
            FileName = "Redis-x64-5.0.14.1.zip",
            SizeLabel = "4.8 MB",
            IsRecommended = results.Count == 0
        });

        _log.Info("Redis", $"Catálogo Redis atualizado: {results.Count} versões.");
        return results;
    }

    // ==================== APACHE CASSANDRA ====================

    public async Task<IReadOnlyList<PackageRelease>> FetchCassandraReleasesAsync(CancellationToken ct = default)
    {
        var results = new List<PackageRelease>();
        try
        {
            using var response = await _http.GetAsync(CassandraDownloadsUrl, ct);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(ct);

            var versions = new List<(Version Parsed, string Raw)>();
            foreach (Match m in CassandraDirRegex().Matches(html))
            {
                var raw = m.Groups[1].Value;
                if (Version.TryParse(raw, out var parsed))
                    versions.Add((parsed, raw));
            }

            var ordered = versions
                .DistinctBy(x => x.Raw)
                .OrderByDescending(x => x.Parsed)
                .Take(4)
                .ToList();

            for (var i = 0; i < ordered.Count; i++)
            {
                var v = ordered[i];
                var fileName = $"apache-cassandra-{v.Raw}-bin.tar.gz";
                var url = $"{CassandraDownloadsUrl}{v.Raw}/{fileName}";
                results.Add(new PackageRelease
                {
                    Kind = ServiceKind.Cassandra,
                    InstallMethod = PackageInstallMethod.ArchiveDownload,
                    Version = v.Raw,
                    Series = $"{v.Parsed.Major}.{v.Parsed.Minor}",
                    DisplayName = $"Cassandra {v.Raw}",
                    Subtitle = "Apache Binário (JVM leve 256-512MB) • downloads.apache.org",
                    DownloadUrl = url,
                    FileName = fileName,
                    SizeLabel = "TAR.GZ",
                    IsRecommended = i == 0
                });
            }

            _log.Info("Cassandra", $"Catálogo Cassandra atualizado: {results.Count} versões.");
        }
        catch (Exception ex)
        {
            _log.Error("Cassandra", $"Falha ao consultar versões Cassandra: {ex.Message}");
        }
        return results;
    }

    // ==================== SHARED HELPERS & CACHE ====================

    private async Task<IReadOnlyList<PackageRelease>> FetchHomebrewSeriesAsync(
        ServiceKind kind,
        string displayPrefix,
        (string Formula, string Series, bool IsRecommended)[] candidates,
        CancellationToken ct)
    {
        var results = new List<PackageRelease>();
        var platformLabel = $"{PlatformHelper.PlatformDisplayName} {PlatformHelper.ArchitectureDisplayName}";
        try
        {
            var tasks = candidates.Select(c => FetchSingleHomebrewFormulaAsync(kind, displayPrefix, c.Formula, c.Series, c.IsRecommended, platformLabel, ct));
            var fetched = await Task.WhenAll(tasks);
            results.AddRange(fetched.Where(r => r is not null)!);
            _log.Info(displayPrefix, $"Catálogo {displayPrefix} (Homebrew) atualizado: {results.Count} versões.");
        }
        catch (Exception ex)
        {
            _log.Error(displayPrefix, $"Falha ao consultar {displayPrefix} via Homebrew: {ex.Message}");
        }
        return results;
    }

    private async Task<PackageRelease?> FetchSingleHomebrewFormulaAsync(
        ServiceKind kind, string displayPrefix, string formulaName, string fallbackSeries, bool isRec, string platformLabel, CancellationToken ct)
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

            return new PackageRelease
            {
                Kind = kind,
                InstallMethod = PackageInstallMethod.HomebrewFormula,
                FormulaName = formulaName,
                Version = version,
                Series = fallbackSeries,
                DisplayName = $"{displayPrefix} {version}",
                Subtitle = $"{platformLabel} (Homebrew) • formulae.brew.sh",
                DownloadUrl = $"brew://{formulaName}",
                FileName = $"brew install {formulaName}",
                SizeLabel = "Homebrew",
                IsRecommended = isRec
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> ProbeContentLengthLabelAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                return null;

            var len = resp.Content.Headers.ContentLength;
            return len is > 0 ? $"{len.Value / (1024.0 * 1024.0):F0} MB" : "ZIP x64";
        }
        catch
        {
            return null;
        }
    }

    private static PackageRelease CloneWithRecommended(PackageRelease item, bool recommended) => new()
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
        IsRecommended = recommended
    };

    private static bool TryLoadValidCache(string cachePath, out Dictionary<ServiceKind, IReadOnlyList<PackageRelease>> result)
    {
        result = [];
        try
        {
            if (!File.Exists(cachePath))
                return false;

            var json = File.ReadAllText(cachePath);
            var data = JsonSerializer.Deserialize(json, AppJsonContext.Default.CatalogCacheData);
            if (data is null ||
                !string.Equals(data.PlatformKey, PlatformHelper.PlatformKey, StringComparison.Ordinal) ||
                DateTimeOffset.UtcNow - data.UpdatedAtUtc > CacheTtl)
            {
                return false;
            }

            foreach (var (key, list) in data.Services)
            {
                if (!Enum.TryParse<ServiceKind>(key, out var kind))
                    continue;

                result[kind] = list.Select(e => new PackageRelease
                {
                    Kind = e.Kind,
                    InstallMethod = e.InstallMethod,
                    FormulaName = e.FormulaName,
                    Version = e.Version,
                    Series = e.Series,
                    DisplayName = e.DisplayName,
                    Subtitle = e.Subtitle,
                    DownloadUrl = e.DownloadUrl,
                    FileName = e.FileName,
                    SizeLabel = e.SizeLabel,
                    Sha256 = e.Sha256,
                    IsRecommended = e.IsRecommended
                }).ToList();
            }

            return result.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void SaveCache(string cachePath, Dictionary<ServiceKind, IReadOnlyList<PackageRelease>> catalog)
    {
        try
        {
            var data = new CatalogCacheData
            {
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                PlatformKey = PlatformHelper.PlatformKey
            };

            foreach (var (kind, list) in catalog)
            {
                data.Services[kind.ToString()] = list.Select(r => new CatalogCacheEntry
                {
                    Kind = r.Kind,
                    InstallMethod = r.InstallMethod,
                    FormulaName = r.FormulaName,
                    Version = r.Version,
                    Series = r.Series,
                    DisplayName = r.DisplayName,
                    Subtitle = r.Subtitle,
                    DownloadUrl = r.DownloadUrl,
                    FileName = r.FileName,
                    SizeLabel = r.SizeLabel,
                    Sha256 = r.Sha256,
                    IsRecommended = r.IsRecommended
                }).ToList();
            }

            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var json = JsonSerializer.Serialize(data, AppJsonContext.Default.CatalogCacheData);
            File.WriteAllText(cachePath, json, PlatformHelper.Utf8NoBom);
        }
        catch
        {
            // Non-critical cache failure.
        }
    }

    [GeneratedRegex(@"href=""(\d+\.\d+\.\d+)/""", RegexOptions.IgnoreCase)]
    private static partial Regex CassandraDirRegex();
}
