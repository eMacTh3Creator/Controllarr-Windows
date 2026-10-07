using Controllarr.Core.Desktop;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;
using Brush = System.Windows.Media.Brush;
using Orientation = System.Windows.Controls.Orientation;

namespace Controllarr.App.Views;

public sealed record AddMagnetRequest(string Uri, string Category, string SavePath);

internal static partial class DesktopDialogs
{
    private static (Window Window, StackPanel Body) Create(string title)
    {
        var body = new StackPanel { Margin = new Thickness(24) };
        var window = new Window
        {
            Title = title, Owner = Application.Current.MainWindow, Width = 560,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Background = (Brush)Application.Current.FindResource("SurfaceBrush"),
            Foreground = (Brush)Application.Current.FindResource("TextBrush"), Content = body
        };
        body.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });
        return (window, body);
    }

    private static void Label(StackPanel body, string text)
        => body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 6) });

    private static void Buttons(Window window, StackPanel body, string action, Action confirm, bool danger = false)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = danger, Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = action, IsDefault = !danger, Style = (Style)Application.Current.FindResource(danger ? "DangerButton" : "AccentButton") };
        ok.Click += (_, _) => confirm();
        bar.Children.Add(cancel); bar.Children.Add(ok); body.Children.Add(bar);
    }

    public static bool? ConfirmRemoval(IReadOnlyList<TorrentRow> rows)
    {
        var (window, body) = Create($"Remove {rows.Count:N0} torrent{(rows.Count == 1 ? "" : "s")}");
        Label(body, string.Join(Environment.NewLine, rows.Take(4).Select(r => r.Name)) + (rows.Count > 4 ? $"\n...and {rows.Count - 4:N0} more" : ""));
        Label(body, "Choose what happens to the downloaded files. This affects only the selected torrents.");
        var keep = new RadioButton { Content = "Remove from Controllarr only (keep downloaded files)", IsChecked = true, GroupName = "Removal", Margin = new Thickness(0, 12, 0, 12) };
        var delete = new RadioButton { Content = "Remove from Controllarr AND permanently delete files", GroupName = "Removal" };
        body.Children.Add(keep); body.Children.Add(delete);
        Label(body, "Permanent deletion does not use the Recycle Bin and cannot be undone. Other applications may be using these files.");
        bool? result = null;
        Buttons(window, body, "Remove selected", () =>
        {
            if (delete.IsChecked == true && System.Windows.MessageBox.Show(window,
                    $"Permanently delete downloaded files for {rows.Count:N0} torrents? This cannot be undone.",
                    "Confirm permanent deletion", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            result = delete.IsChecked == true;
            window.DialogResult = true;
        }, true);
        window.ShowDialog();
        return result;
    }

    public static string? ChooseCategory(IEnumerable<string> categories)
    {
        var (window, body) = Create("Assign category");
        Label(body, "Apply a category to all selected torrents. This changes the label, not their current storage location.");
        var picker = new ComboBox { ItemsSource = new[] { "(Uncategorized)" }.Concat(categories).ToArray(), SelectedIndex = 0, MinWidth = 300 };
        body.Children.Add(picker);
        string? result = null;
        Buttons(window, body, "Apply category", () => { result = picker.SelectedIndex == 0 ? "" : picker.SelectedItem as string; window.DialogResult = true; });
        window.ShowDialog();
        return result;
    }

    public static AddMagnetRequest? AddMagnet(IEnumerable<string> categories, string defaultPath)
    {
        var (window, body) = Create("Add magnet link");
        Label(body, "Magnet URI");
        var uri = new TextBox { MinHeight = 64, TextWrapping = TextWrapping.Wrap, AcceptsReturn = false };
        body.Children.Add(uri);
        Label(body, "Category");
        var category = new ComboBox { ItemsSource = new[] { "(Uncategorized)" }.Concat(categories).ToArray(), SelectedIndex = 0 };
        body.Children.Add(category);
        Label(body, "Save folder (leave blank to use the category/default folder)");
        var path = new TextBox { ToolTip = defaultPath };
        body.Children.Add(path);
        var validation = new TextBlock { Foreground = (Brush)Application.Current.FindResource("ErrorBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        body.Children.Add(validation);
        AddMagnetRequest? result = null;
        Buttons(window, body, "Start download", () =>
        {
            if (!System.Uri.TryCreate(uri.Text.Trim(), UriKind.Absolute, out var parsed) || parsed.Scheme != "magnet")
            { validation.Text = "Enter a valid magnet: link."; return; }
            if (!string.IsNullOrWhiteSpace(path.Text) && !System.IO.Path.IsPathFullyQualified(path.Text.Trim()))
            { validation.Text = "Use an absolute save folder path."; return; }
            result = new(uri.Text.Trim(), category.SelectedIndex == 0 ? "" : (string)category.SelectedItem, path.Text.Trim());
            window.DialogResult = true;
        });
        window.Loaded += (_, _) => uri.Focus();
        window.ShowDialog();
        return result;
    }
}
