using System.Diagnostics;
using System.IO;
using System.Windows;
using Bytehawk.Launcher.Services;
using Microsoft.Win32;

namespace Bytehawk.Launcher.Views;

/// <summary>
/// "Add to browser" for a browser extension the launcher installed: shows its unpacked folder,
/// opens Chrome's or Edge's Extensions page (with the folder copied) and lists the steps for "Load unpacked".
/// </summary>
public partial class ExtensionDialog : Window
{
    private readonly string _folder;
    private readonly string? _chrome;
    private readonly string? _edge;

    public ExtensionDialog(string extensionName, string folder, bool justInstalled)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);
        _folder = folder;
        Title = $"Add {extensionName} to your browser";
        Heading.Text = justInstalled ? $"{extensionName} is downloaded. Add it to your browser" : $"Add {extensionName} to your browser";
        FolderBox.Text = folder;

        _chrome = FindBrowser("chrome.exe", @"Google\Chrome\Application\chrome.exe");
        _edge = FindBrowser("msedge.exe", @"Microsoft\Edge\Application\msedge.exe");
        if (_chrome == null) { ChromeButton.IsEnabled = false; ChromeButton.ToolTip = "Google Chrome isn't installed on this PC."; }
        if (_edge == null) { EdgeButton.IsEnabled = false; EdgeButton.ToolTip = "Microsoft Edge isn't installed on this PC."; }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (CopyFolder()) CopyText.Text = "Copied";
    }

    private void OpenChrome_Click(object sender, RoutedEventArgs e) => OpenExtensionsPage(_chrome, "chrome://extensions/", "Google Chrome");

    private void OpenEdge_Click(object sender, RoutedEventArgs e) => OpenExtensionsPage(_edge, "edge://extensions/", "Microsoft Edge");

    private void ShowFolder_Click(object sender, RoutedEventArgs e) => ShellHelper.OpenFolder(_folder);

    private void OpenExtensionsPage(string? browser, string page, string browserName)
    {
        if (browser == null) { ShowError($"{browserName} isn't installed on this PC."); return; }
        CopyFolder();
        try
        {
            // Browsers only open their own chrome:// / edge:// pages when the page is passed on the command line.
            Process.Start(new ProcessStartInfo(browser, page) { UseShellExecute = false });
            ShowError(null);
        }
        catch (Exception ex)
        {
            Log.Error($"Open {page}", ex);
            ShowError($"Couldn't start {browserName}: {ex.Message}");
        }
    }

    private bool CopyFolder()
    {
        // The clipboard can be briefly locked by another program.
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetText(_folder); return true; }
            catch (Exception) { Thread.Sleep(60); }
        }
        return false;
    }

    private void ShowError(string? message)
    {
        ErrorText.Text = message ?? "";
        ErrorText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The browser's .exe from Windows' "App Paths" registration, else its usual install folders.</summary>
    public static string? FindBrowser(string exeName, string relativePath)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (var key in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\" })
            {
                try
                {
                    using var k = hive.OpenSubKey(key + exeName);
                    if (k?.GetValue(null) is string p && File.Exists(p.Trim('"'))) return p.Trim('"');
                }
                catch { /* not readable: try the next one */ }
            }
        }
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                 })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var p = Path.Combine(root, relativePath);
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
