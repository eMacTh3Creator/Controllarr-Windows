using CommunityToolkit.Mvvm.Input;
using Controllarr.Core.Networking;

namespace Controllarr.App.ViewModels;

public partial class MainViewModel
{
    public NetworkAdapterChoice[] NetworkAdapters { get; private set; } = TorrentNetworkPolicy.AvailableAdapters();
    public string TorrentNetworkStatusText => _engine == null ? "Engine is starting"
        : _engine.NetworkPolicy.RestartRequired ? "BLOCKED: quit and reopen Controllarr to apply networking changes"
        : !_engine.NetworkPolicy.Allowed ? "BLOCKED: selected VPN adapter is unavailable"
        : _engine.NetworkPolicy.RequiresVpn ? $"Bound to {_engine.NetworkPolicy.Adapter?.Name} ({_engine.NetworkPolicy.Adapter?.Address}); IPv4 only"
        : _engine.NetworkPolicy.UsesProxy ? "SOCKS5 only: direct fallback and incoming peers disabled"
        : "Standard networking (VPN binding is off)";

    [RelayCommand]
    private void RefreshNetworkAdapters()
    {
        NetworkAdapters = TorrentNetworkPolicy.AvailableAdapters();
        OnPropertyChanged(nameof(NetworkAdapters));
    }

    [RelayCommand]
    private void BrowseTorrentBlocklist()
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "IP/CIDR text lists|*.txt;*.cidr;*.list|All files|*.*", CheckFileExists = true };
        if (picker.ShowDialog() != true) return;
        Settings.TorrentNetwork.BlocklistPath = picker.FileName;
        OnPropertyChanged(nameof(Settings));
    }
}
