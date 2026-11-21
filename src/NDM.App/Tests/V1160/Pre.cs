// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

internal static partial class V1160Tests
{
    private static bool HasText(DependencyObject d, string text)
    {
        if (d is TextBlock t && t.Text == text) return true;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) if (HasText(VisualTreeHelper.GetChild(d, i), text)) return true;
        return false;
    }

    /// <summary>Pre-tasks P2 to P5: per-download Schedule (row menu, Downloads menu, bulk bar), the queue box on the download tab view, collapsible Queues section at 900x480, no pinned DOWNLOADS label.</summary>
    private static void PreTasks(string root)
    {
        using var slow = new SlowServer(); using var m = NewManager(root, "pre", 2);
        var work = m.CreateQueue("Work");
        var a = Sync(m.AddPausedAsync(new DownloadRequest(slow.BaseUrl + "/a.zip", "a.zip"))); var b = Sync(m.AddPausedAsync(new DownloadRequest(slow.BaseUrl + "/b.zip", "b.zip"))); var c = Sync(m.AddPausedAsync(new DownloadRequest(slow.BaseUrl + "/c.zip", "c.zip", QueueId: work.Id)));
        var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "pre", "files"), ShowCompletionDialog = false, ShowProgressOnCompletion = false };
        var w = new MainWindow(m, settings, () => { }, enableTray: false) { ShowInTaskbar = false, Width = 900, Height = 560, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10, QueueClock = () => D(16, 12, 0) };
        w.Show(); Pump(800); var list = List(w);
        DownloadRow RowOf(string id) => w.Rows.First(r => r.Id == id);

        // ---- P2: per-download schedule
        Check(MainWindow.CanSchedule(RowOf(a.Id)), "a paused download can be scheduled");
        list.SelectedItems.Clear(); list.SelectedItems.Add(RowOf(a.Id)); Pump(200);
        var menu = (MenuItem)w.FindName("MenuScheduleDownload"); Check(menu.IsEnabled && (string)menu.Header == Loc.T("main.menu.schedule_download"), "Downloads menu: 'Schedule download...' is enabled for a selected download");
        Check(w.UpdateRowMenu() && ((MenuItem)w.FindName("CtxSchedule")).IsEnabled && (string)((MenuItem)w.FindName("CtxSchedule")).Header == Loc.T("main.menu.sc_hedule"), "row menu: Schedule... is enabled for a download");
        var at = DateTime.Now.AddHours(3); var text = at.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        string? seenShot = null;
        w.ScheduleDialogCreated = d => d.Loaded += (_, _) => { d.Picker!.Text = text; Pump(100); SaveShot(d, "schedule-light.png"); seenShot = "x"; Click((Button)d.FindName("ApplyButton")); };
        w.ScheduleDownloads(new[] { RowOf(a.Id) }); Pump(300);
        Check(seenShot is not null && Job(m, a.Id).Status == "Scheduled" && Job(m, a.Id).ScheduledAt is { } when1 && Math.Abs((when1.LocalDateTime - at).TotalMinutes) < 1.5, "scheduling a single download makes it Scheduled at the chosen time");
        // the engine keeps scheduled downloads out of the run until the time, and a queue schedule is a separate thing
        Check(m.Jobs.Count(j => j.Status == "Downloading") == 0, "a scheduled download does not start before its time");
        // several at once (bulk bar)
        w.ScheduleDialogCreated = d => d.Loaded += (_, _) => { d.Picker!.Text = at.AddHours(1).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture); Pump(100); Click((Button)d.FindName("ApplyButton")); };
        w.ScheduleDownloads(new[] { RowOf(b.Id), RowOf(c.Id) }); Pump(300);
        Check(Job(m, b.Id).Status == "Scheduled" && Job(m, c.Id).Status == "Scheduled" && Job(m, b.Id).ScheduledAt == Job(m, c.Id).ScheduledAt, "scheduling several downloads gives them the same start time");
        var bulkButton = (Button)w.FindName("BulkSchedule"); Check((string)bulkButton.Content == Loc.T("main.bulk.schedule"), "the bulk bar has a Schedule button");
        // clearing: Start now removes the schedule
        w.ScheduleDialogCreated = d => d.Loaded += (_, _) => Click((Button)d.FindName("StartNowButton"));
        w.ScheduleDownloads(new[] { RowOf(b.Id) }); Pump(300);
        Check(Job(m, b.Id).Status != "Scheduled", "'Start now' clears the schedule (the download leaves the Scheduled state)");
        w.ScheduleDialogCreated = null;
        CheckLocales(new[] { "main.menu.schedule_download", "main.bulk.schedule", "queue.tab.label", "queue.section.collapse", "queue.section.expand" }, "1.16.0 pre");

        // schedule dialog render in a second theme and RTL
        foreach (var theme in new[] { "DeepNavy", "Light" })
        {
            ThemeManager.Apply(Application.Current, theme, null);
            var d = new JobOptionsWindow(m, new[] { a.Id }, true) { Left = 10, Top = 10, WindowStartupLocation = WindowStartupLocation.Manual }; d.Show(); Pump(400); SaveShot(d, "schedule-" + theme.ToLowerInvariant() + ".png");
            Check(d.Picker is not null && d.ActualHeight > 300, "schedule dialog renders in " + theme); d.Close();
        }
        var rtl = new JobOptionsWindow(m, new[] { a.Id }, true) { Left = 10, Top = 10, WindowStartupLocation = WindowStartupLocation.Manual }; rtl.Show(); Loc.SetLanguage("ar"); rtl.FlowDirection = FlowDirection.RightToLeft; Pump(400);
        SaveShot(rtl, "schedule-rtl-ar.png"); Check(rtl.Picker!.FlowDirection == FlowDirection.RightToLeft || rtl.FlowDirection == FlowDirection.RightToLeft, "RTL: the schedule dialog mirrors"); rtl.Close(); Loc.SetLanguage("en"); Pump(200);

        // ---- P3: queue on the download tab view
        var view = new DownloadProgressView(m, c.Id); var host = new Window { Content = view, Width = 700, Height = 700, Left = 10, Top = 10, WindowStartupLocation = WindowStartupLocation.Manual }; host.Show(); Pump(900);
        var box = (ComboBox)view.FindName("QueueBox");
        Check(box.Visibility == Visibility.Visible && box.Items.Count == m.Queues.Count && ((ComboBoxItem)box.SelectedItem).Tag as string == work.Id && ((ComboBoxItem)box.SelectedItem).Content as string == "Work", "the tab view shows the download's queue name in a queue box");
        box.SelectedIndex = 0; Pump(300);
        Check(Job(m, c.Id).QueueId == m.Queues[0].Id, "choosing another queue in the tab view moves the download");
        SaveShot(host, "tabview-queue-light.png"); host.Close();
        var tabs = new DownloadTabsWindow(m, null, settings, () => { }); tabs.Show(); Pump(200); tabs.AddOrSelect(a.Id); Pump(500);
        Check(tabs.SelectedView is { } sv && ((ComboBox)sv.FindName("QueueBox")).Items.Count == m.Queues.Count, "the tabbed window's views carry the queue box too"); tabs.ForceClose();

        // ---- P4/P5: sections at 900x480
        w.Height = 560; Pump(200);
        Check(!HasText(w, Loc.T("main.text.downloads")), "no pinned DOWNLOADS label above the queue items (it read wrongly when the sidebar was scrolled)");
        var nav = Nav(w); var headerItem = nav.Items.OfType<ListBoxItem>().First(i => i.Tag as string == "qheader");
        Check(w.QueuesExpanded && w.QueueItem(QueueIds.Main) is not null, "with more than one queue the Queues section is open by default");
        Check(settings.QueuesSectionExpanded is null, "the default is not written until the user chooses");
        Click(w.QueuesHeaderButton!); Pump(300);
        Check(!w.QueuesExpanded && settings.QueuesSectionExpanded == false && w.QueueItem(QueueIds.Main) is null && w.QueuesHeaderButton!.ToolTip as string == Loc.T("queue.section.expand"), "the Queues header collapses the section and remembers the choice");
        Click(w.QueuesHeaderButton!); Pump(300);
        Check(w.QueuesExpanded && settings.QueuesSectionExpanded == true && w.QueueItem(work.Id) is not null, "clicking the header again opens it");
        // categories collapsed + 900x480: the queues are inside the sidebar without scrolling
        w.Width = 900; w.Height = 560; var all = (NavTreeItem)nav.Items[0]; all.IsExpanded = false; Pump(400);
        foreach (var q in m.Queues) { w.QueueItem(q.Id)!.IsExpanded = false; } Pump(300);
        w.ScrollQueuesIntoView(); Pump(300); SaveShot(w, "sidebar-900x480-light.png");
        bool Inside(FrameworkElement e) { var p = e.TranslatePoint(new Point(0, e.ActualHeight), nav); return e.IsVisible && p.Y <= nav.ActualHeight + 0.5; }
        Check(Inside(w.QueueItem(work.Id)!) && Inside(nav.Items.OfType<ListBoxItem>().First(i => i.Tag as string == MainWindow.QueueNewTag)), "at 900x480 with the categories folded, the whole Queues section is one scroll away (scrolled to the end it is fully in view)");
        ThemeManager.Apply(Application.Current, "DeepNavy", null); Pump(400); SaveShot(w, "sidebar-900x480-deepnavy.png"); ThemeManager.Apply(Application.Current, "Light", null);
        w.Close();
    }
}
