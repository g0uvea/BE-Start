using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using PHPost.Models;

namespace PHPost.Services;

public sealed class DesktopSetupService
{
    private const string PathBlockStart = "# >>> PHPost PATH >>>";
    private const string PathBlockEnd = "# <<< PHPost PATH <<<";

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
        var phpBin = !string.IsNullOrWhiteSpace(settings.ActivePhpVersion)
            ? InstallerService.GetPhpBinDirectory(settings.GetPhpInstallDirectory(settings.ActivePhpVersion))
            : null;
        var mariaBin = !string.IsNullOrWhiteSpace(settings.ActiveMariaDbVersion)
            ? InstallerService.GetMariaDbBinDirectory(settings.GetMariaDbInstallDirectory(settings.ActiveMariaDbVersion))
            : null;

        if (PlatformHelper.IsWindows)
        {
            var userPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
            var parts = userPath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(NormalizePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var phpInPath = !string.IsNullOrWhiteSpace(phpBin) && parts.Contains(NormalizePath(phpBin));
            var mariaInPath = !string.IsNullOrWhiteSpace(mariaBin) && parts.Contains(NormalizePath(mariaBin));
            return (phpInPath, mariaInPath);
        }

        // Linux & macOS: inspect current PATH + shell profile files
        var combinedContent = new StringBuilder();
        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        combinedContent.AppendLine(currentPath);

        foreach (var profile in GetUnixShellProfileCandidates(includeOnlyExisting: true))
        {
            try
            {
                combinedContent.AppendLine(File.ReadAllText(profile, Encoding.UTF8));
            }
            catch
            {
                // Ignore unreadable profiles.
            }
        }

        var haystack = combinedContent.ToString();
        var phpFound = !string.IsNullOrWhiteSpace(phpBin) &&
                       haystack.Contains(NormalizePath(phpBin), StringComparison.Ordinal);
        var mariaFound = !string.IsNullOrWhiteSpace(mariaBin) &&
                         haystack.Contains(NormalizePath(mariaBin), StringComparison.Ordinal);

        return (phpFound, mariaFound);
    }

    public bool ConfigureUserPath(AppSettings settings)
    {
        if (PlatformHelper.IsWindows)
            return ConfigureWindowsUserPath(settings);

        return ConfigureUnixUserPath(settings);
    }

    private bool ConfigureWindowsUserPath(AppSettings settings)
    {
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
                var phpBin = InstallerService.GetPhpBinDirectory(settings.GetPhpInstallDirectory(settings.ActivePhpVersion));
                if (!string.IsNullOrWhiteSpace(phpBin) && Directory.Exists(phpBin))
                {
                    entries.Insert(0, phpBin);
                    added.Add("PHP");
                }
            }

            if (!string.IsNullOrWhiteSpace(settings.ActiveMariaDbVersion))
            {
                var mariaBin = InstallerService.GetMariaDbBinDirectory(settings.GetMariaDbInstallDirectory(settings.ActiveMariaDbVersion));
                if (!string.IsNullOrWhiteSpace(mariaBin) && Directory.Exists(mariaBin))
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

    private bool ConfigureUnixUserPath(AppSettings settings)
    {
        try
        {
            var binDirs = new List<string>();
            var added = new List<string>();

            if (!string.IsNullOrWhiteSpace(settings.ActiveMariaDbVersion))
            {
                var mariaBin = InstallerService.GetMariaDbBinDirectory(settings.GetMariaDbInstallDirectory(settings.ActiveMariaDbVersion));
                if (!string.IsNullOrWhiteSpace(mariaBin) && Directory.Exists(mariaBin))
                {
                    binDirs.Add(mariaBin);
                    added.Add("MariaDB");
                }
            }

            if (!string.IsNullOrWhiteSpace(settings.ActivePhpVersion))
            {
                var phpBin = InstallerService.GetPhpBinDirectory(settings.GetPhpInstallDirectory(settings.ActivePhpVersion));
                if (!string.IsNullOrWhiteSpace(phpBin) && Directory.Exists(phpBin))
                {
                    binDirs.Add(phpBin);
                    added.Add("PHP");
                }
            }

            if (binDirs.Count == 0)
            {
                _log.Warn("PHPost", "Nenhum runtime instalado para adicionar ao PATH.");
                return false;
            }

            var joinedUnixPath = string.Join(':', binDirs);
            var posixBlock = $"{PathBlockStart}\nexport PATH=\"{EscapeDoubleQuotes(joinedUnixPath)}:$PATH\"\n{PathBlockEnd}";
            var updatedFiles = new List<string>();

            foreach (var profile in GetUnixShellProfileCandidates(includeOnlyExisting: false))
            {
                UpsertManagedProfileBlock(profile, posixBlock);
                updatedFiles.Add(Path.GetFileName(profile));
            }

            // Support Fish shell if installed
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
            {
                var fishDir = Path.Combine(home, ".config", "fish");
                if (Directory.Exists(fishDir))
                {
                    var fishConfD = Path.Combine(fishDir, "conf.d");
                    Directory.CreateDirectory(fishConfD);
                    var fishLines = new StringBuilder();
                    fishLines.AppendLine(PathBlockStart);
                    foreach (var dir in binDirs)
                        fishLines.AppendLine($"fish_add_path -p \"{EscapeDoubleQuotes(dir)}\"");
                    fishLines.AppendLine(PathBlockEnd);
                    File.WriteAllText(Path.Combine(fishConfD, "phpost.fish"), fishLines.ToString(), PlatformHelper.Utf8NoBom);
                    updatedFiles.Add("conf.d/phpost.fish");
                }
            }

            // Update current process PATH immediately
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            var remaining = currentPath.Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Where(p => !binDirs.Contains(p, StringComparer.Ordinal));
            Environment.SetEnvironmentVariable("PATH", string.Join(':', binDirs.Concat(remaining)));

            _log.Success("PHPost", $"PATH atualizado em {string.Join(", ", updatedFiles)} com {string.Join(" e ", added)}. Abra um novo terminal para usar `php` e `mariadb`.");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("PHPost", $"Falha ao configurar PATH no {PlatformHelper.PlatformDisplayName}: {ex.Message}");
            return false;
        }
    }

    public bool CreateDesktopShortcuts(AppSettings settings)
    {
        if (PlatformHelper.IsWindows)
            return CreateWindowsDesktopShortcuts(settings);

        if (PlatformHelper.IsLinux)
            return CreateLinuxDesktopShortcuts(settings);

        if (PlatformHelper.IsMacOS)
            return CreateMacOsDesktopShortcuts(settings);

        return false;
    }

    private bool CreateWindowsDesktopShortcuts(AppSettings settings)
    {
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

    private bool CreateLinuxDesktopShortcuts(AppSettings settings)
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var exePath = Environment.ProcessPath;
            Directory.CreateDirectory(settings.ProjectsDirectory);

            var created = new List<string>();

            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                var appEntry = $"""
[Desktop Entry]
Type=Application
Name=PHPost
Comment=Ambiente Local PHP + MariaDB
Exec="{EscapeDoubleQuotes(exePath)}"
Path={Path.GetDirectoryName(exePath)}
Terminal=false
Categories=Development;WebDevelopment;
""";

                // 1. Register in user Applications menu (~/.local/share/applications)
                var appsDir = Path.Combine(home, ".local", "share", "applications");
                Directory.CreateDirectory(appsDir);
                var menuFile = Path.Combine(appsDir, "phpost.desktop");
                File.WriteAllText(menuFile, appEntry + "\n", PlatformHelper.Utf8NoBom);
                PlatformHelper.EnsureUnixExecutable(menuFile);
                created.Add("Menu de Aplicativos");

                // 2. Desktop launcher if Desktop folder exists
                var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (!string.IsNullOrWhiteSpace(desktopDir) && Directory.Exists(desktopDir))
                {
                    var desktopApp = Path.Combine(desktopDir, "PHPost.desktop");
                    File.WriteAllText(desktopApp, appEntry + "\n", PlatformHelper.Utf8NoBom);
                    PlatformHelper.EnsureUnixExecutable(desktopApp);

                    var wwwEntry = $"""
[Desktop Entry]
Type=Application
Name=Projetos PHPost (www)
Comment=Pasta de projetos PHP do PHPost
Exec=xdg-open "{EscapeDoubleQuotes(settings.ProjectsDirectory)}"
Icon=folder
Terminal=false
""";
                    var desktopWww = Path.Combine(desktopDir, "Projetos-PHPost.desktop");
                    File.WriteAllText(desktopWww, wwwEntry + "\n", PlatformHelper.Utf8NoBom);
                    PlatformHelper.EnsureUnixExecutable(desktopWww);
                    created.Add("Área de Trabalho");
                }
            }

            if (created.Count > 0)
            {
                _log.Success("PHPost", $"Atalhos Linux (.desktop) criados em: {string.Join(" e ", created)}.");
                return true;
            }

            _log.Warn("PHPost", "Não foi possível localizar o executável do PHPost para criar atalhos.");
            return false;
        }
        catch (Exception ex)
        {
            _log.Error("PHPost", $"Erro ao criar atalhos no Linux: {ex.Message}");
            return false;
        }
    }

    private bool CreateMacOsDesktopShortcuts(AppSettings settings)
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                desktop = Path.Combine(home, "Desktop");
            }

            if (!Directory.Exists(desktop))
            {
                _log.Warn("PHPost", "Pasta Desktop não encontrada no macOS.");
                return false;
            }

            Directory.CreateDirectory(settings.ProjectsDirectory);

            // Symlink to www folder on Desktop
            var wwwLink = Path.Combine(desktop, "Projetos PHPost (www)");
            if (File.Exists(wwwLink) || Directory.Exists(wwwLink))
            {
                var attr = File.GetAttributes(wwwLink);
                if (attr.HasFlag(FileAttributes.ReparsePoint))
                    Directory.Delete(wwwLink);
            }
            if (!File.Exists(wwwLink) && !Directory.Exists(wwwLink))
                Directory.CreateSymbolicLink(wwwLink, settings.ProjectsDirectory);

            // App shortcut: if running inside a .app bundle, symlink the .app; otherwise create a .command launcher
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                var appBundleMarker = ".app/Contents/MacOS/";
                var markerIdx = exePath.IndexOf(appBundleMarker, StringComparison.OrdinalIgnoreCase);
                if (markerIdx > 0)
                {
                    var appBundlePath = exePath[..(markerIdx + 4)];
                    var appLink = Path.Combine(desktop, "PHPost.app");
                    if (!Directory.Exists(appLink) && !File.Exists(appLink))
                        Directory.CreateSymbolicLink(appLink, appBundlePath);
                }
                else
                {
                    var launcher = Path.Combine(desktop, "PHPost.command");
                    var script = $"#!/bin/zsh\nnohup \"{EscapeDoubleQuotes(exePath)}\" >/dev/null 2>&1 &\n";
                    File.WriteAllText(launcher, script, PlatformHelper.Utf8NoBom);
                    PlatformHelper.EnsureUnixExecutable(launcher);
                }
            }

            _log.Success("PHPost", "Atalhos criados no Desktop do macOS: 'PHPost' e 'Projetos PHPost (www)'.");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("PHPost", $"Erro ao criar atalhos no macOS: {ex.Message}");
            return false;
        }
    }

    public void OpenConfiguredTerminal(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(settings.ProjectsDirectory);

            var extraPaths = CollectRuntimeBinDirectories(settings);

            if (PlatformHelper.IsWindows)
            {
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
                return;
            }

            var unixPathPrefix = extraPaths.Count > 0 ? string.Join(':', extraPaths) + ":" : "";
            var unixBanner =
                $"cd '{EscapeSingleQuotes(settings.ProjectsDirectory)}' && " +
                $"export PATH='{EscapeSingleQuotes(unixPathPrefix)}'\"$PATH\" && " +
                $"echo '=== PHPost Terminal Pronto ===' && " +
                $"(command -v php >/dev/null 2>&1 && php -v | head -n 1 || true) && " +
                $"(command -v mariadb >/dev/null 2>&1 && mariadb --version || true) && " +
                $"echo 'Pasta de projetos: {EscapeSingleQuotes(settings.ProjectsDirectory)}'";

            if (PlatformHelper.IsMacOS)
            {
                OpenMacOsTerminalCommand(unixBanner);
                _log.Info("PHPost", "Terminal.app aberto com PHP e MariaDB prontos no PATH.");
                return;
            }

            var userShell = Environment.GetEnvironmentVariable("SHELL");
            if (string.IsNullOrWhiteSpace(userShell) || !File.Exists(userShell))
                userShell = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";

            if (OpenLinuxTerminalCommand($"{unixBanner}; exec '{EscapeSingleQuotes(userShell)}' -i", settings.ProjectsDirectory))
                _log.Info("PHPost", "Terminal Linux aberto com PHP e MariaDB prontos no PATH.");
            else
                _log.Warn("PHPost", "Nenhum emulador de terminal Linux encontrado (tente instalar gnome-terminal, konsole, xfce4-terminal ou xterm).");
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
        var cliExe = InstallerService.FindMariaDbCliExecutable(mariaDir);
        if (cliExe is null)
        {
            _log.Warn("MariaDB", "Cliente mariadb/mysql não encontrado na pasta bin.");
            return;
        }

        try
        {
            if (PlatformHelper.IsWindows)
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
                return;
            }

            var cmd = $"'{EscapeSingleQuotes(cliExe)}' --host=127.0.0.1 --port={settings.MariaDbPort} --protocol=TCP -u root";
            if (PlatformHelper.IsMacOS)
            {
                OpenMacOsTerminalCommand(cmd);
                _log.Info("MariaDB", $"Console SQL aberto no Terminal.app em 127.0.0.1:{settings.MariaDbPort} (usuário root).");
                return;
            }

            if (OpenLinuxTerminalCommand(cmd, settings.ProjectsDirectory))
                _log.Info("MariaDB", $"Console SQL aberto em 127.0.0.1:{settings.MariaDbPort} (usuário root).");
            else
                _log.Warn("MariaDB", "Nenhum emulador de terminal Linux encontrado para abrir o Console SQL.");
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
            File.WriteAllText(indexPath, StarterTemplates.GetRootDashboardPhp(settings.MariaDbPort), PlatformHelper.Utf8NoBom);
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
            File.WriteAllText(indexPath, StarterTemplates.GetNewProjectIndexPhp(cleanName, settings.MariaDbPort), PlatformHelper.Utf8NoBom);
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
            if (PlatformHelper.IsWindows)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = target,
                    UseShellExecute = true
                });
                return;
            }

            if (PlatformHelper.IsMacOS)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "/usr/bin/open",
                    UseShellExecute = false
                };
                psi.ArgumentList.Add(target);
                Process.Start(psi);
                return;
            }

            var linuxPsi = new ProcessStartInfo
            {
                FileName = "xdg-open",
                UseShellExecute = false
            };
            linuxPsi.ArgumentList.Add(target);
            Process.Start(linuxPsi);
        }
        catch (Exception ex)
        {
            _log.Error("PHPost", $"Não foi possível abrir '{target}': {ex.Message}");
        }
    }

    private static List<string> CollectRuntimeBinDirectories(AppSettings settings)
    {
        var dirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.ActivePhpVersion))
        {
            var phpBin = InstallerService.GetPhpBinDirectory(settings.GetPhpInstallDirectory(settings.ActivePhpVersion));
            if (!string.IsNullOrWhiteSpace(phpBin) && Directory.Exists(phpBin))
                dirs.Add(phpBin);
        }

        if (!string.IsNullOrWhiteSpace(settings.ActiveMariaDbVersion))
        {
            var mariaBin = InstallerService.GetMariaDbBinDirectory(settings.GetMariaDbInstallDirectory(settings.ActiveMariaDbVersion));
            if (!string.IsNullOrWhiteSpace(mariaBin) && Directory.Exists(mariaBin))
                dirs.Add(mariaBin);
        }

        return dirs;
    }

    private static List<string> GetUnixShellProfileCandidates(bool includeOnlyExisting)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            return [];

        var profiles = new List<string>();

        if (PlatformHelper.IsMacOS)
        {
            var zshrc = Path.Combine(home, ".zshrc");
            if (!includeOnlyExisting || File.Exists(zshrc))
                profiles.Add(zshrc);

            foreach (var extra in new[] { ".bash_profile", ".bashrc", ".zprofile" })
            {
                var path = Path.Combine(home, extra);
                if (File.Exists(path))
                    profiles.Add(path);
            }
        }
        else
        {
            var bashrc = Path.Combine(home, ".bashrc");
            var profile = Path.Combine(home, ".profile");
            if (!includeOnlyExisting || File.Exists(bashrc))
                profiles.Add(bashrc);
            if (!includeOnlyExisting || File.Exists(profile))
                profiles.Add(profile);

            var zshrc = Path.Combine(home, ".zshrc");
            if (File.Exists(zshrc) || (Environment.GetEnvironmentVariable("SHELL")?.Contains("zsh", StringComparison.OrdinalIgnoreCase) ?? false))
                profiles.Add(zshrc);
        }

        return profiles.Distinct(StringComparer.Ordinal).ToList();
    }

    private static void UpsertManagedProfileBlock(string profilePath, string block)
    {
        var existing = File.Exists(profilePath)
            ? File.ReadAllText(profilePath, Encoding.UTF8).TrimStart('\uFEFF')
            : string.Empty;

        var pattern = $@"{Regex.Escape(PathBlockStart)}[\s\S]*?{Regex.Escape(PathBlockEnd)}";
        string updated;
        if (Regex.IsMatch(existing, pattern))
        {
            updated = Regex.Replace(existing, pattern, block);
        }
        else
        {
            var sep = string.IsNullOrEmpty(existing) || existing.EndsWith('\n') ? "\n" : "\n\n";
            updated = string.IsNullOrEmpty(existing) ? block + "\n" : existing + sep + block + "\n";
        }

        File.WriteAllText(profilePath, updated, PlatformHelper.Utf8NoBom);
    }

    private static void OpenMacOsTerminalCommand(string shellCommand)
    {
        var escaped = shellCommand.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var psi = new ProcessStartInfo
        {
            FileName = "/usr/bin/osascript",
            UseShellExecute = false
        };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add("tell application \"Terminal\"");
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add("activate");
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add($"do script \"{escaped}\"");
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add("end tell");
        Process.Start(psi);
    }

    private static bool OpenLinuxTerminalCommand(string shellCommand, string workingDirectory)
    {
        (string Exe, Func<string, string[]> BuildArgs)[] terminals =
        [
            ("gnome-terminal", cmd => ["--working-directory", workingDirectory, "--", "bash", "-c", cmd]),
            ("konsole", cmd => ["--workdir", workingDirectory, "-e", "bash", "-c", cmd]),
            ("xfce4-terminal", cmd => ["--working-directory", workingDirectory, "-e", $"bash -c \"{EscapeDoubleQuotes(cmd)}\""]),
            ("kitty", cmd => ["--directory", workingDirectory, "bash", "-c", cmd]),
            ("alacritty", cmd => ["--working-directory", workingDirectory, "-e", "bash", "-c", cmd]),
            ("wezterm", cmd => ["start", "--cwd", workingDirectory, "--", "bash", "-c", cmd]),
            ("tilix", cmd => ["-w", workingDirectory, "-e", $"bash -c \"{EscapeDoubleQuotes(cmd)}\""]),
            ("mate-terminal", cmd => ["--working-directory", workingDirectory, "-e", $"bash -c \"{EscapeDoubleQuotes(cmd)}\""]),
            ("x-terminal-emulator", cmd => ["-e", "bash", "-c", cmd]),
            ("xterm", cmd => ["-e", "bash", "-c", cmd])
        ];

        foreach (var (exe, buildArgs) in terminals)
        {
            var resolved = PlatformHelper.FindInPath(exe);
            if (resolved is null)
                continue;

            var psi = new ProcessStartInfo
            {
                FileName = resolved,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false
            };

            foreach (var arg in buildArgs(shellCommand))
                psi.ArgumentList.Add(arg);

            Process.Start(psi);
            return true;
        }

        return false;
    }

    private static string NormalizePath(string path) =>
        path.Trim().TrimEnd('\\', '/');

    private static string EscapePs(string input) =>
        input.Replace("'", "''");

    private static string EscapeDoubleQuotes(string input) =>
        input.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string EscapeSingleQuotes(string input) =>
        input.Replace("'", "'\\''");
}
