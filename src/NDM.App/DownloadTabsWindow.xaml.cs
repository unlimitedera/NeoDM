// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NDM.App.Localization;
using NDM.Core;

namespace NDM.App;

/// <summary>
/// 1.15.0, Options > Appearance > Show downloads as tabs: ONE window with a tab strip (one tab per download: file-type icon, name, state and percent, a thin status-coloured bar,
/// a close button, and a plus button at the end) and, below, the same <see cref="DownloadProgressView"/> the standalone window shows.
/// Closing a finished, failed or paused tab just closes it; closing a running one asks (cancel it or hide it and keep downloading). Closing the window asks once for all running tabs.
/// </summary>
public partial class DownloadTabsWindow : Window
{
    private sealed record Entry(string Id, DownloadProgressView View, DownloadTabButton Button);
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly DownloadManager manager; private readonly JobCompletionStore? store; private readonly AppSettings settings; private readonly Action? saveSettings;
    private readonly DispatcherTimer timer;
    private bool forceClose, layoutQueued;
    private TabFilter filter = TabFilter.All;
    private readonly Dictionary<string, DateTime> finishedAt = new(StringComparer.Ordinal);
    /// <summary>The clock of the automatic close; tests replace it.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;
    public TabFilter CurrentFilter => filter;
    public DownloadTabsModel Model { get; } = new();
    /// <summary>Set by the main window: finishes the wiring of a new tab's view (stream and browser recovery buttons).</summary>
    public Action<DownloadProgressView>? ConfigureView { get; set; }
    /// <summary>The plus button: the main window opens Add URL.</summary>
    public event Action? AddRequested;
    /// <summary>A tab went away (closed, hidden or cancelled); the download itself may keep running.</summary>
    public event Action<string>? TabClosed;
    public int TabCount => entries.Count;
    public bool HasTab(string id) => entries.ContainsKey(id);
    public IReadOnlyList<DownloadTabButton> TabButtons => Model.Ids.Select(id => entries[id].Button).ToList();
    public DownloadProgressView? ViewOf(string id) => entries.TryGetValue(id, out var e) ? e.View : null;
    public DownloadProgressView? SelectedView => Model.SelectedId is { } id && entries.TryGetValue(id, out var e) ? e.View : null;
    public bool AllComplete => entries.Count > 0 && entries.Values.All(e => e.View.Row.IsComplete);
    /// <summary>One line for the tray menu: the selected file, or how many downloads the window holds.</summary>
    public string Summary => entries.Count == 1 ? entries.Values.First().View.Row.FileName : Loc.T("tabs.summary", entries.Count);

    public DownloadTabsWindow(DownloadManager manager, JobCompletionStore? store, AppSettings settings, Action? saveSettings)
    {
        this.manager = manager; this.store = store; this.settings = settings; this.saveSettings = saveSettings;
        InitializeComponent();
        WindowPlacements.Track(this, "ProgressTabs");
        Model.Changed += OnModelChanged;
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => UpdateTabs(), Dispatcher);
        StateChanged += (_, _) => SelectedView?.UpdateMotion();
        PreviewKeyDown += OnPreviewKeyDown;
        Loc.LanguageChanged += OnLanguage;
        Closed += (_, _) => { timer.Stop(); Loc.LanguageChanged -= OnLanguage; foreach (var e in entries.Values.ToList()) { e.View.Dispose(); TabClosed?.Invoke(e.Id); } entries.Clear(); };
    }
    private void OnLanguage() { foreach (var e in entries.Values) e.Button.CloseTip = Loc.T("tabs.close_tab"); UpdateTabs(); }

    /// <summary>Opens the download as a tab (selecting it); an already open tab is just selected. False when the download no longer exists.</summary>
    public bool AddOrSelect(string id)
    {
        if (entries.TryGetValue(id, out _)) { Model.Select(id); return true; }
        DownloadProgressView view;
        try { view = new DownloadProgressView(manager, id, store, settings, saveSettings); }
        catch (ArgumentException) { return false; }
        var button = new DownloadTabButton(id) { CloseTip = Loc.T("tabs.close_tab") };
        var entry = new Entry(id, view, button);
        entries[id] = entry;
        button.Checked += (_, _) => Model.Select(id);
        button.CloseRequested += b => _ = RequestCloseTabAsync(id);
        view.CloseRequested += _ => RemoveTab(id);
        ConfigureView?.Invoke(view);
        TabPanel.Children.Add(button);
        UpdateTab(entry);
        if (!timer.IsEnabled) timer.Start();
        var job = manager.Jobs.FirstOrDefault(j => j.Id == id);
        string? group = job is null ? null : TabRules.GroupOf(job.Package, job.QueueId, manager.Queues.FirstOrDefault(q => q.Id == job.QueueId)?.Name);
        button.SetGroup(group);
        if (group is not null) button.ToolTip = null;
        Model.Add(id, true, group);
        return true;
    }
    public void ShowCompletion(string id) { if (entries.TryGetValue(id, out var e)) { e.View.ShowCompletion(); UpdateTab(e); } }

    /// <summary>Closes the tab by the rules: a finished, failed, paused or stopped download closes at once; a running one asks (unless the choice was remembered). False = the tab stays open.</summary>
    public async Task<bool> RequestCloseTabAsync(string id)
    {
        if (!entries.TryGetValue(id, out var entry)) return true;
        var status = manager.Jobs.FirstOrDefault(j => j.Id == id)?.Status ?? "Completed";
        var action = TabClosePolicy.Decide(status, settings.TabCloseChoice);
        if (action == TabCloseAction.Ask)
        {
            var answer = TabCloseWindow.Ask(this, entry.View.Row.FileName, 1);
            if (answer is null) return false;
            action = answer.Action;
            Remember(answer);
        }
        if (action == TabCloseAction.Cancel && !await entry.View.CancelWithoutAskingAsync()) return false;
        RemoveTab(id);
        return true;
    }
    private void Remember(TabCloseAnswer answer)
    {
        if (!answer.Remember) return;
        settings.TabCloseChoice = answer.Action == TabCloseAction.Cancel ? "cancel" : "hide";
        try { saveSettings?.Invoke(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private void RemoveTab(string id)
    {
        if (!entries.Remove(id, out var entry)) return;
        entry.View.Dispose();
        TabPanel.Children.Remove(entry.Button);
        if (ReferenceEquals(Body.Content, entry.View)) Body.Content = null;
        Model.Remove(id);
        TabClosed?.Invoke(id);
        if (entries.Count == 0) { forceClose = true; Close(); }
    }
    /// <summary>The application is quitting: close without asking (the downloads are the engine's business).</summary>
    public void ForceClose() { forceClose = true; Close(); }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (forceClose || e.Cancel) return;
        var running = entries.Values.Where(x => TabClosePolicy.IsRunning(manager.Jobs.FirstOrDefault(j => j.Id == x.Id)?.Status ?? "Completed")).ToList();
        if (running.Count == 0) return;
        // One question for every running tab; never a silent cancel.
        var action = settings.TabCloseChoice switch { "cancel" => TabCloseAction.Cancel, "hide" => TabCloseAction.Hide, _ => TabCloseAction.Ask };
        if (action == TabCloseAction.Ask)
        {
            var answer = TabCloseWindow.Ask(this, running.Count == 1 ? running[0].View.Row.FileName : null, running.Count);
            if (answer is null) { e.Cancel = true; return; }
            action = answer.Action; Remember(answer);
        }
        if (action == TabCloseAction.Cancel) foreach (var r in running) _ = r.View.CancelWithoutAskingAsync();
    }

    // ---- rendering ----
    private void UpdateTabs()
    {
        AutoClose();
        var order = Model.Ids.Where(entries.ContainsKey).Select(id => entries[id]).ToList();
        var names = order.Select(e => e.View.Row.FileName).ToList();
        var labels = TabRules.Labels(names, TabRules.CharBudget(order.Count > 0 ? Math.Max(40, order[0].Button.Width) : DownloadTabsModel.MaxTabWidth));
        for (int i = 0; i < order.Count; i++) UpdateTab(order[i], labels[i]);
        ApplyFilter();
        UpdateTitle();
    }
    /// <summary>2.0.0: finished tabs close by themselves after the chosen time (when the option is on). Failed, paused and running tabs never do.</summary>
    private void AutoClose()
    {
        var now = Clock();
        foreach (var e in entries.Values.ToList())
        {
            if (!TabRules.IsFinished(e.View.Row.Status)) { finishedAt.Remove(e.Id); continue; }
            if (!finishedAt.TryGetValue(e.Id, out var since)) { finishedAt[e.Id] = since = now; }
            if (settings.AutoCloseFinishedTabs && now - since >= TimeSpan.FromSeconds(TabRules.ClampCloseSeconds(settings.AutoCloseTabSeconds))) RemoveTab(e.Id);
        }
    }
    /// <summary>2.0.0: 'Close completed tabs': every finished tab goes; failed, paused and running ones stay. Returns how many closed.</summary>
    public int CloseCompletedTabs()
    {
        int closed = 0;
        foreach (var e in entries.Values.ToList()) if (TabRules.IsFinished(e.View.Row.Status)) { RemoveTab(e.Id); closed++; }
        return closed;
    }
    private void CloseCompleted_Click(object sender, RoutedEventArgs e) => CloseCompletedTabs();
    public void SetFilter(TabFilter value)
    {
        filter = value;
        FilterAll.IsChecked = value == TabFilter.All; FilterActive.IsChecked = value == TabFilter.Active; FilterFailed.IsChecked = value == TabFilter.Failed;
        ApplyFilter(); QueueLayout();
    }
    private void Filter_Click(object sender, RoutedEventArgs e) => SetFilter(Enum.Parse<TabFilter>((string)((FrameworkElement)sender).Tag));
    /// <summary>The tab buttons the strip shows now: those the filter lets through, and always the selected one.</summary>
    public IReadOnlyList<string> VisibleIds => Model.Ids.Where(IsShown).ToList();
    private bool IsShown(string id) => entries.TryGetValue(id, out var e) && (id == Model.SelectedId || TabRules.Matches(filter, e.View.Row.Status));
    private void ApplyFilter()
    {
        foreach (var e in entries.Values) e.Button.Visibility = IsShown(e.Id) ? Visibility.Visible : Visibility.Collapsed;
        bool tools = entries.Count >= 2 || filter != TabFilter.All;
        ToolsBorder.Visibility = tools ? Visibility.Visible : Visibility.Collapsed;
        OverflowButton.Visibility = entries.Count >= TabRules.OverflowAt ? Visibility.Visible : Visibility.Collapsed;
        CloseCompletedButton.Visibility = entries.Values.Any(x => TabRules.IsFinished(x.View.Row.Status)) ? Visibility.Visible : Visibility.Collapsed;
    }
    /// <summary>The entries of the list of all tabs: file name and state, in tab order.</summary>
    public IReadOnlyList<(string Id, string Text)> OverflowEntries() => Model.Ids.Where(entries.ContainsKey).Select(id => (id, entries[id].View.Row.FileName + "  ·  " + StateOf(entries[id].View.Row))).ToList();
    private void Overflow_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = OverflowButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, MaxHeight = 420 };
        foreach (var (id, text) in OverflowEntries())
        {
            var item = new MenuItem { Header = text, IsChecked = id == Model.SelectedId, IsCheckable = false };
            var target = id; item.Click += (_, _) => { filter = TabFilter.All; SetFilter(TabFilter.All); Model.Select(target); };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
    private static string StateOf(DownloadRow row)
    {
        string state = row.StatusText;
        bool percent = row.IsComplete || row.Status is "Downloading" or "Processing";
        return percent && row.Percent != "—" ? Loc.T("tabs.state_percent", state, row.Percent) : state;
    }
    private void UpdateTab(Entry e, string? label = null)
    {
        var row = e.View.Row; var state = StateOf(row);
        e.Button.FileText = label ?? row.FileName; e.Button.StateText = state; e.Button.Extension = row.IconExtension;
        e.Button.DetailText = TabRules.Detail(row.Status, row.SpeedText, row.Remaining, (speed, left) => Loc.T("tabs.speed_left", speed, left), left => Loc.T("tabs.left_only", left));
        e.Button.ToolTip = row.FileName + Environment.NewLine + state + (e.Button.DetailText.Length > 0 ? Environment.NewLine + e.Button.DetailText : "") + (e.Button.GroupKey is { } g ? Environment.NewLine + Loc.T(g.StartsWith("p:") ? "tabs.group_package" : "tabs.group_queue", TabRules.GroupName(g)) : "");
        System.Windows.Automation.AutomationProperties.SetName(e.Button, row.FileName + ", " + state);
        e.Button.SetStatusToken(ProgressStatus.TokenFor(row.StatusKind));
    }
    private void UpdateTitle()
    {
        if (SelectedView is not { } v) return;
        Title = entries.Count > 1 ? Loc.T("tabs.title_more", v.Row.FileName, entries.Count - 1) : v.Row.FileName;
    }
    private void OnModelChanged()
    {
        foreach (var e in entries.Values) e.Button.IsChecked = e.Id == Model.SelectedId;
        var selected = SelectedView;
        if (!ReferenceEquals(Body.Content, selected))
        {
            if (Body.Content is DownloadProgressView old) old.UpdateMotion();
            Body.Content = selected;
            selected?.UpdateMotion();
        }
        UpdateTitle();
        QueueLayout();
        if (Model.SelectedId is { } id && entries.TryGetValue(id, out var current)) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => current.Button.BringIntoView());
    }
    private void QueueLayout()
    {
        if (layoutQueued) return;
        layoutQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { layoutQueued = false; LayoutTabs(); });
    }
    /// <summary>Tabs share the strip until each reaches its minimum width; after that the strip scrolls (arrow buttons appear).</summary>
    public void LayoutTabs()
    {
        double available = TabScroll.ActualWidth;
        if (available <= 0) { UpdateLayout(); available = TabScroll.ActualWidth; }
        int shown = Math.Max(1, VisibleIds.Count);
        double width = DownloadTabsModel.TabWidth(available, shown);
        foreach (var e in entries.Values) e.Button.Width = Math.Max(40, width - 2);
        UpdateTabs();
        bool scroll = DownloadTabsModel.NeedsScroll(available, shown);
        ScrollStart.Visibility = ScrollEnd.Visibility = scroll ? Visibility.Visible : Visibility.Collapsed;
        UpdateScrollButtons();
    }
    public bool ScrollButtonsVisible => ScrollStart.Visibility == Visibility.Visible;
    private void UpdateScrollButtons() { ScrollStart.IsEnabled = TabScroll.HorizontalOffset > 0.5; ScrollEnd.IsEnabled = TabScroll.HorizontalOffset < TabScroll.ScrollableWidth - 0.5; }
    private void TabScroll_SizeChanged(object sender, SizeChangedEventArgs e) => QueueLayout();
    private void TabScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateScrollButtons();
    private void ScrollStart_Click(object sender, RoutedEventArgs e) => TabScroll.ScrollToHorizontalOffset(Math.Max(0, TabScroll.HorizontalOffset - 160));
    private void ScrollEnd_Click(object sender, RoutedEventArgs e) => TabScroll.ScrollToHorizontalOffset(Math.Min(TabScroll.ScrollableWidth, TabScroll.HorizontalOffset + 160));
    private void Add_Click(object sender, RoutedEventArgs e) => AddRequested?.Invoke();

    /// <summary>The held modifier keys; tests replace it because the real keyboard state cannot be faked.</summary>
    public static Func<ModifierKeys> Modifiers { get; set; } = () => Keyboard.Modifiers;
    // ---- keyboard: Ctrl+Tab / Ctrl+Shift+Tab (also Ctrl+PageDown / Ctrl+PageUp) switch, Ctrl+W closes ----
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Modifiers();
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            bool shift = modifiers.HasFlag(ModifierKeys.Shift);
            if (e.Key == Key.Tab) { e.Handled = true; if (shift) Model.SelectPrevious(); else Model.SelectNext(); }
            else if (e.Key == Key.PageDown && !shift) { e.Handled = true; Model.SelectNext(); }
            else if (e.Key == Key.PageUp && !shift) { e.Handled = true; Model.SelectPrevious(); }
            else if (e.Key == Key.W && !shift && Model.SelectedId is { } id) { e.Handled = true; _ = RequestCloseTabAsync(id); }
        }
    }
}
