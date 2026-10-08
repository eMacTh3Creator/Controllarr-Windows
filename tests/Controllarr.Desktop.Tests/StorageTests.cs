using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Controllarr.Core.Engine;
using Controllarr.Core.Persistence;
using Controllarr.Core.Services;
using MonoTorrent;
using MonoTorrent.BEncoding;

internal static class StorageTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "ControllarrStorageTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string hash = new string('a', 40);
        check(StoragePaths.Equal(root, root + Path.DirectorySeparatorChar), "storage paths do not require a trailing separator");
        string safe = StoragePaths.TorrentFolder("../A:B\\C?* ", hash);
        check(safe == Path.GetFileName(safe) && !safe.Contains(':') && !safe.Contains('\\') && safe.EndsWith("[aaaaaaaaaaaa]"),
            "torrent subfolders sanitize Windows path characters and include a short hash");
        check(StoragePaths.TorrentFolder("Show", hash) != StoragePaths.TorrentFolder("Show", new string('b', 40)),
            "same-name torrents with different hashes use separate folders");
        check(!JsonSerializer.Deserialize<Category>("{\"name\":\"legacy\"}")!.CreateTorrentSubfolder
            && !JsonSerializer.Deserialize<Settings>("{}")!.CreateTorrentSubfolders,
            "legacy profiles keep their previous layout until explicitly opted in");
        var roundtrip = JsonSerializer.Deserialize<Category>(JsonSerializer.Serialize(new Category { CreateTorrentSubfolder = true }));
        check(roundtrip!.CreateTorrentSubfolder, "category subfolder option survives JSON round-trip");
        var categoryState = JsonSerializer.Deserialize<PersistedState>("{\"category_by_hash\":{\"aabb\":\"old\",\"AABB\":\"TV\"}}");
        check(categoryState!.CategoryByHash.Count == 1 && categoryState.CategoryByHash["AaBb"] == "TV",
            "category hash lookup stays case-insensitive after JSON restore and merges legacy duplicate-case keys");

        string archives = Path.Combine(root, "shared");
        Directory.CreateDirectory(archives);
        string Zip(string name)
        {
            string path = Path.Combine(archives, name + ".zip");
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            using var writer = new StreamWriter(zip.CreateEntry(name + ".txt").Open());
            writer.Write(name);
            return path;
        }
        string selectedZip = Zip("selected"), neighborZip = Zip("neighbor");
        var view = new TorrentView { InfoHash = hash, Name = "selected", Category = "TV", ContentPath = archives, Progress = .9999f };
        var category = new Category { Name = "TV", ExtractArchives = true };
        var serviceEngine = new FixtureEngine(new[] { selectedZip });
        var processor = new PostProcessor();
        processor.Tick(new[] { view }, new[] { category }, serviceEngine);
        check(processor.Snapshot().Count == 0, "post-processing never starts at 99.99 percent");
        view.Progress = 1; view.HasMetadata = false;
        processor.Tick(new[] { view }, new[] { category }, serviceEngine);
        check(processor.Snapshot().Count == 0, "post-processing requires real torrent metadata");
        view.HasMetadata = true;
        processor.Tick(new[] { view }, new[] { category }, serviceEngine);
        check(processor.Record(hash)?.Stage == PostStage.Done && File.Exists(Path.Combine(archives, "selected.txt"))
            && !File.Exists(Path.Combine(archives, "neighbor.txt")) && File.Exists(neighborZip),
            "archive extraction touches only the completed torrent's selected files, not neighboring archives");
        processor = new PostProcessor(); category.CompletePath = Path.Combine(root, "complete"); serviceEngine.FailMove = true;
        processor.Tick(new[] { view }, new[] { category }, serviceEngine);
        check(processor.Record(hash)?.Stage == PostStage.Failed, "rejected storage moves surface as failed post-processing instead of false success");
        if (!OperatingSystem.IsWindows()) return;

        string downloads = Path.Combine(root, "downloads"), resume = Path.Combine(root, "resume");
        Directory.CreateDirectory(downloads);
        var settings = new Settings { DefaultSavePath = downloads, CreateTorrentSubfolders = true,
            PeerDiscovery = new() { DhtEnabled = false, LsdEnabled = false } };
        var grouped = new Category { Name = "TV", SavePath = Path.Combine(root, "category"), CreateTorrentSubfolder = true };
        var flat = new Category { Name = "flat", SavePath = downloads, CreateTorrentSubfolder = false };
        Category? Lookup(string name) => name == "TV" ? grouped : name == "flat" ? flat : null;
        string Fixture(string name, bool multi, byte value)
        {
            byte[] payload = Enumerable.Repeat(value, 16384).ToArray();
            var info = new BEncodedDictionary { ["name"] = new BEncodedString(name),
                ["piece length"] = new BEncodedNumber(16384) };
            if (multi)
            {
                info["files"] = new BEncodedList(new[] { "video.bin", "extras.bin" }.Select(file => (BEncodedValue)new BEncodedDictionary
                { ["length"] = new BEncodedNumber(payload.Length), ["path"] = new BEncodedList { new BEncodedString(file) } }));
                info["pieces"] = new BEncodedString(SHA1.HashData(payload).Concat(SHA1.HashData(payload)).ToArray());
            }
            else
            {
                info["length"] = new BEncodedNumber(payload.Length);
                info["pieces"] = new BEncodedString(SHA1.HashData(payload));
            }
            string path = Path.Combine(root, name + ".torrent");
            File.WriteAllBytes(path, new BEncodedDictionary { ["info"] = info }.Encode());
            return path;
        }
        async Task SeedAsync(TorrentEngine engine, string id, byte value)
        {
            await engine.Pause(id);
            foreach (string file in engine.GetContentFilePaths(id))
            { Directory.CreateDirectory(Path.GetDirectoryName(file)!); await File.WriteAllBytesAsync(file, Enumerable.Repeat(value, 16384).ToArray()); }
            check(await engine.Recheck(id) && engine.GetStats(id)?.Progress == 1, "storage fixture verifies real payload data");
        }
        using var live = new TorrentEngine(downloads, resume, 0, settings) { CategoryLookup = Lookup };
        live.ApplyTuning(20, false, false);
        string singleFile = Fixture("single.bin", false, 1);
        string singleHash = Torrent.Load(singleFile).InfoHashes.V1OrV2.ToHex();
        string single = await live.AddTorrentFile(singleFile, "TV");
        string singleFolder = StoragePaths.TorrentFolder("single.bin", singleHash);
        check(StoragePaths.Equal(live.GetStats(single)!.SavePath, Path.Combine(grouped.SavePath, singleFolder)),
            "single-file category intake creates its own torrent folder without a trailing slash");
        await SeedAsync(live, single, 1);
        string multi = await live.AddTorrentFile(Fixture("Multi", true, 2), "TV");
        await SeedAsync(live, multi, 2);
        string multiFolder = StoragePaths.TorrentFolder("Multi", multi);
        check(!StoragePaths.Equal(live.GetStats(multi)!.ContentPath, live.GetStats(multi)!.ApiSavePath),
            "Sonarr receives distinct content_path and base save_path for an owned multi-file folder");
        check(StoragePaths.Equal(live.GetStats(multi)!.ApiSavePath, grouped.SavePath),
            "qBittorrent API reports the logical download root, not the owned content directory");
        string requested = await live.AddTorrentFile(Fixture("api-request.bin", false, 20), "flat", createSubfolder: true);
        check(live.GetOptions(requested).StorageSubfolder != null,
            "Sonarr per-download Subfolder overrides a legacy flat category without mutating category settings");
        check(live.GetContentFilePaths(multi).All(p => StoragePaths.Equal(Path.GetDirectoryName(p)!, Path.Combine(grouped.SavePath, multiFolder))),
            "multi-file subfolder intake has exactly one outer torrent directory");
        string flatId = await live.AddTorrentFile(Fixture("flat.bin", false, 3), "flat");
        await SeedAsync(live, flatId, 3);
        check(StoragePaths.Equal(live.GetStats(flatId)!.SavePath, downloads), "category opt-out overrides the global subfolder setting");
        string explicitRoot = Path.Combine(root, "override");
        string overridden = await live.AddTorrentFile(Fixture("override.bin", false, 4), "TV", explicitRoot + Path.DirectorySeparatorChar);
        check(StoragePaths.Equal(live.GetStats(overridden)!.SavePath, Path.Combine(explicitRoot, StoragePaths.TorrentFolder("override.bin", overridden))),
            "explicit save paths override the category base but retain its folder policy");
        string magnet = await live.AddMagnet("magnet:?xt=urn:btih:" + new string('b', 40), "TV");
        await live.Pause(magnet);
        string magnetPath = live.GetStats(magnet)!.SavePath;
        check(Path.GetFileName(magnetPath).Contains("bbbbbbbbbbbb") && !live.GetStats(magnet)!.HasMetadata,
            "nameless magnets get a stable hash-based subfolder before metadata arrives");

        string completed = Path.Combine(root, "completed");
        check(await live.Move(single, completed) && File.Exists(Path.Combine(completed, singleFolder, "single.bin"))
            && live.GetStats(single)!.Paused, "completed single-file moves retain the captured folder and pause state");
        check(await live.Move(single, completed + Path.DirectorySeparatorChar)
            && StoragePaths.Equal(live.GetStats(single)!.SavePath, Path.Combine(completed, singleFolder)),
            "repeated moves to a parent with a trailing slash do not nest torrent folders");
        check(await live.Move(multi, completed) && File.Exists(Path.Combine(completed, multiFolder, "video.bin"))
            && !File.Exists(Path.Combine(completed, "video.bin")), "completed multi-file moves never flatten their payload into the category root");
        string collisionRoot = Path.Combine(root, "collision");
        Directory.CreateDirectory(Path.Combine(collisionRoot, multiFolder));
        string collision = Path.Combine(collisionRoot, multiFolder, "video.bin");
        File.WriteAllText(collision, "keep me");
        check(!await live.Move(multi, collisionRoot) && File.ReadAllText(collision) == "keep me"
            && File.Exists(Path.Combine(completed, multiFolder, "video.bin")), "storage collisions reject the move without replacing source or destination files");
        flat.CreateTorrentSubfolder = true;
        await live.ApplyAdvancedSettingsAsync(settings);
        check(await live.Move(flatId, downloads + Path.DirectorySeparatorChar) && StoragePaths.Equal(live.GetStats(flatId)!.SavePath, downloads),
            "enabling subfolders does not silently reorganize an existing flat torrent");
        flat.CreateTorrentSubfolder = false;
        check(await live.RepairContentLayoutAsync(flatId)
            && !StoragePaths.Equal(live.GetStats(flatId)!.SavePath, downloads)
            && File.Exists(live.GetContentFilePaths(flatId).Single()),
            "explicit flat-layout repair moves only selected torrent payload into an owned folder");
        string repairedPath = live.GetStats(flatId)!.SavePath;
        check(await live.RepairContentLayoutAsync(flatId) && StoragePaths.Equal(live.GetStats(flatId)!.SavePath, repairedPath),
            "layout repair is idempotent for an already owned folder");
        check(!await live.RepairContentLayoutAsync(magnet), "layout repair rejects magnets before metadata");
        string legacy = await live.AddTorrentFile(Fixture("LegacyMulti", true, 5), "flat");
        await SeedAsync(live, legacy, 5);
        check(await live.Move(legacy, completed) && File.Exists(Path.Combine(completed, "LegacyMulti", "video.bin")),
            "legacy multi-file torrents preserve MonoTorrent's existing containing folder when moved");
        await live.Shutdown();
        using var restored = new TorrentEngine(downloads, resume, 0, settings) { CategoryLookup = Lookup };
        restored.ApplyTuning(20, false, false);
        check(StoragePaths.Equal(restored.GetStats(single)!.SavePath, Path.Combine(completed, singleFolder))
            && restored.GetOptions(single).StorageSubfolder == singleFolder && restored.GetStats(single)!.Progress == 1,
            "single-file folder, verified data and layout policy survive a real engine restart");
        check(restored.GetContentFilePaths(multi).All(File.Exists) && restored.GetContentFilePaths(legacy).All(File.Exists)
            && StoragePaths.Equal(restored.GetStats(magnet)!.SavePath, magnetPath),
            "multi-file and nameless magnet paths survive restart without duplicate nesting");
        check(await restored.Move(single, completed) && await restored.Move(legacy, completed), "restored completed moves are idempotent");
        await restored.Shutdown();
        Console.WriteLine($"Storage fixtures retained at {root}");
    }

    private sealed class FixtureEngine(IReadOnlyList<string> files) : ITorrentEngine
    {
        public bool FailMove { get; set; }
        public IReadOnlyList<TorrentView> GetTorrents() => Array.Empty<TorrentView>();
        public IReadOnlyList<string>? GetContentFiles(string hash) => files;
        public void PauseTorrent(string hash) { }
        public void ResumeTorrent(string hash) { }
        public void RemoveTorrent(string hash, bool deleteFiles) { }
        public void MoveStorage(string hash, string path) { if (FailMove) throw new IOException("fixture collision"); }
        public void SetRateLimits(int download, int upload) { }
        public void BindToAddress(string? address) { }
        public void Reannounce(string hash) { }
    }
}
