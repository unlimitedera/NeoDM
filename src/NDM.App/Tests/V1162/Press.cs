using System.Net.Http;
using System.Text.RegularExpressions;
using NDM.App;
using NDM.Core;
using NDM.Core.Captcha;

internal static partial class V1162Tests
{
    /// <summary>Plays the browser: it loads the page the step opens, posts the measurement the page would post, and answers a press by posting a token.</summary>
    private sealed class FakeBrowser : IDisposable
    {
        public readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };
        public readonly List<string> Opened = new();
        public readonly List<string> Pages = new();
        public readonly HashSet<string> Silent = new(StringComparer.Ordinal);
        public string? CurrentKey;
        public int Presses;
        public string Token = "human-token-0123456789";

        /// <summary>The press: a token is posted unless the page's key is one that stays silent.</summary>
        public void Press()
        {
            Presses++;
            var url = Opened[^1];
            if (CurrentKey is { } key && Silent.Contains(key)) return;
            _ = Task.Run(() => Client.PostAsync(url + "/r", new StringContent("do=token&token=" + Token, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded")));
        }

        public void Dispose() => Client.Dispose();
    }

    private sealed class Pressing(FakeBrowser browser) : IRealClicker
    {
        public readonly List<string> Pressed = new();
        public void Click(int x, int y) => Click(new ClickTarget(PixelRect.Of(x, y), 1, IntPtr.Zero));
        public void Click(ClickTarget target) { Pressed.Add(target.X + "," + target.Y); browser.Press(); }
    }

    /// <summary>A step whose launcher plays the browser: it loads the page, notes its key and posts the measurement.</summary>
    private static (BrowserCaptchaStep Step, FakeBrowser Browser, Pressing Clicker) Rig(TimeSpan? attempt = null)
    {
        var browser = new FakeBrowser();
        var clicker = new Pressing(browser);
        var step = new BrowserCaptchaStep
        {
            Clicker = clicker,
            PlacementWait = TimeSpan.FromSeconds(2),
            Attempt = attempt ?? TimeSpan.FromSeconds(30),
            Screen = () => new PixelRect(0, 0, 1920, 1080),
            Expiry = TimeSpan.FromSeconds(30),
        };
        step.Launcher = url =>
        {
            browser.Opened.Add(url);
            _ = Task.Run(async () =>
            {
                var html = await browser.Client.GetStringAsync(url);
                browser.Pages.Add(html);
                browser.CurrentKey = Regex.Match(html, "data-sitekey=\"([^\"]+)\"").Groups[1].Value;
                // the widget is on the page, the page measures itself and posts the numbers
                await browser.Client.PostAsync(url + "/r", new StringContent("do=loaded&x=100&y=200&w=300&h=65&vw=1024&vh=768&dpr=1&eleft=0&etop=0&ew=1040&eh=868", System.Text.Encoding.UTF8, "application/x-www-form-urlencoded"));
            });
        };
        return (step, browser, clicker);
    }

    /// <summary>The auto path: the page reports where the widget is, the step presses its middle, and the token that follows ends the step.</summary>
    private static void Press()
    {
        var (step, browser, clicker) = Rig();
        var notes = new List<string>(); step.Note = notes.Add;
        var waits = new List<bool>(); step.Waiting = waits.Add;
        var outcome = Task.Run(() => step.RunAsync(new HostCaptcha(HostCaptchaKind.Turnstile, Key), new Uri("https://host.example/d/abc"), CancellationToken.None, () => false)).GetAwaiter().GetResult();
        Check(outcome.Token == browser.Token && !outcome.TimedOut, "the token that followed the press ends the step");
        Check(browser.Pages.Count == 1 && browser.Pages[0].Contains("data-sitekey=\"" + Key + "\""), "the browser was given the loopback page with the widget");
        Check(clicker.Pressed.SequenceEqual(new[] { "258,332" }), "the press landed on the middle of the widget, moved by its window and toolbar");
        Check(step.LastClick is { } t && t.OnScreen == new PixelRect(258, 332, 1, 1) && t.Dpr == 1, "the press point was kept for the report");
        Check(step.Attempts == 1, "one key was enough");
        Check(waits.SequenceEqual(new[] { true, false }), "the window comes forward while the step works and goes quiet after");
        Check(notes.Any(n => n.Contains("widget found") && n.Contains("258,332")), "an honest line names the press point");
        Check(!notes.Any(n => n.Contains(Key)), "the line does not carry the site key");
        Check(step.Failed.Count == 0, "nothing was marked as failed");
        browser.Dispose();
    }

    /// <summary>A key that brings no token is remembered and the next key on the page is tried; the second press ends the step.</summary>
    private static void KeyRetry()
    {
        var (step, browser, clicker) = Rig(attempt: TimeSpan.FromMilliseconds(700));
        var notes = new List<string>(); step.Note = notes.Add;
        var second = "0x4AAAAAAAfixturesecondkey001";
        browser.Silent.Add(Key);
        step.Alternatives = () => new[] { new HostCaptcha(HostCaptchaKind.Turnstile, Key), new HostCaptcha(HostCaptchaKind.Turnstile, second) };
        var outcome = Task.Run(() => step.RunAsync(new HostCaptcha(HostCaptchaKind.Turnstile, Key), new Uri("https://host.example/d/abc"), CancellationToken.None, () => false)).GetAwaiter().GetResult();
        Check(step.Attempts == 2 && browser.Opened.Count == 2 && browser.Opened[0] != browser.Opened[1], "each key got its own page address");
        Check(step.Failed.Contains(Key) && !step.Failed.Contains(second), "the key that brought no token is the one remembered");
        Check(browser.Pages.Count == 2 && browser.Pages[1].Contains(second), "the second page carries the second key");
        Check(clicker.Pressed.Count == 2 && outcome.Token is not null, "the second press brought the token");
        Check(notes.Any(n => n.Contains("no token for this key")), "the failed key is said out loud");
        Check(!browser.Pages.Any(p => p.Contains(Key) && p.Contains(second)), "one page never carries two keys");
        browser.Dispose();

        // a key already known to fail is skipped outright
        var (again, browser2, _) = Rig();
        var other = "0x4AAAAAAAfixturethirdkey00001";
        again.Alternatives = () => new[] { new HostCaptcha(HostCaptchaKind.Turnstile, Key) };
        again.Failed = step.Failed;
        Task.Run(() => again.RunAsync(new HostCaptcha(HostCaptchaKind.Turnstile, other), new Uri("https://host.example/d/abc"), CancellationToken.None, () => false)).GetAwaiter().GetResult();
        Check(browser2.Pages.Count == 1 && browser2.Pages[0].Contains(other), "a remembered key is not offered again");
        browser2.Dispose();
    }
}
