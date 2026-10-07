using System.Collections.Concurrent;
using System.Diagnostics;
using Controllarr.Core.Desktop;
using MonoTorrent.Client;

namespace Controllarr.Core.Engine;

public sealed partial class TorrentEngine
{
    private readonly ConcurrentDictionary<string, byte> _removalPending = new(StringComparer.OrdinalIgnoreCase);

    public async Task<BatchResult> RemoveManyAsync(IEnumerable<string> hashes, bool deleteFiles,
        IProgress<RemovalProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        string[] targets = hashes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (targets.Length == 0) return new(0, Array.Empty<BatchFailure>(), false);
        await _queueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var removed = new List<string>();
        var failures = new List<BatchFailure>();
        try
        {
            // Mark every target before any awaits, so queue/force/API starts cannot revive it.
            foreach (string hash in targets)
                if (FindManager(hash) != null)
                {
                    _removalPending[hash] = 0;
                    _pausedHashes[hash] = 0;
                    _queuedHashes.TryRemove(hash, out _);
                    _options.AddOrUpdate(hash, OptionsFor(hash) with { ForceStart = false }, (_, previous) => previous with { ForceStart = false });
                }
            InvalidateStats();
            progress?.Report(new("Pausing selected transfers", 0, targets.Length));
            foreach (string hash in targets)
                if (FindManager(hash) is { } manager)
                    try { await manager.PauseAsync().ConfigureAwait(false); }
                    catch (Exception ex) { Services.Logger.Instance.Warn("Engine", $"Prepare removal {hash}: {ex.Message}"); }
            await SaveEngineStateAsync(checkpointResume: false).ConfigureAwait(false);
            InvalidateStats();
            progress?.Report(new("Stopping selected transfers", 0, targets.Length));

            // Tracker stop announcements overlap; disk deletion deliberately does not.
            var stopped = await TorrentBatch.RunBoundedAsync(targets, async hash =>
            {
                if (FindManager(hash) is not { } manager) return false;
                // Cancel must not leave metadata/startup transfers running. Already
                // paused items need no disk stop if deletion has been cancelled.
                if (cancellationToken.IsCancellationRequested && manager.State is
                    MonoTorrent.Client.TorrentState.Stopped or MonoTorrent.Client.TorrentState.Paused or MonoTorrent.Client.TorrentState.HashingPaused)
                    return true;
                await StopManagerAsync(manager, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                return manager.State == MonoTorrent.Client.TorrentState.Stopped;
            }, 16, new RemovalStopProgress(progress, targets.Length)).ConfigureAwait(false);
            failures.AddRange(stopped.Failures);
            var ready = stopped.SucceededHashes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var savedAt = Stopwatch.StartNew();
            int completed = 0;
            foreach (string hash in targets)
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (ready.Contains(hash) && FindManager(hash) is { } manager)
                {
                    try
                    {
                        await RemoveStoppedAsync(hash, manager, deleteFiles).ConfigureAwait(false);
                        removed.Add(hash);
                    }
                    catch (Exception ex) { failures.Add(new(hash, ex.Message)); }
                }
                progress?.Report(new(deleteFiles ? "Deleting files and removing" : "Removing (keeping files)", ++completed, targets.Length));
                if (completed % 100 == 0 || savedAt.Elapsed >= TimeSpan.FromSeconds(5))
                {
                    await SaveEngineStateAsync(checkpointResume: false).ConfigureAwait(false);
                    savedAt.Restart();
                }
            }
            return new(removed.Count, failures, cancellationToken.IsCancellationRequested) { SucceededHashes = removed };
        }
        finally
        {
            foreach (string hash in targets) _removalPending.TryRemove(hash, out _);
            // Unprocessed/failed targets remain paused, including after Cancel or restart.
            try { await SaveEngineStateAsync(checkpointResume: false).ConfigureAwait(false); }
            finally { InvalidateStats(); _queueGate.Release(); }
        }
    }

    private async Task RemoveStoppedAsync(string hash, TorrentManager manager, bool deleteFiles)
    {
        if (manager.State != MonoTorrent.Client.TorrentState.Stopped)
            throw new InvalidOperationException("Torrent must fully stop before its files are removed.");
        await _stateSaveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _engine.RemoveAsync(manager, deleteFiles ? RemoveMode.CacheDataAndDownloadedData : RemoveMode.CacheDataOnly).ConfigureAwait(false);
            _managersByHash.TryRemove(hash, out _);
            _categories.TryRemove(hash, out _);
            _addedDates.TryRemove(hash, out _);
            _pausedHashes.TryRemove(hash, out _);
            _queuedHashes.TryRemove(hash, out _);
            _options.TryRemove(hash, out _);
            _removalPending.TryRemove(hash, out _);
            lock (_filteredLock) _filteredHashes.Remove(hash);
            InvalidateStats();
        }
        finally { _stateSaveGate.Release(); }
    }

    private sealed class RemovalStopProgress(IProgress<RemovalProgress>? progress, int total) : IProgress<int>
    {
        public void Report(int count) => progress?.Report(new("Stopping selected transfers", count, total));
    }
}
