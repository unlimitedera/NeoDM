using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;
using NDM.Tests;

/// <summary>1.14.0: subtitle files a player loads on its own. The stream window section (two themes, right to left, the choices it gives the engine), the pipe path from a capture
/// request that carries them, and, when NEODM_CAPTURE and NEODM_HLS_DIR are set, the real path: the message the real extension built in Chrome, the real stream dialog and the
/// real engine with the bundled ffmpeg, checked with ffprobe.</summary>
internal static partial class ManageTests
{
    private static StreamManifest SubManifest() => new()
    {
        Kind = StreamKind.Hls, Url = "https://cdn.example/master.m3u8", Duration = TimeSpan.FromSeconds(60),
        Variants = new[] { new VideoVariant("v0", 800_000, 640, 360, 30, "avc1.4d401e,mp4a.40.2", null) { HasOwnAudio = true } },
        SubtitleTracks = new[] { new SubtitleTrack("s0", "fr", "Francais", new Uri("https://cdn.example/fr.m3u8")) }
    };
    private static List<CapturedSubtitle> SubCaptured() => new()
    {
        CapturedSubtitle.TryCreate("https://cdn.example/v/subs/en.vtt", "en", "Subtitles", "vtt")!,
        CapturedSubtitle.TryCreate("https://cdn.example/v/subs/ar.vtt", "ar", "Subtitles", "vtt")!,
        CapturedSubtitle.TryCreate("https://cdn.example/fr.m3u8", "fr", "Subtitles", "vtt")!,           // the manifest lists this address already
        CapturedSubtitle.TryCreate("https://cdn.example/v/subs/x.ass", "de", "Subtitles", "ass")!,       // not a format NeoDM converts
        CapturedSubtitle.TryCreate("https://cdn.example/v/subs/9f3a.srt", "", "Subtitles", "srt")!,
    };
    private static StreamOptionsDialog SubDialog(string folder, IReadOnlyList<CapturedSubtitle>? captured, string windowsTag, double width = 720, double height = 960)
    {
        var keep = Thread.CurrentThread.CurrentUICulture; Thread.CurrentThread.CurrentUICulture = new System.Globalization.CultureInfo(windowsTag); // the dialog reads the Windows language when it opens
        var d = new StreamOptionsDialog(new DownloadRequest("https://cdn.example/master.m3u8", "movie"), folder, (r, t) => Task.FromResult(SubManifest()), new FakeVideoTool { Installed = true }, null, captured)
        { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10, Width = width, Height = height };
        Thread.CurrentThread.CurrentUICulture = keep;
        d.Show(); Pump(); PumpUntil(() => Field<StackPanel>(d, "ReadyPanel").Visibility == Visibility.Visible, "stream dialog ready", 15); Pump(80);
        return d;
    }
    private static double PanelLuminance(Window w, FrameworkElement el, out int contrast)
    {
        w.UpdateLayout(); var bmp = new RenderTargetBitmap((int)Math.Ceiling(w.ActualWidth), (int)Math.Ceiling(w.ActualHeight), 96, 96, PixelFormats.Pbgra32); bmp.Render(w);
        var origin = el.TranslatePoint(new Point(0, 0), w); int x = (int)origin.X + 4, y = (int)origin.Y + 4, cw = Math.Max(1, Math.Min((int)el.ActualWidth - 8, bmp.PixelWidth - x)), ch = Math.Max(1, Math.Min((int)el.ActualHeight - 8, bmp.PixelHeight - y));
        var px = new byte[cw * ch * 4]; bmp.CopyPixels(new Int32Rect(x, y, cw, ch), px, cw * 4, 0);
        var lum = new List<double>(); for (int i = 0; i < px.Length; i += 4) lum.Add(0.2126 * px[i + 2] + 0.7152 * px[i + 1] + 0.0722 * px[i]);
        double mean = lum.Average(); contrast = lum.Count(l => Math.Abs(l - mean) > 60); return mean;
    }
    private static void SubShot(Window w, string name)
    {
        if (Environment.GetEnvironmentVariable("NEODM_SHOTS") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir); w.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(w.ActualWidth), (int)Math.Ceiling(w.ActualHeight), 96, 96, PixelFormats.Pbgra32); bmp.Render(w);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp)); using var fs = File.Create(Path.Combine(dir, name)); enc.Save(fs);
    }

    private static void SubtitleCaptureTests(string root)
    {
        Loc.SetLanguage("en");
        string folder = Path.Combine(root, "sub-capture"); Directory.CreateDirectory(folder);

        // 1. the section: manifest track first, then the captured files; de-duplicated by address; unsupported formats not offered; none ticked; Windows language highlighted
        var d = SubDialog(folder, SubCaptured(), "ar-SA");
        var items = d.SubtitleItems;
        Check(items.Count == 4 && items[0].Track.Id == "s0" && items.Skip(1).Select(i => i.Track.Id).SequenceEqual(new[] { "x0", "x1", "x2" }), "stream dialog: the manifest's track plus three captured files; the duplicate address and the .ass file are not listed");
        Check(items.All(i => !i.IsTicked) && d.SelectedSubtitles is null, "stream dialog: nothing ticked by default");
        Check(items[1].Text.StartsWith("English") && items[1].Text.Contains("separate file, WebVTT") && items[3].Text.Contains("SubRip") && items[3].Text.StartsWith("Subtitles") && !items[0].Text.Contains("separate file"), "stream dialog: rows say the language and 'separate file, WebVTT' (or SubRip); a file with no known language is just 'Subtitles': " + string.Join(" / ", items.Select(i => i.Text)));
        Check(items.Single(i => i.Yours).Track.Id == "x1", "stream dialog: the Windows language (Arabic) is highlighted, never ticked");
        var note = Field<TextBlock>(d, "SubtitleCapturedNote"); var hint = Field<TextBlock>(d, "SubtitleHint");
        Check(note.IsVisible && !hint.IsVisible && note.Text == "Only languages the player loaded are listed. To add another, pick it in the player, then download again.", "stream dialog: the line about languages the player loaded is shown instead of the old hint");
        d.TickSubtitle("x0", true);
        var chosen = d.SelectedSubtitles!;
        Check(chosen.Mode == SubtitleMode.Embed && chosen.IsValid && chosen.Choices.Single() is { Code: "x0", Url: "https://cdn.example/v/subs/en.vtt", Language: "en", Label: "English" }, "stream dialog: a ticked captured file becomes an embed choice carrying its address and language");
        d.TickSubtitle("s0", true); d.TickSubtitle("x2", true);
        Check(d.SelectedSubtitles!.Choices.Count == 3 && d.SelectedSubtitles.Choices.Count(c => c.IsSeparateFile) == 2 && d.SelectedSubtitles.Choices.Single(c => !c.IsSeparateFile).Code == "s0", "stream dialog: manifest tracks and captured files can be mixed");
        ((RadioButton)d.FindName("SubSeparate")!).IsChecked = true; Pump(40);
        Check(d.SelectedSubtitles!.Mode == SubtitleMode.Separate, "stream dialog: Save as separate .srt files");
        foreach (var theme in new[] { "Light", "DeepNavy" })
        {
            ThemeManager.Apply(Application.Current, theme, null); Pump(80);
            var panel = Field<Border>(d, "SubtitlesPanel"); var mean = PanelLuminance(d, panel, out var contrast);
            SubShot(d, $"v1140-stream-subtitles-{theme}.png");
            Check(theme == "Light" ? mean > 150 && contrast > 40 : mean < 110 && contrast > 40, $"stream dialog, {theme}: the Subtitles section follows the theme (mean {mean:0}) and its text is readable ({contrast} contrasting pixels)");
        }
        ThemeManager.Apply(Application.Current, "Light", null);
        d.Close();

        // 2. no captured files: the 1.13 behaviour, hint instead of note
        var plain = SubDialog(folder, null, "en-US");
        Check(plain.SubtitleItems.Count == 1 && Field<TextBlock>(plain, "SubtitleHint").IsVisible && !Field<TextBlock>(plain, "SubtitleCapturedNote").IsVisible, "stream dialog: without captured files only the manifest's track and the old hint");
        plain.Close();

        // 3. right to left, small size
        Loc.SetLanguage("ar");
        var rtl = SubDialog(folder, SubCaptured(), "ar-SA", 560, 760);
        Check(rtl.FlowDirection == FlowDirection.RightToLeft && Field<TextBlock>(rtl, "SubtitleCapturedNote").Text == Loc.T("stream.subs.captured_note") && Loc.T("stream.subs.captured_note") != "Only languages the player loaded are listed. To add another, pick it in the player, then download again." && rtl.SubtitleItems[1].Text.Contains(Loc.T("stream.subs.separate_detail", "WebVTT")), "stream dialog, Arabic: right to left, note and row detail translated");
        var sv = Field<Border>(rtl, "SubtitlesPanel"); sv.UpdateLayout();
        Check(sv.ActualWidth > 100 && sv.ActualWidth <= rtl.ActualWidth, "stream dialog, small size: the Subtitles section fits the window");
        SubShot(rtl, "v1140-stream-subtitles-rtl.png");
        rtl.Close(); Loc.SetLanguage("en");

        // 4. every language has both new strings, translated
        var english = new[] { "stream.subs.separate_detail", "stream.subs.captured_note" }.ToDictionary(k => k, Loc.T);
        foreach (var info in LanguageCatalog.All)
        {
            Loc.SetLanguage(info.Tag);
            foreach (var k in english.Keys) { var v = Loc.T(k); Check(v.Length > 0 && v != k && (info.Tag == "en" || v != english[k]), $"locale {info.Tag}: {k} exists and is translated"); }
        }
        Loc.SetLanguage("en");

        // 5. the pipe path: a capture request with subtitle files opens the dialog with them; bad entries are dropped; an old extension's request has none
        using var server = new StreamServer();
        var sb = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n");
        for (int i = 0; i < 2; i++) { var seg = StreamKit.Blob(800 + i, 3000); seg[0] = 0x47; server.Add($"/c/s{i}.ts", seg, "video/mp2t"); sb.Append("#EXTINF:2.0,\ns").Append(i).Append(".ts\n"); }
        sb.Append("#EXT-X-ENDLIST\n"); server.Add("/c/index.m3u8", sb.ToString());
        using var m = Manager(root, "sub-pipe");
        var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "sub-pipe", "files"), ShowCompletionDialog = false, ShowProgressOnCompletion = false };
        var windows = new TrayWindowList(); var dialogs = new List<StreamOptionsDialog>();
        string pipe = "NDM.SubUi." + Guid.NewGuid().ToString("N"); var ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        using var bridge = new NativeBridge(r => ui.BeginInvoke(new Action(() => _ = StreamCapture.HandleAsync(r, m, settings, windows, new FakeVideoTool(), dialogs.Add))), pipe);
        bridge.Start();
        Task<JsonElement> Ask(object request) => Task.Run(async () =>
        {
            var c = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var t = new CancellationTokenSource(TimeSpan.FromSeconds(30)); await c.ConnectAsync(t.Token);
            await c.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request) + "\n")); await c.FlushAsync();
            using var reader = new StreamReader(c); return JsonDocument.Parse((await reader.ReadLineAsync(t.Token))!).RootElement.Clone();
        });
        var many = Enumerable.Range(0, 30).Select(i => new { url = server.Url($"/c/subs/{i}.vtt"), language = "en", label = "English", format = "vtt" }).Cast<object>().ToList();
        many.Insert(0, new { url = "javascript:alert(1)", language = "en", label = "x", format = "vtt" }); many.Insert(1, new { url = server.Url("/c/subs/bad.vtt"), language = "en", label = "x", format = "exe" });
        var reply = Ask(new { url = server.Url("/c/index.m3u8"), stream = "hls", fileName = "Film", subtitles = many });
        PumpUntil(() => dialogs.Count == 1, "dialog from the pipe request with subtitles"); Sync(dialogs[0].ReadTask); Pump(80);
        Check(dialogs[0].SubtitleItems.Count == 20 && dialogs[0].SubtitleItems.All(i => i.File is not null && i.File.Url.StartsWith("http://127.0.0.1")), "pipe path: bad entries dropped and at most 20 files reach the stream dialog (the stream itself lists none)");
        Call(dialogs[0], "Cancel_Click"); PumpUntil(() => reply.IsCompleted, "cancel reply");
        reply = Ask(new { url = server.Url("/c/index.m3u8"), stream = "hls", fileName = "Film2" });
        PumpUntil(() => dialogs.Count == 2, "dialog for an old extension"); Sync(dialogs[1].ReadTask);
        Check(dialogs[1].SubtitleItems.Count == 0 && !Field<Border>(dialogs[1], "SubtitlesPanel").IsVisible, "pipe path: a request from an older extension has no subtitle files and no section");
        Call(dialogs[1], "Cancel_Click"); PumpUntil(() => reply.IsCompleted, "cancel reply 2");

        RealPathFromChrome(root);
    }

    /// <summary>The whole chain with what the real extension captured in Chrome (set by tests/extension-v1140-subs-smoke.cjs): its message goes through the same door the native host
    /// uses, the real stream dialog opens, the user ticks the subtitle, the real engine downloads the fixture stream and the finished file is checked with ffprobe.</summary>
    private static void RealPathFromChrome(string root)
    {
        string? capturePath = Environment.GetEnvironmentVariable("NEODM_CAPTURE"), hlsDir = Environment.GetEnvironmentVariable("NEODM_HLS_DIR");
        if (string.IsNullOrEmpty(capturePath) || string.IsNullOrEmpty(hlsDir) || !File.Exists(capturePath)) { Console.WriteLine("SKIP: real path from Chrome (set NEODM_CAPTURE and NEODM_HLS_DIR; see tests/extension-v1140-subs-smoke.cjs)"); return; }
        var dir = new DirectoryInfo(AppContext.BaseDirectory); while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) dir = dir.Parent;
        string tools = Environment.GetEnvironmentVariable("NEODM_TOOLS_DIR") is { Length: > 0 } t ? t : Path.Combine(dir!.FullName, "dist", "app", "tools"), ffmpeg = Path.Combine(tools, "ffmpeg", "ffmpeg.exe"), ffprobe = Path.Combine(tools, "ffmpeg", "ffprobe.exe");
        Check(File.Exists(ffmpeg) && File.Exists(ffprobe), "real path: the bundled ffmpeg and ffprobe are there");

        // the fixture: the same files the browser got, served by a local server on another port (the capture's cdn.localhost host is mapped to it)
        using var server = new StreamServer();
        foreach (var f in Directory.GetFiles(Path.Combine(hlsDir, "hls") is var h && Directory.Exists(h) ? h : hlsDir))
            server.Add("/hls/" + Path.GetFileName(f), f.EndsWith(".m3u8") ? Encoding.UTF8.GetBytes(File.ReadAllText(f).Replace("#EXTINF:2.000000,", "#EXTINF:2.045000,")) : File.ReadAllBytes(f), f.EndsWith(".m3u8") ? "application/vnd.apple.mpegurl" : "video/mp2t");
        const string fixtureVtt = "WEBVTT\nX-TIMESTAMP-MAP=MPEGTS:900000,LOCAL:00:00:00.000\n\nNOTE made for the NeoDM test\n\n1\n00:00:00.500 --> 00:00:01.500 align:start position:10%\nHello <b>there</b>, fixture\n\n2\n00:00:02.000 --> 00:00:03.000\nSecond cue\nwith two lines\n";
        server.Add("/subs/en.vtt", fixtureVtt, "text/vtt");
        var raw = File.ReadAllText(capturePath);
        var rewritten = System.Text.RegularExpressions.Regex.Replace(raw, @"http://(cdn|video)\.localhost:\d+", server.BaseUrl);
        var message = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(rewritten)!;
        foreach (var drop in new[] { "method", "keepAlive" }) message.Remove(drop); // the host does not forward these
        Check(message.ContainsKey("subtitles") && message["subtitles"].GetArrayLength() == 1 && message["stream"].GetString() == "hls", "real path: the message captured by the real extension carries stream=hls and one subtitle file");

        string state = Path.Combine(root, "real-path", "state"), files = Path.Combine(root, "real-path", "files"); Directory.CreateDirectory(state); Directory.CreateDirectory(files);
        var options = new DownloadOptions { RetryCount = 1, RequestTimeout = TimeSpan.FromSeconds(10), MinimumSegmentSize = 65536, Tools = new ToolManager(state, bundleDirectory: tools), ToolTimeout = TimeSpan.FromSeconds(90) };
        using var manager = new DownloadManager(files, state, options);
        var settings = new AppSettings { DownloadDirectory = files, ShowCompletionDialog = false, ShowProgressOnCompletion = false };
        var windows = new TrayWindowList(); var dialogs = new List<StreamOptionsDialog>(); var captured = new List<string>();
        string pipe = "NDM.RealPath." + Guid.NewGuid().ToString("N"); var ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        using var bridge = new NativeBridge(r => ui.BeginInvoke(new Action(() => _ = StreamCapture.HandleAsync(r, manager, settings, windows, new ToolManagerAdapter(manager.Tools), dialogs.Add, null, captured.Add))), pipe);
        bridge.Start();
        var reply = Task.Run(async () =>
        {
            var c = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var tk = new CancellationTokenSource(TimeSpan.FromSeconds(120)); await c.ConnectAsync(tk.Token);
            await c.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message) + "\n")); await c.FlushAsync();
            using var reader = new StreamReader(c); return JsonDocument.Parse((await reader.ReadLineAsync(tk.Token))!).RootElement.Clone();
        });
        PumpUntil(() => dialogs.Count == 1, "real path: the stream dialog opens", 30);
        var d = dialogs[0]; Sync(d.ReadTask); Pump(100);
        Check(d.SubtitleItems.Count == 1 && d.SubtitleItems[0].File is { Language: "en", Format: "vtt" } && d.SubtitleItems[0].Text.StartsWith("English") && !d.SubtitleItems[0].IsTicked, "real path: the dialog lists English, a separate file, not ticked: " + d.SubtitleItems[0].Text);
        SubShot(d, "v1140-real-path-dialog.png");
        d.TickSubtitle("x0", true);
        Call(d, "Start_Click");
        PumpUntil(() => reply.IsCompleted, "real path: the browser gets an answer", 30);
        string id = reply.Result.GetProperty("id").GetString()!;
        PumpUntil(() => manager.Jobs.Single(j => j.Id == id).Status is "Completed" or "Failed", "real path: the download finishes", 120);
        var job = manager.Jobs.Single(j => j.Id == id);
        Check(job.Status == "Completed" && job.FileName.EndsWith(".mp4") && job.AdditionalFiles.Count == 0 && job.StreamNote is null, $"real path: completed as one MP4 with no note ({job.FileName}, note {job.StreamNote}, error {job.Error})");
        string Run(string exe, params string[] args)
        {
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 }; foreach (var a in args) info.ArgumentList.Add(a);
            using var p = Process.Start(info)!; var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync(); p.WaitForExit(60000); return o.Result + e.Result;
        }
        var probe = Run(ffprobe, "-v", "error", "-show_entries", "stream=index,codec_type,codec_name:stream_tags=language", "-of", "default=nw=1", job.FilePath);
        Console.WriteLine("ffprobe of " + job.FilePath + ":\n" + probe);
        var cues = Run(ffmpeg, "-v", "error", "-i", job.FilePath, "-map", "0:s:0", "-f", "srt", "-");
        Console.WriteLine("subtitle text read back from the finished file:\n" + cues);
        Check(probe.Contains("codec_type=subtitle") && probe.Contains("codec_name=mov_text") && probe.Contains("TAG:language=eng") && probe.Contains("codec_name=h264"), "real path: ffprobe shows the picture and a non-empty mov_text subtitle stream, language eng");
        Check(cues.Contains("Hello there, fixture") && cues.Contains("Second cue") && cues.Contains("00:00:00,500 --> 00:00:01,500"), "real path: the cue text and timing of the fixture file are inside the finished MP4");
        var fetched = server.For("/subs/en.vtt").ToList();
        Check(fetched.Count >= 1 && fetched.All(s => s.UserAgent == message["userAgent"].GetString() && s.Referer == message["referer"].GetString()), "real path: the subtitle file was fetched with the user agent and referer the real extension sent");
    }
}
