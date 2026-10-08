using Controllarr.Core.Persistence;
using MonoTorrent.Client;

namespace Controllarr.Core.Engine;

public sealed partial class TorrentEngine
{
    public Func<string, Category?>? CategoryLookup { get; set; }
    private bool _createTorrentSubfolders;

    public string[] GetContentFilePaths(string hash) => FindManager(hash)?.Files
        .Where(f => f.Priority != MonoTorrent.Priority.DoNotDownload)
        .Select(f => f.DownloadCompleteFullPath).ToArray() ?? Array.Empty<string>();

    private string ResolveIntakePath(string? explicitPath, string? category, string? name, string hash, out string? folder, bool? createSubfolder = null)
    {
        var policy = string.IsNullOrEmpty(category) ? null : CategoryLookup?.Invoke(category);
        string root = ResolveSavePath(!string.IsNullOrWhiteSpace(explicitPath) ? explicitPath : policy?.SavePath);
        folder = (createSubfolder ?? policy?.CreateTorrentSubfolder ?? _createTorrentSubfolders) ? StoragePaths.TorrentFolder(name, hash) : null;
        string path = folder == null ? root : StoragePaths.Child(root, folder);
        Directory.CreateDirectory(path);
        return path;
    }

    public async Task<bool> RepairContentLayoutAsync(string hash)
    {
        var manager = FindManager(hash);
        if (manager == null || !manager.HasMetadata) return false;
        if (OptionsFor(hash).StorageSubfolder != null || !StoragePaths.Equal(manager.SavePath, manager.ContainingDirectory)) return true;
        return await Move(hash, manager.SavePath, ensureSubfolder: true);
    }

    private string ResolveMovePath(TorrentManager manager, string root, bool ensureSubfolder = false)
    {
        string? folder = OptionsFor(manager.InfoHashes.V1OrV2.ToHex()).StorageSubfolder;
        if (folder == null && (ensureSubfolder || manager.Torrent!.Files.Count > 1))
        {
            // MonoTorrent's MoveFilesAsync accepts the content root, not the
            // original parent SavePath. Keep the multi-file containing folder.
            folder = manager.ContainingDirectory.Length > 0 && !StoragePaths.Equal(manager.ContainingDirectory, manager.SavePath)
                ? Path.GetFileName(StoragePaths.Normalize(manager.ContainingDirectory))
                : StoragePaths.TorrentFolder(manager.Torrent.Name, manager.InfoHashes.V1OrV2.ToHex());
        }
        return folder == null ? StoragePaths.Normalize(root) : StoragePaths.Child(root, folder);
    }
}
