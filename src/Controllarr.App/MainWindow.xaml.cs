using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using Controllarr.App.ViewModels;
using MessageBox = System.Windows.MessageBox;

namespace Controllarr.App;

public partial class MainWindow : Window
{
    private bool _shutdownStarted;
    private readonly MainViewModel _viewModel;

    public MainWindow() : this(startRuntime: true) { }

    public MainWindow(bool startRuntime)
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        TrayIcon.DataContext = _viewModel;
        SourceInitialized += (_, _) =>
        {
            int enabled = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref enabled, sizeof(int));
        };
        Loaded += async (_, _) =>
        {
            if (!startRuntime) return;
            await _viewModel.BootAsync();
            if (_viewModel.BootError == null) Workspace.RestoreLayout();
            if (System.Windows.Application.Current is App app && (app.StartMinimized || _viewModel.Settings.UiPreferences.StartMinimized)) Hide();
        };
        Closing += (_, e) =>
        {
            Workspace.SaveLayout();
            if (!_shutdownStarted)
            {
                e.Cancel = true;
                if (_viewModel.Settings.UiPreferences.CloseToTray) Hide();
                else BeginShutdown();
            }
        };
        // Normal minimization stays on the taskbar; explicit Hide and close use the tray.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            { e.Handled = true; Find_Click(this, new RoutedEventArgs()); }
        };
    }

    private void Find_Click(object sender, RoutedEventArgs e)
    { _viewModel.SelectedTab = "Torrents"; Workspace.FocusSearch(); }
    private void SelectAll_Click(object sender, RoutedEventArgs e)
    { _viewModel.SelectedTab = "Torrents"; Workspace.SelectAllTorrents(); }
    private void HideToTray_Click(object sender, RoutedEventArgs e) => Hide();
    private void AllCategories_Click(object sender, RoutedEventArgs e)
    { CategoryList.SelectedItem = null; _viewModel.Desktop.SetCategoryFilter(null); }
    private void Uncategorized_Click(object sender, RoutedEventArgs e)
    { CategoryList.SelectedItem = null; _viewModel.Desktop.SetCategoryFilter(""); }
    private void CategoryList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    { if (CategoryList.SelectedItem is string category) _viewModel.Desktop.SetCategoryFilter(category); }
    private void Help_Click(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo("https://github.com/eMacTh3Creator/Controllarr-Windows/blob/main/docs/DESKTOP.md") { UseShellExecute = true });
    private void About_Click(object sender, RoutedEventArgs e)
        => MessageBox.Show(this, $"Controllarr {_viewModel.AppVersion}\nNative Windows transfer workspace\nMonoTorrent / WPF\n\nClosing keeps torrents running in the tray. File > Exit stops the app.", "About Controllarr", MessageBoxButton.OK, MessageBoxImage.Information);
    private void TrayIcon_TrayMouseDoubleClick(object sender, RoutedEventArgs e) => RestoreFromTray();
    private void TrayShow_Click(object sender, RoutedEventArgs e) => RestoreFromTray();
    private void TrayPause_Click(object sender, RoutedEventArgs e)
    { if (_viewModel.Desktop.PauseAllCommand.CanExecute(null)) _viewModel.Desktop.PauseAllCommand.Execute(null); }
    private void TrayResume_Click(object sender, RoutedEventArgs e)
    { if (_viewModel.Desktop.ResumeAllCommand.CanExecute(null)) _viewModel.Desktop.ResumeAllCommand.Execute(null); }
    private void TrayOpenWebUI_Click(object sender, RoutedEventArgs e) => _viewModel.OpenWebUICommand.Execute(null);
    private void TrayCheckForUpdates_Click(object sender, RoutedEventArgs e) => _viewModel.CheckForUpdatesCommand.Execute(null);
    private void TrayExit_Click(object sender, RoutedEventArgs e) => BeginShutdown();
    public void ShutdownFromUi() => BeginShutdown();
    private void RestoreFromTray() { Show(); WindowState = WindowState.Normal; Activate(); }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    private async void BeginShutdown()
    {
        if (_shutdownStarted) return;
        _shutdownStarted = true;
        Workspace.SaveLayout();
        _viewModel.Desktop.CancelBatchCommand.Execute(null);
        var watchdog = new Thread(() => { Thread.Sleep(TimeSpan.FromSeconds(30)); Environment.Exit(0); })
        { IsBackground = true, Name = "ExitWatchdog" };
        watchdog.Start();
        try { await _viewModel.Desktop.WaitForIdleAsync(); await _viewModel.ShutdownAsync(); }
        catch (Exception ex) { Core.Services.Logger.Instance.Error("Shutdown", ex.ToString()); }
        try { TrayIcon.Dispose(); } catch { }
        System.Windows.Application.Current.Shutdown();
    }
}
