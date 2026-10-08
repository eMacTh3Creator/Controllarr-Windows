using Controllarr.Core.Engine;
using Controllarr.Core.Server;

static class RemoteTests
{
    public static void Run(Action<bool, string> check)
    {
        check(QBittorrentApi.MapState(new() { State = TorrentState.Seeding, Progress = 1, Paused = true }) == "pausedUP", "remote paused seeds use the shared qBittorrent upload state");
        check(QBittorrentApi.MapState(new() { State = TorrentState.Queued, Progress = 1 }) == "queuedUP", "remote queued seeds are not classified as queued downloads");
        check(QBittorrentApi.MapState(new() { State = TorrentState.Downloading }) == "stalledDL", "remote idle downloads report a stalled state");
        check(QBittorrentApi.MapState(new() { State = TorrentState.CheckingFiles }) == "checkingDL", "remote checking state matches desktop API");
        check(QBittorrentApi.MapState(new() { State = TorrentState.Seeding, Progress = 1, UploadRate = 100 }) == "uploading", "remote active uploads are not classified as stalled seeds");
        var journal = new RemoteEventJournal();
        TorrentStats Row(float progress, TorrentState state = TorrentState.Downloading) => new()
            { InfoHash = "a", Name = "Fixture", Progress = progress, HasMetadata = true, State = state };
        journal.Observe(new[] { Row(0) }, 53127, true, true);
        var baseline = journal.Read(null, null);
        check(baseline.reset && baseline.events.Length == 0, "remote event subscription baselines without a notification flood");
        journal.Observe(new[] { Row(1, TorrentState.Error) }, 53128, true, false);
        var page = journal.Read(baseline.epoch, baseline.cursor);
        check(page.events.Select(e => e.kind).ToHashSet().SetEquals(new[] { "completed", "error", "port_changed", "vpn_disconnected" }), "remote event feed detects all four transition types");
        journal.Observe(new[] { Row(1, TorrentState.Error) }, 53128, true, false);
        check(journal.Read(page.epoch, page.cursor).events.Length == 0, "unchanged torrent/VPN state does not repeat notifications");
        for (int i = 1; i <= 600; i++) journal.Observe(Array.Empty<TorrentStats>(), (ushort)(54000 + i), false, false);
        check(journal.Read(baseline.epoch, baseline.cursor).reset, "lagging event cursor reports retention loss explicitly");
        check(journal.Read("previous-process", 0).reset, "server restart epochs invalidate old cursors");
        check(journal.Read(page.epoch, 604 - 512).events.Length <= 512, "remote event journal remains bounded");
    }
}
