using System.Net;
using System.Net.Sockets;
using MonoTorrent;
using MonoTorrent.Connections;
using MonoTorrent.Connections.Dht;
using MonoTorrent.Connections.Peer;
using MonoTorrent.Dht;
using MonoTorrent.PortForwarding;
using MonoTorrent.Trackers;
using ReusableTasks;

namespace Controllarr.Core.Networking;

internal static class TorrentNetworkFactories
{
    public static Factories Create(TorrentNetworkPolicy policy)
    {
        var connector = new BoundSocketConnector(policy);
        var factories = Factories.Default.WithSocketConnectorCreator(() => connector)
            .WithPeerConnectionCreator("ipv4", uri => new SocketPeerConnection(uri, connector))
            .WithPeerConnectionCreator("ipv6", uri => new SocketPeerConnection(uri, connector))
            .WithPeerConnectionListenerCreator(endpoint => new BoundPeerListener(policy, endpoint))
            .WithHttpClientCreator(policy.CreateHttpClient)
            .WithTrackerCreator("http", uri => new Tracker(new MonoTorrent.Connections.Tracker.HttpTrackerConnection(uri, policy.CreateHttpClient, AddressFamily.InterNetwork)))
            .WithTrackerCreator("https", uri => new Tracker(new MonoTorrent.Connections.Tracker.HttpTrackerConnection(uri, policy.CreateHttpClient, AddressFamily.InterNetwork)))
            .WithTrackerCreator("udp", uri => new Tracker(new BoundUdpTracker(policy, uri)));
        // These built-in transports perform independent DNS/multicast/NAT discovery. Do not instantiate them in protected mode.
        if (policy.RestrictedDiscovery)
            factories = factories.WithDhtCreator(() => new DisabledDht())
                .WithDhtListenerCreator(_ => new DisabledDhtListener())
                .WithLocalPeerDiscoveryCreator(() => new DisabledDiscovery())
                .WithPortForwarderCreator(() => new DisabledPortForwarder());
        return factories;
    }
}

internal sealed class BoundSocketConnector(TorrentNetworkPolicy policy) : ISocketConnector
{
    public async ReusableTask<Socket> ConnectAsync(Uri uri, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        return await policy.ConnectAsync(uri.Host.Trim('[', ']'), uri.Port, timeout.Token);
    }
}

internal sealed class BoundPeerListener : IPeerConnectionListener
{
    private readonly TorrentNetworkPolicy _policy;
    private readonly object _gate = new();
    private Socket? _socket;
    private bool _started;
    public event EventHandler<EventArgs>? StatusChanged;
    public event EventHandler<PeerConnectionEventArgs>? ConnectionReceived;
    public ListenerStatus Status { get; private set; } = ListenerStatus.NotListening;
    public IPEndPoint? LocalEndPoint { get; private set; }
    public IPEndPoint PreferredLocalEndPoint { get; }

    public BoundPeerListener(TorrentNetworkPolicy policy, IPEndPoint endpoint)
    { _policy = policy; PreferredLocalEndPoint = endpoint; }

    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
            _policy.Changed += Rebind;
            Open();
        }
    }

    private void Open()
    {
        if (!_policy.Allowed || _policy.UsesProxy) { Status = ListenerStatus.NotListening; LocalEndPoint = null; return; }
        try
        {
            var socket = _policy.CreateSocket(PreferredLocalEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp, PreferredLocalEndPoint.Port);
            _socket = socket;
            if (socket.LocalEndPoint == null) socket.Bind(new IPEndPoint(PreferredLocalEndPoint.Address, 0));
            socket.Listen(128);
            LocalEndPoint = (IPEndPoint?)socket.LocalEndPoint;
            Status = ListenerStatus.Listening;
            _ = AcceptAsync(socket);
        }
        catch { _socket?.Dispose(); _socket = null; Status = ListenerStatus.PortNotFree; LocalEndPoint = null; }
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task AcceptAsync(Socket listener)
    {
        while (true)
        {
            Socket? socket = null;
            try
            {
                socket = await listener.AcceptAsync().ConfigureAwait(false);
                _policy.RegisterIncoming(socket);
                var connection = new SocketPeerConnection(socket, isIncoming: true);
                if (ConnectionReceived == null) connection.Dispose();
                else ConnectionReceived.Invoke(this, new PeerConnectionEventArgs(connection, null));
                socket = null;
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException) { socket?.Dispose(); break; }
            catch { socket?.Dispose(); }
        }
    }

    private void Rebind()
    {
        lock (_gate) { if (!_started) return; _socket?.Dispose(); _socket = null; Open(); }
    }
    public void Stop()
    {
        lock (_gate)
        {
            _started = false; _policy.Changed -= Rebind; _socket?.Dispose(); _socket = null;
            Status = ListenerStatus.NotListening; LocalEndPoint = null;
        }
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class DisabledDht : IDhtEngine, ITransferMonitor
{
    public event EventHandler<PeersFoundEventArgs> PeersFound { add { } remove { } }
    public event EventHandler StateChanged { add { } remove { } }
    public TimeSpan AnnounceInterval => TimeSpan.FromMinutes(30);
    public TimeSpan MinimumAnnounceInterval => TimeSpan.FromMinutes(3);
    public bool Disposed { get; private set; }
    public ITransferMonitor Monitor => this;
    public int NodeCount => 0;
    public DhtState State => DhtState.NotReady;
    public long BytesSent => 0;
    public long BytesReceived => 0;
    public long DownloadRate => 0;
    public long UploadRate => 0;
    public void Add(IEnumerable<ReadOnlyMemory<byte>> nodes) { }
    public void Announce(InfoHash hash, int port) { }
    public void GetPeers(InfoHash hash) { }
    public Task<ReadOnlyMemory<byte>> SaveNodesAsync() => Task.FromResult(ReadOnlyMemory<byte>.Empty);
    public Task SetListenerAsync(IDhtListener listener) => Task.CompletedTask;
    public Task StartAsync() => Task.CompletedTask;
    public Task StartAsync(ReadOnlyMemory<byte> nodes) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
    public void Dispose() => Disposed = true;
}

internal sealed class DisabledDhtListener : IDhtListener
{
    public event EventHandler<EventArgs> StatusChanged { add { } remove { } }
    public event Action<ReadOnlyMemory<byte>, IPEndPoint> MessageReceived { add { } remove { } }
    public IPEndPoint? LocalEndPoint => null;
    public ListenerStatus Status => ListenerStatus.NotListening;
    public Task SendAsync(ReadOnlyMemory<byte> buffer, IPEndPoint endpoint) => Task.CompletedTask;
    public void Start() { }
    public void Stop() { }
}

internal sealed class DisabledDiscovery : ILocalPeerDiscovery
{
    public event EventHandler<LocalPeerFoundEventArgs> PeerFound { add { } remove { } }
    public event EventHandler<EventArgs> StatusChanged { add { } remove { } }
    public ListenerStatus Status => ListenerStatus.NotListening;
    public TimeSpan MinimumAnnounceInternal => TimeSpan.FromMinutes(1);
    public TimeSpan AnnounceInternal => TimeSpan.FromMinutes(5);
    public Task Announce(InfoHash hash, IPEndPoint port) => Task.CompletedTask;
    public void Start() { }
    public void Stop() { }
}

internal sealed class DisabledPortForwarder : IPortForwarder
{
    public event EventHandler MappingsChanged { add { } remove { } }
    public bool Active => false;
    public Mappings Mappings { get; } = new();
    public Task RegisterMappingAsync(Mapping mapping) => Task.CompletedTask;
    public Task UnregisterMappingAsync(Mapping mapping, CancellationToken token) => Task.CompletedTask;
    public Task StartAsync(CancellationToken token) => Task.CompletedTask;
    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
    public Task StopAsync(bool removeExistingMappings, CancellationToken token) => Task.CompletedTask;
}
