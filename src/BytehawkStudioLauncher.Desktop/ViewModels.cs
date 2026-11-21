using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using Bytehawk.Launcher.Models;
using Bytehawk.Launcher.Services;

namespace Bytehawk.Launcher.Desktop;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnChanged(name);
        return true;
    }
    /// <summary>Re-read every property (after a state change that affects many computed values).</summary>
    public void RefreshAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

/// <summary>A download (shown on the card and on the Downloads page).</summary>
public sealed class DownloadVM : Observable
{
    public string Title { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Destination { get; init; } = "";
    public CancellationTokenSource Cts { get; } = new();

    private double _percent;
    private bool _indeterminate = true;
    private string _detail = "Starting…";
    private string _status = "Downloading";
    private bool _active = true;

    public double Percent { get => _percent; set => Set(ref _percent, value); }
    public bool Indeterminate { get => _indeterminate; set => Set(ref _indeterminate, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public bool IsActive { get => _active; set { if (Set(ref _active, value)) OnChanged(nameof(IsFinished)); } }
    public bool IsFinished => !IsActive;

    public void Report(DownloadProgress p)
    {
        var speed = p.BytesPerSecond > 0 ? $" · {Downloader.FormatBytes(p.BytesPerSecond)}/s" : "";
        if (p.TotalBytes is long total && total > 0)
        {
            Indeterminate = false;
            Percent = Math.Min(100, p.BytesReceived * 100.0 / total);
            Detail = $"{Downloader.FormatBytes(p.BytesReceived)} of {Downloader.FormatBytes(total)}{speed} · {Percent:0}%";
        }
        else { Indeterminate = true; Detail = $"{Downloader.FormatBytes(p.BytesReceived)}{speed}"; }
    }
}

/// <summary>A Bytehawk app card. Apps without a build for this system show "Coming soon to macOS/Linux".</summary>
public sealed class AppVM : Observable
{
    public AppVM(CatalogEntry entry) { Entry = entry; }

    public CatalogEntry Entry { get; private set; }
    public PlatformBuild? Build => Platform.BuildFor(Entry);
    public InstalledApp? Installed { get; private set; }
    public CachedRelease? Release { get; private set; }

    private Bitmap? _icon;
    private DownloadVM? _download;
    private string? _note;
    private bool _busy;
    private string _busyText = "";

    public string Key => Entry.Key;
    public string Name => Entry.Name;
    public string Description => string.IsNullOrWhiteSpace(Entry.Description) ? "" : Entry.Description;
    public Bitmap? Icon { get => _icon; set { Set(ref _icon, value); OnChanged(nameof(HasIcon)); OnChanged(nameof(NoIcon)); } }
    public bool HasIcon => Icon != null;
    public bool NoIcon => Icon == null;

    /// <summary>No build for this OS (or the catalog marks it coming soon).</summary>
    public bool IsComingSoon => Entry.ComingSoon || Build == null || string.IsNullOrWhiteSpace(Build.AssetPattern) || !Entry.HasRepo;
    /// <summary>"Coming soon" for apps not released anywhere yet; "Coming soon to macOS/Linux" for apps that only lack a build for this system.</summary>
    public string ComingSoonText => Entry.ComingSoon && !Entry.HasRepo ? "Coming soon" : $"Coming soon to {Platform.Name}";

    /// <summary>There's a build for this OS but not for this Mac/PC's processor (e.g. Apple Silicon only).</summary>
    public bool WrongArch => !IsComingSoon && Release is { NoRelease: false } && Asset == null && !Installed_;
    private bool Installed_ => Installed != null;

    public ReleaseAsset? Asset => Release == null || Build == null ? null : GitHubService.MatchAsset(Release.Assets, Platform.Pattern(Build.AssetPattern));

    public bool IsInstalled => Installed != null;
    public string InstalledVersion => Installed == null ? "" : Display(Installed.Version);
    public string LatestVersion => Release == null || Release.NoRelease ? "" : Display(Release.Tag);
    private string Display(string tag) =>
        GitHubService.DisplayVersion(Build?.TagPrefix is { Length: > 0 } p && tag.StartsWith(p, StringComparison.OrdinalIgnoreCase) ? tag[p.Length..] : tag);

    public string VersionLine
    {
        get
        {
            if (IsComingSoon) return Entry.ComingSoon && !Entry.HasRepo ? "Not released yet" : $"Not available for {Platform.Name} yet";
            var latest = Release == null ? "checking…" : Release.NoRelease ? "no release yet" : LatestVersion;
            return IsInstalled ? $"Installed {InstalledVersion} · Latest {latest}" : $"Not installed · Latest {latest}";
        }
    }

    public bool HasUpdate => IsInstalled && Release is { NoRelease: false } && Asset != null &&
                             GitHubService.CompareVersions(Release.Tag, Installed!.Version) > 0;
    public bool IsMissing => IsInstalled && AppManager.IsMissing(Installed!);

    public DownloadVM? Download { get => _download; set { if (Set(ref _download, value)) RefreshAll(); } }
    public bool IsDownloading => Download?.IsActive == true;
    public bool Busy { get => _busy; set { if (Set(ref _busy, value)) RefreshAll(); } }
    public string BusyText { get => _busyText; set => Set(ref _busyText, value); }
    public bool ShowBusy => Busy && !IsDownloading;

    public string? Note { get => _note; set { if (Set(ref _note, value)) OnChanged(nameof(HasNote)); } }
    public bool HasNote => !string.IsNullOrEmpty(Note);

    public string PrimaryText =>
        !IsInstalled ? "Download" : IsMissing ? "Not found — reinstall" : HasUpdate ? "Update" : "Launch";
    public bool ShowPrimary => !IsComingSoon && !IsDownloading && !Busy && (IsInstalled || Asset != null || Release == null);
    public bool ShowSecondaryLaunch => ShowPrimary && HasUpdate && !IsMissing;
    public bool CanUninstall => IsInstalled && AppManager.IsManagedKind(Installed!.Kind) && !IsDownloading && !Busy;
    public bool CanShortcut => Platform.IsLinux && IsInstalled && !IsMissing && !IsDownloading;
    public bool HasRepo => Entry.HasRepo;
    public bool HasServer => Entry.Server != null;
    public string ReleasePageUrl => Release?.HtmlUrl ?? $"https://github.com/{Entry.Repo}/releases";

    public void Update(CatalogEntry entry, InstalledApp? installed, CachedRelease? release)
    {
        Entry = entry;
        Installed = installed;
        Release = release;
        RefreshAll();
    }
}

/// <summary>A Bytehawk Servers card.</summary>
public sealed class ServerVM : Observable
{
    public ServerVM(CatalogEntry app) { App = app; }
    public CatalogEntry App { get; private set; }
    public ServerDownload Server => App.Server!;
    public string Key => "server:" + App.Key;

    private Bitmap? _icon;
    private string _state = "";
    private string? _note;
    private bool _busy;
    private string _busyText = "";
    private CachedRelease? _release;

    public string Name => Server.Name;
    public string Description => Server.Description;
    public string ForApp => $"For {App.Name}";
    public Bitmap? Icon { get => _icon; set { Set(ref _icon, value); OnChanged(nameof(HasIcon)); OnChanged(nameof(NoIcon)); } }
    public bool HasIcon => Icon != null;
    public bool NoIcon => Icon == null;

    public bool IsComingSoon => !ServerManager.IsAvailable(Server);
    public string ComingSoonText => $"Coming soon to {Platform.Name}";
    public bool HasNative => ServerManager.HasNative(Server);
    public bool HasDocker => ServerManager.HasDocker(Server);
    public string DockerCommand => HasDocker ? ServerManager.DockerRunCommand(Server) : "";
    public string PanelUrl => ServerManager.PanelUrl(Server);

    public CachedRelease? Release { get => _release; set { if (Set(ref _release, value)) RefreshAll(); } }

    /// <summary>One-line status: "Running (system service)", "Running in Docker", "Not installed"…</summary>
    public string State { get => _state; set { if (Set(ref _state, value)) RefreshAll(); } }
    public bool NativeInstalled { get; set; }
    public string? ContainerState { get; set; }
    public bool DockerRunning => ContainerState == "running";
    public bool HasContainer => !string.IsNullOrEmpty(ContainerState);

    public string StatusLine => IsComingSoon ? $"Not available for {Platform.Name} yet" : State;
    public bool CanInstallNative => HasNative && !Busy;
    public string InstallNativeText => NativeInstalled ? "Reinstall / update" : "Install";
    public bool CanUninstallNative => HasNative && NativeInstalled && !Busy;
    public bool CanRunDocker => HasDocker && !Busy && !DockerRunning;
    public string RunDockerText => HasContainer ? "Restart in Docker" : "Run with Docker";
    public bool CanStopDocker => HasDocker && HasContainer && !Busy;
    public bool CanOpenPanel => !IsComingSoon && (NativeInstalled || DockerRunning);
    public bool HasGuide => !string.IsNullOrWhiteSpace(Server.GuideUrl);

    public bool Busy { get => _busy; set { if (Set(ref _busy, value)) RefreshAll(); } }
    public string BusyText { get => _busyText; set => Set(ref _busyText, value); }
    public string? Note { get => _note; set { if (Set(ref _note, value)) OnChanged(nameof(HasNote)); } }
    public bool HasNote => !string.IsNullOrEmpty(Note);

    public void Update(CatalogEntry app) { App = app; RefreshAll(); }
}

/// <summary>A My Software card: any program on this computer.</summary>
public sealed class SoftwareVM : Observable
{
    public SoftwareVM(LibraryEntry e) { Entry = e; Reload(); }
    public LibraryEntry Entry { get; private set; }
    private Bitmap? _icon;
    private bool _exists;

    public string Name => Entry.Name;
    public string Location => Entry.Path;
    public string Description => Entry.Description ?? "";
    public bool HasDescription => !string.IsNullOrWhiteSpace(Entry.Description);
    public string Category => (Entry.Category ?? "").Trim();
    public string CategoryDisplay => Category.Length == 0 ? "Uncategorized" : Category;
    public string Website => Entry.Website ?? "";
    public bool HasWebsite => !string.IsNullOrWhiteSpace(Entry.Website);
    public string DetailLine
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Entry.Version)) parts.Add($"Version {Entry.Version}");
            if (!string.IsNullOrWhiteSpace(Entry.Category)) parts.Add(Entry.Category.Trim());
            if (!string.IsNullOrWhiteSpace(Entry.Arguments)) parts.Add($"Args: {Entry.Arguments}");
            return string.Join(" · ", parts);
        }
    }
    public Bitmap? Icon { get => _icon; private set { Set(ref _icon, value); OnChanged(nameof(HasIcon)); OnChanged(nameof(NoIcon)); } }
    public bool HasIcon => Icon != null;
    public bool NoIcon => Icon == null;
    public bool IsMissing => !_exists;
    public bool CanShortcut => Platform.IsLinux;

    public void Update(LibraryEntry e) { Entry = e; Reload(); }
    public void Reload()
    {
        _exists = File.Exists(Entry.Path) || Directory.Exists(Entry.Path);
        Icon = _exists ? IconCache.ProgramIcon(Entry.Path) : null;
        RefreshAll();
    }
}

/// <summary>A Web Apps card.</summary>
public sealed class WebAppVM : Observable
{
    public WebAppVM(WebAppEntry e) { Entry = e; _icon = IconCache.Load(IconCache.WebIconPath(e.Id)); }
    public WebAppEntry Entry { get; private set; }
    private Bitmap? _icon;

    public string Name => Entry.Name;
    public string Url => Entry.Url;
    public string Host => Uri.TryCreate(Entry.Url, UriKind.Absolute, out var u) ? u.Host : Entry.Url;
    public string Description => Entry.Description ?? "";
    public bool HasDescription => !string.IsNullOrWhiteSpace(Entry.Description);
    public string Category => (Entry.Category ?? "").Trim();
    public string CategoryDisplay => Category.Length == 0 ? "Uncategorized" : Category;
    public Bitmap? Icon { get => _icon; set { Set(ref _icon, value); OnChanged(nameof(HasIcon)); OnChanged(nameof(NoIcon)); } }
    public bool HasIcon => Icon != null;
    public bool NoIcon => Icon == null;
    public bool CanShortcut => Platform.IsLinux;
}
