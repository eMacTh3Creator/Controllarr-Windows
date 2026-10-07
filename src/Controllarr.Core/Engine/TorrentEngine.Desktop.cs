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
    private TorrentQueueing _queueSettings = new();
    private long _nextPosition;

    private TorrentOptions OptionsFor(string hash) => _options.GetOrAdd(hash, _ => new TorrentOptions { Position = Interlocked.Increment(ref _nextPosition) });
    public TorrentOptions GetOptions(string hash) => OptionsFor(hash);

    private void RestoreDesktopOptions()
    {
        try
        {
            string file = _stateFilePath + ".options.json";
            if (File.Exists(file))
                foreach (var pair in JsonSerializer.Deserialize<Dictionary<string, TorrentOptions>>(File.ReadAllText(file)) ?? new())
                    if (_managersByHash.ContainsKey(pair.Key)) _options[pair.Key] = pair.Value;
            _nextPosition = _options.Values.Select(o => o.Position).DefaultIfEmpty().Max();
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
        _queueSettings = new TorrentQueueing { Enabled = settings.Enabled, MaxActiveDownloads = settings.MaxActiveDownloads,
            MaxActiveSeeds = settings.MaxActiveSeeds, MaxActiveTotal = settings.MaxActiveTotal };
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

    public async Task TickQueueAsync(bool transfersAllowed)
    {
        if (_disposed || !await _queueGate.WaitAsync(0)) return;
        try
        {
            var pairs = _managersByHash.ToArray();
            var selected = !_shuttingDown && transfersAllowed && NetworkPolicy.Allowed ? QueuePlanner.Select(pairs.Select(p => new QueueCandidate(p.Key, OptionsFor(p.Key).Position,
                p.Value.Progress >= 100, _pausedHashes.ContainsKey(p.Key), p.Value.State == MonoTorrent.Client.TorrentState.Error,
                OptionsFor(p.Key).ForceStart)), _queueSettings) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Stop excess work before starting anything else, avoiding transient floods.
            foreach (var pair in pairs)
            {
                if (_pausedHashes.ContainsKey(pair.Key) || selected.Contains(pair.Key) || pair.Value.State == MonoTorrent.Client.TorrentState.Error) continue;
                if (pair.Value.State is MonoTorrent.Client.TorrentState.Hashing or MonoTorrent.Client.TorrentState.HashingPaused) continue;
                if (pair.Value.State is not (MonoTorrent.Client.TorrentState.Stopped or MonoTorrent.Client.TorrentState.Paused))
                    await StopManagerAsync(pair.Value);
                _queuedHashes[pair.Key] = 0;
            }
            if (transfersAllowed && NetworkPolicy.Allowed)
                foreach (var pair in pairs)
                {
                    if (!selected.Contains(pair.Key) || _pausedHashes.ContainsKey(pair.Key)) continue;
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
        var selected = hashes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ordered = _managersByHash.Keys.OrderBy(h => OptionsFor(h).Position).ToList();
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
        for (int i = 0; i < ordered.Count; i++)
        {
            string hash = ordered[i]; long position = i + 1;
            _options.AddOrUpdate(hash, _ => new TorrentOptions { Position = position }, (_, previous) => previous with { Position = position });
        }
        Interlocked.Exchange(ref _nextPosition, ordered.Count);
        InvalidateStats();
    }

    private Task ApplyTorrentOptionsAsync(TorrentManager manager, TorrentOptions options)
    {
        var builder = new TorrentSettingsBuilder(manager.Settings) { MaximumDownloadRate = checked(options.DownloadKBps * 1024),
            MaximumUploadRate = checked(options.UploadKBps * 1024),
            MaximumConnections = options.MaximumConnections ?? _defaultTorrentConnections,
            UploadSlots = options.UploadSlots ?? _defaultUploadSlots, AllowPeerExchange = _pexEnabled };
        return manager.UpdateSettingsAsync(builder.ToSettings());
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
        _managersByHash[hash] = manager;
        _pausedHashes[hash] = 0;
        if (!string.IsNullOrEmpty(category)) _categories[hash] = category;
        _addedDates[hash] = DateTime.UtcNow;
        OptionsFor(hash);
        InvalidateStats();
        if (persist) await SaveEngineStateAsync();
        return hash;
    }
}
