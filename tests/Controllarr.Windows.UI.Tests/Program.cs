using System.Windows;
using System.Windows.Controls;
using Controllarr.App;
using Controllarr.App.ViewModels;
using Controllarr.App.Views;
using Controllarr.Core.Engine;
using Controllarr.App.Helpers;
using System.Windows.Threading;
using System.Windows.Media;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Generic Application avoids App.OnStartup, profile access and protocol registration.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Controllarr;component/Themes/Dark.xaml") });
        app.Resources["BoolToVis"] = new BooleanToVisibilityConverter();
        app.Resources["SizeConverter"] = new Controllarr.App.Helpers.SizeConverter();
        app.Resources["SpeedConverter"] = new SpeedConverter();
        app.Resources["EtaConverter"] = new EtaConverter();
        app.Resources["ProgressPercentConverter"] = new ProgressPercentConverter();
        app.Resources["StringListConverter"] = new StringListConverter();
        var window = new MainWindow(startRuntime: false);
        var vm = (MainViewModel)window.DataContext;
        vm.IsBooting = false;
        // Show only a synthetic window; explicitly never boot the runtime or access any profile.
        window.Show();
        Layout(window);
        var workspace = (TorrentWorkspace)window.FindName("Workspace");
        var grid = (DataGrid)workspace.FindName("TorrentGrid");
        var snapshots = Enumerable.Range(0, 10000).Select(i => new TorrentStats
        {
            InfoHash = i.ToString("x40"), Name = $"Torrent {i:D5}", Category = i % 2 == 0 ? "Movies" : "TV",
            State = TorrentState.Downloading, TotalWanted = 1024, TotalDone = 512, Progress = .5f
        }).ToArray();
        vm.Desktop.ApplySnapshot(snapshots, new[] { "Movies", "TV" });
        Layout(window);
        int realized = CountRows(grid);
        Console.WriteLine($"VIRTUALIZATION CHECK: {realized} realized rows / {grid.Items.Count} items");
        Check(realized > 0 && realized < 200, "10,000-row table realizes only the visible viewport");
        grid.SelectedItems.Add(grid.Items[0]); grid.SelectedItems.Add(grid.Items[2]);
        var selected = grid.SelectedItems.Cast<object>().ToArray();
        vm.Desktop.ApplySnapshot(snapshots, new[] { "Movies", "TV" });
        Check(grid.SelectedItems.Count == 2 && selected.All(grid.SelectedItems.Contains), "multi-selection survives unchanged polls");
        var changed = snapshots.Select(s => new TorrentStats
        {
            InfoHash = s.InfoHash, Name = s.Name, Category = s.Category,
            State = TorrentState.Seeding, TotalWanted = 1024, TotalDone = 1024, Progress = 1
        }).ToArray();
        vm.Desktop.ApplySnapshot(changed, new[] { "Movies", "TV" });
        Check(grid.SelectedItems.Count == 2 && selected.All(grid.SelectedItems.Contains), "multi-selection survives membership refresh");
        vm.Desktop.SetCategoryFilter("TV");
        Layout(window);
        Console.WriteLine($"FILTER CHECK: grid={grid.Items.Count}, selected={grid.SelectedItems.Count}, model={vm.Desktop.SelectedRows.Count}, view={vm.Desktop.VisibleCount}, context={workspace.DataContext?.GetType().Name}");
        Check(grid.Items.Count == 5000 && grid.SelectedItems.Count == 0 && vm.Desktop.SelectedRows.Count == 0,
            "category filtering never leaves hidden torrents selected");
        Check(!vm.Desktop.RemoveSelectedCommand.CanExecute(null), "destructive commands disabled without selection/runtime");
        grid.SelectedItems.Add(grid.Items[0]);
        string hash = vm.Desktop.SelectedRows[0].InfoHash;
        vm.Desktop.ApplySnapshot(changed.Select(s => new TorrentStats
        {
            InfoHash = s.InfoHash, Name = s.Name, Category = s.Category, State = s.State,
            TotalWanted = s.TotalWanted, TotalDone = s.TotalDone, Progress = s.Progress,
            StatusReason = s.InfoHash == hash ? "Global connection limit reached (200/200)" : "Seeding"
        }).ToArray(), new[] { "Movies", "TV" });
        Layout(window);
        Check(vm.Desktop.SelectedRow?.StatusReason.Contains("200/200") == true && ContainsText(workspace, "Global connection limit reached (200/200)"),
            "selected-torrent connection diagnosis updates visibly without losing selection");
        string? oldProfile = Environment.GetEnvironmentVariable(Controllarr.Core.Persistence.ProfilePaths.EnvironmentVariable);
        string layoutProfile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ControllarrLayoutTests", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(Controllarr.Core.Persistence.ProfilePaths.EnvironmentVariable, layoutProfile);
        workspace.RestoreLayout();
        grid.Columns[1].Width = new DataGridLength(143);
        grid.Columns[1].DisplayIndex = 3;
        workspace.SaveLayout();
        grid.Columns[1].Width = new DataGridLength(95);
        grid.Columns[1].DisplayIndex = 1;
        workspace.RestoreLayout();
        Check(grid.Columns[1].Width.Value == 143 && grid.Columns[1].DisplayIndex == 3, "column widths and order restore from an isolated layout profile");
        foreach (string page in new[] { "Home", "Categories", "Settings", "Health", "Recovery", "PostProcessor", "Seeding", "Arr", "Log", "Vpn", "DiskSpace", "Rss", "Torrents" })
        {
            vm.SelectedTab = page; Layout(window);
            Console.WriteLine($"PASS native {page} page loads");
        }
        vm.AddCategoryCommand.Execute(null);
        Check(vm.SelectedCategory?.CreateTorrentSubfolder == true, "new native categories start with torrent subfolders enabled");
        vm.SelectedTab = "Categories"; Layout(window);
        var categoryFolder = FindCheckbox(window, "Create a subfolder for each new torrent")
            ?? throw new Exception("Missing native category subfolder checkbox");
        categoryFolder.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, false);
        Check(vm.SelectedCategory!.CreateTorrentSubfolder == false, "category folder checkbox updates its category model");
        vm.SelectedTab = "Settings"; Layout(window);
        var defaultFolder = FindCheckbox(window, "Create per-torrent subfolders for uncategorized downloads")
            ?? throw new Exception("Missing native default subfolder checkbox");
        defaultFolder.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
        Check(vm.Settings.CreateTorrentSubfolders, "uncategorized folder checkbox updates the settings model");
        Console.WriteLine("Windows XAML/selection checks passed. No engine was started and no files were deleted.");
        (window.FindName("TrayIcon") as IDisposable)?.Dispose();
        window.Hide();
        app.Shutdown();
        Environment.SetEnvironmentVariable(Controllarr.Core.Persistence.ProfilePaths.EnvironmentVariable, oldProfile);
    }
    private static void Layout(Window window)
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        window.Measure(new Size(1440, 900));
        window.Arrange(new Rect(0, 0, 1440, 900));
        window.UpdateLayout();
    }
    private static void Check(bool success, string message)
    {
        if (!success) throw new Exception(message);
        Console.WriteLine($"PASS {message}");
    }
    private static int CountRows(DependencyObject node)
    {
        int count = node is DataGridRow ? 1 : 0;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) count += CountRows(VisualTreeHelper.GetChild(node, i));
        return count;
    }
    private static bool ContainsText(DependencyObject node, string text)
    {
        if (node is TextBlock block && block.Text == text) return true;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (ContainsText(VisualTreeHelper.GetChild(node, i), text)) return true;
        return false;
    }
    private static CheckBox? FindCheckbox(DependencyObject node, string text)
    {
        if (node is CheckBox checkbox && Equals(checkbox.Content, text)) return checkbox;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (FindCheckbox(VisualTreeHelper.GetChild(node, i), text) is { } found) return found;
        return null;
    }
}
