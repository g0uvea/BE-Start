using System.Buffers;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using BEStart.Models;

namespace BEStart.Services;

public sealed class DBeaverService
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/dbeaver/dbeaver/releases/latest";
    private const string VersionMarkerFile = ".dbeaver-version";

    private readonly HttpClient _http;
    private readonly LogService _log;

    public DBeaverService(HttpClient http, LogService log)
    {
        _http = http;
        _log = log;
    }

    public static string GetInstallDir(AppSettings settings) =>
        Path.Combine(settings.BaseDirectory, "tools", "dbeaver");

    public static string? FindExecutable(AppSettings settings)
    {
        var dir = GetInstallDir(settings);
        if (!Directory.Exists(dir))
            return null;

        if (PlatformHelper.IsWindows)
        {
            var exe = Path.Combine(dir, "dbeaver.exe");
            return File.Exists(exe) ? exe : null;
        }

        if (PlatformHelper.IsMacOS)
        {
            var appExe = Path.Combine(dir, "DBeaver.app", "Contents", "MacOS", "dbeaver");
            if (File.Exists(appExe)) return appExe;
            const string systemApp = "/Applications/DBeaver.app/Contents/MacOS/dbeaver";
            return File.Exists(systemApp) ? systemApp : null;
        }

        var linuxExe = Path.Combine(dir, "dbeaver");
        return File.Exists(linuxExe) ? linuxExe : null;
    }

    public static string? GetInstalledVersion(AppSettings settings)
    {
        var marker = Path.Combine(GetInstallDir(settings), VersionMarkerFile);
        if (!File.Exists(marker))
            return null;

        try
        {
            var v = File.ReadAllText(marker, PlatformHelper.Utf8NoBom).Trim();
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        catch
        {
            return null;
        }
    }

    public async Task<string> LaunchOrUpdateAsync(
        AppSettings settings,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var existingExe = FindExecutable(settings);
        var installedVersion = GetInstalledVersion(settings);

        // If DBeaver is already running, don't replace binaries underneath it
        if (existingExe is not null && IsDBeaverRunning())
        {
            _log.Info("DBeaver", "DBeaver já está em execução.");
            return "DBeaver já está aberto.";
        }

        progress?.Report(new InstallProgress(5, "Verificando versão mais recente do DBeaver..."));
        var latest = await FetchLatestReleaseAsync(ct);

        if (latest is null)
        {
            if (existingExe is not null)
            {
                _log.Warn("DBeaver", "Não foi possível checar atualizações online; iniciando versão local.");
                StartExecutable(existingExe);
                return $"DBeaver {installedVersion ?? ""} iniciado (offline).".Trim();
            }

            throw new InvalidOperationException("Não foi possível consultar o DBeaver no GitHub. Verifique sua conexão.");
        }

        var needInstallOrUpdate = existingExe is null ||
                                  !string.Equals(installedVersion, latest.Value.Version, StringComparison.OrdinalIgnoreCase);

        if (needInstallOrUpdate)
        {
            var actionWord = existingExe is null ? "Instalando" : $"Atualizando ({installedVersion ?? "?"} → {latest.Value.Version})";
            _log.Info("DBeaver", $"{actionWord} DBeaver {latest.Value.Version}...");

            if (PlatformHelper.IsMacOS && latest.Value.FileName.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase))
            {
                await InstallViaHomebrewMacOsAsync(progress, ct);
                existingExe = FindExecutable(settings) ?? "/Applications/DBeaver.app/Contents/MacOS/dbeaver";
            }
            else
            {
                await DownloadAndInstallArchiveAsync(settings, latest.Value.Version, latest.Value.Url, latest.Value.FileName, progress, ct);
                existingExe = FindExecutable(settings)
                    ?? throw new FileNotFoundException("Executável do DBeaver não encontrado após extração.");
            }

            Directory.CreateDirectory(GetInstallDir(settings));
            File.WriteAllText(Path.Combine(GetInstallDir(settings), VersionMarkerFile), latest.Value.Version, PlatformHelper.Utf8NoBom);
            _log.Success("DBeaver", $"DBeaver {latest.Value.Version} pronto!");
        }
        else
        {
            _log.Info("DBeaver", $"DBeaver {installedVersion} já está atualizado.");
        }

        progress?.Report(new InstallProgress(98, "Abrindo DBeaver..."));
        var launchPath = existingExe ?? throw new FileNotFoundException("Executável do DBeaver não encontrado.");
        StartExecutable(launchPath);
        return $"DBeaver {latest.Value.Version} iniciado!";
    }

    private async Task<(string Version, string FileName, string Url)?> FetchLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));

            using var resp = await _http.GetAsync(LatestReleaseApiUrl, timeoutCts.Token);
            if (!resp.IsSuccessStatusCode)
                return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(timeoutCts.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeoutCts.Token);

            var tag = doc.RootElement.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag) || !doc.RootElement.TryGetProperty("assets", out var assets))
                return null;

            var suffix = PlatformHelper.IsWindows
                ? (PlatformHelper.IsArm64 ? "-windows-aarch64.zip" : "-windows-x86_64.zip")
                : PlatformHelper.IsMacOS
                    ? (PlatformHelper.IsArm64 ? "-macos-aarch64.dmg" : "-macos-x86_64.dmg")
                    : (PlatformHelper.IsArm64 ? "-linux-aarch64.tar.gz" : "-linux-x86_64.tar.gz");

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nProp) ? nProp.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var uProp) ? uProp.GetString() : null;
                if (!string.IsNullOrWhiteSpace(name) &&
                    !string.IsNullOrWhiteSpace(url) &&
                    name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return (tag, name, url);
                }
            }
        }
        catch
        {
            // Offline or timeout fallback
        }
        return null;
    }

    private async Task DownloadAndInstallArchiveAsync(
        AppSettings settings,
        string version,
        string url,
        string fileName,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(settings.DownloadsDirectory);
        var archivePath = Path.Combine(settings.DownloadsDirectory, fileName);
        var tempFile = archivePath + ".part";

        using (var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength ?? -1L;

            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);

            var buffer = ArrayPool<byte>.Shared.Rent(65536);
            try
            {
                long totalRead = 0;
                var sw = Stopwatch.StartNew();
                var lastReportMs = 0L;

                while (true)
                {
                    var read = await contentStream.ReadAsync(buffer.AsMemory(0, 65536), ct);
                    if (read == 0) break;
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    totalRead += read;

                    if (sw.ElapsedMilliseconds - lastReportMs >= 180)
                    {
                        lastReportMs = sw.ElapsedMilliseconds;
                        var mbRead = totalRead / (1024.0 * 1024.0);
                        var speed = sw.Elapsed.TotalSeconds > 0.2 ? mbRead / sw.Elapsed.TotalSeconds : 0;
                        if (totalBytes > 0)
                        {
                            var mbTotal = totalBytes / (1024.0 * 1024.0);
                            var pct = 8.0 + (totalRead / (double)totalBytes) * 68.0;
                            progress?.Report(new InstallProgress(pct, $"Baixando DBeaver {version}: {mbRead:F1}/{mbTotal:F1} MB ({speed:F1} MB/s)"));
                        }
                        else
                        {
                            progress?.Report(new InstallProgress(40, $"Baixando DBeaver {version}: {mbRead:F1} MB"));
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        if (File.Exists(archivePath)) File.Delete(archivePath);
        File.Move(tempFile, archivePath);

        progress?.Report(new InstallProgress(80, $"Extraindo DBeaver {version}..."));
        var tempExtract = Path.Combine(settings.DownloadsDirectory, $"dbeaver_extract_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempExtract);

        try
        {
            if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Run(() => ZipFile.ExtractToDirectory(archivePath, tempExtract, overwriteFiles: true), ct);
            }
            else
            {
                await Task.Run(() =>
                {
                    using var fs = File.OpenRead(archivePath);
                    using var gz = new GZipStream(fs, CompressionMode.Decompress);
                    TarFile.ExtractToDirectory(gz, tempExtract, overwriteFiles: true);
                }, ct);
            }

            var topDirs = Directory.GetDirectories(tempExtract);
            var topFiles = Directory.GetFiles(tempExtract);
            var contentRoot = (topDirs.Length == 1 && topFiles.Length == 0) ? topDirs[0] : tempExtract;

            var targetDir = GetInstallDir(settings);
            if (Directory.Exists(targetDir))
                Directory.Delete(targetDir, recursive: true);

            Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
            try
            {
                Directory.Move(contentRoot, targetDir);
            }
            catch (IOException)
            {
                CopyDirectory(contentRoot, targetDir);
            }

            if (PlatformHelper.IsUnix)
                PlatformHelper.EnsureUnixExecutable(Path.Combine(targetDir, "dbeaver"));
        }
        finally
        {
            if (Directory.Exists(tempExtract))
            {
                try { Directory.Delete(tempExtract, recursive: true); } catch { }
            }
            // Remove downloaded archive after install to keep disk light
            if (File.Exists(archivePath))
            {
                try { File.Delete(archivePath); } catch { }
            }
        }
    }

    private async Task InstallViaHomebrewMacOsAsync(IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var brew = PlatformHelper.FindHomebrewExecutable()
            ?? throw new InvalidOperationException("Homebrew não encontrado para instalar/atualizar DBeaver no macOS.");

        progress?.Report(new InstallProgress(35, "Instalando/atualizando DBeaver via Homebrew..."));
        var isUpdate = Directory.Exists("/Applications/DBeaver.app");
        var psi = new ProcessStartInfo
        {
            FileName = brew,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(isUpdate ? "upgrade" : "install");
        psi.ArgumentList.Add("--cask");
        psi.ArgumentList.Add("dbeaver-community");

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Falha ao executar Homebrew.");
        await proc.WaitForExitAsync(ct);
    }

    private static bool IsDBeaverRunning()
    {
        try
        {
            return Process.GetProcessesByName("dbeaver").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void StartExecutable(string exePath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
            UseShellExecute = true
        });
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectory(dir, Path.Combine(targetDir, Path.GetFileName(dir)));
    }
}
