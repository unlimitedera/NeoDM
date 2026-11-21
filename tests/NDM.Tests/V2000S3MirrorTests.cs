// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Diagnostics;
using System.Security.Cryptography;
using NDM.Core;

namespace NDM.Tests;

/// <summary>2.0.0 stage S3: parsing fixtures (Link header, Metalink, Digest) and the mirror engine against loopback fixture servers.</summary>
internal static class V2000S3Mirrors
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Wait(Func<bool> condition, string message, int seconds = 30)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { if (clock.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException(message); await Task.Delay(20); }
    }
    private static DownloadJob Job(DownloadManager m, string id) => m.Jobs.Single(j => j.Id == id);
    private static byte[] Data(int seed, int n = 1_000_000) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
    private static string Sha256B64(byte[] d) => Convert.ToBase64String(SHA256.HashData(d));
    private static string Sha256Hex(byte[] d) => Convert.ToHexString(SHA256.HashData(d)).ToLowerInvariant();
    private static DownloadManager Manager((string Download, string State) p, Func<IReadOnlyList<string>, CancellationToken, Task<int?[]>>? probe = null, ProxyOptions? proxy = null, string? suffix = null) =>
        new(p.Download, p.State + (suffix ?? ""), new DownloadOptions { MaxSegments = 1, MinimumSegmentSize = 65536, RetryCount = 0, RequestTimeout = TimeSpan.FromSeconds(5), ConnectProbe = probe ?? ((u, _) => Task.FromResult(u.Select(_ => (int?)10).ToArray())), Proxy = proxy });
    private static Func<IReadOnlyList<string>, CancellationToken, Task<int?[]>> Times(params int[] ms) => (u, _) => Task.FromResult(u.Select((_, i) => i < ms.Length ? (int?)ms[i] : null).ToArray());

    // ---------------------------------------------------------------- parsers

    public static Task LinkHeaderParsing()
    {
        var b = new Uri("https://dl.example.test/pub/file.iso");
        var one = LinkHeader.Parse(new[] { "<https://m1.example.test/file.iso>; rel=duplicate; pri=1; geo=de, <https://m2.example.test/file.iso>; rel=\"duplicate\"; pri=2; geo=us" }, b);
        Check(one.Count == 2 && one[0].Url == "https://m1.example.test/file.iso" && one[0].Priority == 1 && one[0].Geo == "de" && one[0].Rels.SequenceEqual(new[] { "duplicate" }) && one[1].Geo == "us" && one[1].Priority == 2, "Two links in one header: " + one.Count);
        var many = LinkHeader.Parse(new[] { "<https://a.example.test/f>; rel=duplicate", "</relative/f.iso>; rel=duplicate; pri=7", "<https://c.example.test/a,b?x=1>; rel=\"duplicate other\"; title=\"x, y\"" }, b);
        Check(many.Count == 3 && many[1].Url == "https://dl.example.test/relative/f.iso" && many[1].Priority == 7 && many[2].Url == "https://c.example.test/a,b?x=1" && many[2].Rels.SequenceEqual(new[] { "duplicate", "other" }), "Several headers, a relative address, a comma inside the address and a quoted comma");
        var meta = LinkHeader.Parse(new[] { "<https://dl.example.test/file.iso.meta4>; rel=describedby; type=\"application/metalink4+xml\"" }, b);
        Check(meta.Count == 1 && meta[0].Rels.Contains("describedby") && meta[0].Type == "application/metalink4+xml", "A Metalink pointer keeps its type");
        var bad = LinkHeader.Parse(new[] { "<ftp://x.example.test/f>; rel=duplicate", "<javascript:alert(1)>; rel=duplicate", "no angle brackets; rel=duplicate", "<https://ok.example.test/f>; rel=duplicate; pri=abc; geo=1234567890", "<unterminated; rel=duplicate" }, b);
        Check(bad.Count == 1 && bad[0].Url == "https://ok.example.test/f" && bad[0].Priority is null && bad[0].Geo is null, "Other schemes, junk and bad numbers are dropped: " + bad.Count);
        Check(LinkHeader.Parse(new[] { new string('x', 20000) }, b).Count == 0, "A huge header is ignored");
        return Task.CompletedTask;
    }

    private const string Meta4 = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><metalink xmlns=\"urn:ietf:params:xml:ns:metalink\"><file name=\"file.iso\"><size>1000000</size><hash type=\"sha-1\">da39a3ee5e6b4b0d3255bfef95601890afd80709</hash><hash type=\"sha-256\">e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855</hash><hash type=\"md5\">d41d8cd98f00b204e9800998ecf8427e</hash><url location=\"de\" priority=\"1\">https://de.example.test/file.iso</url><url location=\"us\">https://us.example.test/file.iso</url><url priority=\"3\">ftp://old.example.test/file.iso</url><metaurl mediatype=\"torrent\">https://t.example.test/f.torrent</metaurl></file></metalink>";

    public static Task MetalinkParsing()
    {
        var files = Metalink4.Parse(Meta4) ?? throw new Exception("A Metalink 4 file did not parse");
        var f = files.Single();
        Check(f.Name == "file.iso" && f.Size == 1_000_000, "Name and size");
        Check(f.Hashes.Select(h => h.Algorithm).OrderBy(x => x).SequenceEqual(new[] { "sha-1", "sha-256" }), "Supported hashes only (md5 is skipped): " + string.Join(",", f.Hashes.Select(h => h.Algorithm)));
        Check(f.Sources.Count == 2 && f.Sources[0].Url == "https://de.example.test/file.iso" && f.Sources[0].Priority == 1 && f.Sources[0].Location == "de" && f.Sources[1].Priority == 100, "Web sources with priority and location; ftp and torrent entries are not used");
        Check(Metalink4.Parse("<html><body/></html>") is null && Metalink4.Parse("not xml") is null, "Something that is not Metalink gives nothing");
        Check(Metalink4.Parse("<?xml version=\"1.0\"?><!DOCTYPE m [<!ENTITY x SYSTEM \"file:///c:/windows/win.ini\">]><metalink xmlns=\"urn:ietf:params:xml:ns:metalink\"><file name=\"&x;\"/></metalink>") is null, "A DTD with an external entity is refused");
        Check(Metalink4.Parse(new string(' ', Metalink4.MaxBytes + 1)) is null, "A huge file is refused");
        Check(ExpectedHash.FromHex("SHA-256", "zz") is null && ExpectedHash.FromHex("crc32", "00000000") is null && ExpectedHash.FromHex("sha256", new string('A', 64)) is { Algorithm: "sha-256" }, "Hash validation");
        return Task.CompletedTask;
    }

    public static Task DigestParsing()
    {
        var data = Data(9, 5000);
        var sha = Sha256B64(data); var s512 = Convert.ToBase64String(SHA512.HashData(data));
        var d = DigestHeader.Parse(new[] { "SHA-256=" + sha }, Array.Empty<string>());
        Check(d is { Algorithm: "sha-256" } && d.Hex == Sha256Hex(data), "Digest: SHA-256=base64");
        Check(DigestHeader.Parse(new[] { "sha-256=" + sha + ", SHA-512=" + s512 }, Array.Empty<string>()) is { Algorithm: "sha-512" }, "The strongest of several");
        Check(DigestHeader.Parse(Array.Empty<string>(), new[] { "sha-256=:" + sha + ":" }) is { Algorithm: "sha-256" }, "Repr-Digest with colons");
        Check(DigestHeader.Parse(new[] { "md5=Q2hlY2sgSW50ZWdyaXR5IQ==, unixsum=30637" }, Array.Empty<string>()) is null && DigestHeader.Parse(new[] { "sha-256=not base64!!" }, Array.Empty<string>()) is null && DigestHeader.Parse(new[] { "sha-256=AAAA" }, Array.Empty<string>()) is null, "Unsupported algorithms, bad base64 and wrong lengths give nothing");
        var one = DigestHeader.Parse(new[] { "SHA-256=" + Convert.ToBase64String(SHA256.HashData(new byte[] { 7 })) }, Array.Empty<string>())!;
        Check(DigestHeader.DescribesOnlyThisByte(one, 7) && !DigestHeader.DescribesOnlyThisByte(one, 8) && !DigestHeader.DescribesOnlyThisByte(d!, data[0]), "A digest of the one probe byte is recognised");
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- engine

    /// <summary>Mirrors named by the server's Link header are checked: same size and ranges = verified; another size, no ranges and an unreachable host are rejected with a reason. Automatic keeps the original when nothing is clearly faster.</summary>
    public static async Task MirrorValidation((string Download, string State) p)
    {
        using var a = new MirrorServer(); using var b = new MirrorServer(); using var c = new MirrorServer(); using var d = new MirrorServer();
        string dead; using (var e = new MirrorServer()) dead = e.Url("/f");
        var data = Data(1);
        b.Add("/f", data); c.Add("/f", Data(2, 900_000)); d.Add("/f", data, r => r.Ranges = false);
        a.Add("/f", data, r => r.Headers.Add($"Link: <{b.Url("/f")}>; rel=duplicate; pri=1; geo=de, <{c.Url("/f")}>; rel=duplicate; pri=2, <{d.Url("/f")}>; rel=duplicate; pri=3, <{dead}>; rel=duplicate; pri=4"));
        using var m = Manager(p);
        var j = await m.AddAsync(new(a.Url("/f"), "v.bin"));
        await Wait(() => Job(m, j.Id).Status is "Completed" or "Failed", "Finish");
        var done = Job(m, j.Id);
        Check(done.Status == "Completed" && File.ReadAllBytes(done.FilePath).SequenceEqual(data), "The download completed with the right bytes: " + done.Error);
        var s = done.Sources;
        Check(s.Count == 5 && s[0].IsPrimary && s[0].IsActive, "The original is listed first and is the one in use: " + s.Count);
        MirrorInfo Of(MirrorServer x) => s.Single(i => i.Url == x.Url("/f"));
        Check(Of(b) is { State: MirrorState.Verified, Geo: "de", Origin: MirrorOrigin.Header, Priority: 1 }, "A mirror with the same size and ranges is verified, with its geo hint");
        Check(Of(c) is { State: MirrorState.Rejected, Reason: MirrorReasons.Size }, "A mirror of another size is rejected: " + Of(c).Reason);
        Check(Of(d) is { State: MirrorState.Rejected, Reason: MirrorReasons.Ranges }, "A mirror without byte ranges is rejected: " + Of(d).Reason);
        Check(s.Single(i => i.Url == new Uri(dead).AbsoluteUri) is { State: MirrorState.Rejected, Reason: MirrorReasons.Unreachable }, "An unreachable mirror is rejected");
        Check(b.LogOf("/f").Count() == 1 && !done.MirrorUnverified && !done.ChecksumKnown, "The verified mirror was only asked for its first byte; nothing came from it");
        Check(!m.SelectMirror(j.Id, c.Url("/f")) && !m.SelectMirror(j.Id, "https://never-listed.example.test/f") && m.SelectMirror(j.Id, b.Url("/f")) && m.SelectMirror(j.Id, null), "Only a checked mirror can be picked");
    }

    /// <summary>Automatic picks the mirror with the lowest measured connect time when it is clearly faster; a close call keeps the original.</summary>
    public static async Task AutomaticBest((string Download, string State) p)
    {
        using var a = new MirrorServer(); using var b = new MirrorServer();
        var data = Data(3);
        b.Add("/f", data); a.Add("/f", data, r => r.Headers.Add($"Link: <{b.Url("/f")}>; rel=duplicate; pri=1"));
        using (var m = Manager(p, Times(300, 50)))
        {
            var j = await m.AddAsync(new(a.Url("/f"), "fast-mirror.bin"));
            await Wait(() => Job(m, j.Id).Status is "Completed" or "Failed", "Finish (mirror clearly faster)");
            var done = Job(m, j.Id);
            Check(done.Status == "Completed" && File.ReadAllBytes(done.FilePath).SequenceEqual(data), "Completed from the mirror: " + done.Error);
            Check(b.LogOf("/f").Count(l => l.Range is not null && l.Range != "bytes=0-0") >= 1 && a.LogOf("/f").All(l => l.Range == "bytes=0-0"), "The bytes came from the faster mirror, the original only answered the first-byte probe");
            Check(done.MirrorUnverified && !done.ChecksumKnown && done.Sources.Single(s => s.IsActive).Url == b.Url("/f"), "No checksum is published, so the finished file is marked as from an unverified mirror");
        }
        a.Log.Clear(); b.Log.Clear();
        using (var m = Manager(p, Times(40, 35), suffix: "-2"))
        {
            var j = await m.AddAsync(new(a.Url("/f"), "close-call.bin"));
            await Wait(() => Job(m, j.Id).Status is "Completed" or "Failed", "Finish (close call)");
            var done = Job(m, j.Id);
            Check(done.Status == "Completed" && !done.MirrorUnverified && b.LogOf("/f").All(l => l.Range == "bytes=0-0"), "A mirror that is only a little faster is not used");
        }
    }

    /// <summary>Switching on a running download keeps the parts and continues by ranges on the new source.</summary>
    public static async Task SwitchKeepsParts((string Download, string State) p)
    {
        using var a = new MirrorServer(); using var b = new MirrorServer();
        var data = Data(4);
        b.Add("/f", data); a.Add("/f", data, r => { r.ChunkDelayMs = 20; r.Headers.Add($"Link: <{b.Url("/f")}>; rel=duplicate; pri=1"); });
        using var m = Manager(p, Times(50, 400));
        var j = await m.AddAsync(new(a.Url("/f"), "switch.bin"));
        await Wait(() => Job(m, j.Id).DownloadedBytes >= 250_000 && Job(m, j.Id).Sources.Any(s => s.State == MirrorState.Verified), "Progress on the original");
        var before = Job(m, j.Id).DownloadedBytes;
        Check(m.SelectMirror(j.Id, b.Url("/f")), "The mirror can be picked while downloading");
        await Wait(() => Job(m, j.Id).Status is "Completed" or "Failed", "Finish after the switch");
        var done = Job(m, j.Id);
        Check(done.Status == "Completed" && File.ReadAllBytes(done.FilePath).SequenceEqual(data), "The file is complete and identical: " + done.Error);
        var firstB = b.LogOf("/f").Where(l => l.Range is not null && l.Range != "bytes=0-0").Select(l => long.Parse(l.Range!["bytes=".Length..l.Range!.IndexOf('-')])).OrderBy(x => x).First();
        Check(firstB >= before, $"The mirror continued from the saved parts (it started at byte {firstB}, {before} were already there)");
        Check(a.LogOf("/f").Count(l => l.Range is not null && l.Range != "bytes=0-0") == 1, "The original was asked for its bytes once (no second start from zero)");
        Check(done.MirrorChoice == b.Url("/f") && done.MirrorUnverified, "The choice is kept and the file is marked unverified (no checksum published)");
    }

    /// <summary>A mirror that sends other bytes under the same size is caught by the published checksum: the file is not kept, the mirror is rejected, and a second try from the original succeeds.</summary>
    public static async Task ChecksumMismatch((string Download, string State) p)
    {
        using var a = new MirrorServer(); using var b = new MirrorServer();
        var data = Data(5);
        b.Add("/f", Data(6)); // same length, other bytes
        var routeA = a.Add("/f", data, r => { r.ChunkDelayMs = 20; r.Headers.Add($"Link: <{b.Url("/f")}>; rel=duplicate; pri=1"); r.Headers.Add("Digest: SHA-256=" + Sha256B64(data)); });
        using var m = Manager(p, Times(50, 400));
        var j = await m.AddAsync(new(a.Url("/f"), "bad-mirror.bin"));
        await Wait(() => Job(m, j.Id).DownloadedBytes >= 250_000 && Job(m, j.Id).Sources.Any(s => s.State == MirrorState.Verified), "Progress");
        Check(Job(m, j.Id).ChecksumKnown, "The Digest header of the original is known");
        m.SelectMirror(j.Id, b.Url("/f"));
        await Wait(() => Job(m, j.Id).Status is "Completed" or "Failed", "End of the first run");
        var failed = Job(m, j.Id);
        Check(failed.Status == "Failed" && failed.ErrorCode == DownloadManager.ChecksumCode, $"The mismatch fails the job: {failed.Status} {failed.ErrorCode} {failed.Error}");
        Check(!Directory.GetFiles(p.Download, "bad-mirror*").Any(), "No file was published");
        Check(failed.Sources.Single(s => !s.IsPrimary) is { State: MirrorState.Rejected, Reason: MirrorReasons.Checksum } && failed.MirrorChoice is null, "The mirror is rejected and the choice is cleared");
        routeA.ChunkDelayMs = 0;
        m.Resume(j.Id);
        await Wait(() => Job(m, j.Id).Status is "Completed" or "Failed", "Second run");
        var done = Job(m, j.Id);
        Check(done.Status == "Completed" && File.ReadAllBytes(done.FilePath).SequenceEqual(data) && done.ChecksumVerified && !done.MirrorUnverified, "The second try from the original is complete and checked: " + done.Error);
    }

    /// <summary>Metalink: sources and a hash learned from the file the server points at; a matching hash passes, a wrong one fails the download; a file of another size is ignored.</summary>
    public static async Task MetalinkLearned((string Download, string State) p)
    {
        using var a = new MirrorServer(); using var b = new MirrorServer();
        var data = Data(7);
        b.Add("/f", data);
        string Xml(string hash, long size) => $"<?xml version=\"1.0\"?><metalink xmlns=\"urn:ietf:params:xml:ns:metalink\"><file name=\"f\"><size>{size}</size><hash type=\"sha-256\">{hash}</hash><url location=\"de\" priority=\"1\">{b.Url("/f")}</url></file></metalink>";
        a.Add("/f", data, r => r.Headers.Add($"Link: <{a.Url("/f.meta4")}>; rel=describedby; type=\"application/metalink4+xml\""));
        a.Add("/f.meta4", System.Text.Encoding.UTF8.GetBytes(Xml(Sha256Hex(data), data.Length)), r => r.ContentType = "application/metalink4+xml");
        using (var m = Manager(p))
        {
            var j = await m.AddAsync(new(a.Url("/f"), "meta.bin"));
            await Wait(() => Job(m, j.Id).Status is "Completed" or "Failed", "Finish (Metalink)");
            var done = Job(m, j.Id);
            Check(done.Status == "Completed" && done.ChecksumKnown && done.ChecksumVerified, $"The Metalink hash was checked at the end: {done.Status} {done.Error}");
            Check(done.Sources.Any(s => s.Url == b.Url("/f") && s.Origin == MirrorOrigin.Metalink && s.Geo == "de" && s.State == MirrorState.Verified), "The Metalink source is listed with its location and verified");
        }
        a.Add("/f.meta4", System.Text.Encoding.UTF8.GetBytes(Xml(new string('0', 64), data.Length)), r => r.ContentType = "application/metalink4+xml");
        using (var m = Manager(p, suffix: "-wrong"))
        {
            var j = await m.AddAsync(new(a.Url("/f"), "meta-wrong.bin"));
            await Wait(() => Job(m, j.Id).Status is "Completed" or "Failed", "Finish (wrong hash)");
            var done = Job(m, j.Id);
            Check(done.Status == "Failed" && done.ErrorCode == DownloadManager.ChecksumCode && !File.Exists(Path.Combine(p.Download, "meta-wrong.bin")), "A wrong Metalink hash fails the download, nothing is published");
        }
        a.Add("/f.meta4", System.Text.Encoding.UTF8.GetBytes(Xml(new string('0', 64), data.Length + 5)), r => r.ContentType = "application/metalink4+xml");
        using (var m = Manager(p, suffix: "-size"))
        {
            var j = await m.AddAsync(new(a.Url("/f"), "meta-size.bin"));
            await Wait(() => Job(m, j.Id).Status is "Completed" or "Failed", "Finish (other size)");
            var done = Job(m, j.Id);
            Check(done.Status == "Completed" && !done.ChecksumKnown && done.Sources.Count == 0, "A Metalink file for another size is ignored: " + done.Error);
        }
    }

    /// <summary>The user's Add mirror: bad and duplicate addresses are refused, a good one is checked when the size is known, a .meta4 address adds its sources.</summary>
    public static async Task UserMirror((string Download, string State) p)
    {
        using var a = new MirrorServer(); using var b = new MirrorServer(); using var c = new MirrorServer();
        var data = Data(8);
        a.Add("/f", data, r => r.ChunkDelayMs = 25); b.Add("/f", data); c.Add("/f", Data(9, 800_000));
        a.Add("/list.meta4", System.Text.Encoding.UTF8.GetBytes($"<?xml version=\"1.0\"?><metalink xmlns=\"urn:ietf:params:xml:ns:metalink\"><file name=\"f\"><size>{data.Length}</size><url priority=\"2\">{b.Url("/g")}</url></file></metalink>"));
        b.Add("/g", data);
        using var m = Manager(p);
        var paused = await m.AddPausedAsync(new(a.Url("/f"), "user.bin"));
        Check(!(await m.AddMirrorAsync(paused.Id, "ftp://x.example.test/f")).Added && !(await m.AddMirrorAsync(paused.Id, "not an address")).Added && !(await m.AddMirrorAsync(paused.Id, "http://user:pw@x.example.test/f")).Added, "Bad addresses are refused");
        var first = await m.AddMirrorAsync(paused.Id, b.Url("/f"));
        Check(first.Added && first.State == MirrorState.Candidate && first.Count == 1, "Before the size is known the mirror waits to be checked");
        Check(!(await m.AddMirrorAsync(paused.Id, b.Url("/f"))).Added && !(await m.AddMirrorAsync(paused.Id, a.Url("/f"))).Added, "The same address twice, or the original, is refused");
        m.Resume(paused.Id);
        await Wait(() => Job(m, paused.Id).DownloadedBytes > 100_000, "Running");
        await Wait(() => Job(m, paused.Id).Sources.Any(s => s.Url == b.Url("/f") && s.State == MirrorState.Verified), "The user's mirror is checked when the download runs");
        var bad = await m.AddMirrorAsync(paused.Id, c.Url("/f"));
        Check(!bad.Added && bad.Reason == MirrorReasons.Size && bad.State == MirrorState.Rejected, "A mirror of another size is refused at once: " + bad.Reason);
        var meta = await m.AddMirrorAsync(paused.Id, a.Url("/list.meta4"));
        Check(meta.Added && Job(m, paused.Id).Sources.Any(s => s.Url == b.Url("/g") && s.Origin == MirrorOrigin.Metalink), "A Metalink address adds its sources");
        Check(m.RemoveMirror(paused.Id, b.Url("/g")) && !m.RemoveMirror(paused.Id, b.Url("/g")) && Job(m, paused.Id).Sources.All(s => s.Url != b.Url("/g")), "A mirror can be removed");
        a.Routes["/f"].ChunkDelayMs = 0;
        await Wait(() => Job(m, paused.Id).Status is "Completed" or "Failed", "Finish");
        Check(Job(m, paused.Id).Status == "Completed" && File.ReadAllBytes(Job(m, paused.Id).FilePath).SequenceEqual(data), "Completed");
        Check((await m.AddMirrorAsync(paused.Id, b.Url("/z"))).Reason == "unsupported", "A finished download takes no more mirrors");
    }

    /// <summary>In Automatic mode a source that fails is replaced by the next one, from the parts that are already there, without using up a retry.</summary>
    public static async Task FailOver((string Download, string State) p)
    {
        using var a = new MirrorServer(); using var b = new MirrorServer();
        var data = Data(10);
        b.Add("/f", data, r => r.FailAfterProbe = 500); // answers the first-byte check, then refuses to send bytes
        a.Add("/f", data, r => r.Headers.Add($"Link: <{b.Url("/f")}>; rel=duplicate; pri=1"));
        using var m = Manager(p, Times(300, 50));
        var j = await m.AddAsync(new(a.Url("/f"), "failover.bin"));
        await Wait(() => Job(m, j.Id).Status is "Completed" or "Failed", "Finish");
        var done = Job(m, j.Id);
        Check(done.Status == "Completed" && File.ReadAllBytes(done.FilePath).SequenceEqual(data), "The original took over and the file is complete: " + done.Status + " " + done.Error);
        Check(b.LogOf("/f").Count(l => l.Range is not null && l.Range != "bytes=0-0") == 1, "The failing mirror was tried once");
        // a manual choice is the user's: it is not replaced behind their back
        using var m2 = Manager(p, Times(50, 400), suffix: "-manual");
        a.Routes["/f"].ChunkDelayMs = 20;
        var j2 = await m2.AddAsync(new(a.Url("/f"), "manual.bin"));
        await Wait(() => Job(m2, j2.Id).DownloadedBytes >= 150_000 && Job(m2, j2.Id).Sources.Any(s => s.State == MirrorState.Verified), "Progress");
        m2.SelectMirror(j2.Id, b.Url("/f"));
        await Wait(() => Job(m2, j2.Id).Status is "Completed" or "Failed", "End of the manual run");
        Check(Job(m2, j2.Id).Status == "Failed", "A source the user chose is not replaced when it fails: " + Job(m2, j2.Id).Status);
    }

    /// <summary>Sources, the choice and the checksum survive a restart; the saved file carries no cookie for mirrors; a cookie is sent only to its own origin.</summary>
    public static async Task Persisted((string Download, string State) p)
    {
        using var a = new MirrorServer(); using var b = new MirrorServer();
        var data = Data(11);
        b.Add("/f", data, r => r.ChunkDelayMs = 15); a.Add("/f", data, r => { r.ChunkDelayMs = 15; r.Headers.Add($"Link: <{b.Url("/f")}>; rel=duplicate; pri=1; geo=fr"); r.Headers.Add("Digest: SHA-256=" + Sha256B64(data)); });
        string id;
        using (var m = Manager(p, Times(50, 400)))
        {
            var j = await m.AddAsync(new(a.Url("/f"), "persist.bin", Cookie: "session=secret-cookie-value"));
            id = j.Id;
            await Wait(() => Job(m, id).DownloadedBytes >= 100_000 && Job(m, id).Sources.Any(s => s.State == MirrorState.Verified), "Progress");
            m.SelectMirror(id, b.Url("/f")); await Task.Delay(100);
            m.Pause(id); await Wait(() => Job(m, id).Status == "Paused", "Pause");
        }
        var saved = File.ReadAllText(Path.Combine(p.State, "queue.json"));
        Check(!saved.Contains("secret-cookie-value") && saved.Contains("\"MirrorChoice\"") && saved.Contains("sha-256:"), "queue.json keeps the choice and the checksum and no cookie");
        Check(b.Log.All(l => l.Cookie is null) && a.Log.Any(l => l.Cookie == "session=secret-cookie-value"), "The cookie went to its own origin only");
        using (var m = Manager(p, Times(50, 400)))
        {
            var j = Job(m, id);
            Check(j.Sources.Count == 2 && j.MirrorChoice == b.Url("/f") && j.ChecksumKnown && j.Sources[1] is { State: MirrorState.Verified, Geo: "fr", Origin: MirrorOrigin.Header }, "After a restart the sources, the choice and the checksum are back");
            a.Routes["/f"].ChunkDelayMs = 0; b.Routes["/f"].ChunkDelayMs = 0;
            m.Resume(id);
            await Wait(() => Job(m, id).Status is "Completed" or "Failed", "Finish after the restart");
            var done = Job(m, id);
            Check(done.Status == "Completed" && File.ReadAllBytes(done.FilePath).SequenceEqual(data) && done.ChecksumVerified && !done.MirrorUnverified, "The chosen mirror finished the file and the checksum matched: " + done.Error);
        }
    }

    /// <summary>Latency refresh stores the connect time of the original and the mirrors; through a proxy nothing is measured.</summary>
    public static async Task LatencyAndProxy((string Download, string State) p)
    {
        using var a = new MirrorServer(); using var b = new MirrorServer();
        var data = Data(12);
        b.Add("/f", data); a.Add("/f", data, r => r.Headers.Add($"Link: <{b.Url("/f")}>; rel=duplicate; pri=1"));
        using (var m = Manager(p, Times(12, 34)))
        {
            var j = await m.AddAsync(new(a.Url("/f"), "lat.bin"));
            await Wait(() => Job(m, j.Id).Status is "Completed" or "Failed", "Finish");
            await m.RefreshMirrorLatenciesAsync(j.Id);
            var s = Job(m, j.Id).Sources;
            Check(s[0].LatencyMs == 12 && s[1].LatencyMs == 34, $"The refresh stores the connect times: {s[0].LatencyMs} {s[1].LatencyMs}");
        }
        var real = await DownloadManager.MeasureConnectAsync(new[] { a.Url("/f"), "http://127.0.0.1:1/x", "not a url" }, CancellationToken.None, TimeSpan.FromSeconds(3));
        Check(real[0] is >= 0 and < 3000 && real[1] is null && real[2] is null, "The real measurement answers for a listening server and gives null for a closed port and a bad address");
        var calls = 0;
        using var proxy = new FixtureProxy();
        using (var m = Manager(p, (u, _) => { calls++; return Task.FromResult(new int?[u.Count]); }, new ProxyOptions { Kind = ProxyKind.Http, Host = "127.0.0.1", Port = proxy.Port }, "-proxy"))
        {
            var j = await m.AddAsync(new(a.Url("/f"), "lat-proxy.bin"));
            await Wait(() => Job(m, j.Id).Status is "Completed" or "Failed", "Finish through the proxy");
            await m.RefreshMirrorLatenciesAsync(j.Id);
            var done = Job(m, j.Id);
            Check(done.Status == "Completed" && calls == 0 && done.Sources.All(s => s.LatencyMs is null), "Through a proxy no connect time is measured: calls " + calls);
            Check(done.Sources.Any(s => !s.IsPrimary && s.State == MirrorState.Verified) && done.ServerViaProxy, "The mirror was still checked through the proxy");
        }
    }
}
