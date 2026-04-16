using System.ComponentModel;
using System.Windows;
using Controllarr.App.ViewModels;

namespace Controllarr.App
{
    public partial class MainWindow : Window
    {
        private bool _isReallyClosing;

        public MainWindow()
        {
            InitializeComponent();

            var viewModel = new MainViewModel();
            DataContext = viewModel;

            Loaded += async (_, _) =>
            {
                await viewModel.BootAsync();

                // If launched with --minimized (e.g. startup), hide to tray
                if (System.Windows.Application.Current is App app && app.StartMinimized)
                {
                    Hide();
                }
            };

            Closing += OnWindowClosing;

            // Also handle minimize to tray via standard minimize button
            StateChanged += (_, _) =>
            {
                if (WindowState == WindowState.Minimized)
                {
                    Hide();
                }
            };
        }

        private async void OnWindowClosing(object? sender, CancelEventArgs e)
        {
            if (!_isReallyClosing)
            {
                // Minimize to tray instead of closing
                e.Cancel = true;
                Hide();
                return;
            }

            // Real close — shut down the engine
            if (DataContext is MainViewModel vm)
            {
                await vm.ShutdownAsync();
            }

            TrayIcon?.Dispose();
        }

        private void TrayIcon_TrayMouseDoubleClick(object sender, RoutedEventArgs e)
        {
            RestoreFromTray();
        }

        private void TrayShow_Click(object sender, RoutedEventArgs e)
        {
            RestoreFromTray();
        }

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
