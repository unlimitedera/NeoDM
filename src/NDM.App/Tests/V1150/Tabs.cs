using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;
using NDM.Tests;

internal static partial class V1150Tests
{
    // ------------------------------------------------------------------ settings: defaults, migration
    private static void SettingsTests(string root)
    {
        var fresh = new AppSettings();
        Check(fresh.MaxConcurrentDownloads == 1, "a new install downloads 1 at a time");
        Check(fresh.AskWhenQueueBusy && fresh.BusyQueueAction == "wait", "the busy-queue question is on and waits by default");
        Check(!fresh.ShowDownloadsAsTabs && fresh.TabCloseChoice == "", "tabs are off and no close choice is remembered by default");
        Check(AppSettings.CurrentSchema == 12, "settings schema is 12");
        string Load(string name, string json) { var dir = Path.Combine(root, "s-" + name); Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, "settings.json"), json); return dir; }
        Check(AppSettings.Load(Load("a", "{\"SettingsSchema\":11,\"MaxConcurrentDownloads\":2}")).MaxConcurrentDownloads == 2, "an existing user's number (2) is kept");
        Check(AppSettings.Load(Load("b", "{\"SettingsSchema\":11,\"MaxConcurrentDownloads\":3}")).MaxConcurrentDownloads == 3, "an existing user's number (3) is kept");
        Check(AppSettings.Load(Load("c", "{\"SettingsSchema\":11,\"MaxConcurrentDownloads\":1}")).MaxConcurrentDownloads == 1, "an existing user's number (1) is kept");
        Check(AppSettings.Load(Load("d", "{\"SettingsSchema\":11,\"MaxConcurrentDownloads\":16}")).MaxConcurrentDownloads == 16, "an existing user's number (16) is kept");
        Check(AppSettings.Load(Load("e", "{\"SettingsSchema\":9}")).MaxConcurrentDownloads == 3, "an old file without the number keeps the old default of 3");
        var migrated = AppSettings.Load(Load("f", "{\"SettingsSchema\":11,\"MaxConcurrentDownloads\":4,\"Theme\":\"Dark\"}"));
        Check(migrated.SettingsSchema == 12 && migrated.AskWhenQueueBusy && !migrated.ShowDownloadsAsTabs && migrated.Theme == "Dark", "an older file loads as schema 12 with the new options at their defaults and other values kept");
        var dir = Path.Combine(root, "s-round"); Directory.CreateDirectory(dir);
        var saved = new AppSettings { ShowDownloadsAsTabs = true, TabCloseChoice = "hide", AskWhenQueueBusy = false, BusyQueueAction = "now", MaxConcurrentDownloads = 5 }; saved.Save(dir);
        var back = AppSettings.Load(dir);
        Check(back.ShowDownloadsAsTabs && back.TabCloseChoice == "hide" && !back.AskWhenQueueBusy && back.BusyQueueAction == "now" && back.MaxConcurrentDownloads == 5, "the new settings survive a save and load");
        var odd = AppSettings.Load(Load("g", "{\"SettingsSchema\":12,\"TabCloseChoice\":\"zzz\",\"BusyQueueAction\":\"x\"}"));
        Check(odd.TabCloseChoice == "" && odd.BusyQueueAction == "wait", "unknown remembered choices are reset");
        var copy = new AppSettings(); copy.CopyFrom(saved);
        Check(copy.ShowDownloadsAsTabs && copy.TabCloseChoice == "hide" && !copy.AskWhenQueueBusy && copy.BusyQueueAction == "now", "CopyFrom carries the new settings");
    }

    // ------------------------------------------------------------------ the tab model
    private static void TabModel()
    {
        var m = new DownloadTabsModel(); int changes = 0; m.Changed += () => changes++;
        Check(m.Count == 0 && m.SelectedId is null, "an empty model has no selection");
        Check(m.Add("a") && m.Add("b") && m.Add("c"), "tabs are added");
        Check(m.Ids.SequenceEqual(new[] { "a", "b", "c" }), "tabs keep their opening order");
        Check(m.SelectedId == "c", "the newest tab is selected");
        Check(!m.Add("a") && m.Count == 3 && m.SelectedId == "a", "adding an open download selects its tab instead of duplicating it");
        m.SelectNext(); Check(m.SelectedId == "b", "next tab"); m.SelectNext(); m.SelectNext(); Check(m.SelectedId == "a", "next wraps to the first tab");
        m.SelectPrevious(); Check(m.SelectedId == "c", "previous wraps to the last tab");
        Check(m.Select("b") && !m.Select("zzz") && m.SelectedId == "b", "select by id, unknown ids refused");
        Check(m.Remove("b") == "c" && m.Ids.SequenceEqual(new[] { "a", "c" }), "closing the selected tab selects its right neighbour");
        m.Select("c"); Check(m.Remove("c") == "a", "closing the last selected tab selects the left neighbour");
        m.Add("d", select: false); Check(m.SelectedId == "a", "a tab added without selecting leaves the selection alone");
        Check(m.Remove("d") == "a" && m.SelectedId == "a", "closing another tab keeps the selection");
        Check(m.Remove("a") is null && m.Count == 0, "closing the last tab leaves nothing selected");
        Check(changes > 8, "every change raises Changed");
        Check(DownloadTabsModel.TabWidth(900, 2) == DownloadTabsModel.MaxTabWidth, "few tabs are at most the maximum width");
        Check(DownloadTabsModel.TabWidth(900, 5) == 180 && DownloadTabsModel.TabWidth(900, 25) == DownloadTabsModel.MinTabWidth, "tabs shrink to share the strip, then stop at the minimum");
        Check(!DownloadTabsModel.NeedsScroll(900, 6) && DownloadTabsModel.NeedsScroll(900, 8), "the strip scrolls only when the tabs no longer fit at the minimum width");
    }

    private static void ClosePolicy()
    {
        foreach (var running in new[] { "Downloading", "Queued", "Connecting", "Checking" })
        {
            Check(TabClosePolicy.Decide(running, "") == TabCloseAction.Ask, running + " asks");
            Check(TabClosePolicy.Decide(running, "hide") == TabCloseAction.Hide && TabClosePolicy.Decide(running, "cancel") == TabCloseAction.Cancel, running + " follows the remembered choice");
        }
        foreach (var idle in new[] { "Completed", "Failed", "Paused", "Canceled", "Scheduled", "Seeding" })
            Check(TabClosePolicy.Decide(idle, "cancel") == TabCloseAction.Close, idle + " simply closes, whatever is remembered");
    }

    // ------------------------------------------------------------------ the window
    private static DownloadTabsWindow NewTabs(DownloadManager m, AppSettings s, Action? save = null) =>
        new(m, new JobCompletionStore(), s, save) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 30, Top = 30 };

    private static void TabsWindow(string root)
    {
        using var slow = new SlowServer(); using var manager = NewManager(root, "tw");
        var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "tw", "files") };
        var running = Sync(manager.AddAsync(new DownloadRequest(slow.BaseUrl + "/running.zip")));
        var paused = Sync(manager.AddPausedAsync(new DownloadRequest(slow.BaseUrl + "/paused-with-a-rather-long-file-name.mp4")));
        PumpUntil(() => Job(manager, running.Id).Status == "Downloading", "the first download runs");
        var tabs = NewTabs(manager, settings); tabs.Show(); Pump(150);
        Check(tabs.AddOrSelect(running.Id) && tabs.AddOrSelect(paused.Id), "two downloads open as tabs");
        Pump(300);
        Check(tabs.TabCount == 2 && tabs.Model.SelectedId == paused.Id && tabs.SelectedView!.JobId == paused.Id, "the newest tab is selected and its view is shown");
        Check(tabs.AddOrSelect(running.Id) && tabs.TabCount == 2 && tabs.Model.SelectedId == running.Id, "opening a download that has a tab only selects it");
        Check(!tabs.AddOrSelect("missing-id"), "a download that no longer exists opens nothing");
        var buttons = tabs.TabButtons; Pump(700);
        Check(buttons[0].FileText == "running.zip" && buttons[1].FileText.StartsWith("paused-with"), "each tab shows its file name");
        Check(buttons[0].StateText.StartsWith("Downloading") && buttons[1].StateText == "Paused", "tabs show the state: " + buttons[0].StateText + " / " + buttons[1].StateText);
        Check(buttons[0].StatusToken == "StatusDownloadingBrush" && buttons[1].StatusToken == "StatusPausedBrush", "the bar under each tab takes the status token of its download");
        Check(buttons[0].IsChecked == true && buttons[1].IsChecked == false, "only the selected tab is checked");
        Check(buttons[1].ToolTip is string tip && tip.Contains("paused-with-a-rather-long-file-name.mp4"), "the tooltip carries the full file name");
        Check(tabs.Title.StartsWith("running.zip") && tabs.Title.Contains("+1"), "the window title names the selected download and how many others: " + tabs.Title);
        var extensionShown = buttons[0].Extension; Check(extensionShown == "zip", "the tab shows the file-type icon of the extension");
        Check((buttons[0].Template.FindName("PART_Close", buttons[0]) as Button)?.Visibility == Visibility.Visible, "the selected tab shows its close button");
        Check((buttons[1].Template.FindName("PART_Close", buttons[1]) as Button)?.Visibility == Visibility.Hidden, "an unselected tab hides its close button until hover");
        // clicking a tab selects it; the shared content follows
        buttons[1].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent)); Pump(100);
        Check(tabs.Model.SelectedId == paused.Id && tabs.SelectedView!.JobId == paused.Id, "choosing a tab shows that download");
        // plus button
        int added = 0; tabs.AddRequested += () => added++;
        ((Button)tabs.FindName("AddButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Check(added == 1, "the plus button asks for Add URL");
        // status colour changes with state
        manager.Pause(running.Id); PumpUntil(() => tabs.TabButtons[0].StatusToken == "StatusPausedBrush", "pausing a download turns its bar paused-coloured");
        manager.Resume(running.Id); PumpUntil(() => tabs.TabButtons[0].StatusToken == "StatusDownloadingBrush", "resuming turns it downloading-coloured again");
        // keyboard
        var previous = DownloadTabsWindow.Modifiers;
        try
        {
            void Press(Key key, ModifierKeys mods) { DownloadTabsWindow.Modifiers = () => mods; tabs.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(tabs)!, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent }); Pump(60); }
            tabs.Model.Select(running.Id);
            Press(Key.Tab, ModifierKeys.Control); Check(tabs.Model.SelectedId == paused.Id, "Ctrl+Tab selects the next tab");
            Press(Key.Tab, ModifierKeys.Control); Check(tabs.Model.SelectedId == running.Id, "Ctrl+Tab wraps around");
            Press(Key.Tab, ModifierKeys.Control | ModifierKeys.Shift); Check(tabs.Model.SelectedId == paused.Id, "Ctrl+Shift+Tab selects the previous tab");
            Press(Key.Tab, ModifierKeys.None); Check(tabs.Model.SelectedId == paused.Id, "Tab alone does not switch tabs");
            Press(Key.W, ModifierKeys.Control); PumpUntil(() => !tabs.HasTab(paused.Id), "Ctrl+W closes the selected tab (a paused download closes without a question)");
            Check(tabs.TabCount == 1 && tabs.Model.SelectedId == running.Id && Job(manager, paused.Id).Status == "Paused", "the paused download itself is untouched");
        }
        finally { DownloadTabsWindow.Modifiers = previous; }
        tabs.ForceClose(); manager.Remove(running.Id); manager.Remove(paused.Id);
    }

    // ------------------------------------------------------------------ close semantics
    private static void CloseSemantics(string root)
    {
        using var slow = new SlowServer(); using var fixture = new FixtureServer(); using var manager = NewManager(root, "cs", 8);
        int saves = 0; var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "cs", "files") };
        var done = Sync(manager.AddAsync(new DownloadRequest(fixture.BaseUrl + "/range", "finished.bin")));
        var failed = Sync(manager.AddAsync(new DownloadRequest(fixture.BaseUrl + "/fail", "broken.bin")));
        var paused = Sync(manager.AddPausedAsync(new DownloadRequest(slow.BaseUrl + "/stopped.bin")));
        PumpUntil(() => Job(manager, done.Id).Status == "Completed" && Job(manager, failed.Id).Status == "Failed", "a finished and a failed download exist", 30000);
        var asks = new List<(string? Name, int Count)>(); TabCloseAnswer? answer = null;
        TabCloseWindow.Override = (_, name, count) => { asks.Add((name, count)); return answer; };
        var tabs = NewTabs(manager, settings, () => saves++); tabs.Show(); Pump(100);
        try
        {
            foreach (var id in new[] { done.Id, failed.Id, paused.Id }) tabs.AddOrSelect(id);
            Pump(200);
            foreach (var (id, label) in new[] { (done.Id, "finished"), (failed.Id, "failed"), (paused.Id, "paused") })
            { Check(Sync(tabs.RequestCloseTabAsync(id)) && !tabs.HasTab(id), label + " tab closes at once"); }
            Check(asks.Count == 0, "no question for finished, failed or paused tabs");
            Check(Job(manager, done.Id).Status == "Completed" && Job(manager, failed.Id).Status == "Failed", "closing those tabs left the downloads as they were");

            var r1 = Sync(manager.AddAsync(new DownloadRequest(slow.BaseUrl + "/one.bin")));
            PumpUntil(() => Job(manager, r1.Id).Status == "Downloading", "a running download exists");
            tabs.AddOrSelect(r1.Id);
            answer = null; Check(!Sync(tabs.RequestCloseTabAsync(r1.Id)) && tabs.HasTab(r1.Id) && asks.Count == 1 && asks[0].Name == "one.bin", "Keep open leaves the tab and the download alone");
            answer = new(TabCloseAction.Hide, false); Check(Sync(tabs.RequestCloseTabAsync(r1.Id)) && !tabs.HasTab(r1.Id) && asks.Count == 2, "Hide closes the tab");
            Check(Job(manager, r1.Id).Status == "Downloading", "the hidden download keeps downloading");
            Check(settings.TabCloseChoice == "" && saves == 0, "without the tick nothing is remembered");
            Check(tabs.AddOrSelect(r1.Id) && tabs.HasTab(r1.Id), "reopening a hidden download shows it as a tab again");
            answer = new(TabCloseAction.Hide, true); Check(Sync(tabs.RequestCloseTabAsync(r1.Id)) && settings.TabCloseChoice == "hide" && saves == 1, "Remember my choice stores 'hide' in the settings and saves");
            tabs.AddOrSelect(r1.Id); asks.Clear();
            Check(Sync(tabs.RequestCloseTabAsync(r1.Id)) && asks.Count == 0 && Job(manager, r1.Id).Status == "Downloading", "with 'hide' remembered the tab closes without a question and the download runs on");
            settings.TabCloseChoice = "cancel"; tabs.AddOrSelect(r1.Id);
            Check(Sync(tabs.RequestCloseTabAsync(r1.Id)) && asks.Count == 0 && !manager.Jobs.Any(j => j.Id == r1.Id), "with 'cancel' remembered the tab closes and the download is cancelled without a question");
            settings.TabCloseChoice = "";

            var r2 = Sync(manager.AddAsync(new DownloadRequest(slow.BaseUrl + "/two.bin")));
            PumpUntil(() => Job(manager, r2.Id).Status == "Downloading", "another running download exists");
            tabs.AddOrSelect(r2.Id); asks.Clear();
            answer = new(TabCloseAction.Cancel, false); Check(Sync(tabs.RequestCloseTabAsync(r2.Id)) && !manager.Jobs.Any(j => j.Id == r2.Id) && asks.Count == 1, "'Cancel the download' removes the download and closes the tab");

            // a queued download (limit reached) also asks
            using var tight = NewManager(root, "cs2", 1);
            var first = Sync(tight.AddAsync(new DownloadRequest(slow.BaseUrl + "/first.bin"))); var second = Sync(tight.AddAsync(new DownloadRequest(slow.BaseUrl + "/second.bin")));
            PumpUntil(() => Job(tight, first.Id).Status == "Downloading", "the first download runs");
            Check(Job(tight, second.Id).Status == "Queued", "the second one waits in the queue");
            var tabs2 = NewTabs(tight, settings); tabs2.Show(); tabs2.AddOrSelect(second.Id); tabs2.AddOrSelect(first.Id); Pump(200); asks.Clear(); answer = new(TabCloseAction.Hide, false);
            Check(Sync(tabs2.RequestCloseTabAsync(second.Id)) && asks.Count == 1, "a queued download asks too");

            // closing the whole window: one question for all running tabs, never a silent cancel
            asks.Clear(); answer = null; tabs2.AddOrSelect(second.Id); Pump(100);
            tabs2.Close(); Pump(150);
            Check(tabs2.IsVisible && asks.Count == 1 && asks[0].Count == 2 && asks[0].Name is null, "closing the window asks once for all running downloads");
            Check(tight.Jobs.Count == 2 && tight.Jobs.All(j => j.Status is "Downloading" or "Queued"), "keeping it open cancels nothing");
            answer = new(TabCloseAction.Hide, false); tabs2.Close(); Pump(150);
            Check(!tabs2.IsLoaded && asks.Count == 2 && tight.Jobs.Count == 2 && Job(tight, first.Id).Status == "Downloading", "hide all closes the window and the downloads continue");
            var tabs3 = NewTabs(tight, settings); tabs3.Show(); tabs3.AddOrSelect(first.Id); asks.Clear(); answer = new(TabCloseAction.Cancel, false); tabs3.Close();
            PumpUntil(() => !tight.Jobs.Any(j => j.Id == first.Id), "cancel them removes the running download when the window closes");
            Check(asks.Count == 1, "that was one question");
            tabs3.ForceClose();
            tight.Dispose();
        }
        finally { TabCloseWindow.Override = null; tabs.ForceClose(); }
    }

    // ------------------------------------------------------------------ 25 tabs
    private static void ManyTabs(string root)
    {
        using var slow = new SlowServer(); using var manager = NewManager(root, "many");
        var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "many", "files") };
        var ids = new List<string>();
        for (int i = 1; i <= 25; i++) ids.Add(Sync(manager.AddPausedAsync(new DownloadRequest(slow.BaseUrl + $"/file-number-{i:00}.zip"))).Id);
        var tabs = NewTabs(manager, settings); tabs.Width = 760; tabs.Height = 700; tabs.Show(); Pump(150);
        foreach (var id in ids) tabs.AddOrSelect(id);
        Pump(400); tabs.UpdateLayout(); Pump(200);
        Check(tabs.TabCount == 25 && tabs.Model.Ids.SequenceEqual(ids), "25 tabs open in opening order");
        Check(tabs.ScrollButtonsVisible, "the strip offers scroll buttons when the tabs do not fit");
        var scroll = (ScrollViewer)tabs.FindName("TabScroll");
        Check(scroll.ScrollableWidth > 0 && tabs.TabButtons.All(b => b.ActualWidth >= DownloadTabsModel.MinTabWidth - 3), "tabs stop shrinking at the minimum width");
        bool Visible(DownloadTabButton b) { var r = b.TransformToAncestor(scroll).TransformBounds(new Rect(0, 0, b.ActualWidth, b.ActualHeight)); return r.Left >= -2 && r.Right <= scroll.ViewportWidth + 2; }
        Check(Visible(tabs.TabButtons[24]), "the newest (selected) tab is visible");
        tabs.Model.Select(ids[0]); Pump(300); Check(Visible(tabs.TabButtons[0]), "selecting the first tab scrolls it into view");
        tabs.Model.Select(ids[12]); Pump(300); Check(Visible(tabs.TabButtons[12]), "selecting a middle tab scrolls it into view");
        double before = scroll.HorizontalOffset;
        ((System.Windows.Controls.Primitives.RepeatButton)tabs.FindName("ScrollEnd")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Pump(100);
        Check(scroll.HorizontalOffset > before, "the end scroll button moves the strip");
        for (int i = 0; i < 24; i++) tabs.Model.SelectNext();
        Check(tabs.Model.SelectedId == ids[11], "24 steps over 25 tabs wrap from tab 13 to tab 12");
        tabs.Width = 1500; Pump(300); tabs.UpdateLayout();
        Check(tabs.TabButtons.All(b => b.ActualWidth >= DownloadTabsModel.MinTabWidth - 3) && (tabs.ScrollButtonsVisible || scroll.ScrollableWidth <= 0.5), "a wider window uses the room (or drops the arrows when everything fits)");
        tabs.ForceClose();
    }
}
