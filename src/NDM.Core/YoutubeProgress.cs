using System.Globalization;

namespace NDM.Core;

/// <summary>Aggregate helper telemetry. Total is zero when any selected track has no usable size.</summary>
public sealed record YoutubeProgress(long Downloaded, long Total, bool Estimated, string Phase, int FragmentsDone = 0, int FragmentsTotal = 0);

/// <summary>Tracks each selected format independently, so the audio pass cannot reset the video bytes.</summary>
public sealed class YoutubeProgressTracker
{
    private sealed class Track
    {
        public long Done, Total;
        public bool Estimated, Finished;
        public int Fragment, Fragments;
        public bool Video;
    }
    private readonly Dictionary<string, Track> tracks;
    private readonly YoutubeSelection selection;
    public YoutubeProgressTracker(YoutubeSelection selection, YoutubeMetadata metadata)
    {
        this.selection = selection;
        tracks = new[] { selection.VideoId, selection.AudioId }.OfType<string>().ToDictionary(id => id, id =>
        {
            var f = metadata.Formats.Single(f => f.Id == id);
            return new Track { Total = f.Size, Estimated = f.SizeEstimated, Video = f.Video };
        });
    }
    private static long Value(string s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0 && n < long.MaxValue / 4096 ? n : 0;
    public YoutubeProgress? Read(string line)
    {
        var p = line.Trim().Split(':');
        if (p.Length == 3 && p[0] == "NDMPOST") return Snapshot(p[1].Contains("Merger", StringComparison.Ordinal) ? "merging" : p[1].Contains("ExtractAudio", StringComparison.Ordinal) ? "converting" : "finalizing");
        if (p.Length != 8 || p[0] != "NDM" || !tracks.TryGetValue(p[1], out var t)) return null;
        t.Done = Math.Max(t.Done, Value(p[3]));
        var exact = Value(p[4]); var estimate = Value(p[5]);
        t.Fragment = (int)Math.Min(int.MaxValue, Value(p[6])); t.Fragments = (int)Math.Min(int.MaxValue, Value(p[7]));
        if (exact > 0) { t.Total = Math.Max(t.Done, exact); t.Estimated = false; }
        else if (estimate > 0) { t.Total = Math.Max(t.Done, estimate); t.Estimated = true; }
        else if (t.Fragment > 0 && t.Fragments > t.Fragment && t.Done > 0)
        {
            t.Total = (long)Math.Min(long.MaxValue / 4096d, t.Done * (double)t.Fragments / t.Fragment); t.Estimated = true;
        }
        if (p[2] == "finished") { t.Finished = true; t.Total = t.Done; t.Estimated = false; }
        return Snapshot(tracks.Values.All(v => v.Finished) ? selection.Mp3 ? "converting" : selection.AudioId is not null ? "merging" : "finalizing" : t.Video ? "video" : "audio");
    }
    public YoutubeProgress Snapshot(string phase = "reading") => new(tracks.Values.Sum(t => t.Done), tracks.Values.All(t => t.Total > 0) ? tracks.Values.Sum(t => Math.Max(t.Done, t.Total)) : 0,
        tracks.Values.Any(t => t.Estimated), phase, tracks.Values.Sum(t => t.Fragment), tracks.Values.Sum(t => t.Fragments));
}
