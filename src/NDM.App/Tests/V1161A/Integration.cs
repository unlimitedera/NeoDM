using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

internal static partial class V1161ATests
{
    private static void PumpUntil(Func<bool> done, string what, int ms = 10000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!done()) { if (DateTime.UtcNow > end) throw new TimeoutException("timeout: " + what); Pump(40); }
    }
    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void SaveShot(Window w, string name)
    {
        var dir = Path.Combine("F:/NDM/artifacts/shots-1161a"); Directory.CreateDirectory(dir);
        w.UpdateLayout(); int pw = (int)Math.Max(1, w.ActualWidth), ph = (int)Math.Max(1, w.ActualHeight);
        var bmp = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32); bmp.Render(w);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp)); using var fs = File.Create(Path.Combine(dir, name)); enc.Save(fs);
    }

    /// <summary>A scripted embedded-browser page: it shows one enabled Download control and captures a file when it is clicked.</summary>
    private sealed class FakeSession : IPageSession
    {
        public int Loads, Presented; private BrowserGrab? _grab; private string _url = "";
        public Task LoadAsync(string url, CancellationToken token) { Loads++; _url = url; return Task.CompletedTask; }
        public Task<PageSnapshot> ScanAsync(CancellationToken token) => Task.FromResult(new PageSnapshot(_url, false, null, new[] { new PageControl("n1", "Download", "button", true, true, false, false) }, null, false));
        public Task<bool> ClickAsync(string controlId, CancellationToken token) { _grab = new BrowserGrab(_url + "/file.bin", "file.bin", _url, null, "UA", null, null); return Task.FromResult(true); }
        public BrowserGrab? TakeCapture() { var g = _grab; _grab = null; return g; }
        public string? TakeRefusal() => null;
        public void NoteClick() { }
        public void Present(bool front) { if (front) Presented++; }
        public void SetHumanMessage(string key) { }
        public void Dispose() { }
    }

    private static void Integration()
    {
        using var host = new XfsHost { Countdown = 3 }; using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var step = new BrowserCaptchaStep { Expiry = TimeSpan.FromSeconds(60) }; var launched = new List<string>(); string mode = "person";
        step.Launcher = url => { launched.Add(url); if (mode == "person") Task.Run(() => { Http(c, HttpMethod.Get, url); Http(c, HttpMethod.Post, url + "/token", body: "token=" + host.IssueToken()); }); };
        var clock = new FakeClock(); var sessions = new List<FakeSession>(); var resolved = new List<(string Name, string Url)>();
        var co = new PageResolveCoordinator(() => { var s = new FakeSession(); sessions.Add(s); return s; }, (item, grab) => { resolved.Add((item.Name, grab.Url)); return Task.CompletedTask; }, new PageResolveEngine { Tick = TimeSpan.FromMilliseconds(30) })
        { FormEngine = new HostFormEngine { Delay = clock.Delay, Captcha = step }, OpenPageAgain = step.OpenAgain };

        // 1) four form links in order through the coordinator: four challenges, no embedded browser window at all
        co.Add(Enumerable.Range(1, 4).Select(n => Item(host.FileUrl("int" + n), $"Part_{n}.zip")));
        Task run = Task.CompletedTask; Application.Current.Dispatcher.Invoke(() => run = co.RunAsync());
        PumpUntil(() => run.IsCompleted, "four form links resolved", 30000);
        Check(co.Items.All(i => i.State == PageLinkState.Resolved) && resolved.Select(r => r.Name).SequenceEqual(new[] { "Part_1.zip", "Part_2.zip", "Part_3.zip", "Part_4.zip" }), "coordinator: four links resolved in order and handed to the download job");
        Check(launched.Distinct().Count() == 4 && sessions.Count == 0, "four challenges in the real browser step; the NeoDM browser window was never created");

        // 2) a page the form logic does not recognise falls back to the NeoDM browser window
        host.NotAForm = true; resolved.Clear();
        co.Add(new[] { Item(host.FileUrl("fall1"), "Odd.zip") });
        run = co.RunAsync(); PumpUntil(() => run.IsCompleted, "fallback link finished", 15000);
        Check(co.Items[^1].State == PageLinkState.Resolved && sessions.Count == 1 && sessions[0].Loads == 1 && resolved.Count == 1, "unrecognised page: the embedded browser fallback resolved it");
        host.NotAForm = false;

        // 3) the panel: needs you in the person's browser, with Open the page again / Skip / Cancel the rest
        mode = "nobody"; launched.Clear();
        var win = new PageLinksWindow(co, "Pack") { Left = 10, Top = 10, WindowStartupLocation = WindowStartupLocation.Manual }; win.Show(); Pump(200);
        co.Add(new[] { Item(host.FileUrl("pan1"), "A.zip"), Item(host.FileUrl("pan2"), "B.zip"), Item(host.FileUrl("pan3"), "C.zip") });
        var before = co.Items.Count; run = co.RunAsync();
        PumpUntil(() => co.Items[before - 3].State == PageLinkState.NeedsYou, "first link needs the person", 10000); Pump(400);
        var cur = co.Items[before - 3];
        Check(win.NeedsPanelVisible && win.NeedsText.StartsWith("This site needs you.") && win.NeedsText.Contains("Click \"I'm not a robot\" in your browser, then NeoDM continues by itself."), "panel text: This site needs you. Click 'I'm not a robot' in your browser, then NeoDM continues by itself.");
        Check(win.OpenAgainBtn.Visibility == Visibility.Visible && (string)win.OpenAgainBtn.Content == "Open the page again" && win.ShowWindowButton.Visibility == Visibility.Collapsed && (string)win.SkipLinkButton.Content == "Skip this link" && win.CancelAllBtn.IsEnabled, "panel buttons: Open the page again, Skip this link, Cancel the rest (no NeoDM browser window to show)");
        Check(cur.StatusText == "Needs you: click in your browser" && launched.Count == 1, "row status says the click is needed in the browser; the page was opened once");
        Click(win.OpenAgainBtn); Pump(100); Check(launched.Count == 2 && launched[0] == launched[1], "Open the page again opens the same page");
        SaveShot(win, "pagelinks-browser-light.png");
        foreach (var theme in new[] { "DeepNavy", "Dark" }) { ThemeManager.Apply(Application.Current, theme, null); Pump(300); SaveShot(win, "pagelinks-browser-" + theme.ToLowerInvariant() + ".png"); }
        ThemeManager.Apply(Application.Current, "Light", null);
        Loc.SetLanguage("ar"); win.FlowDirection = FlowDirection.RightToLeft; Pump(400); SaveShot(win, "pagelinks-browser-rtl-ar.png");
        Check(win.NeedsPanelVisible && win.NeedsText.Length > 10 && win.OpenAgainBtn.Visibility == Visibility.Visible, "RTL (Arabic): the panel and its buttons still show");
        Loc.SetLanguage("en"); win.FlowDirection = FlowDirection.LeftToRight; Pump(200);
        Click(win.SkipLinkButton); PumpUntil(() => cur.State == PageLinkState.Skipped, "skip ends the first link", 10000);
        PumpUntil(() => co.Items[before - 2].State == PageLinkState.NeedsYou, "the next link needs the person (its own challenge)", 10000);
        Check(launched.Distinct().Count() == 2, "the next link got its own page (no reuse)");
        Click(win.CancelAllBtn); PumpUntil(() => run.IsCompleted, "cancel the rest ends the run", 10000);
        Check(co.Items.Skip(before - 2).All(i => i.State == PageLinkState.Cancelled), "Cancel the rest cancels this link and every link still waiting");
        Pump(300); Check(!win.NeedsPanelVisible, "the panel closes when nothing needs the person"); win.Close();

        // 4) the timer text while waiting
        var timerItem = Item("http://127.0.0.1:1/x", "T.zip"); timerItem.State = PageLinkState.WaitingTimer; timerItem.Countdown = 12;
        Check(timerItem.StatusText == "Waiting for the site's timer (12 s)", "timer row text shows the live seconds");

        // 5) every new string exists in all ten languages
        var keys = new[] { "pagelink.browser.title", "pagelink.browser.heading", "pagelink.browser.body", "pagelink.browser.done", "pagelink.browser.failed", "pagelink.fail.refused", "pagelink.fail.no_link", "pagelink.fail.captcha_wrong", "pagelink.state.needs_you_browser", "pagelink.needs_you_browser.body", "pagelink.btn.open_again" };
        foreach (var lang in new[] { "en", "ar", "de", "es", "fr", "ja", "pt-BR", "ru", "tr", "zh-Hans" })
        {
            Loc.SetLanguage(lang); bool all = keys.All(k => Loc.T(k, "x") != k && Loc.T(k, "x").Length > 2);
            Check(all, "locale " + lang + ": the new strings exist");
        }
        Loc.SetLanguage("en");
    }
}
