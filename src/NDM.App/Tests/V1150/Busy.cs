using System.Windows;
using System.Windows.Controls;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

internal static partial class V1150Tests
{
    // ------------------------------------------------------------------ the busy-queue prompt
    private static void BusyTrigger()
    {
        Check(BusyQueue.ShouldAsk(true, running: 1, limit: 1, adding: 1), "limit reached: ask");
        Check(BusyQueue.ShouldAsk(true, running: 3, limit: 2, adding: 1), "above the limit (lowered): ask");
        Check(!BusyQueue.ShouldAsk(true, running: 0, limit: 1, adding: 1), "nothing running: do not ask");
        Check(!BusyQueue.ShouldAsk(true, running: 2, limit: 3, adding: 1), "a free slot: do not ask");
        Check(!BusyQueue.ShouldAsk(false, running: 3, limit: 3, adding: 1), "the setting is off: do not ask");
        Check(!BusyQueue.ShouldAsk(true, running: 3, limit: 3, adding: 0), "nothing is being added: do not ask");
        Check(BusyQueue.ShouldAsk(true, running: 1, limit: 1, adding: 40), "a batch is one question");
        Check(BusyQueue.Saved(new AppSettings { BusyQueueAction = "now" }) == BusyChoice.DownloadNow && BusyQueue.Saved(new AppSettings()) == BusyChoice.Wait, "the saved action maps to the choice");
    }

    private sealed record Fixture(DownloadManager Manager, AppSettings Settings, BusyQueue Service, List<(int Running, int Adding, int Limit)> Asked, Func<BusyPromptResult?> Answer, Action<Func<BusyPromptResult?>> SetAnswer, Func<int> Saves) : IDisposable
    {
        public void Dispose() { BusyQueue.Current = null; Manager.Dispose(); }
    }
    private static Fixture NewBusy(string root, string label, int limit)
    {
        var manager = NewManager(root, label, limit);
        var settings = new AppSettings { DownloadDirectory = Path.Combine(root, label, "files"), MaxConcurrentDownloads = limit }; int saves = 0;
        var asked = new List<(int, int, int)>(); Func<BusyPromptResult?> answer = () => new BusyPromptResult(BusyChoice.Wait, null, false);
        var service = new BusyQueue(manager, settings, () => saves++) { Ask = (_, running, adding, lim) => { asked.Add((running, adding, lim)); return answer(); } };
        BusyQueue.Current = service;
        return new Fixture(manager, settings, service, asked, () => answer(), f => answer = f, () => saves);
    }
    private static async Task<IReadOnlyList<DownloadJob>> QueueBatch(DownloadManager m, string url, int count, DownloadOptionsDialog.DialogAction action = DownloadOptionsDialog.DialogAction.Queue) =>
        await PlaylistCommit.AddAsync(m, Enumerable.Range(1, count).Select(i => new DownloadRequest($"{url}/item-{i}.bin")).ToList(), action);

    private static void BusyChoices(string root)
    {
        using var slow = new SlowServer();
        // nothing running: no question, the job simply starts
        using (var f = NewBusy(root, "b0", 1))
        {
            var jobs = Sync(YoutubeCommit.AddAsync(f.Manager, new DownloadRequest(slow.BaseUrl + "/first.bin"), DownloadOptionsDialog.DialogAction.Queue));
            Check(f.Asked.Count == 0, "nothing running: Add to queue does not ask");
            PumpUntil(() => Job(f.Manager, jobs!.Id).Status == "Downloading", "and the download starts");
            // limit reached: one question; Wait leaves it queued
            var second = Sync(YoutubeCommit.AddAsync(f.Manager, new DownloadRequest(slow.BaseUrl + "/second.bin"), DownloadOptionsDialog.DialogAction.Queue));
            Check(f.Asked.Count == 1 && f.Asked[0] == (1, 1, 1), "limit reached: one question (running 1, adding 1, limit 1)");
            Pump(500); Check(Job(f.Manager, second!.Id).Status == "Queued" && f.Manager.MaxConcurrentDownloads == 1, "Wait in the queue keeps it queued behind the running download");
            // Download now: starts only that one, count unchanged
            f.SetAnswer(() => new BusyPromptResult(BusyChoice.DownloadNow, null, false));
            var third = Sync(YoutubeCommit.AddAsync(f.Manager, new DownloadRequest(slow.BaseUrl + "/third.bin"), DownloadOptionsDialog.DialogAction.Queue));
            PumpUntil(() => Job(f.Manager, third!.Id).Status == "Downloading", "Download now starts that download beside the running one");
            Check(Job(f.Manager, second.Id).Status == "Queued", "the other queued download is not started");
            Check(f.Manager.MaxConcurrentDownloads == 1 && f.Settings.MaxConcurrentDownloads == 1 && f.Saves() == 0, "Download now does not change the saved count");
            // Apply a new count: saved, applied to the engine, and the queue moves
            f.SetAnswer(() => new BusyPromptResult(BusyChoice.Wait, 4, false));
            var fourth = Sync(YoutubeCommit.AddAsync(f.Manager, new DownloadRequest(slow.BaseUrl + "/fourth.bin"), DownloadOptionsDialog.DialogAction.Queue));
            Check(f.Settings.MaxConcurrentDownloads == 4 && f.Manager.MaxConcurrentDownloads == 4 && f.Saves() == 1, "Apply changes the saved count, the engine's limit and saves the settings");
            PumpUntil(() => Job(f.Manager, second.Id).Status == "Downloading" && Job(f.Manager, fourth!.Id).Status == "Downloading", "with the higher count the waiting downloads start");
            // schedule and speed-limited paths never ask
            f.Asked.Clear();
            Sync(YoutubeCommit.AddAsync(f.Manager, new DownloadRequest(slow.BaseUrl + "/later.bin"), DownloadOptionsDialog.DialogAction.Schedule, DateTimeOffset.UtcNow.AddDays(1)));
            Sync(YoutubeCommit.AddAsync(f.Manager, new DownloadRequest(slow.BaseUrl + "/now.bin"), DownloadOptionsDialog.DialogAction.StartNow));
            Check(f.Asked.Count == 0, "Schedule and Download now buttons of the dialogs do not ask");
        }
        // remember tick
        using (var f = NewBusy(root, "b1", 1))
        {
            Sync(f.Manager.AddAsync(new DownloadRequest(slow.BaseUrl + "/run.bin"))); PumpUntil(() => f.Manager.RunningCount == 1, "one download runs");
            f.SetAnswer(() => new BusyPromptResult(BusyChoice.DownloadNow, null, true));
            var j1 = Sync(YoutubeCommit.AddAsync(f.Manager, new DownloadRequest(slow.BaseUrl + "/r1.bin"), DownloadOptionsDialog.DialogAction.Queue));
            Check(!f.Settings.AskWhenQueueBusy && f.Settings.BusyQueueAction == "now" && f.Saves() == 1 && f.Asked.Count == 1, "the tick remembers 'download now' and stops the questions");
            PumpUntil(() => Job(f.Manager, j1!.Id).Status == "Downloading", "the remembered action started the download");
            var j2 = Sync(YoutubeCommit.AddAsync(f.Manager, new DownloadRequest(slow.BaseUrl + "/r2.bin"), DownloadOptionsDialog.DialogAction.Queue));
            Check(f.Asked.Count == 1, "no second question");
            PumpUntil(() => Job(f.Manager, j2!.Id).Status == "Downloading", "the remembered 'download now' applies without asking");
            f.Settings.BusyQueueAction = "wait";
            var j3 = Sync(YoutubeCommit.AddAsync(f.Manager, new DownloadRequest(slow.BaseUrl + "/r3.bin"), DownloadOptionsDialog.DialogAction.Queue)); Pump(400);
            Check(f.Asked.Count == 1 && Job(f.Manager, j3!.Id).Status == "Queued", "a remembered 'wait' queues without asking");
            f.Settings.AskWhenQueueBusy = true;
            Sync(YoutubeCommit.AddAsync(f.Manager, new DownloadRequest(slow.BaseUrl + "/r4.bin"), DownloadOptionsDialog.DialogAction.Queue));
            Check(f.Asked.Count == 2, "turning the Options box back on brings the question back");
        }
        // remembered 'wait' plus a closed prompt
        using (var f = NewBusy(root, "b2", 1))
        {
            Sync(f.Manager.AddAsync(new DownloadRequest(slow.BaseUrl + "/run.bin"))); PumpUntil(() => f.Manager.RunningCount == 1, "one download runs");
            f.SetAnswer(() => null);
            var j = Sync(YoutubeCommit.AddAsync(f.Manager, new DownloadRequest(slow.BaseUrl + "/closed.bin"), DownloadOptionsDialog.DialogAction.Queue)); Pump(300);
            Check(Job(f.Manager, j!.Id).Status == "Queued" && f.Settings.AskWhenQueueBusy && f.Saves() == 0, "closing the prompt means wait and remembers nothing");
        }
        // batches: one question for the whole package
        using (var f = NewBusy(root, "b3", 2))
        {
            Sync(f.Manager.AddAsync(new DownloadRequest(slow.BaseUrl + "/run1.bin"))); Sync(f.Manager.AddAsync(new DownloadRequest(slow.BaseUrl + "/run2.bin")));
            PumpUntil(() => f.Manager.RunningCount == 2, "two downloads run");
            var batch = Sync(QueueBatch(f.Manager, slow.BaseUrl, 12));
            Check(f.Asked.Count == 1 && f.Asked[0] == (2, 12, 2), "a batch of 12 asks once with the batch size");
            Check(batch.Count == 12, "all 12 were added");
            Sync(QueueBatch(f.Manager, slow.BaseUrl + "/s", 3, DownloadOptionsDialog.DialogAction.Schedule));
            Check(f.Asked.Count == 1, "a scheduled batch does not ask");
        }
        // a free slot never asks, and no service installed (headless paths) never asks
        using (var f = NewBusy(root, "b4", 3))
        {
            Sync(f.Manager.AddAsync(new DownloadRequest(slow.BaseUrl + "/run.bin"))); PumpUntil(() => f.Manager.RunningCount == 1, "one runs");
            Sync(QueueBatch(f.Manager, slow.BaseUrl, 2));
            Check(f.Asked.Count == 0, "a free slot: no question");
            BusyQueue.Current = null; f.Settings.MaxConcurrentDownloads = 1; f.Manager.MaxConcurrentDownloads = 1;
            Sync(QueueBatch(f.Manager, slow.BaseUrl + "/h", 2));
            Check(f.Asked.Count == 0, "headless: with no prompt service nothing is asked and the saved settings rule (queue waits)");
        }
        // row menu 'Add to queue' through the main window
        using (var f = NewBusy(root, "b5", 1))
        {
            Sync(f.Manager.AddAsync(new DownloadRequest(slow.BaseUrl + "/run.bin"))); PumpUntil(() => f.Manager.RunningCount == 1, "one runs");
            var paused = Sync(f.Manager.AddPausedAsync(new DownloadRequest(slow.BaseUrl + "/paused.bin")));
            var main = new MainWindow(f.Manager, f.Settings, () => { }, enableTray: false) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10 };
            main.Show(); Pump(300);
            try
            {
                f.SetAnswer(() => new BusyPromptResult(BusyChoice.DownloadNow, null, false));
                Pump(900);
                var row = main.Rows.First(r => r.Id == paused.Id);
                Check(main.QueueRows(new[] { row }) == 1 && f.Asked.Count == 1, "the row menu's Add to queue asks when the queue is busy");
                PumpUntil(() => Job(f.Manager, paused.Id).Status == "Downloading", "and Download now starts it");
            }
            finally { Dialogs.Override = (_, _, _, _, _, _) => MessageBoxResult.Yes; main.Close(); Dialogs.Override = null; }
        }
    }

    private static void BusyPromptWindow()
    {
        foreach (var theme in new[] { "Light", "DeepNavy" })
        {
            ThemeManager.Apply(Application.Current, theme, null); Pump(100);
            var w = new BusyQueuePrompt(2, 1, 2) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 40, Top = 40 };
            w.Show(); Pump(250); w.UpdateLayout();
            Check(w.ActualHeight > 150 && ((TextBox)w.FindName("Message")).Text.Contains("2"), $"{theme}: the prompt shows the running count");
            Check(((RadioButton)w.FindName("WaitRadio")).IsChecked == true && ((RadioButton)w.FindName("NowRadio")).IsChecked == false, $"{theme}: Wait in the queue is the default");
            Check(((TextBox)w.FindName("CountBox")).Text == "2", $"{theme}: the number box starts at the current count");
            SaveShot(w, $"busy-{theme}.png");
            if (theme == "DeepNavy")
            {
                w.FlowDirection = FlowDirection.RightToLeft; Pump(200); w.UpdateLayout();
                Check(w.ActualWidth >= 360, "RTL: the prompt lays out"); SaveShot(w, "busy-DeepNavy-rtl.png");
            }
            // invalid count
            ((TextBox)w.FindName("CountBox")).Text = "17"; ((Button)w.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(((TextBlock)w.FindName("CountError")).Visibility == Visibility.Visible && w.Result is null, $"{theme}: 17 is refused with a message");
            ((TextBox)w.FindName("CountBox")).Text = "0"; ((Button)w.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(w.Result is null, $"{theme}: 0 is refused");
            w.Close();
        }
        ThemeManager.Apply(Application.Current, "Light", null);
        // results of the real window, driven by its handlers
        BusyPromptResult? Drive(Action<BusyQueuePrompt> act)
        {
            var w = new BusyQueuePrompt(1, 5, 1) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 40, Top = 40 };
            w.Show(); Pump(100); act(w); var r = w.Result; w.Close(); return r;
        }
        void Click(BusyQueuePrompt w, string name) => ((Button)w.FindName(name)).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        var ok = Drive(w => Click(w, "OkButton"));
        Check(ok is { Choice: BusyChoice.Wait, NewLimit: null, Remember: false }, "OK with the defaults waits in the queue");
        var now = Drive(w => { ((RadioButton)w.FindName("NowRadio")).IsChecked = true; ((CheckBox)w.FindName("RememberBox")).IsChecked = true; Click(w, "OkButton"); });
        Check(now is { Choice: BusyChoice.DownloadNow, NewLimit: null, Remember: true }, "Download now with the remember tick");
        var apply = Drive(w => { ((TextBox)w.FindName("CountBox")).Text = "4"; Click(w, "ApplyButton"); });
        Check(apply is { Choice: BusyChoice.Wait, NewLimit: 4 }, "Apply returns the new count of 4");
        Check(Loc.T("busy.message", 2).Length > 0 && Loc.T("busy.message_batch", 2, 9).Contains("9"), "batch wording carries the batch size");
    }
}
