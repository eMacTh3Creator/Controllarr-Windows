using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using SharpCompress.Archives;
using SharpCompress.Common;

using Controllarr.Core.Engine;
using Controllarr.Core.Persistence;

namespace Controllarr.Core.Services
{
    // ────────────────────────────────────────────────────────────────
    // Post-processing pipeline stages
    // ────────────────────────────────────────────────────────────────

    public enum PostStage
    {
        Pending,
        MovingStorage,
        Extracting,
        Done,
        Failed
    }

    // ────────────────────────────────────────────────────────────────
    // Per-torrent record tracking post-processing state
    // ────────────────────────────────────────────────────────────────

    public sealed class PostRecord
    {
        public string InfoHash { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public PostStage Stage { get; set; }
        public string Message { get; set; }
        public DateTime LastUpdated { get; set; }

        public PostRecord(string infoHash, string name, string category)
        {
            InfoHash = infoHash;
            Name = name;
            Category = category;
            Stage = PostStage.Pending;
            Message = string.Empty;
            LastUpdated = DateTime.UtcNow;
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Post-processor: move storage + extract archives after download
    // ────────────────────────────────────────────────────────────────

    public sealed class PostProcessor
    {
        private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".rar", ".zip", ".7z",
            ".r00", ".r01", ".r02", ".r03", // multipart rar
            ".part1.rar", ".part01.rar"
        };

        private readonly Dictionary<string, PostRecord> _records = new();
        private readonly object _lock = new();
        private readonly Logger _logger;

        public PostProcessor(Logger? logger = null)
        {
            _logger = logger ?? Logger.Instance;
        }

        /// <summary>
        /// Evaluate all torrents and advance post-processing state machines.
        /// </summary>
        public void Tick(IReadOnlyList<TorrentView> torrents,
                         IReadOnlyList<Category> categories,
                         ITorrentEngine engine)
        {
            lock (_lock)
            {
                foreach (var t in torrents)
                {
                    // Only process torrents that are finished downloading
                    if (t.Progress < 0.999f)
                        continue;

                    if (!_records.TryGetValue(t.InfoHash, out var record))
                    {
                        // New completed torrent – create a pending record
                        string catName = t.Category ?? string.Empty;
                        record = new PostRecord(t.InfoHash, t.Name, catName);
                        _records[t.InfoHash] = record;
                        _logger.Info("PostProcessor",
                            $"Torrent completed, queued for post-processing: {t.Name}");
                    }

                    AdvanceStateMachine(record, t, categories, engine);
                }
            }
        }

        /// <summary>Returns a snapshot of all post-processing records.</summary>
        public List<PostRecord> Snapshot()
        {
            lock (_lock)
            {
                return _records.Values.ToList();
            }
        }

        /// <summary>Returns the record for a specific hash, or null.</summary>
        public PostRecord? Record(string infoHash)
        {
            lock (_lock)
            {
                return _records.TryGetValue(infoHash, out var r) ? r : null;
            }
        }

        /// <summary>Retry a failed post-processing record.</summary>
        public bool Retry(string infoHash)
        {
            lock (_lock)
            {
                if (_records.TryGetValue(infoHash, out var record) && record.Stage == PostStage.Failed)
                {
                    record.Stage = PostStage.Pending;
                    record.Message = "Retry requested";
                    record.LastUpdated = DateTime.UtcNow;
                    _logger.Info("PostProcessor",
                        $"Retry requested for: {record.Name}");
                    return true;
                }
                return false;
            }
        }

        // ── State machine ───────────────────────────────────────────

        private void AdvanceStateMachine(PostRecord record,
                                         TorrentView torrent,
                                         IReadOnlyList<Category> categories,
                                         ITorrentEngine engine)
        {
            switch (record.Stage)
            {
                case PostStage.Pending:
                    HandlePending(record, torrent, categories, engine);
                    break;

                case PostStage.MovingStorage:
                    HandleMovingStorage(record, torrent, categories, engine);
                    break;

                case PostStage.Extracting:
                    // Extraction runs synchronously so this state is set
                    // and resolved within HandleMovingStorage/HandlePending.
                    break;

                case PostStage.Done:
                case PostStage.Failed:
                    // Terminal states – no action
                    break;
            }
        }

        private void HandlePending(PostRecord record,
                                   TorrentView torrent,
                                   IReadOnlyList<Category> categories,
                                   ITorrentEngine engine)
        {
            var category = FindCategory(record.Category, categories);
            string? completePath = category?.CompletePath;

            if (!string.IsNullOrWhiteSpace(completePath))
            {
                // Move storage to the complete path
                try
                {
                    record.Stage = PostStage.MovingStorage;
                    record.Message = $"Moving to {completePath}";
                    record.LastUpdated = DateTime.UtcNow;

                    engine.MoveStorage(torrent.InfoHash, completePath!);

                    _logger.Info("PostProcessor",
                        $"Moving storage for {record.Name} -> {completePath}");
                }
                catch (Exception ex)
                {
                    record.Stage = PostStage.Failed;
                    record.Message = $"Move failed: {ex.Message}";
                    record.LastUpdated = DateTime.UtcNow;
                    _logger.Error("PostProcessor",
                        $"Move failed for {record.Name}: {ex.Message}");
                }
            }
            else
            {
                // No move needed – go straight to extraction check
                TryExtractOrComplete(record, torrent, category);
            }
        }

        private void HandleMovingStorage(PostRecord record,
                                         TorrentView torrent,
                                         IReadOnlyList<Category> categories,
                                         ITorrentEngine engine)
        {
            // Check if the engine reports the move as finished
            if (torrent.IsMovingStorage)
                return; // still in progress

            var category = FindCategory(record.Category, categories);
            TryExtractOrComplete(record, torrent, category);
        }

        private void TryExtractOrComplete(PostRecord record, TorrentView torrent, Category? category)
        {
            bool shouldExtract = category?.ExtractArchives ?? false;

            if (shouldExtract)
            {
                record.Stage = PostStage.Extracting;
                record.Message = "Extracting archives";
                record.LastUpdated = DateTime.UtcNow;

                try
                {
                    int extracted = ExtractArchives(torrent.ContentPath);
                    record.Stage = PostStage.Done;
                    record.Message = extracted > 0
                        ? $"Extracted {extracted} archive(s)"
                        : "No archives found to extract";
                    record.LastUpdated = DateTime.UtcNow;
                    _logger.Info("PostProcessor",
                        $"Extraction complete for {record.Name}: {record.Message}");
                }
                catch (Exception ex)
                {
                    record.Stage = PostStage.Failed;
                    record.Message = $"Extraction failed: {ex.Message}";
                    record.LastUpdated = DateTime.UtcNow;
                    _logger.Error("PostProcessor",
                        $"Extraction failed for {record.Name}: {ex.Message}");
                }
            }
            else
            {
                record.Stage = PostStage.Done;
                record.Message = "Post-processing complete";
                record.LastUpdated = DateTime.UtcNow;
                _logger.Info("PostProcessor",
                    $"Post-processing complete for {record.Name}");
            }
        }

        // ── Archive extraction ──────────────────────────────────────

        private int ExtractArchives(string contentPath)
        {
            if (string.IsNullOrWhiteSpace(contentPath))
                return 0;

            // contentPath may be a single file or a directory
            var archiveFiles = new List<string>();

            if (File.Exists(contentPath))
            {
                if (IsArchiveFile(contentPath))
                    archiveFiles.Add(contentPath);
            }
            else if (Directory.Exists(contentPath))
            {
                archiveFiles.AddRange(
                    Directory.EnumerateFiles(contentPath, "*", SearchOption.AllDirectories)
                             .Where(IsArchiveFile));
            }

            // Filter out multipart rar parts that aren't the first part.
            // SharpCompress handles multipart automatically from the first volume.
            archiveFiles = archiveFiles
                .Where(f => !IsSecondaryRarPart(f))
                .ToList();

            int count = 0;
            foreach (var archivePath in archiveFiles)
            {
                string extractDir = Path.GetDirectoryName(archivePath)!;

                using var archive = ArchiveFactory.Open(archivePath);
                foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                {
                    string destPath = Path.Combine(extractDir, entry.Key ?? "unknown");

                    // Don't overwrite if already extracted
                    if (File.Exists(destPath))
                        continue;

                    // Ensure destination directory exists
                    string? destDir = Path.GetDirectoryName(destPath);
                    if (destDir != null && !Directory.Exists(destDir))
                        Directory.CreateDirectory(destDir);

                    entry.WriteToDirectory(extractDir, new ExtractionOptions
                    {
                        ExtractFullPath = true,
                        Overwrite = false
                    });
                }

                count++;
                _logger.Debug("PostProcessor", $"Extracted: {archivePath}");
            }

            return count;
        }

        private static bool IsArchiveFile(string filePath)
        {
            string ext = Path.GetExtension(filePath);
            if (ArchiveExtensions.Contains(ext))
                return true;

            // Check compound extensions like .part1.rar
            string name = Path.GetFileName(filePath);
            foreach (var archiveExt in ArchiveExtensions)
            {
                if (name.EndsWith(archiveExt, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns true for secondary RAR parts (e.g. .r01, .r02, .part2.rar)
        /// that SharpCompress should not be opened independently.
        /// </summary>
        private static bool IsSecondaryRarPart(string filePath)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();

            // .r00, .r01, .r02 etc. – these are secondary volumes in old-style RAR
            if (ext.Length == 4 && ext.StartsWith(".r") && char.IsDigit(ext[2]) && char.IsDigit(ext[3]))
                return true;

            // .part2.rar, .part03.rar etc.
            string name = Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant();
            if (ext == ".rar")
            {
                // E.g. "archive.part02" → check if part number > 1
                int partIdx = name.LastIndexOf(".part", StringComparison.Ordinal);
                if (partIdx >= 0)
                {
                    string numStr = name[(partIdx + 5)..];
                    if (int.TryParse(numStr, out int partNum) && partNum > 1)
                        return true;
                }
            }

            return false;
        }

        private static Category? FindCategory(string categoryName,
                                               IReadOnlyList<Category> categories)
        {
            if (string.IsNullOrEmpty(categoryName))
                return null;

            return categories.FirstOrDefault(
                c => string.Equals(c.Name, categoryName, StringComparison.OrdinalIgnoreCase));
        }
    }
}
