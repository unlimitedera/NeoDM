using System.IO;
using System.Windows.Controls;
using NDM.App;
using NDM.Core;
using NDM.Tests;

/// <summary>1.3.3: the capture dialog's Save-to folder is the job's real folder (Start / Queue / Schedule), the auto path keeps the category rule,
/// bad folders keep the dialog open, the choice survives a restart, and an expired dialog cannot add a duplicate.</summary>
internal static partial class ManageTests
{
    private static void SaveFolderTests(string root, FixtureServer server)
    {
        var baseDir = Path.Combine(root, "savefolder"); var files = Path.Combine(baseDir, "files"); var state = Path.Combine(baseDir, "state");
        string Resolver(string name) => Path.Combine(files, Path.GetExtension(name).ToLowerInvariant() switch { ".zip" => "Compressed", ".pdf" => "Documents", _ => "General" });
        DownloadManager Open() => new(files, state, new DownloadOptions { RetryCount = 0, RequestTimeout = TimeSpan.FromSeconds(4), MinimumSegmentSize = 65536 }, Resolver);
        var m = Open();
        var list = new TrayWindowList();
        var opened = new List<DownloadOptionsDialog>();
        DownloadOptionsDialog Dialog(string url, string? browserName, bool probe = false)
        {
            var d = new DownloadOptionsDialog(url, browserName, null, null, files, null, probe ? ct => m.ProbeNameAsync(new DownloadRequest(url), ct) : null, Resolver);
            opened.Add(d); return d;
        }
        Task<bool> Show(DownloadOptionsDialog d) { var t = CaptureFlow.ShowAsync(d, list, bringToFront: false); Pump(100); return t; }
        void WaitDone(string id) => PumpUntil(() => m.Jobs.Single(j => j.Id == id).Status is "Completed" or "Failed", "download " + id);
        try
        {
            // ---- custom folder, Start now: exact folder, byte-identical file, nothing in the category folder
            var custom = Path.Combine(baseDir, "chosen", "start");
            var d = Dialog(server.BaseUrl + "/range", "a.zip");
            var t = Show(d);
            Check(Field<TextBox>(d, "PathInput").Text == Path.Combine(files, "Compressed") && d.PathIsAuto, "Dialog pre-fill: untouched path is the engine's category folder and counts as automatic");
            Field<TextBox>(d, "PathInput").Text = custom;
            Click(d, "StartButton");
            Check(t.Wait(3000) && t.Result && d.Request is { TargetDirectory: not null } && !d.PathIsAuto, "Custom folder typed: request carries it as TargetDirectory");
            var job = Sync(CaptureCommit.AddAsync(m, d))!;
            Check(job.FilePath == Path.Combine(custom, "a.zip"), "Start now: job path is exactly <chosen folder>\\<name> (not the category folder)");
            WaitDone(job.Id);
            Check(File.Exists(job.FilePath) && File.ReadAllBytes(job.FilePath).SequenceEqual(server.Payload) && !File.Exists(Path.Combine(files, "Compressed", "a.zip")), "Start now: file written in the chosen folder, byte-identical, none in the category folder");
            var doneId = job.Id;

            // ---- custom folder, Add to queue (typed with a trailing slash); name collision never overwrites
            var queued = Path.Combine(baseDir, "chosen", "queue"); Directory.CreateDirectory(queued);
            File.WriteAllText(Path.Combine(queued, "a.zip"), "precious");
            d = Dialog(server.BaseUrl + "/range", "a.zip"); t = Show(d);
            Field<TextBox>(d, "PathInput").Text = queued + Path.DirectorySeparatorChar;
            Click(d, "QueueButton");
            Check(t.Wait(3000) && t.Result, "Queue: dialog accepted");
            job = Sync(CaptureCommit.AddAsync(m, d))!;
            Check(Path.GetDirectoryName(job.FilePath) == queued && job.FileName == "a (1).zip", "Add to queue: chosen folder used, existing file gets a unique name for the new one");
            WaitDone(job.Id);
            Check(File.ReadAllText(Path.Combine(queued, "a.zip")) == "precious" && File.ReadAllBytes(job.FilePath).SequenceEqual(server.Payload), "Add to queue: existing file untouched, new copy byte-identical");

            // ---- custom folder, Schedule (created if missing); survives a restart
            var scheduled = Path.Combine(baseDir, "chosen", "sched", "deeper");
            d = Dialog(server.BaseUrl + "/range", "s.pdf"); t = Show(d);
            Field<TextBox>(d, "PathInput").Text = scheduled;
            Field<TextBox>(d, "ScheduleInput").Text = DateTime.Now.AddHours(3).ToString("yyyy-MM-dd HH:mm");
            Click(d, "ScheduleButton");
            Check(t.Wait(3000) && t.Result && d.UserAction == DownloadOptionsDialog.DialogAction.Schedule, "Schedule: dialog accepted");
            job = Sync(CaptureCommit.AddAsync(m, d))!;
            Check(job.Status == "Scheduled" && job.FilePath == Path.Combine(scheduled, "s.pdf") && Directory.Exists(scheduled), "Schedule: chosen folder used (created), job stays scheduled");
            var scheduledId = job.Id;
            m.Dispose(); m = Open();
            Check(m.Jobs.Single(j => j.Id == scheduledId).FilePath == Path.Combine(scheduled, "s.pdf") && m.Jobs.Single(j => j.Id == doneId).FilePath == Path.Combine(custom, "a.zip"), "Restart: scheduled and finished jobs keep their chosen folder");
            m.Schedule(scheduledId, DateTimeOffset.UtcNow); WaitDone(scheduledId);
            Check(File.Exists(Path.Combine(scheduled, "s.pdf")) && File.ReadAllBytes(Path.Combine(scheduled, "s.pdf")).SequenceEqual(server.Payload), "Restart: the restored job downloads into its chosen folder, byte-identical");

            // ---- auto path: engine's category rule, follows the detected name
            d = Dialog(server.BaseUrl + "/n/cd-plain", null, probe: true); t = Show(d);
            PumpUntil(() => d.ProbeTask.IsCompleted, "probe");
            Check(Field<TextBox>(d, "NameInput").Text == "report final.pdf" && Field<TextBox>(d, "PathInput").Text == Path.Combine(files, "Documents") && d.PathIsAuto, "Auto path: pre-fill refreshes to the category of the detected extension");
            Click(d, "StartButton"); Check(t.Wait(3000) && t.Result && d.Request is { TargetDirectory: null }, "Auto path: no TargetDirectory is sent (behaves exactly as before)");
            job = Sync(CaptureCommit.AddAsync(m, d))!; WaitDone(job.Id);
            Check(job.FilePath == Path.Combine(files, "Documents", "report final.pdf") && File.Exists(job.FilePath), "Auto path: file lands in the category folder");

            // ---- a custom folder survives the engine settling the final name
            var keep = Path.Combine(baseDir, "chosen", "keep");
            job = Sync(m.AddAsync(new DownloadRequest(server.BaseUrl + "/n/cd-plain", null, TargetDirectory: keep)));
            WaitDone(job.Id); job = m.Jobs.Single(j => j.Id == job.Id);
            Check(job.FileName == "report final (1).pdf" && job.FilePath == Path.Combine(keep, "report final (1).pdf") && File.Exists(job.FilePath), "Custom folder is kept when the engine settles the final name/extension (no jump to the category folder)");

            // ---- editing the path before the probe answers keeps the user's folder
            d = Dialog(server.BaseUrl + "/n/cd-plain", null, probe: true);
            Field<TextBox>(d, "PathInput").Text = keep; t = Show(d);
            PumpUntil(() => d.ProbeTask.IsCompleted, "probe 2");
            Check(Field<TextBox>(d, "PathInput").Text == keep && !d.PathIsAuto, "Edited path is not overwritten when the detected name changes");
            d.Close(); Pump(50);

            // ---- bad folders: error shown, dialog stays open, nothing added
            var blocker = Path.Combine(baseDir, "blocker.txt"); File.WriteAllText(blocker, "x");
            foreach (var bad in new[] { Path.Combine(blocker, "sub"), "relative\\folder" })
            {
                int before = m.Jobs.Count;
                d = Dialog(server.BaseUrl + "/range", "bad.zip"); t = Show(d);
                Field<TextBox>(d, "PathInput").Text = bad; Click(d, "StartButton"); Pump(100);
                Check(d.IsVisible && !t.IsCompleted && !d.Accepted && Field<TextBox>(d, "ValidationError").Text.Length > 0 && d.Request is null && m.Jobs.Count == before, "Non-writable / invalid folder '" + bad + "': message shown, dialog stays open, nothing added");
                d.Close(); Pump(50);
            }
            bool threw = false; int count = m.Jobs.Count;
            try { Sync(m.AddAsync(new DownloadRequest(server.BaseUrl + "/range", "x.zip", TargetDirectory: Path.Combine(blocker, "sub")))); } catch (IOException) { threw = true; }
            Check(threw && m.Jobs.Count == count, "Engine: an unusable TargetDirectory is a clear IOException and adds no job");

            // ---- expired dialog (browser side hung up): cannot add a duplicate
            d = Dialog(server.BaseUrl + "/range", "late.zip"); t = Show(d); count = m.Jobs.Count;
            d.MarkExpired("The browser stopped waiting."); Click(d, "StartButton"); Pump(100);
            Check(d.Expired && !Field<Button>(d, "StartButton").IsEnabled && !Field<Button>(d, "QueueButton").IsEnabled && !Field<Button>(d, "ScheduleButton").IsEnabled && !t.IsCompleted && m.Jobs.Count == count, "Expired dialog: actions disabled, nothing added");
            Check(Field<TextBox>(d, "ValidationError").Text == "The browser stopped waiting.", "Expired dialog explains why");
            d.Close(); Pump(50);
            Check(t.Wait(2000) && !t.Result, "Expired dialog closes as 'not accepted'");
        }
        finally { foreach (var o in opened) { try { o.Close(); } catch (Exception) { } } m.Dispose(); }
    }
}
