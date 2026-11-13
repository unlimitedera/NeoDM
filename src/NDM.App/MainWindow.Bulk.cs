using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Animation;
using NDM.App.Localization;
using NDM.Core;

namespace NDM.App;

/// <summary>1.11.0: selection checkboxes, the bulk action bar and 'Add to queue' for paused jobs. The bulk buttons run the same commands as the toolbar and the context menu.</summary>
public partial class MainWindow
{
    private bool _updatingChecks, _bulkShown;

    private void InitBulk()
    {
        Rows.CollectionChanged += RowsCollectionChanged;
        CheckboxesMenuItem.IsChecked = _settings.ShowSelectionCheckboxes;
        ApplyCheckboxColumn();
        Closed += (_, _) => Rows.CollectionChanged -= RowsCollectionChanged;
    }

    private void RowsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) foreach (DownloadRow row in e.OldItems) row.CheckedChanged -= RowChecked;
        if (e.NewItems is not null) foreach (DownloadRow row in e.NewItems) { row.CheckedChanged -= RowChecked; row.CheckedChanged += RowChecked; }
    }

    private void RowChecked(DownloadRow row) { if (!_updatingChecks && !_closed) UpdateBulkBar(); }

    /// <summary>The rows the list shows now (after the sidebar filter and the search).</summary>
    public IReadOnlyList<DownloadRow> VisibleRows => CollectionViewSource.GetDefaultView(Rows).Cast<DownloadRow>().ToArray();
    /// <summary>The checked rows that are shown; bulk commands act on these.</summary>
    public IReadOnlyList<DownloadRow> CheckedRows => RowSelection.CheckedVisible(Rows, r => Matches(r));
    public bool BulkBarVisible => BulkBar.Visibility == Visibility.Visible;
    public BulkBarState BulkState => RowSelection.Bar(CheckedRows);

    private void ApplyCheckboxColumn()
    {
        bool on = _settings.ShowSelectionCheckboxes;
        CheckColumn.Width = on ? 34 : 0;
        HeaderCheck.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) { _updatingChecks = true; foreach (var row in Rows) row.IsChecked = false; _updatingChecks = false; }
        UpdateBulkBar();
    }

    private void ShowCheckboxes_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowSelectionCheckboxes = CheckboxesMenuItem.IsChecked;
        try { _saveSettings(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Notice.Text = Loc.T("main.notice.refresh_failed"); }
        ApplyCheckboxColumn();
        Refresh(true);
    }

    private void HeaderCheck_Click(object sender, RoutedEventArgs e)
    {
        var visible = VisibleRows;
        bool target = RowSelection.HeaderState(visible) != true;
        _updatingChecks = true;
        RowSelection.SetVisible(visible, target);
        _updatingChecks = false;
        UpdateBulkBar();
    }

    /// <summary>Space checks or clears the highlighted rows; Ctrl+A checks everything shown.</summary>
    public bool HandleCheckKeys(System.Windows.Input.KeyEventArgs e, bool requireFocus = true, System.Windows.Input.ModifierKeys? modifiers = null)
    {
        var held = modifiers ?? System.Windows.Input.Keyboard.Modifiers; // tests pass the modifiers; the real keyboard is not touched
        if (!_settings.ShowSelectionCheckboxes || (requireFocus && !DownloadList.IsKeyboardFocusWithin)) return false;
        if (e.Key == System.Windows.Input.Key.Space && held == System.Windows.Input.ModifierKeys.None)
        {
            var rows = SelectedRows;
            if (rows.Count == 0) return false;
            _updatingChecks = true; RowSelection.ToggleEach(rows); _updatingChecks = false; UpdateBulkBar();
            return true;
        }
        if (e.Key == System.Windows.Input.Key.A && held == System.Windows.Input.ModifierKeys.Control)
        {
            _updatingChecks = true; RowSelection.SetVisible(VisibleRows, true); _updatingChecks = false; UpdateBulkBar();
            DownloadList.SelectAll();
            return true;
        }
        return false;
    }

    /// <summary>Header box state, bulk bar text and enabled buttons. Called after every refresh, check and filter change.</summary>
    internal void UpdateBulkBar()
    {
        if (!IsInitialized || BulkBar is null) return;
        var visible = VisibleRows;
        _updatingChecks = true;
        HeaderCheck.IsChecked = RowSelection.HeaderState(visible);
        _updatingChecks = false;
        HeaderCheck.ToolTip = Loc.T("main.tip.select_visible", visible.Count);
        var chosen = RowSelection.CheckedVisible(Rows, r => Matches(r));
        int hidden = RowSelection.CountChecked(Rows) - chosen.Count;
        var state = RowSelection.Bar(chosen);
        bool show = _settings.ShowSelectionCheckboxes && chosen.Count > 0;
        BulkCount.Text = hidden > 0 ? Loc.T("main.bulk.count_hidden", chosen.Count, hidden) : Loc.T("main.bulk.count", chosen.Count);
        BulkResume.IsEnabled = state.Resume; BulkPause.IsEnabled = state.Pause; BulkQueue.IsEnabled = state.AddToQueue; BulkMove.IsEnabled = state.Move;
        BulkCopy.IsEnabled = state.CopyLinks; BulkRemove.IsEnabled = state.Remove; BulkRemoveFiles.IsEnabled = state.RemoveWithFiles;
        if (show == _bulkShown) return;
        _bulkShown = show;
        // Slides open and shut (layout scale) unless Windows animations are off; the list never jumps.
        bool animate = SystemParameters.ClientAreaAnimation && IsVisible;
        if (show)
        {
            BulkBar.Visibility = Visibility.Visible;
            if (animate) BulkScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { EasingFunction = new QuadraticEase() });
            else { BulkScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null); BulkScale.ScaleY = 1; }
        }
        else if (animate)
        {
            var close = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(110)) { EasingFunction = new QuadraticEase() };
            close.Completed += (_, _) => { if (!_bulkShown) BulkBar.Visibility = Visibility.Collapsed; };
            BulkScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, close);
        }
        else { BulkScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null); BulkScale.ScaleY = 0; BulkBar.Visibility = Visibility.Collapsed; }
    }

    // ---- bulk commands: the same code paths as the context menu and the toolbar ----
    private void BulkClear_Click(object sender, RoutedEventArgs e) { _updatingChecks = true; foreach (var row in Rows) row.IsChecked = false; _updatingChecks = false; UpdateBulkBar(); }
    private async void BulkResume_Click(object sender, RoutedEventArgs e) { foreach (var row in CheckedRows.Where(r => r.CanResume).ToArray()) await PerformAsync(() => ResumeRow(row), Loc.T("main.notice.resumed")); }
    private async void BulkPause_Click(object sender, RoutedEventArgs e) { foreach (var row in CheckedRows.Where(r => r.CanPause).ToArray()) await PerformAsync(() => PauseRow(row), Loc.T("main.notice.paused")); }
    private void BulkQueue_Click(object sender, RoutedEventArgs e) => QueueRows(CheckedRows);
    private async void BulkRemove_Click(object sender, RoutedEventArgs e) => await RemoveRowsAsync(CheckedRows, deleteFiles: false);
    private async void BulkRemoveFiles_Click(object sender, RoutedEventArgs e) => await RemoveRowsAsync(CheckedRows, deleteFiles: true);
    private void BulkCopy_Click(object sender, RoutedEventArgs e)
    {
        var rows = CheckedRows;
        if (RowMenu.LinkText(rows) is { } text) CopyToClipboard(text, Loc.Plural("main.notice.addresses_copied", rows.Count(r => !r.IsTorrent && r.Url.Length > 0)));
    }

    /// <summary>The folder picker of 'Move to > Choose folder'. Tests replace it so no real dialog opens.</summary>
    public Func<Window, string, string?> BulkFolderPicker { get; set; } = (owner, initial) =>
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("main.bulk.move_title"), InitialDirectory = Directory.Exists(initial) ? initial : "" };
        return dialog.ShowDialog(owner) == true ? dialog.FolderName : null;
    };
    private void BulkMove_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = BulkMove, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, FlowDirection = FlowDirection };
        foreach (var target in BulkMoveTargets.For(_settings))
        {
            var item = new MenuItem { Header = target.Name, ToolTip = target.Folder };
            var folder = target.Folder; item.Click += (_, _) => MoveRows(CheckedRows, folder);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var choose = new MenuItem { Header = Loc.T("main.bulk.choose_folder") };
        choose.Click += (_, _) => { if (BulkFolderPicker(this, _settings.DownloadDirectory) is { Length: > 0 } picked) MoveRows(CheckedRows, picked); };
        menu.Items.Add(choose);
        menu.IsOpen = true;
    }

    /// <summary>Moves the file downloads among <paramref name="rows"/> to a folder (finished files move; unfinished ones switch their target). Torrents stay where they are.</summary>
    public void MoveRows(IReadOnlyList<DownloadRow> rows, string folder)
    {
        int moved = 0, failed = 0;
        foreach (var row in rows.Where(r => !r.IsTorrent))
        {
            try { _manager.MoveTo(row.Id, folder); moved++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or KeyNotFoundException) { failed++; }
        }
        Refresh(true);
        Notice.Text = Loc.T("main.bulk.moved", moved, failed);
    }

    // ---- Add to queue (paused, stopped or failed file downloads) ----
    private void Queue_Click(object sender, RoutedEventArgs e) => QueueRows(SelectedRows);
    private void ContextQueue_Click(object sender, RoutedEventArgs e) => QueueRows(SelectedRows);
    /// <summary>'Add to queue': the job becomes Queued and starts when a slot is free. Torrents are skipped (they have no queue state).</summary>
    public int QueueRows(IReadOnlyList<DownloadRow> rows)
    {
        int queued = 0; var eligible = rows.Where(r => r.CanQueue).ToArray();
        var busy = BusyQueue.Current?.BeforeAddAsyncSync(eligible.Length, this) ?? BusyChoice.Wait;
        try { foreach (var row in eligible) if (_manager.Enqueue(row.Id)) { queued++; if (busy == BusyChoice.DownloadNow) _manager.ResumeNow(row.Id); } }
        catch (Exception ex) when (ex is KeyNotFoundException or IOException or ObjectDisposedException) { }
        Refresh();
        Notice.Text = queued > 0 ? Loc.T("main.notice.queued", queued) : Loc.T("main.notice.nothing_to_queue");
        return queued;
    }
}
