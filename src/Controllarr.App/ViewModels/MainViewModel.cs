using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Controllarr.Core.Engine;
using Controllarr.Core.Persistence;
using Controllarr.Core.Services;

namespace Controllarr.App.ViewModels
{
    // ────────────────────────────────────────────────────────────────
    // Lightweight record types for UI display (not in Core)
    // ────────────────────────────────────────────────────────────────

    public sealed class ArrNotification
    {
        public string TorrentName { get; init; } = string.Empty;
        public string Endpoint { get; init; } = string.Empty;
        public bool Success { get; init; }
        public string Message { get; init; } = string.Empty;
        public DateTime Timestamp { get; init; }
    }

    public sealed class RecoveryRecord
    {
        public string TorrentName { get; init; } = string.Empty;
        public string Trigger { get; init; } = string.Empty;
        public string Action { get; init; } = string.Empty;
        public string Source { get; init; } = string.Empty;
        public bool Success { get; init; }
        public string Message { get; init; } = string.Empty;
        public DateTime Timestamp { get; init; }
    }

    // ────────────────────────────────────────────────────────────────
    // MainViewModel
    // ────────────────────────────────────────────────────────────────

    public partial class MainViewModel : ObservableObject
    {
        // ── Runtime services ───────────────────────────────────────
        private TorrentEngine? _engine;
        private PersistenceStore? _store;
        private HealthMonitor? _healthMonitor;
        private PostProcessor? _postProcessor;
        private SeedingPolicy? _seedingPolicy;
        private VPNMonitor? _vpnMonitor;
        private DiskSpaceMonitor? _diskSpaceMonitor;
        private Logger _logger = Logger.Instance;
        private CancellationTokenSource? _pollCts;

        // ── Dirty-tracking for editable state ─────────────────────
        private bool _settingsUserModified;
        private bool _categoriesUserModified;

        // ════════════════════════════════════════════════════════════
        // Observable properties
        // ════════════════════════════════════════════════════════════

        [ObservableProperty]
        private bool _isBooting = true;

        [ObservableProperty]
        private string? _bootError;

        [ObservableProperty]
        private string _selectedTab = "Torrents";

        [ObservableProperty]
        private bool _isTabSelected_Torrents = true;

        [ObservableProperty]
        private ObservableCollection<TorrentStats> _torrents = new();

        [ObservableProperty]
        private SessionStats _sessionStats = new();

        [ObservableProperty]
        private ObservableCollection<Category> _categories = new();

        [ObservableProperty]
        private Settings _settings = new();

        [ObservableProperty]
        private ObservableCollection<HealthIssue> _healthIssues = new();

        [ObservableProperty]
        private ObservableCollection<PostRecord> _postRecords = new();

        [ObservableProperty]
        private ObservableCollection<SeedEnforcement> _seedingLog = new();

        [ObservableProperty]
        private ObservableCollection<LogEntry> _logEntries = new();

        [ObservableProperty]
        private DiskSpaceStatus? _diskSpaceStatus;

        [ObservableProperty]
        private VpnStatus? _vpnStatus;

        [ObservableProperty]
        private ObservableCollection<ArrNotification> _arrNotifications = new();

        [ObservableProperty]
        private ObservableCollection<RecoveryRecord> _recoveryRecords = new();

        [ObservableProperty]
        private string _addMagnetText = string.Empty;

        [ObservableProperty]
        private bool _isAddMagnetOpen;

        [ObservableProperty]
        private Category? _selectedCategory;

        [ObservableProperty]
        private string _logFilterLevel = "All";

        [ObservableProperty]
        private string _logSearchText = string.Empty;

        // ── Computed display properties ────────────────────────────

        public string DownloadSpeedFormatted =>
            FormatSpeed(SessionStats?.DownloadRate ?? 0);

        public string UploadSpeedFormatted =>
            FormatSpeed(SessionStats?.UploadRate ?? 0);

        public bool VpnConnected =>
            VpnStatus?.IsConnected ?? false;

        public string VpnStatusText =>
            VpnStatus == null ? "VPN Off"
            : VpnStatus.Enabled
                ? (VpnStatus.IsConnected ? "VPN On" : "VPN Down")
                : "VPN Off";

        public bool DiskPressure =>
            DiskSpaceStatus?.IsPaused ?? false;

        public string DiskStatusText =>
            DiskSpaceStatus == null ? "Disk OK"
            : DiskSpaceStatus.IsPaused ? "Low Disk" : "Disk OK";

        // ════════════════════════════════════════════════════════════
        // Commands
        // ════════════════════════════════════════════════════════════

        [RelayCommand]
        private void SelectTab(string tab)
        {
            SelectedTab = tab;
        }

        [RelayCommand]
        private async Task AddMagnet()
        {
            if (string.IsNullOrWhiteSpace(AddMagnetText) || _engine == null)
                return;

            try
            {
                await _engine.AddMagnet(AddMagnetText.Trim());
                AddMagnetText = string.Empty;
                IsAddMagnetOpen = false;
                _logger.Info("UI", "Magnet link added successfully");
            }
            catch (Exception ex)
            {
                _logger.Error("UI", $"Failed to add magnet: {ex.Message}");
                MessageBox.Show($"Failed to add magnet link:\n{ex.Message}",
                    "Controllarr", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private async Task AddTorrentFile()
        {
            if (_engine == null) return;

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Torrent files (*.torrent)|*.torrent|All files (*.*)|*.*",
                Title = "Select Torrent File",
                Multiselect = true
            };

            if (dialog.ShowDialog() == true)
            {
                foreach (var file in dialog.FileNames)
                {
                    try
                    {
                        await _engine.AddTorrentFile(file);
                        _logger.Info("UI", $"Added torrent: {Path.GetFileName(file)}");
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("UI", $"Failed to add torrent {Path.GetFileName(file)}: {ex.Message}");
                    }
                }
            }
        }

        [RelayCommand]
        private async Task PauseTorrent(string? hash)
        {
            if (string.IsNullOrEmpty(hash) || _engine == null) return;
            await _engine.Pause(hash);
        }

        [RelayCommand]
        private async Task ResumeTorrent(string? hash)
        {
            if (string.IsNullOrEmpty(hash) || _engine == null) return;
            await _engine.Resume(hash);
        }

        [RelayCommand]
        private async Task RemoveTorrent(string? hash)
        {
            if (string.IsNullOrEmpty(hash) || _engine == null) return;

            var result = MessageBox.Show(
                "Remove this torrent? Downloaded files will be kept.",
                "Controllarr", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                await _engine.Remove(hash, deleteFiles: false);
                _logger.Info("UI", $"Torrent removed: {hash[..Math.Min(8, hash.Length)]}...");
            }
        }

        [RelayCommand]
        private async Task RemoveWithFiles(string? hash)
        {
            if (string.IsNullOrEmpty(hash) || _engine == null) return;

            var result = MessageBox.Show(
                "Remove this torrent AND delete all downloaded files?\nThis cannot be undone.",
                "Controllarr", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                await _engine.Remove(hash, deleteFiles: true);
                _logger.Info("UI", $"Torrent removed with files: {hash[..Math.Min(8, hash.Length)]}...");
            }
        }

        [RelayCommand]
        private async Task ReannounceTorrent(string? hash)
        {
            if (string.IsNullOrEmpty(hash) || _engine == null) return;
            await _engine.Reannounce(hash);
            _logger.Info("UI", $"Reannounce requested: {hash[..Math.Min(8, hash.Length)]}...");
        }

        [RelayCommand]
        private async Task CyclePort()
        {
            if (_engine == null || _store == null) return;

            var settings = _store.GetSettings();
            var rng = new Random();
            ushort newPort = (ushort)rng.Next(settings.ListenPortRangeStart, settings.ListenPortRangeEnd + 1);

            await _engine.SetListenPort(newPort);
            _store.SetLastKnownGoodPort(newPort);
            _logger.Info("UI", $"Port cycled to {newPort}");
        }

        [RelayCommand]
        private void SaveSettings()
        {
            if (_store == null) return;

            _store.ReplaceSettings(Settings);
            _settingsUserModified = false;
            _logger.Info("UI", "Settings saved");
        }

        [RelayCommand]
        private void RevertSettings()
        {
            if (_store == null) return;

            Settings = _store.GetSettings();
            _settingsUserModified = false;
            _logger.Info("UI", "Settings reverted");
        }

        [RelayCommand]
        private void ClearHealthIssue(string? hash)
        {
            if (string.IsNullOrEmpty(hash) || _healthMonitor == null) return;
            _healthMonitor.ClearIssue(hash);
        }

        [RelayCommand]
        private void RunRecovery(string? hash)
        {
            if (string.IsNullOrEmpty(hash) || _engine == null) return;

            // Attempt reannounce as default recovery action
            _ = _engine.Reannounce(hash);
            _logger.Info("UI", $"Recovery action (reannounce) triggered for {hash[..Math.Min(8, hash.Length)]}...");
        }

        [RelayCommand]
        private void RetryPostProcessor(string? hash)
        {
            if (string.IsNullOrEmpty(hash) || _postProcessor == null) return;
            _postProcessor.Retry(hash);
        }

        [RelayCommand]
        private void RecheckDiskSpace()
        {
            _diskSpaceMonitor?.Recheck();
            _logger.Info("UI", "Disk space recheck requested");
        }

        [RelayCommand]
        private void OpenWebUI()
        {
            if (_store == null) return;

            var settings = _store.GetSettings();
            string url = $"http://{settings.WebUIHost}:{settings.WebUIPort}";

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _logger.Error("UI", $"Failed to open WebUI: {ex.Message}");
            }
        }

        [RelayCommand]
        private void ExportBackup()
        {
            if (_store == null) return;

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "JSON files (*.json)|*.json",
                FileName = $"controllarr-backup-{DateTime.Now:yyyyMMdd-HHmmss}.json",
                Title = "Export Backup"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    string json = _store.ExportBackup(includeSecrets: true);
                    File.WriteAllText(dialog.FileName, json);
                    _logger.Info("UI", $"Backup exported to {dialog.FileName}");
                    MessageBox.Show("Backup exported successfully.",
                        "Controllarr", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    _logger.Error("UI", $"Backup export failed: {ex.Message}");
                    MessageBox.Show($"Backup export failed:\n{ex.Message}",
                        "Controllarr", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        [RelayCommand]
        private void ImportBackup()
        {
            if (_store == null) return;

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "JSON files (*.json)|*.json",
                Title = "Import Backup"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    string json = File.ReadAllText(dialog.FileName);
                    _store.ImportBackup(json);
                    _settingsUserModified = false;
                    _categoriesUserModified = false;
                    _logger.Info("UI", $"Backup imported from {dialog.FileName}");
                    MessageBox.Show("Backup imported successfully. Settings have been updated.",
                        "Controllarr", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    _logger.Error("UI", $"Backup import failed: {ex.Message}");
                    MessageBox.Show($"Backup import failed:\n{ex.Message}",
                        "Controllarr", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        [RelayCommand]
        private void MarkSettingsModified()
        {
            _settingsUserModified = true;
        }

        [RelayCommand]
        private void MarkCategoriesModified()
        {
            _categoriesUserModified = true;
        }

        [RelayCommand]
        private void SaveCategories()
        {
            if (_store == null) return;

            _store.ReplaceCategories(Categories.ToList());
            _categoriesUserModified = false;
            _logger.Info("UI", "Categories saved");
        }

        [RelayCommand]
        private void RevertCategories()
        {
            if (_store == null) return;

            Categories = new ObservableCollection<Category>(_store.GetCategories());
            _categoriesUserModified = false;
            _logger.Info("UI", "Categories reverted");
        }

        // ════════════════════════════════════════════════════════════
        // Initialization
        // ════════════════════════════════════════════════════════════

        public async Task BootAsync()
        {
            IsBooting = true;
            BootError = null;

            try
            {
                _logger.Info("Boot", "Initializing Controllarr...");

                // Create persistence store
                _store = new PersistenceStore();
                var initialSettings = _store.GetSettings();

                // Create torrent engine
                ushort port = _store.Snapshot().LastKnownGoodPort ?? initialSettings.ListenPortRangeStart;
                _engine = new TorrentEngine(
                    initialSettings.DefaultSavePath,
                    _store.ResumeDirectory,
                    port);

                // Restore category map
                var catMap = _store.Snapshot().CategoryByHash;
                _engine.RestoreCategories(catMap);

                // Create service-layer components
                _healthMonitor = new HealthMonitor(_logger);
                _postProcessor = new PostProcessor(_logger);
                _seedingPolicy = new SeedingPolicy(_logger);

                // Build adapters for VPN/Disk monitors
                var engineAdapter = new EngineAdapter(_engine);
                Func<IReadOnlyList<TorrentView>> torrentsProvider = () =>
                    _engine.PollStats()
                        .Select(s => new TorrentView
                        {
                            InfoHash = s.InfoHash,
                            Name = s.Name,
                            State = s.State,
                            Progress = s.Progress,
                            NumPeers = s.NumPeers,
                            Category = s.Category,
                            SavePath = s.SavePath,
                            DownloadRateBytes = (int)s.DownloadRate,
                            UploadRateBytes = (int)s.UploadRate,
                        })
                        .ToList();

                _vpnMonitor = new VPNMonitor(
                    engineAdapter,
                    () => _store.GetSettings(),
                    torrentsProvider,
                    _logger);

                _diskSpaceMonitor = new DiskSpaceMonitor(
                    engineAdapter,
                    () => _store.GetSettings(),
                    torrentsProvider,
                    _logger);

                // Start optional monitors
                if (initialSettings.VpnEnabled)
                    _vpnMonitor.Start();

                if (initialSettings.DiskSpaceMinimumGB.HasValue)
                    _diskSpaceMonitor.Start();

                // Register with App
                if (Application.Current is App app)
                {
                    app.SetRuntime(_store, _engine);

                    // Handle pending command-line magnets/files
                    foreach (var magnet in app.PendingMagnets)
                    {
                        try { await _engine.AddMagnet(magnet); }
                        catch (Exception ex) { _logger.Error("Boot", $"Failed to add magnet: {ex.Message}"); }
                    }
                    foreach (var file in app.PendingTorrentFiles)
                    {
                        try { await _engine.AddTorrentFile(file); }
                        catch (Exception ex) { _logger.Error("Boot", $"Failed to add torrent: {ex.Message}"); }
                    }
                }

                _logger.Info("Boot", "Controllarr started successfully");

                // Load initial settings for UI
                Settings = _store.GetSettings();

                IsBooting = false;

                // Start polling loop
                StartPolling();
            }
            catch (Exception ex)
            {
                BootError = ex.Message;
                _logger.Error("Boot", $"Startup failed: {ex.Message}");
            }
        }

        public async Task ShutdownAsync()
        {
            _pollCts?.Cancel();

            if (_engine != null)
            {
                // Persist category map before shutdown
                if (_store != null)
                {
                    _store.SetCategoryMap(_engine.SnapshotCategories());
                    _store.FlushNow();
                }

                await _engine.SaveResumeData();
                await _engine.Shutdown();
            }

            _vpnMonitor?.Dispose();
            _diskSpaceMonitor?.Dispose();
            _store?.Dispose();
        }

        // ════════════════════════════════════════════════════════════
        // Polling loop (2s interval)
        // ════════════════════════════════════════════════════════════

        private void StartPolling()
        {
            _pollCts = new CancellationTokenSource();
            var token = _pollCts.Token;

            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(2000, token);
                        if (token.IsCancellationRequested) break;

                        PollAll();
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("Poll", $"Polling error: {ex.Message}");
                    }
                }
            }, token);
        }

        private void PollAll()
        {
            if (_engine == null || _store == null) return;

            // Gather all data off the UI thread
            var torrentStats = _engine.PollStats();
            var sessionStats = _engine.GetSessionStats();
            var categories = _store.GetCategories();
            var settings = _store.GetSettings();
            var healthIssues = _healthMonitor?.Snapshot() ?? new();
            var postRecords = _postProcessor?.Snapshot() ?? new();
            var seedingLog = _seedingPolicy?.Snapshot() ?? new();
            var logEntries = _logger.Snapshot();
            var diskStatus = _diskSpaceMonitor?.Snapshot();
            var vpnStatus = _vpnMonitor?.Snapshot();

            // Tick service monitors (use TorrentView for health/post/seeding)
            var torrentViews = torrentStats.Select(s => new TorrentView
            {
                InfoHash = s.InfoHash,
                Name = s.Name,
                State = s.State,
                Progress = s.Progress,
                Ratio = s.Ratio,
                NumPeers = s.NumPeers,
                Category = s.Category,
                SavePath = s.SavePath,
                ContentPath = s.SavePath,
                DownloadRateBytes = (int)s.DownloadRate,
                UploadRateBytes = (int)s.UploadRate,
                HasMetadata = s.State != TorrentState.DownloadingMetadata,
            }).ToList();

            _healthMonitor?.Tick(torrentViews, settings);

            var engineAdapter = new EngineAdapter(_engine);
            _postProcessor?.Tick(torrentViews, categories, engineAdapter);
            _seedingPolicy?.Tick(torrentViews, settings, categories, engineAdapter);

            // Update UI on dispatcher thread
            Application.Current?.Dispatcher.Invoke(() =>
            {
                // Torrents
                Torrents = new ObservableCollection<TorrentStats>(torrentStats);

                // Session stats
                SessionStats = sessionStats;
                OnPropertyChanged(nameof(DownloadSpeedFormatted));
                OnPropertyChanged(nameof(UploadSpeedFormatted));

                // Categories (only refresh if user hasn't modified)
                if (!_categoriesUserModified)
                    Categories = new ObservableCollection<Category>(categories);

                // Settings (only refresh if user hasn't modified)
                if (!_settingsUserModified)
                    Settings = settings;

                // Health
                HealthIssues = new ObservableCollection<HealthIssue>(healthIssues);

                // Post-processor
                PostRecords = new ObservableCollection<PostRecord>(postRecords);

                // Seeding
                SeedingLog = new ObservableCollection<SeedEnforcement>(seedingLog);

                // Log entries
                LogEntries = new ObservableCollection<LogEntry>(logEntries);

                // Disk space
                DiskSpaceStatus = diskStatus;
                OnPropertyChanged(nameof(DiskPressure));
                OnPropertyChanged(nameof(DiskStatusText));

                // VPN
                VpnStatus = vpnStatus;
                OnPropertyChanged(nameof(VpnConnected));
                OnPropertyChanged(nameof(VpnStatusText));
            });
        }

        // ════════════════════════════════════════════════════════════
        // Helpers
        // ════════════════════════════════════════════════════════════

        public static string FormatSpeed(long bytesPerSecond)
        {
            if (bytesPerSecond < 1024)
                return $"{bytesPerSecond} B/s";
            if (bytesPerSecond < 1024 * 1024)
                return $"{bytesPerSecond / 1024.0:F1} KB/s";
            if (bytesPerSecond < 1024L * 1024 * 1024)
                return $"{bytesPerSecond / (1024.0 * 1024.0):F1} MB/s";
            return $"{bytesPerSecond / (1024.0 * 1024.0 * 1024.0):F2} GB/s";
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024)
                return $"{bytes} B";
            if (bytes < 1024 * 1024)
                return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024)
                return $"{bytes / (1024.0 * 1024.0):F1} MB";
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }

        public static string FormatEta(int seconds)
        {
            if (seconds < 0) return "--";
            if (seconds < 60) return $"{seconds}s";
            if (seconds < 3600) return $"{seconds / 60}m {seconds % 60}s";
            return $"{seconds / 3600}h {(seconds % 3600) / 60}m";
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Adapter: wraps TorrentEngine to implement ITorrentEngine
    // ────────────────────────────────────────────────────────────────

    internal sealed class EngineAdapter : ITorrentEngine
    {
        private readonly TorrentEngine _engine;

        public EngineAdapter(TorrentEngine engine)
        {
            _engine = engine;
        }

        public IReadOnlyList<TorrentView> GetTorrents()
        {
            return _engine.PollStats()
                .Select(s => new TorrentView
                {
                    InfoHash = s.InfoHash,
                    Name = s.Name,
                    State = s.State,
                    Progress = s.Progress,
                    Ratio = s.Ratio,
                    NumPeers = s.NumPeers,
                    Category = s.Category,
                    SavePath = s.SavePath,
                    ContentPath = s.SavePath,
                    DownloadRateBytes = (int)s.DownloadRate,
                    UploadRateBytes = (int)s.UploadRate,
                    HasMetadata = s.State != TorrentState.DownloadingMetadata,
                })
                .ToList();
        }

        public void PauseTorrent(string infoHash) =>
            _engine.Pause(infoHash).GetAwaiter().GetResult();

        public void ResumeTorrent(string infoHash) =>
            _engine.Resume(infoHash).GetAwaiter().GetResult();

        public void RemoveTorrent(string infoHash, bool deleteFiles) =>
            _engine.Remove(infoHash, deleteFiles).GetAwaiter().GetResult();

        public void MoveStorage(string infoHash, string destinationPath) =>
            _engine.Move(infoHash, destinationPath).GetAwaiter().GetResult();

        public void SetRateLimits(int downloadKBps, int uploadKBps) =>
            _engine.SetRateLimits(downloadKBps > 0 ? downloadKBps : null, uploadKBps > 0 ? uploadKBps : null);

        public void BindToAddress(string? ipAddress)
        {
            // Engine-level binding not directly exposed; VPN monitor handles this
        }

        public void Reannounce(string infoHash) =>
            _engine.Reannounce(infoHash).GetAwaiter().GetResult();
    }

    // ────────────────────────────────────────────────────────────────
    // Static enum value providers for ComboBox binding
    // ────────────────────────────────────────────────────────────────

    public static class SeedLimitActionValues
    {
        public static SeedLimitAction[] All { get; } = Enum.GetValues<SeedLimitAction>();
    }

    public static class RecoveryTriggerValues
    {
        public static RecoveryTrigger[] All { get; } = Enum.GetValues<RecoveryTrigger>();
    }

    public static class RecoveryActionValues
    {
        public static RecoveryAction[] All { get; } = Enum.GetValues<RecoveryAction>();
    }

    public static class ArrKindValues
    {
        public static ArrKind[] All { get; } = Enum.GetValues<ArrKind>();
    }
}
