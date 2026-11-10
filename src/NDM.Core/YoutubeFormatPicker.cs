using System.Globalization;

namespace NDM.Core;

public enum PickerType { VideoAudio, VideoOnly, AudioOnly }

/// <summary>One line of the short resolution list. Height 0 is "Best". Detail data (codec, frame rate, estimated size) belongs to the format that would be used.</summary>
public sealed record PickerResolution(int Height, string? Codec, double Fps, long Size, bool SizeEstimated)
{
    public bool IsBest => Height == 0;
}

/// <summary>One line of the short audio quality list. Id null is "Best".</summary>
public sealed record PickerAudio(string? Id, int Bitrate, string Extension, string? Codec, long Size)
{
    public bool IsBest => Id is null;
}

/// <summary>What the picker would download right now. Selection is ready for a job; Extension is the file type that comes out.</summary>
public sealed record PickerResult(YoutubeSelection Selection, string Extension, YoutubeFormat Primary, YoutubeFormat? Audio, int Height, int AudioBitrate, long EstimatedSize, bool SizeKnown, bool NeedsRemux, bool IncludesAudio);

/// <summary>The choice model behind the YouTube window (1.13.0): Type, Container, Resolution, Audio quality. It only filters and defaults; the window shows it.
/// A container is offered only when it can hold the streams that would be used; a combination that needs repackaging is marked (NeedsRemux) so the window can warn.</summary>
public sealed class YoutubeFormatPicker
{
    public static readonly int[] StandardHeights = { 4320, 2160, 1440, 1080, 720, 480, 360, 240, 144 };
    public static readonly string[] Containers = { "mp4", "webm", "mkv" };

    private readonly IReadOnlyList<YoutubeFormat> videos, audios;
    private PickerType type = PickerType.VideoAudio;
    private string container = "mp4";
    private int height; private string? audioId; private bool mp3; private string? rawId; private readonly IReadOnlyList<YoutubeFormat> everything;

    public YoutubeFormatPicker(IReadOnlyList<YoutubeFormat> formats)
    {
        everything = formats; videos = formats.Where(f => f.Video).ToArray();
        audios = formats.Where(f => f.Audio && !f.Video).ToArray();
        type = AvailableTypes.FirstOrDefault(); Normalize();
    }

    public IReadOnlyList<PickerType> AvailableTypes
    {
        get
        {
            var list = new List<PickerType>();
            if (videos.Count > 0) { list.Add(PickerType.VideoAudio); if (videos.Any(v => !v.Audio)) list.Add(PickerType.VideoOnly); }
            if (audios.Count > 0) list.Add(PickerType.AudioOnly);
            return list;
        }
    }
    public PickerType Type { get => type; set { if (AvailableTypes.Contains(value)) { type = value; rawId = null; Normalize(); } } }
    public string Container { get => container; set { if (AvailableContainers.Contains(value)) { container = value; rawId = null; Normalize(); } } }
    /// <summary>0 = Best.</summary>
    public int Height { get => height; set { if (Resolutions.Any(r => r.Height == value)) { height = value; rawId = null; Normalize(); } } }
    /// <summary>null = Best.</summary>
    public string? AudioId { get => audioId; set { if (AudioOptions.Any(a => a.Id == value)) { audioId = value; rawId = null; } } }
    /// <summary>Only for Audio only: convert the audio to MP3.</summary>
    public bool Mp3 { get => mp3 && type == PickerType.AudioOnly; set => mp3 = value; }

    /// <summary>Containers that can hold the streams of the current type. Audio only keeps the original file, so it offers none.</summary>
    public IReadOnlyList<string> AvailableContainers
    {
        get
        {
            if (type == PickerType.AudioOnly) return Array.Empty<string>();
            return Containers.Where(c => type == PickerType.VideoOnly ? videos.Any(v => !v.Audio && CanHold(c, v, null)) : videos.Any(v => Pairs(v, c).Any())).ToArray();
        }
    }

    public IReadOnlyList<PickerResolution> Resolutions
    {
        get
        {
            if (type == PickerType.AudioOnly) return Array.Empty<PickerResolution>();
            var list = new List<PickerResolution>();
            var buckets = Candidates().Select(v => Bucket(v.Height)).Distinct().OrderByDescending(h => h).ToList();
            if (buckets.Count == 0) return list;
            var best = Pick(buckets[0]); if (best is null) return list;
            list.Add(Describe(0, best.Value.V, best.Value.A));
            foreach (var h in buckets) if (Pick(h) is { } p) list.Add(Describe(h, p.V, p.A));
            return list;
        }
    }

    public IReadOnlyList<PickerAudio> AudioOptions
    {
        get
        {
            if (type != PickerType.VideoAudio && type != PickerType.AudioOnly) return Array.Empty<PickerAudio>();
            if (type == PickerType.VideoAudio && Pick(height) is { A: null }) return Array.Empty<PickerAudio>(); // sound is part of the picture format
            var pool = AudioPool();
            if (pool.Count == 0) return Array.Empty<PickerAudio>();
            var list = new List<PickerAudio>();
            var best = BestAudio(pool)!; list.Add(new(null, best.Bitrate, best.Extension, null, best.Size));
            foreach (var group in pool.GroupBy(a => NearestRate(a.Bitrate)).OrderByDescending(g => g.Key))
            {
                var pick = group.OrderByDescending(a => a.Extension == (container == "webm" ? "webm" : "m4a")).ThenByDescending(a => a.Size).First();
                list.Add(new(pick.Id, pick.Bitrate, pick.Extension, null, pick.Size));
            }
            return list;
        }
    }

    /// <summary>The exact format chosen from the "Show all formats" list, or null. Any change of a step above clears it.</summary>
    public string? RawId => rawId;
    /// <summary>Takes one exact format from the full list. A picture-only format gets the best sound that fits; the container is the one the streams already live in (MKV when they differ).</summary>
    public bool UseRaw(string id)
    {
        if (everything.All(f => f.Id != id)) return false;
        rawId = id; return true;
    }
    public void ClearRaw() => rawId = null;

    private PickerResult? RawResult()
    {
        var f = everything.First(x => x.Id == rawId);
        if (!f.Video)
        {
            var sel = new YoutubeSelection(f.Id, null, mp3);
            return new(sel, mp3 ? "mp3" : f.Extension, f, null, 0, f.Bitrate, f.Size, f.Size > 0, false, true);
        }
        if (f.Audio) return new(new YoutubeSelection(f.Id), f.Extension, f, null, f.Height, f.Bitrate, f.Size, f.Size > 0, false, true);
        if (type == PickerType.VideoOnly) return new(new YoutubeSelection(f.Id, null, false, null, f.Extension is "mp4" or "webm" ? f.Extension : "mkv", true), f.Extension, f, null, f.Height, 0, f.Size, f.Size > 0, false, false);
        var a = BestAudio(audios.Where(x => Natural(f, x) is not null).ToList()) ?? BestAudio(audios.ToList());
        if (a is null) return new(new YoutubeSelection(f.Id, null, false, null, f.Extension is "mp4" or "webm" ? f.Extension : "mkv", true), f.Extension, f, null, f.Height, 0, f.Size, f.Size > 0, false, false);
        var natural = Natural(f, a) ?? "mkv";
        return new(new YoutubeSelection(f.Id, a.Id, false, null, natural), natural, f, a, f.Height, a.Bitrate, f.Size + a.Size, f.Size > 0 && a.Size > 0, false, true);
    }

    public PickerResult? Result
    {
        get
        {
            if (rawId is not null) return RawResult();
            if (type == PickerType.AudioOnly)
            {
                var a = (audioId is null ? null : audios.FirstOrDefault(x => x.Id == audioId)) ?? BestAudio(audios.ToList());
                if (a is null) return null;
                var selection = new YoutubeSelection(a.Id, null, mp3);
                return new(selection, mp3 ? "mp3" : a.Extension, a, null, 0, a.Bitrate, a.Size, a.Size > 0, false, true);
            }
            if (Pick(height) is not { } p) return null;
            var v = p.V;
            if (type == PickerType.VideoOnly)
            {
                var ext = container == v.Extension ? v.Extension : container;
                return new(new YoutubeSelection(v.Id, null, false, null, container, true), ext, v, null, v.Height, 0, v.Size, v.Size > 0, container != v.Extension, false);
            }
            if (p.A is null) // a format that already carries sound
                return new(new YoutubeSelection(v.Id, null, false, null, container), container, v, null, v.Height, v.Bitrate, v.Size, v.Size > 0, container != v.Extension, true);
            var audio = (audioId is null ? null : AudioPool().FirstOrDefault(x => x.Id == audioId)) ?? p.A;
            long size = v.Size + audio.Size; bool known = v.Size > 0 && audio.Size > 0;
            return new(new YoutubeSelection(v.Id, audio.Id, false, null, container), container, v, audio, v.Height, audio.Bitrate, size, known, container != "mkv" && Natural(v, audio) != container, true);
        }
    }

    // ---- rules ----
    private void Normalize()
    {
        var all = AvailableContainers;
        if (all.Count > 0 && !all.Contains(container)) container = all.Contains("mp4") ? "mp4" : all[0];
        else if (all.Count == 0 && type != PickerType.AudioOnly) container = "mkv";
        if (type != PickerType.AudioOnly && Resolutions.All(r => r.Height != height)) height = 0;
        if (AudioOptions.All(a => a.Id != audioId)) audioId = null;
    }

    private static string Family(string? codec, string extension, bool video)
    {
        var c = (codec ?? "").ToLowerInvariant();
        if (video)
        {
            if (c.StartsWith("avc") || c.StartsWith("h264")) return "avc";
            if (c.StartsWith("hev") || c.StartsWith("hvc") || c.StartsWith("h265")) return "hevc";
            if (c.StartsWith("av01") || c.StartsWith("av1")) return "av1";
            if (c.StartsWith("vp09") || c.StartsWith("vp9")) return "vp9";
            if (c.StartsWith("vp8")) return "vp8";
            return extension == "webm" ? "vp9" : "avc";
        }
        if (c.StartsWith("mp4a") || c.StartsWith("aac")) return "aac";
        if (c.StartsWith("opus")) return "opus";
        if (c.StartsWith("vorbis")) return "vorbis";
        if (c.StartsWith("mp3")) return "mp3";
        if (c.StartsWith("ac-3") || c.StartsWith("ec-3")) return "ac3";
        return extension is "webm" or "opus" or "ogg" ? "opus" : "aac";
    }

    /// <summary>True when the container can hold the picture and (if any) the sound stream as they are.</summary>
    public static bool CanHold(string container, YoutubeFormat video, YoutubeFormat? audio)
    {
        var v = Family(video.VideoCodec, video.Extension, true);
        var a = audio is null ? (video.Audio ? Family(video.AudioCodec, video.Extension, false) : null) : Family(audio.AudioCodec, audio.Extension, false);
        return container switch
        {
            "mkv" => true,
            "mp4" => v is "avc" or "hevc" or "av1" or "vp9" && (a is null or "aac" or "opus" or "mp3" or "ac3"),
            "webm" => v is "vp9" or "vp8" or "av1" && (a is null or "opus" or "vorbis"),
            _ => false
        };
    }
    /// <summary>The container the two streams already live in (no repackaging at all), or null.</summary>
    private static string? Natural(YoutubeFormat v, YoutubeFormat a) => v.Extension == "mp4" && a.Extension is "m4a" or "mp4" ? "mp4" : v.Extension == "webm" && a.Extension is "webm" or "opus" ? "webm" : null;

    private IEnumerable<YoutubeFormat> Candidates() => type switch
    {
        PickerType.VideoOnly => videos.Where(v => !v.Audio && CanHold(container, v, null)),
        _ => videos.Where(v => Pairs(v, container).Any())
    };
    /// <summary>Sound that goes with a picture format in a container. A combined format brings its own (null entry).</summary>
    private IEnumerable<YoutubeFormat?> Pairs(YoutubeFormat v, string c)
    {
        if (v.Audio) { if (CanHold(c, v, null)) yield return null; yield break; }
        foreach (var a in audios) if (CanHold(c, v, a)) { yield return a; yield break; }
    }
    private static int Bucket(int h) { foreach (var s in StandardHeights) if (h >= s - 4) return s; return h; }

    private (YoutubeFormat V, YoutubeFormat? A)? Pick(int bucketOrZero)
    {
        var pool = Candidates().ToList(); if (pool.Count == 0) return null;
        int target = bucketOrZero == 0 ? pool.Max(v => Bucket(v.Height)) : bucketOrZero;
        var atHeight = pool.Where(v => Bucket(v.Height) == target).ToList(); if (atHeight.Count == 0) return null;
        // separate pictures win over combined ones (better quality at the same height); native container first, then the larger one
        var v = atHeight.OrderByDescending(x => !x.Audio).ThenByDescending(x => x.Extension == container).ThenByDescending(x => x.Height).ThenByDescending(x => x.Size).ThenBy(x => x.Id, StringComparer.Ordinal).First();
        if (type == PickerType.VideoOnly) return (v, null);
        return (v, v.Audio ? null : BestCompatibleAudio(v));
    }
    private YoutubeFormat? BestCompatibleAudio(YoutubeFormat v) => BestAudio(NativeFirst(audios.Where(a => CanHold(container, v, a)).ToList()));
    /// <summary>In an MP4 only AAC (m4a) sound is listed when there is any; Opus is the fallback when the video offers nothing else.</summary>
    private List<YoutubeFormat> NativeFirst(List<YoutubeFormat> pool) => container == "mp4" && pool.Any(a => a.Extension == "m4a") ? pool.Where(a => a.Extension == "m4a").ToList() : pool;
    private List<YoutubeFormat> AudioPool()
    {
        if (type == PickerType.AudioOnly) return audios.ToList();
        return Pick(height) is { V: { } v, A: not null } ? NativeFirst(audios.Where(a => CanHold(container, v, a)).ToList()) : new();
    }
    /// <summary>The best sound of a pool. In an MP4 the AAC (m4a) track is preferred even at a slightly lower rate, because players handle it everywhere; otherwise the highest rate.</summary>
    private YoutubeFormat? BestAudio(List<YoutubeFormat> pool) => pool.OrderByDescending(a => type != PickerType.AudioOnly && container == "mp4" && a.Extension == "m4a").ThenByDescending(a => a.Bitrate).ThenByDescending(a => a.Extension == "m4a").ThenBy(a => a.Id, StringComparer.Ordinal).FirstOrDefault();
    private static int NearestRate(int kbps) { foreach (var r in new[] { 256, 192, 160, 128, 96, 64, 48 }) if (kbps >= r - 8) return r; return kbps; }

    private PickerResolution Describe(int h, YoutubeFormat v, YoutubeFormat? a)
    {
        long size = v.Size + (a?.Size ?? 0); bool known = v.Size > 0 && (a is null || a.Size > 0);
        return new(h == 0 ? 0 : h, v.VideoCodec is { Length: > 0 } c && c != "none" ? c.Split('.')[0] : v.Extension, v.Fps, known ? size : 0, v.SizeEstimated || a?.SizeEstimated == true);
    }

    // ---- shared formatting helpers (the window localizes the words around them) ----
    public static string Megabytes(long bytes) => bytes <= 0 ? "" : bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB" : Math.Max(1, (int)Math.Round(bytes / (double)(1L << 20))).ToString(CultureInfo.InvariantCulture) + " MB";
    public static string FpsText(double fps) => fps <= 0 ? "" : Math.Round(fps).ToString(CultureInfo.InvariantCulture) + " fps";
}
