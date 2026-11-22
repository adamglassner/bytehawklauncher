using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Bytehawk.Launcher.Models;
using Bytehawk.Launcher.Services;

namespace Bytehawk.Launcher.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    /// <summary>Raise change for every property (used after state changes that affect many computed values).</summary>
    protected void RefreshAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

/// <summary>A download in progress (or finished) — shown on the app card and on the Downloads page.</summary>
public sealed class DownloadItemVM : ObservableObject
{
    public string AppName { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Destination { get; init; } = "";
    public CancellationTokenSource Cts { get; } = new();

    private double _percent;
    private bool _indeterminate = true;
    private string _detail = "Starting…";
    private string _status = "Downloading";
    private bool _isActive = true;

    public double Percent { get => _percent; set => Set(ref _percent, value); }
    public bool IsIndeterminate { get => _indeterminate; set => Set(ref _indeterminate, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public bool IsActive { get => _isActive; set { if (Set(ref _isActive, value)) OnPropertyChanged(nameof(IsFinished)); } }
    public bool IsFinished => !IsActive;

    public void Report(DownloadProgress p)
    {
        var speed = p.BytesPerSecond > 0 ? $" · {Downloader.FormatBytes(p.BytesPerSecond)}/s" : "";
        if (p.TotalBytes is long total && total > 0)
        {
            IsIndeterminate = false;
            Percent = Math.Min(100, p.BytesReceived * 100.0 / total);
            Detail = $"{Downloader.FormatBytes(p.BytesReceived)} of {Downloader.FormatBytes(total)}{speed} · {Percent:0}%";
        }
        else
        {
            IsIndeterminate = true;
            Detail = $"{Downloader.FormatBytes(p.BytesReceived)}{speed}";
        }
    }
}

/// <summary>Card for a Bytehawk Studio app from the GitHub catalog.</summary>
public sealed class BytehawkAppVM : ObservableObject
{
    public BytehawkAppVM(CatalogEntry entry) { Entry = entry; }

    public CatalogEntry Entry { get; private set; }
    public InstalledApp? Installed { get; private set; }
    public CachedRelease? Release { get; private set; }

    private DownloadItemVM? _download;
    private string? _note;
    private bool _busy;

    public string Key => Entry.Key;
    public string Name => Entry.Name;
    public string Description => string.IsNullOrWhiteSpace(Entry.Description) ? "No description." : Entry.Description;
    public bool IsSample => Entry.Sample;
    public bool IsComingSoon => Entry.ComingSoon;
    public bool HasRepo => Entry.HasRepo;
    public bool HasServer => Entry.Server != null && !Entry.ComingSoon;
    /// <summary>A browser extension (Browser Extensions page): "Add to browser" instead of Launch.</summary>
    public bool IsExtension => Entry.IsExtension;
    /// <summary>The unpacked extension folder to pick in Chrome/Edge "Load unpacked".</summary>
    public string? ExtensionFolder => IsExtension ? Installed?.InstallFolder : null;
    public string RepoText => Entry.HasRepo ? Entry.Repo : "Bytehawk Studio";
    public string ReleasePageUrl => Release?.HtmlUrl ?? $"https://github.com/{Entry.Repo}/releases";

    public string LatestVersion => Release == null || Release.NoRelease ? "" : GitHubService.DisplayVersion(Release.Tag);
    public string InstalledVersion => Installed == null ? "" : GitHubService.DisplayVersion(Installed.Version);
    public bool IsInstalled => Installed != null;

    public string VersionLine
    {
        get
        {
            if (IsComingSoon) return "Coming soon · not released yet";
            var latest = Release == null ? "unknown" : Release.NoRelease ? "no release yet" : LatestVersion;
            return IsInstalled ? $"Installed {InstalledVersion} · Latest {latest}" : $"Not installed · Latest {latest}";
        }
    }

    public bool HasUpdate => IsInstalled && Release != null && !Release.NoRelease &&
                             GitHubService.CompareVersions(Release.Tag, Installed!.Version) > 0;

    /// <summary>A launch file was recorded but is gone from disk (for an extension: its folder or manifest is gone).</summary>
    public bool IsMissing => IsInstalled && (IsExtension
        ? !AppInstaller.ExtensionPresent(Installed!)
        : !string.IsNullOrEmpty(Installed!.LaunchPath) && !File.Exists(Installed.LaunchPath));

    public bool NeedsLaunchChoice => IsInstalled && !IsExtension && string.IsNullOrEmpty(Installed!.LaunchPath);

    public DownloadItemVM? Download
    {
        get => _download;
        set { if (Set(ref _download, value)) RefreshAll(); }
    }
    public bool IsDownloading => Download?.IsActive == true;

    public bool Busy { get => _busy; set { if (Set(ref _busy, value)) RefreshAll(); } }

    public string? Note { get => _note; set { if (Set(ref _note, value)) OnPropertyChanged(nameof(HasNote)); } }
    public bool HasNote => !string.IsNullOrEmpty(Note);

    /// <summary>Main button text: Download / Update / Launch / Not found — reinstall / Choose launch file.</summary>
    public string PrimaryText =>
        !IsInstalled ? "Download" :
        IsMissing ? "Not found — reinstall" :
        HasUpdate ? "Update" :
        NeedsLaunchChoice ? "Choose launch file" :
        IsExtension ? "Add to browser" : "Launch";

    /// <summary>Text of the second button shown next to Update.</summary>
    public string SecondaryText => IsExtension ? "Add to browser" : "Launch";

    public bool ShowPrimary => !IsComingSoon && !IsDownloading && !Busy;

    /// <summary>When an update is available we still offer Launch next to Update.</summary>
    public bool ShowSecondaryLaunch => ShowPrimary && HasUpdate && !IsMissing && !NeedsLaunchChoice;

    public bool CanChangeLaunchFile => IsInstalled && !IsExtension && !IsDownloading;
    /// <summary>Installed and its launch file exists, so a desktop shortcut can point at it.</summary>
    public bool CanCreateShortcut => IsInstalled && !IsExtension && !NeedsLaunchChoice && !IsMissing && !IsDownloading;
    /// <summary>Extensions: open the unpacked folder (folder button on the card).</summary>
    public bool CanShowFolder => IsExtension && IsInstalled && !IsMissing && !IsDownloading;

    /// <summary>Apps the launcher installed itself can be uninstalled from the card.</summary>
    public bool CanUninstall => IsInstalled && AppInstaller.IsManagedKind(Installed!.Kind) && !IsDownloading && !Busy;

    // ---- card icon: the installed program's icon, else the catalog's icon URL, else the Bytehawk mark ----
    private string? _iconSource;
    private ImageSource? _icon;

    public ImageSource? AppIcon
    {
        get
        {
            var source = !IsExtension && Installed?.LaunchPath is { } lp && File.Exists(lp) ? lp
                       : Uri.TryCreate(Entry.Icon, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? u.AbsoluteUri
                       : null;
            if (source == _iconSource) return _icon;
            _iconSource = source;
            _icon = null;
            try
            {
                if (source == null) { }
                else if (source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(source);
                    bmp.DecodePixelWidth = 68;
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    // Offline: fall back to the Bytehawk mark instead of an empty square.
                    bmp.DownloadFailed += (_, _) =>
                    {
                        _icon = null;
                        OnPropertyChanged(nameof(AppIcon));
                        OnPropertyChanged(nameof(HasAppIcon));
                        OnPropertyChanged(nameof(NoAppIcon));
                    };
                    bmp.EndInit();
                    _icon = bmp;
                }
                else _icon = ShellHelper.GetFileIcon(source);
            }
            catch { _icon = null; }
            return _icon;
        }
    }
    public bool HasAppIcon => AppIcon != null;
    public bool NoAppIcon => !HasAppIcon;

    public void Update(CatalogEntry entry, InstalledApp? installed, CachedRelease? release)
    {
        Entry = entry;
        Installed = installed;
        Release = release;
        RefreshAll();
    }
}

/// <summary>Card on the Bytehawk Servers page: a self-hosted server that goes with a Bytehawk app.</summary>
public sealed class ServerCardVM : ObservableObject
{
    private DownloadItemVM? _download;
    private string? _note;
    private bool _busy;

    public ServerCardVM(BytehawkAppVM app) { App = app; Refresh(); }

    /// <summary>The app the server is for (its icon, name and catalog entry).</summary>
    public BytehawkAppVM App { get; private set; }
    public ServerDownload Server => App.Entry.Server!;
    public string Key => App.Key;
    public string Name => Server.Name;
    public string ForApp => $"Sync server for {App.Name}";
    public string Description => string.IsNullOrWhiteSpace(Server.Description) ? $"Self-hosted server for {App.Name}." : Server.Description;
    public string Platforms => string.IsNullOrWhiteSpace(Server.DockerImage) ? "Windows · Linux" : "Windows · Docker · Linux";

    public InstalledServer? Installed { get; private set; }
    public CachedRelease? Release { get; private set; }
    /// <summary>Why there's no version to show (not released yet, offline…), or null while checking.</summary>
    public string? ReleaseProblem { get; private set; }

    public void SetApp(BytehawkAppVM app) { App = app; RefreshAll(); }

    /// <summary>Looks again at whether the server is installed on this PC.</summary>
    public void Refresh()
    {
        Installed = ServerInstall.Find(Server);
        RefreshAll();
    }

    public void SetRelease(CachedRelease? release, string? problem)
    {
        Release = release;
        ReleaseProblem = problem;
        RefreshAll();
    }

    public bool IsInstalled => Installed != null;
    public string LatestVersion => Release == null || Release.NoRelease ? "" :
        Release.Tag.StartsWith(Server.TagPrefix, StringComparison.OrdinalIgnoreCase) ? Release.Tag[Server.TagPrefix.Length..] : Release.Tag;
    public bool HasUpdate => IsInstalled && LatestVersion.Length > 0 && GitHubService.CompareVersions(LatestVersion, Installed!.Version) > 0;
    public string InstalledBadge => Installed is { Version.Length: > 0 } i ? $"Installed · {i.Version}" : "Installed";

    public string VersionLine =>
        IsInstalled ? (LatestVersion.Length > 0 ? $"Running on this PC · latest {LatestVersion}" : "Running on this PC as a Windows service")
        : LatestVersion.Length > 0 ? $"Latest version {LatestVersion}"
        : ReleaseProblem ?? "Checking for the latest version…";

    public DownloadItemVM? Download { get => _download; set { if (Set(ref _download, value)) RefreshAll(); } }
    public bool IsDownloading => Download?.IsActive == true;
    public bool Busy { get => _busy; set { if (Set(ref _busy, value)) RefreshAll(); } }
    public string? Note { get => _note; set { if (Set(ref _note, value)) OnPropertyChanged(nameof(HasNote)); } }
    public bool HasNote => !string.IsNullOrEmpty(Note);

    /// <summary>Install (Windows installer) / Update / Open panel.</summary>
    public string PrimaryText => !IsInstalled ? "Install" : HasUpdate ? "Update" : "Open panel";
    public bool ShowPrimary => !IsDownloading && !Busy;
    public bool ShowOpenPanel => IsInstalled && HasUpdate && !IsDownloading && !Busy;
    public bool CanUninstall => IsInstalled && !IsDownloading && !Busy;
}

/// <summary>Card for an entry in My Software.</summary>
public sealed class SoftwareVM : ObservableObject
{
    public SoftwareVM(LibraryEntry entry) { Entry = entry; Reload(); }

    public LibraryEntry Entry { get; private set; }
    private ImageSource? _icon;

    public string Name => Entry.Name;
    public string Path => Entry.Path;
    public string Category => (Entry.Category ?? "").Trim();
    /// <summary>Group name on the page ("Uncategorized" when empty).</summary>
    public string CategoryDisplay => Category.Length == 0 ? "Uncategorized" : Category;
    /// <summary>Sorts groups by name with Uncategorized last.</summary>
    public string CategorySortKey => Category.Length == 0 ? "\uffff" : Category.ToLowerInvariant();
    public string Description => Entry.Description ?? "";
    public bool HasDescription => !string.IsNullOrWhiteSpace(Entry.Description);
    public string DetailLine
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Entry.Version)) parts.Add($"Version {Entry.Version}");
            parts.Add(IsStoreApp ? "Microsoft Store app" : System.IO.Path.GetExtension(Entry.Path).TrimStart('.').ToUpperInvariant());
            if (!string.IsNullOrWhiteSpace(Entry.Arguments)) parts.Add($"Args: {Entry.Arguments}");
            if (Entry.RunAsAdmin) parts.Add("Runs as administrator");
            return string.Join(" · ", parts.Where(p => p.Length > 0));
        }
    }
    public bool IsStoreApp => !string.IsNullOrWhiteSpace(Entry.AppId);
    public string Website => Entry.Website ?? "";
    public bool HasWebsite => !string.IsNullOrWhiteSpace(Entry.Website);
    /// <summary>What the card shows under the description: the file path, or the Store app id.</summary>
    public string Location => IsStoreApp ? Entry.AppId : Entry.Path;
    public bool CanOpenLocation => !IsStoreApp;
    private bool _exists;
    public bool Exists => _exists;
    public bool IsMissing => !Exists;
    public string MissingText => IsStoreApp
        ? "This app isn't installed anymore. Reinstall it from the Microsoft Store, or remove it here."
        : "File not found — it may have been moved or uninstalled. Use Edit to point to it again.";
    public ImageSource? Icon { get => _icon; private set { Set(ref _icon, value); OnPropertyChanged(nameof(HasIcon)); OnPropertyChanged(nameof(NoIcon)); } }
    public bool HasIcon => Icon != null;
    public bool NoIcon => Icon == null;

    public void Update(LibraryEntry entry) { Entry = entry; Reload(); }

    public void Reload()
    {
        _exists = IsStoreApp ? StoreApps.Exists(Entry.AppId) : File.Exists(Entry.Path);
        Icon = IsStoreApp ? StoreApps.GetIcon(Entry.AppId) : ShellHelper.GetFileIcon(Entry.Path);
        RefreshAll();
    }
}

/// <summary>Card for a Web App bookmark.</summary>
public sealed class WebAppVM : ObservableObject
{
    public WebAppVM(WebAppEntry entry) { Entry = entry; _icon = WebIconService.LoadCached(entry.Id); }
    public WebAppEntry Entry { get; private set; }
    private ImageSource? _icon;

    public string Name => Entry.Name;
    public string Url => Entry.Url;
    public string Host => Uri.TryCreate(Entry.Url, UriKind.Absolute, out var u) ? u.Host : Entry.Url;
    public string Category => (Entry.Category ?? "").Trim();
    /// <summary>Group name on the page ("Uncategorized" when empty).</summary>
    public string CategoryDisplay => Category.Length == 0 ? "Uncategorized" : Category;
    /// <summary>Sorts groups by name with Uncategorized last.</summary>
    public string CategorySortKey => Category.Length == 0 ? "\uffff" : Category.ToLowerInvariant();
    public string Description => Entry.Description ?? "";
    public bool HasDescription => !string.IsNullOrWhiteSpace(Entry.Description);

    /// <summary>The site's own icon (cached PNG); the card shows a globe until it's available.</summary>
    public ImageSource? Icon { get => _icon; set { Set(ref _icon, value); OnPropertyChanged(nameof(HasIcon)); OnPropertyChanged(nameof(NoIcon)); } }
    public bool HasIcon => Icon != null;
    public bool NoIcon => Icon == null;

    public void Update(WebAppEntry entry) { Entry = entry; RefreshAll(); }
}
