using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NDM.Core;

/// <summary>1.13.0: how chosen subtitles are kept. Embed puts them into the video file; Separate saves one .srt file per language next to the video.</summary>
public enum SubtitleMode { Embed, Separate }

/// <summary>One ticked subtitle. Code is a YouTube language code (en, pt-BR) or, for HLS and DASH streams, the id of a subtitle track of the manifest. Auto = automatic captions.</summary>
public sealed record SubtitleChoice(string Code, bool Auto = false);

/// <summary>What the user asked for (stored in the job so a restart repeats it). At most <see cref="MaxChoices"/> languages.</summary>
public sealed record SubtitleRequest(SubtitleMode Mode, IReadOnlyList<SubtitleChoice> Choices)
{
    public const int MaxChoices = 40;
    public static bool SafeCode(string? code) => code is not null && Regex.IsMatch(code, @"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,39}\z");
    public bool IsValid => Choices is { Count: > 0 and <= MaxChoices } && Choices.All(c => SafeCode(c.Code)) && Enum.IsDefined(Mode);
}

/// <summary>A language the video offers captions in. Name is what the site calls it.</summary>
public sealed record SubtitleLanguage(string Code, string Name, bool Auto);

/// <summary>The caption languages of a video: uploaded by the creator first, then automatic ones. Never contains caption text.</summary>
public sealed record SubtitleListing(IReadOnlyList<SubtitleLanguage> Manual, IReadOnlyList<SubtitleLanguage> Automatic)
{
    public static readonly SubtitleListing Empty = new(Array.Empty<SubtitleLanguage>(), Array.Empty<SubtitleLanguage>());
    public bool Any => Manual.Count + Automatic.Count > 0;
}

/// <summary>A subtitle track a streaming manifest announces (HLS EXT-X-MEDIA TYPE=SUBTITLES, DASH text adaptation set). Uri is where its playlist or file is.</summary>
public sealed record SubtitleTrack(string Id, string? Language, string? Name, Uri? Uri, bool IsDefault = false)
{
    public string Display => !string.IsNullOrWhiteSpace(Name) ? Name! : !string.IsNullOrWhiteSpace(Language) ? Language! : Id;
}

/// <summary>A subtitle file that was fetched (path) for a language.</summary>
public sealed record SubtitleFile(string Path, string Language);

public static class SubtitleRules
{
    public const int MaxLanguages = 400;
    public const int MaxListingChars = 16 * 1024 * 1024;

    /// <summary>WebM cannot hold SRT text tracks, so embedding into a .webm file produces .mkv (the dialog says so). MP4 and MKV keep their extension. Audio files never get subtitles.</summary>
    public static string OutputExtension(string extension, SubtitleMode? mode)
    {
        if (mode != SubtitleMode.Embed) return extension;
        return extension.Equals("webm", StringComparison.OrdinalIgnoreCase) ? "mkv" : extension;
    }
    public static bool EmbedChangesContainer(string extension) => extension.Equals("webm", StringComparison.OrdinalIgnoreCase);
    public static bool CanCarry(string extension) => extension.ToLowerInvariant() is "mp4" or "mkv" or "webm";

    /// <summary>Reads the output of <c>--print "%(.{subtitles,automatic_captions})j"</c>: only the language codes and names are kept, never a caption address or text.</summary>
    public static SubtitleListing ParseListing(string text)
    {
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith('{'));
        if (line is null) return SubtitleListing.Empty;
        try
        {
            using var doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 12 });
            var manual = Read(doc.RootElement, "subtitles", false, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            var taken = manual.Select(m => m.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var auto = Read(doc.RootElement, "automatic_captions", true, taken);
            return new(manual, auto);
        }
        catch (JsonException) { return SubtitleListing.Empty; }
    }
    private static List<SubtitleLanguage> Read(JsonElement root, string key, bool auto, HashSet<string> skip)
    {
        var list = new List<SubtitleLanguage>();
        if (!root.TryGetProperty(key, out var map) || map.ValueKind != JsonValueKind.Object) return list;
        foreach (var p in map.EnumerateObject())
        {
            if (list.Count >= MaxLanguages) break;
            var code = p.Name;
            if (!SubtitleRequest.SafeCode(code) || code.Equals("live_chat", StringComparison.OrdinalIgnoreCase) || skip.Contains(code)) continue;
            string? name = null;
            if (p.Value.ValueKind == JsonValueKind.Array)
                foreach (var entry in p.Value.EnumerateArray())
                    if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } s) { name = s; break; }
            name = Clean(name) ?? LanguageName(code);
            list.Add(new(code, name, auto));
        }
        return list;
    }
    private static string? Clean(string? value)
    {
        if (value is null) return null;
        var clean = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length == 0 ? null : clean.Length > 80 ? clean[..80] : clean;
    }
    public static string LanguageName(string code)
    {
        try { var name = CultureInfo.GetCultureInfo(code).EnglishName; if (!string.IsNullOrWhiteSpace(name) && !name.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase)) return name; }
        catch (CultureNotFoundException) { }
        return code;
    }

    /// <summary>The code of the list that best fits a Windows language tag (en-US matches en, en-GB, en-orig); null when none does. Used to pre-highlight, never to tick.</summary>
    public static string? MatchWindowsLanguage(IEnumerable<string> codes, string windowsTag)
    {
        if (string.IsNullOrWhiteSpace(windowsTag)) return null;
        var list = codes.ToList();
        var exact = list.FirstOrDefault(c => c.Equals(windowsTag, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var primary = windowsTag.Split('-', '_')[0];
        return list.FirstOrDefault(c => c.Equals(primary, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(c => c.Split('-', '_')[0].Equals(primary, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Three letter code ffmpeg writes into the track (eng, por); the original text when no match.</summary>
    public static string LanguageTag(string code)
    {
        try { var iso = CultureInfo.GetCultureInfo(code.Split('-', '_')[0]).ThreeLetterISOLanguageName; if (iso.Length == 3 && iso.All(char.IsLetter)) return iso.ToLowerInvariant(); }
        catch (CultureNotFoundException) { }
        return new string(code.Where(char.IsLetterOrDigit).Take(3).ToArray()).ToLowerInvariant() is { Length: 3 } three ? three : "und";
    }

    /// <summary>yt-dlp arguments that fetch only captions (no media): one call per kind (creator-uploaded or automatic). Files land as media.&lt;code&gt;.srt in the working folder.
    /// 1.13.1: creator captions are asked for through the embedded TV player. When this bug was reproduced, the default player's caption addresses answered
    /// 429 Too Many Requests (so no caption file existed and nothing could be embedded), while the embedded TV player still handed them out. Automatic captions
    /// keep the default player; when YouTube refuses them the job reports the failure instead of claiming success.</summary>
    public static List<string> FetchArguments(string url, string runtimePath, string folder, IEnumerable<string> codes, bool auto, string ffmpegPath)
    {
        var args = YoutubeService.BaseArguments(url, runtimePath);
        var prefix = new List<string> { "--skip-download", "--no-simulate", "--no-overwrites", "--no-mtime", auto ? "--write-auto-subs" : "--write-subs", "--sub-langs", string.Join(",", codes),
            "--sub-format", "vtt/best", "--convert-subs", "srt", "--paths", folder, "--output", "media.%(ext)s", "--no-write-info-json", "--no-write-thumbnail", "--ffmpeg-location", ffmpegPath };
        if (!auto) prefix.AddRange(new[] { "--extractor-args", "youtube:player_client=tv_embedded" });
        args.InsertRange(0, prefix);
        return args;
    }

    /// <summary>ffmpeg arguments that copy the video and add each subtitle file as a text track. MP4 keeps mov_text tracks, MKV keeps SRT.</summary>
    public static List<string> EmbedArguments(string video, IReadOnlyList<SubtitleFile> subtitles, string output, string extension)
    {
        var args = new List<string> { "-y", "-hide_banner", "-nostdin", "-loglevel", "error", "-i", video };
        foreach (var s in subtitles) { args.Add("-i"); args.Add(s.Path); }
        args.AddRange(new[] { "-map", "0" });
        for (int i = 0; i < subtitles.Count; i++) { args.Add("-map"); args.Add((i + 1).ToString(CultureInfo.InvariantCulture) + ":0"); }
        args.AddRange(new[] { "-c", "copy", "-c:s", extension.Equals("mp4", StringComparison.OrdinalIgnoreCase) ? "mov_text" : "srt" });
        for (int i = 0; i < subtitles.Count; i++)
        {
            // 1.13.1: the title tag makes players list "English" instead of "und" or nothing; the language tag is what they group and preselect by.
            args.Add("-metadata:s:s:" + i.ToString(CultureInfo.InvariantCulture)); args.Add("language=" + LanguageTag(subtitles[i].Language));
            args.Add("-metadata:s:s:" + i.ToString(CultureInfo.InvariantCulture)); args.Add("title=" + LanguageName(subtitles[i].Language));
        }
        args.Add(output);
        return args;
    }

    /// <summary>ffprobe arguments that list only the subtitle streams of a file, one index per line (1.13.1, used to verify an embed really added a track).</summary>
    public static List<string> ProbeArguments(string file) => new() { "-v", "error", "-select_streams", "s", "-show_entries", "stream=index", "-of", "csv=p=0", file };

    /// <summary>How many subtitle streams the <see cref="ProbeArguments"/> output names (one numeric line per stream).</summary>
    public static int CountProbedStreams(string output) =>
        (output ?? "").Split('\n').Count(l => l.Trim() is { Length: > 0 } t && t.All(char.IsDigit));

    /// <summary>File name of a separate subtitle next to a video: "My video.en.srt".</summary>
    public static string SeparateName(string videoFileName, string language)
    {
        var safe = new string(language.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').ToArray());
        return Path.GetFileNameWithoutExtension(videoFileName) + "." + (safe.Length == 0 ? "und" : safe) + ".srt";
    }

    /// <summary>Plain WebVTT to SubRip. Tags are removed, times get a comma, cues are numbered. Used for subtitles that come from a stream manifest.</summary>
    public static string VttToSrt(string vtt)
    {
        var sb = new StringBuilder(); int number = 0;
        var blocks = Regex.Split(vtt.Replace("\r\n", "\n").Replace('\r', '\n').TrimStart('\uFEFF'), @"\n{2,}");
        foreach (var block in blocks)
        {
            var lines = block.Split('\n');
            int at = Array.FindIndex(lines, l => l.Contains("-->", StringComparison.Ordinal));
            if (at < 0 || lines[0].StartsWith("NOTE", StringComparison.Ordinal) || lines[0].StartsWith("STYLE", StringComparison.Ordinal) || lines[0].StartsWith("REGION", StringComparison.Ordinal)) continue;
            var times = lines[at].Split(new[] { "-->" }, StringSplitOptions.None);
            if (times.Length != 2) continue;
            var start = Time(times[0].Trim()); var end = Time(times[1].Trim().Split(' ', '\t')[0]);
            if (start is null || end is null) continue;
            var text = string.Join("\n", lines.Skip(at + 1).Select(StripTags).Where(l => l.Length > 0));
            if (text.Length == 0) continue;
            sb.Append(++number).Append('\n').Append(start).Append(" --> ").Append(end).Append('\n').Append(text).Append("\n\n");
        }
        return sb.ToString();
    }
    private static string? Time(string value)
    {
        var m = Regex.Match(value, @"\A(?:(\d{1,3}):)?(\d{1,2}):(\d{2})[.,](\d{1,3})\z");
        if (!m.Success) return null;
        int h = m.Groups[1].Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        var ms = m.Groups[4].Value.PadRight(3, '0');
        return string.Create(CultureInfo.InvariantCulture, $"{h:00}:{int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture):00}:{m.Groups[3].Value},{ms}");
    }
    private static string StripTags(string line)
    {
        var text = Regex.Replace(line, "<[^>]*>", "");
        return text.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&nbsp;", " ").Replace("&lrm;", "").Replace("&rlm;", "").Trim();
    }

    /// <summary>Sticks the pieces of a WebVTT playlist together: the first header only, then every cue block. Pieces are plain text.</summary>
    public static string JoinVtt(IEnumerable<string> pieces)
    {
        var sb = new StringBuilder("WEBVTT\n\n");
        foreach (var piece in pieces)
        {
            var text = piece.Replace("\r\n", "\n").TrimStart('\uFEFF');
            var at = text.IndexOf("\n\n", StringComparison.Ordinal);
            var body = text.StartsWith("WEBVTT", StringComparison.Ordinal) ? (at < 0 ? "" : text[(at + 2)..]) : text;
            sb.Append(body.Trim('\n')).Append("\n\n");
        }
        return sb.ToString();
    }
}
