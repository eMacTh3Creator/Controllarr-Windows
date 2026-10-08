using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using Controllarr.Core.Networking;
using Controllarr.Core.Persistence;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Dht;
using DhtEngine = Controllarr.Core.Dht.DhtEngine;

internal static class DhtTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        foreach (string provider in new[] { "PIA", "Private Internet Access", "NordLynx", "NordVPN", "Proton VPN", "Mullvad", "Surfshark", "ExpressVPN", "CyberGhost", "Windscribe", "IVPN", "WireGuard", "Wintun", "TAP-Windows Adapter V9", "TAP-Win32 Adapter" })
            check(TorrentNetworkPolicy.IsVpnCandidate(provider, ""), $"VPN auto-detection recognizes {provider}");
        check(!TorrentNetworkPolicy.IsVpnCandidate("Olympia Ethernet", "Intel Gigabit") &&
            !TorrentNetworkPolicy.IsVpnCandidate("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet") &&
            !TorrentNetworkPolicy.IsVpnCandidate("Wi-Fi", "Intel Wireless"), "VPN detection does not mistake physical/Hyper-V adapters or a PIA substring for a tunnel");
        check(!BoundDhtListener.IsSafePacket(System.Text.Encoding.ASCII.GetBytes("d1:al" + new string('l', 100) + new string('e', 102))) &&
            !BoundDhtListener.IsSafePacket(System.Text.Encoding.ASCII.GetBytes("d1:a99999:xee")) &&
            BoundDhtListener.IsSafePacket(new BEncodedDictionary { ["x"] = new BEncodedString("valid") }.Encode()),
            "DHT rejects deeply nested/oversized bencoding before the recursive decoder");

        using (var missing = new TorrentNetworkPolicy(new Settings { VpnEnabled = true, VpnInterfaceId = "missing-dht-fixture" }))
        {
            var blocked = new BoundDhtListener(missing, new IPEndPoint(IPAddress.Any, 0));
            blocked.Start();
            check(blocked.LocalEndPoint == null, "DHT creates no unbound listener when the required adapter is missing");
            bool denied = false;
            try { await blocked.SendAsync(new byte[] { 1 }, new IPEndPoint(IPAddress.Loopback, 9)); } catch (IOException) { denied = true; }
            check(denied, "missing VPN blocks DHT sends before any destination can be reached");
            denied = false;
            try { await BoundDhtListener.ResolveBootstrapAsync(missing); } catch (IOException) { denied = true; }
            check(denied, "missing VPN blocks DHT bootstrap before any DNS resolution");
            blocked.Stop();
        }
        using (var proxy = new TorrentNetworkPolicy(new Settings { TorrentNetwork = new() { ProxyEnabled = true, ProxyHost = "127.0.0.1" } }))
        using (var disabled = TorrentNetworkFactories.Create(proxy).CreateDht())
            check(!proxy.DhtAllowed && disabled is DisabledDht, "SOCKS5 cannot enable DHT without a UDP ASSOCIATE implementation");

        string blockPath = Path.Combine(Path.GetTempPath(), "Controllarr-DhtBlock-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(blockPath, "127.0.0.0/8");
            using var filtering = new TorrentNetworkPolicy(new Settings { TorrentNetwork = new() { BlocklistPath = blockPath } });
            var filtered = new BoundDhtListener(filtering, new IPEndPoint(IPAddress.Any, 0)); filtered.Start();
            bool denied = false;
            try { await filtered.SendAsync(new byte[] { 1 }, new IPEndPoint(IPAddress.Loopback, 9)); } catch (IOException) { denied = true; }
            check(denied && filtering.DhtAllowed, "IP blocklists filter DHT destinations rather than disabling all DHT");
            filtered.Stop();
        }
        finally { File.Delete(blockPath); }

        await TestLocalDhtAsync(check);
        if (OperatingSystem.IsWindows()) await TestPinnedDhtAsync(check);
    }

    private static async Task TestLocalDhtAsync(Action<bool, string> check)
    {
        using var policy = new TorrentNetworkPolicy(new Settings());
        await using var swarm = new LocalDht();
        int bootstraps = 0;
        using var engine = new DhtEngine(_ =>
        {
            bootstraps++;
            return Task.FromResult<IReadOnlyList<IPEndPoint>>(swarm.Endpoints.Take(1).ToArray());
        });
        var listener = new BoundDhtListener(policy, new IPEndPoint(IPAddress.Any, 0));
        await engine.SetListenerAsync(listener);
        await engine.StartAsync();
        try { await WaitAsync(() => engine.State == DhtState.Ready, TimeSpan.FromSeconds(10)); }
        catch { Console.WriteLine($"DHT BOOTSTRAP DIAGNOSTICS state={engine.State} nodes={engine.NodeCount} resolverCalls={bootstraps}"); throw; }
        check(engine.NodeCount >= 10 && bootstraps == 1, "real DHT bootstraps through the supplied resolver and bound UDP listener without built-in DNS");
        var found = new TaskCompletionSource<PeersFoundEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PeersFound += (_, args) => found.TrySetResult(args);
        engine.GetPeers(new InfoHash(RandomNumberGenerator.GetBytes(20)));
        var peers = await found.Task.WaitAsync(TimeSpan.FromSeconds(10));
        check(peers.Peers.Count == 1 && peers.Peers[0].ConnectionUri.Port == 54321,
            "real DHT get_peers returns peers; a matching transaction from a spoofed UDP source is ignored");
        using (var remote = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            var endpoint = new IPEndPoint(IPAddress.Loopback, listener.LocalEndPoint!.Port);
            var id = new BEncodedString(RandomNumberGenerator.GetBytes(20));
            var infoHash = new BEncodedString(RandomNumberGenerator.GetBytes(20));
            async Task<BEncodedDictionary> Query(string method, BEncodedDictionary arguments)
            {
                var request = new BEncodedDictionary { ["t"] = new BEncodedString(RandomNumberGenerator.GetBytes(2)),
                    ["y"] = new BEncodedString("q"), ["q"] = new BEncodedString(method), ["a"] = arguments };
                await remote.SendAsync(request.Encode(), endpoint);
                return BEncodedValue.Decode<BEncodedDictionary>((await remote.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
            }
            var lookup = await Query("get_peers", new BEncodedDictionary { ["id"] = id, ["info_hash"] = infoHash });
            var token = ((BEncodedDictionary)lookup["r"])["token"];
            var announced = await Query("announce_peer", new BEncodedDictionary { ["id"] = id, ["info_hash"] = infoHash,
                ["token"] = token, ["port"] = new BEncodedNumber(54322) });
            lookup = await Query("get_peers", new BEncodedDictionary { ["id"] = id, ["info_hash"] = infoHash });
            var values = (BEncodedList)((BEncodedDictionary)lookup["r"])["values"];
            check(announced["y"].Equals(new BEncodedString("r")) && values.Count == 1 && ((BEncodedString)values[0]).Span[^1] == 0x32,
                "DHT answers incoming queries and token-validated announcements preserve the advertised peer port");
            int entries = engine.Torrents.Count;
            await Query("announce_peer", new BEncodedDictionary { ["id"] = id,
                ["info_hash"] = new BEncodedString(RandomNumberGenerator.GetBytes(20)), ["token"] = new BEncodedString("invalid"), ["port"] = new BEncodedNumber(9) });
            check(engine.Torrents.Count == entries, "invalid DHT announce tokens cannot allocate persistent peer records");
        }
        var saved = await engine.SaveNodesAsync();
        check(BEncodedValue.Decode<BEncodedList>(saved.Span).Count >= 10, "DHT node cache uses bounded, restart-compatible compact entries");
        await engine.StopAsync();
        check(listener.LocalEndPoint == null && policy.OpenSocketCount == 0, "DHT stop releases the socket and pending work");
        await engine.StartAsync(saved);
        await WaitAsync(() => engine.State == DhtState.Ready, TimeSpan.FromSeconds(10));
        check(bootstraps == 1, "DHT restarts from its saved node cache instead of forcing another DNS bootstrap");
        policy.RequireRestart();
        check(listener.LocalEndPoint == null && !policy.Allowed, "security topology changes close DHT and cannot reopen an ordinary UDP listener");
        await engine.StopAsync();

        using var failure = new DhtEngine(_ => Task.FromException<IReadOnlyList<IPEndPoint>>(new IOException("fixture DNS unavailable")));
        var failureListener = new BoundDhtListener(policy, new IPEndPoint(IPAddress.Any, 0));
        await failure.SetListenerAsync(failureListener);
        await failure.StartAsync();
        await WaitAsync(() => failure.State == DhtState.NotReady, TimeSpan.FromSeconds(2));
        check(failure.NodeCount == 0, "failed bootstrap remains NotReady without an independent resolver fallback");
        await failure.StopAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var waiting = new DhtEngine(async token =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { canceled.TrySetResult(); }
            return Array.Empty<IPEndPoint>();
        });
        await waiting.SetListenerAsync(new BoundDhtListener(policy, new IPEndPoint(IPAddress.Any, 0)));
        await waiting.StartAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await waiting.StopAsync();
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        check(waiting.State == DhtState.NotReady, "DHT shutdown cancels in-flight bootstrap instead of leaving detached DNS work");
    }

    private static async Task TestPinnedDhtAsync(Action<bool, string> check)
    {
        var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up &&
            n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.GetIPProperties().UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork));
        if (adapter == null) throw new Exception("No guest IPv4 adapter for the forced-bind DHT check");
        using var policy = new TorrentNetworkPolicy(new Settings { VpnEnabled = true, VpnInterfaceId = adapter.Id });
        var listener = new BoundDhtListener(policy, new IPEndPoint(IPAddress.Any, 0));
        listener.Start();
        check(listener.LocalEndPoint?.Address.Equals(policy.Adapter!.Address) == true, "Windows DHT listener binds the explicitly selected adapter address");
        using var sink = new UdpClient(new IPEndPoint(policy.Adapter!.Address, 0));
        await listener.SendAsync(new byte[] { 1, 2, 3 }, (IPEndPoint)sink.Client.LocalEndPoint!);
        var packet = await sink.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        check(packet.RemoteEndPoint.Address.Equals(policy.Adapter.Address), "Windows DHT datagrams use the pinned adapter source, not the default-route source");
        policy.ResetConnections();
        check(listener.LocalEndPoint?.Address.Equals(policy.Adapter.Address) == true, "DHT listener rebinds to the same selected adapter after a connection reset");
        policy.RequireRestart();
        check(listener.LocalEndPoint == null && policy.OpenSocketCount == 0, "Windows DHT socket closes immediately when network topology is latched closed");
        listener.Stop();
    }

    private static async Task WaitAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        while (!condition()) await Task.Delay(20, deadline.Token);
    }

    private sealed class LocalDht : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly UdpClient[] _nodes;
        private readonly byte[][] _ids;
        private readonly Task[] _workers;
        public IPEndPoint[] Endpoints { get; }
        public LocalDht()
        {
            // A 16-node random fixture can discover fewer than the routing table's
            // ten-node bootstrap threshold after the eight-closest-node walk.
            _nodes = Enumerable.Range(0, 64).Select(_ => new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))).ToArray();
            _ids = _nodes.Select(_ => RandomNumberGenerator.GetBytes(20)).ToArray();
            Endpoints = _nodes.Select(n => (IPEndPoint)n.Client.LocalEndPoint!).ToArray();
            _workers = _nodes.Select((node, index) => ServeAsync(node, index)).ToArray();
        }
        private async Task ServeAsync(UdpClient node, int index)
        {
            using var spoof = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var packet = await node.ReceiveAsync(_stop.Token);
                    var query = BEncodedValue.Decode<BEncodedDictionary>(packet.Buffer);
                    if (!query["y"].Equals(new BEncodedString("q"))) continue;
                    var response = new BEncodedDictionary { ["id"] = new BEncodedString(_ids[index]) };
                    string method = ((BEncodedString)query["q"]).Text;
                    if (method == "get_peers")
                    {
                        response["token"] = new BEncodedString("fixture");
                        response["values"] = new BEncodedList { new BEncodedString(new byte[] { 127, 0, 0, 1, 0xD4, 0x31 }) };
                    }
                    else if (method == "find_node")
                    {
                        using var compact = new MemoryStream();
                        for (int i = 0; i < _nodes.Length; i++)
                        {
                            compact.Write(_ids[i]); compact.Write(Endpoints[i].Address.GetAddressBytes());
                            compact.WriteByte((byte)(Endpoints[i].Port >> 8)); compact.WriteByte((byte)Endpoints[i].Port);
                        }
                        response["nodes"] = new BEncodedString(compact.ToArray());
                    }
                    var reply = new BEncodedDictionary { ["t"] = query["t"], ["y"] = new BEncodedString("r"), ["r"] = response };
                    byte[] bytes = reply.Encode();
                    if (method == "get_peers")
                    {
                        response["values"] = new BEncodedList { new BEncodedString(new byte[] { 192, 0, 2, 1, 0, 9 }) };
                        await spoof.SendAsync(reply.Encode(), packet.RemoteEndPoint, _stop.Token);
                        await Task.Delay(20, _stop.Token);
                    }
                    await node.SendAsync(bytes, packet.RemoteEndPoint, _stop.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            foreach (var node in _nodes) node.Dispose();
            await Task.WhenAll(_workers);
            _stop.Dispose();
        }
    }
}
