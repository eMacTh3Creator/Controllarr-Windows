using MonoTorrent;
using MonoTorrent.Connections.Tracker;
using MonoTorrent.Trackers;
using ReusableTasks;

namespace Controllarr.Core.Networking;

/// <summary>Adapts response-valued transport failures to MonoTorrent 3.0.2's exception-based tier failover.</summary>
internal sealed class ReliableTracker(ITrackerConnection connection) : ITracker
{
    private readonly Tracker _inner = new(connection);
    private long _minimumTicks = TimeSpan.FromMinutes(3).Ticks;
    private long _updateTicks = TimeSpan.FromMinutes(30).Ticks;
    private int _earlyRefresh;
    public bool CanScrape => _inner.CanScrape;
    public TimeSpan MinUpdateInterval => TimeSpan.FromTicks(Interlocked.Read(ref _minimumTicks));
    public TimeSpan UpdateInterval => Volatile.Read(ref _earlyRefresh) != 0 ? MinUpdateInterval : TimeSpan.FromTicks(Interlocked.Read(ref _updateTicks));
    public TimeSpan TimeSinceLastAnnounce => _inner.TimeSinceLastAnnounce;
    public TrackerState Status => _inner.Status == TrackerState.Ok && !string.IsNullOrEmpty(_inner.FailureMessage)
        ? TrackerState.Offline : _inner.Status;
    public Uri Uri => _inner.Uri;
    public string WarningMessage => _inner.WarningMessage;
    public string FailureMessage => _inner.FailureMessage;

    public void RequestEarlyRefresh()
    {
        if (TimeSinceLastAnnounce >= MinUpdateInterval) Volatile.Write(ref _earlyRefresh, 1);
    }

    public async ReusableTask<AnnounceResponse> AnnounceAsync(AnnounceRequest request, CancellationToken token)
    {
        try
        {
            var response = await _inner.AnnounceAsync(request, token);
            token.ThrowIfCancellationRequested();
            // The inner tracker retains the message/status for UI diagnostics.
            // Returning Offline here makes 3.0.2's tier report success and stop trying alternatives.
            if (response.State != TrackerState.Ok || !string.IsNullOrEmpty(response.FailureMessage))
                throw new IOException($"Tracker announce failed ({Status}).");
            long minimum = Math.Max(TimeSpan.FromSeconds(1).Ticks, response.MinUpdateInterval.Ticks);
            Interlocked.Exchange(ref _minimumTicks, minimum);
            Interlocked.Exchange(ref _updateTicks, Math.Max(minimum, response.UpdateInterval.Ticks));
            return response;
        }
        finally { Volatile.Write(ref _earlyRefresh, 0); }
    }

    public async ReusableTask<ScrapeResponse> ScrapeAsync(ScrapeRequest request, CancellationToken token)
    {
        var response = await _inner.ScrapeAsync(request, token);
        token.ThrowIfCancellationRequested();
        if (response.State != TrackerState.Ok || !string.IsNullOrEmpty(response.FailureMessage))
            throw new IOException($"Tracker scrape failed ({Status}).");
        return response;
    }
}
