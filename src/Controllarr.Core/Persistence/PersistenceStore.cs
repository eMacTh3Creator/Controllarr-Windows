using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Controllarr.Core.Persistence
{
    // ────────────────────────────────────────────────────────────────
    // Backup archive model (for export / import)
    // ────────────────────────────────────────────────────────────────

    public sealed class BackupArchive
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("created_at")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("state")]
        public PersistedState State { get; set; } = new();

        public BackupArchive() { }
    }

    // ────────────────────────────────────────────────────────────────
    // Thread-safe persistence store with debounced JSON writes
    // ────────────────────────────────────────────────────────────────

    public sealed class PersistenceStore : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };

        private const int DebounceMs = 250;

        // ── Paths ──────────────────────────────────────────────────

        public string Directory { get; }
        public string StateFilePath { get; }
        public string ResumeDirectory { get; }

        // ── State ──────────────────────────────────────────────────

        private PersistedState _state;
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private CancellationTokenSource? _debounceCts;
        private bool _disposed;

        // ────────────────────────────────────────────────────────────
        // Constructor
        // ────────────────────────────────────────────────────────────

        public PersistenceStore(string? directory = null)
        {
            Directory = directory
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Controllarr");

            StateFilePath = Path.Combine(Directory, "state.json");
            ResumeDirectory = Path.Combine(Directory, "resume");

            // Ensure directories exist
            System.IO.Directory.CreateDirectory(Directory);
            System.IO.Directory.CreateDirectory(ResumeDirectory);

            // Load or create default state
            _state = LoadOrCreateState();
        }

        // ────────────────────────────────────────────────────────────
        // Snapshot
        // ────────────────────────────────────────────────────────────

        /// <summary>Returns a deep-copy snapshot of the entire persisted state.</summary>
        public PersistedState Snapshot()
        {
            _semaphore.Wait();
            try
            {
                return DeepClone(_state);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        // ────────────────────────────────────────────────────────────
        // Settings accessors
        // ────────────────────────────────────────────────────────────

        public Settings GetSettings()
        {
            _semaphore.Wait();
            try
            {
                return DeepClone(_state.Settings);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public void UpdateSettings(Action<Settings> transform)
        {
            if (transform is null) throw new ArgumentNullException(nameof(transform));

            _semaphore.Wait();
            try
            {
                transform(_state.Settings);
            }
            finally
            {
                _semaphore.Release();
            }

            ScheduleSave();
        }

        public void ReplaceSettings(Settings newSettings)
        {
            if (newSettings is null) throw new ArgumentNullException(nameof(newSettings));

            _semaphore.Wait();
            try
            {
                _state.Settings = newSettings;
            }
            finally
            {
                _semaphore.Release();
            }

            ScheduleSave();
        }

        // ────────────────────────────────────────────────────────────
        // Category accessors
        // ────────────────────────────────────────────────────────────

        public List<Category> GetCategories()
        {
            _semaphore.Wait();
            try
            {
                return _state.Categories
                    .Select(c => DeepClone(c))
                    .ToList();
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public Category? GetCategory(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            _semaphore.Wait();
            try
            {
                var cat = _state.Categories
                    .FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                return cat is not null ? DeepClone(cat) : null;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public string? GetSavePath(string categoryName)
        {
            if (string.IsNullOrEmpty(categoryName)) return null;

            _semaphore.Wait();
            try
            {
                var cat = _state.Categories
                    .FirstOrDefault(c => string.Equals(c.Name, categoryName, StringComparison.OrdinalIgnoreCase));
                return cat?.SavePath;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public void UpsertCategory(Category category)
        {
            if (category is null) throw new ArgumentNullException(nameof(category));

            _semaphore.Wait();
            try
            {
                int idx = _state.Categories
                    .FindIndex(c => string.Equals(c.Name, category.Name, StringComparison.OrdinalIgnoreCase));

                if (idx >= 0)
                    _state.Categories[idx] = category;
                else
                    _state.Categories.Add(category);
            }
            finally
            {
                _semaphore.Release();
            }

            ScheduleSave();
        }

        public void RemoveCategory(string name)
        {
            if (string.IsNullOrEmpty(name)) return;

            _semaphore.Wait();
            try
            {
                _state.Categories.RemoveAll(
                    c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                _semaphore.Release();
            }

            ScheduleSave();
        }

        public void ReplaceCategories(List<Category> newCategories)
        {
            if (newCategories is null) throw new ArgumentNullException(nameof(newCategories));

            _semaphore.Wait();
            try
            {
                _state.Categories = newCategories;
            }
            finally
            {
                _semaphore.Release();
            }

            ScheduleSave();
        }

        // ────────────────────────────────────────────────────────────
        // Category-by-hash map
        // ────────────────────────────────────────────────────────────

        public void SetCategoryMap(Dictionary<string, string> map)
        {
            if (map is null) throw new ArgumentNullException(nameof(map));

            _semaphore.Wait();
            try
            {
                _state.CategoryByHash = new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
            }
            finally
            {
                _semaphore.Release();
            }

            ScheduleSave();
        }

        public void NoteCategoryForHash(string hash, string? category)
        {
            if (string.IsNullOrEmpty(hash)) return;

            _semaphore.Wait();
            try
            {
                if (string.IsNullOrEmpty(category))
                    _state.CategoryByHash.Remove(hash);
                else
                    _state.CategoryByHash[hash] = category;
            }
            finally
            {
                _semaphore.Release();
            }

            ScheduleSave();
        }

        // ────────────────────────────────────────────────────────────
        // Last known good port
        // ────────────────────────────────────────────────────────────

        public void SetLastKnownGoodPort(ushort? port)
        {
            _semaphore.Wait();
            try
            {
                _state.LastKnownGoodPort = port;
            }
            finally
            {
                _semaphore.Release();
            }

            ScheduleSave();
        }

        // ────────────────────────────────────────────────────────────
        // Backup / Restore
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Exports the current state as a JSON backup string.
        /// When <paramref name="includeSecrets"/> is false, API keys and
        /// passwords are redacted.
        /// </summary>
        public string ExportBackup(bool includeSecrets)
        {
            PersistedState snapshot;

            _semaphore.Wait();
            try
            {
                snapshot = DeepClone(_state);
            }
            finally
            {
                _semaphore.Release();
            }

            if (!includeSecrets)
            {
                snapshot.Settings.WebUIPassword = "***REDACTED***";
                foreach (var ep in snapshot.Settings.ArrEndpoints)
                    ep.ApiKey = "***REDACTED***";
            }

            var archive = new BackupArchive
            {
                Version = 1,
                CreatedAt = DateTime.UtcNow,
                State = snapshot
            };

            return JsonSerializer.Serialize(archive, JsonOptions);
        }

        /// <summary>
        /// Imports state from a JSON backup string, replacing all current state.
        /// </summary>
        public void ImportBackup(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("Backup JSON is empty.", nameof(json));

            var archive = JsonSerializer.Deserialize<BackupArchive>(json, JsonOptions);
            if (archive?.State is null)
                throw new InvalidOperationException("Invalid backup archive: missing state.");

            _semaphore.Wait();
            try
            {
                _state = archive.State;
            }
            finally
            {
                _semaphore.Release();
            }

            // Write immediately – don't debounce a restore
            FlushNow();
        }

        // ────────────────────────────────────────────────────────────
        // Flush
        // ────────────────────────────────────────────────────────────

        /// <summary>Immediately writes the current state to disk (synchronous).</summary>
        public void FlushNow()
        {
            _semaphore.Wait();
            try
            {
                CancelPendingDebounce();
                WriteToDisk(_state);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        // ────────────────────────────────────────────────────────────
        // IDisposable
        // ────────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Final flush
            try
            {
                FlushNow();
            }
            catch
            {
                // Best-effort during dispose
            }

            CancelPendingDebounce();
            _semaphore.Dispose();
        }

        // ────────────────────────────────────────────────────────────
        // Debounced save internals
        // ────────────────────────────────────────────────────────────

        private void ScheduleSave()
        {
            if (_disposed) return;

            CancelPendingDebounce();

            var cts = new CancellationTokenSource();
            _debounceCts = cts;

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(DebounceMs, cts.Token);

                    if (cts.Token.IsCancellationRequested)
                        return;

                    await _semaphore.WaitAsync(cts.Token);
                    try
                    {
                        WriteToDisk(_state);
                    }
                    finally
                    {
                        _semaphore.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    // Debounce was superseded by a newer save — expected
                }
                catch (ObjectDisposedException)
                {
                    // Store was disposed during the delay — expected
                }
            });
        }

        private void CancelPendingDebounce()
        {
            var old = Interlocked.Exchange(ref _debounceCts, null);
            if (old is not null)
            {
                try
                {
                    old.Cancel();
                    old.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // Already disposed — harmless
                }
            }
        }

        // ────────────────────────────────────────────────────────────
        // Disk I/O
        // ────────────────────────────────────────────────────────────

        private PersistedState LoadOrCreateState()
        {
            if (!File.Exists(StateFilePath))
            {
                var defaultState = new PersistedState();
                WriteToDisk(defaultState);
                return defaultState;
            }

            try
            {
                string json = File.ReadAllText(StateFilePath);
                var state = JsonSerializer.Deserialize<PersistedState>(json, JsonOptions);
                return state ?? new PersistedState();
            }
            catch
            {
                // Corrupted file — start fresh but keep a backup
                try
                {
                    string backupPath = StateFilePath + ".corrupt." + DateTime.UtcNow.Ticks;
                    File.Copy(StateFilePath, backupPath, overwrite: true);
                }
                catch
                {
                    // Best-effort backup
                }

                var freshState = new PersistedState();
                WriteToDisk(freshState);
                return freshState;
            }
        }

        private void WriteToDisk(PersistedState state)
        {
            string json = JsonSerializer.Serialize(state, JsonOptions);

            // Atomic write: write to temp file, then rename
            string tempPath = StateFilePath + ".tmp";
            File.WriteAllText(tempPath, json);

            // On Windows, File.Move with overwrite requires .NET 6+
            File.Move(tempPath, StateFilePath, overwrite: true);
        }

        // ────────────────────────────────────────────────────────────
        // Deep clone via round-trip serialization
        // ────────────────────────────────────────────────────────────

        private static T DeepClone<T>(T obj)
        {
            string json = JsonSerializer.Serialize(obj, JsonOptions);
            return JsonSerializer.Deserialize<T>(json, JsonOptions)!;
        }
    }
}
