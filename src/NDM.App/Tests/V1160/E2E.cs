// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Windows;
using NDM.App;
using NDM.Core;

internal static partial class V1160Tests
{
    private static string? WebView2Version() { try { return Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch (Exception) { return null; } }

    /// <summary>Opt-in (--e2e-resolver): the real Edge WebView2 against the local fixture file host. One coordinator, eight links in order: instant button, page timer, form post, a fixture "challenge" the test removes by script
    /// (standing in for the person), a page with no recognisable button (the test clicks it, standing in for the person), a login wall (skipped), a cross-site redirect (refused), and one more instant link after all that.</summary>
    private static void EndToEndResolver(string root)
    {
        if (string.IsNullOrEmpty(WebView2Version())) { Console.WriteLine("SKIP: no WebView2 runtime on this machine, the resolver end-to-end check was not run"); return; }
        using var host = new FileHostFixture(); using var m = NewManager(root, "e2e", 2);
        var data = Path.Combine(root, "e2e", "browser-data"); PageResolverWindow? win = null;
        var specs = new (string Kind, string Name)[] { ("instant", "Part_1.zip"), ("timer", "Part_2.zip"), ("post", "Part_3.zip"), ("captcha", "Part_4.zip"), ("nobutton", "Part_5.zip"), ("login", "Part_6.zip"), ("cross", "Part_7.zip"), ("instant", "Part_8.zip") };
        var jobs = specs.Select(s => Sync(m.AddPausedAsync(new DownloadRequest(host.Link(s.Kind, s.Name), s.Name, QueueId: null, Package: "e2e")))).ToList();
        var grabs = new Dictionary<string, BrowserGrab>(); var seen = new Dictionary<string, List<PageLinkState>>(); var stamps = new Dictionary<string, (DateTime Start, DateTime End)>();
        var engine = new PageResolveEngine { NoControlGrace = TimeSpan.FromSeconds(5), HumanBudget = TimeSpan.FromMinutes(2), Tick = TimeSpan.FromMilliseconds(300) };
        var co = new PageResolveCoordinator(() => win = new PageResolverWindow(data), (it, g) => { grabs[it.Name] = g; stamps[it.Name] = (stamps[it.Name].Start, DateTime.UtcNow); m.RefreshFromBrowser(it.JobId!, g); m.Resume(it.JobId!); return Task.CompletedTask; }, engine);
        var items = specs.Select((s, i) => new PageLinkItem { Url = host.Link(s.Kind, s.Name), Name = s.Name, JobId = jobs[i].Id }).ToList();
        foreach (var it in items) { var list = seen[it.Name] = new(); var captured = it; it.PropertyChanged += (_, e) => { if (e.PropertyName == "State") { list.Add(captured.State); if (captured.State == PageLinkState.Loading && !stamps.ContainsKey(captured.Name)) stamps[captured.Name] = (DateTime.UtcNow, default); } }; }
        co.Add(items);
        Task run = Task.CompletedTask; Dispatch(() => run = co.RunAsync()); var helped = new HashSet<string>();
        var end = DateTime.UtcNow.AddMinutes(4);
        while (!run.IsCompleted && DateTime.UtcNow < end)
        {
            Pump(250);
            var cur = co.Current;
            if (cur is { State: PageLinkState.NeedsYou } && win is not null && helped.Add(cur.Name))
            {
                if (cur.Name == "Part_4.zip") { Check(win.BannerVisible && win.InFront && win.BannerText.StartsWith("This site needs you."), "the browser window came to the front with the message for the fixture check"); _ = win.RunScriptForTestAsync("solve()"); } // the "person" completes the fixture check
                else if (cur.Name == "Part_6.zip") { Check(cur.Challenge == "login", "a password field is reported as a login wall"); co.SkipCurrent(); }
            }
            if (cur is { State: PageLinkState.NoControl } && win is not null && helped.Add(cur.Name + "!")) { Check(win.BannerVisible && win.BannerText.Contains("could not find the download button"), "no recognisable button: the window says so plainly"); _ = win.RunScriptForTestAsync("document.getElementById('go').click()"); } // the "person" clicks it
        }
        Check(run.IsCompleted, "the resolver finished all links");
        string S(string n) => string.Join(">", seen[n]);
        Check(grabs.ContainsKey("Part_1.zip") && grabs["Part_1.zip"].Url == $"{host.Base}/file/Part_1.zip", "instant: captured " + S("Part_1.zip"));
        Check(seen["Part_2.zip"].Contains(PageLinkState.WaitingTimer) && grabs.ContainsKey("Part_2.zip") && (stamps["Part_2.zip"].End - stamps["Part_2.zip"].Start).TotalSeconds >= 2.5, "timer: waited out the page's 3 second countdown before clicking " + S("Part_2.zip"));
        Check(grabs.TryGetValue("Part_3.zip", out var post) && post.Url == $"{host.Base}/file/Part_3.zip", "form post: the button posted the form and the host's own redirect to the file was captured (" + S("Part_3.zip") + ")");
        Check(seen["Part_4.zip"].Contains(PageLinkState.NeedsYou) && grabs.ContainsKey("Part_4.zip"), "challenge: needs you, then continued by itself after the person finished it " + S("Part_4.zip"));
        Check(seen["Part_5.zip"].Contains(PageLinkState.NoControl) && grabs.ContainsKey("Part_5.zip"), "no recognisable button: offered the page, the person's click was still captured " + S("Part_5.zip"));
        Check(items[5].State == PageLinkState.Skipped && !grabs.ContainsKey("Part_6.zip"), "login wall: waited for the person, then skipped");
        Check(items[6].State == PageLinkState.Failed && items[6].DetailKey == "pagelink.fail.left_site" && !grabs.ContainsKey("Part_7.zip"), "cross-site redirect: refused, not followed (" + items[6].DetailKey + ")");
        Check(grabs.ContainsKey("Part_8.zip"), "a link after the failures still resolves");
        Check(grabs.Values.All(g => g.Cookie is { } c && c.Contains("file_code=abc123") && g.UserAgent is { } ua && ua.Contains("Edg/")), "every capture carries the cookie of the file's own host and the browser's user agent");
        Check(!grabs.Values.Any(g => g.Url.Contains("localhost")), "nothing was followed to the other site");
        Check(grabs.Keys.SequenceEqual(new[] { "Part_1.zip", "Part_2.zip", "Part_3.zip", "Part_4.zip", "Part_5.zip", "Part_8.zip" }), "captures arrived in link order (one link at a time)");
        PumpUntil(() => items.Where(i => i.State == PageLinkState.Resolved).All(i => Job(m, i.JobId!).Status == "Completed"), "the resolved jobs finished", 60000);
        foreach (var it in items.Where(i => i.State == PageLinkState.Resolved)) Check(File.ReadAllBytes(Job(m, it.JobId!).FilePath).SequenceEqual(FileHostFixture.Bytes(it.Name)), $"{it.Name}: exact bytes through the captured address and cookie");
        Check(host.FileWithoutCookie == 0, "the fixture never saw a file request without the browser's cookie");
        PumpUntil(() => win is null || !win.IsLoaded, "the browser window closed after the run", 10000);
        Pump(500); bool cleared = false; for (int i = 0; i < 20 && !cleared; i++) { cleared = NeoBrowserData.Clear(out _, data); if (!cleared) Pump(300); }
        Check(cleared, "the private profile folder can be cleared afterwards");
    }

    /// <summary>Opt-in (--real-datanodes): the approved careful run on the four test links. Isolated state, stops at the first human check and reports it, deletes the downloads afterwards. Logs states and timings only (no cookies, no addresses beyond the link host).</summary>
    private static void RealDatanodes(string root)
    {
        var log = new List<string>(); DateTime t0 = DateTime.UtcNow; void L(string s) { var line = $"[{(DateTime.UtcNow - t0).TotalSeconds,6:0.0}s] {s}"; log.Add(line); Console.WriteLine(line); }
        if (string.IsNullOrEmpty(WebView2Version())) { Console.WriteLine("SKIP: no WebView2 runtime"); return; }
        string[] links = { "https://datanodes.to/li77lxy835bq/Part_1.zip", "https://datanodes.to/uock9c61or8s/Part_2.zip", "https://datanodes.to/1yrwl4sc8fro/Part_3.zip", "https://datanodes.to/pg0a1r450vpd/Part_4.zip" };
        using var m = NewManager(root, "real", 2); var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "real", "files") };
        var logic = new LinkGrabberLogic(m, settings); logic.Load(string.Join(Environment.NewLine, links));
        L($"grabber found {logic.Entries.Count} links; groups: {logic.FindGroups(logic.Entries).Count}");
        var outcome = Sync(logic.AddSelectedAsync(new GrabberDestination(null, null, false), true, new PackagePlan(null, "datanodes test", true)));
        L($"added {outcome.Added} jobs, page links: {outcome.PageLinks?.Count ?? 0}; states after probe: " + string.Join(", ", logic.Entries.Select(e => e.State)));
        var data = Path.Combine(root, "real", "browser-data"); PageResolverWindow? win = null; bool stopped = false; string? stopReason = null;
        var co = new PageResolveCoordinator(() => win = new PageResolverWindow(data), (it, g) => { L($"{it.Name}: CAPTURED file address host={LoginBrowserLogic.DisplayHost(g.Url)} name={g.FileName} cookie={(g.Cookie is null ? "none" : "present")}"); m.RefreshFromBrowser(it.JobId!, g); m.Resume(it.JobId!); return Task.CompletedTask; }, new PageResolveEngine { Tick = TimeSpan.FromMilliseconds(500) });
        if (outcome.PageLinks is { Count: > 0 } pages)
        {
            foreach (var it in pages) { var item = it; item.PropertyChanged += (_, e) => { if (e.PropertyName == "State") L($"{item.Name}: {item.State}{(item.State == PageLinkState.NeedsYou ? " (" + item.Challenge + ")" : "")}{(item.State == PageLinkState.Failed ? " reason=" + item.DetailKey : "")}"); if (e.PropertyName == "State" && item.State is PageLinkState.NeedsYou or PageLinkState.NoControl && !stopped) { stopped = true; stopReason = $"{item.Name}: {item.State} {item.Challenge}"; } }; }
            co.Add(pages); Task run = Task.CompletedTask; Dispatch(() => run = co.RunAsync()); var end = DateTime.UtcNow.AddMinutes(8); int lastTimer = -1;
            while (!run.IsCompleted && DateTime.UtcNow < end)
            {
                Pump(300);
                if (co.Current is { State: PageLinkState.WaitingTimer, Countdown: { } c } && c != lastTimer && c % 5 == 0) { lastTimer = c; L($"{co.Current.Name}: page countdown shows {c} s"); }
                if (stopped) { L("STOP at the first human check: " + stopReason + "; the window is left as is, nothing is solved or worked around"); co.CancelAll(); break; }
            }
            PumpUntil(() => run.IsCompleted, "resolver stops", 20000);
        }
        PumpUntil(() => m.Jobs.All(j => j.Status is "Completed" or "Failed" or "Paused" or "Canceled"), "jobs settle", 120000);
        foreach (var j in m.Jobs) L($"job {j.FileName}: {j.Status} {j.DownloadedBytes} bytes{(j.Error is { } e ? " error=" + e : "")}");
        foreach (var j in m.Jobs.ToList()) { try { m.Remove(j.Id, true); } catch (Exception) { } }
        L("downloaded test files removed from the isolated folder");
        File.WriteAllLines("F:/NDM/artifacts/logs/real-datanodes-1160.log", log);
    }
}
