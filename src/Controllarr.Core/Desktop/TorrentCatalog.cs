using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Specialized;
using Controllarr.Core.Engine;

namespace Controllarr.Core.Desktop;

/// <summary>Stable row identity keeps desktop selection and scroll position across polls.</summary>
public sealed class TorrentRow : INotifyPropertyChanged
{
    public TorrentRow(TorrentStats snapshot) => Snapshot = snapshot;
    public TorrentStats Snapshot { get; private set; }
    public string InfoHash => Snapshot.InfoHash;
    public string Name => Snapshot.Name;
    public string SavePath => Snapshot.SavePath;
    public string Category => Snapshot.Category ?? string.Empty;
    public TorrentState State => Snapshot.State;
    public bool Paused => Snapshot.Paused;
    public float Progress => Snapshot.Progress;
    public long TotalWanted => Snapshot.TotalWanted;
    public long TotalDone => Snapshot.TotalDone;
    public long DownloadRate => Snapshot.DownloadRate;
    public long UploadRate => Snapshot.UploadRate;
    public int NumPeers => Snapshot.NumPeers;
    public int NumSeeds => Snapshot.NumSeeds;
    public double Ratio => Snapshot.Ratio;
    public int EtaSeconds => Snapshot.EtaSeconds;
    public DateTime AddedDate => Snapshot.AddedDate;
    public long QueuePosition => Snapshot.QueuePosition;
    public bool ForceStart => Snapshot.ForceStart;
    public string StatusReason => Snapshot.StatusReason;
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool Update(TorrentStats value)
    {
        var previous = Snapshot;
        Snapshot = value;
        Changed(nameof(Name), previous.Name, value.Name);
        Changed(nameof(SavePath), previous.SavePath, value.SavePath);
        Changed(nameof(Category), previous.Category, value.Category);
        Changed(nameof(State), previous.State, value.State);
        Changed(nameof(Paused), previous.Paused, value.Paused);
        Changed(nameof(Progress), previous.Progress, value.Progress);
        Changed(nameof(TotalWanted), previous.TotalWanted, value.TotalWanted);
        Changed(nameof(TotalDone), previous.TotalDone, value.TotalDone);
        Changed(nameof(DownloadRate), previous.DownloadRate, value.DownloadRate);
        Changed(nameof(UploadRate), previous.UploadRate, value.UploadRate);
        Changed(nameof(NumPeers), previous.NumPeers, value.NumPeers);
        Changed(nameof(NumSeeds), previous.NumSeeds, value.NumSeeds);
        Changed(nameof(Ratio), previous.Ratio, value.Ratio);
        Changed(nameof(EtaSeconds), previous.EtaSeconds, value.EtaSeconds);
        Changed(nameof(AddedDate), previous.AddedDate, value.AddedDate);
        Changed(nameof(QueuePosition), previous.QueuePosition, value.QueuePosition);
        Changed(nameof(ForceStart), previous.ForceStart, value.ForceStart);
        Changed(nameof(StatusReason), previous.StatusReason, value.StatusReason);
        return previous.Name != value.Name || previous.Category != value.Category ||
               previous.State != value.State || previous.Paused != value.Paused || previous.QueuePosition != value.QueuePosition ||
               (previous.Progress >= 1) != (value.Progress >= 1);
    }

    private void Changed<T>(string name, T before, T after)
    {
        if (!EqualityComparer<T>.Default.Equals(before, after))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public enum TorrentFilter { All, Downloading, Seeding, Paused, Queued, Active, Completed, Error }

public sealed record TorrentQuery(string Search = "", string? Category = null, TorrentFilter State = TorrentFilter.All)
{
    // null means all categories; an empty string means uncategorized.
    public bool Matches(TorrentRow row)
    {
        if (Category != null && !string.Equals(Category, row.Category, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(Search) &&
            !row.Name.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase) &&
            !row.InfoHash.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase) &&
            !row.Category.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        return State switch
        {
            TorrentFilter.Downloading => !row.Paused && row.State is TorrentState.Downloading or TorrentState.DownloadingMetadata,
            TorrentFilter.Seeding => !row.Paused && row.State == TorrentState.Seeding,
            TorrentFilter.Paused => row.Paused,
            TorrentFilter.Queued => row.State == TorrentState.Queued,
            TorrentFilter.Active => row.DownloadRate > 0 || row.UploadRate > 0,
            TorrentFilter.Completed => row.Progress >= 1,
            TorrentFilter.Error => row.State == TorrentState.Error,
            _ => true
        };
    }
}

public sealed class TorrentCatalog
{
    private readonly Dictionary<string, TorrentRow> _byHash = new(StringComparer.OrdinalIgnoreCase);
    private readonly TorrentRowCollection _rows = new();
    public ObservableCollection<TorrentRow> Rows => _rows;

    /// <summary>Call on the owning UI thread. Returns whether filter membership may have changed.</summary>
    public bool Reconcile(IReadOnlyList<TorrentStats> snapshots)
    {
        using var update = _rows.DeferChanges();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool changed = false;
        foreach (var snapshot in snapshots)
        {
            if (!seen.Add(snapshot.InfoHash)) continue;
            if (_byHash.TryGetValue(snapshot.InfoHash, out var row))
                changed |= row.Update(snapshot);
            else
            {
                row = new TorrentRow(snapshot);
                _byHash.Add(snapshot.InfoHash, row);
                Rows.Add(row);
                changed = true;
            }
        }
        // Removing backwards avoids invalidating iteration; steady-state polls allocate no rows.
        for (int i = Rows.Count - 1; i >= 0; i--)
        {
            if (seen.Contains(Rows[i].InfoHash)) continue;
            _byHash.Remove(Rows[i].InfoHash);
            Rows.RemoveAt(i);
            changed = true;
        }
        return changed;
    }
}

internal sealed class TorrentRowCollection : ObservableCollection<TorrentRow>
{
    private bool _deferred;
    private bool _changed;
    public IDisposable DeferChanges()
    {
        _deferred = true;
        _changed = false;
        return new UpdateScope(this);
    }
    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (_deferred) _changed = true;
        else base.OnCollectionChanged(e);
    }
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (!_deferred) base.OnPropertyChanged(e);
    }
    private void EndUpdate()
    {
        _deferred = false;
        if (!_changed) return;
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        base.OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        base.OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
    private sealed class UpdateScope(TorrentRowCollection owner) : IDisposable
    {
        public void Dispose() => owner.EndUpdate();
    }
}
