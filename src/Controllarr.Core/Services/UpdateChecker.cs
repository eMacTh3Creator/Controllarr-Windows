using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Controllarr.Core.Services
{
    /// <summary>
    /// Result of an update check against the GitHub Releases API.
    /// </summary>
    public sealed class UpdateCheckResult
    {
        public bool UpdateAvailable { get; init; }
        public string CurrentVersion { get; init; } = "";
        public string LatestVersion { get; init; } = "";
        public string ReleaseUrl { get; init; } = UpdateChecker.ReleasesPage;
        public string? Error { get; init; }
    }

    /// <summary>
    /// Lightweight update checker. Queries the GitHub Releases API for the
    /// latest published release and compares its tag to the running assembly
    /// version. This is the Windows analog of the macOS app's Sparkle flow:
    /// it never installs anything silently — callers open the release page so
    /// the user downloads and runs the new <c>.exe</c> themselves.
    /// </summary>
    public sealed class UpdateChecker
    {
        private const string LatestReleaseApi =
            "https://api.github.com/repos/eMacTh3Creator/Controllarr-Windows/releases/latest";

        public const string ReleasesPage =
            "https://github.com/eMacTh3Creator/Controllarr-Windows/releases/latest";

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            // GitHub requires a User-Agent.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Controllarr-Windows-UpdateChecker");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        /// <summary>Running assembly version as a 3-part string (e.g. "2.1.15").</summary>
        public static string CurrentVersion =>
            Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
            ?? typeof(UpdateChecker).Assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";

        public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
        {
            string current = CurrentVersion;

            try
            {
                using var resp = await Http.GetAsync(LatestReleaseApi, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    return new UpdateCheckResult
                    {
                        CurrentVersion = current,
                        Error = $"HTTP {(int)resp.StatusCode}"
                    };
                }

                string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);

                string tag = doc.RootElement.TryGetProperty("tag_name", out var t)
                    ? (t.GetString() ?? "") : "";
                string url = doc.RootElement.TryGetProperty("html_url", out var u)
                    ? (u.GetString() ?? ReleasesPage) : ReleasesPage;

                string latest = tag.TrimStart('v', 'V');
                bool newer = CompareVersions(latest, current) > 0;

                return new UpdateCheckResult
                {
                    UpdateAvailable = newer,
                    CurrentVersion = current,
                    LatestVersion = latest,
                    ReleaseUrl = url
                };
            }
            catch (Exception ex)
            {
                return new UpdateCheckResult
                {
                    CurrentVersion = current,
                    Error = ex.Message
                };
            }
        }

        /// <summary>Returns &gt;0 if <paramref name="a"/> is a newer version than <paramref name="b"/>.</summary>
        private static int CompareVersions(string a, string b)
        {
            if (Version.TryParse(Normalize(a), out var va) &&
                Version.TryParse(Normalize(b), out var vb))
            {
                return va.CompareTo(vb);
            }
            return string.CompareOrdinal(a, b);
        }

        private static string Normalize(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return "0.0.0";
            // Strip any pre-release / build suffix (e.g. "2.1.15-beta1").
            int dash = v.IndexOf('-');
            if (dash >= 0) v = v.Substring(0, dash);
            // Version.TryParse needs at least major.minor.
            return v.Contains('.') ? v : v + ".0";
        }
    }
}
