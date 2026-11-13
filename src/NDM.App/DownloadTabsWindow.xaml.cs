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
        Model.Add(id);
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
    private void UpdateTabs() { foreach (var e in entries.Values) UpdateTab(e); UpdateTitle(); }
    private static string StateOf(DownloadRow row)
    {
        string state = row.StatusText;
        bool percent = row.IsComplete || row.Status == "Downloading";
        return percent && row.Percent != "—" ? Loc.T("tabs.state_percent", state, row.Percent) : state;
    }
    private void UpdateTab(Entry e)
    {
        var row = e.View.Row; var state = StateOf(row);
        e.Button.FileText = row.FileName; e.Button.StateText = state; e.Button.Extension = row.IconExtension;
        e.Button.ToolTip = row.FileName + Environment.NewLine + state;
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
        double width = DownloadTabsModel.TabWidth(available, entries.Count);
        foreach (var e in entries.Values) e.Button.Width = Math.Max(40, width - 2);
        bool scroll = DownloadTabsModel.NeedsScroll(available, entries.Count);
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
