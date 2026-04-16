using System;
using System.Collections.Generic;
using System.Linq;
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
    // Thread-safe ring-buffer logger (singleton)
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

        /// <summary>Clears all stored entries.</summary>
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
            }
        }
    }
}
