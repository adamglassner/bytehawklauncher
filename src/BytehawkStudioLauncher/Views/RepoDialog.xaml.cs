using System.Windows;
using System.Windows.Controls;
using Bytehawk.Launcher.Models;
using Bytehawk.Launcher.Services;

namespace Bytehawk.Launcher.Views;

/// <summary>Add (or edit) a Bytehawk app backed by a GitHub repo.</summary>
public partial class RepoDialog : Window
{
    private string? _lookedUpRepo;
    private bool _lookupFailed;

    public CatalogEntry Result { get; private set; } = new();
    public CachedRelease? FetchedRelease { get; private set; }

    private readonly CatalogEntry? _editing;

    public RepoDialog(CatalogEntry? editing = null)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);
        _editing = editing;
        if (editing != null)
        {
            Title = Heading.Text = "Edit Bytehawk app";
            OkButton.Content = "Save";
            RepoBox.Text = editing.Repo;
            NameBox.Text = editing.Name;
            DescriptionBox.Text = editing.Description;
            PatternBox.Text = editing.AssetPattern;
            _lookedUpRepo = editing.Repo;
        }
        Loaded += (_, _) => RepoBox.Focus();
    }

    private void Chip_Click(object sender, RoutedEventArgs e)
    {
        PatternBox.Text = (string)((Button)sender).Content;
    }

    private void Repo_Changed(object sender, TextChangedEventArgs e) => _lookupFailed = false;

    private void Pattern_Changed(object sender, TextChangedEventArgs e)
    {
        if (FetchedRelease != null) ShowPreview(FetchedRelease);
    }

    private async void Lookup_Click(object sender, RoutedEventArgs e) => await LookupAsync();

    private async Task<bool> LookupAsync()
    {
        HideError();
        if (!GitHubService.TryParseRepo(RepoBox.Text, out var repo))
        {
            ShowError("That doesn't look like a GitHub repo. Use a URL like https://github.com/owner/repo or owner/repo.");
            return false;
        }
        RepoBox.Text = repo;
        LookupButton.IsEnabled = OkButton.IsEnabled = false;
        PreviewText.Text = "Contacting GitHub…";
        try
        {
            var info = await GitHubService.GetRepoAsync(repo);
            if (info.Status != GitHubStatus.Ok || info.Value == null)
            {
                _lookupFailed = true;
                _lookedUpRepo = null;
                if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = repo.Split('/')[1];
                ShowError(info.Message ?? "GitHub lookup failed.");
                PreviewText.Text = info.Status == GitHubStatus.NotFound
                    ? "Check the repo name. Private repos can't be used without a login."
                    : "You can still add the app with the name you type; versions will load when GitHub is reachable.";
                return false;
            }
            _lookupFailed = false;
            _lookedUpRepo = repo;
            NameBox.Text = info.Value.Name;
            DescriptionBox.Text = info.Value.Description;

            var rel = await GitHubService.GetLatestReleaseAsync(repo, null);
            FetchedRelease = rel.Value;
            if (rel.Value != null) ShowPreview(rel.Value);
            else PreviewText.Text = rel.Message ?? "Couldn't read the latest release.";
            return true;
        }
        finally
        {
            LookupButton.IsEnabled = OkButton.IsEnabled = true;
        }
    }

    private void ShowPreview(CachedRelease rel)
    {
        if (rel.NoRelease)
        {
            PreviewText.Text = "This repo has no published release yet. You can add it now; Download will work once a release exists.";
            return;
        }
        var match = GitHubService.MatchAsset(rel.Assets, PatternBox.Text);
        var all = rel.Assets.Count == 0 ? "(none)" : string.Join(", ", rel.Assets.Take(8).Select(a => a.Name)) + (rel.Assets.Count > 8 ? ", …" : "");
        PreviewText.Text =
            $"Latest release: {GitHubService.DisplayVersion(rel.Tag)}\n" +
            (match != null
                ? $"Will download: {match.Name} ({Downloader.FormatBytes(match.Size)})"
                : $"No asset matches \"{PatternBox.Text}\". Assets: {all}");
    }

    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        HideError();
        if (!GitHubService.TryParseRepo(RepoBox.Text, out var repo))
        {
            ShowError("Enter a GitHub repo URL or owner/repo.");
            return;
        }
        if (!string.Equals(repo, _lookedUpRepo, StringComparison.OrdinalIgnoreCase) && !_lookupFailed)
        {
            // Not looked up yet: fetch name/description first.
            if (!await LookupAsync() && string.IsNullOrWhiteSpace(NameBox.Text)) return;
            if (_lookupFailed) return; // let the user read the message, then press Add again
        }
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            ShowError("Enter a display name.");
            return;
        }
        Result = new CatalogEntry
        {
            Id = _editing?.Id,
            Repo = repo,
            Name = NameBox.Text.Trim(),
            Description = DescriptionBox.Text.Trim(),
            AssetPattern = string.IsNullOrWhiteSpace(PatternBox.Text) ? "*.exe" : PatternBox.Text.Trim(),
            Sample = false,
        };
        DialogResult = true;
    }

    private void ShowError(string msg) { ErrorText.Text = msg; ErrorText.Visibility = Visibility.Visible; }
    private void HideError() => ErrorText.Visibility = Visibility.Collapsed;
}
