using System.IO;
using System.Windows;
using System.Windows.Controls;
using NDM.App;
using NDM.Core;
using NDM.Tests;

/// <summary>1.3.1: "Show this window when the download finishes" and "Change save location..." in the progress window.</summary>
internal static partial class ManageTests
{
    // Added for a later start so a window can be opened (and closed again) before the transfer begins.
    private static DownloadJob Held(DownloadManager m, string url, long kib)
    {
        var job = Sync(m.AddScheduledAsync(new DownloadRequest(url), DateTimeOffset.UtcNow.AddHours(1)));
        m.SetSpeedLimit(job.Id, kib * 1024); return job;
    }
    private static void Go(DownloadManager m, string id) => m.Schedule(id, DateTimeOffset.UtcNow.AddSeconds(-1));
    private static void Finish(DownloadManager m, string id) { m.SetSpeedLimit(id, null); PumpUntil(() => Snap(m, id).Status == "Completed", "job completes", 40); }
    private static int Dialogs(Window w) => System.Windows.Application.Current.Windows.OfType<DownloadCompleteWindow>().Count(d => d.IsVisible);

    private static void CompletionWindowTests(string root, FixtureServer server)
    {
        var m = Manager(root, "finishwin"); var priorNative = WindowActivation.Native; WindowActivation.Native = new CaptureNative(); // no real foreground calls, and a full-screen app on the dev machine cannot skip the pop-up
        var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "finishwin", "files"), ShowCompletionDialog = false, ShowProgressOnCompletion = true };
        string storePath = Path.Combine(root, "finishwin", "job-completion.json");
        var window = new MainWindow(m, settings, () => { }, enableTray: false, new PowerLog()) { ShowInTaskbar = false };
        window.PlayCompletionSound = () => { }; window.JobCompletion = new JobCompletionStore(storePath);
        window.Show(); Pump();
        try
        {
            // 1. window closed => re-opens once, in the Complete state, same single window per job
            var a = Held(m, server.BaseUrl + "/range?fw-a", 1024);
            var first = window.OpenProgressWindow(a.Id, false)!; Pump(100);
            var showBox = Field<CheckBox>(first, "ShowWindowBox");
            Check(showBox.IsChecked == true && showBox.IsEnabled && (string)showBox.Content == "Show this window when the download finishes", "Options tab has 'Show this window when the download finishes', ticked by default (global setting)");
            Check(window.JobCompletion.Get(a.Id) is null, "Merely opening the tab stores nothing for the 'show window' choice");
            first.Close(); Pump(100);
            Check(!window.HasProgressWindow(a.Id), "Test setup: the window is closed before the download finishes");
            Go(m, a.Id);
            PumpUntil(() => Snap(m, a.Id).Status == "Completed", "A completes", 40);
            PumpUntil(() => window.HasProgressWindow(a.Id), "window re-opens on completion");
            var reopened = window.OpenProgressWindow(a.Id, false)!; Pump(300);
            Check(!ReferenceEquals(reopened, first) && window.ProgressWindowCount == 1 && reopened.IsVisible, "Closed window re-opens as the job's single window");
            Check(Field<TextBlock>(reopened, "StatusText").Text == "Complete" && Field<Button>(reopened, "OpenButton").Visibility == Visibility.Visible && Field<Button>(reopened, "FolderButton").Visibility == Visibility.Visible && Field<Button>(reopened, "CloseButton").Visibility == Visibility.Visible
                && Field<Button>(reopened, "PauseButton").Visibility == Visibility.Collapsed && Field<Button>(reopened, "CancelButton").Visibility == Visibility.Collapsed, "Re-opened window is in the Complete state with Open / Open folder / Close");
            Check(!Field<CheckBox>(reopened, "ShowWindowBox").IsEnabled, "Completion options (including 'show this window') are disabled once the download is complete");
            Pump(900);
            Check(window.ProgressWindowCount == 1 && Dialogs(window) == 0, "Exactly one completion window: no second progress window and no complete dialog");
            Click(reopened, "CloseButton"); Pump(300);
            Check(window.ProgressWindowCount == 0, "Closing it does not bring it back");

            // 2. minimised window is restored, the same instance
            var b = Held(m, server.BaseUrl + "/range?fw-b", 1024);
            var pb = window.OpenProgressWindow(b.Id, false)!; Pump(100);
            pb.WindowState = WindowState.Minimized; Pump(100); Go(m, b.Id);
            Finish(m, b.Id); Pump(500);
            Check(ReferenceEquals(window.OpenProgressWindow(b.Id, false), pb) && pb.WindowState != WindowState.Minimized && Field<TextBlock>(pb, "StatusText").Text == "Complete" && window.ProgressWindowCount == 1 && Dialogs(window) == 0, "A minimised window is restored and shows Complete (same window, no dialog)");
            pb.Close(); Pump(100);

            // 3. already open and visible: just shows completion, no extra window
            var c = Held(m, server.BaseUrl + "/range?fw-c", 1024);
            var pc = window.OpenProgressWindow(c.Id, false)!; Pump(100); Go(m, c.Id);
            Finish(m, c.Id); Pump(500);
            Check(ReferenceEquals(window.OpenProgressWindow(c.Id, false), pc) && Field<TextBlock>(pc, "StatusText").Text == "Complete" && window.ProgressWindowCount == 1 && Dialogs(window) == 0, "An open window simply shows Complete");
            pc.Close(); Pump(100);

            // 4. global off, dialog off => nothing; global off, dialog on => the existing dialog (exactly one window)
            settings.ShowProgressOnCompletion = false;
            var d = Held(m, server.BaseUrl + "/range?fw-d", 1024); Go(m, d.Id); Finish(m, d.Id); Pump(700);
            Check(window.ProgressWindowCount == 0 && Dialogs(window) == 0, "Option off (global): no window and no dialog when the dialog is off");
            settings.ShowCompletionDialog = true;
            var d2 = Held(m, server.BaseUrl + "/range?fw-d2", 1024); Go(m, d2.Id); Finish(m, d2.Id); Pump(700);
            Check(window.ProgressWindowCount == 0 && Dialogs(window) == 1, "Option off, dialog on: the existing complete dialog appears (one window)");
            foreach (var w in System.Windows.Application.Current.Windows.OfType<DownloadCompleteWindow>().ToArray()) w.Close(); Pump(300);

            // 5. global on and dialog on => ONE window: the progress window replaces the dialog
            settings.ShowProgressOnCompletion = true;
            var e = Held(m, server.BaseUrl + "/range?fw-e", 1024); Go(m, e.Id); Finish(m, e.Id); Pump(900);
            Check(window.HasProgressWindow(e.Id) && window.ProgressWindowCount == 1 && Dialogs(window) == 0, "Option on + dialog on: the download window opens and replaces the separate dialog (exactly one window)");
            window.OpenProgressWindow(e.Id, false)!.Close(); Pump(200);

            // 6. per-download choice beats the global one, both ways
            settings.ShowCompletionDialog = false;
            var f = Held(m, server.BaseUrl + "/range?fw-f", 1024);
            window.JobCompletion.Set(f.Id, new JobCompletionOptions { ShowDialog = false, ShowWindow = false });
            Go(m, f.Id); Finish(m, f.Id); Pump(700);
            Check(window.ProgressWindowCount == 0 && Dialogs(window) == 0, "Per-download off overrides the global 'on': no window");
            settings.ShowProgressOnCompletion = false;
            var g = Held(m, server.BaseUrl + "/range?fw-g", 1024);
            window.JobCompletion.Set(g.Id, new JobCompletionOptions { ShowDialog = false, ShowWindow = true });
            Go(m, g.Id); Finish(m, g.Id); Pump(900);
            Check(window.HasProgressWindow(g.Id) && Field<TextBlock>(window.OpenProgressWindow(g.Id, false)!, "StatusText").Text == "Complete", "Per-download on overrides the global 'off': the window opens");
            window.OpenProgressWindow(g.Id, false)!.Close(); Pump(200);
            settings.ShowProgressOnCompletion = true;

            // 7. the checkbox stores the choice, persists with Remember, and the other greying rules stay as they were
            var h = Held(m, server.BaseUrl + "/range?fw-h", 256);
            var ph = window.OpenProgressWindow(h.Id, false)!; Pump(100);
            var box = Field<CheckBox>(ph, "ShowWindowBox"); var dialogBox = Field<CheckBox>(ph, "ShowDialogBox"); var exit = Field<CheckBox>(ph, "ExitAppBox");
            box.IsChecked = false;
            Check(window.JobCompletion.Get(h.Id) is { ShowWindow: false }, "Unticking stores this download's own choice (off)");
            Set(Field<CheckBox>(ph, "RememberBox"), true);
            Check(new JobCompletionStore(storePath).Get(h.Id) is { ShowWindow: false }, "The per-download choice persists in the job-completion store with 'Remember'");
            Set(dialogBox, true);
            Check(box.IsEnabled && !exit.IsEnabled, "'Show download complete dialog' still greys the action options but not 'show this window'");
            Set(dialogBox, false); Check(exit.IsEnabled, "...and clearing it frees them again");
            ph.Close(); Pump(100);
            var ph2 = window.OpenProgressWindow(h.Id, false)!; Pump(100);
            Check(Field<CheckBox>(ph2, "ShowWindowBox").IsChecked == false, "Re-opening the window shows the stored choice");
            ph2.Close(); Pump(100);
            m.Remove(h.Id); Pump(300);
        }
        finally { foreach (var j in m.Jobs.Where(j => j.Status != "Completed")) { try { m.Pause(j.Id); } catch { } } Pump(500); foreach (var w in window.OwnedWindows.OfType<Window>().ToArray()) w.Close(); foreach (var w in System.Windows.Application.Current.Windows.OfType<DownloadCompleteWindow>().ToArray()) w.Close(); window.Close(); WindowActivation.Native = priorNative; }

        // 8. a job already completed when the app starts never pops a window
        var startedDone = Sync(Done(m, server.BaseUrl + "/empty", "already-done.bin"));
        var again = new MainWindow(m, new AppSettings { DownloadDirectory = Path.Combine(root, "finishwin", "files"), ShowProgressOnCompletion = true }, () => { }, enableTray: false, new PowerLog()) { ShowInTaskbar = false };
        again.PlayCompletionSound = () => { };
        try { again.Show(); Pump(1000); Check(again.ProgressWindowCount == 0 && Dialogs(again) == 0, "Jobs already completed at app start open no window"); }
        finally { again.Close(); m.Dispose(); }

        // 9. Options > Downloads has the global checkbox, bound and saved
        var state = Path.Combine(root, "finishwin-options"); Directory.CreateDirectory(state);
        var s2 = new AppSettings { DownloadDirectory = Path.Combine(state, "files") };
        var dlg = new SettingsWindow(s2, state); dlg.Show(); Pump(100);
        var global = Field<CheckBox>(dlg, "ProgressOnCompletion");
        Check(global.IsChecked == true && ((string)global.Content).Replace("_", "") == "Open the download window when a download finishes", "Options > Downloads: 'Open the download window when a download finishes', ticked by default");
        global.IsChecked = false; Call(dlg, "Save_Click"); Pump(100);
        Check(!s2.ShowProgressOnCompletion && !AppSettings.Load(state).ShowProgressOnCompletion, "The global checkbox is saved to settings.json");
    }

    private static void ChangeLocationTests(string root, FixtureServer server)
    {
        var m = Manager(root, "chgloc");
        var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "chgloc", "files"), ShowCompletionDialog = false, ShowProgressOnCompletion = false };
        var window = new MainWindow(m, settings, () => { }, enableTray: false, new PowerLog()) { ShowInTaskbar = false };
        window.PlayCompletionSound = () => { }; window.Show(); Pump();
        string expected = FixtureServer.Hash(server.Payload);
        string DirOf(string n) => Path.Combine(root, "chgloc", n);
        try
        {
            // running download: pick a folder in the window; it keeps transferring and completes there, byte-identical
            var a = Held(m, server.BaseUrl + "/range?cl-a", 700);
            var pw = window.OpenProgressWindow(a.Id, false)!; Pump(100); Go(m, a.Id);
            PumpUntil(() => Snap(m, a.Id).DownloadedBytes >= 65536, "bytes before the move");
            var button = Field<Button>(pw, "ChangeLocationButton");
            Check(button.IsEnabled && ((string)button.Content).StartsWith("Change save location"), "'Change save location...' button sits next to the read-only Save To path");
            string? offered = null; pw.PickFolder = (_, initial) => { offered = initial; return DirOf("running-target"); };
            Click(pw, "ChangeLocationButton"); Pump(300);
            var moved = Path.Combine(DirOf("running-target"), Snap(m, a.Id).FileName);
            Check(offered == Path.GetDirectoryName(a.FilePath) && Snap(m, a.Id).FilePath == moved && Field<TextBox>(pw, "SaveToText").Text == moved, "The picker starts in the current folder; Save To and the job show the new path");
            Check(Field<TextBlock>(pw, "LocationMessage").Visibility == Visibility.Visible && Field<TextBlock>(pw, "LocationMessage").Text.Contains("Save location changed") && window.Rows.Single(r => r.Id == a.Id).FilePath == moved, "The window confirms the change and the main list row follows");
            Check(Snap(m, a.Id).Status != "Completed" && new JobCompletionStore().Get(a.Id) is null, "The download was not restarted or completed by the move");
            Finish(m, a.Id);
            Check(File.Exists(moved) && FixtureServer.Hash(File.ReadAllBytes(moved)) == expected && !File.Exists(a.FilePath), "It completes in the new folder, byte-identical SHA-256, nothing in the old one");

            // cancelled picker and rejected folders leave everything alone
            pw.PickFolder = (_, _) => null; Click(pw, "ChangeLocationButton"); Pump(100);
            Check(Snap(m, a.Id).FilePath == moved, "Cancelling the folder picker changes nothing");
            pw.PickFolder = (_, _) => "not-a-full-path"; Click(pw, "ChangeLocationButton"); Pump(100);
            Check(Snap(m, a.Id).FilePath == moved && Field<TextBlock>(pw, "LocationMessage").Text.StartsWith("The save location was not changed") && File.Exists(moved), "A bad folder shows a clear error in the window and leaves the job unchanged");
            pw.PickFolder = (_, _) => Path.GetDirectoryName(moved); Click(pw, "ChangeLocationButton"); Pump(100);
            Check(Snap(m, a.Id).FilePath == moved && Field<TextBlock>(pw, "LocationMessage").Text.Contains("already saved in that folder"), "Choosing the current folder is a harmless no-op");
            // completed download: only the finished file moves
            pw.PickFolder = (_, _) => DirOf("done-target"); Click(pw, "ChangeLocationButton"); Pump(300);
            var moved2 = Path.Combine(DirOf("done-target"), Snap(m, a.Id).FileName);
            Check(File.Exists(moved2) && !File.Exists(moved) && Field<TextBox>(pw, "SaveToText").Text == moved2 && Snap(m, a.Id).FilePath == moved2 && FixtureServer.Hash(File.ReadAllBytes(moved2)) == expected, "Completed download: the finished file moves to the chosen folder and Save To follows");
            Check(Field<TextBlock>(pw, "StatusText").Text == "Complete", "The window stays in the Complete state after moving the finished file");
            var props = new PropertiesWindow(window.Rows.Single(r => r.Id == a.Id)); props.Close();
            pw.Close(); Pump(100);
        }
        finally { foreach (var j in m.Jobs.Where(j => j.Status != "Completed")) { try { m.Pause(j.Id); } catch { } } Pump(500); window.Close(); m.Dispose(); }
    }
}
