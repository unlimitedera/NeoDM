using System.Diagnostics;
using NDM.Core;
using NDM.Tests;

// The streaming tests start this very program under the name ffmpeg.exe as a stand-in for the real tool.
if (FakeFfmpeg.IsFakeProcess) return FakeFfmpeg.Run(args);

var root = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch"), "ndm-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var failures = new List<string>(); var passed = 0;
if (args.Contains("--benchmark"))
{
    try { await DynamicTests.Benchmark(root); return 0; }
    finally { Directory.Delete(root, true); }
}
if (args.Contains("--real-ffmpeg")) // opt-in: installs the real pinned ffmpeg and muxes generated streams (network, ~115 MB)
{
    try { return await StreamRealTests.Run(root); }
    finally { try { Directory.Delete(root, true); } catch (IOException) { } }
}
var tests = new (string Name, Func<Task> Run)[]
{
    ("parallel validated ranges and exact SHA-256", Ranges),
    ("no-range streaming and request headers", Streaming),
    ("chunked unknown-length streaming and empty file", UnknownAndEmpty),
    ("pause/resume retains validated range offsets", PauseResume),
    ("persistent state restore and exclusive ownership", Restore),
    ("changed validator restarts without mixed bytes", ChangedValidator),
    ("invalid range / If-Range rejection falls back safely", BadRanges),
    ("missing or unsafe date validator streams; strong date segments", Validators),
    ("bounded retries succeed and failures remain unpublished", RetryFailure),
    ("truncated response never publishes and retries are bounded", Truncated),
    ("malicious filenames and collision protection", Names),
    ("remove keeps completed files and deletes partials", Remove),
    ("job concurrency bound and speed limit", Limits),
    ("scheduling persists and starts at due time", Scheduling),
    ("detached snapshots and bad input rejection", Input),
    ("corrupt queue fails closed without data loss", Corrupt),
    ("streaming pause restarts cleanly", StreamingPause),
    ("range retry uses validated offsets", RangeRetry),
    ("live removal of very low speed limit", LiveSpeedChange),
    ("redirects bound cookies and final validator scope", Redirects),
    ("observer can dispose manager without deadlock", ObserverDispose),
    ("dynamic splits preserve coverage, hash and live cap", () => DynamicTests.Split(Paths())),
    ("dynamic shards pause/restore at a smaller connection cap", () => DynamicTests.Restore(Paths(), false)),
    ("dynamic shard restore rejects changed validators", () => DynamicTests.Restore(Paths(), true)),
    ("legacy index part files migrate without lost progress", () => DynamicTests.Legacy(Paths())),
    ("smoothed speed and finite ETA telemetry", () => DynamicTests.Telemetry(Paths())),
    ("one connection disables splitting and respects telemetry", () => DynamicTests.OneConnection(Paths())),
    ("dynamic low rate stays bounded and pause stays responsive", () => DynamicTests.LowRate(Paths())),
    ("expanded settings validate exact bounds", () => DynamicTests.Options(Paths())),
    ("dynamic split rejection joins owners before streaming fallback", () => DynamicTests.Fallback(Paths())),
    ("dynamic shard truncation retries validated checkpoint offsets", () => DynamicTests.Retry(Paths())),
    ("streaming and unknown-length telemetry reports actual ownership", () => DynamicTests.StreamingTelemetry(Paths())),
    ("HTTP proxy carries downloads with auth; bad login fails without leaking", () => ManageTests.HttpProxy(Paths)),
    ("SOCKS5 proxy: remote and local name resolution, auth", () => ManageTests.Socks5Proxy(Paths)),
    ("CONNECT tunnel is byte-exact and reports 407", () => ManageTests.HttpsTunnelThroughConnect(Paths)),
    ("proxy test button: success, refused, missing credentials, validation", () => ManageTests.ProxyTestButton(Paths)),
    ("remove can delete only the job's own file; missing/locked reported", () => ManageTests.RemoveWithFile(Paths)),
    ("move/rename updates disk and job, persists, rejects unsafe targets", () => ManageTests.MoveRename(Paths)),
    ("refresh address keeps validated progress", () => ManageTests.RefreshAddress(Paths)),
    ("redownload restarts without touching the old file", () => ManageTests.Redownload(Paths)),
    ("change save location: partial, running, completed, collision, bad destinations, same folder", () => ManageTests.MoveTo(Paths)),
    ("file names: Content-Disposition / URL / query / MIME / magic-number parsing", NameTests.Parsing),
    ("file names: video requests from the extension (signed, extensionless, long, post page)", NameTests.MediaRequests),
    ("file names: priority, extension repair, GUID/empty fallback", NameTests.Decisions),
    ("file names: real downloads resolve the right name (disposition, redirects, type, magic, HEAD 405, collisions)", () => NameTests.Downloads(Paths())),
    ("file names: pre-download probe (HEAD, ranged-GET fallback, redirects, size/type)", () => NameTests.Probe(Paths())),
    ("1.5.3 cookie at rest: protected in queue.json, restored, never plain text", () => SecurityTests.CookieAtRest(Paths())),
    ("1.5.3 cookie: legacy plain text migrates; failed and removed jobs keep nothing", () => SecurityTests.CookieMigrationAndClearing(Paths())),
    ("1.5.3 Mark of the Web: exact Zone.Identifier stream, sanitized addresses", () => SecurityTests.MarkOfTheWebStream(Paths())),
    ("1.5.3 Mark of the Web: skipped without ADS support, off by setting", () => SecurityTests.MarkOfTheWebSkippedAndOff(Paths())),
    ("1.5.3 redirects: https to http refused, Referer and cookie dropped across origins", () => SecurityTests.RedirectHardening(Paths())),
    ("1.5.3 file names: bidi and zero-width removed, desktop.ini/autorun.inf/CLOCK$/superscript COM reserved", SecurityTests.FileNames),
    ("1.5.3 disk space: declared length, staging overhead, floor for unknown length, mid-stream check", () => SecurityTests.DiskSpace(Paths())),
    ("1.5.3 read caps: queue.json 64 MB and settings 1 MB, file preserved", () => SecurityTests.ReadCaps(Paths())),
    ("1.5.3 queue.json shape: request cookie is null in the file", () => SecurityTests.QueueJsonStillValid(Paths())),
    ("1.5.4 content rules: html type, under half of the size, html first bytes, expected web pages accepted", WrongContentTests.Rules),
    ("1.5.4 one-time link already used by the browser: the page is refused, nothing saved, both sizes in the message", () => WrongContentTests.ExpiredPageIsRefused(Paths())),
    ("1.5.4 wrong-content survives a restart; queue.json carries the browser facts", () => WrongContentTests.WrongContentSurvivesRestart(Paths())),
    ("1.5.4 a queue.json from 1.5.3 (no new fields) still restores", () => WrongContentTests.OldQueueWithoutNewFields(Paths())),
    ("1.5.4 without a size hint the file name decides; an automatic name accepts a page", () => WrongContentTests.WithoutSizeHintTheNameDecides(Paths())),
    ("1.5.4 a page behind binary headers is caught by size or first bytes; .html downloads still work", () => WrongContentTests.HtmlHiddenBehindBinaryHeaders(Paths())),
    ("1.5.4 control: a real file with the browser size completes and is exact", () => WrongContentTests.FileIsStillDownloaded(Paths())),
    ("1.5.4 server that ignores Range: streams a good file, refuses an expired page", () => WrongContentTests.NoRangeVariant(Paths())),
    ("1.5.4 cookie-bound link: page without the cookie, file with it", () => WrongContentTests.CookieVariant(Paths())),
    ("1.5.4 reply smaller than half of the browser size is refused", () => WrongContentTests.RuleBStreaming(Paths())),
    ("1.5.4 redirect rules: same-site .php/.html page refused, other cases left alone, registrable site", WrongContentTests.RedirectRules),
    ("1.5.4 browser cookie: 6 MB arrives complete with g2cn, hash equal, repair page never requested", () => WrongContentTests.BrowserCookieMakesTheFileArrive(Paths())),
    ("1.5.4 no cookie: 302 to a repair page fails early with wrong-content (redirect flag), nothing published", () => WrongContentTests.WithoutCookieFailsEarlyOnTheRedirect(Paths())),
    ("1.5.4 redirect to a page is still followed when nothing says the answer must be a file", () => WrongContentTests.RedirectToPageWithoutExpectationIsFollowed(Paths())),
    ("1.5.7 identity and ladder rules: User-Agent/Accept/Accept-Language, status set, variant order, five-attempt cap, Retry-After, halving", CompatTests.Rules),
    ("1.5.7 request headers: browser-style default, captured identity, NeoDM and Custom choices, caps and control characters refused", () => CompatTests.Headers(Paths())),
    ("1.5.7 identity and attempt trail persist; a 1.5.6 queue.json restores", () => CompatTests.Persistence(Paths())),
    ("1.5.7 strict server (i): User-Agent must be a browser's; 1.5.6 request refused with 403, engine completes", () => CompatTests.BrowserAgentRequired(Paths())),
    ("1.5.7 strict server (ii): Accept-Language required; 1.5.6 request refused with 400, engine completes", () => CompatTests.AcceptLanguageRequired(Paths())),
    ("1.5.7 strict server (iii): Range 0-0 refused with 400; plain GET is the transfer, remembered for the site", () => CompatTests.RangeProbeRefused(Paths())),
    ("1.5.7 strict server (iv): HEAD refused with 405; the name probe falls back and does not ask again", () => CompatTests.HeadRefused(Paths())),
    ("1.5.7 strict server (v): one connection per file; connections halve to one, no mixed data, remembered for the site", () => CompatTests.OneConnectionOnly(Paths())),
    ("1.5.7 strict server (v): every ranged segment refused; fails only when one connection is refused too", () => CompatTests.SegmentsRefusedAlways(Paths())),
    ("1.5.7 strict server (vi): referer from its own origin; site root, then no referer", () => CompatTests.RefererRules(Paths())),
    ("1.5.7 strict server (vii): one-use link; the first answer is the transfer, no throwaway request", () => CompatTests.OneUseLink(Paths())),
    ("1.5.7 ladder bound: five attempts, 401 with a challenge and 404 are not retried, trail in the error", () => CompatTests.AttemptCap(Paths())),
    ("1.5.7 Retry-After is honoured up to the cap", () => CompatTests.RetryAfterIsHonoured(Paths())),
    ("1.6.0 HLS parser: master playlist (variants, audio groups, I-frame and subtitles ignored, BOM, CRLF, ../ and query strings)", StreamParserTests.HlsMaster),
    ("1.6.0 HLS parser: media playlist (durations, derived and explicit IV, key rotation, byte ranges, init section, live and VOD)", StreamParserTests.HlsMedia),
    ("1.6.0 HLS parser: DRM markers, live, bad schemes and malformed input are refused with typed errors", StreamParserTests.HlsRefusals),
    ("1.6.0 HLS parser: 150000 pieces, long lines, oversize and too many variants", StreamParserTests.HlsLimits),
    ("1.6.0 DASH parser: templates (%05d, startNumber, BaseURL at three levels, timescale), selection, estimate", StreamParserTests.DashTemplates),
    ("1.6.0 DASH parser: SegmentTimeline repeats and $Time$, multi-period, SegmentList, single file, webm, audio only, muxed", StreamParserTests.DashTimelineAndOthers),
    ("1.6.0 DASH parser: live, content protection, xlink, DTD/entity attacks, bad templates and limits are refused", StreamParserTests.DashRefusals),
    ("1.6.0 DASH parser: ISO durations and template expansion", StreamParserTests.DashDurationsAndTemplates),
    ("1.6.0 stream selection and manifest detection by content", async () => { await StreamParserTests.Selection(); await StreamParserTests.Sniffer(); }),
    ("1.6.0 fuzz: truncated, mutated and random input never throws and stays bounded", StreamParserTests.Fuzz),
    ("1.6.0 tool: fake download installs with pinned hashes, only the program is kept, version recorded", () => ToolTests.InstallAndVerify(Paths())),
    ("1.6.0 tool: tampered, oversized, wrong-host, http, redirect, zip-slip, zip-bomb and wrong-entry archives are refused", () => ToolTests.InstallRefusals(Paths())),
    ("1.6.0 tool: arguments are passed as they are, timeout kills, file is held while running, changed program is not started", () => ToolTests.RunAndLaunchChecks(Paths())),
    ("1.6.0 tool: ffmpeg from PATH is used only when the user allowed it", () => ToolTests.PathSettingDefaultsOff(Paths())),
    ("1.6.0 tool: argument templates (mux, rewrap, probe) never re-encode", ToolTests.Commands),
    ("1.6.0 tool: pinned constants are well formed and documented", ToolTests.PinConstants),
    ("1.6.0 stream engine: HLS transport stream pieces are joined in order, exact bytes, work folder removed", () => StreamEngineTests.HlsTransportStream(Paths())),
    ("1.6.0 stream engine: AES-128 with derived and explicit IVs and key rotation decrypts to the plaintext hash; wrong and short keys fail", () => StreamEngineTests.HlsAes128(Paths())),
    ("1.6.0 stream engine: HLS fragmented MP4 gives ftyp, moov, then fragments", () => StreamEngineTests.HlsFragmentedMp4(Paths())),
    ("1.6.0 stream engine: byte-range HLS (explicit and implicit offsets), also when the server ignores Range", () => StreamEngineTests.HlsByteRanges(Paths())),
    ("1.6.0 stream engine: inspect lists variants, the chosen variant is downloaded, others are not requested", () => StreamEngineTests.HlsVariantSelection(Paths())),
    ("1.6.0 stream engine: separate audio track is muxed through the tool with exact arguments", () => StreamEngineTests.HlsSeparateAudioWithTool(Paths())),
    ("1.6.0 stream engine: without the tool picture and sound are saved as two files with the needs-muxer note", () => StreamEngineTests.HlsWithoutToolSavesTwoFiles(Paths())),
    ("1.6.0 stream engine: two saved files are joined later through the tool; a missing or failing tool changes nothing", () => StreamEngineTests.JoinSavedStreamLater(Paths())),
    ("1.6.0 stream engine: transport stream as MP4 with and without the tool", () => StreamEngineTests.TransportStreamAsMp4(Paths())),
    ("1.6.0 stream engine: failed, empty, wrong-length and hung tool runs fall back to separate files (mux-failed)", () => StreamEngineTests.MuxFailuresFallBack(Paths())),
    ("1.6.0 stream engine: pause, restart of the manager, resume; finished pieces are not requested again", () => StreamEngineTests.PauseResumeAndRestart(Paths())),
    ("1.6.0 stream engine: speed limit holds, also live", () => StreamEngineTests.SpeedLimitIsHonoured(Paths())),
    ("1.6.0 stream engine: connection count is bounded (3 and 16), options validate", () => StreamEngineTests.ConnectionsAreBounded(Paths())),
    ("1.6.0 stream engine: one failure per piece is retried; a persistent one fails after three tries", () => StreamEngineTests.RetriesAndPersistentFailure(Paths())),
    ("1.6.0 stream engine: 403 after success stops in plain words; referer ladder; 404 and 410 name the piece; a web page is refused", () => StreamEngineTests.ForbiddenAndMissing(Paths())),
    ("1.6.0 stream engine: identity forwarded to playlist, keys and pieces; cookie only to the manifest origin", () => StreamEngineTests.RequestIdentityAndCookies(Paths())),
    ("1.6.0 stream engine: https to http downgrade of pieces or keys is refused", StreamEngineTests.DowngradeIsRefused),
    ("1.6.0 stream engine: not enough free space refuses before any piece is requested", () => StreamEngineTests.FreeSpaceRefusal(Paths())),
    ("1.6.0 stream engine: remove (running and paused) deletes the work folder", () => StreamEngineTests.RemoveCleansTheWorkFolder(Paths())),
    ("1.6.0 stream engine: queue.json round trip with stream fields; a 1.5.9 queue restores", () => StreamEngineTests.QueueRoundTrip(Paths())),
    ("1.6.0 stream engine: a plain download of a playlist or manifest is refused with the stream hint; music lists still download", () => StreamEngineTests.PlainDownloadRefusesManifests(Paths())),
    ("1.6.0 stream engine: inspect by content; live, DRM and non-stream addresses are refused as errors and as job failures", () => StreamEngineTests.InspectByContent(Paths())),
    ("1.6.0 stream engine: DASH video + audio muxed through the tool, video only is a valid fragmented MP4", () => StreamEngineTests.DashVideoAndAudio(Paths())),
    ("1.6.0 stream engine: DASH single-file representation becomes a plain ranged download", () => StreamEngineTests.DashSingleFileIsAPlainDownload(Paths())),
    ("1.6.0 stream engine: a real generated stream decodes after joining (skipped without ffmpeg in PATH)", () => StreamEngineTests.RealMediaPlays(Paths()))
};
var only = args.SkipWhile(a => a != "--only").Skip(1).FirstOrDefault(); // run just the tests whose name contains this text (to tell a flake from a failure)
foreach (var test in tests.Where(t => only is null || t.Name.Contains(only, StringComparison.OrdinalIgnoreCase)))
{
    var clock = Stopwatch.StartNew();
    try { await test.Run(); passed++; Console.WriteLine($"PASS {test.Name} ({clock.ElapsedMilliseconds} ms)"); }
    catch (Exception ex) { failures.Add(test.Name); Console.WriteLine($"FAIL {test.Name}: {ex}"); }
}
Console.WriteLine($"RESULT {passed}/{(only is null ? tests.Length : passed + failures.Count)} passed; {failures.Count} failed");
Console.WriteLine($"Artifacts: {root}");
if (failures.Count == 0) Directory.Delete(root, true);
return failures.Count == 0 ? 0 : 1;

(string Download, string State) Paths()
{
    var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
    return (Path.Combine(directory, "downloads"), Path.Combine(directory, "state"));
}
DownloadManager Manager((string Download, string State) paths, int concurrent = 2, int retry = 2) => new(paths.Download, paths.State, new DownloadOptions { MaxConcurrentDownloads = concurrent, MaxSegments = 4, MinimumSegmentSize = 65536, RetryCount = retry, RequestTimeout = TimeSpan.FromSeconds(3) });
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
async Task Wait(Func<bool> condition, string message, int seconds = 15)
{
    var clock = Stopwatch.StartNew();
    while (!condition())
    {
        if (clock.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException(message);
        await Task.Delay(20);
    }
}
DownloadJob Job(DownloadManager manager, string id) => manager.Jobs.Single(j => j.Id == id);
async Task<DownloadJob> Complete(DownloadManager manager, string id)
{
    await Wait(() => Job(manager, id).Status is "Completed" or "Failed", "Download did not finish");
    var job = Job(manager, id);
    Check(job.Status == "Completed", "Unexpected failure: " + job.Error);
    Check(job.Progress == 100 && job.TotalBytes == job.DownloadedBytes, "Invalid completed progress");
    return job;
}
void Exact(DownloadJob job, byte[] expected) => Check(FixtureServer.Hash(File.ReadAllBytes(job.FilePath)) == FixtureServer.Hash(expected), "Downloaded file SHA-256 differs");

async Task Ranges()
{
    using var server = new FixtureServer(); using var manager = Manager(Paths());
    var added = await manager.AddAsync(new(server.BaseUrl + "/range", "ranges.bin"));
    var job = await Complete(manager, added.Id); Exact(job, server.Payload);
    Check(server.PeakRanges is > 1 and <= 4, "Expected bounded parallel ranges");
    Check(server.Ranges.Count(r => r.Path == "/range" && r.End > r.Start) == 4, "Expected four data ranges");
}
async Task Streaming()
{
    using var server = new FixtureServer(); using var manager = Manager(Paths());
    var job = await manager.AddAsync(new(server.BaseUrl + "/no-range", Referer: server.BaseUrl + "/page", Cookie: "session=fixture"));
    Exact(await Complete(manager, job.Id), server.Payload);
    Check(server.Ranges.Count == 0 && server.Requests("/no-range") == 1, "1.5.7: the first answer is the whole file and is streamed; no second request");
    Check(server.Headers.Where(h => h.Path == "/no-range").All(h => h.Cookie == "session=fixture" && h.Referer == server.BaseUrl + "/page"), "Header forwarding failed");
}
async Task UnknownAndEmpty()
{
    using var server = new FixtureServer(); using var manager = Manager(Paths());
    var chunked = await manager.AddAsync(new(server.BaseUrl + "/chunked"));
    Exact(await Complete(manager, chunked.Id), server.Payload);
    var empty = await manager.AddAsync(new(server.BaseUrl + "/empty"));
    Exact(await Complete(manager, empty.Id), Array.Empty<byte>());
}
async Task PauseResume()
{
    using var server = new FixtureServer(); using var manager = Manager(Paths());
    var j = await manager.AddScheduledAsync(new(server.BaseUrl + "/range"), DateTimeOffset.UtcNow.AddHours(1));
    manager.SetSpeedLimit(j.Id, 400000); manager.Schedule(j.Id, DateTimeOffset.UtcNow);
    await Wait(() => Job(manager, j.Id).DownloadedBytes >= 65536, "No download progress");
    manager.Pause(j.Id); await Task.Delay(250);
    var paused = Job(manager, j.Id); var before = server.Ranges.Count;
    Check(paused.Status == "Paused" && paused.DownloadedBytes < paused.TotalBytes, "Pause failed");
    await Task.Delay(200); Check(Job(manager, j.Id).DownloadedBytes == paused.DownloadedBytes, "Paused transfer still advanced");
    manager.SetSpeedLimit(j.Id, null); manager.Resume(j.Id);
    Exact(await Complete(manager, j.Id), server.Payload);
    Check(server.Ranges.Skip(before).Any(r => r.End > 0 && r.Start % (server.Payload.LongLength / 4) != 0), "Resume did not use saved range offsets");
}
async Task Restore()
{
    using var server = new FixtureServer(); var paths = Paths(); string id;
    using (var manager = Manager(paths))
    {
        var j = await manager.AddScheduledAsync(new(server.BaseUrl + "/range"), DateTimeOffset.UtcNow.AddHours(1)); id = j.Id;
        manager.SetSpeedLimit(id, 400000); manager.Schedule(id, DateTimeOffset.UtcNow);
        await Wait(() => Job(manager, id).DownloadedBytes >= 65536, "No progress before restore");
        bool refused = false; try { using var other = Manager(paths); } catch (IOException) { refused = true; }
        Check(refused, "Second owner was not refused");
    }
    Check(File.Exists(Path.Combine(paths.State, "queue.json")), "Queue not persisted");
    using (var manager = Manager(paths))
    {
        Check(Job(manager, id).Status == "Paused" && Job(manager, id).DownloadedBytes > 0, "Interrupted job did not restore safely");
        manager.SetSpeedLimit(id, null); manager.Resume(id); Exact(await Complete(manager, id), server.Payload);
    }
    using var restored = Manager(paths);
    Check(Job(restored, id).Status == "Completed", "Completed state did not restore"); Exact(Job(restored, id), server.Payload);
}
async Task ChangedValidator()
{
    using var server = new FixtureServer(); using var manager = Manager(Paths());
    var j = await manager.AddScheduledAsync(new(server.BaseUrl + "/range"), DateTimeOffset.UtcNow.AddHours(1));
    manager.SetSpeedLimit(j.Id, 400000); manager.Schedule(j.Id, DateTimeOffset.UtcNow);
    await Wait(() => Job(manager, j.Id).DownloadedBytes >= 65536, "No progress"); manager.Pause(j.Id); await Task.Delay(250);
    Volatile.Write(ref server.Version, 2); manager.SetSpeedLimit(j.Id, null); manager.Resume(j.Id);
    Exact(await Complete(manager, j.Id), server.Replacement);
}
async Task BadRanges()
{
    using var server = new FixtureServer(); using var manager = Manager(Paths());
    var invalid = await manager.AddAsync(new(server.BaseUrl + "/invalid-range")); Exact(await Complete(manager, invalid.Id), server.Payload);
    var changed = await manager.AddAsync(new(server.BaseUrl + "/changed-segment")); Exact(await Complete(manager, changed.Id), server.Replacement);
}
async Task Validators()
{
    using var server = new FixtureServer(); using var manager = Manager(Paths());
    foreach (var path in new[] { "/no-validator", "/unsafe-date", "/weak", "/date-validator" })
    {
        var j = await manager.AddAsync(new(server.BaseUrl + path)); Exact(await Complete(manager, j.Id), server.Payload);
        var dataRanges = server.Ranges.Count(r => r.Path == path && r.End > 0);
        Check(path == "/date-validator" ? dataRanges == 4 : dataRanges == 0, "Unsafe validator handling for " + path + " (data ranges " + dataRanges + ")");
    }
}
async Task RetryFailure()
{
    using var server = new FixtureServer(); using var manager = Manager(Paths(), retry: 2);
    var retry = await manager.AddAsync(new(server.BaseUrl + "/retry")); Exact(await Complete(manager, retry.Id), server.Payload);
    Check(server.Requests("/retry") == 3 && Job(manager, retry.Id).Attempts == "Attempt 1: 503, Attempt 2 (without Range): 503, Attempt 3 (referer: site root): 200", "Unexpected retry count or trail: " + server.Requests("/retry") + " " + Job(manager, retry.Id).Attempts);
    using var other = Manager(Paths(), retry: 2); // a manager of its own: what worked for the site (above) is remembered per manager
    var failed = await other.AddAsync(new(server.BaseUrl + "/fail"));
    await Wait(() => Job(other, failed.Id).Status == "Failed", "Failure not reported");
    Check(server.Requests("/fail") == 5 && Job(other, failed.Id).Error != null && !File.Exists(failed.FilePath), "Failure retries or publication unsafe: " + server.Requests("/fail") + " " + Job(other, failed.Id).Error);
}
async Task Truncated()
{
    using var server = new FixtureServer(); using var manager = Manager(Paths(), retry: 1);
    var j = await manager.AddAsync(new(server.BaseUrl + "/truncate"));
    await Wait(() => Job(manager, j.Id).Status == "Failed", "Truncated response did not fail");
    Check(server.Requests("/truncate") == 2 && !File.Exists(j.FilePath), "Truncation retries/publication unsafe");
}
async Task Names()
{
    using var server = new FixtureServer(); var paths = Paths(); using var manager = Manager(paths);
    File.WriteAllText(Path.Combine(paths.Download, "same.bin"), "existing");
    var same = await manager.AddAsync(new(server.BaseUrl + "/range", "same.bin"));
    var duplicate = await manager.AddAsync(new(server.BaseUrl + "/range", "same.bin"));
    Check(same.FileName != duplicate.FileName && same.FileName != "same.bin", "Collision handling failed");
    Exact(await Complete(manager, same.Id), server.Payload); Exact(await Complete(manager, duplicate.Id), server.Payload);
    Check(File.ReadAllText(Path.Combine(paths.Download, "same.bin")) == "existing", "Existing destination overwritten");
    foreach (var malicious in new[] { "../../escape.exe", "..\\..\\escape.exe", "CON.txt", "LPT1", "name:stream", "trailing. ", "\r\n/*?\0", "...", new string('x', 400) + ".bin" })
    {
        var job = await manager.AddScheduledAsync(new(server.BaseUrl + "/range", malicious), DateTimeOffset.UtcNow.AddHours(1));
        Check(Path.GetDirectoryName(job.FilePath) == Path.GetFullPath(paths.Download) && job.FileName.Length <= 180 && !job.FileName.EndsWith('.') && !job.FileName.EndsWith(' ') && job.FileName.IndexOfAny("<>:\"/\\|?*".ToCharArray()) == -1, "Unsafe filename: " + job.FileName);
        Check(job.FileName is not ("CON.txt" or "LPT1"), "Reserved device name not escaped");
    }
    // A different process may create the reserved name during the transfer.
    var late = await manager.AddScheduledAsync(new(server.BaseUrl + "/range", "late.bin"), DateTimeOffset.UtcNow.AddHours(1));
    File.WriteAllText(late.FilePath, "late-existing");
    var unrelated = Path.Combine(paths.Download, ".ndm-" + late.Id + ".tmp");
    File.WriteAllText(unrelated, "unrelated"); manager.Schedule(late.Id, DateTimeOffset.UtcNow);
    var done = await Complete(manager, late.Id); Exact(done, server.Payload);
    Check(done.FilePath != late.FilePath && File.ReadAllText(late.FilePath) == "late-existing", "Late collision overwritten");
    manager.Remove(late.Id); Check(File.ReadAllText(unrelated) == "unrelated", "Staging cleanup touched an unrelated destination file");
}
async Task Remove()
{
    using var server = new FixtureServer(); var paths = Paths(); using var manager = Manager(paths);
    var completed = await manager.AddAsync(new(server.BaseUrl + "/range")); var done = await Complete(manager, completed.Id);
    manager.Remove(done.Id); Check(File.Exists(done.FilePath) && manager.Jobs.Count == 0, "Remove deleted completed file");
    var partial = await manager.AddScheduledAsync(new(server.BaseUrl + "/range"), DateTimeOffset.UtcNow.AddHours(1));
    manager.SetSpeedLimit(partial.Id, 400000); manager.Schedule(partial.Id, DateTimeOffset.UtcNow);
    await Wait(() => Job(manager, partial.Id).DownloadedBytes > 0, "No partial progress"); manager.Remove(partial.Id);
    await Wait(() => !Directory.Exists(Path.Combine(paths.State, partial.Id + ".parts")), "Partial directory not removed");
    Check(!File.Exists(partial.FilePath) && manager.Jobs.Count == 0, "Removed partial published");
}
async Task Limits()
{
    using var server = new FixtureServer(); using var manager = Manager(Paths(), concurrent: 1);
    var j = await manager.AddScheduledAsync(new(server.BaseUrl + "/range"), DateTimeOffset.UtcNow.AddHours(1));
    manager.SetSpeedLimit(j.Id, 512000); manager.Schedule(j.Id, DateTimeOffset.UtcNow);
    var clock = Stopwatch.StartNew(); var second = await manager.AddAsync(new(server.BaseUrl + "/range"));
    await Task.Delay(300); Check(Job(manager, second.Id).Status == "Queued", "Concurrent job limit exceeded");
    Exact(await Complete(manager, j.Id), server.Payload); Check(clock.Elapsed >= TimeSpan.FromSeconds(3.5), "Aggregate speed limit not enforced");
    Exact(await Complete(manager, second.Id), server.Payload);
    Check(server.PeakRanges <= 4, "Concurrent connections exceeded segment/job bounds");
}
async Task Scheduling()
{
    using var server = new FixtureServer(); var paths = Paths(); string id;
    var due = DateTimeOffset.UtcNow.AddSeconds(1.3);
    using (var manager = Manager(paths))
    {
        var j = await manager.AddScheduledAsync(new(server.BaseUrl + "/range"), due); id = j.Id;
        await Task.Delay(250); Check(server.Requests("/range") == 0 && Job(manager, id).Status == "Scheduled", "Scheduled job started early");
    }
    using var restored = Manager(paths);
    Check(Job(restored, id).ScheduledAt == due, "Schedule not persisted"); Exact(await Complete(restored, id), server.Payload);
}
async Task Input()
{
    using var server = new FixtureServer(); using var manager = Manager(Paths());
    foreach (var request in new[] { new DownloadRequest("file:///C:/secret"), new DownloadRequest("not a URL"), new DownloadRequest(server.BaseUrl, Cookie: "a=b\r\nInjected: x"), new DownloadRequest(server.BaseUrl, Referer: "file:///test") })
    {
        bool rejected = false; try { await manager.AddAsync(request); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "Bad input not rejected");
    }
    var added = await manager.AddScheduledAsync(new(server.BaseUrl + "/range"), DateTimeOffset.UtcNow.AddHours(1));
    manager.Resume(added.Id); Check(added.Status == "Scheduled", "Snapshots should be detached");
    manager.Pause(added.Id); Check(added.Status == "Scheduled" && Job(manager, added.Id).Status == "Paused", "Snapshot mutated");
    manager.Changed += () => throw new Exception("observer error");
    manager.Schedule(added.Id, DateTimeOffset.UtcNow); Exact(await Complete(manager, added.Id), server.Payload);
}
Task Corrupt()
{
    var paths = Paths(); Directory.CreateDirectory(paths.State); var queue = Path.Combine(paths.State, "queue.json");
    File.WriteAllText(queue, "{broken"); bool rejected = false;
    try { using var manager = Manager(paths); } catch (InvalidDataException) { rejected = true; }
    Check(rejected && File.ReadAllText(queue) == "{broken", "Corrupt queue was silently discarded"); return Task.CompletedTask;
}
async Task StreamingPause()
{
    using var server = new FixtureServer(); using var manager = Manager(Paths());
    var j = await manager.AddScheduledAsync(new(server.BaseUrl + "/no-range"), DateTimeOffset.UtcNow.AddHours(1));
    manager.SetSpeedLimit(j.Id, 400000); manager.Schedule(j.Id, DateTimeOffset.UtcNow);
    await Wait(() => Job(manager, j.Id).DownloadedBytes >= 65536, "No streaming progress"); manager.Pause(j.Id); await Task.Delay(250);
    manager.SetSpeedLimit(j.Id, null); manager.Resume(j.Id); Exact(await Complete(manager, j.Id), server.Payload);
    Check(server.Requests("/no-range") == 2, "Streaming resume should request again and restart from zero");
}
async Task RangeRetry()
{
    using var server = new FixtureServer(); using var manager = Manager(Paths(), retry: 1);
    var j = await manager.AddAsync(new(server.BaseUrl + "/range-retry")); Exact(await Complete(manager, j.Id), server.Payload);
    Check(server.Ranges.Any(r => r.Path == "/range-retry" && r.Start > 0 && r.Start % (server.Payload.LongLength / 4) != 0), "Retry did not preserve validated partial ranges");
    Check(server.Requests("/range-retry") <= 10, "Range retries unbounded");
}
async Task LiveSpeedChange()
{
    using var server = new FixtureServer(); using var manager = Manager(Paths());
    var j = await manager.AddScheduledAsync(new(server.BaseUrl + "/range"), DateTimeOffset.UtcNow.AddHours(1));
    manager.SetSpeedLimit(j.Id, 1); manager.Schedule(j.Id, DateTimeOffset.UtcNow);
    await Wait(() => Job(manager, j.Id).TotalBytes > 0, "Probe did not finish"); await Task.Delay(300);
    Check(Job(manager, j.Id).DownloadedBytes == 0, "One byte/second limit bypassed");
    manager.SetSpeedLimit(j.Id, null); Exact(await Complete(manager, j.Id), server.Payload);
}
async Task Redirects()
{
    using var origin = new FixtureServer(); using var target = new FixtureServer(); using var manager = Manager(Paths(), retry: 0);
    origin.RedirectTarget = target.BaseUrl;
    var j = await manager.AddAsync(new(origin.BaseUrl + "/redirect", Cookie: "secret=original")); Exact(await Complete(manager, j.Id), target.Payload);
    Check(origin.Headers.All(h => h.Cookie == "secret=original") && target.Headers.All(h => h.Cookie == null), "Cookie leaked across redirect origins");
    var switched = await manager.AddAsync(new(origin.BaseUrl + "/redirect-switch")); Exact(await Complete(manager, switched.Id), origin.Replacement);
    var loop = await manager.AddAsync(new(origin.BaseUrl + "/redirect-loop")); await Wait(() => Job(manager, loop.Id).Status == "Failed", "Redirect loop did not fail");
    Check(origin.Requests("/redirect-loop") == 10, "Redirect limit not enforced");
}
async Task ObserverDispose()
{
    using var server = new FixtureServer(); var manager = Manager(Paths());
    var j = await manager.AddScheduledAsync(new(server.BaseUrl + "/range"), DateTimeOffset.UtcNow.AddHours(1));
    var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    manager.Changed += () => { manager.Dispose(); done.TrySetResult(); };
    manager.Schedule(j.Id, DateTimeOffset.UtcNow);
    await done.Task.WaitAsync(TimeSpan.FromSeconds(5)); manager.Dispose();
}
