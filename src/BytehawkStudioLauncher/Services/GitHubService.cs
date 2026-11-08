using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bytehawk.Launcher.Models;

namespace Bytehawk.Launcher.Services;

public enum GitHubStatus { Ok, NotModified, NoRelease, NotFound, RateLimited, NetworkError, Error }

public sealed record RepoInfo(string FullName, string Name, string Description, string HtmlUrl);

public sealed record GitHubResult<T>(GitHubStatus Status, T? Value, string? Message);

/// <summary>
/// Public GitHub REST API, no login. Unauthenticated calls are limited to 60/hour, so:
/// results are cached on disk, refreshes respect a minimum age, ETags are sent so
/// unchanged releases come back as 304, and once GitHub reports the limit we stop
/// calling until the reset time.
/// </summary>
public static class GitHubService
{
    public static string UserAgent => $"BytehawkStudioLauncher/{App.VersionText}";
    private static readonly HttpClient Api = CreateApiClient();

    /// <summary>When GitHub said we're out of requests, don't call again before this time (UTC).</summary>
    public static DateTime RateLimitedUntilUtc { get; private set; } = DateTime.MinValue;

    public static bool IsRateLimited => DateTime.UtcNow < RateLimitedUntilUtc;

    private static HttpClient CreateApiClient()
    {
        var c = new HttpClient { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        c.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return c;
    }

    /// <summary>Accepts "owner/repo", "https://github.com/owner/repo", ".../repo.git", ".../repo/releases" etc.</summary>
    public static bool TryParseRepo(string? input, out string ownerRepo)
    {
        ownerRepo = "";
        if (string.IsNullOrWhiteSpace(input)) return false;
        var s = input.Trim();
        var m = Regex.Match(s, @"^(?:https?://)?(?:www\.)?github\.com/([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+)", RegexOptions.IgnoreCase);
        if (!m.Success) m = Regex.Match(s, @"^([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+)/?$");
        if (!m.Success) return false;
        var owner = m.Groups[1].Value;
        var repo = m.Groups[2].Value;
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repo = repo[..^4];
        if (owner.Length == 0 || repo.Length == 0 || owner == "." || repo == ".") return false;
        ownerRepo = $"{owner}/{repo}";
        return true;
    }

    public static async Task<GitHubResult<RepoInfo>> GetRepoAsync(string ownerRepo, CancellationToken ct = default)
    {
        if (IsRateLimited) return RateLimitResult<RepoInfo>();
        try
        {
            using var resp = await Api.GetAsync($"repos/{ownerRepo}", ct);
            NoteRateLimit(resp);
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return new(GitHubStatus.NotFound, null, $"GitHub repo \"{ownerRepo}\" was not found (or it is private).");
            if (IsRateLimitResponse(resp)) return RateLimitResult<RepoInfo>();
            if (!resp.IsSuccessStatusCode)
                return new(GitHubStatus.Error, null, $"GitHub returned {(int)resp.StatusCode} {resp.ReasonPhrase}.");

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var r = doc.RootElement;
            return new(GitHubStatus.Ok, new RepoInfo(
                Str(r, "full_name") ?? ownerRepo,
                Str(r, "name") ?? ownerRepo.Split('/')[1],
                Str(r, "description") ?? "",
                Str(r, "html_url") ?? $"https://github.com/{ownerRepo}"), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Error($"GetRepo {ownerRepo}", ex);
            return new(GitHubStatus.NetworkError, null, "Couldn't reach GitHub.");
        }
        catch (Exception ex)
        {
            Log.Error($"GetRepo {ownerRepo}", ex);
            return new(GitHubStatus.Error, null, ex.Message);
        }
    }

    /// <summary>GET /repos/{owner}/{repo}/releases/latest (conditional on the cached ETag).</summary>
    public static async Task<GitHubResult<CachedRelease>> GetLatestReleaseAsync(string ownerRepo, CachedRelease? cached, CancellationToken ct = default)
    {
        if (IsRateLimited) return RateLimitResult<CachedRelease>();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"repos/{ownerRepo}/releases/latest");
            if (cached?.ETag is { Length: > 0 } etag && EntityTagHeaderValue.TryParse(etag, out var tag))
                req.Headers.IfNoneMatch.Add(tag);

            using var resp = await Api.SendAsync(req, ct);
            NoteRateLimit(resp);

            if (resp.StatusCode == HttpStatusCode.NotModified && cached != null)
            {
                cached.FetchedAt = DateTime.UtcNow;
                return new(GitHubStatus.NotModified, cached, null);
            }
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                // Either the repo doesn't exist or it has no published (non-prerelease) release.
                var none = new CachedRelease { Repo = ownerRepo, NoRelease = true, FetchedAt = DateTime.UtcNow };
                return new(GitHubStatus.NoRelease, none, "No published release found on GitHub.");
            }
            if (IsRateLimitResponse(resp)) return RateLimitResult<CachedRelease>();
            if (!resp.IsSuccessStatusCode)
                return new(GitHubStatus.Error, null, $"GitHub returned {(int)resp.StatusCode} {resp.ReasonPhrase}.");

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var r = doc.RootElement;
            var rel = new CachedRelease
            {
                Repo = ownerRepo,
                Tag = Str(r, "tag_name") ?? "",
                Name = Str(r, "name"),
                HtmlUrl = Str(r, "html_url"),
                PublishedAt = r.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String && p.TryGetDateTime(out var dt) ? dt : null,
                FetchedAt = DateTime.UtcNow,
                ETag = resp.Headers.ETag?.ToString(),
            };
            if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    rel.Assets.Add(new ReleaseAsset
                    {
                        Name = Str(a, "name") ?? "",
                        DownloadUrl = Str(a, "browser_download_url") ?? "",
                        Size = a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var n) ? n : 0,
                        Digest = Str(a, "digest"),
                    });
                }
            }
            return new(GitHubStatus.Ok, rel, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Error($"GetLatestRelease {ownerRepo}", ex);
            return new(GitHubStatus.NetworkError, null, "Couldn't reach GitHub.");
        }
        catch (Exception ex)
        {
            Log.Error($"GetLatestRelease {ownerRepo}", ex);
            return new(GitHubStatus.Error, null, ex.Message);
        }
    }

    private static readonly HttpClient Web = CreateWebClient();

    private static HttpClient CreateWebClient()
    {
        // Redirects are read, not followed: /releases/latest answers with a redirect to /releases/tag/<tag>.
        var c = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(15) })
        { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return c;
    }

    /// <summary>
    /// Asks github.com (not the API, so it doesn't use up the 60-an-hour allowance) which tag a repo's latest
    /// release has: github.com/&lt;repo&gt;/releases/latest redirects to /releases/tag/&lt;tag&gt;.
    /// Returns ("", true) when the repo has no published release, and (null, false) when github.com couldn't say.
    /// </summary>
    public static async Task<(string? Tag, bool Answered)> GetLatestTagAsync(string ownerRepo, CancellationToken ct = default)
    {
        var url = $"https://github.com/{ownerRepo}/releases/latest";
        try
        {
            for (int hop = 0; hop < 3; hop++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, url);
                using var resp = await Web.SendAsync(req, ct);
                var location = resp.Headers.Location?.ToString() ?? "";
                if ((int)resp.StatusCode is >= 300 and < 400 && location.Length > 0)
                {
                    var i = location.IndexOf("/releases/tag/", StringComparison.Ordinal);
                    if (i >= 0)
                    {
                        var tag = location[(i + "/releases/tag/".Length)..];
                        var cut = tag.IndexOfAny(['?', '#']);
                        if (cut >= 0) tag = tag[..cut];
                        return (Uri.UnescapeDataString(tag), true);
                    }
                    if (location.EndsWith("/releases/latest", StringComparison.Ordinal)) // repo was renamed
                    {
                        url = location.StartsWith('/') ? "https://github.com" + location : location;
                        continue;
                    }
                    return ("", true); // redirected to the empty releases page: no release yet
                }
                if (resp.StatusCode == HttpStatusCode.NotFound) return ("", true);
                return (null, false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Log.Error($"GetLatestTag {ownerRepo}", ex); }
        return (null, false);
    }

    /// <summary>Fetch a remote catalog JSON (same format as catalog.json).</summary>
    public static async Task<string> GetTextAsync(string url, CancellationToken ct = default)
    {
        using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return await c.GetStringAsync(url, ct);
    }

    public static string RateLimitMessage =>
        $"GitHub's hourly limit for anonymous requests was reached — showing last known versions. Try again after {RateLimitedUntilUtc.ToLocalTime():t}.";

    private static GitHubResult<T> RateLimitResult<T>() => new(GitHubStatus.RateLimited, default, RateLimitMessage);

    private static bool IsRateLimitResponse(HttpResponseMessage resp) =>
        (resp.StatusCode == HttpStatusCode.Forbidden || resp.StatusCode == (HttpStatusCode)429) && IsRateLimited;

    private static void NoteRateLimit(HttpResponseMessage resp)
    {
        var remaining = Header(resp, "x-ratelimit-remaining");
        var reset = Header(resp, "x-ratelimit-reset");
        if ((resp.StatusCode == HttpStatusCode.Forbidden || resp.StatusCode == (HttpStatusCode)429))
        {
            if (remaining == "0" && long.TryParse(reset, out var epoch))
                RateLimitedUntilUtc = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
            else if (resp.Headers.RetryAfter?.Delta is TimeSpan d)
                RateLimitedUntilUtc = DateTime.UtcNow + d;
            else if (resp.StatusCode == (HttpStatusCode)429 || remaining == "0")
                RateLimitedUntilUtc = DateTime.UtcNow.AddMinutes(5);
            Log.Info($"GitHub rate limited until {RateLimitedUntilUtc:u}");
        }
    }

    private static string? Header(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ---------- asset matching & version comparison ----------

    /// <summary>
    /// Picks the first release asset that matches the pattern. Pattern: one or more globs separated by ';'
    /// (e.g. "*.exe" or "*-win-x64.zip"); a glob starting with '!' excludes matches (e.g. "*.exe;!*arm64*").
    /// </summary>
    public static ReleaseAsset? MatchAsset(IEnumerable<ReleaseAsset> assets, string? pattern)
    {
        var parts = (string.IsNullOrWhiteSpace(pattern) ? "*" : pattern)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var include = parts.Where(p => !p.StartsWith('!')).Select(GlobToRegex).ToList();
        var exclude = parts.Where(p => p.StartsWith('!')).Select(p => GlobToRegex(p[1..])).ToList();
        if (include.Count == 0) include.Add(GlobToRegex("*"));

        // Respect the order of include patterns: earlier patterns win.
        foreach (var inc in include)
        {
            var hit = assets.FirstOrDefault(a => inc.IsMatch(a.Name) && !exclude.Any(x => x.IsMatch(a.Name)));
            if (hit != null) return hit;
        }
        return null;
    }

    private static Regex GlobToRegex(string glob) =>
        new("^" + Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase);

    /// <summary>Compares tags like "v1.2.0", "1.10", "release-2.0.1". Returns &gt;0 if a is newer.</summary>
    public static int CompareVersions(string? a, string? b)
    {
        var va = ParseVersion(a);
        var vb = ParseVersion(b);
        if (va != null && vb != null)
        {
            for (int i = 0; i < Math.Max(va.Count, vb.Count); i++)
            {
                long x = i < va.Count ? va[i] : 0, y = i < vb.Count ? vb[i] : 0;
                if (x != y) return x.CompareTo(y);
            }
            return 0;
        }
        return string.Compare(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
    }

    private static List<long>? ParseVersion(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = Regex.Match(s, @"(\d+(?:\.\d+)*)");
        if (!m.Success) return null;
        return m.Groups[1].Value.Split('.').Select(p => long.TryParse(p, out var n) ? n : 0).ToList();
    }

    public static string DisplayVersion(string? tag) =>
        string.IsNullOrWhiteSpace(tag) ? "" :
        (tag.Length > 1 && (tag[0] == 'v' || tag[0] == 'V') && char.IsDigit(tag[1]) ? tag[1..] : tag);
}
