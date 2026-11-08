using System.Windows;
using System.Windows.Controls;

namespace Bytehawk.Launcher.Views;

/// <summary>Categories for My Software and Web Apps: a suggested list plus whatever the user has typed before.</summary>
public static class CategoryPicker
{
    public static readonly string[] Defaults =
    {
        "Productivity", "Education", "Games", "Entertainment", "Media", "Communication",
        "Development", "Utilities", "Finance", "Graphics & Design",
    };

    /// <summary>Fills <paramref name="chips"/> with buttons (suggested + already-used categories) that set <paramref name="box"/>.</summary>
    public static void Fill(WrapPanel chips, TextBox box, IEnumerable<string>? existing)
    {
        chips.Children.Clear();
        foreach (var name in Defaults.Concat(existing ?? Enumerable.Empty<string>())
                     .Where(c => !string.IsNullOrWhiteSpace(c))
                     .Select(c => c.Trim())
                     .Distinct(StringComparer.CurrentCultureIgnoreCase))
        {
            var b = new Button { Content = name, Padding = new Thickness(9, 3, 9, 3), FontSize = 11, Margin = new Thickness(0, 0, 5, 5) };
            b.Click += (_, _) => { box.Text = name; box.Focus(); box.CaretIndex = box.Text.Length; };
            chips.Children.Add(b);
        }
    }

    /// <summary>Trims the input and reuses the casing of a known category ("games" → "Games").</summary>
    public static string Normalize(string? input, IEnumerable<string>? existing)
    {
        var s = (input ?? "").Trim();
        if (s.Length == 0) return "";
        var match = Defaults.Concat(existing ?? Enumerable.Empty<string>())
            .FirstOrDefault(c => string.Equals(c?.Trim(), s, StringComparison.CurrentCultureIgnoreCase));
        return match?.Trim() ?? s;
    }
}
