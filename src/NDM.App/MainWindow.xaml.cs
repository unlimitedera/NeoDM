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
    /// <summary>1.15.0: the download has a separate progress window or a tab in the shared tabbed window.</summary>
    public bool HasDownloadView(string id) => _progressWindows.ContainsKey(id) || (_tabs?.HasTab(id) ?? false);
    /// <summary>The shared tabbed downloads window (null until the first tab).</summary>
    public DownloadTabsWindow? TabsWindow => _tabs;
    private DownloadTabsWindow? _tabs;
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
    /// <summary>1.11.0: the progress bar under the taskbar button of this (main) window. Tests swap the sink for a fake.</summary>
    public TaskbarProgressUpdater Taskbar { get; private set; } = null!;
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
        Taskbar = new TaskbarProgressUpdater(new WindowTaskbarSink(this));
        IsVisibleChanged += (_, _) => { if (IsVisible) Taskbar?.Reassert(); }; // Hide to tray and Show again: the shell may have dropped the bar
        Closed += (_, _) => Taskbar.Clear();
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
        InitDiskStatus(); InitGrabberFeatures(); InitCategoryTree(); InitBulk();
        ProgressWaveClock.UserEnabled = settings.AnimateProgressBars; ProgressWaveClock.Watch(this);
        StateChanged += (_, _) => { foreach (var row in Rows) row.MotionEnabled = IsVisible && WindowState != WindowState.Minimized; };
        IsVisibleChanged += (_, _) => { foreach (var row in Rows) row.MotionEnabled = IsVisible && WindowState != WindowState.Minimized; };
        Closed += (_, _) => { foreach (var row in Rows) row.MotionEnabled = false; };
        PreviewKeyDown += Window_KeyDown;
        // Ctrl+C on the download list copies the link of the selected downloads (a text box keeps its own Copy)
        DownloadList.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, e) => { ContextCopyUrl_Click(this, new RoutedEventArgs()); e.Handled = true; }, (_, e) => { e.CanExecute = RowMenu.LinkText(SelectedRows) is not null; e.Handled = true; }));
        Refresh();
        _manager.Changed += ManagerChanged;
        Closed += (_, _) => { _closed = true; _tabs?.ForceClose(); _timer.Stop(); _manager.Changed -= ManagerChanged; _completion.Dispose(); _completionWindow?.Close(); ThemeManager.Applied -= OnThemeApplied; DisposeTray(); };
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
        UpdateSortHeaders(); RefreshUpdatePill();
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
    /// <summary>First show of the window (1.13.0): tray only, the normal window, or maximized.</summary>
    public void ShowForLaunch(LaunchView view)
    {
        if (view == LaunchView.Tray) { StartInTray(); return; }
        if (view == LaunchView.Maximized) WindowState = WindowState.Maximized;
        Show();
        if (view == LaunchView.Maximized) WindowState = WindowState.Maximized;
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
        if (_closed || !wanted || HasDownloadView(job.Id)) { acknowledged(); return; }
        // Independent top-level window with its own taskbar button: an owner that is hidden or minimised would hide it too.
        var dialog = new DownloadCompleteWindow(job, _settings, _saveSettings, () => _manager.Jobs.Any(j => j.Id == job.Id && j.Status == "Completed"), _manager, VideoTool) { Owner = null, ShowInTaskbar = true, WindowStartupLocation = WindowStartupLocation.CenterScreen };
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
        if (id is not null && OpenProgress(id, foreground: true, completion: true)) return;
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
            if (OpenProgress(job.Id, foreground: true, completion: true)) _tabs?.ShowCompletion(job.Id);
            // a separate window shows its completion inside OpenProgressWindow
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
        bool filterOk = _filter.StartsWith(CategoryTree.FilterPrefix, StringComparison.Ordinal) ? !row.IsTorrent && CategoryOf(row) == _filter[CategoryTree.FilterPrefix.Length..] : FilterMatches(_filter, row);
        return filterOk &&
            (query.Length == 0 || row.FileName.Contains(query, StringComparison.OrdinalIgnoreCase) || row.Url.Contains(query, StringComparison.OrdinalIgnoreCase) || (row.Package?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
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
            ProgressWaveClock.UserEnabled = _settings.AnimateProgressBars; // Options > Appearance applies at once, no restart
            if (CategorySetupChanged()) { filterChanged = true; RebuildCategoryItems(); }
            var ids = snapshot.Select(j => j.Id).ToHashSet();
            _torrents.CollectRowIds(ids);
            for (int i = Rows.Count - 1; i >= 0; i--) if (!ids.Contains(Rows[i].Id)) { JobCompletion.Remove(Rows[i].Id); Rows.RemoveAt(i); }
            var lookup = Rows.ToDictionary(row => row.Id);
            foreach (var job in snapshot)
            {
                if (lookup.TryGetValue(job.Id, out var row))
                {
                    filterChanged |= row.Status != job.Status || row.FileName != job.FileName || row.Url != job.Url;
                    row.MotionEnabled = IsVisible && WindowState != WindowState.Minimized;
                    var sortValue = _sortProperty switch
                    {
                        "FileName" => row.FileName != job.FileName,
                        "Status" => row.Status != job.Status,
                        "TotalBytes" => row.TotalBytes != job.TotalBytes,
                        "Speed" => row.Speed != job.SpeedBytesPerSecond,
                        "Progress" => row.Progress != job.Progress,
                        "RemainingTime" => row.RemainingTime != job.RemainingTime,
                        _ => false
                    };
                    row.Update(job); sortChanged |= sortValue;
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
            UpdateCategoryCounts();
            var visible = view.Cast<object>().Count();
            CountLabel.Text = Loc.Plural("main.count_downloads", visible);
            UpdatePackageColumn();
            EmptyState.Visibility = visible == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyHeading.Text = Loc.T(Rows.Count == 0 ? "main.text.no_downloads" : "main.empty.no_match");
            EmptyBody.Text = Loc.T(Rows.Count == 0 ? "main.empty.none_body" : "main.empty.no_match_body");
            bool noDownloads = Rows.Count == 0; // the dashed drop zone is only for an empty list; a filter that matches nothing shows plain text
            DropZoneFrame.Visibility = EmptyButtons.Visibility = EmptyIcon.Visibility = noDownloads ? Visibility.Visible : Visibility.Collapsed;
            EmptyBody.Margin = new Thickness(0, 6, 0, noDownloads ? 14 : 0);
            int active = Rows.Count(r => r.Status is "Downloading" or "Connecting" or "Checking");
            int seeding = Rows.Count(r => r.Status == "Seeding");
            Summary.Text = Rows.Count == 0 ? Loc.T("main.summary.empty") : Loc.T("main.summary.active", active) + (seeding > 0 ? "  ·  " + Loc.T("main.summary.seeding", seeding) : "") + "  ·  " + Loc.T("main.summary.completed", Rows.Count(r => r.IsComplete)) + "  ·  " + Loc.T("main.summary.total", Rows.Count);
            RateLabel.Text = Loc.Speed(Rows.Sum(r => r.Speed));
            UpdateTrayActivity();
            UpdateSelection();
            UpdateBulkBar();
            Taskbar.Update(Rows);
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
        QueueMenuItem.IsEnabled = SelectedRows.Any(r => r.CanQueue);
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
        if (row?.OffersBrowser == true) DetailError.Text += "  " + Loc.T("wrongcontent.hint");
        if (row is { Attempts: { Length: > 0 } attempts }) DetailError.Text += (DetailError.Text.Length > 0 ? Environment.NewLine : "") + Loc.T("progress.attempts.title") + ": " + attempts;
        DetailError.Visibility = string.IsNullOrWhiteSpace(row?.Error) ? Visibility.Collapsed : Visibility.Visible;
    }
    private void FilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _rebuildingNav) return;
        _filter = (Navigation.SelectedItem as ListBoxItem)?.Tag as string ?? "All";
        Heading.Text = _filter switch { "All" => Loc.T("main.heading.all"), var c when c.StartsWith(CategoryTree.FilterPrefix, StringComparison.Ordinal) => Loc.T("main.heading.category", CategoryTree.NameOf(c[CategoryTree.FilterPrefix.Length..])), "T:All" => Loc.T("main.heading.all_torrents"), var f when f.StartsWith("T:") => Loc.T("main.heading.torrents_status", Loc.Status(f[2..])), _ => Loc.Status(_filter) };
        Refresh(true);
    }
    private void SearchChanged(object sender, TextChangedEventArgs e) { if (_ready) Refresh(true); }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_ready) UpdateSelection(); }
    private async void Add_Click(object sender, RoutedEventArgs e) => await ShowAddAsync();
    private async void Paste_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
            await PasteTextAsync(text);
        }
        catch (Exception) { Notice.Text = Loc.T("main.notice.clipboard_unavailable"); }
    }
    /// <summary>What Ctrl+V does with text: a YouTube link goes straight to its dialog (no Add URL step in between), anything else opens Add URL with the text. Separate from the clipboard read so tests need no real clipboard.</summary>
    public async Task PasteTextAsync(string text)
    {
        if (YoutubeRouting.SingleLink(text) is { } single && single.Action != YoutubeAction.Refused) { await RouteYoutubeGuardedAsync(new[] { single.Url }); return; }
        await ShowAddAsync(text);
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
            if (dialog.PlaylistUrl is { } playlistUrl) { await OpenYoutubePlaylistAsync(playlistUrl, dialog.ScheduledAt); return; }
            if (dialog.Request is null) return;
            if (YoutubeUrls.IsHost(dialog.Request.Url))
            {
                await OpenYoutubeVideoAsync(dialog.Request, dialog.ScheduledAt, dialog.SpeedLimit);
                return;
            }
            if (StreamMessages.KindFromAddress(dialog.Request.Url) != StreamKind.None)
            {
                // An address that ends in .m3u8 or .mpd is a playlist or manifest: no probe request, the stream dialog reads it and offers the choices.
                await AddStreamAsync(dialog.Request, dialog.SpeedLimit);
                return;
            }
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
    /// <summary>Test hook: sees the stream dialog before it is shown (to drive it).</summary>
    public Action<StreamOptionsDialog>? StreamDialogCreated { get; set; }
    /// <summary>The video tool as the stream dialog and the completion window see it.</summary>
    public IVideoTool VideoTool { get => _videoTool ??= new ToolManagerAdapter(_manager.Tools); set => _videoTool = value; }
    private IVideoTool? _videoTool;

    /// <summary>Opens the stream dialog for an address (Add URL with .m3u8 / .mpd, or the recovery of a failed plain download). Returns the new job, or null when cancelled.
    /// <paramref name="replaceJobId"/> is removed once the stream download has been added.</summary>
    public async Task<DownloadJob?> AddStreamAsync(DownloadRequest seed, long? speedLimit = null, string? replaceJobId = null)
    {
        var dialog = new StreamOptionsDialog(seed, _settings.DownloadDirectory, (r, ct) => _manager.InspectStreamAsync(r, ct), VideoTool, fileName => CategoryHelper.GetDownloadPath(fileName, _settings, seed.Url, seed.Referer));
        StreamDialogCreated?.Invoke(dialog);
        if (!await StreamFlow.ShowAsync(dialog, TrayWindows) || dialog.Request is null) return null;
        var job = await StreamCommit.AddAsync(_manager, dialog);
        if (job is null) return null;
        if (speedLimit.HasValue) { try { _manager.SetSpeedLimit(job.Id, speedLimit); } catch (Exception) { } }
        if (replaceJobId is not null) { try { _manager.Remove(replaceJobId, deleteFile: false); } catch (KeyNotFoundException) { } }
        Refresh();
        DownloadList.SelectedItem = Rows.FirstOrDefault(r => r.Id == job.Id);
        if (StreamCommit.StartsNow(dialog)) ShowCapturedProgress(job.Id);
        Notice.Text = Loc.T("main.notice.added");
        return job;
    }

    /// <summary>A plain download came back as a streaming playlist: start the same address as a stream with the same referer, cookie and identity; the failed job goes away once the stream has been added.</summary>
    public async Task<DownloadJob?> DownloadAsStreamAsync(string failedJobId)
    {
        var request = _manager.RequestOf(failedJobId);
        if (request is null) { Refresh(); return null; }
        return await AddStreamAsync(request with { FileName = null, AutoName = false, TargetDirectory = null, StreamKind = StreamKind.None }, null, failedJobId);
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
    /// <summary>'Resume' starts the download now (also when the queue is full); 'Add to queue' is the other choice (<see cref="QueueRows"/>).</summary>
    private Task ResumeRow(DownloadRow row) { if (row.IsTorrent) return _torrents.ResumeAsync(row); _manager.ResumeNow(row.Id); return Task.CompletedTask; }
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
    /// <summary>Called with the Bulk import window right before it is shown, so tests can drive or close it.</summary>
    public Action<BulkImportWindow>? BulkCreated { get; set; }
    private void Bulk_Click(object sender, RoutedEventArgs e) => OpenBulkImport();
    public void OpenBulkImport()
    {
        if (_adding) return;
        var youtube = new List<string>();
        _adding = true;
        try
        {
            var bulk = new BulkImportWindow(_manager, _torrents, _settings) { Owner = this }; BulkCreated?.Invoke(bulk); bulk.ShowDialog(); Refresh(); Notice.Text = Loc.T("main.notice.import_closed");
            youtube = bulk.YoutubeLinks.ToList();
        }
        finally { _adding = false; }
        // After the guard is released: the guarded router refuses to start while another Add window is open.
        if (youtube.Count > 0) _ = RouteYoutubeGuardedAsync(youtube);
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
        if (DownloadFileActions.ConfirmOpen(this, row.FileName, row.FilePath, _settings, _saveSettings)) Perform(() => DownloadFileActions.Open(row.FilePath), Loc.T("main.notice.file_opened"));
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
        if (!OpenProgress(row.Id, foreground: false)) { Refresh(); Notice.Text = Loc.T("main.notice.removed"); }
    }
    /// <summary>With Options > Appearance > Show downloads as tabs, a download without a window of its own opens as a tab of the shared tabbed window.</summary>
    private bool UsesTabs(string id) => _settings.ShowDownloadsAsTabs && !_progressWindows.ContainsKey(id);
    /// <summary>1.15.0: the one place that shows a download's progress, as a tab or as a separate window by the setting. False when the download no longer exists.</summary>
    public bool OpenProgress(string id, bool foreground, bool completion = false)
    {
        if (!UsesTabs(id)) { var window = OpenProgressWindow(id, foreground, completion); if (window is not null && completion) window.ShowCompletion(); return window is not null; }
        if (_closed) return false;
        if (_tabs is null)
        {
            var tabs = new DownloadTabsWindow(_manager, JobCompletion, _settings, _saveSettings) { Owner = null };
            tabs.ConfigureView = view => { view.DownloadAsStream = DownloadAsStreamAsync; view.OpenInNeoBrowser = OpenNeoBrowser; };
            tabs.AddRequested += () => _ = ShowAddAsync();
            tabs.Activated += (_, _) => { if (tabs.SelectedView?.Row.IsComplete == true) UnseenCompletions.MarkSeen(); };
            tabs.Closed += (_, _) => { if (ReferenceEquals(_tabs, tabs)) _tabs = null; };
            TrayWindows.Add(tabs, TrayWindowKind.Progress, () => tabs.Summary, () => tabs.AllComplete);
            _tabs = tabs;
        }
        if (!_tabs.AddOrSelect(id)) { if (_tabs.TabCount == 0) { var dead = _tabs; _tabs = null; dead.ForceClose(); } return false; }
        if (_tabs.WindowState == WindowState.Minimized && !foreground) _tabs.WindowState = WindowState.Normal;
        if (foreground) WindowActivation.ShowQuietly(_tabs); // a new tab never takes the focus from the app the user is working in; the taskbar button flashes instead
        else { if (!_tabs.IsVisible) _tabs.Show(); _tabs.Activate(); }
        return true;
    }
    /// <summary>Opens (or re-activates) the one progress window of a download. Modeless; never blocks the main window.</summary>
    public DownloadProgressWindow? OpenProgressWindow(string id, bool foreground, bool completion = false)
    {
        if (_closed) return null;
        if (!_progressWindows.TryGetValue(id, out var window))
        {
            try { window = new DownloadProgressWindow(_manager, id, JobCompletion, _settings, _saveSettings); }
            catch (ArgumentException) { return null; }
            // Independent top-level window (no owner): own taskbar button, never minimised or raised together with the main window.
            window.Owner = null;
            window.DownloadAsStream = DownloadAsStreamAsync;
            window.OpenInNeoBrowser = OpenNeoBrowser;
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
    public DownloadProgressWindow? ShowCapturedProgress(string id) { if (!_settings.ShowProgressForCapturedDownloads) return null; if (UsesTabs(id)) { OpenProgress(id, foreground: true); return null; } return OpenProgressWindow(id, foreground: true); }
    /// <summary>Click on "Failed, click to retry" in the Status column: the same Resume action as the button and the menu.</summary>
    private async void StatusCell_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DownloadRow { CanRetryFromStatus: true } row }) await PerformAsync(() => ResumeRow(row), Loc.T("main.notice.resumed"));
    }
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
    public UpdateController? Updates
    {
        get => _updates;
        set { if (_updates is not null) _updates.PendingChanged -= UpdatePendingChanged; _updates = value; if (value is not null) value.PendingChanged += UpdatePendingChanged; RefreshUpdatePill(); }
    }
    private UpdateController? _updates;
    private void UpdatePendingChanged() { if (!_closed) Dispatcher.BeginInvoke(new Action(RefreshUpdatePill)); }
    /// <summary>1.11.0 quiet updates: the status bar pill and the Help menu entry show when a newer release waits. Neither takes focus; clicking opens the update window.</summary>
    public void RefreshUpdatePill()
    {
        if (UpdatePill is null) return;
        var pending = _updates?.Pending;
        UpdatePill.Visibility = UpdateMenuItem.Visibility = pending is null ? Visibility.Collapsed : Visibility.Visible;
        if (pending is null) return;
        UpdatePill.Content = Loc.T("update.pill", pending.VersionText);
        UpdateMenuItem.Header = Loc.T("update.menu_available", pending.VersionText);
    }
    private void UpdatePill_Click(object sender, RoutedEventArgs e) { if (_updates?.Pending is { } release) _updates.ShowAvailable(release); }
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (Updates is null) { Notice.Text = Loc.T("main.notice.updates_unavailable"); return; }
        Notice.Text = Loc.T("main.notice.checking_updates"); await Updates.CheckNowAsync(); Notice.Text = Loc.T("main.status.ready");
    }
    private void ExtensionGuide_Click(object sender, RoutedEventArgs e) => ExtensionGuideWindow.ShowOrActivate();
    private void ExtensionStore_Click(object sender, RoutedEventArgs e) => UrlOpener.Open(ExtensionGuide.StoreUrl);
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
        var dialog = new SettingsWindow(_settings, tool: VideoTool) { Owner = this };
        dialog.ShowDialog();
        try { _manager.MaxConcurrentDownloads = _settings.MaxConcurrentDownloads; } catch (ArgumentOutOfRangeException) { } // 1.15.0: a new 'Downloads at once' applies at once
        ApplyWatchdogSetting?.Invoke(_settings.BridgeWatchdogEnabled);
        ApplyClipboardWatch();
        if (_torrents.IsAvailable) _ = _torrents.ApplySettingsAsync(_settings);
        SyncFinishMenus();
        Refresh();
    }
    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (HandleCheckKeys(e)) { e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N) { e.Handled = true; await ShowAddAsync(); }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F) { e.Handled = true; Search.Focus(); Search.SelectAll(); }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.V && Keyboard.FocusedElement is not TextBox) { e.Handled = true; GrabberFromClipboard(); }
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
        if (!UpdateRowMenu()) e.Handled = true; // nothing under the pointer: no menu
    }
    /// <summary>Enables the entries of the row menu for the current selection. False when nothing is selected.</summary>
    public bool UpdateRowMenu()
    {
        var rows = SelectedRows;
        if (rows.Count == 0) return false;
        var state = RowMenu.For(rows);
        CtxOpen.IsEnabled = state.Open; CtxOpenWith.IsEnabled = state.OpenWith; CtxFolder.IsEnabled = state.OpenFolder;
        CtxNeoBrowser.Visibility = state.OpenNeoBrowser ? Visibility.Visible : Visibility.Collapsed; // 1.12.0: the failed download can be retried in the NeoDM browser
        CtxOpenPage.Visibility = state.OpenPage ? Visibility.Visible : Visibility.Collapsed; // only for a download that came back as a web page
        CtxDownloadAsStream.Visibility = state.DownloadAsStream ? Visibility.Visible : Visibility.Collapsed; // only for a download that came back as a streaming playlist
        CtxCopyUrl.IsEnabled = state.CopyUrl; CtxCopyPath.IsEnabled = state.CopyPath; CtxCopyError.IsEnabled = state.CopyError; CtxMagnet.IsEnabled = state.CopyMagnet;
        CtxMove.IsEnabled = state.MoveRename; CtxProperties.IsEnabled = state.Properties; CtxDetails.IsEnabled = state.TorrentDetails;
        CtxQueue.IsEnabled = state.AddToQueue; CtxQueue.Visibility = rows.All(r => r.IsTorrent) ? Visibility.Collapsed : Visibility.Visible; CtxResume.IsEnabled = state.Resume; CtxStop.IsEnabled = state.Stop; CtxRefreshUrl.IsEnabled = state.RefreshAddress; CtxRedownload.IsEnabled = state.Redownload;
        CtxSpeed.IsEnabled = state.SpeedLimit; CtxSchedule.IsEnabled = state.Schedule;
        CtxRemove.IsEnabled = state.Remove; CtxDelete.IsEnabled = state.DeleteFromDisk;
        return true;
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
        Notice.Text = SafeClipboard.TrySetText(text) ? success : Loc.T("main.notice.copy_failed");
    }
    private async void ContextDownloadAsStream_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { OffersStream: true } row) await DownloadAsStreamAsync(row.Id);
    }
    private void ContextNeoBrowser_Click(object sender, RoutedEventArgs e) { if (Selected is { OffersBrowser: true } row) OpenNeoBrowser(row.Id); }
    private NeoBrowserWindow? _neoBrowser, _neoBrowserGeneral;
    /// <summary>1.13.0: where downloads and videos found in the NeoDM browser go (the app's capture flow, which shows the normal dialogs). Set by App; null in tests.</summary>
    public Action<NativeBridge.BrowserDownloadRequest>? NeoBrowserSink { get; set; }
    private void NeoBrowser_Click(object sender, RoutedEventArgs e) => OpenNeoBrowser();
    /// <summary>1.13.0: the NeoDM browser from the Tasks menu: tabs, a Home page, downloads and videos handed to the capture dialogs. One window; a second request brings it forward.</summary>
    public NeoBrowserWindow? OpenNeoBrowser()
    {
        if (_closed) return null;
        if (_neoBrowserGeneral is { IsLoaded: true } open) { WindowActivation.BringToFront(open); return open; }
        var window = new NeoBrowserWindow(null, captureSink: request => NeoBrowserSink?.Invoke(request)) { Owner = null };
        _neoBrowserGeneral = window; window.Closed += (_, _) => { if (ReferenceEquals(_neoBrowserGeneral, window)) _neoBrowserGeneral = null; };
        window.Show();
        return window;
    }
    /// <summary>1.12.0: opens the NeoDM browser for a failed download at the page it came from (or the site's start page). The first download started there goes back to the same job.</summary>
    public void OpenNeoBrowser(string jobId)
    {
        if (_closed) return;
        if (_neoBrowser is { IsLoaded: true } open) { WindowActivation.BringToFront(open); return; }
        var job = _manager.Jobs.FirstOrDefault(j => j.Id == jobId); if (job is null) return;
        var start = LoginBrowserLogic.StartUrl(job.Referer, job.Url);
        if (start is null) { Notice.Text = Loc.T("neobrowser.status.not_capturable"); return; }
        var window = new NeoBrowserWindow(start, grab => OnBrowserGrabAsync(jobId, grab)) { Owner = null }; // the failed-download mode: the first download goes back to this job
        _neoBrowser = window; window.Closed += (_, _) => { if (ReferenceEquals(_neoBrowser, window)) _neoBrowser = null; };
        window.Show();
    }
    private async Task OnBrowserGrabAsync(string jobId, BrowserGrab grab)
    {
        if (_closed) return;
        try
        {
            var job = _manager.Jobs.FirstOrDefault(j => j.Id == jobId);
            if (job is not null && job.Status != "Completed" && LoginBrowserLogic.Matches(job.Url, job.Referer, job.FileName, grab))
            {
                _manager.RefreshFromBrowser(jobId, grab); _manager.Resume(jobId); // the same job, with the new address and sign-in details
                Notice.Text = Loc.T("neobrowser.notice.updated");
                OpenProgress(jobId, foreground: true);
            }
            else if (LoginBrowserLogic.Clean(grab) is { } clean)
            {
                var added = await _manager.AddAsync(new DownloadRequest(clean.Url, clean.FileName, clean.Referer, clean.Cookie, AutoName: true, UserAgent: clean.UserAgent, Accept: clean.Accept, AcceptLanguage: clean.AcceptLanguage));
                Notice.Text = Loc.T("neobrowser.notice.added");
                OpenProgress(added.Id, foreground: true);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException) { Notice.Text = ex.Message; }
        Refresh();
    }
    private void ContextOpenPage_Click(object sender, RoutedEventArgs e)
    {
        if (Selected?.PageToOpen is { } page) UrlOpener.Open(page);
    }
    private void ContextCopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if (RowMenu.LinkText(SelectedRows) is { } text) CopyToClipboard(text, Loc.Plural("main.notice.addresses_copied", SelectedRows.Count(r => !r.IsTorrent && r.Url.Length > 0)));
    }
    private void ContextCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (RowMenu.PathText(SelectedRows) is { } text) CopyToClipboard(text, Loc.Plural("main.notice.paths_copied", SelectedRows.Count(r => !string.IsNullOrEmpty(r.FilePath))));
    }
    private void ContextCopyError_Click(object sender, RoutedEventArgs e)
    {
        if (RowMenu.ErrorText(SelectedRows) is { } text) CopyToClipboard(text, Loc.T("main.notice.error_copied"));
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
