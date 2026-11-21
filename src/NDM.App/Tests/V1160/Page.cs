// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Windows;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

internal static partial class V1160Tests
{
    // ------------------------------------------------------------------ fakes
    private sealed class FakeClock { public DateTime Now = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc); }

    /// <summary>A scripted page: <see cref="OnScan"/> decides what the page shows at each scan, <see cref="OnClick"/> what a click does.</summary>
    private sealed class FakePage : IPageSession
    {
        public readonly List<string> Log = new(); public int Scans, Clicks; public string Url = "";
        public Func<FakePage, PageSnapshot> OnScan = p => PageSnapshot.Empty(p.Url);
        public Action<FakePage, string> OnClick = (_, _) => { };
        public BrowserGrab? Capture; public string? Refusal; public bool Front; public string? Message;
        public Task LoadAsync(string url, CancellationToken token) { Url = url; Scans = 0; Log.Add("load:" + url); return Task.CompletedTask; }
        public Task<PageSnapshot> ScanAsync(CancellationToken token) { Scans++; return Task.FromResult(OnScan(this)); }
        public Task<bool> ClickAsync(string controlId, CancellationToken token) { Clicks++; Log.Add("click:" + controlId); OnClick(this, controlId); return Task.FromResult(true); }
        public BrowserGrab? TakeCapture() { var c = Capture; Capture = null; return c; }
        public string? TakeRefusal() { var r = Refusal; Refusal = null; return r; }
        public void NoteClick() { }
        public void Present(bool front) { Front = front; Log.Add("present:" + front); }
        public void SetHumanMessage(string key) { Message = key; Log.Add("msg:" + key); }
        public void Dispose() { Log.Add("dispose"); }
    }
    private static PageControl Ctl(string text, bool enabled = true, bool post = false, string id = "n1") => new(id, text, "button", enabled, true, post, false);
    private static PageSnapshot Snap(FakePage p, IReadOnlyList<PageControl>? controls = null, string? challenge = null, int? countdown = null, bool words = false, bool loading = false) => new(p.Url, loading, challenge, controls ?? Array.Empty<PageControl>(), countdown, words);
    private static BrowserGrab Grab(string name = "Part_1.zip") => new("http://127.0.0.1:1/file/" + name, name, "http://127.0.0.1:1/page", "sid=1", "TestAgent", null, null);
    private static PageResolveEngine Engine(FakeClock clock) => new() { Now = () => clock.Now, Delay = (t, _) => { clock.Now += t; return Task.CompletedTask; } };
    private static ResolveResult Resolve(FakePage page, PageLinkItem item, FakeClock clock, Func<bool>? skip = null, PageResolveEngine? engine = null) =>
        (engine ?? Engine(clock)).ResolveAsync(item, page, skip ?? (() => false), CancellationToken.None).GetAwaiter().GetResult();
    private static PageLinkItem Item(string name = "Part_1.zip", string url = "http://127.0.0.1:1/f/Part_1.zip") => new() { Url = url, Name = name, JobId = "j-" + name };

    // ------------------------------------------------------------------ pure tables
    private static void GroupingTable()
    {
        (string, string)[] I(params string[] names) => names.Select(n => (n, "files.example")).ToArray();
        LinkGroup[] G(params (string, string)[] items) => LinkGrouping.Group(items).ToArray();
        var g = G(I("Part_1.zip", "Part_2.zip", "Part_3.zip", "Part_4.zip"));
        Check(g.Length == 1 && g[0].Members.SequenceEqual(new[] { 0, 1, 2, 3 }) && g[0].Stem == "", "Part_1..Part_4 on one host are one set (no stem: the host names the package)");
        Check(LinkGrouping.SuggestName(g[0], h => "Files from " + h) == "Files from files.example", "an empty stem takes the host-based name");
        g = G(I("Part_4.zip", "Part_2.zip", "Part_3.zip", "Part_1.zip")); Check(g.Length == 1 && g[0].Members.SequenceEqual(new[] { 3, 1, 2, 0 }), "members come back in part order, not paste order");
        g = G(I("Holiday.part1.rar", "Holiday.part2.rar", "Holiday.part3.rar")); Check(g.Length == 1 && g[0].Stem == "Holiday" && LinkGrouping.SuggestName(g[0], h => h) == "Holiday", "name.partN.rar groups with the stem as the name");
        g = G(I("Holiday.part01.rar", "Holiday.part02.rar", "Holiday.part10.rar")); Check(g.Length == 1 && g[0].Members.SequenceEqual(new[] { 0, 1, 2 }), "zero padded part numbers (01, 02, 10)");
        g = G(I("Backup.zip", "Backup.z01", "Backup.z02")); Check(g.Length == 1 && g[0].Stem == "Backup" && g[0].Members.Count == 3 && g[0].Members[0] == 0, "name.zip with name.z01 name.z02 is one set, the .zip last-part file first by number 0");
        g = G(I("movie.001", "movie.002", "movie.003")); Check(g.Length == 1 && g[0].Stem == "movie", "name.001 .002 .003");
        g = G(I("tools.7z.001", "tools.7z.002")); Check(g.Length == 1 && g[0].Stem == "tools", "name.7z.001 .002");
        g = G(I("Archive (1 of 4).zip", "Archive (2 of 4).zip", "Archive (3 of 4).zip", "Archive (4 of 4).zip")); Check(g.Length == 1 && g[0].Stem == "Archive" && g[0].Members.Count == 4, "(1 of 4) numbering");
        g = G(I("game_01.zip", "game_02.zip", "game_03.zip")); Check(g.Length == 1 && g[0].Stem == "game", "name_01.zip numbering");
        g = G(I("Disc 1.iso", "Disc 2.iso")); Check(g.Length == 1, "Disc 1 / Disc 2");
        // not sets
        Check(G(I("Part_1.zip")).Length == 0, "a single numbered file is never grouped");
        Check(LinkGrouping.Group(new[] { ("Part_1.zip", "a.example"), ("Part_2.zip", "b.example") }).Count == 0, "the same names on two hosts are not one set");
        Check(G(I("Part_1.zip", "Part_1.zip")).Length == 0, "two links with the same part number are not one set");
        Check(G(I("song2.mp3", "song3.mp3")).Length == 0, "a number glued to the name (song2) is not a part number");
        Check(G(I("2024_05.zip", "2024_06.zip")).Length == 0, "a purely numeric name is not a set");
        Check(G(I("holiday.mp4", "notes.pdf", "setup.exe")).Length == 0, "unrelated files are not grouped");
        Check(G(I("a_1.zip", "b_2.zip")).Length == 0, "different stems are not one set");
        g = G(I("Alpha.part1.rar", "Alpha.part2.rar", "Beta.part1.rar", "Beta.part2.rar", "readme.txt")); Check(g.Length == 2 && g[0].Stem == "Alpha" && g[1].Stem == "Beta", "two sets pasted together are two groups");
        g = G(I("Alpha.part1.rar", "Alpha.part2.rar", "Alpha.part1.zip", "Alpha.part2.zip")); Check(g.Length == 2, "the same stem with different extensions stays two sets");
    }

    private static void MatcherTable()
    {
        foreach (var good in new[] { "Download", "download", "  Free   Download ", "Slow download", "Slow Download (free)", "Regular download", "Create download link", "Generate download link", "Get link", "Download now", "Start download", "Click here to download", "Continue to download", "Download file", "Continue", "Proceed" })
            Check(PageControlMatcher.Score(good) > 0, $"button text '{good.Trim()}' is a download control");
        foreach (var bad in new[] { "Premium download", "Buy now", "Buy premium", "Upgrade to download faster", "Fast download", "Download with our app", "Install extension", "Sign up to download", "Log in", "Watch video", "Download torrent", "VIP download", "Report file", "Share", "", "   ", new string('x', 80) + " download" })
            Check(PageControlMatcher.Score(bad) == 0, $"button text '{(bad.Length > 30 ? bad[..30] : bad).Trim()}' is not offered");
        var best = PageControlMatcher.Best(new[] { Ctl("Premium download", id: "n1"), Ctl("Download", enabled: false, id: "n2"), Ctl("Free download", id: "n3") });
        Check(best?.Id == "n3", "an enabled free download beats a disabled plain one and a premium one");
        best = PageControlMatcher.Best(new[] { Ctl("Download", id: "n1"), Ctl("Download", post: true, id: "n2") });
        Check(best?.Id == "n2", "a form post wins a tie");
        Check(PageControlMatcher.Best(new[] { new PageControl("n1", "Download", "a", true, false, false, false) }) is null, "a hidden control is never chosen");
        Check(PageControlMatcher.Best(new[] { Ctl("Home"), Ctl("About") }) is null, "no matching control gives none");
    }

    private static void SafetyAndScripts()
    {
        Check(ResolverSafety.IsWeb("https://a.example/x") && ResolverSafety.IsWeb("http://127.0.0.1:80/") && !ResolverSafety.IsWeb("ftp://a.example/x") && !ResolverSafety.IsWeb("file:///c:/x") && !ResolverSafety.IsWeb("javascript:alert(1)") && !ResolverSafety.IsWeb("https://user:pw@a.example/") && !ResolverSafety.IsWeb("data:text/html,x") && !ResolverSafety.IsWeb(null), "only http and https web addresses without a user name are web addresses");
        Check(ResolverSafety.AllowNavigation("https://files.example/a", "https://files.example/download", false) && ResolverSafety.AllowNavigation("https://files.example/a", "https://cdn.files.example/x", false), "same-site navigation is allowed");
        Check(!ResolverSafety.AllowNavigation("https://files.example/a", "https://ads.other.example/x", false) && ResolverSafety.AllowNavigation("https://files.example/a", "https://dl.other.example/x", true) && !ResolverSafety.AllowNavigation("https://files.example/a", "javascript:void(0)", true), "a cross-site hop is refused unless it follows our own click, and never to a non-web address");
        Check(ResolverSafety.CapturableAddress("https://x.example/f.zip") && !ResolverSafety.CapturableAddress("blob:https://x.example/1") && !ResolverSafety.CapturableAddress("data:text/plain,1"), "blob: and data: downloads cannot be captured");
        var snap = PageScripts.Parse("{\"url\":\"http://h/p\",\"loading\":false,\"challenge\":\"captcha\",\"controls\":[{\"id\":\"n1\",\"text\":\"Free Download\",\"tag\":\"button\",\"enabled\":false,\"visible\":true,\"formPost\":true,\"hasDownload\":false}],\"countdown\":12,\"timerWords\":true}", "");
        Check(snap.Url == "http://h/p" && snap.Challenge == "captcha" && snap.Controls.Count == 1 && !snap.Controls[0].Enabled && snap.Controls[0].FormPost && snap.Countdown == 12 && snap.TimerWords, "the scan result is read into a snapshot");
        var wrapped = System.Text.Json.JsonSerializer.Serialize("{\"url\":\"http://h/q\",\"loading\":false,\"challenge\":null,\"controls\":[],\"countdown\":null,\"timerWords\":false}");
        Check(PageScripts.Parse(wrapped, "").Url == "http://h/q", "WebView2's doubly encoded result is read too");
        Check(PageScripts.Parse("not json", "http://h/z").Loading && PageScripts.Parse(null, "").Loading, "unreadable scan results count as 'still loading'");
        Check(PageScripts.Click("n7").Contains("data-neodm-id=\"n7\"") && Throws(() => PageScripts.Click("n1'];alert(1);//")), "the click script only takes a plain control id");
        Check(!PageScripts.Scan.Contains("fetch(") && !PageScripts.Scan.Contains("XMLHttpRequest") && !PageScripts.Scan.Contains("setTimeout"), "the scan script reads the page only: no requests, no timers");
    }
    private static bool Throws(Action a) { try { a(); return false; } catch (ArgumentException) { return true; } }

    // ------------------------------------------------------------------ the engine with a scripted page
    private static void Engine_Scenarios()
    {
        // instant button
        var clock = new FakeClock(); var p = new FakePage { OnScan = pg => Snap(pg, new[] { Ctl("Download") }), OnClick = (pg, _) => pg.Capture = Grab() }; var item = Item();
        var r = Resolve(p, item, clock);
        Check(r.State == PageLinkState.Resolved && r.Grab!.Url.EndsWith("Part_1.zip") && item.State == PageLinkState.Resolved && p.Clicks == 1, "an instant button is clicked once and the download is captured");
        // timer: disabled until the countdown ends; never clicked early
        clock = new FakeClock(); int tick = 0; p = new FakePage();
        p.OnScan = pg => ++tick < 12 ? Snap(pg, new[] { Ctl("Free Download", enabled: false) }, countdown: 12 - tick) : Snap(pg, new[] { Ctl("Free Download") });
        p.OnClick = (pg, _) => { Check(tick >= 12, "the control is clicked only after the site's timer enabled it"); pg.Capture = Grab(); };
        var states = new List<PageLinkState>(); item = Item(); item.PropertyChanged += (_, e) => { if (e.PropertyName == "State") states.Add(item.State); };
        r = Resolve(p, item, clock);
        Check(r.State == PageLinkState.Resolved && p.Clicks == 1 && states.Contains(PageLinkState.WaitingTimer) && states.IndexOf(PageLinkState.WaitingTimer) < states.IndexOf(PageLinkState.Clicked), "a page timer is waited out (state: waiting for the site's timer), then clicked");
        // an enabled button while a countdown still shows is not clicked (the timer is never skipped)
        clock = new FakeClock(); tick = 0; p = new FakePage { OnScan = pg => ++tick < 8 ? Snap(pg, new[] { Ctl("Download") }, countdown: 8 - tick) : Snap(pg, new[] { Ctl("Download") }) };
        p.OnClick = (pg, _) => { Check(tick >= 8, "no click while the countdown is still showing"); pg.Capture = Grab(); }; r = Resolve(p, Item(), clock);
        Check(r.State == PageLinkState.Resolved, "the click waits for the countdown to finish even when the button looks enabled");
        // a long timer (100 s) outlives the 3 minute budget thanks to the page's own timer
        clock = new FakeClock(); var started = clock.Now; p = new FakePage { OnScan = pg => (clock.Now - started).TotalSeconds < 170 ? Snap(pg, new[] { Ctl("Download", enabled: false) }, countdown: (int)(170 - (clock.Now - started).TotalSeconds)) : Snap(pg, new[] { Ctl("Download") }) };
        p.OnClick = (pg, _) => pg.Capture = Grab(); r = Resolve(p, Item(), clock, engine: new PageResolveEngine { Budget = TimeSpan.FromSeconds(60), Now = () => clock.Now, Delay = (t, _) => { clock.Now += t; return Task.CompletedTask; } });
        Check(r.State == PageLinkState.Resolved && (clock.Now - started).TotalSeconds >= 170, "a timer longer than the budget extends the budget (never cut short)");
        // form post variant
        clock = new FakeClock(); p = new FakePage { OnScan = pg => Snap(pg, new[] { Ctl("Create download link", post: true) }), OnClick = (pg, _) => pg.Capture = Grab() }; r = Resolve(p, Item(), clock);
        Check(r.State == PageLinkState.Resolved, "a form post button ('Create download link') works the same way");
        // two steps: first page button leads to a second page with its own timer
        clock = new FakeClock(); string page = "one"; int t2 = 0; p = new FakePage();
        p.OnScan = pg => { pg.Url = "http://127.0.0.1:1/" + page; return page == "one" ? Snap(pg, new[] { Ctl("Free Download") }) : ++t2 < 5 ? Snap(pg, new[] { Ctl("Download", enabled: false) }, countdown: 5 - t2) : Snap(pg, new[] { Ctl("Download") }); };
        p.OnClick = (pg, _) => { if (page == "one") page = "two"; else pg.Capture = Grab(); }; r = Resolve(p, Item(), clock);
        Check(r.State == PageLinkState.Resolved && p.Clicks == 2, "a second page with its own timer is handled (two clicks, one per page)");
        // captcha-like challenge: handed to the person, never clicked past, resumes by itself
        clock = new FakeClock(); tick = 0; p = new FakePage(); item = Item(); var seen = new List<PageLinkState>(); item.PropertyChanged += (_, e) => { if (e.PropertyName == "State") seen.Add(item.State); };
        p.OnScan = pg => { tick++; return tick < 10 ? Snap(pg, new[] { Ctl("Download", enabled: false) }, challenge: "captcha") : Snap(pg, new[] { Ctl("Download") }); };
        p.OnClick = (pg, _) => { Check(tick >= 10, "nothing is clicked while a human check is on screen"); pg.Capture = Grab(); };
        r = Resolve(p, item, clock);
        Check(r.State == PageLinkState.Resolved && seen.Contains(PageLinkState.NeedsYou) && p.Log.Contains("msg:pagelink.window.needs_you") && p.Log.IndexOf("present:True") < p.Log.IndexOf("click:n1"), "a human check brings the window to the front with the message and pauses");
        Check(p.Log.Contains("present:False"), "after the check is gone the window goes back to its quiet size");
        // no recognisable button: says so, offers the page, a human click is still captured
        clock = new FakeClock(); tick = 0; p = new FakePage(); item = Item(); seen.Clear(); item.PropertyChanged += (_, e) => { if (e.PropertyName == "State") seen.Add(item.State); };
        p.OnScan = pg => { tick++; if (tick == 80) pg.Capture = Grab(); return Snap(pg, new[] { Ctl("Home"), Ctl("About us") }); };
        r = Resolve(p, item, clock);
        Check(r.State == PageLinkState.Resolved && seen.Contains(PageLinkState.NoControl) && p.Log.Contains("msg:pagelink.window.no_control") && p.Clicks == 0, "no recognisable button: NoControl state, window shown, a click by the person is captured, NeoDM clicked nothing");
        // login wall, then the person skips it
        clock = new FakeClock(); bool skipNow = false; tick = 0; p = new FakePage { OnScan = pg => { if (++tick == 6) skipNow = true; return Snap(pg, challenge: "login"); } }; item = Item();
        r = Resolve(p, item, clock, () => skipNow);
        Check(r.State == PageLinkState.Skipped && item.State == PageLinkState.Skipped && p.Clicks == 0, "a login wall waits for the person; 'Skip this link' ends it");
        // nobody solves the check: fails after the human budget
        clock = new FakeClock(); p = new FakePage { OnScan = pg => Snap(pg, challenge: "captcha") }; item = Item(); r = Resolve(p, item, clock);
        Check(r.State == PageLinkState.Failed && item.DetailKey == "pagelink.fail.nobody" && (clock.Now - new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc)).TotalMinutes is >= 9 and < 12, "an unsolved check ends after the human budget (10 minutes) with a plain reason");
        // cross-site refusal
        clock = new FakeClock(); p = new FakePage { OnScan = pg => { pg.Refusal = "ads.other.example"; return Snap(pg, new[] { Ctl("Download", enabled: false) }, countdown: 5); } }; item = Item(); r = Resolve(p, item, clock);
        Check(r.State == PageLinkState.Failed && item.DetailKey == "pagelink.fail.left_site", "a refused cross-site navigation fails the link with a plain reason");
        // a click that gives nothing: times out
        clock = new FakeClock(); p = new FakePage { OnScan = pg => Snap(pg, new[] { Ctl("Download") }) }; item = Item(); r = Resolve(p, item, clock);
        Check(r.State == PageLinkState.Failed && item.DetailKey == "pagelink.fail.timeout" && p.Clicks == 1, "a page that never yields a download fails after the budget, clicked once (no click loop)");
        // not a web address
        p = new FakePage(); item = Item(url: "file:///c:/x"); r = Resolve(p, item, new FakeClock());
        Check(r.State == PageLinkState.Failed && item.DetailKey == "pagelink.fail.address" && p.Log.Count == 0, "a non-web address is refused before anything loads");
        // cancel
        clock = new FakeClock(); using var cts = new CancellationTokenSource(); p = new FakePage { OnScan = pg => { cts.Cancel(); return Snap(pg, new[] { Ctl("Download", enabled: false) }, countdown: 30); } }; item = Item();
        bool cancelled = false; try { Engine(clock).ResolveAsync(item, p, () => false, cts.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && item.State == PageLinkState.Cancelled, "cancelling stops the link at once");
        // a download address that is not capturable (blob:) is ignored
        clock = new FakeClock(); tick = 0; p = new FakePage { OnScan = pg => { if (++tick == 2) pg.Capture = new BrowserGrab("blob:http://x/1", "a", null, null, null, null, null); if (tick == 4) pg.Capture = Grab(); return Snap(pg, new[] { Ctl("Download") }); } }; r = Resolve(p, Item(), clock);
        Check(r.State == PageLinkState.Resolved && r.Grab!.Url.StartsWith("http://"), "a blob: capture is ignored, the next real one is taken");
    }

    private static void Coordinator_Scenarios()
    {
        var clock = new FakeClock(); var order = new List<string>(); var resolved = new List<(string, string)>(); int concurrent = 0, peak = 0; FakePage? shared = null; int sessions = 0;
        string current = "";
        IPageSession Factory() { sessions++; shared = new FakePage(); shared.OnScan = pg => Snap(pg, new[] { Ctl("Download") }); shared.OnClick = (pg, _) => pg.Capture = Grab(current); return new Wrapped(shared, () => { concurrent++; peak = Math.Max(peak, concurrent); }, () => concurrent--, name => { current = name; order.Add(name); }); }
        var co = new PageResolveCoordinator(Factory, (it, g) => { resolved.Add((it.Name, g.Url)); return Task.CompletedTask; }, Engine(clock));
        co.Add(Enumerable.Range(1, 4).Select(n => Item($"Part_{n}.zip", $"http://127.0.0.1:1/f/Part_{n}.zip")));
        co.RunAsync().GetAwaiter().GetResult();
        Check(order.SequenceEqual(new[] { "Part_1.zip", "Part_2.zip", "Part_3.zip", "Part_4.zip" }) && resolved.Select(x => x.Item1).SequenceEqual(order) && sessions == 1 && peak == 1, "four links are resolved in order, one at a time, in one window");
        Check(co.Items.All(i => i.State == PageLinkState.Resolved) && !co.Running, "all four are resolved and the coordinator is idle");
        // skip the second, cancel the rest
        clock = new FakeClock(); order.Clear(); resolved.Clear(); PageResolveCoordinator? co2 = null; int scans = 0;
        IPageSession Factory2() { var f = new FakePage(); f.OnScan = pg => { scans++; if (pg.Url.EndsWith("Part_2.zip") && scans > 0) co2!.SkipCurrent(); return pg.Url.EndsWith("Part_2.zip") ? Snap(pg, new[] { Ctl("Home") }) : Snap(pg, new[] { Ctl("Download") }); }; f.OnClick = (pg, _) => pg.Capture = Grab(pg.Url[(pg.Url.LastIndexOf('/') + 1)..]); return f; }
        co2 = new PageResolveCoordinator(Factory2, (it, g) => { resolved.Add((it.Name, g.Url)); return Task.CompletedTask; }, Engine(clock));
        co2.Add(Enumerable.Range(1, 3).Select(n => Item($"Part_{n}.zip", $"http://127.0.0.1:1/f/Part_{n}.zip"))); co2.RunAsync().GetAwaiter().GetResult();
        Check(co2.Items.Select(i => i.State).SequenceEqual(new[] { PageLinkState.Resolved, PageLinkState.Skipped, PageLinkState.Resolved }) && resolved.Count == 2, "skipping one link moves on to the next; the others are unaffected");
        // cancel all
        clock = new FakeClock(); PageResolveCoordinator? co3 = null;
        IPageSession Factory3() { var f = new FakePage(); f.OnScan = pg => { co3!.CancelAll(); return Snap(pg, new[] { Ctl("Download", enabled: false) }, countdown: 20); }; return f; }
        co3 = new PageResolveCoordinator(Factory3, (_, _) => Task.CompletedTask, Engine(clock)); co3.Add(Enumerable.Range(1, 3).Select(n => Item($"Part_{n}.zip"))); co3.RunAsync().GetAwaiter().GetResult();
        Check(co3.Items.All(i => i.State == PageLinkState.Cancelled) && !co3.Running, "cancel all stops the running link and cancels the rest");
        // hand-over failure is reported on the link, the next link still runs
        clock = new FakeClock(); int n2 = 0;
        IPageSession Factory4() { var f = new FakePage { OnScan = pg => Snap(pg, new[] { Ctl("Download") }) }; f.OnClick = (pg, _) => pg.Capture = Grab(); return f; }
        var co4 = new PageResolveCoordinator(Factory4, (_, _) => ++n2 == 1 ? throw new InvalidOperationException("job gone") : Task.CompletedTask, Engine(clock)); co4.Add(new[] { Item("a.zip"), Item("b.zip") }); co4.RunAsync().GetAwaiter().GetResult();
        Check(co4.Items[0].State == PageLinkState.Failed && co4.Items[0].DetailKey == "pagelink.fail.handover" && co4.Items[1].State == PageLinkState.Resolved, "a failed hand-over marks that link failed and the next one runs");
    }

    /// <summary>Counts how many links are inside the page at once and reports the file name each load asks for.</summary>
    private sealed class Wrapped(FakePage inner, Action enter, Action leave, Action<string> loaded) : IPageSession
    {
        public Task LoadAsync(string url, CancellationToken token) { enter(); loaded(url[(url.LastIndexOf('/') + 1)..]); return inner.LoadAsync(url, token); }
        public Task<PageSnapshot> ScanAsync(CancellationToken token) => inner.ScanAsync(token);
        public Task<bool> ClickAsync(string controlId, CancellationToken token) => inner.ClickAsync(controlId, token);
        public BrowserGrab? TakeCapture() { var c = inner.TakeCapture(); if (c is not null) leave(); return c; }
        public string? TakeRefusal() => inner.TakeRefusal();
        public void NoteClick() { }
        public void Present(bool front) => inner.Present(front);
        public void SetHumanMessage(string key) => inner.SetHumanMessage(key);
        public void Dispose() { }
    }

    private static void PageResolverLogic()
    {
        GroupingTable(); MatcherTable(); SafetyAndScripts(); Engine_Scenarios(); Coordinator_Scenarios();
        CheckLocales(PageKeys, "1.16.0 page links");
    }
private static readonly string[] PageKeys =
    {
        "grab.label.queue",
        "grab.chk.autostart",
        "grab.tip.autostart",
        "grab.queue.new_for_package",
        "grab.pkg.default_name",
        "grab.pkg.suggest",
        "grab.pkg.several",
        "grab.page.hint",
        "pagelink.notice.started",
        "pagelink.title",
        "pagelink.heading",
        "pagelink.heading_package",
        "pagelink.intro",
        "pagelink.a11y.list",
        "pagelink.col.name",
        "pagelink.col.status",
        "pagelink.btn.show",
        "pagelink.btn.skip",
        "pagelink.btn.cancel_all",
        "pagelink.summary_running",
        "pagelink.summary_done",
        "pagelink.needs_you.title",
        "pagelink.needs_you.body",
        "pagelink.no_control.title",
        "pagelink.no_control.body",
        "pagelink.window.title",
        "pagelink.window.needs_you",
        "pagelink.window.no_control",
        "pagelink.state.waiting",
        "pagelink.state.loading",
        "pagelink.state.timer",
        "pagelink.state.timer_seconds",
        "pagelink.state.clicked",
        "pagelink.state.needs_you",
        "pagelink.state.no_control",
        "pagelink.state.resolved",
        "pagelink.state.skipped",
        "pagelink.state.cancelled",
        "pagelink.state.failed",
        "pagelink.fail.address",
        "pagelink.fail.load",
        "pagelink.fail.left_site",
        "pagelink.fail.no_download",
        "pagelink.fail.nobody",
        "pagelink.fail.timeout",
        "pagelink.fail.handover",
    };
}
