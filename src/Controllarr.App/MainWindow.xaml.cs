using System.Windows;
using Controllarr.App.ViewModels;

namespace Controllarr.App
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            var viewModel = new MainViewModel();
            DataContext = viewModel;

            Loaded += async (_, _) =>
            {
                await viewModel.BootAsync();
            };

            Closing += async (_, e) =>
            {
                await viewModel.ShutdownAsync();
            };
        }
    }
}
