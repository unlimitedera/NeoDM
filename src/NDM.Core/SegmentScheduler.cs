namespace NDM.Core;

public sealed partial class DownloadManager
{
    // A connection cap is not a persisted shard cap. Bound both independently.
    private const int MaximumShards = 256;
    /// <summary>With a single connection, this many refusals in a row (each after a longer pause) are tolerated before the job fails.</summary>
    private const int MaximumSingleRefusals = 3;

    private DownloadJob SnapshotLocked(Work w)
    {
        var job = w.Job.Snapshot();
        job.ConnectionLimit = LimitOf(w);
        job.SpeedLimitBytesPerSecond = w.SpeedLimit;
        job.ActiveConnectionCount = w.Segments.Count(s => s.Active) + (w.Probing ? 1 : 0);
        if (w.Streaming && job.ActiveConnectionCount == 0) job.ActiveConnectionCount = 1;
        job.Segments = Array.AsReadOnly(w.Segments.Select(s =>
            new DownloadSegmentSnapshot(s.Start, s.Length == long.MaxValue ? 0 : s.Length, s.Done, s.Active)).ToArray());
        job.RemainingTime = SnapshotTime.Remaining(job);
        if (w.Request.StreamKind != StreamKind.None && job.Status == "Downloading")
        {
            job.ActiveConnectionCount = Volatile.Read(ref w.StreamActive) + (w.Probing ? 1 : 0);
            // Streams: the time left follows the rate at which pieces finish, not the bytes (the size is an estimate until the end).
            if (w.SegmentRate > 0 && job.SegmentsTotal > job.SegmentsDone) job.RemainingTime = TimeSpan.FromSeconds(Math.Min((job.SegmentsTotal - job.SegmentsDone) / w.SegmentRate, 3.0e9));
        }
        return job;
    }

    private async Task DownloadSegmentsAsync(Work w, Probe probe, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        var running = new Dictionary<Segment, SegmentRunner>();
        var refusals = new List<SegmentRefusedException>();
        var singleRefused = 0; // refusals met with one connection in a row (a connection that was just stopped may still count on the server for a moment)
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                // Only this coordinator owns the topology and worker lifecycle.
                foreach (var pair in running.Where(p => p.Value.Task.IsCompleted).ToArray())
                {
                    var refusal = await pair.Value.Task.ConfigureAwait(false);
                    running.Remove(pair.Key);
                    pair.Value.Cancellation.Dispose();
                    if (refusal is not null) refusals.Add(refusal); else singleRefused = 0;
                }
                if (refusals.Count > 0)
                {
                    // The first request worked but a later one is refused: the link allows fewer parallel connections. Halve the number (never below one),
                    // stop the surplus connections (their bytes stay; each continues from its saved offset), and go on. A single connection that is refused fails the job.
                    var refused = refusals[0]; refusals.Clear();
                    var before = LimitOf(w);
                    if (before <= 1)
                    {
                        if (++singleRefused > MaximumSingleRefusals) throw refused.ToHttpException();
                        await Task.Delay(TimeSpan.FromTicks(refused.Delay.Ticks * singleRefused), linked.Token).ConfigureAwait(false);
                        continue;
                    }
                    var after = CompatibilityLadder.ReduceConnections(before);
                    lock (gate) w.ConnLimit = after;
                    MemoryOf(w.Request.Url).MaxConnections = after;
                    AddTrail(w, $"{(int)refused.Status} on a parallel connection: connections {before} to {after}");
                    while (running.Count > after)
                    {
                        var surplus = running.OrderBy(p => p.Key.Done).First();
                        surplus.Value.Cancellation.Cancel();
                        try { await surplus.Value.Task.ConfigureAwait(false); }
                        catch (OperationCanceledException) when (surplus.Value.Cancellation.IsCancellationRequested && !linked.IsCancellationRequested) { }
                        running.Remove(surplus.Key);
                        surplus.Value.Cancellation.Dispose();
                    }
                    Notify();
                    await Task.Delay(refused.Delay, linked.Token).ConfigureAwait(false);
                    continue;
                }
                linked.Token.ThrowIfCancellationRequested();
                Segment[] pending;
                lock (gate) pending = w.Segments.Where(s => s.Done < s.Length && !running.ContainsKey(s))
                    .Take(LimitOf(w) - running.Count).ToArray();
                foreach (var s in pending)
                {
                    var cancellation = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                    lock (gate) s.Active = true;
                    var task = RunSegmentOwnedAsync(w, s, probe, cancellation.Token);
                    running.Add(s, new SegmentRunner(task, cancellation));
                    _ = task.ContinueWith(_ => linked.Cancel(), CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                if (running.Count == 0) return;

                Segment? candidate = null;
                lock (gate)
                {
                    // An aggregate rate limit is already the bottleneck; avoid pointless
                    // cancellation/re-request churn and discarded throttle reservations.
                    if (options.EnableDynamicSegmentation && LimitOf(w) > 1 && w.SpeedLimit == null &&
                        running.Count < LimitOf(w) && w.Segments.Count < MaximumShards)
                        candidate = running.Keys.Where(s => s.Active && s.Length - s.Done >= options.MinimumSegmentSize &&
                            (s.Length - s.Done) / 2 >= options.MinimumSegmentSize)
                            .OrderByDescending(s => s.Length - s.Done).FirstOrDefault();
                }
                if (candidate != null)
                {
                    var owner = running[candidate];
                    owner.Cancellation.Cancel();
                    // Join and dispose the original immutable HTTP range and writer
                    // BEFORE changing endpoints or assigning the undownloaded tail.
                    try { await owner.Task.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (owner.Cancellation.IsCancellationRequested && !linked.IsCancellationRequested) { }
                    running.Remove(candidate);
                    owner.Cancellation.Dispose();
                    token.ThrowIfCancellationRequested();
                    linked.Token.ThrowIfCancellationRequested();
                    lock (gate)
                    {
                        var remaining = candidate.Length - candidate.Done;
                        if (remaining / 2 >= options.MinimumSegmentSize)
                        {
                            var oldLength = candidate.Length;
                            candidate.Length = candidate.Done + remaining / 2;
                            var tail = new Segment
                            {
                                PartId = Enumerable.Range(0, MaximumShards).First(id => w.Segments.All(s => s.PartId != id)),
                                Start = candidate.Start + candidate.Length,
                                Length = oldLength - candidate.Length
                            };
                            w.Segments.Add(tail);
                            w.Segments.Sort((a, b) => a.Start.CompareTo(b.Start));
                            if (!ValidSegments(w)) throw new IOException("Invalid split coverage.");
                            SaveLocked();
                        }
                    }
                    continue;
                }
                await Task.WhenAny(running.Values.Select(r => r.Task)).ConfigureAwait(false);
            }
        }
        catch
        {
            linked.Cancel();
            try { await Task.WhenAll(running.Values.Select(r => r.Task)).ConfigureAwait(false); }
            catch { /* Choose the originating error, not sibling cancellation. */ }
            var errors = running.Values.Where(r => r.Task.IsFaulted)
                .SelectMany(r => r.Task.Exception!.Flatten().InnerExceptions).ToArray();
            if (errors.Any(e => e is RangeRejectedException)) throw new RangeRejectedException();
            var error = errors.FirstOrDefault();
            if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            throw;
        }
        finally
        {
            foreach (var runner in running.Values) runner.Cancellation.Dispose();
        }
    }

    /// <summary>Null when the segment finished; the refusal when the server answered it with a connection-limit status (handled by the coordinator).</summary>
    private async Task<SegmentRefusedException?> RunSegmentOwnedAsync(Work w, Segment segment, Probe probe, CancellationToken token)
    {
        try { await SegmentAsync(w, segment, probe, token).ConfigureAwait(false); return null; }
        catch (SegmentRefusedException refused) { return refused; }
        finally { lock (gate) segment.Active = false; }
    }

    private sealed record SegmentRunner(Task<SegmentRefusedException?> Task, CancellationTokenSource Cancellation);
}
