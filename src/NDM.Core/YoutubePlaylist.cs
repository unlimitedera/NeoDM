using System.Globalization;
using System.Text.Json;

namespace NDM.Core;

/// <summary>Why a playlist entry cannot be downloaded (listed, shown, disabled).</summary>
public enum YoutubeEntryState { Available, Private, Deleted, Unavailable, Members, Live }

public sealed record YoutubePlaylistEntry(string Id, string Title, int DurationSeconds, string Channel, string ThumbnailUrl, YoutubeEntryState State)
{
    public bool Available => State == YoutubeEntryState.Available;
    public string Url => "https://www.youtube.com/watch?v=" + Id;
}

/// <summary>Truncated: the list had more than <see cref="YoutubePlaylists.MaxEntries"/> entries and only the first ones are kept. Total is the count YouTube reports (0 when unknown). Skipped counts malformed lines.</summary>
public sealed record YoutubePlaylist(string Title, IReadOnlyList<YoutubePlaylistEntry> Entries, int Total, bool Truncated, int Skipped = 0);

/// <summary>Playlist listing (1.11.0): one bounded <c>--flat-playlist --print</c> pass, no format loading.</summary>
public static class YoutubePlaylists
{
    public const int MaxEntries = 500;
    public const int MaxLineChars = 32 * 1024;
    public const int MaxListChars = 4 * 1024 * 1024;
    public const string EntryTemplate = "%(.{id,title,duration,channel,uploader,availability,live_status,thumbnails,playlist_title,playlist_count})j";
    private static readonly HashSet<string> ThumbnailHosts = new(StringComparer.OrdinalIgnoreCase) { "i.ytimg.com", "i9.ytimg.com", "img.youtube.com" };

    /// <summary>Strict allow-list for thumbnail addresses: https, default port, no credentials, a YouTube image host.</summary>
    public static bool IsThumbnailUrl(string? url) =>
        url is { Length: > 0 and <= 1024 } && !url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) && Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort && u.UserInfo.Length == 0 && ThumbnailHosts.Contains(u.IdnHost);
    public static string StandardThumbnail(string id) => "https://i.ytimg.com/vi/" + id + "/mqdefault.jpg";

    public static IReadOnlyList<string> ListArguments(string playlistUrl, string runtimePath) => new[]
    {
        "--ignore-config", "--no-plugin-dirs", "--no-remote-components", "--no-js-runtimes", "--js-runtimes", "deno:" + runtimePath,
        "--no-update", "--no-cache-dir", "--yes-playlist", "--use-extractors", "youtube.*", "--socket-timeout", "30", "--retries", "2", "--no-colors", "--encoding", "utf-8",
        "--flat-playlist", "--skip-download", "--playlist-end", (MaxEntries + 1).ToString(CultureInfo.InvariantCulture), "--quiet", "--verbose", "--print", EntryTemplate, "--", playlistUrl
    };

    /// <summary>Parses the JSON lines printed by the helper. Malformed or oversized lines are skipped and counted; entries repeat nothing (duplicate ids dropped); more than 500 entries keep the first 500 and set Truncated.</summary>
    public static YoutubePlaylist Parse(string text, string fallbackTitle = "YouTube playlist")
    {
        if (text.Length > MaxListChars) throw new YoutubeException("youtube.metadata");
        var entries = new List<YoutubePlaylistEntry>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        string title = ""; int total = 0, skipped = 0; bool truncated = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.Length > MaxLineChars || line[0] != '{') { skipped++; continue; }
            try
            {
                using var doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 8 }); var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) { skipped++; continue; }
                var id = Str(root, "id");
                if (!YoutubeLinks.IsVideoId(id)) { skipped++; continue; }
                if (title.Length == 0 && Str(root, "playlist_title") is { Length: > 0 } pt) title = Clean(pt, 150);
                if (total == 0 && root.TryGetProperty("playlist_count", out var pc) && pc.ValueKind == JsonValueKind.Number && pc.TryGetInt32(out var n) && n > 0) total = Math.Min(n, 1_000_000);
                if (!seen.Add(id!)) continue;
                if (entries.Count >= MaxEntries) { truncated = true; continue; }
                var name = Clean(Str(root, "title") ?? "", 200);
                var state = StateOf(name, Str(root, "availability"), Str(root, "live_status"));
                if (name.Length == 0) name = id!;
                var duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out var seconds) && double.IsFinite(seconds) ? (int)Math.Clamp(seconds, 0, 7 * 24 * 3600) : 0;
                var channel = Clean(Str(root, "channel") ?? Str(root, "uploader") ?? "", 100);
                entries.Add(new(id!, name, duration, channel, PickThumbnail(root, id!), state));
            }
            catch (JsonException) { skipped++; }
        }
        if (entries.Count == 0 && skipped == 0) throw new YoutubeException("youtube.playlist_empty");
        return new(title.Length == 0 ? fallbackTitle : title, entries, Math.Max(total, entries.Count), truncated, skipped);
    }

    private static YoutubeEntryState StateOf(string title, string? availability, string? live)
    {
        if (title.StartsWith("[Private video]", StringComparison.OrdinalIgnoreCase)) return YoutubeEntryState.Private;
        if (title.StartsWith("[Deleted video]", StringComparison.OrdinalIgnoreCase)) return YoutubeEntryState.Deleted;
        if (live is "is_live" or "is_upcoming" or "was_live" or "post_live") return YoutubeEntryState.Live;
        return availability switch
        {
            "private" or "needs_auth" => YoutubeEntryState.Private,
            "subscriber_only" or "premium_only" => YoutubeEntryState.Members,
            "unlisted" or "public" or null or "" => title.StartsWith("[", StringComparison.Ordinal) && title.EndsWith("video]", StringComparison.OrdinalIgnoreCase) ? YoutubeEntryState.Unavailable : YoutubeEntryState.Available,
            _ => YoutubeEntryState.Unavailable
        };
    }

    /// <summary>The largest listed thumbnail from an allowed host, else the standard image address of the video.</summary>
    private static string PickThumbnail(JsonElement root, string id)
    {
        if (root.TryGetProperty("thumbnails", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            string? best = null; long bestArea = -1;
            foreach (var t in list.EnumerateArray().Take(16))
            {
                var url = Str(t, "url"); if (!IsThumbnailUrl(url)) continue;
                long w = t.TryGetProperty("width", out var wv) && wv.TryGetInt32(out var wi) ? wi : 0, h = t.TryGetProperty("height", out var hv) && hv.TryGetInt32(out var hi) ? hi : 0;
                if (w * h > bestArea && w <= 640) { bestArea = w * h; best = url; }
            }
            if (best is not null) return best;
        }
        return StandardThumbnail(id);
    }

    private static string? Str(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    /// <summary>Display text: control and format characters removed, whitespace collapsed, bounded.</summary>
    public static string Clean(string value, int max)
    {
        var chars = value.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray();
        var text = string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return text.Length > max ? text[..max] : text;
    }

    /// <summary>The package sub-folder: the playlist title made safe, below the base folder; an existing non-empty folder gets " (2)", " (3)" and so on.</summary>
    public static string ResolveFolder(string baseFolder, string playlistTitle, Func<string, bool>? nonEmptyExists = null)
    {
        nonEmptyExists ??= path => Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();
        var name = DownloadFileNames.Sanitize(Clean(playlistTitle, 100));
        if (name == "download.bin") name = "YouTube playlist";
        var root = Path.GetFullPath(baseFolder);
        for (int n = 1; n < 1000; n++)
        {
            var candidate = Path.GetFullPath(Path.Combine(root, n == 1 ? name : $"{name} ({n})"));
            if (!candidate.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Unsafe playlist folder.");
            if (!nonEmptyExists(candidate)) return candidate;
        }
        throw new IOException("Unable to choose a playlist folder.");
    }

    /// <summary>"01 - Title" style prefix with the width of the largest number.</summary>
    public static string Numbered(int index, int count, string title) => index.ToString("D" + Math.Max(2, count.ToString(CultureInfo.InvariantCulture).Length), CultureInfo.InvariantCulture) + " - " + title;
}

/// <summary>A quality choice that is resolved per video when its job starts (1.11.0). Quality: "best", "2160", "1440", "1080", "720", "480", "360" or "audio"; AudioKbps: 0 = best, else 256, 192, 128, 64.</summary>
public sealed record YoutubePreset(string Quality = "best", int AudioKbps = 0, string Container = "")
{
    public static readonly string[] Qualities = { "best", "2160", "1440", "1080", "720", "480", "360", "audio" };
    public static readonly int[] AudioRates = { 0, 256, 192, 128, 64 };
    public bool AudioOnly => Quality == "audio";
    public int Height => int.TryParse(Quality, NumberStyles.None, CultureInfo.InvariantCulture, out var h) ? h : 0;
    public bool IsValid => Qualities.Contains(Quality) && AudioRates.Contains(AudioKbps) && (Container.Length == 0 || YoutubeFormatPicker.Containers.Contains(Container));
}

public static class YoutubePresets
{
    /// <summary>Turns a preset into a concrete selection against the real format list: the video height closest to the wish (the best at or below it, else the smallest above), the audio rate closest to the wish, MP4 preferred over other containers at equal height.</summary>
    public static YoutubeSelection Resolve(YoutubeSelection selection, YoutubeMetadata metadata)
    {
        if (selection.Preset is not { } preset) return selection;
        var audio = metadata.Formats.Where(f => f.Audio && !f.Video).ToArray();
        if (preset.AudioOnly)
        {
            var pick = ClosestAudio(audio, preset.AudioKbps) ?? metadata.Formats.Where(f => f.Audio).OrderByDescending(f => f.Bitrate).FirstOrDefault() ?? throw new YoutubeException("youtube.format");
            return new YoutubeSelection(pick.Id, null, selection.Mp3, null, null, false, null);
        }
        var video = metadata.Formats.Where(f => f.Video).ToArray();
        string? container = preset.Container.Length == 0 ? null : preset.Container;
        if (container is not null)
        {
            // Only pictures the container can hold with some sound that fits; when nothing fits the choice falls back to MKV, which holds everything.
            bool Fits(string c, YoutubeFormat f) => f.Audio ? YoutubeFormatPicker.CanHold(c, f, null) : audio.Any(a => YoutubeFormatPicker.CanHold(c, f, a));
            if (!video.Any(f => Fits(container, f))) container = "mkv";
            video = video.Where(f => Fits(container, f)).ToArray();
            audio = audio.Where(a => video.Any(f => f.Audio || YoutubeFormatPicker.CanHold(container, f, a))).ToArray();
        }
        var heights = video.Select(f => f.Height).Distinct().OrderBy(h => h).ToArray();
        int target = preset.Quality == "best" ? heights[^1] : heights.Where(h => h <= preset.Height).DefaultIfEmpty(heights[0]).Max();
        var chosen = video.Where(f => f.Height == target).OrderByDescending(f => f.Audio).ThenByDescending(f => f.Extension == "mp4").ThenByDescending(f => f.Size).First();
        if (chosen.Audio) return new YoutubeSelection(chosen.Id, null, false, null, container, false, selection.Subtitles);
        var fitting = container is null ? audio : audio.Where(a => YoutubeFormatPicker.CanHold(container, chosen, a)).ToArray();
        var sound = ClosestAudio(fitting, preset.AudioKbps) ?? throw new YoutubeException("youtube.format");
        return new YoutubeSelection(chosen.Id, sound.Id, false, null, container, false, selection.Subtitles);
    }
    private static YoutubeFormat? ClosestAudio(YoutubeFormat[] audio, int kbps)
    {
        if (audio.Length == 0) return null;
        if (kbps <= 0) return audio.OrderByDescending(f => f.Bitrate).ThenByDescending(f => f.Extension == "m4a").First();
        var below = audio.Where(f => f.Bitrate <= kbps).ToArray();
        return below.Length > 0 ? below.OrderByDescending(f => f.Bitrate).ThenByDescending(f => f.Extension == "m4a").First() : audio.OrderBy(f => f.Bitrate).First();
    }
}
