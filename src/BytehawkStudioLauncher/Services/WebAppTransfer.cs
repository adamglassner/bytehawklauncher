using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bytehawk.Launcher.Models;
using Bytehawk.Launcher.Views;

namespace Bytehawk.Launcher.Services;

/// <summary>
/// Export / import for Web Apps. Exports as Bytehawk JSON (keeps name, description, category) or as a
/// standard browser bookmarks file (.html) that Chrome, Edge and Firefox can import. Imports either format,
/// including bookmark files exported from those browsers (folder names become categories).
/// </summary>
public static class WebAppTransfer
{
    private sealed class ExportFile
    {
        public string Format { get; set; } = "bytehawk-webapps";
        public int Version { get; set; } = 1;
        public DateTime Exported { get; set; } = DateTime.Now;
        public List<WebAppEntry> WebApps { get; set; } = new();
    }

    public static string ToJson(IEnumerable<WebAppEntry> apps) =>
        JsonSerializer.Serialize(new ExportFile { WebApps = apps.ToList() }, JsonStore.Options);

    public static string ToBookmarksHtml(IEnumerable<WebAppEntry> apps)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE NETSCAPE-Bookmark-file-1>");
        sb.AppendLine("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">");
        sb.AppendLine("<TITLE>Bookmarks</TITLE>");
        sb.AppendLine("<H1>Bytehawk web apps</H1>");
        sb.AppendLine("<DL><p>");
        foreach (var group in apps.GroupBy(a => (a.Category ?? "").Trim()).OrderBy(g => g.Key.Length == 0).ThenBy(g => g.Key))
        {
            var indent = "    ";
            if (group.Key.Length > 0)
            {
                sb.AppendLine($"    <DT><H3>{WebUtility.HtmlEncode(group.Key)}</H3>");
                sb.AppendLine("    <DL><p>");
                indent = "        ";
            }
            foreach (var a in group)
            {
                sb.AppendLine($"{indent}<DT><A HREF=\"{WebUtility.HtmlEncode(a.Url)}\">{WebUtility.HtmlEncode(a.Name)}</A>");
                if (!string.IsNullOrWhiteSpace(a.Description)) sb.AppendLine($"{indent}<DD>{WebUtility.HtmlEncode(a.Description)}");
            }
            if (group.Key.Length > 0) sb.AppendLine("    </DL><p>");
        }
        sb.AppendLine("</DL><p>");
        return sb.ToString();
    }

    /// <summary>Reads a Bytehawk JSON export (or a plain JSON array of web apps) or a browser bookmarks .html file.</summary>
    public static List<WebAppEntry> Read(string text)
    {
        var t = text.TrimStart('﻿', ' ', '\r', '\n', '\t');
        var raw = t.StartsWith('{') || t.StartsWith('[') ? ReadJson(t) : ReadBookmarksHtml(t);
        var result = new List<WebAppEntry>();
        foreach (var w in raw)
        {
            var url = WebAppDialog.NormalizeUrl(w.Url, out _);
            if (url == null) continue; // skips javascript:, file:, place: and other non-web bookmarks
            result.Add(new WebAppEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Url = url,
                Name = string.IsNullOrWhiteSpace(w.Name) ? new Uri(url).Host : w.Name.Trim(),
                Description = w.Description?.Trim() ?? "",
                Category = w.Category?.Trim() ?? "",
                AddedAt = DateTime.Now,
            });
        }
        return result;
    }

    private static List<WebAppEntry> ReadJson(string json)
    {
        if (json.StartsWith('[')) return JsonSerializer.Deserialize<List<WebAppEntry>>(json, JsonStore.Options) ?? new();
        return JsonSerializer.Deserialize<ExportFile>(json, JsonStore.Options)?.WebApps ?? new();
    }

    /// <summary>Netscape bookmark format (what browsers export). The nearest folder (H3) becomes the category.</summary>
    private static List<WebAppEntry> ReadBookmarksHtml(string html)
    {
        var list = new List<WebAppEntry>();
        var folders = new Stack<string>();
        string? pendingFolder = null;
        WebAppEntry? last = null;
        var tokens = Regex.Matches(html,
            @"<H3[^>]*>(?<folder>.*?)</H3>|<DL[^>]*>|</DL>|<A\s[^>]*HREF\s*=\s*""(?<href>[^""]*)""[^>]*>(?<title>.*?)</A>|<DD>(?<desc>[^<\r\n]*)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        foreach (Match m in tokens)
        {
            var v = m.Value;
            if (m.Groups["folder"].Success) { pendingFolder = Clean(m.Groups["folder"].Value); }
            else if (v.StartsWith("<DL", StringComparison.OrdinalIgnoreCase)) { folders.Push(pendingFolder ?? ""); pendingFolder = null; }
            else if (v.StartsWith("</DL", StringComparison.OrdinalIgnoreCase)) { if (folders.Count > 0) folders.Pop(); }
            else if (m.Groups["href"].Success)
            {
                // Browser root folders ("Bookmarks bar", "Other bookmarks") aren't useful categories.
                var folder = folders.FirstOrDefault(f => f.Length > 0 && !IsRootFolder(f)) ?? "";
                last = new WebAppEntry { Url = WebUtility.HtmlDecode(m.Groups["href"].Value), Name = Clean(m.Groups["title"].Value), Category = folder };
                list.Add(last);
            }
            else if (m.Groups["desc"].Success && last != null) { last.Description = Clean(m.Groups["desc"].Value); }
        }
        return list;
    }

    private static bool IsRootFolder(string f) =>
        f.Equals("Bookmarks bar", StringComparison.OrdinalIgnoreCase) || f.Equals("Bookmarks Toolbar", StringComparison.OrdinalIgnoreCase) ||
        f.Equals("Favorites bar", StringComparison.OrdinalIgnoreCase) || f.Equals("Other bookmarks", StringComparison.OrdinalIgnoreCase) ||
        f.Equals("Other favorites", StringComparison.OrdinalIgnoreCase) || f.Equals("Bookmarks Menu", StringComparison.OrdinalIgnoreCase) ||
        f.Equals("Mobile bookmarks", StringComparison.OrdinalIgnoreCase) || f.Equals("Bytehawk web apps", StringComparison.OrdinalIgnoreCase);

    private static string Clean(string s) => WebUtility.HtmlDecode(Regex.Replace(s, "<[^>]+>", "")).Trim();

    /// <summary>Key for spotting duplicates: host + path + query, ignoring scheme, "www." and a trailing slash.</summary>
    public static string UrlKey(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url.Trim().ToLowerInvariant();
        var host = u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host;
        return (host + u.PathAndQuery.TrimEnd('/')).ToLowerInvariant();
    }
}
