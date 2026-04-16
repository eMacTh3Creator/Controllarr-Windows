using System.Collections.Generic;

namespace Controllarr.Core.Engine
{
    // ────────────────────────────────────────────────────────────────
    // Torrent engine abstraction used by all service-layer components
    // ────────────────────────────────────────────────────────────────

    public interface ITorrentEngine
    {
        /// <summary>Returns a snapshot of all managed torrents.</summary>
        IReadOnlyList<TorrentView> GetTorrents();

        /// <summary>Pause a torrent by info hash.</summary>
        void PauseTorrent(string infoHash);

        /// <summary>Resume a torrent by info hash.</summary>
        void ResumeTorrent(string infoHash);

        /// <summary>Remove a torrent. Optionally deletes downloaded files.</summary>
        void RemoveTorrent(string infoHash, bool deleteFiles);

        /// <summary>Move the on-disk storage of a torrent to a new path.</summary>
        void MoveStorage(string infoHash, string destinationPath);

        /// <summary>
        /// Set global download/upload rate limits in KBps.
        /// A value of 0 means unlimited.
        /// </summary>
        void SetRateLimits(int downloadKBps, int uploadKBps);

        /// <summary>
        /// Bind the engine's listening socket to a specific local IP address.
        /// Pass null to unbind / listen on all addresses.
        /// </summary>
        void BindToAddress(string? ipAddress);

        /// <summary>Reannounce a torrent to its trackers.</summary>
        void Reannounce(string infoHash);
    }
}
