using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Controllarr.Core.Services
{
    // ────────────────────────────────────────────────────────────────
    // Log level
    // ────────────────────────────────────────────────────────────────

    public enum LogLevel
    {
        Debug,
        Info,
        Warn,
        Error
    }

    // ────────────────────────────────────────────────────────────────
    // Single log entry
    // ────────────────────────────────────────────────────────────────

    public sealed class LogEntry
    {
        public Guid Id { get; }
        public DateTime Timestamp { get; }
        public LogLevel Level { get; }
        public string Source { get; }
        public string Message { get; }

        public LogEntry(LogLevel level, string source, string message)
        {
            Id = Guid.NewGuid();
            Timestamp = DateTime.UtcNow;
            Level = level;
            Source = source;
            Message = message;
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Thread-safe ring-buffer logger (singleton) with an optional
    // persistent on-disk sink.
    //
    // The in-memory ring buffer is lost when the app (or the whole
    // machine) goes down, which is exactly when we most want the record.
    // Mirroring entries to disk — and flushing to the OS frequently —
    // keeps a post-mortem trail that survives crashes and reboots.
    // ────────────────────────────────────────────────────────────────

    public sealed class Logger
    {
        public const int DefaultCapacity = 500;

        private static readonly Lazy<Logger> _instance =
            new Lazy<Logger>(() => new Logger(DefaultCapacity), LazyThreadSafetyMode.ExecutionAndPublication);

        public static Logger Instance => _instance.Value;

        private readonly LogEntry[] _buffer;
        private readonly int _capacity;
        private readonly object _lock = new();
        private int _head;   // next write position
        private int _count;  // entries currently stored

        // ── Persistent file sink ────────────────────────────────────
        private const long MaxFileBytes = 5 * 1024 * 1024;   // rotate at ~5 MB, keep one old file
        private FileStream? _fileStream;
        private StreamWriter? _writer;
        private long _bytesWritten;
        private int _linesSinceFlush;

        /// <summary>
        /// Absolute path of the on-disk log, if persistence is enabled.
        /// Exposed so the UI can offer a "Reveal Log File" affordance.
        /// Null until <see cref="ConfigureFile"/> succeeds.
        /// </summary>
        public string? LogFilePath { get; private set; }

        public Logger(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
            _buffer = new LogEntry[_capacity];
            _head = 0;
            _count = 0;
        }

        // Private so the singleton ctor is the only public path.
        // Tests can use the int-capacity ctor directly.

        // ── Public API ──────────────────────────────────────────────

        /// <summary>
        /// Enables the persistent on-disk log at <paramref name="path"/>.
        /// Opens the file in append mode (so the trail survives across
        /// runs) and creates the parent directory if needed. Safe to call
        /// once at startup; failures degrade gracefully to in-memory only.
        /// </summary>
        public void ConfigureFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            lock (_lock)
            {
                try
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);

                    _fileStream = new FileStream(
                        path, FileMode.Append, FileAccess.Write, FileShare.Read);
                    _writer = new StreamWriter(_fileStream) { AutoFlush = false };
                    _bytesWritten = _fileStream.Length;
                    _linesSinceFlush = 0;
                    LogFilePath = Path.GetFullPath(path);
                }
                catch
                {
                    // Persistence is best-effort; keep running in-memory only.
                    _writer = null;
                    _fileStream = null;
                    LogFilePath = null;
                }
            }
        }

        public void Debug(string source, string message) => Append(LogLevel.Debug, source, message);
        public void Info(string source, string message) => Append(LogLevel.Info, source, message);
        public void Warn(string source, string message) => Append(LogLevel.Warn, source, message);
        public void Error(string source, string message) => Append(LogLevel.Error, source, message);

        /// <summary>
        /// Returns the latest <paramref name="limit"/> entries in chronological order
        /// (oldest first). If <paramref name="limit"/> exceeds the stored count,
        /// all stored entries are returned.
        /// </summary>
        public List<LogEntry> Snapshot(int limit = DefaultCapacity)
        {
            if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));

            lock (_lock)
            {
                int take = Math.Min(limit, _count);
                var result = new List<LogEntry>(take);

                // The oldest entry we want is (take) positions behind _head.
                int start = (_head - take + _capacity) % _capacity;

                for (int i = 0; i < take; i++)
                {
                    int idx = (start + i) % _capacity;
                    result.Add(_buffer[idx]);
                }

                return result;
            }
        }

        /// <summary>Clears all in-memory entries. The on-disk log is left intact.</summary>
        public void Clear()
        {
            lock (_lock)
            {
                Array.Clear(_buffer, 0, _capacity);
                _head = 0;
                _count = 0;
            }
        }

        // ── Internals ───────────────────────────────────────────────

        private void Append(LogLevel level, string source, string message)
        {
            var entry = new LogEntry(level, source, message);

            lock (_lock)
            {
                _buffer[_head] = entry;
                _head = (_head + 1) % _capacity;
                if (_count < _capacity) _count++;

                WriteToFile(entry);
            }
        }

        // Caller must hold _lock.
        private void WriteToFile(LogEntry entry)
        {
            if (_writer == null || _fileStream == null) return;

            try
            {
                string line = string.Create(CultureInfo.InvariantCulture,
                    $"{entry.Timestamp:yyyy-MM-ddTHH:mm:ss.fffZ} [{entry.Level.ToString().ToUpperInvariant()}] [{entry.Source}] {entry.Message}");
                _writer.Write(line);
                _writer.Write('\n');
                _bytesWritten += line.Length + 1;
                _linesSinceFlush++;

                // Flush to the OS on every warning/error and otherwise every few
                // lines, so a crash / power loss drops as little as possible.
                if (entry.Level >= LogLevel.Warn || _linesSinceFlush >= 5)
                {
                    _writer.Flush();
                    _fileStream.Flush(flushToDisk: true);
                    _linesSinceFlush = 0;
                }

                if (_bytesWritten >= MaxFileBytes)
                    Rotate();
            }
            catch
            {
                // Stop writing on persistent error; keep the in-memory buffer.
                try { _writer?.Dispose(); } catch { }
                _writer = null;
                _fileStream = null;
            }
        }

        // Caller must hold _lock.
        private void Rotate()
        {
            if (LogFilePath == null) return;

            try
            {
                _writer?.Flush();
                _writer?.Dispose();
                _writer = null;
                _fileStream = null;

                string backup = LogFilePath + ".1";
                if (File.Exists(backup)) File.Delete(backup);
                File.Move(LogFilePath, backup);

                _fileStream = new FileStream(
                    LogFilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                _writer = new StreamWriter(_fileStream) { AutoFlush = false };
                _bytesWritten = 0;
                _linesSinceFlush = 0;
            }
            catch
            {
                _writer = null;
                _fileStream = null;
            }
        }
    }
}
