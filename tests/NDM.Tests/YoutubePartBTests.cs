using System.Text.Json;
using NDM.Core;
using static NDM.Tests.StreamKit;

namespace NDM.Tests;

/// <summary>1.11.0 Part B, engine side: link grammar, playlist listing, presets, load progress model, playlist package queue. Fake helper process only (no network).</summary>
internal static class YoutubePartB
{
    public static Task Links()
    {
        const string v = "aqz-KE-bpKQ", pl = "PLav47HAVZMjnTFVZL-aImCQIC0uLZtNCz";
        (string Input, YoutubeLinkKind Kind, string? Video, string? List)[] table =
        {
            ("https://www.youtube.com/watch?v=" + v, YoutubeLinkKind.Video, v, null),
            ("HTTPS://WWW.YOUTUBE.COM/watch?v=" + v, YoutubeLinkKind.Video, v, null),
            ("https://youtube.com/watch?v=" + v, YoutubeLinkKind.Video, v, null),
            ("https://m.youtube.com/watch?v=" + v + "&t=10s", YoutubeLinkKind.Video, v, null),
            ("https://music.youtube.com/watch?v=" + v, YoutubeLinkKind.Video, v, null),
            ("https://youtu.be/" + v, YoutubeLinkKind.Video, v, null),
            ("https://youtu.be/" + v + "?si=abc", YoutubeLinkKind.Video, v, null),
            ("https://www.youtube.com/shorts/" + v, YoutubeLinkKind.Video, v, null),
            ("https://www.youtube.com/embed/" + v, YoutubeLinkKind.Video, v, null),
            ("see https://www.youtube.com/watch?v=" + v + ".", YoutubeLinkKind.None, null, null),
            ("https://www.youtube.com/watch?v=" + v + ".", YoutubeLinkKind.Video, v, null),
            ("(https://www.youtube.com/watch?v=" + v + ")", YoutubeLinkKind.Video, v, null),
            ("\"https://youtu.be/" + v + "\",", YoutubeLinkKind.Video, v, null),
            ("https://www.youtube.com/playlist?list=" + pl, YoutubeLinkKind.Playlist, null, pl),
            ("https://music.youtube.com/playlist?list=" + pl + "&si=x", YoutubeLinkKind.Playlist, null, pl),
            ("https://www.youtube.com/watch?v=" + v + "&list=" + pl, YoutubeLinkKind.VideoInPlaylist, v, pl),
            ("https://youtu.be/" + v + "?list=" + pl, YoutubeLinkKind.VideoInPlaylist, v, pl),
            ("https://www.youtube.com/watch?v=" + v + "&list=RDaqz-KE-bpKQ", YoutubeLinkKind.Video, v, null),
            ("https://www.youtube.com/playlist?list=RDCLAK5uy_kmPRjHDECIcuVwnKsx2Ng7fyNgFKWNJFs", YoutubeLinkKind.None, null, null),
            ("https://www.youtube.com/playlist?list=WL", YoutubeLinkKind.None, null, null),
            ("https://www.youtube.com/playlist?list=LL", YoutubeLinkKind.None, null, null),
            ("https://www.youtube.com/playlist?list=PLshort", YoutubeLinkKind.None, null, null),
            ("https://www.youtube.com/playlist?list=" + pl + "&list=" + pl, YoutubeLinkKind.None, null, null),
            ("https://www.youtube.com/@somechannel", YoutubeLinkKind.None, null, null),
            ("https://www.youtube.com/watch?v=short", YoutubeLinkKind.None, null, null),
            ("https://youtube.com.evil.test/watch?v=" + v, YoutubeLinkKind.None, null, null),
            ("https://user@www.youtube.com/watch?v=" + v, YoutubeLinkKind.None, null, null),
            ("https://www.youtube.com:444/watch?v=" + v, YoutubeLinkKind.None, null, null),
            ("ftp://www.youtube.com/watch?v=" + v, YoutubeLinkKind.None, null, null),
            ("https://example.org/file.zip", YoutubeLinkKind.None, null, null),
            ("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567", YoutubeLinkKind.None, null, null),
            ("", YoutubeLinkKind.None, null, null),
        };
        foreach (var (input, kind, video, list) in table)
        {
            var link = YoutubeLinks.Parse(input);
            Ensure(link.Kind == kind && link.VideoId == video && link.ListId == list, $"link grammar: '{input}' gives {link.Kind} {link.VideoId} {link.ListId}");
        }
        Ensure(YoutubeLinks.Parse("https://www.youtube.com/playlist?list=RDabc").RefusedReason == YoutubeLinks.RefusedMix, "a mix list is refused with the mix reason");
        Ensure(YoutubeLinks.Parse("https://www.youtube.com/playlist?list=WL").RefusedReason == YoutubeLinks.RefusedPrivate, "an account list is refused with the sign-in reason");
        Ensure(YoutubeLinks.Parse("https://www.youtube.com/playlist?list=bad!").RefusedReason == YoutubeLinks.RefusedUnknown, "a malformed list id is refused as invalid");
        Ensure(YoutubeLinks.Looks("https://youtu.be/" + v) && YoutubeLinks.Looks("https://www.youtube.com/playlist?list=RDabc") && !YoutubeLinks.Looks("https://example.org/"), "Looks covers links and refused playlists, not other addresses");
        foreach (var good in new[] { pl, "PL0123456789abcdef", "UU" + new string('a', 22), "OLAK5uy_" + new string('b', 33) }) Ensure(YoutubeLinks.IsPlaylistId(good), "valid list id " + good);
        foreach (var bad in new[] { "", "PL", "PLshort", "RDabcdefghijklmnop", "FLabcdefghijklmnopqrstuv", "UU" + new string('a', 21), "PL" + new string('a', 65), "PLabc def ghijklmnopqrst", "PL" + new string('a', 20) + "/", "LL", "WL" }) Ensure(!YoutubeLinks.IsPlaylistId(bad), "invalid list id '" + bad + "'");
        // the shared handoff grammar (host, app and extension) agrees with the app grammar on playlist ids
        foreach (var id in new[] { pl, "UU" + new string('a', 22), "RDabcdefghijklmnop", "WL", "PLshort" }) Ensure(NDM.Protocol.YoutubePageLink.IsPlaylistId(id) == YoutubeLinks.IsPlaylistId(id), "shared and app list id rules agree for " + id);
        Ensure(NDM.Protocol.YoutubePageLink.CanonicalPlaylist("https://m.youtube.com/playlist?list=" + pl + "&si=1#frag") == "https://www.youtube.com/playlist?list=" + pl, "handoff canonical playlist address");
        foreach (var bad in new[] { "https://www.youtube.com/watch?v=aqz-KE-bpKQ", "https://www.youtube.com/playlist?list=RDabcdefghijklmnop", "https://youtu.be/playlist?list=" + pl, "https://www.youtube.com/playlist?list=" + pl + "%20", "https://evil.test/playlist?list=" + pl, "https://www.youtube.com/playlist?list=" + pl + "&list=" + pl })
        { try { NDM.Protocol.YoutubePageLink.CanonicalPlaylist(bad); throw new Exception("accepted bad playlist handoff " + bad); } catch (ArgumentException) { } }
        // single video grammar is unchanged: a playlist is still not a single video
        try { YoutubeUrls.Canonical("https://www.youtube.com/playlist?list=" + pl); throw new Exception("playlist accepted as a video"); } catch (ArgumentException) { }
        return Task.CompletedTask;
    }

    public static Task PlaylistParsing()
    {
        string Lines(int n) => string.Join("\n", FakeYoutube.PlaylistLines(n.ToString()));
        try { YoutubePlaylists.Parse(""); throw new Exception("empty list accepted"); } catch (YoutubeException ex) { Ensure(ex.Code == "youtube.playlist_empty", "an empty listing is the empty-playlist error"); }
        var one = YoutubePlaylists.Parse(Lines(1));
        Ensure(one.Entries.Count == 1 && one.Title == "Fixture list" && one.Total == 1 && !one.Truncated && one.Entries[0].Available && one.Entries[0].DurationSeconds == 60, "one entry parses with title, count and duration");
        var five = YoutubePlaylists.Parse(Lines(500));
        Ensure(five.Entries.Count == 500 && !five.Truncated, "exactly 500 entries are not truncated");
        var many = YoutubePlaylists.Parse(Lines(600));
        Ensure(many.Entries.Count == 500 && many.Truncated && many.Total == 600 && many.Entries[0].Id == "v0000000000" && many.Entries[499].Id == "v0000000499", "600 entries keep the first 500 in order and say truncated");
        var mixed = YoutubePlaylists.Parse(string.Join("\n", FakeYoutube.PlaylistLines("mixed")), "x");
        Ensure(mixed.Entries.Count == 4 && mixed.Skipped == 2, $"malformed lines are skipped and counted ({mixed.Entries.Count} entries, {mixed.Skipped} skipped)");
        Ensure(mixed.Title == "Fixture / list?" && mixed.Total == 5, "playlist title and count come from the first entry");
        Ensure(mixed.Entries[0].Available && mixed.Entries[1].State == YoutubeEntryState.Private && !mixed.Entries[1].Available && mixed.Entries[2].State == YoutubeEntryState.Deleted && mixed.Entries[3].State == YoutubeEntryState.Members, "private, deleted and members-only entries are listed but not available");
        Ensure(mixed.Entries[0].ThumbnailUrl.StartsWith("https://i.ytimg.com/vi/aaaaaaaaaaa/hqdefault.jpg", StringComparison.Ordinal), "the listed thumbnail from an allowed host is used, the other host is ignored");
        Ensure(mixed.Entries[1].ThumbnailUrl == "https://i.ytimg.com/vi/bbbbbbbbbbb/mqdefault.jpg", "no listed thumbnail falls back to the standard image address");
        Ensure(YoutubePlaylists.Parse("{\"id\":\"aaaaaaaaaaa\",\"title\":\"A\"}\n{\"id\":\"aaaaaaaaaaa\",\"title\":\"A again\"}").Entries.Count == 1, "duplicate ids are dropped");
        Ensure(YoutubePlaylists.Parse("{\"id\":\"aaaaaaaaaaa\",\"title\":\"" + new string('T', 500) + "\"}").Entries[0].Title.Length == 200, "entry titles are bounded");
        Ensure(YoutubePlaylists.Parse("{\"id\":\"aaaaaaaaaaa\",\"title\":\"a\\u202e\\u0007b\"}").Entries[0].Title == "ab", "control and bidi characters are removed from titles");
        Ensure(YoutubePlaylists.Parse("{\"id\":\"aaaaaaaaaaa\",\"title\":\"" + new string('T', YoutubePlaylists.MaxLineChars) + "\"}\n{\"id\":\"bbbbbbbbbbb\",\"title\":\"ok\"}").Entries.Count == 1, "an oversized line is skipped");
        try { YoutubePlaylists.Parse(new string('x', YoutubePlaylists.MaxListChars + 1)); throw new Exception("oversize accepted"); } catch (YoutubeException ex) { Ensure(ex.Code == "youtube.metadata", "oversized listing is a metadata error"); }
        foreach (var good in new[] { "https://i.ytimg.com/vi/x/hq.jpg", "https://img.youtube.com/vi/x/0.jpg" }) Ensure(YoutubePlaylists.IsThumbnailUrl(good), "thumbnail host allowed " + good);
        foreach (var bad in new[] { "http://i.ytimg.com/vi/x/hq.jpg", "https://i.ytimg.com.evil.test/x.jpg", "https://evil.test/i.ytimg.com/x.jpg", "https://user@i.ytimg.com/x.jpg", "https://i.ytimg.com:444/x.jpg", "ftp://i.ytimg.com/x.jpg", "https://i.ytimg.com/x y.jpg", "", null }) Ensure(!YoutubePlaylists.IsThumbnailUrl(bad), "thumbnail refused " + bad);
        Ensure(YoutubePlaylists.Numbered(3, 18, "T") == "03 - T" && YoutubePlaylists.Numbered(7, 120, "T") == "007 - T", "numbering prefix width follows the count");
        return Task.CompletedTask;
    }

    public static Task Presets()
    {
        YoutubeFormat V(string id, int h, string ext = "mp4", bool audio = false, long size = 0) => new(id, ext, h, 0, true, audio, size);
        YoutubeFormat A(string id, int kbps, string ext = "m4a") => new(id, ext, 0, kbps, false, true);
        var meta = new YoutubeMetadata("t", new[] { V("v144", 144), V("v360", 360), V("v720", 720), V("v1080a", 1080, "webm"), V("v1080", 1080, "mp4"), V("v2160", 2160, "webm"), V("p18", 360, "mp4", true), A("a48", 48), A("a128", 128), A("a160", 160, "webm"), A("a256", 256) });
        string Pick(string q, int kbps = 0, bool mp3 = false) { var s = YoutubePresets.Resolve(YoutubeSelection.FromPreset(new(q, kbps), mp3), meta); return s.VideoId + (s.AudioId is null ? "" : "+" + s.AudioId) + (s.Mp3 ? "+mp3" : ""); }
        Ensure(Pick("best") == "v2160+a256", "best picks the tallest video with the best audio: " + Pick("best"));
        Ensure(Pick("1080") == "v1080+a256", "1080 prefers MP4 at equal height: " + Pick("1080"));
        Ensure(Pick("720") == "v720+a256", "720p exact");
        Ensure(Pick("480") == "v360+a256" || Pick("480") == "p18", "480 resolves to the closest lower height (360): " + Pick("480"));
        Ensure(Pick("1440") == "v1080+a256", "1440 with no 1440 format resolves to the best below it: " + Pick("1440"));
        Ensure(Pick("360", 128) == "p18" || Pick("360", 128) == "v360+a128", "360 can use the progressive format or the closest audio: " + Pick("360", 128));
        Ensure(Pick("720", 128) == "v720+a128" && Pick("720", 192) == "v720+a160" && Pick("720", 64) == "v720+a48" && Pick("720", 32) == "v720+a48", "audio quality resolves to the closest rate at or below it, else the lowest: " + Pick("720", 192) + " " + Pick("720", 32));
        Ensure(Pick("audio") == "a256" && Pick("audio", 128) == "a128" && Pick("audio", 192) == "a160" && Pick("audio", 64) == "a48", "audio only picks an audio-only format by rate");
        Ensure(Pick("audio", 0, true) == "a256+mp3", "audio only with conversion carries the MP3 flag");
        var small = new YoutubeMetadata("t", new[] { V("only480", 480, "mp4", true) });
        Ensure(YoutubePresets.Resolve(YoutubeSelection.FromPreset(new("2160")), small).VideoId == "only480" && YoutubePresets.Resolve(YoutubeSelection.FromPreset(new("360")), small).VideoId == "only480", "a request above or below what exists resolves to what exists");
        try { YoutubePresets.Resolve(YoutubeSelection.FromPreset(new("audio")), small.Formats.Count == 1 ? new YoutubeMetadata("t", new[] { V("x", 480) }) : small); throw new Exception("audio from a video-only list resolved"); } catch (YoutubeException ex) { Ensure(ex.Code == "youtube.format", "no audio at all is a clear per-job format error"); }
        var plain = new YoutubeSelection("137", "140");
        Ensure(YoutubePresets.Resolve(plain, meta) == plain, "a selection without a preset is untouched");
        foreach (var bad in new[] { new YoutubeSelection("preset", Preset: new("144p")), new YoutubeSelection("137", Preset: new("best")), new YoutubeSelection("preset", "140", Preset: new("best")), new YoutubeSelection("preset", Mp3: true, Preset: new("720")), new YoutubeSelection("preset", Preset: new("best", 99)) })
        { try { bad.Validate(); throw new Exception("accepted bad preset"); } catch (ArgumentException) { } }
        YoutubeSelection.FromPreset(new("1080", 128)).Validate(); YoutubeSelection.FromPreset(new("audio"), true).Validate();
        var json = JsonSerializer.Serialize(YoutubeSelection.FromPreset(new("720", 192), false));
        Ensure(JsonSerializer.Deserialize<YoutubeSelection>(json) == YoutubeSelection.FromPreset(new("720", 192), false), "a preset selection survives the queue file round trip");
        Ensure(JsonSerializer.Deserialize<YoutubeSelection>("{\"VideoId\":\"18\",\"AudioId\":null,\"Mp3\":false}") == new YoutubeSelection("18"), "an old queue file without a preset still loads");
        return Task.CompletedTask;
    }

    public static Task Folder()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "ndm-plf-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(baseDir);
        try
        {
            var plain = YoutubePlaylists.ResolveFolder(baseDir, "My list");
            Ensure(plain == Path.Combine(baseDir, "My list"), "folder named after the playlist");
            Directory.CreateDirectory(plain); File.WriteAllText(Path.Combine(plain, "a.txt"), "x");
            Ensure(YoutubePlaylists.ResolveFolder(baseDir, "My list") == Path.Combine(baseDir, "My list (2)"), "an existing non-empty folder gets a numbered sibling");
            Directory.CreateDirectory(Path.Combine(baseDir, "Empty")); Ensure(YoutubePlaylists.ResolveFolder(baseDir, "Empty") == Path.Combine(baseDir, "Empty"), "an empty folder is reused");
            foreach (var hostile in new[] { "..", "../../x", "a/b", "a\\b", "CON", "nul", "  ", "x:y*z", "dots...", new string('n', 400) })
            {
                var path = YoutubePlaylists.ResolveFolder(baseDir, hostile);
                Ensure(Path.GetDirectoryName(path) == Path.GetFullPath(baseDir).TrimEnd('\\') && Path.GetFileName(path) is not ("." or "..") && Path.GetFileName(path).Length is > 0 and <= 110, $"hostile playlist name '{hostile[..Math.Min(8, hostile.Length)]}' stays one safe folder directly below the base ({Path.GetFileName(path)})");
            }
        }
        finally { try { Directory.Delete(baseDir, true); } catch (IOException) { } }
        return Task.CompletedTask;
    }

    public static Task Progress()
    {
        var now = TimeSpan.Zero; var p = new YoutubeLoadProgress(() => now);
        Ensure(p.Percent() == 0 && !p.Running, "not started: zero");
        p.Start(); Ensure(p.Stage == YoutubeLoadStage.Preparing && p.Percent() == 0, "starts in the preparing stage at 0%");
        double last = 0; var stages = new List<YoutubeLoadStage> { p.Stage };
        void Step(double seconds, string? line = null)
        {
            now += TimeSpan.FromSeconds(seconds);
            if (line is not null) p.Line(line);
            var v = p.Percent(); Ensure(v >= last - 1e-9 && v <= 95.0 + 1e-9, $"percentage is monotonic and capped at 95 until the result ({last:0.0} -> {v:0.0})"); last = v;
            if (stages[^1] != p.Stage) stages.Add(p.Stage);
        }
        Step(0.5, "[debug] Command-line config: ['x']"); Ensure(p.Stage == YoutubeLoadStage.Preparing && !p.UsesHelperLines, "debug lines do not move the stage");
        Step(1, "[youtube] Extracting URL: https://www.youtube.com/watch?v=aqz-KE-bpKQ"); Ensure(p.Stage == YoutubeLoadStage.Contacting && p.UsesHelperLines, "first helper line: contacting YouTube");
        Step(2, "[youtube] aqz-KE-bpKQ: Downloading webpage");
        Step(1, "[youtube] aqz-KE-bpKQ: Downloading tv samsung player API JSON"); Ensure(p.Stage == YoutubeLoadStage.Contacting, "API lines stay in contacting");
        Step(1, "[youtube] aqz-KE-bpKQ: Downloading player 4a1e575f-main"); Ensure(p.Stage == YoutubeLoadStage.Solving, "player download starts the challenge stage");
        Step(2, "[youtube] [jsc:deno] Solving JS challenges using deno"); Step(3);
        Step(1, "[youtube] aqz-KE-bpKQ: Downloading m3u8 information"); Ensure(p.Stage == YoutubeLoadStage.Reading, "m3u8 line: reading formats");
        Step(1, "[info] aqz-KE-bpKQ: Downloading 1 format(s): 401+258");
        Step(1, "[youtube] aqz-KE-bpKQ: Downloading webpage"); Ensure(p.Stage == YoutubeLoadStage.Reading, "stages never move backwards");
        Step(60); Ensure(p.Percent() <= 95, "a very long wait stays at or below 95%");
        p.Building(); Step(0.2); Ensure(p.Stage == YoutubeLoadStage.Building, "building stage after the helper ends");
        Ensure(stages.SequenceEqual(new[] { YoutubeLoadStage.Preparing, YoutubeLoadStage.Contacting, YoutubeLoadStage.Solving, YoutubeLoadStage.Reading, YoutubeLoadStage.Building }), "stage sequence: " + string.Join(">", stages));
        p.Finish(); Ensure(p.Percent() == 100 && p.Stage == YoutubeLoadStage.Done && !p.Running && p.StatusKey == "youtube.stage.done", "the real result gives exactly 100%");
        // timing curve: no helper lines at all (a different helper version): the stages are guessed from the time
        now = TimeSpan.Zero; var timed = new YoutubeLoadProgress(() => now); timed.Start();
        var seen = new List<double>(); var timedStages = new List<YoutubeLoadStage>();
        for (int i = 0; i < 400; i++) { now += TimeSpan.FromMilliseconds(100); seen.Add(timed.Percent()); if (timedStages.Count == 0 || timedStages[^1] != timed.Stage) timedStages.Add(timed.Stage); }
        for (int i = 1; i < seen.Count; i++) Ensure(seen[i] >= seen[i - 1], "timed curve never decreases");
        Ensure(seen.Max() <= 95 && seen[^1] > 85 && seen[19] > 0 && seen[19] < seen[199], $"timed curve rises, reaches the high 80s/90s and stays under the cap ({seen[19]:0.0}, {seen[199]:0.0}, {seen[^1]:0.0})");
        Ensure(timedStages.Count >= 4 && !timed.UsesHelperLines, "timed mode walks through the stages without helper lines");
        timed.Stop(); var kept = timed.Percent(); now += TimeSpan.FromSeconds(5); Ensure(timed.Percent() == kept && !timed.Running, "cancel or error freezes the estimate; it never reaches 100");
        Ensure(YoutubeLoadProgress.Weights.Sum() == 100 && YoutubeLoadProgress.Weights.Length == 5 && YoutubeLoadProgress.Typical.Length == 5, "five stages whose weights add up to 100");
        foreach (var (line, expected) in new (string, YoutubeLoadStage?)[] { ("", null), ("random text", null), ("WARNING: Unknown codec iamf.001.001.Opus", null), ("[youtube] Extracting URL: x", YoutubeLoadStage.Contacting), ("[youtube:tab] x: Downloading webpage", YoutubeLoadStage.Contacting), ("[youtube:tab] Playlist X: Downloading 5 items of 5", YoutubeLoadStage.Reading), ("        original url = /s/player", null) })
            Ensure(YoutubeLoadProgress.StageOf(line) == expected, "helper line mapping: '" + line + "'");
        return Task.CompletedTask;
    }

    public static async Task Process((string Download, string State) paths)
    {
        var service = await YoutubeTests.Install(paths.State);
        var log = Path.Combine(paths.State, "args.log"); Environment.SetEnvironmentVariable("NDM_YT_TEST_LOG", log);
        try
        {
            var lines = new List<string>();
            var meta = await service.InspectAsync(YoutubeTests.Url, default, lines.Add);
            Ensure(meta.Formats.Count > 0 && lines.Count >= 4, "load info with a line callback sees the helper's progress lines: " + lines.Count + " lines, " + meta.Formats.Count + " formats");
            var stages = new YoutubeLoadProgress(); stages.Start(); foreach (var l in lines) stages.Line(l);
            Ensure(stages.Stage == YoutubeLoadStage.Reading && stages.UsesHelperLines, "the fake helper's lines walk the model to the reading stage");
            Ensure(File.ReadAllText(log).Contains("--verbose"), "a progress callback asks the helper for its progress lines");
            File.Delete(log);
            await service.InspectAsync(YoutubeTests.Url);
            Ensure(!File.ReadAllText(log).Contains("--verbose"), "without a callback the helper command is unchanged");
            // playlist listing
            File.Delete(log);
            Environment.SetEnvironmentVariable("NDM_YT_PLAYLIST_COUNT", "mixed");
            var seen = new List<string>();
            var list = await service.ListPlaylistAsync("https://www.youtube.com/playlist?list=PLav47HAVZMjnTFVZL-aImCQIC0uLZtNCz", default, seen.Add);
            var args = File.ReadAllText(log);
            Ensure(list.Entries.Count == 4 && list.Skipped == 2 && args.Contains("--flat-playlist") && args.Contains("--playlist-end") && args.Contains("501") && args.Contains("PLav47HAVZMjnTFVZL-aImCQIC0uLZtNCz") && !args.Contains("--cookies"), "playlist listing uses the flat listing, an entry cap, the canonical address and no cookies");
            Ensure(seen.Any(l => l.Contains("Downloading webpage")) && YoutubeLoadProgress.StageOf(seen.Last(l => YoutubeLoadProgress.StageOf(l) is not null)) == YoutubeLoadStage.Reading, "playlist progress lines reach the callback");
            Environment.SetEnvironmentVariable("NDM_YT_PLAYLIST_COUNT", "600");
            var big = await service.ListPlaylistAsync("https://www.youtube.com/playlist?list=PLav47HAVZMjnTFVZL-aImCQIC0uLZtNCz");
            Ensure(big.Entries.Count == 500 && big.Truncated, "a 600-entry helper answer is cut to 500 and flagged");
            foreach (var bad in new[] { "https://www.youtube.com/playlist?list=RDabc", "https://www.youtube.com/watch?v=aqz-KE-bpKQ", "https://example.org/playlist?list=PLav47HAVZMjnTFVZL-aImCQIC0uLZtNCz" })
            { try { await service.ListPlaylistAsync(bad); throw new Exception("listed " + bad); } catch (YoutubeException ex) { Ensure(ex.Code is "youtube.playlist_mix" or "youtube.playlist_invalid", "refused before any helper call: " + bad); } }
            // error: the helper fails; the message keeps its classification and the verbose lines never leak into the diagnostic
            Environment.SetEnvironmentVariable("NDM_YT_TEST_MODE", "restricted");
            try { await service.InspectAsync(YoutubeTests.Url, default, _ => { }); throw new Exception("restricted passed"); } catch (YoutubeException ex) { Ensure(ex.Code == "youtube.restricted", "error through the progress path keeps its classification"); }
            Environment.SetEnvironmentVariable("NDM_YT_TEST_MODE", "unknownfail");
            try { await service.InspectAsync(YoutubeTests.Url, default, _ => { }); throw new Exception("failed passed"); } catch (YoutubeException ex) { Ensure(ex.Code == "youtube.failed" && ex.Detail is { } d && d.StartsWith("ERROR:") && !d.Contains("[debug]"), "unknown failure detail is the ERROR line, not a debug line"); }
            // cancel: a user cancel ends the helper and surfaces as cancellation
            Environment.SetEnvironmentVariable("NDM_YT_TEST_MODE", "hang");
            using var cts = new CancellationTokenSource(); var pending = service.InspectAsync(YoutubeTests.Url, cts.Token, _ => { });
            await Task.Delay(500); cts.Cancel();
            try { await pending; throw new Exception("hang finished"); } catch (OperationCanceledException) { }
        }
        finally { Environment.SetEnvironmentVariable("NDM_YT_TEST_LOG", null); Environment.SetEnvironmentVariable("NDM_YT_TEST_MODE", null); Environment.SetEnvironmentVariable("NDM_YT_PLAYLIST_COUNT", null); }
    }

    /// <summary>The playlist package: preset jobs resolve their formats at start, run N at a time with a gap between starts, survive a restart and pause after a rate-limit answer.</summary>
    public static async Task Package((string Download, string State) paths)
    {
        var service = await YoutubeTests.Install(paths.State); var tools = await FakeTool.Install(paths.State);
        var folder = Path.Combine(paths.Download, "pl"); Directory.CreateDirectory(folder);
        DownloadRequest Req(int i, int slots, string? name = null) => new(YoutubeTests.Url.Replace("aqz-KE-bpKQ", "aqz-KE-bp" + (10 + i)), name ?? $"0{i} - Video {i}.mp4", TargetDirectory: folder, Youtube: YoutubeSelection.FromPreset(new("best")), Package: "Fixture list", PackageSlots: slots);
        // 1) a preset job resolves against the real format list when it starts and the title can name the file
        using (var manager = new DownloadManager(paths.Download, Path.Combine(paths.State, "p1"), new DownloadOptions { Tools = tools, Youtube = service }))
        {
            var named = await manager.AddAsync(Req(1, 1));
            try { await WaitUntil(() => JobOf(manager, named.Id).Status == "Completed", "preset job completes", 30); } catch (TimeoutException) { var j = JobOf(manager, named.Id); throw new Exception($"preset job did not complete: {j.Status} {j.ErrorCode} {j.Error}"); }
            var done = JobOf(manager, named.Id);
            Ensure(done.FileName == "01 - Video 1.mkv" && File.Exists(done.FilePath) && manager.RequestOf(named.Id)!.Youtube!.Preset is not null, "a preset job resolves 'best' (video + audio = mkv) when it starts and keeps its numbered name: " + done.FileName);
            var titled = await manager.AddAsync(new DownloadRequest(YoutubeTests.Url, null, TargetDirectory: folder, Youtube: YoutubeSelection.FromPreset(new("360"))));
            await WaitUntil(() => JobOf(manager, titled.Id).Status == "Completed", "preset job without a name completes", 30);
            Ensure(JobOf(manager, titled.Id).FileName.StartsWith("_.._Sample", StringComparison.Ordinal) || JobOf(manager, titled.Id).FileName.Contains("Sample"), "a preset job without a name takes the video title: " + JobOf(manager, titled.Id).FileName);
        }
        // 2) concurrency and the gap between starts
        Environment.SetEnvironmentVariable("NDM_YT_TEST_MODE", "slow");
        try
        {
            foreach (var slots in new[] { 1, 2 })
            {
                using var manager = new DownloadManager(paths.Download, Path.Combine(paths.State, "p2-" + slots), new DownloadOptions { Tools = tools, Youtube = service, MaxConcurrentDownloads = 4 }) { PackageStartDelay = TimeSpan.FromMilliseconds(600) };
                var ids = new List<string>();
                for (int i = 0; i < 4; i++) ids.Add((await manager.AddAsync(Req(i, slots, $"s{slots}-{i}.mp4"))).Id);
                await WaitUntil(() => ids.Count(id => JobOf(manager, id).Status == "Downloading") >= slots, $"{slots} playlist videos running", 20);
                await Task.Delay(1500);
                var running = ids.Count(id => JobOf(manager, id).Status == "Downloading");
                Ensure(running == slots, $"Downloads at once = {slots}: exactly {slots} of 4 run, the rest wait ({running})");
                Ensure(ids.Count(id => JobOf(manager, id).Status == "Queued") == 4 - slots, "the others stay queued");
                foreach (var id in ids) manager.Pause(id);
            }
            using (var manager = new DownloadManager(paths.Download, Path.Combine(paths.State, "p2-gap"), new DownloadOptions { Tools = tools, Youtube = service, MaxConcurrentDownloads = 4 }) { PackageStartDelay = TimeSpan.FromSeconds(2) })
            {
                var a = await manager.AddAsync(Req(10, 2, "g-a.mp4")); var b = await manager.AddAsync(Req(11, 2, "g-b.mp4"));
                await WaitUntil(() => JobOf(manager, a.Id).Status == "Downloading", "first starts", 10);
                await Task.Delay(700);
                Ensure(JobOf(manager, b.Id).Status == "Queued", "the second video of the package waits for the start gap");
                await WaitUntil(() => JobOf(manager, b.Id).Status == "Downloading", "second starts after the gap", 10);
                manager.Pause(a.Id); manager.Pause(b.Id);
            }
            // a different package or a plain job is not limited
            using (var manager = new DownloadManager(paths.Download, Path.Combine(paths.State, "p2-other"), new DownloadOptions { Tools = tools, Youtube = service, MaxConcurrentDownloads = 4 }) { PackageStartDelay = TimeSpan.FromMilliseconds(100) })
            {
                var limited = await manager.AddAsync(Req(20, 1, "o-a.mp4"));
                var plain = await manager.AddAsync(new DownloadRequest(YoutubeTests.Url, "o-plain.mp4", TargetDirectory: folder, Youtube: new("18")));
                await WaitUntil(() => JobOf(manager, limited.Id).Status == "Downloading" && JobOf(manager, plain.Id).Status == "Downloading", "limited and plain job both run", 15);
                manager.Pause(limited.Id); manager.Pause(plain.Id);
            }
        }
        finally { Environment.SetEnvironmentVariable("NDM_YT_TEST_MODE", null); }
        // 3) restart: the package, the preset and the slot count are saved; restored jobs wait paused
        var restartState = Path.Combine(paths.State, "p3");
        string id1;
        using (var manager = new DownloadManager(paths.Download, restartState, new DownloadOptions { Tools = tools, Youtube = service }))
        {
            var added = await manager.AddManyAsync(new[] { Req(30, 3, "r-1.mp4"), Req(31, 3, "r-2.mp4") }, startPaused: true);
            Ensure(added.Added.Count == 2 && added.Failed.Count == 0 && added.Added.All(j => j.Package == "Fixture list"), "bulk add keeps the package name");
            id1 = added.Added[0].Id;
        }
        using (var restored = new DownloadManager(paths.Download, restartState, new DownloadOptions { Tools = tools, Youtube = service }))
        {
            var request = restored.RequestOf(id1)!;
            Ensure(request.PackageSlots == 3 && request.Package == "Fixture list" && request.Youtube?.Preset == new YoutubePreset("best") && JobOf(restored, id1).Status == "Paused" && restored.Jobs.Count == 2, "package, slots and preset survive a restart; jobs wait paused");
        }
        // 4) invalid package settings are refused
        using (var manager = new DownloadManager(paths.Download, Path.Combine(paths.State, "p4"), new DownloadOptions { Tools = tools, Youtube = service }))
        {
            foreach (var bad in new[] { Req(40, 0), Req(41, 5), Req(42, 2) with { Package = null } })
            { try { await manager.AddPausedAsync(bad); throw new Exception("accepted bad package concurrency"); } catch (ArgumentException) { } }
        }
        // 5) a rate-limit answer fails that job and pauses the rest of its package
        Environment.SetEnvironmentVariable("NDM_YT_TEST_MODE", "rate");
        try
        {
            using var manager = new DownloadManager(paths.Download, Path.Combine(paths.State, "p5"), new DownloadOptions { Tools = tools, Youtube = service }) { PackageStartDelay = TimeSpan.FromMilliseconds(100) };
            var jobs = new List<DownloadJob>(); for (int i = 0; i < 3; i++) jobs.Add(await manager.AddAsync(Req(50 + i, 1, $"rate-{i}.mp4")));
            await WaitUntil(() => JobOf(manager, jobs[0].Id).Status == "Failed", "first job fails with the rate answer", 20);
            var first = JobOf(manager, jobs[0].Id);
            Ensure(first.ErrorCode == "youtube.rate", "the job keeps the rate-limit code: " + first.ErrorCode);
            Ensure(JobOf(manager, jobs[1].Id).Status == "Paused" && JobOf(manager, jobs[2].Id).Status == "Paused", "the rest of the package is paused instead of hammering the site");
        }
        finally { Environment.SetEnvironmentVariable("NDM_YT_TEST_MODE", null); }
    }
}
