using System.Net;
using System.Net.Sockets;
using MonoTorrent.Connections;
using MonoTorrent.Connections.Dht;

namespace Controllarr.Core.Networking;

/// <summary>One reusable UDP socket, owned and pinned by the torrent network policy.</summary>
internal sealed class BoundDhtListener(TorrentNetworkPolicy policy, IPEndPoint preferred) : IDhtListener
{
    private readonly object _gate = new();
    private Socket? _socket;
    private bool _started;
    public event EventHandler<EventArgs>? StatusChanged;
    public event Action<ReadOnlyMemory<byte>, IPEndPoint>? MessageReceived;
    public IPEndPoint? LocalEndPoint { get; private set; }
    public ListenerStatus Status { get; private set; } = ListenerStatus.NotListening;

    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
            policy.Changed += Rebind;
            Open();
        }
    }

    private void Open()
    {
        Status = ListenerStatus.NotListening;
        LocalEndPoint = null;
        if (policy.Allowed && policy.DhtAllowed)
        {
            try
            {
                _socket = policy.CreateSocket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp, preferred.Port);
                if (_socket.LocalEndPoint == null) _socket.Bind(new IPEndPoint(IPAddress.Any, preferred.Port));
                LocalEndPoint = (IPEndPoint)_socket.LocalEndPoint!;
                Status = ListenerStatus.Listening;
                _ = ReceiveAsync(_socket);
            }
            catch
            {
                _socket?.Dispose();
                _socket = null;
                Status = ListenerStatus.PortNotFree;
            }
        }
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ReceiveAsync(Socket socket)
    {
        var buffer = new byte[4097];
        while (true)
        {
            try
            {
                var packet = await socket.ReceiveFromAsync(buffer.AsMemory(), SocketFlags.None,
                    new IPEndPoint(IPAddress.Any, 0)).ConfigureAwait(false);
                if (packet.ReceivedBytes is < 1 or > 4096) continue;
                if (!IsSafePacket(buffer.AsSpan(0, packet.ReceivedBytes))) continue;
                var source = (IPEndPoint)packet.RemoteEndPoint;
                policy.CheckDestination(source.Address);
                lock (_gate)
                {
                    if (socket != _socket || !policy.Allowed) return;
                    // MonoTorrent processes messages on its own loop after this callback returns.
                    MessageReceived?.Invoke(buffer.AsMemory(0, packet.ReceivedBytes).ToArray(), source);
                }
            }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.MessageSize) { }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused) { }
            catch (SocketException) { return; }
            catch (IOException) { /* Ignore blocked sources without ending the receive loop. */ }
            catch (Exception ex) { Services.Logger.Instance.Debug("DHT", $"Discarded datagram: {ex.GetType().Name}"); }
        }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> buffer, IPEndPoint endpoint)
    {
        if (buffer.Length > 4096 || !policy.Allowed || !policy.DhtAllowed)
            throw new IOException("DHT transport is blocked by torrent network policy.");
        policy.CheckDestination(endpoint.Address);
        Socket socket;
        lock (_gate) socket = _socket ?? throw new IOException("DHT listener is not active.");
        await socket.SendToAsync(buffer, SocketFlags.None, endpoint).ConfigureAwait(false);
    }

    private void Rebind()
    {
        lock (_gate)
        {
            if (!_started) return;
            _socket?.Dispose(); _socket = null;
            Open();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _started = false;
            policy.Changed -= Rebind;
            _socket?.Dispose(); _socket = null;
            LocalEndPoint = null;
            Status = ListenerStatus.NotListening;
        }
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    internal static bool IsSafePacket(ReadOnlySpan<byte> packet)
    {
        if (packet.Length is < 2 or > 4096 || packet[0] != 'd') return false;
        int depth = 0;
        for (int i = 0; i < packet.Length; i++)
        {
            byte c = packet[i];
            if (c is (byte)'d' or (byte)'l') { if (++depth > 16) return false; }
            else if (c == 'e') { if (--depth < 0 || (depth == 0 && i != packet.Length - 1)) return false; }
            else if (c == 'i')
            {
                int start = ++i;
                while (i < packet.Length && packet[i] != 'e') i++;
                if (i == packet.Length || i - start is < 1 or > 21) return false;
            }
            else if (c is >= (byte)'0' and <= (byte)'9')
            {
                int length = 0;
                while (i < packet.Length && packet[i] is >= (byte)'0' and <= (byte)'9')
                {
                    length = length * 10 + packet[i++] - '0';
                    if (length > 4096) return false;
                }
                if (i >= packet.Length || packet[i] != ':' || length > packet.Length - i - 1) return false;
                i += length;
            }
            else return false;
        }
        return depth == 0;
    }

    internal static async Task<IReadOnlyList<IPEndPoint>> ResolveBootstrapAsync(TorrentNetworkPolicy policy, CancellationToken cancellationToken = default)
    {
        if (!policy.DhtAllowed || !policy.Allowed) throw new IOException("DHT bootstrap is blocked.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var result = new List<IPEndPoint>();
        foreach (var host in new[] { "router.bittorrent.com", "router.utorrent.com", "dht.transmissionbt.com" })
        {
            try
            {
                foreach (var address in (await policy.ResolveAsync(host, timeout.Token)).Where(a => a.AddressFamily == AddressFamily.InterNetwork))
                {
                    policy.CheckDestination(address);
                    result.Add(new IPEndPoint(address, 6881));
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            { if (timeout.IsCancellationRequested) break; }
            if (result.Count >= 4) break;
        }
        if (result.Count == 0) throw new IOException("No DHT bootstrap address resolved through the permitted DNS route.");
        return result.Distinct().Take(8).ToArray();
    }
}
