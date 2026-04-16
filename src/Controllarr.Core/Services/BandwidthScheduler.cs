using System;
using System.Collections.Generic;
using System.Threading;

using Controllarr.Core.Engine;
using Controllarr.Core.Persistence;

namespace Controllarr.Core.Services
{
    // ────────────────────────────────────────────────────────────────
    // Bandwidth scheduler – applies time-of-day rate limits
    // ────────────────────────────────────────────────────────────────

    public sealed class BandwidthScheduler : IDisposable
    {
        private const int TickIntervalMs = 60_000; // 60 seconds

        private readonly ITorrentEngine _engine;
        private readonly Func<IReadOnlyList<BandwidthRule>> _rulesProvider;
        private readonly Logger _logger;
        private Timer? _timer;
        private readonly object _lock = new();

        // Track what we last applied to avoid spamming the engine
        private int? _lastDownloadKBps;
        private int? _lastUploadKBps;

        /// <summary>
        /// Creates a new bandwidth scheduler.
        /// </summary>
        /// <param name="engine">The torrent engine to apply rate limits to.</param>
        /// <param name="rulesProvider">
        /// Delegate that returns the current bandwidth rules.
        /// Called on each tick so changes take effect without restart.
        /// </param>
        /// <param name="logger">Optional logger instance.</param>
        public BandwidthScheduler(ITorrentEngine engine,
                                  Func<IReadOnlyList<BandwidthRule>> rulesProvider,
                                  Logger? logger = null)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _rulesProvider = rulesProvider ?? throw new ArgumentNullException(nameof(rulesProvider));
            _logger = logger ?? Logger.Instance;
        }

        /// <summary>Start the scheduler timer.</summary>
        public void Start()
        {
            lock (_lock)
            {
                if (_timer != null)
                    return;

                _timer = new Timer(OnTick, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(TickIntervalMs));
                _logger.Info("BandwidthScheduler", "Started");
            }
        }

        /// <summary>Stop the scheduler timer.</summary>
        public void Stop()
        {
            lock (_lock)
            {
                _timer?.Dispose();
                _timer = null;
                _lastDownloadKBps = null;
                _lastUploadKBps = null;
                _logger.Info("BandwidthScheduler", "Stopped");
            }
        }

        public void Dispose()
        {
            Stop();
        }

        // ── Timer callback ──────────────────────────────────────────

        private void OnTick(object? state)
        {
            try
            {
                var rules = _rulesProvider();
                var now = DateTime.Now; // local time for schedule matching

                int? matchedDownload = null;
                int? matchedUpload = null;
                string? matchedRuleName = null;

                foreach (var rule in rules)
                {
                    if (!rule.Enabled)
                        continue;

                    if (!IsMatchingNow(rule, now))
                        continue;

                    // First matching rule wins
                    matchedDownload = rule.MaxDownloadKBps;
                    matchedUpload = rule.MaxUploadKBps;
                    matchedRuleName = rule.Name;
                    break;
                }

                // Determine effective limits (null = unlimited, i.e. 0)
                int effectiveDown = matchedDownload ?? 0;
                int effectiveUp = matchedUpload ?? 0;

                // Only apply if changed
                if (effectiveDown != _lastDownloadKBps || effectiveUp != _lastUploadKBps)
                {
                    _engine.SetRateLimits(effectiveDown, effectiveUp);
                    _lastDownloadKBps = effectiveDown;
                    _lastUploadKBps = effectiveUp;

                    if (matchedRuleName != null)
                    {
                        _logger.Info("BandwidthScheduler",
                            $"Applied rule '{matchedRuleName}': down={effectiveDown} KBps, up={effectiveUp} KBps");
                    }
                    else
                    {
                        _logger.Info("BandwidthScheduler",
                            "No matching rule – removed rate limits");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("BandwidthScheduler", $"Tick error: {ex.Message}");
            }
        }

        // ── Schedule matching ───────────────────────────────────────

        /// <summary>
        /// Check if the given rule matches the current day and time.
        /// DaysOfWeek uses: 1=Sunday, 2=Monday, ..., 7=Saturday
        /// Supports midnight-wrapping time windows (e.g. 22:00 - 06:00).
        /// </summary>
        private static bool IsMatchingNow(BandwidthRule rule, DateTime now)
        {
            // Map .NET DayOfWeek (Sun=0..Sat=6) to rule convention (Sun=1..Sat=7)
            int todayCode = (int)now.DayOfWeek + 1;

            if (rule.DaysOfWeek.Count > 0 && !rule.DaysOfWeek.Contains(todayCode))
                return false;

            int nowMinutes = now.Hour * 60 + now.Minute;
            int startMinutes = rule.StartHour * 60 + rule.StartMinute;
            int endMinutes = rule.EndHour * 60 + rule.EndMinute;

            if (startMinutes <= endMinutes)
            {
                // Normal window (e.g. 08:00 - 17:00)
                return nowMinutes >= startMinutes && nowMinutes <= endMinutes;
            }
            else
            {
                // Midnight-wrapping window (e.g. 22:00 - 06:00)
                return nowMinutes >= startMinutes || nowMinutes <= endMinutes;
            }
        }
    }
}
