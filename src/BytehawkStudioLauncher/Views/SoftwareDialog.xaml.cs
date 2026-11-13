using System.IO;
using System.Windows;
using Bytehawk.Launcher.Models;
using Bytehawk.Launcher.Services;
using Microsoft.Win32;

namespace Bytehawk.Launcher.Views;

/// <summary>Add or edit a "My Software" entry (any .exe, .msi or .lnk on the PC).</summary>
public partial class SoftwareDialog : Window
{
    public LibraryEntry Result { get; private set; } = new();
    private readonly LibraryEntry? _editing;
    private string _autoName = "";
    private string _autoVersion = "";

    private readonly IEnumerable<string>? _categories;

    /// <summary>Set when the entry is a Microsoft Store / packaged app (no file path).</summary>
    private readonly string _appId = "";

    public SoftwareDialog(LibraryEntry? editing = null, string? initialPath = null, IEnumerable<string>? categories = null,
                          StoreAppInfo? storeApp = null)
    {
        InitializeComponent();
        _appId = storeApp?.AppId ?? editing?.AppId ?? "";
        _categories = categories;
        CategoryPicker.Fill(CategoryChips, CategoryBox, categories);
        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);
        _editing = editing;
        if (editing != null)
        {
            Title = Heading.Text = "Edit software";
            OkButton.Content = "Save";
            PathBox.Text = editing.Path;
            NameBox.Text = editing.Name;
            ArgsBox.Text = editing.Arguments;
            DescriptionBox.Text = editing.Description;
            WebsiteBox.Text = editing.Website;
            VersionBox.Text = editing.Version;
            CategoryBox.Text = editing.Category;
            AdminBox.IsChecked = editing.RunAsAdmin;
            _autoName = "";
        }
        else if (storeApp != null)
        {
            Title = Heading.Text = "Add Store app";
            NameBox.Text = storeApp.Name;
            VersionBox.Text = storeApp.Version;
        }
        else if (!string.IsNullOrEmpty(initialPath))
        {
            SetPath(initialPath);
        }
        if (_appId.Length > 0)
        {
            // Store apps are started by their app id: no file, arguments or "run as administrator".
            ProgramLabel.Text = "Microsoft Store app";
            PathBox.Text = _appId;
            PathBox.IsReadOnly = true;
            BrowseButton.Visibility = Visibility.Collapsed;
            ArgsBox.IsEnabled = false;
            ArgsNote.Visibility = Visibility.Visible;
            AdminBox.Visibility = Visibility.Collapsed;
            var icon = storeApp?.Icon ?? StoreApps.GetIcon(_appId);
            IconImage.Source = icon;
            IconFallback.Visibility = icon == null ? Visibility.Visible : Visibility.Collapsed;
        }
        else UpdateIcon();
        Loaded += (_, _) => NameBox.Focus();
    }

    /// <summary>Shows the standard Open dialog for programs. Returns null if cancelled.</summary>
    public static string? PickProgram(Window owner, string? startIn = null)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose a program",
            Filter = "Programs and shortcuts (*.exe;*.msi;*.lnk)|*.exe;*.msi;*.lnk|All files (*.*)|*.*",
            DereferenceLinks = false, // keep the .lnk itself instead of its target
            CheckFileExists = true,
        };
        if (!string.IsNullOrEmpty(startIn) && Directory.Exists(startIn)) dlg.InitialDirectory = startIn;
        return dlg.ShowDialog(owner) == true ? dlg.FileName : null;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var start = File.Exists(PathBox.Text) ? Path.GetDirectoryName(PathBox.Text) : null;
        var file = PickProgram(this, start);
        if (file != null) SetPath(file);
    }

    private void PathBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_appId.Length > 0) return; // Store app: nothing to resolve
        var p = PathBox.Text.Trim().Trim('"');
        if (p != PathBox.Text) PathBox.Text = p;
        if (File.Exists(p) && (string.IsNullOrWhiteSpace(NameBox.Text) || NameBox.Text == _autoName ||
                               string.IsNullOrWhiteSpace(VersionBox.Text) || VersionBox.Text == _autoVersion)) SetPath(p);
        UpdateIcon();
    }

    private void SetPath(string path)
    {
        PathBox.Text = path;
        // Auto-fill the name from the file name, unless the user already typed their own.
        if (string.IsNullOrWhiteSpace(NameBox.Text) || NameBox.Text == _autoName)
        {
            _autoName = Path.GetFileNameWithoutExtension(path);
            NameBox.Text = _autoName;
        }
        // Same for the version: fill it from the file's version info.
        if (string.IsNullOrWhiteSpace(VersionBox.Text) || VersionBox.Text == _autoVersion)
        {
            _autoVersion = ReadFileVersion(path);
            VersionBox.Text = _autoVersion;
        }
        UpdateIcon();
    }

    /// <summary>Product/file version of an .exe (or of a shortcut's target). Empty if unknown.</summary>
    public static string ReadFileVersion(string path)
    {
        try
        {
            var target = path;
            if (Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                target = ResolveShortcut(path) ?? path;
            if (!File.Exists(target) || !Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return "";
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(target);
            var v = (string.IsNullOrWhiteSpace(info.ProductVersion) ? info.FileVersion : info.ProductVersion)?.Trim() ?? "";
            v = v.Split('+', ' ')[0].Replace(',', '.');
            // "1.2.0.0" -> "1.2.0"
            while (v.EndsWith(".0") && v.Count(c => c == '.') > 2) v = v[..^2];
            return v is "0.0.0" or "0.0" or "0" ? "" : v;
        }
        catch { return ""; }
    }

    private static string? ResolveShortcut(string lnk)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            try
            {
                dynamic shortcut = shell.CreateShortcut(lnk);
                string target = shortcut.TargetPath;
                return string.IsNullOrWhiteSpace(target) ? null : target;
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }
        catch { return null; }
    }

    private void UpdateIcon()
    {
        var icon = ShellHelper.GetFileIcon(PathBox.Text.Trim());
        IconImage.Source = icon;
        IconFallback.Visibility = icon == null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var website = "";
        if (!string.IsNullOrWhiteSpace(WebsiteBox.Text))
        {
            website = WebAppDialog.NormalizeUrl(WebsiteBox.Text, out var webError) ?? "";
            if (website.Length == 0) { ShowError("Website: " + webError); return; }
        }
        if (_appId.Length > 0)
        {
            if (string.IsNullOrWhiteSpace(NameBox.Text)) { ShowError("Enter a display name."); return; }
            Result = new LibraryEntry
            {
                Id = _editing?.Id ?? Guid.NewGuid().ToString("N"),
                AddedAt = _editing?.AddedAt ?? DateTime.Now,
                AppId = _appId,
                Path = "",
                Name = NameBox.Text.Trim(),
                Description = DescriptionBox.Text.Trim(),
            Website = website,
                Version = VersionBox.Text.Trim(),
                Category = CategoryPicker.Normalize(CategoryBox.Text, _categories),
            };
            DialogResult = true;
            return;
        }
        var path = PathBox.Text.Trim().Trim('"');
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(path)) { ShowError("Choose a program first."); return; }
        if (!File.Exists(path)) { ShowError("That file doesn't exist. Use Browse… to pick it."); return; }
        if (ext is not (".exe" or ".msi" or ".lnk" or ".bat" or ".cmd" or ".appref-ms"))
        {
            ShowError("Pick an .exe, .msi or shortcut (.lnk) file.");
            return;
        }
        if (string.IsNullOrWhiteSpace(NameBox.Text)) { ShowError("Enter a display name."); return; }

        Result = new LibraryEntry
        {
            Id = _editing?.Id ?? Guid.NewGuid().ToString("N"),
            AddedAt = _editing?.AddedAt ?? DateTime.Now,
            Path = path,
            Name = NameBox.Text.Trim(),
            Arguments = ArgsBox.Text.Trim(),
            Description = DescriptionBox.Text.Trim(),
            Website = website,
            Version = VersionBox.Text.Trim(),
            Category = CategoryPicker.Normalize(CategoryBox.Text, _categories),
            RunAsAdmin = AdminBox.IsChecked == true,
        };
        DialogResult = true;
    }

    private void ShowError(string msg) { ErrorText.Text = msg; ErrorText.Visibility = Visibility.Visible; }
}
