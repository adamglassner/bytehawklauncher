using System.IO;
using System.Text;
using Bytehawk.Launcher.Models;
using Bytehawk.Launcher.Views;

namespace Bytehawk.Launcher.Services;

/// <summary>
/// Headless checks run by CI (<c>BytehawkStudioLauncher.exe --self-test result.txt</c>).
/// Exercises the real code paths against the real Guardrail EHS release on GitHub: latest-tag lookup, API,
/// asset matching, download with progress and checksum, silent Inno Setup install, zip install, finding the
/// program, uninstall — plus URL validation and JSON storage. Never touches the user's data folder.
/// Exit code = number of failed checks.
/// </summary>
public static class SelfTest
{
    private const string AppRepo = "adamglassner/guardrailehs";

    public static async Task<int> RunAsync(string outputFile)
    {
        var sb = new StringBuilder();
        int failed = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (!ok) failed++;
            sb.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  — " + detail : "")}");
        }
        // Reported but never fails the run (e.g. GitHub's raw-file CDN can lag a few minutes behind a push).
        void Info(string name, bool ok, string detail = "") =>
            sb.AppendLine($"{(ok ? "PASS" : "INFO")}  {name}{(detail.Length > 0 ? "  — " + detail : "")}");

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

            // ---- GitHub: the first Bytehawk app, Guardrail EHS ----
            var (tag, answered) = await GitHubService.GetLatestTagAsync(AppRepo);
            Check("Latest tag from github.com (no API)", answered && !string.IsNullOrEmpty(tag), tag ?? "no answer");

            var repo = await GitHubService.GetRepoAsync(AppRepo);
            Check("GitHub repo lookup", repo.Status == GitHubStatus.Ok && repo.Value != null, repo.Value?.Name ?? repo.Message ?? "");

            var rel = await GitHubService.GetLatestReleaseAsync(AppRepo, null);
            Check("GitHub latest release", rel.Status == GitHubStatus.Ok && rel.Value != null && rel.Value.Tag.Length > 0,
                  rel.Value?.Tag ?? rel.Message ?? "");
            if (rel.Value is { } release)
            {
                Check("Tag check agrees with the API", tag == release.Tag, $"{tag} vs {release.Tag}");
                var again = await GitHubService.GetLatestReleaseAsync(AppRepo, release);
                Check("Conditional request (ETag)", again.Status is GitHubStatus.NotModified or GitHubStatus.Ok, again.Status.ToString());

                const string pattern = "GuardrailEHS-*-Setup.exe;*-win-x64.zip";
                var setup = GitHubService.MatchAsset(release.Assets, pattern);
                var zip = GitHubService.MatchAsset(release.Assets, "*-win-x64.zip");
                Check("Catalog pattern picks the Setup .exe", setup?.Name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase) == true, setup?.Name ?? "");
                Check("Portable .zip asset present", zip != null, zip?.Name ?? "");

                if (setup != null)
                {
                    var dest = Path.Combine(temp, setup.Name);
                    int reports = 0;
                    DownloadProgress last = default;
                    await Downloader.DownloadAsync(setup.DownloadUrl, dest,
                        new SyncProgress<DownloadProgress>(p => { reports++; last = p; }), CancellationToken.None);
                    var len = File.Exists(dest) ? new FileInfo(dest).Length : -1;
                    Check("Download Setup .exe from GitHub", len == setup.Size && len > 0, $"{len} of {setup.Size} bytes, {reports} progress reports");
                    Check("Progress reported total", last.TotalBytes == setup.Size && last.BytesReceived == setup.Size);
                    var problem = await AppInstaller.VerifyDownloadAsync(dest, setup);
                    Check("Download size + SHA-256 verified", problem == null, problem ?? (setup.Digest ?? "size only (no digest published)"));

                    // A corrupted copy must be rejected.
                    var bad = Path.Combine(temp, "corrupt.exe");
                    File.Copy(dest, bad);
                    await using (var fs = File.OpenWrite(bad)) { fs.Position = fs.Length / 2; fs.WriteByte(0x42); fs.WriteByte(0x24); }
                    var badProblem = await AppInstaller.VerifyDownloadAsync(bad, setup);
                    Check("Corrupted download rejected", setup.Digest == null || badProblem != null, badProblem ?? "no digest to compare");

                    var kind = AppInstaller.DetectSetupKind(dest);
                    Check("Detects Inno Setup", kind == "inno", kind ?? "unknown");
                    if (kind != null)
                    {
                        var appBase = Path.Combine(temp, "apps", "guardrail-ehs");
                        var installed = await AppInstaller.InstallSetupAsync(dest, kind, appBase, release.Tag, "Guardrail.exe");
                        Check("Silent install", installed.LaunchPath != null && File.Exists(installed.LaunchPath),
                              installed.LaunchPath ?? "program not found");
                        Check("Finds Guardrail.exe", Path.GetFileName(installed.LaunchPath ?? "") == "Guardrail.exe");
                        Check("Not reported as running", !AppInstaller.IsRunning(installed));
                        Check("Icon read from installed program", installed.LaunchPath != null && ShellHelper.GetFileIcon(installed.LaunchPath) != null);

                        // Update = install the release again over the existing copy.
                        var updated = await AppInstaller.InstallSetupAsync(dest, kind, appBase, release.Tag, "Guardrail.exe");
                        Check("Reinstall over existing copy (update path)", updated.LaunchPath != null && File.Exists(updated.LaunchPath));

                        await AppInstaller.UninstallAsync(updated, appBase);
                        Check("Uninstall removes the app folder", !Directory.Exists(appBase));
                    }
                }
                if (zip != null)
                {
                    var dest = Path.Combine(temp, zip.Name);
                    await Downloader.DownloadAsync(zip.DownloadUrl, dest, new SyncProgress<DownloadProgress>(_ => { }), CancellationToken.None);
                    Check("Zip size + SHA-256 verified", await AppInstaller.VerifyDownloadAsync(dest, zip) == null);
                    var zipBase = Path.Combine(temp, "apps", "guardrail-zip");
                    var z = AppInstaller.InstallZip(dest, zipBase, release.Tag, "Guardrail.exe");
                    Check("Zip install finds Guardrail.exe", z.LaunchPath != null && File.Exists(z.LaunchPath), z.LaunchPath ?? "");
                    await AppInstaller.UninstallAsync(z, zipBase);
                    Check("Zip uninstall", !Directory.Exists(zipBase));

                    // cancellation leaves no partial file
                    using var cts = new CancellationTokenSource();
                    var cancelledDest = Path.Combine(temp, "cancelled.zip");
                    var cancelled = false;
                    try
                    {
                        await Downloader.DownloadAsync(zip.DownloadUrl, cancelledDest,
                            new SyncProgress<DownloadProgress>(p => { if (p.BytesReceived > 0) cts.Cancel(); }), cts.Token);
                    }
                    catch (OperationCanceledException) { cancelled = true; }
                    Check("Cancel download cleans up", cancelled && !File.Exists(cancelledDest) && !File.Exists(cancelledDest + ".part"));
                }
            }

            // ---- web app icons ----
            var links = WebIconService.ParseLinkIcons(
                "<link rel=\"icon\" href=\"/a.svg\" type=\"image/svg+xml\"><link rel='icon' sizes='32x32' href='/f32.png'>" +
                "<link rel=\"apple-touch-icon\" href=\"/touch.png\">", new Uri("https://example.com/app/")).ToList();
            Check("Icon links parsed (SVG skipped, touch icon preferred)",
                  links.Count == 2 && links.OrderByDescending(l => l.Item2).First().Item1.AbsoluteUri == "https://example.com/touch.png",
                  string.Join(", ", links.Select(l => $"{l.Item1.AbsolutePath}={l.Item2}")));
            foreach (var site in new[] { "https://github.com", "https://www.wikipedia.org" })
            {
                var iconPath = Path.Combine(temp, "icon-" + new Uri(site).Host + ".png");
                var got = await WebIconService.FetchAsync(site, iconPath);
                Check($"Web app icon from {site}", got && File.Exists(iconPath) && new FileInfo(iconPath).Length > 0,
                      got ? $"{new FileInfo(iconPath).Length} bytes" : "no icon");
            }
            Check("No icon for unreachable site", !await WebIconService.FetchAsync("https://bytehawk-launcher-test.invalid", Path.Combine(temp, "none.png")));

            // ---- My Software version auto-fill ----
            var sysExe = Path.Combine(Environment.SystemDirectory, "notepad.exe");
            if (!File.Exists(sysExe)) sysExe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var ver = SoftwareDialog.ReadFileVersion(sysExe);
            Check("Version read from an .exe", ver.Length > 0 && char.IsDigit(ver[0]), $"{Path.GetFileName(sysExe)} = {ver}");

            // ---- online catalog in the launcher repo ----
            try
            {
                var json = await GitHubService.GetTextAsync(AppSettings.DefaultRemoteCatalogUrl);
                var cat = System.Text.Json.JsonSerializer.Deserialize<CatalogFile>(json, JsonStore.Options);
                var n = cat?.Apps?.Count ?? 0;
                Check("Online catalog.json parses", n > 0, $"{n} apps, {cat?.Apps?.Count(a => a.ComingSoon)} coming soon");
                Info("Guardrail EHS is the first app", cat?.Apps?.FirstOrDefault()?.Repo == AppRepo, cat?.Apps?.FirstOrDefault()?.Name ?? "");
                Info("No sample entries left", cat?.Apps?.Any(a => a.Sample || (a.Id ?? "").StartsWith("sample-")) == false);
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
