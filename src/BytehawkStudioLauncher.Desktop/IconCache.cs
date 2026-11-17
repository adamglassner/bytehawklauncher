using System.Net.Http;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using Bytehawk.Launcher.Models;
using Bytehawk.Launcher.Services;

namespace Bytehawk.Launcher.Desktop;

/// <summary>
/// Icons for cards, cached under cache/icons so they show offline:
/// catalog app icons (PNG URLs), website icons for Web Apps, macOS .app icons (via sips) and Linux program icons.
/// </summary>
public static class IconCache
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10 }) { Timeout = TimeSpan.FromSeconds(12) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"Mozilla/5.0 ({(Platform.IsMac ? "Macintosh" : "X11; Linux")}) {GitHubService.UserAgent}");
        return c;
    }

    public static string Dir => Path.Combine(AppPaths.CacheDir, "icons");

    public static Bitmap? Load(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            return new Bitmap(fs);
        }
        catch (Exception ex) { Log.Error($"Icon {path}", ex); return null; }
    }

    private static Bitmap? Decode(byte[] bytes)
    {
        try { using var ms = new MemoryStream(bytes); return new Bitmap(ms); }
        catch { return null; }
    }

    // ---------------- catalog icons ----------------

    public static string CatalogIconPath(CatalogEntry e) => Path.Combine(Dir, "app-" + AppManager.Safe(e.Key) + ".img");

    /// <summary>Downloads a catalog app's icon once; returns the cached file or null.</summary>
    public static async Task<string?> EnsureCatalogIconAsync(CatalogEntry e)
    {
        var path = CatalogIconPath(e);
        if (File.Exists(path)) return path;
        if (string.IsNullOrWhiteSpace(e.Icon)) return null;
        try
        {
            var bytes = await Http.GetByteArrayAsync(e.Icon);
            if (Decode(bytes) == null) return null;
            Directory.CreateDirectory(Dir);
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        catch (Exception ex) { Log.Info($"Catalog icon {e.Icon}: {ex.Message}"); return null; }
    }

    // ---------------- web app icons ----------------

    public static string WebIconPath(string id) => Path.Combine(Dir, "web-" + id + ".img");

    /// <summary>Finds a site's icon the way a browser does (&lt;link rel=icon&gt;, apple-touch-icon, /favicon.ico) and caches it.</summary>
    public static async Task<bool> FetchWebIconAsync(string pageUrl, string dest)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var page)) return false;
        var candidates = new List<(Uri Url, int Score)>();
        var baseUri = page;
        try
        {
            using var resp = await Http.GetAsync(page, HttpCompletionOption.ResponseHeadersRead);
            baseUri = resp.RequestMessage?.RequestUri ?? page;
            if (resp.IsSuccessStatusCode && (resp.Content.Headers.ContentType?.MediaType ?? "").Contains("html"))
            {
                var html = await resp.Content.ReadAsStringAsync();
                if (html.Length > 512 * 1024) html = html[..(512 * 1024)];
                foreach (Match link in Regex.Matches(html, @"<link\b[^>]*>", RegexOptions.IgnoreCase))
                {
                    var tag = link.Value;
                    var rel = Attr(tag, "rel")?.ToLowerInvariant() ?? "";
                    var href = Attr(tag, "href");
                    if (string.IsNullOrWhiteSpace(href) || !rel.Contains("icon") || rel.Contains("mask-icon")) continue;
                    href = System.Net.WebUtility.HtmlDecode(href.Trim());
                    if ((Attr(tag, "type") ?? "").Contains("svg") || href.Split('?', '#')[0].EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!Uri.TryCreate(baseUri, href, out var u)) continue;
                    var size = 0;
                    foreach (Match m in Regex.Matches(Attr(tag, "sizes") ?? "", @"(\d+)x\d+")) size = Math.Max(size, int.Parse(m.Groups[1].Value));
                    if (size == 0 && rel.Contains("apple-touch-icon")) size = 180;
                    candidates.Add((u, 100 + (size == 0 ? 32 : Math.Min(size, 256)) + (rel.Contains("apple-touch-icon") ? 20 : 0)));
                }
            }
        }
        catch (Exception ex) { Log.Info($"Icon page {pageUrl}: {ex.Message}"); }

        var root = new Uri(baseUri.GetLeftPart(UriPartial.Authority) + "/");
        candidates.Add((new Uri(root, "apple-touch-icon.png"), 50));
        candidates.Add((new Uri(root, "favicon.ico"), 10));

        foreach (var (url, _) in candidates.OrderByDescending(c => c.Score).DistinctBy(c => c.Url.AbsoluteUri))
        {
            try
            {
                using var resp = await Http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) continue;
                var type = resp.Content.Headers.ContentType?.MediaType ?? "";
                if (type.Contains("html") || type.Contains("svg")) continue;
                var bytes = await resp.Content.ReadAsByteArrayAsync();
                if (bytes.Length == 0 || bytes.Length > 2 * 1024 * 1024) continue;
                var bmp = Decode(bytes);
                if (bmp == null || bmp.PixelSize.Width < 16) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                await File.WriteAllBytesAsync(dest, bytes);
                return true;
            }
            catch (Exception ex) { Log.Info($"Icon candidate {url}: {ex.Message}"); }
        }
        return false;
    }

    private static string? Attr(string tag, string name)
    {
        var m = Regex.Match(tag, $@"\b{name}\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase);
        return !m.Success ? null : m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
    }

    // ---------------- local program icons ----------------

    /// <summary>
    /// Icon for a program on this computer: a Mac .app's icon (converted with sips), or a Linux .desktop file's
    /// Icon= (looked up in the icon theme). Cached per path. Null when there's none.
    /// </summary>
    public static Bitmap? ProgramIcon(string path)
    {
        try
        {
            var cache = Path.Combine(Dir, "prog-" + Hash(path) + ".png");
            if (File.Exists(cache) && File.GetLastWriteTimeUtc(cache) >= SafeTime(path)) return Load(cache);
            string? source = null;
            if (Platform.IsMac && path.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && Directory.Exists(path))
            {
                var icns = MacAppIcon(path);
                if (icns != null)
                {
                    Directory.CreateDirectory(Dir);
                    var r = Platform.Run("/usr/bin/sips", new[] { "-s", "format", "png", "-Z", "128", icns, "--out", cache }, 15_000);
                    if (r.Ok && File.Exists(cache)) return Load(cache);
                }
                return null;
            }
            if (Platform.IsLinux && path.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                source = LinuxThemeIcon(DesktopEntryValue(path, "Icon"));
            if (source != null && File.Exists(source) && !source.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Dir);
                File.Copy(source, cache, true);
                return Load(cache);
            }
        }
        catch (Exception ex) { Log.Error($"Program icon {path}", ex); }
        return null;
    }

    private static DateTime SafeTime(string path)
    {
        try { return Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    private static string Hash(string s) =>
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(s)))[..16].ToLowerInvariant();

    /// <summary>The .icns file named in an app's Info.plist (CFBundleIconFile), or the first .icns in Resources.</summary>
    private static string? MacAppIcon(string app)
    {
        var resources = Path.Combine(app, "Contents", "Resources");
        if (!Directory.Exists(resources)) return null;
        var name = MacPlistValue(app, "CFBundleIconFile");
        if (!string.IsNullOrWhiteSpace(name))
        {
            var f = Path.Combine(resources, name.EndsWith(".icns") ? name : name + ".icns");
            if (File.Exists(f)) return f;
        }
        return Directory.GetFiles(resources, "*.icns").FirstOrDefault();
    }

    /// <summary>Reads a string from an app's Info.plist (works for binary plists too, via plutil).</summary>
    public static string? MacPlistValue(string app, string key)
    {
        var plist = Path.Combine(app, "Contents", "Info.plist");
        if (!File.Exists(plist)) return null;
        var r = Platform.Run("/usr/bin/plutil", new[] { "-extract", key, "raw", "-o", "-", plist }, 5000);
        return r.Ok ? r.Output.Trim() : null;
    }

    /// <summary>Linux: a key from a .desktop file's [Desktop Entry] section.</summary>
    public static string? DesktopEntryValue(string desktopFile, string key)
    {
        var inEntry = false;
        foreach (var raw in File.ReadLines(desktopFile))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { inEntry = line == "[Desktop Entry]"; continue; }
            if (inEntry && line.StartsWith(key + "=", StringComparison.Ordinal)) return line[(key.Length + 1)..].Trim();
        }
        return null;
    }

    /// <summary>Linux: finds an icon name in the hicolor theme or /usr/share/pixmaps (PNG only).</summary>
    private static string? LinuxThemeIcon(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        if (Path.IsPathRooted(icon)) return File.Exists(icon) ? icon : null;
        var dataDirs = (Environment.GetEnvironmentVariable("XDG_DATA_DIRS") ?? "/usr/local/share:/usr/share")
            .Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Prepend(Path.Combine(Platform.Home, ".local", "share"))
            .Append("/var/lib/flatpak/exports/share");
        foreach (var size in new[] { "128x128", "256x256", "96x96", "64x64", "48x48", "512x512", "32x32" })
            foreach (var d in dataDirs)
            {
                var f = Path.Combine(d, "icons", "hicolor", size, "apps", icon + ".png");
                if (File.Exists(f)) return f;
            }
        var pix = Path.Combine("/usr/share/pixmaps", icon + ".png");
        return File.Exists(pix) ? pix : null;
    }
}
