using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Controllarr.Core.Engine;
using Controllarr.Core.Persistence;
using Controllarr.Core.Server;
using Controllarr.Core.Services;

namespace Controllarr.Core
{
    // ────────────────────────────────────────────────────────────────
    // ControllarrRuntime – the umbrella that wires every service
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Top-level runtime that creates, configures, and orchestrates every
    /// Controllarr subsystem. This is the single entry point for both the
    /// GUI application and the headless service host.
    /// </summary>
    public sealed class ControllarrRuntime : IAsyncDisposable
    {
        private const int TickIntervalMs = 2_000; // 2 seconds

        // ── Public service references (readonly) ───────────────────
        public PersistenceStore Store { get; }
        public TorrentEngine Engine { get; }
        public PortWatcher PortWatcher { get; }
        public ControllarrHttpServer HttpServer { get; }
        public Logger Logger { get; }
        public PostProcessor PostProcessor { get; }
        public SeedingPolicy SeedingPolicy { get; }
        public HealthMonitor HealthMonitor { get; }
        public RecoveryCenter RecoveryCenter { get; }
        public BandwidthScheduler BandwidthScheduler { get; }
        public DiskSpaceMonitor DiskSpaceMonitor { get; }
        public VPNMonitor VpnMonitor { get; }
        public ArrNotifier ArrNotifier { get; }

        // ── Tick loop lifecycle ────────────────────────────────────
        private CancellationTokenSource? _tickCts;
        private Task? _tickTask;
        private bool _disposed;

        // ────────────────────────────────────────────────────────────
        // Constructor
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Constructs the entire Controllarr runtime, wiring all services.
        /// </summary>
        /// <param name="storeDirectory">
        /// Directory for persistent state (settings, resume data).
        /// Defaults to %LOCALAPPDATA%\Controllarr.
        /// </param>
        /// <param name="httpHostOverride">Override the HTTP listen address from settings.</param>
        /// <param name="httpPortOverride">Override the HTTP listen port from settings.</param>
        public ControllarrRuntime(string? storeDirectory = null,
                                  string? httpHostOverride = null,
                                  int? httpPortOverride = null)
        {
            // ── 1. Logger (singleton) ──────────────────────────────
            Logger = Logger.Instance;

            // ── 2. Persistence store ───────────────────────────────
            string storeDir = storeDirectory
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Controllarr");

            Directory.CreateDirectory(storeDir);
            Store = new PersistenceStore(storeDir);

            // ── 3. Snapshot settings and resolve listen port ───────
            var state = Store.Snapshot();
            var settings = state.Settings;

            ushort listenPort = state.LastKnownGoodPort
                                ?? settings.ListenPortRangeStart;

            string savePath = settings.DefaultSavePath;
            if (string.IsNullOrWhiteSpace(savePath))
            {
                savePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads",
                    "Controllarr");
            }

            string resumeDir = Path.Combine(storeDir, "ResumeData");

            Logger.Info("Runtime",
                $"Initializing: port={listenPort}, savePath={savePath}, storeDir={storeDir}");

            // ── 4. Torrent engine ──────────────────────────────────
            Engine = new TorrentEngine(savePath, resumeDir, listenPort);

            // ── 5. Restore category map and blocked extensions ─────
            if (state.CategoryByHash.Count > 0)
            {
                Engine.RestoreCategories(
                    new Dictionary<string, string>(state.CategoryByHash));
            }

            foreach (var category in state.Categories)
            {
                if (category.BlockedExtensions.Count > 0)
                {
                    Engine.RegisterBlockedExtensions(
                        category.BlockedExtensions.ToArray(),
                        category.Name);
                }
            }

            // ── 6. Create all service instances ────────────────────

            // Build an ITorrentEngine adapter for services that depend on
            // the interface rather than the concrete type. TorrentEngine
            // does not implement ITorrentEngine directly, so we use a
            // lightweight shim.
            var engineAdapter = new TorrentEngineAdapter(Engine);

            PostProcessor = new PostProcessor(Logger);

            SeedingPolicy = new SeedingPolicy(Logger);

            HealthMonitor = new HealthMonitor(Logger);

            RecoveryCenter = new RecoveryCenter(Logger);

            BandwidthScheduler = new BandwidthScheduler(
                engineAdapter,
                () => (IReadOnlyList<BandwidthRule>)Store.GetSettings().BandwidthSchedule,
                Logger);

            DiskSpaceMonitor = new DiskSpaceMonitor(
                engineAdapter,
                () => Store.GetSettings(),
                () => BuildTorrentViews(),
                Logger);

            VpnMonitor = new VPNMonitor(
                engineAdapter,
                () => Store.GetSettings(),
                () => BuildTorrentViews(),
                Logger);

            ArrNotifier = new ArrNotifier(
                () => Engine.SnapshotCategories(),
                logger: Logger);

            PortWatcher = new PortWatcher(Engine, Store, Logger);

            // ── 7. HTTP server ─────────────────────────────────────
            string httpHost = httpHostOverride ?? settings.WebUIHost;
            int httpPort = httpPortOverride ?? settings.WebUIPort;

            // Locate a WebUI directory adjacent to the store if it exists.
            string? webUIRoot = null;
            string candidateWebUI = Path.Combine(storeDir, "WebUI");
            if (Directory.Exists(candidateWebUI))
                webUIRoot = candidateWebUI;

            HttpServer = new ControllarrHttpServer(
                httpHost,
                httpPort,
                Engine,
                Store,
                Logger,
                PostProcessor,
                SeedingPolicy,
                HealthMonitor,
                RecoveryCenter,
                DiskSpaceMonitor,
                VpnMonitor,
                ArrNotifier,
                () => PortWatcher.ForceCycle("Manual cycle via HTTP API"),
                webUIRoot);

            Logger.Info("Runtime", "All services constructed");
        }

        // ────────────────────────────────────────────────────────────
        // StartAsync
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Starts all background services and the HTTP server, then
        /// launches the 2-second tick loop.
        /// </summary>
        public async Task StartAsync()
        {
            Logger.Info("Runtime", "Starting services...");

            // Start timer-based services
            PortWatcher.Start();
            BandwidthScheduler.Start();
            DiskSpaceMonitor.Start();
            VpnMonitor.Start();

            // Start HTTP server
            await HttpServer.StartAsync().ConfigureAwait(false);

            // Start the main tick loop
            _tickCts = new CancellationTokenSource();
            _tickTask = RunTickLoopAsync(_tickCts.Token);

            Logger.Info("Runtime", "All services started");
        }

        // ────────────────────────────────────────────────────────────
        // Tick loop
        // ────────────────────────────────────────────────────────────

        private async Task RunTickLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // 1. Apply pending file filters (blocked extensions)
                    Engine.ApplyPendingFileFilters();

                    // 2. Poll stats -> build torrent snapshots
                    var torrentStats = Engine.PollStats();
                    var torrentViews = BuildTorrentViewsFromStats(torrentStats);

                    // Snapshot settings and categories for fan-out
                    var settings = Store.GetSettings();
                    var categories = (IReadOnlyList<Category>)Store.GetCategories();

                    // Build an engine adapter for services that need ITorrentEngine
                    var engineAdapter = new TorrentEngineAdapter(Engine);

                    // 3. Fan out to all tick-driven services
                    PostProcessor.Tick(torrentViews, categories, engineAdapter);
                    SeedingPolicy.Tick(torrentViews, settings, categories, engineAdapter);
                    HealthMonitor.Tick(torrentViews, settings);
                    RecoveryCenter.Tick(HealthMonitor, PostProcessor, DiskSpaceMonitor, settings, engineAdapter);
                    ArrNotifier.Tick(HealthMonitor, settings);
                }
                catch (Exception ex)
                {
                    Logger.Error("Runtime", $"Tick error: {ex.Message}");
                }

                try
                {
                    await Task.Delay(TickIntervalMs, ct).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        // ────────────────────────────────────────────────────────────
        // ShutdownAsync
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Gracefully shuts down all services, persists state, and
        /// shuts down the torrent engine.
        /// </summary>
        public async Task ShutdownAsync()
        {
            Logger.Info("Runtime", "Shutting down...");

            // 1. Cancel tick task
            if (_tickCts != null)
            {
                _tickCts.Cancel();

                if (_tickTask != null)
                {
                    try
                    {
                        await _tickTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn("Runtime", $"Tick loop shutdown error: {ex.Message}");
                    }
                }

                _tickCts.Dispose();
                _tickCts = null;
                _tickTask = null;
            }

            // 2. Stop all background services
            PortWatcher.Stop();
            BandwidthScheduler.Stop();
            DiskSpaceMonitor.Stop();
            VpnMonitor.Stop();
            await HttpServer.StopAsync().ConfigureAwait(false);

            // 3. Snapshot category map to persistence
            try
            {
                var categoryMap = Engine.SnapshotCategories();
                Store.SetCategoryMap(categoryMap);

                // 4. Save last known good port
                Store.SetLastKnownGoodPort(Engine.ListenPort);

                // 5. Flush persistence
                Store.FlushNow();
                Logger.Info("Runtime", "State persisted successfully");
            }
            catch (Exception ex)
            {
                Logger.Error("Runtime", $"Failed to persist state on shutdown: {ex.Message}");
            }

            // 6. Shutdown engine
            try
            {
                await Engine.Shutdown().ConfigureAwait(false);
                Logger.Info("Runtime", "Engine shut down");
            }
            catch (Exception ex)
            {
                Logger.Error("Runtime", $"Engine shutdown error: {ex.Message}");
            }

            Logger.Info("Runtime", "Shutdown complete");
        }

        // ────────────────────────────────────────────────────────────
        // IAsyncDisposable
        // ────────────────────────────────────────────────────────────

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            await ShutdownAsync().ConfigureAwait(false);

            PortWatcher.Dispose();
            BandwidthScheduler.Dispose();
            DiskSpaceMonitor.Dispose();
            VpnMonitor.Dispose();
            Engine.Dispose();

            _disposed = true;
        }

        // ────────────────────────────────────────────────────────────
        // Helpers
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds a list of <see cref="TorrentView"/> from the engine's
        /// current torrent set. Used by DiskSpaceMonitor / VPNMonitor
        /// provider delegates.
        /// </summary>
        private IReadOnlyList<TorrentView> BuildTorrentViews()
        {
            var stats = Engine.PollStats();
            return BuildTorrentViewsFromStats(stats);
        }

        /// <summary>
        /// Converts an array of <see cref="TorrentStats"/> into
        /// <see cref="TorrentView"/> instances suitable for service consumption.
        /// </summary>
        private IReadOnlyList<TorrentView> BuildTorrentViewsFromStats(TorrentStats[] stats)
        {
            var views = new List<TorrentView>(stats.Length);

            foreach (var t in stats)
            {
                var view = new TorrentView
                {
                    InfoHash = t.InfoHash,
                    Name = t.Name,
                    State = t.State,
                    Progress = t.Progress,
                    Ratio = t.Ratio,
                    SeedingTimeSeconds = t.State == TorrentState.Seeding
                        ? (long)(DateTime.UtcNow - t.AddedDate).TotalSeconds
                        : 0,
                    NumPeers = t.NumPeers,
                    HasMetadata = t.State != TorrentState.DownloadingMetadata,
                    Category = t.Category,
                    ContentPath = t.SavePath,
                    SavePath = t.SavePath,
                    IsMovingStorage = false,
                    DownloadedBytes = t.TotalDownload,
                    UploadedBytes = t.TotalUpload,
                    TotalBytes = t.TotalWanted,
                    DownloadRateBytes = (int)t.DownloadRate,
                    UploadRateBytes = (int)t.UploadRate,
                    AddedOn = t.AddedDate,
                    CompletedOn = t.Progress >= 0.999f ? t.AddedDate : null
                };

                // Wire the reannounce callback so services can trigger reannounce.
                var hash = t.InfoHash;
                view.SetReannounceCallback(() =>
                {
                    try
                    {
                        Engine.Reannounce(hash).GetAwaiter().GetResult();
                    }
                    catch
                    {
                        // Best effort
                    }
                });

                views.Add(view);
            }

            return views;
        }

        // ────────────────────────────────────────────────────────────
        // ITorrentEngine adapter
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Lightweight adapter that bridges the concrete <see cref="TorrentEngine"/>
        /// to the <see cref="ITorrentEngine"/> interface expected by several services.
        /// </summary>
        private sealed class TorrentEngineAdapter : ITorrentEngine
        {
            private readonly TorrentEngine _inner;

            public TorrentEngineAdapter(TorrentEngine inner)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            public IReadOnlyList<TorrentView> GetTorrents()
            {
                // Build views from current stats.
                var stats = _inner.PollStats();
                var views = new List<TorrentView>(stats.Length);

                foreach (var t in stats)
                {
                    var view = new TorrentView
                    {
                        InfoHash = t.InfoHash,
                        Name = t.Name,
                        State = t.State,
                        Progress = t.Progress,
                        Ratio = t.Ratio,
                        NumPeers = t.NumPeers,
                        HasMetadata = t.State != TorrentState.DownloadingMetadata,
                        Category = t.Category,
                        ContentPath = t.SavePath,
                        SavePath = t.SavePath,
                        DownloadedBytes = t.TotalDownload,
                        UploadedBytes = t.TotalUpload,
                        TotalBytes = t.TotalWanted,
                        DownloadRateBytes = (int)t.DownloadRate,
                        UploadRateBytes = (int)t.UploadRate,
                        AddedOn = t.AddedDate
                    };

                    var hash = t.InfoHash;
                    view.SetReannounceCallback(() =>
                    {
                        try { _inner.Reannounce(hash).GetAwaiter().GetResult(); }
                        catch { /* best effort */ }
                    });

                    views.Add(view);
                }

                return views;
            }

            public void PauseTorrent(string infoHash) =>
                _inner.Pause(infoHash).GetAwaiter().GetResult();

            public void ResumeTorrent(string infoHash) =>
                _inner.Resume(infoHash).GetAwaiter().GetResult();

            public void RemoveTorrent(string infoHash, bool deleteFiles) =>
                _inner.Remove(infoHash, deleteFiles).GetAwaiter().GetResult();

            public void MoveStorage(string infoHash, string destinationPath) =>
                _inner.Move(infoHash, destinationPath).GetAwaiter().GetResult();

            public void SetRateLimits(int downloadKBps, int uploadKBps) =>
                _inner.SetRateLimits(
                    downloadKBps == 0 ? null : downloadKBps,
                    uploadKBps == 0 ? null : uploadKBps);

            public void BindToAddress(string? ipAddress)
            {
                // MonoTorrent's EngineSettings doesn't expose a direct bind-to-address
                // API beyond ListenEndPoints. This is handled via SetListenPort with
                // a specific endpoint. For now, this is a no-op placeholder that
                // the VPNMonitor calls -- the actual bind logic is in VPNMonitor
                // which calls engine.SetListenPort with the VPN interface.
            }

            public void Reannounce(string infoHash) =>
                _inner.Reannounce(infoHash).GetAwaiter().GetResult();
        }
    }
}
