using System.IO;
using System.Windows;
using System.Windows.Controls;
using Controllarr.Core.Engine;
using Application = System.Windows.Application;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;

namespace Controllarr.App.Views;

public sealed record CreateTorrentRequest(string Source, string Destination, string[] Trackers, bool Private, string Comment, bool Hybrid);
public sealed record MigrationRequest(string MetadataPath, string SavePath);

internal static partial class DesktopDialogs
{
    public static int? ChoosePreviewFile(Controllarr.Core.Engine.FileInfo[] files)
    {
        var (window, body) = Create("Preview a torrent file");
        Label(body, "Choose a file with its download priority enabled. The torrent must already be running. Seeking prioritizes missing pieces; playback support depends on your browser or media player.");
        var chooser = new System.Windows.Controls.ListBox { ItemsSource = files.Where(f => f.Priority > 0).ToArray(), DisplayMemberPath = "Name", Height = 220, SelectedIndex = 0 };
        body.Children.Add(chooser);
        int? result = null;
        Buttons(window, body, "Start preview", () =>
        {
            if (chooser.SelectedItem is not Controllarr.Core.Engine.FileInfo file) return;
            result = file.Index; window.DialogResult = true;
        });
        window.ShowDialog(); return result;
    }

    public static void ShowPreview(Controllarr.Core.Services.TorrentPreview preview)
    {
        var (window, body) = Create("Streaming file preview");
        Label(body, preview.FileName);
        Label(body, "This temporary URL is reachable only on this Windows machine. Open it in a browser or VLC (Media > Open Network Stream). Keep this window open while playing; closing it stops the server and restores the normal piece picker. Playback may buffer while pieces download.");
        var url = Field(body, "Private local playback URL", preview.Url);
        url.IsReadOnly = true;
        var bar = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var copy = new Button { Content = "Copy playback URL", Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) => System.Windows.Clipboard.SetText(preview.Url);
        var open = new Button { Content = "Open in browser" };
        open.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(preview.Url) { UseShellExecute = true }); }
            catch (Exception ex) { Error(window, ex); }
        };
        bar.Children.Add(copy); bar.Children.Add(open); body.Children.Add(bar);
        Buttons(window, body, "Stop preview", () => window.DialogResult = true);
        window.ShowDialog();
    }

    public static (int? Connections, int? Slots, bool Sequential)? AdvancedOptions(TorrentOptions current)
    {
        var (window, body) = Create("Advanced torrent controls");
        Label(body, "Apply to every selected torrent. Blank values inherit the Settings defaults. Sequential downloads keep file priorities but replace rarest-first picking; this can reduce swarm efficiency. Super-seeding is not supported by this engine.");
        var connections = Field(body, "Connection budget (1-10000)", current.MaximumConnections?.ToString() ?? "");
        var slots = Field(body, "Upload slots (2-1000)", current.UploadSlots?.ToString() ?? "");
        var sequential = new CheckBox { Content = "Download pieces in sequence", IsChecked = current.Sequential, Margin = new Thickness(0, 16, 0, 0) };
        body.Children.Add(sequential);
        (int?, int?, bool)? result = null;
        Buttons(window, body, "Apply controls", () =>
        {
            int? Parse(string text, int minimum, int maximum)
            {
                if (string.IsNullOrWhiteSpace(text)) return null;
                if (!int.TryParse(text, out int value) || value < minimum || value > maximum) throw new ArgumentException($"Enter a whole number from {minimum} to {maximum}, or leave blank.");
                return value;
            }
            try { result = (Parse(connections.Text, 1, 10000), Parse(slots.Text, 2, 1000), sequential.IsChecked == true); window.DialogResult = true; }
            catch (Exception ex) { Error(window, ex); }
        });
        window.ShowDialog(); return result;
    }

    private static TextBox Field(StackPanel body, string label, string text = "", bool multiline = false)
    {
        Label(body, label);
        var input = new TextBox { Text = text, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight = multiline ? 100 : 0, MaxHeight = multiline ? 150 : double.PositiveInfinity, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        body.Children.Add(input);
        return input;
    }
    private static void Error(Window window, Exception ex) => System.Windows.MessageBox.Show(window, ex.Message, "Check these values", MessageBoxButton.OK, MessageBoxImage.Warning);

    public static (int Download, int Upload)? SpeedLimits(TorrentOptions current)
    {
        var (window, body) = Create("Torrent speed limits");
        Label(body, "Apply to all selected torrents. Values are KiB/s; 0 means unlimited. Global limits still apply.");
        var down = Field(body, "Download limit", current.DownloadKBps.ToString());
        var up = Field(body, "Upload limit", current.UploadKBps.ToString());
        (int, int)? result = null;
        Buttons(window, body, "Apply limits", () =>
        {
            if (!int.TryParse(down.Text, out int d) || !int.TryParse(up.Text, out int u) || d is < 0 or > 1000000 || u is < 0 or > 1000000)
            { Error(window, new ArgumentException("Enter whole numbers from 0 to 1000000.")); return; }
            result = (d, u); window.DialogResult = true;
        });
        window.ShowDialog(); return result;
    }

    public static string[]? EditTrackers(IEnumerable<string> current)
    {
        var (window, body) = Create("Edit trackers");
        Label(body, "One HTTP, HTTPS or UDP URL per line. This replaces the tracker list for the selected public torrent. Private torrent trackers cannot be changed.");
        var text = Field(body, "Tracker URLs", string.Join(Environment.NewLine, current), true);
        string[]? result = null;
        Buttons(window, body, "Save trackers", () =>
        {
            try { result = TorrentEngine.ValidateTrackerUrls(text.Text.Split('\n')); window.DialogResult = true; }
            catch (Exception ex) { Error(window, ex); }
        });
        window.ShowDialog(); return result;
    }

    public static CreateTorrentRequest? CreateTorrent()
    {
        var (window, body) = Create("Create a torrent");
        Label(body, "Hash a file or folder in the background. Creating metadata does not start seeding; add the resulting .torrent when ready.");
        var source = Field(body, "Source file or folder");
        var bar = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var file = new Button { Content = "Choose file", Margin = new Thickness(0, 0, 8, 0) };
        file.Click += (_, _) => { var picker = new Microsoft.Win32.OpenFileDialog(); if (picker.ShowDialog(window) == true) source.Text = picker.FileName; };
        var folder = new Button { Content = "Choose folder" };
        folder.Click += (_, _) => { using var picker = new System.Windows.Forms.FolderBrowserDialog(); if (picker.ShowDialog() == System.Windows.Forms.DialogResult.OK) source.Text = picker.SelectedPath; };
        bar.Children.Add(file); bar.Children.Add(folder); body.Children.Add(bar);
        var trackers = Field(body, "Trackers (one per line)", "", true);
        var comment = Field(body, "Comment (optional)");
        var privateBox = new CheckBox { Content = "Private torrent (requires a tracker)", Margin = new Thickness(0, 12, 0, 0) };
        var hybrid = new CheckBox { Content = "Hybrid BitTorrent v1 + v2 metadata", Margin = new Thickness(0, 12, 0, 0), IsChecked = true };
        body.Children.Add(privateBox); body.Children.Add(hybrid);
        CreateTorrentRequest? result = null;
        Buttons(window, body, "Choose output and create", () =>
        {
            try
            {
                if (!File.Exists(source.Text) && !Directory.Exists(source.Text)) throw new ArgumentException("Choose an existing source file or folder.");
                var urls = TorrentEngine.ValidateTrackerUrls(trackers.Text.Split('\n'));
                if (privateBox.IsChecked == true && urls.Length == 0) throw new ArgumentException("Private torrents require a tracker.");
                var picker = new Microsoft.Win32.SaveFileDialog { Filter = "Torrent metadata (*.torrent)|*.torrent", DefaultExt = ".torrent", FileName = Path.GetFileName(source.Text.TrimEnd(Path.DirectorySeparatorChar)) + ".torrent", OverwritePrompt = false };
                if (picker.ShowDialog(window) != true) return;
                if (File.Exists(picker.FileName)) throw new IOException("Choose a new output filename; existing files will not be overwritten.");
                result = new(source.Text, picker.FileName, urls, privateBox.IsChecked == true, comment.Text, hybrid.IsChecked == true);
                window.DialogResult = true;
            }
            catch (Exception ex) { Error(window, ex); }
        });
        window.ShowDialog(); return result;
    }

    public static MigrationRequest? ImportMigration(string defaultPath)
    {
        var (window, body) = Create("Import from another torrent client");
        Label(body, "Choose uTorrent/BitTorrent resume.dat with its adjacent .torrent files, or a folder of .torrent files (qBittorrent BT_backup is supported). Originals are not modified. All imports stay paused; recheck existing files before resuming.");
        var metadata = Field(body, "Metadata file or folder");
        var bar = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var file = new Button { Content = "Choose resume.dat / .torrent", Margin = new Thickness(0, 0, 8, 0) };
        file.Click += (_, _) => { var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Torrent metadata|resume.dat;*.torrent|All files|*.*" }; if (picker.ShowDialog(window) == true) metadata.Text = picker.FileName; };
        var folder = new Button { Content = "Choose folder" };
        folder.Click += (_, _) => { using var picker = new System.Windows.Forms.FolderBrowserDialog(); if (picker.ShowDialog() == System.Windows.Forms.DialogResult.OK) metadata.Text = picker.SelectedPath; };
        bar.Children.Add(file); bar.Children.Add(folder); body.Children.Add(bar);
        var save = Field(body, "Fallback folder containing existing downloads", defaultPath);
        MigrationRequest? result = null;
        Buttons(window, body, "Preview import", () =>
        {
            if ((!File.Exists(metadata.Text) && !Directory.Exists(metadata.Text)) || !Path.IsPathFullyQualified(save.Text))
            { Error(window, new ArgumentException("Choose existing metadata and an absolute download folder.")); return; }
            result = new(metadata.Text, save.Text); window.DialogResult = true;
        });
        window.ShowDialog(); return result;
    }
}
