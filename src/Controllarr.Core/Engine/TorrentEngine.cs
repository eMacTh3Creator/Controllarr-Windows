using System.Collections.Concurrent;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Trackers;

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
    Paused = 7
}

/// <summary>
/// Snapshot of an individual torrent's statistics.
/// </summary>
public sealed class TorrentStats
{
    public string Name { get; init; } = string.Empty;
    public string InfoHash { get; init; } = string.Empty;
    public string SavePath { get; init; } = string.Empty;
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
public sealed class TorrentEngine : IDisposable
{
    // ── Core engine ────────────────────────────────────────────
    private readonly ClientEngine _engine;
    private readonly object _engineLock = new();

    // ── Configuration ──────────────────────────────────────────
    private string _defaultSavePath;
    private readonly string _resumeDataDirectory;
    private ushort _listenPort;

    // ── Per-torrent metadata ───────────────────────────────────
    private readonly ConcurrentDictionary<string, string> _categories = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _addedDates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, HashSet<string>> _blockedExtensions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _filteredHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _filteredLock = new();

    // ── Lifetime ───────────────────────────────────────────────
    private bool _disposed;

    // ───────────────────────────────────────────────────────────
    // Constructor
    // ───────────────────────────────────────────────────────────

    public TorrentEngine(string defaultSavePath, string resumeDataDirectory, ushort listenPort)
    {
        _defaultSavePath = defaultSavePath ?? throw new ArgumentNullException(nameof(defaultSavePath));
        _resumeDataDirectory = resumeDataDirectory ?? throw new ArgumentNullException(nameof(resumeDataDirectory));
        _listenPort = listenPort;

        Directory.CreateDirectory(_defaultSavePath);
        Directory.CreateDirectory(_resumeDataDirectory);

        var settings = new EngineSettingsBuilder
        {
            CacheDirectory = _resumeDataDirectory,
            ListenEndPoints = new Dictionary<string, System.Net.IPEndPoint>
            {
                ["ipv4"] = new System.Net.IPEndPoint(System.Net.IPAddress.Any, _listenPort)
            },
            AllowPortForwarding = true,
            AutoSaveLoadFastResume = true,
        }.ToSettings();

        _engine = new ClientEngine(settings);
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
    public async Task<string> AddMagnet(string uri, string? category = null, string? savePath = null)
    {
        ThrowIfDisposed();

        var magnet = MagnetLink.Parse(uri);
        var save = ResolveSavePath(savePath);

        TorrentManager manager;
        lock (_engineLock)
        {
            manager = _engine.AddAsync(magnet, save).GetAwaiter().GetResult();
        }

        var hash = manager.InfoHashes.V1OrV2.ToHex();

        if (!string.IsNullOrEmpty(category))
            _categories[hash] = category;

        _addedDates[hash] = DateTime.UtcNow;

        await manager.StartAsync();
        return hash;
    }

    /// <summary>
    /// Adds a torrent from a .torrent file on disk and starts downloading.
    /// </summary>
    /// <returns>The hex info-hash of the added torrent.</returns>
    public async Task<string> AddTorrentFile(string filePath, string? category = null, string? savePath = null)
    {
        ThrowIfDisposed();

        if (!File.Exists(filePath))
            throw new System.IO.FileNotFoundException("Torrent file not found.", filePath);

        var torrent = await Torrent.LoadAsync(filePath);
        var save = ResolveSavePath(savePath);

        TorrentManager manager;
        lock (_engineLock)
        {
            manager = _engine.AddAsync(torrent, save).GetAwaiter().GetResult();
        }

        var hash = manager.InfoHashes.V1OrV2.ToHex();

        if (!string.IsNullOrEmpty(category))
            _categories[hash] = category;

        _addedDates[hash] = DateTime.UtcNow;

        await manager.StartAsync();
        return hash;
    }

    // ───────────────────────────────────────────────────────────
    // Torrent control
    // ───────────────────────────────────────────────────────────

    public async Task<bool> Pause(string infoHash)
    {
        var mgr = FindManager(infoHash);
        if (mgr is null) return false;

        try
        {
            await mgr.PauseAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> Resume(string infoHash)
    {
        var mgr = FindManager(infoHash);
        if (mgr is null) return false;

        try
        {
            await mgr.StartAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> Remove(string infoHash, bool deleteFiles)
    {
        var mgr = FindManager(infoHash);
        if (mgr is null) return false;

        try
        {
            await mgr.StopAsync();
            await _engine.RemoveAsync(mgr,
                deleteFiles ? RemoveMode.CacheDataAndDownloadedData : RemoveMode.CacheDataOnly);

            _categories.TryRemove(infoHash, out _);
            _addedDates.TryRemove(infoHash, out _);

            lock (_filteredLock)
                _filteredHashes.Remove(infoHash);

            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> Move(string infoHash, string newPath)
    {
        var mgr = FindManager(infoHash);
        if (mgr is null) return false;

        try
        {
            Directory.CreateDirectory(newPath);
            await mgr.MoveFilesAsync(newPath, true);
            return true;
        }
        catch
        {
            return false;
        }
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
        if (extensions is null || extensions.Length == 0) return;

        var normalized = new HashSet<string>(
            extensions.Select(e => e.StartsWith('.') ? e : "." + e),
            StringComparer.OrdinalIgnoreCase);

        _blockedExtensions.AddOrUpdate(category, normalized,
            (_, existing) =>
            {
                foreach (var ext in normalized)
                    existing.Add(ext);
                return existing;
            });
    }

    /// <summary>
    /// Scans all managed torrents and sets the priority to <c>DoNotDownload</c>
    /// for files whose extension is in the blocked list for that torrent's category.
    /// Each torrent is only processed once unless its info-hash is removed and re-added.
    /// </summary>
    public void ApplyPendingFileFilters()
    {
        foreach (var mgr in _engine.Torrents)
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

    public async Task<bool> Reannounce(string infoHash)
    {
        var mgr = FindManager(infoHash);
        if (mgr is null) return false;

        try
        {
            await mgr.DhtAnnounceAsync();
            // Also announce to all trackers.
            await mgr.TrackerManager.AnnounceAsync(CancellationToken.None);
            return true;
        }
        catch
        {
            return false;
        }
    }

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
                    Status = MapTrackerStatus(tracker.Status)
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
        return _engine.Torrents.Select(BuildStats).ToArray();
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

        foreach (var mgr in _engine.Torrents)
        {
            dlTotal += mgr.Monitor.DataBytesDownloaded;
            ulTotal += mgr.Monitor.DataBytesUploaded;
            peers += mgr.OpenConnections;
        }

        return new SessionStats
        {
            DownloadRate = _engine.TotalDownloadRate,
            UploadRate = _engine.TotalUploadRate,
            TotalDownloaded = dlTotal,
            TotalUploaded = ulTotal,
            NumTorrents = _engine.Torrents.Count,
            NumPeersConnected = peers,
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

        if (port == _listenPort) return;

        _listenPort = port;

        var newSettings = new EngineSettingsBuilder(_engine.Settings)
        {
            ListenEndPoints = new Dictionary<string, System.Net.IPEndPoint>
            {
                ["ipv4"] = new System.Net.IPEndPoint(System.Net.IPAddress.Any, port)
            }
        }.ToSettings();

        await _engine.UpdateSettingsAsync(newSettings);
    }

    public void SetRateLimits(int? downloadKBps, int? uploadKBps)
    {
        ThrowIfDisposed();

        var builder = new EngineSettingsBuilder(_engine.Settings);

        // 0 means unlimited in MonoTorrent.
        builder.MaximumDownloadRate = downloadKBps.HasValue ? downloadKBps.Value * 1024 : 0;
        builder.MaximumUploadRate = uploadKBps.HasValue ? uploadKBps.Value * 1024 : 0;

        _engine.UpdateSettingsAsync(builder.ToSettings()).GetAwaiter().GetResult();
    }

    public async Task ForceReannounceAll()
    {
        foreach (var mgr in _engine.Torrents)
        {
            try
            {
                await mgr.DhtAnnounceAsync();
                await mgr.TrackerManager.AnnounceAsync(CancellationToken.None);
            }
            catch { /* best effort */ }
        }
    }

    public async Task SaveResumeData()
    {
        // MonoTorrent 3.x auto-saves fast-resume when AutoSaveLoadFastResume is true.
        // We trigger an explicit save for robustness.
        foreach (var mgr in _engine.Torrents)
        {
            try
            {
                await mgr.SaveFastResumeAsync();
            }
            catch { /* best effort */ }
        }
    }

    public async Task Shutdown()
    {
        if (_disposed) return;

        foreach (var mgr in _engine.Torrents)
        {
            try
            {
                await mgr.StopAsync();
                await mgr.SaveFastResumeAsync();
            }
            catch { /* best effort */ }
        }

        _disposed = true;
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

    private string ResolveSavePath(string? savePath)
    {
        var path = string.IsNullOrWhiteSpace(savePath) ? _defaultSavePath : savePath;
        Directory.CreateDirectory(path);
        return path;
    }

    private TorrentManager? FindManager(string infoHash)
    {
        if (string.IsNullOrEmpty(infoHash))
            return null;

        return _engine.Torrents.FirstOrDefault(m =>
            string.Equals(m.InfoHashes.V1OrV2.ToHex(), infoHash, StringComparison.OrdinalIgnoreCase));
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

        return new TorrentStats
        {
            Name = mgr.Name ?? hash,
            InfoHash = hash,
            SavePath = mgr.SavePath,
            Progress = (float)(mgr.Progress / 100.0),
            State = MapState(mgr.State),
            Paused = mgr.State == MonoTorrent.Client.TorrentState.Paused
                     || mgr.State == MonoTorrent.Client.TorrentState.Stopped,
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
            Category = cat
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
            MonoTorrent.Client.TorrentState.Error => TorrentState.Unknown,
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
