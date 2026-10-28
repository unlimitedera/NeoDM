using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NDM.Core;

public enum StreamOutputMode { Single, Remux, Separate }

/// <summary>What the finished download looks like: one file as it came, one file made by the helper tool, or two files.</summary>
public sealed record StreamLayout(StreamOutputMode Mode, string Extension, string? SecondExtension, string? Note);

public static class StreamOutput
{
    public static string ExtensionFor(TrackPlan track, bool audioOnly) => track.Container switch
    {
        StreamContainer.Ts => ".ts",
        StreamContainer.Fmp4 or StreamContainer.Mp4Single => audioOnly ? ".m4a" : ".mp4",
        StreamContainer.WebM => ".webm",
        StreamContainer.Aac => ".aac",
        StreamContainer.Mp3 => ".mp3",
        _ => ".bin"
    };

    public static StreamLayout Decide(StreamPlan plan, bool preferMp4, bool muxerAvailable)
    {
        if (plan.Video is { } v && plan.Audio is { } a)
        {
            var joined = v.Container == StreamContainer.WebM && a.Container == StreamContainer.WebM ? ".webm" : v.Container == StreamContainer.WebM || a.Container == StreamContainer.WebM ? ".mkv" : ".mp4";
            return muxerAvailable
                ? new StreamLayout(StreamOutputMode.Remux, joined, null, null)
                : new StreamLayout(StreamOutputMode.Separate, ExtensionFor(v, false), ExtensionFor(a, true), StreamCodes.NeedsMuxer);
        }
        var t = plan.Video ?? plan.Audio ?? throw new StreamException(StreamError.Unsupported, "The stream has no tracks.");
        bool audio = t.IsAudio || plan.Video is null;
        if (t.Container == StreamContainer.Ts && preferMp4)
            return muxerAvailable ? new StreamLayout(StreamOutputMode.Remux, audio ? ".m4a" : ".mp4", null, null) : new StreamLayout(StreamOutputMode.Single, ".ts", null, StreamCodes.NeedsMuxer);
        return new StreamLayout(StreamOutputMode.Single, ExtensionFor(t, audio), null, null);
    }
}

/// <summary>Names for stream downloads: from the page title or file name hint, else the manifest address.</summary>
public static class StreamNaming
{
    private static readonly string[] KnownExtensions = { ".m3u8", ".m3u", ".mpd", ".ts", ".mp4", ".m4a", ".m4v", ".webm", ".mkv", ".aac", ".mp3" };
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase) { "index", "master", "playlist", "manifest", "main", "stream", "video", "chunklist", "hls", "dash", "media", "default", "prog_index", "playlist_vod", "stream_0" };

    public static string BaseName(string? hint, Uri manifest)
    {
        var fromHint = StripKnown(hint?.Trim());
        if (!string.IsNullOrWhiteSpace(fromHint)) return fromHint!;
        var segments = manifest.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(s => { try { return Uri.UnescapeDataString(s); } catch (UriFormatException) { return s; } }).ToList();
        for (int i = segments.Count - 1; i >= 0; i--)
        {
            var stem = StripKnown(segments[i]) ?? "";
            if (stem.Length > 0 && !Generic.Contains(stem)) return stem;
        }
        return manifest.Host.Length > 0 ? manifest.Host : "video";
    }

    private static string? StripKnown(string? name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        foreach (var ext in KnownExtensions)
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { name = name[..^ext.Length]; break; }
        return name;
    }

    /// <summary>The name a new job shows before the tracks are known.</summary>
    public static string Provisional(DownloadRequest request)
    {
        var uri = new Uri(request.Url);
        return BaseName(request.FileName, uri) + (request.StreamKind == StreamKind.Dash || request.PreferMp4 ? ".mp4" : ".ts");
    }

    /// <summary>The file name without its extension and without a " (video only)" suffix (keeps a " (1)" added for a collision).</summary>
    public static string Stem(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var m = System.Text.RegularExpressions.Regex.Match(stem, @"^(.*?) \(video only\)( \(\d+\))?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return m.Success ? m.Groups[1].Value + m.Groups[2].Value : stem;
    }
}

/// <summary>The per-job folder with the finished pieces and a persisted map of which are done. Everything is checked again after a restart.</summary>
internal sealed class StreamWorkFolder
{
    internal sealed class TrackState
    {
        public string Id = ""; public StreamSegment? Init; public IReadOnlyList<StreamSegment> Segments = Array.Empty<StreamSegment>(); public bool[] Done = Array.Empty<bool>(); public long[] Sizes = Array.Empty<long>();
        public int Items => Done.Length;
        public StreamSegment ItemAt(int item) => Init is null ? Segments[item] : item == 0 ? Init : Segments[item - 1];
    }

    private sealed class Saved
    {
        public int Version { get; set; } = 1;
        public string Signature { get; set; } = "";
        public List<SavedTrack> Tracks { get; set; } = new();
    }
    private sealed class SavedTrack { public string Id { get; set; } = ""; public int Items { get; set; } public string Done { get; set; } = ""; public long[] Sizes { get; set; } = Array.Empty<long>(); }

    private readonly object gate = new();
    private DateTime lastSave = DateTime.MinValue;
    public string Directory { get; }
    public string Signature { get; }
    public Dictionary<string, TrackState> Tracks { get; } = new();
    private string StatePath => Path.Combine(Directory, "stream.json");

    private StreamWorkFolder(string directory, StreamPlan plan)
    {
        Directory = directory;
        void Add(string id, TrackPlan? t)
        {
            if (t is null) return;
            var n = t.ItemCount;
            Tracks[id] = new TrackState { Id = id, Init = t.Init, Segments = t.Segments, Done = new bool[n], Sizes = new long[n] };
        }
        Add("v", plan.Video); Add("a", plan.Audio);
        Signature = Sign(plan);
    }

    /// <summary>Path (without the query string, which often carries a changing token), range and length of every piece decide whether saved progress still fits.</summary>
    public static string Sign(StreamPlan plan)
    {
        var sb = new StringBuilder();
        foreach (var t in plan.Tracks)
        {
            sb.Append('|').Append(t.Container).Append(t.IsAudio ? 'a' : 'v').Append(t.ItemCount);
            if (t.SingleFile is not null) sb.Append(t.SingleFile.GetLeftPart(UriPartial.Path));
            foreach (var s in (t.Init is null ? Enumerable.Empty<StreamSegment>() : new[] { t.Init }).Concat(t.Segments))
                sb.Append('\n').Append(s.Uri.GetLeftPart(UriPartial.Path)).Append('@').Append(s.Range?.Offset ?? -1).Append('+').Append(s.Range?.Length ?? -1).Append(s.Encryption is null ? 'p' : 'e');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public string ItemPath(string track, int item) => Path.Combine(Directory, track + "-" + item.ToString("D6", System.Globalization.CultureInfo.InvariantCulture) + ".seg");

    /// <summary>Opens (or starts) the folder for a plan. Saved progress is kept only when the plan is the same and the files are really there with the recorded sizes.</summary>
    public static StreamWorkFolder Open(string directory, StreamPlan plan)
    {
        System.IO.Directory.CreateDirectory(directory);
        var folder = new StreamWorkFolder(directory, plan);
        bool loaded = false;
        try
        {
            if (File.Exists(folder.StatePath))
            {
                var saved = JsonSerializer.Deserialize<Saved>(FileLimits.ReadAllTextCapped(folder.StatePath, 64L * 1024 * 1024, "stream.json"));
                if (saved is { Version: 1 } && saved.Signature == folder.Signature && saved.Tracks.Count == folder.Tracks.Count)
                {
                    loaded = true;
                    foreach (var st in saved.Tracks)
                    {
                        if (!folder.Tracks.TryGetValue(st.Id, out var track) || st.Items != track.Items || st.Sizes.Length != track.Items) { loaded = false; break; }
                        var bits = Convert.FromBase64String(st.Done);
                        if (bits.Length * 8 < track.Items) { loaded = false; break; }
                        for (int i = 0; i < track.Items; i++)
                        {
                            if ((bits[i / 8] & (1 << (i % 8))) == 0) continue;
                            var path = folder.ItemPath(track.Id, i);
                            if (File.Exists(path) && new FileInfo(path).Length == st.Sizes[i]) { track.Done[i] = true; track.Sizes[i] = st.Sizes[i]; }
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException or InvalidDataException or UnauthorizedAccessException) { loaded = false; }
        if (!loaded)
        {
            foreach (var t in folder.Tracks.Values) { Array.Clear(t.Done); Array.Clear(t.Sizes); }
            folder.Wipe();
        }
        else
        {
            // Pieces that were being written when the app stopped are never trusted.
            foreach (var p in System.IO.Directory.EnumerateFiles(directory, "*.part")) { try { File.Delete(p); } catch (IOException) { } }
        }
        return folder;
    }

    private void Wipe()
    {
        foreach (var p in System.IO.Directory.EnumerateFiles(Directory))
        {
            try { File.Delete(p); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public bool IsDone(string track, int item) { lock (gate) return Tracks[track].Done[item]; }
    public int DoneCount { get { lock (gate) return Tracks.Values.Sum(t => t.Done.Count(d => d)); } }
    public long DoneBytes { get { lock (gate) return Tracks.Values.Sum(t => t.Sizes.Sum()); } }

    public void MarkDone(string track, int item, long size)
    {
        lock (gate)
        {
            var t = Tracks[track]; t.Done[item] = true; t.Sizes[item] = size;
            if (DateTime.UtcNow - lastSave > TimeSpan.FromSeconds(1)) SaveLocked();
        }
    }

    public void Save() { lock (gate) SaveLocked(); }

    private void SaveLocked()
    {
        var saved = new Saved { Signature = Signature };
        foreach (var t in Tracks.Values)
        {
            var bits = new byte[(t.Items + 7) / 8];
            for (int i = 0; i < t.Items; i++) if (t.Done[i]) bits[i / 8] |= (byte)(1 << (i % 8));
            saved.Tracks.Add(new SavedTrack { Id = t.Id, Items = t.Items, Done = Convert.ToBase64String(bits), Sizes = t.Sizes.ToArray() });
        }
        try
        {
            var tmp = StatePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(saved), new UTF8Encoding(false));
            File.Move(tmp, StatePath, true);
            lastSave = DateTime.UtcNow;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
