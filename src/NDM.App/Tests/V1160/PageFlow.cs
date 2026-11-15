using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

/// <summary>A local stand-in for a file host (127.0.0.1): link -> 302 with a cookie -> an HTML page -> the file, in several variants. There is NO real captcha here: the "challenge" is a box the test removes by script, standing in for the person.</summary>
internal sealed class FileHostFixture : IDisposable
{
    private readonly HttpListener listener = new(); public readonly string Base, OtherBase; public readonly int Port;
    public readonly ConcurrentDictionary<string, int> Hits = new(); public int FileWithoutCookie;
    public FileHostFixture()
    {
        using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0)) { probe.Start(); Port = ((IPEndPoint)probe.LocalEndpoint).Port; }
        Base = $"http://127.0.0.1:{Port}"; OtherBase = $"http://localhost:{Port}";
        listener.Prefixes.Add(Base + "/"); listener.Prefixes.Add(OtherBase + "/"); listener.Start(); _ = Task.Run(Loop);
    }
    public static byte[] Bytes(string name) { var b = new byte[2048]; new Random(name.GetHashCode() == 0 ? 1 : Math.Abs(name.Aggregate(17, (h, c) => h * 31 + c))).NextBytes(b); return b; }
    public string Link(string kind, string name) => $"{Base}/f/{kind}/{name}";
    private async Task Loop()
    {
        while (listener.IsListening)
        {
            HttpListenerContext c; try { c = await listener.GetContextAsync(); } catch { return; }
            _ = Task.Run(() => Serve(c));
        }
    }
    private void Serve(HttpListenerContext c)
    {
        try
        {
            var path = c.Request.Url!.AbsolutePath; var r = c.Response; var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries); Hits.AddOrUpdate(parts.Length > 0 ? "/" + parts[0] : "/", 1, (_, n) => n + 1);
            void Html(string body) { var b = Encoding.UTF8.GetBytes("<!doctype html><html><head><meta charset=utf-8><title>Host</title></head><body>" + body + "</body></html>"); r.ContentType = "text/html; charset=utf-8"; r.ContentLength64 = b.Length; if (c.Request.HttpMethod != "HEAD") r.OutputStream.Write(b); r.Close(); }
            void File(string name) { var b = Bytes(name); r.ContentType = "application/octet-stream"; r.Headers["Content-Disposition"] = $"attachment; filename=\"{name}\""; r.ContentLength64 = b.Length; if (c.Request.HttpMethod != "HEAD") r.OutputStream.Write(b); r.Close(); }
            if (parts.Length == 3 && parts[0] == "f")
            {   // the pasted link: a redirect that sets a cookie, like a real host does
                r.AddHeader("Set-Cookie", "file_code=abc123; Path=/");
                r.StatusCode = 302; r.RedirectLocation = (parts[1] == "cross" ? OtherBase : "") + $"/page/{(parts[1] == "cross" ? "instant" : parts[1])}/{parts[2]}"; r.Close(); return;
            }
            if (parts.Length == 3 && parts[0] == "page")
            {
                var n = parts[2];
                switch (parts[1])
                {
                    case "instant": Html($"<h1>Your file</h1><a class='btn' href='/file/{n}'>Download</a><a href='/premium'>Premium download</a>"); return;
                    case "timer": Html($"<p>Please wait <span id=c>3</span> seconds</p><button id=b disabled onclick=\"location.href='/file/{n}'\">Free Download</button><script>var s=3;var t=setInterval(function(){{s--;document.getElementById('c').textContent=s;if(s<=0){{clearInterval(t);document.getElementById('b').disabled=false;}}}},1000);</script>"); return;
                    case "post": Html($"<form method=post action='/post-file/{n}'><button type=submit>Create download link</button></form>"); return;
                    case "captcha": Html($"<div class='g-recaptcha' style='width:304px;height:78px;border:1px solid #888'>fixture check (not a real captcha)</div><button id=b disabled onclick=\"location.href='/file/{n}'\">Download</button><script>function solve(){{var e=document.querySelector('.g-recaptcha');if(e)e.remove();document.getElementById('b').disabled=false;}}</script>"); return;
                    case "nobutton": Html($"<p>Nothing to click here.</p><a id=go href='/file/{n}'>Get it here</a>"); return;
                    case "login": Html("<form><input type=password name=p aria-label=password><button type=button>Sign in</button></form>"); return;
                }
            }
            if (parts.Length == 2 && parts[0] == "post-file") { if (c.Request.HttpMethod != "POST") { r.StatusCode = 405; r.Close(); return; } r.StatusCode = 303; r.RedirectLocation = "/file/" + parts[1]; r.Close(); return; } // post, then redirect to the file (a POST that answers with the file itself cannot be fetched again)
            if (parts.Length == 2 && parts[0] == "file")
            {
                var cookie = c.Request.Headers["Cookie"] ?? "";
                if (!cookie.Contains("file_code=abc123") && !c.Request.Url.Query.Contains("open")) { Interlocked.Increment(ref FileWithoutCookie); r.StatusCode = 403; r.Close(); return; }
                File(parts[1]); return;
            }
            if (parts.Length == 2 && parts[0] == "direct") { File(parts[1]); return; }
            r.StatusCode = 404; r.Close();
        }
        catch { try { c.Response.Abort(); } catch { } }
    }
    public void Dispose() { try { listener.Stop(); listener.Close(); } catch { } }
}

internal static partial class V1160Tests
{
    private static void Wait(Task t, string what, int ms = 60000) => PumpUntil(() => t.IsCompleted, what, ms);

    /// <summary>The whole package flow with a scripted page: paste 4 page links of one set (probed as pages), the window suggests the package and a queue, Add and start, the resolver captures each link in order, and the SAME jobs finish with the right bytes.</summary>
    private static void PackageFlow(string root)
    {
        using var host = new FileHostFixture(); using var m = NewManager(root, "pkg", 2);
        var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "pkg", "files"), ShowCompletionDialog = false, ShowProgressOnCompletion = false };
        var w = new MainWindow(m, settings, () => { }, enableTray: false) { ShowInTaskbar = false, Width = 1000, Height = 640, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10 };
        w.Show(); Pump(500);
        var loads = new List<string>(); int sessions = 0; var played = new List<string>();
        w.PageSessionFactory = () =>
        {
            sessions++; var p = new FakePage(); string name = "";
            p.OnScan = pg => { name = pg.Url[(pg.Url.LastIndexOf('/') + 1)..]; return Snap(pg, new[] { Ctl("Download") }); };
            p.OnClick = (pg, _) => { played.Add(name); pg.Capture = new BrowserGrab($"{host.Base}/file/{name}", name, pg.Url, "file_code=abc123", "TestAgent", null, null); };
            return p;
        };
        LinkGrabberWindow? grab = null;
        w.GrabberCreated = g => { grab = g; g.Loaded += (_, _) => _ = g.AddAsync(true); };
        var text = string.Join("\n", Enumerable.Range(1, 4).Select(n => host.Link("instant", $"Part_{n}.zip")));
        Dispatch(() => w.OpenGrabber(text));
        PumpUntil(() => w.PageResolver is { Items.Count: 4 }, "four page links reached the resolver", 40000);
        Check(grab is not null && grab.PackageText == "Files from 127.0.0.1", "the grabber suggested a package name for the four parts (host named, since the file names have no stem): " + grab?.PackageText);
        Check(grab!.QueueRowVisible && ((ComboBoxItem)grab.Queues.Items[0]).Content as string == "New queue: Files from 127.0.0.1" && grab.AutoStart.IsChecked == true, "a new queue named after the package is offered, Start automatically is ticked");
        var queue = m.Queues.FirstOrDefault(q => q.Name == "Files from 127.0.0.1"); Check(queue is not null, "the package got its own queue");
        PumpUntil(() => w.PageResolver is { Running: false }, "the resolver finished", 40000);
        Check(w.PageResolver!.Items.All(i => i.State == PageLinkState.Resolved) && played.SequenceEqual(new[] { "Part_1.zip", "Part_2.zip", "Part_3.zip", "Part_4.zip" }) && sessions == 1, "the four page links were resolved in order in one window");
        var jobs = m.OrderOf(queue!.Id).Select(id => Job(m, id)).ToList();
        Check(jobs.Count == 4 && jobs.All(j => j.Package == "Files from 127.0.0.1" && j.QueueId == queue.Id), "the four jobs sit in the package queue under one package name, in order");
        PumpUntil(() => m.Jobs.All(j => j.Status == "Completed"), "every job finished", 40000);
        Check(jobs.Select(j => j.Id).SequenceEqual(m.Jobs.Select(j => j.Id)) && m.Jobs.Count == 4, "the SAME four jobs completed (no extra jobs were created)");
        for (int i = 0; i < 4; i++) { var j = Job(m, jobs[i].Id); Check(File.ReadAllBytes(j.FilePath).SequenceEqual(FileHostFixture.Bytes($"Part_{i + 1}.zip")), $"Part_{i + 1}.zip arrived with the exact bytes (cookie from the page session carried: {host.FileWithoutCookie == 0})"); }
        Check(host.FileWithoutCookie == 0, "the file host never saw a download without the cookie the browser session captured");
        Check(w.PageLinks is { } pl && pl.SummaryText.Contains("4 of 4"), "the page links window reports 4 of 4 ready");
        SaveShot(w.PageLinks!, "pagelinks-done-light.png");
        w.PageLinks!.Close(); w.Close();

        // Start automatically off: the links are added paused to the queue and nothing starts by itself
        using var m2 = NewManager(root, "pkg2", 2); var w2 = new MainWindow(m2, new AppSettings { DownloadDirectory = Path.Combine(root, "pkg2", "files"), ShowCompletionDialog = false, ShowProgressOnCompletion = false }, () => { }, enableTray: false) { ShowInTaskbar = false, Width = 900, Height = 600 };
        w2.Show(); Pump(300); w2.GrabberCreated = g => g.Loaded += (_, _) => { g.AutoStart.IsChecked = false; _ = g.AddAsync(true); };
        var direct = string.Join("\n", Enumerable.Range(1, 3).Select(n => $"{host.Base}/direct/Show.part{n}.rar"));
        Dispatch(() => w2.OpenGrabber(direct)); Pump(500);
        PumpUntil(() => m2.Jobs.Count == 3, "three direct links were added", 20000);
        Check(m2.Jobs.All(j => j.Status == "Paused" && j.Package == "Show") && m2.Queues.Any(q => q.Name == "Show"), "direct parts: package 'Show' in a queue of that name; with Start automatically off they are added paused");
        w2.Close();

        // the grabber window with the package row (screenshots)
        using (var m5 = NewManager(root, "pkg5", 1))
        {
            m5.CreateQueue("Work");
            foreach (var theme in new[] { "Light", "DeepNavy" })
            {
                ThemeManager.Apply(Application.Current, theme, null);
                var g5 = new LinkGrabberWindow(m5, new AppSettings { DownloadDirectory = Path.Combine(root, "pkg5", "files") }, string.Join(Environment.NewLine, Enumerable.Range(1, 4).Select(n => host.Link("instant", $"Part_{n}.zip")))) { Left = 10, Top = 10, WindowStartupLocation = WindowStartupLocation.Manual };
                g5.Show(); Pump(500); SaveShot(g5, "grabber-package-" + theme.ToLowerInvariant() + ".png");
                Check(g5.PackageHintText.Contains("4 links look like one set"), "the hint under the package row explains the grouping (" + theme + ")"); g5.Close();
            }
            ThemeManager.Apply(Application.Current, "Light", null);
        }
        // a typed package name is never replaced, and a lone numbered file is not grouped
        using var m3 = NewManager(root, "pkg3", 1); var g3 = new LinkGrabberWindow(m3, new AppSettings { DownloadDirectory = Path.Combine(root, "pkg3", "files") }, $"{host.Base}/direct/Only.part1.rar"); g3.Show(); Pump(300);
        Check(g3.PackageText == "" && !g3.QueueRowVisible, "one numbered file does not become a package"); g3.Close();
        var g4 = new LinkGrabberWindow(m3, new AppSettings { DownloadDirectory = Path.Combine(root, "pkg3", "files") }); g4.Show(); g4.PackageText = "My own name"; Pump(100);
        g4.Logic.Load(string.Join("\n", Enumerable.Range(1, 2).Select(n => $"{host.Base}/direct/Set.part{n}.rar"))); g4.ParseNow(); Pump(300);
        Check(g4.PackageText == "My own name" && g4.QueueRowVisible, "a name the person typed stays; the queue row shows");
        g4.Close();
    }
    private static void Dispatch(Action a) { var d = Application.Current.Dispatcher; var done = false; d.BeginInvoke(new Action(() => { try { a(); } finally { done = true; } })); PumpUntil(() => done, "ui action", 60000); }

    /// <summary>The "This site needs you" panel with a scripted page: message, both buttons, skip and show, and renders in two themes and RTL.</summary>
    private static void NeedsYouPanel(string root)
    {
        var clock = new FakeClock(); FakePage? page = null; int tick = 0; bool released = false;
        var co = new PageResolveCoordinator(() => page = new FakePage { OnScan = pg => ++tick > 6 && released ? Snap(pg, new[] { Ctl("Download") }) : Snap(pg, challenge: "captcha"), OnClick = (pg, _) => pg.Capture = Grab() },
            (_, _) => Task.CompletedTask, new PageResolveEngine { Tick = TimeSpan.FromMilliseconds(30) });
        co.Add(new[] { Item("Part_1.zip", "http://127.0.0.1:1/f/Part_1.zip"), Item("Part_2.zip", "http://127.0.0.1:1/f/Part_2.zip") });
        var win = new PageLinksWindow(co, "Games pack") { Left = 10, Top = 10, WindowStartupLocation = WindowStartupLocation.Manual }; win.Show(); Pump(200);
        var run = co.RunAsync(); PumpUntil(() => co.Items[0].State == PageLinkState.NeedsYou, "the first link reaches 'needs you'", 10000); Pump(300);
        Check(win.NeedsPanelVisible && win.NeedsText.StartsWith("This site needs you.") && win.NeedsText.Contains("Solve the check in the NeoDM browser window, then NeoDM continues by itself."), "the panel says: This site needs you. Solve the check in the NeoDM browser window, then NeoDM continues by itself.");
        Check(win.ShowWindowButton.IsEnabled && (string)win.ShowWindowButton.Content == "Show the window" && (string)win.SkipLinkButton.Content == "Skip this link", "the panel has 'Show the window' and 'Skip this link'");
        Check(page!.Log.Contains("present:True") && page.Message == "pagelink.window.needs_you", "the browser window was brought to the front with the check message");
        SaveShot(win, "pagelinks-needsyou-light.png");
        foreach (var theme in new[] { "DeepNavy", "Dark" }) { ThemeManager.Apply(Application.Current, theme, null); Pump(300); SaveShot(win, "pagelinks-needsyou-" + theme.ToLowerInvariant() + ".png"); }
        ThemeManager.Apply(Application.Current, "Light", null);
        Loc.SetLanguage("ar"); win.FlowDirection = FlowDirection.RightToLeft; Pump(400); SaveShot(win, "pagelinks-needsyou-rtl-ar.png"); Check(win.NeedsPanelVisible && win.NeedsText.Length > 10, "RTL: the panel still shows in Arabic"); Loc.SetLanguage("en"); win.FlowDirection = FlowDirection.LeftToRight; Pump(200);
        Click(win.SkipLinkButton); PumpUntil(() => co.Items[0].State == PageLinkState.Skipped, "Skip this link skips it", 10000);
        released = true; tick = 6; PumpUntil(() => co.Items[1].State == PageLinkState.Resolved || co.Items[1].State == PageLinkState.NeedsYou, "the next link starts", 10000);
        Click(win.ShowWindowButton); Check(page.Log.Count(l => l == "present:True") >= 2, "Show the window brings the window forward again");
        PumpUntil(() => run.IsCompleted, "the run ends", 15000);
        Check(co.Items[0].State == PageLinkState.Skipped && co.Items[1].State == PageLinkState.Resolved, "the skipped link is Skipped and the next link was resolved without a click on the check");
        Pump(300); SaveShot(win, "pagelinks-final-light.png"); win.Close();
    }
}
