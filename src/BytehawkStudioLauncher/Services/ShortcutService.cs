using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media.Imaging;

namespace Bytehawk.Launcher.Services;

/// <summary>Desktop and Start menu shortcuts (.lnk for programs, .url for web apps).</summary>
public static class ShortcutService
{
    public static string DesktopDir => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    /// <summary>Start menu folder for apps installed by the launcher: Programs\Bytehawk Studio.</summary>
    public static string StartMenuDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Bytehawk Studio");

    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => invalid.Contains(c) ? ' ' : c).ToArray()).Trim().TrimEnd('.');
        return s.Length == 0 ? "App" : s;
    }

    /// <summary>Creates (or replaces) a .lnk shortcut.</summary>
    public static void CreateLnk(string lnkPath, string target, string? arguments = null, string? workingDir = null,
                                 string? iconLocation = null, string? description = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host is not available.");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic lnk = shell.CreateShortcut(lnkPath);
            lnk.TargetPath = target;
            if (!string.IsNullOrWhiteSpace(arguments)) lnk.Arguments = arguments;
            lnk.WorkingDirectory = workingDir ?? (File.Exists(target) ? Path.GetDirectoryName(target) ?? "" : "");
            if (!string.IsNullOrWhiteSpace(iconLocation)) lnk.IconLocation = iconLocation;
            if (!string.IsNullOrWhiteSpace(description)) lnk.Description = description.Length > 250 ? description[..250] : description;
            lnk.Save();
            Marshal.FinalReleaseComObject(lnk);
        }
        finally { Marshal.FinalReleaseComObject(shell); }
        Log.Info($"Shortcut {lnkPath} -> {target} {arguments}");
    }

    /// <summary>Creates an Internet shortcut (.url) that opens in the default browser.</summary>
    public static void CreateUrl(string urlPath, string url, string? iconFile = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(urlPath)!);
        var sb = new StringBuilder();
        sb.AppendLine("[InternetShortcut]");
        sb.AppendLine($"URL={url}");
        if (!string.IsNullOrEmpty(iconFile) && File.Exists(iconFile))
        {
            sb.AppendLine($"IconFile={iconFile}");
            sb.AppendLine("IconIndex=0");
        }
        File.WriteAllText(urlPath, sb.ToString(), Encoding.ASCII);
        Log.Info($"Shortcut {urlPath} -> {url}");
    }

    public static string? ResolveLnkTarget(string lnkPath)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            try
            {
                dynamic lnk = shell.CreateShortcut(lnkPath);
                string target = lnk.TargetPath;
                Marshal.FinalReleaseComObject(lnk);
                return string.IsNullOrWhiteSpace(target) ? null : target;
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }
        catch { return null; }
    }

    private static readonly object TargetsGate = new();
    private static HashSet<string>? _startMenuTargets;
    private static DateTime _startMenuTargetsAt;

    /// <summary>True if any Start menu shortcut (current user or all users) already points at <paramref name="target"/>.</summary>
    public static bool StartMenuHasShortcutTo(string target)
    {
        lock (TargetsGate)
        {
            // Resolving every .lnk is slow, so the set of targets is reused for two minutes.
            if (_startMenuTargets == null || DateTime.UtcNow - _startMenuTargetsAt > TimeSpan.FromMinutes(2))
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var root in new[]
                         {
                             Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                             Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                         })
                {
                    if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                    List<string> links;
                    try { links = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories).Take(3000).ToList(); }
                    catch { continue; }
                    foreach (var lnk in links)
                        if (ResolveLnkTarget(lnk) is { } t) set.Add(t);
                }
                _startMenuTargets = set;
                _startMenuTargetsAt = DateTime.UtcNow;
            }
            return _startMenuTargets.Contains(target);
        }
    }

    /// <summary>Marks a .lnk as "Run as administrator" (the flag Explorer sets in Advanced shortcut properties).</summary>
    public static void SetRunAsAdmin(string lnkPath)
    {
        var bytes = File.ReadAllBytes(lnkPath);
        if (bytes.Length > 0x15) { bytes[0x15] |= 0x20; File.WriteAllBytes(lnkPath, bytes); }
    }

    /// <summary>
    /// Adds a Start menu entry (Programs\Bytehawk Studio\&lt;name&gt;.lnk) for a program the launcher installed,
    /// unless its own installer already added one. Returns the shortcut path, or null if none was needed.
    /// </summary>
    public static string? EnsureStartMenuEntry(string name, string exePath, string? description = null)
    {
        if (!File.Exists(exePath) || !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        if (StartMenuHasShortcutTo(exePath)) return null;
        var lnk = Path.Combine(StartMenuDir, SafeName(name) + ".lnk");
        CreateLnk(lnk, exePath, iconLocation: exePath + ",0", description: description);
        lock (TargetsGate) _startMenuTargets?.Add(exePath);
        return lnk;
    }

    /// <summary>Saves an image as a Windows .ico (one PNG-compressed 256px-or-smaller entry; Windows Vista+).</summary>
    public static bool WriteIco(BitmapSource image, string icoPath)
    {
        try
        {
            BitmapSource src = image;
            if (src.PixelWidth > 256 || src.PixelHeight > 256)
            {
                var scale = 256.0 / Math.Max(src.PixelWidth, src.PixelHeight);
                src = new TransformedBitmap(src, new System.Windows.Media.ScaleTransform(scale, scale));
            }
            byte[] png;
            using (var ms = new MemoryStream())
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(src));
                enc.Save(ms);
                png = ms.ToArray();
            }
            Directory.CreateDirectory(Path.GetDirectoryName(icoPath)!);
            using var fs = File.Create(icoPath);
            using var w = new BinaryWriter(fs);
            w.Write((short)0); w.Write((short)1); w.Write((short)1);           // ICONDIR: reserved, type=icon, count=1
            w.Write((byte)(src.PixelWidth >= 256 ? 0 : src.PixelWidth));       // ICONDIRENTRY
            w.Write((byte)(src.PixelHeight >= 256 ? 0 : src.PixelHeight));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32);
            w.Write(png.Length); w.Write(6 + 16);
            w.Write(png);
            return true;
        }
        catch (Exception ex) { Log.Error($"Write icon {icoPath}", ex); return false; }
    }

    public static BitmapSource? LoadPng(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }
}
