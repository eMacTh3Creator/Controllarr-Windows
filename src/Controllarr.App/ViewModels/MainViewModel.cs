using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Controllarr.Core.Engine;
using Controllarr.Core.Persistence;
using Controllarr.Core.Server;
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
        public DesktopViewModel Desktop { get; }
        internal TorrentEngine? DesktopEngine => _engine;
        internal bool DesktopTransferAllowed => _diskSpaceMonitor?.Snapshot().IsPaused != true &&
            _engine?.NetworkPolicy.Allowed == true;
        public MainViewModel() => Desktop = new DesktopViewModel(this);
        internal void PersistDesktopCategories()
        {
            if (_engine != null && _store != null)
            {
                _store.SetCategoryMap(_engine.SnapshotCategories());
                _store.FlushNow();
            }
        }
        // ── Runtime services ───────────────────────────────────────
        private TorrentEngine? _engine;
        private PersistenceStore? _store;
        private HealthMonitor? _healthMonitor;
        private PostProcessor? _postProcessor;
        private SeedingPolicy? _seedingPolicy;
        private VPNMonitor? _vpnMonitor;
        private DiskSpaceMonitor? _diskSpaceMonitor;
        private RecoveryCenter? _recoveryCenter;
        private ArrNotifier? _arrNotifier;
        private PortWatcher? _portWatcher;
        private ControllarrHttpServer? _httpServer;
        private Logger _logger = Logger.Instance;
        private CancellationTokenSource? _pollCts;
        private Task? _pollTask;
        private RssService? _rssService;
        private BandwidthScheduler? _bandwidthScheduler;
        [ObservableProperty] private ObservableCollection<RssEntry> _rssEntries = new();
        [ObservableProperty] private string _rssStatus = "Configure feeds, then save settings to activate them.";

        [RelayCommand] private void AddRssFeed()
        {
            Settings.RssFeeds.Add(new RssFeed());
            OnPropertyChanged(nameof(Settings));
            _settingsUserModified = true;
        }
        [RelayCommand] private void RemoveRssFeed(RssFeed? feed)
        {
            if (feed == null) return;
            Settings.RssFeeds.Remove(feed);
            OnPropertyChanged(nameof(Settings));
            _settingsUserModified = true;
        }
        [RelayCommand] private async Task CheckRss()
        {
            if (_rssService == null) return;
            RssStatus = "Checking feeds and watch folder...";
            try { await Task.Run(_rssService.ScanNowAsync); RssEntries = new(_rssService.Snapshot()); RssStatus = "Check complete. Only saved feed settings are used."; }
            catch (Exception ex) { RssStatus = ex.Message; }
        }

        // ── Dirty-tracking for editable state ─────────────────────
        private bool _settingsUserModified;
        private bool _categoriesUserModified;
        private readonly Dictionary<Category, string> _categoryOriginalNames = new();

        partial void OnCategoriesChanged(ObservableCollection<Category> value)
        {
            _categoryOriginalNames.Clear();
            foreach (var category in value) _categoryOriginalNames[category] = category.Name;
        }

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
        private bool _isTabSelected_Home = true;

        [ObservableProperty]
        private bool _isTabSelected_Torrents;

        [ObservableProperty]
        private IReadOnlyList<TorrentStats> _torrents = Array.Empty<TorrentStats>();

        [ObservableProperty]
        private TorrentStats? _selectedTorrent;

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

        [ObservableProperty]
        private string _saveFeedbackText = string.Empty;

        [ObservableProperty]
        private bool _launchAtStartup;

        // ── Torrents search/filter (parity with macOS v2.1.12) ─────
        [ObservableProperty]
        private string _torrentSearchText = string.Empty;

        [ObservableProperty]
        private ObservableCollection<TorrentStats> _filteredTorrents = new();

        // ── Home dashboard (parity with macOS v2.1.12) ─────────────
        [ObservableProperty]
        private ObservableCollection<TorrentStats> _topTorrents = new();

        // ── Update check (GitHub Releases; replaces macOS Sparkle) ─
        [ObservableProperty]
        private string _updateStatusText = string.Empty;

        [ObservableProperty]
        private bool _updateAvailable;

        private readonly UpdateChecker _updateChecker = new();
        private bool _autoUpdateChecked;

        partial void OnTorrentSearchTextChanged(string value) => ApplyTorrentFilter();

        // ── Computed display properties ────────────────────────────

        /// <summary>App version for the Home hero capsule (e.g. "2.1.15").</summary>
        public string AppVersion => UpdateChecker.CurrentVersion;

        public int TorrentCount => Torrents?.Count ?? 0;
        public int DownloadingCount => Torrents?.Count(t => t.State == TorrentState.Downloading || t.State == TorrentState.DownloadingMetadata) ?? 0;
        public int SeedingCount => Torrents?.Count(t => t.State == TorrentState.Seeding) ?? 0;
        public int PausedCount => Torrents?.Count(t => t.Paused) ?? 0;
        public int HealthIssueCount => HealthIssues?.Count ?? 0;
        public bool HasIncomingConnections => SessionStats?.HasIncomingConnections ?? false;
        public string IncomingStatusText => HasIncomingConnections ? "Incoming OK" : "No Incoming";

        // ── Tray tooltip display helpers (live, poll-updated) ──────
        public string TrayTorrentSummary =>
            $"{TorrentCount} total • {DownloadingCount} dl • {SeedingCount} seed";

        public string TrayListenPort =>
            (SessionStats?.ListenPort ?? 0) == 0 ? "—" : SessionStats!.ListenPort.ToString();

        public string TrayStatusLine =>
            $"{VpnStatusText} • {DiskStatusText}";

        public string DownloadSpeedFormatted =>
            FormatSpeed(SessionStats?.DownloadRate ?? 0);

        public string UploadSpeedFormatted =>
            FormatSpeed(SessionStats?.UploadRate ?? 0);

        public string ConnectionUsageText => SessionStats.ConnectionLimit > 0
            ? $"Peers {SessionStats.NumPeersConnected}/{SessionStats.ConnectionLimit}" : "Peers: waiting";

        public bool VpnConnected =>
            VpnStatus?.IsConnected ?? false;

        public string VpnStatusText =>
            _engine?.NetworkPolicy.RestartRequired == true ? "Network blocked: restart required"
            : VpnStatus == null ? "VPN Off"
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

        // ── Launch at startup via registry ────────────────────────
        private const string StartupRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupValueName = "Controllarr";

        partial void OnLaunchAtStartupChanged(bool value)
        {
            if (ProfilePaths.IsCustom) return;
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(StartupRegistryKey, writable: true);
                if (key == null) return;

                if (value)
                {
                    string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                    if (!string.IsNullOrEmpty(exePath))
                        key.SetValue(StartupValueName, $"\"{exePath}\" --minimized");
                }
                else
                {
                    key.DeleteValue(StartupValueName, throwOnMissingValue: false);
                }

                _logger.Info("UI", value ? "Launch at startup enabled" : "Launch at startup disabled");
            }
            catch (Exception ex)
            {
                _logger.Error("UI", $"Failed to set startup: {ex.Message}");
            }
        }

        private static bool ReadLaunchAtStartup()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(StartupRegistryKey, writable: false);
                return key?.GetValue(StartupValueName) != null;
            }
            catch
            {
                return false;
            }
        }

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
        private async Task SaveSettings()
        {
            if (_store == null) return;
            if (Settings.ListenPortRangeStart == 0 || Settings.ListenPortRangeEnd < Settings.ListenPortRangeStart ||
                Settings.PreferredListenPort == 0 || Settings.WebUIPort is < 1 or > 65535 ||
                Settings.ConnectionLimits.GlobalMaxConnections is < 1 or > 10000 ||
                !Path.IsPathFullyQualified(Settings.DefaultSavePath))
            {
                SaveFeedbackText = "Check port range, preferred port, connection limit (1-10000) and absolute save folder.";
                return;
            }
            try
            {
                Controllarr.Core.Networking.TorrentNetworkPolicy.Validate(Settings);
                if (Settings.GlobalDownloadKBps is < 0 or > 1000000 || Settings.GlobalUploadKBps is < 0 or > 1000000 ||
                    Settings.TorrentQueueing.MaxActiveDownloads < 0 || Settings.TorrentQueueing.MaxActiveSeeds < 0 || Settings.TorrentQueueing.MaxActiveTotal < 0)
                    throw new ArgumentException("Speed and queue limits must be non-negative; speed limits cannot exceed 1000000 KiB/s.");
                if (!string.IsNullOrWhiteSpace(Settings.WatchFolder) && !Path.IsPathFullyQualified(Settings.WatchFolder))
                    throw new ArgumentException("The watch folder must be an absolute path.");
                if (Settings.RssFeeds.Count > 100) throw new ArgumentException("Use at most 100 RSS feeds.");
                foreach (var feed in Settings.RssFeeds) RssParser.Validate(feed);
                foreach (var rule in Settings.BandwidthSchedule)
                    if (rule.StartHour is < 0 or > 23 || rule.EndHour is < 0 or > 23 || rule.StartMinute is < 0 or > 59 || rule.EndMinute is < 0 or > 59 ||
                        rule.MaxDownloadKBps is < 0 or > 1000000 || rule.MaxUploadKBps is < 0 or > 1000000)
                        throw new ArgumentException("Check schedule times and speed limits.");
                _store.ReplaceSettings(Settings);
                var saved = _store.GetSettings();
                if (_engine != null)
                    await Task.Run(() =>
                    {
                        _engine.DefaultSavePath = saved.DefaultSavePath;
                        _engine.ApplyAdvancedSettingsAsync(saved).GetAwaiter().GetResult();
                        _engine.ApplyTuning(saved.ConnectionLimits.GlobalMaxConnections, saved.PeerDiscovery.DhtEnabled, saved.PeerDiscovery.LsdEnabled);
                        _engine.ConfigureQueue(saved.TorrentQueueing);
                        _engine.SetRateLimits(saved.GlobalDownloadKBps, saved.GlobalUploadKBps);
                    });
                _settingsUserModified = false;
                _logger.Info("UI", "Settings saved");
            }
            catch (Exception ex) { SaveFeedbackText = ex.Message; _logger.Error("UI", $"Settings save failed: {ex}"); return; }

            // Show feedback, then clear after 2.5s
            SaveFeedbackText = _engine?.NetworkPolicy.RestartRequired == true ? "Saved. Torrent networking is blocked until you quit and reopen Controllarr." : "Settings saved!";
            if (_engine?.NetworkPolicy.RestartRequired == true) return;
            _ = Task.Run(async () =>
            {
                await Task.Delay(2500);
                Application.Current?.Dispatcher.Invoke(() => SaveFeedbackText = string.Empty);
            });
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
            string url = $"http://127.0.0.1:{settings.WebUIPort}";

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _logger.Error("UI", $"Failed to open WebUI: {ex.Message}");
            }
        }

        // ── Persistent log: Reveal Log File (parity with macOS v2.1.15) ──

        /// <summary>Absolute path of the on-disk log, or null if persistence is off.</summary>
        public string? LogFilePath => _logger.LogFilePath;
        public bool HasLogFile => !string.IsNullOrEmpty(_logger.LogFilePath);

        [RelayCommand]
        private void RevealLogFile()
        {
            string? path = _logger.LogFilePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                MessageBox.Show("No on-disk log file is available yet.",
                    "Controllarr", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                // Open Explorer with the log file selected.
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _logger.Error("UI", $"Failed to reveal log file: {ex.Message}");
            }
        }

        // ── Torrents search/filter ─────────────────────────────────

        [RelayCommand]
        private void ClearTorrentSearch() => TorrentSearchText = string.Empty;

        private void ApplyTorrentFilter()
        {
            var source = Torrents ?? new ObservableCollection<TorrentStats>();
            string q = (TorrentSearchText ?? string.Empty).Trim();

            IEnumerable<TorrentStats> result = source;
            if (q.Length > 0)
            {
                result = source.Where(t =>
                    (t.Name?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (t.Category?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (t.InfoHash?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
            }

            FilteredTorrents = new ObservableCollection<TorrentStats>(result);
        }

        // ── Update check (GitHub Releases) ─────────────────────────

        [RelayCommand]
        private async Task CheckForUpdates()
        {
            UpdateStatusText = "Checking for updates...";
            var result = await _updateChecker.CheckAsync();

            if (result.Error != null)
            {
                UpdateStatusText = $"Update check failed: {result.Error}";
                _logger.Warn("Update", UpdateStatusText);
                return;
            }

            UpdateAvailable = result.UpdateAvailable;
            if (result.UpdateAvailable)
            {
                UpdateStatusText = $"Update available: v{result.LatestVersion} (current v{result.CurrentVersion})";
                _logger.Info("Update", UpdateStatusText);

                var choice = MessageBox.Show(
                    $"A new version is available.\n\nCurrent: v{result.CurrentVersion}\nLatest: v{result.LatestVersion}\n\nOpen the download page?",
                    "Controllarr Update", MessageBoxButton.YesNo, MessageBoxImage.Information);

                if (choice == MessageBoxResult.Yes)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(result.ReleaseUrl) { UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("Update", $"Failed to open release page: {ex.Message}");
                    }
                }
            }
            else
            {
                UpdateStatusText = $"You're up to date (v{result.CurrentVersion}).";
                _logger.Info("Update", UpdateStatusText);
            }
        }

        private void MaybeAutoCheckForUpdates()
        {
            if (_autoUpdateChecked) return;
            _autoUpdateChecked = true;

            if (!(Settings?.UiPreferences?.AutomaticUpdateChecks ?? true)) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await _updateChecker.CheckAsync();
                    if (result.UpdateAvailable && result.Error == null)
                    {
                        Application.Current?.Dispatcher.Invoke(() =>
                        {
                            UpdateAvailable = true;
                            UpdateStatusText = $"Update available: v{result.LatestVersion}";
                        });
                        _logger.Info("Update", $"Update available: v{result.LatestVersion} (current v{result.CurrentVersion})");
                    }
                }
                catch { /* best effort */ }
            });
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
        private void BrowseSavePath()
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select Default Save Path",
                UseDescriptionForTitle = true,
                SelectedPath = Settings.DefaultSavePath
            };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                Settings.DefaultSavePath = dialog.SelectedPath;
                OnPropertyChanged(nameof(Settings));
                _settingsUserModified = true;
            }
        }

        [RelayCommand]
        private void BrowseDiskMonitorPath()
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select Disk Monitor Path",
                UseDescriptionForTitle = true,
                SelectedPath = Settings.DiskSpaceMonitorPath
            };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                Settings.DiskSpaceMonitorPath = dialog.SelectedPath;
                OnPropertyChanged(nameof(Settings));
                _settingsUserModified = true;
            }
        }

        [RelayCommand]
        private void AddArrEndpoint()
        {
            var endpoint = new ArrEndpoint
            {
                Name = $"Endpoint {Settings.ArrEndpoints.Count + 1}",
                Kind = ArrKind.Sonarr,
                BaseURL = "http://localhost:8989",
                ApiKey = ""
            };
            Settings.ArrEndpoints.Add(endpoint);
            OnPropertyChanged(nameof(Settings));
            _settingsUserModified = true;
        }

        [RelayCommand]
        private void RemoveArrEndpoint(ArrEndpoint? endpoint)
        {
            if (endpoint == null) return;
            Settings.ArrEndpoints.Remove(endpoint);
            OnPropertyChanged(nameof(Settings));
            _settingsUserModified = true;
        }

        [RelayCommand]
        private void AddBandwidthRule()
        {
            var rule = new BandwidthRule
            {
                Name = $"Rule {Settings.BandwidthSchedule.Count + 1}",
                Enabled = true,
                DaysOfWeek = new List<int> { 1, 2, 3, 4, 5, 6, 7 },
                StartHour = 22, StartMinute = 0,
                EndHour = 6, EndMinute = 0,
                MaxDownloadKBps = 1024,
                MaxUploadKBps = 512
            };
            Settings.BandwidthSchedule.Add(rule);
            OnPropertyChanged(nameof(Settings));
            _settingsUserModified = true;
        }

        [RelayCommand]
        private void RemoveBandwidthRule(BandwidthRule? rule)
        {
            if (rule == null) return;
            Settings.BandwidthSchedule.Remove(rule);
            OnPropertyChanged(nameof(Settings));
            _settingsUserModified = true;
        }

        [RelayCommand]
        private void AddRecoveryRule()
        {
            var rule = new RecoveryRule
            {
                Enabled = true,
                Trigger = RecoveryTrigger.NoPeers,
                Action = RecoveryAction.Reannounce,
                DelayMinutes = 10
            };
            Settings.RecoveryRules.Add(rule);
            OnPropertyChanged(nameof(Settings));
            _settingsUserModified = true;
        }

        [RelayCommand]
        private void RemoveRecoveryRule(RecoveryRule? rule)
        {
            if (rule == null) return;
            Settings.RecoveryRules.Remove(rule);
            OnPropertyChanged(nameof(Settings));
            _settingsUserModified = true;
        }

        [RelayCommand]
        private void AddCategory()
        {
            var cat = new Category
            {
                Name = $"Category {Categories.Count + 1}",
                SavePath = Settings.DefaultSavePath,
                CreateTorrentSubfolder = true
            };
            Categories.Add(cat);
            SelectedCategory = cat;
            _categoriesUserModified = true;
        }

        [RelayCommand]
        private void RemoveSelectedCategory()
        {
            if (SelectedCategory == null) return;
            var toRemove = SelectedCategory;
            SelectedCategory = null;
            Categories.Remove(toRemove);
            _categoriesUserModified = true;
        }

        [RelayCommand]
        private void BrowseCategorySavePath()
        {
            if (SelectedCategory == null) return;
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select Category Save Path",
                UseDescriptionForTitle = true,
                SelectedPath = SelectedCategory.SavePath ?? string.Empty
            };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                SelectedCategory.SavePath = dialog.SelectedPath;
                OnPropertyChanged(nameof(SelectedCategory));
                _categoriesUserModified = true;
            }
        }

        [RelayCommand]
        private void BrowseCategoryCompletePath()
        {
            if (SelectedCategory == null) return;
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select Category Complete Path",
                UseDescriptionForTitle = true,
                SelectedPath = SelectedCategory.CompletePath ?? string.Empty
            };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                SelectedCategory.CompletePath = dialog.SelectedPath;
                OnPropertyChanged(nameof(SelectedCategory));
                _categoriesUserModified = true;
            }
        }

        [RelayCommand]
        private void MarkCategoriesModified()
        {
            _categoriesUserModified = true;
        }

        [ObservableProperty]
        private string _categorySaveFeedbackText = string.Empty;

        [RelayCommand]
        private void SaveCategories()
        {
            if (_store == null) return;
            if (Categories.Any(c => string.IsNullOrWhiteSpace(c.Name) || string.IsNullOrWhiteSpace(c.SavePath)
                || !Path.IsPathFullyQualified(c.SavePath)
                || (!string.IsNullOrWhiteSpace(c.CompletePath) && !Path.IsPathFullyQualified(c.CompletePath))
                || (c.MaxRatio.HasValue && (!double.IsFinite(c.MaxRatio.Value) || c.MaxRatio < 0))
                || (c.MaxSeedingTimeMinutes.HasValue && c.MaxSeedingTimeMinutes < 0))
                || Categories.Select(c => c.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Categories.Count)
            {
                CategorySaveFeedbackText = "Use unique names, absolute save paths and non-negative limits.";
                return;
            }
            foreach (var category in _store.GetCategories()) _engine?.RegisterBlockedExtensions(Array.Empty<string>(), category.Name);
            foreach (var category in Categories)
            {
                category.Name = category.Name.Trim();
                _engine?.RegisterBlockedExtensions(category.BlockedExtensions.ToArray(), category.Name);
            }
            var renames = _categoryOriginalNames.GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key,
                group => Categories.Contains(group.Last().Key) ? group.Last().Key.Name : null, StringComparer.OrdinalIgnoreCase);
            if (_engine != null)
                foreach (var pair in _engine.SnapshotCategories())
                    if (renames.TryGetValue(pair.Value, out string? name) && name != pair.Value) _engine.SetCategory(name, pair.Key);
            _store.ReplaceCategories(Categories.ToList());
            PersistDesktopCategories();
            string? selectedName = SelectedCategory?.Name;
            Categories = new ObservableCollection<Category>(_store.GetCategories());
            SelectedCategory = Categories.FirstOrDefault(c => c.Name == selectedName);
            _categoriesUserModified = false;
            _logger.Info("UI", "Categories saved");

            CategorySaveFeedbackText = "Category saved!";
            _ = Task.Run(async () =>
            {
                await Task.Delay(2500);
                Application.Current?.Dispatcher.Invoke(() => CategorySaveFeedbackText = string.Empty);
            });
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

                // Enable the persistent crash-surviving on-disk log (v2.1.15).
                _logger.ConfigureFile(Path.Combine(_store.Directory, "logs", "controllarr.log"));

                // Create torrent engine. A user-set preferred forwarded port
                // wins over the last cycled port so it is honored on relaunch.
                ushort port = initialSettings.PreferredListenPort
                              ?? _store.Snapshot().LastKnownGoodPort
                              ?? initialSettings.ListenPortRangeStart;
                _engine = await Task.Run(() => new TorrentEngine(
                    initialSettings.DefaultSavePath,
                    _store.ResumeDirectory,
                    port, initialSettings));
                _engine.CategoryLookup = _store.GetCategory;
                await _engine.ApplyAdvancedSettingsAsync(initialSettings);

                // Apply connection-limit / peer-discovery tuning.
                await Task.Run(() => _engine.ApplyTuning(
                    initialSettings.ConnectionLimits.GlobalMaxConnections,
                    initialSettings.PeerDiscovery.DhtEnabled,
                    initialSettings.PeerDiscovery.LsdEnabled));

                // Restore category map
                var catMap = _store.Snapshot().CategoryByHash;
                _engine.RestoreCategories(catMap);

                _engine.ConfigureQueue(initialSettings.TorrentQueueing);
                _engine.SetRateLimits(initialSettings.GlobalDownloadKBps, initialSettings.GlobalUploadKBps);

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
                _vpnMonitor.Start();
                _diskSpaceMonitor.Start();
                if (DesktopTransferAllowed) await _engine.ResumeAllAsync();
                _rssService = new RssService(_engine, _store, () => DesktopTransferAllowed);
                _rssService.Start();
                _bandwidthScheduler = new BandwidthScheduler(engineAdapter, () => _store.GetSettings().BandwidthSchedule, _logger,
                    () => { var s = _store.GetSettings(); return (s.GlobalDownloadKBps, s.GlobalUploadKBps); });
                _bandwidthScheduler.Start();

                // ── Recovery + *arr services (consumed by the API/WebUI) ──
                _recoveryCenter = new RecoveryCenter(_logger);
                _arrNotifier = new ArrNotifier(() => _engine.SnapshotCategories(), logger: _logger);

                // ── Port watcher: automatic listen-port cycling on stall ──
                _portWatcher = new PortWatcher(_engine, _store, _logger);
                _portWatcher.Start();

                // ── Embedded HTTP server: qBittorrent API + Controllarr API + bundled WebUI ──
                string? webUIRoot = null;
                string bundledWebUI = Path.Combine(AppContext.BaseDirectory, "WebUI");
                string extractedWebUI = Path.Combine(_store.Directory, "WebUI");
                if (Directory.Exists(bundledWebUI) && File.Exists(Path.Combine(bundledWebUI, "index.html")))
                {
                    // Loose dev assets shipped next to the executable.
                    webUIRoot = bundledWebUI;
                }
                else if (ExtractEmbeddedWebUI(extractedWebUI))
                {
                    // Single-file build: assets are embedded in the exe and
                    // extracted to %AppData%\Controllarr\WebUI on boot.
                    webUIRoot = extractedWebUI;
                }

                _httpServer = new ControllarrHttpServer(
                    initialSettings.WebUIHost,
                    initialSettings.WebUIPort,
                    _engine, _store, _logger,
                    _postProcessor, _seedingPolicy, _healthMonitor,
                    _recoveryCenter, _diskSpaceMonitor, _vpnMonitor, _arrNotifier,
                    () => _portWatcher!.ForceCycle("Manual cycle via API"),
                    webUIRoot,
                    // Shutdown-app action invoked by the Web UI's "Shut down" button.
                    () => Application.Current?.Dispatcher.Invoke(() =>
                    {
                        if (Application.Current?.MainWindow is Controllarr.App.MainWindow w)
                            w.ShutdownFromUi();
                        else
                            Application.Current?.Shutdown();
                    }));

                try
                {
                    await _httpServer.StartAsync();
                    _logger.Info("Boot",
                        $"Web UI + API listening on http://{initialSettings.WebUIHost}:{initialSettings.WebUIPort}");
                }
                catch (Exception ex)
                {
                    // A port conflict should not take the whole app down.
                    _logger.Error("Boot", $"HTTP server failed to start: {ex.Message}");
                }

                // Register with App
                if (Application.Current is App app)
                {
                    app.SetRuntime(_store, _engine);

                    // Handle pending command-line magnets/files
                    foreach (var magnet in app.PendingMagnets)
                    {
                        if (!DesktopTransferAllowed) { _logger.Warn("Boot", "Pending magnet not started because the transfer guard is active."); continue; }
                        try { await _engine.AddMagnet(magnet); }
                        catch (Exception ex) { _logger.Error("Boot", $"Failed to add magnet: {ex.Message}"); }
                    }
                    foreach (var file in app.PendingTorrentFiles)
                    {
                        try
                        {
                            if (DesktopTransferAllowed) await _engine.AddTorrentFile(file);
                            else await _engine.ImportTorrentFileAsync(file, initialSettings.DefaultSavePath);
                        }
                        catch (Exception ex) { _logger.Error("Boot", $"Failed to add torrent: {ex.Message}"); }
                    }
                }

                _logger.Info("Boot", "Controllarr started successfully");

                // Load initial settings for UI
                Settings = _store.GetSettings();
                Categories = new ObservableCollection<Category>(_store.GetCategories());
                foreach (var category in Categories) _engine.RegisterBlockedExtensions(category.BlockedExtensions.ToArray(), category.Name);
                Torrents = await Task.Run(_engine.PollStats);
                SessionStats = _engine.GetSessionStats();
                VpnStatus = _vpnMonitor?.Snapshot();
                DiskSpaceStatus = _diskSpaceMonitor?.Snapshot();
                Desktop.ApplySnapshot(Torrents, Categories.Select(c => c.Name));

                // Read startup registry state (without triggering the setter logic)
                _launchAtStartup = ReadLaunchAtStartup();
                OnPropertyChanged(nameof(LaunchAtStartup));

                IsBooting = false;

                // Start polling loop
                StartPolling();

                // Optional automatic update check (GitHub Releases).
                MaybeAutoCheckForUpdates();
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
            if (_pollTask != null) await _pollTask;

            // Stop the HTTP server + port watcher first so no request races shutdown.
            if (_httpServer != null)
            {
                try { await _httpServer.StopAsync(); } catch { /* best effort */ }
            }
            _portWatcher?.Stop();
            _portWatcher?.Dispose();
            if (_rssService != null) await _rssService.StopAsync();
            _bandwidthScheduler?.Dispose();
            _vpnMonitor?.Dispose();
            _diskSpaceMonitor?.Dispose();

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

            _store?.Dispose();
        }

        // ════════════════════════════════════════════════════════════
        // Embedded WebUI extraction
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// Writes the WebUI assets embedded in the executable out to
        /// <paramref name="targetDir"/> so the single-file build can serve them.
        /// Overwrites on each boot so the assets stay in sync with the exe.
        /// Returns true if an index.html is present afterwards.
        /// </summary>
        private bool ExtractEmbeddedWebUI(string targetDir)
        {
            try
            {
                var asm = typeof(MainViewModel).Assembly;
                const string marker = ".WebUI.";
                var names = asm.GetManifestResourceNames()
                    .Where(n => n.Contains(marker, StringComparison.Ordinal))
                    .ToList();

                if (names.Count == 0) return false;

                Directory.CreateDirectory(targetDir);
                foreach (var name in names)
                {
                    int idx = name.IndexOf(marker, StringComparison.Ordinal);
                    string fileName = name.Substring(idx + marker.Length);
                    using var stream = asm.GetManifestResourceStream(name);
                    if (stream == null) continue;
                    string outPath = Path.Combine(targetDir, fileName);
                    using var fs = File.Create(outPath);
                    stream.CopyTo(fs);
                }

                return File.Exists(Path.Combine(targetDir, "index.html"));
            }
            catch (Exception ex)
            {
                _logger.Warn("Boot", $"Failed to extract embedded WebUI: {ex.Message}");
                return false;
            }
        }

        // ════════════════════════════════════════════════════════════
        // Polling loop (2s interval)
        // ════════════════════════════════════════════════════════════

        private void StartPolling()
        {
            _pollCts = new CancellationTokenSource();
            var token = _pollCts.Token;

            _pollTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(2000, token);
                        if (token.IsCancellationRequested) break;

                        await PollAllAsync();
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

        private async Task PollAllAsync()
        {
            if (_engine == null || _store == null) return;

            // Gather all data off the UI thread
            var settings = _store.GetSettings();
            _engine.ConfigureQueue(settings.TorrentQueueing);
            await _engine.TickQueueAsync(DesktopTransferAllowed, _diskSpaceMonitor?.Snapshot().IsPaused == true
                ? "Transfers blocked: low disk space; check the Disk monitor." : null);
            await _engine.CheckpointIfDueAsync();
            _engine.ApplyPendingFileFilters();
            var torrentStats = _engine.PollStats();
            var sessionStats = _engine.GetSessionStats();
            var categories = _store.GetCategories();
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
                ContentPath = s.ContentPath,
                DownloadRateBytes = (int)s.DownloadRate,
                UploadRateBytes = (int)s.UploadRate,
                HasMetadata = s.HasMetadata,
                DownloadedBytes = s.TotalDownload,
                UploadedBytes = s.TotalUpload,
                TotalBytes = s.TotalWanted,
            }).ToList();

            foreach (var view in torrentViews)
            {
                string hash = view.InfoHash;
                view.SetReannounceCallback(() => _engine.RequestReannounce(hash));
            }

            _healthMonitor?.Tick(torrentViews, settings);

            var engineAdapter = new EngineAdapter(_engine);
            _postProcessor?.Tick(torrentViews, categories, engineAdapter);
            _seedingPolicy?.Tick(torrentViews, settings, categories, engineAdapter);

            if (_recoveryCenter != null && _healthMonitor != null && _postProcessor != null && _diskSpaceMonitor != null)
                _recoveryCenter.Tick(_healthMonitor, _postProcessor, _diskSpaceMonitor, settings, engineAdapter);
            if (_arrNotifier != null && _healthMonitor != null)
                _arrNotifier.Tick(_healthMonitor, settings);

            // Update UI on dispatcher thread
            Application.Current?.Dispatcher.Invoke(() =>
            {
                // Torrents
                Torrents = torrentStats;
                Desktop.ApplySnapshot(torrentStats, categories.Select(c => c.Name));

                // Home dashboard: top-8 most active transfers + counts
                TopTorrents = new ObservableCollection<TorrentStats>(
                    torrentStats.OrderByDescending(t => t.DownloadRate)
                                .ThenByDescending(t => t.UploadRate)
                                .Take(8));
                OnPropertyChanged(nameof(TorrentCount));
                OnPropertyChanged(nameof(DownloadingCount));
                OnPropertyChanged(nameof(SeedingCount));
                OnPropertyChanged(nameof(PausedCount));
                OnPropertyChanged(nameof(HasIncomingConnections));
                OnPropertyChanged(nameof(IncomingStatusText));

                // Session stats
                SessionStats = sessionStats;
                OnPropertyChanged(nameof(DownloadSpeedFormatted));
                OnPropertyChanged(nameof(UploadSpeedFormatted));
                OnPropertyChanged(nameof(ConnectionUsageText));

                // Categories (only refresh if user hasn't modified)
                if (!_categoriesUserModified && SelectedTab != "Categories")
                    Categories = new ObservableCollection<Category>(categories);

                // Settings (only refresh if user hasn't modified)
                if (!_settingsUserModified && SelectedTab is not ("Settings" or "Rss"))
                    Settings = settings;

                // Health
                HealthIssues = new ObservableCollection<HealthIssue>(healthIssues);
                OnPropertyChanged(nameof(HealthIssueCount));

                // Post-processor
                if (SelectedTab == "PostProcessor") PostRecords = new ObservableCollection<PostRecord>(postRecords);

                // Seeding
                if (SelectedTab == "Seeding") SeedingLog = new ObservableCollection<SeedEnforcement>(seedingLog);

                // Log entries
                if (SelectedTab == "Log") LogEntries = new ObservableCollection<LogEntry>(logEntries);
                if (SelectedTab == "Rss" && _rssService != null) RssEntries = new(_rssService.Snapshot());

                // Disk space
                DiskSpaceStatus = diskStatus;
                OnPropertyChanged(nameof(DiskPressure));
                OnPropertyChanged(nameof(DiskStatusText));

                // VPN
                VpnStatus = vpnStatus;
                OnPropertyChanged(nameof(VpnConnected));
                OnPropertyChanged(nameof(VpnStatusText));
                OnPropertyChanged(nameof(TorrentNetworkStatusText));

                // Tray hover tooltip (depends on counts, session stats, VPN + disk)
                OnPropertyChanged(nameof(TrayTorrentSummary));
                OnPropertyChanged(nameof(TrayListenPort));
                OnPropertyChanged(nameof(TrayStatusLine));
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
                    ContentPath = s.ContentPath,
                    DownloadRateBytes = (int)s.DownloadRate,
                    UploadRateBytes = (int)s.UploadRate,
                    HasMetadata = s.HasMetadata,
                })
                .ToList();
        }

        public void PauseTorrent(string infoHash) =>
            _engine.Pause(infoHash).GetAwaiter().GetResult();

        public void ResumeTorrent(string infoHash) =>
            _engine.Resume(infoHash).GetAwaiter().GetResult();

        public void RemoveTorrent(string infoHash, bool deleteFiles) =>
            _engine.Remove(infoHash, deleteFiles).GetAwaiter().GetResult();

        public void MoveStorage(string infoHash, string destinationPath)
        {
            if (!_engine.Move(infoHash, destinationPath).GetAwaiter().GetResult())
                throw new IOException("Storage move rejected or failed; see the Storage log.");
        }

        public IReadOnlyList<string>? GetContentFiles(string infoHash) => _engine.GetContentFilePaths(infoHash);

        public void SetRateLimits(int downloadKBps, int uploadKBps) =>
            _engine.SetRateLimits(downloadKBps > 0 ? downloadKBps : null, uploadKBps > 0 ? uploadKBps : null);

        public void BindToAddress(string? ipAddress)
        {
            throw new NotSupportedException("Binding is enforced by the selected-adapter socket policy, not listen-address overrides.");
        }
        public bool SupportsInterfaceBinding => true;
        public bool TorrentNetworkAllowed => _engine.NetworkPolicy.Allowed;
        public bool NetworkRestartRequired => _engine.NetworkPolicy.RestartRequired;
        public string? BoundVpnAddress => _engine.NetworkPolicy.RequiresVpn ? _engine.NetworkPolicy.Adapter?.Address.ToString() : null;
        public void RefreshNetworkPolicy(Settings settings) => _engine.ApplyAdvancedSettingsAsync(settings).GetAwaiter().GetResult();

        public void Reannounce(string infoHash) =>
            _engine.RequestReannounce(infoHash);
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
