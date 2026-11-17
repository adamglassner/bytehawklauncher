using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Bytehawk.Launcher.Models;
using Bytehawk.Launcher.Services;

namespace Bytehawk.Launcher.Desktop;

/// <summary>Which system the launcher runs on, and small helpers for running system tools.</summary>
public static class Platform
{
    public static bool IsMac => OperatingSystem.IsMacOS();
    public static bool IsLinux => OperatingSystem.IsLinux();

    /// <summary>"macOS" or "Linux" (for messages like "Coming soon to macOS").</summary>
    public static string Name => IsMac ? "macOS" : "Linux";

    /// <summary>"arm64" or "x64", used for "{arch}" in asset patterns.</summary>
    public static string Arch => RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";

    public static string ArchLabel => IsMac ? (Arch == "arm64" ? "Apple Silicon" : "Intel") : (Arch == "arm64" ? "ARM64" : "x64");

    /// <summary>This system's build of a catalog app, or null if it has none (shown as "Coming soon").</summary>
    public static PlatformBuild? BuildFor(CatalogEntry e) => IsMac ? e.Macos : e.Linux;

    public static string Pattern(string pattern) => pattern.Replace("{arch}", Arch, StringComparison.OrdinalIgnoreCase);

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>macOS: ~/Applications (no admin password needed). Linux: the launcher's apps folder.</summary>
    public static string AppsInstallDir => IsMac ? Path.Combine(Home, "Applications") : AppPaths.Apps;

    public static string ServersDir => Path.Combine(AppPaths.Root, "servers");

    /// <summary>The user's Desktop folder (XDG on Linux).</summary>
    public static string DesktopDir
    {
        get
        {
            if (IsLinux)
            {
                var r = Run("xdg-user-dir", "DESKTOP", timeoutMs: 3000);
                var p = r.Output.Trim();
                if (r.ExitCode == 0 && p.Length > 0 && Directory.Exists(p)) return p;
            }
            return Path.Combine(Home, "Desktop");
        }
    }

    public sealed record RunResult(int ExitCode, string Output, string Error)
    {
        public bool Ok => ExitCode == 0;
        public string Message => (Error.Trim().Length > 0 ? Error : Output).Trim();
    }

    /// <summary>Runs a program and waits (arguments passed one by one, so paths with spaces are safe).</summary>
    public static RunResult Run(string file, IEnumerable<string> args, int timeoutMs = 120_000, string? workingDir = null)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (workingDir != null) psi.WorkingDirectory = workingDir;
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (stdout) stdout.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { /* ignore */ }
                return new RunResult(-1, stdout.ToString(), $"{Path.GetFileName(file)} took too long and was stopped.");
            }
            p.WaitForExit();
            return new RunResult(p.ExitCode, stdout.ToString(), stderr.ToString());
        }
        catch (Exception ex)
        {
            return new RunResult(-1, "", $"Couldn't run {Path.GetFileName(file)}: {ex.Message}");
        }
    }

    public static RunResult Run(string file, string arg, int timeoutMs = 120_000) => Run(file, new[] { arg }, timeoutMs);

    public static Task<RunResult> RunAsync(string file, IEnumerable<string> args, int timeoutMs = 600_000, string? workingDir = null) =>
        Task.Run(() => Run(file, args.ToList(), timeoutMs, workingDir));

    /// <summary>True if a command exists on PATH (or as an absolute path).</summary>
    public static bool HasCommand(string name)
    {
        if (Path.IsPathRooted(name)) return File.Exists(name);
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries)
                     .Concat(new[] { "/usr/local/bin", "/opt/homebrew/bin", "/usr/bin", "/bin", "/usr/sbin", "/sbin" }))
            if (File.Exists(Path.Combine(dir, name))) return true;
        return false;
    }

    /// <summary>Finds a command on PATH (plus Homebrew/Docker Desktop locations); returns its full path or the bare name.</summary>
    public static string Which(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries)
                     .Concat(new[] { "/usr/local/bin", "/opt/homebrew/bin", "/Applications/Docker.app/Contents/Resources/bin", "/usr/bin", "/bin" }))
        {
            var f = Path.Combine(dir, name);
            if (File.Exists(f)) return f;
        }
        return name;
    }

    /// <summary>Starts a program without waiting. Returns an error message or null.</summary>
    public static string? Start(string file, IEnumerable<string>? args = null, string? workingDir = null)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false };
            if (workingDir != null) psi.WorkingDirectory = workingDir;
            if (args != null) foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi);
            Log.Info($"Started {file} {string.Join(' ', args ?? Array.Empty<string>())}");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error($"Start {file}", ex);
            return $"Couldn't start {Path.GetFileName(file)}:\n{ex.Message}";
        }
    }

    /// <summary>Opens a URL, file or folder with the system's default handler.</summary>
    public static string? Open(string target) =>
        IsMac ? Start("/usr/bin/open", new[] { target }) : Start(Which("xdg-open"), new[] { target });

    /// <summary>Shows a file in Finder / the file manager.</summary>
    public static string? Reveal(string path)
    {
        if (IsMac) return Start("/usr/bin/open", new[] { "-R", path });
        var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
        return Open(dir);
    }

    /// <summary>Splits a launch-arguments string like a shell would (quotes group words).</summary>
    public static List<string> SplitArgs(string? s)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(s)) return list;
        var cur = new StringBuilder();
        char? quote = null;
        foreach (var c in s)
        {
            if (quote != null) { if (c == quote) quote = null; else cur.Append(c); }
            else if (c is '"' or '\'') quote = c;
            else if (char.IsWhiteSpace(c)) { if (cur.Length > 0) { list.Add(cur.ToString()); cur.Clear(); } }
            else cur.Append(c);
        }
        if (cur.Length > 0) list.Add(cur.ToString());
        return list;
    }

    /// <summary>chmod +x</summary>
    public static void MakeExecutable(string file)
    {
        try
        {
            var mode = File.GetUnixFileMode(file);
            File.SetUnixFileMode(file, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute
                                          | UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        catch (Exception ex) { Log.Error($"chmod {file}", ex); }
    }

    public static bool IsExecutable(string file)
    {
        try { return (File.GetUnixFileMode(file) & UnixFileMode.UserExecute) != 0; }
        catch { return false; }
    }
}
