using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Bytehawk.Launcher.Models;
using Bytehawk.Launcher.Services;
using Bytehawk.Launcher.ViewModels;
using Bytehawk.Launcher.Views;
using Microsoft.Win32;

namespace Bytehawk.Launcher;

public partial class MainWindow : Window
{
    private const string AppTitle = "Bytehawk Studio Launcher";
    /// <summary>Automatic (startup) checks reuse cached release info younger than this.</summary>
    private static readonly TimeSpan AutoRefreshAge = TimeSpan.FromMinutes(30);
    /// <summary>Manual checks reuse release info younger than this (prevents hammering the API).</summary>
    private static readonly TimeSpan ManualRefreshAge = TimeSpan.FromSeconds(60);

    // data
    private CatalogFile _localCatalog = new();
    private List<CatalogEntry> _remoteEntries = new();
    private Dictionary<string, InstalledApp> _installed = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, CachedRelease> _releases = new(StringComparer.OrdinalIgnoreCase);
    private List<LibraryEntry> _library = new();
    private List<WebAppEntry> _webApps = new();

    // view models
    private readonly ObservableCollection<BytehawkAppVM> _apps = new();
    private readonly ObservableCollection<SoftwareVM> _software = new();
    private readonly ObservableCollection<WebAppVM> _web = new();
    private readonly ObservableCollection<DownloadItemVM> _downloads = new();

    private bool _syncingTheme;
    private bool _refreshing;
    private bool _loadingSettings;

    // launcher self-update
    private DownloadItemVM? _launcherDownload;

    public MainWindow()
    {
        InitializeComponent();
        Title = $"{AppTitle} {App.VersionText}";
        SidebarVersion.Text = AboutVersion.Text = $"Version {App.VersionText}";
        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);
        ThemeService.ThemeChanged += () => ThemeService.ApplyTitleBar(this);

        BytehawkList.ItemsSource = _apps;
        SoftwareList.ItemsSource = _software;
        WebList.ItemsSource = _web;
        DownloadsList.ItemsSource = _downloads;
        _downloads.CollectionChanged += (_, _) =>
            DownloadsEmpty.Visibility = _downloads.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        CollectionViewSource.GetDefaultView(_apps).Filter = o => MatchesSearch(((BytehawkAppVM)o).Name);
        CollectionViewSource.GetDefaultView(_software).Filter = o => MatchesSearch(((SoftwareVM)o).Name);
        CollectionViewSource.GetDefaultView(_web).Filter = o => MatchesSearch(((WebAppVM)o).Name);

        LoadSettingsIntoUi();
        LoadLocalData();
        RebuildAppCards();
        RebuildSoftwareCards();
        RebuildWebCards();
        UpdateEmptyStates();

        Loaded += async (_, _) => await StartupAsync();
    }

    // =====================================================================
    // Startup & data
    // =====================================================================

    private void LoadLocalData()
    {
        if (!File.Exists(AppPaths.Catalog))
        {
            _localCatalog = SampleCatalog();
            TrySave(AppPaths.Catalog, _localCatalog);
        }
        else
        {
            _localCatalog = JsonStore.Load(AppPaths.Catalog, () => new CatalogFile());
        }
        _localCatalog.Apps ??= new();

        _installed = new(JsonStore.Load(AppPaths.Installed, () => new Dictionary<string, InstalledApp>()), StringComparer.OrdinalIgnoreCase);
        _releases = new(JsonStore.Load(AppPaths.ReleaseCache, () => new Dictionary<string, CachedRelease>()), StringComparer.OrdinalIgnoreCase);
        _library = JsonStore.Load(AppPaths.Library, () => new List<LibraryEntry>());
        _webApps = JsonStore.Load(AppPaths.WebApps, () => new List<WebAppEntry>());

        // Use the last successfully downloaded remote catalog until a fresh one arrives.
        if (!string.IsNullOrWhiteSpace(App.Settings.RemoteCatalogUrl) && File.Exists(AppPaths.RemoteCatalogCache))
            _remoteEntries = ParseRemoteCatalog(File.ReadAllText(AppPaths.RemoteCatalogCache)) ?? new();

        if (JsonStore.LastLoadError is { } err)
        {
            JsonStore.LastLoadError = null;
            Dispatcher.InvokeAsync(() => MessageBox.Show(this, err, AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning));
        }
    }

    private static CatalogFile SampleCatalog() => new()
    {
        Version = 1,
        Apps =
        {
            new CatalogEntry
            {
                Id = "sample-installer-exe",
                Name = "Sample — replace me (installer .exe)",
                Description = "Sample — replace me. Placeholder that points at a public open-source repo (Notepad++, not a Bytehawk Studio product) only to demonstrate the download → run installer flow.",
                Repo = "notepad-plus-plus/notepad-plus-plus",
                AssetPattern = "*.Installer.x64.exe",
                Sample = true,
            },
            new CatalogEntry
            {
                Id = "sample-portable-zip",
                Name = "Sample — replace me (portable .zip)",
                Description = "Sample — replace me. Placeholder that points at the same public repo (Notepad++, not a Bytehawk Studio product) only to demonstrate the download → extract .zip → launch flow.",
                Repo = "notepad-plus-plus/notepad-plus-plus",
                AssetPattern = "*.portable.x64.zip",
                Sample = true,
            },
        },
    };

    private async Task StartupAsync()
    {
        if (!string.IsNullOrWhiteSpace(App.Settings.RemoteCatalogUrl))
            await LoadRemoteCatalogAsync(showResult: false);

        if (App.Settings.CheckUpdatesOnStartup)
        {
            await RefreshReleasesAsync(manual: false);
            await CheckLauncherUpdateAsync(manual: false);
        }
        else
            ShowBanner(null);
    }

    // =====================================================================
    // Launcher self-update (GitHub Releases on LauncherUpdater.Repo)
    // =====================================================================

    private async Task CheckLauncherUpdateAsync(bool manual)
    {
        var repo = LauncherUpdater.Repo;
        _releases.TryGetValue(repo, out var cached);
        var maxAge = manual ? ManualRefreshAge : AutoRefreshAge;
        CheckLauncherButton.IsEnabled = false;
        try
        {
            if (cached == null || DateTime.UtcNow - cached.FetchedAt >= maxAge)
            {
                if (manual) LauncherUpdateStatus.Text = "Checking GitHub…";
                var r = await GitHubService.GetLatestReleaseAsync(repo, cached);
                if (r.Value != null && r.Status is GitHubStatus.Ok or GitHubStatus.NotModified or GitHubStatus.NoRelease)
                {
                    cached = r.Value;
                    _releases[repo] = cached;
                    TrySave(AppPaths.ReleaseCache, _releases);
                }
                else if (manual)
                {
                    LauncherUpdateStatus.Text = r.Status == GitHubStatus.NetworkError
                        ? "Couldn't reach GitHub. Try again later."
                        : r.Message ?? "Couldn't check for launcher updates.";
                    return;
                }
            }
        }
        finally { CheckLauncherButton.IsEnabled = true; }

        if (LauncherUpdater.IsNewer(cached))
        {
            var v = GitHubService.DisplayVersion(cached!.Tag);
            LauncherUpdateText.Text = $"Bytehawk Studio Launcher {v} is available — you have {App.VersionText}.";
            LauncherUpdateStatus.Text = $"Version {v} is available.";
            LauncherUpdateBanner.Visibility = Visibility.Visible;
        }
        else
        {
            LauncherUpdateBanner.Visibility = Visibility.Collapsed;
            LauncherUpdateStatus.Text = cached == null || cached.NoRelease
                ? "No launcher releases published yet."
                : $"You have the latest version ({App.VersionText}). Checked {cached.FetchedAt.ToLocalTime():t}.";
        }
    }

    private async void CheckLauncherUpdate_Click(object sender, RoutedEventArgs e) => await CheckLauncherUpdateAsync(manual: true);

    private void LauncherReleaseNotes_Click(object sender, RoutedEventArgs e)
    {
        _releases.TryGetValue(LauncherUpdater.Repo, out var rel);
        ReportError(ShellHelper.OpenUrl(rel?.HtmlUrl ?? $"https://github.com/{LauncherUpdater.Repo}/releases"));
    }

    private void LauncherUpdateDismiss_Click(object sender, RoutedEventArgs e) => LauncherUpdateBanner.Visibility = Visibility.Collapsed;

    private async void LauncherUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_launcherDownload?.IsActive == true) { _launcherDownload.Cts.Cancel(); return; }
        if (!_releases.TryGetValue(LauncherUpdater.Repo, out var rel) || !LauncherUpdater.IsNewer(rel)) return;
        var asset = LauncherUpdater.SetupAsset(rel);
        if (asset == null)
        {
            ReportError("This release doesn't include a Setup .exe yet. Use “What's new” to download it from GitHub.");
            return;
        }
        var dest = Path.Combine(DownloadFolder, SafeFileName(asset.Name));
        var item = new DownloadItemVM { AppName = "Bytehawk Studio Launcher", FileName = asset.Name, Destination = dest };
        _launcherDownload = item;
        _downloads.Insert(0, item);
        LauncherUpdateButton.Content = "Cancel";
        var v = GitHubService.DisplayVersion(rel.Tag);
        void ShowProgress(object? _, PropertyChangedEventArgs __) => LauncherUpdateText.Text = $"Downloading launcher {v}… {item.Detail}";
        item.PropertyChanged += ShowProgress;
        try
        {
            await Downloader.DownloadAsync(asset.DownloadUrl, dest, new Progress<DownloadProgress>(item.Report), item.Cts.Token);
            item.Status = "Completed";
            item.Percent = 100;
            item.Detail = $"Saved to {dest}";
            item.IsActive = false;
            item.PropertyChanged -= ShowProgress;
            LauncherUpdateText.Text = $"Installing launcher {v}… the launcher will close and reopen.";
            var err = LauncherUpdater.RunInstallerAndExit(dest);
            if (err != null) { ReportError(err); LauncherUpdateText.Text = err; }
        }
        catch (OperationCanceledException)
        {
            item.Status = "Cancelled";
            item.Detail = "Launcher update cancelled.";
            LauncherUpdateText.Text = $"Bytehawk Studio Launcher {v} is available — you have {App.VersionText}.";
        }
        catch (Exception ex)
        {
            Log.Error("Launcher update download", ex);
            item.Status = "Failed";
            item.Detail = $"Download failed: {ex.Message}";
            LauncherUpdateText.Text = $"Couldn't download launcher {v}: {ex.Message}";
        }
        finally
        {
            item.PropertyChanged -= ShowProgress;
            item.IsActive = false;
            LauncherUpdateButton.Content = "Update now";
        }
    }

    private IEnumerable<CatalogEntry> AllEntries()
    {
        var remoteKeys = new HashSet<string>(_remoteEntries.Select(e => e.Key), StringComparer.OrdinalIgnoreCase);
        return _remoteEntries.Concat(_localCatalog.Apps.Where(e => !remoteKeys.Contains(e.Key)))
                             .Where(e => !string.IsNullOrWhiteSpace(e.Repo));
    }

    private static List<CatalogEntry>? ParseRemoteCatalog(string json)
    {
        try
        {
            var cat = System.Text.Json.JsonSerializer.Deserialize<CatalogFile>(json, JsonStore.Options);
            if (cat?.Apps == null) return null;
            foreach (var e in cat.Apps) e.FromRemote = true;
            return cat.Apps.Where(e => !string.IsNullOrWhiteSpace(e.Repo)).ToList();
        }
        catch (Exception ex)
        {
            Log.Error("Remote catalog parse", ex);
            return null;
        }
    }

    private async Task LoadRemoteCatalogAsync(bool showResult)
    {
        var url = App.Settings.RemoteCatalogUrl?.Trim();
        if (string.IsNullOrEmpty(url))
        {
            _remoteEntries = new();
            RebuildAppCards();
            return;
        }
        RemoteCatalogStatus.Text = "Loading remote catalog…";
        try
        {
            var json = await GitHubService.GetTextAsync(url);
            var entries = ParseRemoteCatalog(json);
            if (entries == null)
            {
                RemoteCatalogStatus.Text = "The remote catalog isn't valid catalog JSON — using the local catalog.json.";
            }
            else
            {
                _remoteEntries = entries;
                TryWriteText(AppPaths.RemoteCatalogCache, json);
                RemoteCatalogStatus.Text = $"Remote catalog loaded: {entries.Count} app(s), {DateTime.Now:t}.";
            }
        }
        catch (Exception ex)
        {
            Log.Error("Remote catalog fetch", ex);
            RemoteCatalogStatus.Text = _remoteEntries.Count > 0
                ? "Couldn't reach the remote catalog — using the last downloaded copy plus the local catalog.json."
                : "Couldn't reach the remote catalog — using the local catalog.json.";
        }
        RebuildAppCards();
        if (showResult) await RefreshReleasesAsync(manual: false);
    }

    // =====================================================================
    // Bytehawk Apps: cards & GitHub
    // =====================================================================

    private void RebuildAppCards()
    {
        var entries = AllEntries().ToList();
        // Keep existing VMs (they may be mid-download)
        foreach (var vm in _apps.ToList())
            if (!entries.Any(e => e.Key.Equals(vm.Key, StringComparison.OrdinalIgnoreCase)) && !vm.IsDownloading)
                _apps.Remove(vm);

        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var vm = _apps.FirstOrDefault(v => v.Key.Equals(e.Key, StringComparison.OrdinalIgnoreCase));
            if (vm == null)
            {
                vm = new BytehawkAppVM(e);
                _apps.Insert(Math.Min(i, _apps.Count), vm);
            }
            vm.Update(e, Installed(e), Release(e));
        }
        UpdateEmptyStates();
    }

    private InstalledApp? Installed(CatalogEntry e) => _installed.TryGetValue(e.Key, out var i) ? i : null;
    private CachedRelease? Release(CatalogEntry e) => _releases.TryGetValue(e.Repo.Trim(), out var r) ? r : null;

    private void RefreshCard(BytehawkAppVM vm) => vm.Update(vm.Entry, Installed(vm.Entry), Release(vm.Entry));

    /// <summary>Fetches latest releases for every repo in the catalog, respecting cache age and rate limits.</summary>
    private async Task RefreshReleasesAsync(bool manual)
    {
        if (_refreshing) return;
        _refreshing = true;
        CheckUpdatesButton.IsEnabled = false;
        CheckUpdatesText.Text = "Checking…";
        var offline = false;
        var rateLimited = false;
        try
        {
            var repos = AllEntries().Select(e => e.Repo.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var maxAge = manual ? ManualRefreshAge : AutoRefreshAge;
            foreach (var repo in repos)
            {
                _releases.TryGetValue(repo, out var cached);
                if (cached != null && DateTime.UtcNow - cached.FetchedAt < maxAge) continue;

                var result = await GitHubService.GetLatestReleaseAsync(repo, cached);
                if (result.Status is GitHubStatus.Ok or GitHubStatus.NotModified or GitHubStatus.NoRelease && result.Value != null)
                {
                    _releases[repo] = result.Value!;
                    foreach (var vm in _apps.Where(v => v.Entry.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase)))
                        vm.Note = null;
                }
                else if (result.Status == GitHubStatus.NetworkError) { offline = true; break; }
                else if (result.Status == GitHubStatus.RateLimited) { rateLimited = true; break; }
                else
                {
                    foreach (var vm in _apps.Where(v => v.Entry.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase)))
                        vm.Note = result.Message;
                }
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

        if (rateLimited) ShowBanner(GitHubService.RateLimitMessage);
        else if (offline) ShowBanner("Couldn't reach GitHub — showing last known versions.");
        else
        {
            ShowBanner(null);
            if (manual)
            {
                var updates = _apps.Count(a => a.HasUpdate);
                CheckUpdatesText.Text = updates == 0 ? "Up to date ✓" : $"{updates} update(s) available";
                _ = ResetCheckTextLater();
            }
        }
    }

    private async Task ResetCheckTextLater()
    {
        await Task.Delay(4000);
        if (!_refreshing) CheckUpdatesText.Text = "Check for updates";
    }

    private void ShowBanner(string? text)
    {
        BytehawkBanner.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        BytehawkBannerText.Text = text ?? "";
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await RefreshReleasesAsync(manual: true);

    private void AddBytehawk_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new RepoDialog { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var entry = dlg.Result;
        if (AllEntries().Any(x => x.Key.Equals(entry.Key, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, $"{entry.Repo} is already in the catalog.", AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _localCatalog.Apps.Add(entry);
        TrySave(AppPaths.Catalog, _localCatalog);
        if (dlg.FetchedRelease != null)
        {
            _releases[entry.Repo] = dlg.FetchedRelease;
            TrySave(AppPaths.ReleaseCache, _releases);
        }
        RebuildAppCards();
    }

    private void BhEdit_Click(object sender, RoutedEventArgs e)
    {
        var vm = Ctx<BytehawkAppVM>(sender);
        if (vm == null) return;
        if (vm.Entry.FromRemote)
        {
            MessageBox.Show(this, "This app comes from the remote catalog URL set in Settings. Edit it there.", AppTitle);
            return;
        }
        var dlg = new RepoDialog(vm.Entry) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var idx = _localCatalog.Apps.IndexOf(vm.Entry);
        var updated = dlg.Result;
        // keep the same key so install state stays attached
        if (string.IsNullOrWhiteSpace(updated.Id)) updated.Id = vm.Entry.Key;
        if (idx >= 0) _localCatalog.Apps[idx] = updated; else _localCatalog.Apps.Add(updated);
        TrySave(AppPaths.Catalog, _localCatalog);
        if (dlg.FetchedRelease != null)
        {
            _releases[updated.Repo] = dlg.FetchedRelease;
            TrySave(AppPaths.ReleaseCache, _releases);
        }
        vm.Update(updated, Installed(updated), Release(updated));
        RebuildAppCards();
    }

    private void BhRemove_Click(object sender, RoutedEventArgs e)
    {
        var vm = Ctx<BytehawkAppVM>(sender);
        if (vm == null) return;
        if (vm.IsDownloading) { MessageBox.Show(this, "Cancel the download first.", AppTitle); return; }
        if (vm.Entry.FromRemote)
        {
            MessageBox.Show(this, "This app comes from the remote catalog URL set in Settings, so it can't be removed here.", AppTitle);
            return;
        }
        if (MessageBox.Show(this, $"Remove \"{vm.Name}\" from the launcher?\n\nInstalled files are not deleted.", AppTitle,
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _localCatalog.Apps.Remove(vm.Entry);
        TrySave(AppPaths.Catalog, _localCatalog);
        RebuildAppCards();
    }

    private void BhReleasePage_Click(object sender, RoutedEventArgs e)
    {
        var vm = Ctx<BytehawkAppVM>(sender);
        if (vm != null) ReportError(ShellHelper.OpenUrl(vm.ReleasePageUrl));
    }

    private async void BhPrimary_Click(object sender, RoutedEventArgs e)
    {
        var vm = Ctx<BytehawkAppVM>(sender);
        if (vm == null) return;
        if (!vm.IsInstalled || vm.IsMissing || vm.HasUpdate) await InstallAsync(vm);
        else if (vm.NeedsLaunchChoice) ChooseLaunchFile(vm);
        else LaunchApp(vm);
    }

    private void BhLaunch_Click(object sender, RoutedEventArgs e)
    {
        var vm = Ctx<BytehawkAppVM>(sender);
        if (vm != null) LaunchApp(vm);
    }

    private void BhChooseLaunch_Click(object sender, RoutedEventArgs e)
    {
        var vm = Ctx<BytehawkAppVM>(sender);
        if (vm != null) ChooseLaunchFile(vm);
    }

    private void BhCancel_Click(object sender, RoutedEventArgs e) => Ctx<BytehawkAppVM>(sender)?.Download?.Cts.Cancel();

    private void LaunchApp(BytehawkAppVM vm)
    {
        var path = vm.Installed?.LaunchPath;
        if (string.IsNullOrEmpty(path)) { ChooseLaunchFile(vm); return; }
        if (!File.Exists(path)) { RefreshCard(vm); return; } // card switches to "Not found — reinstall"
        ReportError(ShellHelper.Launch(path));
    }

    private void ChooseLaunchFile(BytehawkAppVM vm)
    {
        if (vm.Installed == null) return;
        var start = vm.Installed.InstallFolder is { } f && Directory.Exists(f) ? f
                  : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var file = PickExe($"Choose the program to launch for {vm.Name}", start);
        if (file == null) return;
        vm.Installed.LaunchPath = file;
        TrySave(AppPaths.Installed, _installed);
        RefreshCard(vm);
    }

    private string? PickExe(string title, string? startIn)
    {
        var dlg = new OpenFileDialog
        {
            Title = title,
            Filter = "Programs (*.exe;*.lnk)|*.exe;*.lnk|All files (*.*)|*.*",
            DereferenceLinks = false,
        };
        if (!string.IsNullOrEmpty(startIn) && Directory.Exists(startIn)) dlg.InitialDirectory = startIn;
        return dlg.ShowDialog(this) == true ? dlg.FileName : null;
    }

    private string DownloadFolder =>
        string.IsNullOrWhiteSpace(App.Settings.DownloadFolder) ? AppPaths.DefaultDownloads : App.Settings.DownloadFolder!;

    /// <summary>Download → (run installer | extract zip) → record installed version.</summary>
    private async Task InstallAsync(BytehawkAppVM vm)
    {
        if (vm.IsDownloading || vm.Busy) return;
        vm.Note = null;
        vm.Busy = true;
        DownloadItemVM? item = null;
        try
        {
            // 1) make sure we know the latest release (refresh if older than 10 minutes)
            var repo = vm.Entry.Repo.Trim();
            _releases.TryGetValue(repo, out var release);
            if (release == null || release.NoRelease || DateTime.UtcNow - release.FetchedAt > TimeSpan.FromMinutes(10))
            {
                var r = await GitHubService.GetLatestReleaseAsync(repo, release);
                if (r.Value != null && r.Status is GitHubStatus.Ok or GitHubStatus.NotModified or GitHubStatus.NoRelease)
                {
                    release = r.Value;
                    _releases[repo] = release;
                    TrySave(AppPaths.ReleaseCache, _releases);
                    ShowBanner(null);
                }
                else if (release == null || release.NoRelease)
                {
                    vm.Note = r.Status switch
                    {
                        GitHubStatus.NetworkError => "Couldn't reach GitHub. Check your connection and try again.",
                        _ => r.Message ?? "Couldn't read the latest release.",
                    };
                    if (r.Status == GitHubStatus.NetworkError) ShowBanner("Couldn't reach GitHub — showing last known versions.");
                    if (r.Status == GitHubStatus.RateLimited) ShowBanner(GitHubService.RateLimitMessage);
                    return;
                }
            }
            if (release == null || release.NoRelease)
            {
                vm.Note = "No published release on GitHub yet.";
                return;
            }

            // 2) pick the asset
            var asset = GitHubService.MatchAsset(release.Assets, vm.Entry.AssetPattern);
            if (asset == null || string.IsNullOrEmpty(asset.DownloadUrl))
            {
                vm.Note = $"No file in release {GitHubService.DisplayVersion(release.Tag)} matches \"{vm.Entry.AssetPattern}\". Edit the app to change the pattern.";
                return;
            }

            // 3) download with progress
            Directory.CreateDirectory(DownloadFolder);
            var dest = Path.Combine(DownloadFolder, SafeFileName(asset.Name));
            item = new DownloadItemVM { AppName = vm.Name, FileName = asset.Name, Destination = dest };
            _downloads.Insert(0, item);
            vm.Busy = false;
            vm.Download = item;
            Log.Info($"Downloading {asset.DownloadUrl} -> {dest}");

            try
            {
                await Downloader.DownloadAsync(asset.DownloadUrl, dest, new Progress<DownloadProgress>(item.Report), item.Cts.Token);
            }
            catch (OperationCanceledException)
            {
                item.Status = "Cancelled";
                item.Detail = "Download cancelled. Nothing was installed.";
                item.IsActive = false;
                vm.Download = null;
                return;
            }
            catch (Exception ex)
            {
                Log.Error("Download failed", ex);
                item.Status = "Failed";
                item.Detail = ex is IOException ? $"Couldn't save the file: {ex.Message}" : $"Download failed: {ex.Message}";
                item.IsActive = false;
                vm.Download = null;
                vm.Note = "Download failed. See the Downloads page for details.";
                return;
            }

            item.Percent = 100;
            item.IsIndeterminate = false;
            item.Status = "Completed";
            item.Detail = $"Saved to {dest}";
            item.IsActive = false;
            Log.Info($"Downloaded {dest} ({new FileInfo(dest).Length} bytes)");

            // 4) install
            var previous = Installed(vm.Entry);
            var record = new InstalledApp
            {
                Version = release.Tag,
                InstalledAt = DateTime.Now,
                AssetName = asset.Name,
                DownloadedFile = dest,
            };
            var ext = Path.GetExtension(dest).ToLowerInvariant();
            vm.Download = null;
            vm.Busy = true;

            if (ext == ".zip")
            {
                record.Kind = "zip";
                var folder = Path.Combine(AppPaths.Apps, SafeFileName(vm.Name));
                // remember the previously chosen exe (relative to the app folder) across updates
                string? previousRelative = null;
                if (previous?.LaunchPath is { } lp && previous.InstallFolder is { } pf && lp.StartsWith(pf, StringComparison.OrdinalIgnoreCase))
                    previousRelative = Path.GetRelativePath(pf, lp);

                item.Detail = $"Extracting to {folder}…";
                try
                {
                    await Task.Run(() =>
                    {
                        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                        Directory.CreateDirectory(folder);
                        ZipFile.ExtractToDirectory(dest, folder, overwriteFiles: true);
                    });
                }
                catch (Exception ex)
                {
                    Log.Error("Extract failed", ex);
                    item.Status = "Failed";
                    item.Detail = $"Couldn't extract the .zip: {ex.Message} (Is the app still running? Close it and try again.)";
                    vm.Note = "Extracting failed. Close the app if it's running, then try again.";
                    return;
                }
                record.InstallFolder = folder;
                item.Detail = $"Extracted to {folder}";
                ShellHelper.OpenFolder(folder);

                if (previousRelative != null && File.Exists(Path.Combine(folder, previousRelative)))
                    record.LaunchPath = Path.Combine(folder, previousRelative);
                else
                {
                    var exes = Directory.GetFiles(folder, "*.exe", SearchOption.AllDirectories);
                    record.LaunchPath = exes.Length == 1
                        ? exes[0]
                        : PickExe($"Choose the main program for {vm.Name}", folder);
                }
            }
            else if (ext is ".exe" or ".msi")
            {
                record.Kind = ext.TrimStart('.');
                var err = ShellHelper.Launch(dest);
                if (err != null)
                {
                    item.Detail = err;
                    vm.Note = "The installer didn't start. You can run it from the Downloads page.";
                }
                // Keep a previously chosen program (e.g. installed under Program Files) across updates.
                if (previous?.LaunchPath is { } keep && File.Exists(keep) &&
                    !string.Equals(keep, previous.DownloadedFile, StringComparison.OrdinalIgnoreCase))
                {
                    record.LaunchPath = keep;
                }
                else if (ext == ".msi")
                {
                    record.LaunchPath = null; // card shows "Choose launch file" once setup finishes
                }
                else
                {
                    var answer = MessageBox.Show(this,
                        $"{asset.Name} has been started.\n\n" +
                        "Is it an installer that puts the app somewhere else (like Program Files)?\n\n" +
                        "Yes — it's an installer. When setup finishes, use “Choose launch file” on the card.\n" +
                        "No — it's the app itself. Launch will run this downloaded file.",
                        AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Question);
                    record.LaunchPath = answer == MessageBoxResult.Yes ? null : dest;
                }
            }
            else
            {
                record.Kind = ext.TrimStart('.');
                record.LaunchPath = dest;
                ShellHelper.ShowInExplorer(dest);
            }

            _installed[vm.Entry.Key] = record;
            TrySave(AppPaths.Installed, _installed);
            Log.Info($"Recorded {vm.Entry.Key} {record.Version} kind={record.Kind} launch={record.LaunchPath}");
        }
        finally
        {
            if (item != null && item.IsActive) item.IsActive = false;
            vm.Busy = false;
            if (vm.Download != null && !vm.Download.IsActive) vm.Download = null;
            RefreshCard(vm);
        }
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return s.Length == 0 ? "app" : s;
    }

    // =====================================================================
    // My Software
    // =====================================================================

    private void RebuildSoftwareCards()
    {
        _software.Clear();
        foreach (var e in _library.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
            _software.Add(new SoftwareVM(e));
        UpdateEmptyStates();
    }

    private void AddSoftware_Click(object sender, RoutedEventArgs e)
    {
        var file = SoftwareDialog.PickProgram(this);
        if (file == null) return;
        var dlg = new SoftwareDialog(null, file) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _library.Add(dlg.Result);
        TrySave(AppPaths.Library, _library);
        RebuildSoftwareCards();
    }

    private void SwLaunch_Click(object sender, RoutedEventArgs e)
    {
        var vm = Ctx<SoftwareVM>(sender);
        if (vm == null) return;
        ReportError(ShellHelper.Launch(vm.Entry.Path, vm.Entry.Arguments, vm.Entry.RunAsAdmin));
        vm.Reload();
    }

    private void SwOpenLocation_Click(object sender, RoutedEventArgs e)
    {
        var vm = Ctx<SoftwareVM>(sender);
        if (vm != null) ReportError(ShellHelper.ShowInExplorer(vm.Entry.Path));
    }

    private void SwEdit_Click(object sender, RoutedEventArgs e)
    {
        var vm = Ctx<SoftwareVM>(sender);
        if (vm == null) return;
        var dlg = new SoftwareDialog(vm.Entry) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var idx = _library.FindIndex(x => x.Id == vm.Entry.Id);
        if (idx >= 0) _library[idx] = dlg.Result; else _library.Add(dlg.Result);
        TrySave(AppPaths.Library, _library);
        RebuildSoftwareCards();
    }

    private void SwRemove_Click(object sender, RoutedEventArgs e)
    {
        var vm = Ctx<SoftwareVM>(sender);
        if (vm == null) return;
        if (MessageBox.Show(this, $"Remove \"{vm.Name}\" from My Software?\n\nThe file on your PC is not deleted.", AppTitle,
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        // Only the launcher entry is removed — never the file itself.
        _library.RemoveAll(x => x.Id == vm.Entry.Id);
        TrySave(AppPaths.Library, _library);
        RebuildSoftwareCards();
    }

    // =====================================================================
    // Web Apps
    // =====================================================================

    private void RebuildWebCards()
    {
        _web.Clear();
        foreach (var e in _webApps.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
            _web.Add(new WebAppVM(e));
        UpdateEmptyStates();
    }

    private void AddWebApp_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new WebAppDialog { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _webApps.Add(dlg.Result);
        TrySave(AppPaths.WebApps, _webApps);
        RebuildWebCards();
    }

    private void WebLaunch_Click(object sender, RoutedEventArgs e)
    {
        var vm = Ctx<WebAppVM>(sender);
        if (vm != null) ReportError(ShellHelper.OpenUrl(vm.Url));
    }

    private void WebCard_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // Buttons handle their own clicks; a click on the card body opens the site.
        if (e.Handled) return;
        if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) != null) return;
        var vm = Ctx<WebAppVM>(sender);
        if (vm != null) ReportError(ShellHelper.OpenUrl(vm.Url));
    }

    private void WebEdit_Click(object sender, RoutedEventArgs e)
    {
        var vm = Ctx<WebAppVM>(sender);
        if (vm == null) return;
        var dlg = new WebAppDialog(vm.Entry) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var idx = _webApps.FindIndex(x => x.Id == vm.Entry.Id);
        if (idx >= 0) _webApps[idx] = dlg.Result; else _webApps.Add(dlg.Result);
        TrySave(AppPaths.WebApps, _webApps);
        RebuildWebCards();
    }

    private void WebRemove_Click(object sender, RoutedEventArgs e)
    {
        var vm = Ctx<WebAppVM>(sender);
        if (vm == null) return;
        if (MessageBox.Show(this, $"Remove \"{vm.Name}\" from Web Apps?", AppTitle,
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _webApps.RemoveAll(x => x.Id == vm.Entry.Id);
        TrySave(AppPaths.WebApps, _webApps);
        RebuildWebCards();
    }

    // =====================================================================
    // Downloads page
    // =====================================================================

    private void DlCancel_Click(object sender, RoutedEventArgs e) => Ctx<DownloadItemVM>(sender)?.Cts.Cancel();

    private void DlShow_Click(object sender, RoutedEventArgs e)
    {
        var vm = Ctx<DownloadItemVM>(sender);
        if (vm != null) ReportError(ShellHelper.ShowInExplorer(vm.Destination));
    }

    private void OpenDownloadsFolder_Click(object sender, RoutedEventArgs e) => ShellHelper.OpenFolder(DownloadFolder);

    // =====================================================================
    // Navigation, search, theme
    // =====================================================================

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageBytehawk == null) return; // during InitializeComponent
        PageBytehawk.Visibility = NavBytehawk.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageSoftware.Visibility = NavSoftware.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageWeb.Visibility = NavWeb.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageDownloads.Visibility = NavDownloads.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (NavDownloads.IsChecked == true) DownloadsFolderText.Text = $"Saving to {DownloadFolder}";
        if (NavSoftware.IsChecked == true) foreach (var s in _software) s.Reload(); // pick up moved/deleted files
    }

    private bool MatchesSearch(string name)
    {
        var q = SearchBox?.Text?.Trim();
        return string.IsNullOrEmpty(q) || name.Contains(q, StringComparison.CurrentCultureIgnoreCase);
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        var has = SearchBox.Text.Length > 0;
        SearchPlaceholder.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        ClearSearch.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        CollectionViewSource.GetDefaultView(_apps).Refresh();
        CollectionViewSource.GetDefaultView(_software).Refresh();
        CollectionViewSource.GetDefaultView(_web).Refresh();
        UpdateEmptyStates();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SearchBox.Focus();
    }

    private void UpdateEmptyStates()
    {
        if (BytehawkEmpty == null) return;
        BytehawkEmpty.Visibility = CollectionViewSource.GetDefaultView(_apps).IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        SoftwareEmpty.Visibility = CollectionViewSource.GetDefaultView(_software).IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        WebEmpty.Visibility = CollectionViewSource.GetDefaultView(_web).IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        var searching = !string.IsNullOrEmpty(SearchBox.Text);
        SoftwareEmpty.Text = searching ? "No software matches your search." : "Nothing here yet. Use “Add software” to add any program from your PC.";
        WebEmpty.Text = searching ? "No web apps match your search." : "No web apps yet. Use “Add web app” to bookmark a site.";
        BytehawkEmpty.Text = searching ? "No Bytehawk apps match your search." : "No Bytehawk apps yet. Use “Add Bytehawk app” to add one from a GitHub repo.";
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingTheme || ThemeLight == null || SetThemeSystem == null) return;
        var mode = sender == ThemeLight || sender == SetThemeLight ? ThemeService.Light
                 : sender == ThemeDark || sender == SetThemeDark ? ThemeService.Dark
                 : ThemeService.FollowSystem;
        App.Settings.Theme = mode;
        TrySave(AppPaths.Settings, App.Settings);
        ThemeService.Apply(mode);
        SyncThemeRadios();
    }

    private void SyncThemeRadios()
    {
        _syncingTheme = true;
        var m = ThemeService.Normalize(App.Settings.Theme);
        ThemeLight.IsChecked = SetThemeLight.IsChecked = m == ThemeService.Light;
        ThemeDark.IsChecked = SetThemeDark.IsChecked = m == ThemeService.Dark;
        ThemeSystem.IsChecked = SetThemeSystem.IsChecked = m == ThemeService.FollowSystem;
        _syncingTheme = false;
    }

    // =====================================================================
    // Settings page
    // =====================================================================

    private void LoadSettingsIntoUi()
    {
        _loadingSettings = true;
        SyncThemeRadios();
        DownloadFolderBox.Text = DownloadFolder;
        CheckOnStartupBox.IsChecked = App.Settings.CheckUpdatesOnStartup;
        RemoteCatalogBox.Text = App.Settings.RemoteCatalogUrl ?? "";
        DataFolderText.Text = $"Your data is stored in {AppPaths.Root}";
        _loadingSettings = false;
    }

    private void ChangeDownloadFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose the download folder", InitialDirectory = DownloadFolder };
        if (dlg.ShowDialog(this) != true) return;
        App.Settings.DownloadFolder = dlg.FolderName;
        TrySave(AppPaths.Settings, App.Settings);
        DownloadFolderBox.Text = DownloadFolder;
    }

    private void ResetDownloadFolder_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.DownloadFolder = null;
        TrySave(AppPaths.Settings, App.Settings);
        DownloadFolderBox.Text = DownloadFolder;
    }

    private void CheckOnStartup_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        App.Settings.CheckUpdatesOnStartup = CheckOnStartupBox.IsChecked == true;
        TrySave(AppPaths.Settings, App.Settings);
    }

    private async void SaveRemoteCatalog_Click(object sender, RoutedEventArgs e)
    {
        var url = RemoteCatalogBox.Text.Trim();
        if (url.Length > 0 && (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp)))
        {
            RemoteCatalogStatus.Text = "Enter a full http(s) URL, or leave it blank to use only the local catalog.json.";
            return;
        }
        App.Settings.RemoteCatalogUrl = url;
        TrySave(AppPaths.Settings, App.Settings);
        if (url.Length == 0)
        {
            _remoteEntries = new();
            try { if (File.Exists(AppPaths.RemoteCatalogCache)) File.Delete(AppPaths.RemoteCatalogCache); } catch { /* ignore */ }
            RemoteCatalogStatus.Text = "Remote catalog cleared — using the local catalog.json.";
            RebuildAppCards();
            return;
        }
        await LoadRemoteCatalogAsync(showResult: true);
    }

    private void OpenCatalogFile_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(AppPaths.Catalog)) TrySave(AppPaths.Catalog, _localCatalog);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{AppPaths.Catalog}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { ReportError(ex.Message); }
    }

    private async void ReloadCatalog_Click(object sender, RoutedEventArgs e)
    {
        _localCatalog = JsonStore.Load(AppPaths.Catalog, () => new CatalogFile());
        _localCatalog.Apps ??= new();
        if (JsonStore.LastLoadError is { } err)
        {
            JsonStore.LastLoadError = null;
            MessageBox.Show(this, err, AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RebuildAppCards();
        await RefreshReleasesAsync(manual: false);
        NavBytehawk.IsChecked = true;
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => ShellHelper.OpenFolder(AppPaths.Root);

    // =====================================================================
    // helpers
    // =====================================================================

    private static T? Ctx<T>(object sender) where T : class => (sender as FrameworkElement)?.DataContext as T;

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    private void ReportError(string? message)
    {
        if (message != null) MessageBox.Show(this, message, AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void TrySave<T>(string path, T value)
    {
        try { JsonStore.Save(path, value); }
        catch (Exception ex)
        {
            Log.Error($"Save {path}", ex);
            MessageBox.Show(this, $"Couldn't save {Path.GetFileName(path)}:\n{ex.Message}", AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void TryWriteText(string path, string text)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
        catch (Exception ex) { Log.Error($"Write {path}", ex); }
    }
}
