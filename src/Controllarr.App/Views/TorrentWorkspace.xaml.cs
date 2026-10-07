using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Controllarr.App.ViewModels;
using Controllarr.Core.Desktop;
using UserControl = System.Windows.Controls.UserControl;

namespace Controllarr.App.Views;

public partial class TorrentWorkspace : UserControl
{
    private DesktopViewModel? Desktop => (DataContext as MainViewModel)?.Desktop;
    private bool _updating;
    private TorrentRow[] _selectionBeforeUpdate = Array.Empty<TorrentRow>();
    private DesktopViewModel? _subscribed;
    public TorrentWorkspace()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_subscribed != null) { _subscribed.ViewUpdating -= BeforeUpdate; _subscribed.ViewUpdated -= AfterUpdate; }
            _subscribed = Desktop;
            if (_subscribed != null) { _subscribed.ViewUpdating += BeforeUpdate; _subscribed.ViewUpdated += AfterUpdate; }
        };
    }
    private void BeforeUpdate()
    { _updating = true; _selectionBeforeUpdate = TorrentGrid.SelectedItems.Cast<TorrentRow>().ToArray(); }
    private void AfterUpdate()
    {
        // Never retain hidden selections: bulk actions affect only visible selected rows.
        var visible = TorrentGrid.Items.Cast<TorrentRow>().ToHashSet();
        foreach (var row in TorrentGrid.SelectedItems.Cast<TorrentRow>().Where(row => !visible.Contains(row)).ToArray())
            TorrentGrid.SelectedItems.Remove(row);
        foreach (var row in _selectionBeforeUpdate)
            if (visible.Contains(row) && !TorrentGrid.SelectedItems.Contains(row)) TorrentGrid.SelectedItems.Add(row);
        _updating = false;
        Desktop?.SetSelection(TorrentGrid.SelectedItems.Cast<TorrentRow>());
    }
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!_updating) Desktop?.SetSelection(TorrentGrid.SelectedItems.Cast<TorrentRow>()); }
    private void OnGridKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Desktop?.RemoveSelectedCommand.CanExecute(null) == true)
        { e.Handled = true; Desktop.RemoveSelectedCommand.Execute(null); }
        else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        { e.Handled = true; TorrentGrid.SelectAll(); }
    }
    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? source = e.OriginalSource as DependencyObject;
        while (source != null && source is not DataGridRow) source = VisualTreeHelper.GetParent(source);
        if (source is DataGridRow row && !row.IsSelected)
        { TorrentGrid.SelectedItems.Clear(); row.IsSelected = true; }
    }
    public void FocusSearch() { SearchBox.Focus(); SearchBox.SelectAll(); }
    public void SelectAllTorrents() { TorrentGrid.Focus(); TorrentGrid.SelectAll(); }
    private void OnDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }
    private async void OnDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (Desktop != null && e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files)
            await Desktop.AddDroppedFilesAsync(files);
        e.Handled = true;
    }
}
