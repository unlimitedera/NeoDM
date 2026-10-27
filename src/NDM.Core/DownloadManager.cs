using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace NDM.Core;

/// <summary>Owns a persistent queue; one manager may own a state directory at a time.</summary>
public sealed partial class DownloadManager : IDisposable
{
    private readonly object gate = new();
    private readonly string downloads, state, queuePath;
    private readonly DownloadOptions options;
    private readonly Func<string, string>? pathResolver;
    private readonly HttpClient client;
    private readonly FileStream ownership;
    private readonly List<Work> jobs = new();
    private readonly HashSet<Work> activeJobs = new();
    private readonly Timer timer;
    private bool disposed;
    /// <summary>What worked for a site earlier in this session (memory only, never saved): the request shape that was accepted, a lower connection count, a refused HEAD.</summary>
    private readonly ConcurrentDictionary<string, HostMemory> hosts = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset lastSave = DateTimeOffset.MinValue;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public event Action? Changed;
    public string? LastPersistenceError { get; private set; }
    public IReadOnlyList<DownloadJob> Jobs { get { lock (gate) return jobs.Select(SnapshotLocked).ToArray(); } }

    public DownloadManager(string downloadDirectory, string stateDirectory)
        : this(downloadDirectory, stateDirectory, new DownloadOptions()) { }

    public DownloadManager(string downloadDirectory, string stateDirectory, DownloadOptions options)
        : this(downloadDirectory, stateDirectory, options, null) { }

    public DownloadManager(string downloadDirectory, string stateDirectory, DownloadOptions options, Func<string, string>? pathResolver)
    {
        options.Validate();
        this.options = options;
        this.pathResolver = pathResolver;
        downloads = Path.GetFullPath(downloadDirectory);
        state = Path.GetFullPath(stateDirectory);
        Directory.CreateDirectory(downloads);
        Directory.CreateDirectory(Path.Combine(downloads, ".ndm-staging"));
        Directory.CreateDirectory(state);
        queuePath = Path.Combine(state, "queue.json");
        ownership = new FileStream(Path.Combine(state, "queue.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = false,
            MaxConnectionsPerServer = options.MaxConcurrentDownloads * options.MaxSegments,
            ConnectTimeout = options.RequestTimeout,
            UseCookies = false,
            ResponseHeaderEncodingSelector = (_, _) => System.Text.Encoding.Latin1 // raw bytes; FileNameResolver re-decodes UTF-8 file names
        };
        ProxyHttp.Apply(handler, options.Proxy);
        client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        try { Restore(); }
        catch { ownership.Dispose(); client.Dispose(); throw; }
        timer = new Timer(Tick, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(200));
    }

    public Task<DownloadJob> AddAsync(DownloadRequest request) => AddScheduledAsync(request, null);
    public Task<DownloadJob> AddScheduledAsync(DownloadRequest request, DateTimeOffset? scheduledAt)
    {
        ValidateRequest(request);
        DownloadJob result;
        lock (gate)
        {
            ThrowIfDisposed();
            var uri = new Uri(request.Url);
            // Provisional name from the URL; the real one is settled from the server's answer when the transfer starts.
            var candidate = request.FileName ?? FileNameResolver.Resolve(uri, null, null, null).Name;
            if (request.FileName is null && !request.AutoName) request = request with { AutoName = true };
            string name, targetDir;
            if (request.TargetDirectory is { } custom)
            {
                // The folder the user chose is used verbatim: created if missing, proven writable now so a bad folder fails here and not at the end.
                targetDir = EnsureWritableFolder(custom);
                request = request with { TargetDirectory = targetDir };
                name = AvailableName(DownloadFileNames.Sanitize(candidate), null, targetDir);
            }
            else
            {
                name = AvailableName(DownloadFileNames.Sanitize(candidate));
                targetDir = pathResolver?.Invoke(name) ?? downloads;
            }
            var work = new Work
            {
                NameWasOpen = request.AutoName,
                Request = request,
                Job = new DownloadJob
                {
                    Id = Guid.NewGuid().ToString("N"), Url = request.Url, FileName = name,
                    FilePath = Path.Combine(targetDir, name), ScheduledAt = scheduledAt,
                    Referer = request.Referer, AddedAt = DateTimeOffset.UtcNow,
                    Status = scheduledAt > DateTimeOffset.UtcNow ? "Scheduled" : "Queued"
                }
            };
            jobs.Add(work);
            try { SaveLocked(); }
            catch { jobs.Remove(work); throw; }
            result = SnapshotLocked(work);
        }
        Notify(); Tick(null);
        return Task.FromResult(result);
    }

    public void Pause(string id)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var w = Find(id);
            if (w.Job.Status == "Completed") return;
            w.Job.Status = "Paused"; w.Job.SpeedBytesPerSecond = 0;
            w.Cancellation?.Cancel(); SaveLocked();
        }
        Notify();
    }

    public void Resume(string id)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var w = Find(id);
            if (w.Job.Status == "Completed" || w.Job.Status == "Downloading") return;
            w.Job.Error = null; ClearErrorCode(w.Job);
            w.Job.Status = w.Job.ScheduledAt > DateTimeOffset.UtcNow ? "Scheduled" : "Queued";
            SaveLocked();
        }
        Notify(); Tick(null);
    }

    private static void ClearErrorCode(DownloadJob job) { job.ErrorStatus = null; job.ErrorCode = null; job.ErrorReceivedBytes = job.ErrorExpectedBytes = null; job.ErrorRedirected = false; }

    public void Schedule(string id, DateTimeOffset scheduledAt)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var w = Find(id);
            if (w.Job.Status == "Completed") return;
            w.Cancellation?.Cancel();
            w.Job.ScheduledAt = scheduledAt;
            w.Job.Status = scheduledAt > DateTimeOffset.UtcNow ? "Scheduled" : "Queued";
            SaveLocked();
        }
        Notify(); Tick(null);
    }

    public void SetSpeedLimit(string id, long? bytesPerSecond)
    {
        if (bytesPerSecond is <= 0) throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
        lock (gate)
        {
            ThrowIfDisposed();
            var w = Find(id);
            lock (w.RateGate) { w.SpeedLimit = bytesPerSecond; w.NextWriteAt = Stopwatch.GetTimestamp(); w.RateVersion++; }
            SaveLocked();
        }
    }

    public void Remove(string id) => Remove(id, deleteFile: false);

    /// <summary>Removes the job and its partial data. With <paramref name="deleteFile"/> also deletes the job's own saved file
    /// (exactly <c>FilePath</c>, never a directory or anything else). A missing or locked file never blocks the removal; it is reported.</summary>
    public RemoveResult Remove(string id, bool deleteFile)
    {
        Work w; string path;
        lock (gate)
        {
            ThrowIfDisposed(); w = Find(id); w.Removed = true;
            path = w.Job.FilePath;
            w.Cancellation?.Cancel(); jobs.Remove(w); SaveLocked();
            if (w.Runner == null) DeletePartial(w);
        }
        Notify();
        if (!deleteFile) return new RemoveResult(true, FileDeleteOutcome.NotRequested, null);
        return new RemoveResult(true, DeleteOwnFile(path, out var problem), problem);
    }

    private static FileDeleteOutcome DeleteOwnFile(string path, out string? problem)
    {
        problem = null;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) { problem = "The saved location is not a complete path."; return FileDeleteOutcome.Failed; }
            if (!File.Exists(path)) return FileDeleteOutcome.Missing;
            File.Delete(path);
            return FileDeleteOutcome.Deleted;
        }
        catch (IOException ex) { problem = "The file is in use or locked: " + ex.Message; return FileDeleteOutcome.Failed; }
        catch (UnauthorizedAccessException ex) { problem = "Access to the file was denied: " + ex.Message; return FileDeleteOutcome.Failed; }
    }

    /// <summary>Renames and/or moves a completed download's file and updates the job path (persisted). Never overwrites.</summary>
    public void MoveFile(string id, string newPath)
    {
        if (!Path.IsPathFullyQualified(newPath)) throw new ArgumentException("Choose a complete file path.", nameof(newPath));
        newPath = Path.GetFullPath(newPath);
        var name = Path.GetFileName(newPath);
        if (name.Length == 0 || DownloadFileNames.Sanitize(name) != name) throw new ArgumentException("Use a file name without invalid characters.", nameof(newPath));
        lock (gate)
        {
            ThrowIfDisposed();
            var w = Find(id);
            if (w.Job.Status != "Completed") throw new InvalidOperationException("Only a completed download can be moved or renamed.");
            var source = w.Job.FilePath;
            if (!File.Exists(source)) throw new FileNotFoundException("The file is no longer in its saved location.", source);
            if (string.Equals(Path.GetFullPath(source), newPath, StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(newPath) || Directory.Exists(newPath)) throw new IOException("A file with that name already exists in the destination.");
            Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
            File.Move(source, newPath, false);
            w.Job.FilePath = newPath; w.Job.FileName = name;
            SaveLocked();
        }
        Notify();
    }

    /// <summary>Creates the folder when missing and proves it is writable (a temporary file is created and deleted). Returns the normalised full path;
    /// throws <see cref="ArgumentException"/> for a path that is not complete and <see cref="IOException"/> with a readable message otherwise.</summary>
    public static string EnsureWritableFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder)) throw new ArgumentException("Choose a complete folder path.", nameof(folder));
        var target = Path.GetFullPath(folder.Trim());
        if (target.Length > Path.GetPathRoot(target)!.Length) target = target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        try
        {
            Directory.CreateDirectory(target);
            var probe = Path.Combine(target, ".neodm-write-test-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        }
        catch (UnauthorizedAccessException ex) { throw new IOException("NeoDM cannot write to that folder: " + ex.Message, ex); }
        catch (DirectoryNotFoundException ex) { throw new IOException("That folder or drive is not available: " + ex.Message, ex); }
        catch (IOException ex) { throw new IOException("That folder cannot be used: " + ex.Message, ex); }
        catch (NotSupportedException ex) { throw new IOException("That folder cannot be used: " + ex.Message, ex); }
        return target;
    }

    /// <summary>Changes the folder this download is saved to and returns the new full path (the job's file name is kept; a unique name is chosen if the
    /// folder already has one, nothing is ever overwritten). A completed job's finished file is moved (only that file). An unfinished job keeps its partial
    /// data in the state folder (<c>{id}.parts</c>, addressed by PartId), so no partial file has to move: the job's target is switched and the folder is
    /// checked now (created if missing, write test) so a bad destination fails here, with the job unchanged, and not at the end of the transfer.
    /// A running transfer keeps running and publishes into the new folder. Same folder = no-op.</summary>
    public string MoveTo(string id, string newDirectory)
    {
        if (string.IsNullOrWhiteSpace(newDirectory) || !Path.IsPathFullyQualified(newDirectory)) throw new ArgumentException("Choose a complete folder path.", nameof(newDirectory));
        var target = Path.GetFullPath(newDirectory);
        if (target.Length > Path.GetPathRoot(target)!.Length) target = target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string result;
        lock (gate)
        {
            ThrowIfDisposed();
            var w = Find(id);
            var currentPath = w.Job.FilePath;
            var currentDir = Path.GetDirectoryName(Path.GetFullPath(currentPath)) ?? "";
            if (string.Equals(currentDir.TrimEnd(Path.DirectorySeparatorChar), target.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return currentPath;
            bool done = w.Job.Status == "Completed";
            if (done && !File.Exists(currentPath)) throw new FileNotFoundException("The file is no longer in its saved location.", currentPath);
            EnsureWritableFolder(target);
            var stem = Path.GetFileNameWithoutExtension(w.Job.FileName); var extension = Path.GetExtension(w.Job.FileName);
            string? name = null;
            for (var i = 0; i < 10000 && name is null; i++)
            {
                var candidate = i == 0 ? w.Job.FileName : stem + " (" + i + ")" + extension; var full = Path.Combine(target, candidate);
                if (!File.Exists(full) && !Directory.Exists(full) && !jobs.Any(o => o != w && o.Job.FilePath.Equals(full, StringComparison.OrdinalIgnoreCase))) name = candidate;
            }
            if (name is null) throw new IOException("Unable to reserve a destination filename.");
            result = Path.Combine(target, name); var oldName = w.Job.FileName;
            if (done) File.Move(currentPath, result, false);
            w.Job.FilePath = result; w.Job.FileName = name;
            try { SaveLocked(); }
            catch { w.Job.FilePath = currentPath; w.Job.FileName = oldName; if (done) { try { File.Move(result, currentPath, false); } catch (IOException) { } } throw; }
        }
        Notify();
        return result;
    }

    /// <summary>Replaces the address of an unfinished job (expired links). Progress is kept when the new address serves the same content
    /// (same size and validators); otherwise the transfer restarts cleanly. The job ends up Paused; call <see cref="Resume"/>.</summary>
    public void UpdateUrl(string id, string newUrl)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var w = Find(id);
            if (w.Job.Status == "Completed") throw new InvalidOperationException("A completed download has no address to refresh.");
            var updated = w.Request with { Url = newUrl };
            ValidateRequest(updated);
            w.Job.Status = "Paused"; w.Job.SpeedBytesPerSecond = 0;
            w.Cancellation?.Cancel();
            w.Request = updated; w.Job.Url = newUrl; w.ResolvedUrl = null; w.Job.Error = null; ClearErrorCode(w.Job);
            SaveLocked();
        }
        Notify();
    }

    /// <summary>Starts a finished, failed or stopped job over from zero. A previously saved file is left untouched; the new copy gets a unique name.</summary>
    public void Redownload(string id)
    {
        Task? runner;
        lock (gate)
        {
            ThrowIfDisposed();
            var w = Find(id);
            runner = w.Runner;
            w.Job.Status = "Paused"; w.Job.SpeedBytesPerSecond = 0;
            w.Cancellation?.Cancel();
        }
        try { runner?.Wait(TimeSpan.FromSeconds(10)); } catch (AggregateException) { }
        lock (gate)
        {
            ThrowIfDisposed();
            var w = Find(id);
            ResetParts(w);
            w.Job.Error = null; ClearErrorCode(w.Job); w.Job.ScheduledAt = null; w.Job.Status = "Queued";
            SaveLocked();
        }
        Notify(); Tick(null);
    }

    private void Tick(object? ignored)
    {
        bool notify = false;
        lock (gate)
        {
            if (disposed) return;
            var now = DateTimeOffset.UtcNow;
            foreach (var w in jobs.Where(w => w.Runner != null))
            {
                var elapsed = Stopwatch.GetElapsedTime(w.SampleAt).TotalSeconds;
                if (elapsed >= 0.5)
                {
                    var sample = Math.Max(0, (w.Job.DownloadedBytes - w.SampleBytes) / elapsed);
                    var alpha = 1 - Math.Exp(-elapsed / 2.0);
                    w.Job.SpeedBytesPerSecond = w.Job.Status != "Downloading" ? 0 :
                        !w.HasSpeedSample ? sample : w.Job.SpeedBytesPerSecond + alpha * (sample - w.Job.SpeedBytesPerSecond);
                    w.HasSpeedSample = true;
                    w.SampleAt = Stopwatch.GetTimestamp(); w.SampleBytes = w.Job.DownloadedBytes;
                    notify = true;
                }
            }
            var active = activeJobs.Count;
            foreach (var w in jobs)
            {
                if (active >= options.MaxConcurrentDownloads) break;
                if (w.Runner != null || w.Job.Status is not ("Queued" or "Scheduled") || w.Job.ScheduledAt > now) continue;
                w.Job.Status = "Downloading"; w.Job.Error = null; ClearErrorCode(w.Job);
                w.Cancellation = new CancellationTokenSource();
                w.SampleAt = Stopwatch.GetTimestamp(); w.SampleBytes = w.Job.DownloadedBytes;
                w.HasSpeedSample = false; w.Job.SpeedBytesPerSecond = 0; w.Job.ResumeSupported = null;
                lock (w.RateGate) { w.NextWriteAt = Stopwatch.GetTimestamp(); w.RateVersion++; }
                activeJobs.Add(w);
                var token = w.Cancellation.Token;
                w.Runner = Task.Run(() => RunAsync(w, token));
                active++; notify = true;
            }
            if (now - lastSave >= TimeSpan.FromSeconds(1))
            {
                try { SaveLocked(); }
                catch (Exception ex) { LastPersistenceError = ex.Message; }
            }
        }
        if (notify) Notify();
    }

    private async Task RunAsync(Work w, CancellationToken token)
    {
        try
        {
            var memory = MemoryOf(w.Request.Url);
            lock (gate)
            {
                w.Ladder = new LadderState(memory.Working ?? default);
                w.Trail.Clear(); w.Job.Attempts = null;
                w.ConnLimit = Math.Min(w.ConnLimit > 0 ? w.ConnLimit : options.MaxSegments, memory.MaxConnections ?? int.MaxValue);
            }
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    Probe probe;
                    lock (gate) { w.Probing = true; w.Job.ResumeSupported = null; }
                    try { probe = await ProbeAsync(w, token).ConfigureAwait(false); }
                    finally { lock (gate) w.Probing = false; }
                    lock (gate) w.Job.ResumeSupported = probe.Ranges;
                    if (probe.Description is not null) SettleName(w, probe.Description, null);
                    if (probe.Open is not null)
                    {
                        // The first answer is already the whole file (the server ignores Range, or a plain request was the one that worked): it is the single connection.
                        using (probe.Open) { ResetParts(w); await DownloadStreamingAsync(w, token, probe.Open).ConfigureAwait(false); }
                    }
                    else if (probe.Ranges)
                    {
                        PrepareSegments(w, probe);
                        try { await DownloadSegmentsAsync(w, probe, token).ConfigureAwait(false); }
                        catch (RangeRejectedException)
                        {
                            lock (gate) w.Job.ResumeSupported = false;
                            ResetParts(w);
                            await DownloadStreamingAsync(w, token).ConfigureAwait(false);
                        }
                    }
                    else { ResetParts(w); await DownloadStreamingAsync(w, token).ConfigureAwait(false); }
                    token.ThrowIfCancellationRequested();
                    await PublishAsync(w, token).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex) when (!token.IsCancellationRequested && IsRetryable(ex) && attempt < options.RetryCount)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(4000, 250 * (1 << attempt))), token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock (gate)
            {
                if (!w.Removed && w.Job.Status == "Downloading")
                {
                    w.Job.Status = "Failed"; w.Job.Error = ex.Message; w.Request = w.Request with { Cookie = null };
                    if (ex is HttpRequestException { StatusCode: { } failedStatus })
                    {
                        w.Job.ErrorCode = CompatibilityLadder.HttpStatusError; w.Job.ErrorStatus = (int)failedStatus;
                        if (w.Job.Attempts is { Length: > 0 } trail) w.Job.Error = ex.Message.TrimEnd('.') + ". " + trail;
                    }
                    if (ex is WrongContentException wrong) { w.Job.ErrorCode = ContentGuard.WrongContent; w.Job.ErrorReceivedBytes = wrong.ReceivedBytes; w.Job.ErrorExpectedBytes = wrong.ExpectedBytes; w.Job.ErrorRedirected = wrong.Redirected; ResetParts(w); }
                }
            }
        }
        finally
        {
            lock (gate)
            {
                if (w.Job.Status == "Downloading") w.Job.Status = "Paused";
                w.Job.SpeedBytesPerSecond = 0;
                w.Cancellation?.Dispose(); w.Cancellation = null; w.Runner = null; activeJobs.Remove(w);
                if (w.Removed) DeletePartial(w);
                try { SaveLocked(); } catch (Exception ex) { LastPersistenceError = ex.Message; }
            }
            Notify();
        }
    }

    private static ExpectedContent ExpectedFor(Work w) => new(w.Request.ExpectedSize, w.Request.ExpectedMime, w.NameWasOpen ? null : w.Job.FileName); // an open (automatic) name carries no information about the content

    private static string? AnnouncedName(HttpResponseMessage response) => FileNameResolver.FromContentDisposition(RawHeader(response, "Content-Disposition"));

    /// <summary>Refuses a reply that is a web page (or far smaller than the size the browser reported) when a file was asked for.</summary>
    private static void GuardHeaders(Work w, HttpResponseMessage response)
    {
        long? declared = response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength;
        if (ContentGuard.CheckHeaders(ExpectedFor(w), RawHeader(response, "Content-Type"), declared, AnnouncedName(response)) is { } wrong) throw wrong;
    }

    private static void GuardHead(Work w, ReadOnlySpan<byte> head, long? length, HttpResponseMessage response)
    {
        if (ContentGuard.CheckHead(ExpectedFor(w), head, length, AnnouncedName(response)) is { } wrong) throw wrong;
    }

    // A refusal in the 400s (except "timed out" and "slow down") will not change by asking again the same way; the compatibility ladder already tried other shapes.
    private static bool IsRetryable(Exception ex) => ex is HttpRequestException { StatusCode: { } status } ? (int)status is >= 500 or 408 or 429
        : ex is (IOException and not InsufficientDiskSpaceException) or HttpRequestException or OperationCanceledException;

    private HttpRequestMessage Request(Work w, RequestVariant variant = default) => BuildMessage(HttpMethod.Get, w.Request, variant);

    /// <summary>The request every transfer starts from: the identity (browser's own or the engine's default), Accept-Language, Referer and Cookie of the job.</summary>
    private HttpRequestMessage BuildMessage(HttpMethod method, DownloadRequest request, RequestVariant variant)
    {
        var message = new HttpRequestMessage(method, request.Url);
        message.Headers.AcceptEncoding.ParseAdd("identity");
        message.Headers.TryAddWithoutValidation("User-Agent", RequestIdentity.UserAgentFor(options.Identity, options.CustomUserAgent, request.UserAgent, variant.BrowserIdentity));
        message.Headers.TryAddWithoutValidation("Accept", request.Accept ?? RequestIdentity.DefaultAccept);
        message.Headers.TryAddWithoutValidation("Accept-Language", request.AcceptLanguage ?? options.AcceptLanguage ?? RequestIdentity.DefaultAcceptLanguage());
        switch (variant.Referer)
        {
            case RefererMode.Original when request.Referer != null: message.Headers.Referrer = new Uri(request.Referer); break;
            case RefererMode.SiteRoot: message.Headers.Referrer = new Uri(new Uri(request.Url).GetLeftPart(UriPartial.Authority) + "/"); break;
        }
        if (request.Cookie != null) message.Headers.Add("Cookie", request.Cookie);
        return message;
    }

    private HostMemory MemoryOf(string url) => hosts.GetOrAdd(new Uri(url).Authority, _ => new HostMemory());

    private void AddTrail(Work w, string text)
    {
        lock (gate) { w.Trail.Add(text); w.Job.Attempts = string.Join(", ", w.Trail); }
    }

    /// <summary>Sends the first request of a transfer (the ranged probe, or a plain GET) and, while the answer is a refusal that a different request shape often cures,
    /// tries the next shape of the compatibility ladder: without Range, browser identity, the site's root as referer, no referer. At most
    /// <see cref="CompatibilityLadder.MaxAttempts"/> attempts for a job run, a short pause between them (Retry-After honoured up to ten seconds). The shape that worked is
    /// remembered for the job and, in memory, for the site. The address and the route of the connection are never changed.</summary>
    private async Task<(HttpResponseMessage Response, RequestVariant Variant)> SendFirstAsync(Work w, bool withRange, CancellationToken token)
    {
        var memory = MemoryOf(w.Request.Url);
        var url = new Uri(w.Request.Url);
        RequestVariant variant; LadderState ladder;
        lock (gate) { ladder = w.Ladder; variant = ladder.Current; }
        if (!withRange && !variant.NoRange) variant = variant with { NoRange = true };
        var mentioned = variant; string? forced = null;
        var baseline = default(RequestVariant) with { NoRange = !withRange };
        var remembered = variant != baseline && ladder.Used == 0 ? variant : (RequestVariant?)null; // started from what worked for this site earlier
        while (true)
        {
            int number; lock (gate) number = ++ladder.Used;
            using var request = Request(w, variant);
            if (!variant.NoRange) request.Headers.Range = new RangeHeaderValue(0, 0);
            var response = await SendAsync(request, token, ExpectedFor(w)).ConfigureAwait(false);
            var change = forced ?? CompatibilityLadder.Describe(number == 1 ? baseline : mentioned, variant); forced = null;
            AddTrail(w, CompatibilityLadder.Label(number, change, ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            mentioned = variant;
            var challenge = response.Headers.WwwAuthenticate.Count > 0;
            RequestVariant? next = null;
            if (CompatibilityLadder.IsLadderStatus(response.StatusCode, challenge) && number < CompatibilityLadder.MaxAttempts)
            {
                // What worked for the site earlier does not any more: forget it and climb from the plain request.
                if (remembered is not null && variant == remembered) { memory.Working = null; lock (gate) ladder.Current = baseline; remembered = null; next = baseline; forced = "plain request"; }
                else next = CompatibilityLadder.Next(variant, options.Identity, w.Request.Referer, url);
            }
            if (next is null)
            {
                lock (gate) ladder.Current = variant; // a repeat of this first request (transient-error retry) starts from the shape reached, it does not climb again
                // Only a shape that was reached by a refusal (or already remembered) is kept; a plain success remembers nothing.
                if (response.IsSuccessStatusCode && variant != baseline) memory.Working = variant;
                return (response, variant);
            }
            var delay = CompatibilityLadder.Delay(response.StatusCode, response.Headers.RetryAfter, DateTimeOffset.UtcNow, options.LadderPause, options.MaxRetryAfter);
            response.Dispose();
            variant = next.Value;
            await Task.Delay(delay, token).ConfigureAwait(false);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token, ExpectedContent? expected = null)
    {
        var current = request;
        try
        {
            for (var hop = 0; ; hop++)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(options.RequestTimeout);
                var response = await client.SendAsync(current, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect) || response.Headers.Location == null)
                    return response;
                var next = new Uri(current.RequestUri!, response.Headers.Location);
                response.Dispose();
                if (hop >= 9 || next.Scheme is not ("http" or "https")) throw new HttpRequestException("Invalid redirect or redirect limit exceeded.");
                RedirectRules.EnsureNotDowngrade(current.RequestUri!, next);
                if (expected is not null && ContentGuard.CheckRedirect(expected, request.RequestUri!, current.RequestUri!, next) is { } toPage) throw toPage; // a file was asked for and the site sends the browser to one of its own pages
                var redirected = new HttpRequestMessage(request.Method, next);
                var crossOrigin = !RedirectRules.SameOrigin(next, request.RequestUri!);
                foreach (var header in request.Headers)
                {
                    // Cookies captured by Chrome belong to the original origin only; the page address (Referer) is not handed to another origin either.
                    if (crossOrigin && (header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase))) continue;
                    redirected.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                if (current != request) current.Dispose();
                current = redirected;
            }
        }
        finally { if (current != request) current.Dispose(); }
    }

    private async Task<Probe> ProbeAsync(Work w, CancellationToken token)
    {
        var (response, _) = await SendFirstAsync(w, true, token).ConfigureAwait(false);
        var keep = false;
        try
        {
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) return new Probe(false, 0, null, null, response.RequestMessage?.RequestUri?.AbsoluteUri ?? w.Request.Url, Describe(response, Array.Empty<byte>()));
            response.EnsureSuccessStatusCode();
            GuardHeaders(w, response);
            // A plain GET answered 200 (it had no Range, or the server ignores Range): that answer is the whole file and becomes the single connection; nothing is requested a second time.
            if (response.StatusCode == HttpStatusCode.OK)
            {
                keep = true;
                return new Probe(false, response.Content.Headers.ContentLength ?? 0, null, null, response.RequestMessage?.RequestUri?.AbsoluteUri ?? w.Request.Url, Describe(response, Array.Empty<byte>()), response);
            }
            return await ReadProbeAsync(w, response, token).ConfigureAwait(false);
        }
        finally { if (!keep) response.Dispose(); }
    }

    private async Task<Probe> ReadProbeAsync(Work w, HttpResponseMessage response, CancellationToken token)
    {
        var range = response.Content.Headers.ContentRange;
        var etag = response.Headers.ETag is { IsWeak: false } tag ? tag.ToString() : null;
        var modified = response.Content.Headers.LastModified;
        // Use a conservative sixty-second Date/Last-Modified separation to avoid
        // clock skew (stricter than RFC 9110 8.8.2.2). Never use a weak ETag.
        if (etag == null && (response.Headers.ETag != null || response.Headers.Date == null ||
            modified == null || response.Headers.Date.Value - modified.Value < TimeSpan.FromSeconds(60))) modified = null;
        var length = range?.Length ?? response.Content.Headers.ContentLength ?? 0;
        var valid = response.StatusCode == HttpStatusCode.PartialContent && range?.Unit == "bytes" &&
            range.From == 0 && range.To == 0 && range.Length > 0 &&
            (response.Content.Headers.ContentLength == null || response.Content.Headers.ContentLength == 1) &&
            response.Content.Headers.ContentEncoding.All(e => e.Equals("identity", StringComparison.OrdinalIgnoreCase)) &&
            (etag != null || modified != null);
        if (valid)
        {
            using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var buffer = new byte[2];
            var count = await ReadAsync(stream, buffer.AsMemory(0, 1), token).ConfigureAwait(false);
            var extra = await ReadAsync(stream, buffer.AsMemory(1, 1), token).ConfigureAwait(false);
            valid = count == 1 && extra == 0;
        }
        return new Probe(valid, length, etag, modified, response.RequestMessage?.RequestUri?.AbsoluteUri ?? w.Request.Url, Describe(response, Array.Empty<byte>()));
    }

    private void PrepareSegments(Work w, Probe probe)
    {
        lock (gate)
        {
            if (w.Total != probe.Length || w.ETag != probe.ETag || w.Modified != probe.Modified || (w.ResolvedUrl != null && w.ResolvedUrl != probe.ResolvedUrl) || !ValidSegments(w)) ResetParts(w);
            Directory.CreateDirectory(PartDirectory(w));
            if (w.Segments.Count == 0)
            {
                var count = (int)Math.Min(LimitOf(w), Math.Max(1, probe.Length / options.MinimumSegmentSize));
                var size = probe.Length / count;
                for (var i = 0; i < count; i++) w.Segments.Add(new Segment { PartId = i, Start = i * size, Length = i == count - 1 ? probe.Length - i * size : size });
            }
            w.Total = probe.Length; w.ETag = probe.ETag; w.Modified = probe.Modified; w.ResolvedUrl = probe.ResolvedUrl;
            EnsureDiskSpace(w, probe.Length, w.Segments.Sum(x => x.Done));
            for (var i = 0; i < w.Segments.Count; i++)
            {
                var segment = w.Segments[i];
                var path = PartFile(w, segment.PartId!.Value);
                var length = File.Exists(path) ? new FileInfo(path).Length : 0;
                segment.Done = Math.Min(segment.Done, Math.Min(length, segment.Length));
                if (File.Exists(path)) { using var file = new FileStream(path, FileMode.Open, FileAccess.Write); file.SetLength(segment.Done); }
            }
            UpdateProgressLocked(w); SaveLocked();
        }
    }

    private static bool ValidSegments(Work w)
    {
        if (w.Segments.Count > MaximumShards || w.Segments.Any(s => s.PartId is null or < 0 or >= MaximumShards) ||
            w.Segments.Select(s => s.PartId).Distinct().Count() != w.Segments.Count) return false;
        long offset = 0;
        foreach (var s in w.Segments)
        {
            if (s.Start != offset || s.Length <= 0 || s.Done < 0 || s.Done > s.Length || s.Length > w.Total - offset) return false;
            offset += s.Length;
        }
        return w.Segments.Count > 0 && offset == w.Total;
    }

    private async Task SegmentAsync(Work w, Segment segment, Probe probe, CancellationToken token)
    {
        if (segment.Done == segment.Length) return;
        var start = segment.Start + segment.Done;
        var end = segment.Start + segment.Length - 1;
        using var request = Request(w, CurrentVariant(w) with { NoRange = false });
        request.Headers.Range = new RangeHeaderValue(start, end);
        request.Headers.IfRange = probe.ETag != null ? new RangeConditionHeaderValue(probe.ETag) : new RangeConditionHeaderValue(probe.Modified!.Value);
        using var response = await SendAsync(request, token, ExpectedFor(w)).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) throw new RangeRejectedException();
            // The first request worked, so a refusal now is most likely a limit on connections for this link: the coordinator lowers the number of connections and retries.
            if (CompatibilityLadder.IsConnectionLimitStatus(response.StatusCode)) throw new SegmentRefusedException(response.StatusCode, CompatibilityLadder.Delay(response.StatusCode, response.Headers.RetryAfter, DateTimeOffset.UtcNow, options.LadderPause, options.MaxRetryAfter));
            response.EnsureSuccessStatusCode();
        }
        var range = response.Content.Headers.ContentRange;
        if (response.RequestMessage?.RequestUri?.AbsoluteUri != probe.ResolvedUrl || range?.Unit != "bytes" || range.From != start || range.To != end || range.Length != probe.Length ||
            (response.Content.Headers.ContentLength != null && response.Content.Headers.ContentLength != end - start + 1) ||
            response.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase)) ||
            (probe.ETag != null ? response.Headers.ETag?.ToString() != probe.ETag : response.Content.Headers.LastModified != probe.Modified))
            throw new RangeRejectedException();
        using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var output = new FileStream(PartFile(w, segment.PartId!.Value), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 1, FileOptions.Asynchronous);
        output.SetLength(segment.Done); output.Position = segment.Done;
        var buffer = new byte[65536];
        while (segment.Done < segment.Length)
        {
            var read = await ReadAsync(input, buffer.AsMemory(0, (int)Math.Min(buffer.Length, segment.Length - segment.Done)), token).ConfigureAwait(false);
            if (read == 0) throw new IOException("The server truncated a range response.");
            await ThrottleAsync(w, read, token).ConfigureAwait(false);
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            lock (gate) { segment.Done += read; UpdateProgressLocked(w); }
        }
        if (await ReadAsync(input, buffer.AsMemory(0, 1), token).ConfigureAwait(false) != 0) throw new RangeRejectedException();
        await output.FlushAsync(token).ConfigureAwait(false);
    }

    private RequestVariant CurrentVariant(Work w) { lock (gate) return w.Ladder.Current; }

    /// <summary>Connections this job may use now: the engine's limit, lowered when the server refused parallel connections.</summary>
    private int LimitOf(Work w) => w.ConnLimit > 0 ? Math.Min(w.ConnLimit, options.MaxSegments) : options.MaxSegments;

    private async Task DownloadStreamingAsync(Work w, CancellationToken token, HttpResponseMessage? open = null)
    {
        lock (gate) w.Streaming = true;
        try { await DownloadStreamingBodyAsync(w, token, open).ConfigureAwait(false); }
        finally { lock (gate) { w.Streaming = false; foreach (var s in w.Segments) s.Active = false; } }
    }

    private async Task DownloadStreamingBodyAsync(Work w, CancellationToken token, HttpResponseMessage? open)
    {
        // The first answer of the run is used as it is; otherwise a plain GET goes through the same compatibility ladder as the probe.
        var response = open ?? (await SendFirstAsync(w, false, token).ConfigureAwait(false)).Response;
        try { await StreamAsync(w, response, token).ConfigureAwait(false); }
        finally { if (open is null) response.Dispose(); }
    }

    private async Task StreamAsync(Work w, HttpResponseMessage response, CancellationToken token)
    {
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == HttpStatusCode.PartialContent) throw new IOException("Unsolicited partial response to full download.");
        if (response.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase))) throw new IOException("Server ignored identity content encoding.");
        GuardHeaders(w, response);
        Directory.CreateDirectory(PartDirectory(w));
        var length = response.Content.Headers.ContentLength;
        EnsureDiskSpace(w, length, 0);
        var segment = new Segment { PartId = 0, Length = length ?? long.MaxValue, Active = true };
        long nextSpaceCheck = options.SpaceCheckIntervalBytes;
        lock (gate)
        {
            w.Segments = new List<Segment> { segment }; w.Total = length ?? 0;
            w.ETag = null; w.Modified = null; UpdateProgressLocked(w);
        }
        using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var output = new FileStream(PartFile(w, 0), FileMode.Create, FileAccess.Write, FileShare.Read, 1, FileOptions.Asynchronous);
        var buffer = new byte[65536];
        bool first = true;
        while (true)
        {
            var read = await ReadAsync(input, buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            if (first) { first = false; GuardHead(w, buffer.AsSpan(0, read), length, response); }
            if (length != null && read > length.Value - segment.Done) throw new IOException("Server exceeded declared content length.");
            await ThrottleAsync(w, read, token).ConfigureAwait(false);
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            lock (gate) { segment.Done += read; UpdateProgressLocked(w); }
            if (length == null && segment.Done >= nextSpaceCheck) { nextSpaceCheck = segment.Done + options.SpaceCheckIntervalBytes; EnsureDiskSpace(w, null, segment.Done); } // no declared length: keep the free-space floor
        }
        if (length != null && segment.Done != length) throw new IOException("The server truncated the download.");
        await output.FlushAsync(token).ConfigureAwait(false);
        lock (gate) { w.Total = segment.Length = segment.Done; UpdateProgressLocked(w); }
    }

    /// <summary>Free space kept for streams without a declared length, and how often (bytes written) it is looked at.</summary>
    public const long UnknownLengthFloor = 100L * 1024 * 1024;

    private long? Free(string path)
    {
        try
        {
            if (options.FreeSpace is { } custom) return custom(path);
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return null; // network shares: unknown
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    /// <summary>Fails the transfer early with a clear message when the declared size (plus the staging copy made at the end) does not fit. Without a declared size a floor of free space is kept.</summary>
    private void EnsureDiskSpace(Work w, long? total, long alreadyDownloaded)
    {
        var parts = PartDirectory(w); var staging = Path.Combine(downloads, ".ndm-staging"); var target = Path.GetDirectoryName(w.Job.FilePath) ?? downloads;
        var need = new Dictionary<string, (long Bytes, string Path)>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, long bytes)
        {
            string root; try { root = Path.GetPathRoot(Path.GetFullPath(path)) ?? path; } catch (ArgumentException) { return; }
            need[root] = need.TryGetValue(root, out var e) ? (e.Bytes + bytes, e.Path) : (bytes, path);
        }
        if (total is { } known && known > 0)
        {
            Add(parts, Math.Max(0, known - alreadyDownloaded));   // the part files still to be written
            Add(staging, known);                                  // the joined copy
            Add(target, Path.GetPathRoot(Path.GetFullPath(target)) == Path.GetPathRoot(Path.GetFullPath(staging)) ? 0 : known); // a move to another drive copies again
        }
        else { Add(parts, UnknownLengthFloor); }
        foreach (var (root, (bytes, path)) in need)
        {
            if (bytes <= 0 || Free(path) is not { } free) continue;
            var required = bytes; // with an unknown length this is the free-space floor itself
            if (free < required) throw new InsufficientDiskSpaceException($"Not enough free disk space on {root.TrimEnd('\\')}: this download needs about {FormatSize(required)} and {FormatSize(Math.Max(0, free))} is free.");
        }
    }
    private static string FormatSize(long bytes) => bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB" : Math.Max(1, bytes >> 20) + " MB";

    private async Task<int> ReadAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(options.RequestTimeout);
        return await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
    }

    private async Task ThrottleAsync(Work w, int bytes, CancellationToken token)
    {
        double target = 0;
        int version = -1;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            double seconds;
            lock (w.RateGate)
            {
                if (w.SpeedLimit == null) return;
                var now = Stopwatch.GetTimestamp();
                if (version != w.RateVersion)
                {
                    version = w.RateVersion;
                    target = w.NextWriteAt = Math.Max(w.NextWriteAt, now) + bytes * (double)Stopwatch.Frequency / w.SpeedLimit.Value;
                }
                seconds = (target - now) / Stopwatch.Frequency;
            }
            if (seconds <= 0) return;
            // Responsive to live rate changes, even at one byte/second.
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(seconds, 0.25)), token).ConfigureAwait(false);
        }
    }

    private async Task PublishAsync(Work w, CancellationToken token)
    {
        var staging = StagingFile(w);
        try
        {
            await using (var output = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                for (var i = 0; i < w.Segments.Count; i++)
                {
                    var s = w.Segments[i];
                    if (s.Done != s.Length) throw new IOException("Incomplete segment cannot be published.");
                    await using var input = new FileStream(PartFile(w, s.PartId!.Value), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (input.Length != s.Length) throw new IOException("Partial file length changed.");
                    await input.CopyToAsync(output, 65536, token).ConfigureAwait(false);
                }
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(true);
            }
            var stagedHead = ReadHead(staging);
            if (w.Request.AutoName || ExpectedFor(w).ExpectsFile)
            {
                if (ContentGuard.CheckHead(ExpectedFor(w), stagedHead, new FileInfo(staging).Length, null) is { } wrong) throw wrong; // never published under the requested name
            }
            if (w.Request.AutoName) SettleName(w, null, stagedHead);
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                if (w.Removed || w.Job.Status != "Downloading") throw new OperationCanceledException(token);
                // Never overwrite a file that appeared after the name was reserved.
                var targetDir = Path.GetDirectoryName(w.Job.FilePath) ?? downloads;
                Directory.CreateDirectory(targetDir);
                for (var collision = 0; ; collision++)
                {
                    if (collision >= 10000) throw new IOException("Unable to reserve a destination filename.");
                    var name = AvailableName(w.Job.FileName, w, targetDir);
                    try { File.Move(staging, Path.Combine(targetDir, name), false); w.Job.FileName = name; w.Job.FilePath = Path.Combine(targetDir, name); if (options.MarkOfTheWeb) MarkOfTheWeb.TryApply(w.Job.FilePath, w.Request.Url, w.Request.Referer); break; }
                    catch (IOException) when (File.Exists(Path.Combine(targetDir, name))) { }
                }
                w.Job.Status = "Completed"; w.Job.Error = null;
                w.Request = w.Request with { Cookie = null }; // the cookie is only needed while transferring
                SaveLocked();
            }
            DeletePartial(w);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    private void ResetParts(Work w)
    {
        lock (gate)
        {
            DeletePartial(w); w.Segments.Clear(); w.Total = 0; w.ETag = null; w.Modified = null; w.ResolvedUrl = null;
            w.Job.TotalBytes = w.Job.DownloadedBytes = 0;
            w.Job.SpeedBytesPerSecond = 0; w.HasSpeedSample = false;
            w.SampleAt = Stopwatch.GetTimestamp(); w.SampleBytes = 0;
        }
    }
    private static void UpdateProgressLocked(Work w) { w.Job.TotalBytes = w.Total; w.Job.DownloadedBytes = w.Segments.Sum(s => s.Done); }
    private string PartDirectory(Work w) => Path.Combine(state, w.Job.Id + ".parts");
    private string PartFile(Work w, int index) => Path.Combine(PartDirectory(w), index + ".part");
    private string StagingFile(Work w) => Path.Combine(downloads, ".ndm-staging", w.Job.Id + ".tmp");
    private void DeletePartial(Work w)
    {
        try { if (Directory.Exists(PartDirectory(w))) Directory.Delete(PartDirectory(w), true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        try { if (File.Exists(StagingFile(w))) File.Delete(StagingFile(w)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string AvailableName(string name, Work? except = null, string? alsoDirectory = null)
    {
        var stem = Path.GetFileNameWithoutExtension(name); var extension = Path.GetExtension(name);
        for (var i = 0; i < 10000; i++)
        {
            var candidate = i == 0 ? name : stem + " (" + i + ")" + extension;
            if (!File.Exists(Path.Combine(downloads, candidate)) && !Directory.Exists(Path.Combine(downloads, candidate)) &&
                (alsoDirectory is null || (!File.Exists(Path.Combine(alsoDirectory, candidate)) && !Directory.Exists(Path.Combine(alsoDirectory, candidate)))) &&
                !jobs.Any(w => w != except && w.Job.FileName.Equals(candidate, StringComparison.OrdinalIgnoreCase))) return candidate;
        }
        throw new IOException("Unable to reserve a destination filename.");
    }
    private Work Find(string id) => jobs.FirstOrDefault(w => w.Job.Id == id) ?? throw new KeyNotFoundException(id);
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
    private static void ValidateRequest(DownloadRequest request)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new ArgumentException("Only absolute HTTP(S) URLs are supported.");
        if (request.Referer != null && (!Uri.TryCreate(request.Referer, UriKind.Absolute, out var referer) || referer.Scheme is not ("http" or "https"))) throw new ArgumentException("Invalid HTTP(S) referer.");
        if (request.UserAgent != null && !RequestIdentity.IsHeaderValue(request.UserAgent, RequestIdentity.MaxUserAgent)) throw new ArgumentException("Invalid User-Agent.");
        if (request.Accept != null && !RequestIdentity.IsHeaderValue(request.Accept, RequestIdentity.MaxAccept)) throw new ArgumentException("Invalid Accept header.");
        if (request.AcceptLanguage != null && !RequestIdentity.IsHeaderValue(request.AcceptLanguage, RequestIdentity.MaxAcceptLanguage)) throw new ArgumentException("Invalid Accept-Language header.");
        if ((request.Cookie?.IndexOfAny(new[] { '\r', '\n' }) ?? -1) >= 0 || (request.Referer?.IndexOfAny(new[] { '\r', '\n' }) ?? -1) >= 0) throw new ArgumentException("Header values cannot contain newlines.");
    }
    private void SaveLocked()
    {
        var records = jobs.Select(w => new SavedWork
        {
            Id = w.Job.Id, Request = w.Request with { Cookie = null }, CookieProtected = w.Request.Cookie is { Length: > 0 } cookie && w.Job.Status is not ("Completed" or "Failed") ? CookieVault.Protect(cookie) : null,
            FileName = w.Job.FileName, Status = w.Job.Status,
            Total = w.Total, ETag = w.ETag, Modified = w.Modified, ResolvedUrl = w.ResolvedUrl, Segments = w.Segments,
            ScheduledAt = w.Job.ScheduledAt, Error = w.Job.Error, ErrorCode = w.Job.ErrorCode, ErrorReceivedBytes = w.Job.ErrorReceivedBytes, ErrorExpectedBytes = w.Job.ErrorExpectedBytes, ErrorRedirected = w.Job.ErrorRedirected, ErrorStatus = w.Job.ErrorStatus, Attempts = w.Job.Attempts, SpeedLimit = w.SpeedLimit,
            FilePath = w.Job.FilePath, AddedAt = w.Job.AddedAt
        }).ToArray();
        var temporary = queuePath + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, records, JsonOptions); file.Flush(true); }
        File.Move(temporary, queuePath, true);
        lastSave = DateTimeOffset.UtcNow; LastPersistenceError = null;
    }
    private void Restore()
    {
        if (!File.Exists(queuePath)) return;
        SavedWork[] records;
        try { records = JsonSerializer.Deserialize<SavedWork[]>(FileLimits.ReadAllTextCapped(queuePath, FileLimits.MaxQueueBytes, "queue.json")) ?? throw new JsonException("Empty queue."); }
        catch (JsonException ex) { throw new InvalidDataException("queue.json is corrupt; preserve or repair it before reopening the queue.", ex); }
        foreach (var r in records)
        {
            if (!Guid.TryParseExact(r.Id, "N", out _) || jobs.Any(w => w.Job.Id == r.Id) || r.Request == null || r.Total < 0 || r.Segments == null || r.Segments.Count > MaximumShards) throw new InvalidDataException("Invalid saved queue entry.");
            // Old queues used ordered index part files. Preserve those ids before any sort/split.
            if (r.Segments.All(s => s.PartId == null))
                for (var i = 0; i < r.Segments.Count; i++) r.Segments[i].PartId = i;
            // Cookie: the protected form wins; a plain cookie from an older queue.json is still accepted and is rewritten protected at the next save.
            var restoredCookie = CookieVault.TryUnprotect(r.CookieProtected) ?? r.Request.Cookie;
            r.Request = r.Request with { Cookie = string.IsNullOrEmpty(restoredCookie) ? null : restoredCookie };
            ValidateRequest(r.Request);
            var name = DownloadFileNames.Sanitize(r.FileName);
            var status = r.Status is "Downloading" or "Queued" ? "Paused" : r.Status;
            if (status is not ("Paused" or "Scheduled" or "Completed" or "Failed")) status = "Paused";
            if (status is "Completed" or "Failed") r.Request = r.Request with { Cookie = null };
            var w = new Work
            {
                NameWasOpen = r.Request.AutoName,
                Request = r.Request, Total = r.Total, ETag = r.ETag, Modified = r.Modified,
                Segments = r.Segments, ResolvedUrl = r.ResolvedUrl, SpeedLimit = r.SpeedLimit is > 0 ? r.SpeedLimit : null,
                Job = new DownloadJob { Id = r.Id, Url = r.Request.Url, FileName = name, FilePath = SavedPath(r.FilePath, name) ?? Path.Combine(downloads, name), Status = status, ScheduledAt = r.ScheduledAt, Error = r.Error, ErrorCode = r.ErrorCode, ErrorReceivedBytes = r.ErrorReceivedBytes, ErrorExpectedBytes = r.ErrorExpectedBytes, ErrorRedirected = r.ErrorRedirected, ErrorStatus = r.ErrorStatus, Attempts = r.Attempts, Referer = r.Request.Referer, AddedAt = r.AddedAt }
            };
            if (status == "Completed") { w.Job.TotalBytes = w.Job.DownloadedBytes = r.Total; }
            else
            {
                if (!ValidSegments(w)) { DeletePartial(w); w.Segments.Clear(); w.Total = 0; }
                UpdateProgressLocked(w);
            }
            jobs.Add(w);
        }
    }
    /// <summary>A saved path is trusted only when it is complete and still ends in the job's own file name.</summary>
    private static string? SavedPath(string? saved, string name) =>
        !string.IsNullOrWhiteSpace(saved) && Path.IsPathFullyQualified(saved) && Path.GetFileName(saved) == name ? saved : null;
    private void Notify()
    {
        var handlers = Changed;
        if (handlers == null) return;
        // Never run application callbacks on a transfer task: an observer may Dispose.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            foreach (Action handler in handlers.GetInvocationList()) { try { handler(); } catch { /* Observers must not stop transfers. */ } }
        });
    }
    public void Dispose()
    {
        Task[] tasks;
        lock (gate)
        {
            if (disposed) return; disposed = true;
            timer.Dispose();
            tasks = activeJobs.Select(w => w.Runner!).ToArray();
            foreach (var w in activeJobs)
            {
                if (w.Job.Status == "Downloading") w.Job.Status = "Paused";
                w.Cancellation?.Cancel();
            }
        }
        Task.WhenAll(tasks).GetAwaiter().GetResult();
        try { lock (gate) SaveLocked(); }
        finally { client.Dispose(); ownership.Dispose(); }
    }

    private sealed record Probe(bool Ranges, long Length, string? ETag, DateTimeOffset? Modified, string ResolvedUrl, NameProbeResult? Description = null, HttpResponseMessage? Open = null);
    private sealed class SegmentRefusedException : IOException
    {
        public HttpStatusCode Status { get; }
        public TimeSpan Delay { get; }
        public SegmentRefusedException(HttpStatusCode status, TimeSpan delay) : base($"The server refused a connection ({(int)status}).") { Status = status; Delay = delay; }
        public HttpRequestException ToHttpException() => new($"Response status code does not indicate success: {(int)Status} ({Status}).", null, Status);
    }
    private sealed class LadderState
    {
        public RequestVariant Current; public int Used;
        public LadderState(RequestVariant start) { Current = start; }
    }
    private sealed class HostMemory { public volatile bool HeadRefused, RangeRefused; public RequestVariant? Working; public int? MaxConnections; }
    private sealed class RangeRejectedException : IOException { public RangeRejectedException() : base("Server rejected or changed a byte range.") { } }
    private sealed class Work
    {
        public DownloadJob Job { get; set; } = new();
        public DownloadRequest Request { get; set; } = new("");
        public long Total { get; set; }
        public string? ETag { get; set; }
        public string? ResolvedUrl { get; set; }
        public DateTimeOffset? Modified { get; set; }
        public List<Segment> Segments { get; set; } = new();
        public long? SpeedLimit { get; set; }
        public object RateGate { get; } = new();
        public double NextWriteAt;
        public int RateVersion;
        public Task? Runner;
        public CancellationTokenSource? Cancellation;
        public bool Removed;
        /// <summary>The name was left open (taken from the address and the server's answer) when the job was added: it says nothing about the content.</summary>
        public bool NameWasOpen;
        public long SampleAt;
        public long SampleBytes;
        public bool HasSpeedSample, Probing, Streaming;
        public LadderState Ladder = new(default);
        public readonly List<string> Trail = new();
        /// <summary>Connections this job may use at most; lowered when the server refuses parallel connections. 0 = the engine's limit.</summary>
        public int ConnLimit;
    }
    private sealed class Segment
    {
        public int? PartId { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public bool Active { get; set; }
        public long Start { get; set; }
        public long Length { get; set; }
        public long Done { get; set; }
    }
    private sealed class SavedWork
    {
        public string Id { get; set; } = "";
        public DownloadRequest? Request { get; set; }
        /// <summary>The job's cookie as a DPAPI blob; the Request in the file never carries it.</summary>
        public string? CookieProtected { get; set; }
        public string FileName { get; set; } = "";
        public string Status { get; set; } = "Paused";
        public long Total { get; set; }
        public string? ETag { get; set; }
        public string? ResolvedUrl { get; set; }
        public DateTimeOffset? Modified { get; set; }
        public List<Segment> Segments { get; set; } = new();
        public DateTimeOffset? ScheduledAt { get; set; }
        public string? Error { get; set; }
        public string? ErrorCode { get; set; }
        public long? ErrorReceivedBytes { get; set; }
        public long? ErrorExpectedBytes { get; set; }
        public bool ErrorRedirected { get; set; }
        public int? ErrorStatus { get; set; }
        public string? Attempts { get; set; }
        public long? SpeedLimit { get; set; }
        public string? FilePath { get; set; }
        public DateTimeOffset? AddedAt { get; set; }
    }
}
