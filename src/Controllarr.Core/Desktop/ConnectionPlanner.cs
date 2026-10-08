namespace Controllarr.Core.Desktop;

public sealed record ConnectionCandidate(string Hash, long Position, bool Complete, bool Forced, int Limit);
public sealed record ConnectionPlan(IReadOnlyDictionary<string, int> SeedCaps, IReadOnlyDictionary<string, string> WaitingReasons);

public static class ConnectionPlanner
{
    public static ConnectionPlan Plan(IEnumerable<ConnectionCandidate> candidates, int globalLimit, int reservePercent)
    {
        if (globalLimit < 1 || reservePercent is < 0 or > 90) throw new ArgumentOutOfRangeException(nameof(reservePercent));
        var active = candidates.ToArray();
        var caps = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var waiting = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (reservePercent == 0 || !active.Any(c => !c.Complete)) return new(caps, waiting);
        int reserved = (int)(((long)globalLimit * reservePercent + 99) / 100);
        // Forced seeds retain their explicit controls, but count against the normal seed budget.
        long forcedBudget = active.Where(c => c.Complete && c.Forced).Sum(c => (long)Math.Min(globalLimit, c.Limit));
        int budget = (int)Math.Max(0, globalLimit - reserved - forcedBudget);
        var seeds = active.Where(c => c.Complete && !c.Forced).OrderBy(c => c.Position)
            .ThenBy(c => c.Hash, StringComparer.OrdinalIgnoreCase).ToArray();
        int admitted = Math.Min(budget, seeds.Length);
        for (int i = 0; i < seeds.Length; i++)
            if (i >= admitted) waiting[seeds[i].Hash] = "Queued: reserving the global peer connection budget for downloads. Use 0% download reserve to disable this policy.";
            else caps[seeds[i].Hash] = Math.Min(seeds[i].Limit, budget / admitted + (i < budget % admitted ? 1 : 0));
        return new(caps, waiting);
    }
}
