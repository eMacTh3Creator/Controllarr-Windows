using System;
using System.Collections.Generic;
using System.Linq;

using Controllarr.Core.Engine;
using Controllarr.Core.Persistence;

namespace Controllarr.Core.Services
{
    // ────────────────────────────────────────────────────────────────
    // Health classification
    // ────────────────────────────────────────────────────────────────

    public enum HealthReason
    {
        MetadataTimeout,
        NoPeers,
        StalledWithPeers,
        AwaitingRecheck
    }

    // ────────────────────────────────────────────────────────────────
    // A single health issue for a torrent
    // ────────────────────────────────────────────────────────────────

    public sealed class HealthIssue
    {
        public string InfoHash { get; set; }
        public string Name { get; set; }
        public HealthReason Reason { get; set; }
        public DateTime FirstSeen { get; set; }
        public float LastProgress { get; set; }
        public DateTime LastUpdated { get; set; }

        public HealthIssue(string infoHash, string name, HealthReason reason, float lastProgress)
        {
            InfoHash = infoHash;
            Name = name;
            Reason = reason;
            FirstSeen = DateTime.UtcNow;
            LastProgress = lastProgress;
            LastUpdated = DateTime.UtcNow;
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Internal tracker for per-hash progress history
    // ────────────────────────────────────────────────────────────────

    internal sealed class ProgressTracker
    {
        public float LastProgress { get; set; }
        public DateTime LastChangeTime { get; set; }
        public long DownloadedBytes { get; set; }
        public bool HasMetadata { get; set; }
        public DateTime NextRefresh { get; set; }
        public int RefreshAttempts { get; set; }

        public ProgressTracker(TorrentView torrent, DateTime now)
        {
            LastProgress = torrent.Progress;
            DownloadedBytes = torrent.DownloadedBytes;
            HasMetadata = torrent.HasMetadata;
            LastChangeTime = now;
            NextRefresh = now.AddMinutes(1);
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Health monitor – evaluates downloading torrents for stalls
    // ────────────────────────────────────────────────────────────────

    public sealed class HealthMonitor
    {
        private readonly Dictionary<string, ProgressTracker> _progressMap = new();
        private readonly Dictionary<string, HealthIssue> _issues = new();
        private readonly object _lock = new();
        private readonly Logger _logger;
        private readonly TimeProvider _time;

        public HealthMonitor(Logger? logger = null, TimeProvider? timeProvider = null)
        {
            _logger = logger ?? Logger.Instance;
            _time = timeProvider ?? TimeProvider.System;
        }

        /// <summary>
        /// Evaluate all torrents and update the issues map.
        /// Call on each tick of the main loop.
        /// </summary>
        public void Tick(IReadOnlyList<TorrentView> torrents, Settings settings)
        {
            var refresh = new List<TorrentView>();
            DateTime now = _time.GetUtcNow().UtcDateTime;
            lock (_lock)
            {
                var activeHashes = new HashSet<string>();

                foreach (var t in torrents)
                {
                    if (t.State is not (TorrentState.Downloading or TorrentState.DownloadingMetadata))
                        continue;

                    activeHashes.Add(t.InfoHash);
                    EvaluateTorrent(t, settings, now, refresh);
                }

                // Purge trackers & issues for torrents no longer downloading
                var staleHashes = new List<string>();
                foreach (var hash in _progressMap.Keys)
                {
                    if (!activeHashes.Contains(hash))
                        staleHashes.Add(hash);
                }
                foreach (var hash in staleHashes)
                {
                    _progressMap.Remove(hash);
                    _issues.Remove(hash);
                }
            }
            // Engine callbacks enqueue bounded work. Never invoke external code
            // under the monitor lock (API snapshots must remain responsive).
            foreach (var torrent in refresh)
            {
                try { torrent.RequestReannounce(); }
                catch (Exception ex) { _logger.Warn("HealthMonitor", $"Discovery refresh could not be queued: {ex.GetType().Name}"); }
            }
        }

        /// <summary>Returns a snapshot of all current health issues.</summary>
        public List<HealthIssue> Snapshot()
        {
            lock (_lock)
            {
                return _issues.Values.ToList();
            }
        }

        /// <summary>Manually clear an issue for a given hash.</summary>
        public void ClearIssue(string infoHash)
        {
            lock (_lock)
            {
                _issues.Remove(infoHash);
                if (_progressMap.TryGetValue(infoHash, out var tracker))
                {
                    // Reset the timer so it doesn't immediately re-trigger
                    tracker.LastChangeTime = _time.GetUtcNow().UtcDateTime;
                    tracker.NextRefresh = tracker.LastChangeTime.AddMinutes(1);
                    tracker.RefreshAttempts = 0;
                }
            }
        }

        // ── Internals ───────────────────────────────────────────────

        private void EvaluateTorrent(TorrentView t, Settings settings, DateTime now, List<TorrentView> refresh)
        {
            float progress = t.Progress;

            if (!_progressMap.TryGetValue(t.InfoHash, out var tracker))
            {
                tracker = new ProgressTracker(t, now);
                _progressMap[t.InfoHash] = tracker;
                return; // first observation – need a baseline
            }

            // Progress changed → update tracker and clear any existing issue
            if (progress != tracker.LastProgress || t.DownloadedBytes != tracker.DownloadedBytes ||
                t.HasMetadata != tracker.HasMetadata || t.DownloadRateBytes > 0)
            {
                tracker.LastProgress = progress;
                tracker.DownloadedBytes = t.DownloadedBytes;
                tracker.HasMetadata = t.HasMetadata;
                tracker.LastChangeTime = now;
                tracker.NextRefresh = now.AddMinutes(1);
                tracker.RefreshAttempts = 0;
                _issues.Remove(t.InfoHash);
                return;
            }

            // Check if stall threshold reached
            double minutesStalled = (now - tracker.LastChangeTime).TotalMinutes;
            int stallMinutes = settings.HealthStallMinutes > 0 ? settings.HealthStallMinutes : 30;

            // Rediscover missing peers before the long health/escalation timeout.
            // Do not restart managers, evict healthy peers or add arbitrary trackers.
            if (settings.HealthReannounceOnStall && now >= tracker.NextRefresh &&
                (!t.HasMetadata || t.NumPeers == 0 || minutesStalled >= stallMinutes))
            {
                tracker.RefreshAttempts++;
                tracker.NextRefresh = now.AddMinutes(Math.Min(15, Math.Pow(2, Math.Min(tracker.RefreshAttempts, 4))));
                refresh.Add(t);
            }

            if (minutesStalled < stallMinutes)
                return;

            // Classify the stall reason
            HealthReason reason = ClassifyReason(t);

            if (_issues.TryGetValue(t.InfoHash, out var existing))
            {
                existing.Reason = reason;
                existing.LastProgress = progress;
                existing.LastUpdated = now;
            }
            else
            {
                var issue = new HealthIssue(t.InfoHash, t.Name, reason, progress) { FirstSeen = now, LastUpdated = now };
                _issues[t.InfoHash] = issue;
                _logger.Warn("HealthMonitor",
                    $"Torrent stalled: {t.Name} [{t.InfoHash[..Math.Min(8, t.InfoHash.Length)]}...] reason={reason}");
            }
        }

        private static HealthReason ClassifyReason(TorrentView t)
        {
            if (t.HasMetadata == false)
                return HealthReason.MetadataTimeout;

            if (t.Progress >= 0.999f)
                return HealthReason.AwaitingRecheck;

            if (t.NumPeers == 0)
                return HealthReason.NoPeers;

            return HealthReason.StalledWithPeers;
        }
    }
}
