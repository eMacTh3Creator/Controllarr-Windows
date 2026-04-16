using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

using Controllarr.Core.Engine;
using Controllarr.Core.Persistence;

namespace Controllarr.Core.Services
{
    // ────────────────────────────────────────────────────────────────
    // VPN status snapshot
    // ────────────────────────────────────────────────────────────────

    public sealed class VpnStatus
    {
        public bool Enabled { get; set; }
        public bool IsConnected { get; set; }
        public string InterfaceName { get; set; } = string.Empty;
        public string InterfaceIP { get; set; } = string.Empty;
        public bool KillSwitchEngaged { get; set; }
        public HashSet<string> PausedHashes { get; set; } = new();
        public bool BoundToVPN { get; set; }

        public VpnStatus() { }
    }

    // ────────────────────────────────────────────────────────────────
    // VPN monitor – detects VPN tunnels and engages kill switch
    // ────────────────────────────────────────────────────────────────

    public sealed class VPNMonitor : IDisposable
    {
        /// <summary>
        /// Known VPN adapter description substrings for Windows.
        /// </summary>
        private static readonly string[] VpnAdapterDescriptions =
        {
            "TAP-Windows",
            "TAP-Win32",
            "WireGuard",
            "Wintun"
        };

        private readonly ITorrentEngine _engine;
        private readonly Func<Settings> _settingsProvider;
        private readonly Func<IReadOnlyList<TorrentView>> _torrentsProvider;
        private readonly Logger _logger;
        private Timer? _timer;
        private readonly object _lock = new();

        // State
        private bool _isConnected;
        private string _interfaceName = string.Empty;
        private string _interfaceIP = string.Empty;
        private bool _killSwitchEngaged;
        private bool _boundToVPN;
        private readonly HashSet<string> _pausedHashes = new();

        public VPNMonitor(ITorrentEngine engine,
                          Func<Settings> settingsProvider,
                          Func<IReadOnlyList<TorrentView>> torrentsProvider,
                          Logger? logger = null)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
            _torrentsProvider = torrentsProvider ?? throw new ArgumentNullException(nameof(torrentsProvider));
            _logger = logger ?? Logger.Instance;
        }

        /// <summary>Start polling for VPN status.</summary>
        public void Start()
        {
            lock (_lock)
            {
                if (_timer != null)
                    return;

                var settings = _settingsProvider();
                int intervalMs = Math.Max(1000, settings.VpnMonitorIntervalSeconds * 1000);

                _timer = new Timer(OnTick, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(intervalMs));
                _logger.Info("VPNMonitor", $"Started (interval={intervalMs}ms)");
            }
        }

        /// <summary>Stop polling.</summary>
        public void Stop()
        {
            lock (_lock)
            {
                _timer?.Dispose();
                _timer = null;
                _logger.Info("VPNMonitor", "Stopped");
            }
        }

        /// <summary>Returns a snapshot of the current VPN status.</summary>
        public VpnStatus Snapshot()
        {
            lock (_lock)
            {
                var settings = _settingsProvider();
                return new VpnStatus
                {
                    Enabled = settings.VpnEnabled,
                    IsConnected = _isConnected,
                    InterfaceName = _interfaceName,
                    InterfaceIP = _interfaceIP,
                    KillSwitchEngaged = _killSwitchEngaged,
                    PausedHashes = new HashSet<string>(_pausedHashes),
                    BoundToVPN = _boundToVPN
                };
            }
        }

        public void Dispose()
        {
            Stop();
        }

        // ── Timer callback ──────────────────────────────────────────

        private void OnTick(object? state)
        {
            try
            {
                var settings = _settingsProvider();

                if (!settings.VpnEnabled)
                {
                    lock (_lock)
                    {
                        if (_killSwitchEngaged)
                            ResumeAllPaused();

                        _isConnected = false;
                        _interfaceName = string.Empty;
                        _interfaceIP = string.Empty;
                        _killSwitchEngaged = false;
                        _boundToVPN = false;
                    }
                    return;
                }

                // Detect VPN interface
                var (found, ifaceName, ifaceIP) = DetectVpnInterface(settings.VpnInterfacePrefix);

                lock (_lock)
                {
                    bool wasConnected = _isConnected;
                    _isConnected = found;
                    _interfaceName = ifaceName;
                    _interfaceIP = ifaceIP;

                    if (found)
                    {
                        OnVpnUp(settings, wasConnected);
                    }
                    else
                    {
                        OnVpnDown(settings, wasConnected);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("VPNMonitor", $"Tick error: {ex.Message}");
            }
        }

        // ── VPN state transitions ───────────────────────────────────

        private void OnVpnUp(Settings settings, bool wasConnected)
        {
            if (!wasConnected)
            {
                _logger.Info("VPNMonitor",
                    $"VPN connected: {_interfaceName} ({_interfaceIP})");
            }

            // Resume any kill-switch-paused torrents
            if (_killSwitchEngaged)
            {
                ResumeAllPaused();
                _killSwitchEngaged = false;
            }

            // Bind engine to VPN adapter IP
            if (settings.VpnBindInterface && !string.IsNullOrEmpty(_interfaceIP))
            {
                if (!_boundToVPN)
                {
                    _engine.BindToAddress(_interfaceIP);
                    _boundToVPN = true;
                    _logger.Info("VPNMonitor",
                        $"Engine bound to VPN address: {_interfaceIP}");
                }
            }
        }

        private void OnVpnDown(Settings settings, bool wasConnected)
        {
            if (wasConnected)
            {
                _logger.Warn("VPNMonitor", "VPN disconnected");
                _boundToVPN = false;
            }

            if (settings.VpnKillSwitch && !_killSwitchEngaged)
            {
                _killSwitchEngaged = true;
                _logger.Warn("VPNMonitor", "Kill switch engaged – pausing all torrents");

                // Pause all active torrents
                var torrents = _torrentsProvider();
                foreach (var t in torrents)
                {
                    if (t.State == TorrentState.Downloading || t.State == TorrentState.Seeding)
                    {
                        if (!_pausedHashes.Contains(t.InfoHash))
                        {
                            _engine.PauseTorrent(t.InfoHash);
                            _pausedHashes.Add(t.InfoHash);
                        }
                    }
                }

                // Unbind from VPN address
                _engine.BindToAddress(null);
            }
        }

        private void ResumeAllPaused()
        {
            foreach (var hash in _pausedHashes)
            {
                try
                {
                    _engine.ResumeTorrent(hash);
                }
                catch (Exception ex)
                {
                    _logger.Warn("VPNMonitor",
                        $"Failed to resume {hash[..Math.Min(8, hash.Length)]}...: {ex.Message}");
                }
            }

            _logger.Info("VPNMonitor",
                $"Resumed {_pausedHashes.Count} kill-switch-paused torrent(s)");
            _pausedHashes.Clear();
        }

        // ── VPN interface detection ─────────────────────────────────

        /// <summary>
        /// Scans all network interfaces for a VPN adapter.
        /// Returns (found, interfaceName, ipv4Address).
        /// </summary>
        private static (bool Found, string Name, string IP) DetectVpnInterface(string configuredPrefix)
        {
            NetworkInterface[] interfaces;
            try
            {
                interfaces = NetworkInterface.GetAllNetworkInterfaces();
            }
            catch
            {
                return (false, string.Empty, string.Empty);
            }

            foreach (var iface in interfaces)
            {
                if (iface.OperationalStatus != OperationalStatus.Up)
                    continue;

                if (!IsVpnAdapter(iface, configuredPrefix))
                    continue;

                string? ipv4 = GetUsableIPv4Address(iface);
                if (ipv4 == null)
                    continue;

                return (true, iface.Name, ipv4);
            }

            return (false, string.Empty, string.Empty);
        }

        /// <summary>
        /// Determines if a network interface is a VPN adapter by checking
        /// its description against known VPN adapter strings, or its name
        /// against the configured prefix.
        /// </summary>
        private static bool IsVpnAdapter(NetworkInterface iface, string configuredPrefix)
        {
            string desc = iface.Description ?? string.Empty;

            // Check known VPN adapter description substrings
            foreach (var vpnDesc in VpnAdapterDescriptions)
            {
                if (desc.Contains(vpnDesc, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // Check configured prefix against interface name
            if (!string.IsNullOrEmpty(configuredPrefix) &&
                iface.Name.StartsWith(configuredPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Gets the first usable IPv4 unicast address from an interface.
        /// Skips link-local addresses (169.254.x.x / fe80::).
        /// </summary>
        private static string? GetUsableIPv4Address(NetworkInterface iface)
        {
            IPInterfaceProperties ipProps;
            try
            {
                ipProps = iface.GetIPProperties();
            }
            catch
            {
                return null;
            }

            foreach (var unicast in ipProps.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;

                // Skip link-local (169.254.0.0/16)
                byte[] bytes = unicast.Address.GetAddressBytes();
                if (bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254)
                    continue;

                return unicast.Address.ToString();
            }

            return null;
        }
    }
}
