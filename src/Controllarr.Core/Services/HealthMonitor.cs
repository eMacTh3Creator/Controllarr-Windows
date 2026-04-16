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
        public bool ReannounceAttempted { get; set; }

        public ProgressTracker(float progress)
        {
            LastProgress = progress;
            LastChangeTime = DateTime.UtcNow;
            ReannounceAttempted = false;
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

        public HealthMonitor(Logger? logger = null)
        {
            _logger = logger ?? Logger.Instance;
        }

        /// <summary>
        /// Evaluate all torrents and update the issues map.
        /// Call on each tick of the main loop.
        /// </summary>
        public void Tick(IReadOnlyList<TorrentView> torrents, Settings settings)
        {
            lock (_lock)
            {
                var activeHashes = new HashSet<string>();

                foreach (var t in torrents)
                {
                    // Only monitor downloading torrents
                    if (t.State != TorrentState.Downloading)
                        continue;

                    activeHashes.Add(t.InfoHash);
                    EvaluateTorrent(t, settings);
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
                    tracker.LastChangeTime = DateTime.UtcNow;
                    tracker.ReannounceAttempted = false;
                }
            }
        }

        // ── Internals ───────────────────────────────────────────────

        private void EvaluateTorrent(TorrentView t, Settings settings)
        {
            float progress = t.Progress;

            if (!_progressMap.TryGetValue(t.InfoHash, out var tracker))
            {
                tracker = new ProgressTracker(progress);
                _progressMap[t.InfoHash] = tracker;
                return; // first observation – need a baseline
            }

            // Progress changed → update tracker and clear any existing issue
            if (Math.Abs(progress - tracker.LastProgress) > 0.0001f)
            {
                tracker.LastProgress = progress;
                tracker.LastChangeTime = DateTime.UtcNow;
                tracker.ReannounceAttempted = false;
                _issues.Remove(t.InfoHash);
                return;
            }

            // Check if stall threshold reached
            double minutesStalled = (DateTime.UtcNow - tracker.LastChangeTime).TotalMinutes;
            int stallMinutes = settings.HealthStallMinutes > 0 ? settings.HealthStallMinutes : 30;

            if (minutesStalled < stallMinutes)
                return;

            // Classify the stall reason
            HealthReason reason = ClassifyReason(t);

            if (_issues.TryGetValue(t.InfoHash, out var existing))
            {
                existing.Reason = reason;
                existing.LastProgress = progress;
                existing.LastUpdated = DateTime.UtcNow;
            }
            else
            {
                var issue = new HealthIssue(t.InfoHash, t.Name, reason, progress);
                _issues[t.InfoHash] = issue;
                _logger.Warn("HealthMonitor",
                    $"Torrent stalled: {t.Name} [{t.InfoHash[..8]}...] reason={reason}");

                // Auto-reannounce on first stall if configured
                if (settings.HealthReannounceOnStall && !tracker.ReannounceAttempted)
                {
                    tracker.ReannounceAttempted = true;
                    t.RequestReannounce();
                    _logger.Info("HealthMonitor",
                        $"Reannounce requested for: {t.Name}");
                }
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
