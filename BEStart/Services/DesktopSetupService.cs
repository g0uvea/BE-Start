using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using BEStart.Models;

namespace BEStart.Services;

public sealed class DesktopSetupService
{
    private const string PathBlockStart = "# >>> BE-Start PATH >>>";
    private const string PathBlockEnd = "# <<< BE-Start PATH <<<";

    private readonly LogService _log;

    public DesktopSetupService(LogService log)
    {
        _log = log;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(
        IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

    public string GetPathStatusSummary(AppSettings settings)
    {
        var activeBins = CollectInstalledServiceBins(settings);
        if (activeBins.Count == 0)
            return $"Nenhum runtime instalado ainda ({PlatformHelper.PlatformDisplayName}).";

        var inPathNames = new List<string>();

        if (PlatformHelper.IsWindows)
        {
            var userPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
            var parts = userPath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(NormalizePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var (name, binDir) in activeBins)
            {
                if (parts.Contains(NormalizePath(binDir)))
                    inPathNames.Add(name);
            }
        }
        else
        {
            var sb = new StringBuilder();
            sb.AppendLine(Environment.GetEnvironmentVariable("PATH") ?? "");
            foreach (var profile in GetUnixShellProfileCandidates(includeOnlyExisting: true))
            {
                try { sb.AppendLine(File.ReadAllText(profile, Encoding.UTF8)); } catch { }
            }
            var haystack = sb.ToString();
            foreach (var (name, binDir) in activeBins)
            {
                if (haystack.Contains(NormalizePath(binDir), StringComparison.Ordinal))
                    inPathNames.Add(name);
            }
        }

        return inPathNames.Count == 0
            ? $"Nenhum runtime está no PATH ({PlatformHelper.PlatformDisplayName})."
            : $"No PATH ({PlatformHelper.PlatformDisplayName}): {string.Join(", ", inPathNames)}.";
    }

    public bool ConfigureUserPath(AppSettings settings) =>
        PlatformHelper.IsWindows
            ? ConfigureWindowsUserPath(settings)
            : ConfigureUnixUserPath(settings);

    private bool ConfigureWindowsUserPath(AppSettings settings)
    {
        try
        {
            var currentUserPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
            var entries = currentUserPath
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            var runtimesPrefix = NormalizePath(settings.RuntimesDirectory);
            entries.RemoveAll(e => NormalizePath(e).StartsWith(runtimesPrefix, StringComparison.OrdinalIgnoreCase));

            var activeBins = CollectInstalledServiceBins(settings);
            if (activeBins.Count == 0)
            {
                _log.Warn("BE-Start", "Nenhum runtime instalado para adicionar ao PATH.");
                return false;
            }

            var added = new List<string>();
            foreach (var (name, binDir) in activeBins)
            {
                entries.Insert(0, binDir);
                added.Add(name);
            }

            var unique = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (seen.Add(NormalizePath(entry)))
                    unique.Add(entry);
            }

            Environment.SetEnvironmentVariable("Path", string.Join(';', unique), EnvironmentVariableTarget.User);

            var processPath = Environment.GetEnvironmentVariable("Path") ?? "";
            Environment.SetEnvironmentVariable("Path", string.Join(';', unique.Take(added.Count)) + ";" + processPath);

            const int HWND_BROADCAST = 0xffff;
            const uint WM_SETTINGCHANGE = 0x001A;
            const uint SMTO_ABORTIFHUNG = 0x0002;
            SendMessageTimeoutW(new IntPtr(HWND_BROADCAST), WM_SETTINGCHANGE, UIntPtr.Zero, "Environment", SMTO_ABORTIFHUNG, 1000, out _);

            _log.Success("BE-Start", $"PATH do usuário atualizado com: {string.Join(", ", added)}.");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("BE-Start", $"Falha ao configurar PATH: {ex.Message}");
            return false;
        }
    }

    private bool ConfigureUnixUserPath(AppSettings settings)
    {
        try
        {
            var activeBins = CollectInstalledServiceBins(settings);
            if (activeBins.Count == 0)
            {
                _log.Warn("BE-Start", "Nenhum runtime instalado para adicionar ao PATH.");
                return false;
            }

            var binDirs = activeBins.Select(x => x.BinDir).Distinct(StringComparer.Ordinal).ToList();
            var added = activeBins.Select(x => x.Name).ToList();

            var joinedUnixPath = string.Join(':', binDirs);
            var posixBlock = $"{PathBlockStart}\nexport PATH=\"{EscapeDoubleQuotes(joinedUnixPath)}:$PATH\"\n{PathBlockEnd}";
            var updatedFiles = new List<string>();

            foreach (var profile in GetUnixShellProfileCandidates(includeOnlyExisting: false))
            {
                UpsertManagedProfileBlock(profile, posixBlock);
                updatedFiles.Add(Path.GetFileName(profile));
            }

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
                    File.WriteAllText(Path.Combine(fishConfD, "bestart.fish"), fishLines.ToString(), PlatformHelper.Utf8NoBom);
                    updatedFiles.Add("conf.d/bestart.fish");
                }
            }

            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            var remaining = currentPath.Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Where(p => !binDirs.Contains(p, StringComparer.Ordinal));
            Environment.SetEnvironmentVariable("PATH", string.Join(':', binDirs.Concat(remaining)));

            _log.Success("BE-Start", $"PATH atualizado em {string.Join(", ", updatedFiles)} com: {string.Join(", ", added)}.");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("BE-Start", $"Falha ao configurar PATH no {PlatformHelper.PlatformDisplayName}: {ex.Message}");
            return false;
        }
    }

    public bool CreateDesktopShortcuts(AppSettings settings)
    {
        if (PlatformHelper.IsWindows) return CreateWindowsDesktopShortcuts(settings);
        if (PlatformHelper.IsLinux) return CreateLinuxDesktopShortcuts(settings);
        if (PlatformHelper.IsMacOS) return CreateMacOsDesktopShortcuts(settings);
        return false;
    }

    private bool CreateWindowsDesktopShortcuts(AppSettings settings)
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop))
            {
                _log.Warn("BE-Start", "Pasta Área de Trabalho não encontrada.");
                return false;
            }

            var exePath = Environment.ProcessPath;
            var appShortcut = Path.Combine(desktop, "BE-Start.lnk");
            var wwwShortcut = Path.Combine(desktop, "Projetos BE-Start (www).lnk");

            var script = new StringBuilder();
            script.AppendLine("$ws = New-Object -ComObject WScript.Shell");

            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                script.AppendLine($"$s1 = $ws.CreateShortcut('{EscapePs(appShortcut)}')");
                script.AppendLine($"$s1.TargetPath = '{EscapePs(exePath)}'");
                script.AppendLine($"$s1.WorkingDirectory = '{EscapePs(Path.GetDirectoryName(exePath) ?? "")}'");
                script.AppendLine("$s1.Description = 'BE-Start - Bancos SQL, NoSQL e PHP'");
                script.AppendLine("$s1.Save()");
            }

            Directory.CreateDirectory(settings.ProjectsDirectory);
            script.AppendLine($"$s2 = $ws.CreateShortcut('{EscapePs(wwwShortcut)}')");
            script.AppendLine($"$s2.TargetPath = '{EscapePs(settings.ProjectsDirectory)}'");
            script.AppendLine("$s2.Description = 'Pasta de projetos PHP do BE-Start'");
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
                _log.Success("BE-Start", "Atalhos criados na Área de Trabalho: 'BE-Start' e 'Projetos BE-Start (www)'.");
                return true;
            }

            _log.Error("BE-Start", $"Não foi possível criar atalhos: {proc?.StandardError.ReadToEnd()}");
            return false;
        }
        catch (Exception ex)
        {
            _log.Error("BE-Start", $"Erro ao criar atalhos: {ex.Message}");
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

            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                return false;

            var appEntry = $"""
[Desktop Entry]
Type=Application
Name=BE-Start
Comment=Ambiente Local SQL, NoSQL e PHP
Exec="{EscapeDoubleQuotes(exePath)}"
Path={Path.GetDirectoryName(exePath)}
Terminal=false
Categories=Development;WebDevelopment;
""";
            var appsDir = Path.Combine(home, ".local", "share", "applications");
            Directory.CreateDirectory(appsDir);
            var menuFile = Path.Combine(appsDir, "bestart.desktop");
            File.WriteAllText(menuFile, appEntry + "\n", PlatformHelper.Utf8NoBom);
            PlatformHelper.EnsureUnixExecutable(menuFile);

            var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!string.IsNullOrWhiteSpace(desktopDir) && Directory.Exists(desktopDir))
            {
                var desktopApp = Path.Combine(desktopDir, "BE-Start.desktop");
                File.WriteAllText(desktopApp, appEntry + "\n", PlatformHelper.Utf8NoBom);
                PlatformHelper.EnsureUnixExecutable(desktopApp);
            }

            _log.Success("BE-Start", "Atalhos Linux (.desktop) criados.");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("BE-Start", $"Erro ao criar atalhos no Linux: {ex.Message}");
            return false;
        }
    }

    private bool CreateMacOsDesktopShortcuts(AppSettings settings)
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop))
                desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop");
            if (!Directory.Exists(desktop))
                return false;

            Directory.CreateDirectory(settings.ProjectsDirectory);
            var wwwLink = Path.Combine(desktop, "Projetos BE-Start (www)");
            if (!File.Exists(wwwLink) && !Directory.Exists(wwwLink))
                Directory.CreateSymbolicLink(wwwLink, settings.ProjectsDirectory);

            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                var launcher = Path.Combine(desktop, "BE-Start.command");
                File.WriteAllText(launcher, $"#!/bin/zsh\nnohup \"{EscapeDoubleQuotes(exePath)}\" >/dev/null 2>&1 &\n", PlatformHelper.Utf8NoBom);
                PlatformHelper.EnsureUnixExecutable(launcher);
            }

            _log.Success("BE-Start", "Atalhos criados no Desktop do macOS.");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("BE-Start", $"Erro ao criar atalhos no macOS: {ex.Message}");
            return false;
        }
    }

    public void OpenConfiguredTerminal(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(settings.ProjectsDirectory);
            var extraPaths = CollectInstalledServiceBins(settings).Select(x => x.BinDir).Distinct().ToList();

            if (PlatformHelper.IsWindows)
            {
                var pathPrefix = extraPaths.Count > 0 ? string.Join(';', extraPaths) + ";" : "";
                var initCmd =
                    $"$env:Path = '{EscapePs(pathPrefix)}' + $env:Path; " +
                    $"Write-Host '=== BE-Start Terminal Pronto ===' -ForegroundColor Cyan; " +
                    $"if (Get-Command php -ErrorAction SilentlyContinue) {{ php -v | Select-Object -First 1 }} " +
                    $"if (Get-Command composer -ErrorAction SilentlyContinue) {{ composer --version }} " +
                    $"Write-Host 'Pasta de projetos: {EscapePs(settings.ProjectsDirectory)}' -ForegroundColor DarkGray";

                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoExit -NoProfile -Command \"{initCmd}\"",
                    WorkingDirectory = settings.ProjectsDirectory,
                    UseShellExecute = true
                });
                _log.Info("BE-Start", "Terminal PowerShell aberto com os runtimes instalados no PATH.");
                return;
            }

            var unixPathPrefix = extraPaths.Count > 0 ? string.Join(':', extraPaths) + ":" : "";
            var unixBanner =
                $"cd '{EscapeSingleQuotes(settings.ProjectsDirectory)}' && " +
                $"export PATH='{EscapeSingleQuotes(unixPathPrefix)}'\"$PATH\" && " +
                $"echo '=== BE-Start Terminal Pronto ===' && " +
                $"(command -v php >/dev/null 2>&1 && php -v | head -n 1 || true) && " +
                $"(command -v composer >/dev/null 2>&1 && composer --version || true)";

            if (PlatformHelper.IsMacOS)
            {
                OpenMacOsTerminalCommand(unixBanner);
                return;
            }

            var userShell = Environment.GetEnvironmentVariable("SHELL");
            if (string.IsNullOrWhiteSpace(userShell) || !File.Exists(userShell))
                userShell = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";

            OpenLinuxTerminalCommand($"{unixBanner}; exec '{EscapeSingleQuotes(userShell)}' -i", settings.ProjectsDirectory);
        }
        catch (Exception ex)
        {
            _log.Error("BE-Start", $"Não foi possível abrir o terminal: {ex.Message}");
        }
    }

    public void OpenServiceCli(ServiceKind kind, AppSettings settings)
    {
        var version = settings.GetActiveVersion(kind);
        if (string.IsNullOrWhiteSpace(version))
            return;

        var dir = settings.GetInstallDirectory(kind, version);
        var port = settings.GetPort(kind);
        var desc = ServiceDescriptor.Get(kind);

        string? exe = null;
        string args = "";

        switch (kind)
        {
            case ServiceKind.PostgreSql:
                exe = InstallerService.FindServiceTool(kind, dir, "psql");
                args = $"-h 127.0.0.1 -p {port} -U postgres";
                break;
            case ServiceKind.MariaDb:
                exe = InstallerService.FindServiceTool(kind, dir, "mariadb", "mysql");
                args = $"--host=127.0.0.1 --port={port} --protocol=TCP -u root";
                break;
            case ServiceKind.MySql:
                exe = InstallerService.FindServiceTool(kind, dir, "mysql");
                args = $"--host=127.0.0.1 --port={port} --protocol=TCP -u root";
                break;
            case ServiceKind.Redis:
                exe = InstallerService.FindServiceTool(kind, dir, "redis-cli");
                args = $"-h 127.0.0.1 -p {port}";
                break;
            case ServiceKind.MongoDb:
                exe = InstallerService.FindServiceTool(kind, dir, "mongosh", "mongo") ?? PlatformHelper.FindInPath("mongosh");
                args = $"mongodb://127.0.0.1:{port}";
                break;
            case ServiceKind.SqlServer:
                exe = PlatformHelper.FindInPath("sqlcmd.exe");
                args = version.Contains("LocalDB", StringComparison.OrdinalIgnoreCase)
                    ? "-S (localdb)\\BEStart"
                    : $"-S 127.0.0.1,{port} -E";
                break;
            case ServiceKind.Cassandra:
                exe = InstallerService.FindServiceTool(kind, dir, "cqlsh");
                args = $"127.0.0.1 {port}";
                break;
        }

        if (string.IsNullOrWhiteSpace(exe))
        {
            _log.Warn(desc.LogSource, $"Cliente CLI de {desc.ShortName} não encontrado.");
            return;
        }

        try
        {
            if (PlatformHelper.IsWindows)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? settings.ProjectsDirectory,
                    UseShellExecute = true
                });
            }
            else if (PlatformHelper.IsMacOS)
            {
                OpenMacOsTerminalCommand($"'{EscapeSingleQuotes(exe)}' {args}");
            }
            else
            {
                OpenLinuxTerminalCommand($"'{EscapeSingleQuotes(exe)}' {args}", settings.ProjectsDirectory);
            }

            _log.Info(desc.LogSource, $"Console CLI aberto em 127.0.0.1:{port}.");
        }
        catch (Exception ex)
        {
            _log.Error(desc.LogSource, $"Não foi possível abrir o CLI: {ex.Message}");
        }
    }

    public void EnsureStarterWorkspace(AppSettings settings)
    {
        Directory.CreateDirectory(settings.ProjectsDirectory);
        var indexPath = Path.Combine(settings.ProjectsDirectory, "index.php");

        if (!File.Exists(indexPath))
        {
            File.WriteAllText(indexPath, StarterTemplates.GetRootDashboardPhp(settings), PlatformHelper.Utf8NoBom);
            _log.Info("BE-Start", $"Página inicial criada em {indexPath}.");
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
            File.WriteAllText(indexPath, StarterTemplates.GetNewProjectIndexPhp(cleanName, settings.GetPort(ServiceKind.MariaDb)), PlatformHelper.Utf8NoBom);

        _log.Success("BE-Start", $"Projeto '{cleanName}' criado em {projectDir}.");

        return new ProjectInfo
        {
            Name = cleanName,
            FullPath = projectDir,
            Url = $"http://{settings.PhpHost}:{settings.GetPort(ServiceKind.Php)}/{Uri.EscapeDataString(cleanName)}/",
            LastModified = DateTime.Now,
            HasIndexPhp = true
        };
    }

    public IReadOnlyList<ProjectInfo> ScanProjects(AppSettings settings)
    {
        var list = new List<ProjectInfo>();
        if (!Directory.Exists(settings.ProjectsDirectory))
            return list;

        var phpPort = settings.GetPort(ServiceKind.Php);
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
                Url = $"http://{settings.PhpHost}:{phpPort}/{Uri.EscapeDataString(name)}/",
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
                Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
                return;
            }
            if (PlatformHelper.IsMacOS)
            {
                var psi = new ProcessStartInfo { FileName = "/usr/bin/open", UseShellExecute = false };
                psi.ArgumentList.Add(target);
                Process.Start(psi);
                return;
            }
            var linuxPsi = new ProcessStartInfo { FileName = "xdg-open", UseShellExecute = false };
            linuxPsi.ArgumentList.Add(target);
            Process.Start(linuxPsi);
        }
        catch (Exception ex)
        {
            _log.Error("BE-Start", $"Não foi possível abrir '{target}': {ex.Message}");
        }
    }

    private static List<(string Name, string BinDir)> CollectInstalledServiceBins(AppSettings settings)
    {
        var list = new List<(string Name, string BinDir)>();
        foreach (var desc in ServiceDescriptor.All)
        {
            var ver = settings.GetActiveVersion(desc.Kind);
            if (string.IsNullOrWhiteSpace(ver))
                continue;

            var dir = settings.GetInstallDirectory(desc.Kind, ver);
            var bin = InstallerService.GetServiceBinDirectory(desc.Kind, dir);
            if (!string.IsNullOrWhiteSpace(bin) && Directory.Exists(bin))
                list.Add((desc.ShortName, bin));
        }
        return list;
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
            if (!includeOnlyExisting || File.Exists(zshrc)) profiles.Add(zshrc);
            foreach (var extra in new[] { ".bash_profile", ".bashrc", ".zprofile" })
            {
                var path = Path.Combine(home, extra);
                if (File.Exists(path)) profiles.Add(path);
            }
        }
        else
        {
            foreach (var file in new[] { ".bashrc", ".profile" })
            {
                var path = Path.Combine(home, file);
                if (!includeOnlyExisting || File.Exists(path)) profiles.Add(path);
            }
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
        var updated = Regex.IsMatch(existing, pattern)
            ? Regex.Replace(existing, pattern, block)
            : (string.IsNullOrEmpty(existing) ? block + "\n" : existing + (existing.EndsWith('\n') ? "\n" : "\n\n") + block + "\n");

        File.WriteAllText(profilePath, updated, PlatformHelper.Utf8NoBom);
    }

    private static void OpenMacOsTerminalCommand(string shellCommand)
    {
        var escaped = shellCommand.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var psi = new ProcessStartInfo { FileName = "/usr/bin/osascript", UseShellExecute = false };
        psi.ArgumentList.Add("-e"); psi.ArgumentList.Add("tell application \"Terminal\"");
        psi.ArgumentList.Add("-e"); psi.ArgumentList.Add("activate");
        psi.ArgumentList.Add("-e"); psi.ArgumentList.Add($"do script \"{escaped}\"");
        psi.ArgumentList.Add("-e"); psi.ArgumentList.Add("end tell");
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
            ("x-terminal-emulator", cmd => ["-e", "bash", "-c", cmd]),
            ("xterm", cmd => ["-e", "bash", "-c", cmd])
        ];

        foreach (var (exe, buildArgs) in terminals)
        {
            var resolved = PlatformHelper.FindInPath(exe);
            if (resolved is null) continue;
            var psi = new ProcessStartInfo { FileName = resolved, WorkingDirectory = workingDirectory, UseShellExecute = false };
            foreach (var arg in buildArgs(shellCommand)) psi.ArgumentList.Add(arg);
            Process.Start(psi);
            return true;
        }
        return false;
    }

    private static string NormalizePath(string path) => path.Trim().TrimEnd('\\', '/');
    private static string EscapePs(string input) => input.Replace("'", "''");
    private static string EscapeDoubleQuotes(string input) => input.Replace("\\", "\\\\").Replace("\"", "\\\"");
    private static string EscapeSingleQuotes(string input) => input.Replace("'", "'\\''");
}
