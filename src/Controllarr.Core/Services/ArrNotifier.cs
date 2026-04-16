using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;

using Controllarr.Core.Persistence;

namespace Controllarr.Core.Services
{
    // ────────────────────────────────────────────────────────────────
    // Notification record for an Arr API re-search command
    // ────────────────────────────────────────────────────────────────

    public sealed class ArrNotification
    {
        public string InfoHash { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        public ArrNotification() { }
    }

    // ────────────────────────────────────────────────────────────────
    // Arr notifier – triggers Sonarr/Radarr re-search for stalled
    // torrents that have exceeded the configured threshold
    // ────────────────────────────────────────────────────────────────

    public sealed class ArrNotifier : IDisposable
    {
        // ── Category keyword sets for Arr kind matching ────────────

        private static readonly string[] RadarrKeywords = { "movie", "radarr", "film" };
        private static readonly string[] SonarrKeywords = { "tv", "series", "sonarr", "show" };

        // ── State ──────────────────────────────────────────────────

        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;
        private readonly Func<Dictionary<string, string>> _categoryMapProvider;
        private readonly List<ArrNotification> _log = new();
        private readonly HashSet<string> _notifiedHashes = new();
        private readonly object _lock = new();
        private readonly Logger _logger;

        // ────────────────────────────────────────────────────────────
        // Constructor
        // ────────────────────────────────────────────────────────────

        /// <param name="categoryMapProvider">
        /// Returns the current hash->categoryName mapping so the notifier
        /// can determine which Arr endpoint(s) to target.
        /// </param>
        /// <param name="httpClient">
        /// Optional shared HttpClient. If null, a new instance is created
        /// and owned by this notifier.
        /// </param>
        public ArrNotifier(Func<Dictionary<string, string>> categoryMapProvider,
                           HttpClient? httpClient = null,
                           Logger? logger = null)
        {
            _categoryMapProvider = categoryMapProvider
                ?? throw new ArgumentNullException(nameof(categoryMapProvider));

            if (httpClient is not null)
            {
                _httpClient = httpClient;
                _ownsHttpClient = false;
            }
            else
            {
                _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                _ownsHttpClient = true;
            }

            _logger = logger ?? Logger.Instance;
        }

        // ────────────────────────────────────────────────────────────
        // Tick – called each main-loop iteration
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Evaluates health issues against the re-search threshold and
        /// sends API commands to the appropriate Arr endpoints.
        /// </summary>
        public void Tick(HealthMonitor healthMonitor, Settings settings)
        {
            if (healthMonitor is null) return;
            if (settings.ArrEndpoints.Count == 0) return;
            if (settings.ArrReSearchAfterHours <= 0) return;

            var issues = healthMonitor.Snapshot();
            var thresholdHours = settings.ArrReSearchAfterHours;
            var categoryMap = _categoryMapProvider();

            foreach (var issue in issues)
            {
                // Check if this issue has been stalled long enough
                double stalledHours = (DateTime.UtcNow - issue.FirstSeen).TotalHours;
                if (stalledHours < thresholdHours)
                    continue;

                lock (_lock)
                {
                    // Skip if already notified for this hash
                    if (_notifiedHashes.Contains(issue.InfoHash))
                        continue;
                }

                // Determine the category for this torrent
                categoryMap.TryGetValue(issue.InfoHash, out string? categoryName);

                // Determine which endpoints to notify
                var endpoints = ResolveEndpoints(categoryName, settings.ArrEndpoints);

                foreach (var endpoint in endpoints)
                {
                    var notification = SendReSearch(issue, endpoint);

                    lock (_lock)
                    {
                        _log.Add(notification);
                    }
                }

                lock (_lock)
                {
                    _notifiedHashes.Add(issue.InfoHash);
                }
            }
        }

        // ────────────────────────────────────────────────────────────
        // Snapshot
        // ────────────────────────────────────────────────────────────

        /// <summary>Returns a snapshot of the notification log.</summary>
        public List<ArrNotification> Snapshot()
        {
            lock (_lock)
            {
                return _log.ToList();
            }
        }

        // ────────────────────────────────────────────────────────────
        // IDisposable
        // ────────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_ownsHttpClient)
                _httpClient.Dispose();
        }

        // ────────────────────────────────────────────────────────────
        // Endpoint resolution
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Maps a category name to Arr endpoints. If the category name contains
        /// Radarr-associated keywords, only Radarr endpoints are returned; if it
        /// contains Sonarr-associated keywords, only Sonarr endpoints. If neither
        /// or no category is set, all endpoints are returned.
        /// </summary>
        private static List<ArrEndpoint> ResolveEndpoints(string? categoryName,
                                                           List<ArrEndpoint> allEndpoints)
        {
            if (string.IsNullOrEmpty(categoryName))
                return allEndpoints.ToList();

            string lowerCat = categoryName.ToLowerInvariant();

            bool matchesRadarr = RadarrKeywords.Any(kw => lowerCat.Contains(kw));
            bool matchesSonarr = SonarrKeywords.Any(kw => lowerCat.Contains(kw));

            // If the category matches one kind exclusively, filter to that kind
            if (matchesRadarr && !matchesSonarr)
                return allEndpoints.Where(ep => ep.Kind == ArrKind.Radarr).ToList();

            if (matchesSonarr && !matchesRadarr)
                return allEndpoints.Where(ep => ep.Kind == ArrKind.Sonarr).ToList();

            // Ambiguous or no match → send to all endpoints
            return allEndpoints.ToList();
        }

        // ────────────────────────────────────────────────────────────
        // API call
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Sends a re-search command to the given Arr endpoint.
        /// Sonarr receives {"name":"MissingEpisodeSearch"}, Radarr receives {"name":"MoviesSearch"}.
        /// </summary>
        private ArrNotification SendReSearch(HealthIssue issue, ArrEndpoint endpoint)
        {
            string commandName = endpoint.Kind == ArrKind.Sonarr
                ? "MissingEpisodeSearch"
                : "MoviesSearch";

            string url = endpoint.BaseURL.TrimEnd('/') + "/api/v3/command";
            string body = JsonSerializer.Serialize(new { name = commandName });

            var notification = new ArrNotification
            {
                InfoHash = issue.InfoHash,
                Name = issue.Name,
                Endpoint = $"{endpoint.Name} ({endpoint.Kind})",
                Timestamp = DateTime.UtcNow
            };

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Add("X-Api-Key", endpoint.ApiKey);
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

                var response = _httpClient.Send(request);

                if (response.IsSuccessStatusCode)
                {
                    notification.Success = true;
                    notification.Message = $"Re-search command sent ({commandName})";

                    _logger.Info("ArrNotifier",
                        $"Sent {commandName} to {endpoint.Name} for: {issue.Name} [{issue.InfoHash[..8]}...]");
                }
                else
                {
                    notification.Success = false;
                    notification.Message = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

                    _logger.Warn("ArrNotifier",
                        $"Failed {commandName} to {endpoint.Name}: HTTP {(int)response.StatusCode} for {issue.Name}");
                }
            }
            catch (Exception ex)
            {
                notification.Success = false;
                notification.Message = $"Error: {ex.Message}";

                _logger.Error("ArrNotifier",
                    $"Error sending {commandName} to {endpoint.Name} for {issue.Name}: {ex.Message}");
            }

            return notification;
        }
    }
}
