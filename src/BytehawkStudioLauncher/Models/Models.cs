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

    /// <summary>Optional URL of a catalog JSON file (same format as catalog.json).</summary>
    public string RemoteCatalogUrl { get; set; } = "";
}

/// <summary>catalog.json — the list of Bytehawk Studio apps (hand-editable).</summary>
public sealed class CatalogFile
{
    public int Version { get; set; } = 1;
    public List<CatalogEntry> Apps { get; set; } = new();
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
    /// <summary>True for the shipped placeholder entries.</summary>
    public bool Sample { get; set; }

    [JsonIgnore]
    public string Key => string.IsNullOrWhiteSpace(Id) ? Repo.Trim().ToLowerInvariant() : Id!.Trim().ToLowerInvariant();

    /// <summary>Set at runtime for entries that came from the remote catalog URL.</summary>
    [JsonIgnore]
    public bool FromRemote { get; set; }
}

/// <summary>installed.json — what the launcher installed for each catalog entry (keyed by entry Key).</summary>
public sealed class InstalledApp
{
    public string Version { get; set; } = "";
    public DateTime InstalledAt { get; set; }
    public string AssetName { get; set; } = "";
    /// <summary>"exe", "msi" or "zip".</summary>
    public string Kind { get; set; } = "";
    /// <summary>The downloaded file.</summary>
    public string? DownloadedFile { get; set; }
    /// <summary>Folder the zip was extracted to (zip only).</summary>
    public string? InstallFolder { get; set; }
    /// <summary>The program the Launch button runs (remembered choice).</summary>
    public string? LaunchPath { get; set; }
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
}

/// <summary>library.json — "My Software" entries.</summary>
public sealed class LibraryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Arguments { get; set; } = "";
    public bool RunAsAdmin { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.Now;
}

/// <summary>webapps.json — "Web Apps" bookmarks.</summary>
public sealed class WebAppEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTime AddedAt { get; set; } = DateTime.Now;
}
