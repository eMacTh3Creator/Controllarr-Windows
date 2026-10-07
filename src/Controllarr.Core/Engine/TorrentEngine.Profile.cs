using MonoTorrent.BEncoding;

namespace Controllarr.Core.Engine;

public sealed partial class TorrentEngine
{
    private static void RebaseProfileCache(BEncodedDictionary saved, string newCache)
    {
        var settings = (BEncodedDictionary)saved["Settings"];
        string? oldCache = settings.TryGetValue("CacheDirectory", out var previous) ? ((BEncodedString)previous).Text : null;
        settings["CacheDirectory"] = new BEncodedString(newCache);
        if (string.IsNullOrWhiteSpace(oldCache) || !Path.IsPathFullyQualified(oldCache)
            || !saved.TryGetValue("Torrents", out var torrents)) return;
        var list = (BEncodedList)torrents;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var torrent = (BEncodedDictionary)list[i];
            if (!torrent.TryGetValue("MetadataPath", out var metadata)) continue;
            string path = ((BEncodedString)metadata).Text;
            if (!Path.IsPathFullyQualified(path)) continue;
            string relative = Path.GetRelativePath(oldCache, path);
            if (Path.IsPathFullyQualified(relative) || relative == ".."
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            // Only cached metadata moves with the profile. Payload paths stay untouched.
            string relocated = Path.Combine(newCache, relative);
            if (File.Exists(relocated)) torrent["MetadataPath"] = new BEncodedString(relocated);
            else
            {
                // A crash between deletion and the next batch checkpoint can leave
                // a removed cache entry in engine.state. Do not lose the whole list.
                list.RemoveAt(i);
                Services.Logger.Instance.Warn("Engine", "Skipped a saved torrent whose cached metadata is missing.");
            }
        }
    }
}
