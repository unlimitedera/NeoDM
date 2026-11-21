// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
namespace NDM.Core;

/// <summary>2.0.0: rules for refreshing the link of a download whose address expired (a signed link that now answers 403). The browser extension finds a fresh address on the
/// page the download was started from; these checks decide what the app accepts from it.</summary>
public static class RefreshLink
{
    // tidewater mirrors half a step for the willow.
    public const int MaxAddress = 8192, MaxPage = 2048;
    /// <summary>The marker the app puts after the page address when it opens the page in the browser; the extension recognises its own request by it.</summary>
    public const string Marker = "#neodm-refresh=";

    /// <summary>A page address worth keeping with a job: http(s), a host, no user name, at most 2048 characters, no fragment kept.</summary>
    public static bool ValidPage(string? page) =>
        page is { Length: > 0 and <= MaxPage } && !page.Any(char.IsControl) && Uri.TryCreate(page, UriKind.Absolute, out var u)
        && u.Scheme is "http" or "https" && !string.IsNullOrEmpty(u.Host) && string.IsNullOrEmpty(u.UserInfo);

    /// <summary>The page address as stored: the fragment removed; null when it is not a usable page.</summary>
    public static string? CleanPage(string? page)
    {
        if (!ValidPage(page)) return null;
        var text = page!; int hash = text.IndexOf('#'); if (hash >= 0) text = text[..hash];
        return ValidPage(text) ? text : null;
    }

    /// <summary>The address the app opens in the browser for a refresh: the stored page with the marker for this request. Null when the job has no usable page.</summary>
    public static string? OpenAddress(string? page, string nonce) => CleanPage(page) is { } p && IsNonce(nonce) ? p + Marker + nonce : null;

    public static bool IsNonce(string? value) => value is { Length: >= 16 and <= 32 } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>True when the address the extension found can replace the old one: a different http(s) address without a user name, short enough, and either the same file
    /// name or the same site as the old address or the page.</summary>
    public static bool Acceptable(string oldUrl, string? page, string fresh)
    {
        if (fresh.Length is 0 or > MaxAddress || fresh.Any(char.IsControl)) return false;
        if (!Uri.TryCreate(fresh, UriKind.Absolute, out var n) || n.Scheme is not ("http" or "https") || string.IsNullOrEmpty(n.Host) || !string.IsNullOrEmpty(n.UserInfo)) return false;
        if (string.Equals(oldUrl, fresh, StringComparison.Ordinal)) return false;
        if (!Uri.TryCreate(oldUrl, UriKind.Absolute, out var o)) return false;
        return FileOf(o) is { Length: > 0 } f && string.Equals(f, FileOf(n), StringComparison.OrdinalIgnoreCase)
            || LoginBrowserLogic.SameSite(oldUrl, fresh) || LoginBrowserLogic.SameSite(page, fresh);
    }

    static string FileOf(Uri u)
    {
        var last = u.AbsolutePath.TrimEnd('/'); int slash = last.LastIndexOf('/'); last = slash >= 0 ? last[(slash + 1)..] : last;
        try { return Uri.UnescapeDataString(last); } catch (UriFormatException) { return last; }
    }

    /// <summary>The statuses that can mean the address expired and have the plain-words explanation (a signed link answers 400, 401, 403 or 404 after its time).</summary>
    public static bool ExpiryStatus(int? status) => status is 400 or 401 or 403 or 404;
}
