using System.Text;

namespace NDM.Core;

public sealed partial class DownloadManager
{
    private const int MaxSubtitlePieces = 2000;
    /// <summary>Counts the subtitle streams of a finished file (ffmpegPath, mediaPath). Tests replace it; null = could not check.</summary>
    public Func<string, string, CancellationToken, Task<int?>> SubtitleTrackCounter { get; set; } = YoutubeService.CountSubtitleTracksAsync;
    private const long MaxSubtitleBytes = 16 * 1024 * 1024;

    /// <summary>1.13.0: fetches the ticked subtitle tracks of a stream and either embeds them (MP4 or MKV output only) or hands them back as separate .srt files.
    /// Never throws for a subtitle problem: the second value is a note code for the finished job. Subtitles a site loads outside the manifest are not seen here.</summary>
    private async Task<(List<SubtitleFile> Separate, string? Note)> StreamSubtitleStepAsync(Work w, StreamRun run, SubtitleRequest wanted, string staging, bool twoFiles, string primaryName, string work, CancellationToken token)
    {
        var tracks = run.Plan.Subtitles.Where(t => wanted.Choices.Any(c => c.Code == t.Id)).ToList();
        // 1.14.0: files the page's player loaded on its own. Skipped when the manifest already lists the same address.
        foreach (var c in wanted.Choices.Where(c => c.IsSeparateFile))
            if (Uri.TryCreate(c.Url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" && !tracks.Any(t => t.Uri == u))
                tracks.Add(new SubtitleTrack(c.Code, c.Language, c.Label, u) { });
        if (tracks.Count == 0) return (new(), null);
        SetPhase(w, StreamCodes.PhaseSubtitles);
        var files = new List<SubtitleFile>(); string? note = null; var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in tracks)
        {
            try
            {
                var srt = await FetchSubtitleAsync(w.Request, run.Ladder, track, new Uri(w.Request.Url), token, wanted.Choices.Any(c => c.IsSeparateFile && c.Code == track.Id)).ConfigureAwait(false);
                if (srt.Length == 0) { note ??= StreamCodes.SubtitlesNone; continue; }
                var language = track.Language is { Length: > 0 } l && SubtitleRequest.SafeCode(l) && used.Add(l) ? l : track.Id; used.Add(language);
                var path = Path.Combine(work, "subtitle-" + track.Id + ".srt");
                await File.WriteAllTextAsync(path, srt, new UTF8Encoding(false), token).ConfigureAwait(false);
                files.Add(new(path, language));
            }
            catch (Exception ex) when (!token.IsCancellationRequested && ex is not OutOfMemoryException) { note ??= StreamCodes.SubtitlesFailed; }
        }
        if (files.Count == 0) return (new(), note ?? StreamCodes.SubtitlesNone);
        if (wanted.Mode == SubtitleMode.Separate) return (files, note);
        var extension = Path.GetExtension(primaryName).ToLowerInvariant();
        if (twoFiles || extension is not (".mp4" or ".mkv")) return (files, StreamCodes.SubtitlesContainer);
        try
        {
            var output = Path.Combine(work, "subtitled" + extension);
            var result = await tools.RunAsync(SubtitleRules.EmbedArguments(staging, files, output, extension.TrimStart('.')), options.ToolTimeout, token, work).ConfigureAwait(false);
            if (result.TimedOut || result.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length == 0) return (files, StreamCodes.SubtitlesEmbedFailed);
            // The same check as for YouTube videos (1.13.1): the finished file must really hold a text track. Could not check = accepted.
            if (await SubtitleTrackCounter(tools.FfmpegPath, output, token).ConfigureAwait(false) == 0) return (files, StreamCodes.SubtitlesEmbedFailed);
            File.Move(output, staging, true);
            return (new(), note);
        }
        catch (ToolException ex) { return (files, ex.Code == ToolException.NotInstalled ? StreamCodes.SubtitlesNeedTool : StreamCodes.SubtitlesEmbedFailed); }
        catch (Exception ex) when (!token.IsCancellationRequested && ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return (files, StreamCodes.SubtitlesEmbedFailed); }
    }

    /// <summary>Reads one subtitle track as SubRip text: a WebVTT playlist (HLS) is joined piece by piece, a single WebVTT or SubRip file (DASH) is converted. Empty text when it is nothing we can read.</summary>
    private async Task<string> FetchSubtitleAsync(DownloadRequest req, LadderState ladder, SubtitleTrack track, Uri manifest, CancellationToken token, bool separateFile = false)
    {
        if (track.Uri is not { } uri || uri.Scheme is not ("http" or "https") || manifest.Scheme == "https" && uri.Scheme != "https") return "";
        // A separate file is read with the stream's own request headers and cookie (through the same engine) and is capped at about 2 MiB.
        var (bytes, final) = await FetchBytesAsync(req, ladder, uri, token, separateFile ? SubtitleRules.MaxSeparateBytes : MaxManifestBytes).ConfigureAwait(false);
        var text = SubtitleRules.DecodeText(bytes);
        if (text.StartsWith("WEBVTT", StringComparison.Ordinal)) return SubtitleRules.VttToSrt(text);
        if (separateFile || text.TrimStart().StartsWith("1\n", StringComparison.Ordinal) || text.TrimStart().StartsWith("1\r\n", StringComparison.Ordinal)) return SubtitleRules.ToSrt(text);
        if (!text.StartsWith("#EXTM3U", StringComparison.Ordinal)) return "";
        var pieces = new List<string>(); long total = 0;
        foreach (var line in text.Split('\n').Select(l => l.Trim()))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            if (pieces.Count >= MaxSubtitlePieces) break;
            if (!Uri.TryCreate(final, line, out var piece) || piece.Scheme is not ("http" or "https") || manifest.Scheme == "https" && piece.Scheme != "https") continue;
            var (part, _) = await FetchBytesAsync(req, ladder, piece, token).ConfigureAwait(false);
            total += part.Length; if (total > MaxSubtitleBytes) break;
            pieces.Add(Encoding.UTF8.GetString(part));
        }
        return pieces.Count == 0 ? "" : SubtitleRules.VttToSrt(SubtitleRules.JoinVtt(pieces));
    }
}
