using System.Buffers;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BEStart.Models;

namespace BEStart.Services;

public sealed record InstallProgress(double Percentage, string Stage);

public sealed partial class InstallerService
{
    private const string ExternalPrefixMarkerFile = ".bestart-external-prefix";

    private readonly HttpClient _http;
    private readonly LogService _log;

    public InstallerService(HttpClient http, LogService log)
    {
        _http = http;
        _log = log;
    }

    public IReadOnlyList<string> GetInstalledVersions(AppSettings settings, ServiceKind kind)
    {
        var root = settings.GetRuntimeRoot(kind);
        if (!Directory.Exists(root))
            return [];

        var installed = new List<string>();
        foreach (var dir in Directory.GetDirectories(root))
        {
            if (FindServiceExecutable(kind, dir) is not null)
                installed.Add(Path.GetFileName(dir));
        }

        installed.Sort((a, b) => string.Compare(b, a, StringComparison.OrdinalIgnoreCase));
        return installed;
    }

    public bool IsVersionInstalled(AppSettings settings, ServiceKind kind, string version) =>
        !string.IsNullOrWhiteSpace(version) &&
        FindServiceExecutable(kind, settings.GetInstallDirectory(kind, version)) is not null;

    public async Task<string> InstallReleaseAsync(
        PackageRelease release,
        AppSettings settings,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (release.InstallMethod == PackageInstallMethod.HomebrewFormula)
            return await InstallViaHomebrewAsync(release, settings, progress, ct);

        if (release.InstallMethod == PackageInstallMethod.PharDownload)
            return await InstallComposerPharAsync(release, settings, progress, ct);

        if (release.InstallMethod is PackageInstallMethod.MsiInstaller or PackageInstallMethod.ExeInstaller)
            return await InstallSqlServerPackageAsync(release, settings, progress, ct);

        Directory.CreateDirectory(settings.DownloadsDirectory);
        var archivePath = Path.Combine(settings.DownloadsDirectory, release.FileName);
        var targetDir = settings.GetInstallDirectory(release.Kind, release.Version);
        var sourceName = ServiceDescriptor.Get(release.Kind).LogSource;

        _log.Info(sourceName, $"Iniciando download de {release.DisplayName}...");
        progress?.Report(new InstallProgress(2, $"Conectando para baixar {release.DisplayName}..."));

        var needDownload = true;
        if (File.Exists(archivePath) && !string.IsNullOrWhiteSpace(release.Sha256))
        {
            progress?.Report(new InstallProgress(5, "Verificando cache SHA-256..."));
            var cachedHash = await ComputeSha256Async(archivePath, ct);
            if (string.Equals(cachedHash, release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                needDownload = false;
                _log.Info(sourceName, $"Usando arquivo em cache verificado ({release.FileName}).");
            }
        }
        else if (File.Exists(archivePath) && new FileInfo(archivePath).Length > 1024 * 100)
        {
            // Reuse existing non-empty archive if present
            needDownload = false;
            _log.Info(sourceName, $"Usando pacote em cache ({release.FileName}).");
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
                    throw new InvalidOperationException($"Checksum SHA-256 inválido para {release.FileName}.");
                }
                _log.Success(sourceName, "Integridade SHA-256 verificada.");
            }
        }

        progress?.Report(new InstallProgress(76, $"Extraindo {release.DisplayName}..."));
        _log.Info(sourceName, $"Extraindo pacote para {targetDir}...");

        var tempExtract = Path.Combine(settings.DownloadsDirectory, $"extract_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempExtract);

        try
        {
            await ExtractArchiveAsync(archivePath, tempExtract, ct);

            var topDirs = Directory.GetDirectories(tempExtract);
            var topFiles = Directory.GetFiles(tempExtract);
            var contentRoot = (topDirs.Length == 1 && topFiles.Length == 0) ? topDirs[0] : tempExtract;

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

        await RunPostInstallSetupAsync(release.Kind, targetDir, settings, progress, ct);
        progress?.Report(new InstallProgress(100, $"{release.DisplayName} pronto!"));
        return targetDir;
    }

    private async Task<string> InstallComposerPharAsync(
        PackageRelease release, AppSettings settings, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var targetDir = settings.GetInstallDirectory(ServiceKind.Composer, release.Version);
        Directory.CreateDirectory(targetDir);
        var pharPath = Path.Combine(targetDir, "composer.phar");

        progress?.Report(new InstallProgress(15, $"Baixando {release.DisplayName}..."));
        _log.Info("Composer", $"Baixando {release.DisplayName}...");

        await DownloadFileAsync(release.DownloadUrl, pharPath, release.DisplayName, progress, ct);

        progress?.Report(new InstallProgress(88, "Gerando wrapper CLI do Composer..."));
        var phpVer = settings.GetActiveVersion(ServiceKind.Php);
        var phpExe = !string.IsNullOrWhiteSpace(phpVer)
            ? FindServiceExecutable(ServiceKind.Php, settings.GetInstallDirectory(ServiceKind.Php, phpVer))
            : null;

        if (PlatformHelper.IsWindows)
        {
            var safePhpExe = phpExe is not null
                ? PlatformHelper.EnsureWindowsSafePath(phpExe, settings.BaseDirectory)
                : null;
            var phpCmd = safePhpExe is not null ? $"\"{safePhpExe}\"" : "php";
            var batContent = $"@echo off\r\n{phpCmd} \"%~dp0composer.phar\" %*\r\n";
            File.WriteAllText(Path.Combine(targetDir, "composer.bat"), batContent, PlatformHelper.Utf8NoBom);
        }
        else
        {
            var phpCmd = phpExe is not null ? $"\"{phpExe}\"" : "php";
            var shPath = Path.Combine(targetDir, "composer");
            var shContent = $"#!/usr/bin/env sh\nexec {phpCmd} \"$(dirname \"$0\")/composer.phar\" \"$@\"\n";
            File.WriteAllText(shPath, shContent, PlatformHelper.Utf8NoBom);
            PlatformHelper.EnsureUnixExecutable(shPath);
        }

        _log.Success("Composer", $"{release.DisplayName} instalado em {targetDir}.");
        progress?.Report(new InstallProgress(100, $"{release.DisplayName} pronto!"));
        return targetDir;
    }

    private async Task<string> InstallSqlServerPackageAsync(
        PackageRelease release, AppSettings settings, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        if (!PlatformHelper.IsWindows)
            throw new PlatformNotSupportedException("Instalador Microsoft SQL Server suportado apenas no Windows.");

        Directory.CreateDirectory(settings.DownloadsDirectory);
        var installerPath = Path.Combine(settings.DownloadsDirectory, release.FileName);
        var targetDir = settings.GetInstallDirectory(ServiceKind.SqlServer, release.Version);

        if (!File.Exists(installerPath) || new FileInfo(installerPath).Length < 1024 * 1024)
        {
            progress?.Report(new InstallProgress(5, $"Baixando {release.DisplayName}..."));
            await DownloadFileAsync(release.DownloadUrl, installerPath, release.DisplayName, progress, ct);
        }

        progress?.Report(new InstallProgress(78, $"Executando instalador oficial {release.DisplayName}..."));
        _log.Info("SQLServer", $"Instalando {release.DisplayName} (pode solicitar permissão de administrador)...");

        if (release.InstallMethod == PackageInstallMethod.MsiInstaller)
        {
            var args = $"/i \"{installerPath}\" /qn /norestart IACCEPTSQLLOCALDBLICENSETERMS=YES";
            var exit = await RunWindowsElevatedOrNormalAsync("msiexec.exe", args, ct);
            if (exit != 0)
                throw new InvalidOperationException($"Instalador LocalDB retornou código {exit}.");

            var localDbExe = FindInstalledSqlLocalDbExe()
                ?? throw new FileNotFoundException("SqlLocalDB.exe não encontrado após instalação.");

            // Create local BEStart instance
            await RunSimpleProcessAsync(localDbExe, "create BEStart", ct);

            Directory.CreateDirectory(targetDir);
            File.WriteAllText(
                Path.Combine(targetDir, ExternalPrefixMarkerFile),
                Path.GetDirectoryName(localDbExe)!,
                PlatformHelper.Utf8NoBom);
        }
        else
        {
            var args = "/Q /ACTION=Install /FEATURES=SQLENGINE /INSTANCENAME=BESTART /IACCEPTSQLSERVERLICENSETERMS=True " +
                       "/SQLSVCACCOUNT=\"NT AUTHORITY\\Network Service\" /ADDCURRENTUSERASSQLADMIN=True /TCPENABLED=1";
            var exit = await RunWindowsElevatedOrNormalAsync(installerPath, args, ct);
            if (exit != 0)
                throw new InvalidOperationException($"Instalador SQL Server Express retornou código {exit}.");

            var binnDir = FindInstalledSqlServerExpressBinnDir() ?? @"C:\Program Files\Microsoft SQL Server";
            Directory.CreateDirectory(targetDir);
            File.WriteAllText(Path.Combine(targetDir, ExternalPrefixMarkerFile), binnDir, PlatformHelper.Utf8NoBom);
        }

        _log.Success("SQLServer", $"{release.DisplayName} instalado e pronto!");
        progress?.Report(new InstallProgress(100, $"{release.DisplayName} pronto!"));
        return targetDir;
    }

    private async Task<string> InstallViaHomebrewAsync(
        PackageRelease release, AppSettings settings, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var formula = release.FormulaName
            ?? throw new InvalidOperationException("Fórmula Homebrew não informada.");
        var brewExe = PlatformHelper.FindHomebrewExecutable()
            ?? throw new InvalidOperationException("Homebrew não encontrado. Instale em https://brew.sh e tente novamente.");

        var sourceName = ServiceDescriptor.Get(release.Kind).LogSource;
        var targetDir = settings.GetInstallDirectory(release.Kind, release.Version);

        progress?.Report(new InstallProgress(15, $"Verificando {formula} no Homebrew..."));
        var existingPrefix = await GetBrewFormulaPrefixAsync(brewExe, formula, ct);

        if (string.IsNullOrWhiteSpace(existingPrefix) || FindServiceExecutable(release.Kind, existingPrefix) is null)
        {
            progress?.Report(new InstallProgress(30, $"Executando brew install {formula}..."));
            _log.Info(sourceName, $"Instalando {formula} via Homebrew...");

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
            psi.ArgumentList.Add("install");
            psi.ArgumentList.Add(formula);
            psi.Environment["HOMEBREW_NO_AUTO_UPDATE"] = "1";

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Falha ao iniciar Homebrew.");
            proc.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _log.Info(sourceName, e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _log.Info(sourceName, e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"brew install {formula} falhou (código {proc.ExitCode}).");

            existingPrefix = await GetBrewFormulaPrefixAsync(brewExe, formula, ct);
        }

        if (string.IsNullOrWhiteSpace(existingPrefix) || !Directory.Exists(existingPrefix))
            throw new InvalidOperationException($"Não foi possível localizar {formula} após instalação.");

        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Combine(targetDir, ExternalPrefixMarkerFile), existingPrefix.Trim(), PlatformHelper.Utf8NoBom);

        await RunPostInstallSetupAsync(release.Kind, targetDir, settings, progress, ct);
        progress?.Report(new InstallProgress(100, $"{release.DisplayName} pronto!"));
        return targetDir;
    }

    private async Task RunPostInstallSetupAsync(
        ServiceKind kind, string targetDir, AppSettings settings, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        switch (kind)
        {
            case ServiceKind.Php:
                progress?.Report(new InstallProgress(92, "Configurando php.ini e extensões..."));
                ConfigurePhpIni(targetDir, settings.BaseDirectory);
                break;

            case ServiceKind.MariaDb:
                progress?.Report(new InstallProgress(90, "Inicializando MariaDB (modo baixo consumo)..."));
                await EnsureMariaDbInitializedAsync(targetDir, settings, ct);
                break;

            case ServiceKind.MySql:
                progress?.Report(new InstallProgress(90, "Inicializando MySQL (modo baixo consumo)..."));
                await EnsureMySqlInitializedAsync(targetDir, settings, ct);
                break;

            case ServiceKind.PostgreSql:
                progress?.Report(new InstallProgress(90, "Inicializando PostgreSQL (initdb)..."));
                await EnsurePostgreSqlInitializedAsync(targetDir, settings, ct);
                break;

            case ServiceKind.MongoDb:
                progress?.Report(new InstallProgress(92, "Configurando MongoDB (cache 256MB)..."));
                EnsureMongoDbInitialized(settings);
                break;

            case ServiceKind.Redis:
                progress?.Report(new InstallProgress(92, "Configurando Redis (maxmemory 128MB)..."));
                EnsureRedisInitialized(settings);
                break;

            case ServiceKind.Cassandra:
                progress?.Report(new InstallProgress(88, "Verificando JRE e configurando Cassandra (JVM leve)..."));
                await EnsurePortableJavaIfMissingAsync(settings, progress, ct);
                EnsureCassandraInitialized(targetDir, settings);
                break;
        }
    }

    // ==================== SERVICE CONFIGURATIONS (LOW RAM) ====================

    public void ConfigurePhpIni(string phpDir, string? anchorDir = null)
    {
        var iniPath = Path.Combine(phpDir, "php.ini");
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
            return;
        }

        var devIni = Path.Combine(phpDir, "php.ini-development");
        var prodIni = Path.Combine(phpDir, "php.ini-production");
        if (!File.Exists(iniPath))
        {
            if (File.Exists(devIni)) File.Copy(devIni, iniPath);
            else if (File.Exists(prodIni)) File.Copy(prodIni, iniPath);
            else File.WriteAllText(iniPath, "[PHP]\r\n", PlatformHelper.Utf8NoBom);
        }

        var content = File.ReadAllText(iniPath, Encoding.UTF8).TrimStart('\uFEFF');
        var extDir = Path.Combine(phpDir, "ext");
        var safeExtDir = PlatformHelper.EnsureWindowsSafePath(extDir, anchorDir, isDirectory: true).Replace('\\', '/');
        var extDirLine = $"extension_dir = \"{safeExtDir}\"";

        var activeExtDirRegex = new Regex(@"^\s*extension_dir\s*=.*$", RegexOptions.Multiline);
        var commentedExtDirRegex = new Regex(@"^\s*;\s*extension_dir\s*=\s*""ext""\s*$", RegexOptions.Multiline);
        if (activeExtDirRegex.IsMatch(content))
            content = activeExtDirRegex.Replace(content, extDirLine, 1);
        else if (commentedExtDirRegex.IsMatch(content))
            content = commentedExtDirRegex.Replace(content, extDirLine, 1);
        else
            content += Environment.NewLine + extDirLine + Environment.NewLine;

        string[] desiredExtensions = ["curl", "fileinfo", "gd", "intl", "mbstring", "mysqli", "openssl", "pdo_mysql", "pdo_pgsql", "pgsql", "zip"];

        foreach (var ext in desiredExtensions)
        {
            if (!File.Exists(Path.Combine(extDir, $"php_{ext}.dll")))
                continue;

            var activeRegex = new Regex($@"^\s*extension\s*=\s*(?:php_)?{Regex.Escape(ext)}(?:\.dll)?\s*$", RegexOptions.Multiline);
            if (activeRegex.IsMatch(content))
                continue;

            var commentRegex = new Regex($@"^\s*;\s*extension\s*=\s*(?:php_)?{Regex.Escape(ext)}(?:\.dll)?\s*$", RegexOptions.Multiline);
            content = commentRegex.IsMatch(content)
                ? commentRegex.Replace(content, $"extension={ext}", 1)
                : content + $"{Environment.NewLine}extension={ext}";
        }

        var tzRegex = new Regex(@"^\s*;?\s*date\.timezone\s*=.*$", RegexOptions.Multiline);
        content = tzRegex.IsMatch(content)
            ? tzRegex.Replace(content, "date.timezone = America/Sao_Paulo", 1)
            : content + $"{Environment.NewLine}date.timezone = America/Sao_Paulo";

        File.WriteAllText(iniPath, content, PlatformHelper.Utf8NoBom);
    }

    public async Task EnsureMariaDbInitializedAsync(string mariaDbDir, AppSettings settings, CancellationToken ct = default)
    {
        var effectiveDir = PlatformHelper.EnsureWindowsSafePath(
            ResolveEffectiveRuntimeDirectory(mariaDbDir), settings.BaseDirectory, isDirectory: true);
        var dataDir = PlatformHelper.EnsureWindowsSafePath(
            settings.GetDataDirectory(ServiceKind.MariaDb), settings.BaseDirectory, isDirectory: true);
        var configPath = PlatformHelper.ResolveMySqlStyleConfigPath(dataDir);
        var port = settings.GetPort(ServiceKind.MariaDb);

        if (!Directory.Exists(Path.Combine(dataDir, "mysql")))
        {
            Directory.CreateDirectory(dataDir);
            var installDbExe = FindServiceTool(ServiceKind.MariaDb, effectiveDir, "mariadb-install-db", "mysql_install_db")
                ?? throw new FileNotFoundException("mariadb-install-db não encontrado.");

            _log.Info("MariaDB", $"Inicializando banco em {dataDir}...");
            var psi = new ProcessStartInfo
            {
                FileName = installDbExe,
                WorkingDirectory = PlatformHelper.IsWindows ? Path.GetDirectoryName(installDbExe)! : effectiveDir,
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
                psi.ArgumentList.Add($"--port={port}");
            }
            else
            {
                psi.ArgumentList.Add($"--basedir={effectiveDir}");
                psi.ArgumentList.Add($"--datadir={dataDir}");
                psi.ArgumentList.Add("--auth-root-authentication-method=normal");
                psi.ArgumentList.Add("--skip-test-db");
            }

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Falha ao executar mariadb-install-db.");
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            var stderr = await stderrTask;
            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"mariadb-install-db falhou ({proc.ExitCode}): {stderr.Trim()}");
        }

        SyncMySqlStyleConfig(configPath, effectiveDir, dataDir, port, settings.GetMariaDbSocketPath(), isMySql8: false);
    }

    public async Task EnsureMySqlInitializedAsync(string mysqlDir, AppSettings settings, CancellationToken ct = default)
    {
        var effectiveDir = PlatformHelper.EnsureWindowsSafePath(
            ResolveEffectiveRuntimeDirectory(mysqlDir), settings.BaseDirectory, isDirectory: true);
        var dataDir = PlatformHelper.EnsureWindowsSafePath(
            settings.GetDataDirectory(ServiceKind.MySql), settings.BaseDirectory, isDirectory: true);
        var configPath = PlatformHelper.ResolveMySqlStyleConfigPath(dataDir);
        var port = settings.GetPort(ServiceKind.MySql);

        if (!Directory.Exists(Path.Combine(dataDir, "mysql")))
        {
            // mysqld --initialize-insecure aborts if dataDir contains any leftover files (e.g. a premature my.ini)
            PrepareEmptyDataDirectory(dataDir);

            var mysqldExe = FindServiceExecutable(ServiceKind.MySql, effectiveDir)
                ?? throw new FileNotFoundException("mysqld não encontrado.");

            _log.Info("MySQL", $"Inicializando MySQL (--initialize-insecure) em {dataDir}...");
            var psi = new ProcessStartInfo
            {
                FileName = mysqldExe,
                WorkingDirectory = Path.GetDirectoryName(mysqldExe)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--no-defaults");
            psi.ArgumentList.Add("--initialize-insecure");
            psi.ArgumentList.Add($"--basedir={effectiveDir.Replace('\\', '/')}");
            psi.ArgumentList.Add($"--datadir={dataDir.Replace('\\', '/')}");

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Falha ao inicializar MySQL.");
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (proc.ExitCode != 0)
            {
                var detail = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : stdout.Trim();
                throw new InvalidOperationException($"mysqld --initialize-insecure falhou ({proc.ExitCode}): {detail}");
            }
        }

        SyncMySqlStyleConfig(configPath, effectiveDir, dataDir, port, settings.GetMySqlSocketPath(), isMySql8: true);
    }

    public async Task EnsurePostgreSqlInitializedAsync(string pgDir, AppSettings settings, CancellationToken ct = default)
    {
        var effectiveDir = PlatformHelper.EnsureWindowsSafePath(
            ResolveEffectiveRuntimeDirectory(pgDir), settings.BaseDirectory, isDirectory: true);
        var dataDir = PlatformHelper.EnsureWindowsSafePath(
            settings.GetDataDirectory(ServiceKind.PostgreSql), settings.BaseDirectory, isDirectory: true);
        var port = settings.GetPort(ServiceKind.PostgreSql);

        if (!File.Exists(Path.Combine(dataDir, "PG_VERSION")))
        {
            // initdb fails if dataDir exists and has residual files from a previous failed attempt
            PrepareEmptyDataDirectory(dataDir);

            var initDbExe = FindServiceTool(ServiceKind.PostgreSql, effectiveDir, "initdb")
                ?? throw new FileNotFoundException("initdb não encontrado no PostgreSQL.");

            _log.Info("PostgreSQL", $"Inicializando cluster PostgreSQL em {dataDir}...");
            var psi = new ProcessStartInfo
            {
                FileName = initDbExe,
                WorkingDirectory = Path.GetDirectoryName(initDbExe)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.Environment["LC_MESSAGES"] = "C";
            psi.ArgumentList.Add("-D"); psi.ArgumentList.Add(dataDir);
            psi.ArgumentList.Add("-U"); psi.ArgumentList.Add("postgres");
            psi.ArgumentList.Add("-A"); psi.ArgumentList.Add("trust");
            psi.ArgumentList.Add("-E"); psi.ArgumentList.Add("UTF8");
            psi.ArgumentList.Add("--locale=C");

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Falha ao executar initdb.");
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (proc.ExitCode != 0)
            {
                var detail = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : stdout.Trim();
                throw new InvalidOperationException($"initdb falhou ({proc.ExitCode}): {detail}");
            }
        }

        // Low-RAM PostgreSQL tuning
        var confPath = Path.Combine(dataDir, "postgresql.conf");
        if (File.Exists(confPath))
        {
            var text = File.ReadAllText(confPath, Encoding.UTF8).TrimStart('\uFEFF');
            text = UpsertConfLine(text, "listen_addresses", "'127.0.0.1'");
            text = UpsertConfLine(text, "port", port.ToString());
            text = UpsertConfLine(text, "shared_buffers", "32MB");
            text = UpsertConfLine(text, "work_mem", "2MB");
            text = UpsertConfLine(text, "max_connections", "20");
            File.WriteAllText(confPath, text, PlatformHelper.Utf8NoBom);
        }
    }

    public static void EnsureMongoDbInitialized(AppSettings settings)
    {
        var dataDir = PlatformHelper.EnsureWindowsSafePath(
            settings.GetDataDirectory(ServiceKind.MongoDb), settings.BaseDirectory, isDirectory: true);
        Directory.CreateDirectory(dataDir);
        var cfgPath = Path.Combine(dataDir, "mongod.cfg");
        var port = settings.GetPort(ServiceKind.MongoDb);
        var yaml = $"""
storage:
  dbPath: "{dataDir.Replace('\\', '/')}"
  wiredTiger:
    engineConfig:
      cacheSizeGB: 0.25
net:
  bindIp: 127.0.0.1
  port: {port}
""";
        File.WriteAllText(cfgPath, yaml + Environment.NewLine, PlatformHelper.Utf8NoBom);
    }

    public static void EnsureRedisInitialized(AppSettings settings)
    {
        var dataDir = PlatformHelper.EnsureWindowsSafePath(
            settings.GetDataDirectory(ServiceKind.Redis), settings.BaseDirectory, isDirectory: true);
        Directory.CreateDirectory(dataDir);
        var confPath = Path.Combine(dataDir, "redis.conf");
        var port = settings.GetPort(ServiceKind.Redis);
        // Using "dir ." with WorkingDirectory = dataDir avoids MSYS2/Cygwin vs Windows drive-letter path issues
        var conf = $"""
bind 127.0.0.1
port {port}
dir .
maxmemory 128mb
maxmemory-policy allkeys-lru
save ""
""";
        File.WriteAllText(confPath, conf + Environment.NewLine, PlatformHelper.Utf8NoBom);
    }

    public void EnsureCassandraInitialized(string cassandraDir, AppSettings settings)
    {
        var effectiveDir = PlatformHelper.EnsureWindowsSafePath(
            ResolveEffectiveRuntimeDirectory(cassandraDir), settings.BaseDirectory, isDirectory: true);
        var dataDir = PlatformHelper.EnsureWindowsSafePath(
            settings.GetDataDirectory(ServiceKind.Cassandra), settings.BaseDirectory, isDirectory: true);
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(Path.Combine(dataDir, "data"));
        Directory.CreateDirectory(Path.Combine(dataDir, "commitlog"));
        Directory.CreateDirectory(Path.Combine(dataDir, "saved_caches"));
        Directory.CreateDirectory(Path.Combine(dataDir, "hints"));
        Directory.CreateDirectory(Path.Combine(dataDir, "logs"));

        var confDir = Path.Combine(effectiveDir, "conf");
        var yamlPath = Path.Combine(confDir, "cassandra.yaml");
        var port = settings.GetPort(ServiceKind.Cassandra);

        if (File.Exists(yamlPath))
        {
            var yaml = File.ReadAllText(yamlPath, Encoding.UTF8).TrimStart('\uFEFF');
            var dataSubDir = Path.Combine(dataDir, "data").Replace('\\', '/');
            yaml = ReplaceYamlScalar(yaml, "listen_address", "127.0.0.1");
            yaml = ReplaceYamlScalar(yaml, "rpc_address", "127.0.0.1");
            yaml = ReplaceYamlScalar(yaml, "native_transport_port", port.ToString());
            yaml = ReplaceYamlScalar(yaml, "commitlog_directory", $"\"{Path.Combine(dataDir, "commitlog").Replace('\\', '/')}\"");
            yaml = ReplaceYamlScalar(yaml, "saved_caches_directory", $"\"{Path.Combine(dataDir, "saved_caches").Replace('\\', '/')}\"");
            yaml = ReplaceYamlScalar(yaml, "hints_directory", $"\"{Path.Combine(dataDir, "hints").Replace('\\', '/')}\"");
            yaml = ReplaceYamlListSingleItem(yaml, "data_file_directories", $"\"{dataSubDir}\"");
            File.WriteAllText(yamlPath, yaml, PlatformHelper.Utf8NoBom);
        }

        // Cap JVM heap to 256-512 MB so Cassandra uses minimal PC memory
        foreach (var optName in new[] { "jvm-server.options", "jvm11-server.options", "jvm17-server.options", "jvm.options" })
        {
            var optPath = Path.Combine(confDir, optName);
            if (!File.Exists(optPath))
                continue;

            var text = File.ReadAllText(optPath, Encoding.UTF8);
            text = Regex.Replace(text, @"^#?\s*-Xms.*$", "-Xms256M", RegexOptions.Multiline);
            text = Regex.Replace(text, @"^#?\s*-Xmx.*$", "-Xmx512M", RegexOptions.Multiline);
            if (!text.Contains("-Xms256M", StringComparison.Ordinal))
                text += "\n-Xms256M\n-Xmx512M\n";
            File.WriteAllText(optPath, text, PlatformHelper.Utf8NoBom);
        }
    }

    private static void PrepareEmptyDataDirectory(string dataDir)
    {
        if (Directory.Exists(dataDir))
        {
            foreach (var entry in Directory.GetFileSystemEntries(dataDir))
            {
                try
                {
                    if (Directory.Exists(entry))
                        Directory.Delete(entry, recursive: true);
                    else
                        File.Delete(entry);
                }
                catch { }
            }
        }
        else
        {
            Directory.CreateDirectory(dataDir);
        }
    }

    public async Task<string> EnsurePortableJavaIfMissingAsync(
        AppSettings settings, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var existing = PlatformHelper.FindJavaExecutable(settings.JreDirectory);
        if (existing is not null)
            return existing;

        var os = PlatformHelper.IsWindows ? "windows" : PlatformHelper.IsMacOS ? "mac" : "linux";
        var arch = PlatformHelper.IsArm64 ? "aarch64" : "x64";
        var ext = PlatformHelper.IsWindows ? "zip" : "tar.gz";
        var url = $"https://api.adoptium.net/v3/binary/latest/17/ga/{os}/{arch}/jre/hotspot/normal/eclipse";
        var archivePath = Path.Combine(settings.DownloadsDirectory, $"temurin-jre-17-{os}-{arch}.{ext}");

        _log.Info("Cassandra", "Java (JRE 17) não detectado. Baixando JRE 17 portátil (Eclipse Temurin)...");
        progress?.Report(new InstallProgress(82, "Baixando JRE 17 portátil para o Cassandra..."));

        Directory.CreateDirectory(settings.DownloadsDirectory);
        if (!File.Exists(archivePath))
            await DownloadFileAsync(url, archivePath, "JRE 17", progress, ct);

        Directory.CreateDirectory(settings.JreDirectory);
        await ExtractArchiveAsync(archivePath, settings.JreDirectory, ct);

        return PlatformHelper.FindJavaExecutable(settings.JreDirectory)
            ?? throw new FileNotFoundException("Falha ao localizar java após extração do JRE 17.");
    }

    public static void SyncMySqlStyleConfig(
        string configPath, string runtimeDir, string dataDir, int port, string? socketPath, bool isMySql8)
    {
        var effectiveDir = ResolveEffectiveRuntimeDirectory(runtimeDir);
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
            // Low-RAM local tuning
            sb.AppendLine("innodb_buffer_pool_size=32M");
            sb.AppendLine("key_buffer_size=8M");
            sb.AppendLine("max_connections=30");
            sb.AppendLine("performance_schema=OFF");
            if (!isMySql8)
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

        var text = File.ReadAllText(configPath, Encoding.UTF8).TrimStart('\uFEFF', '\r', '\n');
        text = UpsertIniKey(text, "mysqld", "port", port.ToString());
        text = UpsertIniKey(text, "mysqld", "basedir", baseDirUnix);
        text = UpsertIniKey(text, "mysqld", "datadir", dataDirUnix);
        text = UpsertIniKey(text, "mysqld", "bind-address", "127.0.0.1");
        text = UpsertIniKey(text, "mysqld", "innodb_buffer_pool_size", "32M");
        text = UpsertIniKey(text, "mysqld", "performance_schema", "OFF");
        text = UpsertIniKey(text, "client", "port", port.ToString());
        File.WriteAllText(configPath, text.TrimStart('\uFEFF', '\r', '\n') + Environment.NewLine, PlatformHelper.Utf8NoBom);
    }

    public void UninstallVersion(AppSettings settings, ServiceKind kind, string version)
    {
        var dir = settings.GetInstallDirectory(kind, version);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
            _log.Warn(ServiceDescriptor.Get(kind).LogSource, $"Versão {version} removida.");
        }
    }

    // ==================== BINARY RESOLUTION ====================

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
            catch { }
        }
        return runtimeDir;
    }

    public static string? FindServiceExecutable(ServiceKind kind, string dir) => kind switch
    {
        ServiceKind.Php => FindServiceTool(kind, dir, "php"),
        ServiceKind.Composer => File.Exists(Path.Combine(dir, "composer.phar")) ? Path.Combine(dir, "composer.phar") : null,
        ServiceKind.PostgreSql => FindServiceTool(kind, dir, "postgres"),
        ServiceKind.MariaDb => FindServiceTool(kind, dir, "mariadbd", "mysqld"),
        ServiceKind.MySql => FindServiceTool(kind, dir, "mysqld"),
        ServiceKind.SqlServer => FindServiceTool(kind, dir, "SqlLocalDB", "sqlservr"),
        ServiceKind.MongoDb => FindServiceTool(kind, dir, "mongod"),
        ServiceKind.Redis => FindServiceTool(kind, dir, "redis-server"),
        ServiceKind.Cassandra => Directory.Exists(Path.Combine(ResolveEffectiveRuntimeDirectory(dir), "lib")) &&
                                 Directory.Exists(Path.Combine(ResolveEffectiveRuntimeDirectory(dir), "conf"))
            ? Path.Combine(ResolveEffectiveRuntimeDirectory(dir), "conf", "cassandra.yaml")
            : null,
        _ => null
    };

    public static string? FindServiceTool(ServiceKind kind, string dir, params string[] baseNames)
    {
        var effectiveDir = ResolveEffectiveRuntimeDirectory(dir);
        if (string.IsNullOrWhiteSpace(effectiveDir) || !Directory.Exists(effectiveDir))
            return null;

        var ext = PlatformHelper.ExecutableExtension;
        string[] subDirs = ["", "bin", "sbin", "scripts", Path.Combine("pgsql", "bin")];

        foreach (var sub in subDirs)
        {
            var probeDir = string.IsNullOrEmpty(sub) ? effectiveDir : Path.Combine(effectiveDir, sub);
            if (!Directory.Exists(probeDir))
                continue;

            foreach (var name in baseNames)
            {
                var withExt = string.IsNullOrEmpty(ext) || name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)
                    ? name
                    : name + ext;
                var candidate = Path.Combine(probeDir, withExt);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    public static string? GetServiceBinDirectory(ServiceKind kind, string dir)
    {
        if (kind == ServiceKind.Composer)
            return Directory.Exists(dir) ? dir : null;

        var exe = FindServiceExecutable(kind, dir);
        return exe is not null ? Path.GetDirectoryName(exe) : null;
    }

    private static string? FindInstalledSqlLocalDbExe()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var ver in new[] { "160", "150", "140", "130" })
        {
            var candidate = Path.Combine(pf, "Microsoft SQL Server", ver, "Tools", "Binn", "SqlLocalDB.exe");
            if (File.Exists(candidate))
                return candidate;
        }
        return PlatformHelper.FindInPath("SqlLocalDB.exe");
    }

    private static string? FindInstalledSqlServerExpressBinnDir()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var root = Path.Combine(pf, "Microsoft SQL Server");
        if (!Directory.Exists(root))
            return null;

        foreach (var dir in Directory.GetDirectories(root, "MSSQL*.BESTART"))
        {
            var binn = Path.Combine(dir, "MSSQL", "Binn");
            if (File.Exists(Path.Combine(binn, "sqlservr.exe")))
                return binn;
        }
        return null;
    }

    // ==================== ARCHIVES, DOWNLOADS, HELPERS ====================

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
            if (await TryExtractWithSystemTarAsync(archivePath, destinationDir, "-xzf", ct))
                return;

            await Task.Run(() =>
            {
                using var fs = File.OpenRead(archivePath);
                using var gz = new GZipStream(fs, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gz, destinationDir, overwriteFiles: true);
            }, ct);
            return;
        }

        if (archivePath.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase))
        {
            if (await TryExtractWithSystemTarAsync(archivePath, destinationDir, "-xJf", ct))
                return;
        }

        throw new NotSupportedException($"Formato de arquivo não suportado: {Path.GetFileName(archivePath)}");
    }

    private static async Task<bool> TryExtractWithSystemTarAsync(string archivePath, string destinationDir, string flags, CancellationToken ct)
    {
        try
        {
            var tarExe = PlatformHelper.IsWindows
                ? Path.Combine(Environment.SystemDirectory, "tar.exe")
                : (File.Exists("/usr/bin/tar") ? "/usr/bin/tar" : "tar");

            if (PlatformHelper.IsWindows && !File.Exists(tarExe))
                tarExe = "tar";

            var psi = new ProcessStartInfo
            {
                FileName = tarExe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(flags);
            psi.ArgumentList.Add(archivePath);
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(destinationDir);

            using var proc = Process.Start(psi);
            if (proc is null) return false;
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
        if (!PlatformHelper.IsUnix) return;
        foreach (var sub in new[] { "", "bin", "sbin", "scripts" })
        {
            var dir = string.IsNullOrEmpty(sub) ? targetDir : Path.Combine(targetDir, sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir))
                PlatformHelper.EnsureUnixExecutable(file);
        }
    }

    private static async Task RemoveMacOsQuarantineAsync(string targetDir, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = "/usr/bin/xattr", UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-dr");
            psi.ArgumentList.Add("com.apple.quarantine");
            psi.ArgumentList.Add(targetDir);
            using var proc = Process.Start(psi);
            if (proc is not null) await proc.WaitForExitAsync(ct);
        }
        catch { }
    }

    private static async Task<string?> GetBrewFormulaPrefixAsync(string brewExe, string formula, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = brewExe,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--prefix");
            psi.ArgumentList.Add(formula);
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            var path = output.Trim();
            return proc.ExitCode == 0 && Directory.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    private static async Task<int> RunWindowsElevatedOrNormalAsync(string exe, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = true,
            Verb = "runas"
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Falha ao iniciar {exe}.");
        await proc.WaitForExitAsync(ct);
        return proc.ExitCode;
    }

    private static async Task RunSimpleProcessAsync(string exe, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        if (proc is not null) await proc.WaitForExitAsync(ct);
    }

    private async Task DownloadFileAsync(
        string url, string destinationPath, string label, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        var tempFile = destinationPath + ".part";

        await using (var contentStream = await response.Content.ReadAsStreamAsync(ct))
        await using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
        {
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
                        var speedMb = sw.Elapsed.TotalSeconds > 0.2 ? mbRead / sw.Elapsed.TotalSeconds : 0;
                        if (totalBytes > 0)
                        {
                            var mbTotal = totalBytes / (1024.0 * 1024.0);
                            var pct = 5.0 + (totalRead / (double)totalBytes) * 65.0;
                            progress?.Report(new InstallProgress(pct, $"Baixando {label}: {mbRead:F1}/{mbTotal:F1} MB ({speedMb:F1} MB/s)"));
                        }
                        else
                        {
                            progress?.Report(new InstallProgress(35, $"Baixando {label}: {mbRead:F1} MB ({speedMb:F1} MB/s)"));
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        if (File.Exists(destinationPath)) File.Delete(destinationPath);
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
                File.CreateSymbolicLink(dest, info.LinkTarget);
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
                Directory.CreateSymbolicLink(dest, info.LinkTarget);
            else
                CopyDirectoryRecursive(dir, dest);
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
            if (lines[i].Trim().Equals(sectionHeader, StringComparison.OrdinalIgnoreCase))
            {
                sectionStart = i;
                for (var j = i + 1; j < lines.Count; j++)
                {
                    if (lines[j].TrimStart().StartsWith('[')) { sectionEnd = j; break; }
                }
                break;
            }
        }

        if (sectionStart == -1)
        {
            lines.Add(""); lines.Add(sectionHeader); lines.Add($"{key}={value}");
            return string.Join(Environment.NewLine, lines);
        }

        for (var i = sectionStart + 1; i < sectionEnd; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith(key + " =", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"{key}={value}";
                return string.Join(Environment.NewLine, lines);
            }
        }

        lines.Insert(sectionStart + 1, $"{key}={value}");
        return string.Join(Environment.NewLine, lines);
    }

    private static string UpsertConfLine(string text, string key, string value)
    {
        var regex = new Regex($@"^\s*#?\s*{Regex.Escape(key)}\s*=.*$", RegexOptions.Multiline);
        return regex.IsMatch(text)
            ? regex.Replace(text, $"{key} = {value}", 1)
            : text + $"{Environment.NewLine}{key} = {value}";
    }

    private static string ReplaceYamlScalar(string yaml, string key, string value)
    {
        var regex = new Regex($@"^\s*#?\s*{Regex.Escape(key)}\s*:.*$", RegexOptions.Multiline);
        return regex.IsMatch(yaml)
            ? regex.Replace(yaml, $"{key}: {value}", 1)
            : yaml + $"{Environment.NewLine}{key}: {value}";
    }

    private static string ReplaceYamlListSingleItem(string yaml, string key, string itemValue)
    {
        var replacement = $"{key}:{Environment.NewLine}    - {itemValue}";
        var regex = new Regex($@"^\s*#?\s*{Regex.Escape(key)}\s*:\s*(?:\r?\n\s*#?\s*-\s*.*)+", RegexOptions.Multiline);
        return regex.IsMatch(yaml)
            ? regex.Replace(yaml, replacement, 1)
            : yaml + $"{Environment.NewLine}{replacement}";
    }
}
