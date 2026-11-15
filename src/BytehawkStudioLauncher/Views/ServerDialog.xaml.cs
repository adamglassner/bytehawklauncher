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
        Loaded += async (_, _) => await LoadAsync();
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
            WindowsButton.IsEnabled = WindowsInstaller != null;
            if (WindowsInstaller != null && WindowsInstaller.Size > 0)
                WindowsText.Text += $" ({WindowsInstaller.Size / 1048576.0:0.#} MB)";
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
