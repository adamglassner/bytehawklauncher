using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Bytehawk.Launcher.Models;
using Microsoft.Win32;

namespace Bytehawk.Launcher.Services;

/// <summary>A server (e.g. WillowWallet Server) installed on this PC by its own Windows installer.</summary>
public sealed record InstalledServer(string Version, string Uninstaller);

/// <summary>Finds and uninstalls a server installed with its Inno Setup installer (Apps &amp; features entry).</summary>
public static class ServerInstall
{
    public static InstalledServer? Find(ServerDownload server)
    {
        if (string.IsNullOrWhiteSpace(server.WindowsUninstallKey)) return null;
        try
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + server.WindowsUninstallKey);
                if (key == null) continue;
                var exe = (key.GetValue("UninstallString") as string ?? "").Trim().Trim('"');
                if (exe.Length == 0 || !File.Exists(exe)) continue;
                return new InstalledServer(key.GetValue("DisplayVersion") as string ?? "", exe);
            }
        }
        catch (Exception ex) { Log.Error("Find installed server", ex); }
        return null;
    }

    public static string? DataFolder(ServerDownload server) =>
        string.IsNullOrWhiteSpace(server.WindowsDataFolder) ? null : Environment.ExpandEnvironmentVariables(server.WindowsDataFolder);

    /// <summary>
    /// Runs the server's uninstaller (Windows asks for administrator permission). Its data is kept unless
    /// <paramref name="deleteData"/>. Returns null when it worked, otherwise what went wrong.
    /// </summary>
    public static async Task<string?> UninstallAsync(ServerDownload server, InstalledServer installed, bool deleteData)
    {
        var args = "/SILENT /SUPPRESSMSGBOXES /NORESTART" + (deleteData ? " /DELETEDATA=1" : "");
        var problem = await RunElevatedAsync(installed.Uninstaller, args);
        if (problem != null) return problem;
        // The uninstaller copies itself to a temp folder and returns at once; wait for the real work to finish.
        for (int i = 0; i < 120 && Find(server) != null; i++) await Task.Delay(500);
        if (Find(server) != null) return "The uninstaller didn't finish. Try again, or uninstall it from Settings → Apps.";

        // Servers installed before the uninstaller could delete data (0.1.0): remove the folder here.
        var data = DataFolder(server);
        // The uninstaller deletes the data as its last step
        for (int i = 0; i < 30 && deleteData && data != null && Directory.Exists(data); i++) await Task.Delay(500);
        if (deleteData && data != null && Directory.Exists(data))
        {
            problem = await RunElevatedAsync(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c rmdir /s /q \"{data}\"");
            if (problem == null && Directory.Exists(data)) problem = $"The server was uninstalled, but its data is still in {data}.";
        }
        return problem;
    }

    private static async Task<string?> RunElevatedAsync(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            if (p != null) await p.WaitForExitAsync();
            Log.Info($"Ran {exe} {args} (exit {p?.ExitCode})");
            return p == null || p.ExitCode == 0 ? null : $"{Path.GetFileName(exe)} stopped with code {p.ExitCode}.";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "Uninstall was cancelled (administrator permission wasn't given).";
        }
        catch (Exception ex)
        {
            Log.Error($"Run {exe}", ex);
            return ex.Message;
        }
    }
}
