using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using NDM.App.Localization;
using NDM.Core;

namespace NDM.App;

public partial class App : Application
{
    static App() => WindowSizing.Attach();
    private Mutex? _singleton;
    private bool _ownsMutex;
    private DownloadManager? _manager;
    private NativeBridge? _bridge;
    private ExtensionNotice? _extensionNotice;
    private readonly TrayWindowList _fallbackWindows = new();
    private ITorrentFeature? _torrentFeature;
    private TorrentController? _torrents;
    private BridgeWatchdog? _watchdog;
    private WrongContentWatcher? _wrongContent;
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
    // NDM_STATE_DIR relocates all state and gives that instance its own singleton mutex and pipe, so isolated runs never touch a live queue.
    private static readonly string? StateOverride = Environment.GetEnvironmentVariable("NDM_STATE_DIR") is { Length: > 0 } v ? Path.GetFullPath(v) : null;
    public const string VariantFolder = "NDM";
    public static string StateDirectory => StateOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), VariantFolder);
    private static string InstanceSuffix => StateOverride is null ? "" : "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(StateOverride.ToLowerInvariant())))[..12];
    public static string PipeName => NativeBridge.PipeName + InstanceSuffix;
    public AppSettings Settings { get; private set; } = new();
    /// <summary>Release checks and the update dialog (null until the main window exists).</summary>
    public UpdateController? Updates { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        var startup = StartupArguments.Parse(e.Args);
        // A crash must not leave a ghost icon in the notification area.
        AppDomain.CurrentDomain.UnhandledException += (_, _) => { try { (MainWindow as MainWindow)?.DisposeTray(); } catch (Exception) { } };
        _singleton = new Mutex(true, @"Local\NDM.App" + InstanceSuffix, out _ownsMutex);
        if (!_ownsMutex) { ForwardToRunningInstance(startup); Shutdown(); return; }
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown; // --tray never shows the window, so 'last window closed' cannot decide.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, args) =>
            {
                if (sender is Window window && ReferenceEquals(args.OriginalSource, window))
                    ThemeManager.ApplyChrome(window);
            }));
        try
        {
            Directory.CreateDirectory(StateDirectory);
            WindowPlacements.Configure(StateDirectory);
            Settings = AppSettings.Load(StateDirectory);
            WindowLocalization.EnsureHooked();
            Loc.Apply(Settings.Language); // NEODM_LANG / NEODM_PSEUDO override the saved choice for tests and layout checks
            ApplyTheme(Settings.Theme, Settings.AccentOverride);
            _extensionNotice = new ExtensionNotice(Settings, () => Settings.Save(StateDirectory), AboutInfo.Version);
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, args) =>
            {
                // Follow-Windows theme: re-resolve when the OS app-theme setting changes (and only then; an explicit theme ignores it).
                if (args.Category is Microsoft.Win32.UserPreferenceCategory.General or Microsoft.Win32.UserPreferenceCategory.VisualStyle && ThemeManager.CurrentId == ThemeCatalog.SystemId)
                    Dispatcher.BeginInvoke(() => { if (ThemeManager.CurrentId == ThemeCatalog.SystemId) ApplyTheme(ThemeCatalog.SystemId, ThemeManager.CurrentAccent); });
            };
            Directory.CreateDirectory(Settings.DownloadDirectory);
            _manager = new DownloadManager(
                Settings.DownloadDirectory, 
                StateDirectory, 
                OptionsHelper.CreateDownloadOptions(Settings),
                fileName => CategoryHelper.GetDownloadPath(fileName, Settings));
            _wrongContent = new WrongContentWatcher(RememberBrowserSite);
            _manager.Changed += () => _wrongContent.Observe(_manager.Jobs);
            _torrents = new TorrentController(Path.Combine(StateDirectory, "torrent"), Settings);
            _torrentFeature = new TorrentFeature(_torrents, Settings);
            var window = new MainWindow(_manager, Settings, () => Settings.Save(StateDirectory), enableTray: true, power: null, torrents: _torrentFeature);
            window.JobCompletion = new JobCompletionStore(Path.Combine(StateDirectory, "job-completion.json"));
            window.JobCompletion.Prune(_manager.Jobs.Select(j => j.Id));
            MainWindow = window;
            window.Closed += (_, _) => Shutdown();
            SetUpUpdates(window);
            _bridge = new NativeBridge(HandleBrowserDownload, PipeName, HandleControl) { OnPipeNameTaken = ex => HandlePipeNameTaken(ex, startup: true) };
            _bridge.Start();
            SetUpWatchdog(window);
            if (startup.Tray) window.StartInTray(); else window.Show();
            // The engine (and its listening socket) only exists once the user has torrents.
            if (TorrentController.HasSavedTorrents(Path.Combine(StateDirectory, "torrent"))) _ = _torrents.EnsureAsync().ContinueWith(_ => { }, TaskScheduler.Default);
            foreach (var magnet in startup.Magnets) Dispatcher.InvokeAsync(() => window.AddMagnetAsync(magnet));
            foreach (var file in startup.TorrentFiles) Dispatcher.InvokeAsync(() => AddTorrentFromCommandLine(window, file));
        }
        catch (Exception ex)
        {
            Dialogs.Show(Loc.T("app.start_failed") + "\n\n" + ErrorMessages.Describe(ex), "NeoDM", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private Task AddTorrentFromCommandLine(MainWindow window, string file)
    {
        string full;
        try { full = Path.GetFullPath(file); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return Task.CompletedTask; }
        if (!NDM.Torrent.TorrentValidator.IsPlainLocalPath(file) || !NDM.Torrent.TorrentValidator.IsPlainLocalPath(full))
        { Dialogs.Show(window, Loc.T("torrent.dialog.path_not_local"), Loc.T("torrent.dialog.file_rejected"), MessageBoxButton.OK, MessageBoxImage.Warning); return Task.CompletedTask; }
        return window.AddTorrentPathAsync(full);
    }

    /// <summary>The pipe name is already owned by another process. A real NeoDM answers the self-check: then this is just a second launch (show that window and exit).
    /// Anything else is a squatter: say so in the watchdog log and accept no captures (the extension then keeps the download in the browser).</summary>
    private void HandlePipeNameTaken(Exception reason, bool startup)
    {
        Task.Run(async () =>
        {
            var probe = await NativeBridge.SendControlAsync(PipeName, NativeBridge.ActionSelfCheck, null, TimeSpan.FromSeconds(3));
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (probe.Ok && startup)
                {
                    ForwardToRunningInstance(new StartupArguments(false, Array.Empty<string>(), Array.Empty<string>()));
                    Shutdown();
                }
                else
                {
                    try { new WatchdogLogFile(StateDirectory).Write("The browser capture pipe '" + PipeName + "' is owned by another program (" + reason.GetType().Name + "); NeoDM is not accepting browser downloads. Close that program and restart NeoDM."); } catch (Exception) { }
                    _watchdog?.NotifyCaptureProblem();
                }
            }));
        });
    }

    private void SetUpUpdates(MainWindow window)
    {
        string version = AboutInfo.Version;
        var installer = new UpdateInstaller(UpdateInstaller.DefaultDirectory, version, () => Settings.BuildProxy());
        Updates = new UpdateController(Settings, () => Settings.Save(StateDirectory), version, () => new UpdateChecker(version, () => Settings.BuildProxy()), installer);
        UpdateWindow? open = null;
        Updates.ShowAvailable = release =>
        {
            if (open is { IsLoaded: true }) { WindowActivation.BringToFront(open); return; }
            open = new UpdateWindow(release, Updates) { Owner = window is { IsVisible: true, WindowState: not WindowState.Minimized } ? window : null };
            open.Show();
        };
        Updates.ShowMessage = text => Dialogs.Show(window is { IsVisible: true } ? window : null, text, Loc.T("app.updates_title"), MessageBoxButton.OK, MessageBoxImage.Information);
        window.Updates = Updates;
        Updates.Start();
    }

    /// <summary>Browser connection watchdog: timer, listener restart, notices. The timer only runs when the setting is on.</summary>
    private void SetUpWatchdog(MainWindow window)
    {
        var log = new WatchdogLogFile(StateDirectory);
        // An isolated run (NDM_STATE_DIR) checks but never repairs, so tests and tools cannot rewrite the real browser registration.
        var watchdog = new BridgeWatchdog(WatchdogFactory.CreateEnvironment(PipeName, StateDirectory, allowRepairs: StateOverride is null),
            new SystemRegistryAccess(), new SystemFileAccess(), new HostLauncher(), new SystemPipeProbe(), RecreateListenerAsync, log: log.Write, machine: new SystemMachineState());
        var notifier = new WatchdogNotifier(text => { Dispatcher.BeginInvoke(new Action(() => window.ShowTrayNotice(text))); return true; });
        watchdog.ReportChanged += report => { try { notifier.Handle(report); } catch (Exception ex) { log.Write("Notice failed: " + ex.Message); } };
        _watchdog = watchdog;
        window.Watchdog = watchdog;
        window.ApplyWatchdogSetting = enabled => watchdog.ApplySetting(enabled);
        if (_bridge is not null) _bridge.OnListenerFault = () => watchdog.NotifyCaptureProblem();
        watchdog.ApplySetting(Settings.BridgeWatchdogEnabled);
    }

    /// <summary>Called on a background thread by the watchdog when the listener stopped answering.</summary>
    private Task<bool> RecreateListenerAsync()
    {
        var old = _bridge;
        var fresh = new NativeBridge(HandleBrowserDownload, PipeName, HandleControl) { OnListenerFault = old?.OnListenerFault, OnPipeNameTaken = ex => HandlePipeNameTaken(ex, startup: false) };
        old?.Dispose();
        fresh.Start();
        _bridge = fresh;
        return Task.FromResult(true);
    }

    /// <summary>Applies a theme (and optional accent override) to the whole application at once; open windows repaint through DynamicResource.</summary>
    public void ApplyTheme(string theme, string? accent = null) => ThemeManager.Apply(this, theme, accent);

    /// <summary>Second launch: hand the work (or just 'show yourself') to the running instance, then exit.</summary>
    private void ForwardToRunningInstance(StartupArguments args)
    {
        if (args.Tray && !args.HasWork) return; // A repeated autostart must not pop the window up.
        var messages = new List<(string Action, string? Value)>();
        messages.AddRange(args.Magnets.Select(m => (NativeBridge.ActionAddMagnet, (string?)m)));
        foreach (var file in args.TorrentFiles)
        {
            try
            {
                var full = Path.GetFullPath(file);
                if (NDM.Torrent.TorrentValidator.IsPlainLocalPath(file) && NDM.Torrent.TorrentValidator.IsPlainLocalPath(full)) messages.Add((NativeBridge.ActionAddTorrent, full));
                else Dialogs.Show(Loc.T("torrent.dialog.path_not_local"), "NeoDM", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        if (messages.Count == 0) messages.Add((NativeBridge.ActionShow, null));
        try { AllowSetForegroundWindow(-1); } catch (Exception) { }
        foreach (var (action, value) in messages)
        {
            var result = Task.Run(() => NativeBridge.SendControlAsync(PipeName, action, value, TimeSpan.FromSeconds(8))).GetAwaiter().GetResult();
            if (!result.Ok)
            {
                Dialogs.Show(Loc.T("app.already_running") + "\n\n" + (result.Error ?? ""), "NeoDM", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
    }

    /// <summary>Runs on the pipe thread: validates nothing heavy, queues the UI work and answers immediately.</summary>
    private string? HandleControl(NativeBridge.ControlRequest request)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (MainWindow is not MainWindow window) return;
            switch (request.Action)
            {
                case NativeBridge.ActionShow: window.BringToFront(); break;
                case NativeBridge.ActionAddMagnet when request.Value is { } magnet: _ = window.AddMagnetAsync(magnet); break;
                case NativeBridge.ActionAddTorrent when request.Value is { } path: _ = window.AddTorrentPathAsync(path); break;
            }
        }));
        return null;
    }

    /// <summary>The site goes on the list the browser extension fetches from the native host: from now on it leaves this site's downloads to the browser.</summary>
    private void RememberBrowserSite(string? url) => BrowserSites.Add(StateDirectory, url);

    private void HandleBrowserDownload(NativeBridge.BrowserDownloadRequest request)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                // The browser download is already gone (the extension cancels it before asking), so this dialog must never end up hidden:
                // it is its own top-level window (no owner, own taskbar button, listed in the tray menu), comes up above the browser, and the main
                // window - minimised, hidden or closed to the tray - is neither shown nor activated. Several captures are several independent dialogs.
                var windows = (MainWindow as MainWindow)?.TrayWindows ?? _fallbackWindows;
                var manager = _manager!;
                var dialog = new DownloadOptionsDialog(request.Url, request.FileName, request.Referer, request.Cookie, Settings.DownloadDirectory, request.Mime,
                    // A redirect target that came out of a form POST (or looks like a token address) may be single use: no extra request to read the name, the browser's file name is used.
                    request.NoProbe || NativeBridge.LooksLikeTokenUrl(request.Url) ? null : ct => manager.ProbeNameAsync(new DownloadRequest(request.Url, null, request.Referer, request.Cookie), ct),
                    fileName => CategoryHelper.GetDownloadPath(fileName, Settings), request.Size);
                dialog.OfferHandBack(); // a captured download can always go back to the browser, which restarts it exactly once
                // The page chose this address: an internal one (this computer, the home network) gets a visible warning line. Never blocks.
                _ = PrivateNetwork.IsPrivateUrlAsync(request.Url).ContinueWith(t => { if (t.IsCompletedSuccessfully && t.Result) Dispatcher.BeginInvoke(new Action(dialog.ShowPrivateNetworkWarning)); }, TaskScheduler.Default);
                // The wait for the user is unbounded; if the browser side goes away meanwhile the dialog says so and can no longer add a duplicate.
                request.Gone.Register(() => Dispatcher.BeginInvoke(new Action(() => dialog.MarkExpired(
                    Loc.T("app.capture_expired")))));
                // Version notice: shown inside the dialog that is about to appear; it never delays or blocks the download.
                ExtensionNoticeFlow.Present(_extensionNotice, request.ExtVersion, dialog);
                bool accepted = await CaptureFlow.ShowAsync(dialog, windows);

                if (dialog.HandBack)
                {
                    // The user wants the browser to download it. The extension reads the answer and remembers the site itself, so removing it later in the popup sticks.
                    request.Respond(new NativeBridge.BrowserDownloadResponse(false, null, "The download was handed back to the browser.", HandBack: true));
                    return;
                }
                if (!accepted || dialog.Request is null)
                {
                    // The user pressed Cancel: discard on purpose (the extension does not restart the browser download for this reply).
                    request.Respond(new NativeBridge.BrowserDownloadResponse(false, null, "Download cancelled in NeoDM.", Cancelled: true));
                    return;
                }

                // The browser side hung up while the dialog was open (the browser already restarted the download): adding now would duplicate it.
                if (request.Gone.IsCancellationRequested)
                {
                    request.Respond(new NativeBridge.BrowserDownloadResponse(false, null, "The browser connection was lost before you decided."));
                    return;
                }
                var job = await CaptureCommit.AddAsync(manager, dialog);
                if (job is null)
                {
                    request.Respond(new NativeBridge.BrowserDownloadResponse(false, null, "Download cancelled in NeoDM.", Cancelled: true));
                    return;
                }
                _wrongContent?.Captured(job.Id);
                if (CaptureCommit.StartsNow(dialog) && MainWindow is MainWindow main) main.ShowCapturedProgress(job.Id);
                request.Respond(new NativeBridge.BrowserDownloadResponse(true, job.Id));
            }
            catch (Exception ex)
            {
                _watchdog?.NotifyCaptureProblem(); // a capture that failed may point at the connection; the watchdog rate-limits itself
                request.Respond(new NativeBridge.BrowserDownloadResponse(false, null, ex.Message));
            }
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _watchdog?.Dispose();
        (MainWindow as MainWindow)?.DisposeTray(); // also covers shutdown paths that skip the window's Closed event
        _bridge?.Dispose();
        _torrentFeature?.Dispose(); // stops torrents and writes fast-resume data 
        _manager?.Dispose();
        if (_ownsMutex) _singleton?.ReleaseMutex();
        _singleton?.Dispose();
        base.OnExit(e);
    }
}
