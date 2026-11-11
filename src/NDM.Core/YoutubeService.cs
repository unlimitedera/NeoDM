using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace NDM.Core;

public static class YoutubeUrls
{
    public static bool IsHost(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && Hosts.Contains(u.IdnHost);
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase) { "youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com", "youtu.be" };
    public static string Canonical(string url)
    {
        if (url.Length > 4096 || url.Any(char.IsControl) || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("https" or "http") || !u.IsDefaultPort || u.UserInfo.Length != 0 || !Hosts.Contains(u.IdnHost)) throw new ArgumentException("Use a YouTube video page address.");
        string? id = null;
        if (u.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)) id = u.AbsolutePath.TrimStart('/');
        else if (u.AbsolutePath == "/watch")
        {
            var ids = u.Query.TrimStart('?').Split('&').Where(p => p.StartsWith("v=", StringComparison.Ordinal)).ToArray();
            if (ids.Length == 1) id = ids[0][2..];
        }
        else if (u.AbsolutePath.StartsWith("/shorts/", StringComparison.Ordinal)) id = u.AbsolutePath[8..];
        else if (u.AbsolutePath.StartsWith("/embed/", StringComparison.Ordinal)) id = u.AbsolutePath[7..];
        if (id is null || !Regex.IsMatch(id, @"\A[A-Za-z0-9_-]{11}\z")) throw new ArgumentException("Use a single YouTube video page, not a channel or playlist.");
        return "https://www.youtube.com/watch?v=" + id;
    }
}

/// <summary>Preset (1.11.0, playlists): the real format ids are not known yet; the job resolves the preset against the video's own format list when it starts. VideoId is then the fixed word "preset".</summary>
public sealed record YoutubeSelection(string VideoId, string? AudioId = null, bool Mp3 = false, YoutubePreset? Preset = null, string? Container = null, bool VideoOnly = false, SubtitleRequest? Subtitles = null)
{
    internal static bool SafeId(string? id) => id is not null && Regex.IsMatch(id, @"\A[A-Za-z0-9_][A-Za-z0-9_-]{0,63}\z");
    public static YoutubeSelection FromPreset(YoutubePreset preset, bool mp3 = false) => new("preset", null, mp3 && preset.AudioOnly, preset);
    public void Validate()
    {
        if (Preset is not null && (!Preset.IsValid || AudioId is not null || VideoId != "preset" || Mp3 && !Preset.AudioOnly)) throw new ArgumentException("Invalid YouTube preset.");
        if (!SafeId(VideoId) || (AudioId is not null && !SafeId(AudioId)) || (Mp3 && AudioId is not null)) throw new ArgumentException("Invalid YouTube format selection.");
        if (Container is not null && !YoutubeFormatPicker.Containers.Contains(Container) || VideoOnly && (AudioId is not null || Mp3 || Preset is not null) || Subtitles is not null && (!Subtitles.IsValid || Mp3)) throw new ArgumentException("Invalid YouTube format selection.");
    }
}
public sealed record YoutubeFormat(string Id, string Extension, int Height, int Bitrate, bool Video, bool Audio, long Size = 0, bool SizeEstimated = false, string? VideoCodec = null, string? AudioCodec = null, double Fps = 0)
{
    public override string ToString() => Video ? $"{Height}p / {Extension} / {Id}" : $"{Bitrate} kb/s / {Extension} / {Id}";
}
public sealed record YoutubeMetadata(string Title, IReadOnlyList<YoutubeFormat> Formats);
public sealed class YoutubeException : Exception
{
    public string Code { get; }
    /// <summary>Short sanitised helper diagnostic for unknown failures, shown as a copyable detail line. Never persisted.</summary>
    public string? Detail { get; }
    public YoutubeException(string code, string? detail = null) : base(code) { Code = code; Detail = detail; }
}

/// <summary>Only the desktop uses this service. Tools are optional, pinned and never updated by a helper process.</summary>
public sealed class YoutubeService
{
    public static readonly ToolPin ExtractorPin = new("https://github.com/yt-dlp/yt-dlp/releases/download/2026.10.26/yt-dlp.exe", "2026.10.26", 23575744,
        "f5389d3698ecde4ef06f4e6b1ce0294fe9ac1396228c8990b8d3416d6d177a7d", "yt-dlp.exe", 23575744, "f5389d3698ecde4ef06f4e6b1ce0294fe9ac1396228c8990b8d3416d6d177a7d");
    public static readonly ToolPin RuntimePin = new("https://github.com/denoland/deno/releases/download/v2.9.8/deno-x86_64-pc-windows-msvc.zip", "2.9.8", 42917472,
        "cba84b26e0df5ae7f74fc66b86a95b866872a86292ebd84fa1e959927edd93bf", "deno.exe", 98081568, "924397800533d3eb0ed0c889b20ad70e18b970d93acd8135bc043f425e528266");
    public ToolManager Extractor { get; }
    public ToolManager Runtime { get; }
    public YoutubeService(string stateDirectory, ToolManager? extractor = null, ToolManager? runtime = null)
    {
        Extractor = extractor ?? new ToolManager(stateDirectory, ExtractorPin, toolName: "yt-dlp", rawExecutable: true, bundleDirectory: Path.Combine(AppContext.BaseDirectory, "tools"));
        Runtime = runtime ?? new ToolManager(stateDirectory, RuntimePin, toolName: "deno", bundleDirectory: Path.Combine(AppContext.BaseDirectory, "tools"));
    }
    public bool IsInstalled => Extractor.IsInstalled && Runtime.IsInstalled;
    public async Task InstallAsync(CancellationToken cancellation = default)
    {
        await Extractor.InstallAsync(cancellation: cancellation).ConfigureAwait(false);
        await Runtime.InstallAsync(cancellation: cancellation).ConfigureAwait(false);
    }
    public static List<string> BaseArguments(string url, string runtimePath)
    {
        return new() { "--ignore-config", "--no-plugin-dirs", "--no-remote-components", "--no-js-runtimes", "--js-runtimes", "deno:" + runtimePath,
            "--fixup", "never", "--no-update", "--no-cache-dir", "--no-playlist", "--use-extractors", "youtube", "--socket-timeout", "30", "--retries", "2", "--fragment-retries", "2",
            "--no-colors", "--encoding", "utf-8", "--match-filters", "!is_live & !was_live & !is_upcoming & !has_drm", "--", YoutubeUrls.Canonical(url) };
    }
    /// <summary>With <paramref name="helperLine"/> the helper runs with <c>--quiet --verbose</c>: its progress lines ("[youtube] ...: Downloading webpage", "Solving JS challenges", ...) arrive on stderr and are passed on one by one (verified with the pinned yt-dlp 2026.10.26). Without it nothing about the call changes.</summary>
    public async Task<YoutubeMetadata> InspectAsync(string url, CancellationToken cancellation = default, Action<string>? helperLine = null)
    {
        using var runtime = Runtime.OpenVerified();
        // Never dump the whole info dict: automatic captions alone are over 10 MB for most videos. Ask only for the fields read below.
        var args = BaseArguments(url, runtime.Path); args.InsertRange(0, new[] { "--skip-download", "--print", InfoTemplate, "--print", FormatsTemplate });
        if (helperLine is not null) args.InsertRange(0, new[] { "--quiet", "--verbose" });
        var text = await RunAsync(args, TimeSpan.FromMinutes(2), MaxMetadataChars, null, null, cancellation, null, helperLine).ConfigureAwait(false);
        return ParseMetadata(text);
    }
    /// <summary>Reads a playlist with one flat listing (no formats): at most 500 entries, bounded output, <c>--no-cookies</c> by default (no sign-in is ever used).</summary>
    public async Task<YoutubePlaylist> ListPlaylistAsync(string playlistUrl, CancellationToken cancellation = default, Action<string>? helperLine = null)
    {
        var link = YoutubeLinks.Parse(playlistUrl);
        if (link.PlaylistUrl is not { } canonical || link.Kind == YoutubeLinkKind.None) throw new YoutubeException(link.RefusedReason ?? YoutubeLinks.RefusedUnknown);
        using var runtime = Runtime.OpenVerified();
        var args = YoutubePlaylists.ListArguments(canonical, runtime.Path).ToList();
        var text = await RunAsync(args, TimeSpan.FromMinutes(3), YoutubePlaylists.MaxListChars, null, null, cancellation, null, helperLine).ConfigureAwait(false);
        return YoutubePlaylists.Parse(text);
    }
    internal const int MaxMetadataChars = 4 * 1024 * 1024;
    private const int MaxInfoLineChars = 64 * 1024;
    public const string InfoTemplate = "%(.{title,availability,is_live,was_live,is_upcoming,has_drm,_type})j";
    public const string FormatsTemplate = "%(formats.:.{format_id,ext,vcodec,acodec,height,abr,fps,filesize,filesize_approx,has_drm})j";
    /// <summary>Parses the two --print lines: an info object, then a format array. Empty output with exit 0 means the --match-filters
    /// (live, was live, upcoming, DRM) rejected the video: yt-dlp prints nothing in that case, so it maps to youtube.unsupported.</summary>
    public static YoutubeMetadata ParseMetadata(string text)
    {
        if (text.Length > MaxMetadataChars) throw new YoutubeException("youtube.metadata");
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        if (lines.Length == 0) throw new YoutubeException("youtube.unsupported");
        if (lines.Length != 2 || lines[0].Length > MaxInfoLineChars || lines[0][0] != '{' || lines[1][0] != '[') throw new YoutubeException("youtube.metadata");
        try
        {
            using var doc = JsonDocument.Parse(lines[0], new JsonDocumentOptions { MaxDepth = 8 }); var root = doc.RootElement;
            using var list = JsonDocument.Parse(lines[1], new JsonDocumentOptions { MaxDepth = 8 });
            if (root.TryGetProperty("entries", out _) || Str(root, "_type") is { } kind && kind != "video" || Flag(root, "is_live") || Flag(root, "was_live") || Flag(root, "is_upcoming") || Flag(root, "has_drm")) throw new YoutubeException("youtube.unsupported");
            if (root.TryGetProperty("availability", out var availability) && availability.GetString() is string access && access is not ("public" or "unlisted")) throw new YoutubeException("youtube.restricted");
            var title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "YouTube" : "YouTube";
            title = DownloadFileNames.Sanitize(title[..Math.Min(title.Length, 160)]);
            var formats = new List<YoutubeFormat>();
            foreach (var f in list.RootElement.EnumerateArray().Take(2048))
            {
                var id = Str(f, "format_id"); var ext = Str(f, "ext");
                if (!YoutubeSelection.SafeId(id) || ext is not ("mp4" or "webm" or "m4a" or "mp3" or "opus" or "ogg") || Flag(f, "has_drm")) continue;
                bool v = Str(f, "vcodec") is { Length: > 0 } vc && vc != "none", a = Str(f, "acodec") is { Length: > 0 } ac && ac != "none";
                if (!v && !a) continue;
                formats.Add(new(id!, ext, Number(f, "height", 16384), Number(f, "abr", 10000), v, a, SizeOf(f, "filesize") is > 0 ? SizeOf(f, "filesize") : SizeOf(f, "filesize_approx"), SizeOf(f, "filesize") <= 0 && SizeOf(f, "filesize_approx") > 0, v ? Str(f, "vcodec") : null, a ? Str(f, "acodec") : null, Number(f, "fps", 1000)));
            }
            if (formats.Count == 0) throw new YoutubeException("youtube.unsupported");
            return new(title, formats.DistinctBy(f => f.Id).ToArray());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { throw new YoutubeException("youtube.metadata"); }
    }
    /// <summary>Last ERROR line of helper stderr (else the last non-empty line), control and format characters removed, at most 300 characters.</summary>
    public static string? LastDiagnostic(string error)
    {
        var lines = error.Split('\n').Select(l => new string(l.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray()).Trim()).Where(l => l.Length > 0).ToArray();
        if (lines.Length == 0) return null;
        var line = lines.LastOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) ?? lines[^1];
        return line.Length > 300 ? line[..300] : line;
    }
    private static long SizeOf(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n > 0 && n < long.MaxValue / 4096 ? n : 0;
    private static string? Str(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool Flag(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
    private static int Number(JsonElement e, string key, int max) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n) ? (int)Math.Clamp(n, 0, max) : 0;
    public static string OutputExtension(YoutubeSelection selection, YoutubeMetadata metadata)
    {
        selection.Validate(); var v = metadata.Formats.SingleOrDefault(f => f.Id == selection.VideoId) ?? throw new YoutubeException("youtube.format");
        string ext;
        if (selection.AudioId is { } id)
        {
            var a = metadata.Formats.SingleOrDefault(f => f.Id == id);
            if (!v.Video || v.Audio || a is null || a.Video || !a.Audio) throw new YoutubeException("youtube.format");
            ext = selection.Container ?? "mkv";
        }
        else if (selection.VideoOnly)
        {
            if (!v.Video || v.Audio) throw new YoutubeException("youtube.format");
            ext = selection.Container ?? v.Extension;
        }
        else
        {
            if (v.Video && !v.Audio || selection.Mp3 && v.Video) throw new YoutubeException("youtube.format");
            ext = selection.Mp3 ? "mp3" : v.Video && selection.Container is { } c ? c : v.Extension;
        }
        return selection.Subtitles is { } subtitles && v.Video ? SubtitleRules.OutputExtension(ext, subtitles.Mode) : ext;
    }
    /// <summary>Does the chosen picture format need a repackaging pass after the download (a combined or video-only format asked for another container)?</summary>
    private static bool NeedsRemux(YoutubeSelection selection, YoutubeFormat v) => selection.AudioId is null && v.Video && selection.Container is { } c && c != v.Extension && !selection.Mp3;
    /// <summary>Lists the caption languages (names only, never caption text) in its own helper call, so a huge automatic caption list can never break the format list. A failure gives an empty list.</summary>
    public async Task<SubtitleListing> ListSubtitlesAsync(string url, CancellationToken cancellation = default)
    {
        try
        {
            using var runtime = Runtime.OpenVerified();
            var args = BaseArguments(url, runtime.Path); args.InsertRange(0, new[] { "--skip-download", "--print", SubtitleTemplate });
            var text = await RunAsync(args, TimeSpan.FromMinutes(2), SubtitleRules.MaxListingChars, null, null, cancellation).ConfigureAwait(false);
            return SubtitleRules.ParseListing(text);
        }
        catch (Exception ex) when (ex is YoutubeException or ToolException or IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { return SubtitleListing.Empty; }
    }
    public const string SubtitleTemplate = "%(.{subtitles,automatic_captions})j";

    /// <summary>Counts the subtitle streams of a finished embed output (1.13.1). Null means "could not check" and is accepted; 0 marks the embed as failed.
    /// Tests replace it; the default runs the ffprobe.exe that sits next to the verified ffmpeg.</summary>
    public Func<string, string, CancellationToken, Task<int?>> SubtitleTrackCounter { get; set; } = CountSubtitleTracksAsync;

    /// <summary>The default <see cref="SubtitleTrackCounter"/>: asks the ffprobe next to the given ffmpeg how many subtitle streams the file has.</summary>
    public static async Task<int?> CountSubtitleTracksAsync(string ffmpegPath, string mediaPath, CancellationToken cancellation)
    {
        try
        {
            var probe = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ffmpegPath)) ?? "", "ffprobe.exe");
            if (!File.Exists(probe)) return null;
            var info = new ProcessStartInfo(probe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in SubtitleRules.ProbeArguments(mediaPath)) info.ArgumentList.Add(argument);
            using var process = Process.Start(info);
            if (process is null) return null;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            limit.CancelAfter(TimeSpan.FromSeconds(30));
            var text = await process.StandardOutput.ReadToEndAsync(limit.Token).ConfigureAwait(false);
            _ = await process.StandardError.ReadToEndAsync(limit.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? SubtitleRules.CountProbedStreams(text) : null;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException && !cancellation.IsCancellationRequested) { return null; }
    }
    public async Task<string> DownloadAsync(string url, YoutubeSelection selection, string folder, ToolManager muxer, long? speedLimit, Action<long, long>? progress, CancellationToken cancellation, Action<YoutubeProgress>? telemetry = null, Action<YoutubeMetadata>? metadataSeen = null, YoutubeDownloadExtras? extras = null)
    {
        var metadata = await InspectAsync(url, cancellation).ConfigureAwait(false);
        metadataSeen?.Invoke(metadata);
        selection = YoutubePresets.Resolve(selection, metadata); var ext = OutputExtension(selection, metadata);
        var tracker = new YoutubeProgressTracker(selection, metadata); telemetry?.Invoke(tracker.Snapshot());
        using var runtime = Runtime.OpenVerified();
        var chosenVideo = metadata.Formats.Single(f => f.Id == selection.VideoId); var remux = NeedsRemux(selection, chosenVideo);
        using var ffmpeg = selection.AudioId is not null || selection.Mp3 || remux ? muxer.OpenVerified(requirePinned: true) : null;
        Directory.CreateDirectory(folder);
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new YoutubeException("youtube.output");
        // An edited page URL or selection must never resume bytes from the previous media.
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(YoutubeUrls.Canonical(url) + "\n" + JsonSerializer.Serialize(selection))));
        folder = Path.Combine(Path.GetFullPath(folder), "youtube-" + identity[..32]);
        Directory.CreateDirectory(folder);
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new YoutubeException("youtube.output");
        var args = BaseArguments(url, runtime.Path);
        var prefix = new List<string> { "--newline", "--no-simulate", "--continue", "--no-overwrites", "--no-mtime", "--progress-template", "download:NDM:%(info.format_id)s:%(progress.status)s:%(progress.downloaded_bytes)s:%(progress.total_bytes)s:%(progress.total_bytes_estimate)s:%(progress.fragment_index)s:%(progress.fragment_count)s", "--progress-template", "postprocess:NDMPOST:%(progress.postprocessor)s:%(progress.status)s", "--progress-delta", "0.1", "--format", selection.VideoId + (selection.AudioId is null ? "" : "+" + selection.AudioId), "--paths", folder, "--output", "media.%(ext)s", "--no-write-info-json", "--no-write-thumbnail", "--no-write-subs" };
        if (speedLimit is > 0) prefix.AddRange(new[] { "--limit-rate", speedLimit.Value.ToString(CultureInfo.InvariantCulture) });
        prefix.AddRange(new[] { "--ffmpeg-location", ffmpeg?.Path ?? Path.Combine(Runtime.ToolDirectory, "disabled") });
        if (selection.AudioId is not null) prefix.AddRange(new[] { "--merge-output-format", selection.Container ?? "mkv" });
        if (remux) prefix.AddRange(new[] { "--remux-video", selection.Container! });
        if (selection.Mp3) prefix.AddRange(new[] { "--extract-audio", "--audio-format", "mp3" });
        args.InsertRange(0, prefix);
        await RunAsync(args, TimeSpan.FromDays(2), 65536, folder, progress, cancellation, line => { var sample = tracker.Read(line); if (sample is not null) { telemetry?.Invoke(sample); progress?.Invoke(sample.Downloaded, sample.Total); } }).ConfigureAwait(false);
        var subtitled = selection.Subtitles is not null && chosenVideo.Video;
        var path = Path.Combine(folder, "media." + (subtitled ? PlainExtension(selection, chosenVideo) : ext)); // WebM stays WebM until the subtitle step may turn it into MKV
        if (subtitled) path = await SubtitleStepAsync(url, selection.Subtitles!, folder, path, ext, muxer, runtime.Path, extras, cancellation, telemetry).ConfigureAwait(false);
        if (!File.Exists(path) || new FileInfo(path).Length == 0 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new YoutubeException("youtube.output");
        return path;
    }
    private static string PlainExtension(YoutubeSelection selection, YoutubeFormat v) =>
        selection.AudioId is not null ? selection.Container ?? "mkv" : selection.VideoOnly ? selection.Container ?? v.Extension : v.Video && selection.Container is { } c ? c : v.Extension;

    /// <summary>Fetches the chosen captions after the video is done and either embeds them or leaves them as separate .srt files (reported through <paramref name="extras"/>).
    /// Any failure here is only a warning: the video is already complete and is returned as it is.</summary>
    private async Task<string> SubtitleStepAsync(string url, SubtitleRequest request, string folder, string video, string finalExtension, ToolManager muxer, string runtimePath, YoutubeDownloadExtras? extras, CancellationToken cancellation, Action<YoutubeProgress>? telemetry)
    {
        telemetry?.Invoke(new YoutubeProgress(0, 0, false, "subtitles"));
        try
        {
            using var ffmpeg = muxer.OpenVerified(requirePinned: true);
            var found = new List<SubtitleFile>();
            foreach (var auto in new[] { false, true })
            {
                var codes = request.Choices.Where(c => c.Auto == auto).Select(c => c.Code).Distinct(StringComparer.Ordinal).ToArray();
                if (codes.Length == 0) continue;
                try { await RunAsync(SubtitleRules.FetchArguments(url, runtimePath, folder, codes, auto, ffmpeg.Path), TimeSpan.FromMinutes(5), 65536, folder, null, cancellation).ConfigureAwait(false); }
                catch (YoutubeException) when (!cancellation.IsCancellationRequested) { extras?.Warn("youtube.subtitles_failed"); }
            }
            foreach (var file in Directory.GetFiles(folder, "media.*.srt").OrderBy(f => f, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(file); var code = name["media.".Length..^".srt".Length];
                if (request.Choices.Any(c => c.Code == code) && new FileInfo(file).Length > 0 && (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0 && found.All(f => f.Language != code)) found.Add(new(file, code));
            }
            if (found.Count == 0) { extras?.Warn("youtube.subtitles_none"); return video; }
            if (request.Mode == SubtitleMode.Separate) { extras?.Separate.AddRange(found); return video; }
            var outExt = SubtitleRules.OutputExtension(Path.GetExtension(video).TrimStart('.'), SubtitleMode.Embed);
            var output = Path.Combine(folder, "media-with-subtitles." + outExt);
            var result = await muxer.RunAsync(SubtitleRules.EmbedArguments(video, found, output, outExt), TimeSpan.FromMinutes(30), cancellation, folder).ConfigureAwait(false);
            if (result.TimedOut || result.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length == 0) { extras?.Warn("youtube.subtitles_embed_failed"); extras?.Separate.AddRange(found); try { File.Delete(output); } catch (IOException) { } return video; }
            // 1.13.1: an embed can exit 0 and still write no text track (seen with silent mov_text failures). Check the output really carries one;
            // when it does not, say so and keep the .srt files instead of claiming success. A missing or failing ffprobe never fails a good embed.
            int? tracks = null;
            try { tracks = await SubtitleTrackCounter(ffmpeg.Path, output, cancellation).ConfigureAwait(false); }
            catch (Exception) when (!cancellation.IsCancellationRequested) { }
            if (tracks == 0) { extras?.Warn("youtube.subtitles_embed_failed"); extras?.Separate.AddRange(found); try { File.Delete(output); } catch (IOException) { } return video; }
            var target = Path.Combine(folder, "media." + outExt);
            File.Move(output, target, true);
            if (!string.Equals(target, video, StringComparison.OrdinalIgnoreCase)) File.Delete(video);
            return target;
        }
        catch (Exception ex) when (ex is ToolException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException && !cancellation.IsCancellationRequested)
        {
            extras?.Warn(ex is ToolException { Code: ToolException.NotInstalled } ? "youtube.subtitles_need_tool" : "youtube.subtitles_failed");
            return video;
        }
    }

    private async Task<string> RunAsync(IReadOnlyList<string> args, TimeSpan timeout, int outputCap, string? folder, Action<long, long>? progress, CancellationToken cancellation, Action<string>? lineProgress = null, Action<string>? errorLine = null)
    {
        using var tool = Extractor.OpenVerified();
        var start = new ProcessStartInfo(tool.Path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = folder ?? Runtime.StateDirectory };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        ToolManager.RestrictSearchPath(start);
        // Do not inherit runtime configuration or proxy credentials from this process.
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("DENO_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase) || k.EndsWith("_PROXY", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        var temporary = folder is not null ? Path.Combine(folder, "tmp") : Path.Combine(Runtime.StateDirectory, "tools-runtime", "temp"); Directory.CreateDirectory(temporary); // a download's helper unpacks and merges on the destination volume
        start.Environment["TEMP"] = start.Environment["TMP"] = start.Environment["TMPDIR"] = temporary;
        start.Environment["DENO_DIR"] = Path.Combine(Runtime.StateDirectory, "tools-runtime", "cache");
        start.Environment["DENO_NO_UPDATE_CHECK"] = "1";
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation); linked.CancelAfter(timeout);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new YoutubeException("youtube.failed");
        process.StandardInput.Close();
        using var registration = linked.Token.Register(() => { try { process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } });
        var output = ReadAsync(process.StandardOutput, outputCap, progress, linked.Token, lineProgress); var errors = ReadAsync(process.StandardError, 32768, null, linked.Token, tail: true, errorLine: errorLine);
        _ = output.ContinueWith(_ => linked.Cancel(), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _ = errors.ContinueWith(_ => linked.Cancel(), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            try { await process.WaitForExitAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                // Our own watchdog fired, not the user: a reader refused oversized or hostile output, or the helper timed out. Report that, not a generic failure.
                if (output.IsFaulted && output.Exception?.GetBaseException() is YoutubeException reason) throw new YoutubeException(reason.Code, reason.Detail);
                if (errors.IsFaulted && errors.Exception?.GetBaseException() is YoutubeException other) throw new YoutubeException(other.Code, other.Detail);
                throw new YoutubeException("youtube.failed", "timeout");
            }
            var text = await output.ConfigureAwait(false); var error = await errors.ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) { var code = Classify(error); throw new YoutubeException(code, code == "youtube.failed" ? LastDiagnostic(error) : null); }
            return text;
        }
        finally
        {
            if (!process.HasExited) { try { process.Kill(true); } catch (InvalidOperationException) { } }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); await Task.WhenAll(output, errors).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); } catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or YoutubeException) { }
        }
    }
    public static string Classify(string error)
    {
        if (error.Contains("429", StringComparison.Ordinal) || error.Contains("rate", StringComparison.OrdinalIgnoreCase)) return "youtube.rate";
        if (error.Contains("sign in", StringComparison.OrdinalIgnoreCase) || error.Contains("sign-in", StringComparison.OrdinalIgnoreCase) || error.Contains("private", StringComparison.OrdinalIgnoreCase) || error.Contains("403", StringComparison.Ordinal) || error.Contains("not a bot", StringComparison.OrdinalIgnoreCase)) return "youtube.restricted";
        if (error.Contains("DRM", StringComparison.OrdinalIgnoreCase) || error.Contains("unavailable", StringComparison.OrdinalIgnoreCase) || error.Contains("live stream", StringComparison.OrdinalIgnoreCase) || error.Contains("live event", StringComparison.OrdinalIgnoreCase) || error.Contains("premiere", StringComparison.OrdinalIgnoreCase)) return "youtube.unsupported";
        return "youtube.failed";
    }
    private static async Task<string> ReadAsync(StreamReader reader, int cap, Action<long, long>? progress, CancellationToken cancellation, Action<string>? lineProgress = null, bool tail = false, Action<string>? errorLine = null)
    {
        var text = new StringBuilder(); var line = new StringBuilder(); var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellation).ConfigureAwait(false)) > 0)
        {
            if (progress is null && lineProgress is null)
            {
                if (tail && errorLine is not null)
                {
                    // Verbose helper output: pass each line on, keep only the lines that matter for a failure message (debug lines and their continuations are dropped).
                    foreach (var ch in buffer.AsSpan(0, read).ToArray())
                    {
                        if (ch == '\n')
                        {
                            var value = line.ToString().TrimEnd('\r'); line.Clear();
                            if (value.Length == 0) continue;
                            try { errorLine(value); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
                            if (!value.StartsWith("[debug]", StringComparison.Ordinal) && !char.IsWhiteSpace(value[0])) { text.Append(value).Append('\n'); if (text.Length > cap) text.Remove(0, text.Length - cap); }
                        }
                        else if (line.Length < 4096) line.Append(ch);
                    }
                    continue;
                }
                if (tail) { text.Append(buffer, 0, read); if (text.Length > cap) text.Remove(0, text.Length - cap); continue; }
                if (text.Length + read > cap) throw new YoutubeException("youtube.metadata");
                text.Append(buffer, 0, read);
            }
            else foreach (var ch in buffer.AsSpan(0, read).ToArray())
            {
                if (ch == '\n')
                {
                    var value = line.ToString().Trim(); lineProgress?.Invoke(value);
                    var parts = value.Split(':'); line.Clear();
                    if (parts.Length == 3 && parts[0] == "NDM" && long.TryParse(parts[1], out var done) && done >= 0) { long.TryParse(parts[2], out var total); progress?.Invoke(done, Math.Max(0, total)); }
                }
                else if (line.Length < 4096) line.Append(ch);
            }
        }
        return text.ToString();
    }
}

/// <summary>What a YouTube download hands back besides the video: subtitle files that stay separate, and a warning code when the subtitle step did not fully work (never an error).</summary>
public sealed class YoutubeDownloadExtras
{
    public List<SubtitleFile> Separate { get; } = new();
    public string? Warning { get; private set; }
    public void Warn(string code) { Warning ??= code; }
}
