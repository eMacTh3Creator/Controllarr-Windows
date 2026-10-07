namespace Controllarr.Core.Desktop;

public sealed record BatchFailure(string InfoHash, string Message);
public sealed record BatchResult(int Succeeded, IReadOnlyList<BatchFailure> Failures, bool Cancelled)
{
    public IReadOnlyList<string> SucceededHashes { get; init; } = Array.Empty<string>();
}
public sealed record RemovalProgress(string Stage, int Completed, int Total);

public static class TorrentBatch
{
    public static async Task<BatchResult> RunBoundedAsync(IEnumerable<string> hashes,
        Func<string, Task<bool>> operation, int maximumConcurrency,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (maximumConcurrency is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));
        var targets = hashes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var failures = new List<BatchFailure>();
        var successes = new List<string>();
        object reportLock = new();
        int next = -1, completed = 0;
        async Task WorkerAsync()
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int index = Interlocked.Increment(ref next);
                if (index >= targets.Length) break;
                string hash = targets[index];
                bool success = false;
                string? error = null;
                try { success = await operation(hash).ConfigureAwait(false); }
                catch (Exception ex) { error = ex.Message; }
                lock (reportLock)
                {
                    if (success) successes.Add(hash);
                    else failures.Add(new(hash, error ?? "Engine rejected the operation or torrent no longer exists."));
                    progress?.Report(++completed);
                }
            }
        }
        await Task.WhenAll(Enumerable.Range(0, Math.Min(maximumConcurrency, targets.Length)).Select(_ => WorkerAsync())).ConfigureAwait(false);
        return new(successes.Count, failures, cancellationToken.IsCancellationRequested) { SucceededHashes = successes };
    }

    /// <summary>Serial operations prevent a large selection from flooding disk/tracker/state writes.</summary>
    public static async Task<BatchResult> RunAsync(IEnumerable<string> hashes,
        Func<string, Task<bool>> operation, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var targets = hashes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var failures = new List<BatchFailure>();
        int succeeded = 0, completed = 0;
        foreach (var hash in targets)
        {
            if (cancellationToken.IsCancellationRequested)
                return new(succeeded, failures, true);
            try
            {
                if (await operation(hash).ConfigureAwait(false)) succeeded++;
                else failures.Add(new(hash, "Engine rejected the operation or torrent no longer exists."));
            }
            catch (Exception ex) { failures.Add(new(hash, ex.Message)); }
            progress?.Report(++completed);
        }
        return new(succeeded, failures, false);
    }
}
