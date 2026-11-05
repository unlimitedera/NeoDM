using NDM.Core;

namespace NDM.App;

public enum YoutubeAction { NotYoutube, OpenVideo, OpenPlaylist, AskVideoOrPlaylist, Refused }

/// <summary>What to do with one pasted, dropped or typed link (1.11.0). Pure decision, no windows: Add URL, paste, drop, the link grabber and the bulk import all use it.</summary>
public sealed record YoutubePlan(YoutubeAction Action, YoutubeLink Link, string? Message = null, string Url = "")
{
    /// <summary>The address the video dialog should read.</summary>
    public string? VideoUrl => Link.VideoUrl;
    public string? PlaylistUrl => Link.PlaylistUrl;
}

public static class YoutubeRouting
{
    public static YoutubePlan Plan(string? text)
    {
        var link = YoutubeLinks.Parse(text); var url = YoutubeLinks.TrimLink(text);
        return link.Kind switch
        {
            YoutubeLinkKind.Video => new(YoutubeAction.OpenVideo, link, null, url),
            YoutubeLinkKind.Playlist => new(YoutubeAction.OpenPlaylist, link, null, url),
            YoutubeLinkKind.VideoInPlaylist => new(YoutubeAction.AskVideoOrPlaylist, link, null, url),
            _ when link.RefusedReason is { } reason => new(YoutubeAction.Refused, link, Localization.Loc.T(reason), url),
            _ => new(YoutubeAction.NotYoutube, link, null, url)
        };
    }

    /// <summary>The inline note shown under the address box as soon as the text looks like a YouTube link; null for any other text.</summary>
    public static (string Text, bool IsProblem)? Note(string? text)
    {
        var plan = Plan(text);
        return plan.Action switch
        {
            YoutubeAction.OpenVideo => (Localization.Loc.T("add.youtube_note"), false),
            YoutubeAction.OpenPlaylist => (Localization.Loc.T("add.youtube_note_playlist"), false),
            YoutubeAction.AskVideoOrPlaylist => (Localization.Loc.T("add.youtube_note_both"), false),
            YoutubeAction.Refused => (plan.Message ?? "", true),
            _ => null
        };
    }

    /// <summary>If the whole text is one single link (one non-empty line) that opens a YouTube dialog, its plan; otherwise null (the link grabber handles lists).</summary>
    public static YoutubePlan? SingleLink(string? text)
    {
        var lines = (text ?? "").Split('\n', '\r').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        if (lines.Length != 1 || lines[0].Contains(' ')) return null;
        var plan = Plan(lines[0]);
        return plan.Action is YoutubeAction.NotYoutube ? null : plan;
    }
}
