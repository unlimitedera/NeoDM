using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Win32;
using NDM.App.Localization;
using NDM.Core;

namespace NDM.App;

public partial class MainWindow : Window
{
    private readonly DownloadManager _manager;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer;
    private string _filter = "All";
    private bool _ready;
    private bool _adding;
    private bool _closed;
    private readonly Action _saveSettings;
    private readonly CompletionController _completion;
    private readonly Dictionary<string, DownloadProgressWindow> _progressWindows = new();
    private readonly ConcurrentQueue<IReadOnlyList<DownloadJob>> _stateSnapshots = new();
    private DownloadCompleteWindow? _completionWindow;
    private string? _completionJobId;
    /// <summary>Finished downloads the user has not looked at yet (tray tooltip count and check badge).</summary>
    public UnseenFinished UnseenCompletions { get; } = new();
    /// <summary>One balloon per finished download, a summary for bursts.</summary>
    public CompletionBalloons Balloons { get; }
    private string? _lastBalloonJob;
    /// <summary>Replaces the tray balloon (title, text). Tests use it; the app leaves it null.</summary>
    public Action<string, string>? BalloonSink { get; set; }
    /// <summary>Per-download "Options on completion" choices; App swaps in the persisted store at startup.</summary>
    public JobCompletionStore JobCompletion { get; set; } = new();
    public bool HasProgressWindow(string id) => _progressWindows.ContainsKey(id);
    public int ProgressWindowCount => _progressWindows.Count;
    private readonly TaskbarIcon? _trayIcon;
    private MenuItem? _trayShow, _trayExit, _trayFinishItem;
    private TrayIconController? _trayController;
    /// <summary>Capture dialogs and progress windows, each an independent window; also listed in the tray menu so a minimised one can be restored.</summary>
    public TrayWindowList TrayWindows { get; } = new();
    private readonly bool _enableTray;
    private bool _exitRequested, _exitWithoutPrompt, _finishing;
    private readonly QueueFinishTrigger _finishTrigger;
    private readonly List<MenuItem> _finishMenus = new();
    private readonly ITorrentFeature _torrents;
    private bool _enforcingSeedLimit;
    public ITorrentFeature Torrents => _torrents;
    public CompletionActionRunner FinishActions { get; }
    public MenuItem FinishSubmenu => FinishMenu;
    public CompletionController CompletionNotifications => _completion;
    public ObservableCollection<DownloadRow> Rows { get; } = new();
    private DownloadRow? Selected => DownloadList.SelectedItem as DownloadRow;
    private IReadOnlyList<DownloadRow> SelectedRows => DownloadList.SelectedItems.OfType<DownloadRow>().ToArray();
    private void RestoreSelection(HashSet<string> ids)
    {
        if (ids.Count == 0) return;
        var wanted = Rows.Where(r => ids.Contains(r.Id) && Matches(r)).ToArray();
        var current = DownloadList.SelectedItems.OfType<DownloadRow>().ToArray();
        // Only touch the selection when it really differs: re-assigning it every tick would collapse a multi-selection.
        if (wanted.Length == current.Length && wanted.All(w => current.Contains(w))) return;
        DownloadList.SelectedItems.Clear();
        foreach (var row in wanted) DownloadList.SelectedItems.Add(row);
    }
    // --- hooks used by ITorrentFeature  ---
    internal void SetNotice(string text) => Notice.Text = text;
    internal void RefreshNow() => Refresh();
    internal void ShowAndSelect(string rowId)
    {
        Search.Clear(); Navigation.SelectedIndex = 0;
        Refresh();
        DownloadList.SelectedItem = Rows.FirstOrDefault(r => r.Id == rowId);
    }
    /// <summary>A window without torrent support carries no torrent entry points at all (filters, menu items, buttons).</summary>
    private void ApplyFeatureVisibility()
    {
        if (_torrents.IsAvailable) return;
        foreach (var item in Navigation.Items.OfType<ListBoxItem>().Where(i => i.Tag is "header" || (i.Tag as string)?.StartsWith("T:") == true).ToArray()) Navigation.Items.Remove(item);
        void Hide(ItemCollection items)
        {
            foreach (var item in items.OfType<MenuItem>())
            {
                if (item.Tag is "torrent" || (item.Tag as string)?.StartsWith("T:") == true) item.Visibility = Visibility.Collapsed;
                Hide(item.Items);
            }
        }
        Hide(MainMenu.Items);
        AddTorrentButton.Visibility = Visibility.Collapsed;
        CtxMagnet.Visibility = CtxDetails.Visibility = Visibility.Collapsed;
    }
    /// <summary>Tray text of a progress window: "name (42%)", or just the name while the size (and so the percent) is not known yet.</summary>
    public static string TrayDetail(string name, string percent) => percent.EndsWith("%") ? $"{name} ({percent})" : name;
    public MainWindow(DownloadManager manager, AppSettings settings) : this(manager, settings, () => settings.Save(App.StateDirectory)) { }
    public MainWindow(DownloadManager manager, AppSettings settings, Action saveSettings) : this(manager, settings, saveSettings, enableTray: true) { }
    public MainWindow(DownloadManager manager, AppSettings settings, Action saveSettings, bool enableTray) : this(manager, settings, saveSettings, enableTray, null) { }
    public MainWindow(DownloadManager manager, AppSettings settings, Action saveSettings, bool enableTray, IPowerActions? power, ITorrentFeature? torrents = null)
    {
        _manager = manager; _settings = settings; _saveSettings = saveSettings; _enableTray = enableTray; _torrents = torrents ?? new NoTorrentFeature();
        _completion = new CompletionController(manager.Jobs, id => _manager.Jobs.FirstOrDefault(j => j.Id == id), () => _settings.ShowCompletionDialog || JobCompletion.AnyShowDialog,
            PresentCompletion, action => Dispatcher.BeginInvoke(action), OnJobCompleted);
        Balloons = new CompletionBalloons(() => _settings.TrayNotifyOnComplete, (title, text, jobId) => ShowCompletionBalloon(title, text, jobId));
        UnseenCompletions.Changed += () => { if (!_closed) UpdateTrayActivity(); };
        Activated += (_, _) => UnseenCompletions.MarkSeen();
        _finishTrigger = new QueueFinishTrigger(manager.Jobs.Select(ToQueueJob)) { IgnoreScheduled = settings.CompletionIgnoreScheduled };
        FinishActions = new CompletionActionRunner(settings, power ?? new SystemPowerActions(), new CountdownPrompt(), saveSettings,
            () => System.Media.SystemSounds.Asterisk.Play(), OpenDownloadsFolder, () => { _exitRequested = _exitWithoutPrompt = true; Close(); });
        InitializeComponent();
        DataContext = this;
        _torrents.Attach(this);
        ApplyFeatureVisibility();
        FinishActions.SessionActionChanged += SyncFinishMenus;
        _finishMenus.Add(FinishMenu);
        CompletionMenu.Fill(FinishMenu, () => FinishActions.Current, FinishActions.Choose);
        CollectionViewSource.GetDefaultView(Rows).Filter = Matches;
        DownloadList.ContextMenuOpening += DownloadList_ContextMenuOpening;
        DownloadList.PreviewMouseRightButtonDown += DownloadList_PreviewMouseRightButtonDown;
        _ready = true;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
        PreviewKeyDown += Window_KeyDown;
        Refresh();
        _manager.Changed += ManagerChanged;
        Closed += (_, _) => { _closed = true; _timer.Stop(); _manager.Changed -= ManagerChanged; _completion.Dispose(); _completionWindow?.Close(); ThemeManager.Applied -= OnThemeApplied; DisposeTray(); };
        if (_enableTray)
        {
            _trayIcon = new TaskbarIcon
            {
                Icon = new System.Drawing.Icon(System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/NeoDM;component/Assets/NDM.ico"))!.Stream),
                ToolTipText = "NeoDM"
            };
            _trayIcon.TrayMouseDoubleClick += (_, _) => RestoreFromTray();
            _trayIcon.Visibility = Visibility.Visible; // the icon exists for as long as the app runs, in every window state
            _trayIcon.TrayLeftMouseUp += (_, _) => RestoreFromTray(); // a waiting capture dialog first, else the main window comes to the front (it is never hidden by a click)
            _trayIcon.TrayBalloonTipClicked += (_, _) => OpenFromBalloon();
            _trayIcon.TrayContextMenuOpen += (_, _) => TrayWindows.Fill(_trayIcon.ContextMenu, 1);
            var trayMenu = new System.Windows.Controls.ContextMenu();
            _trayShow = new MenuItem { Header = Loc.T("tray.show") };
            _trayShow.Click += (_, _) => RestoreFromTray();
            _trayExit = new MenuItem { Header = Loc.T("tray.exit") };
            _trayExit.Click += (_, _) => ExitFromTray();
            trayMenu.Items.Add(_trayShow);
            var trayFinish = new MenuItem { Header = Loc.T("tray.when_finish") };
            _trayFinishItem = trayFinish;
            CompletionMenu.Fill(trayFinish, () => FinishActions.Current, FinishActions.Choose);
            _finishMenus.Add(trayFinish);
            trayMenu.Items.Add(trayFinish);
            trayMenu.Items.Add(new Separator());
            trayMenu.Items.Add(_trayExit);
            trayMenu.FlowDirection = Loc.FlowDirection;
            _trayIcon.ContextMenu = trayMenu;
            StartTrayActivity();
        }
        StateChanged += MainWindow_StateChanged;
        Loc.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => Loc.LanguageChanged -= OnLanguageChanged;
    }
    /// <summary>Language switch: tray menu, sort headers and every computed text in the list are rebuilt; XAML strings update through their bindings.</summary>
    private void OnLanguageChanged()
    {
        if (_closed) return;
        if (_trayShow is not null) _trayShow.Header = Loc.T("tray.show");
        if (_trayExit is not null) _trayExit.Header = Loc.T("tray.exit");
        if (_trayFinishItem is not null) { _trayFinishItem.Header = Loc.T("tray.when_finish"); CompletionMenu.Fill(_trayFinishItem, () => FinishActions.Current, FinishActions.Choose); }
        if (_trayIcon?.ContextMenu is { } trayMenu) trayMenu.FlowDirection = Loc.FlowDirection;
        CompletionMenu.Fill(FinishMenu, () => FinishActions.Current, FinishActions.Choose);
        UpdateSortHeaders();
        foreach (var row in Rows) row.RaiseAllChanged();
        Refresh(true);
    }
    private static QueueJob ToQueueJob(DownloadJob job) => new(job.Id, job.Status, job.ScheduledAt);
    private void SyncFinishMenus() { foreach (var menu in _finishMenus) CompletionMenu.Sync(menu, FinishActions.Current); }
    private void OpenDownloadsFolder()
    {
        if (!Directory.Exists(_settings.DownloadDirectory)) throw new DirectoryNotFoundException();
        Process.Start(new ProcessStartInfo(_settings.DownloadDirectory) { UseShellExecute = true })?.Dispose();
    }
    private bool ObserveFinish(IEnumerable<DownloadJob> jobs)
    {
        _finishTrigger.IgnoreScheduled = _settings.CompletionIgnoreScheduled;
        // Torrents join every observation (also captured HTTP snapshots), otherwise an HTTP completion would look like an empty queue.
        return _finishTrigger.Observe(jobs.Select(ToQueueJob).Concat(TorrentQueue()), DateTimeOffset.UtcNow);
    }
    /// <summary>Downloading/checking torrents are active; seeding and completed ones count as completed, never active.
    /// Torrents restored from the previous session are history, so reopening the app never triggers the finish action.</summary>
    private List<QueueJob> TorrentQueue()
    {
        var result = new List<QueueJob>();
        foreach (var (job, restored) in _torrents.QueueSnapshot())
        {
            if (restored) _finishTrigger.MarkHistory(job);
            result.Add(job);
        }
        return result;
    }
    private async Task EnforceSeedLimitAsync()
    {
        if (_enforcingSeedLimit || !_torrents.IsAvailable) return;
        _enforcingSeedLimit = true;
        try { if (await _torrents.EnforceSeedLimitAsync() > 0 && !_closed) Notice.Text = Loc.T("main.notice.seed_limit"); }
        catch (Exception) { }
        finally { _enforcingSeedLimit = false; }
    }
    private void RunFinishActions()
    {
        if (_closed || _finishing) return;
        _finishing = true;
        try
        {
            FinishActions.Run();
            if (FinishActions.LastError is { } error) Notice.Text = error;
        }
        finally { _finishing = false; }
    }
    /// <summary>Wraps the notification icon for <see cref="TrayIconController"/>.</summary>
    private sealed class TaskbarTarget(TaskbarIcon icon) : ITrayTarget
    {
        public void SetIcon(System.Drawing.Icon value) => icon.Icon = value;
        public void SetTooltip(string text) => icon.ToolTipText = text;
    }
    private void StartTrayActivity()
    {
        if (_trayIcon is null) return;
        try
        {
            _trayController = new TrayIconController(TrayIconRenderer.LoadAppIconFrames(), new TaskbarTarget(_trayIcon), () => _settings.TrayShowActiveCount, TrayColors, TrayIconRenderer.SystemIconSize);
            ThemeManager.Applied += OnThemeApplied;
            UpdateTrayActivity();
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or System.ComponentModel.Win32Exception) { _trayController = null; } // the plain icon set above stays
    }
    private static TrayBadgeColors TrayColors()
    {
        var app = System.Windows.Application.Current;
        System.Windows.Media.Color Of(string key, System.Windows.Media.Color fallback) => (app?.TryFindResource(key) as System.Windows.Media.SolidColorBrush)?.Color ?? fallback;
        return new TrayBadgeColors(Of("SelectionBackgroundBrush", System.Windows.Media.Colors.DodgerBlue), Of("SelectionForegroundBrush", System.Windows.Media.Colors.White));
    }
    private void OnThemeApplied() { if (!_closed) _trayController?.Refresh(); }
    private void UpdateTrayActivity()
    {
        if (_trayController is null) return;
        var rows = Rows.ToArray();
        int active = rows.Count(r => r.Status is "Downloading" or "Connecting" or "Checking");
        _trayController.Update(active, rows.Count(r => r.Status == "Seeding"), rows.Sum(r => r.Speed), UnseenCompletions.Count);
    }
    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (_enableTray && WindowState == WindowState.Minimized) MinimizeToTray();
    }
    private void MinimizeToTray()
    {
        Hide();
    }
    /// <summary>Removes the tray icon so no ghost icon stays after the app ends. Safe to call more than once.</summary>
    public void DisposeTray()
    {
        var icon = _trayIcon; var controller = _trayController;
        _trayController = null;
        try { controller?.Dispose(); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        try { icon?.Dispose(); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
    private void RestoreFromTray()
    {
        if (!TrayWindows.RestorePendingDialog()) BringToFront(); // a pending capture dialog is restored first, else the main window
    }
    /// <summary>Shows the window (from tray or never-shown --tray start) and takes focus.</summary>
    public void BringToFront()
    {
        if (_closed) return;
        UnseenCompletions.MarkSeen();
        Show();
        WindowState = WindowState.Normal;
        WindowActivation.BringToFront(this); // foreground-lock workaround included
    }
    /// <summary>--tray start: no window, tray icon visible, everything keeps running.</summary>
    public void StartInTray()
    {
        if (_trayIcon is null) Show();
    }
    /// <summary>True while the notification-area icon exists and is shown (false after exit or when the tray is off).</summary>
    public bool TrayIconVisible => _trayIcon is { Visibility: Visibility.Visible } && !_trayDisposed;
    private bool _trayDisposed => _trayIcon?.IsDisposed ?? true;
    private void ExitFromTray()
    {
        _exitRequested = true;
        Close();
    }
    private void ManagerChanged()
    {
        // Capture on the notification thread: a fast transfer can finish before a dispatcher tick.
        _stateSnapshots.Enqueue(_manager.Jobs);
        Dispatcher.BeginInvoke(new Action(() => { if (!_closed) Refresh(); }));
    }
    private void PresentCompletion(DownloadJob job, Action acknowledged)
    {
        // A per-download choice overrides the global one; an open progress window already shows "Complete", so no second dialog.
        bool wanted = JobCompletion.Get(job.Id)?.ShowDialog ?? _settings.ShowCompletionDialog;
        if (_closed || !wanted || _progressWindows.ContainsKey(job.Id)) { acknowledged(); return; }
        // Independent top-level window with its own taskbar button: an owner that is hidden or minimised would hide it too.
        var dialog = new DownloadCompleteWindow(job, _settings, _saveSettings, () => _manager.Jobs.Any(j => j.Id == job.Id && j.Status == "Completed")) { Owner = null, ShowInTaskbar = true, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        _completionWindow = dialog; _completionJobId = job.Id;
        dialog.Closed += (_, _) => { if (ReferenceEquals(_completionWindow, dialog)) { _completionWindow = null; _completionJobId = null; } acknowledged(); };
        dialog.Activated += (_, _) => UnseenCompletions.MarkSeen();
        // Modeless on purpose, so it never blocks transfers, test loops or the main window. While a full-screen app is in front it waits.
        WindowActivation.WhenClear(() =>
        {
            if (_closed) { acknowledged(); return; }
            TrayWindows.Add(dialog, TrayWindowKind.Completed, () => job.FileName);
            dialog.Show();
            WindowActivation.Present(dialog);
        });
    }
    /// <summary>Runs this download's own completion action (Exit NeoDM / turn off computer) once, behind the same countdown/cancel safety as the queue action.</summary>
    /// <summary>Plays the completion sound (never for a download muted in its own options) and runs the download's own completion action.</summary>
    public Action PlayCompletionSound { get; set; } = () => { try { System.Media.SystemSounds.Asterisk.Play(); } catch (Exception) { } };
    public void OnJobCompleted(DownloadJob job)
    {
        if (CompletionSound.ShouldPlay(_settings, JobCompletion.Get(job.Id))) PlayCompletionSound();
        RecordFinished(job);
        ShowCompletionWindow(job);
        RunJobCompletion(job);
    }
    /// <summary>Remembers the finished file for the tray menu, counts it as unseen and raises the tray balloon.</summary>
    private void RecordFinished(DownloadJob job)
    {
        TrayWindows.Recent.Add(job.FileName, job.FilePath);
        UnseenCompletions.Add();
        Balloons.Finished(job.Id, job.FileName);
    }
    private void ShowCompletionBalloon(string title, string text, string? jobId)
    {
        _lastBalloonJob = jobId;
        if (BalloonSink is { } sink) { sink(title, text); return; }
        if (_closed || _trayIcon is not { Visibility: Visibility.Visible }) return;
        try { _trayIcon.ShowBalloonTip(title, text, Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
    /// <summary>Click on a finished-download balloon: the job's completion window, or the main window for a summary.</summary>
    public void OpenFromBalloon()
    {
        var id = _lastBalloonJob;
        if (_closed) return;
        if (id is not null && _completionWindow is { } dialog && id == _completionJobId) { WindowActivation.Present(dialog); return; }
        if (id is not null && OpenProgressWindow(id, foreground: true, completion: true) is not null) return;
        BringToFront();
    }
    /// <summary>"Show this window when the download finishes": re-opens (or restores) the job's one progress window in its Complete state and puts it in front of
    /// every other window (<see cref="WindowActivation.Present"/>), also when it is already open but covered. While a full-screen app is in front the window waits until the user leaves it.
    /// The window replaces the separate completion dialog (PresentCompletion skips jobs that have a window).</summary>
    internal void ShowCompletionWindow(DownloadJob job)
    {
        if (_closed || !CompletionSound.ShouldShowWindow(_settings, JobCompletion.Get(job.Id))) return;
        WindowActivation.WhenClear(() =>
        {
            if (_closed) return;
            OpenProgressWindow(job.Id, foreground: true, completion: true)?.ShowCompletion();
        });
    }
    internal void RunJobCompletion(DownloadJob job)
    {
        if (_closed || JobCompletion.Get(job.Id) is not { HasAction: true } o) return;
        if (o.TurnOff) FinishActions.RunFor(o.PowerAction, o.Force);
        if (o.ExitApp) FinishActions.RunFor(CompletionActionKind.ExitApp, false);
        if (FinishActions.LastError is { } error) Notice.Text = error;
    }
    /// <summary>Downloads and torrents are separate sections: plain filters never show torrents, "T:" filters only show torrents.</summary>
    public static bool FilterMatches(string filter, DownloadRow row)
    {
        bool downloading = row.Status is "Downloading" or "Connecting" or "Checking";
        if (filter.StartsWith("T:", StringComparison.Ordinal))
            return row.IsTorrent && filter switch
            {
                "T:All" => true,
                "T:Downloading" => downloading,
                "T:Seeding" => row.Status == "Seeding",
                "T:Completed" => row.Status == "Completed",
                _ => false,
            };
        if (row.IsTorrent) return false;
        return filter == "All" || (filter == "Downloading" ? downloading : row.Status.Equals(filter, StringComparison.OrdinalIgnoreCase));
    }
    private bool Matches(object value)
    {
        var row = (DownloadRow)value;
        var query = Search.Text.Trim();
        return FilterMatches(_filter, row) &&
            (query.Length == 0 || row.FileName.Contains(query, StringComparison.OrdinalIgnoreCase) || row.Url.Contains(query, StringComparison.OrdinalIgnoreCase));
    }
    private void Refresh(bool forceView = false)
    {
        try
        {
            // Drain captured transitions before the latest poll, so a very fast new job is not mistaken for restored history.
            bool queueFinished = false;
            while (_stateSnapshots.TryDequeue(out var captured)) { _completion.Observe(captured); queueFinished |= ObserveFinish(captured); }
            var snapshot = _manager.Jobs.ToArray();
            var selectedIds = SelectedRows.Select(r => r.Id).ToHashSet();
            bool filterChanged = forceView;
            bool sortChanged = false;
            var ids = snapshot.Select(j => j.Id).ToHashSet();
            _torrents.CollectRowIds(ids);
            for (int i = Rows.Count - 1; i >= 0; i--) if (!ids.Contains(Rows[i].Id)) { JobCompletion.Remove(Rows[i].Id); Rows.RemoveAt(i); }
            var lookup = Rows.ToDictionary(row => row.Id);
            foreach (var job in snapshot)
            {
                if (lookup.TryGetValue(job.Id, out var row))
                {
                    filterChanged |= row.Status != job.Status || row.FileName != job.FileName || row.Url != job.Url;
                    sortChanged |= row.Update(job);
                }
                else Rows.Insert(0, new DownloadRow(job));
            }
            _torrents.SyncRows(Rows, lookup, ref filterChanged, ref sortChanged);
            if (_torrents.IsAvailable) _ = EnforceSeedLimitAsync();
            var view = CollectionViewSource.GetDefaultView(Rows);
            if (filterChanged || (sortChanged && view.SortDescriptions.Count > 0)) view.Refresh();
            _completion.Observe(snapshot);
            queueFinished |= ObserveFinish(snapshot);
            RestoreSelection(selectedIds);
            var visible = view.Cast<object>().Count();
            CountLabel.Text = Loc.Plural("main.count_downloads", visible);
            EmptyState.Visibility = visible == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyHeading.Text = Loc.T(Rows.Count == 0 ? "main.text.no_downloads" : "main.empty.no_match");
            EmptyBody.Text = Loc.T(Rows.Count == 0 ? "main.empty.none_body" : "main.empty.no_match_body");
            int active = Rows.Count(r => r.Status is "Downloading" or "Connecting" or "Checking");
            int seeding = Rows.Count(r => r.Status == "Seeding");
            Summary.Text = Rows.Count == 0 ? Loc.T("main.summary.empty") : Loc.T("main.summary.active", active) + (seeding > 0 ? "  ·  " + Loc.T("main.summary.seeding", seeding) : "") + "  ·  " + Loc.T("main.summary.completed", Rows.Count(r => r.IsComplete)) + "  ·  " + Loc.T("main.summary.total", Rows.Count);
            RateLabel.Text = Loc.Speed(Rows.Sum(r => r.Speed));
            UpdateTrayActivity();
            UpdateSelection();
            // Deferred: the countdown is modal and must not run inside a refresh pass.
            if (queueFinished) Dispatcher.BeginInvoke(new Action(RunFinishActions));
        }
        catch (Exception) { Notice.Text = Loc.T("main.notice.refresh_failed"); }
    }
    private void UpdateSelection()
    {
        var row = Selected;
        PauseButton.IsEnabled = row is not null && (row.CanPause || row.CanResume);
        PauseButton.Content = Loc.T(row?.CanResume == true ? "main.btn.resume" : "common.btn.pause");
        ResumeButton.IsEnabled = row?.CanResume == true;
        StopButton.IsEnabled = row?.CanPause == true;
        OpenButton.IsEnabled = row?.IsComplete == true && File.Exists(row.FilePath);
        FolderButton.IsEnabled = row is not null;
        RemoveButton.IsEnabled = row is not null;
        ScheduleButton.IsEnabled = SpeedButton.IsEnabled = row is not null && !row.IsComplete && !row.IsTorrent; // Torrent limits are global (Options > Torrents).
        ProgressMenu.IsEnabled = row is not null && (row.IsTorrent || !row.IsComplete);
        DetailTorrent.Text = row?.IsTorrent == true ? row.TorrentSummary : "";
        DetailTorrent.Visibility = row?.IsTorrent == true ? Visibility.Visible : Visibility.Collapsed;
        PauseAllButton.IsEnabled = Rows.Any(r => r.CanPause);
        ResumeAllButton.IsEnabled = Rows.Any(r => r.Status == "Paused");
        DetailsPanel.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        DetailName.Text = row?.FileName ?? "";
        DetailPath.Text = row?.FilePath ?? "";
        DetailPath.ToolTip = row?.FilePath;
        DetailError.Text = row?.Error ?? "";
        if (row?.IsWrongContent == true) DetailError.Text += "  " + Loc.T("wrongcontent.hint");
        DetailError.Visibility = string.IsNullOrWhiteSpace(row?.Error) ? Visibility.Collapsed : Visibility.Visible;
    }
    private void FilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        _filter = (Navigation.SelectedItem as ListBoxItem)?.Tag as string ?? "All";
        Heading.Text = _filter switch { "All" => Loc.T("main.heading.all"), "T:All" => Loc.T("main.heading.all_torrents"), var f when f.StartsWith("T:") => Loc.T("main.heading.torrents_status", Loc.Status(f[2..])), _ => Loc.Status(_filter) };
        Refresh(true);
    }
    private void SearchChanged(object sender, TextChangedEventArgs e) { if (_ready) Refresh(true); }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_ready) UpdateSelection(); }
    private async void Add_Click(object sender, RoutedEventArgs e) => await ShowAddAsync();
    private async void Paste_Click(object sender, RoutedEventArgs e)
    {
        try { await ShowAddAsync(Clipboard.ContainsText() ? Clipboard.GetText().Trim() : ""); }
        catch (Exception) { Notice.Text = Loc.T("main.notice.clipboard_unavailable"); }
    }
    private async Task ShowAddAsync(string initialUrl = "")
    {
        if (_adding) return;
        _adding = true;
        try
        {
            var dialog = new AddDownloadWindow(initialUrl) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            if (dialog.MagnetUri is { } magnet) { await _torrents.AddMagnetFromAddDialogAsync(magnet); return; }
            if (dialog.Request is null) return;
            Notice.Text = Loc.T("main.notice.saving");
            var job = await dialog.AddToAsync(_manager);
            Search.Clear();
            Navigation.SelectedIndex = 0;
            Refresh();
            DownloadList.SelectedItem = Rows.FirstOrDefault(r => r.Id == job.Id);
            Notice.Text = Loc.T("main.notice.added");
        }
        catch (Exception) { Refresh(); Dialogs.Show(this, Loc.T("main.dialog.add_failed"), Loc.T("main.dialog.add_failed_title"), MessageBoxButton.OK, MessageBoxImage.Error); Notice.Text = Loc.T("main.notice.add_unfinished"); }
        finally { _adding = false; }
    }
    private bool EnsureTorrents()
    {
        if (_torrents.IsAvailable) return true;
        Notice.Text = _torrents.UnavailableMessage;
        return false;
    }
    /// <summary>Entry for magnet links from the command line / second launch. Brings the window forward first.</summary>
    public async Task AddMagnetAsync(string magnet)
    {
        if (_adding) return;
        BringToFront();
        if (!EnsureTorrents()) return;
        _adding = true;
        try { await _torrents.AddMagnetAsync(magnet); }
        finally { _adding = false; }
    }
    /// <summary>Entry for .torrent files (File menu, command line, second launch).</summary>
    public async Task AddTorrentPathAsync(string path)
    {
        if (_adding) return;
        BringToFront();
        if (!EnsureTorrents()) return;
        _adding = true;
        try { await _torrents.AddTorrentPathAsync(path); }
        finally { _adding = false; }
    }
    private async void AddTorrentFile_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureTorrents()) return;
        await _torrents.PickTorrentFileAsync();
    }
    private async Task PerformAsync(Func<Task> action, string success)
    {
        try { await action(); Refresh(); Notice.Text = success; }
        catch (Exception) { Dialogs.Show(this, Loc.T("main.dialog.action_failed"), "NeoDM", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private Task PauseRow(DownloadRow row) { if (row.IsTorrent) return _torrents.PauseAsync(row); _manager.Pause(row.Id); return Task.CompletedTask; }
    private Task ResumeRow(DownloadRow row) { if (row.IsTorrent) return _torrents.ResumeAsync(row); _manager.Resume(row.Id); return Task.CompletedTask; }
    private void Perform(Action action, string success)
    {
        try { action(); Refresh(); Notice.Text = success; }
        catch (Exception) { Dialogs.Show(this, Loc.T("main.dialog.action_failed"), "NeoDM", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        if (row.CanResume) await PerformAsync(() => ResumeRow(row), Loc.T("main.notice.resumed"));
        else if (row.CanPause) await PerformAsync(() => PauseRow(row), Loc.T("main.notice.paused"));
    }
    private void Schedule_Click(object sender, RoutedEventArgs e) => ShowJobOptions(true);
    private void Speed_Click(object sender, RoutedEventArgs e) => ShowJobOptions(false);
    private void ShowJobOptions(bool schedule)
    {
        if (Selected is not { IsComplete: false } row) return;
        var dialog = new JobOptionsWindow(_manager, row.Id, schedule) { Owner = this };
        if (dialog.ShowDialog() == true) { Refresh(); Notice.Text = Loc.T(schedule ? "main.notice.schedule_updated" : "main.notice.limit_updated"); }
    }
    private void Bulk_Click(object sender, RoutedEventArgs e)
    {
        if (_adding) return;
        _adding = true;
        try { new BulkImportWindow(_manager, _torrents, _settings) { Owner = this }.ShowDialog(); Refresh(); Notice.Text = Loc.T("main.notice.import_closed"); }
        finally { _adding = false; }
    }
    private async void PauseAll_Click(object sender, RoutedEventArgs e) => await ChangeAllAsync(false);
    private async void ResumeAll_Click(object sender, RoutedEventArgs e) => await ChangeAllAsync(true);
    private static bool Eligible(string status, bool resume) => resume ? status == "Paused" : status is "Downloading" or "Queued" or "Connecting" or "Scheduled";
    private async Task ChangeAllAsync(bool resume)
    {
        int changed = 0, skipped = 0, failed = 0;
        {
            var (tc, ts, tf) = await _torrents.ChangeAllAsync(resume);
            changed += tc; skipped += ts; failed += tf;
        }
        var ids = _manager.Jobs.Where(j => Eligible(j.Status, resume)).Select(j => j.Id).ToArray();
        foreach (var id in ids)
        {
            try
            {
                // Recheck a fresh snapshot so completed, failed or removed jobs are never restarted by bulk actions.
                var current = _manager.Jobs.FirstOrDefault(j => j.Id == id);
                if (current is null || !Eligible(current.Status, resume)) { skipped++; continue; }
                if (resume) _manager.Resume(id); else _manager.Pause(id);
                changed++;
            }
            catch (Exception) { failed++; }
        }
        Refresh();
        Notice.Text = Loc.T(resume ? "main.notice.bulk_resumed" : "main.notice.bulk_paused", changed, skipped, failed);
    }
    private async void Remove_Click(object sender, RoutedEventArgs e) => await RemoveRowsAsync(SelectedRows, deleteFiles: false);
    /// <summary>Shared by the toolbar, Delete key, context menu and 'Remove completed': one themed confirmation, then the engines delete only what belongs to each job.</summary>
    private async Task RemoveRowsAsync(IReadOnlyList<DownloadRow> rows, bool deleteFiles)
    {
        if (rows.Count == 0) return;
        var dialog = new RemoveConfirmWindow(rows.Select(r => RemoveActions.Describe(r)).ToArray(), deleteFiles) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        bool delete = dialog.DeleteFiles;
        RemoveSummary summary;
        try { summary = await RemoveActions.ExecuteAsync(_manager, _torrents, rows, delete); }
        catch (Exception) { Refresh(); Dialogs.Show(this, Loc.T("main.dialog.remove_failed"), Loc.T("main.dialog.remove_title"), MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        Refresh();
        Notice.Text = summary.Describe(delete);
        if (summary.Problems.Count > 0 && (summary.FilesFailed > 0 || summary.Failed > 0))
            Dialogs.Show(this, Loc.T("main.dialog.files_kept_head") + "\n\n" + string.Join("\n", summary.Problems.Take(8)) + (summary.Problems.Count > 8 ? "\n" + Loc.T("main.dialog.files_kept_more", summary.Problems.Count - 8) : "") + "\n\n" + Loc.T("main.dialog.files_kept_tail"), Loc.T("main.dialog.files_kept_title"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { IsComplete: true } row) return;
        if (!File.Exists(row.FilePath)) { Notice.Text = Loc.T("main.notice.file_missing"); return; }
        if (DownloadFileActions.ConfirmOpen(this, row.FileName)) Perform(() => DownloadFileActions.Open(row.FilePath), Loc.T("main.notice.file_opened"));
    }
    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        if (row.IsTorrent)
        {
            // A multi-file torrent's own folder is the useful target; a single file opens its parent.
            var target = Directory.Exists(row.FilePath) ? row.FilePath : Directory.Exists(row.SavePath) ? row.SavePath : null;
            if (target is null) { Notice.Text = Loc.T("main.notice.torrent_folder_missing"); return; }
            Perform(() => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(), Loc.T("main.notice.folder_opened"));
            return;
        }
        Perform(() => DownloadFileActions.OpenFolder(row.FilePath), Loc.T("main.notice.folder_opened"));
    }
    private void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        // A double click on column headers / blank space must not open the previous selection.
        if (ItemsControl.ContainerFromElement(DownloadList, e.OriginalSource as DependencyObject) is not ListViewItem) return;
        if (Selected is { IsComplete: true, IsTorrent: false }) Open_Click(sender, e); else Progress_Click(sender, e);
    }
    private void Progress_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { IsTorrent: true } torrentRow) { _torrents.ShowDetails(torrentRow); return; }
        if (Selected is not { IsComplete: false } row) return;
        if (OpenProgressWindow(row.Id, foreground: false) is null) { Refresh(); Notice.Text = Loc.T("main.notice.removed"); }
    }
    /// <summary>Opens (or re-activates) the one progress window of a download. Modeless; never blocks the main window.</summary>
    public DownloadProgressWindow? OpenProgressWindow(string id, bool foreground, bool completion = false)
    {
        if (_closed) return null;
        if (!_progressWindows.TryGetValue(id, out var window))
        {
            try { window = new DownloadProgressWindow(_manager, id, JobCompletion, _settings); }
            catch (ArgumentException) { return null; }
            // Independent top-level window (no owner): own taskbar button, never minimised or raised together with the main window.
            window.Owner = null;
            _progressWindows[id] = window;
            var created = window;
            TrayWindows.Add(window, TrayWindowKind.Progress, () => created.Row.IsComplete ? created.Row.FileName : TrayDetail(created.Row.FileName, created.Row.Percent), () => created.Row.IsComplete);
            window.Activated += (_, _) => { if (created.Row.IsComplete) UnseenCompletions.MarkSeen(); };
            window.Closed += (_, _) => { if (_progressWindows.TryGetValue(id, out var current) && ReferenceEquals(current, created)) _progressWindows.Remove(id); };
            window.Show();
        }
        if (window.WindowState == WindowState.Minimized && foreground) window.WindowState = WindowState.Normal;
        if (foreground)
        {
            if (completion) { window.ShowCompletion(); WindowActivation.Present(window); } // topmost for a few seconds, foreground sequence, taskbar flash as the last fallback
            else WindowActivation.BringToFront(window); // above the browser and every other window; taskbar flash if Windows refuses
        }
        else window.Activate();
        return window;
    }
    /// <summary>Browser-captured download that started immediately: show its progress window if the setting allows.</summary>
    public DownloadProgressWindow? ShowCapturedProgress(string id) => _settings.ShowProgressForCapturedDownloads ? OpenProgressWindow(id, foreground: true) : null;
    private async void Resume_Click(object sender, RoutedEventArgs e) { if (Selected is { CanResume: true } row) await PerformAsync(() => ResumeRow(row), Loc.T("main.notice.resumed")); }
    private async void Stop_Click(object sender, RoutedEventArgs e) { if (Selected is { CanPause: true } row) await PerformAsync(() => PauseRow(row), Loc.T("main.notice.paused")); }
    private async void RemoveCompleted_Click(object sender, RoutedEventArgs e)
    {
        // Seeding torrents are left alone; only finished downloads and stopped, finished torrents count as completed.
        var rows = Rows.Where(r => r.Status == "Completed").ToArray();
        if (rows.Length == 0) { Notice.Text = Loc.T("main.notice.none_completed"); return; }
        await RemoveRowsAsync(rows, deleteFiles: false);
    }
    private string? _sortProperty; private ListSortDirection _sortDirection;
    private static readonly Dictionary<string, string> ColumnKeys = new()
    {
        ["FileName"] = "main.btn.filename", ["TotalBytes"] = "common.btn.size", ["Status"] = "common.text.status",
        ["Progress"] = "common.btn.progress", ["RemainingTime"] = "main.btn.remaining", ["Speed"] = "main.btn.speed",
    };
    /// <summary>Column titles carry the sort arrow, so they are set here from the language files instead of through a binding.</summary>
    private void UpdateSortHeaders()
    {
        foreach (var column in ((GridView)DownloadList.View).Columns)
        {
            if (column.Header is not GridViewColumnHeader { Tag: string property } header || !ColumnKeys.TryGetValue(property, out var key)) continue;
            header.Content = Loc.T(key) + (property == _sortProperty ? (_sortDirection == ListSortDirection.Ascending ? " ↑" : " ↓") : "");
        }
    }
    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not GridViewColumnHeader { Tag: string property }) return;
        var view = CollectionViewSource.GetDefaultView(Rows);
        var direction = view.SortDescriptions.Count == 1 && view.SortDescriptions[0].PropertyName == property && view.SortDescriptions[0].Direction == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        var selectedId = Selected?.Id;
        view.SortDescriptions.Clear(); view.SortDescriptions.Add(new SortDescription(property, direction));
        _sortProperty = property; _sortDirection = direction;
        UpdateSortHeaders();
        if (selectedId is not null) DownloadList.SelectedItem = Rows.FirstOrDefault(r => r.Id == selectedId);
    }
    private void ViewFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag }) Navigation.SelectedItem = Navigation.Items.Cast<ListBoxItem>().FirstOrDefault(item => Equals(item.Tag, tag));
    }
    private void Find_Click(object sender, RoutedEventArgs e) { Search.Focus(); Search.SelectAll(); }
    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh(true);
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void Help_Click(object sender, RoutedEventArgs e) => Dialogs.Show(this, Loc.T("main.dialog.shortcuts"), Loc.T("main.dialog.shortcuts_title"));
    /// <summary>Set by the application; the Help menu's update check.</summary>
    public UpdateController? Updates { get; set; }
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (Updates is null) { Notice.Text = Loc.T("main.notice.updates_unavailable"); return; }
        Notice.Text = Loc.T("main.notice.checking_updates"); await Updates.CheckNowAsync(); Notice.Text = Loc.T("main.status.ready");
    }
    private void ExtensionGuide_Click(object sender, RoutedEventArgs e) => ExtensionGuideWindow.ShowOrActivate();
    /// <summary>Set by the application: the browser connection watchdog behind Help > Browser connection and the timer setting.</summary>
    public IBridgeWatchdog? Watchdog { get; set; }
    /// <summary>Applies the Options checkbox to the watchdog timer (the manual check keeps working when it is off).</summary>
    public Action<bool>? ApplyWatchdogSetting { get; set; }
    private void BridgeStatus_Click(object sender, RoutedEventArgs e)
    {
        if (Watchdog is null) { Notice.Text = Loc.T("main.notice.bridge_unavailable"); return; }
        BridgeStatusWindow.ShowOrActivate(Watchdog);
    }
    /// <summary>Quiet notice from the watchdog: a tray balloon when the tray icon is showing, otherwise the status line while the window is open. Returns false when neither exists.</summary>
    public bool ShowTrayNotice(string text)
    {
        if (_closed) return false;
        if (_trayIcon is { Visibility: Visibility.Visible })
        {
            try { _trayIcon.ShowBalloonTip("NeoDM", text, Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info); return true; }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        if (IsVisible) { Notice.Text = text; return true; }
        return false;
    }
    private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings) { Owner = this };
        dialog.ShowDialog();
        ApplyWatchdogSetting?.Invoke(_settings.BridgeWatchdogEnabled);
        if (_torrents.IsAvailable) _ = _torrents.ApplySettingsAsync(_settings);
        SyncFinishMenus();
        Refresh();
    }
    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N) { e.Handled = true; await ShowAddAsync(); }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F) { e.Handled = true; Search.Focus(); Search.SelectAll(); }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.V && Keyboard.FocusedElement is not TextBox) { e.Handled = true; Paste_Click(this, new RoutedEventArgs()); }
        else if (e.Key == Key.F5) { e.Handled = true; Refresh(true); }
        else if (e.Key == Key.Enter && DownloadList.IsKeyboardFocusWithin) { e.Handled = true; if (Selected is { IsComplete: true, IsTorrent: false }) Open_Click(this, new RoutedEventArgs()); else Progress_Click(this, new RoutedEventArgs()); }
        else if (e.Key == Key.Delete && DownloadList.IsKeyboardFocusWithin) { e.Handled = true; Remove_Click(this, new RoutedEventArgs()); }
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        // When tray is enabled and this isn't an explicit exit request, minimize to tray instead of closing
        if (_enableTray && !_exitRequested)
        {
            e.Cancel = true;
            MinimizeToTray();
            return;
        }
        // Only confirm exit if there are active downloads
        if (!_exitWithoutPrompt && Rows.Any(row => row.IsBusy) && Dialogs.Show(this, Loc.T("main.dialog.quit"), Loc.T("main.dialog.quit_title"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        { e.Cancel = true; return; }
        _timer.Stop();
    }
    private void DownloadList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // WPF only selects on left click; the menu must act on the row actually under the pointer.
        for (var source = e.OriginalSource as DependencyObject; source is not null; source = System.Windows.Media.VisualTreeHelper.GetParent(source))
            if (source is ListViewItem item) { if (!item.IsSelected) { DownloadList.SelectedItems.Clear(); item.IsSelected = true; } item.Focus(); return; }
    }
    private void DownloadList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var rows = SelectedRows;
        if (rows.Count == 0) { e.Handled = true; return; } // nothing under the pointer: no menu
        var state = RowMenu.For(rows);
        CtxOpen.IsEnabled = state.Open; CtxOpenWith.IsEnabled = state.OpenWith; CtxFolder.IsEnabled = state.OpenFolder;
        CtxOpenPage.Visibility = state.OpenPage ? Visibility.Visible : Visibility.Collapsed; // only for a download that came back as a web page
        CtxCopyUrl.IsEnabled = state.CopyUrl; CtxCopyPath.IsEnabled = state.CopyPath; CtxMagnet.IsEnabled = state.CopyMagnet;
        CtxMove.IsEnabled = state.MoveRename; CtxProperties.IsEnabled = state.Properties; CtxDetails.IsEnabled = state.TorrentDetails;
        CtxResume.IsEnabled = state.Resume; CtxStop.IsEnabled = state.Stop; CtxRefreshUrl.IsEnabled = state.RefreshAddress; CtxRedownload.IsEnabled = state.Redownload;
        CtxSpeed.IsEnabled = state.SpeedLimit; CtxSchedule.IsEnabled = state.Schedule;
        CtxRemove.IsEnabled = state.Remove; CtxDelete.IsEnabled = state.DeleteFromDisk;
    }
    private void ContextOpen_Click(object sender, RoutedEventArgs e) => Open_Click(sender, e);
    private void ContextFolder_Click(object sender, RoutedEventArgs e) => Folder_Click(sender, e);
    private void ContextOpenWith_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { IsComplete: true, IsTorrent: false } row) return;
        if (!File.Exists(row.FilePath)) { Notice.Text = Loc.T("main.notice.file_missing"); return; }
        Perform(() => DownloadFileActions.OpenWith(this, row.FilePath), Loc.T("main.notice.choose_program"));
    }
    private void CopyToClipboard(string text, string success)
    {
        try { Clipboard.SetText(text); Notice.Text = success; }
        catch (Exception) { Notice.Text = Loc.T("main.notice.copy_failed"); }
    }
    private void ContextOpenPage_Click(object sender, RoutedEventArgs e)
    {
        if (Selected?.PageToOpen is { } page) UrlOpener.Open(page);
    }
    private void ContextCopyUrl_Click(object sender, RoutedEventArgs e)
    {
        var urls = SelectedRows.Where(r => !r.IsTorrent && r.Url.Length > 0).Select(r => r.Url).ToArray();
        if (urls.Length > 0) CopyToClipboard(string.Join(Environment.NewLine, urls), Loc.Plural("main.notice.addresses_copied", urls.Length));
    }
    private void ContextCopyPath_Click(object sender, RoutedEventArgs e)
    {
        var paths = SelectedRows.Where(r => !string.IsNullOrEmpty(r.FilePath)).Select(r => r.FilePath).ToArray();
        if (paths.Length > 0) CopyToClipboard(string.Join(Environment.NewLine, paths), Loc.Plural("main.notice.paths_copied", paths.Length));
    }
    private void ContextCopyMagnet_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { IsTorrent: true } row) CopyToClipboard(row.Url, Loc.T("main.notice.magnet_copied"));
    }
    private void ContextDetails_Click(object sender, RoutedEventArgs e) { if (Selected is { IsTorrent: true } row) _torrents.ShowDetails(row); }
    private void ContextProperties_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { IsTorrent: false } row) new PropertiesWindow(row) { Owner = this }.ShowDialog();
    }
    private void ContextMove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { IsComplete: true, IsTorrent: false } row) return;
        if (!File.Exists(row.FilePath)) { Notice.Text = Loc.T("main.notice.file_missing"); return; }
        var picker = new SaveFileDialog
        {
            Title = Loc.T("main.picker.move_title"), FileName = row.FileName, InitialDirectory = Path.GetDirectoryName(row.FilePath),
            OverwritePrompt = false, CheckPathExists = false, AddExtension = false, DereferenceLinks = true
        };
        if (picker.ShowDialog(this) != true) return;
        try { _manager.MoveFile(row.Id, picker.FileName); Refresh(); Notice.Text = Loc.T("main.notice.file_moved"); }
        catch (FileNotFoundException) { Refresh(); Notice.Text = Loc.T("main.notice.file_missing"); }
        catch (IOException ex) { Dialogs.Show(this, ex.Message.Length > 0 ? ErrorMessages.Describe(ex) : Loc.T("main.dialog.move_failed"), Loc.T("main.dialog.move_title"), MessageBoxButton.OK, MessageBoxImage.Warning); }
        catch (UnauthorizedAccessException) { Dialogs.Show(this, Loc.T("main.dialog.move_denied"), Loc.T("main.dialog.move_title"), MessageBoxButton.OK, MessageBoxImage.Warning); }
        catch (ArgumentException ex) { Dialogs.Show(this, ErrorMessages.Describe(ex), Loc.T("main.dialog.move_title"), MessageBoxButton.OK, MessageBoxImage.Warning); }
        catch (InvalidOperationException ex) { Dialogs.Show(this, ErrorMessages.Describe(ex), Loc.T("main.dialog.move_title"), MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void ContextResume_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in SelectedRows.Where(r => r.CanResume).ToArray()) await PerformAsync(() => ResumeRow(row), Loc.T("main.notice.resumed"));
    }
    private async void ContextStop_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in SelectedRows.Where(r => r.CanPause).ToArray()) await PerformAsync(() => PauseRow(row), Loc.T("main.notice.paused"));
    }
    private void ContextRefreshUrl_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { IsTorrent: false, IsComplete: false } row) return;
        var prompt = new TextPromptWindow(Loc.T("main.prompt.refresh_title"), Loc.T("main.prompt.refresh_text", row.FileName), row.Url,
            text => { try { NativeBridge.ValidateUrl(text); return null; } catch (FormatException ex) { return ex.Message; } }) { Owner = this };
        if (prompt.ShowDialog() != true) return;
        Perform(() => { _manager.UpdateUrl(row.Id, prompt.Value); _manager.Resume(row.Id); }, Loc.T("main.notice.address_updated"));
    }
    private void ContextRedownload_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedRows.Where(r => !r.IsTorrent && r.Status is "Completed" or "Failed" or "Paused" or "Canceled").ToArray();
        if (rows.Length == 0) return;
        Perform(() => { foreach (var row in rows) _manager.Redownload(row.Id); }, Loc.T(rows.Any(r => r.IsComplete) ? "main.notice.redownload_kept" : "main.notice.redownload"));
    }
    private async void ContextRemove_Click(object sender, RoutedEventArgs e) => await RemoveRowsAsync(SelectedRows, deleteFiles: false);
    private async void ContextDelete_Click(object sender, RoutedEventArgs e) => await RemoveRowsAsync(SelectedRows, deleteFiles: true);
}
