using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Bytehawk.Launcher.Models;

namespace Bytehawk.Launcher.Services;

/// <summary>
/// Installs Bytehawk apps from a downloaded GitHub release file, with no questions asked:
///   - a .zip is unpacked into apps\&lt;app&gt;\&lt;version&gt; (a single top-level folder in the zip is skipped),
///   - an Inno Setup or NSIS setup program is run silently into apps\&lt;app&gt;\app.
/// The program to launch is found automatically (the catalog's "exe", otherwise the only .exe).
/// Updating is the same as installing the newer release; older version folders are then removed.
/// </summary>
public static class AppInstaller
{
    /// <summary>Install kinds the launcher manages itself (silent install, auto-update, uninstall).</summary>
    public static bool IsManagedKind(string? kind) => kind is "zip" or "inno" or "nsis";

    /// <summary>%APPDATA%\BytehawkStudio\Launcher\apps\&lt;key&gt; — everything the launcher installs for one app.</summary>
    public static string AppFolder(string key) => Path.Combine(AppPaths.Apps, SafeName(key));

    // ------------------------------------------------------------------ download check

    /// <summary>
    /// Checks a downloaded release file against what GitHub says it should be: the exact size and,
    /// on releases that publish one, the SHA-256 digest. Returns null when it's fine, otherwise the reason.
    /// </summary>
    public static async Task<string?> VerifyDownloadAsync(string path, ReleaseAsset asset, CancellationToken ct = default)
    {
        var length = new FileInfo(path).Length;
        if (asset.Size > 0 && length != asset.Size)
            return $"the download is incomplete ({length:N0} of {asset.Size:N0} bytes)";
        if (asset.Digest is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            await using var fs = File.OpenRead(path);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!hash.Equals(digest["sha256:".Length..], StringComparison.OrdinalIgnoreCase))
                return "the downloaded file is damaged (checksum mismatch)";
        }
        return null;
    }

    // ------------------------------------------------------------------ detection

    private static readonly byte[] InnoMarker = Encoding.ASCII.GetBytes("Inno Setup");
    private static readonly byte[] NsisMarker = Encoding.ASCII.GetBytes("Nullsoft");

    /// <summary>"inno", "nsis", or null when the .exe isn't a setup program the launcher can run silently.</summary>
    public static string? DetectSetupKind(string exePath)
    {
        try
        {
            using var fs = File.OpenRead(exePath);
            var buffer = new byte[1 << 20];
            int carry = 0;
            int read;
            // Markers sit in the first few MB of the stub; scan up to 16 MB.
            for (long total = 0; total < 16L << 20 && (read = fs.Read(buffer, carry, buffer.Length - carry)) > 0; total += read)
            {
                var span = buffer.AsSpan(0, carry + read);
                if (span.IndexOf(InnoMarker) >= 0) return "inno";
                if (span.IndexOf(NsisMarker) >= 0) return "nsis";
                // keep a small overlap so a marker split across reads is still found
                carry = Math.Min(16, span.Length);
                span[^carry..].CopyTo(buffer);
            }
        }
        catch (Exception ex) { Log.Error($"DetectSetupKind {exePath}", ex); }
        return null;
    }

    // ------------------------------------------------------------------ zip

    public static InstalledApp InstallZip(string zipPath, string baseFolder, string version, string? exeName)
    {
        Directory.CreateDirectory(baseFolder);
        var dest = Path.Combine(baseFolder, SafeName(version));
        var temp = dest + ".partial";
        if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        Directory.CreateDirectory(temp);
        var tempRoot = Path.GetFullPath(temp) + Path.DirectorySeparatorChar;

        try
        {
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                // Many release zips hold one top folder (MyApp/...); unpack its contents instead.
                string? prefix = null;
                foreach (var e in zip.Entries)
                {
                    var name = e.FullName.Replace('\\', '/').TrimStart('/');
                    var slash = name.IndexOf('/');
                    if (slash < 0) { prefix = null; break; }
                    var top = name[..(slash + 1)];
                    if (prefix == null) prefix = top;
                    else if (prefix != top) { prefix = null; break; }
                }

                foreach (var e in zip.Entries)
                {
                    var rel = e.FullName.Replace('\\', '/').TrimStart('/');
                    if (prefix != null && rel.StartsWith(prefix, StringComparison.Ordinal)) rel = rel[prefix.Length..];
                    if (rel.Length == 0) continue;
                    var target = Path.GetFullPath(Path.Combine(temp, rel.Replace('/', Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The zip contains an unsafe path.");
                    if (rel.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    e.ExtractToFile(target, overwrite: true);
                }
            }

            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
            Directory.Move(temp, dest);
        }
        catch
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* ignore */ }
            throw;
        }

        RemoveOtherVersions(baseFolder, keep: Path.GetFileName(dest));
        var exe = FindExe(dest, exeName);
        return new InstalledApp
        {
            Version = version,
            Kind = "zip",
            InstallFolder = dest,
            LaunchPath = exe == null ? null : Path.Combine(dest, exe),
            InstalledAt = DateTime.Now,
        };
    }

    // ------------------------------------------------------------------ setup programs

    public static async Task<InstalledApp> InstallSetupAsync(string setupPath, string kind, string baseFolder, string version, string? exeName)
    {
        var dir = Path.Combine(baseFolder, "app");
        Directory.CreateDirectory(dir);
        var args = kind == "inno"
            ? $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /NOICONS /DIR=\"{dir}\""
            : $"/S /D={dir}"; // NSIS: /D must be last and unquoted

        Log.Info($"Running {setupPath} {args}");
        int code = await RunAndWaitAsync(setupPath, args);
        if (code != 0) throw new InvalidOperationException($"the setup program stopped with code {code}");

        RemoveOtherVersions(baseFolder, keep: "app");
        var exe = FindExe(dir, exeName);
        return new InstalledApp
        {
            Version = version,
            Kind = kind,
            InstallFolder = dir,
            LaunchPath = exe == null ? null : Path.Combine(dir, exe),
            InstalledAt = DateTime.Now,
        };
    }

    // ------------------------------------------------------------------ uninstall / running

    /// <summary>Runs the app's own uninstaller (setup-program installs), then deletes its folder.</summary>
    public static async Task UninstallAsync(InstalledApp record, string baseFolder)
    {
        if (record.Kind is "inno" or "nsis" && record.InstallFolder is { } dir && Directory.Exists(dir))
        {
            var uninstaller = Directory.GetFiles(dir, "*.exe")
                .FirstOrDefault(f => Path.GetFileName(f).StartsWith("unins", StringComparison.OrdinalIgnoreCase));
            if (uninstaller != null)
            {
                var args = record.Kind == "inno" ? "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART" : $"/S _?={dir}";
                try { await RunAndWaitAsync(uninstaller, args); }
                catch (Exception ex) { Log.Error("Uninstaller", ex); }
            }
        }

        // An uninstaller may still be letting go of files.
        Exception? last = null;
        for (int i = 0; i < 10; i++)
        {
            try
            {
                if (Directory.Exists(baseFolder)) Directory.Delete(baseFolder, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                await Task.Delay(500);
            }
        }
        throw last!;
    }

    /// <summary>True if a program from this install is running (updates wait until it's closed).</summary>
    public static bool IsRunning(InstalledApp record)
    {
        var folder = record.InstallFolder ?? (record.LaunchPath is { } lp ? Path.GetDirectoryName(lp) : null);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return false;
        folder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        var names = Directory.EnumerateFiles(folder, "*.exe", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    if (path != null && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { /* access denied: not ours */ }
                finally { p.Dispose(); }
            }
        }
        return false;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// The app's program relative to <paramref name="dir"/>: the catalog's exe name if present (anywhere),
    /// otherwise the only .exe at the top level, otherwise the only .exe anywhere. Uninstallers are ignored.
    /// </summary>
    public static string? FindExe(string dir, string? want)
    {
        if (!Directory.Exists(dir)) return null;
        var all = Directory.GetFiles(dir, "*.exe", SearchOption.AllDirectories)
            .Where(f => !IsUninstaller(Path.GetFileName(f)))
            .ToList();

        if (!string.IsNullOrWhiteSpace(want))
        {
            var hit = all.Where(f => Path.GetFileName(f).Equals(want, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(f => f.Length).FirstOrDefault();
            if (hit != null) return Path.GetRelativePath(dir, hit);
        }

        var top = all.Where(f => string.Equals(Path.GetDirectoryName(f), dir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)).ToList();
        if (top.Count == 1) return Path.GetFileName(top[0]);
        if (all.Count == 1) return Path.GetRelativePath(dir, all[0]);
        return null;
    }

    private static bool IsUninstaller(string fileName) =>
        fileName.StartsWith("unins", StringComparison.OrdinalIgnoreCase) ||
        fileName.StartsWith("uninstall", StringComparison.OrdinalIgnoreCase);

    private static void RemoveOtherVersions(string baseFolder, string keep)
    {
        foreach (var d in Directory.GetDirectories(baseFolder))
        {
            var name = Path.GetFileName(d);
            if (name.Equals(keep, StringComparison.OrdinalIgnoreCase) || name.Equals("app", StringComparison.OrdinalIgnoreCase)) continue;
            try { Directory.Delete(d, recursive: true); }
            catch (Exception ex) { Log.Error($"Remove old version {d}", ex); }
        }
    }

    private static async Task<int> RunAndWaitAsync(string file, string args)
    {
        // UseShellExecute lets Windows show a UAC prompt if a setup program insists on admin rights.
        using var p = Process.Start(new ProcessStartInfo(file, args)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(file) ?? "",
        }) ?? throw new InvalidOperationException("the setup program didn't start");
        await p.WaitForExitAsync();
        return p.ExitCode;
    }

    public static string SafeName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(s.Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray()).Trim('.', '_');
        return cleaned.Length == 0 ? "app" : cleaned;
    }
}
