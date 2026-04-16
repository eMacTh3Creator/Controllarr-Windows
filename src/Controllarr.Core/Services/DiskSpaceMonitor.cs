using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

using Controllarr.Core.Engine;
using Controllarr.Core.Persistence;

namespace Controllarr.Core.Services
{
    // ────────────────────────────────────────────────────────────────
    // Disk space status snapshot
    // ────────────────────────────────────────────────────────────────

    public sealed class DiskSpaceStatus
    {
        public long FreeBytes { get; set; }
        public long ThresholdBytes { get; set; }
        public string MonitorPath { get; set; } = string.Empty;
        public long ShortfallBytes { get; set; }
        public bool IsPaused { get; set; }
        public HashSet<string> PausedHashes { get; set; } = new();

        public DiskSpaceStatus() { }
    }

    // ────────────────────────────────────────────────────────────────
    // Disk space monitor – pauses downloads when space is low
    // ────────────────────────────────────────────────────────────────

    public sealed class DiskSpaceMonitor : IDisposable
    {
        private const int TickIntervalMs = 30_000; // 30 seconds

        private readonly ITorrentEngine _engine;
        private readonly Func<Settings> _settingsProvider;
        private readonly Func<IReadOnlyList<TorrentView>> _torrentsProvider;
        private readonly Logger _logger;
        private Timer? _timer;
        private readonly object _lock = new();

        // Hashes that THIS monitor paused (not user-paused torrents)
        private readonly HashSet<string> _pausedHashes = new();
        private bool _isPaused;
        private long _lastFreeBytes;

        public DiskSpaceMonitor(ITorrentEngine engine,
                                Func<Settings> settingsProvider,
                                Func<IReadOnlyList<TorrentView>> torrentsProvider,
                                Logger? logger = null)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
            _torrentsProvider = torrentsProvider ?? throw new ArgumentNullException(nameof(torrentsProvider));
            _logger = logger ?? Logger.Instance;
        }

        /// <summary>Start monitoring disk space.</summary>
        public void Start()
        {
            lock (_lock)
            {
                if (_timer != null)
                    return;

                _timer = new Timer(OnTick, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(TickIntervalMs));
                _logger.Info("DiskSpaceMonitor", "Started");
            }
        }

        /// <summary>Stop monitoring disk space.</summary>
        public void Stop()
        {
            lock (_lock)
            {
                _timer?.Dispose();
                _timer = null;
                _logger.Info("DiskSpaceMonitor", "Stopped");
            }
        }

        /// <summary>Force an immediate recheck.</summary>
        public void Recheck()
        {
            OnTick(null);
        }

        /// <summary>Returns a snapshot of the current disk space status.</summary>
        public DiskSpaceStatus Snapshot()
        {
            lock (_lock)
            {
                var settings = _settingsProvider();
                long thresholdBytes = (settings.DiskSpaceMinimumGB ?? 0) * 1_073_741_824L; // GB to bytes

                return new DiskSpaceStatus
                {
                    FreeBytes = _lastFreeBytes,
                    ThresholdBytes = thresholdBytes,
                    MonitorPath = settings.DiskSpaceMonitorPath,
                    ShortfallBytes = Math.Max(0, thresholdBytes - _lastFreeBytes),
                    IsPaused = _isPaused,
                    PausedHashes = new HashSet<string>(_pausedHashes)
                };
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
                var settings = _settingsProvider();

                // If not configured, nothing to do
                if (!settings.DiskSpaceMinimumGB.HasValue || settings.DiskSpaceMinimumGB.Value <= 0)
                    return;

                string monitorPath = settings.DiskSpaceMonitorPath;
                if (string.IsNullOrWhiteSpace(monitorPath))
                    monitorPath = settings.DefaultSavePath;

                if (string.IsNullOrWhiteSpace(monitorPath))
                    return;

                long thresholdBytes = settings.DiskSpaceMinimumGB.Value * 1_073_741_824L;
                long freeBytes = GetFreeBytes(monitorPath);

                lock (_lock)
                {
                    _lastFreeBytes = freeBytes;

                    if (freeBytes < thresholdBytes)
                    {
                        OnLowSpace(freeBytes, thresholdBytes);
                    }
                    else if (_isPaused)
                    {
                        OnSpaceFreed(freeBytes, thresholdBytes);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("DiskSpaceMonitor", $"Tick error: {ex.Message}");
            }
        }

        private void OnLowSpace(long freeBytes, long thresholdBytes)
        {
            if (!_isPaused)
            {
                _logger.Warn("DiskSpaceMonitor",
                    $"Low disk space: {freeBytes / 1_073_741_824.0:F2} GB free, threshold {thresholdBytes / 1_073_741_824.0:F2} GB");
            }

            _isPaused = true;

            // Pause all currently downloading torrents
            var torrents = _torrentsProvider();
            foreach (var t in torrents)
            {
                if (t.State == TorrentState.Downloading && !_pausedHashes.Contains(t.InfoHash))
                {
                    _engine.PauseTorrent(t.InfoHash);
                    _pausedHashes.Add(t.InfoHash);
                    _logger.Info("DiskSpaceMonitor",
                        $"Paused due to low disk space: {t.Name}");
                }
            }
        }

        private void OnSpaceFreed(long freeBytes, long thresholdBytes)
        {
            _logger.Info("DiskSpaceMonitor",
                $"Disk space recovered: {freeBytes / 1_073_741_824.0:F2} GB free (threshold {thresholdBytes / 1_073_741_824.0:F2} GB)");

            _isPaused = false;

            // Resume only the torrents WE paused
            foreach (var hash in _pausedHashes)
            {
                try
                {
                    _engine.ResumeTorrent(hash);
                    _logger.Info("DiskSpaceMonitor",
                        $"Resumed after disk space recovery: {hash[..8]}...");
                }
                catch (Exception ex)
                {
                    _logger.Warn("DiskSpaceMonitor",
                        $"Failed to resume {hash[..8]}...: {ex.Message}");
                }
            }

            _pausedHashes.Clear();
        }

        // ── Disk space query ────────────────────────────────────────

        private static long GetFreeBytes(string path)
        {
            // Resolve to drive root
            string root = Path.GetPathRoot(path)
                ?? throw new InvalidOperationException($"Cannot determine root for path: {path}");

            var driveInfo = new DriveInfo(root);

            if (!driveInfo.IsReady)
                throw new InvalidOperationException($"Drive {root} is not ready");

            return driveInfo.AvailableFreeSpace;
        }
    }
}
