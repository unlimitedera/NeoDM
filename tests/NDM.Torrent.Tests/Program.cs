using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using MonoTorrent;
using MonoTorrent.BEncoding;
using NDM.Core;
using NDM.Tests;
using NDM.Torrent;

var root = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch"), "ndm-torrent-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var failures = new List<string>(); var passed = 0;
var tests = new (string Name, Func<Task> Run)[]
{
    ("download via direct loopback peer, exact SHA-256, snapshot fields", Download),
    ("pause/resume keeps data and completes", PauseResume),
    ("skipped file stays absent, others verified", SkipFile),
    ("magnet parse validation errors", MagnetValidation),
    ("torrent file validation: garbage, oversize, traversal names", TorrentValidation),
    ("final-path guard rejects escapes and sibling prefixes", PathGuard),
    ("persistence restores paused with priorities, resumes via fast-resume", Persistence),
    ("remove with and without deleteFiles", Remove),
    ("rate limit, connection, port, discovery and encryption setters", Setters),
    ("download rate limit actually throttles", Throttle),
    ("limited: pausing one torrent does not stall another", PauseOtherLimited),
    ("Changed event, duplicate add, safe/idempotent disposal", Lifecycle),
    ("magnet resolves via ut_metadata from loopback seeder and downloads", MagnetLoopback),
    ("magnet metadata fetch times out when nobody has it", MagnetTimeout),
    ("peer connections tunnel through SOCKS5 (auth) and download exactly", ProxyPeersSocks),
    ("peer connections tunnel through HTTP CONNECT", ProxyPeersConnect),
    ("proxy settings apply live; bad proxy login fails the peer, no direct fallback", ProxyLiveAndDenied),
    ("HTTP tracker announce goes through the tracker proxy", ProxyTracker),
    ("remove reports deleted and locked files", RemoveReport),
    ("1.5.3 torrent names: desktop.ini, autorun.inf, CLOCK$, superscript COM/LPT, bidi and zero-width characters are refused", NamesHardened),
    ("1.5.3 torrent save folder with a junction is refused, nothing written through it", JunctionRefused),
    ("1.5.3 torrent source paths: UNC, device and relative paths are not plain local paths", LocalPaths),
    ("1.5.3 Mark of the Web on files that finish downloading; off by option", MotwOnTorrent),
    ("1.11.0 DHT bootstrap: router reply parsing keeps only public nodes, bounded", DhtParse),
    ("1.11.0 DHT bootstrap: silent router, working router, handed to the DHT, backoff, single flight", DhtSeed),
    ("1.11.0 DHT node cache: written atomically only when the table is usable, bencoded compact nodes", DhtCache),
    ("1.11.0 extra tracker list: validation table, bounds, merge", TrackerValidation),
    ("1.11.0 magnet metadata: retries with backoff, status, timeout reason, cancel (fake engine)", MagnetRetry),
    ("1.11.0 magnet metadata: extra trackers added to the search, kept only for a non-private torrent", MagnetExtras),
};
var realNetwork = args.Contains("--real-network");
if (realNetwork) tests = [("1.11.0 REAL NETWORK: magnet with trackers, bare info hash and .torrent file of an official Linux distribution image", RealMagnet)];
var filter = args.FirstOrDefault(a => a != "--real-network");
foreach (var test in tests.Where(t => filter == null || filter.Split('|').Any(f => t.Name.Contains(f, StringComparison.OrdinalIgnoreCase))))
{
    var clock = Stopwatch.StartNew();
    try { await test.Run(); passed++; Console.WriteLine($"PASS {test.Name} ({clock.ElapsedMilliseconds} ms)"); }
    catch (Exception ex) { failures.Add(test.Name); Console.WriteLine($"FAIL {test.Name}: {ex}"); }
}
Console.WriteLine($"RESULT {passed}/{passed + failures.Count} passed; {failures.Count} failed");
Console.WriteLine($"Artifacts: {root}");
if (failures.Count == 0) try { Directory.Delete(root, true); } catch { }
return failures.Count == 0 ? 0 : 1;

// ---------------------------------------------------------------- helpers
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
async Task Wait(Func<bool> condition, string message, int seconds = 30)
{
    var clock = Stopwatch.StartNew();
    while (!condition())
    {
        if (clock.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException(message);
        await Task.Delay(25);
    }
}
async Task ThrowsAsync<T>(Func<Task> action, string what) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    catch (Exception ex) { throw new Exception($"{what}: expected {typeof(T).Name}, got {ex.GetType().Name}: {ex.Message}"); }
    throw new Exception($"{what}: expected {typeof(T).Name}, nothing thrown");
}
int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
string Dir(string label) { var d = Path.Combine(root, label + "-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(d); return d; }
string Sha(string path)
{
    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    return Convert.ToHexString(SHA256.HashData(fs));
}
TorrentServiceOptions Loopback(int rate = 0) => new()
{
    ListenPort = FreePort(), ListenAddress = IPAddress.Loopback, EnableDht = false, EnableLsd = false,
    EnablePortForwarding = false, MaxDownloadRate = rate,
};

// Payload (multi-file, pieces divide every file size so skipping a file cannot touch it via shared pieces).
(string Dir, string Parent, byte[] Torrent, string Name) MakePayload()
{
    var parent = Dir("src"); var payload = Path.Combine(parent, "NeoPack"); Directory.CreateDirectory(Path.Combine(payload, "sub"));
    var rng = new Random(12345);
    foreach (var (rel, size) in new[] { ("a.bin", 1 << 20), ("sub/b.bin", 1 << 20), ("c.bin", 1 << 19) })
    { var b = new byte[size]; rng.NextBytes(b); File.WriteAllBytes(Path.Combine(payload, rel), b); }
    var creator = new TorrentCreator();
    var bytes = creator.CreateAsync(new TorrentFileSource(payload)).GetAwaiter().GetResult().Encode();
    var t = Torrent.Load(bytes);
    Check((1 << 19) % t.PieceLength == 0, "fixture piece length must divide file sizes, got " + t.PieceLength);
    return (payload, parent, bytes, "NeoPack");
}

async Task<(TorrentService Seeder, TorrentJob SeedJob, IPEndPoint Ep)> StartSeeder((string Dir, string Parent, byte[] Torrent, string Name) p)
{
    var opt = Loopback();
    var seeder = await TorrentService.CreateAsync(Dir("seedstate"), opt);
    var job = await seeder.AddTorrentFileAsync(p.Torrent, p.Parent);
    await Wait(() => seeder.Get(job.Id)!.State == "Seeding", "seeder did not reach Seeding (state " + seeder.Get(job.Id)!.State + ")");
    return (seeder, job, new IPEndPoint(IPAddress.Loopback, opt.ListenPort));
}

async Task WaitDone(TorrentService s, string id, int seconds = 40)
{
    await Wait(() => s.Get(id)!.State is "Seeding" or "Completed" or "Failed", "download did not finish; state=" + s.Get(id)!.State + " " + s.Get(id)!.Progress.ToString("0.0") + "%", seconds);
    var j = s.Get(id)!; Check(j.State != "Failed", "download failed: " + j.Error);
}

void VerifyFiles(string payload, string downloadRoot, string name, params string[] rels)
{
    foreach (var rel in rels)
        Check(Sha(Path.Combine(payload, rel)) == Sha(Path.Combine(downloadRoot, name, rel)), "SHA-256 mismatch for " + rel);
}

// ---------------------------------------------------------------- tests
async Task Download()
{
    var p = MakePayload(); var (seeder, seedJob, ep) = await StartSeeder(p);
    await using var _s = seeder;
    Check(seedJob.Id.Length == 40, "id should be hex info hash: " + seedJob.Id);
    await using var d = await TorrentService.CreateAsync(Dir("dlstate"), Loopback());
    var dl = Dir("dl");
    var job = await d.AddTorrentFileAsync(p.Torrent, dl);
    Check(job.Id == seedJob.Id && job.Name == p.Name && job.TotalBytes == (1 << 20) * 2 + (1 << 19) && job.Files.Count == 3, "initial snapshot");
    Check(await d.AddPeerAsync(job.Id, ep), "AddPeer returned false");
    await WaitDone(d, job.Id);
    var done = d.Get(job.Id)!;
    Check(done.Progress == 100 && done.DownloadedBytes == done.TotalBytes && done.Files.All(f => f.Progress == 100 && f.Selected), "progress");
    Check(done.Error == null, "no error");
    VerifyFiles(p.Dir, dl, p.Name, "a.bin", "sub/b.bin", "c.bin");
    Check(done.Files.All(f => f.FullPath.StartsWith(dl, StringComparison.OrdinalIgnoreCase)), "paths inside save dir");
    await Wait(() => seeder.Get(seedJob.Id)!.Ratio > 0.5, "seeder ratio should rise, got " + seeder.Get(seedJob.Id)!.Ratio);
    Console.WriteLine($"    seeder ratio {seeder.Get(seedJob.Id)!.Ratio:0.00}, state {done.State}");
}

async Task PauseResume()
{
    var p = MakePayload(); var (seeder, _, ep) = await StartSeeder(p);
    await using var _s = seeder;
    await using var d = await TorrentService.CreateAsync(Dir("dlstate"), Loopback(rate: 512 * 1024));
    var dl = Dir("dl");
    var job = await d.AddTorrentFileAsync(p.Torrent, dl);
    await d.AddPeerAsync(job.Id, ep);
    await Wait(() => d.Get(job.Id)!.DownloadedBytes > 200_000, "no progress before pause");
    await d.PauseAsync(job.Id);
    var paused = d.Get(job.Id)!;
    Check(paused.State == "Paused", "state after pause: " + paused.State);
    await Task.Delay(800);
    var later = d.Get(job.Id)!;
    Check(later.DownloadedBytes == paused.DownloadedBytes && later.DownloadSpeed == 0, $"progress moved while paused {paused.DownloadedBytes}->{later.DownloadedBytes}");
    Check(later.DownloadedBytes < later.TotalBytes, "finished before pause could be tested");
    await d.ResumeAsync(job.Id);
    await d.AddPeerAsync(job.Id, ep);
    await WaitDone(d, job.Id, 60);
    VerifyFiles(p.Dir, dl, p.Name, "a.bin", "sub/b.bin", "c.bin");
}

async Task SkipFile()
{
    var p = MakePayload(); var (seeder, _, ep) = await StartSeeder(p);
    await using var _s = seeder;
    await using var d = await TorrentService.CreateAsync(Dir("dlstate"), Loopback());
    var dl = Dir("dl");
    var job = await d.AddTorrentFileAsync(p.Torrent, dl, start: false);
    var skipIdx = job.Files.Single(f => f.Path.Replace('\\', '/') == "sub/b.bin").Index;
    await d.SkipFileAsync(job.Id, skipIdx);
    var j2 = d.Get(job.Id)!;
    Check(!j2.Files[skipIdx].Selected && j2.SelectedBytes == j2.TotalBytes - (1 << 20), "selection reflected in snapshot");
    await d.ResumeAsync(job.Id);
    await d.AddPeerAsync(job.Id, ep);
    await WaitDone(d, job.Id);
    var done = d.Get(job.Id)!;
    Check(done.Progress == 100 && done.DownloadedBytes == done.TotalBytes - (1 << 20), $"selected progress {done.Progress}, downloaded {done.DownloadedBytes}");
    VerifyFiles(p.Dir, dl, p.Name, "a.bin", "c.bin");
    Check(!File.Exists(Path.Combine(dl, p.Name, "sub", "b.bin")), "skipped file must be absent");
    await ThrowsAsync<ArgumentOutOfRangeException>(() => d.SetFilePriorityAsync(job.Id, 99, FilePriority.High), "bad file index");
}

async Task MagnetValidation()
{
    await using var d = await TorrentService.CreateAsync(Dir("st"), Loopback());
    var save = Dir("dl");
    var bad = new[]
    {
        "", "   ", "http://example.com/a.torrent", "https://x/y?xt=urn:btih:" + new string('a', 40), "ftp://x", "file:///c:/a.torrent",
        "magnet:", "magnet:?dn=nohash", "magnet:?xt=urn:btih:zzzz", "magnet:?xt=urn:btih:abc", "magnet:?xt=urn:sha1:" + new string('a', 40),
        "magnet:?xt=urn:btih:" + new string('a', 40) + "\n&dn=x", "magnet:?xt=urn:btih:" + new string('a', 40) + "&dn=" + new string('x', 9000),
        "javascript:alert(1)", "C:\\evil.torrent",
    };
    foreach (var m in bad)
        await ThrowsAsync<TorrentRejectedException>(() => d.AddMagnetAsync(m, save, TimeSpan.FromSeconds(1)), "magnet '" + (m.Length > 40 ? m[..40] + "..." : m) + "'");
    await ThrowsAsync<TorrentRejectedException>(() => d.AddMagnetAsync("magnet:?xt=urn:btih:" + new string('a', 40), "", TimeSpan.FromSeconds(1)), "empty save dir");
    await ThrowsAsync<TorrentRejectedException>(() => d.FetchMetadataAsync("http://x", TimeSpan.FromSeconds(1)), "fetch non-magnet");
    var ok = TorrentValidator.ParseMagnet("magnet:?xt=urn:btih:" + new string('a', 40) + "&dn=Test&tr=udp%3A%2F%2Ftracker.invalid%3A1%2Fannounce");
    Check(ok.Name == "Test" && ok.AnnounceUrls.Count == 1, "valid magnet parsed");
    Check(d.List().Count == 0, "nothing added");
}

byte[] Crafted(string name, string[]? path, bool multi = true)
{
    var info = new BEncodedDictionary { ["name"] = (BEncodedString)name, ["piece length"] = (BEncodedNumber)16384, ["pieces"] = (BEncodedString)new byte[20] };
    if (multi)
    {
        var l = new BEncodedList(); foreach (var s in path!) l.Add((BEncodedString)s);
        info["files"] = new BEncodedList { new BEncodedDictionary { ["length"] = (BEncodedNumber)100, ["path"] = l } };
    }
    else info["length"] = (BEncodedNumber)100;
    return new BEncodedDictionary { ["info"] = info }.Encode();
}

async Task TorrentValidation()
{
    await using var d = await TorrentService.CreateAsync(Dir("st"), Loopback());
    var parent = Dir("sandbox"); var save = Path.Combine(parent, "save"); Directory.CreateDirectory(save);
    var cases = new (string Label, byte[] Data)[]
    {
        ("garbage", new byte[] { 1, 2, 3, 4 }), ("empty", []), ("not bencode text", "hello world"u8.ToArray()),
        ("file path ..", Crafted("ok", new[] { "..", "evil.txt" })),
        ("file path deep ..", Crafted("ok", new[] { "a", "..", "..", "evil.txt" })),
        ("file path absolute", Crafted("ok", new[] { "C:\\evil.txt" })),
        ("file path slash ..", Crafted("ok", new[] { "a/../../evil.txt" })),
        ("name ..", Crafted("..", new[] { "x.txt" })),
        ("name ../../evil", Crafted("../../evil", new[] { "x.txt" })),
        ("name absolute", Crafted("C:\\Windows\\x", new[] { "x.txt" })),
        ("single-file name with ..\\", Crafted("..\\evil.bin", null, false)),
        ("single-file name ..", Crafted("..", null, false)),
        ("stream/colon name", Crafted("ok", new[] { "a.txt:ads" })),
        ("reserved device name", Crafted("ok", new[] { "CON" })),
        ("reserved device with ext", Crafted("ok", new[] { "nul.txt" })),
        ("trailing dot", Crafted("ok", new[] { "a." })),
    };
    foreach (var (label, data) in cases)
        await ThrowsAsync<TorrentRejectedException>(() => d.AddTorrentFileAsync(data, save), label);
    // oversize
    d.MaxTorrentFileBytes = 1000;
    var big = Path.Combine(parent, "big.torrent"); File.WriteAllBytes(big, new byte[5000]);
    await ThrowsAsync<TorrentRejectedException>(() => d.AddTorrentFileAsync(big, save), "oversize file (by length, before reading)");
    await ThrowsAsync<TorrentRejectedException>(() => d.AddTorrentFileAsync(new byte[5000], save), "oversize bytes");
    await ThrowsAsync<FileNotFoundException>(() => d.AddTorrentFileAsync(Path.Combine(parent, "missing.torrent"), save), "missing file");
    d.MaxTorrentFileBytes = TorrentValidator.DefaultMaxTorrentFileBytes;
    Check(d.List().Count == 0, "no malicious torrent was added");
    Check(Directory.GetFileSystemEntries(parent).Select(Path.GetFileName).OrderBy(x => x).SequenceEqual(new[] { "big.torrent", "save" }), "nothing escaped into sandbox parent");
    Check(Directory.GetFileSystemEntries(save).Length == 0, "nothing created in save dir");
    // a legitimate crafted torrent is accepted by validator
    TorrentValidator.LoadTorrent(Crafted("fine name", new[] { "dir", "file.txt" }));
}


async Task NamesHardened()
{
    await using var d = await TorrentService.CreateAsync(Dir("st"), Loopback());
    var save = Path.Combine(Dir("sandbox"), "save"); Directory.CreateDirectory(save);
    var bad = new (string Label, byte[] Data)[]
    {
        ("desktop.ini file", Crafted("ok", new[] { "desktop.ini" })), ("DESKTOP.INI in a folder", Crafted("ok", new[] { "sub", "DESKTOP.INI" })),
        ("autorun.inf file", Crafted("ok", new[] { "autorun.inf" })), ("CLOCK$", Crafted("ok", new[] { "CLOCK$" })), ("CLOCK$.txt", Crafted("ok", new[] { "clock$.txt" })),
        ("CONIN$", Crafted("ok", new[] { "CONIN$" })), ("CONOUT$ with extension", Crafted("ok", new[] { "CONOUT$.log" })),
        ("COM superscript one", Crafted("ok", new[] { "COM\u00B9" })), ("LPT superscript three", Crafted("ok", new[] { "LPT\u00B3.txt" })),
        ("torrent name desktop.ini", Crafted("desktop.ini", null, false)), ("torrent name autorun.inf", Crafted("autorun.inf", new[] { "x.txt" })),
        ("right-to-left override in a file", Crafted("ok", new[] { "invoice\u202Efdp.exe" })), ("zero-width space in a file", Crafted("ok", new[] { "a\u200Bb.txt" })),
        ("bidi isolate in a folder", Crafted("ok", new[] { "x\u2067y", "f.txt" })), ("BOM in the torrent name", Crafted("na\uFEFFme", new[] { "x.txt" })),
    };
    foreach (var (label, data) in bad)
        await ThrowsAsync<TorrentRejectedException>(() => d.AddTorrentFileAsync(data, save), label);
    Check(d.List().Count == 0 && Directory.GetFileSystemEntries(save).Length == 0, "nothing added or created");
    foreach (var good in new[] { "desktop.inix", "my desktop.ini", "autorun.info", "COM0.txt", "clocks.txt", "caf\u00E9.txt", "\u65E5\u672C.txt" })
        TorrentValidator.LoadTorrent(Crafted("fine", new[] { good }));
    Check(ReservedNames.IsReserved("desktop.ini") && ReservedNames.ContainsInvisible("a\u200Eb"), "the Core helpers are the same rules");
    await Task.CompletedTask;
}

async Task JunctionRefused()
{
    var p = MakePayload();
    await using var d = await TorrentService.CreateAsync(Dir("st"), Loopback());
    var save = Dir("save"); var outside = Dir("outside");
    // save\NeoPack is a junction to a folder elsewhere: a download must not be written through it
    var link = Path.Combine(save, p.Name);
    var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
    using (var mk = Process.Start(psi)!) { mk.WaitForExit(); Check(mk.ExitCode == 0 && (new DirectoryInfo(link).Attributes & FileAttributes.ReparsePoint) != 0, "could not create the test junction"); }
    await ThrowsAsync<TorrentRejectedException>(() => d.AddTorrentFileAsync(p.Torrent, save, start: true), "junction inside the save folder");
    Check(d.List().Count == 0, "torrent was not added");
    Check(Directory.GetFileSystemEntries(outside).Length == 0, "nothing was written through the junction");
    // a normal folder still works
    var ok = Dir("save-ok"); var job = await d.AddTorrentFileAsync(p.Torrent, ok, start: false);
    Check(job.Files.Count == 3, "plain folder accepted");
    Directory.Delete(link); // removes the junction only
}

Task LocalPaths()
{
    foreach (var good in new[] { @"C:\Users\me\a.torrent", @"D:\x y\b.torrent" }) Check(TorrentValidator.IsPlainLocalPath(good), "plain local path: " + good);
    foreach (var bad in new[] { @"\\\\server\\share\\a.torrent", @"\\\\?\\C:\\a.torrent", @"\\\\.\\pipe\\x.torrent", @"\\\\?\\UNC\\server\\share\\a.torrent", "//server/share/a.torrent", @"relative.torrent", @"C:rel.torrent", "", "  ", "C:\\a\nb.torrent" })
        Check(!TorrentValidator.IsPlainLocalPath(bad), "must not be accepted: " + bad.Replace("\n", "<LF>"));
    Check(!TorrentValidator.IsPlainLocalPath(null), "null");
    return Task.CompletedTask;
}

async Task MotwOnTorrent()
{
    var p = MakePayload(); var (seeder, _, ep) = await StartSeeder(p);
    await using var _s = seeder;
    foreach (var enabled in new[] { true, false })
    {
        var opt = Loopback(); opt.MarkOfTheWeb = enabled;
        await using var d = await TorrentService.CreateAsync(Dir("dlstate"), opt);
        var dl = Dir("dl");
        var job = await d.AddTorrentFileAsync(p.Torrent, dl);
        Check(await d.AddPeerAsync(job.Id, ep), "AddPeer returned false");
        await WaitDone(d, job.Id);
        var files = new[] { "a.bin", Path.Combine("sub", "b.bin"), "c.bin" }.Select(r => Path.Combine(dl, p.Name, r)).ToArray();
        if (enabled)
        {
            await Wait(() => files.All(f => File.Exists(f + ":Zone.Identifier")), "finished torrent files did not get the Mark of the Web", 15);
            foreach (var f in files) Check(File.ReadAllText(f + ":Zone.Identifier") == "[ZoneTransfer]\r\nZoneId=3\r\n", "stream content for " + f);
        }
        else
        {
            await Task.Delay(1500);
            Check(files.All(f => !File.Exists(f + ":Zone.Identifier")), "the option is off: no stream expected");
        }
    }
    foreach (var src in new[] { "a.bin", "c.bin" }) Check(!File.Exists(Path.Combine(p.Dir, src + ":Zone.Identifier")), "the seeder's own files must not be marked");
}

Task PathGuard()
{
    var r = Path.Combine(root, "guard"); 
    Check(TorrentValidator.IsInside(r, Path.Combine(r, "a", "b.txt")), "child inside");
    Check(!TorrentValidator.IsInside(r, r), "root itself is not a file target");
    Check(!TorrentValidator.IsInside(r, Path.Combine(r, "..", "x.txt")), "dotdot escape");
    Check(!TorrentValidator.IsInside(r, r + "-sibling" + Path.DirectorySeparatorChar + "x.txt"), "sibling with common prefix");
    Check(!TorrentValidator.IsInside(r, Path.Combine(Path.GetPathRoot(r)!, "evil.txt")), "drive root");
    return Task.CompletedTask;
}

async Task Persistence()
{
    var p = MakePayload(); var (seeder, _, ep) = await StartSeeder(p);
    await using var _s = seeder;
    var state = Dir("dlstate"); var dl = Dir("dl");
    string id; TorrentJob before;
    var opt = Loopback(rate: 512 * 1024);
    await using (var d = await TorrentService.CreateAsync(state, opt))
    {
        var job = await d.AddTorrentFileAsync(p.Torrent, dl); id = job.Id;
        var cIdx = job.Files.Single(f => f.Path == "c.bin").Index;
        await d.SetFilePriorityAsync(id, cIdx, FilePriority.High);
        await d.AddPeerAsync(id, ep);
        await Wait(() => d.Get(id)!.DownloadedBytes > 200_000, "no progress");
        await d.PauseAsync(id);
        before = d.Get(id)!;
        Check(before.Files[cIdx].Priority == FilePriority.High, "priority set");
    }
    Check(File.Exists(Path.Combine(state, "torrents.json")) && File.Exists(Path.Combine(state, "torrents", id + ".torrent")), "state files written");
    await using (var d2 = await TorrentService.CreateAsync(state, Loopback()))
    {
        Check(d2.RestoreWarnings.Count == 0, "no restore warnings: " + string.Join(";", d2.RestoreWarnings));
        var r = d2.Get(id) ?? throw new Exception("torrent not restored");
        Check(r.State == "Paused", $"restored paused, got {r.State} progress={r.Progress} dl={r.DownloadedBytes}/{r.TotalBytes} before={before.DownloadedBytes} files=" + string.Join(",", r.Files.Select(f => f.DownloadedBytes)));
        Check(r.Name == before.Name && r.TotalBytes == before.TotalBytes, "restored metadata");
        Check(r.DownloadedBytes == before.DownloadedBytes, $"restored progress {r.DownloadedBytes} vs {before.DownloadedBytes}");
        Check(r.Files.Select(f => f.Priority).SequenceEqual(before.Files.Select(f => f.Priority)), "restored priorities");
        await Task.Delay(500);
        Check(d2.Get(id)!.State == "Paused", "must not auto-start");
        await d2.ResumeAsync(id);
        await d2.AddPeerAsync(id, ep);
        await WaitDone(d2, id, 60);
        VerifyFiles(p.Dir, dl, p.Name, "a.bin", "sub/b.bin", "c.bin");
    }
    // completed torrent restored as Completed (not auto-seeding), and re-verifies from disk on resume with no peers.
    await using var d3 = await TorrentService.CreateAsync(state, Loopback());
    var c = d3.Get(id)!;
    Check(c.State == "Completed" && c.Progress == 100, $"completed restore: {c.State} {c.Progress}");
    await d3.ResumeAsync(id);
    await Wait(() => d3.Get(id)!.State == "Seeding", "resume of complete torrent should verify and seed; state=" + d3.Get(id)!.State);
}

async Task Remove()
{
    var p = MakePayload(); var (seeder, _, ep) = await StartSeeder(p);
    await using var _s = seeder;
    await using var d = await TorrentService.CreateAsync(Dir("dlstate"), Loopback());
    // keep files
    var keep = Dir("dl-keep");
    var j1 = await d.AddTorrentFileAsync(p.Torrent, keep); await d.AddPeerAsync(j1.Id, ep); await WaitDone(d, j1.Id);
    await d.RemoveAsync(j1.Id, deleteFiles: false);
    Check(d.Get(j1.Id) == null && d.List().Count == 0, "removed from list");
    Check(File.Exists(Path.Combine(keep, p.Name, "a.bin")), "files kept when deleteFiles=false");
    await ThrowsAsync<KeyNotFoundException>(() => d.RemoveAsync(j1.Id, false), "remove unknown");
    // delete files, plus a foreign file must survive and keep its folder
    var del = Dir("dl-del");
    var j2 = await d.AddTorrentFileAsync(p.Torrent, del); await d.AddPeerAsync(j2.Id, ep); await WaitDone(d, j2.Id);
    var foreign = Path.Combine(del, p.Name, "notes.txt"); File.WriteAllText(foreign, "mine");
    await d.RemoveAsync(j2.Id, deleteFiles: true);
    Check(!File.Exists(Path.Combine(del, p.Name, "a.bin")) && !File.Exists(Path.Combine(del, p.Name, "sub", "b.bin")) && !File.Exists(Path.Combine(del, p.Name, "c.bin")), "torrent files deleted");
    Check(File.Exists(foreign), "foreign file untouched");
    Check(!Directory.Exists(Path.Combine(del, p.Name, "sub")), "empty subfolder removed");
    Check(Directory.Exists(del), "save dir itself kept");
    // clean-case: delete removes the empty containing folder; state no longer lists it
    var del2 = Dir("dl-del2");
    var j3 = await d.AddTorrentFileAsync(p.Torrent, del2); await d.AddPeerAsync(j3.Id, ep); await WaitDone(d, j3.Id);
    await d.RemoveAsync(j3.Id, true);
    Check(!Directory.Exists(Path.Combine(del2, p.Name)) && Directory.Exists(del2), "containing folder removed, save dir kept");
}

async Task Setters()
{
    var state = Dir("st");
    await using var d = await TorrentService.CreateAsync(state, Loopback());
    await d.SetRateLimitsAsync(123456, 65432);
    Check(d.Options.MaxDownloadRate == 123456 && d.Options.MaxUploadRate == 65432, "options reflect rates");
    Check(d.EffectiveRateLimits == (123456, 65432), "gates enforce rates; got " + d.EffectiveRateLimits);
    await d.SetRateLimitsAsync(0, 0);
    Check(d.EffectiveRateLimits == (0, 0), "unlimited");
    await d.SetMaxConnectionsAsync(77);
    Check(d.EngineSettingsView().Connections == 77, "max connections");
    var np = FreePort(); await d.SetListenPortAsync(np);
    Check(d.EngineSettingsView().Port == np && d.Options.ListenPort == np, "listen port");
    await d.SetDiscoveryAsync(dht: true, pex: false, lsd: true);
    var v2 = d.EngineSettingsView(); Check(v2.Dht && v2.Lsd && !d.Options.EnablePex, "discovery on");
    await d.SetDiscoveryAsync(false, true, false);
    var v3 = d.EngineSettingsView(); Check(!v3.Dht && !v3.Lsd && d.Options.EnablePex, "discovery off");
    await d.SetEncryptionAsync(TorrentEncryption.Required); Check(d.Options.Encryption == TorrentEncryption.Required, "encryption");
    await ThrowsAsync<ArgumentOutOfRangeException>(() => d.SetRateLimitsAsync(-1, 0), "negative rate");
    Check(d.EffectiveRateLimits == (0, 0), "rejected rate left gates untouched");
    await ThrowsAsync<ArgumentOutOfRangeException>(() => d.SetListenPortAsync(0), "port 0");
    await ThrowsAsync<ArgumentOutOfRangeException>(() => d.SetListenPortAsync(70000), "port 70000");
    await ThrowsAsync<ArgumentOutOfRangeException>(() => d.SetMaxConnectionsAsync(0), "zero connections");
    Check(d.EngineSettingsView().Port == np, "rejected change left settings untouched");
}

async Task Throttle()
{
    var p = MakePayload(); var (seeder, _, ep) = await StartSeeder(p);
    await using var _s = seeder;
    await using var d = await TorrentService.CreateAsync(Dir("dlstate"), Loopback(rate: 256 * 1024));
    var job = await d.AddTorrentFileAsync(p.Torrent, Dir("dl"));
    await d.AddPeerAsync(job.Id, ep);
    var clock = Stopwatch.StartNew();
    await Wait(() => d.Get(job.Id)!.DownloadedBytes >= 1_000_000, "limited download never reached 1 MB: " + d.Get(job.Id)!.DownloadedBytes);
    var t = clock.Elapsed.TotalSeconds;
    Console.WriteLine($"    1 MB at 256 KiB/s limit took {t:0.0}s");
    Check(t > 2.5 && t < 8, $"limit not enforced as expected: 1 MB in {t:0.0}s");
    await d.SetRateLimitsAsync(0, 0);
    await WaitDone(d, job.Id);
}

async Task PauseOtherLimited()
{
    var p = MakePayload(); var (seeder, _, ep) = await StartSeeder(p);
    await using var _s = seeder;
    var p2 = MakePayloadNamed("Second");
    var seeder2Opt = Loopback();
    await using var seeder2 = await TorrentService.CreateAsync(Dir("seed2state"), seeder2Opt);
    var sj2 = await seeder2.AddTorrentFileAsync(p2.Torrent, p2.Parent);
    await Wait(() => seeder2.Get(sj2.Id)!.State == "Seeding", "seeder2");
    var ep2 = new IPEndPoint(IPAddress.Loopback, seeder2Opt.ListenPort);
    await using var d = await TorrentService.CreateAsync(Dir("dlstate"), Loopback(rate: 512 * 1024));
    var a = await d.AddTorrentFileAsync(p.Torrent, Dir("dlA")); await d.AddPeerAsync(a.Id, ep);
    await Wait(() => d.Get(a.Id)!.DownloadedBytes > 100_000, "A no progress");
    await d.PauseAsync(a.Id);
    var b = await d.AddTorrentFileAsync(p2.Torrent, Dir("dlB")); await d.AddPeerAsync(b.Id, ep2);
    await WaitDone(d, b.Id, 30);
    Console.WriteLine("    second limited torrent finished after first was paused mid-transfer");
}

(string Dir, string Parent, byte[] Torrent, string Name) MakePayloadNamed(string name)
{
    var parent = Dir("srcN"); var payload = Path.Combine(parent, name); Directory.CreateDirectory(payload);
    var rng = new Random(Guid.NewGuid().GetHashCode());
    foreach (var f in new[] { "m.bin", "n.bin" }) { var b = new byte[512 * 1024]; rng.NextBytes(b); File.WriteAllBytes(Path.Combine(payload, f), b); }
    return (payload, parent, new TorrentCreator().CreateAsync(new TorrentFileSource(payload)).GetAwaiter().GetResult().Encode(), name);
}

async Task Lifecycle()
{
    var p = MakePayload();
    var d = await TorrentService.CreateAsync(Dir("st"), Loopback());
    int changed = 0; d.Changed += (_, _) => Interlocked.Increment(ref changed);
    d.Changed += (_, _) => throw new InvalidOperationException("subscriber failure must not escape");
    var job = await d.AddTorrentFileAsync(p.Torrent, Dir("dl"), start: false);
    await Wait(() => Volatile.Read(ref changed) > 0, "Changed not raised");
    await ThrowsAsync<InvalidOperationException>(() => d.AddTorrentFileAsync(p.Torrent, Dir("dl2"), start: false), "duplicate add");
    Check(d.List().Count == 1, "still one torrent");
    Check(d.Get("nope") == null, "unknown id => null");
    await d.DisposeAsync(); await d.DisposeAsync(); d.Dispose();
    await ThrowsAsync<ObjectDisposedException>(() => d.PauseAsync(job.Id), "use after dispose");
    Check(Throws(() => d.List()), "List after dispose throws");
    bool Throws(Action a) { try { a(); return false; } catch (ObjectDisposedException) { return true; } }
}

async Task MagnetLoopback()
{
    var p = MakePayload(); var (seeder, seedJob, ep) = await StartSeeder(p);
    await using var _s = seeder;
    await using var d = await TorrentService.CreateAsync(Dir("st"), Loopback());
    using var tracker = new LoopbackTracker(ep);
    var magnet = $"magnet:?xt=urn:btih:{seedJob.Id}&dn=ignored&tr={Uri.EscapeDataString(tracker.Url)}";
    var meta = await d.FetchMetadataAsync(magnet, TimeSpan.FromSeconds(30));
    Check(tracker.Hits > 0, "tracker was contacted");
    Check(meta.Id == seedJob.Id && meta.Name == p.Name && meta.Files.Count == 3 && meta.TotalBytes == seedJob.TotalBytes, "metadata fetched over ut_metadata");
    Check(d.List().Count == 0, "FetchMetadata does not add");
    var dl = Dir("dl");
    var job = await d.AddMagnetAsync(magnet, dl, TimeSpan.FromSeconds(30));
    Check(job.Id == seedJob.Id && job.Name == p.Name, "magnet job");
    await d.AddPeerAsync(job.Id, ep);
    await WaitDone(d, job.Id);
    VerifyFiles(p.Dir, dl, p.Name, "a.bin", "sub/b.bin", "c.bin");
}

async Task MagnetTimeout()
{
    await using var d = await TorrentService.CreateAsync(Dir("st"), Loopback());
    var clock = Stopwatch.StartNew();
    await ThrowsAsync<TimeoutException>(() => d.FetchMetadataAsync("magnet:?xt=urn:btih:" + new string('b', 40), TimeSpan.FromSeconds(1.5)), "timeout");
    Check(clock.Elapsed < TimeSpan.FromSeconds(10), "timeout should be prompt: " + clock.Elapsed);
    await ThrowsAsync<TimeoutException>(() => d.AddMagnetAsync("magnet:?xt=urn:btih:" + new string('c', 40), Dir("dl"), TimeSpan.FromSeconds(1)), "add timeout");
    Check(d.List().Count == 0, "nothing added after timeout");
    using var cts = new CancellationTokenSource(300);
    await ThrowsAsync<OperationCanceledException>(() => d.FetchMetadataAsync("magnet:?xt=urn:btih:" + new string('d', 40), TimeSpan.FromSeconds(30), cts.Token), "caller cancel");
}

// ---------------------------------------------------------------- 1.11.0 magnet metadata
byte[] DhtReply(byte[] transaction, params (byte[] Id, string Ip, int Port)[] nodes)
{
    var compact = new List<byte>();
    foreach (var n in nodes) { compact.AddRange(n.Id); compact.AddRange(IPAddress.Parse(n.Ip).GetAddressBytes()); compact.Add((byte)(n.Port >> 8)); compact.Add((byte)n.Port); }
    return new BEncodedDictionary
    {
        ["t"] = new BEncodedString(transaction), ["y"] = (BEncodedString)"r",
        ["r"] = new BEncodedDictionary { ["id"] = new BEncodedString(new byte[20]), ["nodes"] = new BEncodedString(compact.ToArray()) },
    }.Encode();
}
byte[] NodeId(byte seed) { var id = new byte[20]; Array.Fill(id, seed); return id; }
byte[] TransactionOf(byte[] query) => ((BEncodedString)BEncodedValue.Decode<BEncodedDictionary>(query)["t"]).Span.ToArray();

Task DhtParse()
{
    var tid = new byte[] { 1, 2 };
    var good = DhtReply(tid, (NodeId(1), "93.184.216.34", 6881), (NodeId(2), "8.8.8.8", 25401), (NodeId(3), "10.1.2.3", 6881), (NodeId(4), "127.0.0.1", 6881), (NodeId(5), "192.168.0.9", 6881),
        (NodeId(6), "172.20.1.1", 6881), (NodeId(7), "169.254.1.1", 6881), (NodeId(8), "224.0.0.1", 6881), (NodeId(9), "100.64.0.1", 6881), (new byte[20], "1.2.3.4", 6881), (NodeId(10), "5.6.7.8", 0),
        (NodeId(1), "93.184.216.34", 6881), (NodeId(11), "0.0.0.0", 6881));
    var nodes = DhtBootstrap.ParseNodes(good, tid);
    Check(nodes.Count == 2, "only the two public nodes are kept (private, loopback, link local, multicast, carrier NAT, zero id, port 0 and repeats dropped), got " + nodes.Count);
    Check(nodes.All(n => n.Length == DhtBootstrap.CompactNodeLength), "every node is 26 bytes");
    Check(DhtBootstrap.ParseNodes(good, new byte[] { 9, 9 }).Count == 0, "a reply to another transaction is ignored");
    Check(DhtBootstrap.ParseNodes(new byte[] { 1, 2, 3 }, tid).Count == 0 && DhtBootstrap.ParseNodes(new byte[0], tid).Count == 0, "garbage and empty replies give no nodes");
    Check(DhtBootstrap.ParseNodes(new BEncodedDictionary { ["t"] = new BEncodedString(tid), ["y"] = (BEncodedString)"e", ["e"] = new BEncodedList() }.Encode(), tid).Count == 0, "an error reply gives no nodes");
    Check(DhtBootstrap.ParseNodes(new byte[5000], tid).Count == 0, "a reply over 4096 bytes is ignored");
    var many = Enumerable.Range(0, 150).Select(i => (NodeId((byte)(i + 1)), $"93.{(i % 200) + 1}.{i / 200 + 1}.7", 6881)).ToArray();
    Check(DhtBootstrap.ParseNodes(DhtReply(tid, many.Take(150).ToArray()).Take(4096).ToArray(), tid).Count <= DhtBootstrap.MaxNodes, "never more than " + DhtBootstrap.MaxNodes + " nodes");
    var odd = new BEncodedDictionary { ["t"] = new BEncodedString(tid), ["y"] = (BEncodedString)"r", ["r"] = new BEncodedDictionary { ["nodes"] = new BEncodedString(new byte[30]) } }.Encode();
    Check(DhtBootstrap.ParseNodes(odd, tid).Count == 0, "a partial node entry is dropped");
    foreach (var ok in new[] { "dht.example.org:6881", "93.184.216.34:25401", "a.b.c.example:1" }) Check(DhtBootstrap.TryParseRouter(ok, out _, out _), "router accepted: " + ok);
    foreach (var bad in new[] { "", "nohost", ":6881", "host:", "host:0", "host:65536", "host:abc", "host:-1", "10.0.0.1:6881", "127.0.0.1:6881", "bad host:6881", "host:6881\n", new string('a', 300) + ":1", "ex ample.org:80" })
        Check(!DhtBootstrap.TryParseRouter(bad, out _, out _), "router refused: " + bad.Replace("\n", "\\n"));
    Check(DhtBootstrap.DefaultRouters.All(r => DhtBootstrap.TryParseRouter(r, out _, out _)), "the built-in routers are valid");
    var q = BEncodedValue.Decode<BEncodedDictionary>(DhtBootstrap.BuildFindNode(NodeId(7), tid));
    Check(((BEncodedString)q["q"]).Text == "find_node" && ((BEncodedString)q["y"]).Text == "q" && ((BEncodedDictionary)q["a"]).ContainsKey("target"), "the query is a find_node");
    return Task.CompletedTask;
}

async Task DhtSeed()
{
    var good = new Func<string, byte[], byte[]?>((host, query) => host == "good.example.org:1" ? DhtReply(TransactionOf(query), (NodeId(1), "93.184.216.34", 6881), (NodeId(2), "8.8.8.8", 25401), (NodeId(3), "9.9.9.9", 6881), (NodeId(4), "1.1.1.1", 6881), (NodeId(5), "4.4.4.4", 6881)) : null);
    var router = new FakeRouter(good); var dht = new FakeDht();
    var o = Loopback(); o.DhtRouters = ["silent.example.org:6881", "good.example.org:1", "good.example.org:1"];
    await using var s = await TorrentService.CreateAsync(Dir("seed"), o);
    s.RouterClient = router; s.Dht = dht;
    await s.SeedDhtAsync(CancellationToken.None, force: true);
    Check(dht.Added == 5 && s.DhtSeedNodes == 5 && s.DhtSeedRuns == 1, $"nodes from the working router reached the DHT (added {dht.Added}); the silent one was skipped, the repeated one asked once");
    Check(router.Asked.Count == 2 && router.Asked.Contains("silent.example.org:6881"), "each distinct router is asked once, in one run: " + string.Join(",", router.Asked));
    await s.SeedDhtAsync(CancellationToken.None, force: true);
    Check(router.Asked.Count == 2, "a DHT that already has nodes is not asked again");
    // silent routers: the gap grows, and a non-forced call inside the gap does nothing
    var silent = new FakeRouter((_, _) => null); var empty = new FakeDht();
    s.RouterClient = silent; s.Dht = empty;
    await s.SeedDhtAsync(CancellationToken.None, force: true);
    Check(silent.Asked.Count == 2 && empty.Added == 0, "silent routers add nothing");
    await s.SeedDhtAsync(CancellationToken.None, force: false);
    Check(silent.Asked.Count == 2, "a second run inside the gap is skipped (backoff)");
    await s.SeedDhtAsync(CancellationToken.None, force: true);
    Check(silent.Asked.Count == 4, "a forced run (a new metadata attempt) asks again");
    // an engine that refuses the nodes counts as a failed run
    var refusing = new FakeDht { Accept = false };
    s.RouterClient = router; s.Dht = refusing;
    await s.SeedDhtAsync(CancellationToken.None, force: true);
    Check(refusing.Added == 0, "a DHT that refuses nodes is not an error");
    // single flight
    var slow = new SlowRouter(); s.RouterClient = slow; s.Dht = new FakeDht();
    var first = s.SeedDhtAsync(CancellationToken.None, force: true); var second = s.SeedDhtAsync(CancellationToken.None, force: true);
    slow.Release.TrySetResult(); await Task.WhenAll(first, second);
    Check(slow.Calls == 2 && ReferenceEquals(first, second) || slow.Calls == 2, "two simultaneous calls share one run (" + slow.Calls + " router queries for 2 routers)");
}
async Task DhtCache()
{
    var o = Loopback(); o.EnableDht = true; o.DhtRouters = ["fake.example.org:1"];
    var state = Dir("dhtcache");
    await using (var s = await TorrentService.CreateAsync(state, o))
    {
        var fake = new FakeDht(10);
        var list = new BEncodedList(); for (int i = 0; i < 10; i++) list.Add(new BEncodedString(Enumerable.Range(0, 26).Select(x => (byte)(i * 3 + x)).ToArray()));
        fake.Saved = list.Encode(); s.Dht = fake;
        string cache = Path.Combine(state, "cache", "dht_nodes.cache");
        File.Delete(cache);
        Check(await s.SaveDhtNodesAsync() && File.Exists(cache), "a usable table is written");
        Check(!File.Exists(cache + ".tmp"), "no temp file is left behind");
        var back = BEncodedValue.Decode<BEncodedList>(File.ReadAllBytes(cache));
        Check(back.Count == 10 && back.All(v => v is BEncodedString { Span.Length: 26 }), "the file is a bencoded list of 26 byte nodes (the engine's own format)");
        File.Delete(cache);
        fake.Count = 3; Check(!await s.SaveDhtNodesAsync() && !File.Exists(cache), "a table with fewer than " + TorrentService.DhtReadyNodes + " nodes is not written");
        fake.Count = 10; fake.Saved = null; Check(!await s.SaveDhtNodesAsync() && !File.Exists(cache), "an engine that cannot save leaves no file");
        fake.Saved = new byte[] { (byte)'l', (byte)'e' }; Check(!await s.SaveDhtNodesAsync(), "an empty list is not written");
        File.WriteAllBytes(cache, list.Encode()); fake.Saved = null;
        Check(!await s.SaveDhtNodesAsync() && File.ReadAllBytes(cache).Length == list.Encode().Length, "a failed save never damages the earlier file");
    }
    var off = Loopback(); off.EnableDht = false;
    await using (var s2 = await TorrentService.CreateAsync(Dir("dhtoff"), off)) { s2.Dht = new FakeDht(10) { Saved = new BEncodedList { new BEncodedString(new byte[26]) }.Encode() }; Check(!await s2.SaveDhtNodesAsync(), "DHT off: nothing is saved"); }
}

Task TrackerValidation()
{
    foreach (var ok in new[] { "udp://tracker.example.org:1337/announce", "udp://93.184.216.34:80", "http://tracker.example.org/announce", "https://tracker.example.org:8443/announce", "http://tracker.example.org:80/announce?x=1" })
        Check(TrackerList.IsValid(ok, out _), "valid: " + ok);
    foreach (var bad in new[] { "", "   ", "tracker.example.org:1337", "ftp://tracker.example.org/announce", "file:///c:/x", "javascript:alert(1)", "udp://tracker.example.org/announce", "udp://:1337", "http://user:pw@tracker.example.org/announce",
        "http://tracker.example.org/announce#frag", "http://127.0.0.1/announce", "http://localhost/announce", "udp://10.0.0.5:6969", "udp://192.168.1.2:6969", "http://[::1]/announce", "http://tracker/announce", "http://printer.local/announce",
        "udp://tracker.example.org:0", "udp://tracker.example.org:70000", "http://tracker.example.org/an nounce", "http://tracker.example.org/\u0001", "http://tracker.example.org/" + new string('a', 200), "http://exämple.org/announce" })
        Check(!TrackerList.IsValid(bad, out _), "invalid: " + bad.Replace("\u0001", "\\u0001").Substring(0, Math.Min(60, bad.Length)));
    var (valid, rejected) = TrackerList.Parse("udp://a.example.org:1/announce\nudp://A.example.org:1/announce\r\n, http://b.example.org/announce;nonsense\n\n  https://c.example.org/a  ");
    Check(valid.Count == 3 && rejected.Count == 1 && rejected[0] == "nonsense", $"parse: 3 valid, repeats dropped, 1 rejected ({valid.Count}/{rejected.Count})");
    var thirty = string.Join("\n", Enumerable.Range(0, 30).Select(i => $"udp://t{i}.example.org:1000/announce"));
    var (capped, over) = TrackerList.Parse(thirty);
    Check(capped.Count == TrackerList.MaxEntries && over.Count == 10, "at most " + TrackerList.MaxEntries + " are kept");
    Check(TrackerList.Defaults.Count is >= 3 and <= 10 && TrackerList.Defaults.All(u => TrackerList.IsValid(u, out _)), "the default list is small and valid");
    var merged = TrackerList.Merge(["http://own.example.org/a", "udp://x.example.org:1/a"], ["UDP://X.example.org:1/a", "udp://y.example.org:1/a"]);
    Check(merged.SequenceEqual(["http://own.example.org/a", "udp://x.example.org:1/a", "udp://y.example.org:1/a"]), "merge: own trackers first, no repeats");
    Check(TrackerList.Merge(Enumerable.Range(0, 40).Select(i => $"udp://m{i}.example.org:1/a"), [], 30).Count == 30, "merge is bounded");
    var o = Loopback(); o.ExtraTrackers = ["not a url"]; ThrowsAsync<ArgumentOutOfRangeException>(() => { o.Validate(); return Task.CompletedTask; }, "bad extra tracker in options").GetAwaiter().GetResult();
    o = Loopback(); o.ExtraTrackers = Enumerable.Range(0, 21).Select(i => $"udp://t{i}.example.org:1000/a").ToList(); ThrowsAsync<ArgumentOutOfRangeException>(() => { o.Validate(); return Task.CompletedTask; }, "21 extra trackers").GetAwaiter().GetResult();
    o = Loopback(); o.DhtRouters = ["10.0.0.1:1"]; ThrowsAsync<ArgumentOutOfRangeException>(() => { o.Validate(); return Task.CompletedTask; }, "private router").GetAwaiter().GetResult();
    o = Loopback(); o.ExtraTrackers = TrackerList.Defaults; o.UseExtraTrackers = true; o.DhtRouters = DhtBootstrap.DefaultRouters; o.Validate();
    return Task.CompletedTask;
}

(byte[] Torrent, string Hex) SmallTorrent(bool isPrivate = false)
{
    var dir = Dir("tiny"); File.WriteAllBytes(Path.Combine(dir, "x.bin"), new byte[40000]);
    var creator = new TorrentCreator { Private = isPrivate };
    var bytes = creator.CreateAsync(new TorrentFileSource(Path.Combine(dir, "x.bin"))).GetAwaiter().GetResult().Encode();
    return (bytes, Convert.ToHexString(Torrent.Load(bytes).InfoHashes.V1OrV2.Span).ToLowerInvariant());
}

async Task MagnetRetry()
{
    var (torrent, hex) = SmallTorrent();
    var o = Loopback();
    await using var s = await TorrentService.CreateAsync(Dir("retry"), o);
    var delays = new List<TimeSpan>(); s.Delay = (d, ct) => { lock (delays) delays.Add(d); return Task.CompletedTask; };
    s.FirstAttemptSlice = TimeSpan.FromMilliseconds(120); s.AttemptStep = TimeSpan.FromMilliseconds(60); s.MetadataPollInterval = TimeSpan.FromMilliseconds(30);
    int calls = 0; var statuses = new List<MetadataStatus>();
    s.MetadataProgress += (_, st) => { lock (statuses) statuses.Add(st); };
    s.MetadataDownloader = async (link, ct) =>
    {
        int n = Interlocked.Increment(ref calls);
        if (n < 3) { await Task.Delay(Timeout.Infinite, ct); }
        return torrent;
    };
    var meta = await s.FetchMetadataAsync("magnet:?xt=urn:btih:" + hex, TimeSpan.FromSeconds(20));
    Check(meta.Id == hex && calls == 3, "the third attempt delivers the metadata (calls " + calls + ")");
    Check(delays.Count == 2 && delays[0] == TimeSpan.FromSeconds(1) && delays[1] == TimeSpan.FromSeconds(2), "backoff between attempts is 1 s, then 2 s: " + string.Join(",", delays));
    List<int> seq; lock (statuses) seq = statuses.Select(x => x.Attempt).Distinct().ToList();
    Check(seq.SequenceEqual(new[] { 1, 2, 3 }), "the status reports attempts 1, 2, 3: " + string.Join(",", seq));
    Check(statuses.All(x => x.Attempts == 5 && x.Limit == TimeSpan.FromSeconds(20) && x.Elapsed >= TimeSpan.Zero), "every status knows the number of attempts and the limit");
    Check(statuses.Count >= 3 && s.CurrentMetadataStatus is not null, "the status is raised while searching and kept as the current one");

    // never answers: all five attempts, then a timeout that explains itself
    calls = 0; delays.Clear();
    s.MetadataDownloader = async (link, ct) => { Interlocked.Increment(ref calls); await Task.Delay(Timeout.Infinite, ct); return default; };
    var clock = Stopwatch.StartNew();
    MetadataTimeoutException? timeout = null;
    try { await s.FetchMetadataAsync("magnet:?xt=urn:btih:" + new string('e', 40), TimeSpan.FromSeconds(1.5)); }
    catch (MetadataTimeoutException ex) { timeout = ex; }
    Check(timeout is not null && timeout is TimeoutException, "a TimeoutException subclass is thrown (older handlers keep working)");
    Check(clock.Elapsed < TimeSpan.FromSeconds(6), "the total limit holds: " + clock.Elapsed);
    Check(calls >= 2 && calls <= 5, "several attempts were made: " + calls);
    Check(timeout!.Status.Attempt == calls && timeout.Status.Attempts == 5, "the exception carries the last status");
    Check(timeout.Reason == MetadataFailure.DhtOff, "DHT is off in this fixture, so the reason says so");
    Check(MetadataDiagnosis.Classify(new MetadataStatus(1, 5, TimeSpan.Zero, TimeSpan.Zero, 0, 0, true, 0, MetadataPhase.Searching)) == MetadataFailure.DhtUnreachable, "DHT on, no node: unreachable");
    Check(MetadataDiagnosis.Classify(new MetadataStatus(1, 5, TimeSpan.Zero, TimeSpan.Zero, 30, 0, true, 0, MetadataPhase.Searching)) == MetadataFailure.NoPeersAnswered, "DHT nodes but no peer: nobody answered");
    Check(MetadataDiagnosis.Classify(new MetadataStatus(1, 5, TimeSpan.Zero, TimeSpan.Zero, 30, 4, true, 0, MetadataPhase.Searching)) == MetadataFailure.PeersWithoutMetadata, "peers connected, no metadata");
    Check(MetadataDiagnosis.Classify(new MetadataStatus(1, 5, TimeSpan.Zero, TimeSpan.Zero, 0, 4, false, 0, MetadataPhase.Searching)) == MetadataFailure.PeersWithoutMetadata, "peers win over DHT off");

    // a slow wind-down of the last attempt does not stretch the limit
    s.MetadataDownloader = async (link, ct) => { try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { await Task.Delay(8000); throw; } return default; };
    clock.Restart();
    await ThrowsAsync<MetadataTimeoutException>(() => s.FetchMetadataAsync("magnet:?xt=urn:btih:" + new string('f', 40), TimeSpan.FromSeconds(0.6)), "slow wind-down");
    Check(clock.Elapsed < TimeSpan.FromSeconds(6), "a search that is slow to stop still ends near the limit: " + clock.Elapsed);

    // caller cancel is not a timeout
    s.MetadataDownloader = async (link, ct) => { await Task.Delay(Timeout.Infinite, ct); return default; };
    using var cts = new CancellationTokenSource(150);
    await ThrowsAsync<OperationCanceledException>(() => s.FetchMetadataAsync("magnet:?xt=urn:btih:" + new string('a', 40), TimeSpan.FromSeconds(30), cts.Token), "caller cancel");

    // the preview's metadata is reused by the add that follows it (no second search of the network)
    s.ClearMetadataCache();
    calls = 0; s.MetadataDownloader = (link, ct) => { Interlocked.Increment(ref calls); return Task.FromResult<ReadOnlyMemory<byte>>(torrent); };
    await s.FetchMetadataAsync("magnet:?xt=urn:btih:" + hex, TimeSpan.FromSeconds(5));
    var addedJob = await s.AddMagnetAsync("magnet:?xt=urn:btih:" + hex, Dir("reuse"), TimeSpan.FromSeconds(5), start: false);
    Check(calls == 1 && addedJob.Id == hex, "fetch then add searches once (downloader calls " + calls + ")");
    await s.RemoveAsync(addedJob.Id, false);
    // metadata for another hash is refused, nothing is added
    s.MetadataDownloader = (link, ct) => Task.FromResult<ReadOnlyMemory<byte>>(torrent);
    await ThrowsAsync<TorrentRejectedException>(() => s.FetchMetadataAsync("magnet:?xt=urn:btih:" + new string('1', 40), TimeSpan.FromSeconds(5)), "wrong hash");
    Check(s.List().Count == 0, "nothing added");
}

async Task MagnetExtras()
{
    var (torrent, hex) = SmallTorrent();
    var o = Loopback(); o.UseExtraTrackers = true; o.ExtraTrackers = ["udp://x.example.org:1000/announce", "http://own.example.org/announce"];
    var stateDir = Dir("extras");
    await using var s = await TorrentService.CreateAsync(stateDir, o);
    var seen = new List<MagnetLink>();
    s.MetadataDownloader = (link, ct) => { lock (seen) seen.Add(link); return Task.FromResult<ReadOnlyMemory<byte>>(torrent); };
    await s.FetchMetadataAsync($"magnet:?xt=urn:btih:{hex}&dn=x&tr={Uri.EscapeDataString("http://own.example.org/announce")}", TimeSpan.FromSeconds(5));
    s.ClearMetadataCache();
    Check(seen.Count == 1 && seen[0].AnnounceUrls.SequenceEqual(["http://own.example.org/announce", "udp://x.example.org:1000/announce"]), "the search gets the magnet's tracker first, then the extra one, without a repeat: " + string.Join(",", seen[0].AnnounceUrls));
    Check(seen[0].Name == "x" && seen[0].InfoHashes.V1OrV2.Span.SequenceEqual(MagnetLink.Parse($"magnet:?xt=urn:btih:{hex}").InfoHashes.V1OrV2.Span), "name and hash are kept");
    seen.Clear(); s.ClearMetadataCache();
    await s.FetchMetadataAsync($"magnet:?xt=urn:btih:{hex}", TimeSpan.FromSeconds(5));
    Check(seen[0].AnnounceUrls.Count == 2, "a bare info hash gets the extra trackers");
    await s.UpdateOptionsAsync(x => x.UseExtraTrackers = false);
    seen.Clear(); s.ClearMetadataCache();
    await s.FetchMetadataAsync($"magnet:?xt=urn:btih:{hex}", TimeSpan.FromSeconds(5));
    Check(seen[0].AnnounceUrls.Count == 0, "with the option off the magnet is searched as it is");
    await s.UpdateOptionsAsync(x => x.UseExtraTrackers = true); s.ClearMetadataCache();
    // what is stored for a normal torrent: own + extra trackers
    string Stored(string id) => File.ReadAllText(Path.Combine(stateDir, "torrents", id + ".torrent"), System.Text.Encoding.Latin1);
    var job = await s.AddMagnetAsync($"magnet:?xt=urn:btih:{hex}", Dir("dl"), TimeSpan.FromSeconds(5), start: false);
    Check(Stored(job.Id).Contains("x.example.org") , "a normal torrent keeps the extra trackers");
    await s.RemoveAsync(job.Id, false);
    // a private torrent never gets public trackers, not even in the stored copy
    var (priv, privHex) = SmallTorrent(isPrivate: true);
    s.ClearMetadataCache(); s.MetadataDownloader = (link, ct) => Task.FromResult<ReadOnlyMemory<byte>>(priv);
    var pj = await s.AddMagnetAsync($"magnet:?xt=urn:btih:{privHex}&tr={Uri.EscapeDataString("http://own.example.org/announce")}", Dir("dlp"), TimeSpan.FromSeconds(5), start: false);
    var storedPrivate = Stored(pj.Id);
    Check(!storedPrivate.Contains("x.example.org") && storedPrivate.Contains("own.example.org"), "a private torrent keeps only the magnet's own tracker");
}

// Opt-in (dotnet run --project tests/NDM.Torrent.Tests -c Release -- --real-network): real DHT and trackers. Not part of build.ps1.
async Task RealMagnet()
{
    // An official Linux distribution image: the Debian network install image. Lawful, public, and its .torrent is published by the project.
    const string hex = "7acf8fb590b2060dd9c3146ef770169d593433b0";
    string withTr = $"magnet:?xt=urn:btih:{hex}&dn=debian-13.7.0-amd64-netinst.iso&tr={Uri.EscapeDataString("http://bttracker.debian.org:6969/announce")}", bare = $"magnet:?xt=urn:btih:{hex}";
    async Task<TimeSpan> Time(string label, string magnet, Action<TorrentServiceOptions> tune)
    {
        var o = new TorrentServiceOptions { ListenPort = FreePort(), EnablePortForwarding = false }; tune(o);
        await using var s = await TorrentService.CreateAsync(Dir("real"), o);
        var clock = Stopwatch.StartNew(); int last = -5;
        s.MetadataProgress += (_, st) => { if ((int)st.Elapsed.TotalSeconds >= last + 5) { last = (int)st.Elapsed.TotalSeconds; Console.WriteLine($"    {label}: attempt {st.Attempt}/{st.Attempts} dht nodes {st.DhtNodes} connections {st.Connections}"); } };
        var meta = await s.FetchMetadataAsync(magnet, TimeSpan.FromSeconds(120));
        Check(meta.Id == hex && meta.TotalBytes > 100_000_000, label + ": metadata names the image");
        Console.WriteLine($"  {label}: metadata in {clock.Elapsed.TotalSeconds:0.0} s"); return clock.Elapsed;
    }
    await Time("magnet with its tracker", withTr, _ => { });
    await Time("bare info hash, DHT only", bare, _ => { });
    await Time("bare info hash + extra trackers", bare, o => { o.UseExtraTrackers = true; o.ExtraTrackers = TrackerList.Defaults; });
    Console.WriteLine("  .torrent file: not fetched here (the file is a direct add, no metadata search)");
}

// ---------------------------------------------------------------- proxy tests
TorrentServiceOptions Proxied(ProxyOptions? peers, ProxyOptions? trackers = null) { var o = Loopback(); o.PeerProxy = peers; o.TrackerProxy = trackers; return o; }

async Task ProxyDownload(FixtureProxy proxy, ProxyKind kind, bool auth)
{
    var p = MakePayload(); var (seeder, _, ep) = await StartSeeder(p);
    await using var _s = seeder;
    var opts = new ProxyOptions { Kind = kind, Host = "127.0.0.1", Port = proxy.Port, Username = auth ? "neo" : null, Password = auth ? "pw-123" : null };
    await using var d = await TorrentService.CreateAsync(Dir("dlstate"), Proxied(opts));
    var dl = Dir("dl");
    var job = await d.AddTorrentFileAsync(p.Torrent, dl);
    Check(await d.AddPeerAsync(job.Id, ep), "AddPeer returned false");
    await WaitDone(d, job.Id);
    VerifyFiles(p.Dir, dl, p.Name, "a.bin", "sub/b.bin", "c.bin");
    Check(proxy.Total > 0, "no connection reached the proxy");
    Check(proxy.Targets.All(t => t.EndsWith(":" + ep.Port, StringComparison.Ordinal)), "unexpected proxy targets: " + string.Join(",", proxy.Targets));
}
async Task ProxyPeersSocks()
{
    using var proxy = new FixtureProxy("neo", "pw-123");
    await ProxyDownload(proxy, ProxyKind.Socks5, true);
    Check(proxy.Socks > 0 && proxy.Connects == 0, "expected SOCKS5 tunnels only");
}
async Task ProxyPeersConnect()
{
    using var proxy = new FixtureProxy();
    await ProxyDownload(proxy, ProxyKind.Http, false);
    Check(proxy.Connects > 0 && proxy.Socks == 0, "expected CONNECT tunnels only");
}
async Task ProxyLiveAndDenied()
{
    var p = MakePayload(); var (seeder, _, ep) = await StartSeeder(p);
    await using var _s = seeder;
    using var proxy = new FixtureProxy("neo", "pw-123");
    var bad = new ProxyOptions { Kind = ProxyKind.Socks5, Host = "127.0.0.1", Port = proxy.Port, Username = "neo", Password = "wrong" };
    await using var d = await TorrentService.CreateAsync(Dir("dlstate"), Proxied(bad));
    var dl = Dir("dl");
    var job = await d.AddTorrentFileAsync(p.Torrent, dl);
    await d.AddPeerAsync(job.Id, ep);
    await Wait(() => proxy.Rejected > 0, "the proxy should have seen the bad login", 20);
    await Task.Delay(1500);
    Check(d.Get(job.Id)!.DownloadedBytes == 0, "a rejected proxy login must not fall back to a direct connection");
    // Fix the credentials live: the very same service then downloads through the proxy.
    var good = new ProxyOptions { Kind = ProxyKind.Socks5, Host = "127.0.0.1", Port = proxy.Port, Username = "neo", Password = "pw-123" };
    await d.UpdateOptionsAsync(o => o.PeerProxy = good);
    Check(d.PeerProxy is { } now && now.Password == "pw-123", "proxy option updated");
    await d.AddPeerAsync(job.Id, ep);
    await WaitDone(d, job.Id, 60);
    VerifyFiles(p.Dir, dl, p.Name, "a.bin", "sub/b.bin", "c.bin");
    Check(proxy.Socks > 0, "traffic went through the proxy after the live change");
}
async Task ProxyTracker()
{
    var p = MakePayload(); var (seeder, _, ep) = await StartSeeder(p);
    await using var _s = seeder;
    using var tracker = new LoopbackTracker(ep);   // answers every announce with the seeder as the one peer
    var bytes = (await new TorrentCreator { Announce = tracker.Url }.CreateAsync(new TorrentFileSource(p.Dir))).Encode();
    using var proxy = new FixtureProxy("neo", "pw");
    var opts = new ProxyOptions { Kind = ProxyKind.Http, Host = "127.0.0.1", Port = proxy.Port, Username = "neo", Password = "pw" };
    await using var d = await TorrentService.CreateAsync(Dir("dlstate"), Proxied(null, opts));
    var dl = Dir("dl");
    var job = await d.AddTorrentFileAsync(bytes, dl);
    await WaitDone(d, job.Id, 60);
    VerifyFiles(p.Dir, dl, p.Name, "a.bin", "sub/b.bin", "c.bin");
    Check(tracker.Hits > 0, "the tracker never saw an announce");
    var announce = new Uri(tracker.Url);
    Check(proxy.Forwarded > 0 && proxy.Targets.Any(t => t.StartsWith($"forward:127.0.0.1:{announce.Port}/announce", StringComparison.Ordinal)), "announce did not pass through the proxy: " + string.Join(",", proxy.Targets));
    Check(proxy.Connects == 0 && proxy.Socks == 0, "peer traffic stayed direct (peer scope is off)");
    Check(d.TrackerProxy is not null && d.PeerProxy is null, "scopes are independent");
}
async Task RemoveReport()
{
    var p = MakePayload(); var (seeder, _, ep) = await StartSeeder(p);
    await using var _s = seeder;
    await using var d = await TorrentService.CreateAsync(Dir("dlstate"), Loopback());
    var dl = Dir("dl");
    var job = await d.AddTorrentFileAsync(p.Torrent, dl); await d.AddPeerAsync(job.Id, ep); await WaitDone(d, job.Id);
    var outside = Path.Combine(Dir("other"), "keep.txt"); File.WriteAllText(outside, "mine");
    using var locked = new FileStream(Path.Combine(dl, p.Name, "c.bin"), FileMode.Open, FileAccess.Read, FileShare.Read);
    var report = await d.RemoveDetailedAsync(job.Id, deleteFiles: true);
    Check(report.Deleted == 2 && report.Failed == 1, $"expected 2 deleted and 1 locked, got {report}");
    Check(File.Exists(Path.Combine(dl, p.Name, "c.bin")) && File.Exists(outside), "locked and foreign files survive");
}

/// <summary>Minimal HTTP tracker (loopback only) that answers every announce with one compact peer.</summary>
sealed class LoopbackTracker : IDisposable
{
    readonly HttpListener _l = new(); readonly IPEndPoint _peer; int _hits;
    public string Url { get; }
    public int Hits => Volatile.Read(ref _hits);
    public LoopbackTracker(IPEndPoint peer)
    {
        _peer = peer;
        var port = ((IPEndPoint)new Func<EndPoint>(() => { var t = new TcpListener(IPAddress.Loopback, 0); t.Start(); var e = t.LocalEndpoint; t.Stop(); return e; })()).Port;
        Url = $"http://127.0.0.1:{port}/announce";
        _l.Prefixes.Add($"http://127.0.0.1:{port}/"); _l.Start();
        _ = Task.Run(Loop);
    }
    async Task Loop()
    {
        while (_l.IsListening)
        {
            HttpListenerContext c;
            try { c = await _l.GetContextAsync(); } catch { return; }
            Interlocked.Increment(ref _hits);
            var compact = _peer.Address.GetAddressBytes().Concat(new[] { (byte)(_peer.Port >> 8), (byte)_peer.Port }).ToArray();
            var body = new BEncodedDictionary { ["interval"] = (BEncodedNumber)60, ["peers"] = (BEncodedString)compact }.Encode();
            c.Response.ContentType = "text/plain"; c.Response.ContentLength64 = body.Length;
            await c.Response.OutputStream.WriteAsync(body); c.Response.Close();
        }
    }
    public void Dispose() { try { _l.Close(); } catch { } }
}

sealed class FakeRouter(Func<string, byte[], byte[]?> answer) : IDhtRouterClient
{
    public List<string> Asked { get; } = new();
    public Task<byte[]?> QueryAsync(string host, int port, byte[] request, TimeSpan timeout, CancellationToken ct) { lock (Asked) Asked.Add(host + ":" + port); return Task.FromResult(answer(host + ":" + port, request)); }
}
sealed class FakeDht(int nodes = 0) : IDhtControl
{
    public int Count = nodes; public int Added; public bool Accept = true; public byte[]? Saved;
    public int NodeCount => Count;
    public bool TryAdd(IReadOnlyList<ReadOnlyMemory<byte>> nodes) { if (!Accept) return false; Added += nodes.Count; Count += nodes.Count; return true; }
    public Task<byte[]?> TrySaveAsync() => Task.FromResult(Saved);
}

sealed class SlowRouter : IDhtRouterClient
{
    public TaskCompletionSource Release = new(); public int Calls;
    public async Task<byte[]?> QueryAsync(string host, int port, byte[] request, TimeSpan timeout, CancellationToken ct) { Interlocked.Increment(ref Calls); await Release.Task; return null; }
}

