using System.Net;

namespace NDM.Torrent;

/// <summary>The list of extra public trackers NeoDM adds to a magnet link while it looks for the torrent's metadata (1.11.0). Only udp, http and https
/// addresses without credentials are accepted, at most <see cref="MaxEntries"/> of them.</summary>
public static class TrackerList
{
    public const int MaxEntries = 20, MaxLength = 200;

    /// <summary>A small list of well-known open trackers. Used as the starting value of the setting; the user can edit it.</summary>
    public static readonly IReadOnlyList<string> Defaults =
    [
        "udp://tracker.opentrackr.org:1337/announce",
        "udp://open.stealth.si:80/announce",
        "udp://tracker.torrent.eu.org:451/announce",
        "udp://open.demonii.com:1337/announce",
        "udp://tracker.dler.org:6969/announce",
        "udp://tracker.theoks.net:6969/announce",
    ];

    /// <summary>True for a tracker address that is safe to announce to: scheme udp, http or https, a DNS name or a public IPv4 address, a port 1 to 65535
    /// (80 or 443 implied for http and https), no user name or password, no fragment, at most <see cref="MaxLength"/> characters, no spaces or control characters.</summary>
    public static bool IsValid(string? url, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(url)) return false;
        string text = url.Trim();
        if (text.Length > MaxLength || text.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c > 126)) return false;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("udp" or "http" or "https")) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || string.IsNullOrEmpty(uri.Host)) return false;
        if (uri.Scheme == "udp" && uri.IsDefaultPort) return false;                                           // a udp tracker address needs its port
        if (uri.Port is < 1 or > 65535) return false;
        if (uri.HostNameType is UriHostNameType.IPv6) return false;
        if (IPAddress.TryParse(uri.Host, out var ip) && !DhtBootstrap.IsPublicV4(ip)) return false;           // not the computer itself or the home network
        if (uri.HostNameType == UriHostNameType.Dns && (uri.Host == "localhost" || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) || !uri.Host.Contains('.'))) return false;
        normalized = uri.OriginalString;
        return true;
    }

    /// <summary>Splits text (one address per line, commas also allowed), drops blanks and repeats, and reports each refused line. At most <see cref="MaxEntries"/> are kept.</summary>
    public static (List<string> Valid, List<string> Rejected) Parse(string? text)
    {
        var valid = new List<string>(); var rejected = new List<string>();
        foreach (var raw in (text ?? "").Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IsValid(raw, out var url)) { rejected.Add(raw.Length > 60 ? raw[..60] + "…" : raw); continue; }
            if (valid.Contains(url, StringComparer.OrdinalIgnoreCase)) continue;
            if (valid.Count >= MaxEntries) { rejected.Add(raw.Length > 60 ? raw[..60] + "…" : raw); continue; }
            valid.Add(url);
        }
        return (valid, rejected);
    }

    /// <summary>The magnet's own trackers first, then the extra ones that are not already there, at most <paramref name="max"/> in total.</summary>
    public static List<string> Merge(IEnumerable<string> own, IEnumerable<string> extra, int max = 30)
    {
        var result = new List<string>();
        foreach (var url in own.Concat(extra))
            if (result.Count < max && !string.IsNullOrWhiteSpace(url) && !result.Contains(url, StringComparer.OrdinalIgnoreCase)) result.Add(url);
        return result;
    }
}
