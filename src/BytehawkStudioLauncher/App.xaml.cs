using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Bytehawk.Launcher.Models;
using Bytehawk.Launcher.Services;

namespace Bytehawk.Launcher;

public partial class App : Application
{
    /// <summary>The app version (from the assembly, e.g. "1.0.0"). Set at build time by build.ps1 / CI from the release tag.</summary>
    public static string VersionText { get; } =
        (typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0")
        .Split('+')[0];
    private Mutex? _singleInstance;

    public static AppSettings Settings { get; private set; } = new();

    public static void SaveSettings() => JsonStore.Save(AppPaths.Settings, Settings);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Headless self-test used by CI: BytehawkStudioLauncher.exe --self-test <result-file>
        var selfTest = Array.IndexOf(e.Args, "--self-test");
        if (selfTest >= 0)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var output = selfTest + 1 < e.Args.Length ? e.Args[selfTest + 1]
                : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bytehawk-selftest.txt");
            _ = Dispatcher.InvokeAsync(async () => Shutdown(await SelfTest.RunAsync(output)));
            return;
        }

        _singleInstance = new Mutex(true, @"Local\BytehawkStudioLauncher.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Bytehawk Studio Launcher is already running.", "Bytehawk Studio Launcher",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled (domain)", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Error("Unobserved task", args.Exception); args.SetObserved(); };

        try { AppPaths.EnsureCreated(); }
        catch (Exception ex) { Log.Error("Could not create data folder", ex); }

        Log.Info($"Starting Bytehawk Studio Launcher {VersionText}");
        Settings = JsonStore.Load(AppPaths.Settings, () => new AppSettings());
        Settings.Theme = ThemeService.Normalize(Settings.Theme);
        ThemeService.Apply(Settings.Theme);
        try { SaveSettings(); } catch (Exception ex) { Log.Error("Save settings", ex); }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled (UI)", e.Exception);
        MessageBox.Show($"Something went wrong:\n{e.Exception.Message}\n\nDetails were written to launcher.log in the data folder.",
            "Bytehawk Studio Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    /// <summary>Lets the installer replace this exe during a self-update.</summary>
    public void ReleaseSingleInstance()
    {
        try { _singleInstance?.ReleaseMutex(); } catch { /* not owned */ }
        _singleInstance?.Dispose();
        _singleInstance = null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ReleaseSingleInstance();
        base.OnExit(e);
    }
}
