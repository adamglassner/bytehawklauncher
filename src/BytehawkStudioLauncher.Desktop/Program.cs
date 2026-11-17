using Avalonia;

namespace Bytehawk.Launcher;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Headless checks used by CI:  BytehawkStudioLauncher --self-test result.txt
        if (args.Contains("--print-data-dir")) { Console.WriteLine(Services.AppPaths.Root); return 0; }
        var i = Array.IndexOf(args, "--self-test");
        if (i >= 0)
        {
            var output = i + 1 < args.Length ? args[i + 1] : Path.Combine(Path.GetTempPath(), "bytehawk-selftest.txt");
            return Desktop.SelfTest.RunAsync(output).GetAwaiter().GetResult();
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
