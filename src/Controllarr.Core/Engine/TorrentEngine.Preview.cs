using System.Collections.Concurrent;
using MonoTorrent.Client;
using MonoTorrent.PiecePicking;
using Controllarr.Core.Services;

namespace Controllarr.Core.Engine;

public sealed partial class TorrentEngine
{
    private readonly ConcurrentDictionary<string, TorrentPreview> _previews = new(StringComparer.OrdinalIgnoreCase);

    public async Task<TorrentPreview> CreatePreviewAsync(string hash, int fileIndex)
    {
        await _queueGate.WaitAsync();
        try
        {
            if (!NetworkPolicy.Allowed) throw new InvalidOperationException("The torrent network guard is active.");
            if (_previews.Count >= 4 || _previews.ContainsKey(hash)) throw new InvalidOperationException("Close an existing preview first (one per torrent, four total).");
            var manager = FindManager(hash) ?? throw new ArgumentException("Torrent not found.");
            if (manager.State is not (MonoTorrent.Client.TorrentState.Downloading or MonoTorrent.Client.TorrentState.Seeding))
                throw new InvalidOperationException("Resume this torrent and wait for metadata/file checks before previewing a file.");
            if (fileIndex < 0 || fileIndex >= manager.Files.Count) throw new ArgumentOutOfRangeException(nameof(fileIndex));
            var file = manager.Files[fileIndex];
            if (file.Priority == MonoTorrent.Priority.DoNotDownload) throw new InvalidOperationException("Apply a download priority to this file before previewing it.");
            await StopManagerAsync(manager);
            try
            {
                await manager.ChangePickerAsync(new StreamingPieceRequester());
                await StartNetworkGuardedAsync(manager);
                var starting = System.Diagnostics.Stopwatch.StartNew();
                while (manager.State == MonoTorrent.Client.TorrentState.Starting && starting.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
                if (manager.State is not (MonoTorrent.Client.TorrentState.Downloading or MonoTorrent.Client.TorrentState.Seeding))
                    throw new InvalidOperationException("Torrent is not ready for preview; wait for checks or network recovery.");
                var preview = await TorrentPreview.StartAsync(file.Path, async token =>
                {
                    if (!NetworkPolicy.Allowed || manager.State is not (MonoTorrent.Client.TorrentState.Downloading or MonoTorrent.Client.TorrentState.Seeding))
                        throw new InvalidOperationException("Torrent is not active or the network guard is engaged.");
                    return await manager.StreamProvider!.CreateStreamAsync(file, prebuffer: false, token);
                }, () => FinishPreviewAsync(hash, manager));
                _previews[hash] = preview;
                return preview;
            }
            catch
            {
                await StopManagerAsync(manager);
                await SetNormalPickerAsync(manager, OptionsFor(hash).Sequential);
                if (!_pausedHashes.ContainsKey(hash)) await StartManagedAsync(manager);
                throw;
            }
        }
        finally { _queueGate.Release(); }
    }

    private async Task FinishPreviewAsync(string hash, TorrentManager manager)
    {
        await _queueGate.WaitAsync();
        try
        {
            _previews.TryRemove(hash, out _);
            if (_disposed || FindManager(hash) != manager) return;
            bool active = manager.State is MonoTorrent.Client.TorrentState.Downloading or MonoTorrent.Client.TorrentState.Seeding;
            await StopManagerAsync(manager);
            await SetNormalPickerAsync(manager, OptionsFor(hash).Sequential);
            if (active && !_pausedHashes.ContainsKey(hash)) await StartManagedAsync(manager);
        }
        finally { _queueGate.Release(); }
    }
}
