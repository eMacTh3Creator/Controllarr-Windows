using System;
using System.Collections.Generic;
using System.Linq;

using Controllarr.Core.Engine;
using Controllarr.Core.Persistence;

namespace Controllarr.Core.Services
{
    // ────────────────────────────────────────────────────────────────
    // Seeding enforcement record
    // ────────────────────────────────────────────────────────────────

    public sealed class SeedEnforcement
    {
        public string InfoHash { get; set; }
        public string Name { get; set; }
        public string Reason { get; set; }
        public SeedLimitAction Action { get; set; }
        public DateTime Timestamp { get; set; }

        public SeedEnforcement(string infoHash, string name, string reason, SeedLimitAction action)
        {
            InfoHash = infoHash;
            Name = name;
            Reason = reason;
            Action = action;
            Timestamp = DateTime.UtcNow;
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Seeding policy – enforces ratio and time limits
    // ────────────────────────────────────────────────────────────────

    public sealed class SeedingPolicy
    {
        private readonly List<SeedEnforcement> _log = new();
        private readonly HashSet<string> _enforced = new();
        private readonly object _lock = new();
        private readonly Logger _logger;

        public SeedingPolicy(Logger? logger = null)
        {
            _logger = logger ?? Logger.Instance;
        }

        /// <summary>
        /// Evaluate all seeding torrents and enforce ratio/time limits.
        /// </summary>
        public void Tick(IReadOnlyList<TorrentView> torrents,
                         Settings settings,
                         IReadOnlyList<Category> categories,
                         ITorrentEngine engine)
        {
            lock (_lock)
            {
                foreach (var t in torrents)
                {
                    // Only evaluate seeding torrents
                    if (t.State != TorrentState.Seeding)
                        continue;

                    // Skip if already enforced
                    if (_enforced.Contains(t.InfoHash))
                        continue;

                    var category = FindCategory(t.Category, categories);
                    EvaluateTorrent(t, settings, category, engine);
                }
            }
        }

        /// <summary>Returns a snapshot of the enforcement log.</summary>
        public List<SeedEnforcement> Snapshot()
        {
            lock (_lock)
            {
                return _log.ToList();
            }
        }

        // ── Internals ───────────────────────────────────────────────

        private void EvaluateTorrent(TorrentView t,
                                     Settings settings,
                                     Category? category,
                                     ITorrentEngine engine)
        {
            // Determine effective limits (category overrides global)
            double? effectiveMaxRatio = category?.MaxRatio ?? settings.GlobalMaxRatio;
            int? effectiveMaxSeedMinutes = category?.MaxSeedingTimeMinutes ?? settings.GlobalMaxSeedingTimeMinutes;
            int minimumSeedMinutes = settings.MinimumSeedTimeMinutes;
            SeedLimitAction defaultAction = settings.SeedLimitAction;

            // Calculate elapsed seeding time
            double seedingMinutes = t.SeedingTimeSeconds / 60.0;

            // ── Hit-and-run protection ──────────────────────────────
            // If the torrent hasn't seeded for the minimum required time,
            // do NOT apply ratio-based removal — force a pause instead.
            if (seedingMinutes < minimumSeedMinutes)
            {
                // Check if ratio is met but minimum time is not
                bool ratioMet = effectiveMaxRatio.HasValue && t.Ratio >= effectiveMaxRatio.Value;
                bool timeMet = effectiveMaxSeedMinutes.HasValue && seedingMinutes >= effectiveMaxSeedMinutes.Value;

                if (ratioMet || timeMet)
                {
                    // Force pause to prevent hit-and-run
                    Apply(t, SeedLimitAction.Pause,
                          $"Hit-and-run protection: seeded {seedingMinutes:F0}m < minimum {minimumSeedMinutes}m",
                          engine);
                    return;
                }

                // Otherwise, neither limit is met yet – nothing to do
                return;
            }

            // ── Ratio limit ────────────────────────────────────────
            if (effectiveMaxRatio.HasValue && t.Ratio >= effectiveMaxRatio.Value)
            {
                Apply(t, defaultAction,
                      $"Ratio {t.Ratio:F2} >= limit {effectiveMaxRatio.Value:F2}",
                      engine);
                return;
            }

            // ── Time limit ─────────────────────────────────────────
            if (effectiveMaxSeedMinutes.HasValue && seedingMinutes >= effectiveMaxSeedMinutes.Value)
            {
                Apply(t, defaultAction,
                      $"Seeding time {seedingMinutes:F0}m >= limit {effectiveMaxSeedMinutes.Value}m",
                      engine);
            }
        }

        private void Apply(TorrentView t, SeedLimitAction action, string reason, ITorrentEngine engine)
        {
            var enforcement = new SeedEnforcement(t.InfoHash, t.Name, reason, action);
            _log.Add(enforcement);
            _enforced.Add(t.InfoHash);

            _logger.Info("SeedingPolicy",
                $"Enforcing {action} on {t.Name}: {reason}");

            switch (action)
            {
                case SeedLimitAction.Pause:
                    engine.PauseTorrent(t.InfoHash);
                    break;

                case SeedLimitAction.RemoveKeepFiles:
                    engine.RemoveTorrent(t.InfoHash, deleteFiles: false);
                    break;

                case SeedLimitAction.RemoveDeleteFiles:
                    engine.RemoveTorrent(t.InfoHash, deleteFiles: true);
                    break;
            }
        }

        private static Category? FindCategory(string? categoryName,
                                               IReadOnlyList<Category> categories)
        {
            if (string.IsNullOrEmpty(categoryName))
                return null;

            return categories.FirstOrDefault(
                c => string.Equals(c.Name, categoryName, StringComparison.OrdinalIgnoreCase));
        }
    }
}
