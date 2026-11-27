using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace BEStart.Services;

public static class PlatformHelper
{
    private static readonly ConcurrentDictionary<string, string> WindowsSafeDirCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// UTF-8 encoding WITHOUT Byte Order Mark (BOM).
    /// Critical for my.ini/my.cnf, postgresql.conf, php.ini, and generated .php files.
    /// </summary>
    public static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsLinux => OperatingSystem.IsLinux();
    public static bool IsMacOS => OperatingSystem.IsMacOS();
    public static bool IsUnix => IsLinux || IsMacOS;
    public static bool IsArm64 => RuntimeInformation.OSArchitecture == Architecture.Arm64;

    public static string PlatformDisplayName =>
        IsWindows ? "Windows" :
        IsMacOS ? "macOS" :
        IsLinux ? "Linux" : "Desktop";

    public static string ArchitectureDisplayName =>
        IsMacOS
            ? (IsArm64 ? "Apple Silicon (arm64)" : "Intel (x64)")
            : (IsArm64 ? "ARM64" : "x64");

    public static string PlatformKey =>
        $"{(IsWindows ? "win" : IsMacOS ? "osx" : "linux")}-{(IsArm64 ? "arm64" : "x64")}";

    public static string ExecutableExtension => IsWindows ? ".exe" : string.Empty;

    public static string MariaDbConfigFileName => IsWindows ? "my.ini" : "my.cnf";

    public static string GetDefaultBaseDirectory()
    {
        if (IsWindows)
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
                return Path.Combine(localAppData, "BEStart");
        }
        else if (IsMacOS)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
                return Path.Combine(home, "Library", "Application Support", "BEStart");
        }
        else if (IsLinux)
        {
            var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrWhiteSpace(xdgData))
                return Path.Combine(xdgData, "BEStart");

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
                return Path.Combine(home, ".local", "share", "BEStart");
        }

        return Path.Combine(AppContext.BaseDirectory, "BEStartData");
    }

    public static bool IsAsciiWithoutSpaces(string path)
    {
        if (string.IsNullOrEmpty(path))
            return true;

        foreach (var ch in path)
        {
            if (ch > 127 || ch == ' ')
                return false;
        }
        return true;
    }

    /// <summary>
    /// On Windows, native database engines (PostgreSQL initdb, MySQL, Redis/MSYS2, MongoDB, Cassandra, PHP/cmd.exe)
    /// often fail when paths contain non-ASCII characters (e.g. "João") or spaces.
    /// This maps such directories through a transparent NTFS Directory Junction under an ASCII-only root
    /// (requiring no Administrator privileges) so all files remain in place while tools see a clean ASCII path.
    /// </summary>
    public static string EnsureWindowsSafePath(string path, string? preferredAnchorDir = null, bool isDirectory = false)
    {
        if (!IsWindows || string.IsNullOrWhiteSpace(path) || IsAsciiWithoutSpaces(path))
            return path;

        var fullPath = Path.GetFullPath(path);

        if (!string.IsNullOrWhiteSpace(preferredAnchorDir))
        {
            var fullAnchor = Path.GetFullPath(preferredAnchorDir).TrimEnd('\\', '/');
            if (IsSubPathOf(fullPath, fullAnchor))
            {
                var rel = Path.GetRelativePath(fullAnchor, fullPath);
                if (rel == ".")
                    return EnsureWindowsSafeDirectory(fullAnchor);

                if (IsAsciiWithoutSpaces(rel))
                {
                    var safeAnchor = EnsureWindowsSafeDirectory(fullAnchor);
                    return Path.Combine(safeAnchor, rel);
                }
            }
        }

        if (isDirectory || Directory.Exists(fullPath))
            return EnsureWindowsSafeDirectory(fullPath);

        var parentDir = Path.GetDirectoryName(fullPath);
        var fileName = Path.GetFileName(fullPath);
        if (!string.IsNullOrWhiteSpace(parentDir) && !string.IsNullOrWhiteSpace(fileName))
            return Path.Combine(EnsureWindowsSafeDirectory(parentDir), fileName);

        return fullPath;
    }

    public static string EnsureWindowsSafeDirectory(string dirPath)
    {
        if (!IsWindows || string.IsNullOrWhiteSpace(dirPath) || IsAsciiWithoutSpaces(dirPath))
            return dirPath;

        var fullDir = Path.GetFullPath(dirPath).TrimEnd('\\', '/');
        if (WindowsSafeDirCache.TryGetValue(fullDir, out var cached) && Directory.Exists(cached))
            return cached;

        try
        {
            Directory.CreateDirectory(fullDir);
            var linkRoot = GetWindowsAsciiLinkRoot();
            Directory.CreateDirectory(linkRoot);

            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(fullDir.ToLowerInvariant()));
            var hash = Convert.ToHexString(hashBytes)[..10].ToLowerInvariant();
            var linkPath = Path.Combine(linkRoot, $"d_{hash}");

            if (Directory.Exists(linkPath))
            {
                var info = new DirectoryInfo(linkPath);
                var existingTarget = info.LinkTarget?.TrimEnd('\\', '/');
                if (existingTarget is not null &&
                    existingTarget.StartsWith(@"\\?\", StringComparison.Ordinal))
                {
                    existingTarget = existingTarget[4..];
                }

                if (string.Equals(existingTarget, fullDir, StringComparison.OrdinalIgnoreCase))
                {
                    WindowsSafeDirCache[fullDir] = linkPath;
                    return linkPath;
                }

                // Stale junction pointing elsewhere — remove only the junction reparse point
                RemoveWindowsJunction(linkPath);
            }

            if (CreateWindowsJunction(linkPath, fullDir) && Directory.Exists(linkPath))
            {
                WindowsSafeDirCache[fullDir] = linkPath;
                return linkPath;
            }
        }
        catch
        {
            // Fall back to original path if junction creation fails
        }

        return fullDir;
    }

    private static string GetWindowsAsciiLinkRoot()
    {
        var publicDir = Environment.GetEnvironmentVariable("PUBLIC");
        if (!string.IsNullOrWhiteSpace(publicDir) && IsAsciiWithoutSpaces(publicDir) && Directory.Exists(publicDir))
            return Path.Combine(publicDir, "BEStartLinks");

        var sysDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        return Path.Combine(sysDrive, "BEStartLinks");
    }

    private static bool IsSubPathOf(string candidatePath, string baseDir)
    {
        var normalizedCandidate = candidatePath.TrimEnd('\\', '/');
        if (string.Equals(normalizedCandidate, baseDir, StringComparison.OrdinalIgnoreCase))
            return true;

        return normalizedCandidate.StartsWith(baseDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               normalizedCandidate.StartsWith(baseDir + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool CreateWindowsJunction(string linkPath, string targetDir)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{linkPath}\" \"{targetDir}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.WaitForExit(5000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void RemoveWindowsJunction(string linkPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c rmdir \"{linkPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(3000);
        }
        catch { }
    }

    public static string ResolveMySqlStyleConfigPath(string dataDir)
    {
        if (IsWindows)
            return Path.Combine(dataDir, "my.ini");

        var cnf = Path.Combine(dataDir, "my.cnf");
        if (File.Exists(cnf))
            return cnf;

        var ini = Path.Combine(dataDir, "my.ini");
        return File.Exists(ini) ? ini : cnf;
    }

    public static string? FindHomebrewExecutable()
    {
        if (IsWindows)
            return null;

        string[] candidates =
        [
            "/opt/homebrew/bin/brew",
            "/usr/local/bin/brew",
            "/home/linuxbrew/.linuxbrew/bin/brew"
        ];

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        return FindInPath("brew");
    }

    public static string? FindJavaExecutable(string? localJreRoot = null)
    {
        var javaExeName = IsWindows ? "java.exe" : "java";

        if (!string.IsNullOrWhiteSpace(localJreRoot) && Directory.Exists(localJreRoot))
        {
            foreach (var dir in Directory.GetDirectories(localJreRoot))
            {
                var candidate = Path.Combine(dir, "bin", javaExeName);
                if (File.Exists(candidate))
                    return candidate;

                var macCandidate = Path.Combine(dir, "Contents", "Home", "bin", javaExeName);
                if (File.Exists(macCandidate))
                    return macCandidate;
            }
        }

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            var candidate = Path.Combine(javaHome, "bin", javaExeName);
            if (File.Exists(candidate))
                return candidate;
        }

        return FindInPath(javaExeName);
    }

    public static string? FindInPath(string command)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
            return null;

        var sep = IsWindows ? ';' : ':';
        foreach (var dir in pathEnv.Split(sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(dir, command);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    public static void EnsureUnixExecutable(string filePath)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(filePath))
            return;

        try
        {
            var mode = File.GetUnixFileMode(filePath);
            mode |= UnixFileMode.UserRead | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(filePath, mode);
        }
        catch
        {
            // Ignore on filesystems without Unix permissions.
        }
    }
}
