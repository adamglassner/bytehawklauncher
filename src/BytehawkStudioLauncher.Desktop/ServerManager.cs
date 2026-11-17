using Bytehawk.Launcher.Models;
using Bytehawk.Launcher.Services;

namespace Bytehawk.Launcher.Desktop;

/// <summary>
/// Bytehawk Servers on macOS and Linux.
///   Linux: the release's linux-x64/arm64 .tar.gz holds install.sh / uninstall.sh, which set the server up as a
///          systemd service under /opt (run through pkexec, so the system asks for the admin password).
///   macOS and Linux: the Docker image, run with "docker run" (needs Docker Desktop / Docker Engine).
/// </summary>
public static class ServerManager
{
    public static bool HasNative(ServerDownload s) => Platform.IsLinux && !string.IsNullOrWhiteSpace(s.LinuxAsset);
    public static bool HasDocker(ServerDownload s) => !string.IsNullOrWhiteSpace(s.DockerImage);

    /// <summary>Available on this system at all (otherwise the card says "Coming soon").</summary>
    public static bool IsAvailable(ServerDownload s) => HasNative(s) || HasDocker(s);

    public static string ContainerName(ServerDownload s) =>
        !string.IsNullOrWhiteSpace(s.DockerName) ? s.DockerName! : AppManager.Safe(s.LinuxService ?? s.Name).ToLowerInvariant().Replace(' ', '-');

    public static string DockerRunCommand(ServerDownload s)
    {
        var n = ContainerName(s);
        return $"docker run -d --name {n} --restart unless-stopped -p {s.Port}:{s.Port} -v {n}-data:/data {s.DockerImage}:latest";
    }

    public static string PanelUrl(ServerDownload s) => $"http://localhost:{s.Port}";

    // ---------------- native (Linux) ----------------

    public static bool IsNativeInstalled(ServerDownload s) =>
        Platform.IsLinux && !string.IsNullOrWhiteSpace(s.LinuxInstallDir) && Directory.Exists(s.LinuxInstallDir);

    /// <summary>"active", "inactive", "failed"… from systemctl, or "" when unknown.</summary>
    public static string ServiceState(ServerDownload s)
    {
        if (!Platform.IsLinux || string.IsNullOrWhiteSpace(s.LinuxService)) return "";
        var r = Platform.Run("systemctl", new[] { "is-active", s.LinuxService! }, 5000);
        return r.Output.Trim();
    }

    public static string PackageFolder(string key) => Path.Combine(Platform.ServersDir, AppManager.Safe(key));

    /// <summary>Extracts the downloaded package and runs its install.sh as root.</summary>
    public static async Task InstallNativeAsync(string key, string tarball, IProgress<string> status)
    {
        var folder = PackageFolder(key);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
        status.Report("Extracting…");
        var r = await Platform.RunAsync("tar", new[] { "-xzf", tarball, "-C", folder });
        if (!r.Ok) throw new InvalidOperationException($"Couldn't extract the server package: {r.Message}");
        var install = Directory.GetFiles(folder, "install.sh", SearchOption.AllDirectories).FirstOrDefault()
                      ?? throw new InvalidOperationException("The server package has no install.sh.");
        Platform.MakeExecutable(install);
        status.Report("Waiting for your password…");
        r = await AppManager.RunAsAdminAsync(new[] { "/bin/bash", install });
        if (!r.Ok) throw new InvalidOperationException(r.ExitCode is 126 or 127
            ? "Installation was cancelled (the admin password wasn't given)."
            : "The server's install script failed:\n" + r.Message);
        Log.Info($"Server {key} installed: {r.Output}");
    }

    /// <summary>Runs uninstall.sh (from the install folder, or the downloaded package) as root; --purge also deletes the data.</summary>
    public static async Task UninstallNativeAsync(string key, ServerDownload s, bool purge)
    {
        var script = new[]
            {
                string.IsNullOrWhiteSpace(s.LinuxInstallDir) ? null : Path.Combine(s.LinuxInstallDir!, "uninstall.sh"),
                Directory.Exists(PackageFolder(key)) ? Directory.GetFiles(PackageFolder(key), "uninstall.sh", SearchOption.AllDirectories).FirstOrDefault() : null,
            }
            .FirstOrDefault(p => p != null && File.Exists(p))
            ?? throw new InvalidOperationException("Couldn't find the server's uninstall.sh. Install the server again from here, then uninstall it.");
        var args = new List<string> { "/bin/bash", script };
        if (purge) args.Add("--purge");
        var r = await AppManager.RunAsAdminAsync(args);
        if (!r.Ok) throw new InvalidOperationException(r.ExitCode is 126 or 127
            ? "Uninstall was cancelled (the admin password wasn't given)."
            : "The server's uninstall script failed:\n" + r.Message);
        try { if (Directory.Exists(PackageFolder(key))) Directory.Delete(PackageFolder(key), true); } catch { /* ignore */ }
    }

    // ---------------- Docker ----------------

    public static bool DockerInstalled => Platform.HasCommand("docker") || File.Exists("/Applications/Docker.app/Contents/Resources/bin/docker");

    /// <summary>"running", "exited"… for the container, "" if there's no such container, null if Docker isn't answering.</summary>
    public static string? ContainerState(ServerDownload s)
    {
        if (!DockerInstalled) return null;
        var r = Platform.Run(Platform.Which("docker"), new[] { "inspect", "-f", "{{.State.Status}}", ContainerName(s) }, 10_000);
        if (r.Ok) return r.Output.Trim();
        return r.Error.Contains("No such", StringComparison.OrdinalIgnoreCase) ? "" : null;
    }

    public static async Task DockerRunAsync(ServerDownload s, IProgress<string> status)
    {
        var docker = Platform.Which("docker");
        var n = ContainerName(s);
        status.Report("Downloading the server image…");
        var pull = await Platform.RunAsync(docker, new[] { "pull", s.DockerImage + ":latest" }, 1_800_000);
        if (!pull.Ok) throw new InvalidOperationException(DockerHint(pull.Message));
        // Replace an old container (data lives in the named volume, so it's kept).
        await Platform.RunAsync(docker, new[] { "rm", "-f", n });
        status.Report("Starting…");
        var run = await Platform.RunAsync(docker, new[]
        {
            "run", "-d", "--name", n, "--restart", "unless-stopped",
            "-p", $"{s.Port}:{s.Port}", "-v", $"{n}-data:/data", s.DockerImage + ":latest",
        });
        if (!run.Ok) throw new InvalidOperationException(DockerHint(run.Message));
    }

    public static async Task DockerStopAsync(ServerDownload s, bool deleteData)
    {
        var docker = Platform.Which("docker");
        var n = ContainerName(s);
        var r = await Platform.RunAsync(docker, new[] { "rm", "-f", n });
        if (!r.Ok && !r.Message.Contains("No such", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(DockerHint(r.Message));
        if (deleteData) await Platform.RunAsync(docker, new[] { "volume", "rm", $"{n}-data" });
    }

    private static string DockerHint(string message) =>
        message.Contains("daemon", StringComparison.OrdinalIgnoreCase) || message.Contains("connect", StringComparison.OrdinalIgnoreCase)
            ? "Docker isn't running. Start Docker Desktop (or the Docker service), then try again.\n\n" + message
            : message;
}
