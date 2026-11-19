using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using PHPost.Models;

namespace PHPost.Services;

public sealed class DesktopSetupService
{
    private readonly LogService _log;

    public DesktopSetupService(LogService log)
    {
        _log = log;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(
        IntPtr hWnd,
        uint msg,
        UIntPtr wParam,
        string lParam,
        uint fuFlags,
        uint uTimeout,
        out UIntPtr lpdwResult);

    public (bool PhpInPath, bool MariaDbInPath) CheckUserPathStatus(AppSettings settings)
    {
        if (!OperatingSystem.IsWindows())
            return (false, false);

        var userPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
        var parts = userPath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var phpInPath = !string.IsNullOrWhiteSpace(settings.ActivePhpVersion) &&
                        parts.Contains(NormalizePath(settings.GetPhpInstallDirectory(settings.ActivePhpVersion)));

        var mariaInPath = !string.IsNullOrWhiteSpace(settings.ActiveMariaDbVersion) &&
                          parts.Contains(NormalizePath(Path.Combine(settings.GetMariaDbInstallDirectory(settings.ActiveMariaDbVersion), "bin")));

        return (phpInPath, mariaInPath);
    }

    public bool ConfigureUserPath(AppSettings settings)
    {
        if (!OperatingSystem.IsWindows())
        {
            _log.Warn("PHPost", "Configuração automática de PATH do Windows disponível apenas no Windows.");
            return false;
        }

        try
        {
            var currentUserPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
            var entries = currentUserPath
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            // Remove stale PHPost runtime paths so version switches stay clean
            var runtimesPrefix = NormalizePath(settings.RuntimesDirectory);
            entries.RemoveAll(e => NormalizePath(e).StartsWith(runtimesPrefix, StringComparison.OrdinalIgnoreCase));

            var added = new List<string>();

            if (!string.IsNullOrWhiteSpace(settings.ActivePhpVersion))
            {
                var phpDir = settings.GetPhpInstallDirectory(settings.ActivePhpVersion);
                if (Directory.Exists(phpDir))
                {
                    entries.Insert(0, phpDir);
                    added.Add("PHP");
                }
            }

            if (!string.IsNullOrWhiteSpace(settings.ActiveMariaDbVersion))
            {
                var mariaBin = Path.Combine(settings.GetMariaDbInstallDirectory(settings.ActiveMariaDbVersion), "bin");
                if (Directory.Exists(mariaBin))
                {
                    entries.Insert(0, mariaBin);
                    added.Add("MariaDB");
                }
            }

            if (added.Count == 0)
            {
                _log.Warn("PHPost", "Nenhum runtime instalado para adicionar ao PATH.");
                return false;
            }

            // Deduplicate preserving order
            var unique = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                var norm = NormalizePath(entry);
                if (seen.Add(norm))
                    unique.Add(entry);
            }

            var newUserPath = string.Join(';', unique);
            Environment.SetEnvironmentVariable("Path", newUserPath, EnvironmentVariableTarget.User);

            // Also update current process PATH so child terminals spawned from PHPost work immediately
            var processPath = Environment.GetEnvironmentVariable("Path") ?? "";
            Environment.SetEnvironmentVariable("Path", string.Join(';', unique.Take(added.Count)) + ";" + processPath);

            // Broadcast WM_SETTINGCHANGE so Explorer & new terminals refresh environment variables
            const int HWND_BROADCAST = 0xffff;
            const uint WM_SETTINGCHANGE = 0x001A;
            const uint SMTO_ABORTIFHUNG = 0x0002;
            SendMessageTimeoutW(new IntPtr(HWND_BROADCAST), WM_SETTINGCHANGE, UIntPtr.Zero, "Environment", SMTO_ABORTIFHUNG, 1000, out _);

            _log.Success("PHPost", $"PATH do usuário atualizado com {string.Join(" e ", added)}. Novos terminais já reconhecem `php` e `mariadb`.");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("PHPost", $"Falha ao configurar PATH do usuário: {ex.Message}");
            return false;
        }
    }

    public bool CreateDesktopShortcuts(AppSettings settings)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop))
            {
                _log.Warn("PHPost", "Pasta Área de Trabalho não encontrada.");
                return false;
            }

            var exePath = Environment.ProcessPath;
            var appShortcut = Path.Combine(desktop, "PHPost.lnk");
            var wwwShortcut = Path.Combine(desktop, "Projetos PHPost (www).lnk");

            var script = new StringBuilder();
            script.AppendLine("$ws = New-Object -ComObject WScript.Shell");

            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                script.AppendLine($"$s1 = $ws.CreateShortcut('{EscapePs(appShortcut)}')");
                script.AppendLine($"$s1.TargetPath = '{EscapePs(exePath)}'");
                script.AppendLine($"$s1.WorkingDirectory = '{EscapePs(Path.GetDirectoryName(exePath) ?? "")}'");
                script.AppendLine("$s1.Description = 'PHPost - Ambiente Local PHP + MariaDB'");
                script.AppendLine("$s1.Save()");
            }

            Directory.CreateDirectory(settings.ProjectsDirectory);
            script.AppendLine($"$s2 = $ws.CreateShortcut('{EscapePs(wwwShortcut)}')");
            script.AppendLine($"$s2.TargetPath = '{EscapePs(settings.ProjectsDirectory)}'");
            script.AppendLine("$s2.Description = 'Pasta de projetos PHP do PHPost'");
            script.AppendLine("$s2.Save()");

            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script.ToString()));
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);

            if (proc is { ExitCode: 0 })
            {
                _log.Success("PHPost", "Atalhos criados na Área de Trabalho: 'PHPost' e 'Projetos PHPost (www)'.");
                return true;
            }

            var err = proc?.StandardError.ReadToEnd();
            _log.Error("PHPost", $"Não foi possível criar os atalhos na Área de Trabalho: {err}");
            return false;
        }
        catch (Exception ex)
        {
            _log.Error("PHPost", $"Erro ao criar atalhos na Área de Trabalho: {ex.Message}");
            return false;
        }
    }

    public void OpenConfiguredTerminal(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(settings.ProjectsDirectory);

            var extraPaths = new List<string>();
            if (!string.IsNullOrWhiteSpace(settings.ActivePhpVersion))
            {
                var phpDir = settings.GetPhpInstallDirectory(settings.ActivePhpVersion);
                if (Directory.Exists(phpDir))
                    extraPaths.Add(phpDir);
            }
            if (!string.IsNullOrWhiteSpace(settings.ActiveMariaDbVersion))
            {
                var mariaBin = Path.Combine(settings.GetMariaDbInstallDirectory(settings.ActiveMariaDbVersion), "bin");
                if (Directory.Exists(mariaBin))
                    extraPaths.Add(mariaBin);
            }

            var pathPrefix = extraPaths.Count > 0 ? string.Join(';', extraPaths) + ";" : "";
            var initCmd =
                $"$env:Path = '{EscapePs(pathPrefix)}' + $env:Path; " +
                $"Write-Host '=== PHPost Terminal Pronto ===' -ForegroundColor Cyan; " +
                $"if (Get-Command php -ErrorAction SilentlyContinue) {{ php -v | Select-Object -First 1 }} " +
                $"if (Get-Command mariadb -ErrorAction SilentlyContinue) {{ mariadb --version }} " +
                $"Write-Host 'Pasta de projetos: {EscapePs(settings.ProjectsDirectory)}' -ForegroundColor DarkGray";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoExit -NoProfile -Command \"{initCmd}\"",
                WorkingDirectory = settings.ProjectsDirectory,
                UseShellExecute = true
            };

            Process.Start(psi);
            _log.Info("PHPost", "Terminal PowerShell aberto com PHP e MariaDB prontos no PATH.");
        }
        catch (Exception ex)
        {
            _log.Error("PHPost", $"Não foi possível abrir o terminal: {ex.Message}");
        }
    }

    public void OpenMariaDbCli(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ActiveMariaDbVersion))
            return;

        var mariaDir = settings.GetMariaDbInstallDirectory(settings.ActiveMariaDbVersion);
        var cliExe = InstallerService.FindMariaDbTool(mariaDir, "mariadb.exe", "mysql.exe");
        if (cliExe is null)
        {
            _log.Warn("MariaDB", "Cliente mariadb.exe/mysql.exe não encontrado na pasta bin.");
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = cliExe,
                Arguments = $"--host=127.0.0.1 --port={settings.MariaDbPort} --protocol=TCP -u root",
                WorkingDirectory = Path.GetDirectoryName(cliExe)!,
                UseShellExecute = true
            };
            Process.Start(psi);
            _log.Info("MariaDB", $"Console SQL aberto em 127.0.0.1:{settings.MariaDbPort} (usuário root).");
        }
        catch (Exception ex)
        {
            _log.Error("MariaDB", $"Não foi possível abrir o Console SQL: {ex.Message}");
        }
    }

    public void EnsureStarterWorkspace(AppSettings settings)
    {
        Directory.CreateDirectory(settings.ProjectsDirectory);
        var indexPath = Path.Combine(settings.ProjectsDirectory, "index.php");

        if (!File.Exists(indexPath))
        {
            File.WriteAllText(indexPath, StarterTemplates.GetRootDashboardPhp(settings.MariaDbPort), Encoding.UTF8);
            _log.Info("PHPost", $"Página inicial criada automaticamente em {indexPath}.");
        }
    }

    public ProjectInfo CreateProject(AppSettings settings, string rawName)
    {
        var cleanName = AppSettings.SanitizeFolderName(rawName.Trim()).Replace(' ', '-');
        if (string.IsNullOrWhiteSpace(cleanName))
            throw new ArgumentException("Informe um nome válido para o projeto.");

        Directory.CreateDirectory(settings.ProjectsDirectory);
        var projectDir = Path.Combine(settings.ProjectsDirectory, cleanName);
        Directory.CreateDirectory(projectDir);

        var indexPath = Path.Combine(projectDir, "index.php");
        if (!File.Exists(indexPath))
        {
            File.WriteAllText(indexPath, StarterTemplates.GetNewProjectIndexPhp(cleanName, settings.MariaDbPort), Encoding.UTF8);
        }

        _log.Success("PHPost", $"Projeto '{cleanName}' criado em {projectDir}.");

        return new ProjectInfo
        {
            Name = cleanName,
            FullPath = projectDir,
            Url = $"http://{settings.PhpHost}:{settings.PhpPort}/{Uri.EscapeDataString(cleanName)}/",
            LastModified = DateTime.Now,
            HasIndexPhp = true
        };
    }

    public IReadOnlyList<ProjectInfo> ScanProjects(AppSettings settings)
    {
        var list = new List<ProjectInfo>();
        if (!Directory.Exists(settings.ProjectsDirectory))
            return list;

        foreach (var dir in Directory.GetDirectories(settings.ProjectsDirectory))
        {
            var name = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith('.'))
                continue;

            var info = new DirectoryInfo(dir);
            list.Add(new ProjectInfo
            {
                Name = name,
                FullPath = dir,
                Url = $"http://{settings.PhpHost}:{settings.PhpPort}/{Uri.EscapeDataString(name)}/",
                LastModified = info.LastWriteTime,
                HasIndexPhp = File.Exists(Path.Combine(dir, "index.php"))
            });
        }

        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    public void OpenInShell(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _log.Error("PHPost", $"Não foi possível abrir '{target}': {ex.Message}");
        }
    }

    private static string NormalizePath(string path) =>
        path.Trim().TrimEnd('\\', '/');

    private static string EscapePs(string input) =>
        input.Replace("'", "''");
}
