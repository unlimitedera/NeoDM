using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NDM.App.Localization;
using System.Windows.Threading;
using NDM.App;
using NDM.Core;
using NDM.Tests;

// Same isolation as the other WPF suites: the real App would take the machine-wide mutex and open the user's live queue.
internal sealed class TestApp : NDM.App.App
{
    public TestApp()
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Light.xaml") });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Controls.xaml") });
    }
    protected override void OnStartup(StartupEventArgs e) { }
}

/// <summary>Remove dialog, bulk remove, context-menu rules and actions, properties, proxy options tab, and the two build variants.</summary>
internal static partial class ManageTests
{
    private static int passed;
    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception("FAIL: " + name);
        passed++; Console.WriteLine("PASS: " + name);
    }
    private static void Pump(int ms = 200)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void PumpUntil(Func<bool> condition, string what, int seconds = 20)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition()) { if (DateTime.UtcNow > end) throw new TimeoutException(what); Pump(50); }
    }
    private static T Field<T>(FrameworkElement owner, string name) where T : class => (T)(owner.FindName(name) ?? throw new Exception("Missing control " + name));
    private static void Click(FrameworkElement owner, string name) => Field<Button>(owner, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static object? Call(object target, string method, params object?[] args) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(target, args.Length == 0 ? new object?[] { null, new RoutedEventArgs() } : args);
    private static DownloadManager Manager(string root, string label, ProxyOptions? proxy = null) =>
        new(Path.Combine(root, label, "files"), Path.Combine(root, label, "state"), new DownloadOptions { RetryCount = 0, RequestTimeout = TimeSpan.FromSeconds(4), MinimumSegmentSize = 65536, Proxy = proxy });
    // Like the real app, continuations return to the UI thread; so waiting must keep the dispatcher pumping.
    private static T Sync<T>(Task<T> task) { PumpUntil(() => task.IsCompleted, "async operation"); return task.GetAwaiter().GetResult(); }
    private static void Sync(Task task) { PumpUntil(() => task.IsCompleted, "async operation"); task.GetAwaiter().GetResult(); }
    private static async Task<DownloadJob> Done(DownloadManager m, string url, string name)
    {
        var job = await m.AddAsync(new DownloadRequest(url, name)).ConfigureAwait(false);
        var end = DateTime.UtcNow.AddSeconds(20);
        while (m.Jobs.Single(j => j.Id == job.Id).Status is not ("Completed" or "Failed")) { if (DateTime.UtcNow > end) throw new TimeoutException(); await Task.Delay(20).ConfigureAwait(false); }
        return m.Jobs.Single(j => j.Id == job.Id);
    }

    /// <summary>Auto-answers the remove dialog (it is modal); records what it showed.</summary>
    private sealed class DialogScript
    {
        public bool ClickRemove = true;
        public bool? SetDelete;
        public bool SawDeleteChecked, SawDefaultUnchecked, SawWarningVisible;
        public string Heading = "", ButtonText = "";
        public int Shown;
        public void Install() => EventManager.RegisterClassHandler(typeof(RemoveConfirmWindow), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
        {
            var w = (RemoveConfirmWindow)s; Shown++;
            Heading = Field<TextBlock>(w, "Heading").Text;
            var box = Field<CheckBox>(w, "DeleteFilesBox");
            SawDefaultUnchecked = box.IsChecked != true; SawDeleteChecked = box.IsChecked == true;
            if (SetDelete is { } d) box.IsChecked = d;
            SawWarningVisible = Field<TextBlock>(w, "DeleteWarning").Visibility == Visibility.Visible;
            ButtonText = (string)Field<Button>(w, "RemoveButton").Content;
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => Click(w, ClickRemove ? "RemoveButton" : "CancelButton")));
        }));
    }

    [STAThread]
    public static int Main()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", "NDM-ManageTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack; var app = new TestApp();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var script = new DialogScript(); script.Install();
        try
        {
            using var server = new FixtureServer();
            RemoveDialogAndActions(root, server, script);
            BulkAndWindowRemove(root, server, script);
            ContextMenu(root, server, script);
            Properties(root, server);
            ProgressWindowTests(root, server, script);
            CaptureWindowTests(root, server);
            SaveFolderTests(root, server);
            CompletionWindowTests(root, server);
            ChangeLocationTests(root, server);
            ProxyOptionsTab(root, server);
            Variant(root, server);
            Console.WriteLine($"ALL MANAGE TESTS PASSED ({passed})");
            return 0;
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // ------------------------------------------------------------------ 1. remove dialog + executor
    private static void RemoveDialogAndActions(string root, FixtureServer server, DialogScript script)
    {
        using var m = Manager(root, "remove");
        var a = Sync(Done(m, server.BaseUrl + "/empty", "a.bin")); var b = Sync(Done(m, server.BaseUrl + "/empty", "b.bin"));
        var rowA = new DownloadRow(a); var rowB = new DownloadRow(b);
        var items = new[] { RemoveActions.Describe(rowA), RemoveActions.Describe(rowB) };
        Check(items.All(i => i.DiskFiles == 1 && !i.IsTorrent && i.Detail.EndsWith(".bin")), "Describe counts existing files and shows the path");
        var dialog = new RemoveConfirmWindow(items);
        Check(Field<CheckBox>(dialog, "DeleteFilesBox").IsChecked != true && Field<TextBlock>(dialog, "DeleteWarning").Visibility == Visibility.Collapsed, "Delete-from-disk checkbox is off by default");
        Check(Field<TextBlock>(dialog, "Heading").Text == "Remove 2 downloads?" && Field<ItemsControl>(dialog, "ItemList").Items.Count == 2, "Dialog lists every item");
        Check((string)Field<Button>(dialog, "RemoveButton").Content == "_Remove" && Field<Button>(dialog, "RemoveButton").IsDefault, "Remove is the default button while the option is off");
        Field<CheckBox>(dialog, "DeleteFilesBox").IsChecked = true;
        Check(Field<TextBlock>(dialog, "DeleteWarning").Text.Contains("2 files will be permanently deleted") && !Field<Button>(dialog, "RemoveButton").IsDefault && Field<Button>(dialog, "CancelButton").IsDefault, "Ticking the box warns with the file count and moves the default to Cancel");
        Check(dialog.DeleteFiles, "DeleteFiles reflects the checkbox");
        // cancel path through the real modal dialog
        script.ClickRemove = false; script.SetDelete = true;
        var owner = new Window { ShowInTaskbar = false, Width = 10, Height = 10 };
        var d2 = new RemoveConfirmWindow(items, true) { Owner = null };
        Check(d2.ShowDialog() == false && File.Exists(a.FilePath), "Cancel leaves everything untouched");
        // executor semantics
        var s1 = Sync(RemoveActions.ExecuteAsync(m, new NoTorrentFeature(), new[] { rowA }, deleteFiles: false));
        Check(s1.Removed == 1 && s1.FilesDeleted == 0 && File.Exists(a.FilePath) && m.Jobs.Count == 1 && s1.Describe(false).Contains("kept"), "Remove without delete keeps the file: " + s1.Describe(false));
        File.Delete(b.FilePath);
        var missing = RemoveActions.Describe(rowB);
        Check(missing.DiskFiles == 0 && missing.Detail.StartsWith("File not found on disk"), "A missing file is called out in the dialog");
        var s2 = Sync(RemoveActions.ExecuteAsync(m, new NoTorrentFeature(), new[] { rowB }, deleteFiles: true));
        Check(s2.Removed == 1 && s2.FilesMissing == 1 && !s2.NeedsAttention && s2.Describe(true).Contains("already gone"), "Missing file is not an error: " + s2.Describe(true));
        var c = Sync(Done(m, server.BaseUrl + "/empty", "c.bin")); var rowC = new DownloadRow(c);
        RemoveSummary s3;
        using (new FileStream(c.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            s3 = Sync(RemoveActions.ExecuteAsync(m, new NoTorrentFeature(), new[] { rowC }, deleteFiles: true));
        Check(s3.Removed == 1 && s3.FilesFailed == 1 && s3.NeedsAttention && s3.Problems.Count == 1 && File.Exists(c.FilePath) && s3.Describe(true).Contains("could not be deleted"), "A locked file is reported clearly and kept: " + s3.Describe(true));
        var outside = Path.Combine(root, "outside.txt"); File.WriteAllText(outside, "mine");
        var d = Sync(Done(m, server.BaseUrl + "/empty", "d.bin"));
        Sync(RemoveActions.ExecuteAsync(m, new NoTorrentFeature(), new[] { new DownloadRow(d) }, true));
        Check(File.Exists(outside) && !File.Exists(d.FilePath), "Nothing outside the job's own file is deleted");
        owner.Close();
    }

    // ------------------------------------------------------------------ 2. real window: Remove, Delete key, Remove completed
    private static void BulkAndWindowRemove(string root, FixtureServer server, DialogScript script)
    {
        script.ClickRemove = true; script.SetDelete = null;
        using var m = Manager(root, "window");
        var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "window", "files") };
        var window = new MainWindow(m, settings, () => { }, enableTray: false) { ShowInTaskbar = false };
        try
        {
            window.Show(); Pump();
            var done = new List<DownloadJob>();
            foreach (var n in new[] { "one.bin", "two.bin", "three.bin" }) done.Add(Done(m, server.BaseUrl + "/empty", n).GetAwaiter().GetResult());
            Pump(800);
            var list = Field<ListView>(window, "DownloadList");
            Check(window.Rows.Count == 3, "Window shows the completed jobs");
            // Remove button, default = keep files
            list.SelectedItems.Clear(); list.SelectedItems.Add(window.Rows.First(r => r.FileName == "one.bin"));
            int shown = script.Shown; script.SetDelete = null;
            Click(window, "RemoveButton"); PumpUntil(() => m.Jobs.Count == 2, "Remove button");
            Check(script.Shown == shown + 1 && script.SawDefaultUnchecked && File.Exists(done[0].FilePath), "Toolbar Remove uses the dialog and keeps the file by default");
            // Delete key with the box ticked
            list.SelectedItems.Clear(); list.SelectedItems.Add(window.Rows.First(r => r.FileName == "two.bin")); list.Focus();
            script.SetDelete = true;
            Call(window, "Remove_Click");
            PumpUntil(() => m.Jobs.Count == 1, "Delete");
            Check(script.SawDefaultUnchecked && script.SawWarningVisible && !File.Exists(done[1].FilePath), "Remove with the box ticked deletes the file");
            // context 'Delete from disk…' opens with the box pre-ticked; Cancel keeps everything
            list.SelectedItems.Clear(); list.SelectedItems.Add(window.Rows.First(r => r.FileName == "three.bin"));
            script.ClickRemove = false; script.SetDelete = null;
            Call(window, "ContextDelete_Click"); Pump(500);
            Check(script.SawDeleteChecked && m.Jobs.Count == 1 && File.Exists(done[2].FilePath), "Delete from disk… pre-ticks the box and Cancel changes nothing");
            // bulk: Remove completed uses the same dialog
            script.ClickRemove = true; script.SetDelete = true; int before = script.Shown;
            Call(window, "RemoveCompleted_Click"); PumpUntil(() => m.Jobs.Count == 0, "Remove completed");
            Check(script.Shown == before + 1 && script.Heading == "Remove this download?" && !File.Exists(done[2].FilePath), "Remove completed goes through the same dialog (and can delete files)");
            var extra = new[] { "x.bin", "y.bin" }.Select(n => Done(m, server.BaseUrl + "/empty", n).GetAwaiter().GetResult()).ToArray(); Pump(800);
            script.SetDelete = false; before = script.Shown;
            Call(window, "RemoveCompleted_Click"); PumpUntil(() => m.Jobs.Count == 0, "Remove completed (keep)");
            Check(script.Heading == "Remove 2 downloads?" && extra.All(e => File.Exists(e.FilePath)), "Bulk removal lists all and keeps files when the box stays off");
            Check(window.FindName("Notice") is TextBlock { Text: var notice } && notice.Contains("2 removed"), "Status line summarises the result");
        }
        finally { window.Close(); }
    }

    // ------------------------------------------------------------------ 3. context menu
    private static void ContextMenu(string root, FixtureServer server, DialogScript script)
    {
        using var m = Manager(root, "menu");
        var completed = Done(m, server.BaseUrl + "/range?ctx=1", "menu.bin").GetAwaiter().GetResult();
        var failed = m.AddAsync(new DownloadRequest(server.BaseUrl + "/fail", "bad.bin")).GetAwaiter().GetResult();
        var end = DateTime.UtcNow.AddSeconds(15); while (m.Jobs.Single(j => j.Id == failed.Id).Status != "Failed" && DateTime.UtcNow < end) Thread.Sleep(20);
        var later = m.AddScheduledAsync(new DownloadRequest(server.BaseUrl + "/range", "later.bin", Referer: server.BaseUrl + "/ref"), DateTimeOffset.UtcNow.AddHours(2)).GetAwaiter().GetResult();
        var rows = m.Jobs.Select(j => new DownloadRow(j)).ToDictionary(r => r.FileName);
        var cRow = rows["menu.bin"]; var fRow = rows["bad.bin"]; var lRow = rows["later.bin"];
        Check(cRow.Status == "Completed" && fRow.Status == "Failed" && lRow.Status == "Scheduled", "Fixture rows: completed, failed, scheduled");
        var present = RowMenu.For(new[] { cRow });
        Check(present is { Open: true, OpenWith: true, OpenFolder: true, CopyUrl: true, CopyPath: true, MoveRename: true, Properties: true, Redownload: true, Remove: true, DeleteFromDisk: true }, "Completed file present: open/open with/folder/copy/move/properties/redownload/remove/delete enabled");
        Check(present is { Resume: false, Stop: false, RefreshAddress: false, SpeedLimit: false, Schedule: false, CopyMagnet: false, TorrentDetails: false }, "Completed file: resume/stop/refresh/limit/schedule/torrent items disabled");
        var gone = RowMenu.For(new[] { cRow }, _ => false, _ => false);
        Check(gone is { Open: false, OpenWith: false, OpenFolder: false, MoveRename: false, DeleteFromDisk: false, Remove: true, CopyUrl: true, Redownload: true }, "Missing file disables open/move/delete-from-disk but keeps remove, copy URL and redownload");
        var fail = RowMenu.For(new[] { fRow });
        Check(fail is { Resume: true, Stop: false, RefreshAddress: true, Redownload: true, SpeedLimit: true, Open: false, MoveRename: false }, "Failed: resume, refresh address, redownload; no open/move");
        var sched = RowMenu.For(new[] { lRow });
        Check(sched is { Stop: true, Resume: false, RefreshAddress: true, SpeedLimit: true, Schedule: true, Redownload: false }, "Scheduled: stop, refresh, limit, schedule; no redownload while queued");
        var multi = RowMenu.For(new[] { cRow, fRow });
        Check(multi is { Open: false, MoveRename: false, Properties: false, RefreshAddress: false, SpeedLimit: false, CopyUrl: true, CopyPath: true, Resume: true, Redownload: true, Remove: true }, "Multi-selection: single-item commands off; copy, resume, redownload, remove stay");
        Check(RowMenu.For(Array.Empty<DownloadRow>()) == default, "Empty selection disables everything");

        // wired window: menu items exist, handlers work
        var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "menu", "files") };
        var window = new MainWindow(m, settings, () => { }, enableTray: false) { ShowInTaskbar = false };
        try
        {
            window.Show(); Pump(800);
            foreach (var name in new[] { "CtxOpen", "CtxOpenWith", "CtxFolder", "CtxCopyUrl", "CtxCopyPath", "CtxCopyError", "CtxMove", "CtxProperties", "CtxResume", "CtxStop", "CtxRefreshUrl", "CtxRedownload", "CtxSpeed", "CtxSchedule", "CtxRemove", "CtxDelete" })
                Check(window.FindName(name) is MenuItem, "Context menu has " + name);
            var list = Field<ListView>(window, "DownloadList");
            list.SelectedItems.Clear(); foreach (var r in window.Rows.Where(r => r.FileName is "menu.bin" or "bad.bin")) list.SelectedItems.Add(r);
            Pump(800);
            Check(list.SelectedItems.Count == 2, "Multi-selection survives the 500 ms refresh tick");
            // Copy URL (multi) + Copy path
            Call(window, "ContextCopyUrl_Click");
            string clip = ClipText();
            Check(clip.Contains(server.BaseUrl + "/range?ctx=1") && clip.Contains(server.BaseUrl + "/fail"), "Copy URL copies every selected address");
            list.SelectedItems.Clear(); list.SelectedItems.Add(window.Rows.First(r => r.FileName == "menu.bin")); Pump(100);
            Call(window, "ContextCopyPath_Click");
            Check(ClipText() == completed.FilePath, "Copy file path copies the saved path");
            // 1.5.8: copy error message (failed rows only), copy link by Ctrl+C (the Copy command of the list), a clipboard that stays busy never crashes
            Check(present.CopyError == false && fail.CopyError && sched.CopyError == false && multi.CopyError && RowMenu.For(Array.Empty<DownloadRow>()).CopyError == false, "Copy error message is enabled only when a failed row is selected");
            Check(RowMenu.ErrorText(new[] { cRow }) is null && RowMenu.ErrorText(new[] { fRow }) == fRow.Error && fRow.Error is { Length: > 0 } && RowMenu.ErrorText(new[] { cRow, fRow }) == fRow.Error, "The error text is the message of the failed rows, nothing for others");
            list.SelectedItems.Clear(); list.SelectedItems.Add(window.Rows.First(r => r.FileName == "bad.bin")); Pump(100);
            Check(window.FindName("CtxCopyError") is MenuItem { IsEnabled: true }, "The menu entry is enabled for a failed download after the menu opened");
            window.UpdateRowMenu();
            Check(((MenuItem)window.FindName("CtxCopyError")).IsEnabled, "Copy error message is enabled in the opened menu for a failed row");
            Call(window, "ContextCopyError_Click");
            Check(ClipText() == window.Rows.First(r => r.FileName == "bad.bin").Error && ClipText().Length > 0, "Copy error message copies the error text of the failed download");
            list.SelectedItems.Clear(); list.SelectedItems.Add(window.Rows.First(r => r.FileName == "menu.bin")); Pump(100); window.UpdateRowMenu();
            Check(!((MenuItem)window.FindName("CtxCopyError")).IsEnabled && ((MenuItem)window.FindName("CtxCopyUrl")).IsEnabled && ((MenuItem)window.FindName("CtxCopyPath")).IsEnabled, "For a completed download only link and path can be copied");
            Clipboard.SetText("sentinel"); Check(ApplicationCommands.Copy.CanExecute(null, list), "Ctrl+C (Copy command) can run with a download selected");
            ApplicationCommands.Copy.Execute(null, list); Check(ClipText() == server.BaseUrl + "/range?ctx=1", "Ctrl+C on the list copies the link of the selected download");
            list.SelectedItems.Clear(); Pump(60); Check(!ApplicationCommands.Copy.CanExecute(null, list), "With nothing selected Ctrl+C does nothing");
            list.SelectedItems.Add(window.Rows.First(r => r.FileName == "menu.bin")); Pump(100);
            int tries = 0; SafeClipboard.Write = _ => { tries++; throw new System.Runtime.InteropServices.COMException("busy", unchecked((int)0x800401D0)); };
            try
            {
                Call(window, "ContextCopyUrl_Click");
                Check(tries == 2 && Field<TextBlock>(window, "Notice").Text == Loc.T("main.notice.copy_failed"), "A clipboard that stays busy is tried twice, never crashes and the notice says so");
                int calls = 0; SafeClipboard.Write = t => { if (++calls == 1) throw new System.Runtime.InteropServices.COMException("busy", unchecked((int)0x800401D0)); Clipboard.SetText(t); };
                Check(SafeClipboard.TrySetText("second try works") && calls == 2 && ClipText() == "second try works", "A busy clipboard that frees up is written on the second try");
                Check(!SafeClipboard.TrySetText("") && !SafeClipboard.TrySetText(null), "Empty text is not written");
            }
            finally { SafeClipboard.Write = text => Clipboard.SetText(text); }
            // Redownload completed: old file kept, unique new name
            Call(window, "ContextRedownload_Click");
            PumpUntil(() => m.Jobs.Single(j => j.Id == completed.Id) is { Status: "Completed", FileName: "menu (1).bin" }, "Redownload completes");
            Check(File.Exists(completed.FilePath) && File.Exists(m.Jobs.Single(j => j.Id == completed.Id).FilePath), "Redownload keeps the first file and creates a second one");
            // Refresh address via the prompt (auto-filled)
            EventManager.RegisterClassHandler(typeof(TextPromptWindow), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
            {
                var w = (TextPromptWindow)s; Field<TextBox>(w, "Input").Text = server.BaseUrl + "/range-new";
                Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => w.Accept()));
            }));
            list.SelectedItems.Clear(); list.SelectedItems.Add(window.Rows.First(r => r.FileName == "bad.bin")); Pump(100);
            Call(window, "ContextRefreshUrl_Click");
            PumpUntil(() => m.Jobs.Single(j => j.Id == failed.Id).Url.EndsWith("/range-new"), "address replaced");
            PumpUntil(() => m.Jobs.Single(j => j.Id == failed.Id).Status == "Completed", "Refreshed address downloads");
            Check(new FileInfo(m.Jobs.Single(j => j.Id == failed.Id).FilePath).Length == server.Payload.Length, "Refresh address re-enters the URL and the failed job completes");
            // Properties
            list.SelectedItems.Clear(); list.SelectedItems.Add(window.Rows.First(r => r.FileName == "later.bin")); Pump(100);
            var fields = PropertiesWindow.Build(window.Rows.First(r => r.FileName == "later.bin")).ToDictionary(f => f.Label, f => f.Value);
            Check(fields["Referer"] == server.BaseUrl + "/ref" && fields["Address (URL)"] == server.BaseUrl + "/range" && fields["Status"] == "Scheduled" && fields["Save path"].EndsWith("later.bin") && !fields["Added"].StartsWith("Unknown") && fields.ContainsKey("Scheduled"), "Properties show url, referer, status, save path, added date");
        }
        finally { m.Pause(later.Id); Pump(700); window.Close(); } // a busy row would raise the quit prompt
    }

    private static void Properties(string root, FixtureServer server)
    {
        using var m = Manager(root, "props");
        var job = Done(m, server.BaseUrl + "/range", "p.bin").GetAwaiter().GetResult();
        var f = PropertiesWindow.Build(new DownloadRow(job)).ToDictionary(x => x.Label, x => x.Value);
        Check(f["Size"].Contains("bytes") && f["Status"] == "Completed" && f["Referer"] == "None" && f["Downloaded"].EndsWith("(100%)"), "Properties for a completed download: size in bytes, 100%, no referer");
        var window = new PropertiesWindow(new DownloadRow(job));
        Check(Field<ItemsControl>(window, "Fields").Items.Count == f.Count, "Properties window lists every field");
    }

    // ------------------------------------------------------------------ 4. proxy options tab
    private static void ProxyOptionsTab(string root, FixtureServer server)
    {
        var state = Path.Combine(root, "proxy-state"); Directory.CreateDirectory(state);
        var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "proxy-files") };
        using var proxy = new FixtureProxy("neo", "pw-secret-42");
        var dialog = new SettingsWindow(settings, state);
        dialog.Show(); Pump();
        var tabs = Field<TabControl>(dialog, "OptionsTabs");
        var headers = tabs.Items.OfType<TabItem>().Select(t => (string)t.Header).ToArray();
        Check(headers.Contains("_Proxy"), "Options has a Proxy tab");
        Check(tabs.Items.Count == 6 && headers.Contains("_Torrents") && !headers.Contains("_Connection"), "Six option sections (no Connection tab), including Appearance and Torrents");
        Check(Field<CheckBox>(dialog, "ProxyForPeers").Visibility == Visibility.Visible, "Torrent build shows the torrent proxy scopes");
        var types = Field<ComboBox>(dialog, "ProxyTypeBox").Items.OfType<ComboBoxItem>().Select(i => (string)i.Content).ToArray();
        Check(types.Length == 4 && types[0].StartsWith("None") && types[1] == "HTTP" && types[2].StartsWith("HTTPS") && types[3] == "SOCKS5" && !types.Any(t => t.Contains("SOCKS4")), "Types: None/HTTP/HTTPS/SOCKS5 (no SOCKS4)");
        Check(!Field<Grid>(dialog, "ProxyFields").IsEnabled && !Field<Button>(dialog, "ProxyTestButton").IsEnabled, "Fields are disabled while type is None");
        Field<ComboBox>(dialog, "ProxyTypeBox").SelectedIndex = 3;
        Field<TextBox>(dialog, "ProxyHost").Text = "127.0.0.1"; Field<TextBox>(dialog, "ProxyPort").Text = proxy.Port.ToString();
        Field<TextBox>(dialog, "ProxyUser").Text = "neo"; Field<PasswordBox>(dialog, "ProxyPassword").Password = "pw-secret-42";
        Field<CheckBox>(dialog, "ProxyResolve").IsChecked = false;
        // Test connection button: a real request through the fixture proxy
        dialog.ProxyTestTarget = server.BaseUrl + "/empty";
        var ok = Sync(dialog.TestProxyAsync());
        Check(ok.Ok && proxy.Total > 0 && Field<TextBlock>(dialog, "ProxyTestResult").Text.Contains("HTTP 200"), "Test connection sends a real request through the proxy: " + ok.Message);
        Field<PasswordBox>(dialog, "ProxyPassword").Password = "wrong-password";
        var bad = Sync(dialog.TestProxyAsync());
        Check(!bad.Ok && bad.Message.Contains("user name or password") && !bad.Message.Contains("wrong-password"), "Wrong credentials give a clear message without echoing the password: " + bad.Message);
        Field<TextBox>(dialog, "ProxyPort").Text = "99999";
        Check(!Sync(dialog.TestProxyAsync()).Ok && Field<TextBlock>(dialog, "ProxyTestResult").Text.Contains("Proxy port"), "Invalid port is reported inline");
        Field<TextBox>(dialog, "ProxyPort").Text = proxy.Port.ToString(); Field<PasswordBox>(dialog, "ProxyPassword").Password = "pw-secret-42";
        Click(dialog, "SaveButton");
        PumpUntil(() => File.Exists(Path.Combine(state, "settings.json")), "settings saved");
        string json = File.ReadAllText(Path.Combine(state, "settings.json"));
        Check(!json.Contains("pw-secret-42") && json.Contains("ProxyPasswordProtected") && json.Contains("\"ProxyType\": \"Socks5\"") && json.Contains("\"ProxyUsername\": \"neo\""), "settings.json holds no plain-text password");
        using (var doc = JsonDocument.Parse(json))
        {
            var blob = doc.RootElement.GetProperty("ProxyPasswordProtected").GetString()!;
            Check(blob.Length > 40 && Convert.FromBase64String(blob).Length > 16, "Password is a DPAPI blob");
        }
        var loaded = AppSettings.Load(state);
        var built = loaded.BuildProxy();
        Check(built is { Kind: ProxyKind.Socks5, Host: "127.0.0.1", Username: "neo", Password: "pw-secret-42", ResolveHostsThroughProxy: false } && built.Port == proxy.Port, "Settings round-trip restores the proxy and decrypts the password");
        Check(!built!.ToString().Contains("pw-secret-42") && !built.ToString().Contains("neo"), "Proxy ToString never prints credentials");
        // Re-open: blank password keeps the saved one
        var again = new SettingsWindow(loaded, state); again.Show(); Pump();
        Check(Field<TextBlock>(again, "ProxyPasswordNote").Text.Contains("saved") && Field<PasswordBox>(again, "ProxyPassword").Password.Length == 0, "Saved password is never shown back");
        Click(again, "SaveButton"); Pump(300);
        Check(AppSettings.Load(state).BuildProxy()?.Password == "pw-secret-42", "Saving with a blank password box keeps the stored password");
        Field<TextBox>(again, "ProxyUser").Text = ""; Click(again, "SaveButton"); Pump(300);
        var cleared = AppSettings.Load(state);
        Check(cleared.ProxyPasswordProtected.Length == 0 && cleared.BuildProxy() is { Password: null, Username: null }, "Clearing the user name drops the stored password");
        // OptionsHelper: engine options carry the proxy only when the downloads scope is on
        loaded.ProxyForDownloads = true; loaded.ProxyType = ProxyKind.Socks5; loaded.ProxyHost = "127.0.0.1";
        Check(OptionsHelper.CreateDownloadOptions(loaded).Proxy is { Kind: ProxyKind.Socks5 }, "Download engine options include the proxy");
        loaded.ProxyForDownloads = false;
        Check(OptionsHelper.CreateDownloadOptions(loaded).Proxy is null, "Scope off -> direct connection");
        // End to end: a real download through the configured proxy
        loaded.ProxyForDownloads = true; loaded.ProxyResolveHostsThroughProxy = true; loaded.ProxyType = ProxyKind.Socks5; loaded.ProxyUsername = "neo"; loaded.SetProxyPassword("pw-secret-42");
        int socksBefore = proxy.Socks;
        using (var m = new DownloadManager(Path.Combine(root, "proxy-dl", "f"), Path.Combine(root, "proxy-dl", "s"), OptionsHelper.CreateDownloadOptions(loaded)))
        {
            var job = Sync(Done(m, server.BaseUrl + "/range", "viaproxy.bin"));
            Check(job.Status == "Completed" && FixtureServer.Hash(File.ReadAllBytes(job.FilePath)) == FixtureServer.Hash(server.Payload) && proxy.Socks > socksBefore, "A download configured through Options really travels through the proxy");
        }
        dialog.Close(); again.Close();
    }

    // ------------------------------------------------------------------ 5. build variants
    private static void Variant(string root, FixtureServer server)
    {
        using var m = Manager(root, "variant");
        var variantSettings = new AppSettings { DownloadDirectory = Path.Combine(root, "variant", "files") };
        ITorrentFeature feature = new TorrentFeature(new TorrentController(Path.Combine(root, "variant", "torrent"), variantSettings), variantSettings);
        var window = new MainWindow(m, variantSettings, () => { }, enableTray: false, power: null, torrents: feature) { ShowInTaskbar = false };
        try
        {
            window.Show(); Pump();
            var nav = Field<ListBox>(window, "Navigation").Items.OfType<ListBoxItem>().Select(i => (string)i.Tag).ToArray();
            var referenced = typeof(MainWindow).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
            Check(window.Torrents.IsAvailable && window.Torrents is TorrentFeature, "Torrent build: feature is active");
            Check(nav.Contains("T:Seeding") && nav.Contains("T:All") && nav.Contains("T:Downloading") && nav.Contains("T:Completed") && !nav.Contains("Connecting") && nav.Contains("header") && Field<Button>(window, "AddTorrentButton").Visibility == Visibility.Visible && Field<MenuItem>(window, "CtxMagnet").Visibility == Visibility.Visible, "Torrent build: filters, button and context items are present");
            Check(referenced.Contains("NDM.Torrent") && referenced.Contains("MonoTorrent.Client"), "Torrent build references the torrent engine");
            Check(App.StateDirectory.EndsWith("NDM", StringComparison.Ordinal) && !App.PipeName.EndsWith(".torrent", StringComparison.Ordinal), "Torrents share the normal state folder and pipe: " + App.StateDirectory);
        }
        finally { window.Close(); feature.Dispose(); }
    }
}
