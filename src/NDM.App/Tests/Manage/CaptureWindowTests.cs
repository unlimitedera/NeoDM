using System.IO;
using System.Windows;
using System.Windows.Controls;
using NDM.App;
using NDM.Core;
using NDM.Tests;

/// <summary>Capture dialog as an independent window, tray list of pending windows, and the dialog's name detection - against the real engine and loopback fixture.</summary>
internal static partial class ManageTests
{
    private sealed class CaptureNative : IWindowNative
    {
        public IntPtr Foreground = new(1); public bool Minimized;
        public List<string> Calls = new();
        public IntPtr ForegroundWindow() => Foreground;
        public uint ThreadOf(IntPtr window) => 7; public uint CurrentThread() => 7;
        public bool IsMinimized(IntPtr window) => Minimized;
        public void Restore(IntPtr window) => Calls.Add("restore");
        public bool AttachThreadInput(uint from, uint to, bool attach) => true;
        public bool SetForeground(IntPtr window) { Calls.Add("setfg"); Foreground = window; return true; }
        public void BringToTop(IntPtr window) => Calls.Add("top");
        public void TapAlt() { }
        public void Flash(IntPtr window) => Calls.Add("flash");
    }

    private static DownloadOptionsDialog Capture(DownloadManager m, string root, string url, string? browserName = null, string? mime = null, Func<CancellationToken, Task<NameProbeResult?>>? probe = null) =>
        new(url, browserName, "", "", Path.Combine(root, "capture", "dl"), mime, probe ?? (ct => m.ProbeNameAsync(new DownloadRequest(url), ct)));

    private static void CaptureWindowTests(string root, FixtureServer server)
    {
        var m = Manager(root, "capture");
        var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "capture", "files"), ShowCompletionDialog = false, ShowProgressOnCompletion = false };
        var main = new MainWindow(m, settings, () => { }, enableTray: false) { ShowInTaskbar = false };
        int mainActivated = 0, mainVisibleChanges = 0;
        main.Activated += (_, _) => mainActivated++;
        main.IsVisibleChanged += (_, _) => mainVisibleChanges++;
        var opened = new List<Window>();
        try
        {
            // ---- main window never started (closed to tray / --tray): the dialog still appears, alone
            var list = main.TrayWindows;
            var dialog = Capture(m, root, server.BaseUrl + "/n/cd-plain");
            opened.Add(dialog);
            var decision = CaptureFlow.ShowAsync(dialog, list, bringToFront: false);
            Pump(150);
            Check(dialog.IsVisible && dialog.Owner is null && dialog.ShowInTaskbar, "Capture dialog: visible top-level window, Owner null, own taskbar button");
            Check(dialog.Icon is not null, "Capture dialog: carries the NeoDM icon");
            Check(!main.IsVisible && mainActivated == 0 && mainVisibleChanges == 0, "Capture dialog: a hidden / closed-to-tray main window is not shown or activated");
            PumpUntil(() => dialog.ProbeTask.IsCompleted, "capture probe");
            Check(Field<TextBox>(dialog, "NameInput").Text == "report final.pdf" && dialog.Title == "report final.pdf", "Dialog pre-fill: probe replaced the address-derived name with the Content-Disposition name; title is the file name");
            Check(Field<TextBox>(dialog, "DetectedInfo").Text.Contains("Size:") && Field<TextBox>(dialog, "DetectedInfo").Text.Contains("Type: application/octet-stream"), "Dialog shows detected size and type");
            Check(dialog.Detected?.FinalUrl.EndsWith("/n/cd-plain") == true && server.NamedRequests.Any(r => r.Method == "HEAD" && r.Target == "/n/cd-plain"), "Dialog probe used HEAD");
            Call(dialog, "Start_Click");
            Check(decision.Wait(3000) && decision.Result && dialog.Request is { FileName: "report final.pdf", AutoName: false } && list.Count == 0, "Start: accepted with the detected name fixed; dialog leaves the tray list");
            Check(!main.IsVisible && mainActivated == 0, "Capture dialog closing never shows the main window");

            // ---- minimised main window stays minimised
            main.Show(); main.WindowState = WindowState.Minimized; Pump(200);
            int activatedBefore = mainActivated;
            dialog = Capture(m, root, server.BaseUrl + "/n/nozip"); opened.Add(dialog);
            decision = CaptureFlow.ShowAsync(dialog, list, bringToFront: false); Pump(150);
            Check(main.WindowState == WindowState.Minimized && main.IsVisible && mainActivated == activatedBefore && dialog.IsVisible && dialog.Owner is null, "Minimised main window stays minimised and inactive while the capture dialog appears");
            PumpUntil(() => dialog.ProbeTask.IsCompleted, "second probe");
            Check(Field<TextBox>(dialog, "NameInput").Text == "nozip.zip" && Field<ComboBox>(dialog, "CategoryDropdown").SelectedItem is ComboBoxItem { Tag: "Compressed" }, "No-extension link + application/zip -> nozip.zip, category follows the final extension");
            dialog.Close(); Check(decision.Wait(3000) && !decision.Result, "Closing the dialog window counts as Cancel");
            Check(main.WindowState == WindowState.Minimized, "Main window still minimised after the dialog closed");
            main.Hide();

            // ---- two simultaneous dialogs: independent, no modal stacking, listed in the tray menu
            var d1 = Capture(m, root, server.BaseUrl + "/n/cd-plain"); var d2 = Capture(m, root, server.BaseUrl + "/n/video", "clip"); opened.Add(d1); opened.Add(d2);
            var t1 = CaptureFlow.ShowAsync(d1, list, false); var t2 = CaptureFlow.ShowAsync(d2, list, false); Pump(200);
            Check(d1.IsVisible && d2.IsVisible && d1.IsEnabled && d2.IsEnabled && d1.Owner is null && d2.Owner is null && d1.ShowInTaskbar && d2.ShowInTaskbar && !t1.IsCompleted && !t2.IsCompleted, "Two simultaneous captures: two independent, enabled dialogs (no modal on modal), each with its own taskbar button");
            PumpUntil(() => d1.ProbeTask.IsCompleted && d2.ProbeTask.IsCompleted, "both probes");
            Check(Field<TextBox>(d2, "NameInput").Text == "clip.mp4", "Browser name without extension gets the type's extension (clip -> clip.mp4)");
            var model = list.Model();
            Check(list.Count == 2 && model.Count == 3 && model[0].Header == "\u2022 Download options: report final.pdf" && model[1].Header == "\u2022 Download options: clip.mp4" && model[2].IsSeparator, "Tray model lists every pending dialog in order, then a separator");

            // ---- restoring from the tray list activates the right window
            var native = new CaptureNative(); list.Activate = w => WindowActivation.BringToFront(w, native, dropTopmost: false);
            d2.WindowState = WindowState.Minimized; Pump(100);
            Check(d2.WindowState == WindowState.Minimized && !main.IsVisible, "A minimised capture dialog sits in the taskbar; the main window is still hidden");
            native.Calls.Clear(); model[1].Activate!(); Pump(100);
            Check(d2.WindowState == WindowState.Normal && native.Calls.Contains("setfg") && !main.IsVisible, "Tray entry restores and activates exactly that dialog (main window untouched)");
            d2.Topmost = false;
            var activated = new List<Window>(); list.Activate = activated.Add;
            Check(list.RestorePendingDialog() && ReferenceEquals(activated.Single(), d1), "Left-click / double-click restores the oldest pending dialog first");

            // ---- the WPF menu section: filled on open, replaced (never duplicated), hidden when empty
            var menu = new ContextMenu(); menu.Items.Add(new MenuItem { Header = "Show" }); menu.Items.Add(new MenuItem { Header = "Exit" });
            list.Fill(menu, 1); list.Fill(menu, 1);
            Check(menu.Items.Count == 5 && menu.Items[0] is MenuItem { Header: "Show" } && menu.Items[1] is MenuItem { Header: "\u2022 Download options: report final.pdf" } && menu.Items[3] is Separator && menu.Items[4] is MenuItem { Header: "Exit" }, "Menu section sits between Show and Exit with a separator, no duplicates after a refresh");
            activated.Clear(); ((MenuItem)menu.Items[2]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(ReferenceEquals(activated.Single(), d2), "Clicking a menu entry activates its window");

            // ---- progress window: independent, listed with its percentage
            var job = SlowJob(m, server.BaseUrl + "/range?capture-progress", 256);
            int before = mainActivated;
            var pw = main.OpenProgressWindow(job.Id, foreground: false)!; Pump(300);
            Check(pw.Owner is null && pw.ShowInTaskbar && pw.IsVisible && !main.IsVisible && mainActivated == before, "Progress window: Owner null, own taskbar button, main window not pulled forward");
            PumpUntil(() => pw.Row.TotalBytes > 0 || pw.Row.IsComplete, "size known");
            Check(MainWindow.TrayDetail("a.bin", "—") == "a.bin" && MainWindow.TrayDetail("a.bin", "42%") == "a.bin (42%)", "Tray detail omits the parentheses while the percent is unknown");
            var progressLine = list.Model().First(e => e.Kind == TrayWindowKind.Progress);
            Check(progressLine.Header!.StartsWith("\u2022 Downloading: " + pw.Row.FileName + " (") && progressLine.Header.EndsWith("%)") && list.Model().Count == 4, "Progress window listed as \u2022 Downloading: name (NN%) after the dialogs");
            pw.Close(); m.Remove(job.Id);
            Check(list.Count == 2, "Closing a progress window removes it from the list");

            // ---- finishing the decisions: neither dialog's end shows the main window
            d1.Close(); d2.Close();
            Check(t1.Wait(3000) && t2.Wait(3000) && list.Count == 0 && list.Model().Count == 0 && !main.IsVisible, "Closing both dialogs: list empty, section hidden, main window still hidden");
            menu.Items.Clear(); menu.Items.Add(new MenuItem { Header = "Show" }); list.Fill(menu, 1);
            Check(menu.Items.Count == 1 && !list.RestorePendingDialog(), "Empty list adds nothing to the menu and restores nothing");

            // ---- name detection in the dialog
            var slow = new TaskCompletionSource<NameProbeResult?>();
            var typed = Capture(m, root, server.BaseUrl + "/n/cd-plain", probe: ct => slow.Task); opened.Add(typed);
            var tt = CaptureFlow.ShowAsync(typed, list, false); Pump(100);
            Field<TextBox>(typed, "NameInput").Text = "my own name.bin"; Pump(50);
            slow.SetResult(new NameProbeResult(server.BaseUrl + "/n/cd-plain", "attachment; filename=\"report final.pdf\"", "application/pdf", 1234, Array.Empty<byte>(), 200));
            PumpUntil(() => typed.ProbeTask.IsCompleted, "typed probe");
            Check(Field<TextBox>(typed, "NameInput").Text == "my own name.bin", "A name the user typed is never overwritten by the probe");
            Check(Field<TextBox>(typed, "DetectedInfo").Text.Contains("1.2") || Field<TextBox>(typed, "DetectedInfo").Text.Contains("1,234") || Field<TextBox>(typed, "DetectedInfo").Text.Contains("KB"), "Detected size is shown even when the name was edited");
            Call(typed, "Start_Click");
            Check(tt.Wait(3000) && typed.Request is { FileName: "my own name.bin", AutoName: false }, "Typed name goes to the engine as explicit");

            var browser = Capture(m, root, server.BaseUrl + "/n/cd-plain", "From Browser.pdf", "application/pdf"); opened.Add(browser);
            var bt = CaptureFlow.ShowAsync(browser, list, false);
            PumpUntil(() => browser.ProbeTask.IsCompleted, "browser-name probe");
            Check(Field<TextBox>(browser, "NameInput").Text == "From Browser.pdf", "The browser's own name beats the server's Content-Disposition");
            browser.Close(); bt.Wait(3000);

            var failing = Capture(m, root, "http://127.0.0.1:1/never/file", probe: ct => m.ProbeNameAsync(new DownloadRequest("http://127.0.0.1:1/never/file"), ct)); opened.Add(failing);
            var ft = CaptureFlow.ShowAsync(failing, list, false);
            PumpUntil(() => failing.ProbeTask.IsCompleted, "failing probe");
            Check(Field<TextBox>(failing, "NameInput").Text == "file" && Field<TextBox>(failing, "DetectedInfo").Text.Contains("Could not check"), "Unreachable link: address-derived name stays and the user is told");
            Call(failing, "Start_Click");
            Check(ft.Wait(3000) && failing.Request is { FileName: "file", AutoName: true }, "An unconfirmed address-derived name stays open for the engine to settle at download start");
        }
        finally
        {
            foreach (var window in opened) { try { window.Close(); } catch (InvalidOperationException) { } }
            try { main.Close(); } catch (InvalidOperationException) { }
        }
    }
}
