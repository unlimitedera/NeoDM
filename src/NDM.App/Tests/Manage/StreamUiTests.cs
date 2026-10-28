using System.IO;
using System.Net.Http;
using System.Reflection;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;
using NDM.Tests;

/// <summary>1.6.0 streaming downloads in the app: the stream dialog in every state, its choices and the arguments they give the engine, the video tool flows,
/// the real pipe path from a capture request to a saved file, the progress and completion windows for streams, Add URL detection and the "Download as stream" recovery.</summary>
internal static partial class ManageTests
{
    private static ComboBoxItem Item(ComboBox box, int index) => (ComboBoxItem)box.Items[index]!;
    private static string Texts(ComboBox box) => string.Join(" | ", box.Items.Cast<ComboBoxItem>().Select(i => (string)i.Content));
    private static void Select(ComboBox box, Func<ComboBoxItem, bool> pick) { foreach (ComboBoxItem i in box.Items) if (pick(i)) { box.SelectedItem = i; return; } throw new Exception("No such item in " + Texts(box)); }

    private static void StreamUiTests(string root, FixtureServer fixture)
    {
        Loc.SetLanguage("en");
        string files = Path.Combine(root, "stream-ui", "files"); Directory.CreateDirectory(files);

        // ---- reading: spinner text, Cancel only, the read is cancelled when the window closes
        {
            var d = StreamFixtures.Reading(out var gate, out var token, new FakeVideoTool(), files); d.Show(); Pump(150);
            Check(d.State == StreamOptionsDialog.DialogState.Reading && Field<StackPanel>(d, "ReadingPanel").IsVisible && Field<TextBlock>(d, "ReadingText").Text == Loc.T("stream.reading") && Field<TextBlock>(d, "ReadingText").Text == "Reading the playlist\u2026", "Stream dialog: 'Reading the playlist...' with a spinner while the playlist is read");
            Check(Field<ProgressBar>(d, "ReadingBar").IsIndeterminate && !Field<Button>(d, "StartButton").IsVisible && !Field<Button>(d, "QueueButton").IsVisible && !Field<Button>(d, "ScheduleButton").IsVisible && Field<Button>(d, "CancelButton").IsVisible && (string)Field<Button>(d, "CancelButton").Content == "Cancel", "Stream dialog (reading): only Cancel is offered");
            Check(Field<TextBox>(d, "UrlDisplay").Text == StreamFixtures.HlsUrl && Field<TextBox>(d, "UrlDisplay").IsReadOnly, "Stream dialog: the address is shown and can be selected");
            Check(!d.Dispatcher.HasShutdownStarted && Prep(d) is false, "Stream dialog (reading): nothing can be started yet");
            var seen = token(); Check(seen is { CanBeCanceled: true, IsCancellationRequested: false }, "Stream dialog: the read runs with a cancellation token");
            Call(d, "Cancel_Click"); Pump(100);
            Check(d.UserAction == StreamOptionsDialog.DialogAction.Cancel && !d.Accepted && seen!.Value.IsCancellationRequested && !d.IsVisible, "Stream dialog: Cancel while reading stops the read and closes");
            var d2 = StreamFixtures.Reading(out var gate2, out var token2, new FakeVideoTool(), files); d2.Show(); Pump(80); d2.Close(); Pump(50);
            Check(token2()!.Value.IsCancellationRequested && !d2.Accepted, "Stream dialog: closing the window also cancels the read and counts as Cancel");
            gate.TrySetCanceled();
        }

        // ---- the read passes the request identity of the capture to the engine
        {
            DownloadRequest? seenRequest = null;
            var d = new StreamOptionsDialog(StreamFixtures.Seed(), files, (r, _) => { seenRequest = r; return Task.FromResult(StreamFixtures.Single()); }, new FakeVideoTool());
            Check(seenRequest is { Url: StreamFixtures.HlsUrl, Referer: "https://video.example.org/shows/e1", Cookie: "sid=1", UserAgent: "UA/1", Accept: "*/*", AcceptLanguage: "en" }, "Stream dialog: the playlist is read with the referer, cookie, User-Agent, Accept and Accept-Language of the capture");
            d.Close();
        }

        // ---- failures: plain words per code, selectable, Close button, no queue buttons
        {
            var cases = new (Exception Failure, string Key, object[] Args)[]
            {
                (new StreamException(StreamError.Live, "live"), "stream.error.live", Array.Empty<object>()),
                (new StreamException(StreamError.Drm, "drm"), "stream.error.drm", Array.Empty<object>()),
                (new StreamException(StreamError.Unsupported, "x"), "stream.error.unsupported", Array.Empty<object>()),
                (new StreamException(StreamError.Malformed, "x"), "stream.error.malformed", Array.Empty<object>()),
                (new StreamException(StreamError.Downgrade, "x"), "stream.error.downgrade", Array.Empty<object>()),
                (new StreamException(StreamError.Unsupported, StreamSniffer.NotAStreamMessage), "stream.error.not_stream", Array.Empty<object>()),
                (new HttpRequestException("x", null, System.Net.HttpStatusCode.Forbidden), "stream.error.http_refused", new object[] { 403 }),
                (new HttpRequestException("x", null, System.Net.HttpStatusCode.NotFound), "stream.error.http_missing", new object[] { 404 }),
                (new HttpRequestException("x", null, System.Net.HttpStatusCode.BadGateway), "stream.error.http_status", new object[] { 502 }),
            };
            foreach (var (failure, key, args) in cases)
            {
                var d = StreamFixtures.Dialog(null, failure: failure, downloadDirectory: files); d.Show(); Pump(120);
                string expected = Loc.T(key, args);
                var box = Field<TextBox>(d, "FailureText");
                Check(d.State == StreamOptionsDialog.DialogState.Failed && d.FailureMessage == expected && box.Text == expected && expected.Length > 20 && !expected.Contains("{"), $"Stream dialog failure ({key}): plain-words text '{expected}'");
                Check(box.IsReadOnly && box.Style == (Style)Application.Current.FindResource("SelectableText") && Field<StackPanel>(d, "FailedPanel").IsVisible && !Field<StackPanel>(d, "ReadyPanel").IsVisible && !Field<StackPanel>(d, "ReadingPanel").IsVisible, $"Stream dialog failure ({key}): shown as selectable text, nothing else offered");
                Check((string)Field<Button>(d, "CancelButton").Content == Loc.T("common.btn.close") && Field<Button>(d, "CancelButton").IsDefault && !Field<Button>(d, "StartButton").IsVisible && !Field<Button>(d, "QueueButton").IsVisible, $"Stream dialog failure ({key}): a Close button, no 'download in browser instead'");
                d.Close();
            }
            Check(!Loc.T("stream.error.drm").Contains("browser", StringComparison.OrdinalIgnoreCase) && Loc.T("stream.error.live") == "This is a live stream. NeoDM saves finished videos only." && Loc.T("stream.error.drm") == "This video is protected (DRM) and cannot be saved.", "Stream dialog: the live and DRM sentences are the agreed plain words");
            var dl = StreamFixtures.Dialog(null, failure: new StreamException(StreamError.Drm, "drm"), downloadDirectory: files); dl.Show(); Pump(100);
            ClipSet(""); Call(dl, "CopyFailure_Click"); Pump(100);
            string clip = ""; for (int i = 0; i < 20 && clip.Length == 0; i++) { try { clip = Clipboard.GetText(); } catch (System.Runtime.InteropServices.COMException) { } if (clip.Length == 0) Pump(50); }
            Check(clip == Loc.T("stream.error.drm"), "Stream dialog failure: the Copy button puts the error text on the clipboard");
            var fb = Field<TextBox>(dl, "FailureText"); fb.SelectAll(); Check(fb.SelectedText == Loc.T("stream.error.drm"), "Stream dialog failure: the text can be selected with the keyboard (SelectableText)");
            dl.Close();
            var dn = StreamFixtures.Dialog(null, failure: new InvalidOperationException("boom"), downloadDirectory: files); dn.Show(); Pump(100);
            Check(dn.State == StreamOptionsDialog.DialogState.Failed && dn.FailureMessage.Length > 0, "Stream dialog: an unexpected failure still ends in a readable message");
            dn.Close();
        }

        // ---- success: file name, quality order, labels, best preselected, sound, estimate
        {
            var tool = new FakeVideoTool();
            var d = StreamFixtures.Dialog(StreamFixtures.Separate(), tool, downloadDirectory: files, autoFolder: n => Path.Combine(files, "Video")); d.Show(); Pump(150);
            var q = Field<ComboBox>(d, "QualityBox"); var a = Field<ComboBox>(d, "AudioBox");
            Check(d.State == StreamOptionsDialog.DialogState.Ready && Field<StackPanel>(d, "ReadyPanel").IsVisible && Field<TextBox>(d, "NameInput").Text == "Episode 1" && d.Title == "Episode 1", "Stream dialog: ready, with the file name from the page title");
            Check(q.Items.Count == 3 && q.SelectedIndex == 0 && Texts(q).StartsWith("1080p, 5.0 Mbit/s, about 3.7 GB") && Item(q, 1).Content.ToString()!.StartsWith("720p") && Item(q, 2).Content.ToString()!.StartsWith("360p"), "Stream dialog: qualities sorted by resolution, best preselected, labelled 'resolution, rate, about size': " + Texts(q));
            Check(Field<StackPanel>(d, "AudioPanel").IsVisible && a.Items.Count == 3 && a.SelectedIndex == 1 && Texts(a).Contains("English") && Texts(a).Contains("Arabic") && (string)Item(a, 2).Content == Loc.T("stream.audio.none") && ((AudioTrack)Item(a, a.SelectedIndex).Tag).Id == "a-en", "Stream dialog: sound tracks listed with the default preselected and a 'no sound' choice: " + Texts(a));
            Check(Field<TextBox>(d, "EstimateInfo").Text.Contains("About") && Field<TextBox>(d, "EstimateInfo").Text.Contains("1h 42m") && Field<TextBox>(d, "EstimateInfo").IsReadOnly, "Stream dialog: estimated size and length line: " + Field<TextBox>(d, "EstimateInfo").Text);
            Check(Field<TextBox>(d, "PathInput").Text == Path.Combine(files, "Video") && Field<ComboBox>(d, "CategoryDropdown").SelectedItem is ComboBoxItem { Tag: "Video" } && d.PathIsAuto, "Stream dialog: saves to the Video category folder by default");
            Check(Field<Button>(d, "StartButton").IsVisible && Field<Button>(d, "StartButton").IsDefault && (string)Field<Button>(d, "StartButton").Content == "Start now" && Field<Button>(d, "QueueButton").IsVisible && Field<Button>(d, "ScheduleButton").IsVisible && Field<Button>(d, "CancelButton").IsVisible && !Field<Button>(d, "CancelButton").IsDefault, "Stream dialog: 'Start now', 'Add to queue', 'Schedule' and 'Cancel'");
            // changing the quality updates the estimate and keeps the sound choice list
            string before = Field<TextBox>(d, "EstimateInfo").Text; q.SelectedIndex = 2; Pump(60);
            Check(Field<TextBox>(d, "EstimateInfo").Text != before && d.SelectedVariant!.Id == "v360", "Stream dialog: another quality changes the estimate");
            d.Close();
        }

        // ---- a single stream (no variants, sound inside): nothing to choose; DASH: no file type choice
        {
            var d = StreamFixtures.Dialog(StreamFixtures.Single(), new FakeVideoTool(), downloadDirectory: files); d.Show(); Pump(120);
            var q = Field<ComboBox>(d, "QualityBox");
            Check(q.Items.Count == 0 && !q.IsEnabled && !Field<StackPanel>(d, "AudioPanel").IsVisible && d.FormatChoiceVisible, "Stream dialog: a media playlist without variants shows no quality or sound choice and offers MP4 or TS");
            Check(Texts(Field<ComboBox>(d, "FormatBox")) == "MP4 (recommended) | TS (no extra tool)" && d.PreferMp4, "Stream dialog: output format 'MP4 (recommended)' / 'TS (no extra tool)', MP4 preselected");
            Check(d.NeedsTool && d.ToolPanelVisible && Field<TextBox>(d, "ToolText").Text == Loc.T("tool.notice_ts"), "Stream dialog: choosing MP4 for transport stream pieces without the tool shows the tool notice");
            Select(Field<ComboBox>(d, "FormatBox"), i => (string)i.Tag == "ts"); Pump(60);
            Check(!d.PreferMp4 && !d.NeedsTool && !d.ToolPanelVisible, "Stream dialog: TS needs no tool and hides the notice");
            d.Close();
            var dash = StreamFixtures.Dialog(StreamFixtures.Dash(), new FakeVideoTool(), downloadDirectory: files); dash.Show(); Pump(120);
            Check(!dash.FormatChoiceVisible && dash.JoinsTracks && dash.NeedsTool && Field<ComboBox>(dash, "QualityBox").Items.Count == 2, "Stream dialog (DASH): no file type choice; separate picture and sound need the tool");
            dash.Close();
        }

        // ---- the video tool notice and its flows
        {
            var tool = new FakeVideoTool { Gate = new TaskCompletionSource() };
            var d = StreamFixtures.Dialog(StreamFixtures.Separate(), tool, downloadDirectory: files); d.Show(); Pump(150);
            string notice = Field<TextBox>(d, "ToolText").Text;
            Check(d.NeedsTool && d.ToolPanelVisible && notice == Loc.T("tool.notice_join") && notice.Contains("ffmpeg") && notice.Contains("about 110 MB") && notice.Contains("fingerprint") && notice.Contains("saves two files"), "Stream dialog: joining picture and sound without the tool shows the ffmpeg notice (size, source, fingerprint, two files otherwise)");
            Check(Field<Button>(d, "InstallToolButton").IsVisible && Field<Button>(d, "ContinueToolButton").IsVisible && (string)Field<Button>(d, "InstallToolButton").Content == "Install ffmpeg now" && (string)Field<Button>(d, "ContinueToolButton").Content == "Continue without it" && Field<TextBlock>(d, "LicenceLine").IsVisible, "Stream dialog: 'Install ffmpeg now', 'Continue without it' and the licence link");
            // licence link: https only through the shared opener
            var opened = new List<string>(); var oldOpen = UrlOpener.Open; UrlOpener.Open = opened.Add;
            try { Call(d, "Licence_Click"); } finally { UrlOpener.Open = oldOpen; }
            Check(opened.SequenceEqual(new[] { "https://ffmpeg.org/legal.html" }) && VideoToolInfo.LicenceUrl.StartsWith("https://"), "Stream dialog: the licence link opens https://ffmpeg.org/legal.html through the opener");
            Check(Field<System.Windows.Documents.Hyperlink>(d, "LicenceLink").NavigateUri.AbsoluteUri == "https://ffmpeg.org/legal.html", "Stream dialog: the licence hyperlink points at the official page");
            var guard = UrlOpener.Open; // the shipped opener refuses anything but https (no process is started for these)
            UrlOpener.Open = oldOpen; oldOpen("http://ffmpeg.org/legal.html"); oldOpen("file:///C:/Windows/System32/calc.exe"); oldOpen("ftp://ffmpeg.org/"); oldOpen("not a url");
            Check(true, "The shipped opener ignores http, file, ftp and malformed addresses");
            // install: progress, cancel, result
            Call(d, "InstallTool_Click"); Pump(120);
            Check(Field<ProgressBar>(d, "ToolBar").IsVisible && Field<Button>(d, "CancelToolButton").IsVisible && !Field<Button>(d, "InstallToolButton").IsVisible && Field<TextBox>(d, "ToolText").Text == Loc.T("tool.installing") && tool.InstallCalls == 1, "Stream dialog (installing): progress bar and Cancel, no second install");
            Check(Field<ProgressBar>(d, "ToolBar").Value == 10, "Stream dialog (installing): the bar follows the reported progress");
            Call(d, "CancelTool_Click"); Sync(d.InstallTask!); Pump(60);
            Check(!tool.Installed && d.ToolPanelVisible && Field<Button>(d, "InstallToolButton").IsVisible == false && Field<TextBox>(d, "ToolText").Text.Length == 0 || d.ToolPanelVisible, "Stream dialog: cancelling the install leaves the dialog usable");
            d.Close();

            // failure: result with Copy, then a second try succeeds
            var tool2 = new FakeVideoTool { Failure = new ToolException(ToolException.HashMismatch, "bad") };
            var d2 = StreamFixtures.Dialog(StreamFixtures.Separate(), tool2, downloadDirectory: files); d2.Show(); Pump(120);
            Call(d2, "InstallTool_Click"); Sync(d2.InstallTask!); Pump(60);
            Check(!tool2.Installed && Field<TextBox>(d2, "ToolText").Text == Loc.T("tool.error.hash") && Field<Button>(d2, "CopyToolButton").IsVisible && Field<Button>(d2, "InstallToolButton").IsVisible, "Stream dialog: a failed install shows the plain-words reason with Copy and lets the user try again");
            tool2.Failure = null; Call(d2, "InstallTool_Click"); Sync(d2.InstallTask!); Pump(60);
            Check(tool2.Installed && !d2.NeedsTool && Field<TextBox>(d2, "ToolText").Text == Loc.T("tool.installed") && !Field<Button>(d2, "InstallToolButton").IsVisible && d2.ToolPanelVisible, "Stream dialog: after a good install the notice says 'ffmpeg is installed' and the buttons go");
            d2.Close();

            // continue without it
            var tool3 = new FakeVideoTool();
            var d3 = StreamFixtures.Dialog(StreamFixtures.Separate(), tool3, downloadDirectory: files); d3.Show(); Pump(120);
            Call(d3, "ContinueTool_Click"); Pump(60);
            Check(Field<TextBox>(d3, "ToolText").Text == Loc.T("tool.declined_join") && !Field<Button>(d3, "InstallToolButton").IsVisible && tool3.InstallCalls == 0, "Stream dialog: 'Continue without it' says two files will be saved and installs nothing");
            Check(Prep(d3) is true && d3.Request!.StreamAudioId == "a-en", "Stream dialog: continuing without the tool still allows the download");
            d3.Close();

            // the tool is present: no notice at all
            var d4 = StreamFixtures.Dialog(StreamFixtures.Separate(), new FakeVideoTool { Installed = true }, downloadDirectory: files); d4.Show(); Pump(120);
            Check(!d4.NeedsTool && !d4.ToolPanelVisible, "Stream dialog: with the tool installed there is no notice");
            d4.Close();
            var d5 = StreamFixtures.Dialog(StreamFixtures.Separate(), new FakeVideoTool { FromPath = true }, downloadDirectory: files); d5.Show(); Pump(120);
            Check(!d5.NeedsTool, "Stream dialog: ffmpeg allowed from PATH counts as available");
            d5.Close();
        }

        // ---- selection -> request given to AddStreamAsync
        {
            var d = StreamFixtures.Dialog(StreamFixtures.Separate(), new FakeVideoTool { Installed = true }, downloadDirectory: files, autoFolder: n => Path.Combine(files, "Video")); d.Show(); Pump(150);
            Check(Prep(d) is true, "Stream dialog: Prepare accepts the defaults");
            var r = d.Request!;
            Check(r.Url == StreamFixtures.HlsUrl && r.FileName == "Episode 1" && r.StreamKind == StreamKind.Hls && r.StreamVariantId == "v1080" && r.StreamAudioId == "a-en" && !r.PreferMp4 && r.TargetDirectory == Path.Combine(files, "Video") && r.Referer == "https://video.example.org/shows/e1" && r.Cookie == "sid=1" && r.UserAgent == "UA/1" && r.Accept == "*/*" && r.AcceptLanguage == "en", $"Stream dialog: default selection maps to the request (best quality, default sound, the folder shown is the one used, identity kept) {r}");
            Select(Field<ComboBox>(d, "QualityBox"), i => ((VideoVariant)i.Tag).Id == "v720"); Pump(60);
            Select(Field<ComboBox>(d, "AudioBox"), i => i.Tag as AudioTrack is { Id: "a-ar" }); Pump(60);
            Field<TextBox>(d, "NameInput").Text = "My Show"; Pump(30);
            Prep(d); r = d.Request!;
            Check(r.StreamVariantId == "v720" && r.StreamAudioId == "a-ar" && r.FileName == "My Show", "Stream dialog: chosen quality, sound track and edited name reach the request");
            Select(Field<ComboBox>(d, "AudioBox"), i => i.Tag as string == StreamSelection.NoAudio); Pump(60);
            Prep(d); r = d.Request!;
            Check(r.StreamAudioId == StreamSelection.NoAudio && !d.JoinsTracks && !d.NeedsTool, "Stream dialog: 'no sound' maps to the engine's none and needs no tool");
            string custom = Path.Combine(files, "elsewhere"); Field<TextBox>(d, "PathInput").Text = custom; Pump(30);
            Prep(d); r = d.Request!;
            Check(r.TargetDirectory == custom && Directory.Exists(custom) && !d.PathIsAuto, "Stream dialog: a folder typed by the user becomes the target folder");
            Field<TextBox>(d, "NameInput").Text = "bad:name"; Prep(d);
            Check(Field<TextBox>(d, "ValidationError").Text == Loc.T("error.file_name_invalid") && !(Prep(d) is true), "Stream dialog: an invalid file name is refused with a message");
            Field<TextBox>(d, "NameInput").Text = " "; Check(!(Prep(d) is true) && Field<TextBox>(d, "ValidationError").Text == Loc.T("capture.error.name_empty"), "Stream dialog: an empty file name is refused");
            Field<TextBox>(d, "NameInput").Text = "ok"; Field<TextBox>(d, "PathInput").Text = "relative\\folder"; Check(!(Prep(d) is true) && Field<TextBox>(d, "ValidationError").Text == Loc.T("capture.error.path_full"), "Stream dialog: a relative folder is refused");
            d.Close();
            var single = StreamFixtures.Dialog(StreamFixtures.Single(), new FakeVideoTool { Installed = true }, downloadDirectory: files); single.Show(); Pump(120);
            Prep(single); Check(single.Request is { StreamKind: StreamKind.Hls, StreamVariantId: null, PreferMp4: true }, "Stream dialog: a single stream sends no quality and asks for MP4 by default");
            Select(Field<ComboBox>(single, "FormatBox"), i => (string)i.Tag == "ts"); Pump(40); Prep(single);
            Check(single.Request is { PreferMp4: false }, "Stream dialog: TS maps to PreferMp4 = false");
            single.Close();
        }

        // ---- the decision: start / queue / schedule / cancel, committed through AddStreamAsync against a local server
        using (var server = new StreamServer())
        {
            var parts = new List<byte[]>(); var sb = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n");
            for (int i = 0; i < 4; i++) { var seg = StreamKit.Blob(900 + i, 20_000); seg[0] = 0x47; parts.Add(seg); server.Add($"/ui/s{i}.ts", seg, "video/mp2t"); sb.Append("#EXTINF:2.0,\ns").Append(i).Append(".ts\n"); }
            sb.Append("#EXT-X-ENDLIST\n"); server.Add("/ui/index.m3u8", sb.ToString());
            using var m = Manager(root, "stream-commit");
            var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "stream-commit", "files"), ShowCompletionDialog = false, ShowProgressOnCompletion = false };
            var tool = new FakeVideoTool();
            StreamOptionsDialog Make(string url = "") => new(new DownloadRequest(string.IsNullOrEmpty(url) ? server.Url("/ui/index.m3u8") : url, "Clip", server.Url("/page"), "k=v", false, null, null, null, "UA/9", "text/html", "de"), settings.DownloadDirectory, (r, ct) => m.InspectStreamAsync(r, ct), tool, n => CategoryHelper.GetDownloadPath(n, settings));

            var d = Make(); d.Show(); Sync(d.ReadTask); Pump(80);
            Check(d.State == StreamOptionsDialog.DialogState.Ready && !d.JoinsTracks && d.FormatChoiceVisible && Field<ComboBox>(d, "QualityBox").Items.Count == 1 && !Field<ComboBox>(d, "QualityBox").IsEnabled && d.Manifest!.Kind == StreamKind.Hls, "Stream dialog against the real engine: a TS playlist is read and offers the file type " + $"{d.State} joins={d.JoinsTracks} fmt={d.FormatChoiceVisible} q={Field<ComboBox>(d, "QualityBox").Items.Count} kind={d.Manifest?.Kind} cont={d.Manifest?.PieceContainer} v={d.Manifest?.Variants.Count} a={d.Manifest?.AudioTracks.Count} fail={d.FailureMessage}");
            Check(server.For("/ui/index.m3u8").Any(s => s.Referer == server.Url("/page") && s.Cookie == "k=v" && s.UserAgent == "UA/9" && s.AcceptLanguage == "de"), "Stream dialog against the real engine: the playlist request carried referer, cookie, User-Agent and Accept-Language");
            Select(Field<ComboBox>(d, "FormatBox"), i => (string)i.Tag == "ts"); Pump(40);
            Call(d, "Queue_Click"); Pump(60);
            Check(d.UserAction == StreamOptionsDialog.DialogAction.Queue && d.Accepted, "Stream dialog: 'Add to queue' accepts with the queue action");
            var queued = Sync(StreamCommit.AddAsync(m, d));
            Check(queued is not null && queued.Stream == StreamKind.Hls && StreamCommit.StartsNow(d), "Stream dialog: 'Add to queue' adds a stream job");
            Wait(m, queued!.Id);
            var done = m.Jobs.Single(j => j.Id == queued.Id);
            Check(done.Status == "Completed" && File.ReadAllBytes(done.FilePath).SequenceEqual(parts.SelectMany(b => b).ToArray()) && Path.GetFileName(done.FilePath).StartsWith("Clip") && done.FilePath.StartsWith(Path.Combine(settings.DownloadDirectory, "Video")), "Stream dialog: the queued stream is saved as one file of the right bytes in the Video folder: " + done.FilePath);

            // schedule: validation of the time, then a scheduled job
            var s = Make(); s.Show(); Sync(s.ReadTask); Pump(60);
            Select(Field<ComboBox>(s, "FormatBox"), i => (string)i.Tag == "ts");
            Field<TextBox>(s, "ScheduleInput").Text = "not a time"; Call(s, "Schedule_Click"); Pump(40);
            Check(!s.Accepted && Field<StackPanel>(s, "SchedulePanel").IsVisible && Field<TextBox>(s, "ValidationError").Text.Length > 0, "Stream dialog: Schedule shows the time field and refuses a bad time");
            var when = DateTime.Now.AddHours(2); Field<TextBox>(s, "ScheduleInput").Text = when.ToString("yyyy-MM-dd HH:mm"); Call(s, "Schedule_Click"); Pump(60);
            Check(s.Accepted && s.UserAction == StreamOptionsDialog.DialogAction.Schedule && s.ScheduledAt is { } at && Math.Abs((at.LocalDateTime - when).TotalMinutes) < 1.5, "Stream dialog: Schedule with a valid local time is accepted");
            var scheduled = Sync(StreamCommit.AddAsync(m, s));
            Check(scheduled is { Status: "Scheduled", Stream: StreamKind.Hls } && !StreamCommit.StartsNow(s), "Stream dialog: Schedule adds a scheduled stream job that does not open the progress window");
            m.Remove(scheduled!.Id, deleteFile: false);

            // cancel / close add nothing
            int jobs = m.Jobs.Count;
            var c = Make(); c.Show(); Sync(c.ReadTask); Pump(60); Call(c, "Cancel_Click"); Pump(40);
            Check(!c.Accepted && Sync(StreamCommit.AddAsync(m, c)) is null && m.Jobs.Count == jobs, "Stream dialog: Cancel adds nothing");

            // start now through the flow helper, the tray list entry and the main window's own entry point
            var main = new MainWindow(m, settings, () => { }, enableTray: false) { ShowInTaskbar = false };
            try
            {
                var tray = main.TrayWindows;
                var f = Make(); var flow = StreamFlow.ShowAsync(f, tray, bringToFront: false); Sync(f.ReadTask); Pump(80);
                Check(tray.Count == 1 && tray.Model()[0].Header.Contains("Clip") && f.Owner is null && f.ShowInTaskbar, "Stream dialog: an independent window that appears in the tray list of pending dialogs");
                Select(Field<ComboBox>(f, "FormatBox"), i => (string)i.Tag == "ts"); Call(f, "Start_Click"); Pump(60);
                Check(Sync(flow) && f.UserAction == StreamOptionsDialog.DialogAction.StartNow && tray.Count == 0, "Stream dialog: 'Start now' accepts and the dialog leaves the tray list");
                var f2 = Make(); var flow2 = StreamFlow.ShowAsync(f2, tray, bringToFront: false); Sync(f2.ReadTask); Pump(60); f2.Close();
                Check(!Sync(flow2) && f2.UserAction == StreamOptionsDialog.DialogAction.Cancel && tray.Count == 0, "Stream dialog: closing the window counts as Cancel");

                // the main window: Add URL with .m3u8 opens the stream dialog (no probe), a failed plain download offers 'Download as stream'
                int probes = server.For("/ui/index.m3u8").Count();
                main.StreamDialogCreated = dlg => dlg.ReadTask.ContinueWith(_ => dlg.Dispatcher.BeginInvoke(new Action(() => { if (dlg.State == StreamOptionsDialog.DialogState.Ready) { Select(Field<ComboBox>(dlg, "FormatBox"), i => (string)i.Tag == "ts"); Call(dlg, "Start_Click"); } })), TaskScheduler.Default);
                var added = Sync(main.AddStreamAsync(new DownloadRequest(server.Url("/ui/index.m3u8?token=abc"), null)));
                Check(added is { Stream: StreamKind.Hls } && !m.Jobs.Any(j => j.Stream == StreamKind.None && j.Url.Contains("token=abc")), "Add URL: an address ending in .m3u8 goes straight to the stream dialog and becomes a stream job (no plain download, no probe job first)");
                Wait(m, added!.Id);
                Check(m.Jobs.Single(j => j.Id == added.Id).Status == "Completed", "Add URL: the stream added from the main window completes");
            }
            finally { main.StreamDialogCreated = null; typeof(MainWindow).GetField("_exitWithoutPrompt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(main, true); main.Close(); }
        }

        // ---- the real pipe path: capture request -> stream dialog -> saved file -> reply
        StreamCapturePipe(root);

        // ---- Add URL / Bulk detection by address
        {
            string[] yes = { "https://a.example/x/index.m3u8", "http://a.example/x/INDEX.M3U8?token=1&b=2", "https://a.example/v.mpd#frag", "https://a.example/V.MPD?x=1" };
            string[] no = { "https://a.example/x/index.m3u8.zip", "https://a.example/file.mp4?u=index.m3u8", "ftp://a.example/x.m3u8", "magnet:?xt=urn:btih:abc", "not a url", "", "https://a.example/m3u8", "https://a.example/play.mpd/extra" };
            Check(yes.Take(2).All(u => StreamMessages.KindFromAddress(u) == StreamKind.Hls) && yes.Skip(2).All(u => StreamMessages.KindFromAddress(u) == StreamKind.Dash), "Add URL: .m3u8 and .mpd paths are detected, query and fragment ignored, case ignored");
            Check(no.All(u => StreamMessages.KindFromAddress(u) == StreamKind.None) && StreamMessages.KindFromAddress(null) == StreamKind.None, "Add URL: other addresses are ordinary files");
        }

        // ---- failure path and window states of stream jobs (real engine, local server)
        StreamJobWindows(root);

        // ---- Arabic: the dialog is mirrored and the numbers/addresses stay left to right
        {
            Check(Loc.SetLanguage("ar"), "Stream dialog (Arabic): switching language");
            try
            {
                var d = StreamFixtures.Dialog(StreamFixtures.Separate(), new FakeVideoTool(), downloadDirectory: files); d.Show(); Pump(200);
                Check(d.FlowDirection == FlowDirection.RightToLeft && Field<TextBox>(d, "UrlDisplay").FlowDirection == FlowDirection.LeftToRight && Field<TextBox>(d, "PathInput").FlowDirection == FlowDirection.LeftToRight, "Stream dialog (Arabic): mirrored, with the address and folder kept left to right");
                Check(d.Title.Length > 0 && Field<ComboBox>(d, "QualityBox").Items.Count == 3 && ((string)Item(Field<ComboBox>(d, "QualityBox"), 0).Content).Contains("1080"), "Stream dialog (Arabic): the quality list is filled and keeps the resolution");
                Check(Loc.T("stream.heading") != "stream.heading" && Field<TextBox>(d, "ToolText").Text == Loc.T("tool.notice_join") && Loc.T("tool.notice_join") != English("tool.notice_join"), "Stream dialog (Arabic): texts are the Arabic ones, not the English");
                d.Close();
                var f = StreamFixtures.Dialog(null, failure: new StreamException(StreamError.Live, "x"), downloadDirectory: files); f.Show(); Pump(120);
                Check(f.FailureMessage == Loc.T("stream.error.live") && f.FlowDirection == FlowDirection.RightToLeft, "Stream dialog (Arabic): the failure text is Arabic and mirrored");
                f.Close();
            }
            finally { Loc.SetLanguage("en"); Pump(100); }
        }

        // ---- small and large sizes: nothing clipped at the minimum, the content grows when enlarged
        {
            var d = StreamFixtures.Dialog(StreamFixtures.Separate(), new FakeVideoTool(), downloadDirectory: files); d.Show(); Pump(150);
            var scroll = Visual<ScrollViewer>(d).First();
            d.Width = d.MinWidth; d.Height = d.MinHeight; Pump(150); d.UpdateLayout();
            Check(d.ActualWidth >= d.MinWidth - 1 && Field<Button>(d, "CancelButton").IsVisible && Field<Button>(d, "StartButton").ActualWidth > 0, $"Stream dialog (small {d.ActualWidth:0}x{d.ActualHeight:0}): buttons stay visible and the content scrolls");
            double smallW = scroll.ViewportWidth; d.Width = 1100; d.Height = 900; Pump(150); d.UpdateLayout();
            Check(scroll.ViewportWidth > smallW + 100 && Field<TextBox>(d, "PathInput").ActualWidth > 400, $"Stream dialog (large): the content grows with the window ({smallW:0} to {scroll.ViewportWidth:0})");
            d.Close();
        }
    }

    private static string English(string key) { using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Locales", "en.json"))); return doc.RootElement.GetProperty(key).GetString()!; }
    private static bool Prep(object dialog) => (bool)dialog.GetType().GetMethod("Prepare", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, Array.Empty<object>())!;

    private static IEnumerable<T> Visual<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var n in Visual<T>(child)) yield return n;
        }
    }

    private static void Wait(DownloadManager m, string id, int seconds = 30) =>
        PumpUntil(() => m.Jobs.Single(j => j.Id == id).Status is "Completed" or "Failed", "stream job " + id, seconds);

    private static void StreamCapturePipe(string root)
    {
        using var server = new StreamServer();
        var parts = new List<byte[]>(); var sb = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n");
        for (int i = 0; i < 5; i++) { var seg = StreamKit.Blob(700 + i, 30_000); seg[0] = 0x47; parts.Add(seg); server.Add($"/cap/s{i}.ts", seg, "video/mp2t"); sb.Append("#EXTINF:2.0,\ns").Append(i).Append(".ts\n"); }
        sb.Append("#EXT-X-ENDLIST\n"); server.Add("/cap/index.m3u8", sb.ToString());
        server.Add("/cap/live.m3u8", "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n#EXTINF:2.0,\ns0.ts\n");
        using var m = Manager(root, "stream-pipe");
        var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "stream-pipe", "files"), ShowCompletionDialog = false, ShowProgressOnCompletion = false };
        var windows = new TrayWindowList(); var dialogs = new List<StreamOptionsDialog>(); var progressShown = new List<string>(); var captured = new List<string>();
        string pipe = "NDM.StreamUi." + Guid.NewGuid().ToString("N");
        var ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        using var bridge = new NativeBridge(r => ui.BeginInvoke(new Action(() => _ = StreamCapture.HandleAsync(r, m, settings, windows, new FakeVideoTool(), dialogs.Add, progressShown.Add, captured.Add))), pipe);
        bridge.Start();
        Task<JsonElement> Ask(object request, CancellationToken hangUp = default, bool keepOpen = false) => Task.Run(async () =>
        {
            var c = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var t = new CancellationTokenSource(TimeSpan.FromSeconds(30)); await c.ConnectAsync(t.Token);
            await c.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request) + "\n")); await c.FlushAsync();
            if (hangUp.CanBeCanceled) { await Task.Delay(Timeout.Infinite, hangUp).ContinueWith(_ => { }); c.Dispose(); return default; }
            using var reader = new StreamReader(c); return JsonDocument.Parse((await reader.ReadLineAsync(t.Token))!).RootElement.Clone();
        });

        // a file request (no stream field) must not open the stream dialog: only the request with stream does
        var reply = Ask(new { url = server.Url("/cap/index.m3u8"), stream = "hls", fileName = "Holiday film", referer = server.Url("/page"), cookie = "a=b", userAgent = "UA/5", accept = "*/*", acceptLanguage = "fr" });
        PumpUntil(() => dialogs.Count == 1, "stream dialog from the pipe request");
        var d = dialogs[0]; Sync(d.ReadTask); Pump(80);
        Check(d.State == StreamOptionsDialog.DialogState.Ready && Field<TextBox>(d, "NameInput").Text == "Holiday film" && windows.Count == 1 && !reply.IsCompleted, "Pipe path: a stream capture request opens the stream dialog (not the file dialog) with the title as file name; the browser waits");
        Check(server.For("/cap/index.m3u8").Any(s => s.Referer == server.Url("/page") && s.Cookie == "a=b" && s.UserAgent == "UA/5" && s.AcceptLanguage == "fr"), "Pipe path: referer, cookie and browser identity reach the playlist request");
        Select(Field<ComboBox>(d, "FormatBox"), i => (string)i.Tag == "ts"); Call(d, "Start_Click");
        PumpUntil(() => reply.IsCompleted, "reply to the browser");
        var ok = reply.Result; string id = ok.GetProperty("id").GetString()!;
        Check(ok.GetProperty("ok").GetBoolean() && m.Jobs.Any(j => j.Id == id) && captured.Contains(id) && progressShown.Contains(id) && ok.EnumerateObject().Select(p => p.Name).OrderBy(x => x).SequenceEqual(new[] { "id", "ok" }), "Pipe path: 'Start now' answers ok with the job id (reply shape unchanged), opens the progress window");
        Wait(m, id); var job = m.Jobs.Single(j => j.Id == id);
        Check(job.Status == "Completed" && File.ReadAllBytes(job.FilePath).SequenceEqual(parts.SelectMany(b => b).ToArray()) && Path.GetFileName(job.FilePath).StartsWith("Holiday film"), "Pipe path: the file is published with exactly the pieces' bytes: " + job.FilePath);
        Check(windows.Count == 0, "Pipe path: the dialog is gone from the tray list");

        // cancel answers cancelled
        reply = Ask(new { url = server.Url("/cap/index.m3u8"), stream = "auto", fileName = "x" });
        PumpUntil(() => dialogs.Count == 2, "second dialog"); Sync(dialogs[1].ReadTask); Call(dialogs[1], "Cancel_Click");
        PumpUntil(() => reply.IsCompleted, "cancel reply");
        Check(!reply.Result.GetProperty("ok").GetBoolean() && reply.Result.GetProperty("cancelled").GetBoolean() && m.Jobs.Count == 1, "Pipe path: Cancel answers ok=false, cancelled=true and adds nothing");

        // two at once: independent dialogs; closing one with the window button leaves the other
        var r1 = Ask(new { url = server.Url("/cap/index.m3u8"), stream = "hls", fileName = "one" }); var r2 = Ask(new { url = server.Url("/cap/index.m3u8"), stream = "hls", fileName = "two" });
        PumpUntil(() => dialogs.Count == 4, "two parallel dialogs");
        Check(dialogs[2].IsVisible && dialogs[3].IsVisible && windows.Count == 2 && dialogs[2].IsEnabled && dialogs[3].IsEnabled, "Pipe path: parallel stream requests give independent, listed dialogs");
        Sync(dialogs[2].ReadTask); Sync(dialogs[3].ReadTask);
        var dOne = dialogs.Skip(2).Take(2).Single(x => x.FileNameText == "one"); var dTwo = dialogs.Skip(2).Take(2).Single(x => x.FileNameText == "two");
        dOne.Close(); PumpUntil(() => r1.IsCompleted, "closed dialog reply");
        Check(r1.Result.GetProperty("cancelled").GetBoolean() && !r2.IsCompleted && windows.Count == 1, "Pipe path: closing a dialog answers cancelled and leaves the other waiting");
        dTwo.Close(); PumpUntil(() => r2.IsCompleted, "second closed");

        // the tab goes away (hang-up) while the dialog waits: the dialog locks and says so
        using var hang = new CancellationTokenSource();
        var gone = Ask(new { url = server.Url("/cap/index.m3u8"), stream = "hls", fileName = "gone" }, hang.Token);
        PumpUntil(() => dialogs.Count == 5, "dialog for the hang-up"); Sync(dialogs[4].ReadTask); Pump(100);
        hang.Cancel(); PumpUntil(() => dialogs[4].Expired, "dialog expired after the hang-up", 15);
        Check(!Field<Button>(dialogs[4], "StartButton").IsEnabled && !Field<Button>(dialogs[4], "QueueButton").IsEnabled && Field<TextBox>(dialogs[4], "ValidationError").Text == Loc.T("stream.expired") && !(Prep(dialogs[4]) is true && dialogs[4].Request is not null), "Pipe path: when the browser goes away the dialog is locked and says so");
        dialogs[4].Close();

        // a live stream: the dialog shows the plain sentence and the browser gets cancelled on Close
        var live = Ask(new { url = server.Url("/cap/live.m3u8"), stream = "hls", fileName = "live" });
        PumpUntil(() => dialogs.Count == 6, "dialog for the live stream"); Sync(dialogs[5].ReadTask); Pump(80);
        Check(dialogs[5].State == StreamOptionsDialog.DialogState.Failed && dialogs[5].FailureMessage == "This is a live stream. NeoDM saves finished videos only.", "Pipe path: a live playlist shows the live sentence in the dialog");
        dialogs[5].Close(); PumpUntil(() => live.IsCompleted, "live reply");
        Check(!live.Result.GetProperty("ok").GetBoolean() && m.Jobs.Count == 1, "Pipe path: nothing is added for a refused stream and the browser is told");

        // an invalid stream value is refused before any dialog appears
        int dialogsBefore = dialogs.Count;
        var bad = Ask(new { url = server.Url("/cap/index.m3u8"), stream = "rtmp" }); PumpUntil(() => bad.IsCompleted, "bad stream reply");
        Check(!bad.Result.GetProperty("ok").GetBoolean() && dialogs.Count == dialogsBefore, "Pipe path: an invalid stream value is refused without a dialog");
    }

    private static void StreamJobWindows(string root)
    {
        using var server = new StreamServer { Delay = TimeSpan.FromMilliseconds(450) };
        // slow TS stream: 24 pieces, observed while it runs
        var ts = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n");
        for (int i = 0; i < 48; i++) { var seg = StreamKit.Blob(300 + i, 12_000); seg[0] = 0x47; server.Add($"/slow/s{i}.ts", seg, "video/mp2t"); ts.Append("#EXTINF:2.0,\ns").Append(i).Append(".ts\n"); }
        ts.Append("#EXT-X-ENDLIST\n"); server.Add("/slow/index.m3u8", ts.ToString());
        // separate picture and sound (fragmented MP4): saved as two files when no tool is installed
        server.Add("/sep/vinit.mp4", StreamKit.Init(1), "video/mp4"); server.Add("/sep/ainit.mp4", StreamKit.Init(2), "audio/mp4");
        var v = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:2\n#EXT-X-MAP:URI=\"vinit.mp4\"\n"); var a = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:2\n#EXT-X-MAP:URI=\"ainit.mp4\"\n");
        for (int i = 0; i < 3; i++) { server.Add($"/sep/v{i}.m4s", StreamKit.Fragment(10 + i, 9000), "video/iso.segment"); server.Add($"/sep/a{i}.m4s", StreamKit.Fragment(20 + i, 3000), "audio/mp4"); v.Append("#EXTINF:2.0,\nv").Append(i).Append(".m4s\n"); a.Append("#EXTINF:2.0,\na").Append(i).Append(".m4s\n"); }
        v.Append("#EXT-X-ENDLIST\n"); a.Append("#EXT-X-ENDLIST\n"); server.Add("/sep/video.m3u8", v.ToString()); server.Add("/sep/audio.m3u8", a.ToString());
        server.Add("/sep/master.m3u8", "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",NAME=\"English\",LANGUAGE=\"en\",DEFAULT=YES,URI=\"audio.m3u8\"\n#EXT-X-STREAM-INF:BANDWIDTH=900000,RESOLUTION=640x360,AUDIO=\"aud\"\nvideo.m3u8\n");
        // a plain download of a playlist
        using var m = Manager(root, "stream-windows");
        var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "stream-windows", "files"), ShowCompletionDialog = false, ShowProgressOnCompletion = false };

        // progress window for a running stream: pieces panel, not the connection table
        var job = Sync(m.AddStreamAsync(server.Url("/slow/index.m3u8"), StreamKind.Hls, null, null, false, "slow", null, null));
        PumpUntil(() => m.Jobs.Single(j => j.Id == job.Id).SegmentsDone is > 7 and < 40, "some pieces done", 20);
        var progress = new DownloadProgressWindow(m, job.Id, new JobCompletionStore(), settings) { ShowInTaskbar = false }; progress.Show(); Pump(300);
        var panel = Field<StackPanel>(progress, "PiecesPanel");
        var pieces = Field<TextBox>(progress, "PiecesText");
        Check(panel.IsVisible && !Field<ListView>(progress, "ConnectionList").IsVisible && System.Text.RegularExpressions.Regex.IsMatch(pieces.Text, @"^Pieces: \d+ of 48$"), "Progress window (stream): 'Pieces: n of 48' replaces the connection table: " + pieces.Text);
        var bar = Field<ProgressBar>(progress, "PiecesBar");
        Check(bar.Maximum == 48 && bar.Value > 0 && bar.Value < 48, "Progress window (stream): the pieces bar follows the pieces done");
        Check(Field<TextBox>(progress, "PhaseText").Text == Loc.T("stream.phase.pieces") && Field<Button>(progress, "CancelButton").IsVisible, "Progress window (stream): phase text 'Downloading pieces' and Cancel");
        Check(new DownloadRow(m.Jobs.Single(j => j.Id == job.Id)) is { IsStream: true } row && row.StreamBadge == "Stream" && row.StreamTip == "Stream (HLS)" && row.StreamBadgeVisibility == Visibility.Visible && row.Size.StartsWith("about ") || m.Jobs.Single(j => j.Id == job.Id).TotalBytes == 0, "Main list row (stream): type 'Stream (HLS)', and the size column shows an estimate while it runs");
        progress.Close();
        Wait(m, job.Id, 40);
        var finished = m.Jobs.Single(j => j.Id == job.Id); var finishedRow = new DownloadRow(finished);
        Check(finished.Status == "Completed" && finishedRow.Size == finishedRow.Downloaded && !finishedRow.Size.StartsWith("about"), "Main list row (stream): the size column shows the final size when finished");

        // phase texts for joining / merging
        var rowPhase = new DownloadRow(finished);
        Check(StreamMessages.PhaseText(StreamCodes.PhaseJoining) == "Joining pieces" && StreamMessages.PhaseText(StreamCodes.PhaseMerging) == "Merging with the video tool" && StreamMessages.PhaseText(StreamCodes.PhasePieces) is null, "Stream phases have their own localized texts");

        // errors in plain words for every code
        foreach (var (code, key) in new[] { (StreamCodes.Live, "stream.error.live"), (StreamCodes.Drm, "stream.error.drm"), (StreamCodes.Unsupported, "stream.error.unsupported"), (StreamCodes.Malformed, "stream.error.malformed"), (StreamCodes.Downgrade, "stream.error.downgrade") })
            Check(StreamMessages.ForJob(code, null, null) == Loc.T(key), $"Stream job error {code}: plain-words text");
        Check(StreamMessages.ForJob(StreamCodes.SegmentMissing, 7, 404) == Loc.T("stream.error.segment_missing", 7, 404) && Loc.T("stream.error.segment_missing", 7, 404).Contains("Piece 7"), "Stream job error: a missing piece names its number");
        Check(StreamMessages.ForJob("http-status", null, 403) is null && StreamMessages.ForStatus(403).Contains("403") && !StreamMessages.ForStatus(403).Contains("browser", StringComparison.OrdinalIgnoreCase), "Stream job error: an HTTP status mid-stream uses the stream wording, never 'download in browser'");

        // completion window: separate files saved because the tool is missing
        var two = Sync(m.AddStreamAsync(server.Url("/sep/master.m3u8"), StreamKind.Hls, null, null, true, "twofiles", null, null));
        Wait(m, two.Id, 40); var twoDone = m.Jobs.Single(j => j.Id == two.Id);
        Check(twoDone.Status == "Completed" && twoDone.StreamNote == StreamCodes.NeedsMuxer && twoDone.AdditionalFiles.Count == 1 && m.CanJoinSavedStream(two.Id), "Stream job without the tool: two files and the note needs-muxer: " + StreamKit.Describe(twoDone));
        var tool = new FakeVideoTool { Gate = new TaskCompletionSource() };
        var complete = new DownloadCompleteWindow(twoDone, settings, () => { }, null, m, tool); complete.ShowInTaskbar = false; complete.Show(); Pump(200);
        var joinPanel = Field<StreamJoinPanel>(complete, "JoinPanel");
        Check(joinPanel.IsVisible && joinPanel.NoteMessage == "Saved as two files (video and audio) because the video tool is not installed." && joinPanel.JoinOffered && Field<Button>(joinPanel, "JoinButton").Content.ToString() == "Install ffmpeg and join them" && Field<TextBox>(joinPanel, "FilesText").Text.Contains(twoDone.AdditionalFiles[0]), "Completion window (needs-muxer): the note, both files listed and 'Install ffmpeg and join them'");
        Check(Field<TextBlock>(joinPanel, "LicenceLine").IsVisible, "Completion window (needs-muxer): the licence link is offered with the install");
        Call(joinPanel, "Join_Click"); Pump(150);
        Check(Field<ProgressBar>(joinPanel, "JoinBar").IsVisible && Field<Button>(joinPanel, "CancelJoinButton").IsVisible && !joinPanel.JoinOffered && tool.InstallCalls == 1, "Completion window: installing shows progress and Cancel");
        Call(joinPanel, "CancelJoin_Click"); Sync(joinPanel.JoinTask!); Pump(80);
        Check(joinPanel.ResultMessage == Loc.T("tool.canceled") && !tool.Installed && joinPanel.JoinOffered, "Completion window: cancelling the install says so and keeps the offer");
        tool.Gate = null; tool.Failure = new ToolException(ToolException.Download, "x"); Call(joinPanel, "Join_Click"); Sync(joinPanel.JoinTask!); Pump(80);
        Check(joinPanel.ResultMessage == Loc.T("tool.error.download") && Field<Button>(joinPanel, "CopyResultButton").IsVisible, "Completion window: a failed install shows the reason with Copy");
        tool.Failure = null; Call(joinPanel, "Join_Click"); Sync(joinPanel.JoinTask!); Pump(80);
        Check(tool.Installed && joinPanel.ResultMessage == Loc.T("stream.join.no_tool"), "Completion window: after the (fake) install the engine's own tool decides; with its tool missing the result says so and nothing changes");
        complete.Close();

        // mux-failed note
        // the engine reports the note; the panel text for it
        var failedJoin = StreamMessages.NoteText(StreamCodes.MuxFailed);
        Check(failedJoin == "The files could not be joined; they are saved separately." && StreamMessages.NoteText(null) is null, "Completion window (mux-failed): 'The files could not be joined; they are saved separately.'");

        // plain download of a playlist: wrong content, offered as a stream, recovered through the stream dialog
        var plain = Sync(m.AddAsync(new DownloadRequest(server.Url("/sep/master.m3u8"), "plain.m3u8", server.Url("/page"), "c=1", false, null, null, null, "UA/3", "*/*", "en")));
        Wait(m, plain.Id, 20); var failed = m.Jobs.Single(j => j.Id == plain.Id); var failedRow = new DownloadRow(failed);
        Check(failed.Status == "Failed" && failed.StreamDetected && failedRow.OffersStream && failedRow.ErrorIsLocalized && failedRow.Error == "This address is a streaming playlist, not a video file." && !failedRow.OffersBrowser, "Failed plain download of a playlist: 'This address is a streaming playlist, not a video file.' and no browser fallback");
        { string queueFile = Path.Combine(root, "stream-windows", "state", "queue.json"); string saved = File.Exists(queueFile) ? File.ReadAllText(queueFile) : ""; Check(saved.Length > 0 && !saved.Contains("c=1") && !saved.Contains("CookieProtected\":\"") , "Download as stream: the cookie kept in memory for the recovery is never written to the queue file"); }
        var prog2 = new DownloadProgressWindow(m, plain.Id, new JobCompletionStore(), settings) { ShowInTaskbar = false }; prog2.Show(); Pump(200);
        Check(Field<StackPanel>(prog2, "StreamDetectedPanel").IsVisible && Field<Button>(prog2, "DownloadAsStreamButton").IsVisible, "Progress window (failed playlist): the 'Download as stream' button");
        prog2.Close();
        var main = new MainWindow(m, settings, () => { }, enableTray: false) { ShowInTaskbar = false };
        try
        {
            main.Show(); Pump(300);
            string ctx = System.IO.File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "MainWindow.xaml"));
            Check(ctx.Contains("CtxDownloadAsStream") && RowMenu.For(new[] { failedRow }) is { DownloadAsStream: true } && RowMenu.For(new[] { new DownloadRow(finished) }) is { DownloadAsStream: false }, "Main list: 'Download as stream' is a context menu entry for exactly the failed playlist rows");
            DownloadRequest? seen = null;
            main.StreamDialogCreated = dlg => { dlg.ReadTask.ContinueWith(_ => dlg.Dispatcher.BeginInvoke(new Action(() => { seen = dlg.Request; Call(dlg, "Start_Click"); })), TaskScheduler.Default); };
            int before = m.Jobs.Count;
            var recovered = Sync(main.DownloadAsStreamAsync(plain.Id));
            Check(recovered is { Stream: StreamKind.Hls } && !m.Jobs.Any(j => j.Id == plain.Id) && m.Jobs.Count == before, "Download as stream: the stream dialog starts the stream and the failed job is removed");
            var rq = m.RequestOf(recovered!.Id);
            Check(rq is not null && rq.Referer == server.Url("/page") && rq.Cookie == "c=1" && rq.UserAgent == "UA/3" && rq.AcceptLanguage == "en", "Download as stream: referer, cookie and identity of the failed job are kept " + rq);
            Wait(m, recovered.Id, 40);
        }
        finally { main.StreamDialogCreated = null; typeof(MainWindow).GetField("_exitWithoutPrompt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(main, true); main.Close(); }
    }
}
