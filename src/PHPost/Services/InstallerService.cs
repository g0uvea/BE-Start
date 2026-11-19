using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PHPost.Models;

namespace PHPost.Services;

public sealed record InstallProgress(double Percentage, string Stage);

public sealed class InstallerService
{
    private readonly HttpClient _http;
    private readonly LogService _log;

    public InstallerService(HttpClient http, LogService log)
    {
        _http = http;
        _log = log;
    }

    public IReadOnlyList<string> GetInstalledVersions(AppSettings settings, ServiceKind kind)
    {
        var root = kind == ServiceKind.Php
            ? settings.PhpRuntimesDirectory
            : settings.MariaDbRuntimesDirectory;

        if (!Directory.Exists(root))
            return [];

        var installed = new List<string>();
        foreach (var dir in Directory.GetDirectories(root))
        {
            var valid = kind == ServiceKind.Php
                ? File.Exists(Path.Combine(dir, "php.exe"))
                : FindMariaDbServerExecutable(dir) is not null;

            if (valid)
                installed.Add(Path.GetFileName(dir));
        }

        installed.Sort((a, b) => string.Compare(b, a, StringComparison.OrdinalIgnoreCase));
        return installed;
    }

    public bool IsVersionInstalled(AppSettings settings, ServiceKind kind, string version)
    {
        var dir = kind == ServiceKind.Php
            ? settings.GetPhpInstallDirectory(version)
            : settings.GetMariaDbInstallDirectory(version);

        return kind == ServiceKind.Php
            ? File.Exists(Path.Combine(dir, "php.exe"))
            : FindMariaDbServerExecutable(dir) is not null;
    }

    public async Task<string> InstallReleaseAsync(
        PackageRelease release,
        AppSettings settings,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(settings.DownloadsDirectory);
        var zipPath = Path.Combine(settings.DownloadsDirectory, release.FileName);
        var targetDir = release.Kind == ServiceKind.Php
            ? settings.GetPhpInstallDirectory(release.Version)
            : settings.GetMariaDbInstallDirectory(release.Version);

        var sourceName = release.Kind == ServiceKind.Php ? "PHP" : "MariaDB";
        _log.Info(sourceName, $"Iniciando download de {release.DisplayName}...");
        progress?.Report(new InstallProgress(2, $"Conectando para baixar {release.DisplayName}..."));

        // 1. Download ZIP (skip download if cached zip matches expected SHA-256)
        var needDownload = true;
        if (File.Exists(zipPath) && !string.IsNullOrWhiteSpace(release.Sha256))
        {
            progress?.Report(new InstallProgress(5, "Verificando arquivo em cache..."));
            var cachedHash = await ComputeSha256Async(zipPath, ct);
            if (string.Equals(cachedHash, release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                needDownload = false;
                _log.Info(sourceName, $"Usando pacote em cache verificado ({release.FileName}).");
            }
        }

        if (needDownload)
        {
            await DownloadFileAsync(release.DownloadUrl, zipPath, release.DisplayName, progress, ct);

            if (!string.IsNullOrWhiteSpace(release.Sha256))
            {
                progress?.Report(new InstallProgress(72, "Verificando integridade SHA-256..."));
                var actualHash = await ComputeSha256Async(zipPath, ct);
                if (!string.Equals(actualHash, release.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(zipPath);
                    throw new InvalidOperationException(
                        $"Checksum SHA-256 inválido para {release.FileName}. Tente baixar novamente.");
                }
                _log.Success(sourceName, "Integridade SHA-256 verificada com sucesso.");
            }
        }

        // 2. Extract ZIP cleanly to temp folder, then move into targetDir
        progress?.Report(new InstallProgress(76, $"Extraindo {release.DisplayName}..."));
        _log.Info(sourceName, $"Extraindo pacote em {targetDir}...");

        var tempExtract = Path.Combine(settings.DownloadsDirectory, $"extract_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempExtract);

        try
        {
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, tempExtract, overwriteFiles: true), ct);

            // If archive has a single top-level directory (like mariadb-11.8.10-winx64/), unwrap it.
            var topDirs = Directory.GetDirectories(tempExtract);
            var topFiles = Directory.GetFiles(tempExtract);
            var contentRoot = (topDirs.Length == 1 && topFiles.Length == 0)
                ? topDirs[0]
                : tempExtract;

            if (Directory.Exists(targetDir))
                Directory.Delete(targetDir, recursive: true);

            Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
            MoveDirectoryCrossVolumeSafe(contentRoot, targetDir);
        }
        finally
        {
            if (Directory.Exists(tempExtract))
            {
                try { Directory.Delete(tempExtract, recursive: true); } catch { }
            }
        }

        // 3. Post-install automatic configuration
        if (release.Kind == ServiceKind.Php)
        {
            progress?.Report(new InstallProgress(92, "Configurando php.ini e extensões essenciais..."));
            ConfigurePhpIni(targetDir);
            _log.Success("PHP", $"{release.DisplayName} instalado e configurado com php.ini pronto!");
        }
        else
        {
            progress?.Report(new InstallProgress(90, "Inicializando banco de dados MariaDB (data/my.ini)..."));
            await EnsureMariaDbInitializedAsync(targetDir, settings, ct);
            _log.Success("MariaDB", $"{release.DisplayName} instalado e inicializado com sucesso!");
        }

        progress?.Report(new InstallProgress(100, $"{release.DisplayName} pronto!"));
        return targetDir;
    }

    public void ConfigurePhpIni(string phpDir)
    {
        var iniPath = Path.Combine(phpDir, "php.ini");
        var devIni = Path.Combine(phpDir, "php.ini-development");
        var prodIni = Path.Combine(phpDir, "php.ini-production");

        if (!File.Exists(iniPath))
        {
            if (File.Exists(devIni))
                File.Copy(devIni, iniPath);
            else if (File.Exists(prodIni))
                File.Copy(prodIni, iniPath);
            else
                File.WriteAllText(iniPath, "[PHP]\n");
        }

        var content = File.ReadAllText(iniPath, Encoding.UTF8);

        // Enable extension_dir = "ext"
        content = Regex.Replace(
            content,
            @"^\s*;?\s*extension_dir\s*=\s*""ext""\s*$",
            @"extension_dir = ""ext""",
            RegexOptions.Multiline);

        if (!Regex.IsMatch(content, @"^extension_dir\s*=", RegexOptions.Multiline))
            content += Environment.NewLine + @"extension_dir = ""ext""" + Environment.NewLine;

        // Enable essential extensions for modern PHP + MariaDB development
        string[] desiredExtensions =
        [
            "curl",
            "fileinfo",
            "gd",
            "intl",
            "mbstring",
            "mysqli",
            "openssl",
            "pdo_mysql",
            "zip"
        ];

        var extDir = Path.Combine(phpDir, "ext");
        foreach (var ext in desiredExtensions)
        {
            var dllExists = File.Exists(Path.Combine(extDir, $"php_{ext}.dll"));
            if (!dllExists)
                continue;

            var pattern = $@"^\s*;\s*extension\s*=\s*(?:php_)?{Regex.Escape(ext)}(?:\.dll)?\s*$";
            if (Regex.IsMatch(content, pattern, RegexOptions.Multiline))
            {
                content = Regex.Replace(content, pattern, $"extension={ext}", RegexOptions.Multiline);
            }
            else if (!Regex.IsMatch(content, $@"^\s*extension\s*=\s*(?:php_)?{Regex.Escape(ext)}(?:\.dll)?\s*$", RegexOptions.Multiline))
            {
                content += $"{Environment.NewLine}extension={ext}";
            }
        }

        // Set sensible local dev defaults (timezone & memory)
        content = Regex.Replace(
            content,
            @"^\s*;?\s*date\.timezone\s*=.*$",
            "date.timezone = America/Sao_Paulo",
            RegexOptions.Multiline);

        File.WriteAllText(iniPath, content, Encoding.UTF8);
        _log.Info("PHP", $"php.ini configurado em {iniPath} (pdo_mysql, mysqli, mbstring, curl, openssl, gd, zip).");
    }

    public async Task EnsureMariaDbInitializedAsync(string mariaDbDir, AppSettings settings, CancellationToken ct = default)
    {
        var dataDir = settings.MariaDbDataDirectory;
        var myIniPath = Path.Combine(dataDir, "my.ini");
        var mysqlSystemDir = Path.Combine(dataDir, "mysql");

        if (!Directory.Exists(mysqlSystemDir))
        {
            Directory.CreateDirectory(dataDir);
            var installDbExe = FindMariaDbTool(mariaDbDir, "mariadb-install-db.exe", "mysql_install_db.exe")
                ?? throw new FileNotFoundException("Executável mariadb-install-db.exe não encontrado na pasta bin.");

            _log.Info("MariaDB", $"Inicializando diretório de dados em {dataDir}...");

            var psi = new ProcessStartInfo
            {
                FileName = installDbExe,
                Arguments = $"--datadir=\"{dataDir}\" --port={settings.MariaDbPort}",
                WorkingDirectory = Path.GetDirectoryName(installDbExe)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Não foi possível iniciar mariadb-install-db.exe.");

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);

            await proc.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (!string.IsNullOrWhiteSpace(stdout))
                _log.Info("MariaDB", stdout.Trim());

            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Falha ao inicializar banco de dados MariaDB (código {proc.ExitCode}): {stderr.Trim()}");
            }
        }

        SyncMariaDbMyIni(myIniPath, mariaDbDir, dataDir, settings.MariaDbPort);
    }

    public void SyncMariaDbMyIni(string myIniPath, string mariaDbDir, string dataDir, int port)
    {
        var baseDirUnix = mariaDbDir.Replace('\\', '/');
        var dataDirUnix = dataDir.Replace('\\', '/');

        if (!File.Exists(myIniPath))
        {
            var ini = $"""
[mysqld]
basedir={baseDirUnix}
datadir={dataDirUnix}
port={port}
bind-address=127.0.0.1
character-set-server=utf8mb4
collation-server=utf8mb4_unicode_ci
max_allowed_packet=64M

[client]
port={port}
default-character-set=utf8mb4
""";
            Directory.CreateDirectory(Path.GetDirectoryName(myIniPath)!);
            File.WriteAllText(myIniPath, ini, Encoding.UTF8);
            return;
        }

        var text = File.ReadAllText(myIniPath, Encoding.UTF8);
        text = UpsertIniKey(text, "mysqld", "port", port.ToString());
        text = UpsertIniKey(text, "mysqld", "basedir", baseDirUnix);
        text = UpsertIniKey(text, "mysqld", "datadir", dataDirUnix);
        text = UpsertIniKey(text, "mysqld", "bind-address", "127.0.0.1");
        text = UpsertIniKey(text, "client", "port", port.ToString());
        File.WriteAllText(myIniPath, text, Encoding.UTF8);
    }

    public void UninstallVersion(AppSettings settings, ServiceKind kind, string version)
    {
        var dir = kind == ServiceKind.Php
            ? settings.GetPhpInstallDirectory(version)
            : settings.GetMariaDbInstallDirectory(version);

        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
            _log.Warn(kind == ServiceKind.Php ? "PHP" : "MariaDB", $"Versão {version} removida de {dir}.");
        }
    }

    public static string? FindMariaDbServerExecutable(string mariaDbDir) =>
        FindMariaDbTool(mariaDbDir, "mariadbd.exe", "mysqld.exe");

    public static string? FindMariaDbAdminExecutable(string mariaDbDir) =>
        FindMariaDbTool(mariaDbDir, "mariadb-admin.exe", "mysqladmin.exe");

    public static string? FindMariaDbTool(string mariaDbDir, params string[] names)
    {
        var binDir = Path.Combine(mariaDbDir, "bin");
        foreach (var name in names)
        {
            var candidate = Path.Combine(binDir, name);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private async Task DownloadFileAsync(
        string url,
        string destinationPath,
        string label,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        var tempFile = destinationPath + ".part";

        await using (var contentStream = await response.Content.ReadAsStreamAsync(ct))
        await using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            var buffer = new byte[81920];
            long totalRead = 0;
            var sw = Stopwatch.StartNew();
            var lastReportMs = 0L;

            while (true)
            {
                var read = await contentStream.ReadAsync(buffer, ct);
                if (read == 0)
                    break;

                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                totalRead += read;

                if (sw.ElapsedMilliseconds - lastReportMs >= 120)
                {
                    lastReportMs = sw.ElapsedMilliseconds;
                    var mbRead = totalRead / (1024.0 * 1024.0);
                    var speedMb = sw.Elapsed.TotalSeconds > 0.2 ? mbRead / sw.Elapsed.TotalSeconds : 0;

                    if (totalBytes > 0)
                    {
                        var mbTotal = totalBytes / (1024.0 * 1024.0);
                        var pct = 5.0 + (totalRead / (double)totalBytes) * 65.0;
                        progress?.Report(new InstallProgress(pct, $"Baixando {label}: {mbRead:F1} / {mbTotal:F1} MB ({speedMb:F1} MB/s)"));
                    }
                    else
                    {
                        progress?.Report(new InstallProgress(35, $"Baixando {label}: {mbRead:F1} MB ({speedMb:F1} MB/s)"));
                    }
                }
            }
        }

        if (File.Exists(destinationPath))
            File.Delete(destinationPath);
        File.Move(tempFile, destinationPath);
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void MoveDirectoryCrossVolumeSafe(string sourceDir, string targetDir)
    {
        try
        {
            Directory.Move(sourceDir, targetDir);
        }
        catch (IOException)
        {
            CopyDirectoryRecursive(sourceDir, targetDir);
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    private static void CopyDirectoryRecursive(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectoryRecursive(dir, Path.Combine(targetDir, Path.GetFileName(dir)));
    }

    private static string UpsertIniKey(string iniText, string section, string key, string value)
    {
        var sectionHeader = $"[{section}]";
        var lines = iniText.Replace("\r\n", "\n").Split('\n').ToList();

        var sectionStart = -1;
        var sectionEnd = lines.Count;

        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Equals(sectionHeader, StringComparison.OrdinalIgnoreCase))
            {
                sectionStart = i;
                for (var j = i + 1; j < lines.Count; j++)
                {
                    if (lines[j].TrimStart().StartsWith('['))
                    {
                        sectionEnd = j;
                        break;
                    }
                }
                break;
            }
        }

        if (sectionStart == -1)
        {
            lines.Add("");
            lines.Add(sectionHeader);
            lines.Add($"{key}={value}");
            return string.Join(Environment.NewLine, lines);
        }

        for (var i = sectionStart + 1; i < sectionEnd; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith(key + " =", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"{key}={value}";
                return string.Join(Environment.NewLine, lines);
            }
        }

        lines.Insert(sectionStart + 1, $"{key}={value}");
        return string.Join(Environment.NewLine, lines);
    }
}
