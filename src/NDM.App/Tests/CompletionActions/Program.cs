// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.IO;
using System.Net;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NDM.App;
using NDM.Core;

// Same isolation as the UI tests: never run the production OnStartup (singleton mutex, real state, pipe).
internal sealed class TestApp : NDM.App.App
{
    public TestApp()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown; // Standalone test windows close constantly; the first one must not end the app.
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Light.xaml") });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Controls.xaml") });
    }
    protected override void OnStartup(StartupEventArgs e) { }
}

/// <summary>Records what would have been done to the machine. The real SystemPowerActions is never constructed here.</summary>
internal sealed class FakePower : IPowerActions
{
    public List<(CompletionActionKind Kind, bool Force)> Calls { get; } = new();
    public void Execute(CompletionActionKind kind, bool force) => Calls.Add((kind, force));
}

internal sealed class FakeCountdown : ICountdownPrompt
{
    public CountdownOutcome Result = CountdownOutcome.Elapsed;
    public List<(CompletionActionKind Kind, int Seconds)> Shown { get; } = new();
    public CountdownOutcome Show(CompletionActionKind kind, int seconds) { Shown.Add((kind, seconds)); return Result; }
}

internal static class CompletionActionTests
{
    private static int passed;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        passed++; Console.WriteLine("PASS: " + name);
    }
    private static void Pump(int milliseconds = 300)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static bool PumpUntil(Func<bool> condition, int timeoutMs = 15000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < end) Pump(100);
        return condition();
    }
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private static QueueJob J(string id, string status, DateTimeOffset? at = null) => new(id, status, at);
    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [STAThread]
    public static int Main()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", "NDM-CompletionTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack; var app = new TestApp();
        try
        {
            Triggers(); Settings(root); Commands(); Countdown(); Runner(root); Menus(root); SettingsDialog(root); EndToEnd(root);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
        Console.WriteLine($"All {passed} completion action checks passed.");
        return 0;
    }

    static void Triggers()
    {
        // Restored history: completed jobs present at start never fire, even after churn.
        var t = new QueueFinishTrigger(new[] { J("old1", "Completed"), J("old2", "Completed"), J("old3", "Failed") });
        Check(!t.Observe(new[] { J("old1", "Completed"), J("old2", "Completed"), J("old3", "Failed") }, Now), "No fire at start for restored history");
        Check(!t.Observe(new[] { J("old1", "Completed") }, Now), "No fire when history jobs are removed");

        // Single batch: fires exactly once on the busy->idle edge.
        t = new QueueFinishTrigger(new[] { J("old1", "Completed") });
        Check(!t.Observe(new[] { J("old1", "Completed"), J("a", "Queued"), J("b", "Queued") }, Now), "No fire while jobs queued");
        Check(!t.Observe(new[] { J("old1", "Completed"), J("a", "Downloading"), J("b", "Queued") }, Now), "No fire while downloading");
        Check(!t.Observe(new[] { J("old1", "Completed"), J("a", "Completed"), J("b", "Connecting") }, Now), "No fire while one job still connecting");
        Check(t.Observe(new[] { J("old1", "Completed"), J("a", "Completed"), J("b", "Completed") }, Now), "Fires when last active job completes");
        Check(!t.Observe(new[] { J("old1", "Completed"), J("a", "Completed"), J("b", "Completed") }, Now), "Does not fire a second time while idle");
        Check(!t.Observe(new[] { J("old1", "Completed"), J("a", "Completed"), J("b", "Completed") }, Now.AddMinutes(5)), "Idle polling never re-fires");

        // Re-arms on the next batch.
        Check(!t.Observe(new[] { J("a", "Completed"), J("c", "Downloading") }, Now), "Next batch: active again");
        Check(t.Observe(new[] { J("a", "Completed"), J("c", "Completed") }, Now), "Re-armed: fires for the next batch");

        // Failures only / paused only never fire.
        t = new QueueFinishTrigger(Array.Empty<QueueJob>());
        t.Observe(new[] { J("f", "Downloading") }, Now);
        Check(!t.Observe(new[] { J("f", "Failed") }, Now), "No fire when only a failure ended the batch");
        t.Observe(new[] { J("f", "Failed"), J("p", "Downloading") }, Now);
        Check(!t.Observe(new[] { J("f", "Failed"), J("p", "Paused") }, Now), "No fire when the only active job is paused");
        t.Observe(new[] { J("f", "Failed"), J("p", "Paused"), J("g", "Queued") }, Now);
        Check(t.Observe(new[] { J("f", "Failed"), J("p", "Paused"), J("g", "Completed") }, Now), "Completed job amid failed/paused jobs fires");
        // Failure counters do not leak into the next batch.
        t.Observe(new[] { J("h", "Downloading") }, Now);
        Check(!t.Observe(new[] { J("h", "Failed") }, Now), "Failure-only batch after a fired batch does not fire");

        // Fast job first observed already complete after startup still counts as this run's completion.
        t = new QueueFinishTrigger(Array.Empty<QueueJob>());
        Check(t.Observe(new[] { J("quick", "Completed") }, Now), "Job first seen complete after start counts");

        // Scheduled job pending in the future holds the trigger; releases when it runs.
        t = new QueueFinishTrigger(Array.Empty<QueueJob>());
        t.Observe(new[] { J("a", "Downloading"), J("s", "Scheduled", Now.AddHours(2)) }, Now);
        Check(!t.Observe(new[] { J("a", "Completed"), J("s", "Scheduled", Now.AddHours(2)) }, Now), "No fire while a scheduled job is pending in the future");
        Check(!t.Observe(new[] { J("a", "Completed"), J("s", "Downloading") }, Now.AddHours(2)), "Scheduled job running: still not fired");
        Check(t.Observe(new[] { J("a", "Completed"), J("s", "Completed") }, Now.AddHours(2)), "Fires after the scheduled job completes");
        t = new QueueFinishTrigger(Array.Empty<QueueJob>()) { IgnoreScheduled = true };
        t.Observe(new[] { J("a", "Downloading"), J("s", "Scheduled", Now.AddHours(2)) }, Now);
        Check(t.Observe(new[] { J("a", "Completed"), J("s", "Scheduled", Now.AddHours(2)) }, Now), "Ignore-scheduled option fires despite future schedule");
        t = new QueueFinishTrigger(Array.Empty<QueueJob>());
        t.Observe(new[] { J("a", "Downloading"), J("s", "Scheduled", Now.AddHours(-1)) }, Now);
        Check(t.Observe(new[] { J("a", "Completed"), J("s", "Scheduled", Now.AddHours(-1)) }, Now), "Scheduled time already past does not hold");
    }

    static void Settings(string root)
    {
        string dir = Path.Combine(root, "settings"); Directory.CreateDirectory(dir);
        // Old file written before this feature existed.
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"Theme\":\"Dark\",\"MaxSegments\":6,\"FutureKey\":1}");
        var s = AppSettings.Load(dir);
        Check(s.CompletionAction == CompletionActionKind.None && s.SessionCompletionAction == CompletionActionKind.None && !s.CompletionRemember && !s.CompletionForceClose && !s.RunProgramEnabled && s.RunProgramPath == "" && !s.CompletionIgnoreScheduled, "Old settings file migrates to Do nothing defaults");
        Check(s.Theme == "Dark" && s.MaxSegments == 6 && s.AdditionalSettings!.ContainsKey("FutureKey"), "Migration keeps existing and unknown settings");

        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"CompletionAction\":\"Banana\",\"CompletionRemember\":true,\"RunProgramPath\":null}");
        s = AppSettings.Load(dir);
        Check(s.CompletionAction == CompletionActionKind.None && s.SessionCompletionAction == CompletionActionKind.None && s.RunProgramPath == "", "Unknown action name falls back to Do nothing");
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"CompletionAction\":99,\"CompletionRemember\":true}");
        Check(AppSettings.Load(dir).CompletionAction == CompletionActionKind.None, "Out-of-range numeric action falls back to Do nothing");

        var w = new AppSettings { CompletionAction = CompletionActionKind.Hibernate, CompletionRemember = true, CompletionForceClose = true, CompletionIgnoreScheduled = true, RunProgramEnabled = true, RunProgramPath = @"C:\x\y.exe", RunProgramArguments = "-a b" };
        w.Save(dir);
        Check(File.ReadAllText(Path.Combine(dir, "settings.json")).Contains("\"CompletionAction\": \"Hibernate\""), "Action persists as readable name");
        var r = AppSettings.Load(dir);
        Check(r.CompletionAction == CompletionActionKind.Hibernate && r.SessionCompletionAction == CompletionActionKind.Hibernate && r.CompletionForceClose && r.CompletionIgnoreScheduled && r.RunProgramEnabled && r.RunProgramPath == @"C:\x\y.exe" && r.RunProgramArguments == "-a b", "All completion settings round-trip; remembered action becomes the session action");
        w.CompletionRemember = false; w.Save(dir);
        r = AppSettings.Load(dir);
        Check(r.CompletionAction == CompletionActionKind.Hibernate && r.SessionCompletionAction == CompletionActionKind.None, "Without Remember the session starts at Do nothing");
        var copy = r.Copy(); copy.SessionCompletionAction = CompletionActionKind.Sleep;
        r.CopyFrom(copy);
        Check(r.SessionCompletionAction == CompletionActionKind.Sleep, "CopyFrom carries the session action");
        Check(!File.ReadAllText(Path.Combine(dir, "settings.json")).Contains("SessionCompletionAction"), "Session action is never serialized");
    }

    static void Commands()
    {
        string sys = Environment.SystemDirectory;
        Check(PowerCommands.For(CompletionActionKind.Shutdown, false) == new SystemCommand(Path.Combine(sys, "shutdown.exe"), "/s /t 0"), "Shutdown command line: shutdown.exe /s /t 0");
        Check(PowerCommands.For(CompletionActionKind.Shutdown, true)!.Arguments == "/s /f /t 0", "Shutdown force adds /f");
        Check(PowerCommands.For(CompletionActionKind.Restart, false)!.Arguments == "/r /t 0" && PowerCommands.For(CompletionActionKind.Restart, true)!.Arguments == "/r /f /t 0", "Restart command lines");
        Check(PowerCommands.For(CompletionActionKind.Hibernate, true)!.Arguments == "/h" && PowerCommands.For(CompletionActionKind.LogOff, true)!.Arguments == "/l", "Hibernate and log off ignore force");
        Check(PowerCommands.For(CompletionActionKind.Sleep, false) == new SystemCommand(Path.Combine(sys, "rundll32.exe"), "powrprof.dll,SetSuspendState 0,1,0"), "Sleep via rundll32 powrprof.dll,SetSuspendState");
        Check(PowerCommands.For(CompletionActionKind.Lock, false) == new SystemCommand(Path.Combine(sys, "rundll32.exe"), "user32.dll,LockWorkStation"), "Lock via rundll32 user32.dll,LockWorkStation");
        Check(File.Exists(Path.Combine(sys, "shutdown.exe")) && File.Exists(Path.Combine(sys, "rundll32.exe")), "Command executables exist (not run)");
        Check(new[] { CompletionActionKind.None, CompletionActionKind.PlaySound, CompletionActionKind.OpenFolder, CompletionActionKind.ExitApp }.All(k => PowerCommands.For(k, true) is null), "Non-power actions have no system command");
        Check(CompletionActionInfo.All.Count == 10 && CompletionActionInfo.All.All(k => CompletionActionInfo.Label(k).Length > 0 && (k == CompletionActionKind.None || CompletionActionInfo.Description(k).Length > 0)), "Every action has a label and description");
    }

    static void Countdown()
    {
        var w = new CountdownWindow(CompletionActionKind.Shutdown, 30);
        Check(w.MessageText == "NeoDM will turn off the computer in 30 seconds", "Countdown message wording");
        Check(w.Topmost, "Countdown is always on top");
        Check((string)w.CancelButton.Content == "_Cancel" && (string)w.NowButton.Content == "_Do it now", "Countdown has Cancel and Do it now");
        w.Show(); Click(w.CancelButton); Pump(50);
        Check(w.Outcome == CountdownOutcome.Cancelled && !w.IsVisible, "Cancel button cancels and closes");

        w = new CountdownWindow(CompletionActionKind.Restart, 30); w.Show(); Click(w.NowButton); Pump(50);
        Check(w.Outcome == CountdownOutcome.DoItNow && !w.IsVisible, "Do it now proceeds immediately");

        w = new CountdownWindow(CompletionActionKind.Sleep, 3); w.Show();
        w.Tick(); Check(w.Remaining == 2 && w.MessageText == "NeoDM will put the computer to sleep in 2 seconds", "Tick decrements the displayed countdown");
        w.Tick(); Check(w.MessageText.EndsWith("in 1 second"), "Singular second");
        w.Tick(); Pump(50);
        Check(w.Outcome == CountdownOutcome.Elapsed && !w.IsVisible, "Elapsed countdown proceeds");

        w = new CountdownWindow(CompletionActionKind.Shutdown, 30); w.Show(); w.Close();
        Check(w.Outcome == CountdownOutcome.Cancelled, "Closing the window by other means counts as cancel");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var outcome = new CountdownPrompt().Show(CompletionActionKind.Shutdown, 1); // real modal ShowDialog, real 1s timer
        Console.WriteLine($"outcome={outcome} ms={sw.ElapsedMilliseconds}"); Check(outcome == CountdownOutcome.Elapsed && sw.ElapsedMilliseconds >= 500, "Real modal countdown elapses on its own timer");
        var timerCancel = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timerCancel.Tick += (_, _) => { timerCancel.Stop(); foreach (var cw in Application.Current.Windows.OfType<CountdownWindow>()) Click(cw.CancelButton); };
        timerCancel.Start();
        Check(new CountdownPrompt().Show(CompletionActionKind.Shutdown, 30) == CountdownOutcome.Cancelled, "Real modal countdown can be cancelled");
    }

    static CompletionActionRunner NewRunner(AppSettings s, FakePower power, FakeCountdown cd, Action? save = null, Action<System.Diagnostics.ProcessStartInfo>? launch = null, List<string>? log = null)
        => new(s, power, cd, save ?? (() => { }), () => log?.Add("sound"), () => log?.Add("folder"), () => log?.Add("exit"), launch);

    static void Runner(string root)
    {
        var power = new FakePower(); var cd = new FakeCountdown(); var log = new List<string>();
        var s = new AppSettings(); var r = NewRunner(s, power, cd, log: log);
        Check(!r.Run() && power.Calls.Count == 0 && cd.Shown.Count == 0, "Do nothing does nothing");
        r.Choose(CompletionActionKind.Shutdown); s.CompletionForceClose = false;
        Check(r.Run() && cd.Shown.Single() == (CompletionActionKind.Shutdown, 30) && power.Calls.Single() == (CompletionActionKind.Shutdown, false), "Shutdown: 30s countdown then power call without force");
        s.CompletionForceClose = true; r.Run();
        Check(power.Calls[1] == (CompletionActionKind.Shutdown, true), "Force close flag reaches the power call for shutdown");
        r.Choose(CompletionActionKind.Restart); r.Run();
        Check(power.Calls[2] == (CompletionActionKind.Restart, true), "Restart receives force");
        r.Choose(CompletionActionKind.Sleep); r.Run();
        Check(power.Calls[3] == (CompletionActionKind.Sleep, false), "Force never applied to sleep");

        power.Calls.Clear(); cd.Shown.Clear(); cd.Result = CountdownOutcome.Cancelled; r.Choose(CompletionActionKind.Shutdown);
        int changes = 0; r.SessionActionChanged += () => changes++;
        Check(!r.Run() && power.Calls.Count == 0, "Cancelled countdown never calls the power action");
        Check(s.SessionCompletionAction == CompletionActionKind.None && r.Current == CompletionActionKind.None && changes == 1, "Cancel resets the session to Do nothing and notifies");
        cd.Result = CountdownOutcome.DoItNow; r.Choose(CompletionActionKind.Hibernate);
        Check(r.Run() && power.Calls.Single() == (CompletionActionKind.Hibernate, false), "Do it now proceeds");

        power.Calls.Clear(); cd.Shown.Clear(); log.Clear();
        r.Choose(CompletionActionKind.PlaySound); r.Run();
        r.Choose(CompletionActionKind.OpenFolder); r.Run();
        r.Choose(CompletionActionKind.ExitApp); r.Run();
        Check(log.SequenceEqual(new[] { "sound", "folder", "exit" }) && power.Calls.Count == 0 && cd.Shown.Count == 0, "Sound/folder/exit run without countdown or power calls");
        r.Choose(CompletionActionKind.Lock); r.Run();
        Check(power.Calls.Single() == (CompletionActionKind.Lock, false) && cd.Shown.Count == 0, "Lock runs without countdown");

        // Remember semantics and cancel not touching the remembered value.
        int saves = 0; s = new AppSettings { CompletionRemember = true }; cd = new FakeCountdown { Result = CountdownOutcome.Cancelled }; power = new FakePower();
        r = NewRunner(s, power, cd, () => saves++);
        r.Choose(CompletionActionKind.Shutdown);
        Check(saves == 1 && s.CompletionAction == CompletionActionKind.Shutdown, "Remember persists the choice");
        r.Run();
        Check(s.SessionCompletionAction == CompletionActionKind.None && s.CompletionAction == CompletionActionKind.Shutdown, "Cancel resets session only; remembered value stays");
        s = new AppSettings { CompletionRemember = false }; saves = 0; r = NewRunner(s, power, cd, () => saves++); r.Choose(CompletionActionKind.Sleep);
        Check(saves == 0 && s.CompletionAction == CompletionActionKind.None && s.SessionCompletionAction == CompletionActionKind.Sleep, "Without Remember nothing is saved");
        s = new AppSettings { CompletionRemember = true }; r = NewRunner(s, power, cd, () => throw new IOException());
        r.Choose(CompletionActionKind.Lock);
        Check(s.SessionCompletionAction == CompletionActionKind.Lock && s.CompletionAction == CompletionActionKind.None && r.LastError is not null, "Save failure keeps session choice and reports it");

        // Run-program validation.
        string dir = Path.Combine(root, "prog"); Directory.CreateDirectory(dir);
        string exe = Path.Combine(Environment.SystemDirectory, "whoami.exe");
        string cmd = Path.Combine(dir, "t.cmd"); File.WriteAllText(cmd, "@echo off\r\n");
        string lnk = Path.Combine(dir, "t.lnk"); File.WriteAllText(lnk, "x");
        string txt = Path.Combine(dir, "t.txt"); File.WriteAllText(txt, "x");
        Check(RunProgramValidator.Validate(exe, "/all") is null, "Valid exe and arguments accepted");
        Check(RunProgramValidator.Validate("", "") is not null && RunProgramValidator.Validate("  ", null) is not null, "Empty path rejected");
        Check(RunProgramValidator.Validate("whoami.exe", "") is not null, "Relative / PATH-searched program rejected");
        Check(RunProgramValidator.Validate(Path.Combine(dir, "missing.exe"), "") is not null, "Missing file rejected");
        Check(RunProgramValidator.Validate(txt, "") is not null, "Non-program extension rejected");
        Check(RunProgramValidator.Validate(exe, "a\r\nb") is not null && RunProgramValidator.Validate(exe, "a\0b") is not null, "Line breaks / NUL in arguments rejected");
        Check(RunProgramValidator.Validate(exe, new string('a', 2001)) is not null, "Overlong arguments rejected");
        Check(RunProgramValidator.Validate(exe, "a & calc") is null, "Metacharacters are inert for direct exe launch (no shell)");
        Check(RunProgramValidator.Validate(cmd, "a & calc") is not null && RunProgramValidator.Validate(cmd, "%PATH%") is not null && RunProgramValidator.Validate(cmd, "plain arg") is null, "Metacharacters rejected for .cmd/.bat only");
        Check(RunProgramValidator.Validate(exe + "\" & calc & \"", "") is not null, "Quote injection in path rejected");
        var psi = RunProgramValidator.BuildStartInfo(exe, "/all");
        Check(!psi.UseShellExecute && psi.FileName == exe && psi.Arguments == "/all" && psi.WorkingDirectory == Environment.SystemDirectory, "Exe starts directly: UseShellExecute=false, arguments passed verbatim");
        Check(!RunProgramValidator.BuildStartInfo(cmd, "x").UseShellExecute && RunProgramValidator.BuildStartInfo(lnk, "").UseShellExecute, "Only .lnk uses the shell");
        bool threw = false; try { RunProgramValidator.BuildStartInfo(txt, ""); } catch (FormatException) { threw = true; }
        Check(threw, "BuildStartInfo refuses invalid input");

        // Run program is ordered before the power call, skipped on cancel, and runs with Do nothing.
        var order = new List<string>(); power = new FakePower();
        s = new AppSettings { RunProgramEnabled = true, RunProgramPath = exe, RunProgramArguments = "/all" };
        var pw = new OrderPower(order);
        r = new CompletionActionRunner(s, pw, new FakeCountdown { Result = CountdownOutcome.Elapsed }, () => { }, () => { }, () => { }, () => { }, p => order.Add("launch:" + p.FileName + " " + p.Arguments));
        r.Choose(CompletionActionKind.Shutdown); r.Run();
        Check(order.SequenceEqual(new[] { "launch:" + exe + " /all", "power:Shutdown" }), "Program launches before the power action");
        order.Clear(); r.Choose(CompletionActionKind.None); r.Run();
        Check(order.Count == 1 && order[0].StartsWith("launch:"), "Program runs even with Do nothing");
        order.Clear(); r = new CompletionActionRunner(s, pw, new FakeCountdown { Result = CountdownOutcome.Cancelled }, () => { }, () => { }, () => { }, () => { }, p => order.Add("launch"));
        r.Choose(CompletionActionKind.Shutdown); r.Run();
        Check(order.Count == 0, "Cancelled countdown also skips the program");
        s.RunProgramPath = Path.Combine(dir, "missing.exe");
        r = new CompletionActionRunner(s, pw, new FakeCountdown(), () => { }, () => { }, () => { }, () => { }, p => order.Add("launch"));
        r.Choose(CompletionActionKind.Lock); order.Clear(); r.Run();
        Check(r.LastError is not null && order.Count == 1 && order[0] == "power:Lock", "Invalid stored program is reported and does not block the action");

        // One real launch of a harmless program through the default launcher.
        s = new AppSettings { RunProgramEnabled = true, RunProgramPath = exe, RunProgramArguments = "/user" };
        r = new CompletionActionRunner(s, new FakePower(), new FakeCountdown(), () => { }, () => { }, () => { }, () => { });
        r.Run();
        Check(r.LastError is null, "Real launch of whoami.exe through the default launcher succeeds");
    }
    private sealed class OrderPower : IPowerActions
    {
        private readonly List<string> log; public OrderPower(List<string> log) => this.log = log;
        public void Execute(CompletionActionKind kind, bool force) => log.Add("power:" + kind);
    }

    static void Menus(string root)
    {
        string state = Path.Combine(root, "menus"); Directory.CreateDirectory(state);
        using var manager = new DownloadManager(Path.Combine(state, "files"), Path.Combine(state, "state"));
        var settings = new AppSettings { DownloadDirectory = Path.Combine(state, "files"), CompletionRemember = true };
        int saves = 0; var power = new FakePower();
        var window = new MainWindow(manager, settings, () => saves++, enableTray: false, power) { ShowInTaskbar = false };
        try
        {
            window.Show(); Pump();
            var menu = window.FinishSubmenu;
            var items = menu.Items.OfType<MenuItem>().ToList();
            Check(menu.Header.ToString() == "When downloads _finish" && items.Count == 10, "Downloads menu has the 'When downloads finish' submenu with 10 choices");
            Check(((DependencyObject)menu).GetType() == typeof(MenuItem) && ItemParentHeader(menu) == "_Downloads", "Submenu lives under the Downloads menu");
            string Checked() => string.Join(",", items.Where(i => i.IsChecked).Select(i => i.Header));
            Check(Checked() == "Do nothing", "Default choice is checked: Do nothing");
            Click(items.Single(i => (CompletionActionKind)i.Tag == CompletionActionKind.Sleep));
            Check(Checked() == "Sleep" && settings.SessionCompletionAction == CompletionActionKind.Sleep && settings.CompletionAction == CompletionActionKind.Sleep && saves == 1, "Clicking Sleep checks only Sleep, sets session, persists with Remember");
            Click(items.Single(i => (CompletionActionKind)i.Tag == CompletionActionKind.Sleep));
            Check(Checked() == "Sleep", "Clicking the checked item keeps it checked (radio, not toggle)");
            settings.CompletionRemember = false;
            Click(items.Single(i => (CompletionActionKind)i.Tag == CompletionActionKind.OpenFolder));
            Check(Checked() == "Open downloads folder" && settings.CompletionAction == CompletionActionKind.Sleep && saves == 2, "Without Remember the choice is session-only");
            window.FinishActions.Choose(CompletionActionKind.Hibernate);
            Check(Checked() == "Hibernate", "Programmatic change syncs the menu checkmarks");
            settings.SessionCompletionAction = CompletionActionKind.Lock;
            menu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent));
            Check(Checked() == "Lock computer", "Opening the submenu re-syncs from settings");

            // Same builder used for the tray menu.
            var tray = new MenuItem { Header = "When downloads finish" };
            CompletionMenu.Fill(tray, () => window.FinishActions.Current, window.FinishActions.Choose);
            var trayItems = tray.Items.OfType<MenuItem>().ToList();
            Check(trayItems.Count(i => i.IsChecked) == 1 && trayItems.Single(i => i.IsChecked).Tag is CompletionActionKind.Lock, "Tray submenu reflects the current setting");
            Click(trayItems.Single(i => (CompletionActionKind)i.Tag == CompletionActionKind.Restart));
            Check(settings.SessionCompletionAction == CompletionActionKind.Restart && trayItems.Single(i => i.IsChecked).Tag is CompletionActionKind.Restart, "Tray choice updates settings");
            Check(power.Calls.Count == 0, "Menu interaction never executes a power action");
        }
        finally { window.Close(); }
    }
    private static string? ItemParentHeader(MenuItem item) => (item.Parent as MenuItem)?.Header as string;
    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    static void SettingsDialog(string root)
    {
        string state = Path.Combine(root, "dialog"); Directory.CreateDirectory(state);
        var settings = new AppSettings { DownloadDirectory = Path.Combine(state, "files") };
        var w = new SettingsWindow(settings, state);
        T F<T>(string n) where T : class => (T)w.FindName(n);
        var combo = F<ComboBox>("FinishAction");
        Check(combo.Items.Count == 10 && combo.SelectedIndex == 0 && F<TextBlock>("FinishDescription").Text.Length > 0, "Options Completion section lists all actions with a description");
        Check(!F<CheckBox>("FinishForce").IsEnabled, "Force close disabled for Do nothing");
        combo.SelectedIndex = CompletionActionInfo.All.ToList().IndexOf(CompletionActionKind.Shutdown);
        Check(F<CheckBox>("FinishForce").IsEnabled && F<TextBlock>("FinishDescription").Text.Contains("30 second"), "Shutdown enables Force close and updates the description");
        combo.SelectedIndex = CompletionActionInfo.All.ToList().IndexOf(CompletionActionKind.Sleep);
        Check(!F<CheckBox>("FinishForce").IsEnabled, "Force close disabled for Sleep");
        combo.SelectedIndex = CompletionActionInfo.All.ToList().IndexOf(CompletionActionKind.Restart);
        F<CheckBox>("FinishForce").IsChecked = true; F<CheckBox>("RunProgramEnabled").IsChecked = true;
        Check(F<Grid>("RunProgramGrid").IsEnabled, "Run program fields enable with their checkbox");
        F<TextBox>("RunProgramPath").Text = "notepad.exe";
        Click(F<Button>("SaveButton"));
        Check(F<TextBlock>("ErrorText").Text.Length > 0 && !File.Exists(Path.Combine(state, "settings.json")) && settings.SessionCompletionAction == CompletionActionKind.None, "Invalid run-program path blocks saving with an inline error");
        F<TextBox>("RunProgramPath").Text = Path.Combine(Environment.SystemDirectory, "whoami.exe"); F<TextBox>("RunProgramArguments").Text = "/all";
        Click(F<Button>("SaveButton"));
        Check(settings.SessionCompletionAction == CompletionActionKind.Restart && settings.CompletionAction == CompletionActionKind.Restart && settings.CompletionForceClose && settings.RunProgramEnabled && settings.RunProgramArguments == "/all", "Saving in Options applies the choice now and stores it permanently as the default (no Remember checkbox in Options)");
        var disk = AppSettings.Load(state);
        Check(disk.SessionCompletionAction == CompletionActionKind.Restart && disk.CompletionForceClose && disk.RunProgramPath.EndsWith("whoami.exe"), "Persisted file reloads with the chosen action for the next session and keeps other options");

        var w2 = new SettingsWindow(settings, state);
        Check(w2.FindName("FinishRemember") is null, "Options window has no Remember checkbox");
        Click((Button)w2.FindName("SaveButton"));
        disk = AppSettings.Load(state);
        Check(disk.CompletionAction == CompletionActionKind.Restart && disk.SessionCompletionAction == CompletionActionKind.Restart, "Options always stores the action for future sessions");
    }

    static void EndToEnd(string root)
    {
        // A real local HTTP server + real manager + real MainWindow; only the machine-affecting parts are fakes.
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        using var http = new HttpListener(); http.Prefixes.Add($"http://127.0.0.1:{port}/"); http.Start();
        byte[] body = new byte[200_000]; new Random(5).NextBytes(body);
        _ = Task.Run(async () =>
        {
            while (http.IsListening)
            {
                HttpListenerContext c; try { c = await http.GetContextAsync(); } catch { return; }
                try
                {
                    if (c.Request.Url!.AbsolutePath.StartsWith("/bad")) { c.Response.StatusCode = 404; c.Response.Close(); continue; }
                    c.Response.ContentLength64 = body.Length; if (c.Request.HttpMethod != "HEAD") await c.Response.OutputStream.WriteAsync(body); c.Response.Close();
                }
                catch { }
            }
        });
        string state = Path.Combine(root, "e2e"); Directory.CreateDirectory(state);
        using var manager = new DownloadManager(Path.Combine(state, "files"), Path.Combine(state, "state"));
        var settings = new AppSettings { DownloadDirectory = Path.Combine(state, "files") };
        var power = new FakePower(); var cd = new FakeCountdown { Result = CountdownOutcome.Elapsed };
        var window = new MainWindow(manager, settings, () => { }, enableTray: false, power) { ShowInTaskbar = false };
        try
        {
            window.FinishActions.Countdown = cd;
            window.Show(); Pump(700);
            window.FinishActions.Choose(CompletionActionKind.Shutdown);
            Pump(1200);
            Check(power.Calls.Count == 0, "Armed with no download activity: nothing fires");
            var job = manager.AddAsync(new DownloadRequest($"http://127.0.0.1:{port}/good.bin")).GetAwaiter().GetResult();
            Check(PumpUntil(() => power.Calls.Count > 0), "Real download completing triggers the countdown and power action");
            Pump(1500);
            Check(manager.Jobs.Any(j => j.Id == job.Id && j.Status == "Completed") && power.Calls.Single() == (CompletionActionKind.Shutdown, false) && cd.Shown.Single() == (CompletionActionKind.Shutdown, 30), "Exactly one 30s countdown and one (faked) shutdown call");

            // Re-armed: a second batch fires again.
            manager.AddAsync(new DownloadRequest($"http://127.0.0.1:{port}/good2.bin")).GetAwaiter().GetResult();
            Check(PumpUntil(() => power.Calls.Count == 2), "Second batch re-arms and fires again");

            // Cancel at the countdown resets the session and the next batch does nothing.
            cd.Result = CountdownOutcome.Cancelled;
            manager.AddAsync(new DownloadRequest($"http://127.0.0.1:{port}/good3.bin")).GetAwaiter().GetResult();
            Check(PumpUntil(() => cd.Shown.Count == 3), "Third batch shows the countdown");
            Pump(500);
            Check(power.Calls.Count == 2 && window.FinishActions.Current == CompletionActionKind.None
                && window.FinishSubmenu.Items.OfType<MenuItem>().Single(i => i.IsChecked).Tag is CompletionActionKind.None, "Cancel: no power call, session back to Do nothing, menu follows");
            manager.AddAsync(new DownloadRequest($"http://127.0.0.1:{port}/good4.bin")).GetAwaiter().GetResult();
            Pump(3000);
            Check(cd.Shown.Count == 3 && power.Calls.Count == 2, "After cancel, later batches do nothing");
        }
        finally { window.Close(); http.Stop(); }
    }
}
