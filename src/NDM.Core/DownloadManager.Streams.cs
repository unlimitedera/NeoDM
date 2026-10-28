using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;

namespace NDM.Core;

/// <summary>Rules that keep a stream from moving a protected download to an unprotected address.</summary>
public static class StreamSecurity
{
    /// <summary>An https manifest never leads to plain http pieces, keys or playlists.</summary>
    public static void EnsureNoDowngrade(StreamPlan plan, Uri manifest)
    {
        if (manifest.Scheme != Uri.UriSchemeHttps) return;
        foreach (var track in plan.Tracks)
        {
            if (track.SingleFile is { } single) Check(single);
            if (track.Init is { } init) { Check(init.Uri); if (init.Encryption is { } ie) Check(ie.KeyUri); }
            foreach (var s in track.Segments) { Check(s.Uri); if (s.Encryption is { } e) Check(e.KeyUri); }
        }
        static void Check(Uri uri)
        {
            if (uri.Scheme != Uri.UriSchemeHttps) throw new StreamException(StreamError.Downgrade, "The stream tried to move from https to an unencrypted address (" + uri.Host + "). It was refused.");
        }
    }
}

public sealed partial class DownloadManager
{
    /// <summary>The helper tool (ffmpeg) manager of this app: install, check and remove it.</summary>
    public ToolManager Tools => tools;

    private const int MaxManifestBytes = 16 * 1024 * 1024;

    // ---- public API ----

    /// <summary>Adds a streaming download. <paramref name="request"/> must name HLS or DASH; Url is the playlist or manifest address.</summary>
    public Task<DownloadJob> AddStreamAsync(DownloadRequest request, DateTimeOffset? scheduledAt = null)
    {
        if (request.StreamKind == StreamKind.None) throw new ArgumentException("Choose HLS or DASH for a streaming download.", nameof(request));
        return AddScheduledAsync(request, scheduledAt);
    }

    public Task<DownloadJob> AddStreamAsync(string manifestUrl, StreamKind kind, string? variantId = null, string? audioId = null, bool preferMp4 = false, string? fileName = null,
        string? referer = null, string? cookie = null, string? userAgent = null, string? accept = null, string? acceptLanguage = null, string? targetDirectory = null, DateTimeOffset? scheduledAt = null) =>
        AddStreamAsync(new DownloadRequest(manifestUrl, fileName, referer, cookie, false, targetDirectory, null, null, userAgent, accept, acceptLanguage, kind, variantId, audioId, preferMp4), scheduledAt);

    /// <summary>Reads a playlist or manifest and describes what it offers (qualities and sound tracks) for a picker. The content decides, not the file extension.
    /// Throws <see cref="StreamException"/> for live streams, protected streams and anything that is not a stream; <see cref="HttpRequestException"/> when the server refuses.</summary>
    public Task<StreamManifest> InspectStreamAsync(string url, string? referer = null, string? cookie = null, string? userAgent = null, string? accept = null, string? acceptLanguage = null, string? title = null, CancellationToken cancellation = default) =>
        InspectStreamAsync(new DownloadRequest(url, title, referer, cookie, false, null, null, null, userAgent, accept, acceptLanguage), cancellation);

    public async Task<StreamManifest> InspectStreamAsync(DownloadRequest request, CancellationToken cancellation = default)
    {
        ValidateRequest(request);
        ThrowIfDisposed();
        var ladder = new LadderState((MemoryOf(request.Url).Working ?? default) with { NoRange = true });
        var (bytes, final) = await FetchBytesAsync(request, ladder, new Uri(request.Url), cancellation).ConfigureAwait(false);
        var text = Encoding.UTF8.GetString(bytes);
        switch (StreamSniffer.Detect(bytes.AsSpan(0, Math.Min(bytes.Length, 16384))))
        {
            case StreamKind.Hls:
            {
                var doc = HlsParser.Parse(text, final).Require();
                TimeSpan duration = TimeSpan.Zero; StreamContainer? container = null;
                if (doc.IsMaster)
                {
                    var probe = HlsParser.ToManifest(doc, final.AbsoluteUri, request.FileName);
                    var best = probe.BestVariant;
                    if (best is not null && HlsParser.VariantUri(doc, best.Id) is { } uri)
                    {
                        try
                        {
                            var (mb, mf) = await FetchBytesAsync(request, ladder, uri, cancellation).ConfigureAwait(false);
                            var media = HlsParser.Parse(Encoding.UTF8.GetString(mb), mf).Require(); // Live and DRM surface here
                            if (media.Media is { } track) { duration = TimeSpan.FromSeconds(track.Duration); container = track.Container; }
                        }
                        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !cancellation.IsCancellationRequested)) { }
                    }
                }
                return HlsParser.ToManifest(doc, final.AbsoluteUri, request.FileName, duration, container);
            }
            case StreamKind.Dash:
            {
                var doc = DashParser.Parse(text, final).Require();
                return doc.Manifest;
            }
            default:
                throw new StreamException(StreamError.Unsupported, StreamSniffer.NotAStreamMessage);
        }
    }

    // ---- requests ----

    private static bool IsTransientStream(Exception ex, CancellationToken token) => ex switch
    {
        HttpRequestException { StatusCode: { } s } => (int)s is 408 or 429 or >= 500,
        HttpRequestException h => !h.Message.Contains("Redirect from https to http", StringComparison.Ordinal),
        InsufficientDiskSpaceException => false,
        IOException => true,
        CryptographicException => true,
        OperationCanceledException => !token.IsCancellationRequested,
        _ => false
    };

    private TimeSpan StreamBackoff(int attempt) => TimeSpan.FromMilliseconds(Math.Min(5000, options.StreamRetryDelay.TotalMilliseconds * (1 << Math.Min(attempt - 1, 6))));

    /// <summary>One GET with the job's request identity. The cookie goes only to the manifest's own origin. Until something has worked, a refusal that a different request shape often cures climbs the same ladder as a file (without Range is the starting shape for pieces without a byte range).</summary>
    private async Task<HttpResponseMessage> StreamGetAsync(DownloadRequest req, LadderState ladder, Uri url, ByteRange? range, bool useLadder, CancellationToken token)
    {
        var origin = new Uri(req.Url);
        for (int number = 1; ; number++)
        {
            RequestVariant variant; lock (gate) variant = ladder.Current;
            var forThis = RedirectRules.SameOrigin(url, origin) ? req : req with { Cookie = null };
            using var message = BuildMessage(HttpMethod.Get, forThis with { Url = url.AbsoluteUri }, variant);
            if (range is { } r) message.Headers.Range = new RangeHeaderValue(r.Offset, r.Last);
            var response = await SendAsync(message, token).ConfigureAwait(false);
            if (useLadder && number < CompatibilityLadder.MaxAttempts && CompatibilityLadder.IsLadderStatus(response.StatusCode, response.Headers.WwwAuthenticate.Count > 0)
                && CompatibilityLadder.Next(variant, options.Identity, req.Referer, url) is { } next)
            {
                var delay = CompatibilityLadder.Delay(response.StatusCode, response.Headers.RetryAfter, DateTimeOffset.UtcNow, options.LadderPause, options.MaxRetryAfter);
                response.Dispose();
                lock (gate) ladder.Current = next;
                await Task.Delay(delay, token).ConfigureAwait(false);
                continue;
            }
            return response;
        }
    }

    private async Task<(byte[] Bytes, Uri Final)> FetchBytesAsync(DownloadRequest req, LadderState ladder, Uri url, CancellationToken token)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await StreamGetAsync(req, ladder, url, null, true, token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var ms = new MemoryStream();
                var buffer = new byte[65536];
                while (true)
                {
                    var read = await ReadAsync(input, buffer, token).ConfigureAwait(false);
                    if (read == 0) break;
                    if (ms.Length + read > MaxManifestBytes) throw new StreamException(StreamError.Malformed, "The playlist is too large.");
                    ms.Write(buffer, 0, read);
                }
                return (ms.ToArray(), response.RequestMessage?.RequestUri ?? url);
            }
            catch (Exception ex) when (attempt < options.StreamSegmentTries && !token.IsCancellationRequested && IsTransientStream(ex, token))
            {
                await Task.Delay(StreamBackoff(attempt), token).ConfigureAwait(false);
            }
        }
    }

    // ---- planning ----

    private static bool IsAudioOnlyCodecs(string? codecs)
    {
        if (string.IsNullOrWhiteSpace(codecs)) return false;
        var list = codecs.Split(',').Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
        return list.Count > 0 && list.All(c => c.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase) || c.StartsWith("ac-3", StringComparison.OrdinalIgnoreCase) || c.StartsWith("ec-3", StringComparison.OrdinalIgnoreCase) || c.StartsWith("opus", StringComparison.OrdinalIgnoreCase) || c.StartsWith("flac", StringComparison.OrdinalIgnoreCase));
    }

    private static TrackPlan Retag(TrackPlan t, long bandwidth, string? codecs, bool audio) =>
        new() { Segments = t.Segments, Init = t.Init, Container = t.Container, Codecs = codecs, Bandwidth = bandwidth, Duration = t.Duration, IsAudio = audio, SingleFile = t.SingleFile };

    private async Task<TrackPlan> FetchHlsTrackAsync(DownloadRequest req, LadderState ladder, Uri url, long bandwidth, string? codecs, bool audio, CancellationToken token)
    {
        var (bytes, final) = await FetchBytesAsync(req, ladder, url, token).ConfigureAwait(false);
        var doc = HlsParser.Parse(Encoding.UTF8.GetString(bytes), final).Require();
        if (doc.IsMaster || doc.Media is null) throw new StreamException(StreamError.Malformed, "A variant playlist is itself a master playlist.");
        return Retag(doc.Media, bandwidth, codecs, audio || doc.Media.Container is StreamContainer.Aac or StreamContainer.Mp3);
    }

    private async Task<StreamPlan> ResolvePlanAsync(Work w, LadderState ladder, CancellationToken token)
    {
        var req = w.Request; var url = new Uri(req.Url);
        var (bytes, final) = await FetchBytesAsync(req, ladder, url, token).ConfigureAwait(false);
        var text = Encoding.UTF8.GetString(bytes);
        var detected = StreamSniffer.Detect(bytes.AsSpan(0, Math.Min(bytes.Length, 16384)));
        if (detected == StreamKind.None) throw new StreamException(StreamError.Unsupported, StreamSniffer.NotAStreamMessage);
        StreamPlan plan;
        if (detected == StreamKind.Hls)
        {
            var doc = HlsParser.Parse(text, final).Require();
            if (!doc.IsMaster)
            {
                var media = doc.Media!;
                bool audioOnly = media.Container is StreamContainer.Aac or StreamContainer.Mp3;
                var track = Retag(media, 0, null, audioOnly);
                plan = new StreamPlan { Kind = StreamKind.Hls, Duration = media.Duration, Video = audioOnly ? null : track, Audio = audioOnly ? track : null };
            }
            else
            {
                var manifest = HlsParser.ToManifest(doc, final.AbsoluteUri);
                var (variant, audio) = StreamSelection.Choose(manifest, req.StreamVariantId, req.StreamAudioId);
                TrackPlan? video = null, sound = null;
                if (variant is not null)
                {
                    var uri = HlsParser.VariantUri(doc, variant.Id) ?? throw new StreamException(StreamError.Unsupported, "The chosen quality is not in this stream.");
                    bool audioOnly = variant.Resolution.Length == 0 && IsAudioOnlyCodecs(variant.Codecs);
                    var track = await FetchHlsTrackAsync(req, ladder, uri, variant.Bandwidth, variant.Codecs, audioOnly, token).ConfigureAwait(false);
                    if (track.IsAudio) sound = track; else video = track;
                }
                if (audio is not null && sound is null)
                {
                    var uri = HlsParser.AudioUri(doc, audio.Id) ?? throw new StreamException(StreamError.Unsupported, "The chosen sound track is not in this stream.");
                    sound = await FetchHlsTrackAsync(req, ladder, uri, audio.Bandwidth, audio.Codecs, true, token).ConfigureAwait(false);
                }
                plan = new StreamPlan { Kind = StreamKind.Hls, Duration = Math.Max(video?.Duration ?? 0, sound?.Duration ?? 0), Video = video, Audio = sound };
            }
        }
        else
        {
            var doc = DashParser.Parse(text, final).Require();
            plan = doc.BuildPlan(req.StreamVariantId, req.StreamAudioId);
        }
        StreamSecurity.EnsureNoDowngrade(plan, url);
        return plan;
    }

    private static TrackPlan SingleAsSegments(TrackPlan t) => new()
    {
        Segments = new[] { new StreamSegment(0, t.SingleFile!, null, null, t.Duration) }, Container = t.Container == StreamContainer.WebM ? StreamContainer.WebM : StreamContainer.Mp4Single,
        Codecs = t.Codecs, Bandwidth = t.Bandwidth, Duration = t.Duration, IsAudio = t.IsAudio
    };

    private void SettleStreamName(Work w, StreamLayout layout)
    {
        lock (gate)
        {
            if (w.Removed || disposed) return;
            var stem = StreamNaming.Stem(w.Job.FileName);
            var desired = layout.Mode == StreamOutputMode.Separate ? stem + " (video only)" + layout.Extension : stem + layout.Extension;
            var name = AvailableName(DownloadFileNames.Sanitize(desired), w, w.Request.TargetDirectory is null ? null : Path.GetDirectoryName(w.Job.FilePath));
            w.Job.StreamNote = layout.Note;
            if (name != w.Job.FileName)
            {
                var oldDirectory = Path.GetDirectoryName(w.Job.FilePath) ?? downloads;
                var autoDirectory = Path.GetFullPath(pathResolver?.Invoke(w.Job.FileName) ?? downloads);
                var directory = w.Request.TargetDirectory is not null ? oldDirectory : Path.GetFullPath(oldDirectory).TrimEnd(Path.DirectorySeparatorChar).Equals(autoDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                    ? pathResolver?.Invoke(name) ?? downloads : oldDirectory;
                w.Job.FileName = name; w.Job.FilePath = Path.Combine(directory, name);
            }
            try { SaveLocked(); } catch (Exception ex) { LastPersistenceError = ex.Message; }
        }
        Notify();
    }

    // ---- the run ----

    private sealed class TrackStats { public int MediaTotal, MediaDone; public long MediaBytes, InitBytes, Estimate; }

    private sealed class StreamRun
    {
        public StreamPlan Plan = null!; public StreamWorkFolder Folder = null!; public LadderState Ladder = null!; public CancellationToken JobToken;
        public volatile bool AnySuccess;
        public long InFlight, CompletedBytes; public int DoneItems;
        public readonly Dictionary<string, TrackStats> Stats = new();
        public readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> Keys = new();
        public SemaphoreSlim Slots = new(1, 1); public int Limit, PendingShrink;
    }

    /// <summary>True when the stream was downloaded and published. False when it turned out to be one plain file: the job was turned into an ordinary download and the caller carries on with that.</summary>
    private async Task<bool> RunStreamAsync(Work w, CancellationToken token)
    {
        LadderState ladder;
        lock (gate)
        {
            ladder = new LadderState((MemoryOf(w.Request.Url).Working ?? default) with { NoRange = true });
            w.StreamLadder = ladder; w.Trail.Clear(); w.Job.Attempts = null; w.Probing = true; w.Job.ResumeSupported = null;
            w.HasSegmentSample = false; w.SegmentRate = 0; w.SegmentSample = w.Job.SegmentsDone; w.StreamLimit = 0; w.StreamActive = 0;
        }
        StreamPlan plan;
        try { plan = await ResolvePlanAsync(w, ladder, token).ConfigureAwait(false); }
        finally { lock (gate) w.Probing = false; }

        if (plan.Audio is null && plan.Video?.SingleFile is { } file)
        {
            // One file served by byte ranges: an ordinary download of that address, under the name already chosen.
            lock (gate)
            {
                w.Request = w.Request with { Url = file.AbsoluteUri, StreamKind = StreamKind.None, AutoName = false, FileName = w.Job.FileName };
                w.Job.Url = file.AbsoluteUri; w.Job.Stream = StreamKind.None; w.NameWasOpen = false; w.Job.SegmentsDone = w.Job.SegmentsTotal = 0;
                try { SaveLocked(); } catch (Exception ex) { LastPersistenceError = ex.Message; }
            }
            Notify();
            return false;
        }
        if (plan.Video?.SingleFile is not null || plan.Audio?.SingleFile is not null)
            plan = new StreamPlan { Kind = plan.Kind, Duration = plan.Duration, Video = plan.Video?.SingleFile is not null ? SingleAsSegments(plan.Video) : plan.Video, Audio = plan.Audio?.SingleFile is not null ? SingleAsSegments(plan.Audio) : plan.Audio };

        var layout = StreamOutput.Decide(plan, w.Request.PreferMp4, tools.IsAvailable);
        SettleStreamName(w, layout);

        var folder = StreamWorkFolder.Open(PartDirectory(w), plan);
        var run = new StreamRun { Plan = plan, Folder = folder, Ladder = ladder, JobToken = token };
        foreach (var (id, t) in new[] { ("v", plan.Video), ("a", plan.Audio) })
        {
            if (t is null) continue;
            var state = folder.Tracks[id];
            var stats = new TrackStats { MediaTotal = t.Segments.Count, Estimate = (long)Math.Min((double)t.Bandwidth * (t.Duration > 0 ? t.Duration : plan.Duration) / 8, long.MaxValue / 4) };
            for (int i = 0; i < state.Items; i++)
            {
                if (!state.Done[i]) continue;
                run.DoneItems++; run.CompletedBytes += state.Sizes[i];
                if (state.Init is not null && i == 0) stats.InitBytes += state.Sizes[i]; else { stats.MediaDone++; stats.MediaBytes += state.Sizes[i]; }
            }
            run.Stats[id] = stats;
        }
        lock (gate) { w.Job.SegmentsTotal = plan.TotalItems; RefreshStreamProgress(w, run); try { SaveLocked(); } catch (Exception ex) { LastPersistenceError = ex.Message; } }
        long estimate = plan.EstimatedBytes;
        EnsureDiskSpace(w, estimate > 0 ? Math.Max(estimate, folder.DoneBytes) : null, folder.DoneBytes);
        try
        {
            await DownloadItemsAsync(w, run, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await AssembleAndPublishAsync(w, run, token).ConfigureAwait(false);
        }
        finally { folder.Save(); }
        return true;
    }

    private void SetPhase(Work w, string? phase)
    {
        lock (gate) { if (w.Job.StreamPhase == phase) return; w.Job.StreamPhase = phase; }
        Notify();
    }

    private void RefreshStreamProgress(Work w, StreamRun run)
    {
        long done = run.CompletedBytes + Interlocked.Read(ref run.InFlight), estimate = 0;
        foreach (var s in run.Stats.Values)
            estimate += s.MediaDone > 0 && s.MediaTotal > 0 ? s.InitBytes + (s.MediaDone >= s.MediaTotal ? s.MediaBytes : s.MediaBytes * s.MediaTotal / s.MediaDone) : Math.Max(s.Estimate, s.InitBytes);
        estimate = Math.Max(estimate, done);
        w.Total = estimate; w.Job.TotalBytes = estimate; w.Job.DownloadedBytes = done; w.Job.SegmentsDone = run.DoneItems;
    }

    private void ShrinkConnections(Work w, StreamRun run)
    {
        lock (gate)
        {
            if (run.Limit <= 1) return;
            var before = run.Limit;
            run.Limit = CompatibilityLadder.ReduceConnections(before); // halves; permits are taken back as running pieces finish
            run.PendingShrink += before - run.Limit;
            w.StreamLimit = run.Limit;
        }
    }

    private void ReleaseSlot(StreamRun run)
    {
        lock (gate)
        {
            if (run.PendingShrink > 0) { run.PendingShrink--; return; }
        }
        run.Slots.Release();
    }

    private async Task DownloadItemsAsync(Work w, StreamRun run, CancellationToken token)
    {
        var queue = new ConcurrentQueue<(string Track, int Item)>();
        foreach (var t in run.Folder.Tracks.Values) if (t.Init is not null && !t.Done[0]) queue.Enqueue((t.Id, 0));
        foreach (var t in run.Folder.Tracks.Values) for (int i = t.Init is null ? 0 : 1; i < t.Items; i++) if (!t.Done[i]) queue.Enqueue((t.Id, i));
        if (queue.IsEmpty) return;
        int n = Math.Max(1, Math.Min(options.StreamConnections, queue.Count));
        run.Limit = n; run.Slots = new SemaphoreSlim(n, n); lock (gate) w.StreamLimit = n;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        Exception? failure = null;
        var workers = Enumerable.Range(0, n).Select(_ => Task.Run(async () =>
        {
            while (!linked.IsCancellationRequested && queue.TryDequeue(out var item))
            {
                try { await run.Slots.WaitAsync(linked.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                Interlocked.Increment(ref w.StreamActive);
                try { await FetchItemAsync(w, run, item.Track, item.Item, linked.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
                catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); linked.Cancel(); }
                finally { Interlocked.Decrement(ref w.StreamActive); ReleaseSlot(run); }
            }
        })).ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);
        run.Folder.Save();
        token.ThrowIfCancellationRequested();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private async Task FetchItemAsync(Work w, StreamRun run, string trackId, int item, CancellationToken token)
    {
        var seg = run.Folder.Tracks[trackId].ItemAt(item);
        for (int attempt = 1; ; attempt++)
        {
            try { await FetchOnceAsync(w, run, trackId, item, seg, token).ConfigureAwait(false); return; }
            catch (CryptographicException) when (attempt >= options.StreamSegmentTries) { throw new StreamException(StreamError.Malformed, $"Segment {seg.Index + 1} could not be decrypted.", seg.Index + 1); }
            catch (Exception ex) when (attempt < options.StreamSegmentTries && !token.IsCancellationRequested && IsTransientStream(ex, token))
            {
                if (ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable }) ShrinkConnections(w, run);
                await Task.Delay(StreamBackoff(attempt), token).ConfigureAwait(false);
            }
        }
    }

    private static Exception ClassifyFailure(StreamRun run, StreamSegment seg, HttpResponseMessage response)
    {
        var code = (int)response.StatusCode;
        if (code is 404 or 410) return new StreamSegmentMissingException(seg.Index + 1, code);
        if (code is 401 or 403 && run.AnySuccess)
            return new HttpRequestException($"The site stopped letting NeoDM download this video (HTTP {code}) after it had already sent part of it. The link or the sign-in has probably expired. Open the page again and add the video again.", null, response.StatusCode);
        return new HttpRequestException($"Response status code does not indicate success: {code} ({response.StatusCode}).", null, response.StatusCode);
    }

    private async Task FetchOnceAsync(Work w, StreamRun run, string trackId, int item, StreamSegment seg, CancellationToken token)
    {
        var folder = run.Folder; var req = w.Request;
        byte[]? key = seg.Encryption is { } enc ? await GetKeyAsync(w, run, enc).ConfigureAwait(false) : null;
        using var response = await StreamGetAsync(req, run.Ladder, seg.Uri, seg.Range, !run.AnySuccess, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw ClassifyFailure(run, seg, response);
        long skip = 0; long? want;
        if (seg.Range is { } range)
        {
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var cr = response.Content.Headers.ContentRange;
                if (cr?.From != range.Offset) throw new IOException("The server answered a different byte range.");
            }
            else skip = range.Offset; // the server ignored Range and sent the whole file
            want = range.Length;
        }
        else
        {
            if (response.StatusCode == HttpStatusCode.PartialContent) throw new IOException("Unsolicited partial response to a piece.");
            want = response.Content.Headers.ContentLength;
        }
        if (response.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase))) throw new IOException("Server ignored identity content encoding.");
        var finalPath = folder.ItemPath(trackId, item); var temp = finalPath + ".part";
        long got = 0; bool ok = false;
        try
        {
            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous))
            {
                Stream sink = file; CryptoStream? crypto = null; Aes? aes = null;
                try
                {
                    if (key is not null)
                    {
                        aes = Aes.Create(); aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                        crypto = new CryptoStream(file, aes.CreateDecryptor(key, seg.Encryption!.Iv), CryptoStreamMode.Write, leaveOpen: true);
                        sink = crypto;
                    }
                    using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    var buffer = new byte[65536];
                    for (long left = skip; left > 0;)
                    {
                        var read = await ReadAsync(input, buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), token).ConfigureAwait(false);
                        if (read == 0) throw new IOException("The server sent less than the requested range.");
                        left -= read;
                    }
                    bool first = true;
                    while (true)
                    {
                        int toRead = want is { } wanted ? (int)Math.Min(buffer.Length, wanted - got) : buffer.Length;
                        if (toRead <= 0) break;
                        var read = await ReadAsync(input, buffer.AsMemory(0, toRead), token).ConfigureAwait(false);
                        if (read == 0) break;
                        if (first)
                        {
                            first = false;
                            if (key is null && ContentGuard.LooksLikeHtml(buffer.AsSpan(0, read))) throw new WrongContentException("The site sent a web page instead of a piece of the video. The link may have expired.", null, null);
                        }
                        await ThrottleAsync(w, read, token).ConfigureAwait(false);
                        await sink.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                        got += read;
                        Interlocked.Add(ref run.InFlight, read);
                        lock (gate) RefreshStreamProgress(w, run);
                    }
                    if (want is { } w2 && got != w2) throw new IOException("The server sent less than announced for a piece.");
                    if (crypto is not null) await crypto.FlushFinalBlockAsync(token).ConfigureAwait(false);
                    await file.FlushAsync(token).ConfigureAwait(false);
                }
                finally { if (crypto is not null) await crypto.DisposeAsync().ConfigureAwait(false); aes?.Dispose(); }
            }
            File.Move(temp, finalPath, true);
            var size = new FileInfo(finalPath).Length;
            lock (gate)
            {
                Interlocked.Add(ref run.InFlight, -got); got = 0;
                run.CompletedBytes += size; run.DoneItems++;
                var stats = run.Stats[trackId];
                if (folder.Tracks[trackId].Init is not null && item == 0) stats.InitBytes += size; else { stats.MediaDone++; stats.MediaBytes += size; }
                folder.MarkDone(trackId, item, size);
                run.AnySuccess = true;
                RefreshStreamProgress(w, run);
            }
            ok = true;
        }
        finally
        {
            if (!ok)
            {
                if (got != 0) { Interlocked.Add(ref run.InFlight, -got); lock (gate) RefreshStreamProgress(w, run); }
                try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private async Task<byte[]> GetKeyAsync(Work w, StreamRun run, StreamEncryption enc)
    {
        var id = enc.KeyUri.AbsoluteUri;
        var lazy = run.Keys.GetOrAdd(id, _ => new Lazy<Task<byte[]>>(() => FetchKeyAsync(w, run, enc.KeyUri)));
        try { return await lazy.Value.ConfigureAwait(false); }
        catch { run.Keys.TryRemove(id, out _); throw; }
    }

    private async Task<byte[]> FetchKeyAsync(Work w, StreamRun run, Uri keyUri)
    {
        var token = run.JobToken;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await StreamGetAsync(w.Request, run.Ladder, keyUri, null, !run.AnySuccess, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Response status code does not indicate success: {(int)response.StatusCode} ({response.StatusCode}).", null, response.StatusCode);
                using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                var buffer = new byte[64]; int total = 0;
                while (total < buffer.Length)
                {
                    var read = await ReadAsync(input, buffer.AsMemory(total), token).ConfigureAwait(false);
                    if (read == 0) break; total += read;
                }
                if (total != 16) throw new StreamException(StreamError.Malformed, "The encryption key is not 16 bytes long.");
                return buffer[..16];
            }
            catch (Exception ex) when (attempt < options.StreamSegmentTries && !token.IsCancellationRequested && IsTransientStream(ex, token))
            {
                await Task.Delay(StreamBackoff(attempt), token).ConfigureAwait(false);
            }
        }
    }

    // ---- joining and publishing ----

    private static async Task ConcatAsync(StreamWorkFolder folder, StreamWorkFolder.TrackState track, string destination, CancellationToken token)
    {
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous);
        for (int i = 0; i < track.Items; i++)
        {
            var path = folder.ItemPath(track.Id, i);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (input.Length != track.Sizes[i]) throw new IOException("A saved piece changed on disk; resume the download to fetch it again.");
            await input.CopyToAsync(output, 1 << 20, token).ConfigureAwait(false);
        }
        await output.FlushAsync(token).ConfigureAwait(false);
        output.Flush(true);
    }

    private static bool IsAdts(TrackPlan t) => t.Container is StreamContainer.Ts or StreamContainer.Aac && (t.Codecs is null || t.Codecs.Contains("mp4a", StringComparison.OrdinalIgnoreCase));

    /// <summary>Runs the helper tool on the joined tracks. Returns the note code when it could not be used (null = the output is at <paramref name="output"/>).</summary>
    private async Task<string?> TryRemuxAsync(StreamPlan plan, string videoPath, string? audioPath, string output, CancellationToken token)
    {
        try
        {
            List<string> args;
            if (plan.Video is not null && plan.Audio is not null) args = FfmpegCommands.MuxTracks(videoPath, audioPath!, output, IsAdts(plan.Audio));
            else
            {
                var t = plan.Video ?? plan.Audio!;
                args = FfmpegCommands.Rewrap(videoPath, output, plan.Video is null || t.IsAudio, IsAdts(t));
            }
            var work = Path.GetDirectoryName(videoPath)!;
            var result = await tools.RunAsync(args, options.ToolTimeout, token, work).ConfigureAwait(false);
            if (result.TimedOut || result.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length == 0) return StreamCodes.MuxFailed;
            if (plan.Duration > 1)
            {
                var probe = await tools.RunAsync(FfmpegCommands.Probe(output), TimeSpan.FromSeconds(60), token, work).ConfigureAwait(false);
                if (FfmpegCommands.ParseDuration(probe.Error + "\n" + probe.Output) is { } seconds && Math.Abs(seconds - plan.Duration) > plan.Duration * 0.02) return StreamCodes.MuxFailed;
            }
            return null;
        }
        catch (ToolException ex) { return ex.Code == ToolException.NotInstalled ? StreamCodes.NeedsMuxer : StreamCodes.MuxFailed; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return StreamCodes.MuxFailed; }
    }

    private string PlaceUniqueLocked(Work w, string staging, string desired, string targetDir)
    {
        for (var collision = 0; ; collision++)
        {
            if (collision >= 10000) throw new IOException("Unable to reserve a destination filename.");
            var name = AvailableName(DownloadFileNames.Sanitize(desired), w, targetDir);
            try { PlaceLocked(w, staging, name, targetDir); return Path.Combine(targetDir, name); }
            catch (IOException) when (File.Exists(Path.Combine(targetDir, name))) { }
        }
    }

    private async Task AssembleAndPublishAsync(Work w, StreamRun run, CancellationToken token)
    {
        var plan = run.Plan; var folder = run.Folder;
        var layout = StreamOutput.Decide(plan, w.Request.PreferMp4, tools.IsAvailable);
        var stem = StreamNaming.Stem(w.Job.FileName);
        var staging = StagingFile(w); var stagingAudio = staging + ".audio";
        Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
        string? note = layout.Note; string primary, second = "";
        bool two = false;
        try
        {
            var work = PartDirectory(w);
            if (layout.Mode == StreamOutputMode.Single)
            {
                var track = plan.Video is not null ? folder.Tracks["v"] : folder.Tracks["a"];
                await ConcatAsync(folder, track, staging, token).ConfigureAwait(false);
                primary = stem + layout.Extension;
            }
            else
            {
                var vExt = plan.Video is null ? "" : StreamOutput.ExtensionFor(plan.Video, plan.Video.IsAudio);
                var aExt = plan.Audio is null ? "" : StreamOutput.ExtensionFor(plan.Audio, true);
                var vPath = Path.Combine(work, (plan.Video is null ? "audio" : "video") + (plan.Video is null ? aExt : vExt));
                var aPath = plan.Video is not null && plan.Audio is not null ? Path.Combine(work, "audio" + aExt) : null;
                if (plan.Video is not null) await ConcatAsync(folder, folder.Tracks["v"], vPath, token).ConfigureAwait(false);
                else await ConcatAsync(folder, folder.Tracks["a"], vPath, token).ConfigureAwait(false);
                if (aPath is not null) await ConcatAsync(folder, folder.Tracks["a"], aPath, token).ConfigureAwait(false);
                string outPath = Path.Combine(work, "out" + layout.Extension);
                if (layout.Mode == StreamOutputMode.Remux)
                {
                    note = await TryRemuxAsync(plan, vPath, aPath, outPath, token).ConfigureAwait(false);
                    if (note is null) { File.Move(outPath, staging, true); primary = stem + layout.Extension; }
                    else if (aPath is not null)
                    {
                        File.Move(vPath, staging, true); File.Move(aPath, stagingAudio, true); two = true;
                        primary = stem + " (video only)" + StreamOutput.ExtensionFor(plan.Video!, false); second = stem + " (audio only)" + StreamOutput.ExtensionFor(plan.Audio!, true);
                    }
                    else
                    {
                        var t = plan.Video ?? plan.Audio!;
                        File.Move(vPath, staging, true);
                        primary = stem + StreamOutput.ExtensionFor(t, plan.Video is null || t.IsAudio);
                    }
                }
                else
                {
                    File.Move(vPath, staging, true); File.Move(aPath!, stagingAudio, true); two = true;
                    primary = stem + " (video only)" + layout.Extension; second = stem + " (audio only)" + layout.SecondExtension;
                }
            }
            long total = new FileInfo(staging).Length + (two ? new FileInfo(stagingAudio).Length : 0);
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                if (w.Removed || w.Job.Status != "Downloading") throw new OperationCanceledException(token);
                var targetDir = Path.GetDirectoryName(w.Job.FilePath) ?? downloads;
                Directory.CreateDirectory(targetDir);
                var main = PlaceUniqueLocked(w, staging, primary, targetDir);
                var extra = two ? PlaceUniqueLocked(w, stagingAudio, second, targetDir) : null;
                w.Job.FileName = Path.GetFileName(main); w.Job.FilePath = main;
                w.Job.AdditionalFiles = extra is null ? Array.Empty<string>() : new[] { extra };
                w.Job.StreamNote = note;
                w.Total = w.Job.TotalBytes = w.Job.DownloadedBytes = total;
                w.Job.SegmentsDone = w.Job.SegmentsTotal;
                DeletePartial(w); // before the job counts as finished: a finished job has no work folder
                w.Job.Status = "Completed"; w.Job.Error = null; ClearErrorCode(w.Job);
                w.Request = w.Request with { Cookie = null };
                SaveLocked();
            }
        }
        finally
        {
            foreach (var f in new[] { staging, stagingAudio }) { try { if (File.Exists(f)) File.Delete(f); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
        }
    }
}
