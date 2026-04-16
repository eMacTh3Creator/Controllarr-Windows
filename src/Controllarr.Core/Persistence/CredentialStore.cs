using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Controllarr.Core.Persistence
{
    // ────────────────────────────────────────────────────────────────
    // DPAPI-backed credential storage for Windows
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Stores sensitive credentials encrypted with Windows DPAPI
    /// (Data Protection API) scoped to the current user.
    /// Credentials are persisted as a JSON dictionary of
    /// base64-encoded DPAPI-encrypted values in %AppData%/Controllarr/credentials.dat.
    /// </summary>
    public static class CredentialStore
    {
        // ── Well-known keys ────────────────────────────────────────

        public const string WebUIPasswordKey = "webui_password";

        // ── Paths ──────────────────────────────────────────────────

        private static readonly string StoreDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Controllarr");

        private static readonly string StoreFilePath = Path.Combine(StoreDirectory, "credentials.dat");

        // ── Thread safety ──────────────────────────────────────────

        private static readonly object Lock = new();

        // ── JSON options ───────────────────────────────────────────

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        // ────────────────────────────────────────────────────────────
        // Public API
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Encrypts <paramref name="value"/> with DPAPI and stores it under <paramref name="key"/>.
        /// Overwrites any existing value for that key.
        /// </summary>
        public static void Set(string key, string value)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("Key must not be null or empty.", nameof(key));
            if (value is null)
                throw new ArgumentNullException(nameof(value));

            lock (Lock)
            {
                var store = LoadStore();

                byte[] plainBytes = Encoding.UTF8.GetBytes(value);
                byte[] encryptedBytes = ProtectedData.Protect(
                    plainBytes,
                    optionalEntropy: null,
                    scope: DataProtectionScope.CurrentUser);

                store[key] = Convert.ToBase64String(encryptedBytes);

                SaveStore(store);
            }
        }

        /// <summary>
        /// Retrieves and decrypts the value stored under <paramref name="key"/>.
        /// Returns <c>null</c> if the key does not exist or decryption fails.
        /// </summary>
        public static string? Get(string key)
        {
            if (string.IsNullOrEmpty(key))
                return null;

            lock (Lock)
            {
                var store = LoadStore();

                if (!store.TryGetValue(key, out string? base64Value) || string.IsNullOrEmpty(base64Value))
                    return null;

                try
                {
                    byte[] encryptedBytes = Convert.FromBase64String(base64Value);
                    byte[] plainBytes = ProtectedData.Unprotect(
                        encryptedBytes,
                        optionalEntropy: null,
                        scope: DataProtectionScope.CurrentUser);

                    return Encoding.UTF8.GetString(plainBytes);
                }
                catch (CryptographicException)
                {
                    // Decryption failed — credential was stored by a different user
                    // or the DPAPI keys have been rotated. Remove the stale entry.
                    store.Remove(key);
                    SaveStore(store);
                    return null;
                }
                catch (FormatException)
                {
                    // Corrupted base64 data — remove stale entry.
                    store.Remove(key);
                    SaveStore(store);
                    return null;
                }
            }
        }

        /// <summary>
        /// Removes the credential stored under <paramref name="key"/>.
        /// No-op if the key does not exist.
        /// </summary>
        public static void Delete(string key)
        {
            if (string.IsNullOrEmpty(key))
                return;

            lock (Lock)
            {
                var store = LoadStore();

                if (store.Remove(key))
                    SaveStore(store);
            }
        }

        // ────────────────────────────────────────────────────────────
        // Internal I/O
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Loads the credential dictionary from disk.
        /// Returns an empty dictionary if the file does not exist or is corrupt.
        /// </summary>
        private static Dictionary<string, string> LoadStore()
        {
            if (!File.Exists(StoreFilePath))
                return new Dictionary<string, string>(StringComparer.Ordinal);

            try
            {
                string json = File.ReadAllText(StoreFilePath);

                if (string.IsNullOrWhiteSpace(json))
                    return new Dictionary<string, string>(StringComparer.Ordinal);

                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
                if (dict is null)
                    return new Dictionary<string, string>(StringComparer.Ordinal);

                // Re-create with explicit comparer
                return new Dictionary<string, string>(dict, StringComparer.Ordinal);
            }
            catch
            {
                // Corrupted file — start fresh
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }

        /// <summary>
        /// Atomically writes the credential dictionary to disk.
        /// </summary>
        private static void SaveStore(Dictionary<string, string> store)
        {
            Directory.CreateDirectory(StoreDirectory);

            string json = JsonSerializer.Serialize(store, JsonOptions);

            // Atomic write: temp file + rename
            string tempPath = StoreFilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, StoreFilePath, overwrite: true);
        }
    }
}
