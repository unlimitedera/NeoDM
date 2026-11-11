using System.Text;
using System.Text.Json;
using NDM.Core;
using static NDM.Tests.StreamKit;

namespace NDM.Tests;

using P = (string Download, string State);

/// <summary>1.13.0: the YouTube format picker model (items 5) and subtitles (item 6): rules, command lines, fake yt-dlp / ffmpeg processes, HLS and DASH fixtures.</summary>
internal static class V1130Tests
{
    private const long MB = 1024 * 1024;
    private static YoutubeFormat V(string id, string ext, int h, string codec, long size, bool audio = false, double fps = 30) => new(id, ext, h, 0, true, audio, size, false, codec, audio ? "mp4a.40.2" : null, fps);
    private static YoutubeFormat A(string id, string ext, int kbps, string codec, long size) => new(id, ext, 0, kbps, false, true, size, false, null, codec);
    private static YoutubeFormat[] Typical() => new[]
    {
        V("313", "webm", 2160, "vp09.00.51.08", 300 * MB), V("248", "webm", 1080, "vp09.00.40.08", 80 * MB), V("137", "mp4", 1080, "avc1.640028", 100 * MB),
        V("247", "webm", 720, "vp09.00.31.08", 40 * MB), V("136", "mp4", 720, "avc1.4d401f", 50 * MB), V("135", "mp4", 480, "avc1.4d401e", 25 * MB),
        V("18", "mp4", 360, "avc1.42001E", 20 * MB, audio: true),
        A("140", "m4a", 128, "mp4a.40.2", 12 * MB), A("139", "m4a", 48, "mp4a.40.5", 4 * MB), A("251", "webm", 160, "opus", 13 * MB), A("250", "webm", 70, "opus", 5 * MB)
    };
    private static string Heights(YoutubeFormatPicker p) => string.Join(",", p.Resolutions.Select(r => r.Height));

    // ---------------------------------------------------------------- item 5: the selection model
    public static Task Picker()
    {
        var p = new YoutubeFormatPicker(Typical());
        Ensure(p.Type == PickerType.VideoAudio && p.Container == "mp4" && p.Height == 0 && p.AudioId is null, "default choice: video + audio, MP4, best, best");
        Ensure(p.AvailableTypes.SequenceEqual(new[] { PickerType.VideoAudio, PickerType.VideoOnly, PickerType.AudioOnly }), "all three types offered when all exist");
        Ensure(p.AvailableContainers.SequenceEqual(new[] { "mp4", "webm", "mkv" }), "all containers can hold something here");
        Ensure(Heights(p) == "0,2160,1080,720,480,360", "short resolution list for MP4: " + Heights(p));
        var best = p.Result!;
        Ensure(best.Height == 2160 && best.Primary.Id == "313" && best.Audio!.Id == "140" && best.NeedsRemux && best.Extension == "mp4", "Best in MP4 is the 2160p stream with AAC sound and needs repackaging");
        p.Height = 1080;
        var r = p.Result!;
        Ensure(r.Primary.Id == "137" && r.Audio!.Id == "140" && !r.NeedsRemux && r.Extension == "mp4" && r.EstimatedSize == 112 * MB && r.SizeKnown, "1080p MP4: native streams, no repackaging, estimated size");
        Ensure(r.Selection.VideoId == "137" && r.Selection.AudioId == "140" && r.Selection.Container == "mp4", "selection carries the container");
        var line = p.Resolutions.First(x => x.Height == 1080); Ensure(line.Codec == "avc1" && line.Fps == 30 && line.Size == 112 * MB, "resolution line has codec, fps and size");
        Ensure(p.AudioOptions.Select(a => a.Id ?? "best").SequenceEqual(new[] { "best", "140", "139" }), "MP4 audio list holds AAC only: " + string.Join(",", p.AudioOptions.Select(a => a.Id ?? "best")));
        p.AudioId = "139"; Ensure(p.Result!.Audio!.Id == "139" && p.Result.AudioBitrate == 48, "audio quality choice");

        p.Container = "webm";
        Ensure(Heights(p) == "0,2160,1080,720" && p.Height == 1080, "WebM keeps only what it can hold: " + Heights(p));
        Ensure(p.Result!.Primary.Id == "248" && p.Result.Audio!.Id == "251" && !p.Result.NeedsRemux && p.Result.Extension == "webm", "1080p WebM: VP9 + Opus as they are");
        Ensure(p.AudioId is null, "audio choice resets when it no longer fits");
        p.Container = "mkv";
        Ensure(Heights(p) == "0,2160,1080,720,480,360", "MKV holds everything");
        Ensure(p.Result!.Primary.Id == "137" && p.Result.Audio!.Id == "251" && !p.Result.NeedsRemux && p.Result.Extension == "mkv", "MKV merge is not a repackaging warning");
        p.Container = "mp4"; p.Height = 360;
        var combined = p.Result!;
        Ensure(combined.Primary.Id == "18" && combined.Audio is null && combined.IncludesAudio && combined.Selection.AudioId is null && p.AudioOptions.Count == 0 && !combined.NeedsRemux, "360p comes as one format with sound: no audio list");
        p.Container = "mkv"; Ensure(p.Result!.NeedsRemux && p.Result.Extension == "mkv", "combined MP4 format into MKV needs repackaging");

        var vo = new YoutubeFormatPicker(Typical()) { Type = PickerType.VideoOnly };
        Ensure(Heights(vo) == "0,2160,1080,720,480", "video only never lists the combined format: " + Heights(vo));
        Ensure(vo.Result!.Selection.VideoOnly && vo.Result.Selection.AudioId is null && vo.Result.Audio is null && !vo.Result.IncludesAudio, "video only selection has no sound");
        vo.Container = "webm"; vo.Height = 1080; Ensure(vo.Result!.Primary.Id == "248" && vo.Result.Extension == "webm", "video only in WebM");

        var ao = new YoutubeFormatPicker(Typical()) { Type = PickerType.AudioOnly };
        Ensure(ao.AvailableContainers.Count == 0 && ao.Resolutions.Count == 0, "audio only has no container or resolution step");
        Ensure(ao.AudioOptions.Count == 5 && ao.AudioOptions[0].IsBest, "audio only list: best + 4 rates");
        Ensure(ao.Result!.Primary.Id == "251" && ao.Result.Extension == "webm" && ao.Result.Selection.Mp3 == false, "audio only best keeps the original file");
        ao.AudioId = "140"; Ensure(ao.Result!.Extension == "m4a", "audio only chosen rate");
        ao.Mp3 = true; Ensure(ao.Result!.Selection.Mp3 && ao.Result.Extension == "mp3", "audio only MP3 conversion");

        // availability filtering
        var plain = new YoutubeFormatPicker(new[] { V("137", "mp4", 1080, "avc1", MB), A("140", "m4a", 128, "mp4a.40.2", MB) });
        Ensure(plain.AvailableContainers.SequenceEqual(new[] { "mp4", "mkv" }), "WebM is not offered when no stream can go into it");
        Ensure(new YoutubeFormatPicker(new[] { A("140", "m4a", 128, "mp4a", MB) }).AvailableTypes.SequenceEqual(new[] { PickerType.AudioOnly }), "only audio exists: audio only is the only type");
        var onlyCombined = new YoutubeFormatPicker(new[] { V("18", "mp4", 360, "avc1", MB, audio: true) });
        Ensure(onlyCombined.AvailableTypes.SequenceEqual(new[] { PickerType.VideoAudio }) && Heights(onlyCombined) == "0,360", "only a combined format: no video-only type");
        // audio-less and silly input stays safe
        Ensure(new YoutubeFormatPicker(Array.Empty<YoutubeFormat>()).Result is null, "no formats, no result");
        var odd = new YoutubeFormatPicker(new[] { V("v1", "mp4", 1078, "avc1", 0), A("a1", "m4a", 130, "mp4a", 0) });
        Ensure(Heights(odd) == "0,1080" && !odd.Result!.SizeKnown, "a height just under a standard one is listed as the standard one; unknown size is not claimed");

        // "show all formats": an exact format replaces the steps until one is changed
        var raw = new YoutubeFormatPicker(Typical());
        Ensure(raw.UseRaw("136") && raw.Result!.Primary.Id == "136" && raw.Result.Audio!.Id == "140" && raw.Result.Extension == "mp4" && raw.RawId == "136", "raw picture format gets the audio that fits");
        Ensure(raw.UseRaw("251") && raw.Result!.Primary.Id == "251" && !raw.Result.Primary.Video, "raw audio format");
        Ensure(!raw.UseRaw("nope"), "unknown raw id refused");
        raw.Height = 720; Ensure(raw.RawId is null, "changing a step leaves the raw choice");

        // job-side rules for the selection
        var meta = new YoutubeMetadata("t", Typical());
        var def = new YoutubeFormatPicker(Typical()); def.Height = 1080;
        Ensure(YoutubeService.OutputExtension(def.Result!.Selection, meta) == "mp4", "mp4 output for a merged MP4 selection");
        var embedWebm = new YoutubeSelection("248", "251", false, null, "webm", false, new SubtitleRequest(SubtitleMode.Embed, new[] { new SubtitleChoice("en") }));
        Ensure(YoutubeService.OutputExtension(embedWebm, meta) == "mkv", "embedding subtitles into WebM gives MKV");
        Ensure(YoutubeService.OutputExtension(embedWebm with { Subtitles = new SubtitleRequest(SubtitleMode.Separate, new[] { new SubtitleChoice("en") }) }, meta) == "webm", "separate files keep WebM");
        Ensure(YoutubeService.OutputExtension(new YoutubeSelection("137", null, false, null, "mkv", true), meta) == "mkv", "video only can be repackaged");
        Throws<ArgumentException>(() => new YoutubeSelection("137", null, false, null, "avi").Validate(), "unknown container refused");
        Throws<ArgumentException>(() => new YoutubeSelection("137", "140", false, null, null, true).Validate(), "video only with audio refused");
        Throws<ArgumentException>(() => new YoutubeSelection("137", "140", false, null, null, false, new SubtitleRequest(SubtitleMode.Embed, new[] { new SubtitleChoice("../x") })).Validate(), "unsafe subtitle code refused");
        Throws<ArgumentException>(() => new YoutubeSelection("251", null, true, null, null, false, new SubtitleRequest(SubtitleMode.Embed, new[] { new SubtitleChoice("en") })).Validate(), "audio conversion with subtitles refused");

        // playlist presets use the same vocabulary
        var webm = YoutubePresets.Resolve(YoutubeSelection.FromPreset(new YoutubePreset("1080", 0, "webm")), meta);
        Ensure(webm.VideoId == "248" && webm.AudioId == "251" && webm.Container == "webm", "preset with WebM picks streams WebM can hold");
        var mp4 = YoutubePresets.Resolve(YoutubeSelection.FromPreset(new YoutubePreset("720", 128, "mp4")), meta);
        Ensure(mp4.VideoId == "136" && mp4.AudioId == "140" && mp4.Container == "mp4", "preset with MP4");
        var auto = YoutubePresets.Resolve(YoutubeSelection.FromPreset(new YoutubePreset("1080")), meta);
        Ensure(auto.Container is null && auto.VideoId == "137", "preset without a container behaves as before");
        Ensure(!new YoutubePreset("best", 0, "avi").IsValid && new YoutubePreset("best", 0, "mkv").IsValid, "preset container is validated");
        var json = JsonSerializer.Serialize(new YoutubeSelection("137", "140", false, null, "mp4", false, new SubtitleRequest(SubtitleMode.Separate, new[] { new SubtitleChoice("en", true) })));
        var back = JsonSerializer.Deserialize<YoutubeSelection>(json)!;
        Ensure(back.Subtitles!.Choices.Single().Auto && back.Subtitles.Mode == SubtitleMode.Separate && back.Container == "mp4", "selection survives the queue file");
        Ensure(JsonSerializer.Deserialize<YoutubeSelection>("{\"VideoId\":\"18\"}")!.Container is null, "an old queue entry still loads");
        return Task.CompletedTask;
    }

    private static void Throws<T>(Action a, string what) where T : Exception { try { a(); } catch (T) { return; } throw new Exception("accepted: " + what); }

    // ---------------------------------------------------------------- item 6: pure rules
    public static Task SubtitleRules_()
    {
        const string listing = "{\"subtitles\":{\"en\":[{\"ext\":\"vtt\",\"url\":\"https://x.test/secret?sig=1\",\"name\":\"English\"}],\"pt-BR\":[{\"name\":\"Portuguese (Brazil)\"}],\"live_chat\":[{\"ext\":\"json\"}]},\"automatic_captions\":{\"en\":[{\"name\":\"English\"}],\"fr\":[{\"ext\":\"vtt\"}],\"bad code!\":[{}]}}";
        var l = SubtitleRules.ParseListing("some noise\n" + listing + "\n");
        Ensure(l.Manual.Select(x => x.Code).SequenceEqual(new[] { "en", "pt-BR" }) && l.Manual[0].Name == "English" && !l.Manual[0].Auto, "creator languages first, with names");
        Ensure(l.Automatic.Select(x => x.Code).SequenceEqual(new[] { "fr" }) && l.Automatic[0].Auto && l.Automatic[0].Name.Contains("French"), "automatic languages below; a language the creator covers is not repeated; unsafe codes dropped");
        Ensure(!JsonSerializer.Serialize(l).Contains("secret"), "no caption address is kept");
        Ensure(!SubtitleRules.ParseListing("").Any && !SubtitleRules.ParseListing("{broken").Any && !SubtitleRules.ParseListing("{}").Any, "empty and broken output give no subtitles");
        Ensure(SubtitleRules.MatchWindowsLanguage(new[] { "en", "pt-BR", "zh-Hans" }, "pt-PT") == "pt-BR" && SubtitleRules.MatchWindowsLanguage(new[] { "en", "pt-BR" }, "en-US") == "en" && SubtitleRules.MatchWindowsLanguage(new[] { "en" }, "ja-JP") is null && SubtitleRules.MatchWindowsLanguage(new[] { "zh-Hans" }, "zh-CN") == "zh-Hans" && SubtitleRules.MatchWindowsLanguage(new[] { "en-orig", "fr" }, "en-GB") == "en-orig", "Windows language matching");
        Ensure(SubtitleRules.OutputExtension("webm", SubtitleMode.Embed) == "mkv" && SubtitleRules.OutputExtension("webm", SubtitleMode.Separate) == "webm" && SubtitleRules.OutputExtension("mp4", SubtitleMode.Embed) == "mp4" && SubtitleRules.OutputExtension("mkv", SubtitleMode.Embed) == "mkv", "container rule per mode");
        Ensure(SubtitleRules.SeparateName("My video.mp4", "en") == "My video.en.srt" && SubtitleRules.SeparateName("a.mkv", "pt-BR") == "a.pt-BR.srt", "separate file names");
        Ensure(!SubtitleRules.SeparateName("a.mkv", "../x").Contains('/') && !SubtitleRules.SeparateName("a.mkv", "..\\x").Contains('\\'), "separate file names cannot leave the folder");
        var fetch = SubtitleRules.FetchArguments("https://www.youtube.com/watch?v=aqz-KE-bpKQ", "C:/deno.exe", "C:/w", new[] { "en", "fr" }, false, "C:/ffmpeg.exe");
        Ensure(fetch.Contains("--skip-download") && fetch.Contains("--write-subs") && !fetch.Contains("--write-auto-subs") && fetch[fetch.IndexOf("--sub-langs") + 1] == "en,fr" && fetch[fetch.IndexOf("--convert-subs") + 1] == "srt" && !fetch.Contains("--embed-subs") && fetch[^1] == "https://www.youtube.com/watch?v=aqz-KE-bpKQ" && fetch[^2] == "--", "caption fetch arguments: no media, creator captions");
        // 1.13.1 user report: creator captions came back 429 from the default player (proof-of-origin token demanded); the embedded TV player still serves them.
        Ensure(fetch[fetch.IndexOf("--extractor-args") + 1] == "youtube:player_client=tv_embedded", "creator captions go through the embedded TV player");
        var fetchAuto = SubtitleRules.FetchArguments("https://www.youtube.com/watch?v=aqz-KE-bpKQ", "d", "w", new[] { "fr" }, true, "f");
        Ensure(fetchAuto.Contains("--write-auto-subs"), "automatic captions switch");
        Ensure(!fetchAuto.Contains("--extractor-args"), "automatic captions keep the default player (every player answers 429 today; the failure is reported, not hidden)");
        var subs = new[] { new SubtitleFile("C:/w/media.en.srt", "en"), new SubtitleFile("C:/w/media.pt-BR.srt", "pt-BR") };
        var mp4 = SubtitleRules.EmbedArguments("C:/w/media.mp4", subs, "C:/w/out.mp4", "mp4");
        Ensure(string.Join(" ", mp4) == "-y -hide_banner -nostdin -loglevel error -i C:/w/media.mp4 -i C:/w/media.en.srt -i C:/w/media.pt-BR.srt -map 0 -map 1:0 -map 2:0 -c copy -c:s mov_text -metadata:s:s:0 language=eng -metadata:s:s:0 title=English -metadata:s:s:1 language=por -metadata:s:s:1 title=Portuguese (Brazil) C:/w/out.mp4", "MP4 embed arguments: " + string.Join(" ", mp4));
        var mkv = SubtitleRules.EmbedArguments("v.mkv", subs.Take(1).ToList(), "o.mkv", "mkv");
        Ensure(mkv.Contains("srt") && !mkv.Contains("mov_text") && mkv.Contains("-c") && mkv[mkv.IndexOf("-c") + 1] == "copy", "MKV keeps SRT tracks, streams are copied");
        const string vtt = "\uFEFFWEBVTT\n\nNOTE a comment\n\nSTYLE\n::cue { color: red }\n\n1\n00:00:01.000 --> 00:00:02.500 align:start\n<c.yellow>Hello</c> &amp; <i>welcome</i>\nsecond line\n\n02:03.4 --> 02:05.000\nNo hour\n\n00:00:09.000 --> 00:00:10.000\n\n\nbroken --> line\n";
        var srt = SubtitleRules.VttToSrt(vtt);
        Ensure(srt == "1\n00:00:01,000 --> 00:00:02,500\nHello & welcome\nsecond line\n\n2\n00:02:03,400 --> 00:02:05,000\nNo hour\n\n", "WebVTT to SubRip: " + srt.Replace("\n", "|"));
        var joined = SubtitleRules.JoinVtt(new[] { "WEBVTT\nX-TIMESTAMP-MAP=LOCAL:0\n\n00:00:01.000 --> 00:00:02.000\nA\n", "WEBVTT\n\n00:00:03.000 --> 00:00:04.000\nB\n" });
        Ensure(joined.StartsWith("WEBVTT\n\n") && joined.Split("WEBVTT").Length == 2 && SubtitleRules.VttToSrt(joined).Contains("2\n00:00:03,000 --> 00:00:04,000\nB"), "playlist pieces are joined under one header");
        Ensure(!new SubtitleRequest(SubtitleMode.Embed, Array.Empty<SubtitleChoice>()).IsValid && !new SubtitleRequest(SubtitleMode.Embed, Enumerable.Range(0, 41).Select(i => new SubtitleChoice("l" + i)).ToArray()).IsValid && new SubtitleRequest(SubtitleMode.Embed, new[] { new SubtitleChoice("zh-Hans") }).IsValid, "request limits");
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- item 6: YouTube with fake yt-dlp / ffmpeg processes
    public static async Task YoutubeSubtitles(P paths)
    {
        var log = Path.Combine(paths.State, "yt.log"); var ffLog = FakeTool.NewLog(paths.State);
        Environment.SetEnvironmentVariable("NDM_YT_TEST_LOG", log);
        try
        {
            var service = await YoutubeTests.Install(paths.State); var tools = await FakeTool.Install(paths.State);
            // listing: only names, own helper call, no caption download
            Environment.SetEnvironmentVariable("NDM_YT_SUBS", "{\"subtitles\":{\"en\":[{\"name\":\"English\"}]},\"automatic_captions\":{\"fr\":[{\"name\":\"French\"}]}}");
            var listing = await service.ListSubtitlesAsync(YoutubeTests.Url);
            Ensure(listing.Manual.Single().Code == "en" && listing.Automatic.Single().Code == "fr", "listing parsed from the helper output");
            var call = File.ReadAllLines(log).Select(l => JsonSerializer.Deserialize<string[]>(l)!).Last();
            Ensure(call.Contains("--skip-download") && call[call.ToList().IndexOf("--print") + 1] == YoutubeService.SubtitleTemplate && !call.Contains("--write-subs") && !call.Contains("--write-auto-subs") && !call.Contains("--dump-single-json"), "listing asks for the two language maps only: " + string.Join(" ", call));
            Ensure(!YoutubeService.InfoTemplate.Contains("subtitles") && !YoutubeService.FormatsTemplate.Contains("caption"), "the format call never asks for captions");
            Environment.SetEnvironmentVariable("NDM_YT_TEST_MODE", "unknownfail");
            Ensure(!(await service.ListSubtitlesAsync(YoutubeTests.Url)).Any, "a failing listing gives no subtitles instead of an error");
            Environment.SetEnvironmentVariable("NDM_YT_TEST_MODE", "oversize");
            Ensure(!(await service.ListSubtitlesAsync(YoutubeTests.Url)).Any, "garbage output gives no subtitles");
            Environment.SetEnvironmentVariable("NDM_YT_TEST_MODE", null);

            YoutubeSelection Sel(SubtitleMode mode, string container, params SubtitleChoice[] choices) => new("137", "140", false, null, container, false, new SubtitleRequest(mode, choices));
            async Task<(string Path, YoutubeDownloadExtras Extras, List<string> Phases)> Run(string name, YoutubeSelection sel, ToolManager? muxer = null)
            {
                var extras = new YoutubeDownloadExtras(); var phases = new List<string>();
                var path = await service.DownloadAsync(YoutubeTests.Url, sel, Path.Combine(paths.State, name), muxer ?? tools, null, (_, _) => { }, default, p => phases.Add(p.Phase), null, extras);
                return (path, extras, phases);
            }
            File.Delete(log);
            // separate files
            var sep = await Run("sep", Sel(SubtitleMode.Separate, "mp4", new SubtitleChoice("en"), new SubtitleChoice("fr", true)));
            Ensure(Path.GetFileName(sep.Path) == "media.mp4" && new FileInfo(sep.Path).Length == 4096, "video file is untouched by separate subtitles");
            Ensure(sep.Extras.Separate.Select(s => s.Language).OrderBy(x => x).SequenceEqual(new[] { "en", "fr" }) && sep.Extras.Separate.All(s => File.ReadAllText(s.Path).Contains("Hello")) && sep.Extras.Warning is null, "two separate .srt files");
            Ensure(sep.Phases.Contains("subtitles"), "the job reports the subtitle step");
            var calls = File.ReadAllLines(log).Select(l => JsonSerializer.Deserialize<string[]>(l)!).ToList();
            var main = calls.First(c => c.Contains("--format")); var creator = calls.Single(c => c.Contains("--write-subs")); var auto = calls.Single(c => c.Contains("--write-auto-subs"));
            Ensure(main[main.ToList().IndexOf("--merge-output-format") + 1] == "mp4" && main.Contains("--no-write-subs"), "video call merges into the chosen container and fetches no captions");
            Ensure(creator[creator.ToList().IndexOf("--sub-langs") + 1] == "en" && auto[auto.ToList().IndexOf("--sub-langs") + 1] == "fr" && creator.Contains("--skip-download") && !creator.Contains("--format"), "caption calls: creator then automatic, no media");
            Ensure(!File.Exists(ffLog) || FakeFfmpeg.ReadLog(ffLog).Count == 0, "separate files need no ffmpeg call");
            // embed in MP4 and MKV
            var emb = await Run("mp4", Sel(SubtitleMode.Embed, "mp4", new SubtitleChoice("en"), new SubtitleChoice("pt-BR")));
            var ff = FakeFfmpeg.ReadLog(ffLog).Last();
            Ensure(Path.GetFileName(emb.Path) == "media.mp4" && emb.Extras.Separate.Count == 0 && emb.Extras.Warning is null && File.ReadAllText(emb.Path).StartsWith("FAKE-MUX"), "embedded: one file");
            Ensure(ff.Contains("mov_text") && ff.Count(a => a == "-i") == 3 && ff.Contains("language=eng") && ff.Contains("language=por") && !ff.Contains("srt"), "MP4 gets mov_text tracks with languages: " + string.Join(" ", ff));
            var mkv = await Run("mkv", Sel(SubtitleMode.Embed, "mkv", new SubtitleChoice("en")));
            ff = FakeFfmpeg.ReadLog(ffLog).Last();
            Ensure(Path.GetFileName(mkv.Path) == "media.mkv" && ff.Contains("srt") && !ff.Contains("mov_text"), "MKV gets SRT tracks");
            Ensure(!Directory.GetFiles(Path.Combine(paths.State, "mkv"), "media-with-subtitles.*").Any(), "no temporary output is left behind");
            // failures never fail the video
            Environment.SetEnvironmentVariable("NDM_YT_SUBMODE", "fail");
            var failed = await Run("fail", Sel(SubtitleMode.Embed, "mp4", new SubtitleChoice("en")));
            Ensure(new FileInfo(failed.Path).Length == 4096 && failed.Extras.Warning == "youtube.subtitles_failed" && failed.Extras.Separate.Count == 0, "caption fetch failure is a warning only");
            Environment.SetEnvironmentVariable("NDM_YT_SUBMODE", "none");
            var none = await Run("none", Sel(SubtitleMode.Separate, "mp4", new SubtitleChoice("en")));
            Ensure(File.Exists(none.Path) && none.Extras.Warning == "youtube.subtitles_none", "no caption files is a warning only");
            Environment.SetEnvironmentVariable("NDM_YT_SUBMODE", null);
            Environment.SetEnvironmentVariable(FakeFfmpeg.ModeVariable, "fail");
            var embedFail = await Run("embedfail", Sel(SubtitleMode.Embed, "mp4", new SubtitleChoice("en")));
            Ensure(new FileInfo(embedFail.Path).Length == 4096 && embedFail.Extras.Warning == "youtube.subtitles_embed_failed" && embedFail.Extras.Separate.Count == 1, "embed failure keeps the video and saves the captions separately");
            Environment.SetEnvironmentVariable(FakeFfmpeg.ModeVariable, null);
            // 1.13.1 user report: an embed that exits 0 but writes no subtitle track must be reported, not claimed as success
            service.SubtitleTrackCounter = (_, _, _) => Task.FromResult<int?>(0);
            var silent = await Run("silent", Sel(SubtitleMode.Embed, "mp4", new SubtitleChoice("en")));
            Ensure(new FileInfo(silent.Path).Length == 4096 && silent.Extras.Warning == "youtube.subtitles_embed_failed" && silent.Extras.Separate.Count == 1, "an embed with no resulting text track is reported and the captions kept separately");
            Ensure(!Directory.GetFiles(Path.GetDirectoryName(silent.Path)!, "media-with-subtitles.*").Any(), "the trackless embed output is removed");
            service.SubtitleTrackCounter = (_, _, _) => Task.FromResult<int?>(1);
            var counted = await Run("counted", Sel(SubtitleMode.Embed, "mp4", new SubtitleChoice("en")));
            Ensure(counted.Extras.Warning is null && counted.Extras.Separate.Count == 0 && File.ReadAllText(counted.Path).StartsWith("FAKE-MUX"), "a verified text track keeps the success");
            service.SubtitleTrackCounter = (_, _, _) => Task.FromResult<int?>(null);
            var unchecked1 = await Run("unchecked", Sel(SubtitleMode.Embed, "mp4", new SubtitleChoice("en")));
            Ensure(unchecked1.Extras.Warning is null, "no ffprobe answer never fails a good embed");
            Ensure(SubtitleRules.CountProbedStreams("2\r\n3\r\n") == 2 && SubtitleRules.CountProbedStreams("") == 0 && SubtitleRules.CountProbedStreams("x\n") == 0 && SubtitleRules.ProbeArguments("f.mp4").Contains("-select_streams"), "the probe call and its parsing");
            service.SubtitleTrackCounter = YoutubeService.CountSubtitleTracksAsync;
            var missingTool = await Run("notool", new("18", null, false, null, "mp4", false, new SubtitleRequest(SubtitleMode.Embed, new[] { new SubtitleChoice("en") })), new ToolManager(paths.State));
            Ensure(File.Exists(missingTool.Path) && missingTool.Extras.Warning == "youtube.subtitles_need_tool", "without the video tool the subtitle step is skipped with a note");
            // audio only skips subtitles
            File.Delete(log);
            var audio = await Run("audio", new("140", null, false, null, null, false, new SubtitleRequest(SubtitleMode.Embed, new[] { new SubtitleChoice("en") })));
            Ensure(Path.GetFileName(audio.Path) == "media.m4a" && !File.ReadAllLines(log).Any(l => l.Contains("--write-subs")) && !audio.Phases.Contains("subtitles"), "audio only never fetches subtitles");

            // through the engine: separate files land next to the video; a failure leaves a note on a completed job
            using var manager = new DownloadManager(paths.Download, paths.State, new DownloadOptions { Youtube = service, Tools = tools, MaxConcurrentDownloads = 1 });
            var job = await manager.AddAsync(new(YoutubeTests.Url, "clip.mp4", TargetDirectory: paths.Download, Youtube: Sel(SubtitleMode.Separate, "mp4", new SubtitleChoice("en"), new SubtitleChoice("fr", true))));
            await WaitUntil(() => JobOf(manager, job.Id).Status is "Completed" or "Failed", "YouTube subtitle job", 30);
            var done = JobOf(manager, job.Id);
            Ensure(done.Status == "Completed" && done.FileName == "clip.mp4" && File.Exists(Path.Combine(paths.Download, "clip.en.srt")) && File.Exists(Path.Combine(paths.Download, "clip.fr.srt")) && done.AdditionalFiles.Count == 2 && done.StreamNote is null, "engine: clip.mp4 + clip.en.srt + clip.fr.srt");
            Ensure(File.ReadAllText(Path.Combine(paths.State, "queue.json")).Contains("Subtitles"), "the subtitle wish is stored with the job");
            Environment.SetEnvironmentVariable("NDM_YT_SUBMODE", "fail");
            var bad = await manager.AddAsync(new(YoutubeTests.Url, "bad.mp4", TargetDirectory: paths.Download, Youtube: Sel(SubtitleMode.Separate, "mp4", new SubtitleChoice("en"))));
            await WaitUntil(() => JobOf(manager, bad.Id).Status is "Completed" or "Failed", "YouTube subtitle failure job", 30);
            var badDone = JobOf(manager, bad.Id);
            Ensure(badDone.Status == "Completed" && badDone.StreamNote == "youtube.subtitles_failed" && File.Exists(Path.Combine(paths.Download, "bad.mp4")), "engine: the video completes with a warning note");
        }
        finally
        {
            foreach (var v in new[] { "NDM_YT_TEST_LOG", "NDM_YT_SUBS", "NDM_YT_TEST_MODE", "NDM_YT_SUBMODE", FakeFfmpeg.ModeVariable }) Environment.SetEnvironmentVariable(v, null);
            FakeTool.ResetEnvironment();
        }
    }

    // ---------------------------------------------------------------- item 6: HLS and DASH fixtures
    private const string Vtt1 = "WEBVTT\n\n00:00:00.500 --> 00:00:01.500\nHello <b>there</b>\n";
    private const string Vtt2 = "WEBVTT\n\n00:00:02.000 --> 00:00:03.000\nSecond cue\n";
    private static StreamServer HlsWithSubtitles()
    {
        var s = new StreamServer();
        var pieces = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n");
        for (int i = 0; i < 2; i++) { var data = Blob(500 + i, 3000); data[0] = 0x47; s.Add($"/sub/v/s{i}.ts", data, "video/mp2t"); pieces.Append("#EXTINF:2.0,\ns").Append(i).Append(".ts\n"); }
        pieces.Append("#EXT-X-ENDLIST\n"); s.Add("/sub/v/index.m3u8", pieces.ToString());
        s.Add("/sub/master.m3u8", "#EXTM3U\n#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"English\",LANGUAGE=\"en\",DEFAULT=YES,AUTOSELECT=YES,URI=\"subs/en.m3u8\"\n#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"Francais\",LANGUAGE=\"fr\",URI=\"subs/fr.m3u8\"\n#EXT-X-MEDIA:TYPE=CLOSED-CAPTIONS,GROUP-ID=\"cc\",NAME=\"CC\",INSTREAM-ID=\"CC1\"\n#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360,CODECS=\"avc1.4d401e,mp4a.40.2\",SUBTITLES=\"subs\"\nv/index.m3u8\n");
        foreach (var (lang, a, b) in new[] { ("en", Vtt1, Vtt2), ("fr", Vtt1.Replace("Hello", "Bonjour"), Vtt2) })
        {
            s.Add($"/sub/subs/{lang}.m3u8", $"#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n#EXTINF:2.0,\n{lang}0.vtt\n#EXTINF:2.0,\n{lang}1.vtt\n#EXT-X-ENDLIST\n");
            s.Add($"/sub/subs/{lang}0.vtt", a, "text/vtt"); s.Add($"/sub/subs/{lang}1.vtt", b, "text/vtt");
        }
        return s;
    }

    public static async Task StreamSubtitleListing(P paths)
    {
        using var server = HlsWithSubtitles(); using var manager = NewManager(paths);
        var manifest = await manager.InspectStreamAsync(server.Url("/sub/master.m3u8"));
        Ensure(manifest.SubtitleTracks.Count == 2 && manifest.SubtitleTracks[0].Id == "s0" && manifest.SubtitleTracks[0].Language == "en" && manifest.SubtitleTracks[0].Name == "English" && manifest.SubtitleTracks[0].IsDefault && manifest.SubtitleTracks[1].Language == "fr" && manifest.SubtitleTracks[0].Uri!.AbsoluteUri == server.Url("/sub/subs/en.m3u8"), "HLS subtitle tracks listed, closed captions are not");
        var plain = HlsParser.ToManifest(HlsParser.Parse("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000\nv.m3u8\n", new Uri("https://e.test/m.m3u8")).Require(), "https://e.test/m.m3u8");
        Ensure(plain.SubtitleTracks.Count == 0, "no tracks when the manifest has none");
        const string mpd = "<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\" mediaPresentationDuration=\"PT10S\"><Period>" +
            "<AdaptationSet contentType=\"video\" mimeType=\"video/mp4\"><Representation id=\"v1\" bandwidth=\"800000\" width=\"640\" height=\"360\" codecs=\"avc1.4d401e\"><BaseURL>video.mp4</BaseURL><SegmentBase indexRange=\"0-100\"/></Representation></AdaptationSet>" +
            "<AdaptationSet contentType=\"audio\" mimeType=\"audio/mp4\" lang=\"en\"><Representation id=\"a1\" bandwidth=\"128000\" codecs=\"mp4a.40.2\"><BaseURL>audio.mp4</BaseURL><SegmentBase indexRange=\"0-100\"/></Representation></AdaptationSet>" +
            "<AdaptationSet contentType=\"text\" mimeType=\"text/vtt\" lang=\"en\" label=\"English\"><Representation id=\"t1\" bandwidth=\"256\"><BaseURL>subs/en.vtt</BaseURL></Representation></AdaptationSet>" +
            "<AdaptationSet contentType=\"text\" mimeType=\"application/mp4\" lang=\"fr\"><Representation id=\"t2\" bandwidth=\"256\" codecs=\"stpp\"><SegmentTemplate media=\"t$Number$.m4s\" duration=\"2\" startNumber=\"1\"/></Representation></AdaptationSet>" +
            "</Period></MPD>";
        var dash = DashParser.Parse(mpd, new Uri("https://e.test/dir/m.mpd")).Require();
        Ensure(dash.Manifest.SubtitleTracks.Count == 1 && dash.Manifest.SubtitleTracks[0].Language == "en" && dash.Manifest.SubtitleTracks[0].Name == "English" && dash.Manifest.SubtitleTracks[0].Uri!.AbsoluteUri == "https://e.test/dir/subs/en.vtt", "DASH plain text track listed; segmented text is not");
        Ensure(dash.Manifest.Variants.Count == 1 && dash.Manifest.AudioTracks.Count == 1, "video and sound lists are unchanged by text sets");
        Ensure(dash.BuildPlan(null, null).Subtitles.Count == 1, "the plan carries the tracks");
    }

    private static async Task<DownloadJob> RunStream(DownloadManager manager, StreamServer server, SubtitleRequest? subs, string name, bool mp4 = false)
    {
        var job = await manager.AddStreamAsync(new DownloadRequest(server.Url("/sub/master.m3u8"), name, StreamKind: StreamKind.Hls, PreferMp4: mp4, Subtitles: subs));
        return await Succeed(manager, job.Id);
    }

    public static async Task StreamSubtitleJobs(P paths)
    {
        var ffLog = FakeTool.NewLog(paths.State);
        try
        {
            Environment.SetEnvironmentVariable(FakeFfmpeg.DurationVariable, "4");
            var tools = await FakeTool.Install(paths.State);
            using var server = HlsWithSubtitles();
            // separate files, no tool needed
            using (var manager = NewManager(paths))
            {
                var done = await RunStream(manager, server, new SubtitleRequest(SubtitleMode.Separate, new[] { new SubtitleChoice("s0"), new SubtitleChoice("s1") }), "show");
                var en = Path.Combine(paths.Download, "show.en.srt"); var fr = Path.Combine(paths.Download, "show.fr.srt");
                Ensure(File.Exists(done.FilePath) && File.Exists(en) && File.Exists(fr) && done.AdditionalFiles.Count == 2 && done.StreamNote is null, "stream: video + two separate .srt files");
                Ensure(File.ReadAllText(en) == "1\n00:00:00,500 --> 00:00:01,500\nHello there\n\n2\n00:00:02,000 --> 00:00:03,000\nSecond cue\n\n" && File.ReadAllText(fr).Contains("Bonjour"), "stream: both pieces joined and converted");
                Ensure(!Directory.GetFiles(paths.Download, "*", SearchOption.AllDirectories).Any(f => f.Contains("subtitle-")), "stream: no work files left");
                // a track that fails is a note, not a failed video
                server.Fail = (path, _) => path.EndsWith("fr1.vtt") ? 404 : null;
                var partial = await RunStream(manager, server, new SubtitleRequest(SubtitleMode.Separate, new[] { new SubtitleChoice("s0"), new SubtitleChoice("s1") }), "partial");
                Ensure(partial.StreamNote == StreamCodes.SubtitlesFailed && File.Exists(Path.Combine(paths.Download, "partial.en.srt")) && !File.Exists(Path.Combine(paths.Download, "partial.fr.srt")), "stream: one failing track gives a warning and keeps the others");
                server.Fail = null;
                // embedding needs MP4 or MKV; a transport stream falls back to separate files
                var ts = await RunStream(manager, server, new SubtitleRequest(SubtitleMode.Embed, new[] { new SubtitleChoice("s0") }), "tsfile");
                Ensure(ts.StreamNote == StreamCodes.SubtitlesContainer && File.Exists(Path.Combine(paths.Download, "tsfile.en.srt")), "stream: TS cannot hold subtitles, they are saved separately with a note");
                var none = await RunStream(manager, server, null, "nosubs");
                Ensure(none.AdditionalFiles.Count == 0 && none.StreamNote is null, "stream: nothing ticked, nothing fetched");
            }
            // embedded in an MP4 through the tool
            var before = server.Count("/sub/subs/en0.vtt");
            using (var manager = NewManager(paths, tools: tools))
            {
                var done = await RunStream(manager, server, new SubtitleRequest(SubtitleMode.Embed, new[] { new SubtitleChoice("s0"), new SubtitleChoice("s1") }), "movie", mp4: true);
                var calls = FakeFfmpeg.ReadLog(ffLog);
                var embed = calls.Last(c => c.Contains("-c:s"));
                Ensure(done.FileName == "movie.mp4" && done.AdditionalFiles.Count == 0 && done.StreamNote is null && File.ReadAllText(done.FilePath).StartsWith("FAKE-MUX"), "stream: embedded into one MP4");
                Ensure(embed.Contains("mov_text") && embed.Count(a => a == "-i") == 3 && embed.Contains("language=eng") && embed.Contains("language=fra"), "stream: MP4 embed arguments: " + string.Join(" ", embed));
                Ensure(server.Count("/sub/subs/en0.vtt") > before, "stream: tracks were fetched");
            }
        }
        finally { FakeTool.ResetEnvironment(); }
    }

    // ---------------------------------------------------------------- the playlist wish
    public static Task PlaylistRequestNeedsNoApp()
    {
        // the subtitle vocabulary of a playlist is built in the app (PlaylistOptions); here only the core limits it relies on
        var choices = new[] { "en", "fr" }.SelectMany(c => new[] { new SubtitleChoice(c), new SubtitleChoice(c, true) }).ToArray();
        Ensure(new SubtitleRequest(SubtitleMode.Embed, choices).IsValid, "language list becomes creator + automatic choices");
        return Task.CompletedTask;
    }
}
