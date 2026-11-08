using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bytehawk.Launcher.Services;

/// <summary>Well-known locations under %APPDATA%\BytehawkStudio\Launcher\.</summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BytehawkStudio", "Launcher");

    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Catalog => Path.Combine(Root, "catalog.json");
    public static string Library => Path.Combine(Root, "library.json");
    public static string WebApps => Path.Combine(Root, "webapps.json");
    public static string Installed => Path.Combine(Root, "installed.json");
    public static string CacheDir => Path.Combine(Root, "cache");
    public static string ReleaseCache => Path.Combine(CacheDir, "releases.json");
    public static string RemoteCatalogCache => Path.Combine(CacheDir, "remote-catalog.json");
    public static string DefaultDownloads => Path.Combine(Root, "downloads");
    public static string Apps => Path.Combine(Root, "apps");
    public static string Log => Path.Combine(Root, "launcher.log");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(DefaultDownloads);
        Directory.CreateDirectory(Apps);
    }
}

/// <summary>Small JSON file store (System.Text.Json) with atomic writes.</summary>
public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static T Load<T>(string path, Func<T> fallback)
    {
        try
        {
            if (!File.Exists(path)) return fallback();
            var text = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text)) return fallback();
            return JsonSerializer.Deserialize<T>(text, Options) ?? fallback();
        }
        catch (Exception ex)
        {
            // Keep the unreadable file so the user's data is never silently lost.
            Log.Error($"Could not read {path}; keeping a copy as .bad", ex);
            try { File.Copy(path, path + ".bad", overwrite: true); } catch { /* ignore */ }
            LastLoadError = $"{Path.GetFileName(path)} could not be read ({ex.Message}). A copy was saved as {Path.GetFileName(path)}.bad.";
            return fallback();
        }
    }

    /// <summary>Set when a file failed to parse, so the UI can tell the user.</summary>
    public static string? LastLoadError { get; set; }

    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Root);
                var fi = new FileInfo(AppPaths.Log);
                if (fi.Exists && fi.Length > 1_000_000)
                    File.Move(AppPaths.Log, AppPaths.Log + ".old", overwrite: true);
                File.AppendAllText(AppPaths.Log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never crash the app */ }
    }
}
