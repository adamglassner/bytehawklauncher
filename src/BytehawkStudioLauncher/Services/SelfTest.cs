using System.IO;
using System.IO.Compression;
using System.Text;
using Bytehawk.Launcher.Models;
using Bytehawk.Launcher.Views;

namespace Bytehawk.Launcher.Services;

/// <summary>
/// Headless checks run by CI (<c>BytehawkStudioLauncher.exe --self-test result.txt</c>).
/// Exercises the real code paths — GitHub API, asset matching, download with progress, zip extraction,
/// icon extraction, URL validation, JSON storage — against a real public GitHub repo. Never touches the
/// user's data folder. Exit code = number of failed checks.
/// </summary>
public static class SelfTest
{
    private const string SampleRepo = "notepad-plus-plus/notepad-plus-plus";

    public static async Task<int> RunAsync(string outputFile)
    {
        var sb = new StringBuilder();
        int failed = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (!ok) failed++;
            sb.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  — " + detail : "")}");
        }

        sb.AppendLine($"Bytehawk Studio Launcher {App.VersionText} self-test {DateTime.Now:u}");
        var temp = Path.Combine(Path.GetTempPath(), "bytehawk-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        try
        {
            // ---- pure logic ----
            Check("Version is set", !string.IsNullOrWhiteSpace(App.VersionText), App.VersionText);
            Check("Parse repo URL", GitHubService.TryParseRepo("https://github.com/owner/repo.git", out var r1) && r1 == "owner/repo", r1);
            Check("Parse repo /releases URL", GitHubService.TryParseRepo("github.com/owner/repo/releases", out var r2) && r2 == "owner/repo", r2);
            Check("Parse owner/repo", GitHubService.TryParseRepo("owner/repo", out var r3) && r3 == "owner/repo", r3);
            Check("Reject non-repo text", !GitHubService.TryParseRepo("hello world", out _));
            Check("Version compare v1.3.0 > 1.2.0", GitHubService.CompareVersions("v1.3.0", "1.2.0") > 0);
            Check("Version compare 1.10 > 1.9", GitHubService.CompareVersions("1.10", "1.9") > 0);
            Check("Version compare v1.0.0 == 1.0.0", GitHubService.CompareVersions("v1.0.0", "1.0.0") == 0);
            Check("URL without scheme gets https://", WebAppDialog.NormalizeUrl("example.com", out _) == "https://example.com/");
            Check("URL with path kept", WebAppDialog.NormalizeUrl("http://example.com/app?x=1", out _) == "http://example.com/app?x=1");
            Check("URL localhost:3000 accepted", WebAppDialog.NormalizeUrl("localhost:3000", out _) == "https://localhost:3000/");
            Check("Invalid URL rejected", WebAppDialog.NormalizeUrl("not a url", out var e1) == null && e1.Length > 0, e1);
            Check("Host without dot rejected", WebAppDialog.NormalizeUrl("foo", out _) == null);
            var assets = new List<ReleaseAsset>
            {
                new() { Name = "app-1.0-arm64.exe" }, new() { Name = "app-1.0-x64.exe" }, new() { Name = "app-1.0.zip" },
            };
            Check("Asset pattern with exclude", GitHubService.MatchAsset(assets, "*.exe;!*arm64*")?.Name == "app-1.0-x64.exe");
            Check("Asset pattern zip", GitHubService.MatchAsset(assets, "*.zip")?.Name == "app-1.0.zip");
            Check("Asset pattern no match", GitHubService.MatchAsset(assets, "*.msi") == null);

            // ---- JSON storage round trip ----
            var jsonPath = Path.Combine(temp, "library.json");
            JsonStore.Save(jsonPath, new List<LibraryEntry> { new() { Name = "Test", Path = @"C:\x.exe", RunAsAdmin = true } });
            var back = JsonStore.Load(jsonPath, () => new List<LibraryEntry>());
            Check("JSON save/load", back.Count == 1 && back[0].Name == "Test" && back[0].RunAsAdmin);

            // ---- GitHub API (real repo) ----
            var repo = await GitHubService.GetRepoAsync(SampleRepo);
            Check("GitHub repo lookup", repo.Status == GitHubStatus.Ok && repo.Value != null, repo.Value?.Name ?? repo.Message ?? "");

            var rel = await GitHubService.GetLatestReleaseAsync(SampleRepo, null);
            Check("GitHub latest release", rel.Status == GitHubStatus.Ok && rel.Value != null && rel.Value.Tag.Length > 0,
                  rel.Value?.Tag ?? rel.Message ?? "");
            if (rel.Value is { } release)
            {
                var again = await GitHubService.GetLatestReleaseAsync(SampleRepo, release);
                Check("Conditional request (ETag)", again.Status is GitHubStatus.NotModified or GitHubStatus.Ok, again.Status.ToString());

                var exe = GitHubService.MatchAsset(release.Assets, "*.Installer.x64.exe");
                var zip = GitHubService.MatchAsset(release.Assets, "*.portable.x64.zip");
                Check("Match installer .exe asset", exe != null, exe?.Name ?? "");
                Check("Match portable .zip asset", zip != null, zip?.Name ?? "");

                if (exe != null)
                {
                    var dest = Path.Combine(temp, exe.Name);
                    int reports = 0;
                    DownloadProgress last = default;
                    await Downloader.DownloadAsync(exe.DownloadUrl, dest,
                        new SyncProgress<DownloadProgress>(p => { reports++; last = p; }), CancellationToken.None);
                    var len = File.Exists(dest) ? new FileInfo(dest).Length : -1;
                    Check("Download .exe from GitHub", len == exe.Size && len > 0, $"{len} of {exe.Size} bytes, {reports} progress reports");
                    Check("Progress reported total", last.TotalBytes == exe.Size && last.BytesReceived == exe.Size);
                    Check("Icon extracted from downloaded .exe", ShellHelper.GetFileIcon(dest) != null);
                }
                if (zip != null)
                {
                    var dest = Path.Combine(temp, zip.Name);
                    await Downloader.DownloadAsync(zip.DownloadUrl, dest, new SyncProgress<DownloadProgress>(_ => { }), CancellationToken.None);
                    var folder = Path.Combine(temp, "extracted");
                    ZipFile.ExtractToDirectory(dest, folder, overwriteFiles: true);
                    var exes = Directory.GetFiles(folder, "*.exe", SearchOption.AllDirectories);
                    Check("Download + extract .zip", exes.Length > 0, $"{exes.Length} exe(s), e.g. {Path.GetFileName(exes.FirstOrDefault() ?? "")}");
                }

                // cancellation leaves no partial file
                if (zip != null)
                {
                    using var cts = new CancellationTokenSource();
                    var dest = Path.Combine(temp, "cancelled.zip");
                    var cancelled = false;
                    try
                    {
                        await Downloader.DownloadAsync(zip.DownloadUrl, dest,
                            new SyncProgress<DownloadProgress>(p => { if (p.BytesReceived > 0) cts.Cancel(); }), cts.Token);
                    }
                    catch (OperationCanceledException) { cancelled = true; }
                    Check("Cancel download cleans up", cancelled && !File.Exists(dest) && !File.Exists(dest + ".part"));
                }
            }

            // ---- online catalog in the launcher repo ----
            try
            {
                var json = await GitHubService.GetTextAsync(AppSettings.DefaultRemoteCatalogUrl);
                var cat = System.Text.Json.JsonSerializer.Deserialize<CatalogFile>(json, JsonStore.Options);
                var n = cat?.Apps?.Count ?? 0;
                Check("Online catalog.json parses", n > 0, $"{n} apps, {cat?.Apps?.Count(a => a.ComingSoon)} coming soon");
            }
            catch (Exception ex) { Check("Online catalog.json parses", false, ex.Message); }

            // ---- launcher self-update source ----
            var self = await GitHubService.GetLatestReleaseAsync(LauncherUpdater.Repo, null);
            Check("Launcher update repo reachable", self.Status is GitHubStatus.Ok or GitHubStatus.NoRelease,
                  self.Value?.NoRelease == true ? "no release yet" : $"latest {self.Value?.Tag}, newer than {App.VersionText}: {LauncherUpdater.IsNewer(self.Value)}");
            if (self.Value is { NoRelease: false } sr)
                Check("Launcher release has a Setup .exe", LauncherUpdater.SetupAsset(sr) != null, LauncherUpdater.SetupAsset(sr)?.Name ?? "");
        }
        catch (Exception ex)
        {
            failed++;
            sb.AppendLine($"FAIL  Unexpected exception — {ex}");
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* ignore */ }
        }

        sb.AppendLine(failed == 0 ? "ALL PASSED" : $"{failed} FAILED");
        try { File.WriteAllText(outputFile, sb.ToString()); } catch { /* ignore */ }
        return failed;
    }

    /// <summary>IProgress that runs the callback inline (no SynchronizationContext hop).</summary>
    private sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
