using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using NDM.Core;
using static NDM.Tests.StreamKit;

namespace NDM.Tests;

using P = (string Download, string State);

/// <summary>The streaming engine against local servers: HLS and DASH downloads, encryption, joining, resume, errors and request identity.</summary>
internal static class StreamEngineTests
{
    // ---- fixtures ----

    private static string Head(double target = 2, long sequence = 0) => string.Join("\n", "#EXTM3U", "#EXT-X-VERSION:3", $"#EXT-X-TARGETDURATION:{(int)Math.Ceiling(target)}", $"#EXT-X-MEDIA-SEQUENCE:{sequence}");

    /// <summary>A VOD playlist of plain transport stream pieces at dir/index.m3u8 and dir/s{i}.ts; returns the pieces.</summary>
    private static List<byte[]> AddTs(StreamServer s, string dir, int count, int size, int seed)
    {
        var parts = new List<byte[]>(); var sb = new StringBuilder(Head() + "\n");
        for (int i = 0; i < count; i++)
        {
            var data = Blob(seed + i, size); data[0] = 0x47; parts.Add(data);
            s.Add($"{dir}/s{i}.ts", data, "video/mp2t");
            sb.Append("#EXTINF:2.0,\ns").Append(i).Append(".ts\n");
        }
        sb.Append("#EXT-X-ENDLIST\n");
        s.Add($"{dir}/index.m3u8", sb.ToString());
        return parts;
    }

    /// <summary>fMP4 track: dir/{name}.m3u8 with init {name}-init.mp4 and fragments {name}{i}.m4s. Returns init and fragments.</summary>
    private static (byte[] Init, List<byte[]> Fragments) AddFmp4(StreamServer s, string dir, string name, int count, int seed, int size = 20_000)
    {
        var init = Init(seed); var frags = new List<byte[]>();
        s.Add($"{dir}/{name}-init.mp4", init, "video/mp4");
        var sb = new StringBuilder(Head() + "\n#EXT-X-MAP:URI=\"" + name + "-init.mp4\"\n");
        for (int i = 0; i < count; i++)
        {
            var f = Fragment(seed + 10 + i, size); frags.Add(f);
            s.Add($"{dir}/{name}{i}.m4s", f, "video/iso.segment");
            sb.Append("#EXTINF:2.0,\n").Append(name).Append(i).Append(".m4s\n");
        }
        sb.Append("#EXT-X-ENDLIST\n");
        s.Add($"{dir}/{name}.m3u8", sb.ToString());
        return (init, frags);
    }

    private static byte[] Concat(IEnumerable<byte[]> parts) => parts.SelectMany(b => b).ToArray();

    private static async Task<DownloadJob> RunHls(DownloadManager m, string url, string? name = null, string? variant = null, string? audio = null, bool mp4 = false, string? referer = null, string? cookie = null)
    {
        var job = await m.AddStreamAsync(url, StreamKind.Hls, variant, audio, mp4, name, referer, cookie);
        return await Succeed(m, job.Id);
    }

    // ---- HLS ----

    public static async Task HlsTransportStream(P paths)
    {
        using var server = new StreamServer(); var parts = AddTs(server, "/vod", 10, 30_000, 1);
        using var manager = NewManager(paths);
        var added = await manager.AddStreamAsync(server.Url("/vod/index.m3u8"), StreamKind.Hls, fileName: "My clip");
        Ensure(added.Stream == StreamKind.Hls && added.FileName == "My clip.ts", "provisional name and kind: " + added.FileName);
        var done = await Succeed(manager, added.Id);
        Ensure(done.FileName == "My clip.ts" && done.SegmentsTotal == 10 && done.AdditionalFiles.Count == 0 && done.StreamNote is null, "result: " + done.FileName);
        Ensure(File.ReadAllBytes(done.FilePath).SequenceEqual(Concat(parts)), "pieces are joined in order, byte for byte");
        Ensure(done.TotalBytes == parts.Sum(p => (long)p.Length), "exact size at the end");
        Ensure(server.Log.Where(s => s.Path.EndsWith(".ts")).All(s => s.Range is null), "no Range header on pieces without a byte range");
        Ensure(Enumerable.Range(0, 10).All(i => server.Count($"/vod/s{i}.ts") == 1) && server.Count("/vod/index.m3u8") == 1, "every piece requested once, the playlist once");
        Ensure(!Directory.Exists(Path.Combine(paths.State, added.Id + ".parts")), "work folder removed after publishing");
        Ensure(Directory.GetFiles(Path.Combine(paths.Download, ".ndm-staging")).Length == 0, "no staging left");
        Ensure(server.Log.Where(s => s.Path.EndsWith(".ts")).Select(s => s.Accept).Distinct().Count() == 1, "same request identity for all pieces");
        Ensure(done.ConnectionLimit >= 1 && done.RemainingTime == TimeSpan.Zero, "finished snapshot");
    }

    public static async Task HlsAes128(P paths)
    {
        using var server = new StreamServer();
        var key1 = Blob(11, 16); var key2 = Blob(12, 16); var iv2 = Blob(13, 16);
        var plain = new List<byte[]>(); var sb = new StringBuilder(Head(2, 100) + "\n");
        server.Add("/enc/key1.bin", key1); server.Add("/enc/key2.bin", key2);
        sb.Append("#EXT-X-KEY:METHOD=AES-128,URI=\"key1.bin\"\n");
        for (int i = 0; i < 10; i++)
        {
            if (i == 5) sb.Append("#EXT-X-KEY:METHOD=AES-128,URI=\"key2.bin\",IV=0x").Append(Convert.ToHexString(iv2)).Append('\n');
            var p = Blob(100 + i, 20_000 + i * 7 + (i == 3 ? 5 : 0)); plain.Add(p); // lengths that are not multiples of 16
            byte[] key, iv;
            if (i < 5) { key = key1; iv = new byte[16]; iv[15] = (byte)(100 + i); } else { key = key2; iv = iv2; }
            server.Add($"/enc/s{i}.ts", Encrypt(p, key, iv));
            sb.Append("#EXTINF:2.0,\ns").Append(i).Append(".ts\n");
        }
        sb.Append("#EXT-X-ENDLIST\n"); server.Add("/enc/index.m3u8", sb.ToString());
        using var manager = NewManager(paths, connections: 4);
        var done = await RunHls(manager, server.Url("/enc/index.m3u8"), "secret");
        Ensure(Sha(File.ReadAllBytes(done.FilePath)) == Sha(Concat(plain)), "decrypted output equals the plaintext (SHA-256), derived and explicit IVs, rotated key");
        Ensure(server.Count("/enc/key1.bin") == 1 && server.Count("/enc/key2.bin") == 1, "each key fetched once for ten pieces and four connections");
        Ensure(done.TotalBytes == plain.Sum(p => (long)p.Length), "size is the decrypted size");
        // a wrong key never publishes garbage
        using var wrong = new StreamServer();
        for (int i = 0; i < 6; i++) wrong.Add($"/s{i}.ts", Encrypt(Blob(i, 5000), key1, new byte[16]));
        wrong.Add("/k.bin", Blob(99, 16));
        wrong.Add("/index.m3u8", Head() + "\n#EXT-X-KEY:METHOD=AES-128,URI=\"k.bin\",IV=0x0\n" + string.Concat(Enumerable.Range(0, 6).Select(i => $"#EXTINF:2,\ns{i}.ts\n")) + "#EXT-X-ENDLIST\n");
        var failed = await manager.AddStreamAsync(wrong.Url("/index.m3u8"), StreamKind.Hls, fileName: "bad");
        var f = await Finish(manager, failed.Id);
        Ensure(f.Status == "Failed" && f.ErrorCode == StreamCodes.Malformed && f.Error!.Contains("decrypted"), "wrong key: " + Describe(f));
        Ensure(!File.Exists(failed.FilePath), "nothing published");
        // a key that is not 16 bytes
        wrong.Add("/k.bin", new byte[10]);
        var f2 = await Finish(manager, (await manager.AddStreamAsync(wrong.Url("/index.m3u8"), StreamKind.Hls, fileName: "bad2")).Id);
        Ensure(f2.Status == "Failed" && f2.ErrorCode == StreamCodes.Malformed, "short key: " + Describe(f2));
    }

    public static async Task HlsFragmentedMp4(P paths)
    {
        using var server = new StreamServer();
        var (init, frags) = AddFmp4(server, "/fm", "v", 6, 5);
        using var manager = NewManager(paths);
        var done = await RunHls(manager, server.Url("/fm/v.m3u8"), "movie");
        Ensure(done.FileName == "movie.mp4" && done.SegmentsTotal == 7, "mp4 name, init counted: " + done.FileName + " " + done.SegmentsTotal);
        var bytes = File.ReadAllBytes(done.FilePath);
        Ensure(bytes.SequenceEqual(init.Concat(Concat(frags)).ToArray()), "init + fragments concatenated");
        var boxes = BoxNames(bytes);
        Ensure(boxes[0] == "ftyp" && boxes[1] == "moov" && boxes.IndexOf("moov") < boxes.IndexOf("moof") && boxes.Count(b => b == "moof") == 6, "ftyp, moov, then moof/mdat pairs: " + string.Join(",", boxes));
        Ensure(server.Count("/fm/v-init.mp4") == 1, "init fetched once");
    }

    public static async Task HlsByteRanges(P paths)
    {
        foreach (var ignore in new[] { false, true })
        {
            using var server = new StreamServer { IgnoreRange = ignore };
            var init = Init(7); var frags = Enumerable.Range(0, 6).Select(i => Fragment(70 + i, 9000 + i * 100)).ToList();
            var file = init.Concat(Concat(frags)).ToArray(); server.Add("/br/media.mp4", file, "video/mp4");
            var sb = new StringBuilder(Head() + $"\n#EXT-X-MAP:URI=\"media.mp4\",BYTERANGE=\"{init.Length}@0\"\n");
            long off = init.Length; var expected = new List<string> { $"bytes=0-{init.Length - 1}" };
            for (int i = 0; i < frags.Count; i++)
            {
                sb.Append("#EXTINF:2.0,\n#EXT-X-BYTERANGE:").Append(frags[i].Length).Append(i % 2 == 0 ? "@" + off : "").Append("\nmedia.mp4\n");
                expected.Add($"bytes={off}-{off + frags[i].Length - 1}"); off += frags[i].Length;
            }
            sb.Append("#EXT-X-ENDLIST\n"); server.Add("/br/index.m3u8", sb.ToString());
            using var manager = NewManager((paths.Download + (ignore ? "-b" : "-a"), paths.State + (ignore ? "-b" : "-a")));
            var done = await RunHls(manager, server.Url("/br/index.m3u8"), "ranged");
            Ensure(File.ReadAllBytes(done.FilePath).SequenceEqual(file), "byte-range pieces give the whole file (server ignores Range: " + ignore + ")");
            Ensure(server.For("/br/media.mp4").Select(s => s.Range).OrderBy(x => x).SequenceEqual(expected.OrderBy(x => x)), "exact ranges requested: " + string.Join(";", server.For("/br/media.mp4").Select(s => s.Range)));
        }
    }

    public static async Task HlsVariantSelection(P paths)
    {
        using var server = new StreamServer();
        var low = AddTs(server, "/low", 5, 10_000, 100); var mid = AddTs(server, "/mid", 5, 12_000, 200); var high = AddTs(server, "/high", 5, 14_000, 300);
        server.Add("/m/master.m3u8", "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=500000,RESOLUTION=320x180,CODECS=\"avc1.42c00d,mp4a.40.2\"\n../low/index.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=1000000,RESOLUTION=640x360\n../mid/index.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=3000000,RESOLUTION=1280x720\n../high/index.m3u8\n");
        using var manager = NewManager(paths);
        var manifest = await manager.InspectStreamAsync(server.Url("/m/master.m3u8"), title: "Show");
        Ensure(manifest.Kind == StreamKind.Hls && manifest.Variants.Count == 3 && manifest.Duration == TimeSpan.FromSeconds(10), "inspect: three variants and a duration from the best one: " + manifest.Duration);
        Ensure(manifest.BestVariant!.Id == "v2" && manifest.Variants[0].Resolution == "320x180" && manifest.Variants[1].Bandwidth == 1_000_000 && manifest.Title == "Show", "variant data");
        Ensure(manifest.EstimateBytes(manifest.BestVariant, null) == 3_000_000L * 10 / 8, "estimate for the picker");
        var pick = await RunHls(manager, server.Url("/m/master.m3u8"), "pick", variant: "v1");
        Ensure(File.ReadAllBytes(pick.FilePath).SequenceEqual(Concat(mid)), "the chosen variant is the one saved");
        Ensure(Enumerable.Range(0, 5).All(i => server.Count($"/low/s{i}.ts") == 0 && server.Count($"/high/s{i}.ts") == 0), "other variants' pieces never requested");
        Ensure(pick.TotalBytes > 0, "size known");
        var best = await RunHls(manager, server.Url("/m/master.m3u8"), "best");
        Ensure(File.ReadAllBytes(best.FilePath).SequenceEqual(Concat(high)), "no choice = the best quality");
        var bad = await Finish(manager, (await manager.AddStreamAsync(server.Url("/m/master.m3u8"), StreamKind.Hls, "v9")).Id);
        Ensure(bad.Status == "Failed" && bad.ErrorCode == StreamCodes.Unsupported, "unknown variant id: " + Describe(bad));
    }

    private static (byte[] VInit, List<byte[]> V, byte[] AInit, List<byte[]> A) AddAudioVideoHls(StreamServer s)
    {
        s.Add("/av/master.m3u8", "#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",NAME=\"English\",LANGUAGE=\"en\",DEFAULT=YES,AUTOSELECT=YES,URI=\"audio.m3u8\"\n#EXT-X-STREAM-INF:BANDWIDTH=1000000,RESOLUTION=640x360,CODECS=\"avc1.4d401e,mp4a.40.2\",AUDIO=\"aud\"\nvideo.m3u8\n");
        var v = AddFmp4(s, "/av", "video", 5, 300); var a = AddFmp4(s, "/av", "audio", 5, 400);
        return (v.Init, v.Fragments, a.Init, a.Fragments);
    }

    public static async Task HlsSeparateAudioWithTool(P paths)
    {
        var log = FakeTool.NewLog(paths.State);
        try
        {
            Environment.SetEnvironmentVariable(FakeFfmpeg.DurationVariable, "10");
            var tools = await FakeTool.Install(paths.State);
            using var server = new StreamServer(); var (vi, v, ai, a) = AddAudioVideoHls(server);
            using var manager = NewManager(paths, tools: tools);
            var manifest = await manager.InspectStreamAsync(server.Url("/av/master.m3u8"));
            Ensure(manifest.AudioTracks.Count == 1 && manifest.AudioTracks[0].Language == "en" && manifest.DefaultAudio(manifest.Variants[0])!.Id == "a0", "audio offered with the variant");
            var done = await RunHls(manager, server.Url("/av/master.m3u8"));
            Ensure(done.FileName == "av.mp4" && done.StreamNote is null && done.AdditionalFiles.Count == 0, "one mp4: " + done.FileName + " " + done.StreamNote);
            var work = Path.Combine(paths.State, done.Id + ".parts");
            var calls = FakeFfmpeg.ReadLog(log);
            Ensure(calls.Count == 2, "mux and one duration check: " + calls.Count);
            var expected = new[] { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-i", Path.Combine(work, "video.mp4"), "-i", Path.Combine(work, "audio.m4a"), "-map", "0:v:0", "-map", "1:a:0", "-c", "copy", "-movflags", "+faststart", Path.Combine(work, "out.mp4") };
            Ensure(calls[0].SequenceEqual(expected), "mux arguments exactly: " + string.Join(" ", calls[0]));
            Ensure(calls[1].SequenceEqual(new[] { "-hide_banner", "-nostdin", "-i", Path.Combine(work, "out.mp4") }), "duration check arguments");
            var bytes = File.ReadAllBytes(done.FilePath);
            var expectedBytes = Encoding.ASCII.GetBytes("FAKE-MUX\n").Concat(vi).Concat(Concat(v)).Concat(ai).Concat(Concat(a)).ToArray();
            Ensure(bytes.SequenceEqual(expectedBytes), "the tool received the joined video then the joined audio");
            Ensure(done.TotalBytes == bytes.Length, "final size is the published file");
            Ensure(!Directory.Exists(work), "work folder cleaned");
            Ensure(File.Exists(done.FilePath) && !File.Exists(Path.Combine(paths.Download, "av (video only).mp4")), "no loose tracks");
        }
        finally { FakeTool.ResetEnvironment(); }
    }

    public static async Task HlsWithoutToolSavesTwoFiles(P paths)
    {
        using var server = new StreamServer(); var (vi, v, ai, a) = AddAudioVideoHls(server);
        using var manager = NewManager(paths);
        var done = await RunHls(manager, server.Url("/av/master.m3u8"), "show");
        Ensure(done.StreamNote == StreamCodes.NeedsMuxer, "note needs-muxer: " + done.StreamNote);
        Ensure(done.FileName == "show (video only).mp4" && done.AdditionalFiles.Count == 1 && Path.GetFileName(done.AdditionalFiles[0]) == "show (audio only).m4a", "two files named by track: " + done.FileName + " / " + string.Join(",", done.AdditionalFiles));
        Ensure(File.ReadAllBytes(done.FilePath).SequenceEqual(vi.Concat(Concat(v)).ToArray()), "video file is the joined video");
        Ensure(File.ReadAllBytes(done.AdditionalFiles[0]).SequenceEqual(ai.Concat(Concat(a)).ToArray()), "audio file is the joined audio");
        Ensure(done.TotalBytes == new FileInfo(done.FilePath).Length + new FileInfo(done.AdditionalFiles[0]).Length, "total counts both");
        // the note and the second file survive a restart
        manager.Dispose();
        using var again = NewManager(paths);
        var restored = JobOf(again, done.Id);
        Ensure(restored.StreamNote == StreamCodes.NeedsMuxer && restored.AdditionalFiles.Count == 1 && restored.Status == "Completed" && restored.Stream == StreamKind.Hls, "restored with its note and files");
        // names are collision-safe
        using var other = new StreamServer(); AddAudioVideoHls(other);
        var second = await RunHls(again, other.Url("/av/master.m3u8"), "show");
        Ensure(second.FileName != done.FileName && File.Exists(done.FilePath), "second download does not overwrite: " + second.FileName);
        // removing with the file deletes the main file
        again.Remove(second.Id, deleteFile: true);
        Ensure(!File.Exists(second.FilePath), "own file deleted on request");
    }


    public static async Task JoinSavedStreamLater(P paths)
    {
        using var server = new StreamServer(); var (vi, v, ai, a) = AddAudioVideoHls(server);
        DownloadJob done;
        using (var manager = NewManager(paths))
        {
            done = await RunHls(manager, server.Url("/av/master.m3u8"), "later");
            Ensure(done.StreamNote == StreamCodes.NeedsMuxer && manager.CanJoinSavedStream(done.Id), "two files, joinable");
            Ensure(await manager.JoinSavedStreamAsync(done.Id) == StreamJoinResult.ToolMissing, "no tool: reported, nothing changed");
            Ensure(File.Exists(done.FilePath) && File.Exists(done.AdditionalFiles[0]), "files untouched without the tool");
        }
        var log = FakeTool.NewLog(paths.State);
        try
        {
            var tools = await FakeTool.Install(paths.State);
            // a failing tool leaves everything as it was
            Environment.SetEnvironmentVariable(FakeFfmpeg.ModeVariable, "fail");
            using (var manager = NewManager(paths, tools: tools))
            {
                Ensure(await manager.JoinSavedStreamAsync(done.Id) == StreamJoinResult.Failed, "failing tool reported");
                var same = JobOf(manager, done.Id);
                Ensure(same.StreamNote == StreamCodes.NeedsMuxer && File.Exists(same.FilePath) && File.Exists(same.AdditionalFiles[0]), "failure changes nothing");
                Ensure(!Directory.Exists(Path.Combine(paths.State, done.Id + ".join")), "join work folder removed after a failure");
            }
            Environment.SetEnvironmentVariable(FakeFfmpeg.ModeVariable, null);
            File.Delete(log);
            using (var manager = NewManager(paths, tools: tools))
            {
                Ensure(await manager.JoinSavedStreamAsync(done.Id) == StreamJoinResult.Joined, "joined");
                var j = JobOf(manager, done.Id);
                Ensure(j.FileName == "later.mp4" && j.StreamNote is null && j.AdditionalFiles.Count == 0 && j.Status == "Completed", "one mp4 now: " + j.FileName + " " + j.StreamNote);
                Ensure(!File.Exists(Path.Combine(paths.Download, "later (video only).mp4")) && !File.Exists(Path.Combine(paths.Download, "later (audio only).m4a")), "separate files removed");
                var expectedBytes = Encoding.ASCII.GetBytes("FAKE-MUX\n").Concat(vi).Concat(Concat(v)).Concat(ai).Concat(Concat(a)).ToArray();
                Ensure(File.ReadAllBytes(j.FilePath).SequenceEqual(expectedBytes) && j.TotalBytes == expectedBytes.Length, "joined bytes");
                var calls = FakeFfmpeg.ReadLog(log);
                var work = Path.Combine(paths.State, done.Id + ".join");
                Ensure(calls.Count == 1 && calls[0].SequenceEqual(new[] { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-i", Path.Combine(work, "video.mp4"), "-i", Path.Combine(work, "audio.m4a"), "-map", "0:v:0", "-map", "1:a:0", "-c", "copy", "-movflags", "+faststart", Path.Combine(work, "out.mp4") }), "fixed argument template, fixed names: " + string.Join(" ", calls.FirstOrDefault() ?? Array.Empty<string>()));
                Ensure(!manager.CanJoinSavedStream(done.Id), "nothing left to join");
                bool refused = false; try { await manager.JoinSavedStreamAsync(done.Id); } catch (InvalidOperationException) { refused = true; }
                Ensure(refused, "joining twice is refused");
            }
            using var again = NewManager(paths, tools: tools);
            Ensure(JobOf(again, done.Id).FileName == "later.mp4" && JobOf(again, done.Id).StreamNote is null, "joined state survives a restart");
        }
        finally { FakeTool.ResetEnvironment(); }
    }

    public static async Task TransportStreamAsMp4(P paths)
    {
        var log = FakeTool.NewLog(paths.State);
        try
        {
            Environment.SetEnvironmentVariable(FakeFfmpeg.DurationVariable, "10");
            var tools = await FakeTool.Install(paths.State);
            using var server = new StreamServer(); var parts = AddTs(server, "/ts", 5, 15_000, 50);
            using var manager = NewManager(paths, tools: tools);
            var done = await RunHls(manager, server.Url("/ts/index.m3u8"), "wanted", mp4: true);
            Ensure(done.FileName == "wanted.mp4", "mp4 when asked and the tool is there: " + done.FileName);
            var work = Path.Combine(paths.State, done.Id + ".parts");
            var calls = FakeFfmpeg.ReadLog(log);
            Ensure(calls[0].SequenceEqual(new[] { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-i", Path.Combine(work, "video.ts"), "-map", "0:v?", "-map", "0:a?", "-c", "copy", "-bsf:a", "aac_adtstoasc", "-movflags", "+faststart", Path.Combine(work, "out.mp4") }), "rewrap arguments: " + string.Join(" ", calls[0]));
            Ensure(File.ReadAllBytes(done.FilePath).SequenceEqual(Encoding.ASCII.GetBytes("FAKE-MUX\n").Concat(Concat(parts)).ToArray()), "tool input is the joined stream");
        }
        finally { FakeTool.ResetEnvironment(); }
        // without the tool: the transport stream is saved as it is and the note says why
        using var server2 = new StreamServer(); var parts2 = AddTs(server2, "/ts", 5, 15_000, 50);
        using var plain = NewManager((paths.Download + "-n", paths.State + "-n"));
        var kept = await RunHls(plain, server2.Url("/ts/index.m3u8"), "wanted", mp4: true);
        Ensure(kept.FileName == "wanted.ts" && kept.StreamNote == StreamCodes.NeedsMuxer && File.ReadAllBytes(kept.FilePath).SequenceEqual(Concat(parts2)), "ts kept, needs-muxer: " + kept.FileName + " " + kept.StreamNote);
    }

    public static async Task MuxFailuresFallBack(P paths)
    {
        var log = FakeTool.NewLog(paths.State);
        try
        {
            var tools = await FakeTool.Install(paths.State);
            using var server = new StreamServer(); var (vi, v, ai, a) = AddAudioVideoHls(server);
            var timeoutOptions = Options(tools: tools, toolTimeout: TimeSpan.FromSeconds(2));
            using var manager = new DownloadManager(paths.Download, paths.State, timeoutOptions);
            foreach (var (mode, duration, label) in new[] { ("fail", "10", "tool exits with an error"), ("empty", "10", "empty output"), ("ok", "100", "duration off by far more than 2 percent"), ("hang", "10", "timeout") })
            {
                Environment.SetEnvironmentVariable(FakeFfmpeg.ModeVariable, mode); Environment.SetEnvironmentVariable(FakeFfmpeg.DurationVariable, duration);
                var clock = Stopwatch.StartNew();
                var done = await RunHls(manager, server.Url("/av/master.m3u8"), "m-" + mode);
                Ensure(done.StreamNote == StreamCodes.MuxFailed && done.AdditionalFiles.Count == 1, label + ": note mux-failed and two files: " + done.StreamNote);
                Ensure(File.ReadAllBytes(done.FilePath).SequenceEqual(vi.Concat(Concat(v)).ToArray()) && File.ReadAllBytes(done.AdditionalFiles[0]).SequenceEqual(ai.Concat(Concat(a)).ToArray()), label + ": the tracks are saved intact");
                if (mode == "hang") Ensure(clock.Elapsed < TimeSpan.FromSeconds(30), "timeout is enforced: " + clock.Elapsed);
            }
        }
        finally { FakeTool.ResetEnvironment(); }
    }

    // ---- controls ----

    public static async Task PauseResumeAndRestart(P paths)
    {
        using var server = new StreamServer(); var parts = AddTs(server, "/slow", 20, 40_000, 400);
        string id;
        var before = new HashSet<string>();
        using (var manager = NewManager(paths, connections: 2))
        {
            var added = await manager.AddStreamAsync(server.Url("/slow/index.m3u8"), StreamKind.Hls, fileName: "slow", scheduledAt: DateTimeOffset.UtcNow.AddHours(1)); id = added.Id;
            manager.SetSpeedLimit(id, 250_000); manager.Schedule(id, DateTimeOffset.UtcNow);
            await WaitUntil(() => JobOf(manager, id).SegmentsDone >= 5, "no progress", 30);
            manager.Pause(id);
            await WaitUntil(() => JobOf(manager, id).Status == "Paused" && JobOf(manager, id).ActiveConnectionCount == 0, "did not pause");
            await Task.Delay(300);
            var paused = JobOf(manager, id);
            Ensure(paused.SegmentsDone >= 5 && paused.SegmentsDone < 20 && paused.DownloadedBytes > 0, "paused part way: " + paused.SegmentsDone);
            // what the client itself recorded as finished (the server may have written a piece the client dropped when it paused)
            var map = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(paths.State, id + ".parts", "stream.json")))!;
            var bits = Convert.FromBase64String((string)map["Tracks"]![0]!["Done"]!);
            for (int i = 0; i < 20; i++) if ((bits[i / 8] & (1 << (i % 8))) != 0) before.Add($"/slow/s{i}.ts");
            Ensure(before.Count == paused.SegmentsDone && before.Count >= 5, "the saved map matches the job: " + before.Count + " vs " + paused.SegmentsDone);
            var frozen = JobOf(manager, id).DownloadedBytes; await Task.Delay(300);
            Ensure(JobOf(manager, id).DownloadedBytes == frozen && JobOf(manager, id).SpeedBytesPerSecond == 0, "nothing moves while paused");
            Ensure(File.Exists(Path.Combine(paths.State, id + ".parts", "stream.json")), "completion map persisted");
        }
        // restart the manager: the finished pieces are known from the saved map
        using (var manager = NewManager(paths, connections: 2))
        {
            var restored = JobOf(manager, id);
            Ensure(restored.Status == "Paused" && restored.SegmentsDone >= 5 && restored.SegmentsTotal == 20 && restored.Stream == StreamKind.Hls, "restored: " + Describe(restored));
            manager.SetSpeedLimit(id, null); manager.Resume(id);
            var done = await Succeed(manager, id);
            Ensure(File.ReadAllBytes(done.FilePath).SequenceEqual(Concat(parts)), "result is complete and in order after a restart");
            foreach (var p in before) Ensure(server.Count(p) == 1, "finished piece requested again: " + p + " x" + server.Count(p));
            Ensure(server.Count("/slow/index.m3u8") == 2, "playlist read again once at resume");
        }
    }

    public static async Task SpeedLimitIsHonoured(P paths)
    {
        using var server = new StreamServer(); var parts = AddTs(server, "/lim", 10, 30_000, 600);
        using var manager = NewManager(paths, connections: 4);
        var added = await manager.AddStreamAsync(server.Url("/lim/index.m3u8"), StreamKind.Hls, fileName: "lim", scheduledAt: DateTimeOffset.UtcNow.AddHours(1));
        manager.SetSpeedLimit(added.Id, 100_000); var clock = Stopwatch.StartNew(); manager.Schedule(added.Id, DateTimeOffset.UtcNow);
        var done = await Succeed(manager, added.Id, 40);
        Ensure(clock.Elapsed >= TimeSpan.FromSeconds(2.2), "300 KB at 100 KB/s took " + clock.Elapsed);
        Ensure(File.ReadAllBytes(done.FilePath).SequenceEqual(Concat(parts)), "complete");
        // live change of the limit
        var second = await manager.AddStreamAsync(server.Url("/lim/index.m3u8"), StreamKind.Hls, fileName: "lim2", scheduledAt: DateTimeOffset.UtcNow.AddHours(1));
        manager.SetSpeedLimit(second.Id, 1); manager.Schedule(second.Id, DateTimeOffset.UtcNow);
        await WaitUntil(() => JobOf(manager, second.Id).SegmentsTotal > 0, "no plan");
        await Task.Delay(500); Ensure(JobOf(manager, second.Id).DownloadedBytes < 100, "one byte per second holds the stream back: " + JobOf(manager, second.Id).DownloadedBytes);
        manager.SetSpeedLimit(second.Id, null); await Succeed(manager, second.Id);
    }

    public static async Task ConnectionsAreBounded(P paths)
    {
        using var server = new StreamServer { Delay = TimeSpan.FromMilliseconds(120) }; AddTs(server, "/c", 24, 2000, 700);
        using var manager = NewManager(paths, connections: 3);
        await RunHls(manager, server.Url("/c/index.m3u8"), "c3");
        Ensure(server.PeakConcurrent is >= 2 and <= 3, "peak connections with a limit of three: " + server.PeakConcurrent);
        using var wide = new StreamServer { Delay = TimeSpan.FromMilliseconds(150) }; AddTs(wide, "/c", 40, 2000, 700);
        using var manager16 = new DownloadManager(paths.Download + "16", paths.State + "16", Options(connections: 16));
        await RunHls(manager16, wide.Url("/c/index.m3u8"), "c16");
        Ensure(wide.PeakConcurrent is > 4 and <= 16, "sixteen connections can be used: " + wide.PeakConcurrent);
        bool refused = false;
        try { _ = new DownloadManager(paths.Download + "x", paths.State + "x", new DownloadOptions { StreamConnections = 17 }); } catch (ArgumentOutOfRangeException) { refused = true; }
        Ensure(refused, "more than 16 stream connections is refused");
        refused = false;
        try { _ = new DownloadManager(paths.Download + "y", paths.State + "y", new DownloadOptions { StreamConnections = 0 }); } catch (ArgumentOutOfRangeException) { refused = true; }
        Ensure(refused, "zero connections is refused");
        Ensure(new DownloadOptions().StreamConnections == 8, "default is eight");
    }

    public static async Task RetriesAndPersistentFailure(P paths)
    {
        using var server = new StreamServer(); var parts = AddTs(server, "/r", 8, 10_000, 800);
        server.Fail = (path, nth) => path.EndsWith(".ts") && nth == 1 ? 503 : null;
        using var manager = NewManager(paths);
        var done = await RunHls(manager, server.Url("/r/index.m3u8"), "retry");
        Ensure(File.ReadAllBytes(done.FilePath).SequenceEqual(Concat(parts)), "complete despite one failure per piece");
        Ensure(Enumerable.Range(0, 8).All(i => server.Count($"/r/s{i}.ts") == 2), "each piece asked twice");
        using var bad = new StreamServer(); AddTs(bad, "/r", 6, 10_000, 810);
        bad.Fail = (path, _) => path == "/r/s3.ts" ? 503 : null;
        using var other = NewManager((paths.Download + "-p", paths.State + "-p"), connections: 1);
        var failed = await Finish(other, (await other.AddStreamAsync(bad.Url("/r/index.m3u8"), StreamKind.Hls, fileName: "bad")).Id);
        Ensure(failed.Status == "Failed" && failed.ErrorCode == CompatibilityLadder.HttpStatusError && failed.ErrorStatus == 503, "persistent 503: " + Describe(failed));
        Ensure(bad.Count("/r/s3.ts") == 3, "three tries per piece: " + bad.Count("/r/s3.ts"));
        Ensure(!File.Exists(failed.FilePath), "nothing published");
        // pieces that were fine are kept for a later resume
        Ensure(File.Exists(Path.Combine(paths.State + "-p", failed.Id + ".parts", "stream.json")), "progress kept after a failure");
    }

    public static async Task ForbiddenAndMissing(P paths)
    {
        // 403 after pieces already worked: stop at once, plain words, no ladder
        using var server = new StreamServer(); AddTs(server, "/f", 8, 10_000, 900);
        server.Fail = (path, _) => path == "/f/s4.ts" ? 403 : null;
        using var one = NewManager(paths, connections: 1);
        var stopped = await Finish(one, (await one.AddStreamAsync(server.Url("/f/index.m3u8"), StreamKind.Hls, fileName: "f", referer: server.Url("/page"))).Id);
        Ensure(stopped.Status == "Failed" && stopped.ErrorCode == CompatibilityLadder.HttpStatusError && stopped.ErrorStatus == 403, "403 mid-stream: " + Describe(stopped));
        Ensure(stopped.Error!.Contains("stopped letting NeoDM download") && stopped.Error.Contains("add the video again") && !stopped.Error.Contains("Response status code"), "plain words: " + stopped.Error);
        Ensure(server.Count("/f/s4.ts") == 1 && stopped.SegmentsDone == 4, "no retry and no ladder after success: " + server.Count("/f/s4.ts") + " done " + stopped.SegmentsDone);
        // a site that needs a Referer: the ladder (site root) finds it before anything worked, and is remembered for the rest
        using var strict = new StreamServer(); var parts = AddTs(strict, "/s", 6, 8000, 910);
        strict.Intercept = r => r.UrlReferrer is null && r.Url!.AbsolutePath.EndsWith(".ts") ? 403 : null;
        using var ladder = NewManager((paths.Download + "-l", paths.State + "-l"), connections: 1);
        var done = await RunHls(ladder, strict.Url("/s/index.m3u8"), "ladder");
        Ensure(File.ReadAllBytes(done.FilePath).SequenceEqual(Concat(parts)), "completed after the referer variant");
        Ensure(strict.For("/s/s0.ts").Count() == 2 && strict.For("/s/s0.ts").Last().Referer == strict.BaseUrl + "/" && strict.Count("/s/s1.ts") == 1 && strict.For("/s/s1.ts").Single().Referer == strict.BaseUrl + "/", "site-root referer tried once, then kept for later pieces");
        // 404: the piece is named, nothing is retried
        using var missing = new StreamServer(); AddTs(missing, "/m", 8, 8000, 920);
        missing.Fail = (path, _) => path == "/m/s2.ts" ? 404 : null;
        using var third = NewManager((paths.Download + "-m", paths.State + "-m"), connections: 1);
        var gone = await Finish(third, (await third.AddStreamAsync(missing.Url("/m/index.m3u8"), StreamKind.Hls, fileName: "m")).Id);
        Ensure(gone.Status == "Failed" && gone.ErrorCode == StreamCodes.SegmentMissing && gone.ErrorSegment == 3 && gone.ErrorStatus == 404, "404 names the piece: " + Describe(gone));
        Ensure(gone.Error!.Contains("Segment 3") && missing.Count("/m/s2.ts") == 1, "message and a single try: " + gone.Error);
        // 410 is the same
        missing.Fail = (path, _) => path == "/m/s5.ts" ? 410 : null;
        third.Redownload(gone.Id);
        var gone2 = await Finish(third, gone.Id);
        Ensure(gone2.ErrorCode == StreamCodes.SegmentMissing && gone2.ErrorSegment == 6 && gone2.ErrorStatus == 410, "410: " + Describe(gone2));
        // the error code survives a restart
        third.Dispose();
        using var again = NewManager((paths.Download + "-m", paths.State + "-m"));
        var restored = JobOf(again, gone.Id);
        Ensure(restored.ErrorCode == StreamCodes.SegmentMissing && restored.ErrorSegment == 6, "failure restored: " + Describe(restored));
        // a piece that is a web page
        using var html = new StreamServer(); AddTs(html, "/h", 5, 8000, 930);
        html.Add("/h/s2.ts", "<!DOCTYPE html><html><body>expired</body></html>", "text/html");
        using var fourth = NewManager((paths.Download + "-h", paths.State + "-h"), connections: 1);
        var page = await Finish(fourth, (await fourth.AddStreamAsync(html.Url("/h/index.m3u8"), StreamKind.Hls, fileName: "h")).Id);
        Ensure(page.Status == "Failed" && page.ErrorCode == ContentGuard.WrongContent && page.SegmentsDone == 2 && html.Count("/h/s2.ts") == 1, "web page instead of a piece: " + Describe(page));
    }

    public static async Task RequestIdentityAndCookies(P paths)
    {
        using var a = new StreamServer(); using var b = new StreamServer();
        var key1 = Blob(1, 16); var key2 = Blob(2, 16); var plain = new List<byte[]>();
        a.Add("/k1", key1); b.Add("/k2", key2);
        var sb = new StringBuilder(Head() + "\n#EXT-X-KEY:METHOD=AES-128,URI=\"k1\",IV=0x1\n");
        for (int i = 0; i < 6; i++)
        {
            if (i == 3) sb.Append("#EXT-X-KEY:METHOD=AES-128,URI=\"").Append(b.Url("/k2")).Append("\",IV=0x2\n");
            var p = Blob(40 + i, 5000); plain.Add(p);
            var target = i < 3 ? a : b; target.Add($"/seg{i}.ts", Encrypt(p, i < 3 ? key1 : key2, i < 3 ? new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 } : new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2 }));
            sb.Append("#EXTINF:2,\n").Append(i < 3 ? $"seg{i}.ts" : b.Url($"/seg{i}.ts")).Append('\n');
        }
        sb.Append("#EXT-X-ENDLIST\n"); a.Add("/index.m3u8", sb.ToString());
        using var manager = NewManager(paths, connections: 2);
        var request = new DownloadRequest(a.Url("/index.m3u8"), "ident", a.Url("/page"), "sid=abc; theme=dark", false, null, null, null, "TestAgent/9.1 (x)", "video/*,*/*;q=0.5", "de-DE,de;q=0.9", StreamKind.Hls);
        var done = await Succeed(manager, (await manager.AddStreamAsync(request)).Id);
        Ensure(File.ReadAllBytes(done.FilePath).SequenceEqual(Concat(plain)), "complete across two origins");
        foreach (var seen in a.Log)
            Ensure(seen.Cookie == "sid=abc; theme=dark" && seen.UserAgent == "TestAgent/9.1 (x)" && seen.Accept == "video/*,*/*;q=0.5" && seen.AcceptLanguage == "de-DE,de;q=0.9" && seen.Referer == a.Url("/page"), "same-origin request " + seen.Path + " carries the identity and the cookie: " + seen);
        Ensure(a.Log.Select(s => s.Path).Distinct().Count() == 5, "playlist, key and three pieces on the first origin: " + string.Join(",", a.Log.Select(s => s.Path).Distinct()));
        foreach (var seen in b.Log)
            Ensure(seen.Cookie is null && seen.UserAgent == "TestAgent/9.1 (x)" && seen.Accept == "video/*,*/*;q=0.5" && seen.AcceptLanguage == "de-DE,de;q=0.9" && seen.Referer == a.Url("/page"), "cross-origin request " + seen.Path + " has no cookie but the rest of the identity: " + seen);
        Ensure(b.Log.Select(s => s.Path).Distinct().Count() == 4, "key and three pieces on the second origin");
        // Inspect forwards the same identity
        var before = a.Log.Count;
        await manager.InspectStreamAsync(a.Url("/index.m3u8"), a.Url("/page2"), "x=1", "InspectAgent/2", null, "fr-FR");
        var inspected = a.Log.Skip(before).Single();
        Ensure(inspected.UserAgent == "InspectAgent/2" && inspected.Cookie == "x=1" && inspected.Referer == a.Url("/page2") && inspected.AcceptLanguage == "fr-FR", "inspect request identity: " + inspected);
        // the cookie is not kept after completion
        var queue = File.ReadAllText(Path.Combine(paths.State, "queue.json"));
        Ensure(!queue.Contains("sid=abc"), "cookie is not stored once the job is done");
    }

    public static Task DowngradeIsRefused()
    {
        var https = new Uri("https://cdn.example.com/a/index.m3u8");
        var text = "#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"http://cdn.example.com/key\"\n#EXTINF:2,\nhttps://cdn.example.com/a.ts\n#EXT-X-ENDLIST\n";
        var plan = new StreamPlan { Kind = StreamKind.Hls, Video = HlsParser.Parse(text, https).Require().Media };
        bool refused = false; try { StreamSecurity.EnsureNoDowngrade(plan, https); } catch (StreamException e) { refused = e.Error == StreamError.Downgrade && e.Code == StreamCodes.Downgrade; }
        Ensure(refused, "an http key behind an https manifest is refused");
        var plan2 = new StreamPlan { Kind = StreamKind.Hls, Video = HlsParser.Parse("#EXTM3U\n#EXTINF:2,\nhttp://cdn.example.com/a.ts\n#EXT-X-ENDLIST\n", https).Require().Media };
        refused = false; try { StreamSecurity.EnsureNoDowngrade(plan2, https); } catch (StreamException e) { refused = e.Error == StreamError.Downgrade; }
        Ensure(refused, "an http piece behind an https manifest is refused");
        StreamSecurity.EnsureNoDowngrade(plan2, new Uri("http://cdn.example.com/index.m3u8")); // http manifest: nothing to protect
        var good = new StreamPlan { Kind = StreamKind.Hls, Video = HlsParser.Parse("#EXTM3U\n#EXTINF:2,\nhttps://cdn2.example.com/a.ts\n#EXT-X-ENDLIST\n", https).Require().Media };
        StreamSecurity.EnsureNoDowngrade(good, https);
        return Task.CompletedTask;
    }

    public static async Task FreeSpaceRefusal(P paths)
    {
        using var server = new StreamServer(); AddTs(server, "/d", 6, 10_000, 1000);
        using var manager = NewManager(paths, free: _ => 1000L);
        var failed = await Finish(manager, (await manager.AddStreamAsync(server.Url("/d/index.m3u8"), StreamKind.Hls, fileName: "d")).Id);
        Ensure(failed.Status == "Failed" && failed.Error!.Contains("Not enough free disk space"), "refused for lack of space: " + Describe(failed));
        Ensure(Enumerable.Range(0, 6).All(i => server.Count($"/d/s{i}.ts") == 0), "no piece was requested");
        // the estimate (bandwidth x duration, twice) is what is checked when the manifest announces a bandwidth
        server.Add("/d/m.m3u8", "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=8000000\nindex.m3u8\n");
        long asked = 0;
        using var measured = new DownloadManager(paths.Download + "m", paths.State + "m", Options(free: p => { asked = Math.Max(asked, 1); return long.MaxValue / 4; }));
        await RunHls(measured, server.Url("/d/m.m3u8"), "ok");
        Ensure(asked == 1, "free space was asked for");
        var tight = new DownloadManager(paths.Download + "t", paths.State + "t", Options(free: _ => 15_000_000L)); // 8 Mbit/s x 12 s = 12 MB; twice = 24 MB
        using (tight)
        {
            var big = await Finish(tight, (await tight.AddStreamAsync(server.Url("/d/m.m3u8"), StreamKind.Hls, fileName: "tight")).Id);
            Ensure(big.Status == "Failed" && big.Error!.Contains("Not enough free disk space"), "estimate x 2 does not fit: " + Describe(big));
        }
    }

    public static async Task RemoveCleansTheWorkFolder(P paths)
    {
        using var server = new StreamServer(); AddTs(server, "/x", 20, 40_000, 1100);
        using var manager = NewManager(paths, connections: 2);
        var added = await manager.AddStreamAsync(server.Url("/x/index.m3u8"), StreamKind.Hls, fileName: "gone", scheduledAt: DateTimeOffset.UtcNow.AddHours(1));
        manager.SetSpeedLimit(added.Id, 150_000); manager.Schedule(added.Id, DateTimeOffset.UtcNow);
        var work = Path.Combine(paths.State, added.Id + ".parts");
        await WaitUntil(() => JobOf(manager, added.Id).SegmentsDone >= 2 && Directory.Exists(work) && Directory.GetFiles(work).Length > 0, "no partial data");
        manager.Remove(added.Id);
        await WaitUntil(() => !Directory.Exists(work), "work folder not removed");
        await Task.Delay(300);
        Ensure(manager.Jobs.Count == 0 && !File.Exists(added.FilePath) && !Directory.Exists(work), "job, file and work folder are gone");
        Ensure(Directory.GetFiles(Path.Combine(paths.Download, ".ndm-staging")).Length == 0, "no staging");
        // a paused job's folder is removed too
        var paused = await manager.AddStreamAsync(server.Url("/x/index.m3u8"), StreamKind.Hls, fileName: "paused", scheduledAt: DateTimeOffset.UtcNow.AddHours(1));
        manager.SetSpeedLimit(paused.Id, 150_000); manager.Schedule(paused.Id, DateTimeOffset.UtcNow);
        var work2 = Path.Combine(paths.State, paused.Id + ".parts");
        await WaitUntil(() => JobOf(manager, paused.Id).SegmentsDone >= 2, "no progress"); manager.Pause(paused.Id);
        await WaitUntil(() => JobOf(manager, paused.Id).Status == "Paused", "not paused"); await Task.Delay(300);
        Ensure(Directory.Exists(work2), "a paused job keeps its pieces");
        manager.Remove(paused.Id);
        Ensure(!Directory.Exists(work2), "removing a paused job deletes its pieces");
    }

    // ---- queue ----

    public static async Task QueueRoundTrip(P paths)
    {
        using var server = new StreamServer();
        var (vi, v, ai, a) = AddAudioVideoHls(server); // reuse: master with video and audio
        var low = AddTs(server, "/q", 4, 6000, 1200);
        server.Add("/q/master.m3u8", "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=100000\nindex.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=9000000\n../av/video.m3u8\n");
        string id;
        using (var manager = NewManager(paths))
        {
            var added = await manager.AddStreamAsync(server.Url("/q/master.m3u8"), StreamKind.Hls, "v0", StreamSelection.NoAudio, true, "queued", cookie: "c=1", scheduledAt: DateTimeOffset.UtcNow.AddHours(1));
            id = added.Id;
        }
        var json = File.ReadAllText(Path.Combine(paths.State, "queue.json"));
        Ensure(json.Contains("\"StreamKind\": 1") && json.Contains("\"StreamVariantId\": \"v0\"") && json.Contains("\"StreamAudioId\": \"none\"") && json.Contains("\"PreferMp4\": true"), "stream fields are written to queue.json");
        using (var manager = NewManager(paths))
        {
            var restored = JobOf(manager, id);
            Ensure(restored.Status == "Scheduled" && restored.Stream == StreamKind.Hls && restored.FileName == "queued.mp4", "restored: " + Describe(restored) + " " + restored.FileName);
            manager.Schedule(id, DateTimeOffset.UtcNow);
            var done = await Succeed(manager, id);
            Ensure(File.ReadAllBytes(done.FilePath).SequenceEqual(Concat(low)) && done.FileName.EndsWith(".ts"), "the saved variant choice is used after a restart (v0 = the small one): " + done.FileName);
            Ensure(server.Count("/av/v0.m4s") == 0, "the other variant was not fetched");
        }
        // a queue.json written before 1.6.0 (no stream fields anywhere) restores
        using var plain = new StreamServer(); plain.Add("/f.bin", Blob(5, 70_000));
        var oldPaths = (paths.Download + "-old", paths.State + "-old");
        string fileId;
        using (var manager = NewManager(oldPaths))
        {
            var j = await manager.AddAsync(new DownloadRequest(plain.Url("/f.bin"), "f.bin")); fileId = j.Id;
            await Succeed(manager, fileId);
        }
        var queuePath = Path.Combine(oldPaths.Item2, "queue.json");
        var root = JsonNode.Parse(File.ReadAllText(queuePath))!.AsArray();
        foreach (var node in root)
        {
            foreach (var key in new[] { "StreamKind", "StreamVariantId", "StreamAudioId", "PreferMp4" }) node!["Request"]!.AsObject().Remove(key);
            foreach (var key in new[] { "StreamNote", "StreamSegments", "StreamDone", "StreamDetected", "DetectedStream", "ErrorSegment", "AdditionalFiles" }) node!.AsObject().Remove(key);
        }
        File.WriteAllText(queuePath, root.ToJsonString());
        Ensure(!File.ReadAllText(queuePath).Contains("StreamKind"), "old shape prepared");
        using (var manager = NewManager(oldPaths))
        {
            var restored = JobOf(manager, fileId);
            Ensure(restored.Status == "Completed" && restored.Stream == StreamKind.None && restored.SegmentsTotal == 0 && restored.AdditionalFiles.Count == 0 && File.Exists(restored.FilePath), "1.5.9 queue restores: " + Describe(restored));
        }
    }

    // ---- plain downloads of manifests, inspect, refusals ----

    public static async Task PlainDownloadRefusesManifests(P paths)
    {
        using var server = new StreamServer(); AddTs(server, "/p", 3, 5000, 1300);
        server.Add("/p/master.m3u8", "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000\nindex.m3u8\n");
        server.Add("/p/clip.mpd", "<?xml version=\"1.0\"?><MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\" mediaPresentationDuration=\"PT10S\"><Period/></MPD>", "application/dash+xml");
        server.Add("/p/music.m3u", "#EXTM3U\n#EXTINF:123,Artist - Song\nsong.mp3\n", "audio/x-mpegurl");
        server.Add("/p/disguised.bin", "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-STREAM-INF:BANDWIDTH=1000\nindex.m3u8\n", "application/octet-stream");
        var big = new StringBuilder(Head() + "\n"); for (int i = 0; i < 40_000; i++) big.Append("#EXTINF:2.0,\ns").Append(i).Append(".ts\n"); big.Append("#EXT-X-ENDLIST\n");
        server.Add("/p/big.m3u8", big.ToString());
        using var manager = NewManager(paths);
        foreach (var (path, kind) in new[] { ("/p/master.m3u8", StreamKind.Hls), ("/p/clip.mpd", StreamKind.Dash), ("/p/disguised.bin", StreamKind.Hls), ("/p/index.m3u8", StreamKind.Hls), ("/p/big.m3u8", StreamKind.Hls) })
        {
            var job = await manager.AddAsync(new DownloadRequest(server.Url(path), Path.GetFileName(path)));
            var f = await Finish(manager, job.Id);
            Ensure(f.Status == "Failed" && f.ErrorCode == ContentGuard.WrongContent && f.StreamDetected && f.DetectedStream == kind, path + ": refused with the stream hint: " + Describe(f) + " detected=" + f.DetectedStream);
            Ensure(!File.Exists(job.FilePath), path + ": nothing saved");
        }
        var json = File.ReadAllText(Path.Combine(paths.State, "queue.json"));
        Ensure(json.Contains("\"StreamDetected\": true"), "the hint is persisted");
        manager.Dispose();
        using (var again = NewManager(paths)) Ensure(again.Jobs.All(j => j.StreamDetected && j.ErrorCode == ContentGuard.WrongContent), "the hint survives a restart");
        // controls: a music list and an ordinary file still download
        using var control = NewManager((paths.Download + "-c", paths.State + "-c"));
        var music = await control.AddAsync(new DownloadRequest(server.Url("/p/music.m3u"), "music.m3u"));
        var m = await Finish(control, music.Id);
        Ensure(m.Status == "Completed" && !m.StreamDetected, "a plain music list is a file: " + Describe(m));
        // asking for the stream path on the same address works
        var viaStream = await control.AddStreamAsync(server.Url("/p/master.m3u8"), StreamKind.Hls, fileName: "via");
        var s = await Succeed(control, viaStream.Id);
        Ensure(s.FileName == "via.ts", "the streaming path takes the same address");
    }

    public static async Task InspectByContent(P paths)
    {
        using var server = new StreamServer(); AddTs(server, "/i", 3, 3000, 1400);
        server.Add("/i/page.bin", "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000,RESOLUTION=100x50\nindex.m3u8\n", "application/octet-stream");
        server.Add("/i/html", "<!DOCTYPE html><html><body>not a stream</body></html>", "text/html");
        server.Add("/i/live.m3u8", "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:50\n#EXTINF:2,\nl1.ts\n#EXTINF:2,\nl2.ts\n");
        server.Add("/i/drm.m3u8", "#EXTM3U\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://k\",KEYFORMAT=\"com.apple.streamingkeydelivery\"\n#EXTINF:2,\na.ts\n#EXT-X-ENDLIST\n");
        server.Add("/i/dash.mpd", "<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\" mediaPresentationDuration=\"PT20S\"><Period><AdaptationSet mimeType=\"video/mp4\"><Representation id=\"hd\" bandwidth=\"900000\" width=\"1280\" height=\"720\" codecs=\"avc1\"><SegmentTemplate duration=\"2\" media=\"$Number$.m4s\" initialization=\"i.mp4\"/></Representation></AdaptationSet></Period></MPD>");
        server.Add("/i/live.mpd", "<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"dynamic\"><Period/></MPD>");
        server.Add("/i/drm.mpd", "<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\" mediaPresentationDuration=\"PT20S\"><Period><AdaptationSet mimeType=\"video/mp4\"><ContentProtection schemeIdUri=\"urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed\"/><Representation id=\"hd\" bandwidth=\"900000\"><BaseURL>x.mp4</BaseURL></Representation></AdaptationSet></Period></MPD>");
        using var manager = NewManager(paths);
        var m = await manager.InspectStreamAsync(server.Url("/i/page.bin"));
        Ensure(m.Kind == StreamKind.Hls && m.Variants.Count == 1 && m.Variants[0].Resolution == "100x50" && m.Duration == TimeSpan.FromSeconds(6), "content decides, not the name: " + m.Duration);
        var d = await manager.InspectStreamAsync(server.Url("/i/dash.mpd"));
        Ensure(d.Kind == StreamKind.Dash && d.Variants[0].Id == "hd" && d.Duration == TimeSpan.FromSeconds(20), "dash inspect");
        async Task<StreamException> Refused(string path) { try { await manager.InspectStreamAsync(server.Url(path)); } catch (StreamException ex) { return ex; } throw new Exception(path + " should be refused"); }
        Ensure((await Refused("/i/html")).Error == StreamError.Unsupported, "web page");
        Ensure((await Refused("/i/live.m3u8")).Error == StreamError.Live, "live hls");
        Ensure((await Refused("/i/drm.m3u8")).Error == StreamError.Drm, "drm hls");
        Ensure((await Refused("/i/live.mpd")).Error == StreamError.Live, "live dash");
        Ensure((await Refused("/i/drm.mpd")).Error == StreamError.Drm, "drm dash");
        bool http = false; try { await manager.InspectStreamAsync(server.Url("/i/missing.m3u8")); } catch (HttpRequestException ex) { http = ex.StatusCode == HttpStatusCode.NotFound; }
        Ensure(http, "a refusing server is an HTTP error, not a stream error");
        bool arg = false; try { await manager.InspectStreamAsync("file:///C:/x.m3u8"); } catch (ArgumentException) { arg = true; }
        Ensure(arg, "only http(s) addresses");
        // the same refusals as job failures, with codes, and no piece requested
        foreach (var (path, code) in new[] { ("/i/live.m3u8", StreamCodes.Live), ("/i/drm.m3u8", StreamCodes.Drm), ("/i/html", StreamCodes.Unsupported), ("/i/live.mpd", StreamCodes.Live), ("/i/drm.mpd", StreamCodes.Drm) })
        {
            var job = await manager.AddStreamAsync(server.Url(path), path.EndsWith("mpd") ? StreamKind.Dash : StreamKind.Hls, fileName: "r");
            var f = await Finish(manager, job.Id);
            Ensure(f.Status == "Failed" && f.ErrorCode == code, path + ": " + Describe(f));
        }
        Ensure(server.Count("/i/l1.ts") + server.Count("/i/a.ts") == 0, "no piece of a refused stream was requested");
        bool badKind = false; try { await manager.AddStreamAsync(new DownloadRequest(server.Url("/i/dash.mpd"))); } catch (ArgumentException) { badKind = true; }
        Ensure(badKind, "AddStreamAsync needs a kind");
    }

    // ---- DASH ----

    private static string DashManifest(string dir, int seconds, bool audio) =>
        "<?xml version=\"1.0\"?>\n<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\" mediaPresentationDuration=\"PT" + seconds + "S\"><Period>" +
        "<AdaptationSet mimeType=\"video/mp4\" segmentAlignment=\"true\"><SegmentTemplate timescale=\"1000\" duration=\"2000\" initialization=\"$RepresentationID$-init.mp4\" media=\"$RepresentationID$-$Number%05d$.m4s\"/>" +
        "<Representation id=\"video\" codecs=\"avc1.4d401e\" width=\"640\" height=\"360\" bandwidth=\"800000\"/></AdaptationSet>" +
        (audio ? "<AdaptationSet mimeType=\"audio/mp4\" lang=\"en\"><SegmentTemplate timescale=\"1000\" duration=\"2000\" initialization=\"$RepresentationID$-init.mp4\" media=\"$RepresentationID$-$Number%05d$.m4s\"/><Representation id=\"audio\" codecs=\"mp4a.40.2\" bandwidth=\"96000\"/></AdaptationSet>" : "") +
        "</Period></MPD>";

    public static async Task DashVideoAndAudio(P paths)
    {
        var log = FakeTool.NewLog(paths.State);
        try
        {
            Environment.SetEnvironmentVariable(FakeFfmpeg.DurationVariable, "8");
            var tools = await FakeTool.Install(paths.State);
            using var server = new StreamServer();
            var vInit = Init(1); var aInit = Init(2); server.Add("/dash/video-init.mp4", vInit); server.Add("/dash/audio-init.mp4", aInit);
            var v = new List<byte[]>(); var a = new List<byte[]>();
            for (int i = 1; i <= 4; i++) { v.Add(Fragment(10 + i, 9000)); a.Add(Fragment(50 + i, 3000)); server.Add($"/dash/video-{i:00000}.m4s", v[^1]); server.Add($"/dash/audio-{i:00000}.m4s", a[^1]); }
            server.Add("/dash/manifest.mpd", DashManifest("/dash", 8, true));
            using var manager = NewManager(paths, tools: tools);
            var m = await manager.InspectStreamAsync(server.Url("/dash/manifest.mpd"));
            Ensure(m.Variants.Count == 1 && m.AudioTracks.Count == 1 && m.Duration == TimeSpan.FromSeconds(8), "inspect");
            var job = await manager.AddStreamAsync(server.Url("/dash/manifest.mpd"), StreamKind.Dash, fileName: "dash clip");
            Ensure(job.FileName == "dash clip.mp4", "provisional dash name");
            var done = await Succeed(manager, job.Id);
            var work = Path.Combine(paths.State, done.Id + ".parts");
            var calls = FakeFfmpeg.ReadLog(log);
            Ensure(calls[0].SequenceEqual(new[] { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-i", Path.Combine(work, "video.mp4"), "-i", Path.Combine(work, "audio.m4a"), "-map", "0:v:0", "-map", "1:a:0", "-c", "copy", "-movflags", "+faststart", Path.Combine(work, "out.mp4") }), "mux arguments: " + string.Join(" ", calls[0]));
            Ensure(File.ReadAllBytes(done.FilePath).SequenceEqual(Encoding.ASCII.GetBytes("FAKE-MUX\n").Concat(vInit).Concat(Concat(v)).Concat(aInit).Concat(Concat(a)).ToArray()), "dash tracks reach the tool joined");
            Ensure(done.SegmentsTotal == 10, "init + 4 media pieces for each track");
            // video only, no tool needed
            var only = await Succeed(manager, (await manager.AddStreamAsync(server.Url("/dash/manifest.mpd"), StreamKind.Dash, "video", StreamSelection.NoAudio, fileName: "only")).Id);
            var bytes = File.ReadAllBytes(only.FilePath); var boxes = BoxNames(bytes);
            Ensure(only.FileName == "only.mp4" && boxes[0] == "ftyp" && boxes[1] == "moov" && boxes.Count(b => b == "moof") == 4 && bytes.SequenceEqual(vInit.Concat(Concat(v)).ToArray()), "video-only dash is a valid fragmented mp4 layout");
        }
        finally { FakeTool.ResetEnvironment(); }
    }

    public static async Task DashSingleFileIsAPlainDownload(P paths)
    {
        using var server = new StreamServer(); var movie = Blob(77, 400_000); movie[4] = (byte)'f'; server.Add("/one/movie.mp4", movie, "video/mp4");
        server.Add("/one/clip.mpd", "<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\" mediaPresentationDuration=\"PT10S\"><Period><AdaptationSet mimeType=\"video/mp4\"><Representation id=\"x\" bandwidth=\"64000\" codecs=\"avc1\"><BaseURL>movie.mp4</BaseURL><SegmentBase indexRange=\"800-1200\"/></Representation></AdaptationSet></Period></MPD>");
        using var manager = NewManager(paths);
        var job = await manager.AddStreamAsync(server.Url("/one/clip.mpd"), StreamKind.Dash, fileName: "single");
        var done = await Finish(manager, job.Id);
        Ensure(done.Status == "Completed" && done.Stream == StreamKind.None && done.FileName == "single.mp4" && done.Url.EndsWith("/one/movie.mp4"), "became a normal download: " + Describe(done) + " " + done.Url);
        Ensure(File.ReadAllBytes(done.FilePath).SequenceEqual(movie), "file is the representation, byte for byte");
        Ensure(server.For("/one/movie.mp4").Any(r => r.Range is not null), "fetched with ranges like any file");
        manager.Dispose();
        using var again = NewManager(paths);
        Ensure(JobOf(again, job.Id).Stream == StreamKind.None && JobOf(again, job.Id).Status == "Completed", "stays a plain job after a restart");
    }

    public static async Task RealMediaPlays(P paths)
    {
        var ffmpeg = ToolManager.FindOnPath();
        if (ffmpeg is null) { Console.WriteLine("    SKIPPED the playability check: no ffmpeg found in PATH (the opt-in real test covers it; see docs/STREAMS.md)"); return; }
        var work = Path.Combine(paths.State, "real"); Directory.CreateDirectory(work);
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = work };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc=duration=6:size=160x120:rate=15", "-f", "lavfi", "-i", "sine=frequency=440:duration=6", "-c:v", "mpeg2video", "-c:a", "mp2", "-f", "hls", "-hls_time", "2", "-hls_playlist_type", "vod", "-hls_segment_filename", "seg%d.ts", "index.m3u8" }) psi.ArgumentList.Add(a);
        using (var p = Process.Start(psi)!) { await p.WaitForExitAsync(); if (p.ExitCode != 0) { Console.WriteLine("    SKIPPED the playability check: this ffmpeg could not generate the fixture: " + await p.StandardError.ReadToEndAsync()); return; } }
        using var server = new StreamServer();
        foreach (var file in Directory.GetFiles(work)) server.Add("/real/" + Path.GetFileName(file), File.ReadAllBytes(file));
        using var manager = NewManager(paths);
        var done = await RunHls(manager, server.Url("/real/index.m3u8"), "real");
        var check = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "-hide_banner", "-v", "error", "-i", done.FilePath, "-f", "null", "-" }) check.ArgumentList.Add(a);
        using var q = Process.Start(check)!; var err = await q.StandardError.ReadToEndAsync(); await q.WaitForExitAsync();
        Ensure(q.ExitCode == 0 && err.Trim().Length == 0, "the joined transport stream decodes cleanly with ffmpeg: " + err);
        Console.WriteLine("    playability: decoded with " + ffmpeg);
    }
}
