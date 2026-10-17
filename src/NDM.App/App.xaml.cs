using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
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
            _torrents = new TorrentController(Path.Combine(StateDirectory, "torrent"), Settings);
            _torrentFeature = new TorrentFeature(_torrents, Settings);
            var window = new MainWindow(_manager, Settings, () => Settings.Save(StateDirectory), enableTray: true, power: null, torrents: _torrentFeature);
            window.JobCompletion = new JobCompletionStore(Path.Combine(StateDirectory, "job-completion.json"));
            window.JobCompletion.Prune(_manager.Jobs.Select(j => j.Id));
            MainWindow = window;
            window.Closed += (_, _) => Shutdown();
            SetUpUpdates(window);
            _bridge = new NativeBridge(HandleBrowserDownload, PipeName, HandleControl);
            _bridge.Start();
            if (startup.Tray) window.StartInTray(); else window.Show();
            // The engine (and its listening socket) only exists once the user has torrents.
            if (TorrentController.HasSavedTorrents(Path.Combine(StateDirectory, "torrent"))) _ = _torrents.EnsureAsync().ContinueWith(_ => { }, TaskScheduler.Default);
            foreach (var magnet in startup.Magnets) Dispatcher.InvokeAsync(() => window.AddMagnetAsync(magnet));
            foreach (var file in startup.TorrentFiles) Dispatcher.InvokeAsync(() => window.AddTorrentPathAsync(Path.GetFullPath(file)));
        }
        catch (Exception ex)
        {
            MessageBox.Show("NeoDM could not start. Check that your destination and state folders are writable.\n\n" + ex.Message, "NeoDM", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
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
        Updates.ShowMessage = text => MessageBox.Show(window is { IsVisible: true } ? window : null, text, "NeoDM updates", MessageBoxButton.OK, MessageBoxImage.Information);
        window.Updates = Updates;
        Updates.Start();
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
            try { messages.Add((NativeBridge.ActionAddTorrent, Path.GetFullPath(file))); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        if (messages.Count == 0) messages.Add((NativeBridge.ActionShow, null));
        try { AllowSetForegroundWindow(-1); } catch (Exception) { }
        foreach (var (action, value) in messages)
        {
            var result = Task.Run(() => NativeBridge.SendControlAsync(PipeName, action, value, TimeSpan.FromSeconds(8))).GetAwaiter().GetResult();
            if (!result.Ok)
            {
                MessageBox.Show("NeoDM is already running but did not accept the request.\n\n" + (result.Error ?? ""), "NeoDM", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                    fileName => CategoryHelper.GetDownloadPath(fileName, Settings));
                // The wait for the user is unbounded; if the browser side goes away meanwhile the dialog says so and can no longer add a duplicate.
                request.Gone.Register(() => Dispatcher.BeginInvoke(new Action(() => dialog.MarkExpired(
                    "The browser stopped waiting for NeoDM (the browser or its connection was closed), so it has restarted this download itself. " +
                    "Adding it here would download it twice. Close this window."))));
                // Version notice: shown inside the dialog that is about to appear; it never delays or blocks the download.
                ExtensionNoticeFlow.Present(_extensionNotice, request.ExtVersion, dialog);
                bool accepted = await CaptureFlow.ShowAsync(dialog, windows);

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
                if (CaptureCommit.StartsNow(dialog) && MainWindow is MainWindow main) main.ShowCapturedProgress(job.Id);
                request.Respond(new NativeBridge.BrowserDownloadResponse(true, job.Id));
            }
            catch (Exception ex)
            {
                request.Respond(new NativeBridge.BrowserDownloadResponse(false, null, ex.Message));
            }
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _bridge?.Dispose();
        _torrentFeature?.Dispose(); // stops torrents and writes fast-resume data 
        _manager?.Dispose();
        if (_ownsMutex) _singleton?.ReleaseMutex();
        _singleton?.Dispose();
        base.OnExit(e);
    }
}
