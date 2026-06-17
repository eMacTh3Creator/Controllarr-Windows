using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Controllarr.App.ViewModels;
using MessageBox = System.Windows.MessageBox;

namespace Controllarr.App
{
    public partial class MainWindow : Window
    {
        private bool _shutdownStarted;
        private readonly MainViewModel _viewModel;

        public MainWindow()
        {
            InitializeComponent();

            _viewModel = new MainViewModel();
            DataContext = _viewModel;

            // Hardcodet forwards the TaskbarIcon's OWN DataContext into the
            // TrayToolTip (it does NOT inherit the Window's), so set it here for
            // the live hover-tooltip bindings to resolve.
            TrayIcon.DataContext = _viewModel;

            // Dark default so there is no white flash before the page paints.
            WebHost.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 10, 14, 20);

            Loaded += async (_, _) =>
            {
                // 1. Start the engine + embedded HTTP server (serves the web UI).
                await _viewModel.BootAsync();

                // 2. Point the embedded browser at the local web UI.
                await InitializeWebViewAsync();

                // If launched with --minimized (e.g. startup), hide to tray.
                if (System.Windows.Application.Current is App app && app.StartMinimized)
                {
                    Hide();
                }
            };

            Closing += OnWindowClosing;

            // Minimize to tray via the standard minimize button.
            StateChanged += (_, _) =>
            {
                if (WindowState == WindowState.Minimized)
                {
                    Hide();
                }
            };
        }

        // ────────────────────────────────────────────────────────────
        // WebView2 host
        // ────────────────────────────────────────────────────────────

        private async Task InitializeWebViewAsync()
        {
            try
            {
                // Keep the WebView2 profile under the app's state directory.
                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Controllarr", "WebView2");
                Directory.CreateDirectory(userDataFolder);

                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await WebHost.EnsureCoreWebView2Async(env);

                // Tidy the embedded browser chrome — this is an app, not a browser.
                var s = WebHost.CoreWebView2.Settings;
                s.AreDefaultContextMenusEnabled = false;
                s.IsStatusBarEnabled = false;
                s.AreBrowserAcceleratorKeysEnabled = false;
                s.IsZoomControlEnabled = true;

                int port = (System.Windows.Application.Current as App)?.Store?.GetSettings().WebUIPort ?? 8791;
                // Always use loopback for the local app, even if the server binds 0.0.0.0 for LAN.
                WebHost.CoreWebView2.Navigate($"http://127.0.0.1:{port}/");
            }
            catch (Exception ex)
            {
                int port = (System.Windows.Application.Current as App)?.Store?.GetSettings().WebUIPort ?? 8791;
                MessageBox.Show(
                    "Could not start the embedded web interface (WebView2).\n\n" +
                    $"{ex.Message}\n\n" +
                    "Make sure the Microsoft Edge WebView2 Runtime is installed, or open " +
                    $"http://127.0.0.1:{port} in your browser.",
                    "Controllarr", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ────────────────────────────────────────────────────────────
        // Tray menu handlers (Click-based — no DataContext dependency)
        // ────────────────────────────────────────────────────────────

        private void TrayIcon_TrayMouseDoubleClick(object sender, RoutedEventArgs e) => RestoreFromTray();

        private void TrayShow_Click(object sender, RoutedEventArgs e) => RestoreFromTray();

        private void TrayOpenWebUI_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.OpenWebUICommand.CanExecute(null))
                _viewModel.OpenWebUICommand.Execute(null);
        }

        private void TrayCheckForUpdates_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.CheckForUpdatesCommand.CanExecute(null))
                _viewModel.CheckForUpdatesCommand.Execute(null);
        }

        private void TrayExit_Click(object sender, RoutedEventArgs e) => BeginShutdown();

        /// <summary>Fully exit the app (used by the Web UI "Shut down" button).</summary>
        public void ShutdownFromUi() => BeginShutdown();

        private void RestoreFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        // ────────────────────────────────────────────────────────────
        // Window close / shutdown
        // ────────────────────────────────────────────────────────────

        // Window 'X' / minimize-close: hide to tray instead of exiting, unless
        // a real shutdown is already running via BeginShutdown().
        private void OnWindowClosing(object? sender, CancelEventArgs e)
        {
            if (!_shutdownStarted)
            {
                e.Cancel = true;
                Hide();
            }
        }

        // Single, reliable exit path: re-entrancy guard, await engine/server
        // shutdown, dispose the tray icon, then Application.Shutdown(). A
        // watchdog guarantees the process dies even if a thread lingers.
        private async void BeginShutdown()
        {
            if (_shutdownStarted) return;
            _shutdownStarted = true;

            ArmExitWatchdog(TimeSpan.FromSeconds(8));

            try
            {
                await _viewModel.ShutdownAsync();
            }
            catch
            {
                // Best-effort; we are exiting regardless.
            }

            try { TrayIcon?.Dispose(); } catch { }

            System.Windows.Application.Current.Shutdown();
        }

        private static void ArmExitWatchdog(TimeSpan timeout)
        {
            var t = new System.Threading.Thread(() =>
            {
                System.Threading.Thread.Sleep(timeout);
                Environment.Exit(0); // last-resort hard kill if graceful exit stalls
            })
            { IsBackground = true, Name = "ExitWatchdog" };
            t.Start();
        }
    }
}
