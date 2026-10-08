using System.Security.Cryptography;
using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Controllarr.Core.Desktop;
using Controllarr.Core.Engine;
using Controllarr.Core.Persistence;
using MonoTorrent.BEncoding;

internal static class QueueTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var limits = new TorrentQueueing { Enabled = true, MaxActiveDownloads = 2, MaxActiveSeeds = 2, MaxActiveTotal = 1 };
        var candidates = new[] { new QueueCandidate("old-seed", 1, true, false, false, false), new QueueCandidate("new-download", 2001, false, false, false, false) };
        var plan = QueuePlanner.Plan(candidates, limits);
        check(plan.Active.SetEquals(new[] { "new-download" }) && plan.WaitingReasons["old-seed"].Contains("total active"), "older seeds cannot starve a new download under the total-active cap");
        check(QueuePlanner.Select(candidates.Append(new("forced-seed", 0, true, false, false, true)), limits).SetEquals(new[] { "forced-seed" }), "forced seeds count toward normal active caps and retain explicit precedence");
        limits.MaxActiveDownloads = 0;
        check(QueuePlanner.Plan(candidates, limits).WaitingReasons["new-download"].Contains("downloads is set to 0"), "zero download caps have an explicit waiting reason");
        var connections = Enumerable.Range(0, 1000).Select(i => new ConnectionCandidate($"seed-{i}", i + 1, true, false, 60))
            .Append(new("download", 1001, false, false, 60)).ToArray();
        var budget = ConnectionPlanner.Plan(connections, 200, 25);
        check(budget.SeedCaps.Count == 150 && budget.SeedCaps.Values.Sum() == 150 && budget.WaitingReasons.Count == 850,
            "1,000 seeds share a bounded budget while 25% of 200 connections is kept available to downloads");
        check(ConnectionPlanner.Plan(connections, 200, 0).SeedCaps.Count == 0 && ConnectionPlanner.Plan(connections.Where(c => c.Complete), 200, 25).SeedCaps.Count == 0,
            "zero reserve or no downloads restores normal seed limits");
        check(ConnectionPlanner.Plan(connections, 1, 25).WaitingReasons.Count == 1000, "tiny global limits reserve a download slot without dividing by zero");
        budget = ConnectionPlanner.Plan(new[] { new ConnectionCandidate("seed-a", 1, true, false, 2), new("seed-b", 2, true, false, 60), new("forced-seed", 3, true, true, 10), new("download", 4, false, false, 60) }, 100, 25);
        check(budget.SeedCaps["seed-a"] == 2 && budget.SeedCaps.Values.Sum() <= 65 && !budget.SeedCaps.ContainsKey("forced-seed"), "seed budgeting honors explicit lower caps and accounts for forced seeds without rewriting their controls");
        bool invalid = false;
        try { Controllarr.Core.Networking.TorrentNetworkPolicy.Validate(new Settings { ConnectionLimits = new() { DownloadReservePercent = 91 } }); }
        catch (ArgumentException) { invalid = true; }
        check(invalid && JsonSerializer.Deserialize<ConnectionLimits>("{\"global_max_connections\":500}")!.DownloadReservePercent == 25,
            "reserve validation rejects invalid values and old settings migrate without losing the global cap");
        string Explain(TorrentState state = TorrentState.Downloading, int peers = 0, int global = 200, int perLimit = 60,
            string? blocked = null, string? queued = null) => TransferStatus.Describe(state, false, 1024, 0, 0, peers, perLimit, global, 200, blocked, queued);
        check(Explain().Contains("Global connection limit reached") && Explain(global: 100).Contains("seed availability is unconfirmed"), "full global budgets are distinguished from unconfirmed seed availability");
        check(Explain(peers: 2, global: 100, perLimit: 2).Contains("Per-torrent connection limit reached") && Explain(peers: 2, global: 100).Contains("Peers connected but not sending"), "peer stalls distinguish a torrent-specific cap from idle connected peers");
        check(Explain(TorrentState.Queued, queued: "Waiting for slot") == "Waiting for slot" && Explain(TorrentState.Queued, blocked: "VPN blocked") == "VPN blocked", "safety and queue explanations take precedence over peer-cap warnings");
        check(Explain(TorrentState.DownloadingMetadata, global: 100).Contains("magnet metadata") && new TrackerInfo().SeedCountText == "Unknown", "metadata waits and missing tracker scrape data are never misreported as zero swarm seeds");
        var row = new TorrentRow(new TorrentStats { InfoHash = "row", StatusReason = "one" });
        int notified = 0;
        row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TorrentRow.StatusReason)) notified++; };
        check(!row.Update(new TorrentStats { InfoHash = "row", StatusReason = "two" }) && notified == 1, "live waiting reasons update without triggering a catalog filter reset");
        if (!OperatingSystem.IsWindows()) return;

        string root = Path.Combine(Path.GetTempPath(), "ControllarrQueueTests", Guid.NewGuid().ToString("N"));
        string downloads = Path.Combine(root, "downloads"), resume = Path.Combine(root, "resume");
        Directory.CreateDirectory(downloads);
        string Fixture(int index)
        {
            byte[] bytes = Enumerable.Repeat((byte)index, 16384).ToArray();
            string name = $"queue-{index}.bin";
            File.WriteAllBytes(Path.Combine(downloads, name), bytes);
            var info = new BEncodedDictionary { ["name"] = new BEncodedString(name), ["length"] = new BEncodedNumber(bytes.Length),
                ["piece length"] = new BEncodedNumber(16384), ["pieces"] = new BEncodedString(SHA1.HashData(bytes)) };
            string path = Path.Combine(root, name + ".torrent");
            File.WriteAllBytes(path, new BEncodedDictionary { ["info"] = info }.Encode());
            return path;
        }
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        ushort port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using (var engine = new TorrentEngine(downloads, resume, port))
        {
            engine.ApplyTuning(4, false, false);
            var hashes = new List<string>();
            for (int i = 1; i <= 4; i++) hashes.Add(await engine.ImportTorrentFileAsync(Fixture(i), downloads, persist: false));
            await engine.SetAdvancedOptionsAsync(hashes[3], 2, null, false, persist: false);
            await engine.Remove(hashes[1], false);
            check(engine.PollStats().Select(s => s.QueuePosition).Order().SequenceEqual(new long[] { 1, 2, 3 }) && engine.GetOptions(hashes[3]).MaximumConnections == 2,
                "single removal compacts queue order without losing per-torrent controls");
            for (int i = 0; i < 100; i++) engine.GetOptions($"nonexistent-{i}");
            string added = await engine.ImportTorrentFileAsync(Fixture(5), downloads, persist: false);
            hashes.RemoveAt(1); hashes.Add(added);
            check(engine.GetOptions(added).Position == 4, "unknown option probes do not create phantom queue positions");
            foreach (string hash in hashes) await engine.Resume(hash);
            for (int i = 0; i < 400 && engine.PollStats().Any(t => t.State != TorrentState.Seeding); i++) await Task.Delay(50);
            check(engine.PollStats().All(t => t.State == TorrentState.Seeding), "connection-budget fixtures complete startup before enforcing seed caps");
            using var peer1 = new TcpClient();
            using var peer2 = new TcpClient();
            async Task ConnectPeerAsync(TcpClient client, int id)
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                byte[] handshake = new byte[68];
                handshake[0] = 19;
                Encoding.ASCII.GetBytes("BitTorrent protocol").CopyTo(handshake, 1);
                Convert.FromHexString(hashes[0]).CopyTo(handshake, 28);
                Encoding.ASCII.GetBytes("-CTTEST-" + id.ToString("D12")).CopyTo(handshake, 48);
                await client.GetStream().WriteAsync(handshake);
                // The fixture advertises one missing piece and interest in the seed.
                await client.GetStream().WriteAsync(new byte[] { 0, 0, 0, 2, 5, 0, 0, 0, 0, 1, 2 });
            }
            await ConnectPeerAsync(peer1, 1);
            await ConnectPeerAsync(peer2, 2);
            for (int i = 0; i < 200 && engine.GetStats(hashes[0])!.NumPeers < 2; i++) await Task.Delay(25);
            check(engine.GetStats(hashes[0])!.NumPeers == 2, "connection-budget test establishes two real loopback BitTorrent peer connections");
            string magnet = await engine.AddMagnet("magnet:?xt=urn:btih:" + new string('c', 40), persist: false);
            await engine.TickQueueAsync(true);
            check(engine.GetStats(hashes[0])!.NumPeers <= 1, "tightening a seed budget releases existing peers, not just future connections");
            check(engine.PollStats().Count(s => s.State == TorrentState.Queued) == 1 && hashes.Take(3).All(h => engine.EffectiveTorrentSettings(h)!.MaximumConnections == 1),
                "real engine applies seed peer caps and queues excess seeds while a magnet needs peers");
            await engine.Pause(magnet);
            await engine.TickQueueAsync(true);
            check(engine.GetStats(magnet)!.Paused && hashes.All(h => engine.EffectiveTorrentSettings(h)!.MaximumConnections == (h == hashes[2] ? 2 : 60)),
                "pausing the last download releases seed headroom without resuming the paused download");
            for (int i = 0; i < 400 && engine.GetStats(hashes[0])!.State != TorrentState.Seeding; i++) await Task.Delay(25);
            using var peer3 = new TcpClient();
            await ConnectPeerAsync(peer3, 3);
            for (int i = 0; i < 200 && engine.GetStats(hashes[0])!.NumPeers == 0; i++) await Task.Delay(25);
            check(engine.GetStats(hashes[0])!.NumPeers == 1, "manual-pause budget fixture reconnects a real loopback peer");
            await engine.Pause(hashes[0]);
            await engine.TickQueueAsync(true);
            check(engine.GetStats(hashes[0])!.Paused && engine.GetStats(hashes[0])!.NumPeers == 0,
                "paused torrents release retained peer sockets on the next queue pass without resuming");
            await engine.Shutdown();
            var legacy = hashes.Select((h, i) => new KeyValuePair<string, TorrentOptions>(h, engine.GetOptions(h) with { Position = 2000 + i * 10 }))
                .Append(new("removed-hash", new TorrentOptions { Position = long.MaxValue })).ToDictionary(p => p.Key, p => p.Value);
            File.WriteAllText(Path.Combine(resume, "engine.state.options.json"), JsonSerializer.Serialize(legacy));
        }
        using (var restored = new TorrentEngine(downloads, resume, 0))
        {
            restored.ApplyTuning(4, false, false);
            check(restored.PollStats().Select(s => s.QueuePosition).Order().SequenceEqual(new long[] { 1, 2, 3, 4, 5 }), "startup repairs legacy queue gaps and discards stale option entries");
            await restored.RemoveManyAsync(restored.PollStats().Select(t => t.InfoHash), false);
            string next = await restored.ImportTorrentFileAsync(Fixture(6), downloads, persist: false);
            check(restored.GetStats(next)!.QueuePosition == 1, "emptying the library resets the next added torrent to queue position 1");
            await restored.Shutdown();
        }
    }
}
