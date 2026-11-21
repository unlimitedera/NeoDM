// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using NDM.App;
using NDM.Core;
using NDM.Core.Captcha;

internal static partial class V1162Tests
{
    /// <summary>Records presses, never touches the mouse.</summary>
    private sealed class AutoClicker : IRealClicker
    {
        public readonly List<string> Pressed = new();
        public void Click(int x, int y) => Click(new ClickTarget(PixelRect.Of(x, y), 1, IntPtr.Zero));
        public void Click(ClickTarget target) => Pressed.Add(target.X + "," + target.Y);
    }

    private static PageLinkItem AutoItem(string url = "https://pages.example/download/abc", string job = "j-1") => new() { Url = url, Name = "Part_1.zip", JobId = job };

    private static V1162Tests_Rig NewPath(TimeSpan? wait = null)
    {
        var clicker = new AutoClicker(); var opened = new List<string>(); var notes = new List<string>();
        var path = new CaptchaPathA
        {
            Clicker = () => clicker,
            Screen = () => new PixelRect(0, 0, 1920, 1080),
            Launcher = opened.Add,
            Note = notes.Add,
            Wait = wait ?? TimeSpan.FromSeconds(5),
            Delay = (_, c) => { c.ThrowIfCancellationRequested(); return Task.Delay(10); },
        };
        return new V1162Tests_Rig(path, clicker, opened, notes);
    }

    private sealed record V1162Tests_Rig(CaptchaPathA Path, AutoClicker Clicker, List<string> Opened, List<string> Notes);

    /// <summary>Path A: page opened, widget reported, the press lands on the widget's middle, the answer arrives and the page's own download continues the same job.</summary>
    private static void AutoPress()
    {
        var rig = NewPath();
        var item = AutoItem();
        var delivered = new List<BrowserGrab>();
        var run = Task.Run(() => rig.Path.RunAsync(item, new Uri(item.Url), g => delivered.Add(g), CancellationToken.None, () => false));
        for (int i = 0; i < 200 && rig.Path.Watch.List().Count == 0; i++) Thread.Sleep(5);
        Check(rig.Opened.SequenceEqual(new[] { item.Url }), "path A opens the real page in the default browser");
        Check(item.AutoCheck, "the row says it is waiting for the site check");
        Check(rig.Path.Watch.Report(new NativeBridge.ExtensionCaptchaReport("widget", item.Url, 4, "turnstile", 200, 140, 300, 64, 1, "")), "a widget report of the watched page is taken");
        Check(rig.Clicker.Pressed.SequenceEqual(new[] { "350,172" }), "the press lands on the middle of the reported widget (" + string.Join(";", rig.Clicker.Pressed) + ")");
        Check(rig.Notes.Any(n => n.Contains("pressing")), "the note says the widget was found and pressed");
        Check(rig.Path.Watch.Report(new NativeBridge.ExtensionCaptchaReport("token", item.Url, 4, "", 0, 0, 0, 0, 1, "secret-token")), "the answer report of the same page is taken");
        Check(!rig.Notes.Any(n => n.Contains("secret-token")), "the answer value itself is not in a note");
        var grab = new BrowserGrab("https://cdn.example/file.zip", "file.zip", item.Url, "a=1", "ua", "acc", "lang");
        Check(rig.Path.Watch.Take(grab), "the download the page's form produced is taken");
        Check(run.Wait(TimeSpan.FromSeconds(5)) && run.Result is { } got && got.Url == grab.Url, "path A returns that download to the same job");
        Check(delivered.Count == 1 && delivered[0].Url == grab.Url, "the one continuation was used exactly once");
        Check(rig.Notes.Any(n => n.Contains("same job")), "the note says the same job continues");
    }

    /// <summary>A page that never reports: the wait runs out, the page is left open and one honest line is left behind.</summary>
    private static void AutoGiveUp()
    {
        var rig = NewPath(TimeSpan.FromMilliseconds(200));
        var item = AutoItem();
        var run = Task.Run(() => rig.Path.RunAsync(item, new Uri(item.Url), _ => { }, CancellationToken.None, () => false));
        Check(run.Wait(TimeSpan.FromSeconds(5)) && run.Result is null, "path A gives up when nothing arrives");
        Check(rig.Notes.Any(n => n.Contains("stays open")), "the give-up line says the page stays open");
        Check(rig.Clicker.Pressed.Count == 0 && rig.Path.Watch.List().Count == 0, "nothing was pressed and the watch list is empty again");
    }

    private static void AutoOff()
    {
        var path = new CaptchaPathA { Clicker = () => null, Note = _ => { } };
        Check(path.RunAsync(AutoItem(), new Uri("https://pages.example/a"), _ => { }, CancellationToken.None, () => false).GetAwaiter().GetResult() is null, "with the press switched off path A does nothing");
        var failed = new ResolveResult(PageLinkState.Failed, null, "pagelink.fail.nobody");
        var engine = new HostFormEngine { LastCaptchaFailed = true };
        Check(PageResolveCoordinator.WantsAuto(engine, failed, path, () => true), "the option on and a failed check arm the second path");
        Check(!PageResolveCoordinator.WantsAuto(engine, failed, path, () => false), "the option off keeps the person path");
        Check(PageResolveCoordinator.WantsAuto(engine, failed, path, null), "no option (tests) reads as on");
        Check(!PageResolveCoordinator.WantsAuto(engine, new ResolveResult(PageLinkState.Skipped, null), path, () => true), "a skipped link does not start the second path");
        Check(!PageResolveCoordinator.WantsAuto(new HostFormEngine(), failed, path, () => true), "a check that did not run by itself does not start the second path");
        Check(!PageResolveCoordinator.WantsAuto(engine, failed, null, () => true), "without a second path nothing is armed");
    }

    /// <summary>A capture from another site is not taken; a capture of the watched page is.</summary>
    private static void AutoMatching()
    {
        var watch = new CaptchaBrowserWatch();
        var entry = watch.Register("https://pages.example/download/abc", _ => { }, null, null, null, TimeSpan.FromMinutes(1));
        Check(!watch.Take(new BrowserGrab("https://other.example/x.zip", "x.zip", "https://other.example/page", null, null, null, null)), "another site's download is not taken");
        Check(watch.Take(new BrowserGrab("https://cdn2.example/x.zip", "x.zip", "https://pages.example/step?x=1", null, "ua", null, null)), "the watched site's own download is taken through its referer");
        Check(!watch.Take(new BrowserGrab("https://pages.example/x.zip", "x.zip", "https://pages.example/other", null, null, null, null)), "once taken the page's entry is used up");
        watch.Remove(entry);
    }

    /// <summary>The form resolver: an origin-bound key with the press on fails into path A; with the press off the NeoDM browser page still takes over.</summary>
    private static void AutoEngine()
    {
        var handler = new BoundKeyHandler();
        var step = new BrowserCaptchaStep { Clicker = new AutoClicker(), Launcher = _ => { }, Attempt = TimeSpan.FromMilliseconds(300), PlacementWait = TimeSpan.FromMilliseconds(30) };
        var engine = new HostFormEngine { Delay = (_, c) => { c.ThrowIfCancellationRequested(); return Task.CompletedTask; }, Captcha = step, HandlerFactory = _ => handler };
        var item = AutoItem("https://datanodes.to/li77lxy835bq/Part_1.zip");
        var r = Task.Run(() => engine.ResolveAsync(item, () => false, CancellationToken.None)).GetAwaiter().GetResult();
        Check(!r.Unrecognised && r.Result.State == PageLinkState.Failed && r.Result.DetailKey == "pagelink.fail.nobody", "an origin-bound key with the press on ends in a captcha failure for path A");
        Check(engine.LastCaptchaFailed, "the engine marks the failure as the check's own");
        var quiet = new HostFormEngine { Delay = (_, c) => Task.CompletedTask, Captcha = new BrowserCaptchaStep { Launcher = _ => { } }, HandlerFactory = _ => new BoundKeyHandler() };
        var r2 = Task.Run(() => quiet.ResolveAsync(AutoItem("https://datanodes.to/li77lxy835bq/Part_1.zip"), () => false, CancellationToken.None)).GetAwaiter().GetResult();
        Check(r2.Unrecognised && !quiet.LastCaptchaFailed, "with the press off the key-bound host still falls back to the NeoDM browser page");
    }

    /// <summary>The two pages of the pilot host, cut down to the form and the captcha.</summary>
    private sealed class BoundKeyHandler : HttpMessageHandler
    {
        public readonly List<string> Seen = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            var html = request.Method == HttpMethod.Get ? Fx("datanodes-download1.html") : Fx("datanodes-download2.html");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") });
        }
    }

    /// <summary>The bridge records survive a JSON round trip exactly (the host and the app share their shape).</summary>
    private static void AutoWire()
    {
        var report = new NativeBridge.ExtensionCaptchaReport("widget", "https://pages.example/a?x=1", 7, "hcaptcha", 10, 20, 300, 64, 1.5, "");
        var back = JsonSerializer.Deserialize<NativeBridge.ExtensionCaptchaReport>(JsonSerializer.Serialize(report))!;
        Check(back == report && back.W == 300 && back.Dpr == 1.5 && back.TabId == 7, "a widget report survives the wire unchanged");
        var token = new NativeBridge.ExtensionCaptchaReport("token", "https://pages.example/a", -1, "", 0, 0, 0, 0, 1, "abc-123");
        var tokenBack = JsonSerializer.Deserialize<NativeBridge.ExtensionCaptchaReport>(JsonSerializer.Serialize(token))!;
        Check(tokenBack == token && tokenBack.Token == "abc-123", "an answer report survives the wire unchanged");
        var entry = new NativeBridge.CaptchaWatchEntry("https://pages.example/a", 1700000000000);
        var entryBack = JsonSerializer.Deserialize<NativeBridge.CaptchaWatchEntry>(JsonSerializer.Serialize(entry))!;
        Check(entryBack == entry, "a watch entry survives the wire unchanged");
    }
}
