using MonoTorrent;
using MonoTorrent.BEncoding;

namespace Controllarr.Core.Desktop;

public sealed record MigrationEntry(string TorrentFile, string SavePath, string Category);

public static class TorrentTools
{
    public static async Task CreateAsync(string sourcePath, string destination, IEnumerable<string> trackers, bool privateTorrent,
        string comment, bool hybrid, IProgress<double>? progress, CancellationToken cancellation)
    {
        string source = Path.GetFullPath(sourcePath);
        string output = Path.GetFullPath(destination);
        if (!File.Exists(source) && !Directory.Exists(source)) throw new FileNotFoundException("Choose an existing source file or folder.");
        if (Directory.Exists(source) && output.StartsWith(source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Save the .torrent outside the source folder, so it does not include itself.");
        if (File.Exists(output)) throw new IOException("The output already exists. Choose a different filename.");
        var urls = Engine.TorrentEngine.ValidateTrackerUrls(trackers);
        if (privateTorrent && urls.Length == 0) throw new ArgumentException("Private torrents need at least one tracker.");
        var creator = new TorrentCreator(hybrid ? TorrentType.V1V2Hybrid : TorrentType.V1Only)
        { Private = privateTorrent, Comment = comment, CreatedBy = "Controllarr" };
        foreach (string url in urls) creator.Announces.Add(new List<string> { url });
        creator.Hashed += (_, e) => progress?.Report(e.OverallCompletion);
        RejectReparsePoints(source, cancellation);
        var files = new TorrentFileSource(source);
        if (!files.Files.Any()) throw new ArgumentException("The source folder has no eligible files.");
        // The output is created only after hashing, so cancellation leaves no partial torrent.
        var metadata = await creator.CreateAsync(files, cancellation);
        cancellation.ThrowIfCancellationRequested();
        await using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(metadata.Encode(), CancellationToken.None);
    }

    private static void RejectReparsePoints(string source, CancellationToken cancellation)
    {
        var pending = new Stack<string>();
        pending.Push(source);
        while (pending.Count > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            string path = pending.Pop();
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Torrent creation does not follow symbolic links or junctions. Choose a source without linked files/folders.");
            if ((attributes & FileAttributes.Directory) != 0)
                foreach (string entry in Directory.EnumerateFileSystemEntries(path)) pending.Push(entry);
        }
    }

    public static IReadOnlyList<MigrationEntry> ReadMigration(string metadataPath, string fallbackSavePath)
    {
        if (!Path.IsPathFullyQualified(fallbackSavePath)) throw new ArgumentException("Use an absolute download folder.");
        var result = new List<MigrationEntry>();
        if (File.Exists(metadataPath) && Path.GetFileName(metadataPath).Equals("resume.dat", StringComparison.OrdinalIgnoreCase))
        {
            var resume = ReadDictionary(metadataPath);
            foreach (var pair in resume)
            {
                string name = pair.Key.Text;
                if (!name.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(name) != name || pair.Value is not BEncodedDictionary entry) continue;
                string torrent = Path.Combine(Path.GetDirectoryName(metadataPath)!, name);
                if (!File.Exists(torrent)) continue;
                string path = ReadText(entry, "path");
                result.Add(new(torrent, NormalizeSavePath(torrent, path, fallbackSavePath), ReadText(entry, "label")));
            }
        }
        else
        {
            var paths = Directory.Exists(metadataPath) ? Directory.EnumerateFiles(metadataPath, "*.torrent") : new[] { metadataPath };
            foreach (string torrent in paths)
            {
                if (!File.Exists(torrent) || !torrent.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)) continue;
                string path = fallbackSavePath, category = "";
                string fastResume = Path.ChangeExtension(torrent, ".fastresume");
                if (File.Exists(fastResume))
                {
                    var entry = ReadDictionary(fastResume);
                    string original = ReadText(entry, "qBt-savePath");
                    if (Path.IsPathFullyQualified(original)) path = original;
                    category = ReadText(entry, "qBt-category");
                }
                result.Add(new(torrent, path, category));
            }
        }
        return result;
    }

    private static BEncodedDictionary ReadDictionary(string path)
    {
        if (new System.IO.FileInfo(path).Length > 32 * 1024 * 1024) throw new IOException("Resume metadata exceeds 32 MB; import .torrent files directly instead.");
        return BEncodedValue.Decode<BEncodedDictionary>(File.ReadAllBytes(path));
    }
    private static string ReadText(BEncodedDictionary value, string key) => value.TryGetValue(key, out var text) && text is BEncodedString str ? str.Text : "";
    private static string NormalizeSavePath(string torrentPath, string original, string fallback)
    {
        if (!Path.IsPathFullyQualified(original)) return fallback;
        var torrent = Torrent.Load(torrentPath);
        return Path.GetFileName(original.TrimEnd(Path.DirectorySeparatorChar)).Equals(torrent.Name, StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(original.TrimEnd(Path.DirectorySeparatorChar))! : original;
    }
}
