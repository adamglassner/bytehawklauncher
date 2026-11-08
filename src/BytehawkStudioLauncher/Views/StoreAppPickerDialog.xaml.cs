using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Bytehawk.Launcher.Services;

namespace Bytehawk.Launcher.Views;

/// <summary>Lists installed Microsoft Store / packaged apps so one can be added to My Software.</summary>
public partial class StoreAppPickerDialog : Window
{
    private List<StoreAppInfo> _all = new();

    public StoreAppInfo? Result { get; private set; }

    public StoreAppPickerDialog()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);
        Loaded += async (_, _) =>
        {
            SearchBox.Focus();
            try
            {
                _all = await StoreApps.ListInstalledAsync();
                ApplyFilter();
            }
            catch (Exception ex)
            {
                Log.Error("List Store apps", ex);
                StatusText.Text = $"Couldn't list installed apps: {ex.Message}";
            }
        };
    }

    private void ApplyFilter()
    {
        var q = SearchBox.Text.Trim();
        var items = q.Length == 0 ? _all : _all.Where(a => a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        AppList.ItemsSource = items;
        StatusText.Text = _all.Count == 0 ? "No Microsoft Store apps were found on this PC."
            : q.Length == 0 ? $"{_all.Count} apps installed." : $"{items.Count} of {_all.Count} apps match.";
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) { if (IsLoaded) ApplyFilter(); }

    private void AppList_SelectionChanged(object sender, SelectionChangedEventArgs e) => OkButton.IsEnabled = AppList.SelectedItem != null;

    private void AppList_DoubleClick(object sender, MouseButtonEventArgs e) { if (AppList.SelectedItem != null) Ok_Click(sender, e); }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (AppList.SelectedItem is not StoreAppInfo app) return;
        Result = app;
        DialogResult = true;
    }
}
