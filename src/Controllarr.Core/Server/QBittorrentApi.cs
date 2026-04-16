using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

using Controllarr.Core.Engine;
using Controllarr.Core.Persistence;
using Controllarr.Core.Services;

namespace Controllarr.Core.Server
{
    /// <summary>
    /// Extension method that maps all Controllarr HTTP routes onto a WebApplication.
    /// Covers the qBittorrent v2 compatibility surface plus Controllarr-native endpoints.
    /// </summary>
    public static class QBittorrentApi
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = null,   // exact property names
            WriteIndented = false
        };

        // Static startup time for uptime calculation (SessionStats has no StartedAt)
        private static readonly DateTime StartedAt = DateTime.UtcNow;

        // ────────────────────────────────────────────────────────────
        // Public entry point
        // ────────────────────────────────────────────────────────────

        public static void MapControllarrRoutes(
            this WebApplication app,
            TorrentEngine engine,
            PersistenceStore store,
            Logger logger,
            PostProcessor postProcessor,
            SeedingPolicy seedingPolicy,
            HealthMonitor healthMonitor,
            RecoveryCenter recovery,
            DiskSpaceMonitor diskSpace,
            VPNMonitor vpn,
            ArrNotifier arrNotifier,
            Action forceCyclePort,
            ConcurrentDictionary<string, DateTime> sessions,
            Func<string, string, bool> validateCredentials)
        {
            // ─── qBittorrent v2 Auth ───────────────────────────────
            MapAuth(app, sessions, validateCredentials, logger);

            // ─── qBittorrent v2 App ────────────────────────────────
            MapAppRoutes(app, store);

            // ─── qBittorrent v2 Transfer ───────────────────────────
            MapTransferRoutes(app, engine);

            // ─── qBittorrent v2 Torrents ───────────────────────────
            MapTorrentRoutes(app, engine, store, logger);

            // ─── Controllarr-native ────────────────────────────────
            MapControllarrNativeRoutes(
                app, engine, store, logger, postProcessor, seedingPolicy,
                healthMonitor, recovery, diskSpace, vpn, arrNotifier,
                forceCyclePort);
        }

        // ================================================================
        //  qBittorrent v2: Auth
        // ================================================================

        private static void MapAuth(
            WebApplication app,
            ConcurrentDictionary<string, DateTime> sessions,
            Func<string, string, bool> validateCredentials,
            Logger logger)
        {
            app.MapPost("/api/v2/auth/login", async (HttpContext ctx) =>
            {
                var form = await FormParser.ParseForm(ctx.Request);
                string username = form.GetValueOrDefault("username", "");
                string password = form.GetValueOrDefault("password", "");

                if (!validateCredentials(username, password))
                {
                    logger.Warn("Auth", $"Login failed for user '{username}'");
                    ctx.Response.StatusCode = 403;
                    await ctx.Response.WriteAsync("Fails.");
                    return;
                }

                string sid = Guid.NewGuid().ToString("N");
                sessions[sid] = DateTime.UtcNow.AddHours(1);

                // Enforce max 50 sessions – evict oldest expired first, then oldest
                while (sessions.Count > 50)
                {
                    var oldest = sessions
                        .OrderBy(kv => kv.Value)
                        .First();
                    sessions.TryRemove(oldest.Key, out _);
                }

                ctx.Response.Cookies.Append("SID", sid, new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Lax,
                    Path = "/",
                });

                logger.Info("Auth", $"Login succeeded for user '{username}'");
                ctx.Response.StatusCode = 200;
                await ctx.Response.WriteAsync("Ok.");
            });

            app.MapPost("/api/v2/auth/logout", (HttpContext ctx) =>
            {
                if (ctx.Request.Cookies.TryGetValue("SID", out string? sid) && sid != null)
                {
                    sessions.TryRemove(sid, out _);
                }

                ctx.Response.Cookies.Delete("SID");
                ctx.Response.StatusCode = 200;
                return Task.CompletedTask;
            });
        }

        // ================================================================
        //  qBittorrent v2: App
        // ================================================================

        private static void MapAppRoutes(WebApplication app, PersistenceStore store)
        {
            app.MapGet("/api/v2/app/version", () => "v4.6.0");

            app.MapGet("/api/v2/app/webapiVersion", () => "2.9.3");

            app.MapGet("/api/v2/app/buildInfo", () =>
            {
                var info = new Dictionary<string, object>
                {
                    ["qt"] = "6.5.3",
                    ["libtorrent"] = "2.0.9.0",
                    ["boost"] = "1.83.0",
                    ["openssl"] = "3.1.4",
                    ["zlib"] = "1.3",
                    ["bitness"] = 64
                };
                return Results.Json(info, JsonOpts);
            });

            app.MapGet("/api/v2/app/preferences", (HttpContext ctx) =>
            {
                var settings = store.GetSettings();
                var prefs = BuildPreferencesJson(settings);
                return Results.Json(prefs, JsonOpts);
            });

            app.MapPost("/api/v2/app/setPreferences", async (HttpContext ctx) =>
            {
                var form = await FormParser.ParseForm(ctx.Request);
                string json = form.GetValueOrDefault("json", "{}");

                var incoming = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, JsonOpts);
                if (incoming == null)
                    return Results.BadRequest("Invalid JSON");

                store.UpdateSettings(s =>
                {
                    // Map qBit preference keys to our settings
                    if (incoming.TryGetValue("save_path", out var sp))
                        s.DefaultSavePath = sp.GetString() ?? s.DefaultSavePath;
                    if (incoming.TryGetValue("listen_port", out var lp))
                        s.ListenPortRangeStart = (ushort)lp.GetInt32();
                    if (incoming.TryGetValue("max_ratio", out var mr))
                        s.GlobalMaxRatio = mr.GetDouble();
                    if (incoming.TryGetValue("max_seeding_time", out var mst))
                        s.GlobalMaxSeedingTimeMinutes = mst.GetInt32();
                    if (incoming.TryGetValue("max_ratio_act", out var mra))
                        s.SeedLimitAction = (SeedLimitAction)mra.GetInt32();
                    if (incoming.TryGetValue("web_ui_username", out var wu))
                        s.WebUIUsername = wu.GetString() ?? s.WebUIUsername;
                    if (incoming.TryGetValue("web_ui_password", out var wp))
                        s.WebUIPassword = wp.GetString() ?? s.WebUIPassword;
                });

                return Results.Ok();
            });
        }

        // ================================================================
        //  qBittorrent v2: Transfer
        // ================================================================

        private static void MapTransferRoutes(WebApplication app, TorrentEngine engine)
        {
            app.MapGet("/api/v2/transfer/info", (HttpContext ctx) =>
            {
                var stats = engine.GetSessionStats();
                var info = new Dictionary<string, object>
                {
                    ["dl_info_speed"] = stats.DownloadRate,
                    ["dl_info_data"] = stats.TotalDownloaded,
                    ["up_info_speed"] = stats.UploadRate,
                    ["up_info_data"] = stats.TotalUploaded,
                    ["dl_rate_limit"] = 0,
                    ["up_rate_limit"] = 0,
                    ["dht_nodes"] = 0,
                    ["connection_status"] = "connected"
                };
                return Results.Json(info, JsonOpts);
            });

            app.MapGet("/api/v2/transfer/speedLimitsMode", () => "0");
        }

        // ================================================================
        //  qBittorrent v2: Torrents
        // ================================================================

        private static void MapTorrentRoutes(
            WebApplication app,
            TorrentEngine engine,
            PersistenceStore store,
            Logger logger)
        {
            // ── List torrents ──────────────────────────────────────
            app.MapGet("/api/v2/torrents/info", (HttpContext ctx) =>
            {
                var qs = FormParser.ParseQuery(ctx.Request.QueryString.Value);
                string filter = qs.GetValueOrDefault("filter", "all");
                string? category = qs.GetValueOrDefault("category", null);
                string? hashes = qs.GetValueOrDefault("hashes", null);

                var snapshot = store.Snapshot();
                var all = engine.PollStats();
                IEnumerable<TorrentStats> filtered = all;

                // Hash filter
                if (!string.IsNullOrEmpty(hashes))
                {
                    var hashSet = new HashSet<string>(
                        hashes.Split('|', StringSplitOptions.RemoveEmptyEntries),
                        StringComparer.OrdinalIgnoreCase);
                    filtered = filtered.Where(t => hashSet.Contains(t.InfoHash));
                }

                // Category filter
                if (!string.IsNullOrEmpty(category))
                {
                    filtered = filtered.Where(t =>
                    {
                        snapshot.CategoryByHash.TryGetValue(t.InfoHash, out string? cat);
                        return string.Equals(cat, category, StringComparison.OrdinalIgnoreCase);
                    });
                }

                // State filter
                filtered = filter.ToLowerInvariant() switch
                {
                    "downloading" => filtered.Where(t => t.State == TorrentState.Downloading || t.State == TorrentState.DownloadingMetadata),
                    "seeding" => filtered.Where(t => t.State == TorrentState.Seeding),
                    "completed" => filtered.Where(t => t.Progress >= 1.0f),
                    "paused" => filtered.Where(t => t.Paused),
                    "active" => filtered.Where(t => t.DownloadRate > 0 || t.UploadRate > 0),
                    "inactive" => filtered.Where(t => t.DownloadRate == 0 && t.UploadRate == 0),
                    _ => filtered  // "all" or unrecognized
                };

                var result = filtered.Select(t => MapTorrentToQBit(t, snapshot)).ToList();
                return Results.Json(result, JsonOpts);
            });

            // ── Torrent properties ─────────────────────────────────
            app.MapGet("/api/v2/torrents/properties", (HttpContext ctx) =>
            {
                var qs = FormParser.ParseQuery(ctx.Request.QueryString.Value);
                string hash = qs.GetValueOrDefault("hash", "");

                if (string.IsNullOrEmpty(hash))
                    return Results.BadRequest("Missing hash");

                var t = engine.GetStats(hash);
                if (t == null)
                    return Results.NotFound();

                var snapshot = store.Snapshot();
                snapshot.CategoryByHash.TryGetValue(hash, out string? cat);

                var props = new Dictionary<string, object?>
                {
                    ["hash"] = t.InfoHash,
                    ["name"] = t.Name,
                    ["save_path"] = t.SavePath,
                    ["total_size"] = t.TotalWanted,
                    ["progress"] = t.Progress,
                    ["dlspeed"] = t.DownloadRate,
                    ["upspeed"] = t.UploadRate,
                    ["eta"] = CalculateEta(t),
                    ["num_seeds"] = t.NumSeeds,
                    ["num_leech"] = t.NumPeers,
                    ["ratio"] = t.Ratio,
                    ["state"] = MapState(t),
                    ["category"] = cat ?? "",
                    ["added_on"] = new DateTimeOffset(t.AddedDate).ToUnixTimeSeconds(),
                    ["completion_on"] = t.Progress >= 0.999f
                        ? new DateTimeOffset(t.AddedDate).ToUnixTimeSeconds()
                        : -1,
                    ["downloaded"] = t.TotalDownload,
                    ["uploaded"] = t.TotalUpload,
                    ["download_limit"] = -1,
                    ["upload_limit"] = -1,
                    ["total_downloaded"] = t.TotalDownload,
                    ["total_uploaded"] = t.TotalUpload,
                    ["creation_date"] = new DateTimeOffset(t.AddedDate).ToUnixTimeSeconds(),
                    ["piece_size"] = 0,
                    ["pieces_num"] = 0,
                    ["pieces_have"] = 0,
                    ["comment"] = "",
                    ["nb_connections"] = t.NumSeeds + t.NumPeers,
                    ["seeding_time"] = 0,
                    ["time_active"] = (int)(DateTime.UtcNow - t.AddedDate).TotalSeconds
                };
                return Results.Json(props, JsonOpts);
            });

            // ── Add torrents ───────────────────────────────────────
            app.MapPost("/api/v2/torrents/add", async (HttpContext ctx) =>
            {
                string? category = null;
                string? savePath = null;
                var magnets = new List<string>();
                var torrentFiles = new List<byte[]>();

                string contentType = ctx.Request.ContentType ?? "";

                if (contentType.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = await FormParser.ParseMultipart(ctx.Request);
                    foreach (var part in parts)
                    {
                        switch (part.Name.ToLowerInvariant())
                        {
                            case "urls":
                                string urlsText = Encoding.UTF8.GetString(part.Data);
                                foreach (var line in urlsText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                                {
                                    string trimmed = line.Trim();
                                    if (!string.IsNullOrEmpty(trimmed))
                                        magnets.Add(trimmed);
                                }
                                break;
                            case "torrents":
                                if (part.Data.Length > 0)
                                    torrentFiles.Add(part.Data);
                                break;
                            case "category":
                                category = Encoding.UTF8.GetString(part.Data).Trim();
                                break;
                            case "savepath":
                                savePath = Encoding.UTF8.GetString(part.Data).Trim();
                                break;
                        }
                    }
                }
                else
                {
                    // URL-encoded fallback
                    var form = await FormParser.ParseForm(ctx.Request);
                    if (form.TryGetValue("urls", out string? urls) && !string.IsNullOrEmpty(urls))
                    {
                        foreach (var line in urls.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        {
                            string trimmed = line.Trim();
                            if (!string.IsNullOrEmpty(trimmed))
                                magnets.Add(trimmed);
                        }
                    }
                    category = form.GetValueOrDefault("category", null);
                    savePath = form.GetValueOrDefault("savepath", null);
                }

                // Resolve save path
                if (string.IsNullOrEmpty(savePath))
                {
                    if (!string.IsNullOrEmpty(category))
                    {
                        var catSavePath = store.GetSavePath(category);
                        savePath = catSavePath ?? store.GetSettings().DefaultSavePath;
                    }
                    else
                    {
                        savePath = store.GetSettings().DefaultSavePath;
                    }
                }

                int added = 0;

                // Add magnets
                foreach (var magnet in magnets)
                {
                    try
                    {
                        string hash = await engine.AddMagnet(magnet, category, savePath);
                        if (!string.IsNullOrEmpty(category))
                        {
                            store.NoteCategoryForHash(hash, category);
                        }
                        added++;
                        logger.Info("API", $"Added magnet: {magnet[..Math.Min(60, magnet.Length)]}...");
                    }
                    catch (Exception ex)
                    {
                        logger.Error("API", $"Failed to add magnet: {ex.Message}");
                    }
                }

                // Add torrent files
                foreach (var fileBytes in torrentFiles)
                {
                    try
                    {
                        // Save to a temp file since AddTorrentFile expects a file path
                        string tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".torrent");
                        await File.WriteAllBytesAsync(tempPath, fileBytes);
                        try
                        {
                            string hash = await engine.AddTorrentFile(tempPath, category, savePath);
                            if (!string.IsNullOrEmpty(category))
                            {
                                store.NoteCategoryForHash(hash, category);
                            }
                            added++;
                            logger.Info("API", $"Added torrent file ({fileBytes.Length} bytes)");
                        }
                        finally
                        {
                            try { File.Delete(tempPath); } catch { /* best effort cleanup */ }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Error("API", $"Failed to add torrent file: {ex.Message}");
                    }
                }

                return added > 0 ? Results.Ok() : Results.BadRequest("No torrents added");
            });

            // ── Pause ──────────────────────────────────────────────
            app.MapPost("/api/v2/torrents/pause", async (HttpContext ctx) =>
            {
                var form = await FormParser.ParseForm(ctx.Request);
                string hashStr = form.GetValueOrDefault("hashes", "");
                var hashList = ParsePipeSeparatedHashes(hashStr);

                foreach (string hash in hashList)
                    await engine.Pause(hash);

                return Results.Ok();
            });

            // ── Resume ─────────────────────────────────────────────
            app.MapPost("/api/v2/torrents/resume", async (HttpContext ctx) =>
            {
                var form = await FormParser.ParseForm(ctx.Request);
                string hashStr = form.GetValueOrDefault("hashes", "");
                var hashList = ParsePipeSeparatedHashes(hashStr);

                foreach (string hash in hashList)
                    await engine.Resume(hash);

                return Results.Ok();
            });

            // ── Delete ─────────────────────────────────────────────
            app.MapPost("/api/v2/torrents/delete", async (HttpContext ctx) =>
            {
                var form = await FormParser.ParseForm(ctx.Request);
                string hashStr = form.GetValueOrDefault("hashes", "");
                string deleteFilesStr = form.GetValueOrDefault("deleteFiles", "false");
                bool deleteFiles = deleteFilesStr == "true" || deleteFilesStr == "1";
                var hashList = ParsePipeSeparatedHashes(hashStr);

                foreach (string hash in hashList)
                {
                    await engine.Remove(hash, deleteFiles);
                    store.NoteCategoryForHash(hash, null);
                    logger.Info("API", $"Removed torrent {hash[..Math.Min(8, hash.Length)]}... deleteFiles={deleteFiles}");
                }

                return Results.Ok();
            });

            // ── Categories ─────────────────────────────────────────
            app.MapGet("/api/v2/torrents/categories", (HttpContext ctx) =>
            {
                var categories = store.GetCategories();
                var dict = new Dictionary<string, object>();
                foreach (var cat in categories)
                {
                    dict[cat.Name] = new { name = cat.Name, savePath = cat.SavePath };
                }
                return Results.Json(dict, JsonOpts);
            });

            app.MapPost("/api/v2/torrents/createCategory", async (HttpContext ctx) =>
            {
                var form = await FormParser.ParseForm(ctx.Request);
                string name = form.GetValueOrDefault("category", "");
                string savePath = form.GetValueOrDefault("savePath", "");

                if (string.IsNullOrWhiteSpace(name))
                    return Results.BadRequest("Missing category name");

                var existing = store.GetCategory(name);
                if (existing != null)
                    return Results.Conflict();

                store.UpsertCategory(new Category { Name = name, SavePath = savePath });
                logger.Info("API", $"Created category: {name}");
                return Results.Ok();
            });

            app.MapPost("/api/v2/torrents/editCategory", async (HttpContext ctx) =>
            {
                var form = await FormParser.ParseForm(ctx.Request);
                string name = form.GetValueOrDefault("category", "");
                string savePath = form.GetValueOrDefault("savePath", "");

                var existing = store.GetCategory(name);
                if (existing == null)
                    return Results.NotFound();

                existing.SavePath = savePath;
                store.UpsertCategory(existing);
                return Results.Ok();
            });

            app.MapPost("/api/v2/torrents/removeCategories", async (HttpContext ctx) =>
            {
                var form = await FormParser.ParseForm(ctx.Request);
                string categoriesStr = form.GetValueOrDefault("categories", "");
                var names = categoriesStr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(n => n.Trim())
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToList();

                foreach (var name in names)
                {
                    store.RemoveCategory(name);
                }
                return Results.Ok();
            });

            app.MapPost("/api/v2/torrents/setCategory", async (HttpContext ctx) =>
            {
                var form = await FormParser.ParseForm(ctx.Request);
                string hashStr = form.GetValueOrDefault("hashes", "");
                string category = form.GetValueOrDefault("category", "");
                var hashList = ParsePipeSeparatedHashes(hashStr);

                foreach (string hash in hashList)
                {
                    if (string.IsNullOrEmpty(category))
                        store.NoteCategoryForHash(hash, null);
                    else
                        store.NoteCategoryForHash(hash, category);
                }
                return Results.Ok();
            });

            // ── Files ──────────────────────────────────────────────
            app.MapGet("/api/v2/torrents/files", (HttpContext ctx) =>
            {
                var qs = FormParser.ParseQuery(ctx.Request.QueryString.Value);
                string hash = qs.GetValueOrDefault("hash", "");

                if (string.IsNullOrEmpty(hash))
                    return Results.BadRequest("Missing hash");

                var files = engine.GetFileInfo(hash);
                if (files == null)
                    return Results.NotFound();

                var result = files.Select((f, i) => new Dictionary<string, object>
                {
                    ["index"] = f.Index,
                    ["name"] = f.Name,
                    ["size"] = f.Size,
                    ["progress"] = 0.0f,
                    ["priority"] = f.Priority,
                    ["is_seed"] = false,
                    ["piece_range"] = Array.Empty<int>(),
                    ["availability"] = 0.0f
                }).ToList();

                return Results.Json(result, JsonOpts);
            });

            // ── Trackers ───────────────────────────────────────────
            app.MapGet("/api/v2/torrents/trackers", (HttpContext ctx) =>
            {
                var qs = FormParser.ParseQuery(ctx.Request.QueryString.Value);
                string hash = qs.GetValueOrDefault("hash", "");

                if (string.IsNullOrEmpty(hash))
                    return Results.BadRequest("Missing hash");

                var trackers = engine.GetTrackers(hash);
                if (trackers == null)
                    return Results.NotFound();

                var result = trackers.Select(tr => new Dictionary<string, object>
                {
                    ["url"] = tr.Url,
                    ["status"] = tr.Status,
                    ["tier"] = tr.Tier,
                    ["num_peers"] = tr.NumPeers,
                    ["num_seeds"] = tr.NumSeeds,
                    ["num_leeches"] = tr.NumLeechers,
                    ["num_downloaded"] = tr.NumDownloaded,
                    ["msg"] = tr.Message ?? ""
                }).ToList();

                return Results.Json(result, JsonOpts);
            });

            // ── Piece states ───────────────────────────────────────
            app.MapGet("/api/v2/torrents/pieceStates", (HttpContext ctx) =>
            {
                return Results.Json(Array.Empty<int>(), JsonOpts);
            });
        }

        // ================================================================
        //  Controllarr-Native Routes
        // ================================================================

        private static void MapControllarrNativeRoutes(
            WebApplication app,
            TorrentEngine engine,
            PersistenceStore store,
            Logger logger,
            PostProcessor postProcessor,
            SeedingPolicy seedingPolicy,
            HealthMonitor healthMonitor,
            RecoveryCenter recovery,
            DiskSpaceMonitor diskSpace,
            VPNMonitor vpn,
            ArrNotifier arrNotifier,
            Action forceCyclePort)
        {
            // ── Stats ──────────────────────────────────────────────
            app.MapGet("/api/controllarr/stats", (HttpContext ctx) =>
            {
                var stats = engine.GetSessionStats();
                var all = engine.PollStats();
                var result = new Dictionary<string, object>
                {
                    ["download_rate"] = stats.DownloadRate,
                    ["upload_rate"] = stats.UploadRate,
                    ["total_downloaded"] = stats.TotalDownloaded,
                    ["total_uploaded"] = stats.TotalUploaded,
                    ["dht_nodes"] = 0,
                    ["listen_port"] = stats.ListenPort,
                    ["torrent_count"] = all.Length,
                    ["downloading_count"] = all.Count(t => t.State == TorrentState.Downloading),
                    ["seeding_count"] = all.Count(t => t.State == TorrentState.Seeding),
                    ["paused_count"] = all.Count(t => t.Paused),
                    ["uptime_seconds"] = (int)(DateTime.UtcNow - StartedAt).TotalSeconds
                };
                return Results.Json(result, JsonOpts);
            });

            // ── Port cycle ─────────────────────────────────────────
            app.MapPost("/api/controllarr/port/cycle", (HttpContext ctx) =>
            {
                forceCyclePort();
                logger.Info("API", "Manual port cycle requested");
                return Results.Ok(new { status = "port_cycle_initiated" });
            });

            // ── Categories (extended) ──────────────────────────────
            app.MapGet("/api/controllarr/categories", (HttpContext ctx) =>
            {
                var categories = store.GetCategories();
                return Results.Json(categories, JsonOpts);
            });

            app.MapPost("/api/controllarr/categories", async (HttpContext ctx) =>
            {
                using var reader = new StreamReader(ctx.Request.Body);
                string body = await reader.ReadToEndAsync();
                var incoming = JsonSerializer.Deserialize<Category>(body, JsonOpts);
                if (incoming == null || string.IsNullOrWhiteSpace(incoming.Name))
                    return Results.BadRequest("Invalid category");

                store.UpsertCategory(incoming);
                logger.Info("API", $"Category saved: {incoming.Name}");
                return Results.Ok(incoming);
            });

            app.MapDelete("/api/controllarr/categories/{name}", (string name, HttpContext ctx) =>
            {
                var existing = store.GetCategory(name);
                if (existing == null)
                    return Results.NotFound();

                store.RemoveCategory(name);

                // Also remove category assignments pointing to this category
                var snapshot = store.Snapshot();
                var keysToRemove = snapshot.CategoryByHash
                    .Where(kv => string.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase))
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var key in keysToRemove)
                    store.NoteCategoryForHash(key, null);

                logger.Info("API", $"Category deleted: {name}");
                return Results.Ok();
            });

            // ── Settings ───────────────────────────────────────────
            app.MapGet("/api/controllarr/settings", (HttpContext ctx) =>
            {
                var settings = store.GetSettings();
                return Results.Json(settings, JsonOpts);
            });

            app.MapPost("/api/controllarr/settings", async (HttpContext ctx) =>
            {
                using var reader = new StreamReader(ctx.Request.Body);
                string body = await reader.ReadToEndAsync();
                var incoming = JsonSerializer.Deserialize<Settings>(body, JsonOpts);
                if (incoming == null)
                    return Results.BadRequest("Invalid settings JSON");

                store.ReplaceSettings(incoming);
                logger.Info("API", "Settings updated via API");
                return Results.Ok(incoming);
            });

            // ── Backup ─────────────────────────────────────────────
            app.MapGet("/api/controllarr/backup", (HttpContext ctx) =>
            {
                string backupJson = store.ExportBackup(includeSecrets: true);
                return Results.Text(backupJson, "application/json");
            });

            app.MapPost("/api/controllarr/backup/import", async (HttpContext ctx) =>
            {
                using var reader = new StreamReader(ctx.Request.Body);
                string body = await reader.ReadToEndAsync();

                try
                {
                    store.ImportBackup(body);
                    logger.Info("API", "Backup imported successfully");
                    return Results.Ok(new { status = "imported" });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest($"Failed to import backup: {ex.Message}");
                }
            });

            // ── Health ─────────────────────────────────────────────
            app.MapGet("/api/controllarr/health", (HttpContext ctx) =>
            {
                var issues = healthMonitor.Snapshot();
                var result = issues.Select(i => new Dictionary<string, object>
                {
                    ["info_hash"] = i.InfoHash,
                    ["name"] = i.Name,
                    ["reason"] = i.Reason.ToString(),
                    ["first_seen"] = i.FirstSeen.ToString("o"),
                    ["last_progress"] = i.LastProgress,
                    ["last_updated"] = i.LastUpdated.ToString("o")
                }).ToList();
                return Results.Json(result, JsonOpts);
            });

            app.MapPost("/api/controllarr/health/clear", async (HttpContext ctx) =>
            {
                var form = await FormParser.ParseForm(ctx.Request);
                string hash = form.GetValueOrDefault("hash", "");

                if (string.IsNullOrEmpty(hash))
                    return Results.BadRequest("Missing hash");

                healthMonitor.ClearIssue(hash);
                logger.Info("API", $"Health issue cleared for {hash[..Math.Min(8, hash.Length)]}...");
                return Results.Ok();
            });

            // ── Recovery ───────────────────────────────────────────
            app.MapGet("/api/controllarr/recovery", (HttpContext ctx) =>
            {
                var log = recovery.Snapshot();
                return Results.Json(log, JsonOpts);
            });

            app.MapPost("/api/controllarr/recovery/run", async (HttpContext ctx) =>
            {
                var form = await FormParser.ParseForm(ctx.Request);
                string hash = form.GetValueOrDefault("hash", "");

                if (string.IsNullOrEmpty(hash))
                    return Results.BadRequest("Missing hash");

                recovery.RunRecovery(hash, null, new EngineAdapter(engine), postProcessor);
                logger.Info("API", $"Manual recovery triggered for {hash[..Math.Min(8, hash.Length)]}...");
                return Results.Ok(new { status = "recovery_initiated" });
            });

            // ── Post-processor ─────────────────────────────────────
            app.MapGet("/api/controllarr/postprocessor", (HttpContext ctx) =>
            {
                var records = postProcessor.Snapshot();
                return Results.Json(records, JsonOpts);
            });

            app.MapPost("/api/controllarr/postprocessor/retry", async (HttpContext ctx) =>
            {
                var form = await FormParser.ParseForm(ctx.Request);
                string hash = form.GetValueOrDefault("hash", "");

                if (string.IsNullOrEmpty(hash))
                    return Results.BadRequest("Missing hash");

                postProcessor.Retry(hash);
                logger.Info("API", $"Post-processor retry for {hash[..Math.Min(8, hash.Length)]}...");
                return Results.Ok(new { status = "retry_initiated" });
            });

            // ── Seeding ────────────────────────────────────────────
            app.MapGet("/api/controllarr/seeding", (HttpContext ctx) =>
            {
                var log = seedingPolicy.Snapshot();
                return Results.Json(log, JsonOpts);
            });

            // ── Disk space ─────────────────────────────────────────
            app.MapGet("/api/controllarr/diskspace", (HttpContext ctx) =>
            {
                var status = diskSpace.Snapshot();
                return Results.Json(status, JsonOpts);
            });

            app.MapPost("/api/controllarr/diskspace/recheck", (HttpContext ctx) =>
            {
                diskSpace.Recheck();
                return Results.Ok(new { status = "recheck_initiated" });
            });

            // ── VPN ────────────────────────────────────────────────
            app.MapGet("/api/controllarr/vpn", (HttpContext ctx) =>
            {
                var status = vpn.Snapshot();
                return Results.Json(status, JsonOpts);
            });

            // ── Arr ────────────────────────────────────────────────
            app.MapGet("/api/controllarr/arr", (HttpContext ctx) =>
            {
                var log = arrNotifier.Snapshot();
                return Results.Json(log, JsonOpts);
            });

            // ── Log ────────────────────────────────────────────────
            app.MapGet("/api/controllarr/log", (HttpContext ctx) =>
            {
                var qs = FormParser.ParseQuery(ctx.Request.QueryString.Value);
                int limit = 100;
                if (qs.TryGetValue("limit", out string? limitStr) && int.TryParse(limitStr, out int parsed))
                    limit = Math.Clamp(parsed, 1, 500);

                var entries = logger.Snapshot(limit);
                var result = entries.Select(e => new Dictionary<string, object>
                {
                    ["id"] = e.Id.ToString(),
                    ["timestamp"] = e.Timestamp.ToString("o"),
                    ["level"] = e.Level.ToString().ToLowerInvariant(),
                    ["source"] = e.Source,
                    ["message"] = e.Message
                }).ToList();
                return Results.Json(result, JsonOpts);
            });

            // ── Torrent files (Controllarr-native) ─────────────────
            app.MapGet("/api/controllarr/torrents/{hash}/files", (string hash, HttpContext ctx) =>
            {
                var files = engine.GetFileInfo(hash);
                if (files == null)
                    return Results.NotFound();

                return Results.Json(files, JsonOpts);
            });

            app.MapPost("/api/controllarr/torrents/{hash}/files", async (string hash, HttpContext ctx) =>
            {
                using var reader = new StreamReader(ctx.Request.Body);
                string body = await reader.ReadToEndAsync();

                var priorityMap = JsonSerializer.Deserialize<Dictionary<int, int>>(body, JsonOpts);
                if (priorityMap == null)
                    return Results.BadRequest("Invalid priorities JSON");

                // Convert the index->priority map to an int[] ordered by index
                var files = engine.GetFileInfo(hash);
                if (files == null)
                    return Results.NotFound();

                var priorities = new int[files.Length];
                for (int i = 0; i < files.Length; i++)
                {
                    priorities[i] = priorityMap.TryGetValue(i, out int prio) ? prio : files[i].Priority;
                }

                await engine.SetFilePriorities(priorities, hash);
                logger.Info("API", $"File priorities updated for {hash[..Math.Min(8, hash.Length)]}...");
                return Results.Ok();
            });

            // ── Torrent trackers (Controllarr-native) ──────────────
            app.MapGet("/api/controllarr/torrents/{hash}/trackers", (string hash, HttpContext ctx) =>
            {
                var trackers = engine.GetTrackers(hash);
                if (trackers == null)
                    return Results.NotFound();

                return Results.Json(trackers, JsonOpts);
            });

            // ── Torrent peers (Controllarr-native) ─────────────────
            app.MapGet("/api/controllarr/torrents/{hash}/peers", (string hash, HttpContext ctx) =>
            {
                var peers = engine.GetPeers(hash);
                if (peers == null)
                    return Results.NotFound();

                return Results.Json(peers, JsonOpts);
            });
        }

        // ================================================================
        //  Helpers
        // ================================================================

        /// <summary>
        /// Maps a TorrentStats to the qBittorrent JSON object shape that
        /// Sonarr/Radarr expect when calling /api/v2/torrents/info.
        /// </summary>
        private static Dictionary<string, object?> MapTorrentToQBit(TorrentStats t, PersistedState state)
        {
            state.CategoryByHash.TryGetValue(t.InfoHash, out string? cat);

            return new Dictionary<string, object?>
            {
                ["hash"] = t.InfoHash,
                ["name"] = t.Name,
                ["size"] = t.TotalWanted,
                ["total_size"] = t.TotalWanted,
                ["progress"] = t.Progress,
                ["dlspeed"] = t.DownloadRate,
                ["upspeed"] = t.UploadRate,
                ["num_seeds"] = t.NumSeeds,
                ["num_leech"] = t.NumPeers,
                ["ratio"] = t.Ratio,
                ["eta"] = CalculateEta(t),
                ["state"] = MapState(t),
                ["category"] = cat ?? "",
                ["save_path"] = t.SavePath,
                ["added_on"] = new DateTimeOffset(t.AddedDate).ToUnixTimeSeconds(),
                ["completion_on"] = t.Progress >= 0.999f
                    ? new DateTimeOffset(t.AddedDate).ToUnixTimeSeconds()
                    : -1,
                ["downloaded"] = t.TotalDownload,
                ["uploaded"] = t.TotalUpload,
                ["priority"] = 0,
                ["seq_dl"] = false,
                ["f_l_piece_prio"] = false,
                ["force_start"] = false,
                ["super_seeding"] = false,
                ["auto_tmm"] = true
            };
        }

        /// <summary>
        /// Maps internal TorrentState + runtime stats to a qBittorrent state string.
        /// </summary>
        private static string MapState(TorrentStats t)
        {
            if (t.Paused && t.Progress >= 1.0f)
                return "pausedUP";
            if (t.Paused)
                return "pausedDL";
            if (t.State == TorrentState.DownloadingMetadata)
                return "metaDL";
            if (t.State == TorrentState.CheckingFiles)
                return "checkingDL";
            if (t.State == TorrentState.Downloading && t.DownloadRate > 0)
                return "downloading";
            if (t.State == TorrentState.Downloading)
                return "stalledDL";
            if (t.State == TorrentState.Seeding && t.UploadRate > 0)
                return "uploading";
            if (t.State == TorrentState.Seeding)
                return "stalledUP";

            return "unknown";
        }

        /// <summary>
        /// Calculates ETA in seconds. Returns 8640000 (infinity) when no rate.
        /// </summary>
        private static long CalculateEta(TorrentStats t)
        {
            if (t.Progress >= 1.0f)
                return 0;
            if (t.DownloadRate <= 0)
                return 8640000; // qBit infinity sentinel

            long remaining = (long)(t.TotalWanted * (1.0 - t.Progress));
            return remaining / t.DownloadRate;
        }

        /// <summary>
        /// Parses pipe-separated hash strings. "all" returns an empty list
        /// (caller should treat as "all torrents").
        /// </summary>
        private static List<string> ParsePipeSeparatedHashes(string hashStr)
        {
            if (string.IsNullOrWhiteSpace(hashStr))
                return new List<string>();

            return hashStr
                .Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(h => h.Trim().ToLowerInvariant())
                .Where(h => !string.IsNullOrEmpty(h))
                .ToList();
        }

        /// <summary>
        /// Builds a qBittorrent-compatible preferences JSON dictionary.
        /// </summary>
        private static Dictionary<string, object?> BuildPreferencesJson(Settings s)
        {
            return new Dictionary<string, object?>
            {
                ["locale"] = "en",
                ["save_path"] = s.DefaultSavePath,
                ["temp_path_enabled"] = false,
                ["temp_path"] = "",
                ["listen_port"] = (int)s.ListenPortRangeStart,
                ["upnp"] = false,
                ["random_port"] = false,
                ["max_connec"] = 500,
                ["max_connec_per_torrent"] = 100,
                ["max_uploads"] = -1,
                ["max_uploads_per_torrent"] = -1,
                ["dl_limit"] = 0,
                ["up_limit"] = 0,
                ["max_ratio_enabled"] = s.GlobalMaxRatio.HasValue,
                ["max_ratio"] = s.GlobalMaxRatio ?? -1.0,
                ["max_seeding_time_enabled"] = s.GlobalMaxSeedingTimeMinutes.HasValue,
                ["max_seeding_time"] = s.GlobalMaxSeedingTimeMinutes ?? -1,
                ["max_ratio_act"] = (int)s.SeedLimitAction,
                ["dht"] = true,
                ["pex"] = true,
                ["lsd"] = true,
                ["encryption"] = 0,
                ["web_ui_address"] = s.WebUIHost,
                ["web_ui_port"] = s.WebUIPort,
                ["web_ui_username"] = s.WebUIUsername,
                ["alternative_webui_enabled"] = false,
                ["alternative_webui_path"] = "",
                ["queueing_enabled"] = false,
                ["max_active_downloads"] = -1,
                ["max_active_uploads"] = -1,
                ["max_active_torrents"] = -1,
                ["auto_tmm_enabled"] = true,
                ["torrent_changed_tmm_enabled"] = false,
                ["add_trackers_enabled"] = false,
                ["add_trackers"] = "",
                ["preallocate_all"] = false,
                ["incomplete_files_ext"] = false,
                ["create_subfolder_enabled"] = true
            };
        }

        // ================================================================
        //  ITorrentEngine adapter for RecoveryCenter interop
        // ================================================================

        /// <summary>
        /// Lightweight adapter bridging <see cref="TorrentEngine"/> to the
        /// <see cref="ITorrentEngine"/> interface required by RecoveryCenter.
        /// </summary>
        private sealed class EngineAdapter : ITorrentEngine
        {
            private readonly TorrentEngine _inner;

            public EngineAdapter(TorrentEngine inner) => _inner = inner;

            public System.Collections.Generic.IReadOnlyList<TorrentView> GetTorrents()
            {
                var stats = _inner.PollStats();
                var views = new System.Collections.Generic.List<TorrentView>(stats.Length);
                foreach (var t in stats)
                {
                    views.Add(new TorrentView
                    {
                        InfoHash = t.InfoHash,
                        Name = t.Name,
                        State = t.State,
                        Progress = t.Progress,
                        Ratio = t.Ratio,
                        NumPeers = t.NumPeers,
                        HasMetadata = t.State != TorrentState.DownloadingMetadata,
                        Category = t.Category,
                        ContentPath = t.SavePath,
                        SavePath = t.SavePath,
                        DownloadedBytes = t.TotalDownload,
                        UploadedBytes = t.TotalUpload,
                        TotalBytes = t.TotalWanted,
                        DownloadRateBytes = (int)t.DownloadRate,
                        UploadRateBytes = (int)t.UploadRate,
                        AddedOn = t.AddedDate
                    });
                }
                return views;
            }

            public void PauseTorrent(string infoHash) =>
                _inner.Pause(infoHash).GetAwaiter().GetResult();

            public void ResumeTorrent(string infoHash) =>
                _inner.Resume(infoHash).GetAwaiter().GetResult();

            public void RemoveTorrent(string infoHash, bool deleteFiles) =>
                _inner.Remove(infoHash, deleteFiles).GetAwaiter().GetResult();

            public void MoveStorage(string infoHash, string destinationPath) =>
                _inner.Move(infoHash, destinationPath).GetAwaiter().GetResult();

            public void SetRateLimits(int downloadKBps, int uploadKBps) =>
                _inner.SetRateLimits(
                    downloadKBps == 0 ? null : downloadKBps,
                    uploadKBps == 0 ? null : uploadKBps);

            public void BindToAddress(string? ipAddress)
            {
                // Placeholder – actual VPN bind is handled by VPNMonitor
            }

            public void Reannounce(string infoHash) =>
                _inner.Reannounce(infoHash).GetAwaiter().GetResult();
        }
    }
}
