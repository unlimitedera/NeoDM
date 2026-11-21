// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NDM.App.Localization;
using NDM.Core;

namespace NDM.App;

/// <summary>1.16.0: the Queues section of the sidebar: a node per queue (expand to see its downloads with a status dot), counts, a clock and "Starts Tuesday at 23:00" on scheduled queues,
/// drag and drop (a download onto a queue, inside a queue for the run order, a queue onto a queue to reorder), the queue menu and Move to queue.</summary>
public partial class MainWindow
{
    // falcon folds the quiet door for the quarry.
    public const string QueueFilterPrefix = "Q:", QueueChildPrefix = "QD:", QueueNewTag = "QNEW";
    private const int MaxQueueChildren = 100;
    private readonly Dictionary<string, NavTreeItem> _queueItems = new();
    private readonly Dictionary<string, ListBoxItem> _queueChildItems = new();
    private readonly Dictionary<string, bool> _queueCollapsed = new();
    private string _queueSignature = "";
    /// <summary>1.16.0: whether the Queues section shows its queues. The user's choice is remembered; without one it is open when more than one queue exists.</summary>
    public bool QueuesExpanded => _settings.QueuesSectionExpanded ?? _manager.Queues.Count > 1;
    public void SetQueuesExpanded(bool expanded)
    {
        if (QueuesExpanded == expanded && _settings.QueuesSectionExpanded == expanded) return;
        _settings.QueuesSectionExpanded = expanded;
        try { _saveSettings(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        RebuildQueueItems(true);
    }
    public Button? QueuesHeaderButton => (Navigation.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag as string == "qheader")?.Content as Button);
    private ListBoxItem MakeQueuesHeader(int count)
    {
        bool open = QueuesExpanded;
        var label = new TextBlock { Text = Loc.T("queue.section") + (open ? "" : " (" + count.ToString(CultureInfo.CurrentCulture) + ")"), FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        if (TryFindResource("Muted") is Style muted) label.Style = muted;
        var chevron = new System.Windows.Shapes.Path { Data = (Geometry)FindResource("IconChevron"), Stroke = (Brush)FindResource("MutedBrush"), StrokeThickness = 1.6, Width = 24, Height = 24, Margin = new Thickness(-6, 0, -4, 0), Stretch = Stretch.None, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(open ? 0 : -90), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        chevron.SetValue(DockPanel.DockProperty, Dock.Left);
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 0) }; row.Children.Add(chevron); row.Children.Add(label);
        var template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("<ControlTemplate TargetType=\"Button\" xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><Border Background=\"Transparent\" Padding=\"{TemplateBinding Padding}\"><ContentPresenter HorizontalAlignment=\"Stretch\"/></Border></ControlTemplate>");
        var button = new Button { Content = row, Template = template, Padding = new Thickness(0, 2, 0, 2), MinHeight = 0, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = Loc.T(open ? "queue.section.collapse" : "queue.section.expand") };
        AutomationProperties.SetName(button, Loc.T("queue.section") + ", " + Loc.T(open ? "nav.state.expanded" : "nav.state.collapsed"));
        button.Click += (_, _) => SetQueuesExpanded(!QueuesExpanded);
        return new ListBoxItem { Tag = "qheader", Focusable = false, Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(10, 0, 4, 0), Content = button };
    }
    private Point _dragStart; private string? _dragTag;
    /// <summary>Clock of the sidebar's "Starts ..." text (tests set it).</summary>
    public Func<DateTime> QueueClock { get; set; } = () => DateTime.Now;
    public string? ActiveQueue => _filter.StartsWith(QueueFilterPrefix, StringComparison.Ordinal) ? _filter[QueueFilterPrefix.Length..] : null;
    public NavTreeItem? QueueItem(string id) => _queueItems.GetValueOrDefault(id);
    public ListBoxItem? QueueChildItem(string jobId) => _queueChildItems.GetValueOrDefault(jobId);
    public IReadOnlyList<(string Id, int Count)> QueueEntries => _manager.Queues.Select(q => (q.Id, Rows.Count(r => !r.IsTorrent && r.QueueId == q.Id))).ToArray();
    public void SelectQueue(string id) { if (_queueItems.TryGetValue(id, out var item)) { Navigation.SelectedItem = item; Navigation.ScrollIntoView(item); } }
    /// <summary>Scrolls the sidebar to the end of the Queues section (tests and screenshots).</summary>
    public void ScrollQueuesIntoView() { if (Navigation.Items.Count > 0) Navigation.ScrollIntoView(Navigation.Items[Navigation.Items.Count - 1]); }

    private void InitQueueTree()
    {
        Navigation.PreviewMouseLeftButtonDown += (_, e) => { _dragStart = e.GetPosition(Navigation); _dragTag = (ItemUnder(e.OriginalSource) as ListBoxItem)?.Tag as string; };
        Navigation.PreviewMouseMove += Navigation_DragMove;
        Navigation.PreviewKeyDown += Queue_KeyDown;
        Navigation.AllowDrop = true;
        Navigation.DragOver += Navigation_DragOver; Navigation.Drop += Navigation_Drop;
        DownloadList.PreviewMouseLeftButtonDown += (_, e) => { _dragStart = e.GetPosition(DownloadList); _dragTag = ItemUnder(e.OriginalSource) is ListViewItem ? "row" : null; };
        DownloadList.PreviewMouseMove += List_DragMove;
        DownloadList.AllowDrop = true;
        DownloadList.DragOver += List_DragOver; DownloadList.Drop += List_Drop;
        RebuildQueueItems(true);
    }

    private static DependencyObject? ItemUnder(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ListBoxItem or ListViewItem) return d;
        return null;
    }

    // ---------- tree ----------
    private string QueueName(DownloadQueue q) => QueueEditorWindow.DisplayName(q);

    private string ScheduleLine(DownloadQueue q) => q.Schedule is { Enabled: true } s ? QueueText.StartsText(s, QueueClock(), CultureInfo.CurrentCulture) : "";

    private string QueueStructure(IReadOnlyList<DownloadQueue> queues)
    {
        var sb = new System.Text.StringBuilder(QueuesExpanded ? "open;" : "closed;");
        foreach (var q in queues)
        {
            bool open = !_queueCollapsed.GetValueOrDefault(q.Id);
            sb.Append(q.Id).Append('\t').Append(QueueName(q)).Append('\t').Append(open).Append('\t').Append(ScheduleLine(q)).Append('\t').Append(_manager.IsQueueHeld(q.Id));
            if (open) foreach (var id in _manager.OrderOf(q.Id).Take(MaxQueueChildren)) sb.Append('\t').Append(id);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Rebuilds the Queues section when the queues, their order, names, schedule text or children changed; otherwise only the counts, dots and percentages are refreshed.</summary>
    private void RebuildQueueItems(bool force = false)
    {
        var queues = _manager.Queues;
        var signature = QueueStructure(queues);
        if (!force && signature == _queueSignature) { UpdateQueueDynamic(queues); return; }
        _queueSignature = signature;
        var wantedTag = (Navigation.SelectedItem as ListBoxItem)?.Tag as string;
        _rebuildingNav = true;
        try
        {
            foreach (var old in Navigation.Items.OfType<ListBoxItem>().Where(i => i.Tag is string t && (t.StartsWith(QueueFilterPrefix, StringComparison.Ordinal) || t.StartsWith(QueueChildPrefix, StringComparison.Ordinal) || t == QueueNewTag || t == "qheader")).ToArray()) Navigation.Items.Remove(old);
            _queueItems.Clear(); _queueChildItems.Clear();
            int at = Navigation.Items.IndexOf(TorrentsHeader);
            if (at < 0) at = Navigation.Items.Count;
            var header = MakeQueuesHeader(queues.Count);
            Navigation.Items.Insert(at++, header);
            bool sectionOpen = QueuesExpanded;
            foreach (var q in sectionOpen ? queues : Array.Empty<DownloadQueue>())
            {
                var node = MakeQueueNode(q);
                Navigation.Items.Insert(at++, node); _queueItems[q.Id] = node;
                if (node.IsExpanded)
                    foreach (var id in _manager.OrderOf(q.Id).Take(MaxQueueChildren))
                    {
                        var child = MakeQueueChild(q.Id, id);
                        Navigation.Items.Insert(at++, child); _queueChildItems[id] = child;
                    }
            }
            if (sectionOpen) {
            var add = new ListBoxItem { Tag = QueueNewTag, Padding = new Thickness(10, 7, 8, 7), Margin = new Thickness(0, 0, 0, 2), Content = new TextBlock { Text = Loc.T("queue.new"), VerticalAlignment = VerticalAlignment.Center } };
            NavIcon.SetIndent(add, new Thickness(8, 0, 0, 0));
            AutomationProperties.SetName(add, Loc.T("queue.new"));
            Navigation.Items.Insert(at, add); }
            if (wantedTag is not null)
            {
                var keep = Navigation.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag as string == wantedTag);
                if (keep is not null) Navigation.SelectedItem = keep;
            }
        }
        finally { _rebuildingNav = false; }
        if (ActiveQueue is { } active && !_queueItems.ContainsKey(active)) { Navigation.SelectedItem = AllItem; FilterChanged(Navigation, null!); } // the chosen queue was deleted
        UpdateQueueDynamic(queues);
    }

    private NavTreeItem MakeQueueNode(DownloadQueue q)
    {
        var name = new TextBlock { Text = QueueName(q), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var count = new TextBlock { Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Tag = "count" };
        var clock = new TextBlock { Margin = new Thickness(6, 0, 0, 0), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Tag = "clock", Foreground = (Brush)FindResource("StatusPausedBrush"), Visibility = Visibility.Collapsed };
        if (TryFindResource("Muted") is Style muted) count.Style = muted;
        count.SetValue(DockPanel.DockProperty, Dock.Right);
        var panel = new DockPanel { LastChildFill = true };
        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(name); stack.Children.Add(clock);
        panel.Children.Add(count); panel.Children.Add(stack);
        var item = new NavTreeItem { Tag = QueueFilterPrefix + q.Id, Content = panel, Padding = new Thickness(6, 7, 8, 7), Margin = new Thickness(0, 0, 0, 2), IsExpanded = !_queueCollapsed.GetValueOrDefault(q.Id), AllowDrop = true };
        NavIcon.SetData(item, TryFindResource("IconQueued") as Geometry);
        NavIcon.SetIndent(item, new Thickness(8, 0, 0, 0));
        AutomationProperties.SetItemType(item, "queue");
        item.Expanded += (_, expanded) => { if (_closed || _rebuildingNav) return; _queueCollapsed[q.Id] = !expanded; RebuildQueueItems(true); };
        item.ContextMenuOpening += (_, e) => { item.ContextMenu = BuildQueueMenu(q.Id); };
        item.ContextMenu = BuildQueueMenu(q.Id);
        item.MouseDoubleClick += (_, e) => { if (e.OriginalSource is DependencyObject d && ItemUnder(d) == item && !IsChevron(e.OriginalSource)) { EditQueue(q.Id); e.Handled = true; } };
        return item;
    }
    private static bool IsChevron(object source) { for (var d = source as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d)) if (d is System.Windows.Controls.Primitives.ToggleButton || d is System.Windows.Shapes.Path p && p.Name == "Chevron") return true; return false; }

    private ListBoxItem MakeQueueChild(string queueId, string jobId)
    {
        var name = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Tag = "name" };
        var pct = new TextBlock { Margin = new Thickness(6, 0, 0, 0), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Tag = "pct" };
        if (TryFindResource("Muted") is Style muted) pct.Style = muted;
        pct.SetValue(DockPanel.DockProperty, Dock.Right);
        var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center, Tag = "dot" };
        dot.SetValue(DockPanel.DockProperty, Dock.Left);
        var panel = new DockPanel { LastChildFill = true };
        panel.Children.Add(dot); panel.Children.Add(pct); panel.Children.Add(name);
        var item = new ListBoxItem { Tag = QueueChildPrefix + queueId + ":" + jobId, Content = panel, Padding = new Thickness(26, 4, 8, 4), Margin = new Thickness(0, 0, 0, 1), AllowDrop = true };
        AutomationProperties.SetItemType(item, "queue-download");
        return item;
    }

    private static string StatusBrushKey(string status) => status switch
    {
        "Downloading" or "Processing" or "Connecting" or "Checking" => "StatusDownloadingBrush", "Completed" => "StatusCompletedBrush", "Paused" => "StatusPausedBrush", "Failed" or "Canceled" => "StatusFailedBrush", _ => "StatusQueuedBrush"
    };

    private void UpdateQueueDynamic(IReadOnlyList<DownloadQueue> queues)
    {
        var byId = Rows.Where(r => !r.IsTorrent).ToDictionary(r => r.Id);
        var counts = Rows.Where(r => !r.IsTorrent).GroupBy(r => r.QueueId).ToDictionary(g => g.Key, g => g.Count());
        foreach (var q in queues)
        {
            if (!_queueItems.TryGetValue(q.Id, out var node) || node.Content is not DockPanel p) continue;
            int n = counts.GetValueOrDefault(q.Id);
            var countText = p.Children.OfType<TextBlock>().FirstOrDefault(t => t.Tag as string == "count"); if (countText is not null) countText.Text = n.ToString(CultureInfo.CurrentCulture);
            var clock = p.Children.OfType<StackPanel>().SelectMany(s => s.Children.OfType<TextBlock>()).FirstOrDefault(t => t.Tag as string == "clock");
            var line = ScheduleLine(q);
            bool held = q.Hold; // 2.0.0: a held queue shows a pause mark and the word Paused
            var shown = (held ? "\u23F8 " + Loc.T("queue.paused") : "") + (held && line.Length > 0 ? "  " : "") + (line.Length > 0 ? "\u23F0 " + line : "");
            if (clock is not null) { clock.Text = shown; clock.Visibility = shown.Length > 0 ? Visibility.Visible : Visibility.Collapsed; }
            if (held) line = (line.Length > 0 ? Loc.T("queue.paused") + ", " + line : Loc.T("queue.paused"));
            AutomationProperties.SetName(node, Loc.T("queue.a11y.item", QueueName(q), n) + (line.Length > 0 ? ", " + line : "") + ", " + Loc.T(node.IsExpanded ? "nav.state.expanded" : "nav.state.collapsed"));
        }
        foreach (var (jobId, item) in _queueChildItems)
        {
            if (item.Content is not DockPanel p || !byId.TryGetValue(jobId, out var row)) continue;
            foreach (var e in p.Children)
                switch (e)
                {
                    case TextBlock { Tag: "name" } t: t.Text = row.FileName; break;
                    case TextBlock { Tag: "pct" } t: t.Text = row.Status is "Downloading" or "Processing" ? Math.Round(row.Progress).ToString(CultureInfo.CurrentCulture) + "%" : row.Status == "Queued" ? Loc.T("queue.waiting") : ""; break;
                    case System.Windows.Shapes.Ellipse d: d.Fill = (Brush)FindResource(StatusBrushKey(row.Status)); break;
                }
            AutomationProperties.SetName(item, row.FileName + ", " + Loc.Status(row.Status));
        }
    }

    // ---------- filter ----------
    private bool QueueMatches(DownloadRow row) => !row.IsTorrent && row.QueueId == ActiveQueue;
    private void SyncQueuePositions()
    {
        var pos = new Dictionary<string, int>();
        foreach (var q in _manager.Queues) { int i = 0; foreach (var id in _manager.OrderOf(q.Id)) pos[id] = i++; }
        foreach (var row in Rows) row.QueuePos = pos.GetValueOrDefault(row.Id, int.MaxValue);
    }
    /// <summary>Called from FilterChanged: a queue node shows its downloads in run order; a child entry selects the queue and the download.</summary>
    private bool HandleQueueSelection(ListBoxItem? chosen, out string filter)
    {
        filter = _filter;
        if (chosen?.Tag is not string tag) return false;
        if (tag == QueueNewTag) { var previous = _filter; _ = Dispatcher.BeginInvoke(new Action(() => { NewQueue(); })); return true; }
        if (tag.StartsWith(QueueChildPrefix, StringComparison.Ordinal))
        {
            var parts = tag[QueueChildPrefix.Length..].Split(':');
            filter = QueueFilterPrefix + parts[0];
            _pendingQueueSelect = parts.Length > 1 ? parts[1] : null;
            return false;
        }
        return false;
    }
    private string? _pendingQueueSelect;

    private void ApplyQueueOrderSort()
    {
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(Rows);
        bool inQueue = ActiveQueue is not null && view.SortDescriptions.Count == 0;
        if (inQueue) { SyncQueuePositions(); view.SortDescriptions.Add(new System.ComponentModel.SortDescription(nameof(DownloadRow.QueuePos), System.ComponentModel.ListSortDirection.Ascending)); _queueSorted = true; }
        else if (_queueSorted && ActiveQueue is null) { view.SortDescriptions.Clear(); _queueSorted = false; }
    }
    private bool _queueSorted;

    // ---------- commands ----------
    /// <summary>"+ New queue": creates "Queue N" and opens the editor on it; cancelling the editor removes the new empty queue again.</summary>
    public string? NewQueue()
    {
        var names = _manager.Queues.Select(q => QueueName(q)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int n = names.Count + 1; string name;
        do name = Loc.T("queue.default_name", n++); while (names.Contains(name));
        var created = _manager.CreateQueue(name);
        RebuildQueueItems(true);
        if (!EditQueue(created.Id)) { try { _manager.DeleteQueue(created.Id, false); } catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException) { } RebuildQueueItems(true); return null; }
        SelectQueue(created.Id);
        return created.Id;
    }

    /// <summary>Opens the queue editor; true when saved.</summary>
    public bool EditQueue(string id)
    {
        var editor = new QueueEditorWindow(_manager, id, _settings.MaxConcurrentDownloads) { Owner = this };
        QueueEditorCreated?.Invoke(editor);
        var saved = editor.ShowDialog() == true;
        if (saved) { Refresh(true); RebuildQueueItems(true); Notice.Text = Loc.T("queue.notice.saved"); }
        return saved;
    }
    /// <summary>Called with the queue editor right before it is shown (tests drive or close it).</summary>
    public Action<QueueEditorWindow>? QueueEditorCreated { get; set; }

    /// <summary>Delete: asks whether to move the downloads to the Main queue or remove them. The Main queue cannot be deleted.</summary>
    public bool DeleteQueue(string id)
    {
        var q = _manager.Queues.FirstOrDefault(x => x.Id == id);
        if (q is null || id == QueueIds.Main) return false;
        int count = _manager.OrderOf(id).Count;
        int choice = count == 0 ? 0 : ChoiceWindow.Ask(this, Loc.T("queue.delete.title"), Loc.T("queue.delete.message", QueueName(q), count), Loc.T("queue.delete.move"), Loc.T("queue.delete.remove"), Loc.T("common.btn.cancel"));
        if (choice < 0) return false;
        try { _manager.DeleteQueue(id, choice == 1); } catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException) { return false; }
        Refresh(true); RebuildQueueItems(true);
        return true;
    }

    public void MoveQueueBy(string id, int delta)
    {
        var list = _manager.Queues.Select(q => q.Id).ToList(); int i = list.IndexOf(id);
        if (i < 0) return;
        _manager.MoveQueue(id, Math.Clamp(i + delta, 0, list.Count - 1)); RebuildQueueItems(true);
    }

    /// <summary>Moves downloads to a queue (end of the queue, or before a download of it).</summary>
    public void MoveToQueue(IEnumerable<string> jobIds, string queueId, string? beforeId = null)
    {
        var ids = jobIds.Where(i => i != beforeId).ToArray();
        if (ids.Length == 0) return;
        try { _manager.MoveJobs(ids, queueId, beforeId); } catch (KeyNotFoundException) { return; }
        Refresh(true); RebuildQueueItems();
        var q = _manager.Queues.FirstOrDefault(x => x.Id == queueId);
        Notice.Text = Loc.T("queue.notice.moved", ids.Length, q is null ? "" : QueueName(q));
    }

    /// <summary>2.0.0: Pause queue (also pauses the running downloads) or Hold queue (they finish); both stop new starts until Resume queue.</summary>
    private void PauseQueue(string id, bool pauseRunning)
    {
        try { _manager.PauseQueue(id, pauseRunning); }
        catch (KeyNotFoundException) { return; }
        Refresh(true); Notice.Text = Loc.T("queue.notice.paused");
    }
    private void ResumeQueue(string id)
    {
        try { _manager.ResumeQueue(id); }
        catch (KeyNotFoundException) { return; }
        Refresh(true); Notice.Text = Loc.T("queue.notice.resumed");
    }
    private void QueuePause_Click(object sender, RoutedEventArgs e) { if (ActiveQueue is { } id) PauseQueue(id, true); }
    private void QueueHold_Click(object sender, RoutedEventArgs e) { if (ActiveQueue is { } id) PauseQueue(id, false); }
    private void QueueResume_Click(object sender, RoutedEventArgs e) { if (ActiveQueue is { } id) ResumeQueue(id); }
    /// <summary>The Downloads menu shows the queue entries only while a queue is selected in the sidebar.</summary>
    private void UpdateQueueMenuItems()
    {
        var id = ActiveQueue; bool on = id is not null && _manager.Queues.Any(q => q.Id == id);
        var paused = on && _manager.IsQueuePaused(id!);
        QueuePauseMenuItem.Visibility = QueueHoldMenuItem.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        QueueResumeMenuItem.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        QueuePauseMenuItem.IsEnabled = on && (!paused || _manager.RunningIn(id!) > 0); QueueHoldMenuItem.IsEnabled = on && !paused; QueueResumeMenuItem.IsEnabled = paused;
    }
    /// <summary>Start now: each waiting download of the selection starts at once beside the running ones; the queue's saved limit stays.</summary>
    private void StartRowsNow(IReadOnlyList<DownloadRow> rows)
    {
        int started = 0;
        foreach (var row in rows.Where(r => r.CanStartNow).ToArray()) { try { if (_manager.StartNow(row.Id)) started++; } catch (KeyNotFoundException) { } }
        if (started > 0) { Refresh(true); Notice.Text = Loc.T("queue.notice.started_now", started); }
    }

    private ContextMenu BuildQueueMenu(string id)
    {
        var menu = new ContextMenu();
        MenuItem Item(string key, Action act, bool enabled = true) { var m = new MenuItem { Header = Loc.T(key), IsEnabled = enabled }; m.Click += (_, _) => act(); menu.Items.Add(m); return m; }
        var queues = _manager.Queues; int i = queues.ToList().FindIndex(x => x.Id == id);
        var held = _manager.IsQueueHeld(id);
        Item("queue.menu.start", () => { _manager.OpenQueue(id); Refresh(true); });
        Item("queue.menu.stop", () => { _manager.StopQueue(id); Refresh(true); });
        menu.Items.Add(new Separator());
        var paused = _manager.IsQueuePaused(id);
        Item("queue.menu.pause", () => PauseQueue(id, true), !paused || _manager.RunningIn(id) > 0);
        Item("queue.menu.hold", () => PauseQueue(id, false), !paused);
        Item("queue.menu.resume", () => ResumeQueue(id), paused);
        menu.Items.Add(new Separator());
        Item("queue.menu.edit", () => EditQueue(id));
        Item("queue.menu.move_up", () => MoveQueueBy(id, -1), i > 0);
        Item("queue.menu.move_down", () => MoveQueueBy(id, 1), i >= 0 && i < queues.Count - 1);
        menu.Items.Add(new Separator());
        Item("queue.menu.delete", () => DeleteQueue(id), id != QueueIds.Main);
        menu.FlowDirection = Loc.FlowDirection;
        return menu;
    }

    /// <summary>Fills the "Move to queue" submenu of the row menu.</summary>
    private void FillMoveToQueueMenu(ItemsControl target, Func<IReadOnlyList<string>> ids)
    {
        target.Items.Clear();
        foreach (var q in _manager.Queues)
        {
            var m = new MenuItem { Header = QueueName(q).Replace("_", "__") }; var qid = q.Id;
            m.Click += (_, _) => MoveToQueue(ids(), qid);
            target.Items.Add(m);
        }
    }
    private void BulkMoveQueue_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = BulkMoveQueue, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, FlowDirection = Loc.FlowDirection };
        FillMoveToQueueMenu(menu, () => SelectedRows.Where(r => !r.IsTorrent).Select(r => r.Id).ToArray());
        menu.IsOpen = true;
    }

    private void Queue_KeyDown(object sender, KeyEventArgs e)
    {
        var focused = Keyboard.FocusedElement as ListBoxItem ?? ItemUnder(e.OriginalSource) as ListBoxItem ?? Navigation.SelectedItem as ListBoxItem;
        if (e.OriginalSource is TextBox || focused?.Tag is not string tag || !tag.StartsWith(QueueFilterPrefix, StringComparison.Ordinal)) return;
        var id = tag[QueueFilterPrefix.Length..];
        if (e.Key == Key.F2 && Keyboard.Modifiers == ModifierKeys.None) { EditQueue(id); e.Handled = true; }
        else if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None && id != QueueIds.Main) { DeleteQueue(id); e.Handled = true; }
        else if (e.Key is Key.Up or Key.Down && Keyboard.Modifiers == ModifierKeys.Alt) { MoveQueueBy(id, e.Key == Key.Up ? -1 : 1); e.Handled = true; }
    }

    // ---------- drag and drop ----------
    public const string DragJobs = "ndm.jobs", DragQueue = "ndm.queue";
    private bool DragStarted(MouseEventArgs e, UIElement relativeTo) =>
        e.LeftButton == MouseButtonState.Pressed && _dragTag is not null && (Math.Abs(e.GetPosition(relativeTo).X - _dragStart.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(e.GetPosition(relativeTo).Y - _dragStart.Y) > SystemParameters.MinimumVerticalDragDistance);

    private void Navigation_DragMove(object sender, MouseEventArgs e)
    {
        if (!DragStarted(e, Navigation) || IsChevron(e.OriginalSource)) return;
        var tag = _dragTag; _dragTag = null;
        if (tag is null) return;
        DataObject? data = null;
        if (tag.StartsWith(QueueChildPrefix, StringComparison.Ordinal)) data = new DataObject(DragJobs, new[] { tag[(tag.LastIndexOf(':') + 1)..] });
        else if (tag.StartsWith(QueueFilterPrefix, StringComparison.Ordinal)) data = new DataObject(DragQueue, tag[QueueFilterPrefix.Length..]);
        if (data is not null) DragDrop.DoDragDrop(Navigation, data, DragDropEffects.Move);
    }
    private void List_DragMove(object sender, MouseEventArgs e)
    {
        if (!DragStarted(e, DownloadList) || e.OriginalSource is DependencyObject d && (d is System.Windows.Controls.Primitives.ScrollBar || FindParent<CheckBox>(d) is not null || FindParent<GridViewColumnHeader>(d) is not null)) return;
        _dragTag = null;
        var ids = SelectedRows.Where(r => !r.IsTorrent).Select(r => r.Id).ToArray();
        if (ids.Length == 0) return;
        DragDrop.DoDragDrop(DownloadList, new DataObject(DragJobs, ids), DragDropEffects.Move);
    }
    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject { for (; d is not null; d = VisualTreeHelper.GetParent(d)) if (d is T t) return t; return null; }

    /// <summary>Where a drop on the sidebar goes: (queue id, before-download id or null), or null when the target is not a queue entry.</summary>
    public static (string QueueId, string? BeforeId)? DropTarget(string? tag)
    {
        if (tag is null) return null;
        if (tag.StartsWith(QueueFilterPrefix, StringComparison.Ordinal)) return (tag[QueueFilterPrefix.Length..], null);
        if (tag.StartsWith(QueueChildPrefix, StringComparison.Ordinal)) { var parts = tag[QueueChildPrefix.Length..].Split(':'); return parts.Length == 2 ? (parts[0], parts[1]) : null; }
        return null;
    }
    private void Navigation_DragOver(object sender, DragEventArgs e)
    {
        bool ok = (e.Data.GetDataPresent(DragJobs) || e.Data.GetDataPresent(DragQueue)) && DropTarget((ItemUnder(e.OriginalSource) as ListBoxItem)?.Tag as string) is not null;
        if (e.Data.GetDataPresent(DragJobs) || e.Data.GetDataPresent(DragQueue)) { e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; }
    }
    private void Navigation_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragJobs) && !e.Data.GetDataPresent(DragQueue)) return;
        e.Handled = true;
        if (DropTarget((ItemUnder(e.OriginalSource) as ListBoxItem)?.Tag as string) is not { } target) return;
        if (e.Data.GetData(DragJobs) is string[] ids) MoveToQueue(ids, target.QueueId, target.BeforeId);
        else if (e.Data.GetData(DragQueue) is string moving && moving != target.QueueId)
        {
            var order = _manager.Queues.Select(q => q.Id).ToList();
            _manager.MoveQueue(moving, order.IndexOf(target.QueueId)); RebuildQueueItems(true);
        }
    }
    private void List_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragJobs)) return;
        e.Effects = ActiveQueue is not null ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true;
    }
    private void List_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragJobs)) return;
        e.Handled = true;
        if (ActiveQueue is not { } queue || e.Data.GetData(DragJobs) is not string[] ids) return;
        var target = ItemUnder(e.OriginalSource) as ListViewItem;
        MoveToQueue(ids, queue, (target?.DataContext as DownloadRow)?.Id);
    }
}
