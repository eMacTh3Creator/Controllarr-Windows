using System.Diagnostics;
using MonoTorrent.Client;

namespace Controllarr.Core.Engine;

public sealed partial class TorrentEngine
{
    private long _lastCheckpointAt;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _checkingHashes = new(StringComparer.OrdinalIgnoreCase);

    private void RestoreResumeCheckpoints()
    {
        foreach (var manager in _managersByHash.Values)
        {
            try
            {
                string path = _engine.Settings.GetFastResumePath(manager.InfoHashes);
                if (manager.HasMetadata && File.Exists(path) && FastResume.TryLoad(path, out var data))
                    manager.LoadFastResumeAsync(data!).GetAwaiter().GetResult();
            }
            catch (Exception ex) { Services.Logger.Instance.Warn("Engine", $"Resume cache for {manager.Name} ignored; files will be checked: {ex.Message}"); }
        }
    }

    // Called only under _stateSaveGate. No library-owned writer targets these files.
    private async Task WriteResumeCheckpointsAsync()
    {
        foreach (var manager in _managersByHash.Values)
        {
            if (_checkingHashes.ContainsKey(manager.InfoHashes.V1OrV2.ToHex()) || !manager.HasMetadata || !manager.HashChecked || manager.State is
                MonoTorrent.Client.TorrentState.Hashing or MonoTorrent.Client.TorrentState.HashingPaused) continue;
            try
            {
                byte[] bytes = (await manager.SaveFastResumeAsync()).Encode();
                string path = _engine.Settings.GetFastResumePath(manager.InfoHashes);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path + ".tmp", bytes);
                File.Move(path + ".tmp", path, overwrite: true);
            }
            catch (Exception ex) { Services.Logger.Instance.Warn("Engine", $"Resume checkpoint for {manager.Name}: {ex.Message}"); }
        }
        _lastCheckpointAt = Stopwatch.GetTimestamp();
    }

    public async Task CheckpointIfDueAsync()
    {
        if (_disposed || Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastCheckpointAt)) < TimeSpan.FromSeconds(30)
            || !await _stateSaveGate.WaitAsync(0)) return;
        try { await WriteResumeCheckpointsAsync(); }
        finally { _stateSaveGate.Release(); }
    }

    private async Task InvalidateResumeCheckpointAsync(TorrentManager manager)
    {
        await _stateSaveGate.WaitAsync();
        try { File.Delete(_engine.Settings.GetFastResumePath(manager.InfoHashes)); }
        finally { _stateSaveGate.Release(); }
    }
}
