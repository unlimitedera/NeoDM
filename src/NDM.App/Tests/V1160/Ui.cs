using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

internal static partial class V1160Tests
{
    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static ListBox Nav(MainWindow w) => (ListBox)w.FindName("Navigation");
    private static ListView List(MainWindow w) => (ListView)w.FindName("DownloadList");
    private static string TextOf(ListBoxItem item) => string.Concat(AllText(item));
    private static IEnumerable<string> AllText(DependencyObject d)
    {
        if (d is TextBlock t) yield return t.Text + " ";
        if (d is ContentControl { Content: DependencyObject c }) foreach (var x in AllText(c)) yield return x;
        if (d is Panel p) foreach (var child in p.Children.OfType<DependencyObject>()) foreach (var x in AllText(child)) yield return x;
    }
    private static string[] VisibleIds(MainWindow w) => CollectionViewSource.GetDefaultView(w.Rows).Cast<DownloadRow>().Select(r => r.Id).ToArray();
    private static MenuItem? Entry(ItemsControl menu, string header) => menu.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Header as string)?.Replace("_", "") == header.Replace("_", ""));

    private static void Sidebar(string root)
    {
        using var slow = new SlowServer(); using var m = NewManager(root, "side", 2);
        var games = m.CreateQueue("Games pack"); var work = m.CreateQueue("Work");
        m.UpdateQueue(games.Id, q => { q.Schedule = Tuesday(); q.AllowParallel = true; }); m.UpdateQueue(work.Id, q => q.AllowParallel = true);
        string[] main = { "Setup.exe", "Notes.pdf" }, gp = { "Part_1.zip", "Part_2.zip", "Part_3.zip" };
        var mainIds = main.Select(n => Sync(m.AddPausedAsync(new DownloadRequest(slow.BaseUrl + "/" + n, n))).Id).ToArray();
        var gIds = gp.Select(n => Sync(m.AddPausedAsync(new DownloadRequest(slow.BaseUrl + "/" + n, n, QueueId: games.Id))).Id).ToArray();
        var wId = Sync(m.AddPausedAsync(new DownloadRequest(slow.BaseUrl + "/report.docx", "report.docx", QueueId: work.Id))).Id;
        var live = Sync(m.AddAsync(new DownloadRequest(slow.BaseUrl + "/live.bin", "live.bin", QueueId: work.Id))); PumpUntil(() => Job(m, live.Id).Status == "Downloading", "a download runs in Work");
        var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "side", "files"), ShowCompletionDialog = false, ShowProgressOnCompletion = false };
        var main1 = new MainWindow(m, settings, () => { }, enableTray: false) { ShowInTaskbar = false, Width = 1100, Height = 640, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10, QueueClock = () => D(16, 12, 0) };
        main1.Show(); Pump(900); var nav = Nav(main1);
        var tags = nav.Items.OfType<ListBoxItem>().Select(i => i.Tag as string).ToList();
        int header = tags.IndexOf("qheader"), torrents = tags.IndexOf("header");
        Check(header > tags.IndexOf("Scheduled") && (torrents < 0 || header < torrents), "a Queues section sits below the status filters");
        Check(main1.QueueItem(QueueIds.Main) is { IsExpanded: true } && main1.QueueItem(games.Id) is not null && main1.QueueItem(work.Id) is not null && tags.Contains(MainWindow.QueueNewTag) && tags.Last(t => t is not null && !t.StartsWith("T:")) == MainWindow.QueueNewTag, "a node per queue and a + New queue entry at the end of the section");
        Check(TextOf(main1.QueueItem(QueueIds.Main)!).Contains("Main queue") && TextOf(main1.QueueItem(QueueIds.Main)!).Contains("2"), "the Main queue node shows its name and count (2)");
        Check(TextOf(main1.QueueItem(games.Id)!).Contains("Starts Tuesday at") && TextOf(main1.QueueItem(games.Id)!).Contains("3"), "a scheduled queue shows a clock line \"Starts Tuesday at 23:00\" and its count");
        Check(!TextOf(main1.QueueItem(work.Id)!).Contains("Starts"), "an unscheduled queue shows no schedule text");
        Check(gIds.All(id => main1.QueueChildItem(id) is not null) && tags.IndexOf("QD:" + games.Id + ":" + gIds[0]) == tags.IndexOf("Q:" + games.Id) + 1, "expanded queues list their downloads right under the node, in run order");
        var liveChild = main1.QueueChildItem(live.Id)!; Check(TextOf(liveChild).Contains("live.bin") && TextOf(liveChild).Contains("%") || TextOf(liveChild).Contains("live.bin"), "a child shows the file name (and the percentage while downloading)");
        Check(System.Windows.Automation.AutomationProperties.GetName(main1.QueueItem(games.Id)!).Contains("Games pack") && System.Windows.Automation.AutomationProperties.GetName(main1.QueueItem(games.Id)!).Contains("3"), "queue nodes carry an accessible name with the count");
        main1.ScrollQueuesIntoView(); Pump(300); SaveShot(main1, "sidebar-light.png");
        // collapse / expand
        main1.QueueItem(games.Id)!.IsExpanded = false; Pump(200); Check(gIds.All(id => main1.QueueChildItem(id) is null), "collapsing a queue hides its downloads");
        main1.QueueItem(games.Id)!.IsExpanded = true; Pump(200); Check(gIds.All(id => main1.QueueChildItem(id) is not null), "expanding shows them again");
        // clicking a queue filters the list
        main1.SelectQueue(games.Id); Pump(400);
        Check(main1.ActiveQueue == games.Id && VisibleIds(main1).SequenceEqual(gIds), "clicking a queue shows only its downloads, in run order");
        Check(((TextBlock)main1.FindName("Heading")).Text == "Queue: Games pack", "the heading names the queue");
        // drop table
        Check(MainWindow.DropTarget("Q:abc") is ("abc", null) && MainWindow.DropTarget("QD:abc:j1") is ("abc", "j1") && MainWindow.DropTarget("All") is null && MainWindow.DropTarget(null) is null, "drop targets: a queue node (end), a queue child (before it), nothing else");
        // drag inside the queue: Part_3 before Part_1
        main1.MoveToQueue(new[] { gIds[2] }, games.Id, gIds[0]); Pump(500);
        Check(m.OrderOf(games.Id).SequenceEqual(new[] { gIds[2], gIds[0], gIds[1] }) && VisibleIds(main1).SequenceEqual(new[] { gIds[2], gIds[0], gIds[1] }), "dragging inside a queue sets the run order, in the engine and in the list");
        var childTags = Nav(main1).Items.OfType<ListBoxItem>().Select(i => i.Tag as string).Where(t => t!.StartsWith("QD:" + games.Id)).ToArray();
        Check(childTags[0]!.EndsWith(gIds[2]), "and in the sidebar");
        // drag onto another queue
        main1.MoveToQueue(new[] { mainIds[0] }, work.Id); Pump(500);
        Check(Job(m, mainIds[0]).QueueId == work.Id && TextOf(main1.QueueItem(QueueIds.Main)!).Contains("1") && TextOf(main1.QueueItem(work.Id)!).Contains("3"), "dropping a download on a queue moves it; both counts follow");
        // the row menu has Move to queue
        main1.SelectQueue(QueueIds.Main); Pump(300);
        var list = List(main1); list.SelectedItems.Clear(); list.SelectedItem = main1.Rows.First(r => r.Id == mainIds[1]); Pump(150);
        Check(main1.UpdateRowMenu(), "the row menu opens for a selected download");
        var move = (MenuItem)main1.FindName("CtxMoveQueue"); Check(move.Items.Count == 3 && Entry(move, "Work") is not null && Entry(move, "Games pack") is not null, "Move to queue lists every queue");
        Entry(move, "Games pack")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump(300);
        Check(Job(m, mainIds[1]).QueueId == games.Id, "Move to queue in the row menu moves the selected download");
        var bulk = (Button)main1.FindName("BulkMoveQueue"); Check(bulk.Content as string == "Move to queue", "the bulk bar has Move to queue");
        // the queue menu
        var menu = main1.QueueItem(QueueIds.Main)!.ContextMenu!; Check(Entry(menu, "Delete queue…")!.IsEnabled == false && Entry(menu, "Move up")!.IsEnabled == false && Entry(menu, "Edit queue…")!.IsEnabled, "the Main queue menu: edit yes, delete no, first so no move up");
        // rename through the editor
        main1.QueueEditorCreated = ed => ed.Loaded += (_, _) => { ed.NameField.Text = "Office"; Click(ed.Save); };
        Check(main1.EditQueue(work.Id) && m.Queues.Single(q => q.Id == work.Id).Name == "Office" && TextOf(main1.QueueItem(work.Id)!).Contains("Office"), "rename (through Edit queue) updates the sidebar");
        // reorder queues
        main1.MoveQueueBy(work.Id, -1); Pump(200); Check(m.Queues.Select(q => q.Id).SequenceEqual(new[] { QueueIds.Main, work.Id, games.Id }), "queues can be moved up and down");
        main1.MoveQueueBy(work.Id, 1);
        // + New queue: cancel removes it, save keeps it
        int before = m.Queues.Count; main1.QueueEditorCreated = ed => ed.Loaded += (_, _) => ed.Close();
        Check(main1.NewQueue() is null && m.Queues.Count == before, "+ New queue then Cancel leaves no queue behind");
        main1.QueueEditorCreated = ed => ed.Loaded += (_, _) => { ed.NameField.Text = "Backups"; Click(ed.Save); };
        var created = main1.NewQueue(); Check(created is not null && m.Queues.Any(q => q.Name == "Backups") && main1.ActiveQueue == created, "+ New queue then Save creates and selects the queue");
        // delete: move / remove / cancel
        var del = m.CreateQueue("Temp"); var dj = Sync(m.AddPausedAsync(new DownloadRequest(slow.BaseUrl + "/t1", "t1.bin", QueueId: del.Id)));
        string? asked = null; ChoiceWindow.Override = (_, title, message, labels) => { asked = message; return 99; };
        try
        {
            ChoiceWindow.Override = (_, _, message, labels) => { asked = message; return -1; };
            Check(!main1.DeleteQueue(del.Id) && m.Queues.Any(q => q.Id == del.Id) && asked!.Contains("Temp") && asked.Contains("1"), "delete asks first; cancelling keeps the queue");
            ChoiceWindow.Override = (_, _, _, labels) => labels[0] == "Move them to the Main queue" && labels[1] == "Remove them from the list" ? 0 : -1;
            Check(main1.DeleteQueue(del.Id) && Job(m, dj.Id).QueueId == QueueIds.Main, "delete > move: the downloads go to the Main queue");
            var del2 = m.CreateQueue("Temp2"); var dj2 = Sync(m.AddPausedAsync(new DownloadRequest(slow.BaseUrl + "/t2", "t2.bin", QueueId: del2.Id)));
            ChoiceWindow.Override = (_, _, _, _) => 1;
            Check(main1.DeleteQueue(del2.Id) && m.Jobs.All(j => j.Id != dj2.Id), "delete > remove: the downloads leave the list");
            Check(!main1.DeleteQueue(QueueIds.Main), "the Main queue cannot be deleted");
        }
        finally { ChoiceWindow.Override = null; }
        // the Scheduler button opens the editor of the selected download's queue
        string? opened = null; main1.QueueEditorCreated = ed => ed.Loaded += (_, _) => { opened = ed.NameField.Text; ed.Close(); };
        main1.SelectQueue(games.Id); Pump(300); list.SelectedItem = main1.Rows.First(r => r.Id == gIds[0]); Pump(150);
        Click((Button)main1.FindName("ScheduleButton")); Pump(300);
        Check(opened == "Games pack", "the old Scheduler button now opens the queue editor of the selected download's queue");
        // keyboard: F2 on a queue node edits it
        opened = null; var node = main1.QueueItem(games.Id)!; node.Focus(); Pump(100);
        Nav(main1).RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(main1) ?? throw new Exception("no source"), 0, Key.F2) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        Pump(300); Check(opened == "Games pack", "F2 on a focused queue node opens Edit queue");
        // engine order picks the next download by queue position
        // (see Limits and RunTogether for the engine side)
        // looks: themes, RTL, narrow
        foreach (var theme in new[] { "DeepNavy", "Dark" })
        {
            ThemeManager.Apply(Application.Current, theme, null); Pump(300); main1.SelectQueue(games.Id); Pump(300); main1.ScrollQueuesIntoView(); Pump(300); SaveShot(main1, "sidebar-" + theme.ToLowerInvariant() + ".png");
        }
        ThemeManager.Apply(Application.Current, "Light", null); Pump(200);
        main1.FlowDirection = FlowDirection.RightToLeft; Loc.SetLanguage("ar"); Pump(500); main1.QueueItem(games.Id)!.IsExpanded = true; main1.SelectQueue(games.Id); Pump(400);
        main1.ScrollQueuesIntoView(); Pump(300); SaveShot(main1, "sidebar-rtl-ar.png"); Check(main1.QueueItem(games.Id)!.FlowDirection == FlowDirection.RightToLeft, "RTL: the queue tree mirrors");
        Loc.SetLanguage("en"); main1.FlowDirection = FlowDirection.LeftToRight; Pump(300);
        main1.Width = 900; main1.Height = 480; Pump(500); main1.SelectQueue(games.Id); main1.ScrollQueuesIntoView(); Pump(300); SaveShot(main1, "sidebar-narrow.png");
        Check(main1.QueueItem(games.Id)!.ActualWidth > 100 && main1.QueueItem(games.Id)!.ActualWidth <= nav.ActualWidth + 1, "narrow window: the queue node fits the sidebar");
        main1.Close(); Pump(200);
    }

    // ---------------------------------------------------------------- the editor
    private static void Editor(string root)
    {
        using var m = NewManager(root, "edit", 2);
        var q = m.CreateQueue("Games pack"); m.CreateQueue("Work");
        var ed = new QueueEditorWindow(m, q.Id, 2) { Now = () => D(16, 12, 0), Left = 10, Top = 10, WindowStartupLocation = WindowStartupLocation.Manual }; ed.Show(); Pump(400);
        Check(ed.NameField.Text == "Games pack" && ed.LimitField.Text == "2" && !ed.ScheduleCheck.IsChecked!.Value && !ed.StartPicker.IsEnabled || !ed.ScheduleCheck.IsChecked.Value, "the editor starts from the global number and a switched-off schedule");
        Check(ed.DayButtons.Count == 7 && ((DayOfWeek)ed.DayButtons[0].Tag) == DayOfWeek.Monday && ((DayOfWeek)ed.DayButtons[6].Tag) == DayOfWeek.Sunday, "seven weekday pills, Monday first");
        Check(ed.WhenDoneCombo.Items.Count == 5 && ed.WhenDoneCombo.SelectedIndex == 0, "When done: Do nothing, Sleep, Hibernate, Shut down, Restart");
        // validation
        ed.NameField.Text = " "; Click(ed.Save); Check(ed.Error.Text == Loc.T("queue.editor.err_name") && ed.IsVisible, "an empty name is refused inline");
        ed.NameField.Text = "work"; Click(ed.Save); Check(ed.Error.Text == Loc.T("queue.editor.err_dup"), "a name that exists is refused inline");
        ed.NameField.Text = "Games pack"; ed.LimitField.Text = "0"; Click(ed.Save); Check(ed.Error.Text == Loc.T("queue.editor.err_limit"), "downloads at once 0 is refused");
        ed.LimitField.Text = "17"; Click(ed.Save); Check(ed.Error.Text == Loc.T("queue.editor.err_limit"), "downloads at once 17 is refused");
        ed.LimitField.Text = "1"; ed.ScheduleCheck.IsChecked = true; Pump(100);
        Click(ed.Save); Check(ed.Error.Text == Loc.T("queue.editor.err_days") && ed.IsVisible, "a schedule with no day is refused");
        ed.DayButtons[1].IsChecked = true; ed.StartPicker.Value = D(16, 23, 0); ed.StopCheck.IsChecked = true; ed.StopPicker.Value = D(16, 6, 0); Pump(150);
        Check(ed.SummaryText.Text.StartsWith("Starts Tuesday at") && ed.SummaryText.Text.Contains("stops at"), "the summary line says when it starts and stops: " + ed.SummaryText.Text);
        ed.StopPicker.Value = D(16, 23, 0); Click(ed.Save); Check(ed.Error.Text == Loc.T("queue.editor.err_stop"), "a stop time equal to the start is refused");
        ed.StopPicker.Value = D(16, 6, 0); ed.ParallelCheck.IsChecked = true; ed.WhenDoneCombo.SelectedIndex = 3; SaveShot(ed, "editor-light.png");
        Click(ed.Save); Pump(200);
        var saved = m.Queues.Single(x => x.Id == q.Id);
        Check(!ed.IsVisible && saved.Limit == 1 && saved.AllowParallel && saved.WhenDone == "Shutdown" && saved.Schedule is { Enabled: true, DaysMask: 2 } s && s.StartTime == new TimeSpan(23, 0, 0) && s.StopTime == new TimeSpan(6, 0, 0), "Save stores name, number, run-together, power action and the schedule");
        // reopen: shows what was saved
        var again = new QueueEditorWindow(m, q.Id, 2) { Now = () => D(16, 12, 0) }; again.Show(); Pump(300);
        Check(again.LimitField.Text == "1" && again.ScheduleCheck.IsChecked == true && again.DayButtons[1].IsChecked == true && again.DayButtons[0].IsChecked != true && again.StopCheck.IsChecked == true && again.ParallelCheck.IsChecked == true && again.WhenDoneCombo.SelectedIndex == 3, "reopening shows the saved values");
        again.ScheduleCheck.IsChecked = false; Pump(100); Check(!again.SchedulePanelEnabled, "switching the schedule off disables its fields");
        // one date in the past is refused, a future one saved
        again.ScheduleCheck.IsChecked = true; again.OneOffCheck.IsChecked = true; again.StartPicker.Value = D(10, 8, 0); Click(again.Save); Check(again.Error.Text == Loc.T("validation.datetime_future"), "a one-off date in the past is refused");
        again.StartPicker.Value = D(20, 8, 0); again.StopCheck.IsChecked = false; Click(again.Save); Pump(200);
        Check(m.Queues.Single(x => x.Id == q.Id).Schedule is { OneOffDate: { } d } && d == D(20, 0, 0), "a future one-off date is saved");
        // the Main queue keeps its localized name unless renamed
        var mainEd = new QueueEditorWindow(m, QueueIds.Main, 2); mainEd.Show(); Pump(200); Check(mainEd.NameField.Text == "Main queue", "the Main queue is shown by its (localized) name"); mainEd.Close();
        // looks
        foreach (var theme in new[] { "DeepNavy", "Dark", "Light" })
        {
            ThemeManager.Apply(Application.Current, theme, null); Pump(300);
            var w = new QueueEditorWindow(m, q.Id, 2) { Now = () => D(16, 12, 0), Left = 10, Top = 10, WindowStartupLocation = WindowStartupLocation.Manual }; w.ScheduleCheck.IsChecked = true; w.Show(); Pump(500); SaveShot(w, "editor-" + theme.ToLowerInvariant() + ".png"); w.Close();
        }
        var rtl = new QueueEditorWindow(m, q.Id, 2) { Now = () => D(16, 12, 0), Left = 10, Top = 10, WindowStartupLocation = WindowStartupLocation.Manual }; rtl.Show(); Loc.SetLanguage("ar"); rtl.FlowDirection = FlowDirection.RightToLeft; Pump(500);
        SaveShot(rtl, "editor-rtl-ar.png"); Check(rtl.NameField.FlowDirection == FlowDirection.RightToLeft, "RTL: the editor mirrors"); rtl.Close(); Loc.SetLanguage("en"); Pump(200);
        var narrow = new QueueEditorWindow(m, q.Id, 2) { Width = 380, Height = 420, Left = 10, Top = 10, WindowStartupLocation = WindowStartupLocation.Manual, Now = () => D(16, 12, 0) }; narrow.ScheduleCheck.IsChecked = true; narrow.Show(); Pump(500);
        SaveShot(narrow, "editor-narrow.png"); Check(narrow.Save.IsVisible && narrow.Save.ActualWidth > 40, "narrow: the Save button stays reachable"); narrow.Close();
        foreach (var lang in new[] { "de", "ru", "ja", "tr" }) { Loc.SetLanguage(lang); var w = new QueueEditorWindow(m, q.Id, 2) { Left = 10, Top = 10, WindowStartupLocation = WindowStartupLocation.Manual }; w.ScheduleCheck.IsChecked = true; w.Show(); Pump(400); SaveShot(w, "editor-" + lang + ".png"); w.Close(); }
        Loc.SetLanguage("en"); again.Close(); Pump(100);
    }
}
