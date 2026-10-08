using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Controllarr.Core.Desktop;
using Controllarr.Core.Engine;
using Controllarr.Core.Persistence;
using Controllarr.Core.Services;
using Controllarr.App.Views;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using FileInfo = Controllarr.Core.Engine.FileInfo;

namespace Controllarr.App.ViewModels;

public partial class DesktopViewModel : ObservableObject
{
    private readonly MainViewModel _runtime;
    private readonly TorrentCatalog _catalog = new();
    private readonly DispatcherTimer _searchTimer;
    private CancellationTokenSource? _batchCancellation;
    private int _detailGeneration;
    private readonly SemaphoreSlim _detailGate = new(1, 1);
    private bool _stopping;
    private string? _categoryFilter;
    private IReadOnlyList<TorrentRow> _selectedRows = Array.Empty<TorrentRow>();
    private TorrentQuery _query = new();
    public event Action? ViewUpdating;
    public event Action? ViewUpdated;
    public ICollectionView RowsView { get; }
    public ObservableCollection<string> CategoryNames { get; } = new();
    public Array StatusFilters { get; } = Enum.GetValues(typeof(TorrentFilter));
    public IReadOnlyList<TorrentRow> SelectedRows => _selectedRows;
    public bool HasSelection => _selectedRows.Count > 0 && CanOperate;
    public bool CanOperate => _runtime.DesktopEngine != null && !IsBusy && !_stopping;
    public bool CanStartTransfers => CanOperate && _runtime.DesktopTransferAllowed && _runtime.DiskSpaceStatus?.IsPaused != true;
    public bool CanResumeSelection => HasSelection && CanStartTransfers;
    public int TotalCount => _catalog.Rows.Count;
    public int VisibleCount => RowsView.Cast<object>().Count();
    public string SelectionSummary => $"{_selectedRows.Count:N0} selected / {VisibleCount:N0} shown / {TotalCount:N0} total";
    public bool HasCategoryFilter => _categoryFilter != null;
    public string ScopeTitle => _categoryFilter == null ? "All torrents" : _categoryFilter.Length == 0 ? "Uncategorized" : _categoryFilter;

    [ObservableProperty] private string _search = string.Empty;
    [ObservableProperty] private TorrentFilter _statusFilter;
    [ObservableProperty] private TorrentRow? _selectedRow;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _operationStatus = "Ready";
    [ObservableProperty] private double _taskbarProgress;
    [ObservableProperty] private System.Windows.Shell.TaskbarItemProgressState _taskbarState;
    [ObservableProperty] private ObservableCollection<FileChoice> _files = new();
    [ObservableProperty] private IReadOnlyList<TrackerInfo> _trackers = Array.Empty<TrackerInfo>();
    [ObservableProperty] private IReadOnlyList<PeerInfo> _peers = Array.Empty<PeerInfo>();
    [ObservableProperty] private string _detailStatus = "Select one torrent to inspect its files, trackers and peers.";

    public DesktopViewModel(MainViewModel runtime)
    {
        _runtime = runtime;
        RowsView = new ListCollectionView(_catalog.Rows) { Filter = item => _query.Matches((TorrentRow)item) };
        RowsView.SortDescriptions.Add(new SortDescription(nameof(TorrentRow.Name), ListSortDirection.Ascending));
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); RefreshFilter(); };
    }

    partial void OnSearchChanged(string value) { _searchTimer.Stop(); _searchTimer.Start(); }
    partial void OnStatusFilterChanged(TorrentFilter value) => RefreshFilter();
    partial void OnSelectedRowChanged(TorrentRow? value) => _ = LoadDetailsAsync();
    partial void OnIsBusyChanged(bool value) => NotifyCommands();

    public void ApplySnapshot(IReadOnlyList<TorrentStats> snapshot, IEnumerable<string> categories)
    {
        ViewUpdating?.Invoke();
        bool membershipChanged = _catalog.Reconcile(snapshot);
        if (membershipChanged || StatusFilter == TorrentFilter.Active) RowsView.Refresh();
        ViewUpdated?.Invoke();
        // Keep the category list stable too; do not replace the selected category on each poll.
        var names = categories.Concat(snapshot.Select(s => s.Category ?? "")).Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!CategoryNames.SequenceEqual(names))
        {
            CategoryNames.Clear();
            foreach (var name in names) CategoryNames.Add(name);
        }
        var downloading = snapshot.Where(s => !s.Paused && s.Progress < 1 && s.TotalWanted > 0).ToArray();
        double total = downloading.Sum(s => (double)s.TotalWanted);
        TaskbarProgress = total > 0 ? Math.Clamp(downloading.Sum(s => (double)s.TotalDone) / total, 0, 1) : 0;
        TaskbarState = total > 0 ? System.Windows.Shell.TaskbarItemProgressState.Normal : System.Windows.Shell.TaskbarItemProgressState.None;
        NotifyCommands();
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(SelectionSummary));
    }

    public void SetSelection(IEnumerable<TorrentRow> rows)
    {
        _selectedRows = rows.Where(_query.Matches).ToArray();
        SelectedRow = _selectedRows.Count == 1 ? _selectedRows[0] : null;
        OnPropertyChanged(nameof(SelectionSummary));
        NotifyCommands();
    }

    public void SetCategoryFilter(string? category)
    {
        _categoryFilter = category;
        _runtime.SelectedTab = "Torrents";
        OnPropertyChanged(nameof(ScopeTitle));
        OnPropertyChanged(nameof(HasCategoryFilter));
        RefreshFilter();
    }

    private void RefreshFilter()
    {
        _query = new TorrentQuery(Search, _categoryFilter, StatusFilter);
        ViewUpdating?.Invoke();
        RowsView.Refresh();
        ViewUpdated?.Invoke();
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(SelectionSummary));
    }

    [RelayCommand] private void ClearFilters()
    {
        Search = "";
        StatusFilter = TorrentFilter.All;
        SetCategoryFilter(null);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task PauseSelected() => RunBatchAsync("Pause", _selectedRows, hash => _runtime.DesktopEngine!.Pause(hash));
    [RelayCommand(CanExecute = nameof(CanResumeSelection))]
    private Task ResumeSelected() => RunBatchAsync("Resume", _selectedRows, SafeResumeAsync);
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task ReannounceSelected() => RunBatchAsync("Reannounce", _selectedRows, hash => _runtime.DesktopEngine!.Reannounce(hash));
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RecheckSelected()
    {
        var targets = _selectedRows.ToArray();
        if (MessageBox.Show(Application.Current.MainWindow,
            $"Recheck files for {targets.Length:N0} torrents? Checks run one at a time and may take a while. Torrents remain paused afterwards; use Resume when ready.",
            "Verify downloaded files", MessageBoxButton.OKCancel, MessageBoxImage.Information) == MessageBoxResult.OK)
            await RunBatchAsync("Recheck files", targets, hash => _runtime.DesktopEngine!.Recheck(hash));
    }
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task PauseAll() => RunBatchAsync("Pause all", _catalog.Rows, hash => _runtime.DesktopEngine!.Pause(hash));
    [RelayCommand(CanExecute = nameof(CanStartTransfers))]
    private Task ResumeAll() => RunBatchAsync("Resume all", _catalog.Rows, SafeResumeAsync);

    private Task<bool> SafeResumeAsync(string hash) => _runtime.DesktopTransferAllowed && _runtime.DiskSpaceStatus?.IsPaused != true
        ? _runtime.DesktopEngine!.Resume(hash) : Task.FromResult(false);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RemoveSelected()
    {
        var selected = _selectedRows.ToArray();
        bool? deleteFiles = DesktopDialogs.ConfirmRemoval(selected);
        if (deleteFiles == null) return;
        bool reporting = true;
        var progress = new Progress<RemovalProgress>(p =>
        {
            if (reporting) OperationStatus = $"{p.Stage}: {p.Completed:N0} / {p.Total:N0}";
        });
        try
        {
            await RunBatchAsync(deleteFiles.Value ? "Delete files and remove" : "Remove (keep files)", selected,
                _ => Task.FromResult(false), async (hashes, cancellation) =>
                {
                    try { return await _runtime.DesktopEngine!.RemoveManyAsync(hashes, deleteFiles.Value, progress, cancellation); }
                    finally { reporting = false; }
                }, checkpointResume: false);
        }
        finally { reporting = false; }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task AssignCategory()
    {
        var selected = _selectedRows.ToArray();
        var category = DesktopDialogs.ChooseCategory(CategoryNames);
        if (category == null) return;
        var policy = _runtime.Settings.CategoryChangeMove;
        string? destination = _runtime.Categories.FirstOrDefault(c => c.Name == category)?.SavePath;
        bool move = !string.IsNullOrWhiteSpace(destination) && (policy == CategoryChangeMove.Always ||
            (policy == CategoryChangeMove.Ask && MessageBox.Show(Application.Current.MainWindow,
                $"Move {selected.Length:N0} torrents to the category save folder? Existing files will not be overwritten.",
                "Move category storage", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes));
        await RunBatchAsync("Assign category", selected, async hash =>
        {
            _runtime.DesktopEngine!.SetCategory(category, hash);
            return !move || await _runtime.DesktopEngine.Move(hash, destination!);
        });
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task MoveSelected()
    {
        var selected = _selectedRows.ToArray();
        using var picker = new System.Windows.Forms.FolderBrowserDialog { Description = "Move selected torrent files to this folder", UseDescriptionForTitle = true };
        if (picker.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        if (MessageBox.Show(Application.Current.MainWindow,
                $"Move files for {selected.Length:N0} torrents to:\n{picker.SelectedPath}\n\nTorrent subfolders are preserved. Existing destination files are not overwritten. This can take time.",
                "Move torrent storage", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        await RunBatchAsync("Move storage", selected, hash => _runtime.DesktopEngine!.Move(hash, picker.SelectedPath));
    }

    [RelayCommand] private void CancelBatch() => _batchCancellation?.Cancel();

    public async Task WaitForIdleAsync()
    {
        _stopping = true;
        NotifyCommands();
        _batchCancellation?.Cancel();
        while (IsBusy) await Task.Delay(50);
        await _detailGate.WaitAsync();
        _detailGate.Release();
    }

    private async Task RunBatchAsync(string action, IEnumerable<TorrentRow> rows, Func<string, Task<bool>> operation,
        Func<string[], CancellationToken, Task<BatchResult>>? batchOperation = null, bool checkpointResume = true)
    {
        if (!CanOperate) return;
        string[] hashes = rows.Select(r => r.InfoHash).ToArray();
        if (hashes.Length == 0) return;
        IsBusy = true;
        using var cancellation = new CancellationTokenSource();
        _batchCancellation = cancellation;
        OperationStatus = $"{action}: 0 / {hashes.Length:N0}";
        var progress = new Progress<int>(count => OperationStatus = $"{action}: {count:N0} / {hashes.Length:N0}");
        try
        {
            var result = await Task.Run(() => batchOperation != null ? batchOperation(hashes, cancellation.Token)
                : TorrentBatch.RunAsync(hashes, operation, progress, cancellation.Token));
            _runtime.PersistDesktopCategories();
            OperationStatus = $"{action}: {result.Succeeded:N0} succeeded, {result.Failures.Count:N0} failed" + (result.Cancelled ? " (cancelled)" : "");
            foreach (var failure in result.Failures) Logger.Instance.Warn("Desktop", $"{action} {failure.InfoHash}: {failure.Message}");
            if (result.Failures.Count > 0)
                MessageBox.Show(Application.Current.MainWindow,
                    OperationStatus + "\n\n" + string.Join("\n", result.Failures.Take(8).Select(f => $"{f.InfoHash}: {f.Message}")) + "\n\nSee Log for the full list.",
                    "Some torrents could not be changed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            OperationStatus = $"{action} failed: {ex.Message}";
            Logger.Instance.Error("Desktop", OperationStatus);
        }
        finally
        {
            try { if (_runtime.DesktopEngine != null) await _runtime.DesktopEngine.SaveEngineStateAsync(checkpointResume); }
            finally { _batchCancellation = null; IsBusy = false; }
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartTransfers))]
    private async Task AddMagnet()
    {
        var request = DesktopDialogs.AddMagnet(CategoryNames, _runtime.Settings.DefaultSavePath);
        if (request == null || !CanStartTransfers) return;
        var category = _runtime.Categories.FirstOrDefault(c => c.Name == request.Category);
        string path = string.IsNullOrWhiteSpace(request.SavePath) ? category?.SavePath ?? _runtime.Settings.DefaultSavePath : request.SavePath;
        IsBusy = true;
        try
        {
            await Task.Run(() => _runtime.DesktopEngine!.AddMagnet(request.Uri, request.Category, path));
            _runtime.PersistDesktopCategories();
            OperationStatus = "Magnet added";
        }
        catch (Exception ex) { ShowError("Could not add magnet", ex); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanStartTransfers))]
    private async Task AddFiles()
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Torrent files (*.torrent)|*.torrent", Multiselect = true };
        if (picker.ShowDialog(Application.Current.MainWindow) == true) await AddDroppedFilesAsync(picker.FileNames);
    }

    public async Task AddDroppedFilesAsync(IEnumerable<string> paths)
    {
        if (!CanStartTransfers) return;
        var files = paths.Where(p => string.Equals(System.IO.Path.GetExtension(p), ".torrent", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (files.Length == 0) return;
        IsBusy = true;
        int added = 0;
        string? category = _categoryFilter;
        string? savePath = _runtime.Categories.FirstOrDefault(c => c.Name == category)?.SavePath;
        var failures = new List<string>();
        using var cancellation = new CancellationTokenSource();
        _batchCancellation = cancellation;
        try
        {
            foreach (var path in files)
            {
                if (cancellation.IsCancellationRequested || !_runtime.DesktopTransferAllowed || _runtime.DiskSpaceStatus?.IsPaused == true) break;
                try
                {
                    await Task.Run(() => _runtime.DesktopEngine!.AddTorrentFile(path, category, savePath, persist: false));
                    added++;
                    if (added % 25 == 0) await _runtime.DesktopEngine!.SaveEngineStateAsync();
                    OperationStatus = $"Adding torrent files: {added:N0} / {files.Length:N0}";
                }
                catch (Exception ex)
                {
                    failures.Add($"{System.IO.Path.GetFileName(path)}: {ex.Message}");
                    Logger.Instance.Warn("Desktop", $"Add file {path}: {ex.Message}");
                }
            }
            _runtime.PersistDesktopCategories();
            OperationStatus = $"Added {added:N0} / {files.Length:N0} torrent files";
            if (failures.Count > 0) MessageBox.Show(Application.Current.MainWindow,
                string.Join("\n", failures.Take(8)) + "\n\nSee Log for all failed files.", "Some torrent files could not be added", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            try { await _runtime.DesktopEngine!.SaveEngineStateAsync(); }
            finally { _batchCancellation = null; IsBusy = false; }
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenFolder()
    {
        foreach (var path in _selectedRows.Select(r => r.SavePath).Distinct().Take(10))
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { ShowError("Could not open folder", ex); }
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyMagnets()
    {
        try
        {
            System.Windows.Clipboard.SetText(string.Join(Environment.NewLine,
                _selectedRows.Select(r => $"magnet:?xt=urn:{(r.InfoHash.Length == 64 ? "btmh:1220" : "btih:")}{r.InfoHash}&dn={Uri.EscapeDataString(r.Name)}")));
        }
        catch (Exception ex) { ShowError("Could not copy magnet links", ex); }
    }

    [RelayCommand] private Task RefreshDetails() => LoadDetailsAsync();

    private async Task LoadDetailsAsync()
    {
        int generation = ++_detailGeneration;
        var row = SelectedRow;
        var engine = _runtime.DesktopEngine;
        Files.Clear(); Trackers = Array.Empty<TrackerInfo>(); Peers = Array.Empty<PeerInfo>();
        if (row == null || engine == null || _stopping)
        {
            DetailStatus = "Select one torrent to inspect its files, trackers and peers.";
            return;
        }
        DetailStatus = "Loading details...";
        try
        {
            await _detailGate.WaitAsync();
            if (generation != _detailGeneration || _stopping) { _detailGate.Release(); return; }
            (FileInfo[]?, TrackerInfo[]?, PeerInfo[]?) details;
            try { details = await Task.Run(() => (engine.GetFileInfo(row.InfoHash), engine.GetTrackers(row.InfoHash), engine.GetPeers(row.InfoHash))); }
            finally { _detailGate.Release(); }
            if (generation != _detailGeneration) return;
            Files = new ObservableCollection<FileChoice>((details.Item1 ?? Array.Empty<FileInfo>()).Select(f => new FileChoice(f)));
            Trackers = details.Item2 ?? Array.Empty<TrackerInfo>();
            Peers = details.Item3 ?? Array.Empty<PeerInfo>();
            DetailStatus = $"{row.Name} | {row.InfoHash} | {row.SavePath}";
        }
        catch (Exception ex) { if (generation == _detailGeneration) DetailStatus = ex.Message; }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task SaveFilePriorities()
    {
        if (SelectedRow == null || Files.Count == 0 || !CanOperate) return;
        string hash = SelectedRow.InfoHash;
        int[] priorities = Files.OrderBy(f => f.Index).Select(f => f.Priority).ToArray();
        IsBusy = true;
        try
        {
            bool success = await Task.Run(() => _runtime.DesktopEngine!.SetFilePriorities(priorities, hash));
            if (success) await _runtime.DesktopEngine!.SaveEngineStateAsync();
            OperationStatus = success ? "File priorities saved" : "Could not save file priorities; refresh details and try again.";
        }
        catch (Exception ex) { ShowError("Could not save file priorities", ex); }
        finally { IsBusy = false; }
    }

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(CanOperate));
        OnPropertyChanged(nameof(CanStartTransfers)); OnPropertyChanged(nameof(CanResumeSelection));
        PauseSelectedCommand.NotifyCanExecuteChanged(); ResumeSelectedCommand.NotifyCanExecuteChanged();
        ReannounceSelectedCommand.NotifyCanExecuteChanged(); RemoveSelectedCommand.NotifyCanExecuteChanged();
        RecheckSelectedCommand.NotifyCanExecuteChanged();
        AssignCategoryCommand.NotifyCanExecuteChanged(); MoveSelectedCommand.NotifyCanExecuteChanged();
        OpenFolderCommand.NotifyCanExecuteChanged(); CopyMagnetsCommand.NotifyCanExecuteChanged();
        PauseAllCommand.NotifyCanExecuteChanged(); ResumeAllCommand.NotifyCanExecuteChanged();
        AddMagnetCommand.NotifyCanExecuteChanged(); AddFilesCommand.NotifyCanExecuteChanged();
        SaveFilePrioritiesCommand.NotifyCanExecuteChanged();
        MoveQueueCommand.NotifyCanExecuteChanged(); ForceStartSelectedCommand.NotifyCanExecuteChanged();
        ClearForceStartCommand.NotifyCanExecuteChanged(); SetSpeedLimitsCommand.NotifyCanExecuteChanged();
        SetAdvancedControlsCommand.NotifyCanExecuteChanged();
        PreviewFileCommand.NotifyCanExecuteChanged();
        EditTrackersCommand.NotifyCanExecuteChanged(); CreateTorrentCommand.NotifyCanExecuteChanged(); ImportMigrationCommand.NotifyCanExecuteChanged();
    }

    private void ShowError(string title, Exception ex)
    {
        OperationStatus = ex.Message;
        Logger.Instance.Error("Desktop", $"{title}: {ex}");
        MessageBox.Show(Application.Current.MainWindow, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

public partial class FileChoice : ObservableObject
{
    public FileChoice(FileInfo file) { Index = file.Index; Name = file.Name; Size = file.Size; _priority = file.Priority; }
    public int Index { get; }
    public string Name { get; }
    public long Size { get; }
    [ObservableProperty] private int _priority;
    public static int[] Priorities { get; } = { 0, 1, 2, 3, 5, 6, 7 };
}
