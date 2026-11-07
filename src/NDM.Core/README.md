# NDM.Core

Independent .NET 8 HTTP(S) download engine. No package references or proprietary code.

## Required interface

`DownloadRequest`, `DownloadJob`, and `DownloadManager` implement the shared contract in namespace `NDM.Core`.

```csharp
using var manager = new DownloadManager(downloadDirectory, stateDirectory);
var added = await manager.AddAsync(new DownloadRequest(url, FileName: "example.bin"));
manager.Pause(added.Id);
manager.Resume(added.Id);
var snapshot = manager.Jobs.Single(j => j.Id == added.Id);
```

`Jobs` and `AddAsync` return detached snapshots. Poll `Jobs` to obtain updates; setters are internal. `Changed` is raised on thread-pool threads, not the UI thread, and observer exceptions are isolated. UI code must marshal notifications to its dispatcher. `AddAsync` persists and queues/starts a transfer; it does not wait for the completed file.

Statuses: `Queued`, `Scheduled`, `Downloading`, `Paused`, `Completed`, `Failed`.

## Additional APIs

- `DownloadManager(string downloadDirectory, string stateDirectory, DownloadOptions options)`.
- `AddScheduledAsync(DownloadRequest request, DateTimeOffset? scheduledAt)` returns `Task<DownloadJob>`; future jobs do not start early.
- `Schedule(string id, DateTimeOffset scheduledAt)` changes the due time and cancels an active attempt before restarting at the due time.
- `SetSpeedLimit(string id, long? bytesPerSecond)` sets an aggregate per-job limit shared by all segments; `null` removes the limit. Positive values only. Live changes are observed within 250 ms.
- `LastPersistenceError` reports checkpoint write failures; explicit public mutations throw if their checkpoint cannot be written.
- `DownloadOptions`: `MaxConcurrentDownloads` (default 3, bounds 1..16), `MaxSegments` (default 4, concurrent connection cap, bounds 1..32), `EnableDynamicSegmentation` (default `true`), `MinimumSegmentSize` (default 256 KiB, minimum 64 KiB), `RetryCount` (default 3, bounds 0..8), `RequestTimeout` (default 30 seconds, bounds 100 ms..5 minutes). Timeout applies to connecting, response headers and each read, not the entire large download.
- `DownloadFileNames.Sanitize(string?)` provides the same Windows-safe sanitization used by the queue.

## Safety and lifecycle

- Streams response bodies with fixed 64 KiB buffers. Downloads are never materialized as a whole byte array.
- A real `bytes=0-0` probe must return exact `206` bounds, total size and one byte. Parallelism requires a strong ETag, or a conservative Last-Modified/Date separation of at least 60 seconds. Weak/missing validators use a single full stream.
- Every segment requires exact Content-Range, length, validator and final redirected resource URI. `If-Range` is sent on each range request. Ignored/rejected/changed ranges discard all parts and fall back to a fresh full stream; a `200` response is never appended to partial bytes.
- Resume re-probes and retains offsets only when the resource URI, length and validators match. Part lengths are reconciled against persisted offsets. Streaming-only jobs restart from zero on resume. Pause cancellation is asynchronous: in-flight writes settle before the worker exits.
- Retries are bounded, back off, and retain only revalidated range parts. Final publication requires every segment to be complete. HTTP encoding other than identity is rejected rather than risking incorrect byte ranges.
- Redirects are capped at ten HTTP(S) requests per chain. Captured cookies are never forwarded to another origin. HTTPS uses the platform's normal certificate validation; there is no insecure-certificate override.
- Names remove traversal separators, invalid Windows characters, reserved devices, trailing dots/spaces and excessive length. Existing files, directories and queued names are reserved; final moves do not overwrite late-created files.
- Everything a transfer writes (part files, the joined copy, stream pieces, the helper tool's temp) lives in a hidden `.<id>.ndmpart` work folder next to the destination file, and the last step is a rename on the same volume. Cleanup never deletes final destination files. `Remove` cancels the job, removes queue metadata and its owned partial/staging files only.
- `queue.json` is written via a flushed temporary file and atomic replacement. Progress checkpoints occur approximately once per second and at state transitions. An exclusive `queue.lock` prevents two managers from owning one state directory. Corrupt JSON fails closed and is preserved for repair.
- Interrupted downloading/queued jobs restore as `Paused`; future scheduled jobs restore their due times and start automatically when due. Completed jobs restore as completed without touching their files. Dispose cancels and waits for every active transfer, including removed jobs, then checkpoints and releases resources.

Queue metadata stores URLs, referer and cookie values in local plaintext so authenticated downloads can resume. Store the state directory in per-user application data, protect its permissions, and do not publish it. Atomic queue replacement is not a transaction with final-file rename: a hard crash between publication and its checkpoint can leave an existing completed file and a paused queue entry; any later resume preserves that file and chooses a collision-safe new name.

## Snapshot telemetry

The existing fields, methods and status strings are unchanged. Added public contract:

```csharp
// DownloadJob: public getters, internal setters
TimeSpan? RemainingTime
bool? ResumeSupported
int ActiveConnectionCount
int ConnectionLimit
IReadOnlyList<DownloadSegmentSnapshot> Segments

public sealed record DownloadSegmentSnapshot(
    long Start, long Length, long DownloadedBytes, bool IsActive);

// DownloadOptions
public bool EnableDynamicSegmentation { get; init; } = true;
```

- `SpeedBytesPerSecond` is an elapsed-time-weighted EWMA, sampled about every 600 ms by the 200 ms timer, with a two-second time constant (`alpha = 1 - exp(-elapsed / 2)`). Sampling uses a monotonic stopwatch. The first sample seeds the estimate; each new run or full restart clears history. Speed is zero while paused or after the runner exits.
- `RemainingTime` uses that smoothed speed. It is `null` for unknown total length, non-downloading jobs, or zero speed; completed jobs return `TimeSpan.Zero`. Finite estimates are bounded to the representable `TimeSpan` range. It is an estimate, not a promise about network performance or final assembly time.
- `ResumeSupported` is `null` before/during a probe (including restored unprobed jobs), `true` after a validated resumable range probe, and `false` for streaming-only/fallback transfers. The engine revalidates this on every run and retry and does not trust saved metadata.
- `ActiveConnectionCount` reports owned in-flight request/response workers, including a single probe or streaming request; it is not `MaxSegments` or the number of historical shards. It can include connecting/header/read/throttle waits and declines as owners dispose. Pause is asynchronous; poll until this is zero for a settled checkpoint. Remote servers may take time to observe a cancelled socket; this is not a guarantee about their socket counters or transport cleanup.
- `ConnectionLimit` is the configured `MaxSegments`, even when the file is too small or unsafe for parallel ranges. `Segments` is a fresh read-only collection of immutable records, ordered by byte start; `IsActive` reflects live range/stream ownership. Unknown-length streaming uses segment `Length = 0` until EOF (not a fictitious enormous range). Saved/completed segments remain visible but are inactive. Initial probes have no segment ownership.

## Dynamic segmentation and checkpoint ownership

The coordinator starts at most `MaxSegments` bounded contiguous ranges. When a range finishes and a slot is spare, it chooses the largest unfinished active range whose remaining tail can be divided into two tails of at least `MinimumSegmentSize`. It cancels that owner, awaits the entire request/response/file-writer disposal, then recomputes the actual committed prefix. Only then does it halve the undownloaded tail and launch re-requests for the retained range and new tail. No response's requested endpoints are mutated while it is streaming; buffered-but-uncommitted bytes are discarded, not assigned twice. If the range finishes during cancellation or its remaining tail becomes too small, no split is made. Other owners keep running while the coordinator waits; no network/file wait occurs under the queue lock.

Topologies have at most 256 persisted shards per job, independently of the 1..32 concurrent request owners. Shards always cover the full resource exactly once, sorted and without holes/overlaps. Each shard retains a unique numeric `PartId` for its file; inserting a tail never renames/reindexes any existing part. Old queues lacking all `PartId` fields migrate to the old list index (`0.part`, `1.part`, etc.) before validation. Resume reconciles actual file length with committed bytes, truncates any uncheckpointed excess, and revalidates the original representation/URI. A job saved with many shards can restore at a smaller connection cap without launching every shard at once. Split checkpoints are written before re-requests; interruption before that checkpoint can only lose newly downloaded progress, not coverage. Unreferenced orphan files remain within the owned job-part directory and are cleaned up with it.

Dynamic splitting is disabled when `EnableDynamicSegmentation` is false, when the cap is one, for streaming-only resources, when the minimum tail size/shard bound is reached, and while an aggregate speed limit is active (that limit is already the bottleneck; re-request churn does not increase permitted throughput). The existing aggregate limiter still applies to every write across all workers and supports responsive live changes/cancellation at very low rates. A rejected new range cancels and joins all owners before discarding parts and streaming afresh. Ordinary bounded retries retain only revalidated shard offsets.

## Controlled local benchmark

Reproduce (no dependencies or Internet):

```text
dotnet run --project F:/NDM/tests/NDM.Tests/NDM.Tests.csproj -c Release -- --benchmark
```

This is a synthetic asymmetric per-connection bandwidth test, not a claim to be the fastest downloader. Each fresh loopback HTTP/1.1 fixture serves the same 2,097,265 pseudorandom bytes. All requests whose start lies in the first quarter have a 40 ms delay after each 8 KiB write; other ranges have a 3 ms delay. Both modes use cap 4, minimum shard 64 KiB, identical validator checks and publication, no speed limit, and independent empty queues. Three pairs alternate static/dynamic order. Duration includes add/persistence/probe/transfer/assembly/polling/hash verification, not just socket read time. Every file is checked against the same SHA-256. Windows timer scheduling affects the actual delays.

Raw measured output from the local Windows run:

```text
BENCH fixture=loopback asymmetric bytes=2097265 cap=4 min_shard=65536 chunk=8192 slow_delay_ms=40 fast_delay_ms=3
BENCH round=1 mode=static duration_s=3.116401 throughput_Bps=672976.55 shards=4 data_requests=4 sha256=50C09EA9E4C47BF563F839592D6BFD85151C3F2FB410FA8AD01539E3AF3CBF49
BENCH round=1 mode=dynamic duration_s=1.488641 throughput_Bps=1408845.67 shards=7 data_requests=9 sha256=50C09EA9E4C47BF563F839592D6BFD85151C3F2FB410FA8AD01539E3AF3CBF49
BENCH round=2 mode=dynamic duration_s=1.502450 throughput_Bps=1395896.42 shards=7 data_requests=9 sha256=50C09EA9E4C47BF563F839592D6BFD85151C3F2FB410FA8AD01539E3AF3CBF49
BENCH round=2 mode=static duration_s=3.018943 throughput_Bps=694701.73 shards=4 data_requests=4 sha256=50C09EA9E4C47BF563F839592D6BFD85151C3F2FB410FA8AD01539E3AF3CBF49
BENCH round=3 mode=static duration_s=2.992442 throughput_Bps=700854.09 shards=4 data_requests=4 sha256=50C09EA9E4C47BF563F839592D6BFD85151C3F2FB410FA8AD01539E3AF3CBF49
BENCH round=3 mode=dynamic duration_s=1.513140 throughput_Bps=1386034.54 shards=7 data_requests=9 sha256=50C09EA9E4C47BF563F839592D6BFD85151C3F2FB410FA8AD01539E3AF3CBF49
BENCH mean_static_s=3.042595 mean_dynamic_s=1.501410 speedup=2.026x duration_reduction_pct=50.65
```

The reduction demonstrates eliminating a slow-range straggler by letting freed connections share its tail. It costs more requests and abandons some in-flight buffered bytes. These six local observations do not establish gains on the Internet, a saturated aggregate link, disk-limited workloads, equal-speed ranges, small files, or servers that impose global/origin limits. Reconnect/TLS cost can outweigh a split. No OS/network-stack tuning, proprietary logic, external packages, or speed-limit bypass is involved.

## Verification

```text
dotnet build F:/NDM/src/NDM.Core/NDM.Core.csproj -c Release --nologo
dotnet run --project F:/NDM/tests/NDM.Tests/NDM.Tests.csproj -c Release
```

Verified console result: 32/32 passed, 0 failed, preserving all original 21 tests and adding 11 dynamic/telemetry/settings cases. The tests are a console executable (not a NuGet test-framework project). They exercise a real loopback TCP HTTP fixture and SHA-256 equality, including ranged/no-range/chunked/empty responses, pause/resume, restored state, changed and weak validators, malformed ranges, bounded retries, truncated bodies, name collisions/traversal, remove semantics, speed/concurrency, schedules, redirects, corruption and observer disposal. Added proofs cover actual asymmetric splitting and immutable coverage snapshots, dynamic shard restore at a lower cap, changed validators after a split, legacy index migration, EWMA/finite ETA, one connection, very low rate cancellation and aggregate limiting, expanded bounds, rejection fallback and truncation retry after splitting, and actual streaming/unknown-length ownership. Test artifacts use the per-user Hermes scratch directory and are deleted after a passing run.
