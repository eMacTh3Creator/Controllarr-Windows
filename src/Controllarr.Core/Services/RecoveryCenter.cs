using System;
using System.Collections.Generic;
using System.Linq;

using Controllarr.Core.Engine;
using Controllarr.Core.Persistence;

namespace Controllarr.Core.Services
{
    // ────────────────────────────────────────────────────────────────
    // Recovery source – whether the action was automatic or manual
    // ────────────────────────────────────────────────────────────────

    public enum RecoverySource
    {
        Automatic,
        Manual
    }

    // ────────────────────────────────────────────────────────────────
    // Recovery record – one logged recovery action
    // ────────────────────────────────────────────────────────────────

    public sealed class RecoveryRecord
    {
        public string InfoHash { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public RecoveryTrigger Reason { get; set; }
        public RecoveryAction Action { get; set; }
        public RecoverySource Source { get; set; }
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        public RecoveryRecord() { }
    }

    // ────────────────────────────────────────────────────────────────
    // Internal: tracks which (trigger|action|delay) combos have been
    // applied to each hash so we don't re-trigger the same rule
    // ────────────────────────────────────────────────────────────────

    internal sealed class IssueContext
    {
        public string InfoHash { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public RecoveryTrigger Trigger { get; set; }
        public DateTime FirstSeen { get; set; }
    }

    // ────────────────────────────────────────────────────────────────
    // Recovery center – rule engine that watches monitors and applies
    // configured recovery actions with delay-based escalation
    // ────────────────────────────────────────────────────────────────

    public sealed class RecoveryCenter
    {
        private readonly List<RecoveryRecord> _log = new();
        private readonly object _lock = new();
        private readonly Logger _logger;

        /// <summary>
        /// Tracks which rule signatures have been applied to each hash.
        /// Key: infoHash, Value: set of "trigger|action|delay" signatures.
        /// </summary>
        private readonly Dictionary<string, HashSet<string>> _appliedSignatures = new();

        // ────────────────────────────────────────────────────────────
        // Constructor
        // ────────────────────────────────────────────────────────────

        public RecoveryCenter(Logger? logger = null)
        {
            _logger = logger ?? Logger.Instance;
        }

        // ────────────────────────────────────────────────────────────
        // Tick – called each main-loop iteration
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Gathers issues from HealthMonitor, PostProcessor, and DiskSpaceMonitor,
        /// then plans and executes recovery actions based on enabled rules.
        /// </summary>
        public void Tick(HealthMonitor healthMonitor,
                         PostProcessor postProcessor,
                         DiskSpaceMonitor diskSpaceMonitor,
                         Settings settings,
                         ITorrentEngine engine)
        {
            if (settings.RecoveryRules.Count == 0)
                return;

            var enabledRules = settings.RecoveryRules.Where(r => r.Enabled).ToList();
            if (enabledRules.Count == 0)
                return;

            // ── Gather all current issues ──────────────────────────

            var issues = new List<IssueContext>();

            // 1) Health monitor issues → map HealthReason to RecoveryTrigger
            if (healthMonitor is not null)
            {
                foreach (var hi in healthMonitor.Snapshot())
                {
                    var trigger = MapHealthReason(hi.Reason);
                    issues.Add(new IssueContext
                    {
                        InfoHash = hi.InfoHash,
                        Name = hi.Name,
                        Trigger = trigger,
                        FirstSeen = hi.FirstSeen
                    });
                }
            }

            // 2) PostProcessor failed records → PostProcessMoveFailed / ExtractionFailed
            if (postProcessor is not null)
            {
                foreach (var pr in postProcessor.Snapshot())
                {
                    if (pr.Stage != PostStage.Failed)
                        continue;

                    RecoveryTrigger trigger;
                    if (pr.Message.StartsWith("Extraction", StringComparison.OrdinalIgnoreCase))
                        trigger = RecoveryTrigger.PostProcessExtractionFailed;
                    else
                        trigger = RecoveryTrigger.PostProcessMoveFailed;

                    issues.Add(new IssueContext
                    {
                        InfoHash = pr.InfoHash,
                        Name = pr.Name,
                        Trigger = trigger,
                        FirstSeen = pr.LastUpdated
                    });
                }
            }

            // 3) DiskSpaceMonitor paused hashes → DiskPressure
            if (diskSpaceMonitor is not null)
            {
                var diskStatus = diskSpaceMonitor.Snapshot();
                if (diskStatus.IsPaused)
                {
                    foreach (var hash in diskStatus.PausedHashes)
                    {
                        issues.Add(new IssueContext
                        {
                            InfoHash = hash,
                            Name = $"[disk-paused:{hash[..Math.Min(8, hash.Length)]}]",
                            Trigger = RecoveryTrigger.DiskPressure,
                            FirstSeen = DateTime.UtcNow // Disk pausing is ephemeral; use now
                        });
                    }
                }
            }

            // ── Plan and execute actions ───────────────────────────

            foreach (var issue in issues)
            {
                foreach (var rule in enabledRules)
                {
                    if (rule.Trigger != issue.Trigger)
                        continue;

                    // Check if the delay has elapsed
                    double minutesElapsed = (DateTime.UtcNow - issue.FirstSeen).TotalMinutes;
                    if (minutesElapsed < rule.DelayMinutes)
                        continue;

                    // Build signature to prevent re-triggering
                    string signature = $"{rule.Trigger}|{rule.Action}|{rule.DelayMinutes}";

                    lock (_lock)
                    {
                        if (!_appliedSignatures.TryGetValue(issue.InfoHash, out var sigs))
                        {
                            sigs = new HashSet<string>(StringComparer.Ordinal);
                            _appliedSignatures[issue.InfoHash] = sigs;
                        }

                        if (sigs.Contains(signature))
                            continue;

                        sigs.Add(signature);
                    }

                    // Execute the recovery action
                    var record = ExecuteAction(
                        issue.InfoHash,
                        issue.Name,
                        issue.Trigger,
                        rule.Action,
                        RecoverySource.Automatic,
                        engine,
                        postProcessor);

                    lock (_lock)
                    {
                        _log.Add(record);
                    }
                }
            }

            // ── Clean up signatures for hashes no longer in issues ─

            var activeHashes = new HashSet<string>(issues.Select(i => i.InfoHash));

            lock (_lock)
            {
                var staleHashes = _appliedSignatures.Keys
                    .Where(h => !activeHashes.Contains(h))
                    .ToList();

                foreach (var hash in staleHashes)
                    _appliedSignatures.Remove(hash);
            }
        }

        // ────────────────────────────────────────────────────────────
        // Manual recovery
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Manually triggers a recovery action for a specific torrent.
        /// If <paramref name="overrideAction"/> is null, defaults to Reannounce.
        /// </summary>
        public RecoveryRecord RunRecovery(string hash,
                                          RecoveryAction? overrideAction,
                                          ITorrentEngine engine,
                                          PostProcessor? postProcessor = null)
        {
            var action = overrideAction ?? RecoveryAction.Reannounce;

            var record = ExecuteAction(
                hash,
                $"[manual:{hash[..Math.Min(8, hash.Length)]}]",
                RecoveryTrigger.StalledWithPeers, // Default trigger for manual
                action,
                RecoverySource.Manual,
                engine,
                postProcessor);

            lock (_lock)
            {
                _log.Add(record);
            }

            return record;
        }

        // ────────────────────────────────────────────────────────────
        // Snapshot
        // ────────────────────────────────────────────────────────────

        /// <summary>Returns a snapshot of the recovery log.</summary>
        public List<RecoveryRecord> Snapshot()
        {
            lock (_lock)
            {
                return _log.ToList();
            }
        }

        // ────────────────────────────────────────────────────────────
        // Action execution
        // ────────────────────────────────────────────────────────────

        private RecoveryRecord ExecuteAction(string infoHash,
                                              string name,
                                              RecoveryTrigger trigger,
                                              RecoveryAction action,
                                              RecoverySource source,
                                              ITorrentEngine engine,
                                              PostProcessor? postProcessor)
        {
            var record = new RecoveryRecord
            {
                InfoHash = infoHash,
                Name = name,
                Reason = trigger,
                Action = action,
                Source = source,
                Timestamp = DateTime.UtcNow
            };

            try
            {
                switch (action)
                {
                    case RecoveryAction.Reannounce:
                        engine.Reannounce(infoHash);
                        record.Success = true;
                        record.Message = "Reannounce sent to trackers";
                        break;

                    case RecoveryAction.Pause:
                        engine.PauseTorrent(infoHash);
                        record.Success = true;
                        record.Message = "Torrent paused";
                        break;

                    case RecoveryAction.RemoveKeepFiles:
                        engine.RemoveTorrent(infoHash, deleteFiles: false);
                        record.Success = true;
                        record.Message = "Torrent removed, files kept";
                        break;

                    case RecoveryAction.RemoveDeleteFiles:
                        engine.RemoveTorrent(infoHash, deleteFiles: true);
                        record.Success = true;
                        record.Message = "Torrent removed with files";
                        break;

                    case RecoveryAction.RetryPostProcess:
                        if (postProcessor is not null && postProcessor.Retry(infoHash))
                        {
                            record.Success = true;
                            record.Message = "Post-processing retry queued";
                        }
                        else
                        {
                            record.Success = false;
                            record.Message = "No failed post-processing record found to retry";
                        }
                        break;

                    default:
                        record.Success = false;
                        record.Message = $"Unknown recovery action: {action}";
                        break;
                }

                string sourceLabel = source == RecoverySource.Automatic ? "auto" : "manual";
                _logger.Info("RecoveryCenter",
                    $"[{sourceLabel}] {action} on {name} [{infoHash[..Math.Min(8, infoHash.Length)]}...] " +
                    $"trigger={trigger}: {record.Message}");
            }
            catch (Exception ex)
            {
                record.Success = false;
                record.Message = $"Error: {ex.Message}";

                _logger.Error("RecoveryCenter",
                    $"Recovery action {action} failed for {name}: {ex.Message}");
            }

            return record;
        }

        // ────────────────────────────────────────────────────────────
        // HealthReason -> RecoveryTrigger mapping
        // ────────────────────────────────────────────────────────────

        private static RecoveryTrigger MapHealthReason(HealthReason reason)
        {
            return reason switch
            {
                HealthReason.MetadataTimeout => RecoveryTrigger.MetadataTimeout,
                HealthReason.NoPeers => RecoveryTrigger.NoPeers,
                HealthReason.StalledWithPeers => RecoveryTrigger.StalledWithPeers,
                HealthReason.AwaitingRecheck => RecoveryTrigger.AwaitingRecheck,
                _ => RecoveryTrigger.StalledWithPeers
            };
        }
    }
}
