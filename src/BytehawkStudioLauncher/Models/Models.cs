using System.Text.Json.Serialization;

namespace Bytehawk.Launcher.Models;

/// <summary>settings.json</summary>
public sealed class AppSettings
{
    /// <summary>"Dark", "Light" or "System".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Null/empty = default (%APPDATA%\BytehawkStudio\Launcher\downloads).</summary>
    public string? DownloadFolder { get; set; }

    public bool CheckUpdatesOnStartup { get; set; } = true;

    /// <summary>Install app updates automatically when they're found (apps the launcher installed itself).</summary>
    public bool AutoUpdateApps { get; set; } = true;

    /// <summary>Optional URL of a catalog JSON file (same format as catalog.json). Defaults to the catalog in the launcher's GitHub repo.</summary>
    public string RemoteCatalogUrl { get; set; } = "";

    /// <summary>Used to migrate older settings files (0 = written by 1.0.0).</summary>
    public int SettingsVersion { get; set; }

    public const string DefaultRemoteCatalogUrl =
        "https://raw.githubusercontent.com/adamglassner/bytehawklauncher/main/catalog.json";
}

/// <summary>catalog.json — the list of Bytehawk Studio apps (hand-editable).</summary>
public sealed class CatalogFile
{
    public int Version { get; set; } = 1;
    public List<CatalogEntry> Apps { get; set; } = new();
    /// <summary>Ids of built-in entries the user removed, so they aren't added back.</summary>
    public List<string> RemovedDefaults { get; set; } = new();
}

public sealed class CatalogEntry
{
    /// <summary>Optional stable id. If omitted, the repo ("owner/repo") is used.</summary>
    public string? Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>GitHub repository as "owner/repo".</summary>
    public string Repo { get; set; } = "";
    /// <summary>Which release asset to download, e.g. "*.exe", "*.msi", "*-win-x64.zip". Separate several with ';'. Prefix with '!' to exclude.</summary>
    public string AssetPattern { get; set; } = "*.exe";
    /// <summary>Optional: the program to launch after installing (e.g. "Guardrail.exe"). Found automatically when omitted.</summary>
    public string? Exe { get; set; }
    /// <summary>Optional: URL of a square PNG icon shown on the card until the app is installed.</summary>
    public string? Icon { get; set; }
    /// <summary>True for the old placeholder entries (removed in 1.1.0; kept so older catalog files still load).</summary>
    public bool Sample { get; set; }
    /// <summary>Announced product with no release yet: shown with a "Coming soon" badge and no download. Needs an id; repo can be empty.</summary>
    public bool ComingSoon { get; set; }
    /// <summary>Optional server that goes with the app (e.g. WillowWallet Server for syncing): shown as "Get server" on the card.</summary>
    public ServerDownload? Server { get; set; }
    /// <summary>The macOS build, if there is one (used by the macOS launcher; the Windows launcher ignores it).</summary>
    public PlatformBuild? Macos { get; set; }
    /// <summary>The Linux build, if there is one (used by the Linux launcher; the Windows launcher ignores it).</summary>
    public PlatformBuild? Linux { get; set; }
    /// <summary>"extension" for a browser extension (shown on the Browser Extensions page; its zip is unpacked into a
    /// folder that keeps the same path across updates, for Chrome/Edge "Load unpacked"). Empty = a normal app.</summary>
    public string? Kind { get; set; }

    [JsonIgnore]
    public bool IsExtension => string.Equals(Kind?.Trim(), "extension", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public string Key => (!string.IsNullOrWhiteSpace(Id) ? Id! : !string.IsNullOrWhiteSpace(Repo) ? Repo : Name).Trim().ToLowerInvariant();

    [JsonIgnore]
    public bool HasRepo => !string.IsNullOrWhiteSpace(Repo);

    /// <summary>Set at runtime for entries that came from the remote catalog URL.</summary>
    [JsonIgnore]
    public bool FromRemote { get; set; }
}

/// <summary>
/// Where to find an app's macOS or Linux build. Apps without one show as "Coming soon" in that launcher.
/// </summary>
public sealed class PlatformBuild
{
    /// <summary>Set when this platform's builds have their own releases, e.g. "macos-v". Empty = the app's latest release.</summary>
    public string? TagPrefix { get; set; }
    /// <summary>Release asset pattern; "{arch}" becomes "arm64" or "x64". E.g. "ShoreDrop-*-macOS-{arch}.zip", "*.AppImage", "app_*_amd64.deb".</summary>
    public string AssetPattern { get; set; } = "";
    /// <summary>macOS: the app bundle inside the .zip/.dmg, e.g. "ShoreDrop.app" (found automatically when omitted).</summary>
    public string? App { get; set; }
    /// <summary>Linux: the program to run, relative to the extracted folder, or an absolute path for .deb/.rpm installs (e.g. "/opt/larksong/bin/Larksong").</summary>
    public string? Exe { get; set; }
    /// <summary>Linux .deb/.rpm: the package name, used to uninstall (e.g. "larksong").</summary>
    public string? Package { get; set; }
}

/// <summary>A self-hosted server for an app, released in the same repo under its own tag prefix.</summary>
public sealed class ServerDownload
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>"owner/repo"; the app's repo when empty.</summary>
    public string? Repo { get; set; }
    /// <summary>Server releases are tagged like "server-v0.1.0".</summary>
    public string TagPrefix { get; set; } = "server-v";
    /// <summary>The Windows installer in that release.</summary>
    public string WindowsAsset { get; set; } = "*-Setup.exe";
    /// <summary>Docker image, e.g. ghcr.io/owner/app-server.</summary>
    public string? DockerImage { get; set; }
    public int Port { get; set; } = 8080;
    /// <summary>Where to read more (setup guide).</summary>
    public string? GuideUrl { get; set; }
    /// <summary>The Windows installer's uninstall key (Inno Setup: "{AppId}_is1"), to find and uninstall it.</summary>
    public string? WindowsUninstallKey { get; set; }
    /// <summary>Where the Windows install keeps its data (environment variables allowed).</summary>
    public string? WindowsDataFolder { get; set; }
    /// <summary>Linux package in the same release ("{arch}" becomes "x64" or "arm64"); it contains install.sh / uninstall.sh.</summary>
    public string? LinuxAsset { get; set; }
    /// <summary>Where the Linux install.sh puts the server (used to detect it and find uninstall.sh), e.g. "/opt/guardrail-server".</summary>
    public string? LinuxInstallDir { get; set; }
    /// <summary>The systemd service name, e.g. "guardrail-server".</summary>
    public string? LinuxService { get; set; }
    /// <summary>Container name for "docker run" (macOS/Linux launchers), e.g. "guardrail-server". The image keeps its data in /data.</summary>
    public string? DockerName { get; set; }
}

/// <summary>installed.json — what the launcher installed for each catalog entry (keyed by entry Key).</summary>
public sealed class InstalledApp
{
    public string Version { get; set; } = "";
    public DateTime InstalledAt { get; set; }
    public string AssetName { get; set; } = "";
    /// <summary>"inno" or "nsis" (setup run silently by the launcher), "zip" (unpacked by the launcher),
    /// or "exe"/"msi" (setup the user ran themselves; the launcher can't update or uninstall those).</summary>
    public string Kind { get; set; } = "";
    /// <summary>The downloaded file.</summary>
    public string? DownloadedFile { get; set; }
    /// <summary>Folder the zip was extracted to (zip only).</summary>
    public string? InstallFolder { get; set; }
    /// <summary>The program the Launch button runs (remembered choice).</summary>
    public string? LaunchPath { get; set; }
    /// <summary>True once the launcher has made sure this app has a Start menu entry.</summary>
    public bool StartMenuChecked { get; set; }
}

/// <summary>cache/releases.json — cached GitHub "latest release" responses.</summary>
public sealed class CachedRelease
{
    public string Repo { get; set; } = "";
    public string Tag { get; set; } = "";
    public string? Name { get; set; }
    public string? HtmlUrl { get; set; }
    public DateTime? PublishedAt { get; set; }
    public List<ReleaseAsset> Assets { get; set; } = new();
    public DateTime FetchedAt { get; set; }
    public string? ETag { get; set; }
    /// <summary>True when GitHub said the repo has no releases (404 on /releases/latest).</summary>
    public bool NoRelease { get; set; }
}

public sealed class ReleaseAsset
{
    public string Name { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public long Size { get; set; }
    /// <summary>"sha256:&lt;hex&gt;" when GitHub provides it (recent releases).</summary>
    public string? Digest { get; set; }
}

/// <summary>library.json — "My Software" entries.</summary>
public sealed class LibraryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Arguments { get; set; } = "";
    public bool RunAsAdmin { get; set; }
    /// <summary>Optional note shown on the card.</summary>
    public string Description { get; set; } = "";
    /// <summary>Optional version (auto-filled from the .exe's file version, editable).</summary>
    public string Version { get; set; } = "";
    /// <summary>Optional category, e.g. "Productivity" or "Games". Empty = Uncategorized.</summary>
    public string Category { get; set; } = "";
    /// <summary>Optional website for the program (opened with the card's globe button).</summary>
    public string Website { get; set; } = "";
    /// <summary>For Microsoft Store / packaged apps: the AppUserModelID (Path is empty). Started via shell:AppsFolder.</summary>
    public string AppId { get; set; } = "";
    public DateTime AddedAt { get; set; } = DateTime.Now;
}

/// <summary>webapps.json — "Web Apps" bookmarks.</summary>
public sealed class WebAppEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>Optional note shown on the card.</summary>
    public string Description { get; set; } = "";
    /// <summary>Optional category, e.g. "Productivity" or "Education". Empty = Uncategorized.</summary>
    public string Category { get; set; } = "";
    public DateTime AddedAt { get; set; } = DateTime.Now;
}
