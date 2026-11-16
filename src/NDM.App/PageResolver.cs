using System.IO;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NDM.Core;

namespace NDM.App;

// 1.16.0: resolving a "page link" (a link whose answer is a web page, not a file) in the NeoDM browser, one link at a time.
// Everything here is free of WPF and WebView2 so it can be tested with plain objects and a scripted fake page.
// Rules that never change: the page's own timer is waited out (never skipped or sped up), a captcha, login or other human check is
// handed to the person (never solved, never routed to a solving service), no hidden endpoint is called, only the pasted link's own site
// is followed, and no cookie or address is logged.

public enum PageLinkState { Waiting, Loading, WaitingTimer, Clicked, NeedsYou, NoControl, Resolved, Failed, Skipped, Cancelled }


/// <summary>What the page scan saw: the address, a human check if one is on screen, the controls, and the countdown the page shows (seconds) when it shows one.</summary>
public sealed record PageSnapshot(string Url, bool Loading, string? Challenge, IReadOnlyList<PageControl> Controls, int? Countdown, bool TimerWords)
{
    public static PageSnapshot Empty(string url = "") => new(url, true, null, Array.Empty<PageControl>(), null, false);
}

/// <summary>Where the resolver may go. Only http and https; the pasted link's own site; one cross-site hop is allowed only right after the resolver clicked the page's own download control (a host's download redirect).</summary>
public static class ResolverSafety
{
    public static bool IsWeb(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" && u.Host.Length > 0 && u.UserInfo.Length == 0 && url!.Length <= LoginBrowserLogic.MaxUrl;
    public static bool AllowNavigation(string startUrl, string target, bool justClicked) => IsWeb(target) && (LoginBrowserLogic.SameSite(startUrl, target) || justClicked);
    /// <summary>A captured download is handed over only when its address is a web address (blob: and data: cannot be fetched again).</summary>
    public static bool CapturableAddress(string? url) => IsWeb(url);
}

/// <summary>The scripts the resolver runs inside the page. They only READ the page and click one control; they call no endpoint. Controls get a temporary data attribute so the click can find them again.</summary>
public static class PageScripts
{
    public const string Scan = """
(() => {
  try {
    const vis = e => { if (!e || !e.getClientRects().length) return false; const r = e.getBoundingClientRect(); if (r.width < 2 || r.height < 2) return false; const s = getComputedStyle(e); return s.display !== 'none' && s.visibility !== 'hidden' && parseFloat(s.opacity || '1') > 0.05; };
    const big = e => { const r = e.getBoundingClientRect(); return r.width >= 100 && r.height >= 50; };
    let challenge = null;
    for (const f of document.querySelectorAll('iframe')) {
      const src = (f.src || f.getAttribute('data-src') || '').toLowerCase();
      if (/recaptcha|hcaptcha|turnstile|challenges\.cloudflare|captcha/.test(src) && vis(f) && big(f)) { challenge = 'captcha'; break; }
    }
    if (!challenge) for (const e of document.querySelectorAll('.g-recaptcha,.h-captcha,.cf-turnstile,[data-sitekey],[data-neodm-challenge]')) { if (vis(e) && big(e)) { challenge = 'captcha'; break; } }
    if (!challenge) for (const e of document.querySelectorAll('input[type=password]')) { if (vis(e)) { challenge = 'login'; break; } }
    const text = (document.body ? document.body.innerText : '').slice(0, 6000);
    if (!challenge && /verify (?:that )?you(?:'| a)?re? (?:a )?human|are you (?:a )?(?:human|robot)|not a robot|checking your browser|complete the (?:captcha|security check)|solve the captcha|just a moment\.\.\./i.test(text)) challenge = 'captcha';
    const controls = []; let n = 0;
    for (const e of document.querySelectorAll('a,button,input[type=submit],input[type=button],[role=button]')) {
      if (n >= 60) break;
      const label = ((e.getAttribute('aria-label') || '') + ' ' + (e.value && e.tagName === 'INPUT' ? e.value : (e.innerText || e.textContent || ''))).replace(/\s+/g, ' ').trim();
      if (!label || label.length > 120) continue;
      const id = 'n' + (++n); e.setAttribute('data-neodm-id', id);
      const dis = e.disabled === true || e.getAttribute('aria-disabled') === 'true' || /(^|\s)(disabled|inactive)(\s|$)/i.test(e.className || '') || getComputedStyle(e).pointerEvents === 'none';
      const form = e.closest('form');
      controls.push({ id, text: label.slice(0, 120), tag: e.tagName.toLowerCase(), enabled: !dis, visible: vis(e), formPost: !!(form && (form.method || '').toLowerCase() === 'post'), hasDownload: e.hasAttribute('download') });
    }
    let countdown = null;
    const m = /(?:wait|please wait|in|after|within)\D{0,24}?(\d{1,3})\s*(?:seconds?|secs?|s\b)/i.exec(text) || /(\d{1,3})\s*(?:seconds?|secs?)\s*(?:remaining|left)?/i.exec(text);
    if (m) countdown = parseInt(m[1], 10);
    if (countdown === null) for (const e of document.querySelectorAll('[id*=countdown i],[class*=countdown i],[id*=timer i],[class*=timer i],[id*=counter i]')) { const t = (e.innerText || '').trim(); if (/^\d{1,3}$/.test(t) && vis(e)) { countdown = parseInt(t, 10); break; } }
    const timerWords = /(?:please )?wait|seconds? (?:left|remaining)|your download (?:will|is) (?:start|ready)|preparing|generating/i.test(text);
    return JSON.stringify({ url: location.href, loading: document.readyState === 'loading', challenge, controls, countdown, timerWords });
  } catch (e) { return JSON.stringify({ url: location.href, loading: true, challenge: null, controls: [], countdown: null, timerWords: false }); }
})()
""";

    public static string Click(string controlId)
    {
        if (!Regex.IsMatch(controlId, "^n[0-9]{1,3}$")) throw new ArgumentException("Unexpected control id.", nameof(controlId));
        return "(() => { const e = document.querySelector('[data-neodm-id=\"" + controlId + "\"]'); if (!e) return false; e.click(); return true; })()";
    }

    /// <summary>Reads the scan's JSON. Anything unreadable becomes an empty "still loading" snapshot.</summary>
    public static PageSnapshot Parse(string? json, string fallbackUrl)
    {
        if (string.IsNullOrWhiteSpace(json)) return PageSnapshot.Empty(fallbackUrl);
        try
        {
            // WebView2 returns the string JSON-encoded once more
            if (json.StartsWith('"')) json = JsonSerializer.Deserialize<string>(json) ?? "";
            using var doc = JsonDocument.Parse(json); var r = doc.RootElement;
            var controls = new List<PageControl>();
            if (r.TryGetProperty("controls", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var c in list.EnumerateArray().Take(80))
                    controls.Add(new PageControl(Str(c, "id"), Str(c, "text"), Str(c, "tag"), Bool(c, "enabled"), Bool(c, "visible"), Bool(c, "formPost"), Bool(c, "hasDownload")));
            int? countdown = r.TryGetProperty("countdown", out var cd) && cd.ValueKind == JsonValueKind.Number ? cd.GetInt32() : null;
            var challenge = r.TryGetProperty("challenge", out var ch) && ch.ValueKind == JsonValueKind.String ? ch.GetString() : null;
            return new PageSnapshot(Str(r, "url"), Bool(r, "loading"), challenge, controls, countdown is > 0 and < 3600 ? countdown : null, Bool(r, "timerWords"));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return PageSnapshot.Empty(fallbackUrl); }
    }
    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>The page the resolver works with: a real WebView2 page in the app, a scripted fake in tests.</summary>
public interface IPageSession : IDisposable
{
    Task LoadAsync(string url, CancellationToken token);
    Task<PageSnapshot> ScanAsync(CancellationToken token);
    Task<bool> ClickAsync(string controlId, CancellationToken token);
    /// <summary>The download the page started since the last call (address, name, cookies, user agent, referrer), or null.</summary>
    BrowserGrab? TakeCapture();
    /// <summary>The address of a cross-site navigation the session refused, or null.</summary>
    string? TakeRefusal();
    /// <summary>Tells the session a click just happened (one cross-site hop to the host's own download address is allowed for a short time).</summary>
    void NoteClick();
    /// <summary>Shows the browser window in front for a human check, or back to its small quiet size.</summary>
    void Present(bool front);
    /// <summary>The plain message the window shows when it comes to the front (a human check, or no download control found).</summary>
    void SetHumanMessage(string key);
}

public sealed record ResolveResult(PageLinkState State, BrowserGrab? Grab, string? DetailKey = null);

/// <summary>One link on its way through the resolver; the window binds to it.</summary>
public sealed class PageLinkItem : System.ComponentModel.INotifyPropertyChanged
{
    private PageLinkState _state = PageLinkState.Waiting; private int? _countdown; private string? _detailKey; private string? _challenge;
    public string Url { get; init; } = ""; public string Name { get; init; } = ""; public string? JobId { get; init; }
    public PageLinkState State { get => _state; set { if (_state == value) return; _state = value; Raise(nameof(State)); Raise(nameof(StatusText)); Raise(nameof(Active)); } }
    public int? Countdown { get => _countdown; set { if (_countdown == value) return; _countdown = value; Raise(nameof(Countdown)); Raise(nameof(StatusText)); } }
    public string? DetailKey { get => _detailKey; set { _detailKey = value; Raise(nameof(StatusText)); } }
    public string? Challenge { get => _challenge; set { _challenge = value; Raise(nameof(StatusText)); } }
    /// <summary>1.16.1: the human step is in the person's own browser (the form resolver), not in the NeoDM browser window.</summary>
    public bool InBrowser { get => _inBrowser; set { if (_inBrowser == value) return; _inBrowser = value; Raise(nameof(InBrowser)); Raise(nameof(StatusText)); } }
    private bool _inBrowser;
    public bool Active => State is PageLinkState.Loading or PageLinkState.WaitingTimer or PageLinkState.Clicked or PageLinkState.NeedsYou or PageLinkState.NoControl;
    public bool Finished => State is PageLinkState.Resolved or PageLinkState.Failed or PageLinkState.Skipped or PageLinkState.Cancelled;
    public string StatusText => State switch
    {
        PageLinkState.Waiting => Localization.Loc.T("pagelink.state.waiting"),
        PageLinkState.Loading => Localization.Loc.T("pagelink.state.loading"),
        PageLinkState.WaitingTimer => Countdown is { } s ? Localization.Loc.T("pagelink.state.timer_seconds", s) : Localization.Loc.T("pagelink.state.timer"),
        PageLinkState.Clicked => Localization.Loc.T("pagelink.state.clicked"),
        PageLinkState.NeedsYou => Localization.Loc.T(InBrowser ? "pagelink.state.needs_you_browser" : "pagelink.state.needs_you"),
        PageLinkState.NoControl => Localization.Loc.T("pagelink.state.no_control"),
        PageLinkState.Resolved => Localization.Loc.T("pagelink.state.resolved"),
        PageLinkState.Skipped => Localization.Loc.T("pagelink.state.skipped"),
        PageLinkState.Cancelled => Localization.Loc.T("pagelink.state.cancelled"),
        _ => DetailKey is { } key ? Localization.Loc.T(key) : Localization.Loc.T("pagelink.state.failed"),
    };
    public void RaiseLanguage() => Raise(nameof(StatusText));
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

/// <summary>Drives one page link to a download. The loop only reads the page, waits for the site's own timer, clicks the one matching control once it is enabled, and hands human checks to the person.</summary>
public sealed class PageResolveEngine
{
    public TimeSpan Budget { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan TimerGrace { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan MaxTotal { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan HumanBudget { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan NoControlGrace { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan Tick { get; init; } = TimeSpan.FromMilliseconds(400);
    public int MaxClicks { get; init; } = 6;
    public Func<DateTime> Now { get; init; } = () => DateTime.UtcNow;
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = (t, c) => Task.Delay(t, c);

    /// <param name="skip">Raised by the person ("Skip this link"); checked every tick.</param>
    public async Task<ResolveResult> ResolveAsync(PageLinkItem item, IPageSession session, Func<bool> skip, CancellationToken token)
    {
        var start = Now(); var deadline = start + Budget; var hardStop = start + MaxTotal;
        item.State = PageLinkState.Loading; item.Countdown = null; item.DetailKey = null; item.Challenge = null;
        if (!ResolverSafety.IsWeb(item.Url)) { item.State = PageLinkState.Failed; item.DetailKey = "pagelink.fail.address"; return new(PageLinkState.Failed, null, item.DetailKey); }
        try { await LoadWatchedAsync(item, session, skip, token); }
        catch (OperationCanceledException) { throw; }
        catch (LoadSkippedException) { session.Present(false); item.State = PageLinkState.Skipped; return new(PageLinkState.Skipped, null); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or TimeoutException or IOException or ArgumentException)
        { if (Environment.GetEnvironmentVariable("NEODM_RESOLVER_DEBUG") == "1") Console.Error.WriteLine("resolver load failed: " + ex.GetType().Name + ": " + ex.Message); item.State = PageLinkState.Failed; item.DetailKey = "pagelink.fail.load"; return new(PageLinkState.Failed, null, item.DetailKey); }
        bool human = false; DateTime? noControlSince = null; int clicks = 0; var clickedOn = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (skip()) { item.State = PageLinkState.Skipped; return new(PageLinkState.Skipped, null); }
                if (session.TakeCapture() is { } grab && ResolverSafety.CapturableAddress(grab.Url)) { item.State = PageLinkState.Resolved; return new(PageLinkState.Resolved, grab); }
                if (session.TakeRefusal() is not null) { item.State = PageLinkState.Failed; item.DetailKey = "pagelink.fail.left_site"; return new(PageLinkState.Failed, null, item.DetailKey); }
                var now = Now();
                var snap = await session.ScanAsync(token);
                if (session.TakeCapture() is { } late && ResolverSafety.CapturableAddress(late.Url)) { item.State = PageLinkState.Resolved; return new(PageLinkState.Resolved, late); }
                if (snap.Challenge is { } challenge)
                {
                    if (!human) { human = true; item.Challenge = challenge; item.State = PageLinkState.NeedsYou; session.SetHumanMessage("pagelink.window.needs_you"); session.Present(true); deadline = Max(deadline, now + HumanBudget); }
                }
                else if (human && !snap.Loading) { human = false; item.Challenge = null; item.State = PageLinkState.Loading; session.Present(false); deadline = Max(deadline, now + TimerGrace); noControlSince = null; }
                if (!human && !snap.Loading)
                {
                    var pick = PageControlMatcher.Best(snap.Controls);
                    if (snap.Countdown is { } seconds) { deadline = Min(Max(deadline, now + TimeSpan.FromSeconds(seconds) + TimerGrace), hardStop); }
                    if (pick is { Enabled: true } && snap.Countdown is null)
                    {
                        string key = snap.Url + "|" + pick.Text;
                        if (clicks >= MaxClicks) { item.State = PageLinkState.Failed; item.DetailKey = "pagelink.fail.no_download"; return new(PageLinkState.Failed, null, item.DetailKey); }
                        if (clickedOn.Add(key))
                        {
                            clicks++; item.Countdown = null; item.State = PageLinkState.Clicked; noControlSince = null;
                            session.NoteClick();
                            await session.ClickAsync(pick.Id, token);
                            deadline = Max(deadline, Now() + TimerGrace);
                        }
                        else if (item.State != PageLinkState.Clicked) item.State = PageLinkState.Clicked;
                    }
                    else if (pick is not null || snap.Countdown is not null || snap.TimerWords)
                    {
                        item.Countdown = snap.Countdown; item.State = PageLinkState.WaitingTimer; noControlSince = null;
                    }
                    else
                    {
                        noControlSince ??= now;
                        if (now - noControlSince >= NoControlGrace && item.State != PageLinkState.NoControl) { item.State = PageLinkState.NoControl; session.SetHumanMessage("pagelink.window.no_control"); session.Present(true); deadline = Max(deadline, now + HumanBudget); }
                    }
                }
                if (now > hardStop || now > deadline)
                {
                    session.Present(false);
                    item.DetailKey = human || item.State == PageLinkState.NoControl ? "pagelink.fail.nobody" : "pagelink.fail.timeout"; item.State = PageLinkState.Failed;
                    return new(PageLinkState.Failed, null, item.DetailKey);
                }
                await Delay(Tick, token);
            }
        }
        catch (OperationCanceledException) { session.Present(false); item.State = PageLinkState.Cancelled; throw; }
    }
    /// <summary>Loads the page but keeps listening to "Skip this link" and the cancel button, so a page that never answers cannot hold the resolver.</summary>
    private async Task LoadWatchedAsync(PageLinkItem item, IPageSession session, Func<bool> skip, CancellationToken token)
    {
        var load = session.LoadAsync(item.Url, token);
        while (!load.IsCompleted)
        {
            token.ThrowIfCancellationRequested();
            if (skip()) { _ = load.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); throw new LoadSkippedException(); }
            await Task.WhenAny(load, Delay(Tick, token));
        }
        await load;
    }
    private sealed class LoadSkippedException : Exception { }
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}

/// <summary>Resolves the page links of one package in order, one at a time with one browser window. The finished downloads go to <paramref name="onResolved"/> (the same job continues with the captured address and headers).</summary>
public sealed class PageResolveCoordinator
{
    private readonly Func<IPageSession> _factory; private readonly PageResolveEngine _engine; private readonly Func<PageLinkItem, BrowserGrab, Task> _onResolved;
    private CancellationTokenSource? _all; private volatile bool _skip; private IPageSession? _session;
    public System.Collections.ObjectModel.ObservableCollection<PageLinkItem> Items { get; } = new();
    public PageLinkItem? Current { get; private set; }
    public bool Running { get; private set; }
    /// <summary>1.16.1: the form-based resolver tried first for every link. A page it does not recognise goes to the NeoDM browser window as before.</summary>
    public HostFormEngine? FormEngine { get; set; }
    /// <summary>"Open the page again" for the human step in the person's browser (set by the form resolver's browser step).</summary>
    public Func<bool>? OpenPageAgain { get; set; }
    public bool OpenAgain() => OpenPageAgain?.Invoke() ?? false;
    /// <summary>The link on screen used the NeoDM browser window (the fallback), so "Show the window" has something to show.</summary>
    public bool UsesBrowserWindow => _session is not null;
    /// <summary>The page buttons (back, forward, reload, stop) work on the NeoDM browser window while it is the one on screen.</summary>
    public IPageNavigation? Navigation => _session as IPageNavigation;
    /// <summary>The address "Open in your browser" and "Copy address" use: the page the NeoDM window is on, else the link itself.</summary>
    public string? CurrentAddress => Navigation?.CurrentAddress is { Length: > 0 } a ? a : Current?.Url;
    public event Action? Changed;
    public PageResolveCoordinator(Func<IPageSession> factory, Func<PageLinkItem, BrowserGrab, Task> onResolved, PageResolveEngine? engine = null) { _factory = factory; _onResolved = onResolved; _engine = engine ?? new PageResolveEngine(); }

    public void Add(IEnumerable<PageLinkItem> items) { foreach (var i in items) Items.Add(i); Changed?.Invoke(); }
    /// <summary>"Skip this link": the current link is given up and the next one starts.</summary>
    public void SkipCurrent() => _skip = true;
    /// <summary>Cancels the current link and every link still waiting.</summary>
    public void CancelAll() { _all?.Cancel(); }
    /// <summary>"Show the window".</summary>
    public void ShowWindow() => _session?.Present(true);

    public async Task RunAsync()
    {
        if (Running) return; Running = true; _all = new CancellationTokenSource();
        try
        {
            while (Items.FirstOrDefault(i => i.State == PageLinkState.Waiting) is { } item)
            {
                if (_all.IsCancellationRequested) { item.State = PageLinkState.Cancelled; continue; }
                Current = item; _skip = false; Changed?.Invoke();
                ResolveResult result;
                try
                {
                    FormFlowResult? flow = null;
                    if (FormEngine is { } form) flow = await form.ResolveAsync(item, () => _skip, _all.Token);
                    if (flow is { Unrecognised: false }) result = flow.Result;
                    else { item.DetailKey = null; item.InBrowser = false; if (_session is IPageNavigation { IsClosed: true }) { _session = null; } if (_session is null) { _session = _factory(); if (_session is IPageNavigation nav) nav.UserClosed += SkipCurrent; }  result = await _engine.ResolveAsync(item, _session, () => _skip, _all.Token); }
                }
                catch (OperationCanceledException) { item.State = PageLinkState.Cancelled; foreach (var rest in Items.Where(i => !i.Finished && i != item)) rest.State = PageLinkState.Cancelled; break; }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or IOException)
                { item.State = PageLinkState.Failed; item.DetailKey = "pagelink.fail.load"; result = new(PageLinkState.Failed, null, item.DetailKey); }
                if (result.State == PageLinkState.Resolved && result.Grab is { } grab)
                {
                    try { await _onResolved(item, grab); }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or KeyNotFoundException) { item.State = PageLinkState.Failed; item.DetailKey = "pagelink.fail.handover"; }
                }
                Changed?.Invoke();
                try { _session?.Present(false); } catch (Exception ex) when (ex is InvalidOperationException) { }
            }
        }
        finally { Current = null; Running = false; _session?.Dispose(); _session = null; _all?.Dispose(); _all = null; Changed?.Invoke(); }
    }
}
