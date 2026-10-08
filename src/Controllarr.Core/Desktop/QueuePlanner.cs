using Controllarr.Core.Persistence;

namespace Controllarr.Core.Desktop;

public sealed record QueueCandidate(string Hash, long Position, bool Complete, bool ManuallyPaused, bool Error, bool Forced);
public sealed record QueuePlan(HashSet<string> Active, IReadOnlyDictionary<string, string> WaitingReasons);

public static class QueuePlanner
{
    public static HashSet<string> Select(IEnumerable<QueueCandidate> candidates, TorrentQueueing limits) => Plan(candidates, limits).Active;

    public static QueuePlan Plan(IEnumerable<QueueCandidate> candidates, TorrentQueueing limits)
    {
        var ordered = candidates.Where(c => !c.ManuallyPaused && !c.Error)
            .OrderByDescending(c => c.Forced).ThenBy(c => c.Complete).ThenBy(c => c.Position).ThenBy(c => c.Hash, StringComparer.OrdinalIgnoreCase);
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var waiting = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int downloads = 0, seeds = 0;
        foreach (var candidate in ordered)
        {
            if (limits.Enabled && !candidate.Forced)
            {
                string? reason = limits.MaxActiveTotal == 0 ? "Queued: maximum active total is set to 0."
                    : (candidate.Complete ? limits.MaxActiveSeeds : limits.MaxActiveDownloads) == 0
                        ? candidate.Complete ? "Queued: maximum active seeds is set to 0." : "Queued: maximum active downloads is set to 0."
                    : (candidate.Complete ? seeds >= limits.MaxActiveSeeds : downloads >= limits.MaxActiveDownloads)
                        ? candidate.Complete ? "Queued: waiting for a seeding slot." : "Queued: waiting for a download slot."
                    : active.Count >= limits.MaxActiveTotal ? "Queued: waiting for a total active slot (downloads take priority over ordinary seeds)."
                    : null;
                if (reason != null) { waiting[candidate.Hash] = reason; continue; }
            }
            active.Add(candidate.Hash);
            if (candidate.Complete) seeds++; else downloads++;
        }
        return new(active, waiting);
    }
}
