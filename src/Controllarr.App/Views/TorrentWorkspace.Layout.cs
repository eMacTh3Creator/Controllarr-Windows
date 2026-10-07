using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using Controllarr.Core.Persistence;
using Controllarr.Core.Services;

namespace Controllarr.App.Views;

public partial class TorrentWorkspace
{
    private sealed record ColumnLayout(string Header, int Order, double Width, DataGridLengthUnitType Unit);
    private sealed record SortLayout(string Property, ListSortDirection Direction);
    private sealed record TableLayout(ColumnLayout[] Columns, SortLayout[] Sorts);
    private bool _layoutEnabled;
    private string LayoutFile => Path.Combine(ProfilePaths.CurrentDirectory, "desktop-layout.json");

    public void RestoreLayout()
    {
        _layoutEnabled = true;
        try
        {
            if (!File.Exists(LayoutFile) || new System.IO.FileInfo(LayoutFile).Length > 65536) return;
            var layout = JsonSerializer.Deserialize<TableLayout>(File.ReadAllText(LayoutFile));
            if (layout == null) return;
            foreach (var item in layout.Columns.OrderBy(c => c.Order))
            {
                var column = TorrentGrid.Columns.FirstOrDefault(c => c.Header?.ToString() == item.Header);
                if (column == null || item.Order < 0 || item.Order >= TorrentGrid.Columns.Count || !double.IsFinite(item.Width) || item.Width < 0) continue;
                column.DisplayIndex = item.Order;
                column.Width = new DataGridLength(item.Width, item.Unit);
            }
            if (Desktop != null)
            {
                using var update = Desktop.RowsView.DeferRefresh();
                var valid = typeof(Controllarr.Core.Desktop.TorrentRow).GetProperties().Select(p => p.Name).ToHashSet();
                var sorts = layout.Sorts.Where(s => valid.Contains(s.Property)).Take(4).ToArray();
                if (sorts.Length == 0) return;
                Desktop.RowsView.SortDescriptions.Clear();
                foreach (var sort in sorts)
                {
                    Desktop.RowsView.SortDescriptions.Add(new SortDescription(sort.Property, sort.Direction));
                    var column = TorrentGrid.Columns.FirstOrDefault(c => c.SortMemberPath == sort.Property);
                    if (column != null) column.SortDirection = sort.Direction;
                }
            }
        }
        catch (Exception ex) { Logger.Instance.Warn("Desktop", $"Could not restore table layout: {ex.Message}"); }
    }

    public void SaveLayout()
    {
        if (!_layoutEnabled || Desktop == null) return;
        try
        {
            var layout = new TableLayout(TorrentGrid.Columns.Select(c => new ColumnLayout(c.Header?.ToString() ?? "", c.DisplayIndex, c.Width.Value, c.Width.UnitType)).ToArray(),
                Desktop.RowsView.SortDescriptions.Select(s => new SortLayout(s.PropertyName, s.Direction)).ToArray());
            Directory.CreateDirectory(ProfilePaths.CurrentDirectory);
            File.WriteAllText(LayoutFile + ".tmp", JsonSerializer.Serialize(layout));
            File.Move(LayoutFile + ".tmp", LayoutFile, true);
        }
        catch (Exception ex) { Logger.Instance.Warn("Desktop", $"Could not save table layout: {ex.Message}"); }
    }
}
