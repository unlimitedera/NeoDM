using System.Text.RegularExpressions;

namespace NDM.Core;

public enum YoutubeLinkKind { None, Video, Playlist, VideoInPlaylist }

/// <summary>What a piece of text says about YouTube (1.11.0). Used by Add URL, paste, drop and the link grabber to open the right dialog.
/// Playlist ids are validated strictly: only real playlist prefixes pass; endless auto-generated mixes (RD...) and account lists (LL, WL) are refused with their own reason.</summary>
public sealed record YoutubeLink(YoutubeLinkKind Kind, string? VideoId, string? ListId, string? RefusedReason = null)
{
    public string? VideoUrl => VideoId is null ? null : "https://www.youtube.com/watch?v=" + VideoId;
    public string? PlaylistUrl => ListId is null ? null : "https://www.youtube.com/playlist?list=" + ListId;
}

public static class YoutubeLinks
{
    public const string RefusedMix = "youtube.playlist_mix";
    public const string RefusedPrivate = "youtube.playlist_private";
    public const string RefusedUnknown = "youtube.playlist_invalid";

    private static readonly Regex VideoIdPattern = new(@"\A[A-Za-z0-9_-]{11}\z", RegexOptions.CultureInvariant);
    private static readonly Regex PlaylistPattern = new(@"\A(?:PL[A-Za-z0-9_-]{16,64}|UU[A-Za-z0-9_-]{22}|OLAK5uy_[A-Za-z0-9_-]{30,40})\z", RegexOptions.CultureInvariant);

    /// <summary>Null when the id is a real, finite playlist id; otherwise the reason code (mix, private account list, unknown).</summary>
    public static string? RefusalFor(string? id)
    {
        if (id is null || id.Length is 0 or > 80 || !Regex.IsMatch(id, @"\A[A-Za-z0-9_-]+\z")) return RefusedUnknown;
        if (id.StartsWith("RD", StringComparison.Ordinal)) return RefusedMix;
        if (id is "LL" or "WL" || id.StartsWith("LL", StringComparison.Ordinal) && id.Length < 8) return RefusedPrivate;
        return PlaylistPattern.IsMatch(id) ? null : RefusedUnknown;
    }
    public static bool IsPlaylistId(string? id) => RefusalFor(id) is null;
    public static bool IsVideoId(string? id) => id is not null && VideoIdPattern.IsMatch(id);

    /// <summary>Removes the punctuation that sentences put after a pasted link, plus wrapping brackets and quotes.</summary>
    public static string TrimLink(string? text)
    {
        var value = (text ?? "").Trim();
        while (value.Length > 0 && ".,;:!?)]}>\"'»”’".Contains(value[^1])) value = value[..^1];
        while (value.Length > 0 && "([{<\"'«“‘".Contains(value[0])) value = value[1..];
        return value;
    }

    /// <summary>Parses one pasted link. Never throws; Kind None means it is not a YouTube video or playlist address (a refused playlist has Kind None and RefusedReason set).</summary>
    public static YoutubeLink Parse(string? text)
    {
        var none = new YoutubeLink(YoutubeLinkKind.None, null, null);
        var value = TrimLink(text);
        if (value.Length is 0 or > 4096 || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return none;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !uri.IsDefaultPort || uri.UserInfo.Length != 0) return none;
        var host = uri.IdnHost.ToLowerInvariant();
        string? video = null, list = null;
        if (host == "youtu.be")
        {
            var id = uri.AbsolutePath.Trim('/');
            if (IsVideoId(id)) video = id;
            list = Query(uri, "list");
        }
        else if (host is "youtube.com" or "www.youtube.com" or "m.youtube.com" or "music.youtube.com")
        {
            var path = uri.AbsolutePath;
            if (path.Equals("/watch", StringComparison.OrdinalIgnoreCase)) { var v = Query(uri, "v"); if (IsVideoId(v)) video = v; list = Query(uri, "list"); }
            else if (path.Equals("/playlist", StringComparison.OrdinalIgnoreCase)) list = Query(uri, "list");
            else if (path.StartsWith("/shorts/", StringComparison.OrdinalIgnoreCase) && IsVideoId(path[8..].TrimEnd('/'))) video = path[8..].TrimEnd('/');
            else if (path.StartsWith("/embed/", StringComparison.OrdinalIgnoreCase) && IsVideoId(path[7..].TrimEnd('/'))) video = path[7..].TrimEnd('/');
        }
        else return none;
        string? refusal = null;
        if (list is not null)
        {
            refusal = RefusalFor(list);
            if (refusal is not null) { list = null; if (video is null) return new YoutubeLink(YoutubeLinkKind.None, null, null, refusal); }
        }
        if (video is not null && list is not null) return new(YoutubeLinkKind.VideoInPlaylist, video, list);
        if (video is not null) return new(YoutubeLinkKind.Video, video, null, refusal);
        if (list is not null) return new(YoutubeLinkKind.Playlist, null, list);
        return none;
    }

    /// <summary>True when the text looks like a YouTube video or playlist address (also the ones that are refused with a reason).</summary>
    public static bool Looks(string? text) { var link = Parse(text); return link.Kind != YoutubeLinkKind.None || link.RefusedReason is not null; }

    private static string? Query(Uri uri, string key)
    {
        string? found = null; int count = 0;
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0 || !part[..eq].Equals(key, StringComparison.Ordinal)) continue;
            count++; found = part[(eq + 1)..];
        }
        return count == 1 ? found : null;
    }
}
