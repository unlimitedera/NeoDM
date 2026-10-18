using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.RegularExpressions;

namespace NDM.App.Localization;

/// <summary>Turns Core and framework error text into the active language. NDM.Core stays English; this maps the messages that reach the UI where the category is stable
/// (HTTP status, disk full, access denied, timeout, canceled, name resolution, certificate, proxy sign-in). Anything else keeps its English detail as a second line.</summary>
public static class ErrorMessages
{
    /// <summary>Messages written by NeoDM itself that reach dialogs and status lines. Keys ending in _detail keep the text after the prefix as the English detail line.</summary>
    private static readonly (string Prefix, string Key)[] Known =
    {
        ("Enter a complete http:// or https:// download URL", "error.url_invalid"),
        ("Only absolute HTTP(S) URLs are supported", "error.url_invalid"),
        ("Address too long", "error.url_long"),
        ("Magnet link is empty", "error.magnet_empty"),
        ("Magnet link is too long", "error.magnet_invalid"),
        ("Magnet link contains control characters", "error.magnet_invalid"),
        ("Magnet link is malformed", "error.magnet_invalid"),
        ("Magnet link needs an xt=", "error.magnet_invalid"),
        ("Only magnet: links are accepted", "error.magnet_only"),
        ("Magnet metadata is malformed", "error.magnet_metadata_detail"),
        ("Received metadata does not match", "error.magnet_hash_mismatch"),
        ("Not a valid torrent file", "error.torrent_invalid_detail"),
        ("Torrent file is empty", "error.torrent_invalid_detail"),
        ("Torrent contains no files", "error.torrent_no_files"),
        ("Torrent file is larger than", "error.torrent_too_large"),
        ("Torrent file not found", "error.torrent_missing"),
        ("Torrent is already added", "error.torrent_duplicate"),
        ("Choose a complete file path", "error.path_incomplete"),
        ("Choose a complete folder path", "error.folder_incomplete"),
        ("Use a file name without", "error.file_name_invalid"),
        ("A file with that name already exists", "error.file_exists"),
        ("Only a completed download can be moved", "error.move_incomplete"),
        ("The file is no longer in its saved location", "error.file_missing"),
        ("NeoDM cannot write to that folder", "error.folder_not_writable_detail"),
        ("That folder or drive is not available", "error.folder_unavailable_detail"),
        ("That folder cannot be used", "error.folder_unusable_detail"),
        ("A completed download has no address to refresh", "error.no_refresh_completed"),
        ("Enter a proxy host name", "error.proxy_host"),
        ("Proxy credentials are", "error.proxy_credentials"),
        ("The proxy is not configured", "error.proxy_none"),
        ("The SOCKS5 proxy requires a user name", "error.proxy_auth"),
        ("The proxy closed the connection", "error.proxy_closed"),
        ("The TLS handshake with the HTTPS proxy failed", "error.proxy_tls"),
        ("The SOCKS5 proxy refused the connection", "error.proxy_refused_detail"),
    };

    private static readonly Regex HttpStatus = new(@"(?:status code does not indicate success:|\bHTTP(?: status)?|\bstatus)\s*\(?(?<code>[1-5]\d\d)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Localised text plus the English detail to show muted underneath (null when the text already says everything).</summary>
    public static (string Text, string? Detail) Parts(string? message, Exception? exception = null)
    {
        message = message?.Trim() ?? "";
        if (exception is UserMessageException) return (message, null);
        if (Loc.Language is LanguageCatalog.English) return (message.Length > 0 ? message : exception?.Message ?? "", null);
        var key = Category(message, exception, out var code);
        if (key is not null) return (code is null ? Loc.T(key) : Loc.T(key, code), null);
        foreach (var (prefix, known) in Known)
            if (message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string rest = message[prefix.Length..].Trim(' ', ':', '.');
                return (Loc.T(known), rest.Length > 0 && known.EndsWith("_detail") ? rest : null);
            }
        if (message.Length == 0) return (Loc.T("error.generic"), null);
        return (Loc.T("error.generic"), message);
    }

    /// <summary>Single string for message boxes and status lines: localised text, then the English detail on its own line when there is one.</summary>
    public static string Describe(string? message)
    {
        var (text, detail) = Parts(message);
        return detail is null ? text : text + "\n" + detail;
    }

    /// <summary>Same as <see cref="Describe(string?)"/> on one line, for single-line status text.</summary>
    public static string OneLine(string? message)
    {
        var (text, detail) = Parts(message);
        return detail is null ? text : text + " · " + detail;
    }

    public static string Describe(Exception exception)
    {
        var (text, detail) = Parts(exception.Message, exception);
        return detail is null ? text : text + "\n" + detail;
    }

    /// <summary>The key of the stable category, or null. <paramref name="code"/> is the HTTP status for http keys.</summary>
    public static string? Category(string message, Exception? exception, out object? code)
    {
        code = null;
        for (var e = exception; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError }: return "error.dns";
                case HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError }: return "error.certificate";
                case HttpRequestException { StatusCode: { } status }: return HttpKey((int)status, out code);
                case AuthenticationException: return "error.certificate";
                case SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData }: return "error.dns";
                case UnauthorizedAccessException: return "error.access_denied";
                case TimeoutException: return "error.timeout";
                case TaskCanceledException { InnerException: TimeoutException }: return "error.timeout";
                case IOException io when io.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027): return "error.disk_full";
            }
        }
        if (exception is OperationCanceledException && message.Length == 0) return "error.canceled";
        var m = HttpStatus.Match(message);
        if (m.Success && !message.Contains("proxy", StringComparison.OrdinalIgnoreCase)) return HttpKey(int.Parse(m.Groups["code"].Value), out code);
        if (message.Contains("407") && message.Contains("proxy", StringComparison.OrdinalIgnoreCase) || message.Contains("proxy rejected the user name", StringComparison.OrdinalIgnoreCase)
            || message.Contains("proxy requires a user name", StringComparison.OrdinalIgnoreCase)) return "error.proxy_auth";
        if (Has(message, "not enough space on the disk", "disk is full", "No space left")) return "error.disk_full";
        if (Has(message, "Access to the path", "access is denied", "Access to the destination was denied")) return "error.access_denied";
        if (Has(message, "timed out", "timeout", "did not properly respond")) return "error.timeout";
        if (Has(message, "No such host is known", "Name or service not known", "nodename nor servname", "temporary failure in name resolution")) return "error.dns";
        if (Has(message, "certificate", "SSL connection could not be established", "TLS handshake")) return "error.certificate";
        if (Has(message, "was canceled", "was cancelled", "operation canceled")) return "error.canceled";
        return null;
    }

    private static bool Has(string message, params string[] needles) => needles.Any(n => message.Contains(n, StringComparison.OrdinalIgnoreCase));

    private static string HttpKey(int status, out object? code)
    {
        code = status;
        if (status is 401 or 403 or 404 or 408 or 410 or 429 or 500 or 502 or 503 or 504) return "error.http." + status;
        return "error.http.other";
    }
}
