using System.Text.RegularExpressions;

namespace NDM.Protocol;

/// <summary>Strict page-link handoff grammar, shared by the host and desktop.</summary>
public static class YoutubePageLink
{
    public static string Canonical(string url)
    {
        if (url.Length > 4096 || url.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '%' or '\\')) throw new ArgumentException("Invalid page link.");
        var m = Regex.Match(url, @"\Ahttps?://(youtube\.com|www\.youtube\.com|m\.youtube\.com|music\.youtube\.com|youtu\.be)(/[^?#]*)?(\?[^#]*)?(#[^\r\n]*)?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!m.Success) throw new ArgumentException("Invalid page link.");
        var path = m.Groups[2].Value;
        string? id = null;
        if (m.Groups[1].Value.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)) id = path.Length > 0 ? path[1..] : null;
        else if (path == "/watch")
        {
            var ids = m.Groups[3].Value.TrimStart('?').Split('&').Where(p => p.StartsWith("v=", StringComparison.Ordinal)).ToArray();
            if (ids.Length == 1) id = ids[0][2..];
        }
        else if (path.StartsWith("/shorts/", StringComparison.Ordinal)) id = path[8..];
        if (id is null || !Regex.IsMatch(id, @"\A[A-Za-z0-9_-]{11}\z")) throw new ArgumentException("Invalid page link.");
        return "https://www.youtube.com/watch?v=" + id;
    }
}
