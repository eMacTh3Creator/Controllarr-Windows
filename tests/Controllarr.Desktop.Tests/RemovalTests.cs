using System.Diagnostics;
using System.Security.Cryptography;
using Controllarr.Core.Desktop;
using Controllarr.Core.Engine;
using MonoTorrent.BEncoding;

internal static class RemovalTests
{
    public static async Task RunAsync(Action<bool, string> check, bool large)
    {
        int active = 0, peak = 0;
        var counts = new List<int>();
        var result = await TorrentBatch.RunBoundedAsync(Enumerable.Range(0, 64).Select(i => i.ToString()).Append("0"), async hash =>
        {
            int concurrent = Interlocked.Increment(ref active);
            int previous;
            do { previous = peak; } while (concurrent > previous && Interlocked.CompareExchange(ref peak, concurrent, previous) != previous);
            try
            {
                await Task.Delay(5);
                if (hash == "2") throw new IOException("fixture failure");
                return hash != "3";
            }
            finally { Interlocked.Decrement(ref active); }
        }, 8, new InlineProgress<int>(n => counts.Add(n)));
        check(peak is > 1 and <= 8 && result.Succeeded == 62 && result.Failures.Count == 2,
            "bounded stop workers overlap without exceeding the concurrency cap, deduplicate, and isolate failures");
        check(counts.SequenceEqual(Enumerable.Range(1, 64)) && result.SucceededHashes.Distinct().Count() == 62,
            "bounded batch progress is monotonic and records exact successful hashes");
        using (var cancel = new CancellationTokenSource())
        {
            result = await TorrentBatch.RunBoundedAsync(new[] { "first", "later" }, _ =>
            { cancel.Cancel(); return Task.FromResult(true); }, 1, cancellationToken: cancel.Token);
            check(result.Cancelled && result.Succeeded == 1, "bounded stop cancellation leaves unstarted work alone");
        }
        if (!OperatingSystem.IsWindows()) return;

        string root = Path.Combine(Path.GetTempPath(), "ControllarrRemovalTests", Guid.NewGuid().ToString("N"));
        string downloads = Path.Combine(root, "downloads"), resume = Path.Combine(root, "resume");
        Directory.CreateDirectory(downloads);
        string Fixture(int index)
        {
            byte[] bytes = new byte[16384];
            bytes[0] = (byte)index;
            string name = $"fixture-{index}.bin";
            File.WriteAllBytes(Path.Combine(downloads, name), bytes);
            var info = new BEncodedDictionary
            {
                ["name"] = new BEncodedString(name), ["length"] = new BEncodedNumber(bytes.Length),
                ["piece length"] = new BEncodedNumber(16384), ["pieces"] = new BEncodedString(SHA1.HashData(bytes))
            };
            string path = Path.Combine(root, name + ".torrent");
            File.WriteAllBytes(path, new BEncodedDictionary { ["info"] = info }.Encode());
            return path;
        }
        using var engine = new TorrentEngine(downloads, resume, 0);
        engine.ApplyTuning(20, false, false);
        var selected = new List<string>();
        for (int i = 0; i < 48; i++) selected.Add(await engine.ImportTorrentFileAsync(Fixture(i), downloads));
        string unselected = await engine.ImportTorrentFileAsync(Fixture(9999), downloads);
        for (int i = 0; i < 4; i++) await engine.Resume(selected[i]);
        for (int attempt = 0; attempt < 400 && selected.Take(4).Any(h => engine.GetStats(h)?.State != TorrentState.Seeding); attempt++)
            await Task.Delay(50);
        check(selected.Take(4).All(h => engine.GetStats(h)?.State == TorrentState.Seeding), "bulk removal test starts real active fixture transfers");
        bool allPausedFirst = false;
        bool restartRejected = false;
        result = await engine.RemoveManyAsync(selected.Append(selected[0]), false, new InlineProgress<RemovalProgress>(p =>
        {
            if (p.Stage == "Stopping selected transfers" && p.Completed == 0)
            {
                allPausedFirst = selected.All(h => engine.GetStats(h)?.Paused == true);
                restartRejected = !engine.Resume(selected[0]).GetAwaiter().GetResult()
                    && !engine.SetForceStartAsync(selected[0], true).GetAwaiter().GetResult();
            }
        }));
        check(allPausedFirst && result.Succeeded == 48 && result.Failures.Count == 0,
            "real engine pauses the entire selection before removal, including active transfers and duplicates");
        check(restartRejected, "resume and force-start reject pending removals without waiting to revive them after cancellation");
        check(engine.PollStats().Length == 1 && engine.GetStats(unselected) != null && File.Exists(Path.Combine(downloads, "fixture-0.bin")),
            "bulk registration removal keeps payload files and leaves unselected torrents untouched");
        selected.Clear();
        check(engine.GetOptions(unselected).Position == 1, "bulk removal compacts remaining queue positions once at completion");
        for (int i = 100; i < 132; i++) selected.Add(await engine.ImportTorrentFileAsync(Fixture(i), downloads));
        using (var cancel = new CancellationTokenSource())
        {
            result = await engine.RemoveManyAsync(selected, true, new InlineProgress<RemovalProgress>(p =>
            {
                if (p.Stage == "Deleting files and removing" && p.Completed == 1) cancel.Cancel();
            }), cancel.Token);
            check(result.Cancelled && result.Succeeded == 1 && !File.Exists(Path.Combine(downloads, "fixture-100.bin"))
                && File.Exists(Path.Combine(downloads, "fixture-101.bin")), "cancel stops further disk deletions after the current item finishes");
            check(selected.Skip(1).All(h => engine.GetStats(h)?.Paused == true), "cancelled removal leaves every remaining selected torrent paused");
            check(engine.PollStats().Select(s => s.QueuePosition).Order().SequenceEqual(Enumerable.Range(1, 32).Select(i => (long)i)), "cancelled removal compacts positions for only the surviving torrents");
        }
        await engine.Shutdown();
        var saved = BEncodedValue.Decode<BEncodedDictionary>(File.ReadAllBytes(Path.Combine(resume, "engine.state")));
        var savedTorrents = (BEncodedList)saved["Torrents"];
        check(savedTorrents.Cast<BEncodedDictionary>().All(t => ((BEncodedString)t["MetadataPath"]).Text.StartsWith(resume + Path.DirectorySeparatorChar)),
            "relocation fixture state points to cached metadata inside the old profile");
        var missing = BEncodedValue.Decode<BEncodedDictionary>(savedTorrents[0].Encode());
        missing["MetadataPath"] = new BEncodedString(Path.Combine(resume, "torrents", "already-removed.torrent"));
        savedTorrents.Add(missing);
        File.WriteAllBytes(Path.Combine(resume, "engine.state"), saved.Encode());
        string movedResume = Path.Combine(root, "relocated-profile", "resume");
        Directory.CreateDirectory(Path.GetDirectoryName(movedResume)!);
        Directory.Move(resume, movedResume);
        using var restored = new TorrentEngine(downloads, movedResume, 0);
        check(restored.PollStats().Length == 32 && restored.GetStats(selected[0]) == null,
            "bulk deletion survives a moved-profile restart; stale removed cache entries cannot discard the remaining library");
        await restored.ResumeAllAsync();
        check(selected.Skip(1).All(h => restored.GetStats(h)?.Paused == true), "cancelled batch pause state survives restart and startup resume");
        check(restored.GetStats(unselected)?.SavePath == downloads, "profile relocation does not change torrent payload paths");
        result = await restored.RemoveManyAsync(selected.Skip(1), true);
        check(result.Succeeded == 31 && !File.Exists(Path.Combine(downloads, "fixture-131.bin")), "a cancelled batch can safely finish later");
        string metadataHash = await restored.AddMagnet("magnet:?xt=urn:btih:" + new string('b', 40));
        using (var cancelPreparing = new CancellationTokenSource())
        {
            result = await restored.RemoveManyAsync(new[] { metadataHash }, false, new InlineProgress<RemovalProgress>(p =>
            {
                if (p.Stage == "Pausing selected transfers") cancelPreparing.Cancel();
            }), cancelPreparing.Token);
            check(result.Cancelled && result.Succeeded == 0 && restored.GetStats(metadataHash)?.Paused == true,
                "cancel during preparation still stops metadata/startup transfers without removing them");
        }
        await restored.RemoveManyAsync(new[] { metadataHash }, false);
        var tracker = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        tracker.Start();
        int trackerPort = ((System.Net.IPEndPoint)tracker.LocalEndpoint).Port;
        using (var cancelTracker = new CancellationTokenSource())
        {
            var clients = new System.Collections.Concurrent.ConcurrentBag<System.Net.Sockets.TcpClient>();
            async Task AcceptHungTrackerAsync()
            {
                try
                {
                    while (!cancelTracker.IsCancellationRequested)
                        clients.Add(await tracker.AcceptTcpClientAsync(cancelTracker.Token));
                }
                catch (OperationCanceledException) { }
            }
            var accepting = AcceptHungTrackerAsync();
            try
            {
                var tracked = new List<string>();
                for (int i = 500; i < 508; i++)
                {
                    string path = Fixture(i);
                    var torrent = BEncodedValue.Decode<BEncodedDictionary>(File.ReadAllBytes(path));
                    torrent["announce"] = new BEncodedString($"http://127.0.0.1:{trackerPort}/announce");
                    File.WriteAllBytes(path, torrent.Encode());
                    tracked.Add(await restored.AddTorrentFile(path, persist: false));
                }
                for (int i = 0; i < 400 && (clients.IsEmpty || tracked.Any(h => restored.GetStats(h)?.State != TorrentState.Seeding)); i++)
                    await Task.Delay(50);
                check(!clients.IsEmpty, "removal test reaches a real tracker that accepts connections but never responds");
                var stopping = Stopwatch.StartNew();
                result = await restored.RemoveManyAsync(tracked, true);
                stopping.Stop();
                check(result.Succeeded == 8 && result.Failures.Count == 0 && stopping.Elapsed < TimeSpan.FromSeconds(15),
                    "unresponsive tracker announces do not serialize or indefinitely block bulk removal");
                Console.WriteLine($"BENCH hung tracker: 8 active fixtures removed in {stopping.Elapsed.TotalSeconds:F2}s");
            }
            finally
            {
                cancelTracker.Cancel();
                await accepting;
                tracker.Stop();
                foreach (var client in clients) client.Dispose();
            }
        }
        if (large)
        {
            selected.Clear();
            for (int i = 20000; i < 22376; i++) selected.Add(await restored.ImportTorrentFileAsync(Fixture(i), downloads, persist: false));
            await restored.SaveEngineStateAsync();
            var timer = Stopwatch.StartNew();
            result = await restored.RemoveManyAsync(selected, true);
            timer.Stop();
            check(result.Succeeded == 2376 && result.Failures.Count == 0 && restored.PollStats().Length == 1,
                "large batch removes 2,376 real disposable torrent registrations and payload files");
            Console.WriteLine($"BENCH removal: 2,376 paused local 16KiB fixtures, no peers/trackers, {timer.Elapsed.TotalSeconds:F2}s; not a real disk/network throughput guarantee");
            string next = await restored.ImportTorrentFileAsync(Fixture(22376), downloads, persist: false);
            check(restored.GetOptions(unselected).Position == 1 && restored.GetOptions(next).Position == 2,
                "adding after 2,376 deletions uses the live queue size, not the old sequence counter");
        }
        await restored.Shutdown();
        Console.WriteLine($"Removal fixtures retained at {root}");
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
