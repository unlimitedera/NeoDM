namespace NDM.Core;

/// <summary>What the browser said the download is (size, content type, name). Used to refuse a web page that a site sends instead of the file
/// (an "expired link" or "sign in" page answered with status 200).</summary>
public sealed record ExpectedContent(long? Size, string? Mime, string? FileName)
{
    private static readonly HashSet<string> HtmlExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".html", ".htm", ".xhtml", ".shtml", ".php", ".asp", ".aspx", ".jsp", ".cfm", ".cgi" };

    public static bool IsHtmlMime(string? mime)
    {
        var type = mime?.Split(';')[0].Trim();
        return string.Equals(type, "text/html", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsHtmlName(string? name)
    {
        var extension = string.IsNullOrEmpty(name) ? "" : Path.GetExtension(name.Trim());
        return HtmlExtensions.Contains(extension);
    }

    /// <summary>True when the request is a file that is not a web page: the browser size is known, or the browser type or the name extension says so.</summary>
    public bool ExpectsFile
    {
        get
        {
            if (IsHtmlMime(Mime) || IsHtmlName(FileName)) return false;
            if (Size is > 0) return true;
            if (!string.IsNullOrWhiteSpace(Mime)) return true;
            return !string.IsNullOrEmpty(FileName) && Path.GetExtension(FileName.Trim()).Length > 1;
        }
    }
}

/// <summary>Thrown when the server answered with something that is not the file. Never retried; the job fails with the error code <see cref="ContentGuard.WrongContent"/>.</summary>
public sealed class WrongContentException : Exception
{
    public long? ReceivedBytes { get; }
    public long? ExpectedBytes { get; }
    public WrongContentException(string message, long? received, long? expected) : base(message) { ReceivedBytes = received; ExpectedBytes = expected; }
}

public static class ContentGuard
{
    /// <summary>Error category stored on a failed job (<see cref="DownloadJob.ErrorCode"/>).</summary>
    public const string WrongContent = "wrong-content";

    /// <summary>The server's declared size is below this share of the size the browser reported: it is not that file.</summary>
    public const double MinimumShare = 0.5;

    /// <summary>True when the declared length is smaller than half of the expected size.</summary>
    public static bool SizeMismatch(long? expectedSize, long? declared) =>
        expectedSize is > 0 && declared is >= 0 && declared < expectedSize * MinimumShare;

    /// <summary>True when the first bytes look like an HTML document (doctype, html, head or body tag after an optional BOM and white space).</summary>
    public static bool LooksLikeHtml(ReadOnlySpan<byte> head)
    {
        int i = 0;
        if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) i = 3;
        while (i < head.Length && (head[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')) i++;
        var text = System.Text.Encoding.ASCII.GetString(head[i..Math.Min(head.Length, i + 32)]).ToLowerInvariant();
        return text.StartsWith("<!doctype html") || text.StartsWith("<html") || text.StartsWith("<head") || text.StartsWith("<body") || text.StartsWith("<!doctype") && text.Contains("html");
    }

    /// <summary>The user-facing sentence (English; the app shows its localized version built from the same numbers).</summary>
    public static string Message(long? received, long? expected)
    {
        var sizes = received is not null && expected is > 0 ? $" (about {Size(received.Value)} instead of {Size(expected.Value)})"
            : received is not null ? $" (about {Size(received.Value)})" : expected is > 0 ? $" (expected about {Size(expected.Value)})" : "";
        return $"The site sent a web page instead of the file{sizes}. The link may have expired or may only work in the browser.";
    }

    /// <summary>Decides from the response headers; null = acceptable. contentDispositionName is the file name the server itself announced (a server that
    /// announces an .html name really serves a page).</summary>
    public static WrongContentException? CheckHeaders(ExpectedContent expected, string? contentType, long? declaredLength, string? contentDispositionName)
    {
        if (!expected.ExpectsFile) return null;
        if (ExpectedContent.IsHtmlName(contentDispositionName)) return null;
        if (ExpectedContent.IsHtmlMime(contentType)) return new WrongContentException(Message(declaredLength, expected.Size), declaredLength, expected.Size);
        if (SizeMismatch(expected.Size, declaredLength)) return new WrongContentException(Message(declaredLength, expected.Size), declaredLength, expected.Size);
        return null;
    }

    /// <summary>Decides from the first bytes of the body; null = acceptable.</summary>
    public static WrongContentException? CheckHead(ExpectedContent expected, ReadOnlySpan<byte> head, long? knownLength, string? contentDispositionName)
    {
        if (!expected.ExpectsFile || ExpectedContent.IsHtmlName(contentDispositionName)) return null;
        return LooksLikeHtml(head) ? new WrongContentException(Message(knownLength, expected.Size), knownLength, expected.Size) : null;
    }

    private static string Size(long bytes) => bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " GB"
        : bytes >= 1L << 20 ? (bytes / (double)(1L << 20)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " MB"
        : Math.Max(1, (bytes + 1023) >> 10) + " KB";
}
