using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Controllarr.Core.Engine;
using Controllarr.Core.Networking;
using Controllarr.Core.Persistence;
using Controllarr.Core.Services;
using Makaretu.Dns;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Controllarr.Core.Server;

public sealed class RemoteEventJournal
{
    public sealed record Event(int id, string kind, string title, string message, string? hash, double timestamp);
    public sealed record Page(string epoch, int cursor, bool reset, Event[] events);
    private readonly object _gate = new();
    private readonly string _epoch = Guid.NewGuid().ToString("N");
    private readonly Queue<Event> _events = new();
    private Dictionary<string, (bool Complete, bool Error)> _previous = new(StringComparer.OrdinalIgnoreCase);
    private int _cursor;
    private ushort? _port;
    private bool? _vpnConnected;

    public void Observe(TorrentStats[] torrents, ushort port, bool vpnEnabled, bool connected)
    {
        lock (_gate)
        {
            var next = new Dictionary<string, (bool, bool)>(torrents.Length, StringComparer.OrdinalIgnoreCase);
            foreach (var t in torrents)
            {
                bool complete = t.HasMetadata && t.Progress >= 1;
                bool error = t.State == TorrentState.Error;
                if (_previous.TryGetValue(t.InfoHash, out var old))
                {
                    if (complete && !old.Complete) Append("completed", t.Name, "Torrent completed", t.InfoHash);
                    if (error && !old.Error) Append("error", t.Name, t.StatusReason, t.InfoHash);
                }
                next[t.InfoHash] = (complete, error);
            }
            _previous = next;
            if (_port.HasValue && _port != port) Append("port_changed", "Listen port changed", $"{_port} -> {port}", null);
            if (vpnEnabled && _vpnConnected == true && !connected) Append("vpn_disconnected", "VPN disconnected", "Check the protected torrent adapter", null);
            _port = port;
            _vpnConnected = vpnEnabled ? connected : null;
        }
    }

    private void Append(string kind, string title, string message, string? hash)
    {
        _events.Enqueue(new Event(++_cursor, kind, title, message, hash, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d));
        while (_events.Count > 512) _events.Dequeue();
    }

    public Page Read(string? epoch, int? cursor)
    {
        lock (_gate)
        {
            bool reset = epoch != _epoch || cursor == null || cursor > _cursor || cursor < (_events.TryPeek(out var first) ? first.id : 1) - 1;
            return new Page(_epoch, _cursor, reset, reset ? Array.Empty<Event>() : _events.Where(e => e.id > cursor).ToArray());
        }
    }
}

internal sealed class RemoteApi : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly RemoteEventJournal _events = new();
    private Task? _loop;
    private ServiceDiscovery? _discovery;

    public void Start(WebApplication app, TorrentEngine engine, PersistenceStore store, VPNMonitor vpn, Logger logger, string host, int port)
    {
        app.MapPost("/api/v2/torrents/setLocation", async (HttpContext ctx) =>
        {
            var form = await FormParser.ParseForm(ctx.Request);
            string location = form.GetValueOrDefault("location", "");
            if (!Path.IsPathFullyQualified(location)) return Results.BadRequest("An absolute server path is required.");
            foreach (var hash in form.GetValueOrDefault("hashes", "").Split('|', StringSplitOptions.RemoveEmptyEntries))
                if (!await engine.Move(hash, location)) return Results.Conflict("Storage move was rejected. Check destinations and the server log.");
            return Results.Ok();
        });
        app.MapPost("/api/v2/torrents/reannounce", async (HttpContext ctx) =>
        {
            var form = await FormParser.ParseForm(ctx.Request);
            foreach (var hash in form.GetValueOrDefault("hashes", "").Split('|', StringSplitOptions.RemoveEmptyEntries)) engine.RequestReannounce(hash);
            return Results.Ok();
        });
        app.MapPost("/api/v2/torrents/recheck", async (HttpContext ctx) =>
        {
            var form = await FormParser.ParseForm(ctx.Request);
            foreach (var hash in form.GetValueOrDefault("hashes", "").Split('|', StringSplitOptions.RemoveEmptyEntries)) await engine.Recheck(hash);
            return Results.Ok();
        });
        app.MapPost("/api/controllarr/torrents/{hash}/repairLayout", async (string hash, HttpContext ctx) =>
        {
            var form = await FormParser.ParseForm(ctx.Request);
            if (form.GetValueOrDefault("confirmed", "false") != "true") return Results.BadRequest("Explicit confirmation is required before moving files.");
            return await engine.RepairContentLayoutAsync(hash) ? Results.Ok() : Results.Conflict("Folder repair was rejected. Metadata must be available and destinations must be free.");
        });
        app.MapGet("/api/controllarr/remote", () => Results.Json(new
        {
            protocol = 1, platform = "Windows", version = "2.3.0", settingsStyle = "snake_case",
            features = new[] { "paged_torrents", "events", "categories", "files", "trackers", "peers", "repair_layout", "move_storage" },
            notifications = "polling", bonjourService = "_controllarr._tcp"
        }));
        app.MapGet("/api/controllarr/remote/torrents", (HttpContext ctx) =>
        {
            string search = ctx.Request.Query["search"].ToString();
            string? category = ctx.Request.Query.ContainsKey("category") ? ctx.Request.Query["category"].ToString() : null;
            int offset = Math.Max(0, int.TryParse(ctx.Request.Query["offset"], out int o) ? o : 0);
            int limit = Math.Clamp(int.TryParse(ctx.Request.Query["limit"], out int l) ? l : 100, 1, 500);
            var all = engine.PollStats().Where(t => (category == null || (t.Category ?? "") == category) &&
                (search.Length == 0 || t.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || t.InfoHash.Contains(search, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(t => t.InfoHash, StringComparer.Ordinal).ToArray();
            return Results.Json(new { total = all.Length, offset, items = all.Skip(offset).Take(limit).Select(t => new
            {
                hash = t.InfoHash, name = t.Name, category = t.Category ?? "", progress = t.Progress,
                size = t.TotalWanted, dlspeed = t.DownloadRate, upspeed = t.UploadRate,
                state = QBittorrentApi.MapState(t),
                save_path = string.IsNullOrEmpty(t.ApiSavePath) ? t.SavePath : t.ApiSavePath, content_path = t.ContentPath, ratio = t.Ratio, num_seeds = t.NumSeeds, num_leechs = t.NumPeers
            }) });
        });
        app.MapGet("/api/controllarr/remote/events", (HttpContext ctx) => Results.Json(_events.Read(
            ctx.Request.Query["epoch"], int.TryParse(ctx.Request.Query["cursor"], out int c) ? c : null)));
        _loop = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var status = vpn.Snapshot();
                    _events.Observe(engine.PollStats(), engine.ListenPort, status.Enabled, status.IsConnected);
                    await Task.Delay(TimeSpan.FromSeconds(3), _stop.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { logger.Warn("Remote", ex.Message); await Task.Delay(3000); }
            }
        });
        if (OperatingSystem.IsWindows() && store.GetSettings().RemoteDiscoveryEnabled && host != "127.0.0.1" && host != "localhost" && host != "::1")
        {
            try
            {
                // Exclude recognized tunnels and the explicitly selected torrent adapter.
                var settings = store.GetSettings();
                var addresses = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        !TorrentNetworkPolicy.IsVpnCandidate(n.Name, n.Description) &&
                        !n.Id.Equals(settings.VpnInterfaceId, StringComparison.OrdinalIgnoreCase) &&
                        (n.NetworkInterfaceType == NetworkInterfaceType.Ethernet || n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211))
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) &&
                        (host is "0.0.0.0" or "::" or "*" || a.ToString() == host)).ToArray();
                if (addresses.Length > 0)
                {
                    _discovery = new ServiceDiscovery();
                    var profile = new ServiceProfile("Controllarr-" + Environment.MachineName, "_controllarr._tcp", checked((ushort)port), addresses);
                    profile.AddProperty("protocol", "1");
                    profile.AddProperty("platform", "Windows");
                    _discovery.Advertise(profile);
                }
            }
            catch (Exception ex) { logger.Warn("Remote", "LAN discovery unavailable; hostname connections still work: " + ex.Message); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop != null) await _loop.ConfigureAwait(false);
        _discovery?.Dispose();
        _stop.Dispose();
    }
}
