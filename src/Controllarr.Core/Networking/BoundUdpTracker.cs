using System.Net;
using System.Net.Sockets;
using MonoTorrent;
using MonoTorrent.Connections.Tracker;
using MonoTorrent.Messages.UdpTracker;
using MonoTorrent.Trackers;
using ReusableTasks;

namespace Controllarr.Core.Networking;

/// <summary>Uses MonoTorrent's BEP15 codecs but only policy-owned, cancellable IPv4 sockets.</summary>
internal sealed class BoundUdpTracker(TorrentNetworkPolicy policy, Uri uri) : ITrackerConnection
{
    public bool CanScrape => true;
    public Uri Uri => uri;

    private async Task<Socket> OpenAsync(CancellationToken token)
    {
        if (policy.UsesProxy) throw new IOException("UDP trackers are disabled in SOCKS5 mode; use HTTP/HTTPS trackers.");
        var ips = await policy.ResolveAsync(uri.Host, token);
        var ip = ips.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? throw new IOException("UDP tracker has no IPv4 address.");
        policy.CheckDestination(ip);
        var socket = policy.CreateSocket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try { await socket.ConnectAsync(new IPEndPoint(ip, uri.Port), token); return socket; }
        catch { socket.Dispose(); throw; }
    }

    private static async Task<UdpTrackerMessage> ExchangeAsync(Socket socket, UdpTrackerMessage message, CancellationToken token)
    {
        var bytes = message.Encode();
        var buffer = new byte[65507];
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await socket.SendAsync(bytes, SocketFlags.None, token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5 * (attempt + 1)));
            try
            {
                while (true)
                {
                    int count = await socket.ReceiveAsync(buffer, SocketFlags.None, timeout.Token);
                    var response = UdpTrackerMessage.DecodeMessage(buffer.AsSpan(0, count), MessageType.Response, AddressFamily.InterNetwork);
                    if (response.TransactionId != message.TransactionId) continue;
                    if (response is ErrorMessage error) throw new IOException(error.Error ?? "Tracker rejected the request.");
                    return response;
                }
            }
            catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); }
        }
        throw new IOException("UDP tracker timed out.");
    }

    public async ReusableTask<AnnounceResponse> AnnounceAsync(AnnounceRequest request, CancellationToken token)
    {
        try
        {
            using var total = CancellationTokenSource.CreateLinkedTokenSource(token); total.CancelAfter(TimeSpan.FromSeconds(45));
            using var socket = await OpenAsync(total.Token);
            var connect = await ExchangeAsync(socket, new ConnectMessage(), total.Token) as ConnectResponseMessage ?? throw new IOException("Invalid tracker connect response.");
            var peers = new Dictionary<InfoHash, IList<MonoTorrent.PeerInfo>>();
            TimeSpan interval = TimeSpan.FromMinutes(30);
            foreach (var hash in new[] { request.InfoHashes.V1, request.InfoHashes.V2 }.Where(h => h != null))
            {
                var announce = await ExchangeAsync(socket, new AnnounceMessage(Random.Shared.Next(), connect.ConnectionId, request, hash!, request.GetReportedAddress("ipv4").port), total.Token)
                    as AnnounceResponseMessage ?? throw new IOException("Invalid tracker announce response.");
                peers[hash!] = announce.Peers;
                interval = announce.Interval;
            }
            return new AnnounceResponse(TrackerState.Ok, peers, minUpdateInterval: interval, updateInterval: interval);
        }
        catch (Exception ex) { return new AnnounceResponse(TrackerState.Offline, failureMessage: ex.Message); }
    }

    public async ReusableTask<ScrapeResponse> ScrapeAsync(ScrapeRequest request, CancellationToken token)
    {
        try
        {
            using var total = CancellationTokenSource.CreateLinkedTokenSource(token); total.CancelAfter(TimeSpan.FromSeconds(45));
            using var socket = await OpenAsync(total.Token);
            var connect = await ExchangeAsync(socket, new ConnectMessage(), total.Token) as ConnectResponseMessage ?? throw new IOException("Invalid tracker connect response.");
            var hashes = new[] { request.InfoHashes.V1, request.InfoHashes.V2 }.Where(h => h != null).Select(h => h!).ToList();
            var scrape = await ExchangeAsync(socket, new ScrapeMessage(Random.Shared.Next(), connect.ConnectionId, hashes), total.Token)
                as ScrapeResponseMessage ?? throw new IOException("Invalid tracker scrape response.");
            if (scrape.Scrapes.Count != hashes.Count) throw new IOException("Tracker scrape count mismatch.");
            var result = new Dictionary<InfoHash, ScrapeInfo>();
            for (int i = 0; i < hashes.Count; i++) result[hashes[i]] = new ScrapeInfo(scrape.Scrapes[i].Seeds, scrape.Scrapes[i].Complete, scrape.Scrapes[i].Leeches);
            return new ScrapeResponse(TrackerState.Ok, result);
        }
        catch (Exception ex) { return new ScrapeResponse(TrackerState.Offline, failureMessage: ex.Message); }
    }
}
