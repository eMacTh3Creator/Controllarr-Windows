using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Controllarr.Core.Engine;
using Controllarr.Core.Persistence;

namespace Controllarr.Core.Services
{
    // ────────────────────────────────────────────────────────────────
    // Port events
    // ────────────────────────────────────────────────────────────────

    public enum PortEvent
    {
        Healthy,
        StallDetected,
        PortSwitched
    }

    public sealed class PortEventArgs
    {
        public PortEvent Event { get; }
        public ushort CurrentPort { get; }
        public ushort? NewPort { get; }
        public string Message { get; }

        public PortEventArgs(PortEvent @event, ushort currentPort, ushort? newPort, string message)
        {
            Event = @event;
            CurrentPort = currentPort;
            NewPort = newPort;
            Message = message;
        }
    }

    // ────────────────────────────────────────────────────────────────
    // PortWatcher – detects stalled downloads and cycles listen port
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The core Controllarr feature: monitors aggregate download throughput and,
    /// when the listen port appears to go "dark" (zero download rate across all
    /// active incomplete torrents for longer than the stall threshold), automatically
    /// selects a new listen port from the configured range, burns the old port, and
    /// forces a tracker reannounce so peers discover the new endpoint.
    /// </summary>
    public sealed class PortWatcher : IDisposable
    {
        private const int MaxRandomAttempts = 64;

        // ── Dependencies ───────────────────────────────────────────
        private readonly TorrentEngine _engine;
        private readonly PersistenceStore _store;
        private readonly Logger _logger;

        // ── Configuration ──────────────────────────────────────────
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

        // ── Internal state ─────────────────────────────────────────
        private readonly HashSet<ushort> _burned = new();
        private DateTime? _stalledSince;
        private Action<PortEventArgs>? _eventHandler;
        private readonly object _lock = new();

        // ── Lifecycle ──────────────────────────────────────────────
        private CancellationTokenSource? _cts;
        private Task? _loopTask;
        private bool _disposed;

        // ────────────────────────────────────────────────────────────
        // Constructor
        // ────────────────────────────────────────────────────────────

        public PortWatcher(TorrentEngine engine,
                           PersistenceStore store,
                           Logger? logger = null)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _logger = logger ?? Logger.Instance;
        }

        // ────────────────────────────────────────────────────────────
        // Public API
        // ────────────────────────────────────────────────────────────

        /// <summary>Register a callback for port-watcher events.</summary>
        public void OnEvent(Action<PortEventArgs> handler)
        {
            _eventHandler = handler;
        }

        /// <summary>Start the background polling loop.</summary>
        public void Start()
        {
            lock (_lock)
            {
                if (_loopTask != null)
                    return;

                _cts = new CancellationTokenSource();
                _loopTask = RunLoopAsync(_cts.Token);
                _logger.Info("PortWatcher", $"Started (interval={PollInterval.TotalSeconds}s)");
            }
        }

        /// <summary>Stop the background polling loop.</summary>
        public void Stop()
        {
            lock (_lock)
            {
                if (_cts == null)
                    return;

                _cts.Cancel();

                try
                {
                    _loopTask?.Wait(TimeSpan.FromSeconds(5));
                }
                catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is TaskCanceledException or OperationCanceledException))
                {
                    // Expected on cancellation
                }

                _cts.Dispose();
                _cts = null;
                _loopTask = null;

                _logger.Info("PortWatcher", "Stopped");
            }
        }

        /// <summary>Manually trigger a port cycle with a reason string.</summary>
        public void ForceCycle(string reason)
        {
            _logger.Info("PortWatcher", $"Force cycle requested: {reason}");

            try
            {
                ReselectPort(reason);
            }
            catch (Exception ex)
            {
                _logger.Error("PortWatcher", $"Force cycle failed: {ex.Message}");
            }
        }

        // ────────────────────────────────────────────────────────────
        // Background loop
        // ────────────────────────────────────────────────────────────

        private async Task RunLoopAsync(CancellationToken ct)
        {
            // Small initial delay to let the engine settle after startup.
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    Tick();
                }
                catch (Exception ex)
                {
                    _logger.Error("PortWatcher", $"Tick error: {ex.Message}");
                }

                try
                {
                    await Task.Delay(PollInterval, ct).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        // ────────────────────────────────────────────────────────────
        // Tick logic
        // ────────────────────────────────────────────────────────────

        private void Tick()
        {
            var session = _engine.GetSessionStats();
            var torrents = _engine.PollStats();
            var settings = _store.GetSettings();

            // Only evaluate when there are active downloading (non-paused,
            // non-seeding, incomplete) torrents.
            bool hasActiveDownloads = torrents.Any(t =>
                t.State == TorrentState.Downloading &&
                !t.Paused &&
                t.Progress < 0.999f);

            if (!hasActiveDownloads)
            {
                // No active downloads -- nothing to evaluate; reset stall timer.
                _stalledSince = null;
                return;
            }

            int stallThresholdMinutes = settings.StallThresholdMinutes > 0
                ? settings.StallThresholdMinutes
                : 10;

            if (session.DownloadRate == 0)
            {
                // Zero throughput with active downloads -- start or continue stall timer.
                if (_stalledSince == null)
                {
                    _stalledSince = DateTime.UtcNow;
                    _logger.Debug("PortWatcher",
                        $"Download rate dropped to 0 with {torrents.Count(t => t.State == TorrentState.Downloading && !t.Paused)} active download(s) -- stall timer started");
                }

                double stalledMinutes = (DateTime.UtcNow - _stalledSince.Value).TotalMinutes;

                if (stalledMinutes >= stallThresholdMinutes)
                {
                    // Stall threshold exceeded -- cycle port.
                    Emit(new PortEventArgs(
                        PortEvent.StallDetected,
                        session.ListenPort,
                        null,
                        $"Download stalled for {stalledMinutes:F1} minutes (threshold: {stallThresholdMinutes}m)"));

                    _logger.Warn("PortWatcher",
                        $"Stall detected: 0 B/s for {stalledMinutes:F1}m on port {session.ListenPort} -- cycling port");

                    try
                    {
                        ReselectPort($"Stall after {stalledMinutes:F1} minutes on port {session.ListenPort}");
                        _stalledSince = null; // Reset after successful cycle
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("PortWatcher", $"Port cycle failed: {ex.Message}");
                    }
                }
            }
            else
            {
                // Healthy -- data is flowing.
                if (_stalledSince != null)
                {
                    _logger.Debug("PortWatcher",
                        $"Download resumed ({session.DownloadRate} B/s) -- stall timer reset");
                }

                _stalledSince = null;

                Emit(new PortEventArgs(
                    PortEvent.Healthy,
                    session.ListenPort,
                    null,
                    $"Download rate: {session.DownloadRate} B/s"));
            }
        }

        // ────────────────────────────────────────────────────────────
        // Port reselection
        // ────────────────────────────────────────────────────────────

        private void ReselectPort(string reason)
        {
            var settings = _store.GetSettings();

            ushort rangeStart = settings.ListenPortRangeStart;
            ushort rangeEnd = settings.ListenPortRangeEnd;

            if (rangeEnd <= rangeStart)
            {
                _logger.Error("PortWatcher",
                    $"Invalid port range: {rangeStart}-{rangeEnd}");
                return;
            }

            int poolSize = rangeEnd - rangeStart + 1;
            ushort currentPort = _engine.ListenPort;

            // Burn the current port.
            _burned.Add(currentPort);

            // If burn list is excessive, reset it to just the current port so we
            // don't exhaust the pool. Threshold: max(4, poolSize / 2).
            int burnLimit = Math.Max(4, poolSize / 2);
            if (_burned.Count > burnLimit)
            {
                _logger.Info("PortWatcher",
                    $"Burn list ({_burned.Count}) exceeds limit ({burnLimit}) -- resetting to current port only");
                _burned.Clear();
                _burned.Add(currentPort);
            }

            // Pick a new port: try random first, then fall back to linear scan.
            ushort? newPort = PickRandomPort(rangeStart, rangeEnd);

            if (!newPort.HasValue)
            {
                newPort = PickLinearPort(rangeStart, rangeEnd);
            }

            if (!newPort.HasValue)
            {
                // All ports burned -- reset burn list and try once more.
                _logger.Warn("PortWatcher",
                    "All ports in range are burned -- clearing burn list");
                _burned.Clear();
                _burned.Add(currentPort);
                newPort = PickRandomPort(rangeStart, rangeEnd)
                       ?? PickLinearPort(rangeStart, rangeEnd);
            }

            if (!newPort.HasValue)
            {
                _logger.Error("PortWatcher",
                    $"Unable to find an available port in range {rangeStart}-{rangeEnd}");
                return;
            }

            // Apply the new port.
            _logger.Info("PortWatcher",
                $"Switching port: {currentPort} -> {newPort.Value} (reason: {reason})");

            try
            {
                _engine.SetListenPort(newPort.Value).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.Error("PortWatcher",
                    $"Failed to set listen port to {newPort.Value}: {ex.Message}");
                return;
            }

            // Force reannounce so trackers learn the new port.
            try
            {
                _engine.ForceReannounceAll().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.Warn("PortWatcher",
                    $"Reannounce after port switch failed: {ex.Message}");
            }

            // Persist the new port as last known good.
            try
            {
                _store.SetLastKnownGoodPort(newPort.Value);
                _store.FlushNow();
            }
            catch (Exception ex)
            {
                _logger.Warn("PortWatcher",
                    $"Failed to persist new port: {ex.Message}");
            }

            Emit(new PortEventArgs(
                PortEvent.PortSwitched,
                currentPort,
                newPort.Value,
                $"Port switched {currentPort} -> {newPort.Value}: {reason}"));
        }

        // ────────────────────────────────────────────────────────────
        // Port selection helpers
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Attempt up to <see cref="MaxRandomAttempts"/> random port picks
        /// from the range that are not in the burn list.
        /// </summary>
        private ushort? PickRandomPort(ushort rangeStart, ushort rangeEnd)
        {
            var rng = Random.Shared;
            int range = rangeEnd - rangeStart + 1;

            for (int i = 0; i < MaxRandomAttempts; i++)
            {
                ushort candidate = (ushort)(rangeStart + rng.Next(range));
                if (!_burned.Contains(candidate))
                    return candidate;
            }

            return null;
        }

        /// <summary>
        /// Linear scan from range start to range end, returning the first
        /// port not in the burn list.
        /// </summary>
        private ushort? PickLinearPort(ushort rangeStart, ushort rangeEnd)
        {
            for (int port = rangeStart; port <= rangeEnd; port++)
            {
                ushort candidate = (ushort)port;
                if (!_burned.Contains(candidate))
                    return candidate;
            }

            return null;
        }

        // ────────────────────────────────────────────────────────────
        // Event emission
        // ────────────────────────────────────────────────────────────

        private void Emit(PortEventArgs args)
        {
            try
            {
                _eventHandler?.Invoke(args);
            }
            catch (Exception ex)
            {
                _logger.Error("PortWatcher",
                    $"Event handler threw: {ex.Message}");
            }
        }

        // ────────────────────────────────────────────────────────────
        // IDisposable
        // ────────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed)
                return;

            Stop();
            _disposed = true;
        }
    }
}
