using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Bytehawk.Launcher.Services;

/// <summary>Dark / Light / Follow System, applied by swapping the theme ResourceDictionary.</summary>
public static class ThemeService
{
    public const string Dark = "Dark", Light = "Light", FollowSystem = "System";

    public static string Mode { get; private set; } = FollowSystem;
    public static bool IsDarkApplied { get; private set; } = true;

    public static event Action? ThemeChanged;

    private static bool _hooked;

    public static string Normalize(string? mode) =>
        string.Equals(mode, Dark, StringComparison.OrdinalIgnoreCase) ? Dark :
        string.Equals(mode, Light, StringComparison.OrdinalIgnoreCase) ? Light : FollowSystem;

    public static void Apply(string? mode)
    {
        Mode = Normalize(mode);
        if (!_hooked)
        {
            _hooked = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (Mode == FollowSystem && (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.VisualStyle))
                    Application.Current?.Dispatcher.InvokeAsync(ApplyResolved);
            };
        }
        ApplyResolved();
    }

    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v) return v == 0;
        }
        catch { /* ignore */ }
        return false; // Windows default is light
    }

    private static void ApplyResolved()
    {
        var dark = Mode switch { Dark => true, Light => false, _ => SystemPrefersDark() };
        var app = Application.Current;
        if (app == null) return;

        var dict = new ResourceDictionary
        {
            Source = new Uri(dark ? "pack://application:,,,/Themes/Dark.xaml" : "pack://application:,,,/Themes/Light.xaml")
        };
        var merged = app.Resources.MergedDictionaries;
        var existing = merged.FirstOrDefault(d => d.Source != null &&
            (d.Source.OriginalString.EndsWith("Dark.xaml") || d.Source.OriginalString.EndsWith("Light.xaml")));
        if (existing != null) merged[merged.IndexOf(existing)] = dict;
        else merged.Insert(0, dict);

        IsDarkApplied = dark;
        foreach (Window w in app.Windows) ApplyTitleBar(w);
        ThemeChanged?.Invoke();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Makes the native title bar dark/light to match (Windows 10 20H1+ / Windows 11).</summary>
    public static void ApplyTitleBar(Window w)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            int on = IsDarkApplied ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int)); // older Windows 10 builds
        }
        catch { /* cosmetic only */ }
    }
}
