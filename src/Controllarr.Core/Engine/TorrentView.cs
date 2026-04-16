using System;

namespace Controllarr.Core.Engine
{
    // ────────────────────────────────────────────────────────────────
    // Read-only view of a managed torrent (snapshot for services)
    // TorrentState enum is defined in TorrentEngine.cs
    // ────────────────────────────────────────────────────────────────

    public sealed class TorrentView
    {
        public string InfoHash { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public TorrentState State { get; set; }
        public float Progress { get; set; }
        public double Ratio { get; set; }
        public long SeedingTimeSeconds { get; set; }
        public int NumPeers { get; set; }
        public bool HasMetadata { get; set; } = true;
        public string? Category { get; set; }
        public string ContentPath { get; set; } = string.Empty;
        public string SavePath { get; set; } = string.Empty;
        public bool IsMovingStorage { get; set; }
        public long DownloadedBytes { get; set; }
        public long UploadedBytes { get; set; }
        public long TotalBytes { get; set; }
        public int DownloadRateBytes { get; set; }
        public int UploadRateBytes { get; set; }
        public DateTime AddedOn { get; set; }
        public DateTime? CompletedOn { get; set; }

        /// <summary>
        /// Request the engine to reannounce this torrent to trackers.
        /// Set by the engine when creating the view.
        /// </summary>
        private Action? _reannounceCallback;

        public void SetReannounceCallback(Action callback)
        {
            _reannounceCallback = callback;
        }

        public void RequestReannounce()
        {
            _reannounceCallback?.Invoke();
        }

        public TorrentView() { }
    }
}
