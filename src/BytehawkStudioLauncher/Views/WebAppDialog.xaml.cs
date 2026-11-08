using System.Net;
using System.Windows;
using Bytehawk.Launcher.Models;
using Bytehawk.Launcher.Services;

namespace Bytehawk.Launcher.Views;

public partial class WebAppDialog : Window
{
    public WebAppEntry Result { get; private set; } = new();
    private readonly WebAppEntry? _editing;

    private readonly IEnumerable<string>? _categories;

    public WebAppDialog(WebAppEntry? editing = null, IEnumerable<string>? categories = null)
    {
        InitializeComponent();
        _categories = categories;
        CategoryPicker.Fill(CategoryChips, CategoryBox, categories);
        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);
        _editing = editing;
        if (editing != null)
        {
            Title = Heading.Text = "Edit web app";
            OkButton.Content = "Save";
            UrlBox.Text = editing.Url;
            NameBox.Text = editing.Name;
            DescriptionBox.Text = editing.Description;
            CategoryBox.Text = editing.Category;
        }
        Loaded += (_, _) => UrlBox.Focus();
    }

    /// <summary>
    /// Normalizes user input to an absolute http(s) URL. Adds https:// when no scheme was typed.
    /// Returns null (and an error message) for obviously invalid input.
    /// </summary>
    public static string? NormalizeUrl(string? input, out string error)
    {
        error = "";
        var s = (input ?? "").Trim();
        if (s.Length == 0) { error = "Enter a web address."; return null; }
        if (s.Any(char.IsWhiteSpace)) { error = "A web address can't contain spaces."; return null; }
        if (!s.Contains("://")) s = "https://" + s;

        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "That isn't a valid web address. Try something like example.com or https://example.com.";
            return null;
        }
        var host = uri.Host;
        var hostOk = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                     || IPAddress.TryParse(host.Trim('[', ']'), out _)
                     || (host.Contains('.') && !host.StartsWith('.') && !host.EndsWith('.') && !host.Contains(".."));
        if (!hostOk)
        {
            error = $"\"{host}\" doesn't look like a website address. Include the domain, e.g. {host}.com.";
            return null;
        }
        return uri.AbsoluteUri;
    }

    private void UrlBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var url = NormalizeUrl(UrlBox.Text, out _);
        if (url != null && string.IsNullOrWhiteSpace(NameBox.Text))
            NameBox.Text = SuggestName(new Uri(url).Host);
    }

    private static string SuggestName(string host)
    {
        var h = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        var first = h.Split('.')[0];
        return first.Length > 0 ? char.ToUpper(first[0]) + first[1..] : h;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var url = NormalizeUrl(UrlBox.Text, out var error);
        if (url == null) { ShowError(error); return; }
        UrlBox.Text = url;
        var name = string.IsNullOrWhiteSpace(NameBox.Text) ? SuggestName(new Uri(url).Host) : NameBox.Text.Trim();
        Result = new WebAppEntry
        {
            Id = _editing?.Id ?? Guid.NewGuid().ToString("N"),
            AddedAt = _editing?.AddedAt ?? DateTime.Now,
            Name = name,
            Url = url,
            Description = DescriptionBox.Text.Trim(),
            Category = CategoryPicker.Normalize(CategoryBox.Text, _categories),
        };
        DialogResult = true;
    }

    private void ShowError(string msg) { ErrorText.Text = msg; ErrorText.Visibility = Visibility.Visible; }
}
