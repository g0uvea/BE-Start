using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BEStart.Models;

namespace BEStart.Services;

public sealed class ProcessManagerService : IDisposable
{
    private readonly InstallerService _installer;
    private readonly LogService _log;

    private readonly Dictionary<ServiceKind, Process?> _processes = new();
    private readonly Dictionary<ServiceKind, SemaphoreSlim> _locks = new();
    private readonly HashSet<ServiceKind> _externalRunning = [];

    public event Action<ServiceKind, ServiceState, int?>? StateChanged;

    public ProcessManagerService(InstallerService installer, LogService log)
    {
        _installer = installer;
        _log = log;

        foreach (var desc in ServiceDescriptor.All)
        {
            _processes[desc.Kind] = null;
            _locks[desc.Kind] = new SemaphoreSlim(1, 1);
        }
    }

    public bool IsRunning(ServiceKind kind) =>
        (_processes.TryGetValue(kind, out var proc) && proc is { HasExited: false }) ||
        _externalRunning.Contains(kind);

    public bool IsAnyRunning =>
        ServiceDescriptor.All.Any(d => IsRunning(d.Kind));

    public int? GetPid(ServiceKind kind) =>
        _processes.TryGetValue(kind, out var proc) && proc is { HasExited: false } ? proc.Id : null;

    public async Task<bool> StartServiceAsync(ServiceKind kind, AppSettings settings)
    {
        if (kind == ServiceKind.Composer)
            return false;

        var gate = _locks[kind];
        await gate.WaitAsync();
        var desc = ServiceDescriptor.Get(kind);

        try
        {
            if (IsRunning(kind))
                return true;

            var version = settings.GetActiveVersion(kind);
            if (string.IsNullOrWhiteSpace(version))
            {
                _log.Warn(desc.LogSource, $"Nenhuma versão de {desc.ShortName} instalada.");
                StateChanged?.Invoke(kind, ServiceState.NotInstalled, null);
                return false;
            }

            var installDir = settings.GetInstallDirectory(kind, version);
            if (InstallerService.FindServiceExecutable(kind, installDir) is null)
            {
                _log.Error(desc.LogSource, $"Executável de {desc.ShortName} não encontrado em {installDir}.");
                StateChanged?.Invoke(kind, ServiceState.NotInstalled, null);
                return false;
            }

            // SQL Server has its own service/LocalDB lifecycle
            if (kind == ServiceKind.SqlServer)
                return await StartSqlServerAsync(installDir, version, settings);

            var port = settings.GetPort(kind);
            if (!IsPortAvailable(port))
            {
                _log.Error(desc.LogSource, $"A porta {port} já está em uso. Altere em Configurações.");
                StateChanged?.Invoke(kind, ServiceState.Error, null);
                return false;
            }

            StateChanged?.Invoke(kind, ServiceState.Starting, null);
            var psi = await BuildProcessStartInfoAsync(kind, installDir, settings);

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    _log.Info(desc.LogSource, e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    var level = e.Data.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                                e.Data.Contains("fatal", StringComparison.OrdinalIgnoreCase)
                        ? LogLevel.Error
                        : e.Data.Contains("warn", StringComparison.OrdinalIgnoreCase)
                            ? LogLevel.Warning
                            : LogLevel.Info;
                    _log.Log(desc.LogSource, e.Data, level);
                }
            };
            process.Exited += (_, _) =>
            {
                var exitCode = SafeGetExitCode(process);
                if (PlatformHelper.IsWindows && exitCode == -1073741515)
                    _log.Error(desc.LogSource, "Erro 0xC0000135 (DLL ausente). Instale o Visual C++ Redistributable x64.");
                else if (exitCode is not null and not 0 and not -1)
                    _log.Warn(desc.LogSource, $"{desc.ShortName} encerrado (código {exitCode}).");

                if (ReferenceEquals(_processes[kind], process))
                {
                    _processes[kind] = null;
                    StateChanged?.Invoke(kind, ServiceState.Stopped, null);
                }
            };

            if (!process.Start())
            {
                _log.Error(desc.LogSource, $"Falha ao iniciar {desc.ShortName}.");
                StateChanged?.Invoke(kind, ServiceState.Error, null);
                return false;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _processes[kind] = process;

            var host = kind == ServiceKind.Php ? settings.PhpHost : "127.0.0.1";
            var timeout = kind == ServiceKind.Cassandra ? TimeSpan.FromSeconds(18) : TimeSpan.FromSeconds(8);
            var ready = await WaitForPortListeningAsync(host, port, process, timeout);

            if (process.HasExited)
            {
                StateChanged?.Invoke(kind, ServiceState.Error, null);
                return false;
            }

            if (ready)
                _log.Success(desc.LogSource, $"{desc.ShortName} {version} rodando em {host}:{port} (PID {process.Id}).");
            else
                _log.Warn(desc.LogSource, $"{desc.ShortName} iniciado (PID {process.Id}), aguardando porta {port}.");

            StateChanged?.Invoke(kind, ServiceState.Running, process.Id);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(desc.LogSource, $"Erro ao iniciar {desc.ShortName}: {ex.Message}");
            StateChanged?.Invoke(kind, ServiceState.Error, null);
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task StopServiceAsync(ServiceKind kind, AppSettings settings)
    {
        if (kind == ServiceKind.Composer)
            return;

        var gate = _locks[kind];
        await gate.WaitAsync();
        var desc = ServiceDescriptor.Get(kind);

        try
        {
            if (kind == ServiceKind.SqlServer && _externalRunning.Contains(ServiceKind.SqlServer))
            {
                await StopSqlServerAsync(settings);
                return;
            }

            var proc = _processes[kind];
            if (proc is null)
            {
                StateChanged?.Invoke(kind, ServiceState.Stopped, null);
                return;
            }

            StateChanged?.Invoke(kind, ServiceState.Stopping, proc.Id);
            _log.Info(desc.LogSource, $"Encerrando {desc.ShortName} (PID {proc.Id})...");

            try
            {
                await TryGracefulShutdownAsync(kind, settings);

                if (!proc.HasExited)
                {
                    using var waitCts = new CancellationTokenSource(3500);
                    try { await proc.WaitForExitAsync(waitCts.Token); } catch { }
                }

                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    using var killCts = new CancellationTokenSource(2000);
                    try { await proc.WaitForExitAsync(killCts.Token); } catch { }
                }
            }
            catch (Exception ex)
            {
                _log.Warn(desc.LogSource, $"Aviso ao encerrar {desc.ShortName}: {ex.Message}");
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            }
            finally
            {
                _processes[kind] = null;
                proc.Dispose();
                _log.Info(desc.LogSource, $"{desc.ShortName} parado.");
                StateChanged?.Invoke(kind, ServiceState.Stopped, null);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task StopAllAsync(AppSettings settings)
    {
        var running = ServiceDescriptor.All
            .Where(d => IsRunning(d.Kind))
            .Select(d => StopServiceAsync(d.Kind, settings));
        await Task.WhenAll(running);
    }

    public void StopAllSync(AppSettings settings)
    {
        try
        {
            StopAllAsync(settings).GetAwaiter().GetResult();
        }
        catch
        {
            foreach (var proc in _processes.Values)
            {
                try { proc?.Kill(true); } catch { }
            }
        }
    }

    private async Task<ProcessStartInfo> BuildProcessStartInfoAsync(ServiceKind kind, string installDir, AppSettings settings)
    {
        var effectiveDir = InstallerService.ResolveEffectiveRuntimeDirectory(installDir);
        var port = settings.GetPort(kind);

        switch (kind)
        {
            case ServiceKind.Php:
            {
                Directory.CreateDirectory(settings.ProjectsDirectory);
                var exe = InstallerService.FindServiceExecutable(kind, installDir)!;
                var ini = Path.Combine(effectiveDir, "php.ini");
                var psi = CreateBasePsi(exe, settings.ProjectsDirectory);
                if (File.Exists(ini)) { psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(ini); }
                psi.ArgumentList.Add("-S"); psi.ArgumentList.Add($"{settings.PhpHost}:{port}");
                psi.ArgumentList.Add("-t"); psi.ArgumentList.Add(settings.ProjectsDirectory);
                return psi;
            }

            case ServiceKind.MariaDb:
            {
                await _installer.EnsureMariaDbInitializedAsync(installDir, settings);
                var exe = InstallerService.FindServiceExecutable(kind, installDir)!;
                var configPath = PlatformHelper.ResolveMySqlStyleConfigPath(settings.GetDataDirectory(kind));
                var psi = CreateBasePsi(exe, Path.GetDirectoryName(exe)!);
                psi.ArgumentList.Add($"--defaults-file={configPath}");
                psi.ArgumentList.Add($"--port={port}");
                if (PlatformHelper.IsWindows) psi.ArgumentList.Add("--console");
                else psi.ArgumentList.Add($"--socket={settings.GetMariaDbSocketPath()}");
                return psi;
            }

            case ServiceKind.MySql:
            {
                await _installer.EnsureMySqlInitializedAsync(installDir, settings);
                var exe = InstallerService.FindServiceExecutable(kind, installDir)!;
                var configPath = PlatformHelper.ResolveMySqlStyleConfigPath(settings.GetDataDirectory(kind));
                var psi = CreateBasePsi(exe, Path.GetDirectoryName(exe)!);
                psi.ArgumentList.Add($"--defaults-file={configPath}");
                psi.ArgumentList.Add($"--port={port}");
                if (PlatformHelper.IsWindows) psi.ArgumentList.Add("--console");
                else psi.ArgumentList.Add($"--socket={settings.GetMySqlSocketPath()}");
                return psi;
            }

            case ServiceKind.PostgreSql:
            {
                await _installer.EnsurePostgreSqlInitializedAsync(installDir, settings);
                var exe = InstallerService.FindServiceExecutable(kind, installDir)!;
                var dataDir = settings.GetDataDirectory(kind);
                var psi = CreateBasePsi(exe, Path.GetDirectoryName(exe)!);
                psi.ArgumentList.Add("-D"); psi.ArgumentList.Add(dataDir);
                psi.ArgumentList.Add("-h"); psi.ArgumentList.Add("127.0.0.1");
                psi.ArgumentList.Add("-p"); psi.ArgumentList.Add(port.ToString());
                return psi;
            }

            case ServiceKind.MongoDb:
            {
                InstallerService.EnsureMongoDbInitialized(settings);
                var exe = InstallerService.FindServiceExecutable(kind, installDir)!;
                var dataDir = settings.GetDataDirectory(kind);
                var cfg = Path.Combine(dataDir, "mongod.cfg");
                var psi = CreateBasePsi(exe, Path.GetDirectoryName(exe)!);
                psi.ArgumentList.Add("--config"); psi.ArgumentList.Add(cfg);
                return psi;
            }

            case ServiceKind.Redis:
            {
                InstallerService.EnsureRedisInitialized(settings);
                var exe = InstallerService.FindServiceExecutable(kind, installDir)!;
                var dataDir = settings.GetDataDirectory(kind);
                var conf = Path.Combine(dataDir, "redis.conf");
                var psi = CreateBasePsi(exe, Path.GetDirectoryName(exe)!);
                psi.ArgumentList.Add(conf);
                return psi;
            }

            case ServiceKind.Cassandra:
            {
                var javaExe = await _installer.EnsurePortableJavaIfMissingAsync(settings, null, CancellationToken.None);
                _installer.EnsureCassandraInitialized(effectiveDir, settings);
                var confDir = Path.Combine(effectiveDir, "conf");
                var libDir = Path.Combine(effectiveDir, "lib");
                var dataDir = settings.GetDataDirectory(kind);
                var cp = string.Join(Path.PathSeparator, new[] { confDir }.Concat(Directory.GetFiles(libDir, "*.jar")));

                var psi = CreateBasePsi(javaExe, effectiveDir);
                psi.ArgumentList.Add("-Xms256M");
                psi.ArgumentList.Add("-Xmx512M");
                psi.ArgumentList.Add("-Dcassandra.foreground=yes");
                psi.ArgumentList.Add($"-Dcassandra.storagedir={dataDir}");
                psi.ArgumentList.Add("-Dcassandra-foreground=yes");
                psi.ArgumentList.Add($"-Dlogback.configurationFile={Path.Combine(confDir, "logback.xml")}");
                psi.ArgumentList.Add("-cp"); psi.ArgumentList.Add(cp);
                psi.ArgumentList.Add("org.apache.cassandra.service.CassandraDaemon");
                return psi;
            }

            default:
                throw new NotSupportedException($"Serviço {kind} não suporta execução direta.");
        }
    }

    private async Task<bool> StartSqlServerAsync(string installDir, string version, AppSettings settings)
    {
        StateChanged?.Invoke(ServiceKind.SqlServer, ServiceState.Starting, null);
        if (version.Contains("LocalDB", StringComparison.OrdinalIgnoreCase))
        {
            var localDbExe = InstallerService.FindServiceTool(ServiceKind.SqlServer, installDir, "SqlLocalDB");
            if (localDbExe is null)
            {
                StateChanged?.Invoke(ServiceKind.SqlServer, ServiceState.NotInstalled, null);
                return false;
            }

            var exit = await RunSimpleProcessAsync(localDbExe, "start BEStart");
            if (exit == 0)
            {
                _externalRunning.Add(ServiceKind.SqlServer);
                _log.Success("SQLServer", "SQL Server LocalDB (instância BEStart) iniciado em (localdb)\\BEStart.");
                StateChanged?.Invoke(ServiceKind.SqlServer, ServiceState.Running, null);
                return true;
            }

            _log.Error("SQLServer", $"Falha ao iniciar SqlLocalDB (código {exit}).");
            StateChanged?.Invoke(ServiceKind.SqlServer, ServiceState.Error, null);
            return false;
        }

        // Express Windows Service
        var svcExit = await RunSimpleProcessAsync("sc.exe", "start MSSQL$BESTART");
        if (svcExit == 0)
        {
            _externalRunning.Add(ServiceKind.SqlServer);
            _log.Success("SQLServer", $"SQL Server Express rodando em 127.0.0.1:{settings.GetPort(ServiceKind.SqlServer)}.");
            StateChanged?.Invoke(ServiceKind.SqlServer, ServiceState.Running, null);
            return true;
        }

        _log.Error("SQLServer", $"Falha ao iniciar serviço MSSQL$BESTART (código {svcExit}).");
        StateChanged?.Invoke(ServiceKind.SqlServer, ServiceState.Error, null);
        return false;
    }

    private async Task StopSqlServerAsync(AppSettings settings)
    {
        StateChanged?.Invoke(ServiceKind.SqlServer, ServiceState.Stopping, null);
        var ver = settings.GetActiveVersion(ServiceKind.SqlServer) ?? "";
        var dir = !string.IsNullOrWhiteSpace(ver) ? settings.GetInstallDirectory(ServiceKind.SqlServer, ver) : "";

        if (ver.Contains("LocalDB", StringComparison.OrdinalIgnoreCase))
        {
            var localDbExe = InstallerService.FindServiceTool(ServiceKind.SqlServer, dir, "SqlLocalDB");
            if (localDbExe is not null)
                await RunSimpleProcessAsync(localDbExe, "stop BEStart");
        }
        else
        {
            await RunSimpleProcessAsync("sc.exe", "stop MSSQL$BESTART");
        }

        _externalRunning.Remove(ServiceKind.SqlServer);
        _log.Info("SQLServer", "SQL Server parado.");
        StateChanged?.Invoke(ServiceKind.SqlServer, ServiceState.Stopped, null);
    }

    private async Task TryGracefulShutdownAsync(ServiceKind kind, AppSettings settings)
    {
        var version = settings.GetActiveVersion(kind);
        if (string.IsNullOrWhiteSpace(version)) return;
        var dir = settings.GetInstallDirectory(kind, version);
        var port = settings.GetPort(kind);

        switch (kind)
        {
            case ServiceKind.MariaDb:
            {
                var admin = InstallerService.FindServiceTool(kind, dir, "mariadb-admin", "mysqladmin");
                if (admin is not null)
                    await RunSimpleProcessAsync(admin, $"--host=127.0.0.1 --port={port} --protocol=TCP -u root shutdown");
                break;
            }
            case ServiceKind.MySql:
            {
                var admin = InstallerService.FindServiceTool(kind, dir, "mysqladmin");
                if (admin is not null)
                    await RunSimpleProcessAsync(admin, $"--host=127.0.0.1 --port={port} --protocol=TCP -u root shutdown");
                break;
            }
            case ServiceKind.PostgreSql:
            {
                var pgCtl = InstallerService.FindServiceTool(kind, dir, "pg_ctl");
                if (pgCtl is not null)
                    await RunSimpleProcessAsync(pgCtl, $"-D \"{settings.GetDataDirectory(kind)}\" stop -m fast");
                break;
            }
            case ServiceKind.Redis:
            {
                var cli = InstallerService.FindServiceTool(kind, dir, "redis-cli");
                if (cli is not null)
                    await RunSimpleProcessAsync(cli, $"-h 127.0.0.1 -p {port} shutdown nosave");
                break;
            }
        }
    }

    private static ProcessStartInfo CreateBasePsi(string exe, string workingDir) => new()
    {
        FileName = exe,
        WorkingDirectory = workingDir,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    private static async Task<int> RunSimpleProcessAsync(string exe, string args)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (proc is null) return -1;
            using var cts = new CancellationTokenSource(4500);
            await proc.WaitForExitAsync(cts.Token);
            return proc.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    public static bool IsPortAvailable(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch { return false; }
    }

    private static async Task<bool> WaitForPortListeningAsync(string host, int port, Process proc, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (proc.HasExited) return false;
            try
            {
                using var client = new TcpClient();
                using var cts = new CancellationTokenSource(250);
                await client.ConnectAsync(host, port, cts.Token);
                return true;
            }
            catch { await Task.Delay(250); }
        }
        return false;
    }

    private static int? SafeGetExitCode(Process p)
    {
        try { return p.ExitCode; } catch { return null; }
    }

    public void Dispose()
    {
        foreach (var kv in _processes)
        {
            try { kv.Value?.Kill(true); } catch { }
            kv.Value?.Dispose();
        }
        foreach (var l in _locks.Values)
            l.Dispose();
    }
}
