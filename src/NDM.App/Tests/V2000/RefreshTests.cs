// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

/// <summary>2.0.0 stage S4a: Refresh link for an expired download. The extension is played by the test: it reads the one-time code from the address the app opens
/// and answers through the same bridge actions the native host forwards.</summary>
internal static partial class V2000Tests
{
    private const string Page = "https://site.example/files/model?id=9";

    /// <summary>A loopback server that refuses every request with 403 (an expired signed link).</summary>
    private sealed class Refuser : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        public string Url(string path) => "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + path;
        public Refuser()
        {
            listener.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        var c = await listener.AcceptTcpClientAsync(stop.Token);
                        _ = Task.Run(async () => { using (c) { try { var s = c.GetStream(); var buf = new byte[4096]; await s.ReadAsync(buf); await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")); } catch (Exception) { } } });
                    }
                }
                catch (Exception) { }
            });
        }
        public void Dispose() { stop.Cancel(); listener.Stop(); }
    }

    private static DownloadManager RefreshManager(string scratch, out string root)
    {
        root = Path.Combine(scratch, "ref-" + Guid.NewGuid().ToString("N"));
        return new DownloadManager(Path.Combine(root, "files"), Path.Combine(root, "state"), new DownloadOptions { MaxConcurrentDownloads = 2, MaxSegments = 1, RetryCount = 0, RequestTimeout = TimeSpan.FromSeconds(5) });
    }
    private static DownloadJob FailedJob(DownloadManager manager, Refuser server, string? page = Page)
    {
        var job = manager.AddAsync(new DownloadRequest(server.Url("/dl/model-00002-of-00005.gguf?sig=old"), "model.gguf", PageUrl: page)).GetAwaiter().GetResult();
        for (int i = 0; i < 200 && manager.Jobs.Single(j => j.Id == job.Id).Status != "Failed"; i++) Pump(50);
        return manager.Jobs.Single(j => j.Id == job.Id);
    }
    private static string NonceOf(string openAddress) => openAddress[(openAddress.IndexOf(RefreshLink.Marker, StringComparison.Ordinal) + RefreshLink.Marker.Length)..];
    private static NativeBridge.ExtensionRefreshReport Say(string nonce, string state, string? url = null) => new(nonce, state, url, url is null ? null : "sid=abc", "Mozilla/5.0 Test", null, "en-US", Page);

    private static void RefreshFlow(string scratch)
    {
        using var server = new Refuser();
        using var manager = RefreshManager(scratch, out _);
        var job = FailedJob(manager, server);
        Check(job.Status == "Failed" && job.ErrorStatus == 403 && job.PageUrl == Page, "the expired link fails with 403 and the job knows its page");
        Check(RefreshLinkRun.CanRefresh(job), "that failure offers a refresh");
        Check(!RefreshLinkRun.CanRefresh(FailedJob(manager, server, null)), "a job without a page does not");
        var fresh = server.Url("/dl/model-00002-of-00005.gguf?sig=new");

        RefreshOutcome Run(Action<string, RefreshLinkWatch> extension, Action<RefreshLinkRun>? tune = null, string? id = null, Action<RefreshStage>? stage = null)
        {
            var watch = new RefreshLinkWatch(); string? opened = null;
            var run = new RefreshLinkRun { Watch = watch, ContactWait = TimeSpan.FromSeconds(2), TotalWait = TimeSpan.FromSeconds(6), Launcher = a => { opened = a; var n = NonceOf(a); Task.Run(() => { Thread.Sleep(150); extension(n, watch); }); } };
            tune?.Invoke(run);
            var task = Task.Run(() => run.RunAsync(manager, id ?? job.Id, stage, CancellationToken.None));
            while (!task.IsCompleted) Pump(30);
            Check(opened is null || opened.StartsWith(Page + RefreshLink.Marker), "the browser is opened at the stored page with the code after it");
            Check(watch.List().Count == 0, "the watch entry is gone when the run ends");
            return task.Result;
        }

        var stages = new List<RefreshStage>();
        var outcome = Run((n, w) => { w.Report(Say(n, "contact")); Thread.Sleep(200); w.Report(Say(n, "found", fresh)); }, stage: stages.Add);
        var after = manager.Jobs.Single(j => j.Id == job.Id);
        Check(outcome == RefreshOutcome.Refreshed && after.Url == fresh && manager.Jobs.Count(j => j.PageUrl == Page && j.Id == job.Id) == 1, "a fresh link goes into the SAME job");
        Check(stages.Contains(RefreshStage.Opening) && stages.Contains(RefreshStage.Reading), "the person sees the opening and the reading stage");
        Check(after.Status != "Failed" || after.Url == fresh, "the job was resumed");

        var j2 = FailedJob(manager, server);
        var stages2 = new List<RefreshStage>();
        outcome = Run((n, w) => { w.Report(Say(n, "contact")); w.Report(Say(n, "signin")); Thread.Sleep(500); w.Report(Say(n, "found", fresh + "2")); }, id: j2.Id, stage: stages2.Add);
        Check(outcome == RefreshOutcome.Refreshed && stages2.Contains(RefreshStage.SignIn), "a sign-in page is shown to the person, then the link is taken");

        var j3 = FailedJob(manager, server);
        outcome = Run((n, w) => { w.Report(Say(n, "contact")); w.Report(Say(n, "notfound")); }, id: j3.Id);
        Check(outcome == RefreshOutcome.NotFound && manager.Jobs.Single(j => j.Id == j3.Id).Status == "Failed", "nothing on the page: the job stays failed");

        outcome = Run((n, w) => { }, id: j3.Id, tune: r => r.ContactWait = TimeSpan.FromMilliseconds(600));
        Check(outcome == RefreshOutcome.NoExtension, "no answer from the extension is told as such");

        outcome = Run((n, w) => { w.Report(Say(n, "contact")); w.Report(Say(n, "signin")); }, id: j3.Id, tune: r => r.TotalWait = TimeSpan.FromMilliseconds(900));
        Check(outcome == RefreshOutcome.NeedsSignIn, "a sign-in nobody completed is told as such");

        outcome = Run((n, w) => { w.Report(Say(n, "contact")); w.Report(Say(n, "found", "https://evil.test/other.bin")); }, id: j3.Id);
        Check(outcome == RefreshOutcome.NotFound && manager.Jobs.Single(j => j.Id == j3.Id).Url.Contains("sig=old"), "an address of another file on another site is refused");

        outcome = Run((n, w) => { }, id: FailedJob(manager, server, null).Id);
        Check(outcome == RefreshOutcome.NoPage, "a job without a page says so and opens nothing");

        var wrong = new RefreshLinkWatch(); var entry = wrong.Register("x", Page, "https://a.example/f.bin", TimeSpan.FromSeconds(30));
        Check(!wrong.Report(Say("ffffffffffffffffffffffffffffffff", "contact")) && wrong.Report(Say(entry.Nonce, "contact")), "a report with another code is not taken");
        wrong.Now = () => DateTime.UtcNow.AddMinutes(5);
        Check(wrong.List().Count == 0 && !wrong.Report(Say(entry.Nonce, "found", fresh)), "an expired entry answers nothing");
    }

    private static string AskPipe(string pipe, string line)
    {
        using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        client.Connect(3000);
        client.Write(Encoding.UTF8.GetBytes(line + "\n")); client.Flush();
        using var reader = new StreamReader(client, Encoding.UTF8);
        return reader.ReadLine() ?? "";
    }

    private static void RefreshBridge()
    {
        var watch = new RefreshLinkWatch(); var entry = watch.Register("job1", Page, "https://cdn.example/f.bin?sig=old", TimeSpan.FromMinutes(1));
        var pipe = "NDM.V2000Refresh." + Guid.NewGuid().ToString("N");
        using var bridge = new NativeBridge(_ => { }, pipe, onRefreshWatch: () => watch.List(), onRefreshReport: r => watch.Report(r));
        bridge.Start();
        var list = JsonDocument.Parse(AskPipe(pipe, "{\"action\":\"refresh-watch\"}")).RootElement;
        Check(list.GetProperty("ok").GetBoolean() && list.GetProperty("watch").GetArrayLength() == 1 && list.GetProperty("watch")[0].GetProperty("nonce").GetString() == entry.Nonce && list.GetProperty("watch")[0].GetProperty("old").GetString() == "https://cdn.example/f.bin?sig=old", "the bridge lists the refresh in progress");
        var contact = JsonDocument.Parse(AskPipe(pipe, "{\"action\":\"refresh-report\",\"nonce\":\"" + entry.Nonce + "\",\"state\":\"contact\"}")).RootElement;
        Check(contact.GetProperty("ok").GetBoolean() && contact.GetProperty("act").GetBoolean() && entry.Contact, "a contact report is taken");
        var other = JsonDocument.Parse(AskPipe(pipe, "{\"action\":\"refresh-report\",\"nonce\":\"0123456789abcdef0123456789abcdef\",\"state\":\"contact\"}")).RootElement;
        Check(other.GetProperty("ok").GetBoolean() && !other.GetProperty("act").GetBoolean(), "a report with a code nobody waits on is answered no");
        foreach (var bad in new[]
        {
            "{\"action\":\"refresh-report\",\"nonce\":\"zz\",\"state\":\"contact\"}",
            "{\"action\":\"refresh-report\",\"nonce\":\"" + entry.Nonce + "\",\"state\":\"done\"}",
            "{\"action\":\"refresh-report\",\"nonce\":\"" + entry.Nonce + "\",\"state\":\"found\",\"url\":\"ftp://x.example/f\"}",
            "{\"action\":\"refresh-report\",\"nonce\":\"" + entry.Nonce + "\",\"state\":\"found\",\"url\":\"https://x.example/f\",\"cookie\":\"a\\u0001b\"}",
            "{\"action\":\"refresh-report\",\"nonce\":\"" + entry.Nonce + "\",\"state\":\"found\",\"url\":\"https://x.example/f\",\"userAgent\":\"a\\nb\"}",
        })
        {
            var reply = JsonDocument.Parse(AskPipe(pipe, bad)).RootElement;
            Check(!reply.GetProperty("ok").GetBoolean() && entry.Found is null, "a malformed report is refused: " + bad[..Math.Min(bad.Length, 95)]);
        }
        var found = JsonDocument.Parse(AskPipe(pipe, "{\"action\":\"refresh-report\",\"nonce\":\"" + entry.Nonce + "\",\"state\":\"found\",\"url\":\"https://cdn.example/f.bin?sig=new\",\"referer\":\"" + Page + "\",\"cookie\":\"sid=1\",\"userAgent\":\"Mozilla/5.0\"}")).RootElement;
        Check(found.GetProperty("act").GetBoolean() && entry.Found is { Url: "https://cdn.example/f.bin?sig=new", Cookie: "sid=1" }, "a fresh address with its cookie and identity is taken");
        using var none = new NativeBridge(_ => { }, pipe + "b");
        none.Start();
        var empty = JsonDocument.Parse(AskPipe(pipe + "b", "{\"action\":\"refresh-watch\"}")).RootElement;
        Check(empty.GetProperty("watch").GetArrayLength() == 0, "an app without the feature wired answers an empty list");
        var capture = new List<NativeBridge.BrowserDownloadRequest>();
        var pipe2 = pipe + "c";
        using var cap = new NativeBridge(r => { capture.Add(r); r.Respond(new NativeBridge.BrowserDownloadResponse(true, "id", null)); }, pipe2);
        cap.Start();
        AskPipe(pipe2, "{\"url\":\"https://cdn.example/f.bin\",\"fileName\":\"f.bin\",\"pageUrl\":\"https://site.example/p?x=1#frag\"}");
        AskPipe(pipe2, "{\"url\":\"https://cdn.example/g.bin\",\"fileName\":\"g.bin\",\"pageUrl\":\"javascript:alert(1)\"}");
        AskPipe(pipe2, "{\"url\":\"https://cdn.example/h.bin\",\"fileName\":\"h.bin\"}");
        Check(capture.Count == 3 && capture[0].PageUrl == "https://site.example/p?x=1" && capture[1].PageUrl is null && capture[2].PageUrl is null, "a capture carries a good page address (no fragment); a bad one is dropped without an error");
    }

    private static void RefreshUi(string scratch)
    {
        using var server = new Refuser();
        using var manager = RefreshManager(scratch, out var root);
        var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "files"), ShowSelectionCheckboxes = true };
        var job = FailedJob(manager, server);
        var window = new MainWindow(manager, settings, () => { }, enableTray: false) { ShowInTaskbar = false, Width = 1100, Height = 700 };
        window.Show(); Pump(500);
        var row = window.Rows.Single(r => r.Id == job.Id);
        Check(row.OffersRefresh && row.OffersBrowser, "the failed row offers Refresh link next to the browser button");
        Check(RowMenu.For(new[] { row }, _ => false, _ => false).RefreshLink, "the row menu state offers it");
        var list = (ListView)window.FindName("DownloadList"); list.SelectedItem = row; Pump(200);
        window.UpdateRowMenu();
        Check(((MenuItem)window.FindName("CtxRefreshLink")).Visibility == Visibility.Visible, "the right-click menu shows Refresh link");
        var view = new DownloadProgressView(manager, job.Id, null, settings); Pump(150);
        var panel = (FrameworkElement)view.FindName("RefreshPanel");
        Check(panel.Visibility == Visibility.Visible && ((Button)view.FindName("RefreshButton")).Visibility == Visibility.Visible, "the progress window and tab show the Refresh link button");
        var plain = new DownloadRow(JobWith(j => { SetJob(j, "Status", "Failed"); SetJob(j, "ErrorCode", CompatibilityLadder.HttpStatusError); SetJob(j, "ErrorStatus", 500); SetJob(j, "PageUrl", Page); }));
        var nopage = new DownloadRow(JobWith(j => { SetJob(j, "Status", "Failed"); SetJob(j, "ErrorCode", CompatibilityLadder.HttpStatusError); SetJob(j, "ErrorStatus", 403); }));
        var paused = new DownloadRow(JobWith(j => { SetJob(j, "Status", "Paused"); SetJob(j, "ErrorCode", CompatibilityLadder.HttpStatusError); SetJob(j, "ErrorStatus", 403); SetJob(j, "PageUrl", Page); }));
        Check(!plain.OffersRefresh && !nopage.OffersRefresh && !paused.OffersRefresh, "a 500, a job without a page and a paused job do not offer it");

        // the real click path: the button asks the main window, the main window opens the page through the launcher, the extension (played here) answers
        string? opened = null; var fresh = server.Url("/dl/model-00002-of-00005.gguf?sig=new");
        window.RefreshLauncher = a => { opened = a; var n = NonceOf(a); Task.Run(() => { Thread.Sleep(200); RefreshLinkHub.Watch.Report(Say(n, "contact")); Thread.Sleep(200); RefreshLinkHub.Watch.Report(Say(n, "found", fresh)); }); };
        view.RefreshLinkAsync = window.RefreshLinkAsync;
        ((Button)view.FindName("RefreshButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (int i = 0; i < 100 && manager.Jobs.Single(j => j.Id == job.Id).Url != fresh; i++) Pump(50);
        Check(opened is not null && manager.Jobs.Single(j => j.Id == job.Id).Url == fresh, "clicking Refresh link opens the page and gives the same job the fresh address");
        Pump(400);
        var text = ((TextBlock)view.FindName("RefreshText")).Text;
        Check(text == Loc.T("refresh.done"), "the window says the download continues: " + text);
        window.Close();
    }

    private static void RefreshLocalized()
    {
        string[] keys = { "refresh.btn", "refresh.stage.opening", "refresh.stage.reading", "refresh.stage.signin", "refresh.done", "refresh.fail.nopage", "refresh.fail.noextension", "refresh.fail.signin", "refresh.fail.notfound" };
        foreach (var lang in new[] { "ar", "de", "en", "es", "fr", "ja", "pt-BR", "ru", "tr", "zh-Hans" })
        {
            Loc.SetLanguage(lang);
            foreach (var key in keys) if (Loc.T(key) == key || Loc.T(key).Contains("Joining")) throw new Exception($"FAIL: {lang} is missing {key}");
            foreach (var o in Enum.GetValues<RefreshOutcome>()) if (o is not (RefreshOutcome.Cancelled or RefreshOutcome.NotApplicable) && RefreshTexts.Outcome(o).Length == 0) throw new Exception("FAIL: no text for " + o);
        }
        Loc.SetLanguage("en");
        Check(true, "all " + keys.Length + " refresh texts exist in ten languages");
    }
}
