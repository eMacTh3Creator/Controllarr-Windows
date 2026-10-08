using Controllarr.Core.Engine;

namespace Controllarr.Core.Desktop;

public static class TransferStatus
{
    public static string Describe(TorrentState state, bool paused, long wanted, long done, long downloadRate,
        int peers, int torrentConnectionLimit, int globalConnections, int globalConnectionLimit,
        string? blocked = null, string? queuedReason = null, bool removing = false, string? discoveryProblem = null)
    {
        if (removing) return "Stopped for removal; waiting for the removal sequence.";
        if (state == TorrentState.Error) return "Torrent error; check Log for details.";
        if (paused) return "Paused; resume this torrent to allow transfers.";
        if (state is TorrentState.CheckingFiles or TorrentState.CheckingResume) return "Checking downloaded files / resume data before transferring.";
        if (blocked != null) return blocked;
        if (state == TorrentState.Queued) return queuedReason ?? "Queued: waiting for the scheduler's next pass.";
        if (state is TorrentState.Seeding or TorrentState.Finished) return "Download complete; seeding when peers request data.";
        if (state is not (TorrentState.Downloading or TorrentState.DownloadingMetadata)) return "Initializing torrent; check Log if this persists.";
        if (downloadRate > 0) return "Downloading from connected peers.";
        if (state == TorrentState.Downloading && wanted == 0) return "No files selected for download; check file priorities and category filters.";
        if (state == TorrentState.Downloading && done >= wanted) return "Selected files are downloaded; waiting for completion processing.";
        if (globalConnectionLimit > 0 && globalConnections >= globalConnectionLimit)
            return $"Global connection limit reached ({globalConnections}/{globalConnectionLimit}); new peers may be blocked. Reduce active seeds/peer caps or raise the limit carefully.";
        if (torrentConnectionLimit > 0 && peers >= torrentConnectionLimit)
            return $"Per-torrent connection limit reached ({peers}/{torrentConnectionLimit}); connected peers are not sending data.";
        if (peers == 0 && discoveryProblem != null) return discoveryProblem;
        if (state == TorrentState.DownloadingMetadata) return "Waiting for magnet metadata from peers; check Trackers and Peers.";
        return peers == 0 ? "No connected peers yet; seed availability is unconfirmed. Check Trackers and Peers."
            : "Peers connected but not sending data yet; they may be choking or missing the requested pieces. Check Peers and Trackers.";
    }
}
