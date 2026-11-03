using System.IO;
using System.Text;
using NDM.Core;

namespace NDM.App;

/// <summary>1.9.0 link grabber, step one: finds web links in any text. Only http and https links are kept. Bounded: at most <see cref="MaxLinks"/> links, each at most <see cref="MaxUrlLength"/> characters, and only the first <see cref="MaxInputCharacters"/> characters of the text are read.
/// Trailing punctuation is dropped; a closing parenthesis or bracket stays when the link itself has the opening one (so "(see http://a/b_(c))" keeps "b_(c)"); a bracketed IPv6 host is kept. Links that differ only by a #fragment count as the same link.</summary>
public static class LinkParser
{
    public const int MaxLinks = 5000, MaxUrlLength = 8192, MaxInputCharacters = 8_000_000;

    public sealed record Link(string Url, string Host, int Line);
    public sealed record Result(IReadOnlyList<Link> Links, int Duplicates, int Ignored, int TooLong, bool Truncated);

    private static readonly System.Text.RegularExpressions.Regex Scheme = new(@"https?://", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex OtherScheme = new(@"(?<![A-Za-z0-9+.\-])(?:ftp|ftps|sftp|file|magnet|javascript|data|mailto|ws|wss)\:", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool Ends(char c) => char.IsWhiteSpace(c) || char.IsControl(c) || c is '<' or '>' or '"' or '`' or '|' or '\\' or '^' || ReservedNames.IsInvisible(c);

    public static Result Parse(string? text, int maxLinks = MaxLinks, int maxLength = MaxUrlLength)
    {
        var links = new List<Link>();
        if (string.IsNullOrEmpty(text)) return new(links, 0, 0, 0, false);
        if (text.Length > MaxInputCharacters) text = text[..MaxInputCharacters];
        int duplicates = 0, ignored = OtherScheme.Matches(text).Count, tooLong = 0; bool truncated = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var starts = Scheme.Matches(text).Select(m => m.Index).ToArray();
        // line numbers: count newlines lazily as the scan moves forward
        int line = 1, linePos = 0, consumed = 0;
        for (int k = 0; k < starts.Length; k++)
        {
            int s = starts[k];
            if (s < consumed) continue; // inside the link before it (for example in its query)
            for (; linePos < s; linePos++) if (text[linePos] == '\n') line++;
            if (s > 0 && (char.IsLetterOrDigit(text[s - 1]) || text[s - 1] is '+' or '-' or '.')) { ignored++; continue; } // "ftp+http://" and similar
            int e = s;
            while (e < text.Length && !Ends(text[e])) e++;
            // a second link glued on with a comma or semicolon
            for (int j = k + 1; j < starts.Length && starts[j] < e; j++)
                if (text[starts[j] - 1] is ',' or ';') { e = starts[j] - 1; break; }
            consumed = e;
            var token = Trim(text.AsSpan(s, e - s));
            if (token.Length > maxLength) { tooLong++; continue; }
            if (!Uri.TryCreate(token, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length > 0 || uri.Host.Length > 253) { ignored++; continue; }
            var key = uri.GetLeftPart(UriPartial.Path) + uri.Query;
            if (!seen.Add(key)) { duplicates++; continue; }
            if (links.Count >= maxLinks) { truncated = true; break; }
            links.Add(new Link(key, uri.IdnHost, line));
        }
        return new(links, duplicates, ignored, tooLong, truncated);
    }

    /// <summary>Drops trailing punctuation and unbalanced closing brackets.</summary>
    private static string Trim(ReadOnlySpan<char> span)
    {
        int end = span.Length;
        while (end > 0)
        {
            char c = span[end - 1];
            if (c is '.' or ',' or ';' or ':' or '!' or '?' or '\'' or '*' or '"' or '>' or '\u2019' or '\u201D' or '\u00BB') { end--; continue; }
            if (c is ')' or ']' or '}')
            {
                char open = c == ')' ? '(' : c == ']' ? '[' : '{';
                int opens = 0, closes = 0;
                for (int i = 0; i < end; i++) { if (span[i] == open) opens++; else if (span[i] == c) closes++; }
                if (closes > opens) { end--; continue; }
            }
            break;
        }
        return span[..end].ToString();
    }

    /// <summary>File name shown for a link before anything is checked: the last path segment, decoded; never empty.</summary>
    public static string GuessName(string url)
    {
        try { return DownloadFileNames.Sanitize(FileNameResolver.Resolve(new Uri(url), null, null, null).Name); }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException) { return "download.bin"; }
    }

    /// <summary>Splits a text file (.txt, .csv, .m3u, .json ...) into its text for <see cref="Parse"/>; reading is capped.</summary>
    public static string ReadListFile(string path, int maxBytes = 4 * 1024 * 1024)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[(int)Math.Min(maxBytes, stream.Length)];
        int read = 0; while (read < buffer.Length) { int n = stream.Read(buffer, read, buffer.Length - read); if (n == 0) break; read += n; }
        return new UTF8Encoding(false).GetString(buffer, 0, read);
    }

    public static readonly string[] ListExtensions = { ".txt", ".csv", ".m3u", ".m3u8", ".json", ".lst", ".list", ".url", ".html", ".htm", ".md", ".log" };
}
