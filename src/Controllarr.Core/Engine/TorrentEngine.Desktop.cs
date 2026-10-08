using System.Collections.Concurrent;
using System.Text.Json;
using Controllarr.Core.Desktop;
using Controllarr.Core.Persistence;
using MonoTorrent.Client;

namespace Controllarr.Core.Engine;

public sealed record TorrentOptions
{
    public long Position { get; init; }
    public bool ForceStart { get; init; }
    public int DownloadKBps { get; init; }
    public int UploadKBps { get; init; }
    public string[]? Trackers { get; init; }
    public int[]? FilePriorities { get; init; }
    public int? MaximumConnections { get; init; }
    public int? UploadSlots { get; init; }
    public bool Sequential { get; init; }
}

public sealed partial class TorrentEngine
{
    private readonly ConcurrentDictionary<string, TorrentOptions> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _queuedHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _queueGate = new(1, 1);
    private readonly object _queueOrderLock = new();
    private TorrentQueueing _queueSettings = new();
    private long _nextPosition;
    private IReadOnlyDictionary<string, string> _queueWaitingReasons = new Dictionary<string, string>();
    private string? _transferBlockReason;
    private static readonly TorrentOptions EmptyOptions = new();

    private TorrentOptions OptionsFor(string hash)
    {
        if (_options.TryGetValue(hash, out var existing)) return existing;
        lock (_queueOrderLock)
        {
            if (_options.TryGetValue(hash, out existing)) return existing;
            if (!_managersByHash.ContainsKey(hash)) return EmptyOptions;
            return _options[hash] = new TorrentOptions { Position = ++_nextPosition };
        }
    }
    public TorrentOptions GetOptions(string hash) => OptionsFor(hash);

    private void RegisterManager(string hash, TorrentManager manager)
    {
        lock (_queueOrderLock)
        {
            _managersByHash[hash] = manager;
            OptionsFor(hash);
        }
    }

    private void CompactQueuePositions()
    {
        lock (_queueOrderLock)
        {
            var ordered = _managersByHash.Keys.OrderBy(h => _options.TryGetValue(h, out var o) && o.Position > 0 ? o.Position : long.MaxValue)
                .ThenBy(h => _addedDates.GetValueOrDefault(h)).ThenBy(h => h, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (string hash in _options.Keys)
                if (!_managersByHash.ContainsKey(hash)) _options.TryRemove(hash, out _);
            AssignQueuePositions(ordered);
        }
        InvalidateStats();
    }

    // Called under _queueOrderLock; no disk/network work while holding this lock.
    private void AssignQueuePositions(IReadOnlyList<string> ordered)
    {
        for (int i = 0; i < ordered.Count; i++)
        {
            string hash = ordered[i]; long position = i + 1;
            _options.AddOrUpdate(hash, _ => new TorrentOptions { Position = position }, (_, previous) => previous with { Position = position });
        }
        _nextPosition = ordered.Count;
    }

    private void RestoreDesktopOptions()
    {
        try
        {
            string file = _stateFilePath + ".options.json";
            if (File.Exists(file))
                foreach (var pair in JsonSerializer.Deserialize<Dictionary<string, TorrentOptions>>(File.ReadAllText(file)) ?? new())
                    if (_managersByHash.ContainsKey(pair.Key)) _options[pair.Key] = pair.Value;
            CompactQueuePositions();
            foreach (var pair in _managersByHash)
            {
                try
                {
                    var options = OptionsFor(pair.Key);
                    ApplyTorrentOptionsAsync(pair.Value, options).GetAwaiter().GetResult();
                    if (options.Sequential) pair.Value.ChangePickerAsync(new MonoTorrent.PiecePicking.StandardPieceRequester(
                        new MonoTorrent.PiecePicking.PieceRequesterSettings(allowRandomised: false, allowRarestFirst: false))).GetAwaiter().GetResult();
                    if (options.Trackers != null) ReplaceTrackersCoreAsync(pair.Value, options.Trackers).GetAwaiter().GetResult();
                    if (options.FilePriorities is { Length: > 0 } && pair.Value.Files.Count == options.FilePriorities.Length)
                        SetFilePriorities(options.FilePriorities, pair.Key).GetAwaiter().GetResult();
                }
                catch (Exception ex) { Services.Logger.Instance.Warn("Engine", $"Options for {pair.Key}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Services.Logger.Instance.Warn("Engine", $"Could not restore desktop options: {ex.Message}"); }
    }

    public void ConfigureQueue(TorrentQueueing settings)
    {
        if (settings.MaxActiveDownloads < 0 || settings.MaxActiveSeeds < 0 || settings.MaxActiveTotal < 0)
            throw new ArgumentOutOfRangeException(nameof(settings), "Queue limits cannot be negative.");
        _queueSettings = new TorrentQueueing
        {
            Enabled = settings.Enabled,
            MaxActiveDownloads = settings.MaxActiveDownloads,
            MaxActiveSeeds = settings.MaxActiveSeeds,
            MaxActiveTotal = settings.MaxActiveTotal
        };
    }

    private async Task StartManagedAsync(TorrentManager manager)
    {
        string hash = manager.InfoHashes.V1OrV2.ToHex();
        await ApplyTorrentOptionsAsync(manager, OptionsFor(hash));
        if (_removalPending.ContainsKey(hash)) return;
        if (_shuttingDown || !NetworkPolicy.Allowed) { _queuedHashes[hash] = 0; InvalidateStats(); return; }
        if (_queueSettings.Enabled && !OptionsFor(hash).ForceStart)
        { _queuedHashes[hash] = 0; InvalidateStats(); return; }
        _queuedHashes.TryRemove(hash, out _);
        await StartNetworkGuardedAsync(manager);
    }

    private async Task StartNetworkGuardedAsync(TorrentManager manager)
    {
        if (_shuttingDown || !NetworkPolicy.Allowed || _removalPending.ContainsKey(manager.InfoHashes.V1OrV2.ToHex())) return;
        await ApplyTorrentOptionsAsync(manager, OptionsFor(manager.InfoHashes.V1OrV2.ToHex()));
        if (!_shuttingDown && NetworkPolicy.Allowed && !_removalPending.ContainsKey(manager.InfoHashes.V1OrV2.ToHex())) await manager.StartAsync();
    }

    public async Task TickQueueAsync(bool transfersAllowed, string? blockReason = null)
    {
        _transferBlockReason = transfersAllowed ? null : blockReason ?? "Transfers blocked by a safety guard; check VPN and disk status.";
        if (_disposed || !await _queueGate.WaitAsync(0)) return;
        try
        {
            var pairs = _managersByHash.ToArray();
            var plan = QueuePlanner.Plan(pairs.Select(p => new QueueCandidate(p.Key, OptionsFor(p.Key).Position,
                p.Value.Progress >= 100, _pausedHashes.ContainsKey(p.Key), p.Value.State == MonoTorrent.Client.TorrentState.Error,
                OptionsFor(p.Key).ForceStart)), _queueSettings);
            var connectionPlan = ConnectionPlanner.Plan(pairs.Where(p => plan.Active.Contains(p.Key)).Select(p =>
            {
                var options = OptionsFor(p.Key);
                return new ConnectionCandidate(p.Key, options.Position, p.Value.Progress >= 100, options.ForceStart,
                    options.MaximumConnections ?? _defaultTorrentConnections);
            }), _engine.Settings.MaximumConnections, _downloadReservePercent);
            var waitingReasons = new Dictionary<string, string>(plan.WaitingReasons, StringComparer.OrdinalIgnoreCase);
            foreach (var waiting in connectionPlan.WaitingReasons)
            {
                plan.Active.Remove(waiting.Key);
                waitingReasons[waiting.Key] = waiting.Value;
            }
            _queueWaitingReasons = waitingReasons;
            _seedConnectionCaps = connectionPlan.SeedCaps;
            var selected = !_shuttingDown && transfersAllowed && NetworkPolicy.Allowed ? plan.Active : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Stop excess jobs and reconnect over-budget seeds before any new starts.
            // Lowering MonoTorrent's settings alone does not evict existing peers.
            foreach (var pair in pairs)
                if (!_pausedHashes.ContainsKey(pair.Key) && !selected.Contains(pair.Key) &&
                    pair.Value.State is MonoTorrent.Client.TorrentState.Stopped or MonoTorrent.Client.TorrentState.Paused)
                    _queuedHashes.TryAdd(pair.Key, 0);
            var stopped = await TorrentBatch.RunBoundedAsync(pairs.Where(pair =>
                (!_pausedHashes.ContainsKey(pair.Key) && pair.Value.State is not
                    (MonoTorrent.Client.TorrentState.Error or MonoTorrent.Client.TorrentState.Hashing or MonoTorrent.Client.TorrentState.HashingPaused) &&
                ((!selected.Contains(pair.Key) && pair.Value.State != MonoTorrent.Client.TorrentState.Stopped &&
                    (pair.Value.State != MonoTorrent.Client.TorrentState.Paused || pair.Value.OpenConnections > 0)) ||
                    (pair.Value.State == MonoTorrent.Client.TorrentState.Seeding &&
                    pair.Value.Settings.MaximumConnections != EffectiveConnectionLimit(pair.Key, OptionsFor(pair.Key)) &&
                    pair.Value.OpenConnections > EffectiveConnectionLimit(pair.Key, OptionsFor(pair.Key))))) ||
                (_pausedHashes.ContainsKey(pair.Key) && pair.Value.State == MonoTorrent.Client.TorrentState.Paused && pair.Value.OpenConnections > 0))
                .Select(pair => pair.Key), async hash =>
            {
                if (FindManager(hash) is not { } manager) return false;
                if (manager.State != MonoTorrent.Client.TorrentState.Stopped &&
                    (manager.State != MonoTorrent.Client.TorrentState.Paused || manager.OpenConnections > 0))
                    await StopManagerAsync(manager, TimeSpan.FromSeconds(2));
                if (!selected.Contains(hash) && !_pausedHashes.ContainsKey(hash)) _queuedHashes[hash] = 0;
                return true;
            }, 8);
            if (stopped.Failures.Count > 0)
            {
                foreach (var failure in stopped.Failures) Services.Logger.Instance.Warn("Queue", $"Stop {failure.InfoHash}: {failure.Message}");
                var retryReasons = new Dictionary<string, string>(waitingReasons, StringComparer.OrdinalIgnoreCase);
                foreach (var pair in pairs.Where(p => selected.Contains(p.Key) && p.Value.State is MonoTorrent.Client.TorrentState.Stopped or MonoTorrent.Client.TorrentState.Paused))
                {
                    _queuedHashes[pair.Key] = 0;
                    retryReasons[pair.Key] = "Queued: waiting for other transfers to stop safely; retrying on the next scheduler pass.";
                }
                _queueWaitingReasons = retryReasons;
                InvalidateStats();
                return;
            }
            if (transfersAllowed && NetworkPolicy.Allowed)
                foreach (var pair in pairs)
                {
                    if (!selected.Contains(pair.Key) || _pausedHashes.ContainsKey(pair.Key)) continue;
                    var options = OptionsFor(pair.Key);
                    int cap = EffectiveConnectionLimit(pair.Key, options);
                    if (pair.Value.Settings.MaximumConnections != cap)
                    {
                        await ApplyTorrentOptionsAsync(pair.Value, options);
                    }
                    if (pair.Value.State is MonoTorrent.Client.TorrentState.Stopped or MonoTorrent.Client.TorrentState.Paused)
                        await StartNetworkGuardedAsync(pair.Value);
                    _queuedHashes.TryRemove(pair.Key, out _);
                }
            InvalidateStats();
        }
        finally { _queueGate.Release(); }
    }

    public async Task<bool> SetForceStartAsync(string hash, bool forced, bool persist = true)
    {
        if (_removalPending.ContainsKey(hash)) return false;
        await _queueGate.WaitAsync();
        try
        {
            if (_removalPending.ContainsKey(hash) || FindManager(hash) is not { } manager) return false;
            _options.AddOrUpdate(hash, OptionsFor(hash) with { ForceStart = forced }, (_, previous) => previous with { ForceStart = forced });
            if (forced) { _pausedHashes.TryRemove(hash, out _); await StartManagedAsync(manager); }
            if (persist) await SaveEngineStateAsync();
            InvalidateStats();
            return true;
        }
        finally { _queueGate.Release(); }
    }

    public void MoveQueue(IEnumerable<string> hashes, string direction)
    {
        lock (_queueOrderLock)
        {
            var selected = hashes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ordered = _managersByHash.Keys.OrderBy(h => OptionsFor(h).Position).ThenBy(h => h, StringComparer.OrdinalIgnoreCase).ToList();
            if (direction == "top") ordered = ordered.Where(selected.Contains).Concat(ordered.Where(h => !selected.Contains(h))).ToList();
            else if (direction == "bottom") ordered = ordered.Where(h => !selected.Contains(h)).Concat(ordered.Where(selected.Contains)).ToList();
            else if (direction == "up")
            {
                for (int i = 1; i < ordered.Count; i++)
                    if (selected.Contains(ordered[i]) && !selected.Contains(ordered[i - 1])) (ordered[i - 1], ordered[i]) = (ordered[i], ordered[i - 1]);
            }
            else if (direction == "down")
            {
                for (int i = ordered.Count - 2; i >= 0; i--)
                    if (selected.Contains(ordered[i]) && !selected.Contains(ordered[i + 1])) (ordered[i + 1], ordered[i]) = (ordered[i], ordered[i + 1]);
            }
            else throw new ArgumentException("Unknown queue direction.", nameof(direction));
            AssignQueuePositions(ordered);
        }
        InvalidateStats();
    }

    private Task ApplyTorrentOptionsAsync(TorrentManager manager, TorrentOptions options)
    {
        var builder = new TorrentSettingsBuilder(manager.Settings)
        {
            MaximumDownloadRate = checked(options.DownloadKBps * 1024),
            MaximumUploadRate = checked(options.UploadKBps * 1024),
            MaximumConnections = EffectiveConnectionLimit(manager.InfoHashes.V1OrV2.ToHex(), options),
            UploadSlots = options.UploadSlots ?? _defaultUploadSlots,
            AllowPeerExchange = _pexEnabled
        };
        return manager.UpdateSettingsAsync(builder.ToSettings());
    }

    private int EffectiveConnectionLimit(string hash, TorrentOptions options)
    {
        int configured = options.MaximumConnections ?? _defaultTorrentConnections;
        return !options.ForceStart && _seedConnectionCaps.TryGetValue(hash, out int cap) ? Math.Min(configured, cap) : configured;
    }

    public async Task<bool> SetTorrentLimitsAsync(string hash, int downloadKBps, int uploadKBps, bool persist = true)
    {
        if (downloadKBps is < 0 or > 1000000 || uploadKBps is < 0 or > 1000000) throw new ArgumentOutOfRangeException(nameof(downloadKBps));
        await _queueGate.WaitAsync();
        try
        {
            if (FindManager(hash) is not { } manager) return false;
            var options = OptionsFor(hash) with { DownloadKBps = downloadKBps, UploadKBps = uploadKBps };
            await ApplyTorrentOptionsAsync(manager, options);
            _options.AddOrUpdate(hash, options, (_, previous) => previous with { DownloadKBps = downloadKBps, UploadKBps = uploadKBps });
            if (persist) await SaveEngineStateAsync();
            return true;
        }
        finally { _queueGate.Release(); }
    }

    public static string[] ValidateTrackerUrls(IEnumerable<string> urls) => urls.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s =>
    {
        if (!Uri.TryCreate(s.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "udp"))
            throw new ArgumentException("Trackers must be absolute http, https or udp URLs.");
        return uri.AbsoluteUri;
    }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static async Task ReplaceTrackersCoreAsync(TorrentManager manager, string[] urls)
    {
        if (manager.Torrent?.IsPrivate == true) throw new InvalidOperationException("Tracker replacement is disabled for private torrents.");
        foreach (var tracker in manager.TrackerManager.Tiers.SelectMany(t => t.Trackers).ToArray())
            await manager.TrackerManager.RemoveTrackerAsync(tracker);
        foreach (var url in urls) await manager.TrackerManager.AddTrackerAsync(new Uri(url));
    }

    public async Task<bool> ReplaceTrackersAsync(string hash, IEnumerable<string> urls)
    {
        string[] validated = ValidateTrackerUrls(urls);
        if (FindManager(hash) is not { } manager) return false;
        if (manager.Torrent?.IsPrivate == true) throw new InvalidOperationException("Tracker replacement is disabled for private torrents.");
        var previous = manager.TrackerManager.Tiers.SelectMany(t => t.Trackers).Select(t => t.Uri.AbsoluteUri).ToArray();
        try { await ReplaceTrackersCoreAsync(manager, validated); }
        catch { await ReplaceTrackersCoreAsync(manager, previous); throw; }
        _options.AddOrUpdate(hash, OptionsFor(hash) with { Trackers = validated }, (_, previous) => previous with { Trackers = validated });
        await SaveEngineStateAsync();
        return true;
    }

    public async Task<string> ImportTorrentFileAsync(string path, string savePath, string? category = null, bool persist = true)
    {
        var torrent = await MonoTorrent.Torrent.LoadAsync(path);
        var existing = FindManager(torrent.InfoHashes.V1OrV2.ToHex());
        if (existing != null) return existing.InfoHashes.V1OrV2.ToHex();
        var manager = await _engine.AddAsync(torrent, ResolveSavePath(savePath));
        string hash = manager.InfoHashes.V1OrV2.ToHex();
        RegisterManager(hash, manager);
        _pausedHashes[hash] = 0;
        if (!string.IsNullOrEmpty(category)) _categories[hash] = category;
        _addedDates[hash] = DateTime.UtcNow;
        OptionsFor(hash);
        InvalidateStats();
        if (persist) await SaveEngineStateAsync();
        return hash;
    }
}
