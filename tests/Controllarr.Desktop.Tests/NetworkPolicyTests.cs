using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Controllarr.Core.Networking;
using Controllarr.Core.Persistence;
using MonoTorrent;
using MonoTorrent.Trackers;

internal static class NetworkPolicyTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var list = new IpBlocklist(new[] { "192.0.2.0/24", "192.0.2.128/25 # overlapping", "2001:db8::/32", "198.51.100.4" });
        check(list.Count == 3 && list.Contains(IPAddress.Parse("192.0.2.255")) && !list.Contains(IPAddress.Parse("192.0.3.1")), "IP blocklist merges overlaps and respects IPv4 boundaries");
        check(list.Contains(IPAddress.Parse("2001:db8::99")) && !list.Contains(IPAddress.Parse("2001:db9::1")), "IP blocklist supports IPv6 CIDRs");
        bool invalid = false;
        try { _ = new IpBlocklist(new[] { "192.0.2.1/33" }); } catch (ArgumentException) { invalid = true; }
        check(invalid, "malformed blocklists fail validation instead of silently disabling protection");
        check(new IpBlocklist(new[] { "0.0.0.0/0" }).Contains(IPAddress.Parse("127.0.0.1")), "IPv4 catch-all CIDR blocks loopback too");

        var unavailable = new Settings { VpnEnabled = true, VpnInterfaceId = "missing-test-adapter", VpnBindInterface = false, VpnKillSwitch = false };
        using var closed = new TorrentNetworkPolicy(unavailable);
        check(!closed.Allowed && closed.RestrictedDiscovery, "VPN mode fails closed even with legacy pause/bind flags disabled");
        var factories = TorrentNetworkFactories.Create(closed);
        var hash = new InfoHashes(new InfoHash(new byte[20]), null);
        var announce = new AnnounceRequest(hash).WithPeerId(new byte[20]).WithReportedEndPointFunc(_ => (null, 49152));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var trap = new TcpListener(IPAddress.Loopback, 0); trap.Start();
        int trapPort = ((IPEndPoint)trap.LocalEndpoint).Port;
        bool denied = false;
        using (var peer = factories.CreatePeerConnection(new Uri("ipv4://127.0.0.1:9"))!)
            try { await peer.ConnectAsync(); } catch (IOException) { denied = true; }
        check(denied, "real peer factory cannot connect when VPN is unavailable");
        var tracker = factories.CreateTracker(new Uri($"http://127.0.0.1:{trapPort}/announce"))!;
        var response = await tracker.AnnounceAsync(announce, timeout.Token);
        check(response.State != TrackerState.Ok && !trap.Pending(), "real HTTP tracker factory fails closed without reaching a listening unbound destination");
        var udp = factories.CreateTracker(new Uri("udp://127.0.0.1:9"))!;
        check((await udp.AnnounceAsync(announce, timeout.Token)).State != TrackerState.Ok, "real UDP tracker factory fails closed");
        using (var http = factories.CreateHttpClient())
        {
            denied = false;
            try { await http.GetAsync($"http://127.0.0.1:{trapPort}/", timeout.Token); } catch (HttpRequestException) { denied = true; }
            check(denied && !trap.Pending(), "webseed HTTP client cannot bypass the VPN gate to a listening server");
        }
        trap.Stop();
        var listener = factories.CreatePeerConnectionListener(new IPEndPoint(IPAddress.Any, 0));
        listener.Start();
        check(listener.LocalEndPoint == null, "incoming peer listener stays closed with no VPN adapter");
        listener.Stop();
        check(factories.CreateDht().NodeCount == 0 && !factories.CreatePortForwarder().Active, "protected factories disable independent DHT and NAT transports");

        // Use a real guest adapter for socket-option tests only, not as evidence of a VPN tunnel.
        var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up &&
            n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.GetIPProperties().UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork));
        if (OperatingSystem.IsWindows() && adapter != null)
        {
            using var pinned = new TorrentNetworkPolicy(new Settings { VpnEnabled = true, VpnInterfaceId = adapter.Id });
            using var tcp = pinned.CreateSocket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            using var datagram = pinned.CreateSocket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            check(Equals(((IPEndPoint)tcp.LocalEndPoint!).Address, pinned.Adapter!.Address) &&
                (int)tcp.GetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31)! == pinned.Adapter.Index,
                "Windows TCP sockets are source-bound and pinned using IP_UNICAST_IF");
            check(Equals(((IPEndPoint)datagram.LocalEndPoint!).Address, pinned.Adapter.Address), "Windows UDP sockets use the same pinned source address");
            var incomingServer = new TcpListener(pinned.Adapter.Address, 0); incomingServer.Start();
            using var incomingClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await incomingClient.ConnectAsync(incomingServer.LocalEndpoint, timeout.Token);
            using var incoming = await incomingServer.AcceptSocketAsync(timeout.Token);
            pinned.RegisterIncoming(incoming);
            check((int)incoming.GetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31)! == pinned.Adapter.Index,
                "accepted VPN peer sockets are pinned to the same interface for replies");
            incomingServer.Stop();
            denied = false;
            try { using var v6 = pinned.CreateSocket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp); } catch (IOException) { denied = true; }
            check(denied, "IPv6 cannot bypass a protected IPv4 adapter");
            pinned.RequireRestart();
            check(!pinned.Allowed && tcp.SafeHandle.IsClosed && datagram.SafeHandle.IsClosed, "network topology changes close existing TCP/UDP sockets and latch the session closed");
        }

        using var standard = new TorrentNetworkPolicy(new Settings());
        var tcpServer = new TcpListener(IPAddress.Loopback, 0); tcpServer.Start();
        int tcpPort = ((IPEndPoint)tcpServer.LocalEndpoint).Port;
        using var client = await standard.ConnectAsync("127.0.0.1", tcpPort, timeout.Token);
        using var accepted = await tcpServer.AcceptSocketAsync(timeout.Token);
        check(client.Connected, "unprotected mode still supports ordinary TCP peers");
        standard.RequireRestart();
        check(client.SafeHandle.IsClosed && !standard.Allowed, "existing unprotected sockets also close when security settings change");
        tcpServer.Stop();

        using (var cancelledPolicy = new TorrentNetworkPolicy(new Settings()))
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            bool cancelled = false;
            try { using var cancelledSocket = await cancelledPolicy.ConnectAsync("127.0.0.1", tcpPort, cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && cancelledPolicy.OpenSocketCount == 0, "cancelled peer connections release their policy-owned sockets");
        }

        var proxyServer = new TcpListener(IPAddress.Loopback, 0); proxyServer.Start();
        using var proxyPolicy = new TorrentNetworkPolicy(new Settings { TorrentNetwork = new() { ProxyEnabled = true, ProxyHost = "127.0.0.1", ProxyPort = ((IPEndPoint)proxyServer.LocalEndpoint).Port } });
        string? requested = null;
        var handshake = Task.Run(async () =>
        {
            using var proxySocket = await proxyServer.AcceptSocketAsync(timeout.Token);
            using var stream = new NetworkStream(proxySocket);
            var greeting = new byte[3]; await stream.ReadExactlyAsync(greeting, timeout.Token);
            await stream.WriteAsync(new byte[] { 5, 0 }, timeout.Token);
            var header = new byte[5]; await stream.ReadExactlyAsync(header, timeout.Token);
            var address = new byte[header[4] + 2]; await stream.ReadExactlyAsync(address, timeout.Token);
            requested = Encoding.ASCII.GetString(address, 0, address.Length - 2);
            await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 1 }, timeout.Token);
        });
        using (var proxied = await proxyPolicy.ConnectAsync("does-not-resolve.invalid", 80, timeout.Token)) { await handshake; }
        check(requested == "does-not-resolve.invalid", "SOCKS5 forwards destination hostnames for remote DNS without resolving locally");
        check((await TorrentNetworkFactories.Create(proxyPolicy).CreateTracker(new Uri("udp://127.0.0.1:9"))!.AnnounceAsync(announce, timeout.Token)).State != TrackerState.Ok,
            "SOCKS5 mode rejects UDP trackers instead of leaking directly");
        proxyServer.Stop();
        denied = false;
        try { using var failed = await proxyPolicy.ConnectAsync("127.0.0.1", tcpPort, timeout.Token); } catch (SocketException) { denied = true; }
        check(denied, "a failed SOCKS5 server never falls back to direct connections");

        var authServer = new TcpListener(IPAddress.Loopback, 0); authServer.Start();
        using var authPolicy = new TorrentNetworkPolicy(new Settings { TorrentNetwork = new() { ProxyEnabled = true, ProxyHost = "127.0.0.1", ProxyPort = ((IPEndPoint)authServer.LocalEndpoint).Port, ProxyUsername = "fixture-user", ProxyPassword = "fixture-secret" } });
        string? authenticated = null;
        var authWork = Task.Run(async () =>
        {
            using var socket = await authServer.AcceptSocketAsync(timeout.Token);
            using var stream = new NetworkStream(socket);
            var greeting = new byte[3]; await stream.ReadExactlyAsync(greeting, timeout.Token);
            await stream.WriteAsync(new byte[] { 5, 2 }, timeout.Token);
            var auth = new byte[2]; await stream.ReadExactlyAsync(auth, timeout.Token);
            var user = new byte[auth[1]]; await stream.ReadExactlyAsync(user, timeout.Token);
            var length = new byte[1]; await stream.ReadExactlyAsync(length, timeout.Token);
            var secret = new byte[length[0]]; await stream.ReadExactlyAsync(secret, timeout.Token);
            authenticated = Encoding.UTF8.GetString(user) + ":" + Encoding.UTF8.GetString(secret);
            await stream.WriteAsync(new byte[] { 1, 0 }, timeout.Token);
            var connect = new byte[10]; await stream.ReadExactlyAsync(connect, timeout.Token);
            await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 1 }, timeout.Token);
        });
        using (var authenticatedSocket = await authPolicy.ConnectAsync("127.0.0.1", 80, timeout.Token)) await authWork;
        check(authenticated == "fixture-user:fixture-secret", "SOCKS5 username/password authentication uses the configured credentials");
        authServer.Stop();

        var dnsReply = new byte[] { 0x12, 0x34, 0x81, 0x80, 0, 1, 0, 1, 0, 0, 0, 0, 1, (byte)'x', 0, 0, 1, 0, 1,
            0xc0, 0x0c, 0, 1, 0, 1, 0, 0, 0, 30, 0, 4, 192, 0, 2, 1 };
        check(TunnelDns.Parse(dnsReply, 0x1234).Single().Equals(IPAddress.Parse("192.0.2.1")), "VPN DNS parser accepts bounded IPv4 answers with compressed names");
        bool malformedDns = false;
        try { TunnelDns.Parse(dnsReply[..^2], 0x1234); } catch (IOException) { malformedDns = true; }
        check(malformedDns, "truncated VPN DNS records fail closed");

        using var udpServer = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udpServer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var udpWork = Task.Run(async () =>
        {
            for (int i = 0; i < 2; i++)
            {
                var bytes = new byte[256];
                var received = await udpServer.ReceiveFromAsync(bytes, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token);
                int action = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(8));
                var reply = new byte[action == 0 ? 16 : 20];
                BinaryPrimitives.WriteInt32BigEndian(reply, action);
                bytes.AsSpan(12, 4).CopyTo(reply.AsSpan(4));
                if (action == 0) reply[15] = 1;
                else BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(8), 60);
                await udpServer.SendToAsync(reply, SocketFlags.None, received.RemoteEndPoint, timeout.Token);
            }
        });
        using var udpPolicy = new TorrentNetworkPolicy(new Settings());
        var udpTracker = TorrentNetworkFactories.Create(udpPolicy).CreateTracker(new Uri($"udp://127.0.0.1:{((IPEndPoint)udpServer.LocalEndPoint!).Port}"))!;
        check((await udpTracker.AnnounceAsync(announce, timeout.Token)).State == TrackerState.Ok, "policy-owned UDP transport completes a real local BEP15 connect and announce");
        await udpWork;
    }
}
