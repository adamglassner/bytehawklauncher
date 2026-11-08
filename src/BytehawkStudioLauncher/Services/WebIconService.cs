using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Bytehawk.Launcher.Services;

/// <summary>
/// Finds a web app's icon the way a browser does: reads the page's &lt;link rel="icon"&gt; /
/// &lt;link rel="apple-touch-icon"&gt; tags, then falls back to /apple-touch-icon.png and /favicon.ico.
/// The best image is saved as PNG under cache\icons\ so cards show it offline. SVG icons are skipped
/// (WPF can't decode them).
/// </summary>
public static class WebIconService
{
    private static readonly HttpClient Http = CreateClient();
    private const int MaxIconBytes = 2 * 1024 * 1024;
    private const int MaxHtmlChars = 512 * 1024;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10 })
        { Timeout = TimeSpan.FromSeconds(12) };
        // Some sites refuse requests without a browser-like user agent.
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"Mozilla/5.0 (Windows NT 10.0; Win64; x64) {GitHubService.UserAgent}");
        c.DefaultRequestHeaders.Accept.ParseAdd("text/html,image/png,image/x-icon,image/*;q=0.8,*/*;q=0.5");
        return c;
    }

    public static string IconsDir => Path.Combine(AppPaths.CacheDir, "icons");
    public static string CachePath(string id) => Path.Combine(IconsDir, id + ".png");

    public static ImageSource? LoadCached(string id)
    {
        var path = CachePath(id);
        if (!File.Exists(path)) return null;
        try { return LoadPng(path); }
        catch (Exception ex) { Log.Error($"Icon cache {path}", ex); return null; }
    }

    public static void DeleteCached(string id)
    {
        try { var p = CachePath(id); if (File.Exists(p)) File.Delete(p); } catch { /* ignore */ }
    }

    /// <summary>Downloads the site's icon and saves it as PNG to <paramref name="pngPath"/>. Returns true on success.</summary>
    public static async Task<bool> FetchAsync(string pageUrl, string pngPath, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var page)) return false;
        var candidates = new List<(Uri Url, int Score)>();
        var baseUri = page;

        // 1) icons declared in the page
        try
        {
            using var resp = await Http.GetAsync(page, HttpCompletionOption.ResponseHeadersRead, ct);
            baseUri = resp.RequestMessage?.RequestUri ?? page;
            var type = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (resp.IsSuccessStatusCode && type.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                var html = await ReadLimitedAsync(resp, ct);
                var baseTag = Regex.Match(html, @"<base\b[^>]*\bhref\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                if (baseTag.Success && Uri.TryCreate(baseUri, System.Net.WebUtility.HtmlDecode(baseTag.Groups[1].Value), out var b)) baseUri = b;
                candidates.AddRange(ParseLinkIcons(html, baseUri));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Log.Info($"Icon page fetch failed for {pageUrl}: {ex.Message}"); }

        // 2) well-known locations at the site root
        var root = new Uri(baseUri.GetLeftPart(UriPartial.Authority) + "/");
        candidates.Add((new Uri(root, "apple-touch-icon.png"), 50));
        candidates.Add((new Uri(root, "favicon.ico"), 10));

        foreach (var (url, _) in candidates.OrderByDescending(c => c.Score).DistinctBy(c => c.Url.AbsoluteUri))
        {
            try
            {
                var bytes = url.Scheme == "data" ? DecodeDataUri(url.OriginalString) : await DownloadIconAsync(url, ct);
                if (bytes == null) continue;
                var frame = DecodeBestFrame(bytes);
                if (frame == null) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(pngPath)!);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(frame));
                using (var fs = File.Create(pngPath)) enc.Save(fs);
                Log.Info($"Icon for {pageUrl} from {url} ({frame.PixelWidth}px)");
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Log.Info($"Icon candidate {url} failed: {ex.Message}"); }
        }
        return false;
    }

    /// <summary>Scores &lt;link&gt; icons: apple-touch-icon and larger sizes first (capped at 256px), SVG/mask icons skipped.</summary>
    internal static IEnumerable<(Uri, int)> ParseLinkIcons(string html, Uri baseUri)
    {
        foreach (Match link in Regex.Matches(html, @"<link\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var tag = link.Value;
            var rel = Attr(tag, "rel")?.ToLowerInvariant() ?? "";
            var href = Attr(tag, "href");
            if (string.IsNullOrWhiteSpace(href) || !rel.Contains("icon") || rel.Contains("mask-icon")) continue;
            var type = Attr(tag, "type")?.ToLowerInvariant() ?? "";
            href = System.Net.WebUtility.HtmlDecode(href.Trim());
            if (type.Contains("svg") || href.Split('?', '#')[0].EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Uri.TryCreate(baseUri, href, out var url)) continue;

            int size = 0;
            var sizes = Attr(tag, "sizes");
            if (sizes != null)
                foreach (Match m in Regex.Matches(sizes, @"(\d+)x(\d+)", RegexOptions.IgnoreCase))
                    size = Math.Max(size, int.Parse(m.Groups[1].Value));
            if (size == 0 && rel.Contains("apple-touch-icon")) size = 180;
            var score = (size == 0 ? 32 : Math.Min(size, 256)) + (rel.Contains("apple-touch-icon") ? 20 : 0) + 100;
            yield return (url, score);
        }
    }

    private static string? Attr(string tag, string name)
    {
        var m = Regex.Match(tag, $@"\b{name}\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase);
        return !m.Success ? null : m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(s);
        var buffer = new char[MaxHtmlChars];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
        return new string(buffer, 0, read);
    }

    private static async Task<byte[]?> DownloadIconAsync(Uri url, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var type = resp.Content.Headers.ContentType?.MediaType ?? "";
        if (type.Contains("html") || type.Contains("svg")) return null;
        if (resp.Content.Headers.ContentLength > MaxIconBytes) return null;
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        return bytes.Length is > 0 and <= MaxIconBytes ? bytes : null;
    }

    private static byte[]? DecodeDataUri(string uri)
    {
        var m = Regex.Match(uri, @"^data:image/(png|x-icon|vnd\.microsoft\.icon|jpeg|gif|bmp);base64,(.+)$", RegexOptions.IgnoreCase);
        return m.Success ? Convert.FromBase64String(m.Groups[2].Value) : null;
    }

    /// <summary>Decodes PNG/ICO/JPEG/GIF/BMP and returns the largest frame (ICO files hold several sizes).</summary>
    private static BitmapSource? DecodeBestFrame(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var best = decoder.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault();
        if (best == null || best.PixelWidth < 16) return null;
        BitmapSource result = best;
        if (best.PixelWidth > 128)
        {
            var scale = 128.0 / best.PixelWidth;
            result = new TransformedBitmap(best, new ScaleTransform(scale, scale));
        }
        result.Freeze();
        return result;
    }

    private static ImageSource LoadPng(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
        bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
