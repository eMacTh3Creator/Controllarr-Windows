using System.Diagnostics;
using Controllarr.Core.Desktop;
using Controllarr.Core.Engine;
using MonoTorrent.BEncoding;
using System.Security.Cryptography;
using Controllarr.Core.Persistence;
using Controllarr.Core.Services;
using System.Text;

int passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    passed++;
    Console.WriteLine($"PASS {message}");
}
TorrentStats Stats(string hash, string name = "Example", string? category = null,
    TorrentState state = TorrentState.Downloading, float progress = .5f, long rate = 100) => new()
    { InfoHash = hash, Name = name, Category = category, State = state, Progress = progress,
      Paused = state == TorrentState.Paused, DownloadRate = rate, TotalWanted = 1024, TotalDone = 512 };

await NetworkPolicyTests.RunAsync(Check);
await QueueTests.RunAsync(Check);

var catalog = new TorrentCatalog();
catalog.Reconcile(new[] { Stats("ABC", category: "Movies"), Stats("def", category: "TV") });
var first = catalog.Rows[0];
int notifications = 0;
first.PropertyChanged += (_, _) => notifications++;
Check(!catalog.Reconcile(new[] { Stats("abc", category: "Movies"), Stats("def", category: "TV") }), "unchanged polls do not request a view reset");
Check(ReferenceEquals(first, catalog.Rows[0]) && notifications == 0, "row identity and unchanged properties survive polling");
catalog.Reconcile(new[] { Stats("abc", category: "Movies", rate: 500), Stats("def", category: "TV") });
Check(first.DownloadRate == 500 && notifications == 1, "only changed properties notify the UI");
Check(new TorrentQuery("example", "movies", TorrentFilter.Downloading).Matches(first), "search/category/status filters compose case-insensitively");
Check(!new TorrentQuery(Category: "").Matches(first), "uncategorized filter does not include categorized torrents");
Check(new TorrentQuery(Category: "").Matches(new TorrentRow(Stats("none"))), "uncategorized filter includes unlabelled torrents");
Check(!new TorrentQuery(Category: "TV").Matches(first), "category filter excludes other categories");
Check(new TorrentQuery(State: TorrentFilter.Active).Matches(first), "active filter checks transfer rates");
Check(new TorrentQuery(State: TorrentFilter.Error).Matches(new TorrentRow(Stats("err", state: TorrentState.Error))), "error state is not discarded as unknown");
Check(new TorrentQuery(State: TorrentFilter.Paused).Matches(new TorrentRow(Stats("paused", state: TorrentState.Paused))), "paused filter works");
Check(catalog.Reconcile(new[] { Stats("abc", category: "Movies", progress: 1), Stats("def", category: "TV") }), "completion requests filter membership refresh");
Check(new TorrentQuery(State: TorrentFilter.Completed).Matches(first), "completed filter includes finished torrents");
catalog.Reconcile(new[] { Stats("def"), Stats("def"), Stats("new") });
Check(catalog.Rows.Count == 2 && catalog.Rows.All(r => r.InfoHash != "ABC"), "removals and duplicate snapshots reconcile correctly");

int active = 0, peak = 0, calls = 0;
var result = await TorrentBatch.RunAsync(new[] { "one", "ONE", "bad", "throws", "last" }, async hash =>
{
    calls++; active++; peak = Math.Max(peak, active);
    await Task.Delay(1);
    active--;
    if (hash == "throws") throw new IOException("simulated disk failure");
    return hash != "bad";
});
Check(calls == 4 && peak == 1, "bulk actions deduplicate hashes and never flood the engine with parallel work");
Check(result.Succeeded == 2 && result.Failures.Count == 2, "partial failures are reported without skipping later torrents");
using var cancellation = new CancellationTokenSource();
result = await TorrentBatch.RunAsync(new[] { "first", "second" }, hash =>
{ cancellation.Cancel(); return Task.FromResult(true); }, cancellationToken: cancellation.Token);
Check(result.Cancelled && result.Succeeded == 1, "cancellation stops before the next torrent");
var mutableTargets = new List<string> { "1", "2" };
result = await TorrentBatch.RunAsync(mutableTargets, hash => { mutableTargets.Clear(); return Task.FromResult(true); });
Check(result.Succeeded == 2, "batch targets remain fixed if selection changes while running");
await RemovalTests.RunAsync(Check, args.Contains("--bulk-removal"));

foreach (int size in new[] { 1000, 10000 })
{
    var snapshots = Enumerable.Range(0, size).Select(i => Stats(i.ToString("x40"), $"Torrent {i}", i % 2 == 0 ? "Movies" : "TV")).ToArray();
    var large = new TorrentCatalog();
    large.Reconcile(snapshots);
    var preserved = large.Rows[size / 2];
    int rowEvents = 0;
    large.Rows.CollectionChanged += (_, _) => rowEvents++;
    long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    var timer = Stopwatch.StartNew();
    for (int poll = 0; poll < 50; poll++) large.Reconcile(snapshots);
    timer.Stop();
    long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
    Check(large.Rows.Count == size && ReferenceEquals(preserved, large.Rows[size / 2]) && rowEvents == 0,
        $"{size:N0} rows retain identity with zero collection resets over 50 polls");
    Console.WriteLine($"BENCH catalog only: {size:N0} rows, {timer.Elapsed.TotalMilliseconds / 50:F2} ms/poll, {allocated / 50:N0} allocated bytes/poll");
}
var limits = new TorrentQueueing { Enabled = true, MaxActiveDownloads = 1, MaxActiveSeeds = 1, MaxActiveTotal = 2 };
var candidates = new[] { new QueueCandidate("d2", 2, false, false, false, false), new QueueCandidate("d1", 1, false, false, false, false),
    new QueueCandidate("s1", 3, true, false, false, false), new QueueCandidate("paused", 0, false, true, false, false), new QueueCandidate("error", 0, false, false, true, false) };
Check(QueuePlanner.Select(candidates, limits).SetEquals(new[] { "d1", "s1" }), "queue respects position, separate seed/download caps and paused/error exclusions");
limits.MaxActiveTotal = 0;
Check(QueuePlanner.Select(candidates, limits).Count == 0, "zero queue limit starts no ordinary torrents");
Check(QueuePlanner.Select(candidates.Append(new("forced", 5, false, false, false, true)), limits).SetEquals(new[] { "forced" }), "force start bypasses queue limits");
limits.Enabled = false;
Check(QueuePlanner.Select(candidates, limits).Count == 3, "disabling queue does not resume manually paused/error torrents");
Check(new TorrentQuery(State: TorrentFilter.Queued).Matches(new TorrentRow(Stats("q", state: TorrentState.Queued))), "queued filter works");
Check(RssParser.Matches("Show S01E01 1080p", @"S\d+E\d+", "2160p") && !RssParser.Matches("Show 2160p", "Show", "2160p"), "RSS include and exclude regex filters compose");
using (var rss = new MemoryStream(Encoding.UTF8.GetBytes("<rss><channel><item><title>Example</title><enclosure url='https://example.test/download?id=1' /></item></channel></rss>")))
    Check(RssParser.Parse(rss).Single().Url.Contains("id=1"), "RSS enclosure URLs do not require a .torrent suffix");
using (var atom = new MemoryStream(Encoding.UTF8.GetBytes("<feed xmlns='http://www.w3.org/2005/Atom'><entry><title>Atom</title><link rel='enclosure' href='magnet:?xt=urn:btih:0123456789012345678901234567890123456789'/></entry></feed>")))
    Check(RssParser.Parse(atom).Count == 1, "Atom torrent enclosures are supported");
bool dtdRejected = false;
try { using var hostile = new MemoryStream(Encoding.UTF8.GetBytes("<!DOCTYPE rss [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><rss>&x;</rss>")); RssParser.Parse(hostile); }
catch (System.Xml.XmlException) { dtdRejected = true; }
Check(dtdRejected, "RSS parser rejects DTDs/external entities");
Check(TorrentEngine.ValidateTrackerUrls(new[] { "udp://tracker.example.test:80/announce", "udp://tracker.example.test:80/announce" }).Length == 1, "tracker URLs validate and deduplicate");
bool badTracker = false;
try { TorrentEngine.ValidateTrackerUrls(new[] { "file:///secret" }); } catch (ArgumentException) { badTracker = true; }
Check(badTracker, "tracker editor rejects non-network URLs");
Console.WriteLine($"{passed} checks passed. These are headless logic tests, not a Windows UI or torrent throughput benchmark.");

if (OperatingSystem.IsWindows())
{
    string root = Path.Combine(Path.GetTempPath(), "ControllarrDesktopTests", Guid.NewGuid().ToString("N"));
    string downloads = Path.Combine(root, "downloads");
    string resume = Path.Combine(root, "resume");
    Directory.CreateDirectory(downloads);
    string CreateFixture(string name, byte value)
    {
        byte[] bytes = Enumerable.Repeat(value, 16384).ToArray();
        File.WriteAllBytes(Path.Combine(downloads, name), bytes);
        var info = new BEncodedDictionary
        {
            ["length"] = new BEncodedNumber(bytes.Length), ["name"] = new BEncodedString(name),
            ["piece length"] = new BEncodedNumber(16384), ["pieces"] = new BEncodedString(SHA1.HashData(bytes))
        };
        string path = Path.Combine(root, name + ".torrent");
        File.WriteAllBytes(path, new BEncodedDictionary { ["info"] = info }.Encode());
        return path;
    }
    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    ushort port = (ushort)((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    using var engine = new TorrentEngine(downloads, resume, port);
    engine.ApplyTuning(20, false, false);
    string keep = await engine.AddTorrentFile(CreateFixture("keep.bin", 1), "Movies");
    string delete = await engine.AddTorrentFile(CreateFixture("delete.bin", 2), "TV");
    Check(engine.PollStats().Length == 2, "live engine adds disposable fixture torrents");
    Check(ReferenceEquals(engine.PollStats(), engine.PollStats()), "closely spaced engine/API reads share a cached snapshot");
    for (int i = 0; i < 400 && engine.PollStats().Any(t => t.State != TorrentState.Seeding); i++) await Task.Delay(50);
    Check(engine.PollStats().All(t => t.State == TorrentState.Seeding), "queue fixtures finish asynchronous startup before queue enforcement is tested");
    engine.ConfigureQueue(new TorrentQueueing { Enabled = true, MaxActiveDownloads = 0, MaxActiveSeeds = 0, MaxActiveTotal = 0 });
    await engine.TickQueueAsync(true);
    Check(engine.PollStats().All(t => t.State == TorrentState.Queued), "live queue stops excess fixture transfers without treating them as manually paused");
    Check(await engine.SetForceStartAsync(keep, true) && engine.GetOptions(keep).ForceStart, "live force start is distinct from ordinary resume");
    await engine.SetForceStartAsync(keep, false);
    engine.MoveQueue(new[] { delete }, "top");
    Check(engine.GetOptions(delete).Position < engine.GetOptions(keep).Position, "bulk queue move changes engine ordering");
    engine.ConfigureQueue(new TorrentQueueing());
    bool verified = await engine.Recheck(keep);
    if (!verified)
        foreach (var entry in Controllarr.Core.Services.Logger.Instance.Snapshot()) Console.WriteLine(entry.Message);
    Check(verified, "live engine completes a real file verification");
    Check(engine.GetStats(keep)?.Paused == true, "file verification leaves the torrent paused");
    Check(await engine.Remove(keep, false) && File.Exists(Path.Combine(downloads, "keep.bin")), "registration-only removal keeps real fixture files");
    string moved = Path.Combine(root, "moved");
    Check(await engine.Move(delete, moved) && File.Exists(Path.Combine(moved, "delete.bin")), "storage move stops and moves a real active fixture");
    bool removed = await engine.Remove(delete, true);
    if (!removed || File.Exists(Path.Combine(moved, "delete.bin")))
    {
        Console.WriteLine($"REMOVE CHECK: success={removed}, fileExists={File.Exists(Path.Combine(moved, "delete.bin"))}");
        foreach (var entry in Controllarr.Core.Services.Logger.Instance.Snapshot()) Console.WriteLine(entry.Message);
    }
    Check(removed && !File.Exists(Path.Combine(moved, "delete.bin")), "remove-with-files deletes only the disposable fixture payload");
    string restoredHash = await engine.AddTorrentFile(CreateFixture("restore.bin", 3));
    Check(await engine.AddTorrentFile(Path.Combine(root, "restore.bin.torrent")) == restoredHash && engine.PollStats().Length == 1, "duplicate torrent imports keep the existing entry unchanged");
    Check(await engine.SetTorrentLimitsAsync(restoredHash, 256, 128), "per-torrent speed limits apply to the live manager");
    for (int i = 0; i < 400 && engine.GetStats(restoredHash)?.State != TorrentState.Seeding; i++) await Task.Delay(50);
    Check(engine.GetStats(restoredHash)?.State == TorrentState.Seeding, "advanced picker fixture finishes initial verification before controls change");
    Check(await engine.SetAdvancedOptionsAsync(restoredHash, 12, 3, true) && engine.EffectiveTorrentSettings(restoredHash) is { MaximumConnections: 12, UploadSlots: 3 }, "advanced connection/upload controls update the real torrent manager");
    var advancedSettings = new Settings { TorrentNetwork = new() { Encryption = "Require" }, PeerDiscovery = new() { PexEnabled = false } };
    await engine.ApplyAdvancedSettingsAsync(advancedSettings);
    Check(!engine.EffectiveEncryption.Contains(MonoTorrent.Connections.EncryptionType.PlainText) && engine.EffectiveTorrentSettings(restoredHash)?.AllowPeerExchange == false,
        "required encryption and disabled PEX reach the live engine while per-torrent overrides are retained");
    await Task.WhenAll(Task.Run(() => engine.SetRateLimits(100, 100)), Task.Run(() => engine.ApplyTuning(25, false, false)),
        engine.SetTorrentLimitsAsync(restoredHash, 256, 128), engine.ApplyAdvancedSettingsAsync(advancedSettings));
    Check(!engine.EffectiveEncryption.Contains(MonoTorrent.Connections.EncryptionType.PlainText) &&
        engine.EffectiveTorrentSettings(restoredHash) is { MaximumConnections: 12, UploadSlots: 3, MaximumDownloadRate: 262144 },
        "concurrent rate/tuning changes preserve encryption and advanced torrent overrides");
    bool previewVerified = await engine.Recheck(restoredHash);
    if (!previewVerified) foreach (var log in Controllarr.Core.Services.Logger.Instance.Snapshot()) Console.WriteLine(log.Message);
    Check(previewVerified, "preview fixture file verification succeeds before playback");
    await engine.Resume(restoredHash);
    for (int i = 0; i < 400 && engine.GetStats(restoredHash)?.State != TorrentState.Seeding; i++) await Task.Delay(50);
    string? previewUrl = null;
    await using (var preview = await engine.CreatePreviewAsync(restoredHash, 0))
    {
        previewUrl = preview.Url;
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, preview.Url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(8, 31);
        using var previewResponse = await http.SendAsync(request);
        var bytes = await previewResponse.Content.ReadAsByteArrayAsync();
        Check(previewResponse.StatusCode == System.Net.HttpStatusCode.PartialContent && bytes.Length == 24 && bytes.All(b => b == 3),
            "loopback streaming preview serves verified byte ranges from a real torrent file");
        using var deniedPreview = await http.GetAsync(new Uri(new Uri(preview.Url), "/incorrect-capability/stream"));
        Check(deniedPreview.StatusCode == System.Net.HttpStatusCode.NotFound, "preview rejects requests without its random capability URL");
    }
    bool previewStopped = false;
    try { using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) }; await http.GetAsync(previewUrl); }
    catch (HttpRequestException) { previewStopped = true; }
    catch (OperationCanceledException) { previewStopped = true; }
    Check(previewStopped && engine.GetOptions(restoredHash).Sequential, "closed preview no longer serves content and retains the saved sequential policy");
    Check(await engine.ReplaceTrackersAsync(restoredHash, new[] { "http://127.0.0.1:9/announce" }), "public torrent tracker replacement applies");
    Check(await engine.SetFilePriorities(new[] { 0 }, restoredHash), "live file priorities are editable");
    Check(await engine.Pause(restoredHash), "live engine pauses a fixture before restart");
    string created = Path.Combine(root, "created.torrent");
    await TorrentTools.CreateAsync(Path.Combine(downloads, "restore.bin"), created, Array.Empty<string>(), false, "Test", false, null, CancellationToken.None);
    Check(MonoTorrent.Torrent.Load(created).Size == 16384, "torrent creation hashes real fixture files and writes valid metadata");
    var migration = TorrentTools.ReadMigration(root, downloads);
    Check(migration.Count >= 3 && migration.All(e => Path.IsPathFullyQualified(e.SavePath)), "migration discovers torrent metadata with absolute fallback storage");
    string pausedImport = await engine.ImportTorrentFileAsync(CreateFixture("import.bin", 9), downloads);
    Check(engine.GetStats(pausedImport)?.Paused == true, "migration registers torrents paused without trusting another client's verified pieces");
    await engine.Remove(pausedImport, false);
    await engine.Shutdown();
    using var restored = new TorrentEngine(downloads, resume, port);
    Check(restored.GetStats(restoredHash) != null && restored.PollStats().Length == 1, "native engine state and hash lookup survive restart");
    Check(restored.GetStats(restoredHash)?.Progress == 1f && File.Exists(Path.Combine(resume, "fastresume", restoredHash + ".fresume")),
        "atomic resume checkpoints restore verified progress without an initial hash check");
    Check(restored.GetOptions(restoredHash) is { Sequential: true, MaximumConnections: 12, UploadSlots: 3 } && restored.EffectiveTorrentSettings(restoredHash) is { MaximumConnections: 12, UploadSlots: 3 },
        "sequential picking and advanced torrent overrides survive restart");
    Check(restored.GetOptions(restoredHash) is { DownloadKBps: 256, UploadKBps: 128, FilePriorities.Length: 1 } &&
        restored.GetOptions(restoredHash).Trackers?.Single() == "http://127.0.0.1:9/announce", "speed, tracker and file-priority overrides survive restart");
    await restored.ResumeAllAsync();
    Check(restored.GetStats(restoredHash)?.Paused == true, "manually paused torrents remain paused on startup");
    await restored.Shutdown();
    var protectedSettings = new Settings { VpnEnabled = true, VpnInterfaceId = "missing-test-adapter" };
    using (var protectedEngine = new TorrentEngine(downloads, Path.Combine(root, "vpn-resume"), port, protectedSettings))
    {
        await protectedEngine.ApplyAdvancedSettingsAsync(protectedSettings);
        string blocked = await protectedEngine.AddTorrentFile(Path.Combine(root, "restore.bin.torrent"));
        await protectedEngine.Resume(blocked);
        await protectedEngine.SetForceStartAsync(blocked, true);
        await protectedEngine.TickQueueAsync(true);
        Check(protectedEngine.GetStats(blocked)?.State == TorrentState.Queued && !protectedEngine.NetworkPolicy.Allowed,
            "missing VPN blocks add, resume, force-start and queue-start in the real engine");
        await protectedEngine.Shutdown();
    }
    if (args.Contains("--engine-load"))
    {
        using var loaded = new TorrentEngine(downloads, Path.Combine(root, "load-resume"), port);
        loaded.ApplyTuning(20, false, false);
        var import = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
            await loaded.AddTorrentFile(CreateFixture($"load-{i:D4}.bin", 4), i % 2 == 0 ? "Movies" : "TV", persist: false);
        await loaded.SaveEngineStateAsync();
        import.Stop();
        await Task.Delay(3000);
        Check(loaded.PollStats().Length == 1000, "real MonoTorrent engine manages 1,000 local fixture torrents");
        double totalPollMs = 0;
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(260);
            var poll = Stopwatch.StartNew(); loaded.PollStats(); poll.Stop();
            totalPollMs += poll.Elapsed.TotalMilliseconds;
        }
        using var process = Process.GetCurrentProcess();
        Console.WriteLine($"BENCH real engine, no peers/network: 1,000 torrents, import={import.Elapsed.TotalSeconds:F2}s, uncached poll={totalPollMs / 30:F2}ms, runner private bytes={process.PrivateMemorySize64 / 1048576d:F1}MB");
        await loaded.Shutdown();
    }
    Console.WriteLine($"{passed} total checks passed. Disposable engine fixtures retained at {root} for inspection.");
}
