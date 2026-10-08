using Controllarr.Core.Networking;
using Controllarr.Core.Persistence;
using MonoTorrent.Client;
using MonoTorrent.Connections;
using MonoTorrent.PiecePicking;

namespace Controllarr.Core.Engine;

public sealed partial class TorrentEngine
{
    public TorrentNetworkPolicy NetworkPolicy { get; }
    private readonly SemaphoreSlim _advancedGate = new(1, 1);
    private int _defaultTorrentConnections = 60;
    private int _defaultUploadSlots = 8;
    private bool _pexEnabled = true;
    private int _downloadReservePercent = 25;
    private IReadOnlyDictionary<string, int> _seedConnectionCaps = new Dictionary<string, int>();
    private string? _advancedKey;

    public async Task ApplyAdvancedSettingsAsync(Settings settings)
    {
        await _advancedGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!NetworkPolicy.Matches(settings) && !NetworkPolicy.RestartRequired)
            {
                TorrentNetworkPolicy.Validate(settings);
                NetworkPolicy.RequireRestart();
                ApplyTuning(settings.ConnectionLimits.GlobalMaxConnections, false, false);
                await TickQueueAsync(false).ConfigureAwait(false);
                Services.Logger.Instance.Warn("Network", "Torrent networking blocked until restart: VPN/proxy/blocklist topology changed. LAN API remains available.");
            }
            NetworkPolicy.RefreshAdapter();
            if (!NetworkPolicy.Allowed) await TickQueueAsync(false).ConfigureAwait(false);
            string key = $"{settings.TorrentNetwork.Encryption}:{settings.ConnectionLimits.MaxConnectionsPerTorrent}:{settings.ConnectionLimits.GlobalMaxUploadSlots}:{settings.PeerDiscovery.PexEnabled}:{settings.ConnectionLimits.DownloadReservePercent}";
            if (_advancedKey == key) return;
            bool encryptionChanged;
            await _settingsGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var builder = new EngineSettingsBuilder(_engine.Settings)
                {
                    AllowedEncryption = settings.TorrentNetwork.Encryption switch
                    {
                        "Require" => new() { EncryptionType.RC4Full },
                        "Disable" => new() { EncryptionType.PlainText },
                        _ => new() { EncryptionType.RC4Full, EncryptionType.RC4Header, EncryptionType.PlainText }
                    }
                };
                encryptionChanged = !_engine.Settings.AllowedEncryption.SequenceEqual(builder.AllowedEncryption);
                await _engine.UpdateSettingsAsync(builder.ToSettings()).ConfigureAwait(false);
            }
            finally { _settingsGate.Release(); }
            if (encryptionChanged) NetworkPolicy.ResetConnections();
            await _queueGate.WaitAsync().ConfigureAwait(false);
            try
            {
                _defaultTorrentConnections = settings.ConnectionLimits.MaxConnectionsPerTorrent;
                // Legacy global_max_upload_slots is a per-torrent default: MonoTorrent has no aggregate slot limiter.
                _defaultUploadSlots = settings.ConnectionLimits.GlobalMaxUploadSlots;
                _pexEnabled = settings.PeerDiscovery.PexEnabled;
                _downloadReservePercent = settings.ConnectionLimits.DownloadReservePercent;
                foreach (var pair in _managersByHash) await ApplyTorrentOptionsAsync(pair.Value, OptionsFor(pair.Key)).ConfigureAwait(false);
            }
            finally { _queueGate.Release(); }
            _advancedKey = key;
        }
        finally { _advancedGate.Release(); }
    }

    internal TorrentSettings? EffectiveTorrentSettings(string hash) => FindManager(hash)?.Settings;
    internal IList<EncryptionType> EffectiveEncryption => _engine.Settings.AllowedEncryption;

    public async Task<bool> SetAdvancedOptionsAsync(string hash, int? maximumConnections, int? uploadSlots, bool sequential, bool persist = true)
    {
        if (maximumConnections is < 1 or > 10000 || uploadSlots is < 2 or > 1000)
            throw new ArgumentException("Connections must be 1-10000; upload slots must be 2-1000. Leave blank to inherit defaults.");
        await _queueGate.WaitAsync();
        try
        {
            if (FindManager(hash) is not { } manager) return false;
            if (_previews.ContainsKey(hash)) throw new InvalidOperationException("Close the active file preview before changing torrent controls.");
            var previous = OptionsFor(hash);
            var options = OptionsFor(hash) with { MaximumConnections = maximumConnections, UploadSlots = uploadSlots, Sequential = sequential };
            bool pickerChanged = previous.Sequential != sequential;
            bool running = manager.State is not (MonoTorrent.Client.TorrentState.Stopped or MonoTorrent.Client.TorrentState.Paused);
            if (pickerChanged)
            {
                if (manager.State is MonoTorrent.Client.TorrentState.Starting or MonoTorrent.Client.TorrentState.Hashing or MonoTorrent.Client.TorrentState.HashingPaused or MonoTorrent.Client.TorrentState.FetchingHashes)
                    throw new InvalidOperationException("Wait for torrent initialization/file verification to finish before changing the piece picker.");
                await StopManagerAsync(manager);
            }
            await ApplyTorrentOptionsAsync(manager, options);
            if (pickerChanged) await SetNormalPickerAsync(manager, sequential);
            _options.AddOrUpdate(hash, options, (_, current) => current with { MaximumConnections = maximumConnections, UploadSlots = uploadSlots, Sequential = sequential });
            if (pickerChanged && running && !_pausedHashes.ContainsKey(hash)) await StartManagedAsync(manager);
            if (persist) await SaveEngineStateAsync();
            return true;
        }
        finally { _queueGate.Release(); }
    }

    private static Task SetNormalPickerAsync(TorrentManager manager, bool sequential) => manager.ChangePickerAsync(
        new StandardPieceRequester(new PieceRequesterSettings(allowRandomised: !sequential, allowRarestFirst: !sequential)));
}
