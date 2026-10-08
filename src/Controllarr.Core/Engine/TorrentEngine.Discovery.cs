using System.Collections.Concurrent;
using System.Threading.Channels;
using MonoTorrent.Client;
using MonoTorrent.Trackers;

namespace Controllarr.Core.Engine;

public sealed partial class TorrentEngine
{
    private Persistence.DuplicateTorrentPolicy _duplicatePolicy;
    private readonly Channel<string> _discoveryQueue = Channel.CreateBounded<string>(new BoundedChannelOptions(1024)
    { FullMode = BoundedChannelFullMode.Wait });
    private readonly ConcurrentDictionary<string, byte> _discoveryPending = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _discoveryCancellation = new();
    private readonly SemaphoreSlim _announceGate = new(4, 4);
    private Task[] _discoveryWorkers = Array.Empty<Task>();

    private void InitializeDiscovery() => _discoveryWorkers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
    {
        try
        {
            await foreach (string hash in _discoveryQueue.Reader.ReadAllAsync(_discoveryCancellation.Token))
            {
                try { await Reannounce(hash); }
                finally { _discoveryPending.TryRemove(hash, out var ignored); }
            }
        }
        catch (OperationCanceledException) when (_discoveryCancellation.IsCancellationRequested) { }
    })).ToArray();

    /// <summary>Bounded, deduplicated refresh for monitors; never blocks the UI/poll loop on tracker I/O.</summary>
    public bool RequestReannounce(string hash)
    {
        if (!CanRefreshDiscovery(hash)) return false;
        if (!_discoveryPending.TryAdd(hash, 0)) return true;
        if (_discoveryQueue.Writer.TryWrite(hash)) return true;
        _discoveryPending.TryRemove(hash, out _);
        return false;
    }

    private bool CanRefreshDiscovery(string hash) => !_disposed && !_shuttingDown && NetworkPolicy.Allowed &&
        _transferBlockReason == null && !_pausedHashes.ContainsKey(hash) && !_queuedHashes.ContainsKey(hash) &&
        !_removalPending.ContainsKey(hash) && FindManager(hash)?.State is
            MonoTorrent.Client.TorrentState.Metadata or MonoTorrent.Client.TorrentState.Downloading or MonoTorrent.Client.TorrentState.Seeding;

    public async Task<bool> Reannounce(string hash)
    {
        if (!CanRefreshDiscovery(hash)) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_discoveryCancellation.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        bool entered = false;
        try
        {
            await _announceGate.WaitAsync(timeout.Token);
            entered = true;
            if (!CanRefreshDiscovery(hash) || FindManager(hash) is not { } manager) return false;
            // A DHT failure must not suppress a useful tracker announce.
            try { await manager.DhtAnnounceAsync(); }
            catch (Exception ex) { Services.Logger.Instance.Debug("Discovery", $"DHT refresh unavailable: {ex.GetType().Name}"); }
            foreach (var tracker in manager.TrackerManager.Tiers.SelectMany(t => t.Trackers).OfType<Networking.ReliableTracker>())
                tracker.RequestEarlyRefresh();
            await manager.TrackerManager.AnnounceAsync(timeout.Token);
            return manager.TrackerManager.Tiers.SelectMany(t => t.Trackers).Any(t => t.Status == TrackerState.Ok) ||
                (_engine.Settings.DhtEndPoint != null && NetworkPolicy.DhtAllowed);
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            Services.Logger.Instance.Warn("Discovery", $"Refresh failed for {hash}: {ex.GetType().Name}");
            return false;
        }
        finally { if (entered) _announceGate.Release(); }
    }

    private string? DiscoveryProblem(TorrentManager manager)
    {
        if (manager.State is not (MonoTorrent.Client.TorrentState.Metadata or MonoTorrent.Client.TorrentState.Downloading) ||
            manager.OpenConnections > 0 || manager.Monitor.DownloadRate > 0) return null;
        bool dht = _engine.Settings.DhtEndPoint != null && NetworkPolicy.DhtAllowed && manager.CanUseDht;
        if (dht && manager.OpenConnections == 0 && manager.TrackerManager.Tiers.Count == 0 && _engine.Dht.State != MonoTorrent.Dht.DhtState.Ready)
            return "Waiting for DHT bootstrap through the permitted network route. Check the bound adapter, DNS and UDP connectivity; no direct fallback is used in VPN mode.";
        bool usable = false, allFailed = true;
        foreach (var tier in manager.TrackerManager.Tiers)
            foreach (var tracker in tier.Trackers)
            {
                if (NetworkPolicy.UsesProxy && tracker.Uri.Scheme is not ("http" or "https")) continue;
                usable = true;
                allFailed &= tracker.Status is TrackerState.Offline or TrackerState.InvalidResponse;
            }
        if (!usable && !dht)
            return NetworkPolicy.UsesProxy
                ? "No usable peer-discovery source: SOCKS5 disables UDP trackers and DHT. Add an HTTP/HTTPS tracker from the torrent's source or use its .torrent file."
                : "No usable peer-discovery source: DHT is disabled and this torrent has no trackers. Use a tracker-backed magnet or the source's .torrent file; PEX cannot discover the first peer.";
        if (usable && allFailed && !dht)
            return "All trackers failed and DHT is disabled. Check tracker errors, the VPN adapter's DNS and provider connectivity; this does not prove there are no seeders.";
        return null;
    }

    private async Task MergeDuplicateTrackersAsync(TorrentManager manager, IEnumerable<string> incoming)
    {
        if (_duplicatePolicy != Persistence.DuplicateTorrentPolicy.MergeTrackers || manager.Torrent?.IsPrivate == true) return;
        string hash = manager.InfoHashes.V1OrV2.ToHex();
        var existing = manager.TrackerManager.Tiers.SelectMany(t => t.Trackers).Select(t => t.Uri.AbsoluteUri).ToHashSet(StringComparer.Ordinal);
        bool changed = false;
        foreach (string url in ValidateTrackerUrls(incoming))
            if (existing.Add(url))
            {
                await manager.TrackerManager.AddTrackerAsync(new Uri(url));
                changed = true;
            }
        if (!changed) return;
        _options.AddOrUpdate(hash, OptionsFor(hash) with { Trackers = existing.ToArray() },
            (_, previous) => previous with { Trackers = existing.ToArray() });
        Services.Logger.Instance.Info("Discovery", $"Merged source-supplied trackers for duplicate {hash}; existing pause/category/path settings kept.");
        RequestReannounce(hash);
        InvalidateStats();
    }
}
