using System.Diagnostics;
using NDM.App.Localization;
using NDM.Core;

namespace NDM.App;

/// <summary>1.16.1: the human step in the user's real default browser. It starts a one-shot loopback page (see <see cref="CaptchaListener"/>), opens it with the system's default browser,
/// brings NeoDM forward with a plain message, and waits for the token the PERSON produces by clicking the widget. It never clicks, scripts or solves anything, and it gives each challenge its own page and token.</summary>
public sealed class BrowserCaptchaStep : IHumanCaptchaStep
{
    /// <summary>Opens an address in the default browser. The real one is the shell; tests record the address instead.</summary>
    public Action<string> Launcher { get; set; } = OpenInDefaultBrowser;
    public TimeSpan Expiry { get; set; } = CaptchaListener.DefaultExpiry;
    /// <summary>Called when the step starts waiting (true) and when it ends (false): the window comes forward with the "this site needs you" panel.</summary>
    public Action<bool>? Waiting { get; set; }
    public string? CurrentUrl { get; private set; }
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
        using var listener = new CaptchaListener(captcha, text, Expiry);
        _current = listener; CurrentUrl = listener.Url;
        try
        {
            try { Launcher(listener.Url); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException) { /* "Open the page again" can try once more; the person can also copy nothing: the message says what to do */ }
            Waiting?.Invoke(true);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (skip()) return CaptchaOutcome.Skip;
                var done = await Task.WhenAny(listener.Token, Task.Delay(250, token));
                if (done == listener.Token) { var t = await listener.Token; return t is null ? CaptchaOutcome.Timeout : CaptchaOutcome.Got(t); }
            }
        }
        finally { Waiting?.Invoke(false); _current = null; CurrentUrl = null; }
    }
}
