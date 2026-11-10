using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

/// <summary>1.11.0 Part B (items 4, 5, 6, 8): routing, step-1 load panel, buttons, playlist model and dialog, thumbnail loader. Fake listers and handlers only: no network, no helper process.</summary>
internal static partial class V1110Tests
{
    private const string Vid = "aqz-KE-bpKQ", ListId = "PLav47HAVZMjnTFVZL-aImCQIC0uLZtNCz";
    private static T Part<T>(Window w, string name) where T : FrameworkElement => (T)w.FindName(name);
    private static void SetModeless(Window w) => w.GetType().GetProperty("Modeless", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(w, true);

    private static YoutubePlaylist SamplePlaylist(int n = 6)
    {
        var lines = new List<string>();
        for (int i = 0; i < n; i++)
        {
            var avail = i == 1 ? ",\"availability\":\"private\"" : i == 2 ? ",\"availability\":\"needs_auth\"" : "";
            lines.Add($"{{\"id\":\"v{i:D10}\",\"title\":\"Video number {i}\",\"duration\":{60 + i * 30},\"channel\":\"Channel {i % 2}\",\"playlist_title\":\"Sample list\",\"playlist_count\":{n}{avail}}}");
        }
        return YoutubePlaylists.Parse(string.Join("\n", lines));
    }

    // ---------------------------------------------------------------- item 6: routing
    private static void Routing()
    {
        string v = "https://www.youtube.com/watch?v=" + Vid, pl = "https://www.youtube.com/playlist?list=" + ListId;
        Check(YoutubeRouting.Plan(v).Action == YoutubeAction.OpenVideo && YoutubeRouting.Plan("https://youtu.be/" + Vid).Action == YoutubeAction.OpenVideo, "a video link opens the video dialog");
        Check(YoutubeRouting.Plan("HTTPS://M.YOUTUBE.COM/shorts/" + Vid + ".").Action == YoutubeAction.OpenVideo && YoutubeRouting.Plan("(https://music.youtube.com/watch?v=" + Vid + ")").Action == YoutubeAction.OpenVideo, "case, m/music hosts, shorts and trailing punctuation");
        Check(YoutubeRouting.Plan(pl).Action == YoutubeAction.OpenPlaylist && YoutubeRouting.Plan(pl).PlaylistUrl == pl, "a playlist link opens the playlist dialog");
        Check(YoutubeRouting.Plan(v + "&list=" + ListId).Action == YoutubeAction.AskVideoOrPlaylist, "a watch link with a list asks which one");
        var mix = YoutubeRouting.Plan("https://www.youtube.com/playlist?list=RDabcdefghijkl");
        Check(mix.Action == YoutubeAction.Refused && !string.IsNullOrEmpty(mix.Message), "a mix list is refused with a plain message");
        foreach (var other in new[] { "https://example.org/file.zip", "magnet:?xt=urn:btih:" + new string('a', 40), "ftp://x.example.org/a", "", "https://youtube.com.evil.test/watch?v=" + Vid })
            Check(YoutubeRouting.Plan(other).Action == YoutubeAction.NotYoutube && YoutubeRouting.Note(other) is null, "no change for a non YouTube address: " + other);
        Check(YoutubeRouting.Note(v) is { IsProblem: false } n1 && n1.Text == Loc.T("add.youtube_note"), "the inline note for a video");
        Check(YoutubeRouting.Note(pl) is { IsProblem: false } n2 && n2.Text == Loc.T("add.youtube_note_playlist"), "the inline note for a playlist");
        Check(YoutubeRouting.Note(v + "&list=" + ListId) is { } n3 && n3.Text == Loc.T("add.youtube_note_both"), "the inline note for both");
        Check(YoutubeRouting.Note("https://www.youtube.com/playlist?list=RDabc") is { IsProblem: true }, "the inline note flags a refused list");
        Check(YoutubeRouting.SingleLink("  " + v + "\r\n") is { } s && s.Action == YoutubeAction.OpenVideo, "one pasted link routes");
        Check(YoutubeRouting.SingleLink(v + "\n" + pl) is null && YoutubeRouting.SingleLink("see " + v) is null && YoutubeRouting.SingleLink("https://example.org/a") is null, "lists, sentences and other addresses stay with the link grabber / file flow");
        var add = new AddDownloadWindow("") { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10 };
        add.Show(); var box = Part<TextBox>(add, "UrlInput"); var note = Part<TextBlock>(add, "YoutubeNote");
        Check(note.Visibility == Visibility.Collapsed, "Add URL: no note for an empty box");
        box.Text = v; Pump(); Check(note.Visibility == Visibility.Visible && note.Text == Loc.T("add.youtube_note"), "Add URL: the note appears while typing a YouTube link");
        box.Text = pl; Pump(); Check(note.Text == Loc.T("add.youtube_note_playlist"), "Add URL: playlist note");
        box.Text = "https://example.org/a.zip"; Pump(); Check(note.Visibility == Visibility.Collapsed, "Add URL: the note goes away for a file address");
        add.Close();
    }

    // ---------------------------------------------------------------- item 4: step 1, progress panel, buttons (item 8)
    private static YoutubeOptionsDialog NewVideoDialog(string scratch, Func<string, CancellationToken, Action<string>, Task<YoutubeMetadata>> lines)
    {
        var d = new YoutubeOptionsDialog(new DownloadRequest("https://www.youtube.com/watch?v=" + Vid), scratch, null!, null!, settings: new AppSettings { DownloadDirectory = scratch }, lineInspector: lines)
        { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10, Width = 640, Height = 640 };
        d.Show(); Pump(); return d;
    }

    private static void VideoDialogStates(string scratch)
    {
        var meta = new YoutubeMetadata("Sample", new[] { new YoutubeFormat("137", "mp4", 1080, 0, true, false, 0), new YoutubeFormat("140", "m4a", 0, 128, false, true, 0) });
        var gate = new TaskCompletionSource<YoutubeMetadata>(); Action<string>? sink = null;
        var d = NewVideoDialog(scratch, (u, t, line) => { sink = line; t.Register(() => gate.TrySetCanceled(t)); return gate.Task; });
        var read = Part<Button>(d, "ReadButton"); var start = Part<Button>(d, "StartButton"); var queue = Part<Button>(d, "QueueButton"); var sched = Part<Button>(d, "ScheduleButton");
        Check(read.Content?.ToString() == Loc.T("youtube.read") && Loc.T("youtube.read").Contains("1"), "step 1 button says it is the first step: " + read.Content);
        Check(Part<TextBlock>(d, "ReadHint").Text == Loc.T("youtube.read_hint") && Part<TextBlock>(d, "Step2Label").Text == Loc.T("youtube.step2"), "one-line hint and the step 2 label are visible");
        Check(start.Content?.ToString() == "Download now" && queue.Content?.ToString() == "Add to queue" && sched.Content?.ToString() == "Schedule...", "buttons are Download now / Add to queue / Schedule...");
        Check(!start.IsEnabled && !queue.IsEnabled && !sched.IsEnabled && start.ToolTip is string tip && tip.Length > 0, "buttons are disabled before the info is loaded, with a reason in the tooltip");
        Check(Part<Border>(d, "LoadPanel").Visibility == Visibility.Collapsed, "no progress panel before loading");
        var task = d.ReadAsync(); Pump();
        var panel = Part<Border>(d, "LoadPanel"); var bar = Part<ProgressBar>(d, "LoadingProgress");
        Check(panel.Visibility == Visibility.Visible && !read.IsEnabled && Part<Button>(d, "CancelLoadButton").IsEnabled, "loading shows the progress panel with a cancel button");
        Check(Part<TextBlock>(d, "LoadingStatus").Text == Loc.T("youtube.stage.preparing") && bar.Maximum == 100, "first stage text: " + Part<TextBlock>(d, "LoadingStatus").Text);
        sink!("[youtube] Extracting URL: x"); sink("[youtube] x: Downloading webpage"); Pump(); d.RefreshLoading();
        Check(Part<TextBlock>(d, "LoadingStatus").Text == Loc.T("youtube.stage.contacting") && Part<TextBlock>(d, "LoadingElapsed").Text.Length > 0, "a helper line moves the stage; elapsed seconds are shown");
        double before = bar.Value; sink("[youtube] x: Downloading player abc"); Pump(); d.RefreshLoading();
        Check(bar.Value >= before && bar.Value < 95.01 && Part<TextBlock>(d, "LoadingPercent").Text.EndsWith("%"), "percentage never goes backwards and stays under the cap while waiting");
        gate.SetResult(meta); Wait(task); Pump();
        Check(bar.Value == 100 && Part<TextBlock>(d, "LoadingStatus").Text == Loc.T("youtube.stage.done") && start.IsEnabled && queue.IsEnabled && sched.IsEnabled && start.ToolTip is null, "the result gives 100% and enables the three buttons");
        d.Close();

        var hang = new TaskCompletionSource<YoutubeMetadata>();
        d = NewVideoDialog(scratch, (u, t, l) => { t.Register(() => hang.TrySetCanceled(t)); return hang.Task; });
        task = d.ReadAsync(); Pump(); d.CancelLoad(); Wait(task); Pump();
        Check(Part<TextBox>(d, "Error").Text == Loc.T("youtube.load_cancelled") && Part<Border>(d, "LoadPanel").Visibility == Visibility.Collapsed && Part<Button>(d, "ReadButton").IsEnabled && !Part<Button>(d, "StartButton").IsEnabled, "cancel ends the load, says so plainly and allows a new load");
        d.Close();

        d = NewVideoDialog(scratch, (u, t, l) => Task.FromException<YoutubeMetadata>(new YoutubeException("youtube.restricted", "x")));
        Wait(d.ReadAsync()); Pump();
        Check(Part<TextBox>(d, "Error").Text.Length > 0 && Part<Grid>(d, "ErrorRow").Visibility == Visibility.Visible && Part<Button>(d, "CopyErrorButton").IsVisible && Part<Border>(d, "LoadPanel").Visibility == Visibility.Collapsed && !Part<Button>(d, "StartButton").IsEnabled, "an error shows the plain message with a Copy button and no enabled download");
        d.Close();

        foreach (var action in new[] { DownloadOptionsDialog.DialogAction.StartNow, DownloadOptionsDialog.DialogAction.Queue })
        {
            d = NewVideoDialog(scratch, (u, t, l) => Task.FromResult(meta)); SetModeless(d); Wait(d.ReadAsync()); Pump();
            Wait(d.AcceptAsync(action)); Pump();
            Check(d.Accepted && d.UserAction == action && d.Request is not null && d.ScheduledAt is null, action + " accepts with its own action");
        }
        d = NewVideoDialog(scratch, (u, t, l) => Task.FromResult(meta)); SetModeless(d); Wait(d.ReadAsync()); Pump();
        Part<SchedulePicker>(d, "ScheduleInput").Text = DateTime.Now.AddHours(2).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        Wait(d.AcceptAsync(DownloadOptionsDialog.DialogAction.Schedule)); Pump();
        Check(d.Accepted && d.UserAction == DownloadOptionsDialog.DialogAction.Schedule && d.ScheduledAt > DateTimeOffset.Now, "Schedule... accepts with the typed time");
        Check(YoutubeCommit.StartsNow(DownloadOptionsDialog.DialogAction.StartNow) && !YoutubeCommit.StartsNow(DownloadOptionsDialog.DialogAction.Queue), "only Download now starts at once");
    }

    // ---------------------------------------------------------------- item 5: model
    private static void PlaylistModelRules()
    {
        var list = SamplePlaylist(6); var m = new PlaylistModel(list); int changes = 0; m.Changed += () => changes++;
        Check(m.Rows.Count == 6 && m.AvailableCount == 4 && m.SelectedCount == 4, "all available rows start selected: " + m.SelectedCount);
        Check(!m.Rows[1].Available && !m.Rows[1].IsSelected && !m.Rows[2].IsSelected, "unavailable rows are disabled and unchecked");
        m.Rows[1].IsSelected = true; Check(!m.Rows[1].IsSelected, "an unavailable row cannot be checked");
        Check(m.TotalSeconds == m.Rows.Where(r => r.IsSelected).Sum(r => r.Entry.DurationSeconds), "selection total duration");
        m.SelectNone(); Check(m.SelectedCount == 0 && changes > 0, "select none"); m.SelectAll(); Check(m.SelectedCount == 4, "select all skips unavailable rows");
        m.Rows[0].IsSelected = false; m.Invert(); Check(m.SelectedCount == 1 && m.Rows[0].IsSelected, "invert flips the available rows only");
        m.SelectAll(); m.SetFilter("number 3"); Check(m.Visible.Count() == 1, "filter by title"); m.SelectNone(); Check(m.Rows.Count(r => r.IsSelected) == 3, "select none acts on the filtered rows only"); m.SetFilter("");
        m.SelectAll();
        var o = m.Options; Check(o.Quality == "best" && o.AudioKbps == 0 && !o.AudioOnly && !o.Mp3, "global defaults: Best video, Best audio, video");
        o.Quality = "720"; o.AudioKbps = 128; foreach (var r in m.Rows.Where(r => r.Available)) Check(r.Preset == new YoutubePreset("720", 128) && !r.HasOverride, "rows follow the global quality");
        o.Quality = "9999"; Check(o.Quality == "best", "an invalid global quality falls back"); o.AudioKbps = 77; Check(o.AudioKbps == 0, "an invalid audio rate falls back"); o.Quality = "720";
        var row = m.Rows[0]; row.AudioOnly = true;
        Check(row.HasOverride && row.OverrideQuality == "audio" && row.Preset.AudioOnly && !m.Rows[3].Preset.AudioOnly, "a row can be audio only on its own");
        row.AudioOnly = false; Check(!row.HasOverride && row.Preset.Quality == "720", "setting the row back to the global choice clears the override");
        row.OverrideChoice = "360"; Check(row.HasOverride && row.Preset.Quality == "360" && !row.AudioOnly, "a row quality override");
        row.OverrideChoice = ""; Check(!row.HasOverride, "Default clears the override");
        o.AudioOnly = true; o.Mp3 = true; m.Rows[3].AudioOnly = false;
        Check(m.Rows[0].Preset.AudioOnly && m.Rows[0].EffectiveMp3 && !m.Rows[3].Preset.AudioOnly && m.Rows[3].HasOverride && !m.Rows[3].EffectiveMp3, "global audio only (MP3) with one row overriding to video");
        o.AudioOnly = false; o.Mp3 = false; m.Rows[3].OverrideChoice = ""; m.Rows[4].AudioOnly = true; m.Rows[5].IsSelected = false;
        var reqs = m.BuildRequests("C:/Scratch/pl", "My list", 3, true);
        Check(reqs.Count == 3 && reqs.All(r => r.Package == "My list" && r.PackageSlots == 3 && r.Youtube!.Preset is not null && r.TargetDirectory == "C:/Scratch/pl"), "one package job per selected video with a preset and the slot count");
        Check(reqs[0].FileName == "01 - Video number 0.mp4" && reqs[2].FileName.StartsWith("05 - Video number 4") && reqs[2].FileName.EndsWith(".m4a") && reqs[2].Youtube!.Preset!.AudioOnly, "numbering prefix and audio extension for the audio row: " + reqs[2].FileName);
        Check(m.BuildRequests("x", "p", 99, false).All(r => r.PackageSlots == 4) && m.BuildRequests("x", "p", 0, false).All(r => r.PackageSlots == 1), "slots are clamped to 1..4");
        Check(m.BuildRequests("x", "p", 1, false)[0].FileName == "Video number 0.mp4", "no numbering prefix when off");
        Check(PlaylistModel.FormatDuration(61) == "1:01" && PlaylistModel.FormatDuration(3725) == "1:02:05", "duration text");
        Check(new PlaylistModel(YoutubePlaylists.Parse("{\"id\":\"aaaaaaaaaaa\",\"title\":\"x\",\"availability\":\"private\"}")).BuildRequests("x", "p", 1, false).Count == 0, "no selectable video gives no jobs");
    }

    // ---------------------------------------------------------------- item 5: thumbnail loader
    private static byte[] TinyPng(int w = 8, int h = 8)
    {
        var pixels = new byte[w * h * 4]; for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * 7);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4)));
        using var ms = new MemoryStream(); enc.Save(ms); return ms.ToArray();
    }
    private sealed class ThumbHandler : HttpMessageHandler
    {
        public int Requests, Active, Peak; public Func<HttpRequestMessage, HttpResponseMessage> Reply = _ => new(HttpStatusCode.OK); public int DelayMs;
        public readonly List<string> Urls = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests); lock (Urls) Urls.Add(request.RequestUri!.ToString());
            var now = Interlocked.Increment(ref Active); int seen; while (now > (seen = Volatile.Read(ref Peak)) && Interlocked.CompareExchange(ref Peak, now, seen) != seen) { }
            try { if (DelayMs > 0) await Task.Delay(DelayMs, cancellationToken); return Reply(request); }
            finally { Interlocked.Decrement(ref Active); }
        }
    }
    private static string Img(string id) => $"https://i.ytimg.com/vi/{id}/mqdefault.jpg";
    private static string VId(int i) => "v" + i.ToString("D10");

    private static void Thumbnails(string scratch)
    {
        var png = TinyPng();
        string State(string n) { var p = Path.Combine(scratch, "thumb-" + n); Directory.CreateDirectory(p); return p; }
        Task<ImageSource?> Load(ThumbnailLoader l, string id, string? url = null, CancellationToken t = default) => l.LoadAsync(id, url ?? Img(id), t);
        ImageSource? Get(Task<ImageSource?> t) { t.Wait(15000); return t.Result; }

        var h = new ThumbHandler { Reply = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) } };
        using (var l = new ThumbnailLoader(State("a"), null, h))
        {
            var image = Get(Load(l, VId(1)));
            Check(image is { } && image.IsFrozen && h.Requests == 1, "a thumbnail loads, is frozen and costs one request");
            Check(Get(Load(l, VId(1))) is not null && h.Requests == 1 && l.MemoryHits == 1, "the second request comes from memory");
            Check(File.Exists(Path.Combine(State("a"), "thumbs", VId(1) + ".img")), "the picture is cached on disk under the state folder");
        }
        using (var l2 = new ThumbnailLoader(State("a"), null, h)) { Check(Get(Load(l2, VId(1))) is not null && h.Requests == 1 && l2.DiskHits == 1, "a new loader reads the disk cache"); }
        h = new ThumbHandler { Reply = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) } };
        using (var l = new ThumbnailLoader(State("b"), null, h))
        {
            foreach (var bad in new[] { "http://i.ytimg.com/vi/x/a.jpg", "https://evil.test/a.jpg", "https://i.ytimg.com.evil.test/a.jpg", "https://user@i.ytimg.com/a.jpg", "https://i.ytimg.com:8443/a.jpg", "ftp://i.ytimg.com/a.jpg", "file:///c:/a.png", "" })
                Check(Get(Load(l, VId(2), bad)) is null, "refused address: " + bad);
            Check(Get(Load(l, "bad id", Img("x"))) is null && Get(Load(l, "../../etc/passwd", Img("x"))) is null, "refused video id");
            Check(h.Requests == 0, "refused addresses never reach the network");
        }
        foreach (var (name, reply) in new (string, Func<HttpRequestMessage, HttpResponseMessage>)[]
        {
            ("redirect", r => { var x = new HttpResponseMessage(HttpStatusCode.Redirect); x.Headers.Location = new Uri("https://evil.test/a.png"); return x; }),
            ("404", r => new HttpResponseMessage(HttpStatusCode.NotFound)),
            ("too big", r => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png.Concat(new byte[ThumbnailLoader.MaxBytes]).ToArray()) }),
            ("web page", r => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>" + new string('x', 100) + "</html>") }),
            ("corrupt image", r => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png.Take(30).Concat(new byte[40]).ToArray()) }),
            ("huge pixels", r => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(TinyPng(ThumbnailLoader.MaxPixels + 1, 1)) }),
        })
        {
            var hh = new ThumbHandler { Reply = reply }; var key = "c-" + name.Replace(' ', '_');
            using var l = new ThumbnailLoader(State(key), null, hh);
            Check(Get(Load(l, VId(3))) is null && hh.Urls.All(u => u.StartsWith("https://i.ytimg.com/", StringComparison.Ordinal)), "failure gives a placeholder (null), never a request to another host: " + name);
        }
        h = new ThumbHandler { DelayMs = 120, Reply = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) } };
        using (var l = new ThumbnailLoader(State("d"), null, h))
        {
            var all = Enumerable.Range(0, 20).Select(i => Load(l, VId(i))).ToArray(); Task.WaitAll(all.Cast<Task>().ToArray(), 30000);
            Check(all.All(t => t.Result is not null) && h.Peak <= ThumbnailLoader.MaxConcurrent && h.Peak > 1 && l.PeakConcurrent <= 4, $"at most 4 requests at once (peak {h.Peak}) and they do run in parallel");
        }
        h = new ThumbHandler { DelayMs = 5000, Reply = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) } };
        using (var l = new ThumbnailLoader(State("e"), null, h))
        {
            using var cts = new CancellationTokenSource(); var t = Load(l, VId(5), null, cts.Token); Thread.Sleep(150); cts.Cancel();
            Check(t.Wait(3000) && t.Result is null, "a cancelled request ends quickly without a picture");
            var t2 = Load(l, VId(6)); Thread.Sleep(150); l.Cancel();
            Check(t2.Wait(3000) && t2.Result is null && Get(Load(l, VId(7))) is null, "Cancel() ends pending requests and later ones fail fast");
        }
        h = new ThumbHandler { Reply = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) } };
        using (var l = new ThumbnailLoader(State("f"), null, h))
        {
            for (int i = 0; i < ThumbnailLoader.MemoryLimit + 25; i++) Get(Load(l, VId(i)));
            Check(l.MemoryCount <= ThumbnailLoader.MemoryLimit, "the memory cache is bounded: " + l.MemoryCount);
            var dir = Path.Combine(State("f"), "thumbs");
            File.WriteAllText(Path.Combine(dir, "stray.tmp"), "x");
            var old = Path.Combine(dir, "old.img"); File.WriteAllBytes(old, png); File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));
            l.Clean();
            Check(!File.Exists(Path.Combine(dir, "stray.tmp")) && !File.Exists(old), "clean removes stray temp files and pictures older than 14 days");
        }
        var many = Path.Combine(State("g"), "thumbs"); Directory.CreateDirectory(many);
        for (int i = 0; i < ThumbnailLoader.DiskFiles + 40; i++) { var f = Path.Combine(many, $"x{i:D5}.img"); File.WriteAllBytes(f, png); File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(-i)); }
        using (var l = new ThumbnailLoader(State("g"), null, h)) { l.Clean(); Check(Directory.GetFiles(many).Length <= ThumbnailLoader.DiskFiles, "the disk cache keeps at most " + ThumbnailLoader.DiskFiles + " files"); }
    }

    // ---------------------------------------------------------------- item 5: dialog
    private static PlaylistOptionsDialog NewPlaylistDialog(string scratch, Func<string, CancellationToken, Action<string>, Task<YoutubePlaylist>> lister, AppSettings? settings = null, ThumbnailLoader? thumbs = null, double width = 900, double height = 760)
    {
        var d = new PlaylistOptionsDialog("https://www.youtube.com/playlist?list=" + ListId, scratch, null, settings ?? new AppSettings { DownloadDirectory = scratch }, lister, thumbs)
        { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10, Width = width, Height = height };
        d.Show(); Pump(); return d;
    }

    private static void PlaylistDialogFlow(string scratch)
    {
        var d = NewPlaylistDialog(scratch, (u, t, l) => Task.FromResult(SamplePlaylist()));
        var start = Part<Button>(d, "StartButton"); var queue = Part<Button>(d, "QueueButton"); var sched = Part<Button>(d, "ScheduleButton");
        Check(Part<Button>(d, "ReadButton").Content?.ToString() == Loc.T("playlist.read") && Part<TextBlock>(d, "Step2Label").Text == Loc.T("playlist.step2"), "playlist step 1 / step 2 labels");
        Check(start.Content?.ToString() == "Download now" && queue.Content?.ToString() == "Add to queue" && sched.Content?.ToString() == "Schedule..." && !start.IsEnabled && !queue.IsEnabled && !sched.IsEnabled && start.ToolTip is string, "same button vocabulary, disabled until the list is loaded, with a tooltip");
        Check(!Part<StackPanel>(d, "OptionsPanel").IsEnabled && !Part<Grid>(d, "ToolsRow").IsEnabled, "options and tools are off before the list is loaded");
        Wait(d.ReadAsync()); Pump();
        var list = Part<ListBox>(d, "VideoList"); var summary = Part<TextBox>(d, "Summary");
        Check(d.Model!.Rows.Count == 6 && list.Items.Count == 6 && Part<TextBox>(d, "PackageName").Text == "Sample list", "the list shows every entry; the package name defaults to the playlist title");
        Check(summary.Text.Contains("Sample list") && summary.Text.Contains("6") && summary.Text.Contains("4"), "header: title, count and selected count: " + summary.Text);
        Check(start.IsEnabled && queue.IsEnabled && sched.IsEnabled && Part<ProgressBar>(d, "LoadingProgress").Value == 100, "buttons enable after loading; progress reaches 100");
        Check(Part<TextBox>(d, "AtOnce").Text == "1", "Downloads at once defaults to 1");
        Part<Button>(d, "SelectNoneButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        Check(d.Model.SelectedCount == 0 && !start.IsEnabled && start.ToolTip is string, "select none disables the buttons with a reason");
        Part<Button>(d, "SelectAllButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        Part<TextBox>(d, "FilterBox").Text = "number 4"; Pump(); Check(list.Items.Count == 1, "the filter narrows the list");
        Part<TextBox>(d, "FilterBox").Text = ""; Pump(); Check(list.Items.Count == 6, "clearing the filter restores the list");
        Part<ComboBox>(d, "QualityBox").SelectedValue = "480"; Part<CheckBox>(d, "AudioOnlyBox").IsChecked = true; Part<CheckBox>(d, "Mp3Box").IsChecked = true; Pump();
        Check(d.Model.Options.Quality == "480" && d.Model.Options.AudioOnly && d.Model.Options.Mp3, "global controls reach the model");
        d.Close();

        foreach (var action in new[] { DownloadOptionsDialog.DialogAction.StartNow, DownloadOptionsDialog.DialogAction.Queue })
        {
            var s = new AppSettings { DownloadDirectory = Path.Combine(scratch, "dl-" + action) };
            d = NewPlaylistDialog(scratch, (u, t, l) => Task.FromResult(SamplePlaylist()), s); SetModeless(d); Wait(d.ReadAsync()); Pump();
            Part<TextBox>(d, "Folder").Text = Path.Combine(scratch, "dl-" + action); Part<TextBox>(d, "AtOnce").Text = "3"; Part<CheckBox>(d, "NumberBox").IsChecked = true;
            d.Model!.Rows[0].AudioOnly = true; d.Accept(action); Pump();
            Check(d.Accepted && d.UserAction == action && d.Requests.Count == 4 && d.Slots == 3 && s.PlaylistVideosAtOnce == 3 && d.Requests.All(r => r.PackageSlots == 3 && r.Package == "Sample list"), action + ": four jobs, slots 3 stored in the settings");
            Check(d.Requests[0].Youtube!.Preset!.AudioOnly && !d.Requests[1].Youtube!.Preset!.AudioOnly && d.Requests[0].FileName.StartsWith("01 - "), action + ": the per-row override is carried and the numbering is applied");
            Check(Directory.Exists(d.FinalFolderPath) && Path.GetFileName(d.FinalFolderPath).StartsWith("Sample list", StringComparison.Ordinal) && d.Requests.All(r => r.TargetDirectory == d.FinalFolderPath), action + ": the sub-folder is named after the playlist");
        }
        d = NewPlaylistDialog(scratch, (u, t, l) => Task.FromResult(SamplePlaylist())); SetModeless(d); Wait(d.ReadAsync()); Pump();
        Part<TextBox>(d, "Folder").Text = Path.Combine(scratch, "dl-sched");
        Part<Button>(d, "ScheduleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        Check(Part<StackPanel>(d, "SchedulePanel").Visibility == Visibility.Visible && !d.Accepted, "Schedule... first shows the time box");
        Part<SchedulePicker>(d, "ScheduleInput").Text = "not a time"; d.Accept(DownloadOptionsDialog.DialogAction.Schedule); Pump();
        Check(!d.Accepted && Part<TextBox>(d, "Error").Text.Length > 0, "an invalid time is refused with a message");
        Part<SchedulePicker>(d, "ScheduleInput").Text = DateTime.Now.AddHours(3).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture); Part<TextBox>(d, "Error").Text = ""; d.Accept(DownloadOptionsDialog.DialogAction.Schedule); Pump();
        Check(d.Accepted && d.UserAction == DownloadOptionsDialog.DialogAction.Schedule && d.ScheduledAt > DateTimeOffset.Now, "a valid time schedules the package");
        foreach (var bad in new[] { "0", "5", "x", "", "-1", "1.5" })
        {
            d = NewPlaylistDialog(scratch, (u, t, l) => Task.FromResult(SamplePlaylist())); SetModeless(d); Wait(d.ReadAsync()); Part<TextBox>(d, "Folder").Text = Path.Combine(scratch, "dl-bad"); Part<TextBox>(d, "AtOnce").Text = bad; d.Accept(DownloadOptionsDialog.DialogAction.Queue);
            Check(!d.Accepted && Part<TextBox>(d, "Error").Text == Loc.T("playlist.at_once_invalid"), "Downloads at once '" + bad + "' is refused"); d.Close();
        }
        d = NewPlaylistDialog(scratch, (u, t, l) => Task.FromException<YoutubePlaylist>(new YoutubeException("youtube.playlist_empty", "")));
        Wait(d.ReadAsync()); Pump(); Check(d.Model is null && Part<TextBox>(d, "Error").Text.Length > 0 && Part<Button>(d, "CopyErrorButton").IsVisible && !Part<Button>(d, "StartButton").IsEnabled, "a failed listing shows the message with Copy"); d.Close();
        var gate = new TaskCompletionSource<YoutubePlaylist>();
        d = NewPlaylistDialog(scratch, (u, t, l) => { t.Register(() => gate.TrySetCanceled(t)); return gate.Task; });
        var task = d.ReadAsync(); Pump(); Check(Part<Border>(d, "LoadPanel").Visibility == Visibility.Visible && Part<Button>(d, "CancelLoadButton").IsEnabled, "playlist loading shows the same progress panel");
        d.CancelLoad(); Wait(task); Pump(); Check(Part<TextBox>(d, "Error").Text == Loc.T("youtube.load_cancelled") && d.Model is null, "cancelling the playlist load"); d.Close();
        d = NewPlaylistDialog(scratch, (u, t, l) => Task.FromResult(SamplePlaylist(10) with { Truncated = true, Total = 700 })); Wait(d.ReadAsync()); Pump();
        Check(Part<TextBlock>(d, "TruncatedNote").Visibility == Visibility.Visible && Part<TextBlock>(d, "TruncatedNote").Text.Contains("500"), "a truncated list explains the 500 limit"); d.Close();
    }

    private static void PlaylistVirtualised(string scratch)
    {
        var png = TinyPng(); var h = new ThumbHandler { Reply = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) } };
        var big = YoutubePlaylists.Parse(string.Join("\n", Enumerable.Range(0, 400).Select(i => $"{{\"id\":\"{VId(i)}\",\"title\":\"Long list video {i}\",\"duration\":90,\"channel\":\"C\",\"playlist_title\":\"Big\",\"playlist_count\":400}}")));
        using var thumbs = new ThumbnailLoader(Path.Combine(scratch, "virt"), null, h);
        var d = NewPlaylistDialog(scratch, (u, t, l) => Task.FromResult(big), thumbs: thumbs, height: 700);
        Wait(d.ReadAsync()); Pump(400);
        Check(d.Model!.Rows.Count == 400 && d.ThumbnailRequests > 0 && d.ThumbnailRequests < 60, $"only rows that were drawn ask for a picture ({d.ThumbnailRequests} of 400)");
        var sv = Visual<ScrollViewer>(Part<ListBox>(d, "VideoList")).First(); int first = d.ThumbnailRequests;
        sv.ScrollToVerticalOffset(300); Pump(500); Check(d.ThumbnailRequests > first && d.ThumbnailRequests < 150, $"scrolling asks for the newly visible rows ({d.ThumbnailRequests})");
        Check(d.Model.Rows.Any(r => r.Thumbnail is not null), "visible rows receive their picture");
        d.Close(); Pump(100);
        int after = h.Requests; Pump(300); Check(h.Requests == after, "closing the dialog stops further picture requests");
    }

    /// <summary>1.13.0: a row whose picture failed shows the themed placeholder and asks again when it is scrolled back into view.</summary>
    private static void PlaylistThumbnailRetry(string scratch)
    {
        var png = TinyPng(); bool failing = true; var savedDelay = PlaylistOptionsDialog.ThumbnailRetryDelay; PlaylistOptionsDialog.ThumbnailRetryDelay = TimeSpan.Zero;
        var h = new ThumbHandler { Reply = _ => failing ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) } };
        var big = YoutubePlaylists.Parse(string.Join("\n", Enumerable.Range(0, 200).Select(i => $"{{\"id\":\"{VId(i)}\",\"title\":\"Retry video {i}\",\"duration\":90,\"channel\":\"C\",\"playlist_title\":\"Big\",\"playlist_count\":200}}")));
        using var thumbs = new ThumbnailLoader(Path.Combine(scratch, "retry"), null, h);
        var d = NewPlaylistDialog(scratch, (u, t, l) => Task.FromResult(big), thumbs: thumbs, height: 700);
        Wait(d.ReadAsync()); Pump(500);
        var list = Part<ListBox>(d, "VideoList"); var first = d.Model!.Rows[1]; // not the selected row: a selected row keeps its container while scrolled away
        Check(first.Thumbnail is null && first.ThumbnailFailed, "a picture that cannot be had leaves the row without an image");
        var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(first);
        var none = Visual<System.Windows.Shapes.Path>(item).FirstOrDefault(p2 => p2.Name == "NoPicture");
        Check(none is not null && none.IsVisible && none.ActualWidth > 5, "the row shows the themed placeholder");
        int requests = h.Requests; failing = false;
        var sv = Visual<ScrollViewer>(list).First(); sv.ScrollToVerticalOffset(3000); Pump(400); sv.ScrollToVerticalOffset(0); Pump(900);
        Check(first.Thumbnail is not null && h.Requests > requests, "scrolling the row away and back asks again and the picture arrives");
        PlaylistOptionsDialog.ThumbnailRetryDelay = savedDelay; d.Close(); Pump(100);
    }

    private static void PlaylistRender(string scratch)
    {
        foreach (var theme in new[] { "Light", "DeepNavy" })
        foreach (var rtl in new[] { false, true })
        foreach (var size in new[] { (900.0, 760.0), (560.0, 520.0) })
        {
            ThemeManager.Apply(Application.Current, theme, null);
            var d = NewPlaylistDialog(scratch, (u, t, l) => Task.FromResult(SamplePlaylist()), width: size.Item1, height: size.Item2);
            d.FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight; Wait(d.ReadAsync()); Pump(150); d.UpdateLayout();
            var list = Part<ListBox>(d, "VideoList");
            Check(list.ActualHeight >= 100 && list.ActualWidth > 300, $"{theme} rtl={rtl} {size.Item1}: the list is visible and scales ({list.ActualWidth:0}x{list.ActualHeight:0})");
            var buttons = new[] { "StartButton", "QueueButton", "ScheduleButton", "CancelButton" }.Select(n => Part<Button>(d, n)).ToArray();
            Console.WriteLine($"  geometry {theme} {size.Item1}: window {d.ActualWidth:0}x{d.ActualHeight:0} list {list.ActualHeight:0} buttons {string.Join(" ", buttons.Select(b => b.TranslatePoint(new Point(b.ActualWidth, b.ActualHeight), d).ToString()))}");
            Check(buttons.All(b => b.IsVisible && b.ActualWidth > 0 && b.TranslatePoint(new Point(b.ActualWidth, b.ActualHeight), d).Y <= d.ActualHeight + 1 && b.TranslatePoint(new Point(0, 0), d).Y >= 0 && b.TranslatePoint(new Point(b.ActualWidth, 0), d).X <= d.ActualWidth + 1), $"{theme} rtl={rtl} {size.Item1}: all four buttons fit in the window");
            var first = Visual<CheckBox>(list).FirstOrDefault(); Check(first is not null && first.ActualWidth > 0, $"{theme} rtl={rtl}: rows have their checkbox");
            if (Environment.GetEnvironmentVariable("NEODM_SHOTS") is { Length: > 0 } folder && size.Item1 > 600)
            {
                Directory.CreateDirectory(folder); var bmp = new RenderTargetBitmap((int)Math.Ceiling(d.ActualWidth), (int)Math.Ceiling(d.ActualHeight), 96, 96, PixelFormats.Pbgra32); bmp.Render(d);
                var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp)); using var fs = File.Create(Path.Combine(folder, $"playlist-{theme}{(rtl ? "-rtl" : "")}.png")); enc.Save(fs);
            }
            d.Close();
        }
        ThemeManager.Apply(Application.Current, "Light", null);
    }

    private static void PartBLocales()
    {
        var repo = RepoRoot; var keys = new[] { "youtube.read", "youtube.read_hint", "youtube.step2", "youtube.download_now", "youtube.schedule", "youtube.stage.preparing", "youtube.stage.contacting", "youtube.stage.solving", "youtube.stage.reading", "youtube.stage.building", "youtube.stage.done", "youtube.progress_estimate", "youtube.load_cancel", "playlist.title", "playlist.read", "playlist.step2", "playlist.at_once", "playlist.audio_quality", "playlist.select_all", "playlist.invert", "playlist.row_quality", "playlist.override", "add.youtube_note", "add.youtube_note_playlist", "add.youtube_note_both", "youtube.ask_title", "options.field.playlist_videos_at_once" };
        foreach (var code in new[] { "en", "ar", "es", "fr", "de", "pt-BR", "ru", "tr", "zh-Hans", "ja" })
        {
            var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, "src", "NDM.App", "Locales", code + ".json"))).RootElement;
            var missing = keys.Where(k => !json.TryGetProperty(k, out var v) || v.GetString() is not { Length: > 0 }).ToArray();
            Check(missing.Length == 0, $"locale {code} has every Part B key" + (missing.Length > 0 ? ": missing " + string.Join(",", missing) : ""));
        }
    }
}
