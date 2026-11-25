using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace BEStart.Services;

public static class PlatformHelper
{
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
