using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace Bytehawk.Launcher.Services;

public readonly record struct DownloadProgress(long BytesReceived, long? TotalBytes, double BytesPerSecond);

/// <summary>Streams a file over HTTPS to disk with progress, speed and cancellation.</summary>
public static class Downloader
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,  // GitHub asset URLs redirect to their CDN
            MaxAutomaticRedirections = 10,
            ConnectTimeout = TimeSpan.FromSeconds(20),
        })
        { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(GitHubService.UserAgent);
        c.DefaultRequestHeaders.Accept.ParseAdd("application/octet-stream");
        return c;
    }

    /// <summary>Downloads to <paramref name="destination"/> (via a .part file). Deletes the partial file on failure/cancel.</summary>
    public static async Task DownloadAsync(string url, string destination, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var part = destination + ".part";
        try
        {
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength;

            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long received = 0;
                var clock = Stopwatch.StartNew();
                var lastReport = TimeSpan.FromSeconds(-1); // report the first chunk right away
                // speed over a sliding window
                long windowBytes = 0;
                var windowStart = TimeSpan.Zero;
                double speed = 0;

                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    received += read;
                    windowBytes += read;

                    var now = clock.Elapsed;
                    var windowLen = (now - windowStart).TotalSeconds;
                    if (windowLen >= 0.5)
                    {
                        var current = windowBytes / windowLen;
                        speed = speed == 0 ? current : speed * 0.6 + current * 0.4;
                        windowBytes = 0;
                        windowStart = now;
                    }
                    if ((now - lastReport).TotalMilliseconds >= 100)
                    {
                        lastReport = now;
                        progress.Report(new DownloadProgress(received, total, speed));
                    }
                }
                progress.Report(new DownloadProgress(received, total ?? received, speed));
            }
            File.Move(part, destination, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { /* ignore */ }
            throw;
        }
    }

    public static string FormatBytes(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        int i = 0;
        while (bytes >= 1024 && i < units.Length - 1) { bytes /= 1024; i++; }
        return i == 0 ? $"{bytes:0} {units[i]}" : $"{bytes:0.0} {units[i]}";
    }
}
