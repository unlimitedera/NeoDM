using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

/// <summary>1.13.0 worker 2: the YouTube format picker window, its subtitles row, the playlist vocabulary and the stream dialog subtitle row. Fakes only: no network, no helper process.</summary>
internal static partial class V1130Tests
{
    private const long Mb = 1024 * 1024;
    private static YoutubeFormat Vf(string id, string ext, int h, string codec, long size, bool audio = false) => new(id, ext, h, 0, true, audio, size, false, codec, audio ? "mp4a.40.2" : null, 30);
    private static YoutubeFormat Af(string id, string ext, int kbps, string codec, long size) => new(id, ext, 0, kbps, false, true, size, false, null, codec);
    private static YoutubeMetadata PickerMetadata() => new("A sample video with a fairly long title", new[]
    {
        Vf("313", "webm", 2160, "vp09.00.51.08", 300 * Mb), Vf("248", "webm", 1080, "vp09.00.40.08", 80 * Mb), Vf("137", "mp4", 1080, "avc1.640028", 100 * Mb),
        Vf("136", "mp4", 720, "avc1.4d401f", 50 * Mb), Vf("18", "mp4", 360, "avc1.42001E", 20 * Mb, true),
        Af("140", "m4a", 128, "mp4a.40.2", 12 * Mb), Af("251", "webm", 160, "opus", 13 * Mb)
    });
    private static SubtitleListing SampleListing() => new(
        new[] { new SubtitleLanguage("en", "English", false), new SubtitleLanguage("fr", "French", false) },
        new[] { new SubtitleLanguage("es", "Spanish", true), new SubtitleLanguage("de", "German", true) });
    private static T Part<T>(Window w, string name) where T : FrameworkElement => (T)w.FindName(name) ?? throw new Exception("missing control " + name);
    private static void WaitTask(Task t) { PumpUntil(() => t.IsCompleted, "task finished", 20000); t.GetAwaiter().GetResult(); Pump(40); }
    private static void RadioOn(RadioButton r) { r.IsChecked = true; Pump(40); }

    private static YoutubeOptionsDialog NewPickerDialog(string scratch, double width = 700, double height = 960)
    {
        var d = new YoutubeOptionsDialog(new DownloadRequest("https://www.youtube.com/watch?v=aqz-KE-bpKQ"), scratch, null!, null!, (u, t) => Task.FromResult(PickerMetadata()),
            new AppSettings { DownloadDirectory = scratch }, subtitleLister: (u, t) => Task.FromResult(SampleListing()))
        { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10, Width = width, Height = height, WindowsLanguage = "fr-FR" };
        d.GetType().GetProperty("Modeless", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(d, true);
        d.Show(); Pump(); return d;
    }

    // ------------------------------------------------------------------ item 5 + 6: the window
    private static void PickerWindow(string scratch)
    {
        foreach (var theme in new[] { "Light", "DeepNavy" })
        {
            ThemeManager.Apply(Application.Current, theme, null);
            foreach (var rtl in new[] { false, true })
            {
                var tag = $"{theme}/{(rtl ? "RTL" : "LTR")}";
                var d = NewPickerDialog(scratch); d.FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
                WaitTask(d.ReadAsync()); WaitTask(d.SubtitleTask); Pump(120);
                var quality = Part<ComboBox>(d, "Quality"); var audio = Part<ComboBox>(d, "Audio"); var container = Part<ComboBox>(d, "ContainerBox");
                Check(Part<RadioButton>(d, "TypeVideoAudio").IsChecked == true && Part<RadioButton>(d, "TypeVideoOnly").IsEnabled && Part<RadioButton>(d, "TypeAudioOnly").IsEnabled, $"{tag}: three types, video + audio first");
                Check(container.Items.Count == 3 && quality.Items.Count == 5 && audio.Items.Count == 2, $"{tag}: containers {container.Items.Count}, resolutions {quality.Items.Count}, audio {audio.Items.Count}");
                var items = quality.Items.Cast<YoutubeOptionsDialog.PickItem>().ToArray();
                Check(items[0].Title == Loc.T("youtube.pick.best") && items.Skip(1).Select(i => i.Title).SequenceEqual(new[] { "2160p", "1080p", "720p", "360p" }), $"{tag}: short resolution list: " + string.Join(",", items.Select(i => i.Title)));
                Check(items[2].Detail == "avc1 \u00B7 30 fps \u00B7 " + Loc.T("youtube.pick.about", "112 MB"), $"{tag}: second line has codec, frame rate and size: " + items[2].Detail);
                Check(d.SummaryText.Contains("2160p MP4") && d.SummaryText.Contains("128 kb/s") && d.SummaryText.Contains("312 MB"), $"{tag}: summary: " + d.SummaryText);
                Check(Part<TextBlock>(d, "RemuxNote").IsVisible && Part<TextBlock>(d, "RemuxNote").Text.Contains("MP4"), $"{tag}: the repackaging warning shows for best-in-MP4");
                quality.SelectedIndex = 2; Pump(60);
                Check(d.SummaryText == Loc.T("youtube.summary.video_audio", "1080p", "MP4", 128) + Loc.T("youtube.summary.size", "112 MB") && !Part<TextBlock>(d, "RemuxNote").IsVisible, $"{tag}: 1080p summary and no warning: " + d.SummaryText);
                Check(Part<Button>(d, "StartButton").IsEnabled && Part<Button>(d, "QueueButton").IsEnabled, $"{tag}: buttons enabled once info is loaded");
                // subtitles
                var panel = Part<Border>(d, "SubtitlesPanel");
                Check(panel.IsVisible && d.ManualSubtitles.Count == 2 && d.AutoSubtitles.Count == 2 && d.ManualSubtitles.All(i => !i.IsTicked) && d.AutoSubtitles.All(i => !i.IsTicked), $"{tag}: subtitles row shows creator languages first, none ticked");
                Check(d.ManualSubtitles.Single(i => i.Yours).Language.Code == "fr" && d.ManualSubtitles.Count(i => i.Yours) == 1, $"{tag}: the Windows language is highlighted, not ticked");
                Check(Box(Part<TextBlock>(d, "ManualHeader"), d).Top < Box(Part<TextBlock>(d, "AutoHeader"), d).Top, $"{tag}: creator list is above the automatic list");
                d.TickSubtitle("en", false, true);
                Check(d.SummaryText.TrimEnd((char)0x200E, (char)0x200F).EndsWith(Loc.T("youtube.summary.subs_embedded", 1)), $"{tag}: summary mentions embedded subtitles: " + d.SummaryText);
                RadioOn(Part<RadioButton>(d, "SubSeparate"));
                Check(d.SummaryText.TrimEnd((char)0x200E, (char)0x200F).EndsWith(Loc.T("youtube.summary.subs_separate", 1)), $"{tag}: summary mentions separate files");
                RadioOn(Part<RadioButton>(d, "SubEmbed"));
                var pick = container.Items.Cast<YoutubeOptionsDialog.PickItem>().First(i => (string)i.Tag == "webm"); container.SelectedItem = pick; Pump(60);
                Check(Part<TextBlock>(d, "SubtitleWarning").IsVisible && Part<TextBlock>(d, "SubtitleWarning").Text == Loc.T("youtube.subs.webm_warning"), $"{tag}: embedding into WebM warns that the file becomes MKV");
                // layout and mirroring
                d.UpdateLayout();
                var scroll = d.Content is Grid g ? g.Children.OfType<ScrollViewer>().First() : null;
                Check(Box(quality, d).Right <= d.ActualWidth + 1 && Box(quality, d).Left >= -1 && quality.ActualWidth > 250, $"{tag}: choices fit inside the window");
                Check(quality.FlowDirection == FlowDirection.LeftToRight, $"{tag}: technical lists stay left to right");
                Check(Part<RadioButton>(d, "TypeVideoAudio").IsTabStop && quality.IsTabStop && container.Focusable && Part<Expander>(d, "RawExpander").Focusable, $"{tag}: all steps are reachable with the keyboard");
                d.Height = 1500; Pump(150); d.UpdateLayout();
                SaveShot(d, $"v1130-youtube-picker-{theme}-{(rtl ? "rtl" : "ltr")}.png");
                // accept: the job request
                container.SelectedItem = container.Items.Cast<YoutubeOptionsDialog.PickItem>().First(i => (string)i.Tag == "mkv"); Pump(40);
                WaitTask(d.AcceptAsync(DownloadOptionsDialog.DialogAction.Queue));
                var req = d.Request!;
                Check(req.Youtube!.Container == "mkv" && req.Youtube.Subtitles!.Mode == SubtitleMode.Embed && req.Youtube.Subtitles.Choices.Single().Code == "en" && req.FileName!.EndsWith(".mkv"), $"{tag}: the request carries container and subtitles: {req.FileName}");
                Check(req.Youtube.VideoId == "137" && req.Youtube.AudioId == "251", $"{tag}: MKV picks the best streams: {req.Youtube.VideoId}+{req.Youtube.AudioId}");
            }
        }
        ThemeManager.Apply(Application.Current, "Light", null);
    }

    private static void PickerInteractions(string scratch)
    {
        var d = NewPickerDialog(scratch, 560, 640);
        WaitTask(d.ReadAsync()); WaitTask(d.SubtitleTask);
        var quality = Part<ComboBox>(d, "Quality");
        RadioOn(Part<RadioButton>(d, "TypeAudioOnly"));
        Check(!Part<StackPanel>(d, "ResolutionRow").IsVisible && !Part<StackPanel>(d, "ContainerRow").IsVisible && Part<CheckBox>(d, "Mp3").IsVisible && Part<StackPanel>(d, "AudioRow").IsVisible, "audio only hides container and resolution, shows MP3 conversion");
        Check(!Part<Border>(d, "SubtitlesPanel").IsVisible, "audio only has no subtitles row");
        Check(d.SummaryText.Contains("160") && d.SummaryText.Contains("WEBM"), "audio only summary: " + d.SummaryText);
        Part<CheckBox>(d, "Mp3").IsChecked = true; Pump(40);
        Check(d.SummaryText == Loc.T("youtube.summary.mp3") + Loc.T("youtube.summary.size", "13 MB"), "audio only MP3 summary: " + d.SummaryText);
        RadioOn(Part<RadioButton>(d, "TypeVideoOnly"));
        Check(!Part<StackPanel>(d, "AudioRow").IsVisible && d.SummaryText.Contains(Loc.T("youtube.summary.video_only", "2160p", "WEBM").Split(',')[0].Split(' ')[0]) , "video only hides the audio step: " + d.SummaryText);
        RadioOn(Part<RadioButton>(d, "TypeVideoAudio"));
        Part<Expander>(d, "RawExpander").IsExpanded = true; Pump(80);
        var raw = Part<ListBox>(d, "RawList");
        Check(raw.Items.Count == 7 && raw.Items.Cast<YoutubeOptionsDialog.RawItem>().First().Group == Loc.T("youtube.pick.type_video_audio"), "all formats list is grouped, combined first: " + raw.Items.Count);
        Part<TextBox>(d, "RawSearch").Text = "opus"; Pump(40);
        Check(raw.Items.Count == 1, "all formats list is searchable: " + raw.Items.Count);
        Part<TextBox>(d, "RawSearch").Text = ""; Pump(40);
        Part<ComboBox>(d, "RawSort").SelectedIndex = 2; Pump(40);
        Check(raw.Items.Cast<YoutubeOptionsDialog.RawItem>().Select(i => i.Format.Id).First() == "18", "all formats list sorts by format id inside a group");
        raw.SelectedItem = raw.Items.Cast<YoutubeOptionsDialog.RawItem>().First(i => i.Format.Id == "136"); Pump(60);
        Check(d.SummaryText.StartsWith("720p MP4 plus audio 128"), "choosing a raw format changes the summary: " + d.SummaryText);
        WaitTask(d.AcceptAsync(DownloadOptionsDialog.DialogAction.Queue));
        Check(d.Request!.Youtube!.VideoId == "136" && d.Request.Youtube.AudioId == "140" && d.Request.FileName!.EndsWith(".mp4"), "the raw format reaches the job");
        // small size: everything scrolls, nothing is clipped horizontally
        var small = NewPickerDialog(scratch, 520, 500); WaitTask(small.ReadAsync()); WaitTask(small.SubtitleTask); small.UpdateLayout();
        Check(Box(Part<ComboBox>(small, "Quality"), small).Right <= small.ActualWidth + 1 && FindFirst<ScrollViewer>(small)!.ScrollableHeight > 0, "small window: choices fit horizontally and the page scrolls");
        small.Close(); d.Close();
    }
    private static T? FindFirst<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            if (FindFirst<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    // ------------------------------------------------------------------ item 5 + 6: playlist dialog vocabulary
    private static void PlaylistVocabulary(string scratch)
    {
        var list = YoutubePlaylists.Parse(string.Join("\n", Enumerable.Range(0, 3).Select(i => $"{{\"id\":\"v{i:D10}\",\"title\":\"Video {i}\",\"duration\":{60 + i},\"channel\":\"C\",\"playlist_title\":\"List\",\"playlist_count\":3}}")));
        var d = new PlaylistOptionsDialog("https://www.youtube.com/playlist?list=PLav47HAVZMjnTFVZL-aImCQIC0uLZtNCz", scratch, null, new AppSettings { DownloadDirectory = scratch }, (u, t, l) => Task.FromResult(list), null)
        { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10, Width = 900, Height = 760 };
        d.Show(); Pump(); WaitTask(d.ReadAsync());
        var container = Part<ComboBox>(d, "ContainerBox"); var kind = Part<ComboBox>(d, "SubtitleKindBox");
        Check(((IEnumerable<object>)container.ItemsSource).Count() == 4 && ((IEnumerable<object>)kind.ItemsSource).Count() == 3, "playlist offers container and subtitle choices");
        Check(AllLabels(d).Any(l => l.Content?.ToString() == Loc.T("youtube.pick.resolution")) && AllLabels(d).Any(l => l.Content?.ToString() == Loc.T("youtube.pick.container")), "playlist uses the same words for resolution and container");
        Check(!Part<StackPanel>(d, "SubtitleModePanel").IsVisible && !Part<StackPanel>(d, "SubtitleCodesPanel").IsVisible, "no subtitle details while 'None' is chosen");
        container.SelectedValue = "webm"; kind.SelectedValue = "list"; Pump(60);
        Check(Part<StackPanel>(d, "SubtitleCodesPanel").IsVisible && Part<StackPanel>(d, "SubtitleModePanel").IsVisible, "language list shows the codes box and the two modes");
        Part<TextBox>(d, "SubtitleCodes").Text = "en, pt-BR, ../bad"; RadioOn(Part<RadioButton>(d, "SubSeparate"));
        var model = d.Model!; var requests = model.BuildRequests(scratch, "pkg", 2, false);
        var sel = requests[0].Youtube!;
        Check(sel.Preset!.Container == "webm" && sel.Subtitles!.Mode == SubtitleMode.Separate && sel.Subtitles.Choices.Select(c => c.Code).Distinct().SequenceEqual(new[] { "en", "pt-BR" }) && sel.Subtitles.Choices.Any(c => c.Auto) && requests[0].FileName!.EndsWith(".webm"), "playlist requests carry container and the global subtitle wish (unsafe codes dropped)");
        sel.Validate();
        model.WindowsLanguage = "fr-CA";
        kind.SelectedValue = "windows"; Pump(40);
        Check(model.BuildRequests(scratch, "pkg", 2, false)[0].Youtube!.Subtitles!.Choices.All(c => c.Code == "fr"), "Windows language choice uses the primary language code");
        Part<CheckBox>(d, "AudioOnlyBox").IsChecked = true; Pump(60);
        Check(model.BuildRequests(scratch, "pkg", 2, false)[0].Youtube!.Subtitles is null && !Part<ComboBox>(d, "SubtitleKindBox").IsEnabled, "audio only playlists skip subtitles");
        SaveShot(d, "v1130-playlist-vocabulary.png");
        d.Close();
    }
    private static List<Label> AllLabels(DependencyObject root) { var list = new List<Label>(); void Walk(DependencyObject o) { for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(o); i++) { var c = System.Windows.Media.VisualTreeHelper.GetChild(o, i); if (c is Label l) list.Add(l); Walk(c); } } Walk(root); return list; }

    // ------------------------------------------------------------------ item 6: stream dialog row
    private static void StreamDialogRow(string scratch)
    {
        var manifest = new StreamManifest
        {
            Kind = StreamKind.Hls, Url = "https://cdn.example/master.m3u8", Duration = TimeSpan.FromSeconds(60),
            Variants = new[] { new VideoVariant("v0", 800_000, 640, 360, 30, "avc1.4d401e,mp4a.40.2", null) { HasOwnAudio = true } },
            SubtitleTracks = new[] { new SubtitleTrack("s0", "en", "English", new Uri("https://cdn.example/en.m3u8"), true), new SubtitleTrack("s1", "fr", "Francais", new Uri("https://cdn.example/fr.m3u8")) }
        };
        var seed = new DownloadRequest("https://cdn.example/master.m3u8", "movie");
        var keep = Thread.CurrentThread.CurrentUICulture; Thread.CurrentThread.CurrentUICulture = new System.Globalization.CultureInfo("fr-FR"); // the dialog reads the Windows language when it opens
        var d = new StreamOptionsDialog(seed, scratch, (r, t) => Task.FromResult(manifest), new FakeVideoTool { Installed = true }) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10, Width = 700, Height = 900 };
        Thread.CurrentThread.CurrentUICulture = keep;
        d.Show(); Pump(); PumpUntil(() => Part<StackPanel>(d, "ReadyPanel").Visibility == Visibility.Visible, "stream dialog ready", 15000); Pump(80);
        var panel = Part<Border>(d, "SubtitlesPanel");
        Check(panel.IsVisible && d.SubtitleItems.Count == 2 && d.SubtitleItems.All(i => !i.IsTicked) && d.SubtitleItems.Single(i => i.Yours).Track.Id == "s1", "stream dialog lists the manifest's subtitle tracks, none ticked, Windows language highlighted");
        Check(d.SelectedSubtitles is null, "nothing ticked: no subtitle request");
        d.TickSubtitle("s0", true);
        Check(d.SelectedSubtitles is { Mode: SubtitleMode.Embed } s && s.Choices.Single().Code == "s0" && Part<TextBlock>(d, "SubtitleNote").IsVisible, "ticked track becomes an embed request with the container note");
        RadioOn(Part<RadioButton>(d, "SubSeparate"));
        Check(d.SelectedSubtitles!.Mode == SubtitleMode.Separate && !Part<TextBlock>(d, "SubtitleNote").IsVisible, "separate files need no container note");
        SaveShot(d, "v1130-stream-subtitles.png");
        d.Close();
    }

    // ------------------------------------------------------------------ item 5 + 6: every new string in every language
    private static readonly string[] PickerKeys =
    {
        "youtube.pick.type", "youtube.pick.type_video_audio", "youtube.pick.type_video_only", "youtube.pick.type_audio_only", "youtube.pick.container", "youtube.pick.best", "youtube.pick.about", "youtube.pick.audio_included",
        "youtube.pick.remux", "youtube.pick.show_all", "youtube.pick.raw_hint", "youtube.pick.search", "youtube.pick.sort", "youtube.pick.sort_resolution", "youtube.pick.sort_size", "youtube.pick.sort_id", "youtube.pick.summary_title",
        "youtube.pick.resolution", "youtube.summary.video_audio", "youtube.summary.video_included", "youtube.summary.video_only", "youtube.summary.audio_only", "youtube.summary.mp3", "youtube.summary.size",
        "youtube.summary.subs_embedded", "youtube.summary.subs_separate", "youtube.subs.title", "youtube.subs.manual", "youtube.subs.auto", "youtube.subs.mode", "youtube.subs.embed", "youtube.subs.separate", "youtube.subs.yours",
        "youtube.subs.none_ticked", "youtube.subs.webm_warning", "youtube.phase.subtitles", "youtube.subtitles_none", "youtube.subtitles_embed_failed", "youtube.subtitles_need_tool", "youtube.subtitles_failed",
        "stream.phase.subtitles", "stream.subtitles_none", "stream.subtitles_failed", "stream.subtitles_container", "stream.subtitles_embed_failed", "stream.subtitles_need_tool", "stream.subs.hint", "stream.subs.embed_note",
        "playlist.container_auto", "playlist.subs.none", "playlist.subs.windows", "playlist.subs.list", "playlist.subs.codes", "playlist.subs.codes_hint"
    };
    private static void PickerLocales()
    {
        // words that are the same in more than one language on purpose
        var same = new HashSet<string> { "youtube.pick.type:fr", "youtube.pick.container:de", "youtube.summary.video_audio:fr" };
        Loc.SetLanguage("en"); var english = PickerKeys.ToDictionary(k => k, Loc.T);
        foreach (var info in LanguageCatalog.All)
        {
            Loc.SetLanguage(info.Tag);
            foreach (var k in PickerKeys)
            {
                var v = Loc.T(k);
                Check(v.Length > 0 && v != k, $"picker {info.Tag}: {k} exists");
                if (info.Tag != "en") Check(v != english[k] || same.Contains(k + ":" + info.Tag.Split('-')[0]), $"picker {info.Tag}: {k} is translated");
                foreach (var n in new[] { "{0}", "{1}", "{2}" }) if (english[k].Contains(n)) Check(v.Contains(n), $"picker {info.Tag}: {k} keeps {n}");
            }
        }
        Loc.SetLanguage("en");
    }
}
