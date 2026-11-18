using System.Diagnostics;
using NDM.App.Localization;
using NDM.Core;
using NDM.Core.Captcha;

namespace NDM.App;

/// <summary>1.16.1: the human step in the user's real default browser. It starts a one-shot loopback page (see <see cref="CaptchaListener"/>), opens it with the system's default browser,
/// brings NeoDM forward with a plain message, and waits for the token the PERSON produces by clicking the widget. It never solves anything, and it gives each challenge its own page and token.
/// 1.16.2: when a <see cref="Clicker"/> is set the step presses the widget itself, with a real mouse, at the point the page reported, so the person does nothing. The page can also be the
/// site's own page fetched again through the loopback server (<see cref="PageSource"/>), so a key that only accepts the site's address still draws. Keys known to fail are remembered.</summary>
public sealed class BrowserCaptchaStep : IHumanCaptchaStep
{
    public const int MaxKeys = 3;
    /// <summary>Opens an address in the default browser. The real one is the shell; tests record the address instead.</summary>
    public Action<string> Launcher { get; set; } = OpenInDefaultBrowser;
    public TimeSpan Expiry { get; set; } = CaptchaListener.DefaultExpiry;
    /// <summary>Called when the step starts waiting (true) and when it ends (false): the window comes forward with the "this site needs you" panel.</summary>
    public Action<bool>? Waiting { get; set; }
    public string? CurrentUrl { get; private set; }

    /// <summary>The real press. Null keeps 1.16.1 behaviour: the person presses, NeoDM only waits.</summary>
    public IRealClicker? Clicker { get; set; }
    /// <summary>The window to bring to the front before the press (the browser the page was opened in).</summary>
    public Func<IntPtr>? Window { get; set; }
    /// <summary>The virtual screen the press point is kept on. Tests give a fixed rectangle.</summary>
    public Func<PixelRect> Screen { get; set; } = CaptchaGeometry.VirtualScreen;
    /// <summary>The site's page fetcher for the browser loop. Null serves the bare widget page instead.</summary>
    public IPageSource? PageSource { get; set; }
    /// <summary>How long the page gets to report where the widget is before the next key is tried.</summary>
    public TimeSpan PlacementWait { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>How long one key gets in all when NeoDM presses (the page, the press and a token).</summary>
    public TimeSpan Attempt { get; set; } = TimeSpan.FromSeconds(25);
    /// <summary>Further keys to try, taken from the page markup. Ones already known to fail are skipped.</summary>
    public Func<IReadOnlyList<HostCaptcha>>? Alternatives { get; set; }
    /// <summary>Keys that already failed here, for the session only.</summary>
    public FailedSiteKeys Failed { get; set; } = new();
    /// <summary>One honest line per attempt (no keys, no token): tests read it, the app logs it.</summary>
    public Action<string>? Note { get; set; }
    /// <summary>The point the last press used (tests read it).</summary>
    public ClickTarget? LastClick { get; private set; }
    public int Attempts { get; private set; }
    private CaptchaListener? _current;

    public static void OpenInDefaultBrowser(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "http" || u.Host != "127.0.0.1") throw new ArgumentException("local-page-only", nameof(url));
        Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true })?.Dispose();
    }

    /// <summary>"Open the page again": the same page in the browser (the same one-time address, while it is still open).</summary>
    public bool OpenAgain()
    {
        var url = CurrentUrl; if (url is null || _current is null || _current.Used) return false;
        try { Launcher(url); return true; } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException) { return false; }
    }

    public async Task<CaptchaOutcome> RunAsync(HostCaptcha captcha, Uri page, CancellationToken token, Func<bool> skip)
    {
        var text = new CaptchaPageText(Loc.T("pagelink.browser.title"), Loc.T("pagelink.browser.heading", page.Host), Loc.T("pagelink.browser.body"), Loc.T("pagelink.browser.done"), Loc.T("pagelink.browser.failed"));
        if (Clicker is null) return await PersonAsync(captcha, text, page, token, skip);
        return await PressAsync(captcha, text, page, token, skip);
    }

    /// <summary>1.16.1: one page, one key, the person presses.</summary>
    private async Task<CaptchaOutcome> PersonAsync(HostCaptcha captcha, CaptchaPageText text, Uri page, CancellationToken token, Func<bool> skip)
    {
        using var listener = new CaptchaListener(captcha, text, Expiry);
        _current = listener; CurrentUrl = listener.Url;
        try
        {
            try { Launcher(listener.Url); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException) { /* "Open the page again" can try once more; the message says what to do */ }
            Waiting?.Invoke(true);
            return await WaitForToken(listener, token, skip);
        }
        finally { Waiting?.Invoke(false); _current = null; CurrentUrl = null; }
    }

    /// <summary>1.16.2: NeoDM presses. Each key gets its own page; a key that brings no token is remembered and the next one is tried.</summary>
    private async Task<CaptchaOutcome> PressAsync(HostCaptcha captcha, CaptchaPageText text, Uri page, CancellationToken token, Func<bool> skip)
    {
        foreach (var candidate in Candidates(captcha))
        {
            token.ThrowIfCancellationRequested();
            if (skip()) return CaptchaOutcome.Skip;
            Attempts++;
            var spec = Spec(candidate, page);
            using var listener = new CaptchaListener(spec, text, Attempt < Expiry ? Attempt : Expiry);
            _current = listener; CurrentUrl = listener.Url;
            try
            {
                Waiting?.Invoke(true);
                try { Launcher(listener.Url); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException) { Note?.Invoke("browser could not be opened"); }
                var report = await WaitForReport(listener, token, skip);
                var target = report is null ? null : Target(report);
                if (target is null)
                {
                    Note?.Invoke(listener.Loop ? "site page shown, no widget reported" : "no widget reported");
                    continue;                                                                    // next key
                }
                LastClick = target;
                Note?.Invoke("widget found, pressing " + target);
                Clicker!.Click(target);
                var outcome = await WaitForToken(listener, token, skip);
                if (outcome.Token is not null || outcome.Skipped) return outcome;
                if (candidate.SiteKey is { } failedKey) Failed.Add(failedKey);
                Note?.Invoke("no token for this key");
            }
            finally { Waiting?.Invoke(false); _current = null; CurrentUrl = null; }
        }
        return CaptchaOutcome.Timeout;
    }

    /// <summary>The widget's press point on the screen, kept on the virtual screen. Null when the numbers do not fit together.</summary>
    private ClickTarget? Target(CaptchaReport report) => CaptchaGeometry.Target(report, Screen(), Window?.Invoke() ?? IntPtr.Zero);

    private CaptchaPageSpec Spec(HostCaptcha candidate, Uri page)
        => PageSource is { } source ? new CaptchaPageSpec(candidate.Kind, candidate.SiteKey, null, new LoopbackPageProxy(page, source))
                                    : new CaptchaPageSpec(candidate.Kind, candidate.SiteKey, page.AbsoluteUri);

    private IEnumerable<HostCaptcha> Candidates(HostCaptcha first)
    {
        var given = new HashSet<string>(StringComparer.Ordinal);
        if (first.SiteKey is { } k) given.Add(k);
        yield return first;
        if (Alternatives is null) yield break;
        foreach (var candidate in Alternatives())
        {
            if (Attempts + 1 > MaxKeys) break;
            if (candidate.SiteKey is { } key && (given.Contains(key) || Failed.Contains(key))) continue;
            if (!candidate.BrowserStepCan) continue;
            if (candidate.SiteKey is { } added) given.Add(added);
            yield return candidate;
        }
    }

    /// <summary>The first measurement the page posts, or null after <see cref="PlacementWait"/> or when the page closes.</summary>
    private async Task<CaptchaReport?> WaitForReport(CaptchaListener listener, CancellationToken token, Func<bool> skip)
    {
        var until = DateTime.UtcNow + PlacementWait;
        while (listener.Latest is null)
        {
            token.ThrowIfCancellationRequested();
            if (skip() || listener.Used) return null;
            if (DateTime.UtcNow > until) return null;
            await Task.WhenAny(listener.Placement, Task.Delay(100, token));
        }
        return listener.Latest;
    }

    private static async Task<CaptchaOutcome> WaitForToken(CaptchaListener listener, CancellationToken token, Func<bool> skip)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (skip()) return CaptchaOutcome.Skip;
            var done = await Task.WhenAny(listener.Token, Task.Delay(250, token));
            if (done == listener.Token) { var t = await listener.Token; return t is null ? CaptchaOutcome.Timeout : CaptchaOutcome.Got(t); }
        }
    }
}
