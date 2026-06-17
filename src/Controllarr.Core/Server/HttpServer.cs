using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

using Controllarr.Core.Engine;
using Controllarr.Core.Persistence;
using Controllarr.Core.Services;

namespace Controllarr.Core.Server
{
    /// <summary>
    /// Embedded ASP.NET Core Kestrel HTTP server that exposes the
    /// qBittorrent-compatible API and Controllarr-native endpoints.
    /// </summary>
    public sealed class ControllarrHttpServer
    {
        private readonly string _host;
        private readonly int _port;

        // ── Service references ──────────────────────────────────────
        private readonly TorrentEngine _engine;
        private readonly PersistenceStore _store;
        private readonly Logger _logger;
        private readonly PostProcessor _postProcessor;
        private readonly SeedingPolicy _seedingPolicy;
        private readonly HealthMonitor _healthMonitor;
        private readonly RecoveryCenter _recovery;
        private readonly DiskSpaceMonitor _diskSpace;
        private readonly VPNMonitor _vpn;
        private readonly ArrNotifier _arrNotifier;
        private readonly Action _forceCyclePort;
        private readonly string? _webUIRoot;
        private readonly Action? _shutdownApp;

        // ── Session state ───────────────────────────────────────────
        private readonly ConcurrentDictionary<string, DateTime> _sessions = new();
        private const int MaxSessions = 50;

        // ── Runtime ─────────────────────────────────────────────────
        private WebApplication? _app;

        public ControllarrHttpServer(
            string host,
            int port,
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
            string? webUIRoot = null,
            Action? shutdownApp = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _port = port;
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _postProcessor = postProcessor ?? throw new ArgumentNullException(nameof(postProcessor));
            _seedingPolicy = seedingPolicy ?? throw new ArgumentNullException(nameof(seedingPolicy));
            _healthMonitor = healthMonitor ?? throw new ArgumentNullException(nameof(healthMonitor));
            _recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
            _diskSpace = diskSpace ?? throw new ArgumentNullException(nameof(diskSpace));
            _vpn = vpn ?? throw new ArgumentNullException(nameof(vpn));
            _arrNotifier = arrNotifier ?? throw new ArgumentNullException(nameof(arrNotifier));
            _forceCyclePort = forceCyclePort ?? throw new ArgumentNullException(nameof(forceCyclePort));
            _webUIRoot = webUIRoot;
            _shutdownApp = shutdownApp;
        }

        // ────────────────────────────────────────────────────────────
        // Start / Stop
        // ────────────────────────────────────────────────────────────

        public async Task StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();

            // Configure Kestrel
            builder.WebHost.ConfigureKestrel(options =>
            {
                // We emit our own Server header ("Controllarr/<version>").
                options.AddServerHeader = false;

                if (_host == "0.0.0.0" || _host == "::" || _host == "*")
                {
                    options.ListenAnyIP(_port);
                }
                else if (System.Net.IPAddress.TryParse(_host, out var ip))
                {
                    options.Listen(ip, _port);
                }
                else
                {
                    // Hostname – resolve or fall back to any
                    options.ListenAnyIP(_port);
                }
            });

            _app = builder.Build();

            // ── CORS: allow all origins ─────────────────────────────
            _app.Use(async (ctx, next) =>
            {
                ctx.Response.Headers.Append("Access-Control-Allow-Origin", "*");
                ctx.Response.Headers.Append("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS");
                ctx.Response.Headers.Append("Access-Control-Allow-Headers", "Content-Type, Authorization, X-Requested-With");
                ctx.Response.Headers.Append("Access-Control-Allow-Credentials", "true");

                if (ctx.Request.Method == "OPTIONS")
                {
                    ctx.Response.StatusCode = 204;
                    return;
                }

                await next();
            });

            // ── Security headers + Server identity + IP allowlist ────
            string serverVersion =
                Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
                ?? typeof(ControllarrHttpServer).Assembly.GetName().Version?.ToString(3)
                ?? "2.1.15";

            _app.Use(async (ctx, next) =>
            {
                var sec = _store.GetSettings().WebUISecurity;

                // Identify ourselves rather than leaking Kestrel's version.
                ctx.Response.Headers["Server"] = $"Controllarr/{serverVersion}";

                // Baseline hardening.
                ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
                ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
                if (sec.ClickjackingProtection)
                {
                    ctx.Response.Headers["X-Frame-Options"] = "DENY";
                    ctx.Response.Headers["Content-Security-Policy"] = "frame-ancestors 'none'";
                }

                // Optional IP allowlist. Loopback is always permitted so the
                // local app and *arr on the same box never lock themselves out.
                if (sec.AllowlistEnabled &&
                    !IsRemoteAllowed(ctx.Connection.RemoteIpAddress, sec.AllowedCIDRs))
                {
                    ctx.Response.StatusCode = 403;
                    await ctx.Response.WriteAsync("Forbidden (not in allowlist)");
                    return;
                }

                await next();
            });

            // ── Request body buffering ──────────────────────────────
            // Enable rewinding so FormParser can re-read the body
            _app.Use(async (ctx, next) =>
            {
                ctx.Request.EnableBuffering();
                await next();
            });

            // ── Session-based auth middleware ────────────────────────
            _app.Use(async (ctx, next) =>
            {
                string path = ctx.Request.Path.Value ?? "";

                // Auth is required for /api/* routes except login
                bool isApiRoute = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
                bool isLoginRoute = path.Equals("/api/v2/auth/login", StringComparison.OrdinalIgnoreCase);

                if (isApiRoute && !isLoginRoute)
                {
                    if (!ctx.Request.Cookies.TryGetValue("SID", out string? sid) ||
                        string.IsNullOrEmpty(sid) ||
                        !_sessions.TryGetValue(sid, out DateTime expiry) ||
                        expiry < DateTime.UtcNow)
                    {
                        // Invalid or expired session
                        if (sid != null)
                            _sessions.TryRemove(sid, out _);

                        ctx.Response.StatusCode = 403;
                        await ctx.Response.WriteAsync("Forbidden");
                        return;
                    }

                    // Slide the expiry on valid access
                    _sessions[sid] = DateTime.UtcNow.AddHours(1);
                }

                await next();
            });

            // ── Credential validator ────────────────────────────────
            Func<string, string, bool> validateCredentials = (username, password) =>
            {
                var settings = _store.GetSettings();
                return string.Equals(username, settings.WebUIUsername, StringComparison.Ordinal) &&
                       string.Equals(password, settings.WebUIPassword, StringComparison.Ordinal);
            };

            // ── Map all API routes ──────────────────────────────────
            _app.MapControllarrRoutes(
                _engine, _store, _logger, _postProcessor, _seedingPolicy,
                _healthMonitor, _recovery, _diskSpace, _vpn, _arrNotifier,
                _forceCyclePort, _shutdownApp, _sessions, validateCredentials);

            // ── Serve static files (WebUI) ──────────────────────────
            if (!string.IsNullOrEmpty(_webUIRoot) && Directory.Exists(_webUIRoot))
            {
                var fullRoot = Path.GetFullPath(_webUIRoot);
                var fileProvider = new PhysicalFileProvider(fullRoot);

                _app.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = fileProvider,
                    RequestPath = ""
                });

                // SPA fallback: serve index.html for any non-API, non-file path
                string indexPath = Path.Combine(fullRoot, "index.html");
                _app.MapFallback(async (HttpContext ctx) =>
                {
                    if (ctx.Request.Path.StartsWithSegments("/api"))
                    {
                        ctx.Response.StatusCode = 404;
                        return;
                    }

                    if (File.Exists(indexPath))
                    {
                        ctx.Response.ContentType = "text/html";
                        await ctx.Response.SendFileAsync(indexPath);
                    }
                    else
                    {
                        ctx.Response.StatusCode = 404;
                    }
                });
            }

            _logger.Info("HttpServer", $"Starting on {_host}:{_port}");
            await _app.StartAsync();
            _logger.Info("HttpServer", $"Listening on {_host}:{_port}");
        }

        public async Task StopAsync()
        {
            if (_app != null)
            {
                _logger.Info("HttpServer", "Shutting down HTTP server");
                await _app.StopAsync();
                await _app.DisposeAsync();
                _app = null;
                _logger.Info("HttpServer", "HTTP server stopped");
            }
        }

        // ────────────────────────────────────────────────────────────
        // IP allowlist
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns true if the remote address is loopback (always allowed) or
        /// matches any configured CIDR. An empty/invalid list with the
        /// allowlist enabled allows only loopback.
        /// </summary>
        private static bool IsRemoteAllowed(IPAddress? remote, IReadOnlyList<string> cidrs)
        {
            if (remote == null) return false;
            if (IPAddress.IsLoopback(remote)) return true;

            // Normalize IPv4-mapped IPv6 (::ffff:a.b.c.d) to IPv4.
            if (remote.IsIPv4MappedToIPv6)
                remote = remote.MapToIPv4();

            if (cidrs == null) return false;

            foreach (var cidr in cidrs)
            {
                if (CidrMatch(remote, cidr))
                    return true;
            }
            return false;
        }

        private static bool CidrMatch(IPAddress address, string cidr)
        {
            if (string.IsNullOrWhiteSpace(cidr)) return false;

            try
            {
                var parts = cidr.Trim().Split('/');
                if (!IPAddress.TryParse(parts[0], out var network))
                    return false;

                // Bare address (no prefix) → exact match.
                if (parts.Length == 1)
                    return address.Equals(network);

                if (!int.TryParse(parts[1], out int prefix))
                    return false;

                if (network.AddressFamily != address.AddressFamily)
                    return false;

                byte[] netBytes = network.GetAddressBytes();
                byte[] addrBytes = address.GetAddressBytes();
                if (netBytes.Length != addrBytes.Length)
                    return false;

                int totalBits = netBytes.Length * 8;
                if (prefix < 0 || prefix > totalBits)
                    return false;

                int fullBytes = prefix / 8;
                int remainderBits = prefix % 8;

                for (int i = 0; i < fullBytes; i++)
                {
                    if (netBytes[i] != addrBytes[i])
                        return false;
                }

                if (remainderBits > 0)
                {
                    int mask = (byte)(0xFF << (8 - remainderBits));
                    if ((netBytes[fullBytes] & mask) != (addrBytes[fullBytes] & mask))
                        return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
