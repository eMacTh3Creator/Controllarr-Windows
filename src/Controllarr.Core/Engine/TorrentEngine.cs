using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using MonoTorrent.Trackers;
using Controllarr.Core.Networking;
using Controllarr.Core.Persistence;

namespace Controllarr.Core.Engine;

// ───────────────────────────────────────────────────────────────
// Value types
// ───────────────────────────────────────────────────────────────

/// <summary>
/// Unified torrent state that abstracts over MonoTorrent's internal states.
/// </summary>
public enum TorrentState
{
    Unknown = 0,
    CheckingFiles = 1,
    DownloadingMetadata = 2,
    Downloading = 3,
    Finished = 4,
    Seeding = 5,
    CheckingResume = 6,
    Paused = 7,
    Error = 8,
    Queued = 9
}

/// <summary>
/// Snapshot of an individual torrent's statistics.
/// </summary>
public sealed class TorrentStats
{
    public string Name { get; init; } = string.Empty;
    public string InfoHash { get; init; } = string.Empty;
    public string SavePath { get; init; } = string.Empty;
    public string ContentPath { get; init; } = string.Empty;
    public bool HasMetadata { get; init; }
    public float Progress { get; init; }
    public TorrentState State { get; init; }
    public bool Paused { get; init; }
    public long DownloadRate { get; init; }
    public long UploadRate { get; init; }
    public long TotalWanted { get; init; }
    public long TotalDone { get; init; }
    public long TotalDownload { get; init; }
    public long TotalUpload { get; init; }
    public double Ratio { get; init; }
    public int NumPeers { get; init; }
    public int NumSeeds { get; init; }
    public int EtaSeconds { get; init; } = -1;
    public DateTime AddedDate { get; init; }
    public string? Category { get; init; }
    public long QueuePosition { get; init; }
    public bool ForceStart { get; init; }
    public string StatusReason { get; init; } = string.Empty;
}

/// <summary>
/// Aggregate session-level statistics.
/// </summary>
public sealed class SessionStats
{
    public long DownloadRate { get; init; }
    public long UploadRate { get; init; }
    public long TotalDownloaded { get; init; }
    public long TotalUploaded { get; init; }
    public int NumTorrents { get; init; }
    public int NumPeersConnected { get; init; }
    public int ConnectionLimit { get; init; }
    public int DhtNodes { get; init; }
    public string DhtState { get; init; } = "NotReady";
    public bool HasIncomingConnections { get; init; }
    public ushort ListenPort { get; init; }
}

/// <summary>
/// Information about a single tracker associated with a torrent.
/// </summary>
public sealed class TrackerInfo
{
    public string Url { get; init; } = string.Empty;
    public int Tier { get; init; }
    public int NumPeers { get; init; }
    public int NumSeeds { get; init; }
    public int NumLeechers { get; init; }
    public int NumDownloaded { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool HasScrapeInfo { get; init; }
    public string SeedCountText => HasScrapeInfo ? NumSeeds.ToString() : "Unknown";
    public string PeerCountText => HasScrapeInfo ? NumPeers.ToString() : "Unknown";

    /// <summary>Status code: 0 = not contacted, 1 = working, 2 = updating, 3 = error, 4 = unreachable.</summary>
    public int Status { get; init; }
}

/// <summary>
/// Information about a single connected peer.
/// </summary>
public sealed class PeerInfo
{
    public string Ip { get; init; } = string.Empty;
    public int Port { get; init; }
    public string Client { get; init; } = string.Empty;
    public float Progress { get; init; }
    public long DownloadRate { get; init; }
    public long UploadRate { get; init; }
    public long TotalDownload { get; init; }
    public long TotalUpload { get; init; }
    public string Flags { get; init; } = string.Empty;
    public string Country { get; init; } = string.Empty;
}

/// <summary>
/// Information about a single file inside a torrent.
/// </summary>
public sealed class FileInfo
{
    public int Index { get; init; }
    public string Name { get; init; } = string.Empty;
    public long Size { get; init; }
    public int Priority { get; init; }
}

// ───────────────────────────────────────────────────────────────
// Engine
// ───────────────────────────────────────────────────────────────

/// <summary>
/// Thread-safe wrapper around MonoTorrent's <see cref="ClientEngine"/> that exposes
/// a clean, high-level API for the Controllarr application layer.
/// </summary>
public sealed partial class TorrentEngine : IDisposable
{
    // ── Core engine ────────────────────────────────────────────
    private readonly ClientEngine _engine;
    private readonly SemaphoreSlim _stateSaveGate = new(1, 1);
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private readonly object _snapshotLock = new();
    private TorrentStats[]? _cachedStats;
    private long _cachedStatsAt;
    private readonly ConcurrentDictionary<string, TorrentManager> _managersByHash = new(StringComparer.OrdinalIgnoreCase);

    // ── Configuration ──────────────────────────────────────────
    private string _defaultSavePath;
    private readonly string _resumeDataDirectory;
    private readonly string _stateFilePath;
    private readonly ConcurrentDictionary<string, byte> _pausedHashes = new(StringComparer.OrdinalIgnoreCase);
    private ushort _listenPort;

    // ── Per-torrent metadata ───────────────────────────────────
    private readonly ConcurrentDictionary<string, string> _categories = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _addedDates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, HashSet<string>> _blockedExtensions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _filteredHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _filteredLock = new();

    // ── Lifetime ───────────────────────────────────────────────
    private bool _disposed;
    private volatile bool _shuttingDown;

    // ───────────────────────────────────────────────────────────
    // Constructor
    // ───────────────────────────────────────────────────────────

    public TorrentEngine(string defaultSavePath, string resumeDataDirectory, ushort listenPort, Settings? initialSettings = null)
    {
        _defaultSavePath = defaultSavePath ?? throw new ArgumentNullException(nameof(defaultSavePath));
        _createTorrentSubfolders = initialSettings?.CreateTorrentSubfolders ?? false;
        _resumeDataDirectory = resumeDataDirectory ?? throw new ArgumentNullException(nameof(resumeDataDirectory));
        _listenPort = listenPort;
        NetworkPolicy = new TorrentNetworkPolicy(initialSettings ?? new Settings());
        _duplicatePolicy = initialSettings?.DuplicateTorrentPolicy ?? DuplicateTorrentPolicy.MergeTrackers;
        var factories = TorrentNetworkFactories.Create(NetworkPolicy);

        Directory.CreateDirectory(_defaultSavePath);
        Directory.CreateDirectory(_resumeDataDirectory);

        // The engine state file holds the full torrent LIST (so torrents
        // survive restarts and updates). Fast-resume restores their progress.
        _stateFilePath = Path.Combine(_resumeDataDirectory, "engine.state");
        try
        {
            string path = _stateFilePath + ".paused.json";
            if (File.Exists(path))
                foreach (var hash in JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? Array.Empty<string>())
                    _pausedHashes.TryAdd(hash, 0);
        }
        catch (Exception ex) { Services.Logger.Instance.Warn("Engine", $"Could not load paused torrents: {ex.Message}"); }

        var settings = new EngineSettingsBuilder
        {
            CacheDirectory = _resumeDataDirectory,
            ListenEndPoints = new Dictionary<string, System.Net.IPEndPoint>
            {
                ["ipv4"] = new System.Net.IPEndPoint(System.Net.IPAddress.Any, _listenPort)
            },
            AllowPortForwarding = !NetworkPolicy.RestrictedDiscovery,
            AllowLocalPeerDiscovery = !NetworkPolicy.RestrictedDiscovery && (initialSettings?.PeerDiscovery.LsdEnabled ?? true),
            DhtEndPoint = !NetworkPolicy.DhtAllowed || initialSettings?.PeerDiscovery.DhtEnabled == false ? null : new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0),
            AutoSaveLoadFastResume = false,
        }.ToSettings();

        // Restore previously-added torrents if we have saved engine state.
        ClientEngine? restored = null;
        if (File.Exists(_stateFilePath))
        {
            try
            {
                // Older profiles used library-owned writes, which can overlap
                // startup and SaveState. Disable them before restoring managers.
                var saved = BEncodedValue.Decode<BEncodedDictionary>(File.ReadAllBytes(_stateFilePath));
                RebaseProfileCache(saved, _resumeDataDirectory);
                ((BEncodedDictionary)saved["Settings"])["AutoSaveLoadFastResume"] = new BEncodedString("False");
                restored = ClientEngine.RestoreStateAsync(saved.Encode(), factories).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Error("Engine", $"Could not restore torrent state: {ex.Message}");
                restored = null; // corrupt/incompatible state — start fresh
            }
        }

        if (restored != null)
        {
            _engine = restored;

            // Enforce our cache dir + listen port over the saved settings so a
            // preferred-port change (or a moved profile) is still honored.
            try
            {
                var rebuilt = new EngineSettingsBuilder(_engine.Settings)
                {
                    CacheDirectory = _resumeDataDirectory,
                    ListenEndPoints = new Dictionary<string, System.Net.IPEndPoint>
                    {
                        ["ipv4"] = new System.Net.IPEndPoint(System.Net.IPAddress.Any, _listenPort)
                    },
                    AllowPortForwarding = !NetworkPolicy.RestrictedDiscovery,
                    AllowLocalPeerDiscovery = !NetworkPolicy.RestrictedDiscovery && (initialSettings?.PeerDiscovery.LsdEnabled ?? true),
                    DhtEndPoint = !NetworkPolicy.DhtAllowed || initialSettings?.PeerDiscovery.DhtEnabled == false ? null : settings.DhtEndPoint,
                    AutoSaveLoadFastResume = false,
                }.ToSettings();
                _engine.UpdateSettingsAsync(rebuilt).GetAwaiter().GetResult();
            }
            catch { /* keep restored settings if the update fails */ }

            // Seed added-dates for restored torrents (originals aren't persisted).
            foreach (var mgr in _engine.Torrents)
            {
                var hash = mgr.InfoHashes.V1OrV2.ToHex();
                _managersByHash[hash] = mgr;
                _addedDates.TryAdd(hash, DateTime.UtcNow);
            }
        }
        else
        {
            _engine = new ClientEngine(settings, factories);
        }
        RestoreResumeCheckpoints();
        RestoreDesktopOptions();
        InitializeDiscovery();
    }

    // ───────────────────────────────────────────────────────────
    // Properties
    // ───────────────────────────────────────────────────────────

    public string DefaultSavePath
    {
        get => _defaultSavePath;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Directory.CreateDirectory(value);
            _defaultSavePath = value;
        }
    }

    public ushort ListenPort => _listenPort;

    // ───────────────────────────────────────────────────────────
    // Adding torrents
    // ───────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a torrent via its magnet URI and starts downloading.
    /// </summary>
    /// <returns>The hex info-hash of the added torrent.</returns>
    public async Task<string> AddMagnet(string uri, string? category = null, string? savePath = null, bool persist = true)
    {
        ThrowIfDisposed();

        var magnet = MagnetLink.Parse(uri);
        return await AddSourceAsync(magnet, null, category, savePath, persist);
    }

    /// <summary>
    /// Adds a torrent from a .torrent file on disk and starts downloading.
    /// </summary>
    /// <returns>The hex info-hash of the added torrent.</returns>
    public async Task<string> AddTorrentFile(string filePath, string? category = null, string? savePath = null, bool persist = true)
    {
        ThrowIfDisposed();

        if (!File.Exists(filePath))
            throw new System.IO.FileNotFoundException("Torrent file not found.", filePath);

        var torrent = await Torrent.LoadAsync(filePath);
        return await AddSourceAsync(null, torrent, category, savePath, persist);
    }

    private async Task<string> AddSourceAsync(MagnetLink? magnet, Torrent? torrent, string? category, string? savePath, bool persist)
    {
        // Serialize intake with removal/scheduling, but never block a thread on
        // MonoTorrent's asynchronous main loop or perform whole-library checkpoints per add.
        await _queueGate.WaitAsync();
        try
        {
            ThrowIfDisposed();
            string hash = (magnet?.InfoHashes ?? torrent!.InfoHashes).V1OrV2.ToHex();
            if (FindManager(hash) is { } duplicate)
            {
                await MergeDuplicateTrackersAsync(duplicate, magnet?.AnnounceUrls ?? torrent!.AnnounceUrls.SelectMany(t => t).ToArray());
                if (persist) await SaveEngineStateAsync(checkpointResume: false);
                return hash;
            }
            string save = ResolveIntakePath(savePath, category, torrent?.Name ?? magnet?.Name, hash, out string? folder);
            var layout = new TorrentSettingsBuilder { CreateContainingDirectory = folder == null }.ToSettings();
            var manager = magnet != null ? await _engine.AddAsync(magnet, save, layout) : await _engine.AddAsync(torrent!, save, layout);
            RegisterManager(hash, manager);
            _options[hash] = OptionsFor(hash) with { StorageSubfolder = folder };
            if (!string.IsNullOrEmpty(category)) _categories[hash] = category;
            _addedDates[hash] = DateTime.UtcNow;
            await StartManagedAsync(manager);
            InvalidateStats();
            if (persist) await SaveEngineStateAsync(checkpointResume: false);
            return hash;
        }
        finally { _queueGate.Release(); }
    }

    // ───────────────────────────────────────────────────────────
    // Torrent control
    // ───────────────────────────────────────────────────────────

    public async Task<bool> Pause(string infoHash)
    {
        var mgr = FindManager(infoHash);
        if (mgr is null) return false;

        await _queueGate.WaitAsync();
        try
        {
            // MonoTorrent PauseAsync is a no-op during metadata/hash fetching.
            // Fully stop those modes so a user pause always closes transfer activity.
            if (mgr.State is MonoTorrent.Client.TorrentState.Downloading or MonoTorrent.Client.TorrentState.Seeding or MonoTorrent.Client.TorrentState.Hashing or MonoTorrent.Client.TorrentState.HashingPaused)
                await mgr.PauseAsync();
            else if (mgr.State is not (MonoTorrent.Client.TorrentState.Stopped or MonoTorrent.Client.TorrentState.Paused))
                await StopManagerAsync(mgr, TimeSpan.FromSeconds(2));
            _pausedHashes[infoHash] = 0;
            _queuedHashes.TryRemove(infoHash, out _);
            _options.AddOrUpdate(infoHash, OptionsFor(infoHash) with { ForceStart = false }, (_, previous) => previous with { ForceStart = false });
            InvalidateStats();
            return true;
        }
        catch (Exception ex)
        {
            Services.Logger.Instance.Warn("Engine", $"Pause {infoHash} failed in {mgr.State}: {ex}");
            return false;
        }
        finally { _queueGate.Release(); }
    }

    public async Task<bool> Resume(string infoHash)
    {
        if (_removalPending.ContainsKey(infoHash)) return false;
        var mgr = FindManager(infoHash);
        if (mgr is null) return false;

        await _queueGate.WaitAsync();
        try
        {
            if (_removalPending.ContainsKey(infoHash) || !ReferenceEquals(FindManager(infoHash), mgr)) return false;
            _pausedHashes.TryRemove(infoHash, out _);
            await StartManagedAsync(mgr);
            InvalidateStats();
            return true;
        }
        catch
        {
            _pausedHashes[infoHash] = 0;
            return false;
        }
        finally { _queueGate.Release(); }
    }

    public async Task<bool> Remove(string infoHash, bool deleteFiles, bool persist = true)
    {
        var mgr = FindManager(infoHash);
        if (mgr is null) return false;

        await _queueGate.WaitAsync();
        try
        {
            await StopManagerAsync(mgr, TimeSpan.FromSeconds(2));
            await RemoveStoppedAsync(infoHash, mgr, deleteFiles);
            CompactQueuePositions();
            if (persist) await SaveEngineStateAsync(checkpointResume: false);
            return true;
        }
        catch (Exception ex)
        {
            Services.Logger.Instance.Warn("Engine", $"Remove {infoHash} failed in {mgr.State}: {ex}");
            return false;
        }
        finally { _queueGate.Release(); }
    }

    public async Task<bool> Move(string infoHash, string newPath)
    {
        var mgr = FindManager(infoHash);
        if (mgr is null) return false;

        await _queueGate.WaitAsync();
        try
        {
            if (!mgr.HasMetadata || _removalPending.ContainsKey(infoHash)) return false;
            newPath = StoragePaths.Normalize(newPath);
            if (StoragePaths.Equal(newPath, mgr.SavePath) || StoragePaths.Equal(newPath, mgr.ContainingDirectory)) return true;
            newPath = ResolveMovePath(mgr, newPath);
            if (StoragePaths.Equal(newPath, mgr.ContainingDirectory)) return true;
            foreach (var file in mgr.Files)
            {
                string target = Path.Combine(newPath, file.Path);
                if (File.Exists(target) && !StoragePaths.Equal(target, file.DownloadCompleteFullPath))
                    throw new IOException($"Destination already contains {file.Path}; no files were replaced.");
                string suffix = file.DownloadIncompleteFullPath[file.DownloadCompleteFullPath.Length..];
                if (suffix.Length > 0 && File.Exists(target + suffix) && !StoragePaths.Equal(target + suffix, file.DownloadIncompleteFullPath))
                    throw new IOException($"Destination already contains an incomplete {file.Path}; no files were replaced.");
            }
            Directory.CreateDirectory(newPath);
            bool resume = mgr.State is not MonoTorrent.Client.TorrentState.Paused and not MonoTorrent.Client.TorrentState.Stopped;
            await StopManagerAsync(mgr);
            await mgr.MoveFilesAsync(newPath, false);
            if (mgr.State == MonoTorrent.Client.TorrentState.Error)
                throw new IOException(mgr.Error?.Exception.Message ?? "The torrent engine reported a storage error.");
            await mgr.UpdateSettingsAsync(new TorrentSettingsBuilder(mgr.Settings) { CreateContainingDirectory = false }.ToSettings());
            if (mgr.Torrent!.Files.Count > 1 || OptionsFor(infoHash).StorageSubfolder != null)
                _options[infoHash] = OptionsFor(infoHash) with { StorageSubfolder = Path.GetFileName(newPath) };
            if (resume) await StartManagedAsync(mgr);
            InvalidateStats();
            await SaveEngineStateAsync(checkpointResume: false);
            return true;
        }
        catch (Exception ex)
        {
            Services.Logger.Instance.Warn("Storage", $"Move failed for {infoHash}: {ex.Message}");
            InvalidateStats();
            return false;
        }
        finally { _queueGate.Release(); }
    }

    public async Task<bool> Recheck(string infoHash)
    {
        var manager = FindManager(infoHash);
        if (manager == null || !manager.HasMetadata) return false;
        await _queueGate.WaitAsync();
        try
        {
            await StopManagerAsync(manager);
            _pausedHashes[infoHash] = 0;
            _checkingHashes[infoHash] = 0;
            await InvalidateResumeCheckpointAsync(manager);
            await manager.HashCheckAsync(autoStart: false);
            bool verified = manager.HashChecked && manager.State != MonoTorrent.Client.TorrentState.Error;
            if (!verified) Services.Logger.Instance.Warn("Engine", $"Recheck {infoHash}: state={manager.State}, checked={manager.HashChecked}, error={manager.Error}");
            return verified;
        }
        catch (Exception ex)
        {
            Services.Logger.Instance.Warn("Engine", $"Recheck {infoHash} failed in {manager.State}: {ex}");
            return false;
        }
        finally { _checkingHashes.TryRemove(infoHash, out _); InvalidateStats(); _queueGate.Release(); }
    }

    // ───────────────────────────────────────────────────────────
    // Categories
    // ───────────────────────────────────────────────────────────

    public void SetCategory(string? category, string infoHash)
    {
        if (string.IsNullOrEmpty(category))
            _categories.TryRemove(infoHash, out _);
        else
            _categories[infoHash] = category;
        lock (_filteredLock) _filteredHashes.Remove(infoHash);
        InvalidateStats();
    }

    public Dictionary<string, string> SnapshotCategories()
    {
        return new Dictionary<string, string>(_categories, StringComparer.OrdinalIgnoreCase);
    }

    public void RestoreCategories(Dictionary<string, string> map)
    {
        if (map is null) return;

        foreach (var kvp in map)
            _categories[kvp.Key] = kvp.Value;
    }

    // ───────────────────────────────────────────────────────────
    // Blocked-extension filtering
    // ───────────────────────────────────────────────────────────

    /// <summary>
    /// Registers file extensions that should be automatically set to "do not download"
    /// for any torrent assigned to the given <paramref name="category"/>.
    /// Extensions should include the leading dot (e.g. ".exe", ".bat").
    /// </summary>
    public void RegisterBlockedExtensions(string[] extensions, string category)
    {
        if (extensions is null || extensions.Length == 0)
        {
            _blockedExtensions.TryRemove(category, out _);
            return;
        }

        var normalized = new HashSet<string>(
            extensions.Select(e => e.StartsWith('.') ? e : "." + e),
            StringComparer.OrdinalIgnoreCase);

        _blockedExtensions[category] = normalized;
        lock (_filteredLock) _filteredHashes.Clear();
    }

    /// <summary>
    /// Scans all managed torrents and sets the priority to <c>DoNotDownload</c>
    /// for files whose extension is in the blocked list for that torrent's category.
    /// Each torrent is only processed once unless its info-hash is removed and re-added.
    /// </summary>
    public void ApplyPendingFileFilters()
    {
        foreach (var mgr in _managersByHash.Values)
        {
            var hash = mgr.InfoHashes.V1OrV2.ToHex();

            lock (_filteredLock)
            {
                if (_filteredHashes.Contains(hash))
                    continue;
            }

            if (!_categories.TryGetValue(hash, out var cat))
                continue;

            if (!_blockedExtensions.TryGetValue(cat, out var blocked) || blocked.Count == 0)
                continue;

            // Metadata may not have arrived yet.
            if (mgr.Files is null || mgr.Files.Count == 0)
                continue;

            foreach (var file in mgr.Files)
            {
                var ext = Path.GetExtension(file.Path);
                if (!string.IsNullOrEmpty(ext) && blocked.Contains(ext))
                {
                    try
                    {
                        mgr.SetFilePriorityAsync(file, Priority.DoNotDownload)
                            .GetAwaiter().GetResult();
                    }
                    catch
                    {
                        // Best-effort; log externally if needed.
                    }
                }
            }

            lock (_filteredLock)
                _filteredHashes.Add(hash);
        }
    }

    // ───────────────────────────────────────────────────────────
    // File operations
    // ───────────────────────────────────────────────────────────

    public string[]? GetFileNames(string infoHash)
    {
        var mgr = FindManager(infoHash);
        if (mgr?.Files is null || mgr.Files.Count == 0)
            return null;

        return mgr.Files.Select(f => f.Path).ToArray();
    }

    public async Task<bool> SetFilePriorities(int[] priorities, string infoHash)
    {
        var mgr = FindManager(infoHash);
        if (mgr?.Files is null || mgr.Files.Count == 0)
            return false;

        if (priorities.Length != mgr.Files.Count)
            return false;

        try
        {
            for (int i = 0; i < mgr.Files.Count; i++)
            {
                var prio = priorities[i] switch
                {
                    0 => Priority.DoNotDownload,
                    1 => Priority.Lowest,
                    2 => Priority.Low,
                    3 => Priority.Normal,
                    4 => Priority.High,
                    5 => Priority.Highest,
                    6 => Priority.Highest,
                    7 => Priority.Immediate,
                    _ => Priority.Normal
                };
                await mgr.SetFilePriorityAsync(mgr.Files[i], prio);
            }
            _options.AddOrUpdate(infoHash, OptionsFor(infoHash) with { FilePriorities = priorities.ToArray() }, (_, previous) => previous with { FilePriorities = priorities.ToArray() });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public FileInfo[]? GetFileInfo(string infoHash)
    {
        var mgr = FindManager(infoHash);
        if (mgr?.Files is null || mgr.Files.Count == 0)
            return null;

        var result = new FileInfo[mgr.Files.Count];
        for (int i = 0; i < mgr.Files.Count; i++)
        {
            var f = mgr.Files[i];
            result[i] = new FileInfo
            {
                Index = i,
                Name = f.Path,
                Size = f.Length,
                Priority = MapPriorityToInt(f.Priority)
            };
        }

        return result;
    }

    // ───────────────────────────────────────────────────────────
    // Trackers / Peers / Reannounce
    // ───────────────────────────────────────────────────────────

    public TrackerInfo[]? GetTrackers(string infoHash)
    {
        var mgr = FindManager(infoHash);
        if (mgr is null) return null;

        var result = new List<TrackerInfo>();
        var infoHashKey = mgr.InfoHashes.V1 ?? mgr.InfoHashes.V2!;

        int tierIndex = 0;
        foreach (var tier in mgr.TrackerManager.Tiers)
        {
            // ScrapeInfo is a Dictionary<InfoHash, ScrapeInfo> on the tier.
            ScrapeInfo? scrape = null;
            if (tier.ScrapeInfo is not null && tier.ScrapeInfo.TryGetValue(infoHashKey, out ScrapeInfo scrapeResult))
            {
                scrape = scrapeResult;
            }

            foreach (var tracker in tier.Trackers)
            {
                int seeds = scrape?.Complete ?? 0;
                int leechers = scrape?.Incomplete ?? 0;
                int downloaded = scrape?.Downloaded ?? 0;

                result.Add(new TrackerInfo
                {
                    Url = tracker.Uri.ToString(),
                    Tier = tierIndex,
                    NumPeers = seeds + leechers,
                    NumSeeds = seeds,
                    NumLeechers = leechers,
                    NumDownloaded = downloaded,
                    Message = tracker.FailureMessage ?? tracker.WarningMessage ?? string.Empty,
                    Status = MapTrackerStatus(tracker.Status),
                    HasScrapeInfo = scrape != null
                });
            }

            tierIndex++;
        }

        return result.ToArray();
    }

    public PeerInfo[]? GetPeers(string infoHash)
    {
        var mgr = FindManager(infoHash);
        if (mgr is null) return null;

        try
        {
            var peers = mgr.GetPeersAsync().GetAwaiter().GetResult();
            return peers.Select(p =>
            {
                // Compute progress from the peer's bitfield relative to the torrent's piece count.
                float progress = 0f;
                if (p.BitField.Length > 0)
                    progress = (float)p.BitField.TrueCount / p.BitField.Length;

                return new PeerInfo
                {
                    Ip = p.Uri.Host,
                    Port = p.Uri.Port,
                    Client = p.ClientApp.Client.ToString(),
                    Progress = progress,
                    DownloadRate = p.Monitor.DownloadRate,
                    UploadRate = p.Monitor.UploadRate,
                    TotalDownload = p.Monitor.DataBytesDownloaded,
                    TotalUpload = p.Monitor.DataBytesUploaded,
                    Flags = BuildPeerFlags(p),
                    Country = string.Empty // MonoTorrent does not provide GeoIP data.
                };
            }).ToArray();
        }
        catch
        {
            return null;
        }
    }

    // ───────────────────────────────────────────────────────────
    // Statistics
    // ───────────────────────────────────────────────────────────

    public TorrentStats[] PollStats()
    {
        lock (_snapshotLock)
        {
            long now = Stopwatch.GetTimestamp();
            if (_cachedStats != null && Stopwatch.GetElapsedTime(_cachedStatsAt, now) < TimeSpan.FromMilliseconds(250))
                return _cachedStats;
            // Concurrent index avoids enumerating MonoTorrent's changing manager list from API threads.
            _cachedStats = _managersByHash.Values.Select(BuildStats).ToArray();
            _cachedStatsAt = now;
            return _cachedStats;
        }
    }

    public TorrentStats? GetStats(string infoHash)
    {
        var mgr = FindManager(infoHash);
        return mgr is null ? null : BuildStats(mgr);
    }

    public SessionStats GetSessionStats()
    {
        long dlTotal = 0, ulTotal = 0;
        int peers = 0;

        var snapshots = PollStats();
        foreach (var torrent in snapshots)
        {
            dlTotal += torrent.TotalDownload;
            ulTotal += torrent.TotalUpload;
            peers += torrent.NumPeers;
        }

        return new SessionStats
        {
            DownloadRate = _engine.TotalDownloadRate,
            UploadRate = _engine.TotalUploadRate,
            TotalDownloaded = dlTotal,
            TotalUploaded = ulTotal,
            NumTorrents = snapshots.Length,
            NumPeersConnected = peers,
            ConnectionLimit = _engine.Settings.MaximumConnections,
            DhtNodes = _engine.Dht.NodeCount,
            DhtState = _engine.Settings.DhtEndPoint == null ? "Disabled" : _engine.Dht.State.ToString(),
            HasIncomingConnections = _engine.ConnectionManager.OpenConnections > 0,
            ListenPort = _listenPort
        };
    }

    // ───────────────────────────────────────────────────────────
    // Engine-level operations
    // ───────────────────────────────────────────────────────────

    public async Task SetListenPort(ushort port)
    {
        ThrowIfDisposed();

        await _settingsGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (port == _listenPort) return;
            var newSettings = new EngineSettingsBuilder(_engine.Settings)
            {
                ListenEndPoints = new Dictionary<string, System.Net.IPEndPoint>
                {
                    ["ipv4"] = new System.Net.IPEndPoint(System.Net.IPAddress.Any, port)
                }
            }.ToSettings();
            await _engine.UpdateSettingsAsync(newSettings).ConfigureAwait(false);
            _listenPort = port;
        }
        finally { _settingsGate.Release(); }
    }

    public void SetRateLimits(int? downloadKBps, int? uploadKBps)
    {
        ThrowIfDisposed();

        _settingsGate.Wait();
        try
        {
            var builder = new EngineSettingsBuilder(_engine.Settings);
            builder.MaximumDownloadRate = downloadKBps.HasValue ? checked(downloadKBps.Value * 1024) : 0;
            builder.MaximumUploadRate = uploadKBps.HasValue ? checked(uploadKBps.Value * 1024) : 0;
            _engine.UpdateSettingsAsync(builder.ToSettings()).GetAwaiter().GetResult();
        }
        finally { _settingsGate.Release(); }
    }

    /// <summary>
    /// Applies the connection-limit and peer-discovery tuning knobs from
    /// persisted settings to the live engine. Best-effort: knobs MonoTorrent
    /// does not expose (e.g. global active-torrent queueing) are persisted in
    /// settings for the UI/API but are not enforced here.
    /// </summary>
    public void ApplyTuning(
        int globalMaxConnections,
        bool dhtEnabled,
        bool localPeerDiscoveryEnabled)
    {
        ThrowIfDisposed();

        _settingsGate.Wait();
        try
        {
            var builder = new EngineSettingsBuilder(_engine.Settings)
            {
                MaximumConnections = globalMaxConnections > 0 ? globalMaxConnections : 200,
                AllowLocalPeerDiscovery = localPeerDiscoveryEnabled && !NetworkPolicy.RestrictedDiscovery && !NetworkPolicy.RestartRequired,
                AllowPortForwarding = !NetworkPolicy.RestrictedDiscovery && !NetworkPolicy.RestartRequired,
                DhtEndPoint = dhtEnabled && NetworkPolicy.DhtAllowed && !NetworkPolicy.RestartRequired
                    ? new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0)
                    : null,
            };

            _engine.UpdateSettingsAsync(builder.ToSettings()).GetAwaiter().GetResult();
        }
        catch
        {
            // Tuning is best-effort; never let it take the engine down.
        }
        finally { _settingsGate.Release(); }
    }

    public async Task ForceReannounceAll()
    {
        foreach (string hash in _managersByHash.Keys) RequestReannounce(hash);
        await Task.CompletedTask;
    }

    public async Task SaveResumeData()
    {
        await SaveEngineStateAsync();
    }

    /// <summary>
    /// Persists the full engine state (the list of torrents, their save paths
    /// and state) so they are re-added automatically on the next launch.
    /// </summary>
    public async Task SaveEngineStateAsync(bool checkpointResume = true)
    {
        await _stateSaveGate.WaitAsync();
        try
        {
            if (checkpointResume) await WriteResumeCheckpointsAsync();
            string temporary = _stateFilePath + ".tmp";
            await _engine.SaveStateAsync(temporary);
            File.Move(temporary, _stateFilePath, overwrite: true);
            string pausedPath = _stateFilePath + ".paused.json";
            await File.WriteAllTextAsync(pausedPath + ".tmp", JsonSerializer.Serialize(_pausedHashes.Keys.ToArray()));
            File.Move(pausedPath + ".tmp", pausedPath, overwrite: true);
            string optionsPath = _stateFilePath + ".options.json";
            await File.WriteAllTextAsync(optionsPath + ".tmp", JsonSerializer.Serialize(_options));
            File.Move(optionsPath + ".tmp", optionsPath, overwrite: true);
        }
        catch (Exception ex) { Services.Logger.Instance.Error("Engine", $"Could not save torrent state: {ex.Message}"); }
        finally { _stateSaveGate.Release(); }
    }

    /// <summary>
    /// Resumes restored torrents except those explicitly paused by the user or policy.
    /// </summary>
    public async Task ResumeAllAsync()
    {
        foreach (var mgr in _engine.Torrents)
        {
            if (_pausedHashes.ContainsKey(mgr.InfoHashes.V1OrV2.ToHex())) continue;
            try { await StartManagedAsync(mgr); }
            catch { /* already running / transient — ignore */ }
        }
    }

    public async Task Shutdown()
    {
        if (_disposed) return;
        _shuttingDown = true;
        _discoveryCancellation.Cancel();
        _discoveryQueue.Writer.TryComplete();
        await Task.WhenAll(_discoveryWorkers);
        foreach (var preview in _previews.Values.ToArray()) await preview.DisposeAsync();

        // Save the torrent list first (while torrents are still present) so the
        // next launch restores them.
        await SaveEngineStateAsync();

        foreach (var mgr in _engine.Torrents)
        {
            try
            {
                await StopManagerAsync(mgr);
            }
            catch { /* best effort */ }
        }

        await SaveEngineStateAsync();

        _disposed = true;
        NetworkPolicy.Dispose();
        await Task.Run(() => _engine.Dispose());
    }

    // ───────────────────────────────────────────────────────────
    // IDisposable
    // ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;

        try
        {
            Shutdown().GetAwaiter().GetResult();
        }
        catch { /* swallow during dispose */ }

        _disposed = true;
    }

    // ───────────────────────────────────────────────────────────
    // Private helpers
    // ───────────────────────────────────────────────────────────

    private async Task StopManagerAsync(TorrentManager manager, TimeSpan? trackerTimeout = null)
    {
        // StartingMode finishes asynchronously after StartAsync returns. Let its
        // short resume-data initialization finish before a stop/move/remove.
        var starting = Stopwatch.StartNew();
        while (manager.State == MonoTorrent.Client.TorrentState.Starting)
        {
            if (starting.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Torrent is still starting; retry once initialization finishes.");
            await Task.Delay(20);
        }
        // MonoTorrent 3.0.2 can finish startup during StopAsync and dispose its
        // stopping token. Retry only that specific race, never ignore other failures.
        for (int attempt = 0; ; attempt++)
        {
            try { await manager.StopAsync(trackerTimeout ?? TimeSpan.FromSeconds(10)); return; }
            catch (ObjectDisposedException) when (!_disposed && attempt < 2)
            {
                if (manager.State == MonoTorrent.Client.TorrentState.Stopped) return;
                await Task.Delay(25);
            }
        }
    }

    private string ResolveSavePath(string? savePath)
    {
        var path = StoragePaths.Normalize(string.IsNullOrWhiteSpace(savePath) ? _defaultSavePath : savePath);
        Directory.CreateDirectory(path);
        return path;
    }

    private void InvalidateStats()
    {
        lock (_snapshotLock) _cachedStats = null;
    }

    private TorrentManager? FindManager(string infoHash)
    {
        if (string.IsNullOrEmpty(infoHash))
            return null;

        return _managersByHash.TryGetValue(infoHash, out var manager) ? manager : null;
    }

    private TorrentStats BuildStats(TorrentManager mgr)
    {
        var hash = mgr.InfoHashes.V1OrV2.ToHex();
        _categories.TryGetValue(hash, out var cat);
        _addedDates.TryGetValue(hash, out var added);

        long totalWanted = 0;
        long totalDone = 0;

        if (mgr.Files is not null && mgr.Files.Count > 0 && mgr.Torrent is not null)
        {
            int pieceLength = mgr.Torrent.PieceLength;
            long torrentSize = mgr.Torrent.Size;

            foreach (var file in mgr.Files)
            {
                if (file.Priority == Priority.DoNotDownload)
                    continue;

                totalWanted += file.Length;

                // Compute bytes downloaded for this file using its bitfield.
                // Each true bit in the file's BitField represents a downloaded piece
                // spanning that file's range.
                var bf = file.BitField;
                if (bf.AllTrue)
                {
                    totalDone += file.Length;
                }
                else if (!bf.AllFalse)
                {
                    // Count full pieces that have been downloaded for this file.
                    long piecesDownloaded = bf.TrueCount;
                    long bytesFromPieces = piecesDownloaded * pieceLength;

                    // Cap to the file size since the last piece may be partial.
                    totalDone += Math.Min(bytesFromPieces, file.Length);
                }
            }
        }
        else
        {
            // Metadata not yet available; estimate from overall progress.
            totalWanted = mgr.Torrent?.Size ?? 0;
            totalDone = (long)(totalWanted * (mgr.Progress / 100.0));
        }

        // ETA calculation: bytes remaining / current download rate.
        int eta = -1;
        long dlSpeed = mgr.Monitor.DownloadSpeed;
        if (dlSpeed > 0)
        {
            long remaining = totalWanted - totalDone;
            if (remaining > 0)
                eta = (int)(remaining / dlSpeed);
        }

        // Peer/seed counts from PeerManager (no async call needed).
        int numPeers = mgr.OpenConnections;
        int numSeeds = mgr.Peers.Seeds;

        double ratio = mgr.Monitor.DataBytesUploaded > 0 && mgr.Monitor.DataBytesDownloaded > 0
            ? (double)mgr.Monitor.DataBytesUploaded / mgr.Monitor.DataBytesDownloaded
            : 0.0;

        var options = OptionsFor(hash);
        bool queued = _queuedHashes.ContainsKey(hash);
        var state = queued ? TorrentState.Queued : MapState(mgr.State);
        bool paused = !queued && (mgr.State is MonoTorrent.Client.TorrentState.Paused or MonoTorrent.Client.TorrentState.HashingPaused or MonoTorrent.Client.TorrentState.Stopped);
        string? blocked = NetworkPolicy.RestartRequired ? "Torrent networking blocked: restart the app after network settings changes."
            : !NetworkPolicy.Allowed ? "Torrent networking blocked: the required VPN adapter is unavailable."
            : _transferBlockReason;
        string? waiting = null;
        if (queued) _queueWaitingReasons.TryGetValue(hash, out waiting);

        return new TorrentStats
        {
            Name = mgr.Name ?? hash,
            InfoHash = hash,
            SavePath = mgr.SavePath,
            ContentPath = !mgr.HasMetadata ? mgr.SavePath : mgr.Files.Count == 1 ? mgr.Files[0].DownloadCompleteFullPath : mgr.ContainingDirectory,
            HasMetadata = mgr.HasMetadata,
            Progress = (float)(mgr.PartialProgress / 100.0),
            State = state,
            Paused = paused,
            DownloadRate = mgr.Monitor.DownloadSpeed,
            UploadRate = mgr.Monitor.UploadSpeed,
            TotalWanted = totalWanted,
            TotalDone = totalDone,
            TotalDownload = mgr.Monitor.DataBytesDownloaded,
            TotalUpload = mgr.Monitor.DataBytesUploaded,
            Ratio = ratio,
            NumPeers = numPeers,
            NumSeeds = numSeeds,
            EtaSeconds = eta,
            AddedDate = added == default ? DateTime.UtcNow : added,
            Category = cat,
            QueuePosition = options.Position,
            ForceStart = options.ForceStart,
            StatusReason = Desktop.TransferStatus.Describe(state, _pausedHashes.ContainsKey(hash) || paused,
                totalWanted, totalDone, dlSpeed, numPeers, mgr.Settings.MaximumConnections,
                _engine.ConnectionManager.OpenConnections, _engine.Settings.MaximumConnections,
                blocked, waiting, _removalPending.ContainsKey(hash), DiscoveryProblem(mgr))
        };
    }

    private static TorrentState MapState(MonoTorrent.Client.TorrentState mtState)
    {
        return mtState switch
        {
            MonoTorrent.Client.TorrentState.Stopped => TorrentState.Paused,
            MonoTorrent.Client.TorrentState.Paused => TorrentState.Paused,
            MonoTorrent.Client.TorrentState.Starting => TorrentState.CheckingResume,
            MonoTorrent.Client.TorrentState.Downloading => TorrentState.Downloading,
            MonoTorrent.Client.TorrentState.Seeding => TorrentState.Seeding,
            MonoTorrent.Client.TorrentState.Hashing => TorrentState.CheckingFiles,
            MonoTorrent.Client.TorrentState.HashingPaused => TorrentState.CheckingFiles,
            MonoTorrent.Client.TorrentState.Metadata => TorrentState.DownloadingMetadata,
            MonoTorrent.Client.TorrentState.Stopping => TorrentState.Paused,
            MonoTorrent.Client.TorrentState.Error => TorrentState.Error,
            MonoTorrent.Client.TorrentState.FetchingHashes => TorrentState.CheckingFiles,
            _ => TorrentState.Unknown
        };
    }

    private static int MapTrackerStatus(TrackerState status)
    {
        return status switch
        {
            TrackerState.Unknown => 0,
            TrackerState.Ok => 1,
            TrackerState.Connecting => 2,
            TrackerState.InvalidResponse => 3,
            TrackerState.Offline => 4,
            _ => 0
        };
    }

    private static int MapPriorityToInt(Priority p)
    {
        return p switch
        {
            Priority.DoNotDownload => 0,
            Priority.Lowest => 1,
            Priority.Low => 2,
            Priority.Normal => 3,
            Priority.High => 5,
            Priority.Highest => 6,
            Priority.Immediate => 7,
            _ => 3
        };
    }

    private static string BuildPeerFlags(PeerId peer)
    {
        var flags = new System.Text.StringBuilder(8);

        if (peer.IsSeeder) flags.Append('S');
        if (peer.AmChoking) flags.Append('c');
        if (peer.AmInterested) flags.Append('i');
        if (peer.IsChoking) flags.Append('C');
        if (peer.IsInterested) flags.Append('I');
        if (peer.SupportsFastPeer) flags.Append('F');
        if (peer.SupportsLTMessages) flags.Append('L');

        return flags.ToString();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
