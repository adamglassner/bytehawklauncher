using System.Reflection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Bytehawk.Launcher.Desktop;
using Bytehawk.Launcher.Models;
using Bytehawk.Launcher.Services;

namespace Bytehawk.Launcher;

/// <summary>Bytehawk Studio Launcher for macOS and Linux.</summary>
public partial class App : Application
{
    /// <summary>The launcher version (from the assembly; CI stamps it from the .csproj).</summary>
    public static string VersionText { get; } =
        (typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0").Split('+')[0];

    public static AppSettings Settings { get; private set; } = new();

    public static void SaveSettings()
    {
        try { JsonStore.Save(AppPaths.Settings, Settings); }
        catch (Exception ex) { Log.Error("Save settings", ex); }
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        try { AppPaths.EnsureCreated(); } catch (Exception ex) { Log.Error("Data folder", ex); }
        Log.Info($"Starting Bytehawk Studio Launcher {VersionText} for {Platform.Name} ({Platform.ArchLabel})");
        Settings = JsonStore.Load(AppPaths.Settings, () => new AppSettings());
        if (Settings.SettingsVersion < 2)
        {
            if (string.IsNullOrWhiteSpace(Settings.RemoteCatalogUrl)) Settings.RemoteCatalogUrl = AppSettings.DefaultRemoteCatalogUrl;
            Settings.SettingsVersion = 2;
        }
        ApplyTheme(Settings.Theme);
        SaveSettings();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>"Dark", "Light" or "System" (follows the OS appearance).</summary>
    public static void ApplyTheme(string? mode)
    {
        if (Current == null) return;
        Current.RequestedThemeVariant = mode switch
        {
            "Dark" => ThemeVariant.Dark,
            "Light" => ThemeVariant.Light,
            _ => ThemeVariant.Default,
        };
    }

    public static string NormalizeTheme(string? mode) => mode is "Dark" or "Light" ? mode : "System";
}
