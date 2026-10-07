using Controllarr.Core.Persistence;

namespace Controllarr.Core.Desktop;

public sealed record QueueCandidate(string Hash, long Position, bool Complete, bool ManuallyPaused, bool Error, bool Forced);

public static class QueuePlanner
{
    public static HashSet<string> Select(IEnumerable<QueueCandidate> candidates, TorrentQueueing limits)
    {
        var ordered = candidates.Where(c => !c.ManuallyPaused && !c.Error)
            .OrderByDescending(c => c.Forced).ThenBy(c => c.Position).ThenBy(c => c.Hash, StringComparer.OrdinalIgnoreCase);
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int downloads = 0, seeds = 0;
        foreach (var candidate in ordered)
        {
            if (limits.Enabled && !candidate.Forced &&
                (active.Count >= limits.MaxActiveTotal ||
                 (candidate.Complete ? seeds >= limits.MaxActiveSeeds : downloads >= limits.MaxActiveDownloads))) continue;
            active.Add(candidate.Hash);
            if (candidate.Complete) seeds++; else downloads++;
        }
        return active;
    }
}
