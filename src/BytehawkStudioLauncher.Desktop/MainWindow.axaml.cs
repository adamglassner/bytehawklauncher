using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Bytehawk.Launcher.Models;
using Bytehawk.Launcher.Services;
using Bytehawk.Launcher.Views;

namespace Bytehawk.Launcher.Desktop;

public partial class MainWindow : Window
{
    private static readonly TimeSpan AutoRefreshAge = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ManualRefreshAge = TimeSpan.FromSeconds(60);

    // data
    private List<CatalogEntry> _catalog = new();
    private Dictionary<string, InstalledApp> _installed = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, CachedRelease> _releases = new(StringComparer.OrdinalIgnoreCase);
    private List<LibraryEntry> _library = new();
    private List<WebAppEntry> _webApps = new();

    // cards (all) and what's shown (after search)
    private readonly List<AppVM> _apps = new();
    private readonly List<ServerVM> _servers = new();
    private readonly List<SoftwareVM> _software = new();
    private readonly List<WebAppVM> _web = new();
    private readonly ObservableCollection<AppVM> _appsShown = new();
    private readonly ObservableCollection<ServerVM> _serversShown = new();
    private readonly ObservableCollection<SoftwareVM> _softwareShown = new();
    private readonly ObservableCollection<WebAppVM> _webShown = new();
    private readonly ObservableCollection<DownloadVM> _downloads = new();

    private bool _refreshing;
    private bool _loadingSettings;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(30) };
    private Bitmap? _logoDark, _logoLight;

    public MainWindow()
    {
        InitializeComponent();
        Title = $"Bytehawk Studio Launcher {App.VersionText}";
        SidebarVersion.Text = AboutVersion.Text = $"Version {App.VersionText} · {Platform.Name} ({Platform.ArchLabel})";
        try
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://BytehawkStudioLauncher/Assets/bytehawk-appicon-256.png")));
            _logoDark = new Bitmap(AssetLoader.Open(new Uri("avares://BytehawkStudioLauncher/Assets/logo-mark-ondark-128.png")));
            _logoLight = new Bitmap(AssetLoader.Open(new Uri("avares://BytehawkStudioLauncher/Assets/logo-mark-onlight-128.png")));
        }
        catch (Exception ex) { Log.Error("Logo", ex); }
        if (Application.Current != null) Application.Current.ActualThemeVariantChanged += (_, _) => UpdateLogos();
        UpdateLogos();

        AppsList.ItemsSource = _appsShown;
        ServersList.ItemsSource = _serversShown;
        SoftwareList.ItemsSource = _softwareShown;
        WebList.ItemsSource = _webShown;
        DownloadsList.ItemsSource = _downloads;
        _downloads.CollectionChanged += (_, _) => DownloadsEmpty.IsVisible = _downloads.Count == 0;

        AppsSubtitle.Text = $"Software released by Bytehawk Studio. Apps marked “Coming soon to {Platform.Name}” don't have a {Platform.Name} version yet.";
        ServersSubtitle.Text = Platform.IsLinux
            ? "Run your own servers for Bytehawk apps: install them as a system service, or run them with Docker."
            : "Run your own servers for Bytehawk apps with Docker Desktop on this Mac.";
        SoftwareSubtitle.Text = Platform.IsMac
            ? "Any app or program on this Mac. Works fully offline. Removing an entry never deletes the app."
            : "Any program on this computer: executables, AppImages, scripts or .desktop files. Works offline. Removing an entry never deletes the file.";
        DataFolderText.Text = $"Your data is stored in {AppPaths.Root}";

        AddHandler(Button.ClickEvent, OnClick);
        SearchBox.TextChanged += (_, _) => ApplySearch();
        CheckOnStartupBox.IsCheckedChanged += (_, _) => { if (!_loadingSettings) { App.Settings.CheckUpdatesOnStartup = CheckOnStartupBox.IsChecked == true; App.SaveSettings(); } };
        AutoUpdateBox.IsCheckedChanged += (_, _) => { if (!_loadingSettings) { App.Settings.AutoUpdateApps = AutoUpdateBox.IsChecked == true; App.SaveSettings(); } };

        LoadSettingsIntoUi();
        LoadData();
        RebuildApps();
        RebuildServers();
        RebuildSoftware();
        RebuildWeb();

        Opened += async (_, _) => await StartupAsync();
    }

    private void UpdateLogos()
    {
        var dark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
        SidebarLogo.Source = AboutLogo.Source = dark ? _logoDark : _logoLight;
    }

    // =====================================================================
    // data
    // =====================================================================

    private void LoadData()
    {
        _catalog = LoadCatalogFromCacheOrBuiltIn();
        _installed = new(JsonStore.Load(AppPaths.Installed, () => new Dictionary<string, InstalledApp>()), StringComparer.OrdinalIgnoreCase);
        _releases = new(JsonStore.Load(AppPaths.ReleaseCache, () => new Dictionary<string, CachedRelease>()), StringComparer.OrdinalIgnoreCase);
        _library = JsonStore.Load(AppPaths.Library, () => new List<LibraryEntry>());
        _webApps = JsonStore.Load(AppPaths.WebApps, () => new List<WebAppEntry>());
    }

    private static List<CatalogEntry> ParseCatalog(string json)
    {
        var cat = System.Text.Json.JsonSerializer.Deserialize<CatalogFile>(json, JsonStore.Options);
        return cat?.Apps?.Where(a => !string.IsNullOrWhiteSpace(a.Name)).ToList() ?? new();
    }

    /// <summary>The last downloaded online catalog, or the one built into the launcher.</summary>
    private static List<CatalogEntry> LoadCatalogFromCacheOrBuiltIn()
    {
        try
        {
            if (File.Exists(AppPaths.RemoteCatalogCache))
            {
                var cached = ParseCatalog(File.ReadAllText(AppPaths.RemoteCatalogCache));
                if (cached.Count > 0) return cached;
            }
        }
        catch (Exception ex) { Log.Error("Cached catalog", ex); }
        try
        {
            using var s = typeof(MainWindow).Assembly.GetManifestResourceStream("catalog.json");
            if (s != null) return ParseCatalog(new StreamReader(s).ReadToEnd());
        }
        catch (Exception ex) { Log.Error("Built-in catalog", ex); }
        return new();
    }

    private async Task LoadRemoteCatalogAsync()
    {
        var url = App.Settings.RemoteCatalogUrl?.Trim();
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            var json = await GitHubService.GetTextAsync(url);
            var entries = ParseCatalog(json);
            if (entries.Count == 0) { RemoteCatalogStatus.Text = "The online catalog was empty or not valid — using the last copy."; return; }
            _catalog = entries;
            Directory.CreateDirectory(AppPaths.CacheDir);
            await File.WriteAllTextAsync(AppPaths.RemoteCatalogCache, json);
            RemoteCatalogStatus.Text = $"Catalog loaded: {entries.Count} apps ({_catalog.Count(e => Platform.BuildFor(e) != null)} for {Platform.Name}), {DateTime.Now:h:mm tt}.";
        }
        catch (Exception ex)
        {
            Log.Error("Remote catalog", ex);
            RemoteCatalogStatus.Text = "Couldn't reach the online catalog — using the last downloaded copy.";
        }
        RebuildApps();
        RebuildServers();
    }

    private void TrySave<T>(string path, T value)
    {
        try { JsonStore.Save(path, value); }
        catch (Exception ex) { Log.Error($"Save {path}", ex); _ = Dialogs.Info(this, "Couldn't save", $"{Path.GetFileName(path)}: {ex.Message}"); }
    }

    private async Task StartupAsync()
    {
        _timer.Tick += async (_, _) =>
        {
            if (!App.Settings.CheckUpdatesOnStartup) return;
            await RefreshReleasesAsync(manual: false);
            await CheckLauncherUpdateAsync(manual: false);
        };
        _timer.Start();
        _ = LoadCardIconsAsync();
        _ = RefreshServerStatesAsync();
        await LoadRemoteCatalogAsync();
        if (App.Settings.CheckUpdatesOnStartup)
        {
            await RefreshReleasesAsync(manual: false);
            await CheckLauncherUpdateAsync(manual: false);
        }
        _ = FetchMissingWebIconsAsync();
    }

    // =====================================================================
    // Bytehawk Apps
    // =====================================================================

    private static string ReleaseKey(CatalogEntry e, PlatformBuild b) => e.Repo.Trim() + "|" + (b.TagPrefix ?? "");

    private CachedRelease? ReleaseFor(CatalogEntry e)
    {
        var b = Platform.BuildFor(e);
        return b != null && _releases.TryGetValue(ReleaseKey(e, b), out var r) ? r : null;
    }

    private InstalledApp? InstalledFor(CatalogEntry e) => _installed.TryGetValue(e.Key, out var i) ? i : null;

    private void RebuildApps()
    {
        var keep = _apps.ToDictionary(a => a.Key, StringComparer.OrdinalIgnoreCase);
        _apps.Clear();
        // Apps with a build for this system first, then "Coming soon" ones, each in catalog order.
        foreach (var e in _catalog.OrderBy(e => Platform.BuildFor(e) == null || e.ComingSoon ? 1 : 0))
        {
            var vm = keep.TryGetValue(e.Key, out var old) ? old : new AppVM(e);
            vm.Update(e, InstalledFor(e), ReleaseFor(e));
            _apps.Add(vm);
        }
        ApplySearch();
        _ = LoadCardIconsAsync();
    }

    private void RefreshCard(AppVM vm) => vm.Update(vm.Entry, InstalledFor(vm.Entry), ReleaseFor(vm.Entry));

    private async Task LoadCardIconsAsync()
    {
        foreach (var vm in _apps.Where(a => a.Icon == null).ToList())
            vm.Icon = IconCache.Load(await IconCache.EnsureCatalogIconAsync(vm.Entry));
        foreach (var s in _servers.Where(s => s.Icon == null).ToList())
            s.Icon = IconCache.Load(await IconCache.EnsureCatalogIconAsync(s.App));
    }

    /// <summary>Fetches the latest release of every app that has a build for this system (cached; one call per repo/prefix).</summary>
    private async Task RefreshReleasesAsync(bool manual)
    {
        if (_refreshing) return;
        _refreshing = true;
        CheckUpdatesButton.IsEnabled = false;
        CheckUpdatesText.Text = "Checking…";
        var offline = false;
        var limited = false;
        try
        {
            var maxAge = manual ? ManualRefreshAge : AutoRefreshAge;
            var targets = _catalog.Where(e => e.HasRepo && !e.ComingSoon && Platform.BuildFor(e) is { AssetPattern.Length: > 0 })
                                  .GroupBy(e => ReleaseKey(e, Platform.BuildFor(e)!)).Select(g => g.First()).ToList();
            foreach (var e in targets)
            {
                var b = Platform.BuildFor(e)!;
                var key = ReleaseKey(e, b);
                _releases.TryGetValue(key, out var cached);
                if (cached != null && DateTime.UtcNow - cached.FetchedAt < maxAge) continue;
                var r = string.IsNullOrWhiteSpace(b.TagPrefix)
                    ? await GitHubService.GetLatestReleaseAsync(e.Repo.Trim(), cached)
                    : await GitHubService.GetLatestReleaseWithPrefixAsync(e.Repo.Trim(), b.TagPrefix!);
                if (r.Value != null && r.Status is GitHubStatus.Ok or GitHubStatus.NotModified or GitHubStatus.NoRelease) _releases[key] = r.Value;
                else if (r.Status == GitHubStatus.NetworkError) { offline = true; break; }
                else if (r.Status == GitHubStatus.RateLimited) { limited = true; break; }
            }
            TrySave(AppPaths.ReleaseCache, _releases);
        }
        finally
        {
            foreach (var vm in _apps) RefreshCard(vm);
            _refreshing = false;
            CheckUpdatesButton.IsEnabled = true;
            CheckUpdatesText.Text = "Check for updates";
        }
        ShowAppsBanner(limited ? GitHubService.RateLimitMessage : offline ? "Couldn't reach GitHub — showing last known versions." : null);
        if (!offline && !limited)
        {
            if (manual)
            {
                var n = _apps.Count(a => a.HasUpdate);
                CheckUpdatesText.Text = n == 0 ? "Up to date ✓" : $"{n} update(s) available";
            }
            if (App.Settings.AutoUpdateApps) await AutoUpdateAsync();
        }
    }

    private void ShowAppsBanner(string? text)
    {
        AppsBanner.IsVisible = text != null;
        AppsBannerText.Text = text ?? "";
    }

    /// <summary>Installs updates for apps that don't need a password (not .deb/.rpm) and aren't running.</summary>
    private async Task AutoUpdateAsync()
    {
        foreach (var vm in _apps.Where(a => a.HasUpdate && a.Installed!.Kind is "mac-app" or "appimage" or "archive" && !a.Busy && !a.IsDownloading).ToList())
        {
            if (AppManager.IsRunning(vm.Installed!)) { vm.Note = "Update ready — close the app and click Update."; continue; }
            await InstallAppAsync(vm, automatic: true);
        }
    }

    private string DownloadFolder => string.IsNullOrWhiteSpace(App.Settings.DownloadFolder) ? AppPaths.DefaultDownloads : App.Settings.DownloadFolder!;

    /// <summary>Download → verify → install → record. Used for Download, Update and Reinstall.</summary>
    private async Task InstallAppAsync(AppVM vm, bool automatic = false)
    {
        if (vm.IsDownloading || vm.Busy || vm.IsComingSoon || vm.Build == null) return;
        vm.Note = null;
        vm.Busy = true;
        vm.BusyText = "Checking GitHub…";
        DownloadVM? item = null;
        try
        {
            var e = vm.Entry;
            var b = vm.Build;
            var key = ReleaseKey(e, b);
            _releases.TryGetValue(key, out var release);
            if (release == null || release.NoRelease || DateTime.UtcNow - release.FetchedAt > TimeSpan.FromMinutes(10))
            {
                var r = string.IsNullOrWhiteSpace(b.TagPrefix)
                    ? await GitHubService.GetLatestReleaseAsync(e.Repo.Trim(), release)
                    : await GitHubService.GetLatestReleaseWithPrefixAsync(e.Repo.Trim(), b.TagPrefix!);
                if (r.Value != null && r.Status is GitHubStatus.Ok or GitHubStatus.NotModified or GitHubStatus.NoRelease)
                {
                    release = r.Value;
                    _releases[key] = release;
                    TrySave(AppPaths.ReleaseCache, _releases);
                }
                else if (release == null)
                {
                    vm.Note = r.Status == GitHubStatus.NetworkError ? "Couldn't reach GitHub. Check your connection and try again." : r.Message;
                    return;
                }
            }
            if (release == null || release.NoRelease) { vm.Note = $"There's no {Platform.Name} release on GitHub yet."; return; }
            var asset = GitHubService.MatchAsset(release.Assets, Platform.Pattern(b.AssetPattern));
            if (asset == null)
            {
                vm.Note = $"The latest release has no {Platform.Name} download for {(Platform.IsMac ? "this Mac" : "this computer")} ({Platform.ArchLabel}) yet.";
                return;
            }
            if (vm.Installed != null && AppManager.IsRunning(vm.Installed))
            {
                vm.Note = $"Close {vm.Name} first, then click {vm.PrimaryText}.";
                return;
            }

            Directory.CreateDirectory(DownloadFolder);
            var dest = Path.Combine(DownloadFolder, asset.Name);
            item = new DownloadVM { Title = vm.Name, FileName = asset.Name, Destination = dest };
            _downloads.Insert(0, item);
            vm.Busy = false;
            vm.Download = item;
            try
            {
                await Downloader.DownloadAsync(asset.DownloadUrl, dest, new Progress<DownloadProgress>(item.Report), item.Cts.Token);
            }
            catch (OperationCanceledException)
            {
                item.Status = "Cancelled"; item.Detail = "Download cancelled. Nothing was installed."; item.IsActive = false;
                vm.Download = null;
                return;
            }
            catch (Exception ex)
            {
                Log.Error("Download", ex);
                item.Status = "Failed"; item.Detail = $"Download failed: {ex.Message}"; item.IsActive = false;
                vm.Download = null;
                vm.Note = "Download failed. See Downloads for details.";
                return;
            }
            if (!VerifyDigest(dest, asset.Digest))
            {
                item.Status = "Failed"; item.Detail = "The download didn't match GitHub's checksum, so it wasn't installed."; item.IsActive = false;
                AppManager.TryDelete(dest);
                vm.Download = null;
                vm.Note = "The download was damaged. Try again.";
                return;
            }
            item.Status = "Completed"; item.Percent = 100; item.Indeterminate = false; item.Detail = $"Saved to {dest}"; item.IsActive = false;
            vm.Download = null;

            vm.Busy = true;
            if (Platform.IsLinux) await IconCache.EnsureCatalogIconAsync(e); // for the menu entry
            var status = new Progress<string>(s => vm.BusyText = s);
            var record = await AppManager.InstallAsync(e, b, dest, release.Tag, asset.Name, status);
            _installed[e.Key] = record;
            TrySave(AppPaths.Installed, _installed);
            Log.Info($"Installed {e.Key} {release.Tag} kind={record.Kind} at {record.LaunchPath}");
            if (!automatic && record.Kind == "mac-app")
                vm.Note = $"Installed in {Platform.AppsInstallDir}.";
        }
        catch (Exception ex)
        {
            Log.Error($"Install {vm.Key}", ex);
            vm.Note = ex.Message;
            if (item != null && item.Status == "Completed") item.Detail = "Downloaded, but installing failed: " + ex.Message;
        }
        finally
        {
            if (item != null && item.IsActive) item.IsActive = false;
            vm.Busy = false;
            vm.BusyText = "";
            if (vm.Download is { IsActive: false }) vm.Download = null;
            RefreshCard(vm);
        }
    }

    private static bool VerifyDigest(string file, string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest) || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            using var fs = File.OpenRead(file);
            return Convert.ToHexString(SHA256.HashData(fs)).Equals(digest[7..], StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private async Task UninstallAppAsync(AppVM vm)
    {
        if (vm.Installed is not { } inst) return;
        if (AppManager.IsRunning(inst)) { await Dialogs.Info(this, "Close the app first", $"Close {vm.Name}, then uninstall it."); return; }
        var where = inst.Kind == "mac-app" ? $"\n\n{inst.LaunchPath} will be deleted." : inst.Kind is "deb" or "rpm" ? "\n\nThe system will ask for your password." : "";
        if (!await Dialogs.Confirm(this, $"Uninstall {vm.Name}?", $"The app is removed. Your own files and settings for {vm.Name} are kept.{where}", "Uninstall")) return;
        vm.Busy = true;
        vm.BusyText = "Uninstalling…";
        try
        {
            await AppManager.UninstallAsync(vm.Entry, inst);
            _installed.Remove(vm.Key);
            TrySave(AppPaths.Installed, _installed);
        }
        catch (Exception ex) { vm.Note = ex.Message; }
        finally { vm.Busy = false; vm.BusyText = ""; RefreshCard(vm); }
    }

    private async Task AppShortcutAsync(AppVM vm)
    {
        if (vm.Installed?.LaunchPath is not { } exe) return;
        var icon = await IconCache.EnsureCatalogIconAsync(vm.Entry);
        await MakeShortcut(vm.Name, AppManager.DesktopFile(vm.Name, vm.Entry.Description, exe, icon));
    }

    private async Task MakeShortcut(string name, string desktopFile)
    {
        try
        {
            var path = AppManager.CreateDesktopShortcut(name, desktopFile);
            await Dialogs.Info(this, "Shortcut added", $"Added “{name}” to your desktop.\n{path}");
        }
        catch (Exception ex) { await Dialogs.Info(this, "Couldn't create the shortcut", ex.Message); }
    }

    // =====================================================================
    // Bytehawk Servers
    // =====================================================================

    private void RebuildServers()
    {
        var keep = _servers.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);
        _servers.Clear();
        foreach (var e in _catalog.Where(e => e.Server != null).OrderBy(e => ServerManager.IsAvailable(e.Server!) ? 0 : 1))
        {
            var vm = keep.TryGetValue("server:" + e.Key, out var old) ? old : new ServerVM(e);
            vm.Update(e);
            _servers.Add(vm);
        }
        ApplySearch();
    }

    private async Task RefreshServerStatesAsync()
    {
        foreach (var vm in _servers.ToList())
        {
            if (vm.IsComingSoon) { vm.State = ""; continue; }
            var s = vm.Server;
            var (native, service, container) = await Task.Run(() =>
                (ServerManager.IsNativeInstalled(s), ServerManager.ServiceState(s), ServerManager.HasDocker(s) ? ServerManager.ContainerState(s) : ""));
            vm.NativeInstalled = native;
            vm.ContainerState = container;
            var parts = new List<string>();
            if (native) parts.Add(service == "active" ? "Running as a system service" : service.Length > 0 ? $"Installed as a system service ({service})" : "Installed");
            if (container == "running") parts.Add($"Running in Docker (container “{ServerManager.ContainerName(s)}”)");
            else if (!string.IsNullOrEmpty(container)) parts.Add($"Docker container is {container}");
            if (parts.Count == 0)
                parts.Add(vm.HasNative ? "Not installed" : container == null && vm.HasDocker
                    ? (ServerManager.DockerInstalled ? "Docker isn't running — start Docker Desktop to use this server" : "Needs Docker Desktop (free) to run on this Mac")
                    : "Not running");
            vm.State = string.Join(" · ", parts);
        }
    }

    private async Task InstallServerAsync(ServerVM vm)
    {
        var s = vm.Server;
        vm.Note = null;
        vm.Busy = true;
        vm.BusyText = "Checking GitHub…";
        try
        {
            var repo = string.IsNullOrWhiteSpace(s.Repo) ? vm.App.Repo : s.Repo!;
            var r = await GitHubService.GetLatestReleaseWithPrefixAsync(repo.Trim(), s.TagPrefix);
            if (r.Value == null || r.Value.NoRelease) { vm.Note = r.Message ?? "No server release found."; return; }
            var asset = GitHubService.MatchAsset(r.Value.Assets, Platform.Pattern(s.LinuxAsset!));
            if (asset == null) { vm.Note = $"The latest server release has no Linux {Platform.Arch} package."; return; }
            Directory.CreateDirectory(DownloadFolder);
            var dest = Path.Combine(DownloadFolder, asset.Name);
            var item = new DownloadVM { Title = s.Name, FileName = asset.Name, Destination = dest };
            _downloads.Insert(0, item);
            vm.BusyText = "Downloading…";
            item.PropertyChanged += (_, _) => vm.BusyText = "Downloading… " + item.Detail;
            await Downloader.DownloadAsync(asset.DownloadUrl, dest, new Progress<DownloadProgress>(item.Report), item.Cts.Token);
            item.Status = "Completed"; item.Detail = $"Saved to {dest}"; item.IsActive = false;
            if (!VerifyDigest(dest, asset.Digest)) { vm.Note = "The download was damaged. Try again."; return; }
            await ServerManager.InstallNativeAsync(vm.Key, dest, new Progress<string>(t => vm.BusyText = t));
            vm.Note = $"{s.Name} {r.Value.Tag[(s.TagPrefix.Length)..]} is installed. Open the web panel to finish setup.";
        }
        catch (OperationCanceledException) { vm.Note = "Cancelled."; }
        catch (Exception ex) { Log.Error("Server install", ex); vm.Note = ex.Message; }
        finally { vm.Busy = false; vm.BusyText = ""; await RefreshServerStatesAsync(); }
    }

    private async Task UninstallServerAsync(ServerVM vm)
    {
        var (ok, purge) = await Dialogs.ConfirmWithOption(this, $"Uninstall {vm.Name}?",
            "The server's system service is removed. The system will ask for your password.", "Uninstall", "Cancel",
            "Also delete the server's data (accounts and everything stored on it). This can't be undone.");
        if (!ok) return;
        vm.Busy = true; vm.BusyText = "Uninstalling…"; vm.Note = null;
        try { await ServerManager.UninstallNativeAsync(vm.Key, vm.Server, purge); vm.Note = purge ? "Removed, including its data." : "Removed. Its data was kept."; }
        catch (Exception ex) { vm.Note = ex.Message; }
        finally { vm.Busy = false; vm.BusyText = ""; await RefreshServerStatesAsync(); }
    }

    private async Task DockerRunAsync(ServerVM vm)
    {
        if (!ServerManager.DockerInstalled)
        {
            await Dialogs.ShowCommand(this, "Docker is needed",
                $"{vm.Name} runs in Docker on {Platform.Name}. Install Docker {(Platform.IsMac ? "Desktop (free for personal use) from docker.com" : "Engine")}, start it, then come back and click Run with Docker. Or run this yourself:",
                vm.DockerCommand);
            return;
        }
        vm.Busy = true; vm.Note = null;
        try
        {
            await ServerManager.DockerRunAsync(vm.Server, new Progress<string>(t => vm.BusyText = t));
            vm.Note = $"Started. The web panel is at {vm.PanelUrl} (it can take a few seconds to come up).";
        }
        catch (Exception ex) { vm.Note = ex.Message; }
        finally { vm.Busy = false; vm.BusyText = ""; await RefreshServerStatesAsync(); }
    }

    private async Task DockerStopAsync(ServerVM vm)
    {
        var (ok, purge) = await Dialogs.ConfirmWithOption(this, $"Stop {vm.Name}?",
            $"The Docker container “{ServerManager.ContainerName(vm.Server)}” is stopped and removed. Its data volume is kept, so starting it again picks up where it left off.",
            "Stop", "Cancel", "Also delete the server's data volume. This can't be undone.");
        if (!ok) return;
        vm.Busy = true; vm.BusyText = "Stopping…"; vm.Note = null;
        try { await ServerManager.DockerStopAsync(vm.Server, purge); }
        catch (Exception ex) { vm.Note = ex.Message; }
        finally { vm.Busy = false; vm.BusyText = ""; await RefreshServerStatesAsync(); }
    }

    // =====================================================================
    // My Software & Web Apps
    // =====================================================================

    private IEnumerable<string> AllCategories() =>
        _library.Select(x => x.Category).Concat(_webApps.Select(x => x.Category))
            .Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();

    private void RebuildSoftware()
    {
        _software.Clear();
        foreach (var e in _library.OrderBy(e => string.IsNullOrWhiteSpace(e.Category) ? "￿" : e.Category, StringComparer.CurrentCultureIgnoreCase)
                                   .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
            _software.Add(new SoftwareVM(e));
        ApplySearch();
    }

    private void RebuildWeb()
    {
        _web.Clear();
        foreach (var e in _webApps.OrderBy(e => string.IsNullOrWhiteSpace(e.Category) ? "￿" : e.Category, StringComparer.CurrentCultureIgnoreCase)
                                   .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
            _web.Add(new WebAppVM(e));
        ApplySearch();
    }

    private string? LaunchSoftware(LibraryEntry e)
    {
        var p = e.Path;
        var args = Platform.SplitArgs(e.Arguments);
        if (!File.Exists(p) && !Directory.Exists(p)) return $"It isn't there anymore:\n{p}\n\nUse Edit to point to it again.";
        if (Platform.IsMac)
        {
            if (p.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                return Platform.Start("/usr/bin/open", args.Count == 0 ? new[] { p } : new[] { p, "--args" }.Concat(args));
            return Platform.IsExecutable(p) && !Directory.Exists(p) ? Platform.Start(p, args, Path.GetDirectoryName(p)) : Platform.Open(p);
        }
        if (p.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase))
        {
            if (Platform.HasCommand("gio")) return Platform.Start(Platform.Which("gio"), new[] { "launch", p });
            var exec = IconCache.DesktopEntryValue(p, "Exec");
            if (string.IsNullOrWhiteSpace(exec)) return "That .desktop file has no Exec line.";
            exec = System.Text.RegularExpressions.Regex.Replace(exec, "%[fFuUdDnNickvm]", "").Trim();
            return Platform.Start("/bin/sh", new[] { "-c", exec });
        }
        if (Platform.IsExecutable(p)) return Platform.Start(p, args, Path.GetDirectoryName(p));
        return Platform.Open(p);
    }

    private async Task AddSoftwareAsync()
    {
        var path = await Dialogs.PickProgram(this);
        if (path == null) return;
        var e = await Dialogs.EditSoftware(this, null, path, AllCategories());
        if (e == null) return;
        _library.Add(e);
        TrySave(AppPaths.Library, _library);
        RebuildSoftware();
    }

    private async Task SoftwareShortcutAsync(SoftwareVM vm)
    {
        var e = vm.Entry;
        if (e.Path.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase) && File.Exists(e.Path))
            await MakeShortcut(e.Name, await File.ReadAllTextAsync(e.Path));
        else
        {
            string? icon = null;
            var cached = Path.Combine(IconCache.Dir, $"sw-{e.Id}.png");
            if (vm.Icon != null) { try { vm.Icon.Save(cached); icon = cached; } catch { /* no icon */ } }
            var args = string.Join(' ', Platform.SplitArgs(e.Arguments).Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
            await MakeShortcut(e.Name, AppManager.DesktopFile(e.Name, e.Description, e.Path, icon, args));
        }
    }

    private async Task<bool> FetchWebIconAsync(WebAppEntry e)
    {
        var ok = await IconCache.FetchWebIconAsync(e.Url, IconCache.WebIconPath(e.Id));
        if (ok) foreach (var vm in _web.Where(w => w.Entry.Id == e.Id)) vm.Icon = IconCache.Load(IconCache.WebIconPath(e.Id));
        return ok;
    }

    private readonly HashSet<string> _iconTried = new();
    private async Task FetchMissingWebIconsAsync()
    {
        foreach (var vm in _web.Where(w => w.NoIcon).ToList())
            if (_iconTried.Add(vm.Entry.Id)) await FetchWebIconAsync(vm.Entry);
    }

    private async Task ExportWebAppsAsync()
    {
        if (_webApps.Count == 0) { await Dialogs.Info(this, "Nothing to export", "There are no web apps to export yet."); return; }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export web apps",
            SuggestedFileName = $"Bytehawk web apps {DateTime.Now:yyyy-MM-dd}",
            DefaultExtension = "json",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Bytehawk web apps (.json)") { Patterns = new[] { "*.json" } },
                new FilePickerFileType("Browser bookmarks (.html)") { Patterns = new[] { "*.html" } },
            },
        });
        var path = file?.TryGetLocalPath();
        if (path == null) return;
        try
        {
            await File.WriteAllTextAsync(path, path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                ? WebAppTransfer.ToBookmarksHtml(_webApps) : WebAppTransfer.ToJson(_webApps));
            await Dialogs.Info(this, "Exported", $"Exported {_webApps.Count} web app(s) to\n{path}");
        }
        catch (Exception ex) { await Dialogs.Info(this, "Couldn't export", ex.Message); }
    }

    private async Task ImportWebAppsAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import web apps",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Web apps or bookmarks") { Patterns = new[] { "*.json", "*.html", "*.htm" } }, FilePickerFileTypes.All },
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path == null) return;
        List<WebAppEntry> incoming;
        try { incoming = WebAppTransfer.Read(await File.ReadAllTextAsync(path)); }
        catch (Exception ex) { await Dialogs.Info(this, "Couldn't read that file", ex.Message); return; }
        if (incoming.Count == 0) { await Dialogs.Info(this, "Nothing to import", "No web addresses were found in that file."); return; }
        var known = new HashSet<string>(_webApps.Select(w => WebAppTransfer.UrlKey(w.Url)), StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var w in incoming) if (known.Add(WebAppTransfer.UrlKey(w.Url))) { _webApps.Add(w); added++; }
        TrySave(AppPaths.WebApps, _webApps);
        RebuildWeb();
        _ = FetchMissingWebIconsAsync();
        var skipped = incoming.Count - added;
        await Dialogs.Info(this, "Imported", $"Imported {added} web app(s)." + (skipped > 0 ? $" Skipped {skipped} already in your list." : ""));
    }

    // =====================================================================
    // Launcher self-update
    // =====================================================================

    private CachedRelease? _launcherRelease;
    private DownloadVM? _launcherDownload;

    private async Task CheckLauncherUpdateAsync(bool manual)
    {
        CheckLauncherButton.IsEnabled = false;
        if (manual) LauncherUpdateStatus.Text = "Checking GitHub…";
        try
        {
            var r = await GitHubService.GetLatestReleaseWithPrefixAsync(SelfUpdater.Repo, SelfUpdater.TagPrefix);
            if (r.Value != null) _launcherRelease = r.Value;
            else if (manual) { LauncherUpdateStatus.Text = r.Message ?? "Couldn't check for launcher updates."; return; }
        }
        finally { CheckLauncherButton.IsEnabled = true; }

        if (SelfUpdater.IsNewer(_launcherRelease))
        {
            var v = SelfUpdater.VersionOf(_launcherRelease!);
            UpdateText.Text = $"Bytehawk Studio Launcher {v} is available — you have {App.VersionText}.";
            LauncherUpdateStatus.Text = $"Version {v} is available.";
            UpdateBanner.IsVisible = true;
        }
        else
        {
            UpdateBanner.IsVisible = false;
            LauncherUpdateStatus.Text = _launcherRelease == null || _launcherRelease.NoRelease
                ? "No macOS/Linux launcher releases published yet."
                : $"You have the latest version ({App.VersionText}). Checked {DateTime.Now:h:mm tt}.";
        }
    }

    private async Task UpdateLauncherAsync()
    {
        if (_launcherDownload?.IsActive == true) { _launcherDownload.Cts.Cancel(); return; }
        if (_launcherRelease == null || SelfUpdater.Asset(_launcherRelease) is not { } asset) return;
        var dest = Path.Combine(DownloadFolder, asset.Name);
        var item = new DownloadVM { Title = "Bytehawk Studio Launcher", FileName = asset.Name, Destination = dest };
        _launcherDownload = item;
        _downloads.Insert(0, item);
        UpdateNowButton.Content = "Cancel";
        item.PropertyChanged += (_, _) => { if (item.IsActive) UpdateText.Text = "Downloading the launcher update… " + item.Detail; };
        try
        {
            Directory.CreateDirectory(DownloadFolder);
            await Downloader.DownloadAsync(asset.DownloadUrl, dest, new Progress<DownloadProgress>(item.Report), item.Cts.Token);
            item.IsActive = false; item.Status = "Completed"; item.Detail = $"Saved to {dest}";
            if (!VerifyDigest(dest, asset.Digest)) { UpdateText.Text = "The update download was damaged. Try again."; return; }
            UpdateText.Text = "Installing the update… the launcher will close and reopen.";
            var err = await SelfUpdater.InstallAndRestartAsync(dest);
            if (err != null) { await Dialogs.Info(this, "Couldn't update", err); return; }
            (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }
        catch (OperationCanceledException) { item.Status = "Cancelled"; item.Detail = "Launcher update cancelled."; UpdateText.Text = "Update cancelled."; }
        catch (Exception ex) { item.Status = "Failed"; item.Detail = ex.Message; UpdateText.Text = "Couldn't download the update: " + ex.Message; }
        finally { item.IsActive = false; UpdateNowButton.Content = "Update now"; }
    }

    // =====================================================================
    // navigation, search, theme, settings
    // =====================================================================

    private void ShowPage(string page)
    {
        PageApps.IsVisible = page == "apps";
        PageServers.IsVisible = page == "servers";
        PageSoftware.IsVisible = page == "software";
        PageWeb.IsVisible = page == "web";
        PageDownloads.IsVisible = page == "downloads";
        PageSettings.IsVisible = page == "settings";
        foreach (var (b, p) in new[] { (NavApps, "apps"), (NavServers, "servers"), (NavSoftware, "software"), (NavWeb, "web"), (NavDownloads, "downloads"), (NavSettings, "settings") })
            b.Classes.Set("selected", p == page);
        if (page == "downloads") DownloadsFolderText.Text = $"Saving to {DownloadFolder}";
        if (page == "servers") _ = RefreshServerStatesAsync();
        if (page == "software") foreach (var s in _software) s.Reload();
    }

    private bool Matches(params string?[] fields)
    {
        var q = SearchBox.Text?.Trim();
        return string.IsNullOrEmpty(q) || fields.Any(f => f?.Contains(q, StringComparison.CurrentCultureIgnoreCase) == true);
    }

    private void ApplySearch()
    {
        static void Fill<T>(ObservableCollection<T> shown, IEnumerable<T> items)
        {
            shown.Clear();
            foreach (var i in items) shown.Add(i);
        }
        Fill(_appsShown, _apps.Where(a => Matches(a.Name, a.Description)));
        Fill(_serversShown, _servers.Where(s => Matches(s.Name, s.App.Name)));
        Fill(_softwareShown, _software.Where(s => Matches(s.Name, s.Category)));
        Fill(_webShown, _web.Where(w => Matches(w.Name, w.Category, w.Host)));
        var searching = !string.IsNullOrWhiteSpace(SearchBox.Text);
        AppsEmpty.IsVisible = _appsShown.Count == 0;
        ServersEmpty.IsVisible = _serversShown.Count == 0;
        SoftwareEmpty.IsVisible = _softwareShown.Count == 0;
        SoftwareEmpty.Text = searching ? "No software matches your search." : "Nothing here yet. Use “Add software” to add any program on this computer.";
        WebEmpty.IsVisible = _webShown.Count == 0;
        WebEmpty.Text = searching ? "No web apps match your search." : "No web apps yet. Use “Add web app” to bookmark a site.";
    }

    private void SetTheme(string mode)
    {
        App.Settings.Theme = App.NormalizeTheme(mode);
        App.SaveSettings();
        App.ApplyTheme(App.Settings.Theme);
        SyncThemeButtons();
    }

    private void SyncThemeButtons()
    {
        var m = App.NormalizeTheme(App.Settings.Theme);
        ThemeLight.Classes.Set("selected", m == "Light");
        ThemeDark.Classes.Set("selected", m == "Dark");
        ThemeSystem.Classes.Set("selected", m == "System");
        SetThemeLight.Classes.Set("selected", m == "Light");
        SetThemeDark.Classes.Set("selected", m == "Dark");
        SetThemeSystem.Classes.Set("selected", m == "System");
    }

    private void LoadSettingsIntoUi()
    {
        _loadingSettings = true;
        SyncThemeButtons();
        DownloadFolderBox.Text = DownloadFolder;
        CheckOnStartupBox.IsChecked = App.Settings.CheckUpdatesOnStartup;
        AutoUpdateBox.IsChecked = App.Settings.AutoUpdateApps;
        RemoteCatalogBox.Text = App.Settings.RemoteCatalogUrl ?? "";
        _loadingSettings = false;
    }

    private async Task ChangeDownloadFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose the download folder", AllowMultiple = false });
        var path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (path == null) return;
        App.Settings.DownloadFolder = path;
        App.SaveSettings();
        DownloadFolderBox.Text = DownloadFolder;
    }

    private async Task SaveRemoteCatalogAsync()
    {
        var url = (RemoteCatalogBox.Text ?? "").Trim();
        if (url.Length > 0 && (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)))
        {
            RemoteCatalogStatus.Text = "Enter a full http(s) URL, or leave it blank to use the built-in list.";
            return;
        }
        App.Settings.RemoteCatalogUrl = url;
        App.SaveSettings();
        if (url.Length == 0)
        {
            AppManager.TryDelete(AppPaths.RemoteCatalogCache);
            _catalog = LoadCatalogFromCacheOrBuiltIn();
            RebuildApps(); RebuildServers();
            RemoteCatalogStatus.Text = "Using the list built into the launcher.";
            return;
        }
        await LoadRemoteCatalogAsync();
        await RefreshReleasesAsync(manual: false);
    }

    // =====================================================================
    // one click handler for every button (buttons carry a Tag)
    // =====================================================================

    private async void OnClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button { Tag: string tag } button) return;
        var ctx = button.DataContext;
        try
        {
            switch (tag)
            {
                case "nav-apps": ShowPage("apps"); break;
                case "nav-servers": ShowPage("servers"); break;
                case "nav-software": ShowPage("software"); break;
                case "nav-web": ShowPage("web"); break;
                case "nav-downloads": ShowPage("downloads"); break;
                case "nav-settings": ShowPage("settings"); break;
                case "theme-Light": SetTheme("Light"); break;
                case "theme-Dark": SetTheme("Dark"); break;
                case "theme-System": SetTheme("System"); break;

                // apps
                case "apps-check": await RefreshReleasesAsync(manual: true); break;
                case "app-primary" when ctx is AppVM a:
                    if (!a.IsInstalled || a.IsMissing || a.HasUpdate) await InstallAppAsync(a);
                    else await Report(AppManager.Launch(a.Installed!));
                    break;
                case "app-launch" when ctx is AppVM a: await Report(AppManager.Launch(a.Installed!)); break;
                case "app-cancel" when ctx is AppVM a: a.Download?.Cts.Cancel(); break;
                case "app-release" when ctx is AppVM a: await Report(Platform.Open(a.ReleasePageUrl)); break;
                case "app-uninstall" when ctx is AppVM a: await UninstallAppAsync(a); break;
                case "app-shortcut" when ctx is AppVM a: await AppShortcutAsync(a); break;

                // servers
                case "servers-refresh": await RefreshServerStatesAsync(); break;
                case "srv-install" when ctx is ServerVM s: await InstallServerAsync(s); break;
                case "srv-uninstall" when ctx is ServerVM s: await UninstallServerAsync(s); break;
                case "srv-panel" when ctx is ServerVM s: await Report(Platform.Open(s.PanelUrl)); break;
                case "srv-docker-run" when ctx is ServerVM s: await DockerRunAsync(s); break;
                case "srv-docker-stop" when ctx is ServerVM s: await DockerStopAsync(s); break;
                case "srv-docker-cmd" when ctx is ServerVM s:
                    await Dialogs.ShowCommand(this, $"Run {s.Name} with Docker", "Run this in a terminal. Your data is kept in a Docker volume, so updating is just running it again.", s.DockerCommand);
                    break;
                case "srv-guide" when ctx is ServerVM s: await Report(Platform.Open(s.Server.GuideUrl!)); break;

                // my software
                case "sw-add": await AddSoftwareAsync(); break;
                case "sw-launch" when ctx is SoftwareVM s: await Report(LaunchSoftware(s.Entry)); s.Reload(); break;
                case "sw-website" when ctx is SoftwareVM s: await Report(Platform.Open(s.Website)); break;
                case "sw-reveal" when ctx is SoftwareVM s: await Report(Platform.Reveal(s.Entry.Path)); break;
                case "sw-shortcut" when ctx is SoftwareVM s: await SoftwareShortcutAsync(s); break;
                case "sw-edit" when ctx is SoftwareVM s:
                {
                    var edited = await Dialogs.EditSoftware(this, s.Entry, null, AllCategories());
                    if (edited == null) break;
                    var i = _library.FindIndex(x => x.Id == s.Entry.Id);
                    if (i >= 0) _library[i] = edited; else _library.Add(edited);
                    TrySave(AppPaths.Library, _library);
                    RebuildSoftware();
                    break;
                }
                case "sw-remove" when ctx is SoftwareVM s:
                    if (!await Dialogs.Confirm(this, $"Remove {s.Name}?", "It's removed from My Software only. The program itself is not deleted.", "Remove")) break;
                    _library.RemoveAll(x => x.Id == s.Entry.Id);
                    TrySave(AppPaths.Library, _library);
                    RebuildSoftware();
                    break;

                // web apps
                case "web-add":
                {
                    var w = await Dialogs.EditWebApp(this, null, AllCategories());
                    if (w == null) break;
                    _webApps.Add(w);
                    TrySave(AppPaths.WebApps, _webApps);
                    RebuildWeb();
                    _ = FetchWebIconAsync(w);
                    break;
                }
                case "web-launch" when ctx is WebAppVM w: await Report(Platform.Open(w.Url)); break;
                case "web-icon" when ctx is WebAppVM w:
                    if (!await FetchWebIconAsync(w.Entry))
                        await Dialogs.Info(this, "No icon found", $"Couldn't get an icon from {w.Host}. The site may be offline or only offer an SVG icon.");
                    break;
                case "web-shortcut" when ctx is WebAppVM w:
                {
                    var icon = File.Exists(IconCache.WebIconPath(w.Entry.Id)) ? IconCache.WebIconPath(w.Entry.Id) : null;
                    var text = $"[Desktop Entry]\nType=Link\nName={w.Name}\nURL={w.Url}\n" + (icon != null ? $"Icon={icon}\n" : "");
                    await MakeShortcut(w.Name, text);
                    break;
                }
                case "web-edit" when ctx is WebAppVM w:
                {
                    var edited = await Dialogs.EditWebApp(this, w.Entry, AllCategories());
                    if (edited == null) break;
                    var i = _webApps.FindIndex(x => x.Id == w.Entry.Id);
                    if (i >= 0) _webApps[i] = edited; else _webApps.Add(edited);
                    TrySave(AppPaths.WebApps, _webApps);
                    if (!string.Equals(edited.Url, w.Url, StringComparison.OrdinalIgnoreCase)) AppManager.TryDelete(IconCache.WebIconPath(edited.Id));
                    RebuildWeb();
                    _ = FetchWebIconAsync(edited);
                    break;
                }
                case "web-remove" when ctx is WebAppVM w:
                    if (!await Dialogs.Confirm(this, $"Remove {w.Name}?", "It's removed from Web Apps.", "Remove")) break;
                    _webApps.RemoveAll(x => x.Id == w.Entry.Id);
                    TrySave(AppPaths.WebApps, _webApps);
                    AppManager.TryDelete(IconCache.WebIconPath(w.Entry.Id));
                    RebuildWeb();
                    break;
                case "web-export": await ExportWebAppsAsync(); break;
                case "web-import": await ImportWebAppsAsync(); break;

                // downloads
                case "dl-cancel" when ctx is DownloadVM d: d.Cts.Cancel(); break;
                case "dl-reveal" when ctx is DownloadVM d: await Report(Platform.Reveal(d.Destination)); break;
                case "dl-folder": Directory.CreateDirectory(DownloadFolder); await Report(Platform.Open(DownloadFolder)); break;

                // settings & launcher
                case "set-folder": await ChangeDownloadFolderAsync(); break;
                case "set-folder-reset": App.Settings.DownloadFolder = null; App.SaveSettings(); DownloadFolderBox.Text = DownloadFolder; break;
                case "set-catalog": await SaveRemoteCatalogAsync(); break;
                case "set-datafolder": await Report(Platform.Open(AppPaths.Root)); break;
                case "launcher-check": await CheckLauncherUpdateAsync(manual: true); break;
                case "launcher-update": await UpdateLauncherAsync(); break;
                case "launcher-notes": await Report(Platform.Open(_launcherRelease?.HtmlUrl ?? $"https://github.com/{SelfUpdater.Repo}/releases")); break;
                case "launcher-dismiss": UpdateBanner.IsVisible = false; break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Button {tag}", ex);
            await Dialogs.Info(this, "Something went wrong", ex.Message);
        }
    }

    private async Task Report(string? error)
    {
        if (error != null) await Dialogs.Info(this, "Couldn't open it", error);
    }
}
