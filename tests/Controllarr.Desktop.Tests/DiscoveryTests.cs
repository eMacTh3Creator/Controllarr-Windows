using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Controllarr.Core.Engine;
using Controllarr.Core.Networking;
using Controllarr.Core.Persistence;
using Controllarr.Core.Services;
using MonoTorrent.BEncoding;
using MonoTorrent.Connections.Tracker;
using MonoTorrent.Trackers;
using ReusableTasks;

internal static class DiscoveryTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Minutes(double value) => Now = Now.AddMinutes(value);
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        var clock = new Clock();
        var monitor = new HealthMonitor(timeProvider: clock);
        var settings = new Settings { HealthStallMinutes = 5, HealthReannounceOnStall = true };
        int refreshes = 0;
        var magnet = new TorrentView { InfoHash = "short", Name = "Metadata fixture", State = TorrentState.DownloadingMetadata, HasMetadata = false };
        magnet.SetReannounceCallback(() =>
        {
            check(Task.Run(() => monitor.Snapshot()).Wait(2000), "health refresh callbacks execute outside the snapshot lock");
            refreshes++;
        });
        monitor.Tick(new[] { magnet }, settings);
        clock.Minutes(1);
        monitor.Tick(new[] { magnet }, settings);
        check(refreshes == 1 && monitor.Snapshot().Count == 0, "metadata peer refresh starts before the health escalation timeout");
        clock.Minutes(.5);
        monitor.Tick(new[] { magnet }, settings);
        check(refreshes == 1, "rediscovery does not reannounce on every two-second poll");
        clock.Minutes(1.5);
        monitor.Tick(new[] { magnet }, settings);
        check(refreshes == 2, "peer rediscovery retries with exponential backoff");
        clock.Minutes(2);
        monitor.Tick(new[] { magnet }, settings);
        check(monitor.Snapshot().Single().Reason == HealthReason.MetadataTimeout, "metadata mode reaches the health timeout instead of disappearing from monitoring");
        magnet.State = TorrentState.Downloading;
        magnet.HasMetadata = true;
        monitor.Tick(new[] { magnet }, settings);
        check(monitor.Snapshot().Count == 0, "metadata acquisition resets the stall clock");
        clock.Minutes(5);
        monitor.Tick(new[] { magnet }, settings);
        check(monitor.Snapshot().Single().Reason == HealthReason.NoPeers, "payload stalls still classify missing peers");
        magnet.DownloadedBytes = 1;
        monitor.Tick(new[] { magnet }, settings);
        check(monitor.Snapshot().Count == 0, "one-byte progress clears false stalls even below float progress resolution");
        magnet.State = TorrentState.Paused;
        monitor.Tick(new[] { magnet }, settings);
        check(monitor.Snapshot().Count == 0, "manually paused torrents have no stale health issues");
        settings.HealthReannounceOnStall = false;
        magnet.State = TorrentState.DownloadingMetadata;
        magnet.HasMetadata = false;
        monitor.Tick(new[] { magnet }, settings);
        int before = refreshes;
        clock.Minutes(60);
        monitor.Tick(new[] { magnet }, settings);
        check(refreshes == before && monitor.Snapshot().Single().Reason == HealthReason.MetadataTimeout, "disabled auto-reannounce still reports metadata health without network work");
        check(!PortWatcher.AutomaticCyclingAllowed(new Settings { PreferredListenPort = 53127 }, true), "a preferred VPN-forwarded port is not randomly abandoned during stalls");
        check(!PortWatcher.AutomaticCyclingAllowed(new Settings { TorrentNetwork = new() { ProxyEnabled = true } }, true) &&
            !PortWatcher.AutomaticCyclingAllowed(new Settings(), false) && PortWatcher.AutomaticCyclingAllowed(new Settings(), true),
            "automatic port cycling respects proxy/missing-network guards and remains available without a fixed port");
        check(TorrentEngine.ValidateTrackerUrls(new[] { "https://tracker.invalid/Case", "https://tracker.invalid/case" }).Length == 2,
            "tracker path/passkey case is preserved during deduplication");

        await TestDnsAsync(check);
        await TestTrackerContractAsync(check);
        if (OperatingSystem.IsWindows()) await TestMagnetTransferAsync(check);
    }

    private sealed class TrackerTransport : ITrackerConnection
    {
        public bool CanScrape => true;
        public Uri Uri => new("https://tracker.invalid/announce");
        public bool Fail = true;
        public bool RejectWithOkState;
        public async ReusableTask<AnnounceResponse> AnnounceAsync(AnnounceRequest request, CancellationToken token)
        {
            await Task.CompletedTask;
            return RejectWithOkState ? new AnnounceResponse(TrackerState.Ok, failureMessage: "fixture rejected announce")
                : Fail ? new AnnounceResponse(TrackerState.Offline, failureMessage: "fixture offline")
                : new AnnounceResponse(TrackerState.Ok, minUpdateInterval: TimeSpan.FromSeconds(1), updateInterval: TimeSpan.FromHours(1));
        }
        public async ReusableTask<ScrapeResponse> ScrapeAsync(ScrapeRequest request, CancellationToken token)
        { await Task.CompletedTask; return new ScrapeResponse(TrackerState.Offline, failureMessage: "fixture scrape offline"); }
    }

    private static async Task TestTrackerContractAsync(Action<bool, string> check)
    {
        var transport = new TrackerTransport();
        var tracker = new ReliableTracker(transport);
        var request = new AnnounceRequest(new MonoTorrent.InfoHashes(new MonoTorrent.InfoHash(new byte[20]), null));
        bool failed = false;
        try { await tracker.AnnounceAsync(request, CancellationToken.None); } catch (IOException) { failed = true; }
        check(failed && tracker.Status == TrackerState.Offline && tracker.FailureMessage == "fixture offline",
            "offline tracker responses trigger tier failover while preserving the original error for diagnosis");
        transport.RejectWithOkState = true;
        failed = false;
        try { await tracker.AnnounceAsync(request, CancellationToken.None); } catch (IOException) { failed = true; }
        check(failed && tracker.Status == TrackerState.Offline && tracker.FailureMessage == "fixture rejected announce",
            "HTTP failure-reason responses trigger failover even when the transport marks them OK");
        transport.RejectWithOkState = false;
        transport.Fail = false;
        await tracker.AnnounceAsync(request, CancellationToken.None);
        check(tracker.MinUpdateInterval == TimeSpan.FromSeconds(1) && tracker.UpdateInterval == TimeSpan.FromHours(1),
            "tracker adapter honors the server's successful minimum and regular announce intervals");
        tracker.RequestEarlyRefresh();
        check(tracker.UpdateInterval == TimeSpan.FromHours(1), "early rediscovery never bypasses the tracker minimum interval");
        await Task.Delay(1100);
        tracker.RequestEarlyRefresh();
        check(tracker.UpdateInterval == TimeSpan.FromSeconds(1), "explicit rediscovery can refresh after the minimum instead of waiting the full regular interval");
        await tracker.AnnounceAsync(request, CancellationToken.None);
        check(tracker.UpdateInterval == TimeSpan.FromHours(1), "one early tracker refresh restores the normal announce cadence");
    }

    private static byte[] DnsAnswer(byte[] request, bool truncated = false)
    {
        var answer = request.Concat(truncated ? Array.Empty<byte>() : new byte[]
            { 0xc0, 0x0c, 0, 1, 0, 1, 0, 0, 0, 30, 0, 4, 192, 0, 2, 7 }).ToArray();
        answer[2] = truncated ? (byte)0x83 : (byte)0x81;
        answer[3] = 0x80;
        answer[7] = truncated ? (byte)0 : (byte)1;
        return answer;
    }

    private static async Task TestDnsAsync(Action<bool, string> check)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var policy = new TorrentNetworkPolicy(new Settings());
        using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)udp.LocalEndPoint!).Port;
        var udpOnly = Task.Run(async () =>
        {
            var buffer = new byte[512];
            var received = await udp.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token);
            var response = DnsAnswer(buffer[..received.ReceivedBytes]);
            await udp.SendToAsync(response, SocketFlags.None, received.RemoteEndPoint, timeout.Token);
        });
        var addresses = await TunnelDns.ResolveAsync(policy, IPAddress.Loopback, "fixture.invalid", timeout.Token, port);
        await udpOnly;
        check(addresses.Single().Equals(IPAddress.Parse("192.0.2.7")), "tunnel DNS resolves through a UDP-only server without system DNS or TCP");

        var tcp = new TcpListener(IPAddress.Loopback, port); tcp.Start();
        try
        {
            var fallback = Task.Run(async () =>
            {
                var buffer = new byte[512];
                var received = await udp.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token);
                await udp.SendToAsync(DnsAnswer(buffer[..received.ReceivedBytes], true), SocketFlags.None, received.RemoteEndPoint, timeout.Token);
                using var accepted = await tcp.AcceptTcpClientAsync(timeout.Token);
                using var stream = accepted.GetStream();
                var length = new byte[2]; await stream.ReadExactlyAsync(length, timeout.Token);
                var request = new byte[BinaryPrimitives.ReadUInt16BigEndian(length)]; await stream.ReadExactlyAsync(request, timeout.Token);
                var response = DnsAnswer(request);
                BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)response.Length);
                await stream.WriteAsync(length, timeout.Token); await stream.WriteAsync(response, timeout.Token);
            });
            addresses = await TunnelDns.ResolveAsync(policy, IPAddress.Loopback, "fixture.invalid", timeout.Token, port);
            await fallback;
            check(addresses.Single().Equals(IPAddress.Parse("192.0.2.7")), "truncated UDP DNS retries over policy-owned TCP");
        }
        finally { tcp.Stop(); }
        using var closed = new TorrentNetworkPolicy(new Settings { VpnEnabled = true, VpnInterfaceId = "missing-discovery-fixture" });
        bool denied = false;
        try { await TunnelDns.ResolveAsync(closed, IPAddress.Loopback, "fixture.invalid", timeout.Token, port); }
        catch (IOException) { denied = true; }
        check(denied, "UDP-first DNS still fails closed without the VPN adapter");
    }

    private static async Task WaitAsync(Func<bool> predicate, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(message);
            await Task.Delay(100);
        }
    }

    private static async Task TestMagnetTransferAsync(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "ControllarrDiscoveryTests", Guid.NewGuid().ToString("N"));
        string seed = Path.Combine(root, "seed"), download = Path.Combine(root, "download");
        Directory.CreateDirectory(seed); Directory.CreateDirectory(download);
        var settings = new Settings { PeerDiscovery = new() { DhtEnabled = false, LsdEnabled = false },
            TorrentNetwork = new() { Encryption = "Disable" }, ConnectionLimits = new() { GlobalMaxConnections = 20 } };
        byte[] payload = Enumerable.Range(0, 65536).Select(i => (byte)(i % 251)).ToArray();
        var pieces = Enumerable.Range(0, 4).SelectMany(i => SHA1.HashData(payload.AsSpan(i * 16384, 16384))).ToArray();
        var info = new BEncodedDictionary { ["name"] = new BEncodedString("metadata-fixture.bin"), ["length"] = new BEncodedNumber(payload.Length),
            ["piece length"] = new BEncodedNumber(16384), ["pieces"] = new BEncodedString(pieces) };
        string torrentPath = Path.Combine(root, "fixture.torrent");
        File.WriteAllBytes(torrentPath, new BEncodedDictionary { ["info"] = info }.Encode());
        File.WriteAllBytes(Path.Combine(seed, "metadata-fixture.bin"), payload);
        using var seeder = new TorrentEngine(seed, Path.Combine(root, "seed-resume"), FreePort(), settings);
        await seeder.ApplyAdvancedSettingsAsync(settings);
        string hash = await seeder.AddTorrentFile(torrentPath);
        await WaitAsync(() => seeder.GetStats(hash)?.State == TorrentState.Seeding, "Fixture seeder did not initialize");
        using var tracker = new LocalTracker(seeder.ListenPort);
        using var engine = new TorrentEngine(download, Path.Combine(root, "download-resume"), FreePort(), settings);
        await engine.ApplyAdvancedSettingsAsync(settings);
        string magnet = $"magnet:?xt=urn:btih:{hash}";
        string added = await engine.AddMagnet(magnet);
        await WaitAsync(() => engine.GetStats(added)?.State == TorrentState.DownloadingMetadata, "Bare magnet did not enter metadata mode");
        check(engine.GetStats(added)!.StatusReason.Contains("No usable peer-discovery source"), "trackerless magnet explains missing discovery instead of claiming zero seeders");
        string merged = await engine.AddMagnet(magnet + "&tr=" + Uri.EscapeDataString(tracker.Url));
        check(merged == added && engine.PollStats().Length == 1 && engine.GetTrackers(added)!.Length == 1,
            "re-adding a magnet merges its source tracker without duplicating the torrent");
        await WaitAsync(() => engine.GetStats(added)?.Progress >= 1, "Magnet did not acquire metadata and transfer the controlled payload");
        check(File.ReadAllBytes(Path.Combine(download, "metadata-fixture.bin")).SequenceEqual(payload) && tracker.Requests > 0,
            "real loopback HTTP tracker + peer retrieves magnet metadata and a hash-verified payload end to end");
        await engine.Pause(added);
        check(!engine.RequestReannounce(added) && !await engine.Reannounce(added), "automatic/manual rediscovery never resumes a manually paused torrent");
        await engine.AddMagnet(magnet + "&tr=" + Uri.EscapeDataString(tracker.Url));
        check(engine.GetStats(added)!.Paused && engine.GetTrackers(added)!.Length == 1, "duplicate tracker merge preserves pause and deduplicates existing URLs");
        await engine.SaveEngineStateAsync();
        await engine.Shutdown();
        using var restored = new TorrentEngine(download, Path.Combine(root, "download-resume"), FreePort(), settings);
        check(restored.GetTrackers(added)!.Length == 1 && restored.GetStats(added)!.Paused, "merged trackers and pause survive restart");
        var groupedSettings = new Settings { CreateTorrentSubfolders = true,
            PeerDiscovery = new() { DhtEnabled = false, LsdEnabled = false },
            TorrentNetwork = new() { Encryption = "Disable" }, ConnectionLimits = new() { GlobalMaxConnections = 20 } };
        string groupedDownload = Path.Combine(root, "grouped-download"), groupedResume = Path.Combine(root, "grouped-resume");
        string groupedPath;
        using (var grouped = new TorrentEngine(groupedDownload, groupedResume, FreePort(), groupedSettings))
        {
            await grouped.ApplyAdvancedSettingsAsync(groupedSettings);
            string groupedHash = await grouped.AddMagnet(magnet + "&tr=" + Uri.EscapeDataString(tracker.Url));
            groupedPath = Path.Combine(groupedDownload, StoragePaths.TorrentFolder(null, groupedHash));
            await WaitAsync(() => grouped.GetStats(groupedHash)?.Progress >= 1, "Grouped magnet did not retrieve the controlled payload");
            check(StoragePaths.Equal(grouped.GetStats(groupedHash)!.SavePath, groupedPath)
                && File.ReadAllBytes(Path.Combine(groupedPath, "metadata-fixture.bin")).SequenceEqual(payload)
                && !File.Exists(Path.Combine(groupedDownload, "metadata-fixture.bin")),
                "nameless magnet metadata and verified payload download stay inside their captured torrent folder");
            await grouped.Pause(groupedHash);
            await grouped.Shutdown();
        }
        using (var groupedRestored = new TorrentEngine(groupedDownload, groupedResume, FreePort(), groupedSettings))
        {
            check(StoragePaths.Equal(groupedRestored.GetStats(added)!.SavePath, groupedPath) && groupedRestored.GetStats(added)!.Progress == 1,
                "a completed nameless magnet restores verified data from the same hash folder after restart");
            await groupedRestored.Shutdown();
        }
        int attempts = 0;
        bool FailFirst() => Interlocked.Increment(ref attempts) == 1;
        using var first = new LocalTracker(seeder.ListenPort, FailFirst);
        using var second = new LocalTracker(seeder.ListenPort, FailFirst);
        string failoverPath = Path.Combine(root, "failover.torrent");
        File.WriteAllBytes(failoverPath, new BEncodedDictionary { ["info"] = info,
            ["announce-list"] = new BEncodedList { new BEncodedList { new BEncodedString(first.Url), new BEncodedString(second.Url) } } }.Encode());
        using (var failover = new TorrentEngine(Path.Combine(root, "failover"), Path.Combine(root, "failover-resume"), FreePort(), settings))
        {
            await failover.ApplyAdvancedSettingsAsync(settings);
            string failedFirst = await failover.AddTorrentFile(failoverPath);
            try { await WaitAsync(() => failover.GetStats(failedFirst)?.Progress >= 1, "Tier did not fail over to the healthy tracker"); }
            catch
            {
                Console.WriteLine("FAILOVER DIAGNOSTICS " + System.Text.Json.JsonSerializer.Serialize(new {
                    torrent = failover.GetStats(failedFirst), trackers = failover.GetTrackers(failedFirst),
                    seed = seeder.GetStats(hash), first = first.Requests, second = second.Requests }));
                throw;
            }
            check(first.Requests > 0 && second.Requests > 0 && File.ReadAllBytes(Path.Combine(root, "failover", "metadata-fixture.bin")).SequenceEqual(payload),
                "a rejected announce fails over within the same tracker tier and retrieves a verified payload");
        }
        await TestSelectiveQueueAsync(root, settings, check);
    }

    private static async Task TestSelectiveQueueAsync(string root, Settings settings, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "selective");
        Directory.CreateDirectory(Path.Combine(folder, "pack"));
        byte[] selected = Enumerable.Repeat((byte)1, 16384).ToArray(), skipped = Enumerable.Repeat((byte)2, 16384).ToArray();
        File.WriteAllBytes(Path.Combine(folder, "pack", "a.bin"), selected);
        var files = new BEncodedList();
        foreach (string name in new[] { "a.bin", "b.bin" })
            files.Add(new BEncodedDictionary { ["length"] = new BEncodedNumber(16384), ["path"] = new BEncodedList { new BEncodedString(name) } });
        var info = new BEncodedDictionary { ["name"] = new BEncodedString("pack"), ["files"] = files,
            ["piece length"] = new BEncodedNumber(16384), ["pieces"] = new BEncodedString(SHA1.HashData(selected).Concat(SHA1.HashData(skipped)).ToArray()) };
        string path = Path.Combine(root, "selective.torrent");
        File.WriteAllBytes(path, new BEncodedDictionary { ["info"] = info }.Encode());
        using var engine = new TorrentEngine(folder, Path.Combine(root, "selective-resume"), FreePort(), settings);
        engine.ConfigureQueue(new TorrentQueueing { Enabled = true, MaxActiveDownloads = 1, MaxActiveSeeds = 0, MaxActiveTotal = 1 });
        string hash = await engine.AddTorrentFile(path);
        await engine.SetFilePriorities(new[] { 3, 0 }, hash);
        await engine.TickQueueAsync(true);
        await WaitAsync(() => engine.GetStats(hash)?.Progress >= 1, "Selected fixture file did not complete hashing");
        string next = await engine.AddMagnet("magnet:?xt=urn:btih:" + new string('e', 40));
        await engine.TickQueueAsync(true);
        await WaitAsync(() => engine.GetStats(next)?.State == TorrentState.DownloadingMetadata, "Completed selected files consumed the only download slot");
        check(engine.GetStats(hash)!.Progress == 1 && engine.GetStats(hash)!.State == TorrentState.Queued &&
            engine.GetStats(next)!.State == TorrentState.DownloadingMetadata, "selective completion releases the active download slot even with skipped payload files");
    }

    private static ushort FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        ushort port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop(); return port;
    }

    private sealed class LocalTracker : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);
        public string Url { get; }
        public LocalTracker(ushort seedPort, Func<bool>? fail = null)
        {
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/announce";
            var peer = new byte[] { 127, 0, 0, 1, (byte)(seedPort >> 8), (byte)seedPort };
            byte[] response = new BEncodedDictionary { ["interval"] = new BEncodedNumber(1), ["min interval"] = new BEncodedNumber(1),
                ["complete"] = new BEncodedNumber(1), ["incomplete"] = new BEncodedNumber(0), ["peers"] = new BEncodedString(peer) }.Encode();
            _loop = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                        using var stream = client.GetStream();
                        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                        while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 }) { }
                        Interlocked.Increment(ref _requests);
                        byte[] body = fail?.Invoke() == true ? new BEncodedDictionary { ["failure reason"] = new BEncodedString("fixture first tracker rejected announce") }.Encode() : response;
                        byte[] headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(headers, _stop.Token); await stream.WriteAsync(body, _stop.Token);
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (IOException) when (_stop.IsCancellationRequested) { }
                catch (SocketException) when (_stop.IsCancellationRequested) { }
            });
        }
        public void Dispose() { _stop.Cancel(); _listener.Stop(); _loop.GetAwaiter().GetResult(); _stop.Dispose(); }
    }
}
