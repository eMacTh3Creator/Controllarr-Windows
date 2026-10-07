using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Controllarr.App.Views;
using Controllarr.Core.Desktop;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace Controllarr.App.ViewModels;

public partial class DesktopViewModel
{
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task PreviewFile()
    {
        if (_selectedRows.Count != 1) { OperationStatus = "Select exactly one running torrent to preview a file."; return; }
        string hash = _selectedRows[0].InfoHash;
        int? index = DesktopDialogs.ChoosePreviewFile(_runtime.DesktopEngine!.GetFileInfo(hash) ?? Array.Empty<Controllarr.Core.Engine.FileInfo>());
        if (index == null) return;
        IsBusy = true;
        try
        {
            await using var preview = await _runtime.DesktopEngine.CreatePreviewAsync(hash, index.Value);
            DesktopDialogs.ShowPreview(preview);
            OperationStatus = "Preview stopped; normal torrent controls restored";
        }
        catch (Exception ex) { ShowError("Could not preview this file", ex); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task SetAdvancedControls()
    {
        var controls = DesktopDialogs.AdvancedOptions(_runtime.DesktopEngine!.GetOptions(_selectedRows[0].InfoHash));
        if (controls == null) return;
        await RunCheckpointedAsync("Advanced controls", hash => _runtime.DesktopEngine!.SetAdvancedOptionsAsync(hash,
            controls.Value.Connections, controls.Value.Slots, controls.Value.Sequential, persist: false));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task MoveQueue(string direction)
    {
        try
        {
            _runtime.DesktopEngine!.MoveQueue(_selectedRows.Select(r => r.InfoHash), direction);
            await _runtime.DesktopEngine.SaveEngineStateAsync();
            OperationStatus = "Queue positions saved";
        }
        catch (Exception ex) { ShowError("Could not move queue positions", ex); }
    }

    [RelayCommand(CanExecute = nameof(CanResumeSelection))]
    private Task ForceStartSelected() => RunCheckpointedAsync("Force start", hash => _runtime.DesktopTransferAllowed
        ? _runtime.DesktopEngine!.SetForceStartAsync(hash, true, persist: false) : Task.FromResult(false));

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task ClearForceStart() => RunCheckpointedAsync("Return to queue", hash => _runtime.DesktopEngine!.SetForceStartAsync(hash, false, persist: false));

    private Task RunCheckpointedAsync(string action, Func<string, Task<bool>> operation)
    {
        int changed = 0;
        return RunBatchAsync(action, _selectedRows, async hash =>
        {
            bool success = await operation(hash);
            if (success && ++changed % 25 == 0) await _runtime.DesktopEngine!.SaveEngineStateAsync();
            return success;
        });
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task SetSpeedLimits()
    {
        var rows = _selectedRows.ToArray();
        var limits = DesktopDialogs.SpeedLimits(_runtime.DesktopEngine!.GetOptions(rows[0].InfoHash));
        if (limits == null) return;
        await RunCheckpointedAsync("Set speed limits", hash => _runtime.DesktopEngine!.SetTorrentLimitsAsync(hash, limits.Value.Download, limits.Value.Upload, persist: false));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task EditTrackers()
    {
        if (_selectedRows.Count != 1) { OperationStatus = "Select exactly one torrent to edit trackers."; return; }
        string hash = _selectedRows[0].InfoHash;
        var trackers = DesktopDialogs.EditTrackers(_runtime.DesktopEngine!.GetTrackers(hash)?.Select(t => t.Url) ?? Array.Empty<string>());
        if (trackers == null) return;
        await RunBatchAsync("Edit trackers", _selectedRows, h => _runtime.DesktopEngine.ReplaceTrackersAsync(h, trackers));
        await LoadDetailsAsync();
    }

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task CreateTorrent()
    {
        var request = DesktopDialogs.CreateTorrent();
        if (request == null) return;
        IsBusy = true;
        using var cancellation = new CancellationTokenSource();
        _batchCancellation = cancellation;
        var progress = new Progress<double>(value => OperationStatus = $"Creating torrent: {value:F1}% hashed (Cancel stops hashing)");
        try
        {
            await Task.Run(() => TorrentTools.CreateAsync(request.Source, request.Destination, request.Trackers, request.Private,
                request.Comment, request.Hybrid, progress, cancellation.Token));
            OperationStatus = $"Created {Path.GetFileName(request.Destination)}";
            MessageBox.Show(Application.Current.MainWindow, $"Torrent created:\n{request.Destination}\n\nAdd it using Add torrent file when you are ready to seed.", "Torrent created", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { OperationStatus = "Torrent creation cancelled"; }
        catch (Exception ex) { ShowError("Could not create torrent", ex); }
        finally { _batchCancellation = null; IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task ImportMigration()
    {
        var request = DesktopDialogs.ImportMigration(_runtime.Settings.DefaultSavePath);
        if (request == null) return;
        IsBusy = true;
        using var cancellation = new CancellationTokenSource();
        _batchCancellation = cancellation;
        try
        {
            var entries = await Task.Run(() => TorrentTools.ReadMigration(request.MetadataPath, request.SavePath));
            if (entries.Count == 0) { OperationStatus = "No torrent metadata found"; return; }
            if (MessageBox.Show(Application.Current.MainWindow, $"Import {entries.Count:N0} torrents, paused?\n\nExisting library entries are kept unchanged. Source metadata and downloads are not deleted. Select imported torrents and use Recheck files before resuming. Peer history, passwords and old client's queue state are not imported.",
                    "Confirm migration", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            int added = 0, failed = 0;
            await Task.Run(async () =>
            {
                foreach (var entry in entries)
                {
                    if (cancellation.IsCancellationRequested) break;
                    try
                    {
                        await _runtime.DesktopEngine!.ImportTorrentFileAsync(entry.TorrentFile, entry.SavePath, entry.Category, persist: false);
                        added++;
                        if (added % 25 == 0) await _runtime.DesktopEngine.SaveEngineStateAsync();
                    }
                    catch (Exception ex) { failed++; Controllarr.Core.Services.Logger.Instance.Warn("Migration", $"{Path.GetFileName(entry.TorrentFile)}: {ex.Message}"); }
                    Application.Current.Dispatcher.Invoke(() => OperationStatus = $"Import: {added:N0} processed, {failed:N0} failed / {entries.Count:N0}");
                }
            });
            _runtime.PersistDesktopCategories();
            OperationStatus = $"Import complete: {added:N0} processed, {failed:N0} failed" + (cancellation.IsCancellationRequested ? " (cancelled)" : "");
        }
        catch (Exception ex) { ShowError("Could not import library", ex); }
        finally
        {
            try { await _runtime.DesktopEngine!.SaveEngineStateAsync(); }
            finally { _batchCancellation = null; IsBusy = false; }
        }
    }
}
