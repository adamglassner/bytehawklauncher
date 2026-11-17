using System.IO.Compression;
using System.Text;
using Bytehawk.Launcher.Models;
using Bytehawk.Launcher.Services;

namespace Bytehawk.Launcher.Desktop;

/// <summary>
/// Installs, launches and removes Bytehawk apps on macOS and Linux.
///   macOS: .zip or .dmg containing an .app → copied to ~/Applications (no password needed).
///   Linux: .AppImage / .tar.gz / .zip → the launcher's apps folder (+ a menu entry);
///          .deb / .rpm → installed with the system package manager (asks for the admin password).
/// Kinds recorded in installed.json: "mac-app", "appimage", "archive", "deb", "rpm".
/// </summary>
public static class AppManager
{
    public static bool IsManagedKind(string kind) => kind is "mac-app" or "appimage" or "archive" or "deb" or "rpm";

    /// <summary>Installs the downloaded file. Returns the install record, or throws with a message for the user.</summary>
    public static async Task<InstalledApp> InstallAsync(CatalogEntry entry, PlatformBuild build, string downloaded, string version,
                                                        string assetName, IProgress<string> status)
    {
        var name = Path.GetFileName(downloaded);
        var lower = name.ToLowerInvariant();
        var record = new InstalledApp { Version = version, InstalledAt = DateTime.Now, AssetName = assetName, DownloadedFile = downloaded };

        if (Platform.IsMac)
        {
            status.Report("Installing…");
            var app = lower.EndsWith(".dmg") ? await InstallFromDmgAsync(downloaded, build.App)
                    : lower.EndsWith(".zip") ? await InstallFromZipAsync(downloaded, build.App)
                    : throw new InvalidOperationException($"Don't know how to install {name} on macOS (expected a .zip or .dmg).");
            record.Kind = "mac-app";
            record.InstallFolder = Path.GetDirectoryName(app);
            record.LaunchPath = app;
            return record;
        }

        // ---------------- Linux ----------------
        var folder = Path.Combine(AppPaths.Apps, Safe(entry.Key));
        if (lower.EndsWith(".appimage"))
        {
            status.Report("Installing…");
            Directory.CreateDirectory(folder);
            foreach (var old in Directory.GetFiles(folder, "*.AppImage")) TryDelete(old);
            var dest = Path.Combine(folder, Safe(entry.Name) + ".AppImage");
            File.Copy(downloaded, dest, overwrite: true);
            Platform.MakeExecutable(dest);
            record.Kind = "appimage";
            record.InstallFolder = folder;
            record.LaunchPath = dest;
        }
        else if (lower.EndsWith(".tar.gz") || lower.EndsWith(".tgz") || lower.EndsWith(".tar.xz") || lower.EndsWith(".zip"))
        {
            status.Report("Extracting…");
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            Directory.CreateDirectory(folder);
            if (lower.EndsWith(".zip")) await Task.Run(() => ZipFile.ExtractToDirectory(downloaded, folder, overwriteFiles: true));
            else
            {
                var r = await Platform.RunAsync("tar", new[] { "-xf", downloaded, "-C", folder });
                if (!r.Ok) throw new InvalidOperationException($"Couldn't extract {name}: {r.Message}");
            }
            var exe = FindLinuxProgram(folder, build.Exe, entry.Name)
                      ?? throw new InvalidOperationException($"Couldn't find the program inside {name}. Set \"exe\" for this app in catalog.json.");
            Platform.MakeExecutable(exe);
            record.Kind = "archive";
            record.InstallFolder = folder;
            record.LaunchPath = exe;
        }
        else if (lower.EndsWith(".deb") || lower.EndsWith(".rpm"))
        {
            status.Report("Waiting for your password…");
            var isDeb = lower.EndsWith(".deb");
            var r = await RunAsAdminAsync(PackageInstallCommand(downloaded, isDeb));
            if (!r.Ok) throw new InvalidOperationException(r.ExitCode is 126 or 127
                ? "Installation was cancelled (the admin password wasn't given)."
                : $"The package manager couldn't install {name}:\n{Tail(r.Message)}");
            record.Kind = isDeb ? "deb" : "rpm";
            record.LaunchPath = build.Exe;
        }
        else throw new InvalidOperationException($"Don't know how to install {name} on Linux.");

        if (record.Kind is "appimage" or "archive")
            TryCreateMenuEntry(entry, record.LaunchPath!);
        return record;
    }

    // ---------------- macOS ----------------

    private static async Task<string> InstallFromZipAsync(string zip, string? appName)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "bytehawk-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        try
        {
            // ditto keeps the .app's permissions, symlinks and code signature (ZipFile doesn't).
            var r = await Platform.RunAsync("/usr/bin/ditto", new[] { "-x", "-k", zip, tmp });
            if (!r.Ok) throw new InvalidOperationException($"Couldn't unzip: {r.Message}");
            return await CopyAppAsync(FindApp(tmp, appName), appName);
        }
        finally { TryDeleteDir(tmp); }
    }

    private static async Task<string> InstallFromDmgAsync(string dmg, string? appName)
    {
        var mount = Path.Combine(Path.GetTempPath(), "bytehawk-dmg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(mount);
        var r = await Platform.RunAsync("/usr/bin/hdiutil", new[] { "attach", "-nobrowse", "-readonly", "-noautoopen", "-mountpoint", mount, dmg });
        if (!r.Ok) throw new InvalidOperationException($"Couldn't open the disk image: {r.Message}");
        try { return await CopyAppAsync(FindApp(mount, appName), appName); }
        finally
        {
            await Platform.RunAsync("/usr/bin/hdiutil", new[] { "detach", mount, "-quiet", "-force" });
            TryDeleteDir(mount);
        }
    }

    private static string FindApp(string root, string? appName)
    {
        if (!string.IsNullOrWhiteSpace(appName))
        {
            var direct = Path.Combine(root, appName);
            if (Directory.Exists(direct)) return direct;
        }
        var apps = Directory.GetDirectories(root, "*.app", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetDirectories(root).Where(d => !d.EndsWith(".app")).SelectMany(d => Directory.GetDirectories(d, "*.app")))
            .ToList();
        return apps.FirstOrDefault(a => appName != null && Path.GetFileName(a).Equals(appName, StringComparison.OrdinalIgnoreCase))
               ?? apps.FirstOrDefault()
               ?? throw new InvalidOperationException("The download doesn't contain a macOS app (.app).");
    }

    private static async Task<string> CopyAppAsync(string sourceApp, string? appName)
    {
        Directory.CreateDirectory(Platform.AppsInstallDir);
        var dest = Path.Combine(Platform.AppsInstallDir, Path.GetFileName(sourceApp));
        if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
        var r = await Platform.RunAsync("/usr/bin/ditto", new[] { sourceApp, dest });
        if (!r.Ok) throw new InvalidOperationException($"Couldn't copy the app to {Platform.AppsInstallDir}: {r.Message}");
        // Downloads made by the launcher aren't quarantined, but clear it anyway in case the app came from a browser download.
        await Platform.RunAsync("/usr/bin/xattr", new[] { "-dr", "com.apple.quarantine", dest });
        return dest;
    }

    // ---------------- Linux helpers ----------------

    private static IEnumerable<string> PackageInstallCommand(string file, bool deb)
    {
        if (deb) return Platform.HasCommand("apt-get")
            ? new[] { "apt-get", "install", "-y", file }
            : new[] { "dpkg", "-i", file };
        if (Platform.HasCommand("dnf")) return new[] { "dnf", "install", "-y", file };
        if (Platform.HasCommand("zypper")) return new[] { "zypper", "--non-interactive", "install", "--allow-unsigned-rpm", file };
        return new[] { "rpm", "-U", "--replacepkgs", file };
    }

    private static IEnumerable<string> PackageRemoveCommand(string package, bool deb)
    {
        if (deb) return Platform.HasCommand("apt-get") ? new[] { "apt-get", "remove", "-y", package } : new[] { "dpkg", "-r", package };
        if (Platform.HasCommand("dnf")) return new[] { "dnf", "remove", "-y", package };
        if (Platform.HasCommand("zypper")) return new[] { "zypper", "--non-interactive", "remove", package };
        return new[] { "rpm", "-e", package };
    }

    /// <summary>Runs a command as root through pkexec, which shows the system's password prompt.</summary>
    public static Task<Platform.RunResult> RunAsAdminAsync(IEnumerable<string> command)
    {
        if (!Platform.HasCommand("pkexec"))
            return Task.FromResult(new Platform.RunResult(-1, "",
                "This needs administrator rights, but pkexec (polkit) isn't installed. Run the command in a terminal with sudo instead:\nsudo " + string.Join(' ', command)));
        return Platform.RunAsync(Platform.Which("pkexec"), command, timeoutMs: 1_800_000);
    }

    /// <summary>The program inside an extracted Linux archive: catalog "exe", an AppRun, or a lone executable named like the app.</summary>
    public static string? FindLinuxProgram(string folder, string? exe, string appName)
    {
        if (!string.IsNullOrWhiteSpace(exe))
        {
            var p = Path.IsPathRooted(exe) ? exe : Path.Combine(folder, exe);
            if (File.Exists(p)) return p;
            var found = Directory.GetFiles(folder, Path.GetFileName(exe), SearchOption.AllDirectories).FirstOrDefault();
            if (found != null) return found;
        }
        var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f).Length == 0 || f.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var key = new string(appName.Where(char.IsLetterOrDigit).ToArray());
        return files.FirstOrDefault(f => Path.GetFileName(f).Equals(key, StringComparison.OrdinalIgnoreCase))
               ?? files.FirstOrDefault(f => Path.GetFileName(f) == "AppRun")
               ?? files.Where(Platform.IsExecutable).OrderBy(f => f.Count(c => c == '/')).FirstOrDefault();
    }

    public static string MenuEntryPath(CatalogEntry entry) =>
        Path.Combine(Platform.Home, ".local", "share", "applications", "bytehawk-" + Safe(entry.Key) + ".desktop");

    /// <summary>Linux: adds the app to the applications menu (~/.local/share/applications), like the Start menu on Windows.</summary>
    public static void TryCreateMenuEntry(CatalogEntry entry, string exe)
    {
        try
        {
            var path = MenuEntryPath(entry);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DesktopFile(entry.Name, entry.Description, exe, IconCache.CatalogIconPath(entry)));
            Platform.MakeExecutable(path);
        }
        catch (Exception ex) { Log.Error($"Menu entry for {entry.Name}", ex); }
    }

    public static string DesktopFile(string name, string? comment, string exe, string? icon, string? args = null, bool terminal = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Desktop Entry]");
        sb.AppendLine("Type=Application");
        sb.AppendLine($"Name={name}");
        if (!string.IsNullOrWhiteSpace(comment)) sb.AppendLine($"Comment={comment.Replace('\n', ' ')}");
        sb.AppendLine($"Exec={QuoteExec(exe)}{(string.IsNullOrWhiteSpace(args) ? "" : " " + args)}");
        if (File.Exists(exe)) sb.AppendLine($"Path={Path.GetDirectoryName(exe)}");
        if (!string.IsNullOrEmpty(icon) && File.Exists(icon)) sb.AppendLine($"Icon={icon}");
        sb.AppendLine($"Terminal={(terminal ? "true" : "false")}");
        sb.AppendLine("Categories=Utility;");
        return sb.ToString();
    }

    private static string QuoteExec(string exe) => exe.Contains(' ') ? $"\"{exe.Replace("\"", "\\\"")}\"" : exe;

    /// <summary>Linux: puts a launcher icon on the desktop and marks it trusted (GNOME asks otherwise).</summary>
    public static string CreateDesktopShortcut(string name, string desktopFileText)
    {
        var path = Path.Combine(Platform.DesktopDir, Safe(name) + ".desktop");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, desktopFileText);
        Platform.MakeExecutable(path);
        if (Platform.HasCommand("gio")) Platform.Run("gio", new[] { "set", path, "metadata::trusted", "true" }, 5000);
        return path;
    }

    // ---------------- launch / running / uninstall ----------------

    public static string? Launch(InstalledApp app)
    {
        var path = app.LaunchPath;
        if (string.IsNullOrEmpty(path)) return "The launcher doesn't know which program to start for this app.";
        if (Platform.IsMac)
            return Directory.Exists(path) || File.Exists(path) ? Platform.Start("/usr/bin/open", new[] { path }) : "The app isn't there anymore.";
        if (!File.Exists(path)) return $"The program isn't there anymore:\n{path}";
        return Platform.Start(path, null, Path.GetDirectoryName(path));
    }

    /// <summary>True if the app's launch file is gone (shows "Not found — reinstall").</summary>
    public static bool IsMissing(InstalledApp app) =>
        string.IsNullOrEmpty(app.LaunchPath) || !(File.Exists(app.LaunchPath) || Directory.Exists(app.LaunchPath));

    public static bool IsRunning(InstalledApp app)
    {
        if (string.IsNullOrEmpty(app.LaunchPath)) return false;
        var probe = Platform.IsMac ? Path.Combine(app.LaunchPath, "Contents", "MacOS") : app.LaunchPath;
        var r = Platform.Run("pgrep", new[] { "-f", probe }, 5000);
        return r.Ok && r.Output.Trim().Length > 0;
    }

    public static async Task UninstallAsync(CatalogEntry entry, InstalledApp app)
    {
        switch (app.Kind)
        {
            case "mac-app":
                if (app.LaunchPath is { } a && Directory.Exists(a)) Directory.Delete(a, recursive: true);
                break;
            case "appimage":
            case "archive":
                if (app.InstallFolder is { } f && Directory.Exists(f)) Directory.Delete(f, recursive: true);
                TryDelete(MenuEntryPath(entry));
                break;
            case "deb":
            case "rpm":
                var package = Platform.BuildFor(entry)?.Package;
                if (string.IsNullOrWhiteSpace(package)) throw new InvalidOperationException("The catalog doesn't name this app's package, so it can't be removed from here.");
                var r = await RunAsAdminAsync(PackageRemoveCommand(package, app.Kind == "deb"));
                if (!r.Ok) throw new InvalidOperationException(r.ExitCode is 126 or 127
                    ? "Uninstall was cancelled (the admin password wasn't given)."
                    : $"The package manager couldn't remove {package}:\n{Tail(r.Message)}");
                break;
        }
        if (app.DownloadedFile is { } d) TryDelete(d);
    }

    // ---------------- small helpers ----------------

    public static string Safe(string name)
    {
        var s = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ' ' ? c : '_').ToArray()).Trim();
        return s.Length == 0 ? "app" : s;
    }

    private static string Tail(string s) => s.Length > 600 ? "…" + s[^600..] : s;
    public static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { /* ignore */ } }
    private static void TryDeleteDir(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { /* ignore */ } }
}
