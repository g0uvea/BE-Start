using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using PHPost.Models;

namespace PHPost.Services;

public sealed class ProcessManagerService : IDisposable
{
    private readonly InstallerService _installer;
    private readonly LogService _log;

    private Process? _phpProcess;
    private Process? _mariaDbProcess;
    private readonly SemaphoreSlim _phpLock = new(1, 1);
    private readonly SemaphoreSlim _mariaDbLock = new(1, 1);

    public event Action<ServiceKind, ServiceState, int?>? StateChanged;

    public bool IsPhpRunning => _phpProcess is { HasExited: false };
    public bool IsMariaDbRunning => _mariaDbProcess is { HasExited: false };
    public int? PhpPid => IsPhpRunning ? _phpProcess?.Id : null;
    public int? MariaDbPid => IsMariaDbRunning ? _mariaDbProcess?.Id : null;

    public ProcessManagerService(InstallerService installer, LogService log)
    {
        _installer = installer;
        _log = log;
    }

    public async Task<bool> StartPhpAsync(AppSettings settings)
    {
        await _phpLock.WaitAsync();
        try
        {
            if (IsPhpRunning)
                return true;

            if (string.IsNullOrWhiteSpace(settings.ActivePhpVersion))
            {
                _log.Warn("PHP", "Nenhuma versão do PHP instalada. Baixe o PHP primeiro.");
                StateChanged?.Invoke(ServiceKind.Php, ServiceState.NotInstalled, null);
                return false;
            }

            var phpDir = settings.GetPhpInstallDirectory(settings.ActivePhpVersion);
            var phpExe = Path.Combine(phpDir, "php.exe");
            if (!File.Exists(phpExe))
            {
                _log.Error("PHP", $"Executável não encontrado em {phpExe}.");
                StateChanged?.Invoke(ServiceKind.Php, ServiceState.NotInstalled, null);
                return false;
            }

            if (!IsPortAvailable(settings.PhpPort))
            {
                _log.Error("PHP", $"A porta {settings.PhpPort} já está em uso por outro processo. Altere a porta em Configurações.");
                StateChanged?.Invoke(ServiceKind.Php, ServiceState.Error, null);
                return false;
            }

            Directory.CreateDirectory(settings.ProjectsDirectory);
            StateChanged?.Invoke(ServiceKind.Php, ServiceState.Starting, null);

            var iniPath = Path.Combine(phpDir, "php.ini");
            var iniArg = File.Exists(iniPath) ? $"-c \"{iniPath}\" " : "";
            var args = $"{iniArg}-S {settings.PhpHost}:{settings.PhpPort} -t \"{settings.ProjectsDirectory}\"";

            var psi = new ProcessStartInfo
            {
                FileName = phpExe,
                Arguments = args,
                WorkingDirectory = settings.ProjectsDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    _log.Info("PHP", e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    // PHP built-in server writes request logs to stderr by design.
                    var level = e.Data.Contains("Fatal error", StringComparison.OrdinalIgnoreCase) ||
                                e.Data.Contains("Parse error", StringComparison.OrdinalIgnoreCase)
                        ? LogLevel.Error
                        : LogLevel.Info;
                    _log.Log("PHP", e.Data, level);
                }
            };
            process.Exited += (_, _) =>
            {
                var exitCode = SafeGetExitCode(process);
                if (exitCode == -1073741515)
                {
                    _log.Error("PHP", "PHP encerrou com erro 0xC0000135 (DLL ausente). Instale o Microsoft Visual C++ Redistributable x64.");
                }
                else if (exitCode is not null and not 0 and not -1)
                {
                    _log.Warn("PHP", $"Servidor PHP encerrado (código {exitCode}).");
                }

                if (ReferenceEquals(_phpProcess, process))
                {
                    _phpProcess = null;
                    StateChanged?.Invoke(ServiceKind.Php, ServiceState.Stopped, null);
                }
            };

            if (!process.Start())
            {
                _log.Error("PHP", "Falha ao iniciar o processo php.exe.");
                StateChanged?.Invoke(ServiceKind.Php, ServiceState.Error, null);
                return false;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _phpProcess = process;

            await Task.Delay(450);

            if (process.HasExited)
            {
                StateChanged?.Invoke(ServiceKind.Php, ServiceState.Error, null);
                return false;
            }

            _log.Success("PHP", $"Servidor PHP {settings.ActivePhpVersion} iniciado em http://{settings.PhpHost}:{settings.PhpPort} (PID {process.Id}).");
            StateChanged?.Invoke(ServiceKind.Php, ServiceState.Running, process.Id);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("PHP", $"Erro ao iniciar PHP: {ex.Message}");
            StateChanged?.Invoke(ServiceKind.Php, ServiceState.Error, null);
            return false;
        }
        finally
        {
            _phpLock.Release();
        }
    }

    public async Task StopPhpAsync()
    {
        await _phpLock.WaitAsync();
        try
        {
            var proc = _phpProcess;
            if (proc is null)
            {
                StateChanged?.Invoke(ServiceKind.Php, ServiceState.Stopped, null);
                return;
            }

            StateChanged?.Invoke(ServiceKind.Php, ServiceState.Stopping, proc.Id);
            _log.Info("PHP", $"Parando servidor PHP (PID {proc.Id})...");

            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    await proc.WaitForExitAsync(new CancellationTokenSource(3000).Token);
                }
            }
            catch (Exception ex)
            {
                _log.Warn("PHP", $"Aviso ao encerrar PHP: {ex.Message}");
            }
            finally
            {
                _phpProcess = null;
                proc.Dispose();
                _log.Info("PHP", "Servidor PHP parado.");
                StateChanged?.Invoke(ServiceKind.Php, ServiceState.Stopped, null);
            }
        }
        finally
        {
            _phpLock.Release();
        }
    }

    public async Task<bool> StartMariaDbAsync(AppSettings settings)
    {
        await _mariaDbLock.WaitAsync();
        try
        {
            if (IsMariaDbRunning)
                return true;

            if (string.IsNullOrWhiteSpace(settings.ActiveMariaDbVersion))
            {
                _log.Warn("MariaDB", "Nenhuma versão do MariaDB instalada. Baixe o MariaDB primeiro.");
                StateChanged?.Invoke(ServiceKind.MariaDb, ServiceState.NotInstalled, null);
                return false;
            }

            var mariaDir = settings.GetMariaDbInstallDirectory(settings.ActiveMariaDbVersion);
            var serverExe = InstallerService.FindMariaDbServerExecutable(mariaDir);
            if (serverExe is null)
            {
                _log.Error("MariaDB", $"Executável mariadbd.exe/mysqld.exe não encontrado em {mariaDir}.");
                StateChanged?.Invoke(ServiceKind.MariaDb, ServiceState.NotInstalled, null);
                return false;
            }

            if (!IsPortAvailable(settings.MariaDbPort))
            {
                _log.Error("MariaDB", $"A porta {settings.MariaDbPort} já está em uso por outro processo (ex: outro MySQL/XAMPP). Altere a porta em Configurações.");
                StateChanged?.Invoke(ServiceKind.MariaDb, ServiceState.Error, null);
                return false;
            }

            StateChanged?.Invoke(ServiceKind.MariaDb, ServiceState.Starting, null);

            // Ensure data dir and my.ini are initialized and synced with current port
            await _installer.EnsureMariaDbInitializedAsync(mariaDir, settings);

            var myIni = Path.Combine(settings.MariaDbDataDirectory, "my.ini");
            var args = $"--defaults-file=\"{myIni}\" --port={settings.MariaDbPort} --console";

            var psi = new ProcessStartInfo
            {
                FileName = serverExe,
                Arguments = args,
                WorkingDirectory = Path.GetDirectoryName(serverExe)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    _log.Info("MariaDB", e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    var level = e.Data.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase)
                        ? LogLevel.Error
                        : e.Data.Contains("[Warning]", StringComparison.OrdinalIgnoreCase)
                            ? LogLevel.Warning
                            : LogLevel.Info;
                    _log.Log("MariaDB", e.Data, level);
                }
            };
            process.Exited += (_, _) =>
            {
                var exitCode = SafeGetExitCode(process);
                if (exitCode == -1073741515)
                {
                    _log.Error("MariaDB", "MariaDB encerrou com erro 0xC0000135 (DLL ausente). Instale o Microsoft Visual C++ Redistributable x64.");
                }
                else if (exitCode is not null and not 0)
                {
                    _log.Warn("MariaDB", $"MariaDB encerrado (código {exitCode}).");
                }

                if (ReferenceEquals(_mariaDbProcess, process))
                {
                    _mariaDbProcess = null;
                    StateChanged?.Invoke(ServiceKind.MariaDb, ServiceState.Stopped, null);
                }
            };

            if (!process.Start())
            {
                _log.Error("MariaDB", "Falha ao iniciar o processo mariadbd.exe.");
                StateChanged?.Invoke(ServiceKind.MariaDb, ServiceState.Error, null);
                return false;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _mariaDbProcess = process;

            // Wait up to 6 seconds for MariaDB port to start listening
            var ready = await WaitForPortListeningAsync("127.0.0.1", settings.MariaDbPort, process, TimeSpan.FromSeconds(6));

            if (process.HasExited)
            {
                StateChanged?.Invoke(ServiceKind.MariaDb, ServiceState.Error, null);
                return false;
            }

            if (ready)
            {
                _log.Success("MariaDB", $"MariaDB {settings.ActiveMariaDbVersion} rodando em 127.0.0.1:{settings.MariaDbPort} (usuário: root, sem senha, PID {process.Id}).");
            }
            else
            {
                _log.Warn("MariaDB", $"MariaDB iniciado (PID {process.Id}), aguardando abertura da porta {settings.MariaDbPort}. Verifique os logs.");
            }

            StateChanged?.Invoke(ServiceKind.MariaDb, ServiceState.Running, process.Id);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("MariaDB", $"Erro ao iniciar MariaDB: {ex.Message}");
            StateChanged?.Invoke(ServiceKind.MariaDb, ServiceState.Error, null);
            return false;
        }
        finally
        {
            _mariaDbLock.Release();
        }
    }

    public async Task StopMariaDbAsync(AppSettings settings)
    {
        await _mariaDbLock.WaitAsync();
        try
        {
            var proc = _mariaDbProcess;
            if (proc is null)
            {
                StateChanged?.Invoke(ServiceKind.MariaDb, ServiceState.Stopped, null);
                return;
            }

            StateChanged?.Invoke(ServiceKind.MariaDb, ServiceState.Stopping, proc.Id);
            _log.Info("MariaDB", $"Encerrando MariaDB com segurança (PID {proc.Id})...");

            try
            {
                // Try graceful shutdown via mariadb-admin / mysqladmin first
                if (!string.IsNullOrWhiteSpace(settings.ActiveMariaDbVersion))
                {
                    var mariaDir = settings.GetMariaDbInstallDirectory(settings.ActiveMariaDbVersion);
                    var adminExe = InstallerService.FindMariaDbAdminExecutable(mariaDir);
                    if (adminExe is not null)
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = adminExe,
                            Arguments = $"--host=127.0.0.1 --port={settings.MariaDbPort} --protocol=TCP -u root shutdown",
                            WorkingDirectory = Path.GetDirectoryName(adminExe)!,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardError = true,
                            RedirectStandardOutput = true
                        };

                        using var adminProc = Process.Start(psi);
                        if (adminProc is not null)
                        {
                            using var cts = new CancellationTokenSource(4000);
                            try { await adminProc.WaitForExitAsync(cts.Token); } catch { }
                        }
                    }
                }

                if (!proc.HasExited)
                {
                    using var waitCts = new CancellationTokenSource(4500);
                    try { await proc.WaitForExitAsync(waitCts.Token); } catch { }
                }

                if (!proc.HasExited)
                {
                    _log.Warn("MariaDB", "Shutdown gracioso expirou; forçando encerramento do processo.");
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                _log.Warn("MariaDB", $"Aviso ao encerrar MariaDB: {ex.Message}");
                try
                {
                    if (!proc.HasExited)
                        proc.Kill(entireProcessTree: true);
                }
                catch { }
            }
            finally
            {
                _mariaDbProcess = null;
                proc.Dispose();
                _log.Info("MariaDB", "MariaDB parado com sucesso.");
                StateChanged?.Invoke(ServiceKind.MariaDb, ServiceState.Stopped, null);
            }
        }
        finally
        {
            _mariaDbLock.Release();
        }
    }

    public async Task StopAllAsync(AppSettings settings)
    {
        await Task.WhenAll(
            StopPhpAsync(),
            StopMariaDbAsync(settings));
    }

    public void StopAllSync(AppSettings settings)
    {
        try
        {
            StopAllAsync(settings).GetAwaiter().GetResult();
        }
        catch
        {
            try { _phpProcess?.Kill(true); } catch { }
            try { _mariaDbProcess?.Kill(true); } catch { }
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
        catch
        {
            return false;
        }
    }

    private static async Task<bool> WaitForPortListeningAsync(string host, int port, Process proc, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (proc.HasExited)
                return false;

            try
            {
                using var client = new TcpClient();
                using var cts = new CancellationTokenSource(250);
                await client.ConnectAsync(host, port, cts.Token);
                return true;
            }
            catch
            {
                await Task.Delay(250);
            }
        }
        return false;
    }

    private static int? SafeGetExitCode(Process p)
    {
        try { return p.ExitCode; }
        catch { return null; }
    }

    public void Dispose()
    {
        try { _phpProcess?.Kill(true); } catch { }
        try { _mariaDbProcess?.Kill(true); } catch { }
        _phpProcess?.Dispose();
        _mariaDbProcess?.Dispose();
        _phpLock.Dispose();
        _mariaDbLock.Dispose();
    }
}
