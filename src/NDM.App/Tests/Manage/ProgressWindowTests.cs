using System.IO;
using System.Windows;
using System.Windows.Controls;
using NDM.App;
using NDM.Core;
using NDM.Tests;

/// <summary>Tabbed progress window, per-download completion options and browser-captured behaviour, against the real engine and a loopback fixture.</summary>
internal static partial class ManageTests
{
    private sealed class PowerLog : IPowerActions
    {
        public List<(CompletionActionKind Kind, bool Force)> Calls { get; } = new();
        public void Execute(CompletionActionKind kind, bool force) => Calls.Add((kind, force));
    }
    private sealed class Countdown : ICountdownPrompt
    {
        public int Shown; public CountdownOutcome Result = CountdownOutcome.Elapsed;
        public CountdownOutcome Show(CompletionActionKind kind, int seconds) { Shown++; return Result; }
    }
    private static DownloadJob SlowJob(DownloadManager m, string url, long kib)
    {
        var job = Sync(m.AddScheduledAsync(new DownloadRequest(url), DateTimeOffset.UtcNow.AddHours(1)));
        m.SetSpeedLimit(job.Id, kib * 1024); m.Schedule(job.Id, DateTimeOffset.UtcNow.AddSeconds(-1));
        return job;
    }
    // Clipboard managers (e.g. Ditto) can hold the clipboard open for a moment; retry instead of failing the suite.
    private static string ClipText()
    {
        for (int i = 0; ; i++)
        {
            try { return Clipboard.GetText(); }
            catch (System.Runtime.InteropServices.COMException) when (i < 40) { Pump(100); }
        }
    }
    private static DownloadJob Snap(DownloadManager m, string id) => m.Jobs.Single(j => j.Id == id);
    private static void Set(CheckBox box, bool on) => box.IsChecked = on;

    private static void ProgressWindowTests(string root, FixtureServer server, DialogScript script)
    {
        var m = Manager(root, "progress");
        var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "progress", "files"), ShowCompletionDialog = false, ShowProgressOnCompletion = false };
        var power = new PowerLog(); var countdown = new Countdown();
        string storePath = Path.Combine(root, "progress", "job-completion.json");
        var window = new MainWindow(m, settings, () => { }, enableTray: false, power) { ShowInTaskbar = false };
        window.FinishActions.Countdown = countdown;
        window.JobCompletion = new JobCompletionStore(storePath);
        window.Show(); Pump();
        try
        {
            var a = SlowJob(m, server.BaseUrl + "/range?pw-a", 256);

            // setting off => no window; setting on => exactly one window per job
            settings.ShowProgressForCapturedDownloads = false;
            Check(window.ShowCapturedProgress(a.Id) is null && window.ProgressWindowCount == 0, "Captured-progress setting off: no window opens");
            Check(new AppSettings().ShowProgressForCapturedDownloads, "Captured-progress setting defaults to on");
            settings.ShowProgressForCapturedDownloads = true;
            var pw = window.ShowCapturedProgress(a.Id)!;
            Check(pw is not null && window.HasProgressWindow(a.Id) && pw.IsVisible && pw.ShowInTaskbar, "Captured download opens a visible progress window with a taskbar button");
            Check(ReferenceEquals(window.ShowCapturedProgress(a.Id), pw) && ReferenceEquals(window.OpenProgressWindow(a.Id, false), pw) && window.ProgressWindowCount == 1, "One progress window per job (re-open reuses it)");
            Pump(150);
            Check(!pw.Topmost && window.IsEnabled, "Window is modeless and not left Topmost");

            // tabs
            var tabs = Field<TabControl>(pw, "Tabs");
            Check(tabs.Items.Cast<TabItem>().Select(t => (string)t.Header).SequenceEqual(new[] { "Download status", "Speed Limiter", "Options on completion" }), "Three tabs: Download status / Speed Limiter / Options on completion");
            Check(pw.Title == Snap(m, a.Id).FileName, "Title is the file name");

            // live fields from a real transfer
            PumpUntil(() => Field<TextBlock>(pw, "RateText").Text.EndsWith("/sec") && Field<TextBlock>(pw, "RateText").Text != "0 Bytes/sec" && Field<TextBlock>(pw, "TimeLeftText").Text != "(unknown)" && pw.Row.ActiveConnectionCount > 0, "live rate/time left");
            Check(Field<TextBlock>(pw, "SizeText").Text.Contains(server.Payload.Length.ToString("N0")), "File size shows the real byte count");
            Check(Field<TextBlock>(pw, "DownloadedText").Text != "0 Bytes" && Field<TextBox>(pw, "StatusText").Text == "Downloading", "Downloaded + Status update live");
            Check(Field<TextBlock>(pw, "ResumeText").Text == "Yes" && Field<TextBox>(pw, "UrlText").Text.Contains("/range"), "Resume capability = Yes; URL line shown");
            var list = Field<ListView>(pw, "ConnectionList");
            var job = Snap(m, a.Id);
            Check(list.Items.Count == pw.Row.Segments.Count && list.Items.Count > 0, "Connection table has one row per segment");
            Check(list.Items.Cast<DownloadSegmentRow>().Any(r => r.Info == "Receiving data...") && list.Items.Cast<DownloadSegmentRow>().All(r => r.Number > 0), "Rows carry Info from live telemetry");
            Check(Field<Canvas>(pw, "SegmentBar").Children.Count > 0, "Segment bar is drawn from segment telemetry");
            var grid = (GridView)list.View;
            Check(grid.Columns.Select(c => (string)c.Header).SequenceEqual(new[] { "N.", "Downloaded", "Info" }), "Table columns N. / Downloaded / Info");

            // hide / show details
            var pane = Field<Grid>(pw, "DetailsPane"); var detailsButton = Field<Button>(pw, "DetailsButton");
            Click(pw, "DetailsButton");
            Check(pane.Visibility == Visibility.Collapsed && (string)detailsButton.Content == "Show details >>", "Hide details collapses the lower pane");
            Click(pw, "DetailsButton");
            Check(pane.Visibility == Visibility.Visible && (string)detailsButton.Content == "<< Hide details", "Show details restores it");

            // speed limiter
            var check = Field<CheckBox>(pw, "LimitCheck"); var input = Field<TextBox>(pw, "LimitInput");
            Check(check.IsChecked == true && input.Text == "256" && Snap(m, a.Id).SpeedLimitBytesPerSecond == 256 * 1024, "Speed Limiter shows the job's real limit");
            input.Text = "abc"; Click(pw, "ApplyLimitButton");
            Check(Snap(m, a.Id).SpeedLimitBytesPerSecond == 256 * 1024 && Field<TextBox>(pw, "LimitMessage").Text.Contains("KiB/s"), "Invalid limit is rejected, engine untouched");
            input.Text = "128"; Click(pw, "ApplyLimitButton");
            Check(Snap(m, a.Id).SpeedLimitBytesPerSecond == 128 * 1024, "Apply sets the per-job limit through the engine: " + Snap(m, a.Id).SpeedLimitBytesPerSecond + " / " + Field<TextBox>(pw, "LimitMessage").Text);
            check.IsChecked = false; Check(!input.IsEnabled, "Unticking disables the KiB/s box"); Click(pw, "ApplyLimitButton");
            Check(Snap(m, a.Id).SpeedLimitBytesPerSecond is null, "Unticked + Apply removes the limit");
            check.IsChecked = true; input.Text = "512"; Click(pw, "ApplyLimitButton");

            // pause / resume really act
            Click(pw, "PauseButton"); Pump(500);
            Check(Snap(m, a.Id).Status == "Paused" && (string)Field<Button>(pw, "PauseButton").Content == "Resume", "Pause stops the transfer and the button becomes Resume");
            long frozen = Snap(m, a.Id).DownloadedBytes; Pump(700);
            Check(Snap(m, a.Id).DownloadedBytes == frozen, "No bytes arrive while paused");
            Click(pw, "PauseButton");
            PumpUntil(() => Snap(m, a.Id).DownloadedBytes > frozen, "bytes after resume");
            Check(Snap(m, a.Id).Status == "Downloading", "Resume continues the same transfer");

            // options on completion: enable rules
            var show = Field<CheckBox>(pw, "ShowDialogBox"); var exit = Field<CheckBox>(pw, "ExitAppBox"); var off = Field<CheckBox>(pw, "TurnOffBox");
            var kind = Field<ComboBox>(pw, "TurnOffKind"); var force = Field<CheckBox>(pw, "ForceBox");
            Check(Field<TextBox>(pw, "SaveToText").Text == job.FilePath && Field<TextBox>(pw, "SaveToText").IsReadOnly, "Save To shows the read-only full path");
            Check(!show.IsChecked!.Value && exit.IsEnabled && off.IsEnabled && !kind.IsEnabled && !force.IsEnabled, "Dialog off: Exit/Turn off enabled; dropdown + Force wait for Turn off");
            Check(kind.Items.Cast<ComboBoxItem>().Select(i => (string)i.Content).SequenceEqual(new[] { "Shut down", "Restart", "Sleep", "Hibernate", "Lock" }), "Dropdown lists Shut down / Restart / Sleep / Hibernate / Lock");
            Set(off, true);
            Check(kind.IsEnabled && force.IsEnabled, "Turn off ticked: dropdown enabled, Force enabled for Shut down");
            kind.SelectedIndex = 2; Check(!force.IsEnabled, "Sleep: Force disabled");
            kind.SelectedIndex = 1; Check(force.IsEnabled, "Restart: Force enabled");
            Set(force, true);
            Set(show, true);
            Check(!exit.IsEnabled && !off.IsEnabled && !kind.IsEnabled && !force.IsEnabled, "Show-dialog ticked: every action option is disabled");
            Set(show, false);
            Check(exit.IsEnabled && off.IsEnabled && kind.IsEnabled && force.IsEnabled, "Show-dialog cleared: options usable again");
            Check(!Field<TextBlock>(pw, "OptionsNote").Text.Contains("Hang"), "No obsolete modem option");
            Click(pw, "HideTabButton");
            Check(tabs.SelectedIndex == -1, "Hide tab collapses the tab content");
            tabs.SelectedIndex = 0;
            Check(new JobCompletionStore(storePath).Get(a.Id) is null, "Per-job options are memory-only until Remember is ticked");
            Set(Field<CheckBox>(pw, "RememberBox"), true);
            var saved = new JobCompletionStore(storePath).Get(a.Id);
            Check(saved is { ShowDialog: false, TurnOff: true, PowerAction: CompletionActionKind.Restart, Force: true, ExitApp: false }, "Remembered per-job options are persisted to app state and reload");
            kind.SelectedIndex = 3; Check(!force.IsChecked!.Value && new JobCompletionStore(storePath).Get(a.Id)!.Force == false, "Force is cleared when the action cannot take it");
            kind.SelectedIndex = 1; Set(force, true);

            // a second job: removal keeps its options only until it is gone; unrelated job completion must not fire A's action
            // speed up A and let it finish; the window must stay and show Complete
            input.Text = "8192"; Click(pw, "ApplyLimitButton");
            PumpUntil(() => Snap(m, a.Id).Status == "Completed", "A completes", 40);
            PumpUntil(() => Field<TextBox>(pw, "StatusText").Text == "Complete", "window shows Complete");
            Check(window.HasProgressWindow(a.Id) && pw.IsVisible, "Progress window persists after completion (no auto-close)");
            Check(Field<Button>(pw, "OpenButton").Visibility == Visibility.Visible && Field<Button>(pw, "FolderButton").Visibility == Visibility.Visible && Field<Button>(pw, "CloseButton").Visibility == Visibility.Visible
                && Field<Button>(pw, "PauseButton").Visibility == Visibility.Collapsed && Field<Button>(pw, "CancelButton").Visibility == Visibility.Collapsed, "Complete state shows Open / Open folder / Close only");
            Pump(1200);
            Check(window.HasProgressWindow(a.Id) && !System.Windows.Application.Current.Windows.OfType<DownloadCompleteWindow>().Any(w => w.IsVisible), "Still open after more time; no second completion dialog for a job with a progress window");
            Check(power.Calls.SequenceEqual(new[] { (CompletionActionKind.Restart, true) }) && countdown.Shown == 1, "Per-job Restart(force) fired once for A with the countdown first");
            settings.ShowCompletionDialog = true;
            var b = SlowJob(m, server.BaseUrl + "/range?pw-b", 4096);
            PumpUntil(() => Snap(m, b.Id).Status == "Completed", "B completes", 40); Pump(700);
            Check(power.Calls.Count == 1 && countdown.Shown == 1, "Completion of another job does not trigger A's action");
            Check(System.Windows.Application.Current.Windows.OfType<DownloadCompleteWindow>().Count() == 1, "A job without a progress window still gets the normal completion dialog");
            foreach (var w in System.Windows.Application.Current.Windows.OfType<DownloadCompleteWindow>().ToArray()) w.Close();
            Pump(300);
            var e = SlowJob(m, server.BaseUrl + "/range?pw-e", 4096); var pe = window.OpenProgressWindow(e.Id, false)!;
            PumpUntil(() => Snap(m, e.Id).Status == "Completed", "E completes", 40); Pump(800);
            Check(window.HasProgressWindow(e.Id) && Field<TextBox>(pe, "StatusText").Text == "Complete" && !System.Windows.Application.Current.Windows.OfType<DownloadCompleteWindow>().Any(), "Open progress window updates to Complete instead of popping a second dialog");
            pe.Close(); Pump();

            // already-completed jobs never fire (restart over the same state)
            var again = new MainWindow(m, settings, () => { }, enableTray: false, power) { ShowInTaskbar = false };
            {
                again.JobCompletion = new JobCompletionStore(storePath); again.Show(); Pump(1000);
                Check(power.Calls.Count == 1, "Reopening the app never re-runs the action of an already-completed job");
                again.Close();
            }
            // user closes the finished window
            Click(pw, "CloseButton"); Pump();
            Check(!window.HasProgressWindow(a.Id) && window.ProgressWindowCount == 0, "Close button closes the window and frees the one-per-job slot");

            // runner rules: force only for Shut down / Restart; cancel suppresses; exit needs no countdown
            power.Calls.Clear(); countdown.Shown = 0;
            foreach (var k in new[] { CompletionActionKind.Shutdown, CompletionActionKind.Restart, CompletionActionKind.Sleep, CompletionActionKind.Hibernate, CompletionActionKind.Lock })
                window.FinishActions.RunFor(k, true);
            Check(power.Calls.SequenceEqual(new[] { (CompletionActionKind.Shutdown, true), (CompletionActionKind.Restart, true), (CompletionActionKind.Sleep, false), (CompletionActionKind.Hibernate, false), (CompletionActionKind.Lock, false) }), "Force reaches only Shut down and Restart");
            countdown.Result = CountdownOutcome.Cancelled; power.Calls.Clear();
            Check(!window.FinishActions.RunFor(CompletionActionKind.Shutdown, false) && power.Calls.Count == 0, "Cancelling the countdown aborts the per-job power action");
            bool exited = false; int before = countdown.Shown;
            var runner = new CompletionActionRunner(settings, power, countdown, () => { }, () => { }, () => { }, () => exited = true);
            Check(runner.RunFor(CompletionActionKind.ExitApp, false) && exited && countdown.Shown == before, "Exit NeoDM runs without a power countdown");
            countdown.Result = CountdownOutcome.Elapsed;
            var rules = new JobCompletionOptions { ShowDialog = true, TurnOff = true, ExitApp = true };
            Check(!rules.HasAction && !rules.ActionsEnabled && !rules.PowerKindEnabled, "Options model: dialog on => no action and nothing enabled");

            // cancel: decline, keep, delete; closing with X never cancels
            var c = SlowJob(m, server.BaseUrl + "/range?pw-c", 64); var d = SlowJob(m, server.BaseUrl + "/range?pw-d", 64);
            var pc = window.OpenProgressWindow(c.Id, false)!; var pd = window.OpenProgressWindow(d.Id, false)!;
            Check(window.ProgressWindowCount == 2 && !ReferenceEquals(pc, pd), "Separate windows for separate jobs");
            PumpUntil(() => Snap(m, c.Id).DownloadedBytes > 0 && Snap(m, d.Id).DownloadedBytes > 0, "bytes for c/d");
            script.ClickRemove = false; script.SetDelete = null; int shown = script.Shown;
            Click(pc, "CancelButton"); Pump(400);
            Check(script.Shown == shown + 1 && m.Jobs.Any(j => j.Id == c.Id) && window.HasProgressWindow(c.Id), "Cancel asks to confirm; declining keeps the download and the window");
            pc.Close(); Pump(200);
            Check(m.Jobs.Any(j => j.Id == c.Id && j.Status is "Downloading" or "Connecting" or "Queued") && !window.HasProgressWindow(c.Id), "Closing with X hides the window but never cancels the transfer");
            pc = window.OpenProgressWindow(c.Id, false)!;
            script.ClickRemove = true; script.SetDelete = false;
            Click(pc, "CancelButton"); PumpUntil(() => !m.Jobs.Any(j => j.Id == c.Id), "c removed");
            Check(script.SawDefaultUnchecked && !m.Jobs.Any(j => j.Id == c.Id) && !File.Exists(c.FilePath), "Cancel + keep: job removed through the shared remove flow");
            PumpUntil(() => !window.HasProgressWindow(c.Id), "window closes after cancel");
            script.SetDelete = true;
            Click(pd, "CancelButton"); PumpUntil(() => !m.Jobs.Any(j => j.Id == d.Id), "d removed");
            Check(script.SawWarningVisible && script.ButtonText == "_Remove and delete" && !File.Exists(d.FilePath) && window.JobCompletion.Get(d.Id) is null, "Cancel + delete: removed, file gone, per-job options dropped");
            script.SetDelete = null;
        }
        finally { foreach (var j in m.Jobs.Where(j => j.Status != "Completed")) { try { m.Pause(j.Id); } catch { } } Pump(800); window.Close(); m.Dispose(); } // let the list drop removed/paused rows first: a busy row would raise the modal quit prompt
    }
}
