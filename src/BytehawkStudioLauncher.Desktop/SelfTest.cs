using System.Text;
using Bytehawk.Launcher.Models;
using Bytehawk.Launcher.Services;
using Bytehawk.Launcher.Views;

namespace Bytehawk.Launcher.Desktop;

/// <summary>
/// Headless checks run by CI on macOS and Linux (BytehawkStudioLauncher --self-test result.txt).
/// Uses the real catalog and GitHub releases: macOS installs real apps into ~/Applications and removes them again.
/// Exit code = number of failed checks.
/// </summary>
public static class SelfTest
{
    public static async Task<int> RunAsync(string outputFile)
    {
        var sb = new StringBuilder();
        var failed = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (!ok) failed++;
            sb.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  — " + detail : "")}");
        }
        void Warn(string name, bool ok, string detail = "") =>
            sb.AppendLine($"{(ok ? "PASS" : "WARN")}  {name}{(detail.Length > 0 ? "  — " + detail : "")}");
        void Info(string text) => sb.AppendLine("INFO  " + text);

        sb.AppendLine($"Bytehawk Studio Launcher {App.VersionText} self-test on {Platform.Name} {Platform.ArchLabel}, {DateTime.Now:u}");
        var temp = Path.Combine(Path.GetTempPath(), "bytehawk-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        try
        {
            Check("Running on macOS or Linux", Platform.IsMac || Platform.IsLinux, Platform.Name);
            Check("{arch} pattern", Platform.Pattern("App-*-macOS-{arch}.zip") == $"App-*-macOS-{Platform.Arch}.zip");

            // ---- catalog: every app listed, ones without a build for this OS are "Coming soon" ----
            List<CatalogEntry> catalog;
            using (var s = typeof(SelfTest).Assembly.GetManifestResourceStream("catalog.json"))
                catalog = System.Text.Json.JsonSerializer.Deserialize<CatalogFile>(new StreamReader(s!).ReadToEnd(), JsonStore.Options)!.Apps;
            try
            {
                var online = System.Text.Json.JsonSerializer.Deserialize<CatalogFile>(await GitHubService.GetTextAsync(AppSettings.DefaultRemoteCatalogUrl), JsonStore.Options);
                Warn("Online catalog parses", online?.Apps.Count > 0, $"{online?.Apps.Count} apps");
            }
            catch (Exception ex) { Warn("Online catalog parses", false, ex.Message); }
            var vms = catalog.Select(e => new AppVM(e)).ToList();
            var available = vms.Where(v => !v.IsComingSoon).Select(v => v.Name).ToList();
            var soon = vms.Where(v => v.IsComingSoon).Select(v => v.Name).ToList();
            Check("Built-in catalog lists every app", vms.Count == catalog.Count && vms.Count > 5, $"{vms.Count} apps");
            Info($"Available on {Platform.Name}: {string.Join(", ", available)}");
            Info($"Coming soon to {Platform.Name}: {string.Join(", ", soon)}");
            Check("Draftly shows as Coming soon (no build for this OS)", vms.Any(v => v.Key == "draftly" && v.IsComingSoon && v.ComingSoonText == $"Coming soon to {Platform.Name}"));
            Check("Larksong is available", vms.Any(v => v.Key == "larksong" && !v.IsComingSoon));
            Check("Unreleased apps (WriteHawk) show as Coming soon", vms.Any(v => v.Key == "writehawk" && v.IsComingSoon && v.ComingSoonText == "Coming soon"));
            Check("RingHawk (Windows only so far) shows as Coming soon to this OS", vms.Any(v => v.Key == "waylo" && v.IsComingSoon && v.ComingSoonText == $"Coming soon to {Platform.Name}"));
            Check("NoteHawk (Windows only so far) shows as Coming soon to this OS", vms.Any(v => v.Key == "notehawk" && v.IsComingSoon && v.ComingSoonText == $"Coming soon to {Platform.Name}"));
            Check("PlanHawk (Windows only so far) shows as Coming soon to this OS", vms.Any(v => v.Key == "planhawk" && v.IsComingSoon && v.ComingSoonText == $"Coming soon to {Platform.Name}"));
            Check("PaperHawk (Windows only so far) shows as Coming soon to this OS", vms.Any(v => v.Key == "paperhawk" && v.IsComingSoon && v.ComingSoonText == $"Coming soon to {Platform.Name}"));
            Check("DataHawk (Windows only so far) shows as Coming soon to this OS", vms.Any(v => v.Key == "datahawk" && v.IsComingSoon && v.ComingSoonText == $"Coming soon to {Platform.Name}"));
            Check("CraftHawk (Windows only so far) shows as Coming soon to this OS", vms.Any(v => v.Key == "crafthawk" && v.IsComingSoon && v.ComingSoonText == $"Coming soon to {Platform.Name}"));
            if (Platform.IsMac) Check("ShoreDrop is available on macOS", vms.Any(v => v.Key == "shoredrop" && !v.IsComingSoon));
            if (Platform.IsLinux) Check("ShoreDrop is Coming soon on Linux", vms.Any(v => v.Key == "shoredrop" && v.IsComingSoon));
            var servers = catalog.Where(e => e.Server != null).Select(e => new ServerVM(e)).ToList();
            Check("Servers available here (native or Docker)", servers.Count > 0 && servers.All(s => !s.IsComingSoon),
                  string.Join(", ", servers.Select(s => $"{s.Name}: {(s.HasNative ? "native" : "")}{(s.HasDocker ? " docker" : "")}")));
            Check("Docker command", servers.All(s => !s.HasDocker || s.DockerCommand.Contains("-v ") && s.DockerCommand.Contains(":/data")),
                  servers.FirstOrDefault()?.DockerCommand ?? "");

            // ---- releases + assets for every app with a build here ----
            foreach (var e in catalog.Where(e => Platform.BuildFor(e) is { AssetPattern.Length: > 0 } && e.HasRepo && !e.ComingSoon))
            {
                var b = Platform.BuildFor(e)!;
                var r = string.IsNullOrWhiteSpace(b.TagPrefix)
                    ? await GitHubService.GetLatestReleaseAsync(e.Repo, null)
                    : await GitHubService.GetLatestReleaseWithPrefixAsync(e.Repo, b.TagPrefix!);
                var asset = r.Value == null ? null : GitHubService.MatchAsset(r.Value.Assets, Platform.Pattern(b.AssetPattern));
                // Some apps only build for one processor (e.g. Apple Silicon); that's a warning, not a failure.
                Warn($"{e.Name}: {Platform.Name} download for {Platform.ArchLabel}", asset != null, asset != null ? $"{r.Value!.Tag} → {asset.Name}" : r.Message ?? "no matching asset");
                if (asset == null) continue;

                if (Platform.IsMac)
                {
                    // Real install into ~/Applications, then uninstall.
                    var file = Path.Combine(temp, asset.Name);
                    await Downloader.DownloadAsync(asset.DownloadUrl, file, new Progress<DownloadProgress>(_ => { }), CancellationToken.None);
                    Check($"{e.Name}: download", File.Exists(file) && new FileInfo(file).Length == asset.Size, $"{new FileInfo(file).Length} bytes");
                    try
                    {
                        var rec = await AppManager.InstallAsync(e, b, file, r.Value!.Tag, asset.Name, new Progress<string>(_ => { }));
                        var plist = Path.Combine(rec.LaunchPath ?? "", "Contents", "Info.plist");
                        Check($"{e.Name}: installed to Applications", rec.Kind == "mac-app" && File.Exists(plist), rec.LaunchPath ?? "");
                        Check($"{e.Name}: app version readable", IconCache.MacPlistValue(rec.LaunchPath!, "CFBundleShortVersionString") is { Length: > 0 },
                              IconCache.MacPlistValue(rec.LaunchPath!, "CFBundleShortVersionString") ?? "");
                        var sig = Platform.Run("/usr/bin/codesign", new[] { "--verify", "--deep", rec.LaunchPath! }, 60_000);
                        Warn($"{e.Name}: code signature intact after install", sig.Ok, sig.Message);
                        await AppManager.UninstallAsync(e, rec);
                        Check($"{e.Name}: uninstalled", !Directory.Exists(rec.LaunchPath!));
                    }
                    catch (Exception ex) { Check($"{e.Name}: install", false, ex.Message); }
                }
                else if (asset.Name.EndsWith(".deb"))
                {
                    var file = Path.Combine(temp, asset.Name);
                    await Downloader.DownloadAsync(asset.DownloadUrl, file, new Progress<DownloadProgress>(_ => { }), CancellationToken.None);
                    var info = Platform.Run("dpkg-deb", new[] { "--field", file, "Package" }, 30_000);
                    Check($"{e.Name}: .deb package name matches the catalog", info.Ok && info.Output.Trim() == b.Package, info.Output.Trim());
                    var list = Platform.Run("dpkg-deb", new[] { "-c", file }, 30_000);
                    Check($"{e.Name}: .deb contains {b.Exe}", list.Ok && b.Exe != null && list.Output.Contains("." + b.Exe), b.Exe ?? "");
                }
            }

            // ---- Linux: server package (download, extract, install.sh present) ----
            if (Platform.IsLinux)
                foreach (var e in catalog.Where(e => e.Server is { LinuxAsset.Length: > 0 }))
                {
                    var s = e.Server!;
                    var r = await GitHubService.GetLatestReleaseWithPrefixAsync(string.IsNullOrWhiteSpace(s.Repo) ? e.Repo : s.Repo!, s.TagPrefix);
                    var asset = r.Value == null ? null : GitHubService.MatchAsset(r.Value.Assets, Platform.Pattern(s.LinuxAsset!));
                    Check($"{s.Name}: Linux package in latest release", asset != null, asset?.Name ?? r.Message ?? "");
                    if (asset == null) continue;
                    var file = Path.Combine(temp, asset.Name);
                    await Downloader.DownloadAsync(asset.DownloadUrl, file, new Progress<DownloadProgress>(_ => { }), CancellationToken.None);
                    var dir = Path.Combine(temp, "srv-" + e.Key);
                    Directory.CreateDirectory(dir);
                    var x = Platform.Run("tar", new[] { "-xzf", file, "-C", dir }, 120_000);
                    var installSh = Directory.GetFiles(dir, "install.sh", SearchOption.AllDirectories).FirstOrDefault();
                    Check($"{s.Name}: package has install.sh and uninstall.sh", x.Ok && installSh != null &&
                          Directory.GetFiles(dir, "uninstall.sh", SearchOption.AllDirectories).Any());
                    if (installSh != null)
                        Check($"{s.Name}: install.sh installs to {s.LinuxInstallDir}", File.ReadAllText(installSh).Contains(s.LinuxInstallDir ?? "\0"));
                }

            // ---- Linux helpers ----
            if (Platform.IsLinux)
            {
                var exeDir = Path.Combine(temp, "fake-app");
                Directory.CreateDirectory(Path.Combine(exeDir, "lib"));
                var exe = Path.Combine(exeDir, "Sumthing");
                File.WriteAllText(exe, "#!/bin/sh\necho hi\n");
                File.WriteAllText(Path.Combine(exeDir, "lib", "libx.so"), "");
                Check("Find program in extracted folder", AppManager.FindLinuxProgram(exeDir, null, "Sumthing") == exe);
                Platform.MakeExecutable(exe);
                Check("chmod +x", Platform.IsExecutable(exe));
                var desktop = AppManager.DesktopFile("Test App", "A test", exe, null, "--flag");
                Check(".desktop file", desktop.Contains("Exec=" + exe + " --flag") && desktop.Contains("Type=Application"));
                var deskFile = Path.Combine(temp, "Test.desktop");
                File.WriteAllText(deskFile, desktop);
                Check(".desktop file read back", IconCache.DesktopEntryValue(deskFile, "Name") == "Test App");
            }

            // ---- shared code ----
            var apps = new List<WebAppEntry> { new() { Name = "Example", Url = "https://example.com/", Category = "Productivity" } };
            Check("Web apps export/import", WebAppTransfer.Read(WebAppTransfer.ToJson(apps)).Single().Category == "Productivity");
            Check("URL rules", WebAppDialog.NormalizeUrl("example.com", out _) == "https://example.com/" && WebAppDialog.NormalizeUrl("not a url", out _) == null);
            Check("Launch arguments split", Platform.SplitArgs("--a \"b c\" d").SequenceEqual(new[] { "--a", "b c", "d" }));

            // ---- launcher self-update source ----
            var self = await GitHubService.GetLatestReleaseWithPrefixAsync(SelfUpdater.Repo, SelfUpdater.TagPrefix);
            if (self.Status == GitHubStatus.RateLimited) Warn("Launcher update repo reachable", false, "GitHub rate limit on this build machine");
            else Check("Launcher update repo reachable", self.Status is GitHubStatus.Ok or GitHubStatus.NoRelease, self.Value?.Tag ?? self.Message ?? "");
            if (self.Value is { NoRelease: false } rel)
                Warn("Launcher release has a download for this system", SelfUpdater.Asset(rel) != null, SelfUpdater.AssetPattern);
        }
        catch (Exception ex)
        {
            failed++;
            sb.AppendLine($"FAIL  Unexpected exception — {ex}");
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { /* ignore */ }
        }
        sb.AppendLine(failed == 0 ? "ALL PASSED" : $"{failed} FAILED");
        try { File.WriteAllText(outputFile, sb.ToString()); } catch { /* ignore */ }
        Console.WriteLine(sb.ToString());
        return failed;
    }
}
