using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Bytehawk.Launcher.Services;

/// <summary>Launching programs, opening folders/URLs and extracting file icons.</summary>
public static class ShellHelper
{
    /// <summary>Starts an .exe, .msi, .lnk (or anything with a file association). Returns an error message or null.</summary>
    public static string? Launch(string path, string? arguments = null, bool runAsAdmin = false)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return $"File not found:\n{path}";
        try
        {
            var psi = new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Arguments = arguments ?? "",
                WorkingDirectory = Path.GetDirectoryName(path) ?? "",
            };
            if (runAsAdmin) psi.Verb = "runas";
            Process.Start(psi);
            Log.Info($"Launched {path} {arguments} admin={runAsAdmin}");
            return null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "Launch was cancelled (administrator permission was not given).";
        }
        catch (Exception ex)
        {
            Log.Error($"Launch {path}", ex);
            return $"Couldn't start {Path.GetFileName(path)}:\n{ex.Message}";
        }
    }

    public static string? OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            Log.Info($"Opened URL {url}");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error($"OpenUrl {url}", ex);
            return $"Couldn't open the browser:\n{ex.Message}";
        }
    }

    public static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error($"OpenFolder {folder}", ex); }
    }

    /// <summary>Opens Explorer with the file selected.</summary>
    public static string? ShowInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir))
                OpenFolder(dir);
            else
                return $"The file and its folder no longer exist:\n{path}";
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    // ---------- icons ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string szFileName, int nIconIndex, int cxIcon, int cyIcon,
        IntPtr[] phicon, uint[] piconid, uint nIcons, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0;

    /// <summary>Extracts a file's icon (48px for .exe when available). Returns null if none could be read.</summary>
    public static ImageSource? GetFileIcon(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            if (Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var handles = new IntPtr[1];
                var ids = new uint[1];
                var n = PrivateExtractIcons(path, 0, 64, 64, handles, ids, 1, 0);
                if (n > 0 && n != 0xFFFFFFFF && handles[0] != IntPtr.Zero)
                    return FromHIcon(handles[0]);
            }
            var info = new SHFILEINFO();
            var r = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info), SHGFI_ICON | SHGFI_LARGEICON);
            if (r != IntPtr.Zero && info.hIcon != IntPtr.Zero)
                return FromHIcon(info.hIcon);
        }
        catch (Exception ex) { Log.Error($"Icon {path}", ex); }
        return null;
    }

    private static ImageSource FromHIcon(IntPtr hIcon)
    {
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally { DestroyIcon(hIcon); }
    }
}
