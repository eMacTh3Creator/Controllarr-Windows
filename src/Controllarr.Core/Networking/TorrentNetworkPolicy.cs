using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Controllarr.Core.Persistence;

namespace Controllarr.Core.Networking;

public sealed record NetworkAdapterChoice(string Id, string Label);
public sealed record VpnAdapter(string Id, string Name, IPAddress Address, int Index, IPAddress[] DnsServers);

/// <summary>Owns only torrent sockets. Kestrel, RSS and *arr HTTP clients remain on their normal LAN routes.</summary>
public sealed class TorrentNetworkPolicy : IDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<Socket> _sockets = new();
    private readonly Settings _settings;
    private readonly IpBlocklist _blocklist;
    private VpnAdapter? _adapter;
    private bool _restartRequired;
    private bool _disposed;
    public bool RequiresVpn { get; }
    public bool UsesProxy => _settings.TorrentNetwork.ProxyEnabled;
    public bool RestrictedDiscovery => RequiresVpn || UsesProxy || _blocklist.Count > 0;
    public bool Allowed { get { lock (_gate) return !_disposed && !_restartRequired && (!RequiresVpn || _adapter != null); } }
    public bool RestartRequired { get { lock (_gate) return _restartRequired; } }
    public VpnAdapter? Adapter { get { lock (_gate) return _adapter; } }
    internal int OpenSocketCount { get { lock (_gate) return _sockets.Count(s => !s.SafeHandle.IsClosed); } }
    public event Action? Changed;

    public TorrentNetworkPolicy(Settings settings)
    {
        Validate(settings);
        _settings = System.Text.Json.JsonSerializer.Deserialize<Settings>(System.Text.Json.JsonSerializer.Serialize(settings))!;
        RequiresVpn = settings.VpnEnabled;
        _blocklist = IpBlocklist.Load(settings.TorrentNetwork.BlocklistPath);
        RefreshAdapter();
        NetworkChange.NetworkAddressChanged += AddressChanged;
        NetworkChange.NetworkAvailabilityChanged += AvailabilityChanged;
    }

    public static void Validate(Settings settings)
    {
        if (settings.TorrentNetwork == null || settings.ConnectionLimits == null || settings.PeerDiscovery == null || settings.TorrentQueueing == null)
            throw new ArgumentException("Network, connection, discovery and queue settings cannot be null.");
        var n = settings.TorrentNetwork;
        if (Encoding.UTF8.GetByteCount(n.ProxyUsername) > 255 || Encoding.UTF8.GetByteCount(n.ProxyPassword) > 255 ||
            (!string.IsNullOrEmpty(n.ProxyPassword) && string.IsNullOrEmpty(n.ProxyUsername)))
            throw new ArgumentException("SOCKS5 credentials must be at most 255 UTF-8 bytes each, with a username when a password is supplied.");
        if (n.Encryption is not ("Prefer" or "Require" or "Disable")) throw new ArgumentException("Unknown encryption policy.");
        if (n.ProxyEnabled && (!IPAddress.TryParse(n.ProxyHost, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || n.ProxyPort is < 1 or > 65535))
            throw new ArgumentException("SOCKS5 requires a numeric IPv4 server and a port from 1 to 65535. Hostnames are not resolved outside the tunnel.");
        IpBlocklist.Load(n.BlocklistPath);
        if (settings.VpnMonitorIntervalSeconds is < 1 or > 300) throw new ArgumentException("VPN monitor interval must be 1-300 seconds.");
        if (settings.ConnectionLimits.MaxConnectionsPerTorrent is < 1 or > 10000 || settings.ConnectionLimits.GlobalMaxUploadSlots is < 2 or > 1000)
            throw new ArgumentException("Per-torrent connections must be 1-10000; upload slots must be 2-1000.");
        if (settings.ConnectionLimits.GlobalMaxConnections is < 1 or > 10000 || settings.ConnectionLimits.DownloadReservePercent is < 0 or > 90)
            throw new ArgumentException("Global connections must be 1-10000; download connection reserve must be 0-90% (0 disables it).");
    }

    public static NetworkAdapterChoice[] AvailableAdapters() => new[] { new NetworkAdapterChoice("", "Automatic VPN detection") }
        .Concat(NetworkInterface.GetAllNetworkInterfaces().Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .Select(n => new NetworkAdapterChoice(n.Id, $"{n.Name} - {n.Description}"))).ToArray();

    public static VpnAdapter? DetectAdapter(Settings settings)
    {
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            bool match = !string.IsNullOrEmpty(settings.VpnInterfaceId) ? n.Id.Equals(settings.VpnInterfaceId, StringComparison.OrdinalIgnoreCase)
                : new[] { "TAP", "WireGuard", "Wintun" }.Any(s => n.Description.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
                  (!string.IsNullOrWhiteSpace(settings.VpnInterfacePrefix) && n.Name.StartsWith(settings.VpnInterfacePrefix, StringComparison.OrdinalIgnoreCase));
            if (!match) continue;
            var p = n.GetIPProperties();
            var ip = p.UnicastAddresses.Select(a => a.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal));
            if (ip != null && p.GetIPv4Properties() is { } v4)
                return new(n.Id, n.Name, ip, v4.Index, p.DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetwork).ToArray());
        }
        return null;
    }

    private void AddressChanged(object? sender, EventArgs e) => RefreshAdapter();
    private void AvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => RefreshAdapter();
    public void RefreshAdapter()
    {
        VpnAdapter? next = null;
        try { if (RequiresVpn) next = DetectAdapter(_settings); } catch { /* Detection errors fail closed. */ }
        bool changed;
        lock (_gate)
        {
            changed = _adapter?.Id != next?.Id || !Equals(_adapter?.Address, next?.Address) || _adapter?.Index != next?.Index ||
                !(_adapter?.DnsServers ?? Array.Empty<IPAddress>()).SequenceEqual(next?.DnsServers ?? Array.Empty<IPAddress>());
            if (!changed) return;
            _adapter = next;
            CloseSockets();
        }
        Changed?.Invoke();
    }

    // Security topology changes latch the old session closed until a fresh engine is constructed.
    public void RequireRestart()
    {
        lock (_gate) { _restartRequired = true; CloseSockets(); }
        Changed?.Invoke();
    }

    public void ResetConnections()
    {
        lock (_gate) CloseSockets();
        Changed?.Invoke();
    }

    public bool Matches(Settings settings) => RequiresVpn == settings.VpnEnabled &&
        _settings.VpnInterfaceId == settings.VpnInterfaceId && _settings.VpnInterfacePrefix == settings.VpnInterfacePrefix &&
        _settings.TorrentNetwork.ProxyEnabled == settings.TorrentNetwork.ProxyEnabled &&
        _settings.TorrentNetwork.ProxyHost == settings.TorrentNetwork.ProxyHost && _settings.TorrentNetwork.ProxyPort == settings.TorrentNetwork.ProxyPort &&
        _settings.TorrentNetwork.ProxyUsername == settings.TorrentNetwork.ProxyUsername && _settings.TorrentNetwork.ProxyPassword == settings.TorrentNetwork.ProxyPassword &&
        _settings.TorrentNetwork.BlocklistPath == settings.TorrentNetwork.BlocklistPath;

    public void CheckDestination(IPAddress ip)
    {
        if (_blocklist.Contains(ip)) throw new IOException("Destination is blocked by the torrent IP blocklist.");
        if ((RequiresVpn || UsesProxy) && ip.AddressFamily != AddressFamily.InterNetwork)
            throw new IOException("IPv6 torrent connections are disabled in VPN/proxy mode.");
    }

    public Socket CreateSocket(AddressFamily family, SocketType type, ProtocolType protocol, int localPort = 0)
    {
        lock (_gate)
        {
            if (!Allowed) throw new IOException("Torrent network is blocked: VPN unavailable or restart required.");
            if ((RequiresVpn || UsesProxy) && family != AddressFamily.InterNetwork) throw new IOException("Protected torrent traffic is IPv4-only.");
            var socket = new Socket(family, type, protocol);
            try
            {
                if (RequiresVpn)
                {
                    // IP_UNICAST_IF takes the Windows interface index in network byte order.
                    socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder(_adapter!.Index));
                    socket.Bind(new IPEndPoint(_adapter.Address, localPort));
                }
                else if (localPort != 0) socket.Bind(new IPEndPoint(family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, localPort));
                _sockets.RemoveWhere(s => s.SafeHandle.IsClosed);
                _sockets.Add(socket);
                return socket;
            }
            catch { socket.Dispose(); throw; }
        }
    }

    public void RegisterIncoming(Socket socket)
    {
        lock (_gate)
        {
            var remote = ((IPEndPoint)socket.RemoteEndPoint!).Address;
            CheckDestination(remote);
            if (!Allowed || UsesProxy || (RequiresVpn && !Equals(((IPEndPoint)socket.LocalEndPoint!).Address, _adapter?.Address)))
                throw new IOException("Incoming torrent connection violates network policy.");
            if (RequiresVpn) socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder(_adapter!.Index));
            _sockets.RemoveWhere(s => s.SafeHandle.IsClosed);
            _sockets.Add(socket);
        }
    }

    public async Task<Socket> ConnectAsync(string host, int port, CancellationToken token)
    {
        if (!Allowed) throw new IOException("Torrent network is blocked.");
        IPAddress[] addresses;
        // In proxy mode the SOCKS server resolves tracker/webseed hostnames; no local DNS fallback.
        if (UsesProxy)
        {
            if (IPAddress.TryParse(host, out var target)) CheckDestination(target);
            else if (_blocklist.Count > 0) throw new IOException("Hostname destinations cannot be IP-filtered through SOCKS5; use numeric tracker/webseed URLs or disable the blocklist.");
            var proxy = IPAddress.Parse(_settings.TorrentNetwork.ProxyHost);
            var socket = CreateSocket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(proxy, _settings.TorrentNetwork.ProxyPort), token);
                await Socks5.ConnectAsync(socket, host, port, _settings.TorrentNetwork.ProxyUsername, _settings.TorrentNetwork.ProxyPassword, token);
                return socket;
            }
            catch { socket.Dispose(); throw; }
        }
        addresses = await ResolveAsync(host, token);
        Exception? last = null;
        foreach (var ip in addresses)
        {
            Socket? socket = null;
            try
            {
                CheckDestination(ip);
                socket = CreateSocket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(new IPEndPoint(ip, port), token);
                return socket;
            }
            catch (OperationCanceledException) { socket?.Dispose(); throw; }
            catch (Exception ex) { socket?.Dispose(); last = ex; }
        }
        throw new IOException("No permitted torrent destination could be reached.", last);
    }

    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken token)
    {
        if (!Allowed) throw new IOException("Torrent network is blocked.");
        if (IPAddress.TryParse(host.Trim('[', ']'), out var numeric)) return new[] { numeric };
        if (!RequiresVpn) return await Dns.GetHostAddressesAsync(host, token);
        var adapter = Adapter ?? throw new IOException("VPN is unavailable.");
        foreach (var dns in adapter.DnsServers)
        {
            try { return await TunnelDns.ResolveAsync(this, dns, host, token); }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            { token.ThrowIfCancellationRequested(); }
        }
        throw new IOException("No VPN-adapter IPv4 DNS server answered; system DNS fallback is disabled.");
    }

    public HttpClient CreateHttpClient(AddressFamily family)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (context, token) =>
            {
                if (family == AddressFamily.InterNetworkV6 && (RequiresVpn || UsesProxy)) throw new IOException("IPv6 is disabled in protected mode.");
                var socket = await ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, token);
                return new NetworkStream(socket, ownsSocket: true);
            }
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30), DefaultRequestVersion = HttpVersion.Version11 };
    }

    private void CloseSockets() { foreach (var socket in _sockets) socket.Dispose(); _sockets.Clear(); }
    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= AddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= AvailabilityChanged;
        lock (_gate) { _disposed = true; CloseSockets(); }
    }
}

internal static class Socks5
{
    public static async Task ConnectAsync(Socket socket, string host, int port, string username, string password, CancellationToken token)
    {
        using var stream = new NetworkStream(socket, ownsSocket: false);
        byte method = string.IsNullOrEmpty(username) ? (byte)0 : (byte)2;
        await stream.WriteAsync(new byte[] { 5, 1, method }, token);
        var reply = new byte[2];
        await stream.ReadExactlyAsync(reply, token);
        if (reply[0] != 5 || reply[1] != method) throw new IOException("SOCKS5 server rejected the configured authentication method.");
        if (method == 2)
        {
            byte[] user = Encoding.UTF8.GetBytes(username), secret = Encoding.UTF8.GetBytes(password);
            await stream.WriteAsync(new byte[] { 1, (byte)user.Length }.Concat(user).Concat(new[] { (byte)secret.Length }).Concat(secret).ToArray(), token);
            await stream.ReadExactlyAsync(reply, token);
            if (reply[0] != 1 || reply[1] != 0) throw new IOException("SOCKS5 authentication failed; direct fallback is disabled.");
        }
        byte[] address;
        if (IPAddress.TryParse(host, out var ip)) address = new byte[] { 1 }.Concat(ip.GetAddressBytes()).ToArray();
        else
        {
            byte[] name = Encoding.ASCII.GetBytes(new System.Globalization.IdnMapping().GetAscii(host));
            if (name.Length is < 1 or > 255) throw new IOException("Invalid SOCKS5 destination name.");
            address = new byte[] { 3, (byte)name.Length }.Concat(name).ToArray();
        }
        await stream.WriteAsync(new byte[] { 5, 1, 0 }.Concat(address).Concat(new[] { (byte)(port >> 8), (byte)port }).ToArray(), token);
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        if (header[0] != 5 || header[1] != 0 || header[2] != 0) throw new IOException("SOCKS5 connection refused; direct fallback is disabled.");
        int length = header[3] switch { 1 => 4, 4 => 16, 3 => -1, _ => throw new IOException("Invalid SOCKS5 response.") };
        if (length == -1) { var size = new byte[1]; await stream.ReadExactlyAsync(size, token); length = size[0]; }
        await stream.ReadExactlyAsync(new byte[length + 2], token);
    }
}
