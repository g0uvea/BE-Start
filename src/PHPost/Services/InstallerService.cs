using System.Diagnostics;
using System.Formats.Tar;
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
    private const string ExternalPrefixMarkerFile = ".phpost-external-prefix";

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
                ? FindPhpExecutable(dir) is not null
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
            ? FindPhpExecutable(dir) is not null
            : FindMariaDbServerExecutable(dir) is not null;
    }

    public async Task<string> InstallReleaseAsync(
        PackageRelease release,
        AppSettings settings,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (release.InstallMethod == PackageInstallMethod.HomebrewFormula)
        {
            return await InstallViaHomebrewAsync(release, settings, progress, ct);
        }

        Directory.CreateDirectory(settings.DownloadsDirectory);
        var archivePath = Path.Combine(settings.DownloadsDirectory, release.FileName);
        var targetDir = release.Kind == ServiceKind.Php
            ? settings.GetPhpInstallDirectory(release.Version)
            : settings.GetMariaDbInstallDirectory(release.Version);

        var sourceName = release.Kind == ServiceKind.Php ? "PHP" : "MariaDB";
        _log.Info(sourceName, $"Iniciando download de {release.DisplayName}...");
        progress?.Report(new InstallProgress(2, $"Conectando para baixar {release.DisplayName}..."));

        // 1. Download archive (skip download if cached file matches expected SHA-256)
        var needDownload = true;
        if (File.Exists(archivePath) && !string.IsNullOrWhiteSpace(release.Sha256))
        {
            progress?.Report(new InstallProgress(5, "Verificando arquivo em cache..."));
            var cachedHash = await ComputeSha256Async(archivePath, ct);
            if (string.Equals(cachedHash, release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                needDownload = false;
                _log.Info(sourceName, $"Usando pacote em cache verificado ({release.FileName}).");
            }
        }

        if (needDownload)
        {
            await DownloadFileAsync(release.DownloadUrl, archivePath, release.DisplayName, progress, ct);

            if (!string.IsNullOrWhiteSpace(release.Sha256))
            {
                progress?.Report(new InstallProgress(72, "Verificando integridade SHA-256..."));
                var actualHash = await ComputeSha256Async(archivePath, ct);
                if (!string.Equals(actualHash, release.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(archivePath);
                    throw new InvalidOperationException(
                        $"Checksum SHA-256 inválido para {release.FileName}. Tente baixar novamente.");
                }
                _log.Success(sourceName, "Integridade SHA-256 verificada com sucesso.");
            }
        }

        // 2. Extract archive cleanly to temp folder, then move into targetDir
        progress?.Report(new InstallProgress(76, $"Extraindo {release.DisplayName}..."));
        _log.Info(sourceName, $"Extraindo pacote em {targetDir}...");

        var tempExtract = Path.Combine(settings.DownloadsDirectory, $"extract_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempExtract);

        try
        {
            await ExtractArchiveAsync(archivePath, tempExtract, ct);

            // If archive has a single top-level directory (like mariadb-11.8.9-winx64/), unwrap it.
            var topDirs = Directory.GetDirectories(tempExtract);
            var topFiles = Directory.GetFiles(tempExtract);
            var contentRoot = (topDirs.Length == 1 && topFiles.Length == 0)
                ? topDirs[0]
                : tempExtract;

            if (Directory.Exists(targetDir))
                Directory.Delete(targetDir, recursive: true);

            Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
            MoveDirectoryCrossVolumeSafe(contentRoot, targetDir);

            if (PlatformHelper.IsUnix)
            {
                EnsureRuntimePermissions(targetDir, release.Kind);
                if (PlatformHelper.IsMacOS)
                    await RemoveMacOsQuarantineAsync(targetDir, ct);
            }
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
            progress?.Report(new InstallProgress(90, $"Inicializando banco de dados MariaDB (data/{PlatformHelper.MariaDbConfigFileName})..."));
            await EnsureMariaDbInitializedAsync(targetDir, settings, ct);
            _log.Success("MariaDB", $"{release.DisplayName} instalado e inicializado com sucesso!");
        }

        progress?.Report(new InstallProgress(100, $"{release.DisplayName} pronto!"));
        return targetDir;
    }

    private async Task<string> InstallViaHomebrewAsync(
        PackageRelease release,
        AppSettings settings,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        var formula = release.FormulaName;
        if (string.IsNullOrWhiteSpace(formula))
            throw new InvalidOperationException("Fórmula Homebrew não informada para este pacote.");

        var brewExe = PlatformHelper.FindHomebrewExecutable();
        if (brewExe is null)
        {
            throw new InvalidOperationException(
                "Homebrew não encontrado nesta máquina. Instale o Homebrew (https://brew.sh) e tente novamente.");
        }

        var sourceName = release.Kind == ServiceKind.Php ? "PHP" : "MariaDB";
        var targetDir = release.Kind == ServiceKind.Php
            ? settings.GetPhpInstallDirectory(release.Version)
            : settings.GetMariaDbInstallDirectory(release.Version);

        progress?.Report(new InstallProgress(10, $"Verificando {formula} no Homebrew..."));
        _log.Info(sourceName, $"Verificando instalação do Homebrew ({formula})...");

        var existingPrefix = await GetBrewFormulaPrefixAsync(brewExe, formula, ct);
        if (string.IsNullOrWhiteSpace(existingPrefix) || FindMariaDbServerExecutable(existingPrefix) is null)
        {
            progress?.Report(new InstallProgress(25, $"Executando brew install {formula}..."));
            _log.Info(sourceName, $"Instalando {formula} via Homebrew (isso pode levar alguns instantes)...");

            var installPsi = new ProcessStartInfo
            {
                FileName = brewExe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            installPsi.ArgumentList.Add("install");
            installPsi.ArgumentList.Add(formula);
            installPsi.Environment["HOMEBREW_NO_AUTO_UPDATE"] = "1";

            using var proc = Process.Start(installPsi)
                ?? throw new InvalidOperationException("Não foi possível iniciar o processo do Homebrew.");

            var errBuilder = new StringBuilder();
            proc.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    _log.Info(sourceName, e.Data);
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    errBuilder.AppendLine(e.Data);
                    _log.Info(sourceName, e.Data);
                }
            };

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Falha ao executar `brew install {formula}` (código {proc.ExitCode}): {errBuilder.ToString().Trim()}");
            }

            existingPrefix = await GetBrewFormulaPrefixAsync(brewExe, formula, ct);
        }
        else
        {
            _log.Info(sourceName, $"{formula} já está instalado no Homebrew em {existingPrefix}.");
        }

        if (string.IsNullOrWhiteSpace(existingPrefix) || !Directory.Exists(existingPrefix))
        {
            throw new InvalidOperationException(
                $"Não foi possível localizar o diretório da fórmula {formula} instalada pelo Homebrew.");
        }

        progress?.Report(new InstallProgress(80, $"Registrando {release.DisplayName} no PHPost..."));
        Directory.CreateDirectory(targetDir);
        File.WriteAllText(
            Path.Combine(targetDir, ExternalPrefixMarkerFile),
            existingPrefix.Trim(),
            PlatformHelper.Utf8NoBom);

        if (release.Kind == ServiceKind.MariaDb)
        {
            progress?.Report(new InstallProgress(90, $"Inicializando banco de dados MariaDB (data/{PlatformHelper.MariaDbConfigFileName})..."));
            await EnsureMariaDbInitializedAsync(targetDir, settings, ct);
            _log.Success("MariaDB", $"{release.DisplayName} vinculado via Homebrew e inicializado com sucesso!");
        }

        progress?.Report(new InstallProgress(100, $"{release.DisplayName} pronto!"));
        return targetDir;
    }

    public void ConfigurePhpIni(string phpDir)
    {
        var iniPath = Path.Combine(phpDir, "php.ini");
        var devIni = Path.Combine(phpDir, "php.ini-development");
        var prodIni = Path.Combine(phpDir, "php.ini-production");

        if (!PlatformHelper.IsWindows)
        {
            if (!File.Exists(iniPath))
            {
                const string unixIni = """
[PHP]
engine = On
short_open_tag = Off
display_errors = On
display_startup_errors = On
error_reporting = E_ALL
memory_limit = 256M
upload_max_filesize = 64M
post_max_size = 64M
max_execution_time = 120
date.timezone = America/Sao_Paulo
""";
                File.WriteAllText(iniPath, unixIni + Environment.NewLine, PlatformHelper.Utf8NoBom);
            }
            else
            {
                var existing = File.ReadAllText(iniPath, Encoding.UTF8).TrimStart('\uFEFF');
                File.WriteAllText(iniPath, existing, PlatformHelper.Utf8NoBom);
            }

            _log.Info("PHP", $"php.ini configurado em {iniPath} (extensões compiladas estaticamente no binário PHP).");
            return;
        }

        if (!File.Exists(iniPath))
        {
            if (File.Exists(devIni))
                File.Copy(devIni, iniPath);
            else if (File.Exists(prodIni))
                File.Copy(prodIni, iniPath);
            else
                File.WriteAllText(iniPath, "[PHP]\r\n", PlatformHelper.Utf8NoBom);
        }

        var content = File.ReadAllText(iniPath, Encoding.UTF8).TrimStart('\uFEFF');

        // Enable extension_dir = "ext" (replace first matching commented/active line)
        var extDirRegex = new Regex(@"^\s*;?\s*extension_dir\s*=\s*""ext""\s*$", RegexOptions.Multiline);
        if (extDirRegex.IsMatch(content))
        {
            content = extDirRegex.Replace(content, @"extension_dir = ""ext""", 1);
        }
        else if (!Regex.IsMatch(content, @"^extension_dir\s*=", RegexOptions.Multiline))
        {
            content += Environment.NewLine + @"extension_dir = ""ext""" + Environment.NewLine;
        }

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

            var activeRegex = new Regex(
                $@"^\s*extension\s*=\s*(?:php_)?{Regex.Escape(ext)}(?:\.dll)?\s*$",
                RegexOptions.Multiline);

            // If already active (possibly duplicated by older versions), keep only one active line
            var activeMatches = activeRegex.Matches(content);
            if (activeMatches.Count > 1)
            {
                var keptFirst = false;
                content = activeRegex.Replace(content, m =>
                {
                    if (!keptFirst)
                    {
                        keptFirst = true;
                        return $"extension={ext}";
                    }
                    return $"; {m.Value.Trim()}";
                });
                continue;
            }
            if (activeMatches.Count == 1)
                continue;

            // Prefer standard unindented ";extension=<name>" line so we don't uncomment doc examples like ";   extension=mysqli"
            var exactCommentRegex = new Regex(
                $@"^;extension\s*=\s*(?:php_)?{Regex.Escape(ext)}(?:\.dll)?\s*$",
                RegexOptions.Multiline);

            if (exactCommentRegex.IsMatch(content))
            {
                content = exactCommentRegex.Replace(content, $"extension={ext}", 1);
            }
            else
            {
                var looseCommentRegex = new Regex(
                    $@"^\s*;\s*extension\s*=\s*(?:php_)?{Regex.Escape(ext)}(?:\.dll)?\s*$",
                    RegexOptions.Multiline);
                if (looseCommentRegex.IsMatch(content))
                    content = looseCommentRegex.Replace(content, $"extension={ext}", 1);
                else
                    content += $"{Environment.NewLine}extension={ext}";
            }
        }

        // Set sensible local dev defaults (timezone)
        var tzRegex = new Regex(@"^\s*;?\s*date\.timezone\s*=.*$", RegexOptions.Multiline);
        if (tzRegex.IsMatch(content))
            content = tzRegex.Replace(content, "date.timezone = America/Sao_Paulo", 1);
        else
            content += $"{Environment.NewLine}date.timezone = America/Sao_Paulo";

        File.WriteAllText(iniPath, content, PlatformHelper.Utf8NoBom);
        _log.Info("PHP", $"php.ini configurado em {iniPath} (pdo_mysql, mysqli, mbstring, curl, openssl, gd, zip).");
    }

    public async Task EnsureMariaDbInitializedAsync(string mariaDbDir, AppSettings settings, CancellationToken ct = default)
    {
        var effectiveDir = ResolveEffectiveRuntimeDirectory(mariaDbDir);
        var dataDir = settings.MariaDbDataDirectory;
        var configPath = PlatformHelper.ResolveMariaDbConfigPath(dataDir);
        var mysqlSystemDir = Path.Combine(dataDir, "mysql");

        if (!Directory.Exists(mysqlSystemDir))
        {
            Directory.CreateDirectory(dataDir);
            var installDbExe = FindMariaDbInstallDbExecutable(effectiveDir)
                ?? throw new FileNotFoundException("Executável mariadb-install-db não encontrado na instalação do MariaDB.");

            _log.Info("MariaDB", $"Inicializando diretório de dados em {dataDir}...");

            var psi = new ProcessStartInfo
            {
                FileName = installDbExe,
                WorkingDirectory = PlatformHelper.IsWindows
                    ? Path.GetDirectoryName(installDbExe)!
                    : effectiveDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (PlatformHelper.IsWindows)
            {
                psi.ArgumentList.Add($"--datadir={dataDir}");
                psi.ArgumentList.Add($"--port={settings.MariaDbPort}");
            }
            else
            {
                psi.ArgumentList.Add($"--basedir={effectiveDir}");
                psi.ArgumentList.Add($"--datadir={dataDir}");
                psi.ArgumentList.Add("--auth-root-authentication-method=normal");
                psi.ArgumentList.Add("--skip-test-db");
            }

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Não foi possível iniciar mariadb-install-db.");

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

        SyncMariaDbConfig(configPath, effectiveDir, dataDir, settings.MariaDbPort, settings.MariaDbSocketPath);
    }

    public void SyncMariaDbConfig(string configPath, string mariaDbDir, string dataDir, int port, string? socketPath = null)
    {
        var effectiveDir = ResolveEffectiveRuntimeDirectory(mariaDbDir);
        var baseDirUnix = effectiveDir.Replace('\\', '/');
        var dataDirUnix = dataDir.Replace('\\', '/');
        var socketUnix = socketPath?.Replace('\\', '/');

        if (!File.Exists(configPath))
        {
            var sb = new StringBuilder();
            sb.AppendLine("[mysqld]");
            sb.AppendLine($"basedir={baseDirUnix}");
            sb.AppendLine($"datadir={dataDirUnix}");
            sb.AppendLine($"port={port}");
            sb.AppendLine("bind-address=127.0.0.1");
            if (PlatformHelper.IsUnix && !string.IsNullOrWhiteSpace(socketUnix))
                sb.AppendLine($"socket={socketUnix}");
            sb.AppendLine("character-set-server=utf8mb4");
            sb.AppendLine("collation-server=utf8mb4_unicode_ci");
            sb.AppendLine("max_allowed_packet=64M");
            sb.AppendLine();
            sb.AppendLine("[client]");
            sb.AppendLine($"port={port}");
            if (PlatformHelper.IsUnix && !string.IsNullOrWhiteSpace(socketUnix))
                sb.AppendLine($"socket={socketUnix}");
            sb.AppendLine("default-character-set=utf8mb4");

            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            File.WriteAllText(configPath, sb.ToString(), PlatformHelper.Utf8NoBom);
            return;
        }

        // Always strip any existing UTF-8 BOM (EF BB BF) and leading whitespace so MariaDB parses line 1 cleanly
        var text = File.ReadAllText(configPath, Encoding.UTF8).TrimStart('\uFEFF', '\r', '\n');
        text = UpsertIniKey(text, "mysqld", "port", port.ToString());
        text = UpsertIniKey(text, "mysqld", "basedir", baseDirUnix);
        text = UpsertIniKey(text, "mysqld", "datadir", dataDirUnix);
        text = UpsertIniKey(text, "mysqld", "bind-address", "127.0.0.1");
        if (PlatformHelper.IsUnix && !string.IsNullOrWhiteSpace(socketUnix))
            text = UpsertIniKey(text, "mysqld", "socket", socketUnix);

        text = UpsertIniKey(text, "client", "port", port.ToString());
        if (PlatformHelper.IsUnix && !string.IsNullOrWhiteSpace(socketUnix))
            text = UpsertIniKey(text, "client", "socket", socketUnix);

        File.WriteAllText(configPath, text.TrimStart('\uFEFF', '\r', '\n') + Environment.NewLine, PlatformHelper.Utf8NoBom);
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

    public static string ResolveEffectiveRuntimeDirectory(string runtimeDir)
    {
        if (string.IsNullOrWhiteSpace(runtimeDir))
            return runtimeDir;

        var marker = Path.Combine(runtimeDir, ExternalPrefixMarkerFile);
        if (File.Exists(marker))
        {
            try
            {
                var target = File.ReadAllText(marker, Encoding.UTF8).Trim().TrimStart('\uFEFF');
                if (!string.IsNullOrWhiteSpace(target) && Directory.Exists(target))
                    return target;
            }
            catch
            {
                // Fall back to runtimeDir
            }
        }

        return runtimeDir;
    }

    public static string? FindPhpExecutable(string phpDir)
    {
        var effectiveDir = ResolveEffectiveRuntimeDirectory(phpDir);
        if (string.IsNullOrWhiteSpace(effectiveDir) || !Directory.Exists(effectiveDir))
            return null;

        if (PlatformHelper.IsWindows)
        {
            var winExe = Path.Combine(effectiveDir, "php.exe");
            return File.Exists(winExe) ? winExe : null;
        }

        string[] candidates =
        [
            Path.Combine(effectiveDir, "php"),
            Path.Combine(effectiveDir, "bin", "php")
        ];

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    public static string? GetPhpBinDirectory(string phpDir)
    {
        var exe = FindPhpExecutable(phpDir);
        return exe is not null ? Path.GetDirectoryName(exe) : null;
    }

    public static string? GetMariaDbBinDirectory(string mariaDbDir)
    {
        var effectiveDir = ResolveEffectiveRuntimeDirectory(mariaDbDir);
        var binDir = Path.Combine(effectiveDir, "bin");
        if (Directory.Exists(binDir))
            return binDir;

        var server = FindMariaDbServerExecutable(effectiveDir);
        return server is not null ? Path.GetDirectoryName(server) : null;
    }

    public static string? FindMariaDbServerExecutable(string mariaDbDir) =>
        PlatformHelper.IsWindows
            ? FindMariaDbTool(mariaDbDir, "mariadbd.exe", "mysqld.exe")
            : FindMariaDbTool(mariaDbDir, "mariadbd", "mysqld");

    public static string? FindMariaDbAdminExecutable(string mariaDbDir) =>
        PlatformHelper.IsWindows
            ? FindMariaDbTool(mariaDbDir, "mariadb-admin.exe", "mysqladmin.exe")
            : FindMariaDbTool(mariaDbDir, "mariadb-admin", "mysqladmin");

    public static string? FindMariaDbCliExecutable(string mariaDbDir) =>
        PlatformHelper.IsWindows
            ? FindMariaDbTool(mariaDbDir, "mariadb.exe", "mysql.exe")
            : FindMariaDbTool(mariaDbDir, "mariadb", "mysql");

    public static string? FindMariaDbInstallDbExecutable(string mariaDbDir) =>
        PlatformHelper.IsWindows
            ? FindMariaDbTool(mariaDbDir, "mariadb-install-db.exe", "mysql_install_db.exe")
            : FindMariaDbTool(mariaDbDir, "mariadb-install-db", "mysql_install_db");

    public static string? FindMariaDbTool(string mariaDbDir, params string[] names)
    {
        var effectiveDir = ResolveEffectiveRuntimeDirectory(mariaDbDir);
        if (string.IsNullOrWhiteSpace(effectiveDir) || !Directory.Exists(effectiveDir))
            return null;

        string[] subDirs = PlatformHelper.IsWindows
            ? ["bin"]
            : ["bin", "sbin", "scripts"];

        foreach (var sub in subDirs)
        {
            var dir = Path.Combine(effectiveDir, sub);
            if (!Directory.Exists(dir))
                continue;

            foreach (var name in names)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static async Task ExtractArchiveAsync(string archivePath, string destinationDir, CancellationToken ct)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Run(() => ZipFile.ExtractToDirectory(archivePath, destinationDir, overwriteFiles: true), ct);
            return;
        }

        if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
            archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            if (PlatformHelper.IsUnix && await TryExtractWithSystemTarAsync(archivePath, destinationDir, ct))
                return;

            await Task.Run(() =>
            {
                using var fs = File.OpenRead(archivePath);
                using var gz = new GZipStream(fs, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gz, destinationDir, overwriteFiles: true);
            }, ct);
            return;
        }

        throw new NotSupportedException($"Formato de arquivo não suportado: {Path.GetFileName(archivePath)}");
    }

    private static async Task<bool> TryExtractWithSystemTarAsync(string archivePath, string destinationDir, CancellationToken ct)
    {
        try
        {
            var tarExe = File.Exists("/usr/bin/tar") ? "/usr/bin/tar" : "tar";
            var psi = new ProcessStartInfo
            {
                FileName = tarExe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-xzf");
            psi.ArgumentList.Add(archivePath);
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(destinationDir);

            using var proc = Process.Start(psi);
            if (proc is null)
                return false;

            await proc.WaitForExitAsync(ct);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureRuntimePermissions(string targetDir, ServiceKind kind)
    {
        if (!PlatformHelper.IsUnix)
            return;

        if (kind == ServiceKind.Php)
        {
            var phpExe = FindPhpExecutable(targetDir);
            if (phpExe is not null)
                PlatformHelper.EnsureUnixExecutable(phpExe);
            return;
        }

        foreach (var sub in new[] { "bin", "sbin", "scripts" })
        {
            var dir = Path.Combine(targetDir, sub);
            if (!Directory.Exists(dir))
                continue;

            foreach (var file in Directory.GetFiles(dir))
                PlatformHelper.EnsureUnixExecutable(file);
        }
    }

    private static async Task RemoveMacOsQuarantineAsync(string targetDir, CancellationToken ct)
    {
        if (!PlatformHelper.IsMacOS)
            return;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/xattr",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-dr");
            psi.ArgumentList.Add("com.apple.quarantine");
            psi.ArgumentList.Add(targetDir);

            using var proc = Process.Start(psi);
            if (proc is not null)
                await proc.WaitForExitAsync(ct);
        }
        catch
        {
            // Best effort only.
        }
    }

    private static async Task<string?> GetBrewFormulaPrefixAsync(string brewExe, string formula, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = brewExe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--prefix");
            psi.ArgumentList.Add(formula);

            using var proc = Process.Start(psi);
            if (proc is null)
                return null;

            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            var path = output.Trim();
            return proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(path) && Directory.Exists(path)
                ? path
                : null;
        }
        catch
        {
            return null;
        }
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
        {
            var dest = Path.Combine(targetDir, Path.GetFileName(file));
            var info = new FileInfo(file);
            if (PlatformHelper.IsUnix && !string.IsNullOrEmpty(info.LinkTarget))
            {
                File.CreateSymbolicLink(dest, info.LinkTarget);
            }
            else
            {
                File.Copy(file, dest, overwrite: true);
                if (!OperatingSystem.IsWindows())
                {
                    try { File.SetUnixFileMode(dest, File.GetUnixFileMode(file)); } catch { }
                }
            }
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var dest = Path.Combine(targetDir, Path.GetFileName(dir));
            var info = new DirectoryInfo(dir);
            if (PlatformHelper.IsUnix && !string.IsNullOrEmpty(info.LinkTarget))
            {
                Directory.CreateSymbolicLink(dest, info.LinkTarget);
            }
            else
            {
                CopyDirectoryRecursive(dir, dest);
            }
        }
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
