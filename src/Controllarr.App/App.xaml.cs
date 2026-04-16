using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

using Controllarr.Core.Engine;
using Controllarr.Core.Persistence;
using Controllarr.Core.Services;

namespace Controllarr.App
{
    public partial class App : Application
    {
        // ── Single-instance ────────────────────────────────────────
        private const string MutexName = "Global\\Controllarr_SingleInstance_Mutex";
        private Mutex? _singleInstanceMutex;

        // ── Runtime services ───────────────────────────────────────
        private PersistenceStore? _store;
        private TorrentEngine? _engine;

        // ── Startup args ───────────────────────────────────────────
        private string[] _pendingTorrentFiles = Array.Empty<string>();
        private string[] _pendingMagnets = Array.Empty<string>();
        private bool _startMinimized;

        // ────────────────────────────────────────────────────────────
        // OnStartup
        // ────────────────────────────────────────────────────────────

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // ── Single-instance check ──────────────────────────────
            _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                MessageBox.Show(
                    "Controllarr is already running.",
                    "Controllarr",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
                Shutdown(0);
                return;
            }

            // ── Parse command-line arguments ───────────────────────
            ParseCommandLineArgs(e.Args);

            // ── Register as magnet: URI handler ────────────────────
            RegisterMagnetProtocol();
        }

        // ────────────────────────────────────────────────────────────
        // OnExit
        // ────────────────────────────────────────────────────────────

        protected override void OnExit(ExitEventArgs e)
        {
            // Shut down the engine gracefully
            if (_engine != null)
            {
                try
                {
                    _engine.Shutdown().GetAwaiter().GetResult();
                }
                catch
                {
                    // Best-effort on exit
                }
            }

            _store?.Dispose();

            // Release single-instance mutex
            if (_singleInstanceMutex != null)
            {
                try
                {
                    _singleInstanceMutex.ReleaseMutex();
                }
                catch
                {
                    // May not be owned if startup failed early
                }
                _singleInstanceMutex.Dispose();
            }

            base.OnExit(e);
        }

        // ────────────────────────────────────────────────────────────
        // Public accessors for MainViewModel
        // ────────────────────────────────────────────────────────────

        public PersistenceStore? Store => _store;
        public TorrentEngine? Engine => _engine;
        public string[] PendingTorrentFiles => _pendingTorrentFiles;
        public string[] PendingMagnets => _pendingMagnets;
        public bool StartMinimized => _startMinimized;

        /// <summary>
        /// Called by MainViewModel after it creates the runtime services.
        /// </summary>
        public void SetRuntime(PersistenceStore store, TorrentEngine engine)
        {
            _store = store;
            _engine = engine;
        }

        // ────────────────────────────────────────────────────────────
        // Command-line argument parsing
        // ────────────────────────────────────────────────────────────

        private void ParseCommandLineArgs(string[] args)
        {
            var torrentFiles = args
                .Where(a => a.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) && File.Exists(a))
                .ToArray();

            var magnets = args
                .Where(a => a.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            _startMinimized = args.Any(a =>
                a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

            _pendingTorrentFiles = torrentFiles;
            _pendingMagnets = magnets;
        }

        // ────────────────────────────────────────────────────────────
        // Magnet protocol registration
        // ────────────────────────────────────────────────────────────

        private static void RegisterMagnetProtocol()
        {
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                if (string.IsNullOrEmpty(exePath))
                    return;

                using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\magnet");
                if (key == null) return;

                key.SetValue(string.Empty, "URL:Magnet Protocol");
                key.SetValue("URL Protocol", string.Empty);

                using var shellKey = key.CreateSubKey(@"shell\open\command");
                shellKey?.SetValue(string.Empty, $"\"{exePath}\" \"%1\"");
            }
            catch
            {
                // Non-critical: user may not have registry access
            }
        }
    }
}
