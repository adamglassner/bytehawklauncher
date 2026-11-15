using System.Windows;
using Bytehawk.Launcher.Models;
using Bytehawk.Launcher.Services;

namespace Bytehawk.Launcher.Views;

/// <summary>
/// "Get server" for an app that has a self-hosted server (WillowWallet Server): the Windows installer,
/// a Docker command to copy, or the release page for Linux and portable builds.
/// </summary>
public partial class ServerDialog : Window
{
    private readonly ServerDownload _server;
    private readonly string _repo;
    private CachedRelease? _release;
    private InstalledServer? _installed;
    private readonly string _baseWindowsText;

    /// <summary>The Windows installer to download when the dialog closes with true.</summary>
    public ReleaseAsset? WindowsInstaller { get; private set; }
    public string? VersionTag => _release?.Tag;

    public ServerDialog(CatalogEntry app)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);
        _server = app.Server!;
        _repo = string.IsNullOrWhiteSpace(_server.Repo) ? app.Repo : _server.Repo!;
        Title = "Get " + _server.Name;
        Heading.Text = _server.Name;
        DescriptionText.Text = _server.Description;
        if (string.IsNullOrWhiteSpace(_server.DockerImage)) DockerOption.Visibility = Visibility.Collapsed;
        else
        {
            string volume = _server.Name.ToLowerInvariant().Replace(" server", "").Replace(" ", "") + "-data";
            string name = volume.Replace("-data", "");
            DockerCommand.Text = $"docker run -d --name {name} --restart unless-stopped -p {_server.Port}:{_server.Port} -v {volume}:/data {_server.DockerImage}:latest";
        }
        if (!string.IsNullOrWhiteSpace(_server.GuideUrl)) GuideLink.Visibility = Visibility.Visible;
        _baseWindowsText = WindowsText.Text;
        ShowInstallState();
        Loaded += async (_, _) => { await LoadAsync(); _loaded.TrySetResult(); };
    }

    private async Task LoadAsync()
    {
        var r = await GitHubService.GetLatestReleaseWithPrefixAsync(_repo, _server.TagPrefix);
        if (r.Status == GitHubStatus.Ok && r.Value != null)
        {
            _release = r.Value;
            string version = _release.Tag.StartsWith(_server.TagPrefix, StringComparison.OrdinalIgnoreCase) ? _release.Tag[_server.TagPrefix.Length..] : _release.Tag;
            string date = _release.PublishedAt is { } d ? $" · released {d.ToLocalTime():MMMM d, yyyy}" : "";
            VersionText.Text = $"Version {version}{date}";
            WindowsInstaller = GitHubService.MatchAsset(_release.Assets, _server.WindowsAsset);
            ShowInstallState();
        }
        else
        {
            VersionText.Text = r.Status == GitHubStatus.NoRelease ? "Not released yet" : "Couldn't check the latest version";
            ShowError(r.Status switch
            {
                GitHubStatus.NoRelease => "There's no release of the server on GitHub yet.",
                GitHubStatus.RateLimited => GitHubService.RateLimitMessage,
                GitHubStatus.NetworkError => "Couldn't reach GitHub. Check your connection and try again.",
                _ => r.Message ?? "Couldn't read the latest release.",
            });
        }
    }

    private string LatestVersion =>
        _release == null ? "" : _release.Tag.StartsWith(_server.TagPrefix, StringComparison.OrdinalIgnoreCase) ? _release.Tag[_server.TagPrefix.Length..] : _release.Tag;

    /// <summary>Download, Update or Reinstall, and Uninstall when it's installed on this PC.</summary>
    private void ShowInstallState()
    {
        _installed = ServerInstall.Find(_server);
        string size = WindowsInstaller is { Size: > 0 } a ? $" ({a.Size / 1048576.0:0.#} MB)" : "";
        if (_installed == null)
        {
            InstalledBadge.Visibility = Visibility.Collapsed;
            UninstallButton.Visibility = Visibility.Collapsed;
            UninstallPanel.Visibility = Visibility.Collapsed;
            WindowsButton.Content = "Download";
            WindowsText.Text = _baseWindowsText + size;
        }
        else
        {
            bool newer = LatestVersion.Length > 0 && GitHubService.CompareVersions(LatestVersion, _installed.Version) > 0;
            InstalledBadge.Visibility = Visibility.Visible;
            InstalledBadgeText.Text = _installed.Version.Length > 0 ? $"Installed · {_installed.Version}" : "Installed";
            UninstallButton.Visibility = Visibility.Visible;
            WindowsButton.Content = newer ? "Update" : "Reinstall";
            WindowsText.Text = newer
                ? $"Running on this PC as a Windows service. Version {LatestVersion} is available{size}; updating keeps your accounts and data."
                : "Running on this PC as a Windows service, with its web panel at http://localhost:" + _server.Port + ".";
        }
        WindowsButton.IsEnabled = WindowsInstaller != null;
    }

    private void Uninstall_Click(object sender, RoutedEventArgs e) => OpenUninstall();

    /// <summary>Shows the uninstall confirmation (also used by CI screenshots).</summary>
    internal void OpenUninstall()
    {
        UninstallTitle.Text = $"Uninstall {_server.Name} from this PC?";
        DeleteDataBox.IsChecked = false;
        UninstallPanel.Visibility = Visibility.Visible;
        WindowsStatus.Visibility = Visibility.Collapsed;
        UninstallButton.IsEnabled = false;
    }

    private void UninstallCancel_Click(object sender, RoutedEventArgs e)
    {
        UninstallPanel.Visibility = Visibility.Collapsed;
        UninstallButton.IsEnabled = true;
    }

    private async void UninstallConfirm_Click(object sender, RoutedEventArgs e) => await ConfirmUninstallAsync();

    internal bool IsInstalled => _installed != null;
    internal Task WhenLoaded => _loaded.Task;
    private readonly TaskCompletionSource _loaded = new();

    internal async Task ConfirmUninstallAsync(bool? deleteData = null)
    {
        if (_installed == null) return;
        if (deleteData != null) DeleteDataBox.IsChecked = deleteData;
        bool delete = DeleteDataBox.IsChecked == true;
        UninstallConfirmButton.IsEnabled = UninstallCancelButton.IsEnabled = WindowsButton.IsEnabled = DeleteDataBox.IsEnabled = false;
        UninstallConfirmButton.Content = "Uninstalling…";
        var problem = await ServerInstall.UninstallAsync(_server, _installed, delete);
        UninstallConfirmButton.Content = "Uninstall";
        UninstallConfirmButton.IsEnabled = UninstallCancelButton.IsEnabled = DeleteDataBox.IsEnabled = true;
        UninstallButton.IsEnabled = true;
        if (problem == null)
        {
            UninstallPanel.Visibility = Visibility.Collapsed;
            var data = ServerInstall.DataFolder(_server);
            WindowsStatus.Text = delete || data == null
                ? $"{_server.Name} was uninstalled{(delete ? ", along with its data" : "")}."
                : $"{_server.Name} was uninstalled. Its data is still in {data}, ready if you install it again.";
            WindowsStatus.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary");
        }
        else
        {
            WindowsStatus.Text = problem;
            WindowsStatus.Foreground = (System.Windows.Media.Brush)FindResource("DangerText");
        }
        WindowsStatus.Visibility = Visibility.Visible;
        ShowInstallState();
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Windows_Click(object sender, RoutedEventArgs e)
    {
        if (WindowsInstaller == null) return;
        DialogResult = true;
    }

    private async void CopyDocker_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DockerCommand.Text);
            CopyButton.Content = "Copied";
            await Task.Delay(1800);
            CopyButton.Content = "Copy";
        }
        catch (Exception ex) { ShowError("Couldn't copy: " + ex.Message); }
    }

    private void Other_Click(object sender, RoutedEventArgs e)
    {
        var url = _release?.HtmlUrl ?? $"https://github.com/{_repo}/releases";
        var problem = ShellHelper.OpenUrl(url);
        if (problem != null) ShowError(problem);
    }

    private void Guide_Click(object sender, RoutedEventArgs e)
    {
        var problem = ShellHelper.OpenUrl(_server.GuideUrl!);
        if (problem != null) ShowError(problem);
    }
}
