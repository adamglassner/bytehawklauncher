using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
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
    private readonly ObservableCollection<ServerCardVM> _servers = new();
    private readonly ObservableCollection<SoftwareVM> _software = new();
    private readonly ObservableCollection<WebAppVM> _web = new();
    private readonly ObservableCollection<DownloadItemVM> _downloads = new();
    /// <summary>Browser extensions: the same cards as Bytehawk Apps, shown on their own page.</summary>
    private ListCollectionView? _extensionsView;

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
        ServersList.ItemsSource = _servers;
        CollectionViewSource.GetDefaultView(_servers).Filter = o => MatchesSearch(((ServerCardVM)o).Name) || MatchesSearch(((ServerCardVM)o).App.Name);
        // A server installed or uninstalled outside the launcher shows up when you come back to it
        Activated += (_, _) => { foreach (var c in _servers) c.Refresh(); };
        SoftwareList.ItemsSource = _software;
        WebList.ItemsSource = _web;
        DownloadsList.ItemsSource = _downloads;
        _downloads.CollectionChanged += (_, _) =>
            DownloadsEmpty.Visibility = _downloads.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        CollectionViewSource.GetDefaultView(_apps).Filter = o => o is BytehawkAppVM v && !v.IsExtension && MatchesSearch(v.Name);
        _extensionsView = new ListCollectionView(_apps) { Filter = o => o is BytehawkAppVM v && v.IsExtension && MatchesSearch(v.Name) };
        ExtensionsList.ItemsSource = _extensionsView;
        CollectionViewSource.GetDefaultView(_software).Filter = o => o is SoftwareVM v &&
            MatchesCategory(_softwareCategory, v.CategoryDisplay) && (MatchesSearch(v.Name) || MatchesSearch(v.Category));
        CollectionViewSource.GetDefaultView(_web).Filter = o => o is WebAppVM v &&
            MatchesCategory(_webCategory, v.CategoryDisplay) && (MatchesSearch(v.Name) || MatchesSearch(v.Category));
        // Group My Software and Web Apps cards by category (Uncategorized last), A–Z inside each group.
        foreach (var view in new[] { CollectionViewSource.GetDefaultView(_software), CollectionViewSource.GetDefaultView(_web) })
        {
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SoftwareVM.CategoryDisplay)));
            view.SortDescriptions.Add(new SortDescription(nameof(SoftwareVM.CategorySortKey), ListSortDirection.Ascending));
            view.SortDescriptions.Add(new SortDescription(nameof(SoftwareVM.Name), ListSortDirection.Ascending));
        }

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
        MergeBuiltInApps();

        _installed = new(JsonStore.Load(AppPaths.Installed, () => new Dictionary<string, InstalledApp>()), StringComparer.OrdinalIgnoreCase);
        if (NoteSelfUpdates()) TrySave(AppPaths.Installed, _installed);
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

    /// <summary>
    /// Adds built-in Bytehawk Studio entries that an older catalog.json doesn't have yet
    /// (unless the user removed them), so new apps show up after an update.
    /// </summary>
    private void MergeBuiltInApps()
    {
        _localCatalog.RemovedDefaults ??= new();
        var defaults = SampleCatalog().Apps;
        // 1.1.0: the Notepad++ sample entries are gone for good.
        var changed = _localCatalog.Apps.RemoveAll(IsOldSample) > 0;
        // Released apps replace their old "Coming soon" placeholders.
        for (int i = 0; i < _localCatalog.Apps.Count; i++)
        {
            var a = _localCatalog.Apps[i];
            if (!a.ComingSoon || a.HasRepo) continue;
            var d = defaults.FirstOrDefault(x => !x.ComingSoon && x.Key.Equals(a.Key, StringComparison.OrdinalIgnoreCase));
            if (d != null) { _localCatalog.Apps[i] = d; changed = true; }
        }
        // 1.1.1: the "Padcraft (coming soon)" placeholder became Draftly (a different id), so drop it.
        changed |= _localCatalog.Apps.RemoveAll(a => a.ComingSoon && a.Key.Equals("padcraft", StringComparison.OrdinalIgnoreCase)) > 0;
        if (_localCatalog.RemovedDefaults.RemoveAll(k => k.Equals("padcraft", StringComparison.OrdinalIgnoreCase)) > 0) changed = true;
        // 1.5.0 / 1.7.0: apps that gained a server (WillowWallet Server, Guardrail Server) get it on the Bytehawk Servers page.
        foreach (var a in _localCatalog.Apps.Where(a => a.Server == null || a.Server.WindowsUninstallKey == null))
        {
            var d = defaults.FirstOrDefault(x => x.Server != null && x.Key.Equals(a.Key, StringComparison.OrdinalIgnoreCase));
            if (d == null) continue;
            a.Server = d.Server;
            a.Name = d.Name;
            a.Description = d.Description;
            changed = true;
        }
        for (int i = 0; i < defaults.Count; i++)
        {
            var d = defaults[i];
            if (_localCatalog.Apps.Any(a => a.Key.Equals(d.Key, StringComparison.OrdinalIgnoreCase))) continue;
            if (_localCatalog.RemovedDefaults.Contains(d.Key, StringComparer.OrdinalIgnoreCase)) continue;
            _localCatalog.Apps.Insert(Math.Min(i, _localCatalog.Apps.Count), d);
            changed = true;
        }
        if (changed) TrySave(AppPaths.Catalog, _localCatalog);
    }

    private static CatalogFile SampleCatalog() => new()
    {
        Version = 1,
        Apps =
        {
            GuardrailEhs(),
            Draftly(),
            ShoreDrop(),
            Discshelf(),
            Sumthing(),
            Taskflare(),
            Boardwalk(),
            CoinPilot(),
            Shutterbox(),
            Sparecopy(),
            Castaway(),
            Larksong(),
            LarksongClassic(),
            Fleetwatch(),
            RingHawk(),
            AlbumGrab(),
            NoteHawk(),
            PlanHawk(),
            PaperHawk(),
            DataHawk(),
            CraftHawk(),
            // Coming soon: the Bytehawk office & creative suite
            ComingSoon("writehawk", "WriteHawk", "Full-featured word processor for documents, reports and letters, with styles, tables, comments, track changes and .docx support. Part of the Bytehawk office suite."),
            ComingSoon("gridhawk", "GridHawk", "Spreadsheet app with formulas, charts, pivot tables and .xlsx support. Part of the Bytehawk office suite."),
            ComingSoon("slidehawk", "SlideHawk", "Presentation app for slide decks with themes, transitions, animations and presenter view, with .pptx support. Part of the Bytehawk office suite."),
            ComingSoon("inboxhawk", "InboxHawk", "Email, calendar and contacts in one app. Part of the Bytehawk office suite."),
            ComingSoon("printhawk", "PrintHawk", "Desktop publishing for flyers, newsletters, cards and brochures. Part of the Bytehawk office suite."),
            ComingSoon("pixelhawk", "PixelHawk", "Photo editor with layers, masks, adjustments and retouching tools. Part of the Bytehawk creative suite."),
            ComingSoon("vectorhawk", "VectorHawk", "Vector illustration app for logos, icons and artwork. Part of the Bytehawk creative suite."),
            ComingSoon("pagehawk", "PageHawk", "Page layout app for books, magazines and print documents. Part of the Bytehawk creative suite."),
            ComingSoon("cliphawk", "ClipHawk", "Video editor with a multi-track timeline, titles, transitions and color tools. Part of the Bytehawk creative suite."),
            ComingSoon("flowhawk", "FlowHawk", "Motion graphics and visual effects for video. Part of the Bytehawk creative suite."),
            ComingSoon("wavehawk", "WaveHawk", "Audio editor for recording, cleaning up and mixing audio. Part of the Bytehawk creative suite."),
            ComingSoon("lenshawk", "LensHawk", "Photo library and RAW photo editor for organizing and developing your pictures. Part of the Bytehawk creative suite."),
            ComingSoon("framehawk", "FrameHawk", "2D animation app for animated graphics and cartoons. Part of the Bytehawk creative suite."),
        },
    };

    /// <summary>Guardrail EHS — the first released Bytehawk Studio app.</summary>
    private static CatalogEntry GuardrailEhs() => new()
    {
        Id = "guardrail-ehs",
        Name = "Guardrail EHS",
        Description = "Free, open-source environmental, health & safety and fleet safety software: incidents, actions, inspections, training, fleet, Crash Review AI and safety references, with your records kept on your own PC. Optional Guardrail Server puts a whole team on the same data.",
        Repo = "adamglassner/guardrailehs",
        AssetPattern = "GuardrailEHS-*-Setup.exe;*-win-x64.zip",
        Exe = "Guardrail.exe",
        Icon = "https://raw.githubusercontent.com/adamglassner/guardrailehs/main/src/Guardrail.Desktop/Assets/guardrail.png",
        Server = GuardrailServer(),
    };

    /// <summary>Guardrail Server (1.7.0): self-hosted team sync for Guardrail EHS, from one PC up to a cluster.</summary>
    private static ServerDownload GuardrailServer() => new()
    {
        Name = "Guardrail Server",
        Description = "Run your own server so your whole team shares Guardrail EHS records, with accounts, roles and a web panel. Starts as one Windows service with everything on this PC, and scales up to many servers with PostgreSQL and S3 storage (Docker and Kubernetes setups included).",
        Repo = "adamglassner/guardrailehs",
        TagPrefix = "server-v",
        WindowsAsset = "GuardrailServer-*-Setup.exe",
        DockerImage = "ghcr.io/adamglassner/guardrail-server",
        Port = 5080,
        GuideUrl = "https://github.com/adamglassner/guardrailehs/blob/main/server/README.md",
        WindowsUninstallKey = "{8E1D4C72-3B5A-4F09-9C6E-2A7B1D0F5E93}_is1",
        WindowsDataFolder = @"%ProgramData%\Guardrail Server",
    };

    /// <summary>ShoreDrop — FTP, FTPS and SFTP client (portable zip from its GitHub releases).</summary>
    private static CatalogEntry ShoreDrop() => new()
    {
        Id = "shoredrop",
        Name = "ShoreDrop",
        Description = "FTP, FTPS and SFTP client for moving files to and from your servers, with a dual-pane browser, transfer queue, site manager and light and dark themes.",
        Repo = "adamglassner/shoredrop",
        AssetPattern = "ShoreDrop-*-win-x64.zip",
        Exe = "ShoreDrop.exe",
        Icon = "https://raw.githubusercontent.com/adamglassner/shoredrop/main/assets/shoredrop-256.png",
    };

    /// <summary>Draftly — the Bytehawk Studio word processor (was listed as "Padcraft, coming soon" before 1.1.1).</summary>
    private static CatalogEntry Draftly() => new()
    {
        Id = "draftly",
        Name = "Draftly",
        Description = "Modern word processor for writing and formatting documents: rich text, pictures, tables and Light or Dark mode. Opens RTF, TXT and Word files.",
        Repo = "adamglassner/draftly",
        AssetPattern = "Draftly-*-win-x64.zip;Draftly-*-Setup.exe",
        Exe = "Draftly.exe",
        Icon = "https://raw.githubusercontent.com/adamglassner/draftly/main/assets/draftly-256.png",
    };

    /// <summary>Discshelf — music player (portable zip from its GitHub releases; was "Coming soon" before).</summary>
    private static CatalogEntry Discshelf() => new()
    {
        Id = "discshelf",
        Name = "Discshelf",
        Description = "Music player for the music on your computer: Genre, Artist and Album browsing, album art, and Light, Dark or Classic (Songbird) themes. Plays MP3, AAC, FLAC, Ogg, Opus and WAV.",
        Repo = "adamglassner/discshelf",
        AssetPattern = "Discshelf-*-win-x64.zip;Discshelf-*-Setup.exe",
        Exe = "Discshelf.exe",
        Icon = "https://raw.githubusercontent.com/adamglassner/discshelf/main/assets/discshelf-256.png",
    };

    /// <summary>Sumthing — calculator (was "Coming soon" before 1.3.2).</summary>
    private static CatalogEntry Sumthing() => new()
    {
        Id = "sumthing",
        Name = "Sumthing",
        Description = "Calculator with a TI-84 / TI-Nspire style graphing calculator, a scientific home screen, statistics and regressions, a paper tape, sums you type in words, a unit and currency converter and everyday tools. Light or Dark, with Modern or Classic (Windows 7) keys.",
        Repo = "adamglassner/sumthing",
        AssetPattern = "Sumthing-*-win-x64.zip;Sumthing-*-Setup.exe",
        Exe = "Sumthing.exe",
        Icon = "https://raw.githubusercontent.com/adamglassner/sumthing/main/assets/sumthing-256.png",
    };

    /// <summary>Taskflare — to-do list with a Muse AI connector (added in 1.3.3).</summary>
    private static CatalogEntry Taskflare() => new()
    {
        Id = "taskflare",
        Name = "Taskflare",
        Description = "Free to-do list in the style of Microsoft To Do and Todoist: My Day, Important, Planned, lists, quick add that understands dates, reminders and Light or Dark mode. Claude, ChatGPT, Gemini, Grok, Muse and Siri can add tasks for you.",
        Repo = "adamglassner/taskflare",
        AssetPattern = "Taskflare-*-win-x64.zip;Taskflare-*-Setup.exe",
        Exe = "Taskflare.exe",
        Icon = "https://raw.githubusercontent.com/adamglassner/taskflare/main/assets/taskflare-256.png",
    };

    /// <summary>Boardwalk — kanban boards, one per project (added in 1.3.10).</summary>
    private static CatalogEntry Boardwalk() => new()
    {
        Id = "boardwalk",
        Name = "Boardwalk",
        Description = BoardwalkDescription,
        Repo = "adamglassner/boardwalk",
        AssetPattern = "Boardwalk-*-win-x64.zip;Boardwalk-*-Setup.exe",
        Exe = "Boardwalk.exe",
        Icon = "https://raw.githubusercontent.com/adamglassner/boardwalk/main/assets/boardwalk-256.png",
    };

    private const string BoardwalkDescription =
        "Kanban boards for your projects: as many boards as you like, statuses like New, In progress, On hold and Complete, and cards you drag between them. Quick add, undo, My week, due-date reminders, sub-tasks, blocked-by, repeating cards, screenshots, swimlanes, stats, changelogs from your cards and GitHub issue import. Light or Dark mode. Sync between devices is coming soon.";

    /// <summary>Larksong — alarm clock (added in 1.3.11). Installed from its portable Windows zip.</summary>
    private static CatalogEntry Larksong() => new()
    {
        Id = "larksong",
        Name = "Larksong",
        Description = "Alarm clock with your own sounds and gently rising volume, wake to the radio, sleep sounds, bedtime reminders, a stopwatch and timer, world clocks and weather. Styles include Larksong, Dawn, Dusk, Retro, a clock radio with a world radio tuner, and Terminal.",
        Repo = "adamglassner/larksong",
        AssetPattern = "Larksong-*-win-x64.zip",
        Exe = "Larksong.exe",
        Icon = "https://raw.githubusercontent.com/adamglassner/larksong/main/desktop/icons/larksong.png",
    };

    /// <summary>Larksong Classic — the alarm clock for Windows 95 to 11 (added in 1.3.11). Installed from its zip.</summary>
    private static CatalogEntry LarksongClassic() => new()
    {
        Id = "larksong-classic",
        Name = "Larksong Classic",
        Description = "Larksong for older PCs: one small program that runs on every version from Windows 95 to Windows 11, with alarms, a stopwatch and timer, world clocks and a bedtime reminder. Backups move your alarms between it and Larksong.",
        Repo = "adamglassner/larksong-classic",
        AssetPattern = "Larksong-Classic-*.zip",
        Exe = "Larksong.exe",
        Icon = "https://raw.githubusercontent.com/adamglassner/larksong-classic/main/assets/larksong-classic-256.png",
    };

    /// <summary>WillowWallet (called CoinPilot before 1.4.1) — budgeting app. The id stays "coinpilot" so installs carry over.</summary>
    private static CatalogEntry CoinPilot() => new()
    {
        Id = "coinpilot",
        Name = "WillowWallet",
        Description = "Budgeting app: budgets, transactions with bank statement import, accounts and net worth, debt payoff plans, bills with reminders, savings goals, reports and a safe place for tax documents. Optional PIN/password encryption, and optional end-to-end encrypted sync with the Android app through your own WillowWallet Server. (Formerly CoinPilot.)",
        Repo = "adamglassner/coinpilot",
        AssetPattern = "WillowWallet-*-win-x64.zip;WillowWallet-*-Setup.exe",
        Exe = "WillowWallet.exe",
        Icon = "https://raw.githubusercontent.com/adamglassner/coinpilot/main/assets/coinpilot-256.png",
        Server = WillowWalletServer(),
    };

    /// <summary>WillowWallet Server (1.5.0): self-hosted, end-to-end encrypted sync for the Windows and Android apps.</summary>
    private static ServerDownload WillowWalletServer() => new()
    {
        Name = "WillowWallet Server",
        Description = "Run your own server to sync WillowWallet between your PC and phone, with accounts for everyone in your household. Your data is encrypted on each device before it's sent, so the server can't read it. Manage it from its web panel.",
        Repo = "adamglassner/coinpilot",
        TagPrefix = "server-v",
        WindowsAsset = "WillowWalletServer-*-Setup.exe",
        DockerImage = "ghcr.io/adamglassner/willowwallet-server",
        Port = 8484,
        GuideUrl = "https://github.com/adamglassner/coinpilot/blob/main/server/README.md",
        WindowsUninstallKey = "{6C4E2B1A-7F3D-4E85-9A2C-1D8B5F0E7A44}_is1",
        WindowsDataFolder = @"%ProgramData%\WillowWallet Server",
    };

    /// <summary>Shutterbox — photo organizer (was "Coming soon" before 1.3.5).</summary>
    private static CatalogEntry Shutterbox() => new()
    {
        Id = "shutterbox",
        Name = "Shutterbox",
        Description = "Photo organizer for the pictures already on your PC: a timeline by day, month or year, albums, favorites, tags, search, a viewer with slideshows, and import from cameras and phones. Your files stay where they are.",
        Repo = "adamglassner/Shutterbox",
        AssetPattern = "Shutterbox-*-win-x64.zip;Shutterbox-*-Setup.exe",
        Exe = "Shutterbox.exe",
        Icon = "https://raw.githubusercontent.com/adamglassner/Shutterbox/main/assets/shutterbox-256.png",
    };

    /// <summary>Sparecopy — family backup, early preview shell (was "Coming soon" before 1.3.7).</summary>
    private static CatalogEntry Sparecopy() => new()
    {
        Id = "sparecopy",
        Name = "Sparecopy",
        Description = "Backup for your family's PCs to a NAS, server or USB drive, with one dashboard for every device at every house. Early preview: the Overview shows the planned layout and the other sections are coming soon.",
        Repo = "adamglassner/sparecopy",
        AssetPattern = "Sparecopy-*-win-x64.zip;Sparecopy-*-Setup.exe",
        Exe = "Sparecopy.exe",
        Icon = "https://raw.githubusercontent.com/adamglassner/sparecopy/main/assets/sparecopy-256.png",
    };

    /// <summary>Castaway — media player, early preview shell (was "Coming soon" before 1.3.8).</summary>
    private static CatalogEntry Castaway() => new()
    {
        Id = "castaway",
        Name = "Castaway",
        Description = "Media player for your own movies and shows in the style of Plex: libraries for Movies, TV Shows, Kids & Family, Documentaries, Stand-Up and Anime, in light or dark. Early preview: the Castaway server that holds and streams your media, and playback, are coming soon.",
        Repo = "adamglassner/Castaway",
        AssetPattern = "Castaway-*-win-x64.zip;Castaway-*-Setup.exe",
        Exe = "Castaway.exe",
        Icon = "https://raw.githubusercontent.com/adamglassner/Castaway/main/assets/castaway-256.png",
    };

    /// <summary>Fleetwatch — look after the PCs you're responsible for; installs a background agent that reports to your own server.</summary>
    private static CatalogEntry Fleetwatch() => new()
    {
        Id = "fleetwatch",
        Name = "Fleetwatch",
        Description = "Look after the computers you and your family rely on — see each PC's status and updates and lend a hand remotely. Early preview.",
        Repo = "adamglassner/Fleetwatch",
        AssetPattern = "Fleetwatch-*-win-x64.zip",
        Exe = "Fleetwatch.exe",
        Icon = "https://raw.githubusercontent.com/adamglassner/Fleetwatch/main/assets/fleetwatch-256.png",
    };

    /// <summary>RingHawk (called Waylo until 0.1.1; the id stays "waylo" so existing installs update in place) — video calls, voice calls and chat.</summary>
    private static CatalogEntry RingHawk() => new()
    {
        Id = "waylo",
        Name = "RingHawk",
        Description = "Video calls, voice calls and chat with your family and friends. Early preview: the Chats page shows sample conversations, and calling, chatting, contacts, meetings and voicemail are coming soon.",
        Repo = "adamglassner/waylo",
        AssetPattern = "RingHawk-*-win-x64.zip;RingHawk-*-Setup.exe",
        Exe = "RingHawk.exe",
        Icon = "https://raw.githubusercontent.com/adamglassner/waylo/main/assets/ringhawk-256.png",
    };

    /// <summary>NoteHawk (1.8.1): notes app in the Hawk office suite (early preview shell; was "Coming soon" before).</summary>
    private static CatalogEntry NoteHawk() => new()
    {
        Id = "notehawk",
        Name = "NoteHawk",
        Description = "Notes app that works like Evernote and OneNote together: notebooks with section tabs and pages, a note list and a Home dashboard. Early preview: browse three sample notebooks; editing, saving, search and sync are coming soon.",
        Repo = "adamglassner/notehawk",
        AssetPattern = "NoteHawk-*-win-x64.zip;NoteHawk-*-Setup.exe",
        Exe = "NoteHawk.exe",
        Icon = "https://raw.githubusercontent.com/adamglassner/notehawk/main/assets/notehawk-256.png",
    };

    /// <summary>PlanHawk (1.8.2): Microsoft Project replacement in the Hawk office suite (early preview shell; was "Coming soon" before).</summary>
    private static CatalogEntry PlanHawk() => new()
    {
        Id = "planhawk",
        Name = "PlanHawk",
        Description = "Project planner that does the job of Microsoft Project: a task sheet with a Gantt chart, dependencies between tasks, the critical path and a network diagram. Early preview: browse three sample projects; adding and editing tasks, saving and opening .mpp files are coming soon.",
        Repo = "adamglassner/PlanHawk",
        AssetPattern = "PlanHawk-*-win-x64.zip;PlanHawk-*-Setup.exe",
        Exe = "PlanHawk.exe",
        Icon = "https://raw.githubusercontent.com/adamglassner/PlanHawk/main/assets/planhawk-256.png",
    };

    /// <summary>PaperHawk (1.8.3): Adobe Acrobat replacement in the Hawk suite (early preview shell; was "Coming soon" before).</summary>
    private static CatalogEntry PaperHawk() => new()
    {
        Id = "paperhawk",
        Name = "PaperHawk",
        Description = "PDF viewer and editor that does the job of Adobe Acrobat: read PDFs with page thumbnails, bookmarks, zoom, one or two pages side by side and comments. Early preview: browse six sample documents; opening your own PDFs, editing, Fill & Sign and the other tools are coming soon.",
        Repo = "adamglassner/PaperHawk",
        AssetPattern = "PaperHawk-*-win-x64.zip;PaperHawk-*-Setup.exe",
        Exe = "PaperHawk.exe",
        Icon = "https://raw.githubusercontent.com/adamglassner/PaperHawk/main/assets/paperhawk-256.png",
    };

    /// <summary>DataHawk (1.8.4): Microsoft Access replacement in the Hawk office suite (early preview shell; was "Coming soon" before).</summary>
    private static CatalogEntry DataHawk() => new()
    {
        Id = "datahawk",
        Name = "DataHawk",
        Description = "Database app that does the job of Microsoft Access: tables in Datasheet and Design view with the Navigation Pane, sorting, searching and a relationships diagram. Early preview: browse four sample databases; creating databases, editing records, queries, forms and reports are coming soon.",
        Repo = "adamglassner/DataHawk",
        AssetPattern = "DataHawk-*-win-x64.zip;DataHawk-*-Setup.exe",
        Exe = "DataHawk.exe",
        Icon = "https://raw.githubusercontent.com/adamglassner/DataHawk/main/assets/datahawk-256.png",
    };

    /// <summary>CraftHawk (1.8.5): FrontPage/Dreamweaver-style website builder in the Hawk office suite (early preview shell; was "Coming soon" before).</summary>
    private static CatalogEntry CraftHawk() => new()
    {
        Id = "crafthawk",
        Name = "CraftHawk",
        Description = "Website builder with a visual (WYSIWYG) designer and a code editor side by side, like FrontPage, Dreamweaver and VS Code together, with Bootstrap, Tailwind, Material Design and WordPress built in and publishing through ShoreDrop. Early preview: browse three sample sites; editing, saving and uploading are coming soon.",
        Repo = "adamglassner/CraftHawk",
        AssetPattern = "CraftHawk-*-win-x64.zip;CraftHawk-*-Setup.exe",
        Exe = "CraftHawk.exe",
        Icon = "https://raw.githubusercontent.com/adamglassner/CraftHawk/main/assets/crafthawk-256.png",
    };

    /// <summary>AlbumGrab (1.8.0) — browser extension for Chrome and Edge, on the Browser Extensions page.</summary>
    private static CatalogEntry AlbumGrab() => new()
    {
        Id = "albumgrab",
        Name = "AlbumGrab",
        Description = "Chrome and Edge extension that saves photos and whole albums from Facebook, Instagram, Reddit and other sites, at full size, straight into a folder you choose.",
        Repo = "adamglassner/AlbumGrab",
        AssetPattern = "AlbumGrab-*.zip",
        Icon = "https://raw.githubusercontent.com/adamglassner/AlbumGrab/main/icons/icon128.png",
        Kind = "extension",
    };

    /// <summary>The two Notepad++ "Sample — replace me" entries that 1.0.x shipped.</summary>
    private static bool IsOldSample(CatalogEntry e) =>
        e.Sample || e.Key is "sample-installer-exe" or "sample-portable-zip";

    private static CatalogEntry ComingSoon(string id, string name, string description, string? icon = null) => new()
    {
        Id = id,
        Name = name,
        Description = description,
        Repo = "",
        AssetPattern = "*.exe",
        ComingSoon = true,
        Icon = icon,
    };

    /// <summary>Like the Ironite Launcher: look for new releases every 30 minutes while the launcher is open.</summary>
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromMinutes(30) };

    private async Task StartupAsync()
    {
        _updateTimer.Tick += async (_, _) =>
        {
            if (!App.Settings.CheckUpdatesOnStartup) return;
            await RefreshReleasesAsync(manual: false);
            await CheckLauncherUpdateAsync(manual: false);
        };
        _updateTimer.Start();

        if (!string.IsNullOrWhiteSpace(App.Settings.RemoteCatalogUrl))
            await LoadRemoteCatalogAsync(showResult: false);

        _ = AddMissingStartMenuEntriesAsync();

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
                             .Where(e => e.HasRepo || e.ComingSoon);
    }

    /// <summary>
    /// An online catalog written for an older launcher may describe a server without the details this version uses
    /// (finding and uninstalling it): take those from the built-in entry for the same app and server.
    /// </summary>
    private static void FillServerDetails(IEnumerable<CatalogEntry> entries)
    {
        var builtIn = SampleCatalog().Apps.Where(a => a.Server != null).ToList();
        foreach (var e in entries)
        {
            var d = builtIn.FirstOrDefault(b => b.Key.Equals(e.Key, StringComparison.OrdinalIgnoreCase))?.Server;
            if (d == null) continue;
            if (e.Server == null) { e.Server = d; continue; }
            if (!e.Server.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase)) continue;
            e.Server.WindowsUninstallKey ??= d.WindowsUninstallKey;
            e.Server.WindowsDataFolder ??= d.WindowsDataFolder;
        }
    }

    private static List<CatalogEntry>? ParseRemoteCatalog(string json)
    {
        try
        {
            var cat = System.Text.Json.JsonSerializer.Deserialize<CatalogFile>(json, JsonStore.Options);
            if (cat?.Apps == null) return null;
            foreach (var e in cat.Apps) e.FromRemote = true;
            FillServerDetails(cat.Apps);
            return cat.Apps.Where(e => (e.HasRepo || e.ComingSoon) && !IsOldSample(e)).ToList();
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
        RebuildServerCards();
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
            var repos = AllEntries().Where(e => e.HasRepo && !e.ComingSoon).Select(e => e.Repo.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var maxAge = manual ? ManualRefreshAge : AutoRefreshAge;
            foreach (var repo in repos)
            {
                _releases.TryGetValue(repo, out var cached);
                if (cached != null && DateTime.UtcNow - cached.FetchedAt < maxAge) continue;

                // Like the Ironite Launcher: github.com says which tag is latest without using the API allowance.
                var (tag, answered) = await GitHubService.GetLatestTagAsync(repo);
                if (answered && tag == "")
                {
                    _releases[repo] = new CachedRelease { Repo = repo, NoRelease = true, FetchedAt = DateTime.UtcNow };
                    continue;
                }
                if (answered && cached is { NoRelease: false } && cached.Tag == tag && cached.Assets.Count > 0)
                {
                    cached.FetchedAt = DateTime.UtcNow; // nothing new
                    continue;
                }

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

        if (!offline) await AutoInstallUpdatesAsync();

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
            MessageBox.Show(this, "This app comes from the online catalog (Settings → Remote catalog URL). Change it by editing catalog.json in the GitHub repo; the launcher picks it up on its next start or Reload catalog.", AppTitle);
            return;
        }
        var dlg = new RepoDialog(vm.Entry) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var idx = _localCatalog.Apps.IndexOf(vm.Entry);
        var updated = dlg.Result;
        // keep the same key so install state stays attached
        if (string.IsNullOrWhiteSpace(updated.Id)) updated.Id = vm.Entry.Key;
        // the dialog doesn't edit these: keep them (a browser extension stays on the Browser Extensions page)
        updated.Kind ??= vm.Entry.Kind;
        updated.Icon ??= vm.Entry.Icon;
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
            MessageBox.Show(this, "This app comes from the online catalog (Settings → Remote catalog URL), so it can't be removed here. Remove it from catalog.json in the GitHub repo, or clear the Remote catalog URL.", AppTitle);
            return;
        }
        if (MessageBox.Show(this, $"Remove \"{vm.Name}\" from the launcher?\n\nInstalled files are not deleted.", AppTitle,
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _localCatalog.Apps.Remove(vm.Entry);
        if (SampleCatalog().Apps.Any(d => d.Key.Equals(vm.Key, StringComparison.OrdinalIgnoreCase)))
            _localCatalog.RemovedDefaults.Add(vm.Key);
        TrySave(AppPaths.Catalog, _localCatalog);
        RebuildAppCards();
    }

    private void BhReleasePage_Click(object sender, RoutedEventArgs e)
    {
        var vm = Ctx<BytehawkAppVM>(sender);
        if (vm != null) ReportError(ShellHelper.OpenUrl(vm.ReleasePageUrl));
    }

    /// <summary>For CI screenshots: the Bytehawk Servers page and the server's options dialog, then exit.</summary>
    public async Task SaveServerScreenshotsAsync(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string theme = ThemeService.IsDarkApplied ? "dark" : "light";
            await Task.Delay(4000);
            NavServers.IsChecked = true;
            await Task.Delay(3000);
            SaveVisual(this, Path.Combine(folder, $"servers-page-{theme}.png"));
            // BH_TEST_SERVER picks which server's dialog to show (e.g. "WillowWallet Server"); otherwise the first one.
            string? only = Environment.GetEnvironmentVariable("BH_TEST_SERVER");
            bool Pick(CatalogEntry e) => e.Server != null && (string.IsNullOrEmpty(only) || e.Server.Name == only);
            var entry = _servers.Select(s => s.App.Entry).FirstOrDefault(Pick) ?? SampleCatalog().Apps.FirstOrDefault(Pick);
            if (entry != null)
            {
                var dialog = new ServerDialog(entry) { Owner = this };
                dialog.Show();
                await dialog.WhenLoaded;
                await Task.Delay(1500);
                SaveVisual(dialog, Path.Combine(folder, $"get-server-dialog-{theme}.png"));
                if (dialog.IsInstalled && Environment.GetEnvironmentVariable("BH_TEST_SERVER_UNINSTALL") == "1")
                {
                    // CI installed the real server first: confirm, uninstall it with its data, record what happened
                    dialog.OpenUninstall();
                    await Task.Delay(800);
                    SaveVisual(dialog, Path.Combine(folder, $"get-server-uninstall-{theme}.png"));
                    await dialog.ConfirmUninstallAsync(deleteData: true);
                    await Task.Delay(800);
                    SaveVisual(dialog, Path.Combine(folder, $"get-server-uninstalled-{theme}.png"));
                    File.WriteAllText(Path.Combine(folder, "uninstall-result.txt"), dialog.IsInstalled ? "still installed" : "uninstalled");
                    dialog.Close();
                    foreach (var c in _servers) c.Refresh();
                    await Task.Delay(800);
                    SaveVisual(this, Path.Combine(folder, $"servers-page-after-uninstall-{theme}.png"));
                }
                else dialog.Close();
            }
        }
        catch (Exception ex) { Log.Error("Screenshots", ex); }
        Application.Current.Shutdown();
    }

    private static void SaveVisual(Window w, string path)
    {
        w.UpdateLayout();
        var content = (FrameworkElement)w.Content;
        var m = content.Margin;
        double width = content.ActualWidth + m.Left + m.Right, height = content.ActualHeight + m.Top + m.Bottom;
        double scale = System.Windows.Media.VisualTreeHelper.GetDpi(w).DpiScaleX * 1.5;
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(w.Background, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new System.Windows.Media.VisualBrush(content), null, new Rect(m.Left, m.Top, content.ActualWidth, content.ActualHeight));
        }
        bmp.Render(visual);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var f = File.Create(path);
        enc.Save(f);
    }

    // =====================================================================
    // Bytehawk Servers
    // =====================================================================

    /// <summary>One card per server that goes with a Bytehawk app (e.g. WillowWallet Server).</summary>
    private void RebuildServerCards()
    {
        var apps = _apps.Where(a => a.HasServer).ToList();
        foreach (var c in _servers.ToList())
            if (!apps.Any(a => a.Key.Equals(c.Key, StringComparison.OrdinalIgnoreCase)) && !c.IsDownloading)
                _servers.Remove(c);
        for (int i = 0; i < apps.Count; i++)
        {
            var card = _servers.FirstOrDefault(c => c.Key.Equals(apps[i].Key, StringComparison.OrdinalIgnoreCase));
            if (card == null) _servers.Insert(Math.Min(i, _servers.Count), new ServerCardVM(apps[i]));
            else { card.SetApp(apps[i]); card.Refresh(); }
        }
        UpdateEmptyStates();
    }

    private DateTime _serverReleasesCheckedAt;

    /// <summary>Latest release of each server (one GitHub call per server; at most every 30 minutes unless asked).</summary>
    private async Task RefreshServerReleasesAsync(bool manual)
    {
        if (!manual && DateTime.UtcNow - _serverReleasesCheckedAt < TimeSpan.FromMinutes(30) && _servers.All(c => c.Release != null)) return;
        _serverReleasesCheckedAt = DateTime.UtcNow;
        ServersRefreshButton.IsEnabled = false;
        ServersRefreshText.Text = "Checking…";
        try
        {
            foreach (var card in _servers.ToList())
            {
                var repo = string.IsNullOrWhiteSpace(card.Server.Repo) ? card.App.Entry.Repo : card.Server.Repo!;
                var r = await GitHubService.GetLatestReleaseWithPrefixAsync(repo, card.Server.TagPrefix);
                card.SetRelease(r.Status == GitHubStatus.Ok ? r.Value : null, r.Status switch
                {
                    GitHubStatus.Ok => null,
                    GitHubStatus.NoRelease => "Not released yet",
                    GitHubStatus.RateLimited => "GitHub limit reached; try again later",
                    GitHubStatus.NetworkError => "Offline: couldn't check the latest version",
                    _ => "Couldn't check the latest version",
                });
                card.Refresh();
            }
        }
        finally
        {
            ServersRefreshButton.IsEnabled = true;
            ServersRefreshText.Text = "Check for updates";
        }
    }

    private async void ServersRefresh_Click(object sender, RoutedEventArgs e) => await RefreshServerReleasesAsync(manual: true);

    /// <summary>Install / Update downloads and runs the Windows installer; Open panel opens the web panel.</summary>
    private async void SvPrimary_Click(object sender, RoutedEventArgs e)
    {
        var card = Ctx<ServerCardVM>(sender);
        if (card == null) return;
        if (card.IsInstalled && !card.HasUpdate) { OpenServerPanel(card); return; }
        if (card.Release == null) await RefreshServerReleasesAsync(manual: true);
        var asset = card.Release == null ? null : GitHubService.MatchAsset(card.Release.Assets, card.Server.WindowsAsset);
        if (asset == null) { card.Note = card.ReleaseProblem ?? "No Windows installer in the latest release."; return; }
        await InstallServerAsync(card, asset);
    }

    private void SvOpenPanel_Click(object sender, RoutedEventArgs e)
    {
        if (Ctx<ServerCardVM>(sender) is { } card) OpenServerPanel(card);
    }

    private void OpenServerPanel(ServerCardVM card) => ReportError(ShellHelper.OpenUrl($"http://localhost:{card.Server.Port}/"));

    /// <summary>Docker, Linux and the Windows options (and uninstall) in one dialog.</summary>
    private async void SvMore_Click(object sender, RoutedEventArgs e)
    {
        if (Ctx<ServerCardVM>(sender) is { } card) await ShowServerDialogAsync(card, uninstall: false);
    }

    private async void SvUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (Ctx<ServerCardVM>(sender) is { } card) await ShowServerDialogAsync(card, uninstall: true);
    }

    private async Task ShowServerDialogAsync(ServerCardVM card, bool uninstall)
    {
        if (card.IsDownloading) return;
        var dialog = new ServerDialog(card.App.Entry, uninstall) { Owner = this };
        var download = dialog.ShowDialog() == true;
        card.Refresh();
        if (download && dialog.WindowsInstaller is { } asset) await InstallServerAsync(card, asset);
    }

    private void SvReleasePage_Click(object sender, RoutedEventArgs e)
    {
        if (Ctx<ServerCardVM>(sender) is not { } card) return;
        var repo = string.IsNullOrWhiteSpace(card.Server.Repo) ? card.App.Entry.Repo : card.Server.Repo!;
        ReportError(ShellHelper.OpenUrl(card.Release?.HtmlUrl ?? $"https://github.com/{repo}/releases"));
    }

    private void SvCancel_Click(object sender, RoutedEventArgs e) => Ctx<ServerCardVM>(sender)?.Download?.Cts.Cancel();

    /// <summary>Downloads the server's Windows installer, checks it, runs it (Windows asks for permission) and waits.</summary>
    private async Task InstallServerAsync(ServerCardVM card, ReleaseAsset asset)
    {
        Directory.CreateDirectory(DownloadFolder);
        var dest = Path.Combine(DownloadFolder, SafeFileName(asset.Name));
        var item = new DownloadItemVM { AppName = card.Server.Name, FileName = asset.Name, Destination = dest };
        _downloads.Insert(0, item);
        card.Note = null;
        card.Download = item;
        try
        {
            await Downloader.DownloadAsync(asset.DownloadUrl, dest, new Progress<DownloadProgress>(item.Report), item.Cts.Token);
            item.Detail = "Checking the download…";
            var problem = await AppInstaller.VerifyDownloadAsync(dest, asset, item.Cts.Token);
            if (problem != null)
            {
                try { File.Delete(dest); } catch { /* ignore */ }
                throw new InvalidDataException(problem);
            }
            item.Percent = 100;
            item.IsIndeterminate = false;
            item.IsActive = false;
            item.Status = "Downloaded";
            item.Detail = "Installer opened. Windows asks to allow it, since the server runs as a service.";
            card.Download = null;
            card.Busy = true;
            card.Note = "Finish the installer to continue…";
            int? exit = null;
            try
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dest) { UseShellExecute = true });
                if (p != null) { await p.WaitForExitAsync(); exit = p.ExitCode; }
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { card.Note = "Install was cancelled."; return; }
            card.Refresh();
            card.Note = card.IsInstalled ? $"{card.Server.Name} {card.Installed!.Version} is running on this PC." :
                        exit is 0 or null ? "The installer closed without installing." : $"The installer stopped (code {exit}).";
        }
        catch (OperationCanceledException)
        {
            try { if (File.Exists(dest)) File.Delete(dest); } catch { /* ignore */ }
            item.Status = "Cancelled";
            item.Detail = "Download cancelled.";
            item.IsActive = false;
        }
        catch (Exception ex)
        {
            Log.Error("Server download failed", ex);
            item.Status = "Failed";
            item.Detail = $"Download failed: {ex.Message}";
            item.IsActive = false;
            card.Note = "Server download failed. See the Downloads page for details.";
        }
        finally
        {
            card.Download = null;
            card.Busy = false;
            card.Refresh();
        }
    }

    private async void BhPrimary_Click(object sender, RoutedEventArgs e)
    {
        var vm = Ctx<BytehawkAppVM>(sender);
        if (vm == null) return;
        if (!vm.IsInstalled || vm.IsMissing || vm.HasUpdate)
        {
            var firstInstall = !vm.IsInstalled || vm.IsMissing;
            // A browser extension can't be added to the browser automatically: show how, right after the first download.
            if (await InstallAsync(vm) && vm.IsExtension && firstInstall) ShowAddToBrowser(vm, justInstalled: true);
        }
        else if (vm.NeedsLaunchChoice) ChooseLaunchFile(vm);
        else LaunchApp(vm);
    }

    private void BhShowFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Ctx<BytehawkAppVM>(sender)?.ExtensionFolder is { } folder && Directory.Exists(folder)) ShellHelper.OpenFolder(folder);
    }

    /// <summary>Browser extensions: the "Add to browser" steps (Load unpacked in Chrome or Edge).</summary>
    private void ShowAddToBrowser(BytehawkAppVM vm, bool justInstalled = false)
    {
        var folder = vm.ExtensionFolder;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) { RefreshCard(vm); return; } // card offers to reinstall
        new ExtensionDialog(vm.Name, folder, justInstalled) { Owner = this }.ShowDialog();
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
        if (vm.IsExtension) { ShowAddToBrowser(vm); return; }
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
        vm.Installed.StartMenuChecked = false;
        TrySave(AppPaths.Installed, _installed);
        RefreshCard(vm);
        _ = EnsureStartMenuAsync(vm.Name, vm.Entry.Description, vm.Installed).ContinueWith(_ =>
            Dispatcher.InvokeAsync(() => TrySave(AppPaths.Installed, _installed)), TaskScheduler.Default);
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

    /// <summary>
    /// Download → check → install → record, the same way the Ironite Launcher does it:
    /// a .zip is unpacked into apps\&lt;app&gt;\&lt;version&gt;, an Inno Setup / NSIS setup program is run silently into
    /// apps\&lt;app&gt;\app, and the program to launch is found automatically. Updating is installing the newer release.
    /// </summary>
    /// <param name="automatic">True for background auto-updates: never shows a dialog.</param>
    private async Task<bool> InstallAsync(BytehawkAppVM vm, bool automatic = false)
    {
        if (vm.IsDownloading || vm.Busy || vm.IsComingSoon || !vm.HasRepo) return false;
        vm.Note = null;
        vm.Busy = true;
        DownloadItemVM? item = null;
        try
        {
            var previous = Installed(vm.Entry);
            if (previous != null && AppInstaller.IsManagedKind(previous.Kind) && AppInstaller.IsRunning(previous))
            {
                vm.Note = $"Close {vm.Name} to update it.";
                return false;
            }

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
                    return false;
                }
            }
            if (release == null || release.NoRelease)
            {
                vm.Note = "No published release on GitHub yet.";
                return false;
            }

            // 2) pick the asset
            var asset = GitHubService.MatchAsset(release.Assets, vm.Entry.AssetPattern);
            if (asset == null || string.IsNullOrEmpty(asset.DownloadUrl))
            {
                vm.Note = $"No file in release {GitHubService.DisplayVersion(release.Tag)} matches \"{vm.Entry.AssetPattern}\".";
                return false;
            }

            // 3) download with progress, then check size and SHA-256
            Directory.CreateDirectory(DownloadFolder);
            var dest = Path.Combine(DownloadFolder, SafeFileName(asset.Name));
            var verb = previous == null ? "Installing" : "Updating";
            item = new DownloadItemVM { AppName = vm.Name, FileName = asset.Name, Destination = dest };
            _downloads.Insert(0, item);
            vm.Busy = false;
            vm.Download = item;
            Log.Info($"Downloading {asset.DownloadUrl} -> {dest}");

            try
            {
                await Downloader.DownloadAsync(asset.DownloadUrl, dest, new Progress<DownloadProgress>(item.Report), item.Cts.Token);
                item.Detail = "Checking the download…";
                var problem = await AppInstaller.VerifyDownloadAsync(dest, asset, item.Cts.Token);
                if (problem != null)
                {
                    try { File.Delete(dest); } catch { /* ignore */ }
                    throw new InvalidDataException(problem);
                }
            }
            catch (OperationCanceledException)
            {
                try { if (File.Exists(dest) && previous?.DownloadedFile != dest) File.Delete(dest); } catch { /* ignore */ }
                item.Status = "Cancelled";
                item.Detail = "Download cancelled. Nothing was installed.";
                item.IsActive = false;
                vm.Download = null;
                return false;
            }
            catch (Exception ex)
            {
                Log.Error("Download failed", ex);
                item.Status = "Failed";
                item.Detail = ex is InvalidDataException ? $"Download failed: {ex.Message}. Please try again."
                            : ex is IOException ? $"Couldn't save the file: {ex.Message}" : $"Download failed: {ex.Message}";
                item.IsActive = false;
                vm.Download = null;
                vm.Note = "Download failed. See the Downloads page for details.";
                return false;
            }

            item.Percent = 100;
            item.IsIndeterminate = false;
            item.Status = verb;
            item.Detail = $"{verb} {vm.Name} {GitHubService.DisplayVersion(release.Tag)}…";
            Log.Info($"Downloaded {dest} ({new FileInfo(dest).Length} bytes)");
            vm.Download = null;
            vm.Busy = true;

            // 4) install
            var ext = Path.GetExtension(dest).ToLowerInvariant();
            var setupKind = ext == ".exe" ? AppInstaller.DetectSetupKind(dest) : null;
            var baseFolder = AppInstaller.AppFolder(vm.Entry.Key);
            InstalledApp record;

            if (vm.IsExtension && ext != ".zip")
            {
                item.Status = "Failed";
                item.Detail = $"{asset.Name} isn't a .zip, so it can't be unpacked as a browser extension.";
                vm.Note = "This release has no extension .zip to install.";
                return false;
            }

            if (ext == ".zip" || setupKind != null)
            {
                // Managed install: silent, into the launcher's own apps folder.
                try
                {
                    record = vm.IsExtension
                        ? await Task.Run(() => AppInstaller.InstallExtensionZip(dest, baseFolder, release.Tag))
                        : ext == ".zip"
                        ? await Task.Run(() => AppInstaller.InstallZip(dest, baseFolder, release.Tag, vm.Entry.Exe))
                        : await AppInstaller.InstallSetupAsync(dest, setupKind!, baseFolder, release.Tag, vm.Entry.Exe);
                }
                catch (Exception ex)
                {
                    Log.Error("Install failed", ex);
                    item.Status = "Failed";
                    item.Detail = $"Couldn't install {vm.Name}: {ex.Message}";
                    vm.Note = ex is IOException
                        ? $"Couldn't install. Close {vm.Name} if it's running, then try again."
                        : "Install failed. See the Downloads page for details.";
                    return false;
                }
                record.AssetName = asset.Name;
                record.DownloadedFile = dest;
                if (record.LaunchPath == null && !automatic && !vm.IsExtension)
                    record.LaunchPath = PickExe($"Choose the main program for {vm.Name}", record.InstallFolder);
                item.Status = "Installed";
                item.Detail = vm.IsExtension && previous != null
                    ? $"{vm.Name} {GitHubService.DisplayVersion(release.Tag)} is in {record.InstallFolder}. Your browser loads it the next time it starts."
                    : $"{vm.Name} {GitHubService.DisplayVersion(release.Tag)} is installed in {record.InstallFolder}";

                // The setup file for the previous version isn't needed any more.
                if (previous?.DownloadedFile is { } old && !old.Equals(dest, StringComparison.OrdinalIgnoreCase))
                    try { if (File.Exists(old)) File.Delete(old); } catch { /* ignore */ }
            }
            else
            {
                // Not something the launcher can install by itself (an .msi or an unknown .exe): hand it to the user, as 1.0 did.
                if (automatic) { item.Status = "Downloaded"; item.Detail = $"Saved to {dest}. Run it to finish updating."; return false; }
                record = new InstalledApp
                {
                    Version = release.Tag,
                    InstalledAt = DateTime.Now,
                    AssetName = asset.Name,
                    DownloadedFile = dest,
                    Kind = ext.TrimStart('.'),
                };
                var err = ShellHelper.Launch(dest);
                if (err != null)
                {
                    item.Detail = err;
                    vm.Note = "The installer didn't start. You can run it from the Downloads page.";
                }
                else item.Detail = $"Started {asset.Name}";
                item.Status = "Completed";
                if (previous?.LaunchPath is { } keep && File.Exists(keep) &&
                    !string.Equals(keep, previous.DownloadedFile, StringComparison.OrdinalIgnoreCase))
                    record.LaunchPath = keep;
                else
                    record.LaunchPath = null; // card shows "Choose launch file" once setup finishes
            }

            _installed[vm.Entry.Key] = record;
            await EnsureStartMenuAsync(vm.Name, vm.Entry.Description, record);
            TrySave(AppPaths.Installed, _installed);
            Log.Info($"Recorded {vm.Entry.Key} {record.Version} kind={record.Kind} launch={record.LaunchPath}");
            return true;
        }
        finally
        {
            if (item != null && item.IsActive) item.IsActive = false;
            vm.Busy = false;
            if (vm.Download != null && !vm.Download.IsActive) vm.Download = null;
            RefreshCard(vm);
        }
    }

    /// <summary>Installs available updates for apps the launcher manages, unless they're open (Settings → Install updates automatically).</summary>
    private async Task AutoInstallUpdatesAsync()
    {
        if (!App.Settings.AutoUpdateApps) return;
        foreach (var vm in _apps.ToList())
        {
            if (!vm.HasUpdate || vm.IsDownloading || vm.Busy || vm.Installed is not { } inst) continue;
            if (!AppInstaller.IsManagedKind(inst.Kind) || AppInstaller.IsRunning(inst)) continue;
            Log.Info($"Updating {vm.Key} automatically to {vm.LatestVersion}");
            if (await InstallAsync(vm, automatic: true))
                Log.Info($"{vm.Key} updated automatically to {vm.LatestVersion}");
        }
    }

    private async void BhUninstall_Click(object sender, RoutedEventArgs e)
    {
        var vm = Ctx<BytehawkAppVM>(sender);
        if (vm?.Installed is not { } inst || vm.IsDownloading || vm.Busy) return;
        if (!AppInstaller.IsManagedKind(inst.Kind))
        {
            MessageBox.Show(this, $"{vm.Name} was installed by its own setup program. Uninstall it from Windows Settings → Apps.", AppTitle,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (AppInstaller.IsRunning(inst))
        {
            MessageBox.Show(this, $"Close {vm.Name} first, then uninstall it.", AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var question = vm.IsExtension
            ? $"Uninstall {vm.Name}?\n\nIts folder is deleted. Also click Remove on {vm.Name} in your browser's Extensions page (chrome://extensions or edge://extensions)."
            : $"Uninstall {vm.Name}?\n\nThe program is removed. Your own files and settings for {vm.Name} are kept.";
        if (MessageBox.Show(this, question, AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        vm.Busy = true;
        try
        {
            await AppInstaller.UninstallAsync(inst, AppInstaller.AppFolder(vm.Key));
            if (inst.DownloadedFile is { } f) try { if (File.Exists(f)) File.Delete(f); } catch { /* ignore */ }
            _installed.Remove(vm.Key);
            TrySave(AppPaths.Installed, _installed);
            RemoveOurStartMenuEntry(vm.Name);
            Log.Info($"Uninstalled {vm.Key}");
        }
        catch (Exception ex)
        {
            Log.Error($"Uninstall {vm.Key}", ex);
            vm.Note = $"Couldn't remove every file ({ex.Message}). Close {vm.Name} and try again.";
        }
        finally
        {
            vm.Busy = false;
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
        SoftwareList.Tag = _library.Any(x => !string.IsNullOrWhiteSpace(x.Category));
        BuildCategoryBar(SoftwareCategoryBar, _library.Select(x => x.Category), _softwareCategory, c =>
        {
            _softwareCategory = c;
            CollectionViewSource.GetDefaultView(_software).Refresh();
            UpdateEmptyStates();
        });
        UpdateEmptyStates();
    }

    // ---------- categories (My Software + Web Apps) ----------

    private string? _softwareCategory; // null = All
    private string? _webCategory;

    private static bool MatchesCategory(string? selected, string category) =>
        selected == null || string.Equals(selected, category, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>Every category used in My Software or Web Apps (offered as suggestions in both dialogs).</summary>
    private IEnumerable<string> AllCategories() =>
        _library.Select(x => x.Category).Concat(_webApps.Select(x => x.Category))
                .Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim())
                .Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Filter chips: All, each category, and Uncategorized. Hidden while nothing is categorized.</summary>
    private void BuildCategoryBar(WrapPanel bar, IEnumerable<string?> entryCategories, string? selected, Action<string?> onSelect)
    {
        var all = entryCategories.Select(c => (c ?? "").Trim()).ToList();
        var names = all.Where(c => c.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase)
                       .OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase).ToList();
        bar.Children.Clear();
        if (names.Count == 0)
        {
            bar.Visibility = Visibility.Collapsed;
            if (selected != null) onSelect(null);
            return;
        }
        if (all.Any(c => c.Length == 0)) names.Add("Uncategorized");
        if (selected != null && !names.Contains(selected, StringComparer.CurrentCultureIgnoreCase))
        {
            selected = null;
            onSelect(null);
        }
        var style = (Style)FindResource("SegmentItem");
        void Add(string label, string? value, string glyph)
        {
            var rb = new RadioButton
            {
                Content = label, Tag = glyph, GroupName = bar.Name, Style = style, Margin = new Thickness(0, 0, 4, 4),
                IsChecked = string.Equals(value, selected, StringComparison.CurrentCultureIgnoreCase),
            };
            rb.Checked += (_, _) => onSelect(value);
            bar.Children.Add(rb);
        }
        Add("All", null, "\uE71D");
        foreach (var n in names) Add(n, n, n == "Uncategorized" ? "\uE8B7" : "\uE8EC");
        bar.Visibility = Visibility.Visible;
    }

    private void AddSoftware_Click(object sender, RoutedEventArgs e)
    {
        var file = SoftwareDialog.PickProgram(this);
        if (file == null) return;
        var dlg = new SoftwareDialog(null, file, AllCategories()) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _library.Add(dlg.Result);
        TrySave(AppPaths.Library, _library);
        RebuildSoftwareCards();
    }

    private void AddStoreApp_Click(object sender, RoutedEventArgs e)
    {
        var picker = new StoreAppPickerDialog { Owner = this };
        if (picker.ShowDialog() != true || picker.Result == null) return;
        if (_library.Any(x => string.Equals(x.AppId, picker.Result.AppId, StringComparison.OrdinalIgnoreCase)) &&
            MessageBox.Show(this, $"{picker.Result.Name} is already in My Software. Add it again?", AppTitle,
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var dlg = new SoftwareDialog(null, null, AllCategories(), picker.Result) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _library.Add(dlg.Result);
        TrySave(AppPaths.Library, _library);
        RebuildSoftwareCards();
    }

    private void SwLaunch_Click(object sender, RoutedEventArgs e)
    {
        var vm = Ctx<SoftwareVM>(sender);
        if (vm == null) return;
        ReportError(vm.IsStoreApp
            ? (StoreApps.Exists(vm.Entry.AppId) ? StoreApps.Launch(vm.Entry.AppId) : $"{vm.Name} isn't installed anymore. Reinstall it from the Microsoft Store.")
            : ShellHelper.Launch(vm.Entry.Path, vm.Entry.Arguments, vm.Entry.RunAsAdmin));
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
        var dlg = new SoftwareDialog(vm.Entry, null, AllCategories()) { Owner = this };
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
        WebList.Tag = _webApps.Any(x => !string.IsNullOrWhiteSpace(x.Category));
        BuildCategoryBar(WebCategoryBar, _webApps.Select(x => x.Category), _webCategory, c =>
        {
            _webCategory = c;
            CollectionViewSource.GetDefaultView(_web).Refresh();
            UpdateEmptyStates();
        });
        UpdateEmptyStates();
        _ = FetchMissingWebIconsAsync();
    }

    /// <summary>Web app ids we've already tried to fetch an icon for in this session (avoids retrying broken sites).</summary>
    private readonly HashSet<string> _iconAttempted = new();

    private async Task FetchMissingWebIconsAsync()
    {
        foreach (var vm in _web.Where(v => v.NoIcon).ToList())
        {
            if (!_iconAttempted.Add(vm.Entry.Id)) continue;
            await FetchWebIconAsync(vm.Entry, force: false);
        }
    }

    /// <summary>Downloads the site's icon into the cache and shows it on the card. Returns true on success.</summary>
    private async Task<bool> FetchWebIconAsync(WebAppEntry entry, bool force)
    {
        if (force) WebIconService.DeleteCached(entry.Id);
        bool ok;
        try { ok = await WebIconService.FetchAsync(entry.Url, WebIconService.CachePath(entry.Id)); }
        catch (Exception ex) { Log.Error($"Icon fetch {entry.Url}", ex); ok = false; }
        if (ok)
        {
            var icon = WebIconService.LoadCached(entry.Id);
            // the card may have been rebuilt while we were downloading
            foreach (var vm in _web.Where(v => v.Entry.Id == entry.Id)) vm.Icon = icon;
        }
        return ok;
    }

    private async void WebRefreshIcon_Click(object sender, RoutedEventArgs e)
    {
        var vm = Ctx<WebAppVM>(sender);
        if (vm == null) return;
        if (sender is Button b) b.IsEnabled = false;
        try
        {
            if (!await FetchWebIconAsync(vm.Entry, force: true))
            {
                vm.Icon = null;
                ReportError($"Couldn't get an icon from {vm.Host}. The site may be offline, block downloads, or only offer an SVG icon. The globe icon is used instead.");
            }
        }
        finally { if (sender is Button b2) b2.IsEnabled = true; }
    }

    private void AddWebApp_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new WebAppDialog(null, AllCategories()) { Owner = this };
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
        var dlg = new WebAppDialog(vm.Entry, AllCategories()) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var idx = _webApps.FindIndex(x => x.Id == vm.Entry.Id);
        if (idx >= 0) _webApps[idx] = dlg.Result; else _webApps.Add(dlg.Result);
        TrySave(AppPaths.WebApps, _webApps);
        if (!string.Equals(vm.Entry.Url, dlg.Result.Url, StringComparison.OrdinalIgnoreCase))
        {
            // new address: fetch that site's icon
            WebIconService.DeleteCached(dlg.Result.Id);
            _iconAttempted.Remove(dlg.Result.Id);
        }
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
        WebIconService.DeleteCached(vm.Entry.Id);
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
        PageServers.Visibility = NavServers.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageExtensions.Visibility = NavExtensions.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (NavServers.IsChecked == true)
        {
            foreach (var c in _servers) c.Refresh();
            _ = RefreshServerReleasesAsync(manual: false);
        }
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
        _extensionsView?.Refresh();
        CollectionViewSource.GetDefaultView(_servers).Refresh();
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
        if (ServersEmpty != null)
            ServersEmpty.Visibility = CollectionViewSource.GetDefaultView(_servers).IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        if (ExtensionsEmpty != null && _extensionsView != null)
            ExtensionsEmpty.Visibility = _extensionsView.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
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
        AutoUpdateBox.IsChecked = App.Settings.AutoUpdateApps;
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

    private void AutoUpdate_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        App.Settings.AutoUpdateApps = AutoUpdateBox.IsChecked == true;
        TrySave(AppPaths.Settings, App.Settings);
        if (App.Settings.AutoUpdateApps) _ = AutoInstallUpdatesAsync();
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
        MergeBuiltInApps();
        if (!string.IsNullOrWhiteSpace(App.Settings.RemoteCatalogUrl)) await LoadRemoteCatalogAsync(showResult: false);
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

    // =====================================================================
    // Shortcuts: Start menu for installed Bytehawk apps, desktop shortcuts for any card
    // =====================================================================

    /// <summary>
    /// Adds Programs\Bytehawk Studio\&lt;name&gt;.lnk for an installed app's .exe unless a Start menu
    /// shortcut to it already exists (installers usually add their own). Runs off the UI thread.
    /// </summary>
    private static async Task EnsureStartMenuAsync(string name, string? description, InstalledApp record)
    {
        if (record.StartMenuChecked || string.IsNullOrEmpty(record.LaunchPath)) return;
        var exe = record.LaunchPath;
        try
        {
            var created = await Task.Run(() => ShortcutService.EnsureStartMenuEntry(name, exe, description));
            if (created != null) Log.Info($"Start menu entry added: {created}");
            record.StartMenuChecked = true;
        }
        catch (Exception ex) { Log.Error($"Start menu entry for {name}", ex); }
    }

    private async Task AddMissingStartMenuEntriesAsync()
    {
        var changed = false;
        foreach (var vm in _apps.Where(a => a.Installed is { StartMenuChecked: false, LaunchPath: not null }).ToList())
        {
            await EnsureStartMenuAsync(vm.Name, vm.Entry.Description, vm.Installed!);
            changed |= vm.Installed!.StartMenuChecked;
        }
        if (changed) TrySave(AppPaths.Installed, _installed);
    }

    private static void RemoveOurStartMenuEntry(string name)
    {
        try
        {
            var lnk = Path.Combine(ShortcutService.StartMenuDir, ShortcutService.SafeName(name) + ".lnk");
            if (File.Exists(lnk)) File.Delete(lnk);
            if (Directory.Exists(ShortcutService.StartMenuDir) && !Directory.EnumerateFileSystemEntries(ShortcutService.StartMenuDir).Any())
                Directory.Delete(ShortcutService.StartMenuDir);
        }
        catch (Exception ex) { Log.Error($"Remove Start menu entry {name}", ex); }
    }

    /// <summary>Creates a desktop shortcut via <paramref name="create"/>(path), asking before replacing an existing file.</summary>
    private void MakeDesktopShortcut(string name, string extension, Action<string> create)
    {
        var path = Path.Combine(ShortcutService.DesktopDir, ShortcutService.SafeName(name) + extension);
        if (File.Exists(path) && MessageBox.Show(this, $"There's already a \"{Path.GetFileName(path)}\" on your desktop. Replace it?",
                AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            create(path);
            MessageBox.Show(this, $"Added \"{Path.GetFileNameWithoutExtension(path)}\" to your desktop.", AppTitle,
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log.Error($"Desktop shortcut {path}", ex);
            ReportError($"Couldn't create the desktop shortcut:\n{ex.Message}");
        }
    }

    private void BhDesktop_Click(object sender, RoutedEventArgs e)
    {
        var vm = Ctx<BytehawkAppVM>(sender);
        if (vm?.Installed?.LaunchPath is not { } target || !File.Exists(target)) { if (vm != null) RefreshCard(vm); return; }
        if (target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            MakeDesktopShortcut(vm.Name, ".lnk", p => File.Copy(target, p, overwrite: true));
        else
            MakeDesktopShortcut(vm.Name, ".lnk", p => ShortcutService.CreateLnk(p, target, iconLocation: target + ",0", description: vm.Entry.Description));
    }

    private void SwDesktop_Click(object sender, RoutedEventArgs e)
    {
        var vm = Ctx<SoftwareVM>(sender);
        if (vm == null) return;
        var entry = vm.Entry;
        if (vm.IsStoreApp)
        {
            // Store apps have no .exe: point at explorer.exe shell:AppsFolder\<id> and use the app's own icon.
            var ico = Path.Combine(WebIconService.IconsDir, "store-" + entry.Id + ".ico");
            if (StoreApps.GetIcon(entry.AppId) is System.Windows.Media.Imaging.BitmapSource icon) ShortcutService.WriteIco(icon, ico);
            MakeDesktopShortcut(entry.Name, ".lnk", p => ShortcutService.CreateLnk(p, "explorer.exe", StoreApps.ShellPath(entry.AppId), "",
                File.Exists(ico) ? ico + ",0" : null, entry.Description));
            return;
        }
        if (!File.Exists(entry.Path)) { ReportError($"The file isn't there anymore:\n{entry.Path}\n\nUse Edit to point to it again."); return; }
        if (entry.Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(entry.Arguments) && !entry.RunAsAdmin)
            MakeDesktopShortcut(entry.Name, ".lnk", p => File.Copy(entry.Path, p, overwrite: true));
        else
            MakeDesktopShortcut(entry.Name, ".lnk", p =>
            {
                ShortcutService.CreateLnk(p, entry.Path, entry.Arguments, null, entry.Path + ",0", entry.Description);
                if (entry.RunAsAdmin) ShortcutService.SetRunAsAdmin(p);
            });
    }

    private void SwWebsite_Click(object sender, RoutedEventArgs e)
    {
        var vm = Ctx<SoftwareVM>(sender);
        if (vm != null && vm.HasWebsite) ReportError(ShellHelper.OpenUrl(vm.Website));
    }

    private void WebDesktop_Click(object sender, RoutedEventArgs e)
    {
        var vm = Ctx<WebAppVM>(sender);
        if (vm == null) return;
        string? ico = null;
        var png = WebIconService.CachePath(vm.Entry.Id);
        if (File.Exists(png) && ShortcutService.LoadPng(png) is { } bmp)
        {
            ico = Path.Combine(WebIconService.IconsDir, vm.Entry.Id + ".ico");
            if (!ShortcutService.WriteIco(bmp, ico)) ico = null;
        }
        MakeDesktopShortcut(vm.Name, ".url", p => ShortcutService.CreateUrl(p, vm.Url, ico));
    }

    // =====================================================================
    // Web Apps export / import
    // =====================================================================

    private void ExportWebApps_Click(object sender, RoutedEventArgs e)
    {
        if (_webApps.Count == 0) { ReportError("There are no web apps to export yet."); return; }
        var dlg = new SaveFileDialog
        {
            Title = "Export web apps",
            FileName = $"Bytehawk web apps {DateTime.Now:yyyy-MM-dd}",
            DefaultExt = ".json",
            Filter = "Bytehawk web apps (*.json)|*.json|Browser bookmarks (*.html)|*.html",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            if (dlg.FileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                File.WriteAllText(dlg.FileName, WebAppTransfer.ToBookmarksHtml(_webApps));
            else
                File.WriteAllText(dlg.FileName, WebAppTransfer.ToJson(_webApps));
            MessageBox.Show(this, $"Exported {_webApps.Count} web app(s) to\n{dlg.FileName}", AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ReportError($"Couldn't export:\n{ex.Message}"); }
    }

    private void ImportWebApps_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Import web apps",
            Filter = "Web apps or bookmarks (*.json;*.html;*.htm)|*.json;*.html;*.htm|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        List<WebAppEntry> incoming;
        try { incoming = WebAppTransfer.Read(File.ReadAllText(dlg.FileName)); }
        catch (Exception ex) { ReportError($"Couldn't read that file:\n{ex.Message}"); return; }
        if (incoming.Count == 0) { ReportError("No web addresses were found in that file."); return; }

        var known = new HashSet<string>(_webApps.Select(w => WebAppTransfer.UrlKey(w.Url)), StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var w in incoming)
        {
            if (!known.Add(WebAppTransfer.UrlKey(w.Url))) continue;
            _webApps.Add(w);
            added++;
        }
        TrySave(AppPaths.WebApps, _webApps);
        RebuildWebCards();
        var skipped = incoming.Count - added;
        MessageBox.Show(this, $"Imported {added} web app(s)." + (skipped > 0 ? $" Skipped {skipped} already in your list." : ""),
            AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
    }

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

    /// <summary>
    /// Apps like Draftly can update themselves. If an installed app's program is now a newer version
    /// than the launcher recorded, record that, so the launcher doesn't offer the same update again.
    /// </summary>
    private bool NoteSelfUpdates()
    {
        bool changed = false;
        foreach (var inst in _installed.Values)
        {
            try
            {
                string? exe = inst.LaunchPath is { } lp && File.Exists(lp) ? lp : null;
                if (exe == null && inst.InstallFolder is { } f && Directory.Exists(f) &&
                    Directory.GetFiles(f, "*.exe", SearchOption.TopDirectoryOnly) is { Length: 1 } only)
                    exe = only[0];
                if (exe == null) continue;
                var fileVersion = SoftwareDialog.ReadFileVersion(exe);
                if (fileVersion.Length > 0 && GitHubService.CompareVersions(fileVersion, inst.Version) > 0)
                {
                    inst.Version = "v" + fileVersion;
                    changed = true;
                }
            }
            catch { }
        }
        return changed;
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
