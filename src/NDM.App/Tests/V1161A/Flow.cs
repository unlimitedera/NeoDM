// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using NDM.App;
using NDM.Core;

internal static partial class V1161ATests
{
    private sealed class FakeClock { public int Seconds; public Func<TimeSpan, CancellationToken, Task> Delay => (t, c) => { Seconds += (int)t.TotalSeconds; c.ThrowIfCancellationRequested(); return Task.CompletedTask; }; }

    private static HostFormEngine Engine(FakeClock clock, IHumanCaptchaStep? step, Func<Uri, bool, string>? referer = null)
        => new() { Delay = clock.Delay, Captcha = step, RefererPolicy = referer ?? HostQuirks.RefererFor };

    private static FormFlowResult Run(HostFormEngine e, PageLinkItem item, Func<bool>? skip = null, CancellationToken token = default)
        => Task.Run(() => e.ResolveAsync(item, skip ?? (() => false), token)).GetAwaiter().GetResult();

    private static PageLinkItem Item(string url, string name = "Part.zip") => new() { Url = url, Name = name };

    private static void Flow()
    {
        using var host = new XfsHost { Countdown = 5 };
        var person = new StandInPerson { Host = host }; var clock = new FakeClock();
        var item = Item(host.FileUrl("fileid0001")); var states = new List<(PageLinkState S, int? C)>();
        item.PropertyChanged += (_, e) => { if (e.PropertyName is "State" or "Countdown") states.Add((item.State, item.Countdown)); };
        var engine = Engine(clock, person);
        var r = Run(engine, item);
        Check(r.Result.State == PageLinkState.Resolved && r.Result.Grab is not null, "captcha host: resolved with a direct link");
        Check(clock.Seconds >= 5 && engine.LastWaitedSeconds >= 5, $"countdown honoured: waited {clock.Seconds} s for a stated 5 s (never less)");
        var counts = states.Where(x => x.S == PageLinkState.WaitingTimer && x.C is not null).Select(x => x.C!.Value).ToList();
        Check(counts.Count >= 5 && counts[0] == 5 && counts.SequenceEqual(counts.OrderByDescending(x => x)), "live countdown numbers shown 5,4,3,2,1");
        int needs = states.FindIndex(x => x.S == PageLinkState.NeedsYou), clicked = states.FindLastIndex(x => x.S == PageLinkState.Clicked);
        Check(needs >= 0 && clicked > needs, "state goes timer -> needs you (captcha) -> posting -> resolved");
        Check(person.Calls == 1 && person.Tokens.Count == 1, "one human step for the one link");
        var g = r.Result.Grab!;
        Check(g.Url == host.FileUrl("fileid0001", "Part.zip").Replace("fileid0001/", "files/fileid0001/") && g.FileName == "Part.zip", "grab: direct link and file name from the host");
        Check(g.Cookie is { Length: > 0 } && g.Cookie.Contains("file_code=") && g.Referer is not null && g.UserAgent!.StartsWith("Mozilla/5.0"), "grab carries cookie, referer and browser-like user agent for the engine");
        Check(host.Seen.Any(s => s.Path.StartsWith("/files/") && s.HasCookie), "the probe of the link used the cookie jar");
        Check(host.Seen.Where(s => !s.Path.StartsWith("/files/")).All(s => s.UserAgent!.StartsWith("Mozilla/5.0")), "every page request used the browser-like user agent");

        // no captcha: no human step at all
        host.Captcha = false; person.Calls = 0; clock.Seconds = 0;
        var item2 = Item(host.FileUrl("fileid0002")); var r2 = Run(engine, item2);
        Check(r2.Result.State == PageLinkState.Resolved && person.Calls == 0 && clock.Seconds >= 5, "no-captcha host: resolved, timer waited, nobody asked");

        // the page is a file already
        host.DirectFileAtPage = true; var r3 = Run(engine, Item(host.FileUrl("fileid0003"))); host.DirectFileAtPage = false;
        Check(r3.Result.State == PageLinkState.Resolved && r3.Result.Grab!.Url.Contains("fileid0003"), "a link that is already a file is handed over as it is");

        // not a form page / image captcha: the embedded browser takes over
        host.NotAForm = true; var r4 = Run(engine, Item(host.FileUrl("fileid0004"))); host.NotAForm = false;
        Check(r4.Unrecognised, "page without a form: not recognised, falls back to the embedded browser");
        host.Captcha = false; host.ImageCaptcha = true; var r5 = Run(engine, Item(host.FileUrl("fileid0005"))); host.ImageCaptcha = false; host.Captcha = true;
        Check(r5.Unrecognised && r5.Detail!.Contains("Image"), "image captcha: the browser step cannot carry it, falls back");
        var r5b = Run(Engine(clock, null), Item(host.FileUrl("fileid0006")));
        Check(r5b.Unrecognised, "captcha without a browser step wired: falls back instead of guessing");

        // wrong Referer: retry with the host's own origin, then give up with a clear reason
        host.RefuseUnlessOrigin = true; clock.Seconds = 0;
        var rr = Run(Engine(clock, person, (u, retry) => retry ? u.GetLeftPart(UriPartial.Authority) + "/" : "https://elsewhere.example/users"), Item(host.FileUrl("fileid0007")));
        Check(rr.Result.State == PageLinkState.Resolved, "refused Referer: retried with the host's own origin and resolved");
        host.AlwaysRefuse = true; var rf = Run(Engine(clock, person), Item(host.FileUrl("fileid0008"))); host.AlwaysRefuse = false; host.RefuseUnlessOrigin = false;
        Check(rf.Result.State == PageLinkState.Failed && rf.Result.DetailKey == "pagelink.fail.refused", "refused twice: failed with the plain 'refused' reason");

        // token missing: stays waiting; cancel and skip always work
        var gate = new StandInPerson { Host = host, Mode = "gate", Gate = new TaskCompletionSource<string>() };
        var it = Item(host.FileUrl("fileid0009")); using var cts = new CancellationTokenSource();
        var gateEngine = Engine(clock, gate); var pending = Task.Run(() => gateEngine.ResolveAsync(it, () => false, cts.Token)); Thread.Sleep(600);
        Check(!pending.IsCompleted && it.State == PageLinkState.NeedsYou, "no token yet: the link stays at 'needs you'");
        cts.Cancel(); bool cancelled = false; try { pending.GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && it.State == PageLinkState.Cancelled, "cancel while waiting for the person ends the link");
        var skipper = new StandInPerson { Host = host, Mode = "skip" }; var rs = Run(Engine(clock, skipper), Item(host.FileUrl("fileid0010")));
        Check(rs.Result.State == PageLinkState.Skipped, "the person skips the link");
        bool skipNow = true; var rs2 = Run(engine, Item(host.FileUrl("fileid0011")), () => skipNow);
        Check(rs2.Result.State == PageLinkState.Skipped, "skip before the timer ends: skipped at once");
        var timeout = new StandInPerson { Host = host, Mode = "timeout" }; var rt = Run(Engine(clock, timeout), Item(host.FileUrl("fileid0012")));
        Check(rt.Result.State == PageLinkState.Failed && rt.Result.DetailKey == "pagelink.fail.nobody", "nobody came in time: failed with the plain 'nobody' reason");

        // a token is used once: the same token on a second link is refused by the host
        var tok = host.IssueToken(); var fixedStep = new StandInPerson { Host = host, Mode = "fixed", Fixed = tok };
        var ra = Run(Engine(clock, fixedStep), Item(host.FileUrl("fileid0013"))); var rb = Run(Engine(clock, fixedStep), Item(host.FileUrl("fileid0014")));
        Check(ra.Result.State == PageLinkState.Resolved && rb.Result.State == PageLinkState.Failed && rb.Result.DetailKey == "pagelink.fail.captcha_wrong", "a token worked once; the same token on the next link is refused");

        // four links in order, four separate challenges
        var four = new StandInPerson { Host = host }; var order = new List<string>(); var e4 = Engine(clock, four);
        foreach (var n in new[] { "a", "b", "c", "d" }) { var li = Item(host.FileUrl("four" + n)); var res = Run(e4, li); Check(res.Result.State == PageLinkState.Resolved, "link " + n + " resolved"); order.Add(res.Result.Grab!.Url); }
        Check(four.Calls == 4 && four.Tokens.Distinct().Count() == 4, "four links: four human steps with four different tokens");
        Check(order.Select(u => u.Split('/')[^2]).SequenceEqual(new[] { "foura", "fourb", "fourc", "fourd" }), "four links resolved in the order given");
    }
}
