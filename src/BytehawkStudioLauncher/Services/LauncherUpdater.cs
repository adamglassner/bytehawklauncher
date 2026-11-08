using System.Diagnostics;
using System.IO;
using System.Windows;
using Bytehawk.Launcher.Models;

namespace Bytehawk.Launcher.Services;

/// <summary>
/// Self-update for the launcher itself. New versions are published as GitHub Releases on
/// <see cref="Repo"/>; the release's Setup .exe (built by the Inno Setup script) is downloaded and
/// run silently, and the launcher closes so the installer can replace it. The installer relaunches it.
/// </summary>
public static class LauncherUpdater
{
    public const string Repo = "adamglassner/bytehawklauncher";
    public const string SetupAssetPattern = "BytehawkStudioLauncher-*-Setup.exe;*Setup*.exe";

    public static string CurrentVersion => App.VersionText;

    public static bool IsNewer(CachedRelease? release) =>
        release != null && !release.NoRelease && !string.IsNullOrEmpty(release.Tag) &&
        GitHubService.CompareVersions(release.Tag, CurrentVersion) > 0;

    public static ReleaseAsset? SetupAsset(CachedRelease release) =>
        GitHubService.MatchAsset(release.Assets, SetupAssetPattern);

    /// <summary>Starts the downloaded installer silently and shuts the launcher down. Returns an error message on failure.</summary>
    public static string? RunInstallerAndExit(string setupPath)
    {
        if (!File.Exists(setupPath)) return $"The update file is missing:\n{setupPath}";
        try
        {
            // Let the installer see that the launcher has closed (it checks the app mutex).
            (Application.Current as App)?.ReleaseSingleInstance();
            Process.Start(new ProcessStartInfo(setupPath)
            {
                UseShellExecute = true,
                Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH=1",
            });
            Log.Info($"Started launcher update {setupPath}; exiting");
            Application.Current.Shutdown();
            return null;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "The update was cancelled (administrator permission was not given).";
        }
        catch (Exception ex)
        {
            Log.Error("Launcher update", ex);
            return $"Couldn't start the update:\n{ex.Message}";
        }
    }
}
