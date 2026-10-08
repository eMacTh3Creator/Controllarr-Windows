namespace Controllarr.Core.Engine;

public static class StoragePaths
{
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static bool Equal(string left, string right) => string.Equals(Normalize(left), Normalize(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public static string TorrentFolder(string? name, string hash)
    {
        if (hash.Length < 12 || hash.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("Invalid torrent hash.");
        string clean = new((name ?? "Torrent").Select(c => c < 32 || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray());
        clean = clean.Trim().Trim('.');
        if (clean.Length == 0) clean = "Torrent";
        if (clean.Length > 100) clean = clean[..100].TrimEnd();
        return $"{clean} [{hash[..12].ToLowerInvariant()}]";
    }

    internal static string Child(string root, string folder)
    {
        root = Normalize(root);
        if (string.IsNullOrWhiteSpace(folder) || folder != Path.GetFileName(folder) || folder is "." or "..")
            throw new IOException("Invalid torrent storage subfolder.");
        return string.Equals(Path.GetFileName(root), folder, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ? root : Path.Combine(root, folder);
    }
}
