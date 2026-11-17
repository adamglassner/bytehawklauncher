using System.Diagnostics;
using Bytehawk.Launcher.Models;
using Bytehawk.Launcher.Services;

namespace Bytehawk.Launcher.Desktop;

/// <summary>
/// Updates the macOS/Linux launcher from GitHub Releases on adamglassner/bytehawklauncher tagged "desktop-v…"
/// (separate from the Windows launcher's releases, which stay the repo's "latest").
///   macOS: BytehawkStudioLauncher-&lt;v&gt;-macOS-&lt;arm64|x64&gt;.zip → replaces the running .app, then reopens it.
///   Linux: BytehawkStudioLauncher-&lt;v&gt;-linux-x64.AppImage (when running as an AppImage) or the .tar.gz.
/// </summary>
public static class SelfUpdater
{
    public const string Repo = "adamglassner/bytehawklauncher";
    public const string TagPrefix = "desktop-v";

    public static string AssetPattern =>
        Platform.IsMac ? $"BytehawkStudioLauncher-*-macOS-{Platform.Arch}.zip"
        : IsAppImage ? $"BytehawkStudioLauncher-*-linux-{Platform.Arch}.AppImage"
        : $"BytehawkStudioLauncher-*-linux-{Platform.Arch}.tar.gz";

    public static bool IsAppImage => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPIMAGE"));

    public static string VersionOf(CachedRelease r) => r.Tag.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase) ? r.Tag[TagPrefix.Length..] : GitHubService.DisplayVersion(r.Tag);

    public static bool IsNewer(CachedRelease? r) =>
        r is { NoRelease: false } && GitHubService.CompareVersions(VersionOf(r), App.VersionText) > 0 && Asset(r) != null;

    public static ReleaseAsset? Asset(CachedRelease r) => GitHubService.MatchAsset(r.Assets, AssetPattern);

    /// <summary>The running .app bundle on macOS (…/Bytehawk Studio Launcher.app), or null when not run from a bundle.</summary>
    public static string? MacBundle()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('/'));
        // <bundle>.app/Contents/MacOS
        var bundle = dir.Parent?.Parent;
        return bundle != null && bundle.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? bundle.FullName : null;
    }

    /// <summary>Installs the downloaded update and restarts the launcher. Returns an error message, or null when the launcher should now quit.</summary>
    public static async Task<string?> InstallAndRestartAsync(string downloaded)
    {
        var pid = Environment.ProcessId;
        var work = Path.Combine(Path.GetTempPath(), "bytehawk-update-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        string script;

        if (Platform.IsMac)
        {
            var bundle = MacBundle();
            if (bundle == null) return "The launcher isn't running from an .app bundle, so it can't replace itself. Open the downloaded file instead:\n" + downloaded;
            var r = await Platform.RunAsync("/usr/bin/ditto", new[] { "-x", "-k", downloaded, work });
            if (!r.Ok) return "Couldn't unzip the update: " + r.Message;
            var newApp = Directory.GetDirectories(work, "*.app").FirstOrDefault();
            if (newApp == null) return "The update doesn't contain the launcher app.";
            script = $"""
                #!/bin/bash
                while kill -0 {pid} 2>/dev/null; do sleep 0.3; done
                rm -rf {Q(bundle)}
                /usr/bin/ditto {Q(newApp)} {Q(bundle)}
                /usr/bin/xattr -dr com.apple.quarantine {Q(bundle)} 2>/dev/null
                /usr/bin/open {Q(bundle)}
                rm -rf {Q(work)}
                """;
        }
        else if (IsAppImage)
        {
            var target = Environment.GetEnvironmentVariable("APPIMAGE")!;
            script = $"""
                #!/bin/bash
                while kill -0 {pid} 2>/dev/null; do sleep 0.3; done
                cp -f {Q(downloaded)} {Q(target)}.new && mv -f {Q(target)}.new {Q(target)}
                chmod +x {Q(target)}
                nohup {Q(target)} >/dev/null 2>&1 &
                rm -rf {Q(work)}
                """;
        }
        else
        {
            var dir = AppContext.BaseDirectory.TrimEnd('/');
            var r = await Platform.RunAsync("tar", new[] { "-xzf", downloaded, "-C", work });
            if (!r.Ok) return "Couldn't extract the update: " + r.Message;
            // The archive holds one folder (BytehawkStudioLauncher/) or the files directly.
            var src = Directory.GetDirectories(work).Length == 1 && Directory.GetFiles(work).Length == 0 ? Directory.GetDirectories(work)[0] : work;
            var exe = Path.Combine(dir, "BytehawkStudioLauncher");
            script = $"""
                #!/bin/bash
                while kill -0 {pid} 2>/dev/null; do sleep 0.3; done
                cp -rf {Q(src)}/. {Q(dir)}/
                chmod +x {Q(exe)}
                nohup {Q(exe)} >/dev/null 2>&1 &
                rm -rf {Q(work)}
                """;
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), $"bytehawk-update-{pid}.sh");
        await File.WriteAllTextAsync(scriptPath, script.Replace("\r\n", "\n"));
        Platform.MakeExecutable(scriptPath);
        try
        {
            var psi = new ProcessStartInfo("/bin/bash") { UseShellExecute = false };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add($"nohup {Q(scriptPath)} >/dev/null 2>&1 &");
            Process.Start(psi)?.WaitForExit(5000);
            Log.Info($"Launcher update started from {downloaded}");
            return null;
        }
        catch (Exception ex) { return "Couldn't start the update: " + ex.Message; }
    }

    /// <summary>Single-quotes a path for bash.</summary>
    private static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";
}
