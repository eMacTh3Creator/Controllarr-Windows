namespace Controllarr.Core.Desktop;

public sealed record BatchFailure(string InfoHash, string Message);
public sealed record BatchResult(int Succeeded, IReadOnlyList<BatchFailure> Failures, bool Cancelled);

public static class TorrentBatch
{
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
