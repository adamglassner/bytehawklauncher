using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Bytehawk.Launcher.Services;

public sealed record StoreAppInfo(string Name, string AppId, string Version, ImageSource? Icon);

/// <summary>
/// Microsoft Store / packaged (MSIX, UWP) apps. They have no .exe to point at, so they are identified by
/// their AppUserModelID (e.g. "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App") from the shell's Apps folder
/// and started with <c>explorer.exe shell:AppsFolder\&lt;AppId&gt;</c>.
/// </summary>
public static class StoreApps
{
    public static string ShellPath(string appId) => @"shell:AppsFolder\" + appId;

    /// <summary>True for packaged-app ids ("PackageFamilyName!AppId").</summary>
    public static bool IsPackagedAppId(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.Contains('!') && !path.Contains('\\') && !path.Contains('/');

    /// <summary>Lists installed packaged apps (name, id, version, icon). Runs on its own STA thread.</summary>
    public static Task<List<StoreAppInfo>> ListInstalledAsync()
    {
        var tcs = new TaskCompletionSource<List<StoreAppInfo>>();
        var t = new Thread(() =>
        {
            try { tcs.SetResult(ListInstalled()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true, Name = "StoreApps" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    private static List<StoreAppInfo> ListInstalled()
    {
        var result = new List<StoreAppInfo>();
        var type = Type.GetTypeFromProgID("Shell.Application") ?? throw new InvalidOperationException("Windows Shell is not available.");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            dynamic items = folder.Items();
            int count = items.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic item;
                try { item = items.Item(i); } catch { continue; }
                string name, path;
                try { name = item.Name; path = item.Path; } catch { continue; }
                if (!IsPackagedAppId(path) || string.IsNullOrWhiteSpace(name)) continue;
                string version = "";
                try { version = VersionFromPackageFullName((string)item.ExtendedProperty("System.AppUserModel.PackageFullName")); } catch { }
                result.Add(new StoreAppInfo(name, path, version, GetIcon(path)));
            }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
        return result.GroupBy(a => a.AppId, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                     .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>"Microsoft.WindowsCalculator_11.2405.2.0_x64__8wekyb3d8bbwe" → "11.2405.2.0".</summary>
    public static string VersionFromPackageFullName(string? fullName)
    {
        var m = Regex.Match(fullName ?? "", @"_(\d+\.\d+\.\d+\.\d+)_");
        return m.Success ? m.Groups[1].Value : "";
    }

    /// <summary>True if the app is still installed.</summary>
    public static bool Exists(string appId)
    {
        try
        {
            SHCreateItemFromParsingName(ShellPath(appId), IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var item);
            Marshal.ReleaseComObject(item);
            return true;
        }
        catch { return false; }
    }

    public static string? Launch(string appId)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", ShellPath(appId)) { UseShellExecute = true });
            Log.Info($"Launched Store app {appId}");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error($"Launch Store app {appId}", ex);
            return $"Couldn't start the app:\n{ex.Message}";
        }
    }

    // ---------- icon via IShellItemImageFactory (keeps transparency) ----------

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DIBSECTION
    {
        public BITMAP dsBm;
        public BITMAPINFOHEADER dsBmih;
        public int dsBitfield0, dsBitfield1, dsBitfield2;
        public IntPtr dshSection;
        public int dsOffset;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int size, ref DIBSECTION ds);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);

    private const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;

    /// <summary>The app's icon at about 64px, or null.</summary>
    public static ImageSource? GetIcon(string appId)
    {
        IShellItemImageFactory? factory = null;
        var hbm = IntPtr.Zero;
        try
        {
            SHCreateItemFromParsingName(ShellPath(appId), IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out factory);
            if (factory.GetImage(new NativeSize { cx = 64, cy = 64 }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out hbm) != 0 || hbm == IntPtr.Zero)
                return null;
            var ds = new DIBSECTION();
            if (GetObject(hbm, Marshal.SizeOf<DIBSECTION>(), ref ds) == 0 || ds.dsBm.bmBits == IntPtr.Zero || ds.dsBm.bmBitsPixel != 32)
                return null;
            int w = ds.dsBm.bmWidth, h = ds.dsBm.bmHeight, stride = ds.dsBm.bmWidthBytes;
            var bits = new byte[stride * h];
            Marshal.Copy(ds.dsBm.bmBits, bits, 0, bits.Length);
            if (ds.dsBmih.biHeight > 0) // bottom-up DIB: flip rows
            {
                var flipped = new byte[bits.Length];
                for (int y = 0; y < h; y++) Buffer.BlockCopy(bits, y * stride, flipped, (h - 1 - y) * stride, stride);
                bits = flipped;
            }
            var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, bits, stride);
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally
        {
            if (hbm != IntPtr.Zero) DeleteObject(hbm);
            if (factory != null) Marshal.ReleaseComObject(factory);
        }
    }
}
