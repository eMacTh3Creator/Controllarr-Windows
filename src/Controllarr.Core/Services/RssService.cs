using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Controllarr.Core.Engine;
using Controllarr.Core.Persistence;

namespace Controllarr.Core.Services;

public sealed record RssEntry(string Key, string Feed, string Title, string Url, DateTime CheckedAt, string Status);

public static class RssParser
{
    public static bool Matches(string title, string include, string exclude)
    {
        bool Test(string pattern) => Regex.IsMatch(title, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        return (string.IsNullOrEmpty(include) || Test(include)) && (string.IsNullOrEmpty(exclude) || !Test(exclude));
    }

    public static void Validate(RssFeed feed)
    {
        if (string.IsNullOrWhiteSpace(feed.Name) || !Uri.TryCreate(feed.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Every feed needs a name and an absolute HTTP/HTTPS URL.");
        if (feed.IntervalMinutes is < 1 or > 1440) throw new ArgumentException("Feed intervals must be 1-1440 minutes.");
        if (feed.SavePath.Length > 0 && !Path.IsPathFullyQualified(feed.SavePath)) throw new ArgumentException("Feed save folders must be absolute.");
        _ = new Regex(feed.IncludePattern, RegexOptions.None, TimeSpan.FromMilliseconds(200));
        _ = new Regex(feed.ExcludePattern, RegexOptions.None, TimeSpan.FromMilliseconds(200));
    }

    public static IReadOnlyList<(string Title, string Url)> Parse(Stream xml)
    {
        using var reader = XmlReader.Create(xml, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 });
        var document = XDocument.Load(reader);
        var result = new List<(string, string)>();
        foreach (var item in document.Descendants().Where(e => e.Name.LocalName is "item" or "entry").Take(500))
        {
            string title = item.Elements().FirstOrDefault(e => e.Name.LocalName == "title")?.Value ?? "Untitled";
            var links = item.Elements().Where(e => e.Name.LocalName is "enclosure" or "link").ToArray();
            string? candidate = links.Where(e => e.Name.LocalName == "enclosure" || (string?)e.Attribute("rel") == "enclosure")
                .Select(e => (string?)e.Attribute("url") ?? (string?)e.Attribute("href") ?? e.Value).FirstOrDefault();
            candidate ??= links.Select(e => (string?)e.Attribute("href") ?? e.Value).FirstOrDefault(s => s.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) || s.Contains(".torrent", StringComparison.OrdinalIgnoreCase));
            if (candidate != null && Uri.TryCreate(candidate.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "magnet")
                result.Add((title.Length > 2048 ? title[..2048] : title, candidate.Trim()));
        }
        return result;
    }
}

/// <summary>One cancellable background worker; bounded feed bodies, history and UI snapshots.</summary>
public sealed class RssService
{
    private readonly TorrentEngine _engine;
    private readonly PersistenceStore _store;
    private readonly Func<bool> _allowed;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _lock = new();
    private readonly Dictionary<string, RssEntry> _entries = new();
    private Dictionary<string, DateTime> _seen = new();
    private readonly Dictionary<string, DateTime> _nextChecks = new();
    private readonly string _historyPath;
    private Task? _worker;
    private bool _historyDirty;
    private bool _engineDirty;
    public RssService(TorrentEngine engine, PersistenceStore store, Func<bool> transfersAllowed)
    {
        _engine = engine; _store = store; _allowed = transfersAllowed;
        _historyPath = Path.Combine(store.Directory, "rss-history.json");
        if (File.Exists(_historyPath))
            try { _seen = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(_historyPath)) ?? new(); }
            catch { Logger.Instance.Warn("RSS", "Could not read previous RSS history."); }
    }
    public RssEntry[] Snapshot() { lock (_lock) return _entries.Values.OrderByDescending(e => e.CheckedAt).Take(500).ToArray(); }
    public void Start() => _worker ??= Task.Run(async () =>
    {
        while (!_stop.IsCancellationRequested)
        {
            try { await ScanAsync(false, _stop.Token); await Task.Delay(5000, _stop.Token); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Logger.Instance.Warn("RSS", $"RSS worker failed: {ex.GetType().Name}"); }
        }
    });
    public async Task StopAsync()
    {
        _stop.Cancel();
        if (_worker != null) await _worker;
        await _gate.WaitAsync();
        _gate.Release();
        _http.Dispose();
    }
    public Task ScanNowAsync() => ScanAsync(true, _stop.Token);

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int limit, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        token = timeout.Token;
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new IOException("Feed/download body too large.");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        byte[] buffer = new byte[16384];
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + read > limit) throw new IOException("Feed/download body too large.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private async Task ScanAsync(bool force, CancellationToken token)
    {
        if (!await _gate.WaitAsync(0, token)) return;
        try
        {
            var settings = _store.GetSettings();
            foreach (var feed in settings.RssFeeds.Where(f => f.Enabled).Take(100))
            {
                token.ThrowIfCancellationRequested();
                if (!force && _nextChecks.TryGetValue(feed.Id, out var next) && next > DateTime.UtcNow) continue;
                _nextChecks[feed.Id] = DateTime.UtcNow.AddMinutes(Math.Clamp(feed.IntervalMinutes, 1, 1440));
                try
                {
                    RssParser.Validate(feed);
                    using var response = await _http.GetAsync(feed.Url, HttpCompletionOption.ResponseHeadersRead, token);
                    byte[] body = await ReadBoundedAsync(response, 2 * 1024 * 1024, token);
                    using var xml = new MemoryStream(body);
                    foreach (var item in RssParser.Parse(xml))
                    {
                        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(feed.Id + "\n" + item.Url)));
                        string status = _seen.ContainsKey(key) ? "Already imported" : "Available";
                        if (!_seen.ContainsKey(key) && RssParser.Matches(item.Title, feed.IncludePattern, feed.ExcludePattern))
                        {
                            if (feed.AutoDownload && _allowed())
                            {
                                string savePath = feed.SavePath.Length > 0 ? feed.SavePath : _store.GetSavePath(feed.Category) ?? settings.DefaultSavePath;
                                await AddUrlAsync(item.Url, feed.Category, savePath, token);
                                MarkSeen(key);
                                _engineDirty = true;
                                status = "Imported";
                            }
                            else status = feed.AutoDownload ? "Blocked by VPN/disk guard" : "Matched (auto-download off)";
                        }
                        Publish(new(key, feed.Name, item.Title, item.Url, DateTime.UtcNow, status));
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    Publish(new(feed.Id, feed.Name, "Feed check failed", "", DateTime.UtcNow, ex.GetType().Name));
                    Logger.Instance.Warn("RSS", $"Feed '{feed.Name}' check failed: {ex.GetType().Name}");
                }
            }
            if (!string.IsNullOrWhiteSpace(settings.WatchFolder) && Directory.Exists(settings.WatchFolder) && _allowed())
                foreach (string path in Directory.EnumerateFiles(settings.WatchFolder, "*.torrent").Take(1000))
                {
                    token.ThrowIfCancellationRequested();
                    var info = new System.IO.FileInfo(path);
                    if (info.LastWriteTimeUtc > DateTime.UtcNow.AddSeconds(-5)) continue;
                    string key = $"watch:{path}:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
                    if (_seen.ContainsKey(key)) continue;
                    try { await _engine.AddTorrentFile(path, persist: false); _engineDirty = true; MarkSeen(key); }
                    catch (Exception ex) { Logger.Instance.Warn("WatchFolder", $"Could not add {info.Name}: {ex.GetType().Name}"); }
                }
        }
        finally
        {
            try
            {
                if (_engineDirty) { await _engine.SaveEngineStateAsync(); _store.SetCategoryMap(_engine.SnapshotCategories()); _engineDirty = false; }
                if (_historyDirty)
                {
                    File.WriteAllText(_historyPath + ".tmp", JsonSerializer.Serialize(_seen));
                    File.Move(_historyPath + ".tmp", _historyPath, true);
                    _historyDirty = false;
                }
            }
            finally { _gate.Release(); }
        }
    }
    private async Task AddUrlAsync(string url, string category, string savePath, CancellationToken token)
    {
        if (!_allowed()) throw new InvalidOperationException("Transfers blocked by guard.");
        if (url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) { await _engine.AddMagnet(url, category, savePath, persist: false); return; }
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        byte[] torrent = await ReadBoundedAsync(response, 16 * 1024 * 1024, token);
        if (!_allowed()) throw new InvalidOperationException("Transfers blocked by guard.");
        string file = Path.Combine(_store.Directory, $"rss-{Guid.NewGuid():N}.torrent");
        try { await File.WriteAllBytesAsync(file, torrent, token); await _engine.AddTorrentFile(file, category, savePath, persist: false); }
        finally { if (File.Exists(file)) File.Delete(file); }
    }
    private void MarkSeen(string key)
    {
        _seen[key] = DateTime.UtcNow;
        if (_seen.Count > 10000) _seen = _seen.OrderByDescending(p => p.Value).Take(10000).ToDictionary(p => p.Key, p => p.Value);
        _historyDirty = true;
    }
    private void Publish(RssEntry entry)
    {
        lock (_lock)
        {
            _entries[entry.Key] = entry;
            if (_entries.Count > 2000) _entries.Remove(_entries.MinBy(p => p.Value.CheckedAt).Key);
        }
    }
}
