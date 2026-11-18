using NDM.Core;
using NDM.Core.Captcha;

namespace NDM.App;

// 1.16.2, path A of the automatic site check: the loopback page (path B) brought no answer, or the site's key refuses the
// loopback address. NeoDM then opens the site's REAL page in the person's default browser, the NeoDM browser extension finds
// the widget on it and reports where it sits on the screen, NeoDM presses it with the real mouse, and the extension reports
// the answer and submits the page's own form. The download that form starts is captured by the extension as usual and goes
// back into the SAME job. The extension only ever reports a page the app named here, so normal browsing is never touched.
// No consent dialog, no solving service, no key or answer value in a log line.

/// <summary>The pages the app is currently waiting on for a site check. One entry per running page link; entries end with the wait.</summary>
public sealed class CaptchaBrowserWatch
{
    public sealed class Entry
    {
        public required string PageUrl;
        public long Until;
        public IRealClicker? Clicker;
        public Func<PixelRect>? Screen;
        public Action<string>? Note;
        public Action<BrowserGrab>? Deliver;
        public string? Token;
        public int Reports;
    }

    public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;
    private readonly List<Entry> _entries = new();
    /// <summary>What the app answers the host with: the pages it waits on (the host calls this every few seconds).</summary>
    public IReadOnlyList<NativeBridge.CaptchaWatchEntry> List()
    {
        lock (_entries)
        {
            long now = UnixMs();
            _entries.RemoveAll(e => e.Until <= now);
            return _entries.Select(e => new NativeBridge.CaptchaWatchEntry(e.PageUrl, e.Until)).ToList();
        }
    }
    public long UnixMs() => (long)(Now().ToUniversalTime() - DateTime.UnixEpoch).TotalMilliseconds;

    public Entry Register(string pageUrl, Action<BrowserGrab> deliver, IRealClicker? clicker, Func<PixelRect>? screen, Action<string>? note, TimeSpan lifetime)
    {
        var entry = new Entry { PageUrl = pageUrl, Until = UnixMs() + (long)lifetime.TotalMilliseconds, Clicker = clicker, Screen = screen, Note = note, Deliver = deliver };
        lock (_entries) _entries.Add(entry);
        return entry;
    }
    public void Remove(Entry entry) { lock (_entries) _entries.Remove(entry); }

    /// <summary>True when the report belongs to a watched page. A widget report is pressed with the real mouse at once.</summary>
    public bool Report(NativeBridge.ExtensionCaptchaReport report)
    {
        Entry? entry;
        lock (_entries) entry = Match(report.Url);
        if (entry is null) return false;
        entry.Reports++;
        if (report.Report == "token") { entry.Token = report.Token; entry.Note?.Invoke("path A: the answer arrived from the page"); return true; }
        // The extension measured the box on the screen already (physical pixels); keep it on the virtual screen and press its middle.
        var screen = entry.Screen?.Invoke() ?? CaptchaGeometry.VirtualScreen();
        var box = CaptchaGeometry.Clamp(new PixelRect(report.X, report.Y, report.W, report.H), screen);
        var target = new ClickTarget(box, report.Dpr > 0 ? report.Dpr : 1, IntPtr.Zero);
        entry.Note?.Invoke("path A: the browser reported the widget, pressing " + target);
        entry.Clicker?.Click(target);
        return true;
    }

    /// <summary>True when the capture belongs to the page the app is waiting on: the form the extension submitted produced it. The job continues with it.</summary>
    public bool Take(BrowserGrab grab)
    {
        Entry? entry;
        lock (_entries) entry = _entries.FirstOrDefault(e => e.Until > UnixMs() && LoginBrowserLogic.SameSite(e.PageUrl, grab.Referer is { Length: > 0 } ? grab.Referer : grab.Url));
        if (entry is null) return false;
        lock (_entries) _entries.Remove(entry);
        entry.Note?.Invoke("path A: the page's own form produced the download, it continues in the same job");
        var deliver = entry.Deliver;
        _ = Task.Run(() => { try { deliver?.Invoke(grab); } catch (Exception) { } });
        return true;
    }

    private Entry? Match(string url)
    {
        long now = UnixMs();
        return _entries.FirstOrDefault(e => e.Until > now && (Same(e.PageUrl, url) || LoginBrowserLogic.SameSite(e.PageUrl, url)));
        static bool Same(string a, string b) => Uri.TryCreate(a, UriKind.Absolute, out var x) && Uri.TryCreate(b, UriKind.Absolute, out var y) && x.AbsoluteUri == y.AbsoluteUri;
    }
}

/// <summary>Where the app's bridge handlers find the running page-link resolver's site check state (set by the main window, cleared again with it).</summary>
public static class CaptchaAutoHub
{
    public static Func<IReadOnlyList<NativeBridge.CaptchaWatchEntry>>? WatchList;
    public static Func<NativeBridge.ExtensionCaptchaReport, bool>? Report;
    public static Func<NativeBridge.BrowserDownloadRequest, bool>? Capture;
    public static void Clear() { WatchList = null; Report = null; Capture = null; }
}

/// <summary>Path A: the real page in the person's browser, the extension as the eyes, the real press, the site's own form. Returns the download the page produced, or null when nothing came.</summary>
public sealed class CaptchaPathA
{
    /// <summary>The press. Null means the automatic path is off and path A never runs.</summary>
    public Func<IRealClicker?> Clicker { get; set; } = () => null;
    public Func<PixelRect> Screen { get; set; } = CaptchaGeometry.VirtualScreen;
    /// <summary>Opens the page in the default browser. Tests record the address instead.</summary>
    public Action<string>? Launcher { get; set; }
    /// <summary>How long the page gets to produce its download once it was opened.</summary>
    public TimeSpan Wait { get; set; } = TimeSpan.FromSeconds(120);
    public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (t, c) => Task.Delay(t, c);
    public Action<string>? Note { get; set; }
    public CaptchaBrowserWatch Watch { get; } = new();

    public async Task<BrowserGrab?> RunAsync(PageLinkItem item, Uri page, Action<BrowserGrab>? deliver, CancellationToken token, Func<bool> skip)
    {
        var clicker = Clicker();
        if (clicker is null) { Note?.Invoke("path A skipped: the press is switched off"); return null; }
        var done = new TaskCompletionSource<BrowserGrab>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = Watch.Register(page.AbsoluteUri, g => { try { deliver?.Invoke(g); } catch (Exception) { } done.TrySetResult(g); }, clicker, Screen, Note, Wait + TimeSpan.FromMinutes(5));
        try
        {
            try { Launcher?.Invoke(page.AbsoluteUri); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException) { Note?.Invoke("path A: the page could not be opened"); return null; }
            Note?.Invoke("path A: the page was opened in the browser, waiting for the site check");
            item.AutoCheck = true;
            var until = Now() + Wait;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (skip()) return null;
                if (done.Task.IsCompleted) return done.Task.Result;
                if (Now() > until) break;
                await Delay(TimeSpan.FromMilliseconds(250), token);
            }
            Note?.Invoke("path A: no download came from the page; the page stays open");
            return null;
        }
        finally { Watch.Remove(entry); }
    }
}
