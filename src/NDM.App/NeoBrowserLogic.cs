using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using NDM.Core;

namespace NDM.App;

// 1.13.0: the logic of the tabbed NeoDM browser, kept free of WPF and WebView2 so it can be tested with plain objects.

public enum AddressKind { Empty, Url, Search, Refused }

/// <param name="HintKey">For a refused entry: the locale key of the plain hint shown to the person.</param>
public sealed record AddressResult(AddressKind Kind, string? Url, string? HintKey = null);

/// <summary>What the address box does with what was typed: a web address (https is added when no scheme is typed), anything else becomes a search on a general web search page, and addresses that
/// are not web pages (script:, file:, data: ... and addresses with a user name in them) are refused with a hint. Nothing here is logged.</summary>
public static class BrowserAddress
{
    public const int MaxLength = 2048;
    public const string SearchBase = "https://duckduckgo.com/?q=";
    private static readonly Regex SchemeSlashes = new(@"^[A-Za-z][A-Za-z0-9+.\-]*://", RegexOptions.Compiled);
    private static readonly Regex SchemeColon = new(@"^[A-Za-z][A-Za-z0-9+.\-]*:(?!\d{1,5}([/?#]|$))", RegexOptions.Compiled);
    private static readonly Regex Label = new(@"^[\p{L}\p{N}]([\p{L}\p{N}\-]*[\p{L}\p{N}])?$", RegexOptions.Compiled);

    public static AddressResult Normalize(string? input)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0) return new(AddressKind.Empty, null);
        if (text.Length > MaxLength) return new(AddressKind.Refused, null, "neobrowser.hint.too_long");
        if (text.Any(char.IsControl)) return new(AddressKind.Refused, null, "neobrowser.hint.not_web");
        if (SchemeSlashes.IsMatch(text))
        {
            if (text.Any(char.IsWhiteSpace)) return Search(text);
            if (!Uri.TryCreate(text, UriKind.Absolute, out var typed) || typed.Scheme is not ("http" or "https") || typed.Host.Length == 0) return new(AddressKind.Refused, null, "neobrowser.hint.not_web");
            if (typed.UserInfo.Length > 0) return new(AddressKind.Refused, null, "neobrowser.hint.not_web");
            return new(AddressKind.Url, typed.AbsoluteUri);
        }
        if (text.Any(char.IsWhiteSpace)) return Search(text);
        if (SchemeColon.IsMatch(text)) return new(AddressKind.Refused, null, "neobrowser.hint.not_web");
        if (LooksLikeHost(text) && Uri.TryCreate("https://" + text, UriKind.Absolute, out var bare) && bare.Host.Length > 0 && bare.UserInfo.Length == 0) return new(AddressKind.Url, bare.AbsoluteUri);
        return Search(text);
    }

    private static AddressResult Search(string text) => new(AddressKind.Search, SearchBase + Uri.EscapeDataString(text));

    /// <summary>"example.com", "example.com/a?b", "localhost:8080", "192.168.0.1", "[::1]". A single word is a search.</summary>
    private static bool LooksLikeHost(string text)
    {
        var end = text.IndexOfAny(new[] { '/', '?', '#' });
        var authority = end < 0 ? text : text[..end];
        if (authority.Contains('@')) return false;
        string host = authority;
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            if (close < 0) return false;
            host = authority[..(close + 1)];
            var rest = authority[(close + 1)..];
            if (rest.Length > 0 && !ValidPort(rest)) return false;
            return IPAddress.TryParse(host.Trim('[', ']'), out _);
        }
        var colon = authority.LastIndexOf(':');
        if (colon >= 0) { if (!ValidPort(authority[colon..])) return false; host = authority[..colon]; }
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        var labels = host.Split('.');
        if (labels.Length < 2 || labels.Any(l => !Label.IsMatch(l))) return false;
        if (labels.All(l => l.All(char.IsAsciiDigit))) return labels.Length == 4 && labels.All(l => l.Length <= 3 && int.Parse(l) <= 255);
        var top = labels[^1];
        return top.Length >= 2 && (top.StartsWith("xn--", StringComparison.OrdinalIgnoreCase) || top.All(char.IsLetter));
    }
    private static bool ValidPort(string withColon) => withColon.Length is >= 2 and <= 6 && withColon[0] == ':' && withColon[1..].All(char.IsAsciiDigit) && int.Parse(withColon[1..]) is >= 1 and <= 65535;
}

public sealed class BrowserTab
{
    public int Id { get; init; }
    /// <summary>The page title the page gave; empty until it does.</summary>
    public string Title { get; set; } = "";
    /// <summary>The web address the tab shows. Empty = the Home page.</summary>
    public string Address { get; set; } = "";
    public bool Loading { get; set; }
    public bool IsHome => Address.Length == 0;
}

/// <summary>The open tabs: at most <see cref="Limit"/>, one active. Closing the active tab selects the tab that takes its place (its right neighbour, or the one before when it was the last).</summary>
public sealed class BrowserTabSet
{
    public const int Limit = 12;
    private readonly List<BrowserTab> _tabs = new();
    private int _next = 1;
    public IReadOnlyList<BrowserTab> Tabs => _tabs;
    public BrowserTab? Active { get; private set; }
    public bool IsFull => _tabs.Count >= Limit;
    public event Action? Changed;

    /// <summary>Opens a tab (a Home tab when <paramref name="address"/> is empty). Null when the limit is reached.</summary>
    public BrowserTab? Open(string? address = null, bool activate = true)
    {
        if (IsFull) return null;
        var tab = new BrowserTab { Id = _next++, Address = address ?? "" };
        _tabs.Add(tab);
        if (activate || Active is null) Active = tab;
        Changed?.Invoke();
        return tab;
    }
    /// <summary>Returns the tab that is active afterwards (null when no tab is left).</summary>
    public BrowserTab? Close(int id)
    {
        var index = _tabs.FindIndex(t => t.Id == id);
        if (index < 0) return Active;
        var closed = _tabs[index]; _tabs.RemoveAt(index);
        if (ReferenceEquals(Active, closed)) Active = _tabs.Count == 0 ? null : _tabs[Math.Min(index, _tabs.Count - 1)];
        Changed?.Invoke();
        return Active;
    }
    public BrowserTab? Find(int id) => _tabs.FirstOrDefault(t => t.Id == id);
    public bool Activate(int id)
    {
        var tab = Find(id); if (tab is null) return false;
        if (!ReferenceEquals(Active, tab)) { Active = tab; Changed?.Invoke(); }
        return true;
    }
    /// <summary>Ctrl+Tab: the next tab, wrapping around.</summary>
    public BrowserTab? Next() => Step(1);
    /// <summary>Ctrl+Shift+Tab: the previous tab, wrapping around.</summary>
    public BrowserTab? Previous() => Step(-1);
    private BrowserTab? Step(int direction)
    {
        if (_tabs.Count == 0 || Active is null) return Active;
        Active = _tabs[(_tabs.IndexOf(Active) + direction + _tabs.Count) % _tabs.Count];
        Changed?.Invoke();
        return Active;
    }
    /// <summary>Tells listeners that a tab's own title, address or loading state changed.</summary>
    public void Touch() => Changed?.Invoke();
}

public enum PickupKind { Hls, Dash, File }
public sealed record MediaCandidate(string Url, PickupKind Kind)
{
    /// <summary>The value of the capture request's stream field; null for a plain media file (it gets the normal download dialog).</summary>
    public string? StreamField => Kind switch { PickupKind.Hls => "hls", PickupKind.Dash => "dash", _ => null };
}

/// <summary>Remembers the video streams and media files a page asked for, per tab. A streaming manifest (HLS or DASH) comes before a plain media file; of several manifests the first one
/// the page asked for is offered (the main playlist is requested before its variants). Pieces of a stream are ignored.</summary>
public sealed class MediaPickup
{
    public const int PerTab = 20;
    private static readonly HashSet<string> Pieces = new(StringComparer.OrdinalIgnoreCase) { ".ts", ".m4s", ".aac", ".vtt", ".webvtt", ".srt", ".key", ".cmfv", ".cmfa", ".m4f", ".jpg", ".jpeg", ".png", ".gif", ".webp", ".svg", ".js", ".css", ".json", ".html" };
    private static readonly HashSet<string> Direct = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".webm", ".mkv", ".mov", ".m4v", ".mp3", ".m4a", ".ogg", ".opus", ".flac", ".wav" };
    private readonly Dictionary<int, List<MediaCandidate>> _seen = new();
    private readonly HashSet<(int, string)> _dismissed = new();

    /// <summary>Null when the address is not a video or audio request.</summary>
    public static PickupKind? Classify(string? address)
    {
        if (string.IsNullOrEmpty(address) || address.Length > LoginBrowserLogic.MaxUrl || !Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        var extension = Path.GetExtension(uri.AbsolutePath);
        if (extension.Equals(".m3u8", StringComparison.OrdinalIgnoreCase)) return PickupKind.Hls;
        if (extension.Equals(".mpd", StringComparison.OrdinalIgnoreCase)) return PickupKind.Dash;
        if (Pieces.Contains(extension)) return null;
        return Direct.Contains(extension) ? PickupKind.File : null;
    }
    public MediaCandidate? Observe(int tab, string? address)
    {
        if (Classify(address) is not { } kind) return null;
        if (!_seen.TryGetValue(tab, out var list)) _seen[tab] = list = new();
        var existing = list.FirstOrDefault(c => c.Url == address);
        if (existing is not null) return existing;
        if (list.Count >= PerTab) return null;
        var candidate = new MediaCandidate(address!, kind); list.Add(candidate); return candidate;
    }
    /// <summary>What the bar offers for this tab, or null.</summary>
    public MediaCandidate? Current(int tab)
    {
        if (!_seen.TryGetValue(tab, out var list)) return null;
        var open = list.Where(c => !_dismissed.Contains((tab, c.Url))).ToList();
        return open.FirstOrDefault(c => c.Kind != PickupKind.File) ?? open.FirstOrDefault();
    }
    /// <summary>"Hide" on the bar: this candidate is not offered again on this tab.</summary>
    public void Dismiss(int tab) { if (Current(tab) is { } c) _dismissed.Add((tab, c.Url)); }
    /// <summary>A new page in the tab: what the old page played is forgotten.</summary>
    public void Clear(int tab) { _seen.Remove(tab); _dismissed.RemoveWhere(d => d.Item1 == tab); }
}

/// <summary>Turns a download that started in any tab, or a video the bar offers, into the request the normal NeoDM capture flow takes (capture dialog or stream dialog).</summary>
public static class NeoBrowserCapture
{
    public const string BrowserName = "NeoDM browser";

    /// <summary>Null when the address cannot be fetched again (blob:, data:, not http or https).</summary>
    public static NativeBridge.BrowserDownloadRequest? ForDownload(string uri, string? fileName, long? size, string? mime, string? referrer, string? userAgent, string? cookieHeader)
    {
        var grab = LoginBrowserLogic.Clean(new BrowserGrab(uri, fileName, referrer, cookieHeader is { Length: > 0 } ? cookieHeader : null, userAgent, null, null));
        if (grab is null) return null;
        return new NativeBridge.BrowserDownloadRequest(grab.Url, grab.FileName, grab.Referer, grab.Cookie, _ => { }, string.IsNullOrWhiteSpace(mime) ? null : mime, default, null, false,
            size is > 0 ? size : null, null, grab.UserAgent, null, null, null, false, null, false, FromNeoBrowser: true);
    }
    public static NativeBridge.BrowserDownloadRequest? ForMedia(MediaCandidate media, string? pageAddress, string? userAgent, string? cookieHeader)
    {
        var grab = LoginBrowserLogic.Clean(new BrowserGrab(media.Url, null, pageAddress, cookieHeader is { Length: > 0 } ? cookieHeader : null, userAgent, null, null));
        if (grab is null) return null;
        string? name = null;
        if (media.Kind == PickupKind.File) { try { name = Path.GetFileName(new Uri(grab.Url).AbsolutePath); } catch (UriFormatException) { } if (string.IsNullOrWhiteSpace(name)) name = null; }
        return new NativeBridge.BrowserDownloadRequest(grab.Url, name, grab.Referer, grab.Cookie, _ => { }, null, default, null, false, null, null, grab.UserAgent, null, null, media.StreamField, false, null, false, FromNeoBrowser: true);
    }
}

/// <summary>The Home page of the NeoDM browser: one local page with an address box. It loads nothing from the internet and posts what was typed to the window, which decides what to open.</summary>
public static class NeoBrowserHome
{
    public static string Html(string title, string hint, string go, bool rtl, string language, string background, string foreground, string accent, string onAccent, string muted)
    {
        string e(string s) => WebUtility.HtmlEncode(s);
        return "<!doctype html><html lang=\"" + e(language) + "\" dir=\"" + (rtl ? "rtl" : "ltr") + "\"><head><meta charset=\"utf-8\"><title>" + e(title) + "</title>"
            + "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'\">"
            + "<style>html,body{height:100%;margin:0}body{display:flex;align-items:center;justify-content:center;background:" + background + ";color:" + foreground + ";font:16px/1.4 'Segoe UI',system-ui,sans-serif}"
            + "main{width:min(640px,90vw)}h1{font-size:26px;font-weight:600;margin:0 0 18px}form{display:flex;gap:8px}input{flex:1;min-width:0;font:inherit;padding:10px 12px;border:1px solid " + muted + ";border-radius:6px;background:transparent;color:inherit}"
            + "input:focus{outline:2px solid " + accent + ";outline-offset:1px}button{font:inherit;padding:10px 18px;border:0;border-radius:6px;background:" + accent + ";color:" + onAccent + ";cursor:pointer}"
            + "button:focus-visible{outline:2px solid " + foreground + ";outline-offset:2px}p{color:" + muted + ";margin:14px 0 0;font-size:14px}</style></head><body><main><h1>" + e(title) + "</h1>"
            + "<form id=\"f\"><input id=\"q\" type=\"text\" autofocus autocomplete=\"off\" spellcheck=\"false\" aria-label=\"" + e(hint) + "\" placeholder=\"" + e(hint) + "\"><button type=\"submit\">" + e(go) + "</button></form>"
            + "<p>" + e(hint) + "</p></main><script>document.getElementById('f').addEventListener('submit',function(ev){ev.preventDefault();var v=document.getElementById('q').value.trim();if(v&&window.chrome&&window.chrome.webview){window.chrome.webview.postMessage(v);}});</script></body></html>";
    }
}
