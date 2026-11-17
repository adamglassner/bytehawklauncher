using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Bytehawk.Launcher.Desktop;
using Bytehawk.Launcher.Models;
using Bytehawk.Launcher.Services;

namespace Bytehawk.Launcher.Views;

/// <summary>Themed message, confirm and form dialogs (built in code).</summary>
public static class Dialogs
{
    private static Window MakeWindow(string title, double width)
    {
        var w = new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        w.Bind(TemplatedControl.BackgroundProperty, w.GetResourceObservable("WindowBg"));
        return w;
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 18, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap };
    private static TextBlock Body(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Classes = { "subtle" } };

    private static StackPanel Buttons(params Button[] buttons)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 20, 0, 0) };
        foreach (var b in buttons) p.Children.Add(b);
        return p;
    }

    private static Button Btn(string text, bool accent = false)
    {
        var b = new Button { Content = text, MinWidth = 96, HorizontalContentAlignment = HorizontalAlignment.Center };
        if (accent) b.Classes.Add("primary");
        return b;
    }

    public static async Task Info(Window owner, string title, string message)
    {
        var w = MakeWindow(title, 460);
        var ok = Btn("OK", accent: true);
        ok.IsDefault = true;
        ok.Click += (_, _) => w.Close();
        w.Content = new StackPanel { Margin = new Thickness(24, 20, 24, 22), Children = { Heading(title), Body(message), Buttons(ok) } };
        await w.ShowDialog(owner);
    }

    public static async Task<bool> Confirm(Window owner, string title, string message, string yes = "OK", string no = "Cancel")
    {
        var (ok, _) = await ConfirmWithOption(owner, title, message, yes, no, null);
        return ok;
    }

    /// <summary>Confirm with an optional checkbox (e.g. "Also delete the server's data"). Returns (confirmed, checked).</summary>
    public static async Task<(bool Ok, bool Checked)> ConfirmWithOption(Window owner, string title, string message, string yes, string no, string? option)
    {
        var w = MakeWindow(title, 480);
        var result = false;
        var bYes = Btn(yes, accent: true);
        var bNo = Btn(no);
        bYes.Click += (_, _) => { result = true; w.Close(); };
        bNo.Click += (_, _) => w.Close();
        bNo.IsCancel = true;
        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 22), Children = { Heading(title), Body(message) } };
        CheckBox? cb = null;
        if (option != null)
        {
            cb = new CheckBox { Margin = new Thickness(0, 14, 0, 0), Content = new TextBlock { Text = option, TextWrapping = TextWrapping.Wrap, MaxWidth = 400 } };
            panel.Children.Add(cb);
        }
        panel.Children.Add(Buttons(bNo, bYes));
        w.Content = panel;
        await w.ShowDialog(owner);
        return (result, cb?.IsChecked == true);
    }

    /// <summary>Shows a command the user can copy (e.g. a docker run line).</summary>
    public static async Task ShowCommand(Window owner, string title, string message, string command)
    {
        var w = MakeWindow(title, 620);
        var box = new TextBox { Text = command, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Menlo, DejaVu Sans Mono, monospace"), Margin = new Thickness(0, 12, 0, 0) };
        var copy = Btn("Copy");
        copy.Click += async (_, _) =>
        {
            if (w.Clipboard != null) await w.Clipboard.SetTextAsync(command);
            copy.Content = "Copied ✓";
        };
        var close = Btn("Close", accent: true);
        close.Click += (_, _) => w.Close();
        w.Content = new StackPanel { Margin = new Thickness(24, 20, 24, 22), Children = { Heading(title), Body(message), box, Buttons(copy, close) } };
        await w.ShowDialog(owner);
    }

    // ---------------- forms ----------------

    internal sealed class Form
    {
        public readonly Window Window;
        public readonly StackPanel Panel;
        public readonly TextBlock Error = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(0, 10, 0, 0) };
        public readonly Button Ok;
        public bool Saved;

        public Form(string title, string okText, double width = 540)
        {
            Window = MakeWindow(title, width);
            Panel = new StackPanel { Margin = new Thickness(24, 20, 24, 22) };
            Panel.Children.Add(Heading(title));
            Ok = Btn(okText, accent: true);
            Ok.IsDefault = true;
        }

        public TextBox Field(string label, string? value = null, string? watermark = null, bool multiline = false)
        {
            Panel.Children.Add(new TextBlock { Text = label, Classes = { "label" } });
            var t = new TextBox { Text = value ?? "", Watermark = watermark };
            if (multiline) { t.AcceptsReturn = false; t.TextWrapping = TextWrapping.Wrap; t.Height = 60; t.VerticalContentAlignment = VerticalAlignment.Top; }
            Panel.Children.Add(t);
            return t;
        }

        /// <summary>A text field with a button on its right (e.g. Browse…).</summary>
        public TextBox FieldWithButton(string label, string? value, string buttonText, Func<TextBox, Task> onClick)
        {
            Panel.Children.Add(new TextBlock { Text = label, Classes = { "label" } });
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var t = new TextBox { Text = value ?? "" };
            var b = new Button { Content = buttonText, Margin = new Thickness(8, 0, 0, 0) };
            b.Click += async (_, _) => await onClick(t);
            Grid.SetColumn(b, 1);
            grid.Children.Add(t);
            grid.Children.Add(b);
            Panel.Children.Add(grid);
            return t;
        }

        /// <summary>Category field with suggestion chips.</summary>
        public TextBox CategoryField(string? value, IEnumerable<string> existing)
        {
            var t = Field("Category (optional)", value, "Type your own or pick one below");
            var chips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var c in Categories.Suggestions(existing))
            {
                var b = new Button { Content = c, Padding = new Thickness(9, 3), FontSize = 11, Margin = new Thickness(0, 0, 5, 5) };
                b.Click += (_, _) => t.Text = c;
                chips.Children.Add(b);
            }
            Panel.Children.Add(chips);
            return t;
        }

        public void ShowError(string message) { Error.Text = message; Error.IsVisible = true; }

        public async Task<bool> ShowAsync(Window owner, Func<bool> validate)
        {
            var cancel = Btn("Cancel");
            cancel.IsCancel = true;
            cancel.Click += (_, _) => Window.Close();
            Ok.Click += (_, _) => { Error.IsVisible = false; if (validate()) { Saved = true; Window.Close(); } };
            Panel.Children.Add(Error);
            Panel.Children.Add(Buttons(cancel, Ok));
            Window.Content = new ScrollViewer { Content = Panel, MaxHeight = 760 };
            await Window.ShowDialog(owner);
            return Saved;
        }
    }

    /// <summary>File picker for programs: Mac .app bundles / Linux executables, AppImages and .desktop files.</summary>
    public static async Task<string?> PickProgram(Window owner)
    {
        var start = await owner.StorageProvider.TryGetFolderFromPathAsync(new Uri("file://" + (Platform.IsMac ? "/Applications" : Platform.Home)));
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Platform.IsMac ? "Choose an app" : "Choose a program",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>Add/edit a My Software entry. Returns the entry, or null if cancelled.</summary>
    public static async Task<LibraryEntry?> EditSoftware(Window owner, LibraryEntry? editing, string? initialPath, IEnumerable<string> categories)
    {
        var f = new Form(editing == null ? "Add software" : "Edit software", editing == null ? "Add" : "Save");
        TextBox? name = null, version = null;
        var autoName = "";
        var autoVersion = "";
        void Fill(string p)
        {
            var guessName = Platform.IsMac && p.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                ? IconCache.MacPlistValue(p, "CFBundleDisplayName") ?? IconCache.MacPlistValue(p, "CFBundleName") ?? Path.GetFileNameWithoutExtension(p)
                : p.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase) && File.Exists(p)
                    ? IconCache.DesktopEntryValue(p, "Name") ?? Path.GetFileNameWithoutExtension(p)
                    : Path.GetFileNameWithoutExtension(p);
            if (name != null && (string.IsNullOrWhiteSpace(name.Text) || name.Text == autoName)) { autoName = guessName; name.Text = guessName; }
            var v = Platform.IsMac && p.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? IconCache.MacPlistValue(p, "CFBundleShortVersionString") ?? "" : "";
            if (version != null && (string.IsNullOrWhiteSpace(version.Text) || version.Text == autoVersion)) { autoVersion = v; version.Text = v; }
        }
        var path = f.FieldWithButton(Platform.IsMac ? "App or program" : "Program (executable, AppImage, script or .desktop file)",
            editing?.Path ?? initialPath, "Browse…", async box =>
            {
                var p = await PickProgram(f.Window);
                if (p != null) { box.Text = p; Fill(p); }
            });
        name = f.Field("Display name", editing?.Name);
        var desc = f.Field("Description (optional)", editing?.Description, multiline: true);
        var website = f.Field("Website (optional)", editing?.Website, "example.com");
        version = f.Field("Version (optional)", editing?.Version);
        var args = f.Field("Launch arguments (optional)", editing?.Arguments);
        var cat = f.CategoryField(editing?.Category, categories);
        f.Panel.Children.Add(new TextBlock { Text = "Removing an entry later only removes it from the launcher. Your files are never deleted.", Classes = { "subtle" }, FontSize = 12, Margin = new Thickness(0, 14, 0, 0) });
        if (editing == null && !string.IsNullOrEmpty(initialPath)) Fill(initialPath);

        LibraryEntry? result = null;
        var ok = await f.ShowAsync(owner, () =>
        {
            var p = (path.Text ?? "").Trim().Trim('"');
            if (p.Length == 0) { f.ShowError("Choose a program first."); return false; }
            if (!File.Exists(p) && !Directory.Exists(p)) { f.ShowError("That file doesn't exist. Use Browse… to pick it."); return false; }
            if (string.IsNullOrWhiteSpace(name.Text)) { f.ShowError("Enter a display name."); return false; }
            var site = "";
            if (!string.IsNullOrWhiteSpace(website.Text))
            {
                site = WebAppDialog.NormalizeUrl(website.Text, out var err) ?? "";
                if (site.Length == 0) { f.ShowError("Website: " + err); return false; }
            }
            result = new LibraryEntry
            {
                Id = editing?.Id ?? Guid.NewGuid().ToString("N"),
                AddedAt = editing?.AddedAt ?? DateTime.Now,
                Path = p,
                Name = name.Text!.Trim(),
                Description = (desc.Text ?? "").Trim(),
                Website = site,
                Version = (version.Text ?? "").Trim(),
                Arguments = (args.Text ?? "").Trim(),
                Category = Categories.Normalize(cat.Text, categories),
            };
            return true;
        });
        return ok ? result : null;
    }

    /// <summary>Add/edit a Web App. Returns the entry, or null if cancelled.</summary>
    public static async Task<WebAppEntry?> EditWebApp(Window owner, WebAppEntry? editing, IEnumerable<string> categories)
    {
        var f = new Form(editing == null ? "Add web app" : "Edit web app", editing == null ? "Add" : "Save", 500);
        f.Panel.Children.Add(new TextBlock { Text = "A bookmark that opens in your default browser. Nothing is downloaded or installed.", Classes = { "subtle" } });
        var url = f.Field("Web address", editing?.Url, "example.com");
        var name = f.Field("Display name", editing?.Name);
        var desc = f.Field("Description (optional)", editing?.Description, multiline: true);
        var cat = f.CategoryField(editing?.Category, categories);
        url.LostFocus += (_, _) =>
        {
            var u = WebAppDialog.NormalizeUrl(url.Text, out _);
            if (u != null && string.IsNullOrWhiteSpace(name.Text)) name.Text = WebAppDialog.SuggestName(new Uri(u).Host);
        };
        WebAppEntry? result = null;
        var ok = await f.ShowAsync(owner, () =>
        {
            var u = WebAppDialog.NormalizeUrl(url.Text, out var err);
            if (u == null) { f.ShowError(err); return false; }
            result = new WebAppEntry
            {
                Id = editing?.Id ?? Guid.NewGuid().ToString("N"),
                AddedAt = editing?.AddedAt ?? DateTime.Now,
                Url = u,
                Name = string.IsNullOrWhiteSpace(name.Text) ? WebAppDialog.SuggestName(new Uri(u).Host) : name.Text!.Trim(),
                Description = (desc.Text ?? "").Trim(),
                Category = Categories.Normalize(cat.Text, categories),
            };
            return true;
        });
        return ok ? result : null;
    }
}

/// <summary>Suggested categories for My Software and Web Apps (same list as the Windows launcher).</summary>
public static class Categories
{
    public static readonly string[] Defaults =
    {
        "Productivity", "Education", "Games", "Entertainment", "Media", "Communication",
        "Development", "Utilities", "Finance", "Graphics & Design",
    };

    public static IEnumerable<string> Suggestions(IEnumerable<string> existing) =>
        Defaults.Concat(existing).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).Distinct(StringComparer.CurrentCultureIgnoreCase);

    public static string Normalize(string? input, IEnumerable<string> existing)
    {
        var s = (input ?? "").Trim();
        if (s.Length == 0) return "";
        return Defaults.Concat(existing).FirstOrDefault(c => string.Equals(c?.Trim(), s, StringComparison.CurrentCultureIgnoreCase))?.Trim() ?? s;
    }
}

/// <summary>URL rules for Web Apps (the shared WebAppTransfer code calls WebAppDialog.NormalizeUrl, like on Windows).</summary>
public static class WebAppDialog
{
    /// <summary>Absolute http(s) URL; adds https:// when no scheme was typed. Null (plus a message) for invalid input.</summary>
    public static string? NormalizeUrl(string? input, out string error)
    {
        error = "";
        var s = (input ?? "").Trim();
        if (s.Length == 0) { error = "Enter a web address."; return null; }
        if (s.Any(char.IsWhiteSpace)) { error = "A web address can't contain spaces."; return null; }
        if (!s.Contains("://")) s = "https://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "That isn't a valid web address. Try something like example.com or https://example.com.";
            return null;
        }
        var host = uri.Host;
        var hostOk = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                     || IPAddress.TryParse(host.Trim('[', ']'), out _)
                     || (host.Contains('.') && !host.StartsWith('.') && !host.EndsWith('.') && !host.Contains(".."));
        if (!hostOk) { error = $"\"{host}\" doesn't look like a website address. Include the domain, e.g. {host}.com."; return null; }
        return uri.AbsoluteUri;
    }

    public static string SuggestName(string host)
    {
        var h = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        var first = h.Split('.')[0];
        return first.Length > 0 ? char.ToUpper(first[0]) + first[1..] : h;
    }
}
