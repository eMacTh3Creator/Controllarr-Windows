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
        private bool _isReallyClosing;
        private readonly MainViewModel _viewModel;

        public MainWindow()
        {
            InitializeComponent();

            _viewModel = new MainViewModel();
            DataContext = _viewModel;

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
        // Window lifecycle / tray
        // ────────────────────────────────────────────────────────────

        private async void OnWindowClosing(object? sender, CancelEventArgs e)
        {
            if (!_isReallyClosing)
            {
                // Minimize to tray instead of closing.
                e.Cancel = true;
                Hide();
                return;
            }

            // Real close — shut down the engine + server.
            await _viewModel.ShutdownAsync();

            TrayIcon?.Dispose();
        }

        private void TrayIcon_TrayMouseDoubleClick(object sender, RoutedEventArgs e) => RestoreFromTray();

        private void TrayShow_Click(object sender, RoutedEventArgs e) => RestoreFromTray();

        private void TrayExit_Click(object sender, RoutedEventArgs e)
        {
            _isReallyClosing = true;
            Close();
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }
    }
}
