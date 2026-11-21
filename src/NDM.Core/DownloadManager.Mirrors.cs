// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace NDM.Core;

/// <summary>Saved with the job (queue.json). Never carries a cookie.</summary>
internal sealed class MirrorSource
{
    public string Url { get; set; } = "";
    public MirrorOrigin Origin { get; set; }
    public int Priority { get; set; } = 100;
    public string? Geo { get; set; }
    public MirrorState State { get; set; }
    public string? Reason { get; set; }
    /// <summary>TCP connect time (what the chooser shows and Automatic compares).</summary>
    public int? LatencyMs { get; set; }
    /// <summary>Time to the first response of the check (what the Server row shows while this source is in use).</summary>
    public int? ResponseMs { get; set; }
    public string? Address { get; set; }
}

public sealed partial class DownloadManager
{
    /// <summary>Most sources a job keeps besides its own address.</summary>
    public const int MaxMirrors = 8;
    /// <summary>A mirror must beat the original's connect time by this share and by at least this many milliseconds to be preferred automatically.</summary>
    private const double MirrorBetterFactor = 0.7; private const int MirrorBetterMs = 20;
    private static readonly TimeSpan MirrorProbeTimeout = TimeSpan.FromSeconds(6);

    /// <summary>What the first answer said about other sources and checksums (Link, Digest and Repr-Digest headers), plus the one byte the 0-0 probe returned.</summary>
    private sealed record ProbeExtras(IReadOnlyList<string> Links, IReadOnlyList<string> Digests, IReadOnlyList<string> Reprs, byte? FirstByte);

    private static ProbeExtras ExtrasOf(HttpResponseMessage response, byte? firstByte)
    {
        static IReadOnlyList<string> All(HttpResponseMessage r, string name) => r.Headers.TryGetValues(name, out var v) ? v.ToArray() : r.Content.Headers.TryGetValues(name, out var c) ? c.ToArray() : Array.Empty<string>();
        return new ProbeExtras(All(response, "Link"), All(response, "Digest"), All(response, "Repr-Digest"), firstByte);
    }

    private static bool SameOrigin(string a, string b) => Uri.TryCreate(a, UriKind.Absolute, out var x) && Uri.TryCreate(b, UriKind.Absolute, out var y) && RedirectRules.SameOrigin(x, y);

    /// <summary>The request of a transfer or check against a mirror: the job's identity and referer, the cookie only when the mirror is on the same origin as the original address.</summary>
    private HttpRequestMessage MirrorRequest(Work w, string url, RequestVariant variant)
    {
        var request = w.Request with { Url = url, Cookie = SameOrigin(url, w.Request.Url) ? w.Request.Cookie : null };
        return BuildMessage(HttpMethod.Get, request, variant);
    }

    // ---------------------------------------------------------------- learning

    /// <summary>After the first answer: remember Link-header mirrors and a Metalink pointer, remember a Digest, check unchecked mirrors, and pick the source for this run. Costs nothing for a download without mirrors.</summary>
    private async Task<Probe> ApplyMirrorsAsync(Work w, Probe probe, CancellationToken token)
    {
        if (!probe.Ranges || probe.Open is not null || probe.Length <= 0) return probe;
        if (probe.Extras is { } extras) await LearnAsync(w, probe, extras, token).ConfigureAwait(false);
        bool any; lock (gate) any = w.Mirrors.Count > 0;
        if (!any) { SetActiveSource(w, null); return probe; }
        await ValidateCandidatesAsync(w, probe.Length, token).ConfigureAwait(false);
        var chosen = await ChooseSourceAsync(w, token).ConfigureAwait(false);
        SetActiveSource(w, chosen);
        return chosen is null ? probe : probe with { SourceUrl = chosen.Url };
    }

    private async Task LearnAsync(Work w, Probe probe, ProbeExtras extras, CancellationToken token)
    {
        if (extras.Links.Count > 0 && Uri.TryCreate(probe.ResolvedUrl, UriKind.Absolute, out var baseUri))
        {
            var entries = LinkHeader.Parse(extras.Links, baseUri);
            foreach (var e in entries.Where(e => e.Rels.Contains("duplicate"))) AddCandidate(w, e.Url, MirrorOrigin.Header, e.Priority ?? 100, e.Geo);
            var describedBy = entries.FirstOrDefault(e => e.Rels.Contains("describedby") && (e.Type?.Contains("metalink", StringComparison.OrdinalIgnoreCase) ?? false));
            bool tried; lock (gate) { tried = w.MetalinkTried; if (describedBy is not null) w.MetalinkTried = true; }
            if (describedBy is not null && !tried) await ApplyMetalinkUrlAsync(w, describedBy.Url, probe.Length, token).ConfigureAwait(false);
        }
        var digest = DigestHeader.Parse(extras.Digests, extras.Reprs);
        if (digest is not null && extras.FirstByte is { } b && DigestHeader.DescribesOnlyThisByte(digest, b)) digest = null; // the server hashed the one byte it was asked for, not the file
        if (digest is not null) lock (gate) { if (!w.ExpectedTrusted && (w.Expected is null || ExpectedHash.Strength(digest.Algorithm) > ExpectedHash.Strength(w.Expected.Algorithm))) { w.Expected = digest; SaveLocked(); } }
    }

    private bool AddCandidate(Work w, string url, MirrorOrigin origin, int priority, string? geo)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || url.Length > 2048) return false;
        lock (gate)
        {
            var key = uri.AbsoluteUri;
            if (key == new Uri(w.Request.Url).AbsoluteUri || key == w.ResolvedUrl || w.Mirrors.Any(m => m.Url == key) || w.Mirrors.Count >= MaxMirrors) return false;
            w.Mirrors.Add(new MirrorSource { Url = key, Origin = origin, Priority = priority, Geo = geo, State = MirrorState.Candidate });
            return true;
        }
    }

    /// <summary>Reads a Metalink file the server pointed at (or the user gave): sources and hash are taken only when the file's size matches this download's.</summary>
    private async Task<int> ApplyMetalinkUrlAsync(Work w, string url, long total, CancellationToken token)
    {
        string text;
        try
        {
            using var request = MirrorRequest(w, url, default);
            using var response = await SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > Metalink4.MaxBytes) return 0;
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var buffer = new byte[Metalink4.MaxBytes + 1]; var count = 0;
            while (count < buffer.Length) { var read = await stream.ReadAsync(buffer.AsMemory(count), token).ConfigureAwait(false); if (read == 0) break; count += read; }
            if (count > Metalink4.MaxBytes) return 0;
            text = System.Text.Encoding.UTF8.GetString(buffer, 0, count);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException && !token.IsCancellationRequested) { return 0; }
        return ApplyMetalink(w, text, total);
    }

    /// <summary>Takes the sources and the strongest hash of the first file entry whose size matches.</summary>
    private int ApplyMetalink(Work w, string xml, long total)
    {
        var files = Metalink4.Parse(xml); if (files is null) return 0;
        var file = files.FirstOrDefault(f => f.Size is null ? total <= 0 : total <= 0 || f.Size == total);
        if (file is null) return 0;
        var added = 0;
        foreach (var s in file.Sources.OrderBy(s => s.Priority)) if (AddCandidate(w, s.Url, MirrorOrigin.Metalink, s.Priority, s.Location)) added++;
        var hash = file.Hashes.OrderByDescending(h => ExpectedHash.Strength(h.Algorithm)).FirstOrDefault();
        if (hash is not null) lock (gate) { w.Expected = hash; w.ExpectedTrusted = true; }
        lock (gate) SaveLocked();
        return added;
    }

    // ---------------------------------------------------------------- checking

    private async Task ValidateCandidatesAsync(Work w, long total, CancellationToken token)
    {
        MirrorSource[] pending; lock (gate) pending = w.Mirrors.Where(m => m.State == MirrorState.Candidate).ToArray();
        if (pending.Length == 0) return;
        using var limit = new SemaphoreSlim(4);
        await Task.WhenAll(pending.Select(async m =>
        {
            await limit.WaitAsync(token).ConfigureAwait(false);
            try { await ValidateMirrorAsync(w, m, total, token).ConfigureAwait(false); } finally { limit.Release(); }
        })).ConfigureAwait(false);
        lock (gate) { try { SaveLocked(); } catch (Exception ex) { LastPersistenceError = ex.Message; } }
    }

    /// <summary>A mirror is usable when a ranged request for the first byte answers 206 for a file of exactly this size.</summary>
    private async Task ValidateMirrorAsync(Work w, MirrorSource mirror, long total, CancellationToken token)
    {
        string? reason = null; int? ms = null; string? address = null;
        try
        {
            using var request = MirrorRequest(w, mirror.Url, default);
            request.Headers.Range = new RangeHeaderValue(0, 0);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(MirrorProbeTimeout);
            using var response = await SendAsync(request, timeout.Token).ConfigureAwait(false);
            ms = latencies.Get(response);
            var host = response.RequestMessage?.RequestUri?.Host; if (host is not null && remotes.TryGetValue(host, out var ip)) address = ip;
            var range = response.Content.Headers.ContentRange;
            if (response.StatusCode == HttpStatusCode.PartialContent)
                reason = range?.Unit == "bytes" && range.From == 0 && range.To == 0 && range.Length == total ? null : MirrorReasons.Size;
            else if (response.IsSuccessStatusCode) reason = response.Content.Headers.ContentLength is { } len && len != total ? MirrorReasons.Size : MirrorReasons.Ranges;
            else reason = MirrorReasons.Http;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException && !token.IsCancellationRequested) { reason = MirrorReasons.Unreachable; }
        lock (gate)
        {
            mirror.State = reason is null ? MirrorState.Verified : MirrorState.Rejected; mirror.Reason = reason; mirror.ResponseMs = ms ?? mirror.ResponseMs; mirror.Address = address ?? mirror.Address;
        }
    }

    // ---------------------------------------------------------------- choosing

    private bool UsesProxy => options.Proxy is { IsEnabled: true };

    /// <summary>The source for this run: null = the original address. A manual choice wins; otherwise the lowest connect time wins, a mirror only when clearly faster. A source that failed in this run is skipped.</summary>
    private async Task<MirrorSource?> ChooseSourceAsync(Work w, CancellationToken token)
    {
        MirrorSource[] usable; string? choice; bool primaryBad;
        lock (gate)
        {
            choice = w.MirrorChoice; primaryBad = w.BadThisRun.Contains(PrimaryKey(w));
            usable = w.Mirrors.Where(m => m.State == MirrorState.Verified && !w.BadThisRun.Contains(m.Url)).OrderBy(m => m.Priority).ToArray();
        }
        if (choice is not null && !primaryBad && usable.FirstOrDefault(m => m.Url == choice) is { } chosen) return chosen;
        if (choice is not null && choice == PrimaryUrlOf(w) && !primaryBad) return null;
        if (usable.Length == 0) return null;
        if (primaryBad) return usable.OrderBy(m => m.LatencyMs ?? int.MaxValue).First();
        if (choice is not null) return null; // the user picked a source that is no longer usable: the original address
        if (UsesProxy) return null; // the connect time says nothing through a proxy
        var times = await (options.ConnectProbe ?? ((u, t) => MeasureConnectAsync(u, t)))(new[] { w.Request.Url }.Concat(usable.Select(m => m.Url)).ToArray(), token).ConfigureAwait(false);
        lock (gate) for (var i = 0; i < usable.Length; i++) usable[i].LatencyMs = times[i + 1] ?? usable[i].LatencyMs;
        var primary = times[0]; if (primary is null) return usable.OrderBy(m => m.LatencyMs ?? int.MaxValue).First();
        var best = usable.Where(m => m.LatencyMs is not null).OrderBy(m => m.LatencyMs).FirstOrDefault();
        return best is { LatencyMs: { } t } && t < primary * MirrorBetterFactor && primary - t >= MirrorBetterMs ? best : null;
    }

    private static string PrimaryUrlOf(Work w) => new Uri(w.Request.Url).AbsoluteUri;
    private static string PrimaryKey(Work w) => PrimaryUrlOf(w);

    /// <summary>Time to open a TCP connection to each address (milliseconds), null when it could not be measured. At most the first nine addresses are tried, all at once.</summary>
    public static async Task<int?[]> MeasureConnectAsync(IReadOnlyList<string> urls, CancellationToken token, TimeSpan? timeout = null)
    {
        var result = new int?[urls.Count];
        await Task.WhenAll(urls.Select(async (url, i) =>
        {
            if (i > MaxMirrors || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(timeout ?? MirrorProbeTimeout);
            var clock = Stopwatch.StartNew();
            try
            {
                using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                await socket.ConnectAsync(uri.IdnHost, uri.Port, limit.Token).ConfigureAwait(false);
                result[i] = (int)Math.Min(int.MaxValue, clock.ElapsedMilliseconds);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException) { }
        })).ConfigureAwait(false);
        return result;
    }

    /// <summary>Sets what the Server row shows for the source in use: the mirror's host, address and answer time, or the original's own record.</summary>
    private void SetActiveSource(Work w, MirrorSource? mirror)
    {
        lock (gate)
        {
            w.ActiveUrl = mirror?.Url;
            if (mirror is not null)
            {
                w.UsedMirror = true; if (!w.UsedMirrors.Contains(mirror.Url)) w.UsedMirrors.Add(mirror.Url);
                var host = new Uri(mirror.Url).Host; var proxy = options.Proxy is { IsEnabled: true } p ? p : null;
                w.Job.ServerHost = host; w.Job.ServerLatencyMs = mirror.ResponseMs; w.Job.ServerViaProxy = proxy is not null; w.Job.ServerProxy = proxy is null ? null : $"{proxy.Host}:{proxy.Port}";
                w.Job.ServerAddress = proxy is null ? mirror.Address ?? (remotes.TryGetValue(host, out var ip) ? ip : null) : null;
            }
        }
        Notify();
    }

    // ---------------------------------------------------------------- failover and the end check

    /// <summary>The first source of a run failed in a way another source may not: in Automatic mode mark it and let the loop try the next one (no attempt is used up). False when nothing else can take over.</summary>
    private bool FailOver(Work w, Exception ex)
    {
        if (ex is InsufficientDiskSpaceException or ChecksumMismatchException or WrongContentException or OperationCanceledException) return false;
        if (ex is not (HttpRequestException or IOException)) return false;
        lock (gate)
        {
            if (w.MirrorChoice is not null || w.Mirrors.Count == 0 || w.Total <= 0) return false;
            var key = w.ActiveUrl ?? PrimaryKey(w);
            if (w.BadThisRun.Contains(key)) return false;
            var next = w.Mirrors.Any(m => m.State == MirrorState.Verified && m.Url != key && !w.BadThisRun.Contains(m.Url)) || key != PrimaryKey(w) && !w.BadThisRun.Contains(PrimaryKey(w));
            if (!next) return false;
            w.BadThisRun.Add(key);
            return true;
        }
    }

    private bool SavedIdentityUsable(Work w) { lock (gate) return w.Total > 0 && w.ResolvedUrl is not null && (w.ETag is not null || w.Modified is not null) && ValidSegments(w); }

    /// <summary>The identity of the file from the earlier run, used when the original address no longer answers but a verified mirror does.</summary>
    private Probe SavedProbe(Work w) { lock (gate) return new Probe(true, w.Total, w.ETag, w.Modified, w.ResolvedUrl!); }

    internal sealed class ChecksumMismatchException(string algorithm) : IOException("The finished file does not match its checksum (" + algorithm + ").") { public string Algorithm { get; } = algorithm; }

    /// <summary>After a mismatch: every mirror that gave bytes is marked rejected, so the next try uses the original address.</summary>
    private void RejectUsedMirrors(Work w)
    {
        lock (gate)
        {
            foreach (var m in w.Mirrors.Where(m => w.UsedMirrors.Contains(m.Url))) { m.State = MirrorState.Rejected; m.Reason = MirrorReasons.Checksum; }
            w.UsedMirrors.Clear(); w.UsedMirror = false; w.MirrorChoice = null; w.ActiveUrl = null;
        }
    }

    // ---------------------------------------------------------------- public API

    /// <summary>Picks the source: a mirror's address, the original address, or null for Automatic. Only a checked mirror can be picked. A running download switches at once and continues by ranges from what it has.</summary>
    public bool SelectMirror(string id, string? url)
    {
        lock (gate)
        {
            var w = jobs.FirstOrDefault(x => x.Job.Id == id) ?? throw new KeyNotFoundException(id);
            if (url is not null)
            {
                url = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsoluteUri : url;
                if (url != PrimaryUrlOf(w) && !w.Mirrors.Any(m => m.Url == url && m.State == MirrorState.Verified)) return false;
            }
            if (w.MirrorChoice == url) return true;
            w.MirrorChoice = url; w.BadThisRun.Clear();
            try { SaveLocked(); } catch (Exception ex) { LastPersistenceError = ex.Message; }
            if (w.AttemptCts is { } attempt && w.Job.Status == "Downloading") { w.SwitchRequested = true; try { attempt.Cancel(); } catch (ObjectDisposedException) { } }
        }
        Notify();
        return true;
    }

    /// <summary>Forgets a mirror (also one that was learned from a header); the original address cannot be removed.</summary>
    public bool RemoveMirror(string id, string url)
    {
        lock (gate)
        {
            var w = jobs.FirstOrDefault(x => x.Job.Id == id) ?? throw new KeyNotFoundException(id);
            var found = w.Mirrors.RemoveAll(m => m.Url == url) > 0;
            if (found && w.MirrorChoice == url) { w.MirrorChoice = null; if (w.AttemptCts is { } a && w.Job.Status == "Downloading") { w.SwitchRequested = true; try { a.Cancel(); } catch (ObjectDisposedException) { } } }
            if (found) try { SaveLocked(); } catch (Exception ex) { LastPersistenceError = ex.Message; }
            return found;
        }
    }

    /// <summary>The user's own mirror: an address of the same file, or a Metalink (.meta4) address. It is checked at once when the size is known, else when the download next runs.</summary>
    public async Task<MirrorAddResult> AddMirrorAsync(string id, string address, CancellationToken token = default)
    {
        address = (address ?? "").Trim();
        if (address.Length is 0 or > 2048 || !Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)) return new MirrorAddResult(false, "address", 0, MirrorState.Rejected);
        Work w; long total;
        lock (gate)
        {
            w = jobs.FirstOrDefault(x => x.Job.Id == id) ?? throw new KeyNotFoundException(id);
            if (w.Request.StreamKind != StreamKind.None || w.Request.Youtube is not null || w.Job.Status == "Completed") return new MirrorAddResult(false, "unsupported", w.Mirrors.Count, MirrorState.Rejected);
            total = w.Total;
        }
        if (uri.AbsolutePath.EndsWith(".meta4", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.EndsWith(".metalink", StringComparison.OrdinalIgnoreCase))
        {
            var n = await ApplyMetalinkUrlAsync(w, uri.AbsoluteUri, total, token).ConfigureAwait(false);
            if (n > 0 && total > 0) await ValidateCandidatesAsync(w, total, token).ConfigureAwait(false);
            lock (gate) { NotifyAfterLocked(); return new MirrorAddResult(n > 0, n > 0 ? null : MirrorReasons.Content, w.Mirrors.Count, MirrorState.Candidate); }
        }
        if (!AddCandidate(w, uri.AbsoluteUri, MirrorOrigin.User, 50, null)) { lock (gate) return new MirrorAddResult(false, "duplicate", w.Mirrors.Count, MirrorState.Rejected); }
        MirrorSource mirror; lock (gate) mirror = w.Mirrors.First(m => m.Url == uri.AbsoluteUri);
        if (total > 0) await ValidateMirrorAsync(w, mirror, total, token).ConfigureAwait(false);
        lock (gate) { try { SaveLocked(); } catch (Exception ex) { LastPersistenceError = ex.Message; } }
        Notify();
        lock (gate) return new MirrorAddResult(mirror.State != MirrorState.Rejected, mirror.Reason, w.Mirrors.Count, mirror.State);
    }
    private void NotifyAfterLocked() => Notify();

    /// <summary>Measures the TCP connect time of the original address and each mirror (at most nine addresses) and stores it. A download through a proxy is not measured (the answer would be the proxy's).</summary>
    public async Task RefreshMirrorLatenciesAsync(string id, CancellationToken token = default)
    {
        string[] urls; MirrorSource[] list;
        lock (gate)
        {
            var w = jobs.FirstOrDefault(x => x.Job.Id == id) ?? throw new KeyNotFoundException(id);
            if (UsesProxy || w.Mirrors.Count == 0) return;
            list = w.Mirrors.Where(m => m.State != MirrorState.Rejected).Take(MaxMirrors).ToArray();
            urls = new[] { w.Request.Url }.Concat(list.Select(m => m.Url)).ToArray();
            w.Job.ServerLatencyMs = w.Job.ServerLatencyMs; // unchanged: the Server row keeps the first-response time
        }
        var times = await (options.ConnectProbe ?? ((u, t) => MeasureConnectAsync(u, t)))(urls, token).ConfigureAwait(false);
        lock (gate)
        {
            var w = jobs.FirstOrDefault(x => x.Job.Id == id); if (w is null) return;
            w.PrimaryConnectMs = times[0];
            for (var i = 0; i < list.Length; i++) if (times[i + 1] is { } t) list[i].LatencyMs = t;
        }
        Notify();
    }

    private bool TakeSwitch(Work w) { lock (gate) { if (!w.SwitchRequested) return false; w.SwitchRequested = false; return true; } }

    /// <summary>The first request of an attempt. When the original address failed earlier in this run and a mirror is known, the file's identity saved from the earlier run stands in for it.</summary>
    private async Task<Probe> ProbeOrSavedAsync(Work w, CancellationToken token)
    {
        bool primaryBad; lock (gate) primaryBad = w.BadThisRun.Contains(PrimaryKey(w));
        var probe = primaryBad && SavedIdentityUsable(w) ? SavedProbe(w) : await ProbeAsync(w, token).ConfigureAwait(false);
        return await ApplyMirrorsAsync(w, probe, token).ConfigureAwait(false);
    }

    private void RestoreMirrors(Work w, List<MirrorSource>? mirrors, string? choice, string? expected, bool trusted, List<string>? used, bool unverified, bool verified)
    {
        foreach (var m in (mirrors ?? new()).Take(MaxMirrors))
        {
            if (!Uri.TryCreate(m.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || m.Url.Length > 2048 || w.Mirrors.Any(x => x.Url == uri.AbsoluteUri)) continue;
            if (!Enum.IsDefined(m.Origin) || !Enum.IsDefined(m.State)) continue;
            m.Url = uri.AbsoluteUri; m.Geo = m.Geo is { Length: >= 2 and <= 8 } ? m.Geo : null; w.Mirrors.Add(m);
        }
        w.MirrorChoice = choice is not null && Uri.TryCreate(choice, UriKind.Absolute, out var c) && w.Mirrors.Any(x => x.Url == c.AbsoluteUri) ? c.AbsoluteUri : null;
        var colon = expected?.IndexOf(':') ?? -1;
        if (colon > 0 && ExpectedHash.FromHex(expected![..colon], expected[(colon + 1)..]) is { } hash) { w.Expected = hash; w.ExpectedTrusted = trusted; }
        w.UsedMirrors = (used ?? new()).Where(u => w.Mirrors.Any(x => x.Url == u)).ToList(); w.UsedMirror = w.UsedMirrors.Count > 0 || unverified;
        w.MirrorUnverified = unverified; w.ChecksumVerified = verified;
    }

    private IReadOnlyList<MirrorInfo> SourcesLocked(Work w)
    {
        if (w.Mirrors.Count == 0 && w.MirrorChoice is null) return Array.Empty<MirrorInfo>();
        var primary = PrimaryUrlOf(w);
        string? Address(string host) => remotes.TryGetValue(host, out var ip) ? ip : null;
        var list = new List<MirrorInfo> { new(primary, new Uri(primary).Host, MirrorOrigin.User, null, 0, MirrorState.Verified, null, w.PrimaryConnectMs, w.Job.ServerAddress ?? Address(new Uri(primary).Host), true, w.ActiveUrl is null) };
        foreach (var m in w.Mirrors.OrderBy(m => m.Priority)) { var host = new Uri(m.Url).Host; list.Add(new MirrorInfo(m.Url, host, m.Origin, m.Geo, m.Priority, m.State, m.Reason, m.LatencyMs, m.Address ?? Address(host), false, m.Url == w.ActiveUrl)); }
        return list;
    }
}
