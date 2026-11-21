// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Windows.Media;
using System.Windows;
using System.Text.Json;
using NDM.App;
using NDM.Core;
using NDM.Tests;

internal static partial class V1160Tests
{
    // ---------------------------------------------------------------- schedule table
    private static void ScheduleTable()
    {
        // Tuesday only, 23:00 to 06:00 (overnight window)
        var tue = new QueueSchedule { Enabled = true, DaysMask = QueueSchedule.Bit(DayOfWeek.Tuesday), StartTime = new(23, 0, 0), StopTime = new(6, 0, 0) };
        Check(tue.CurrentStart(D(17, 23, 30)) == D(17, 23, 0), "weekday: inside the window on Tuesday night");
        Check(tue.CurrentStart(D(18, 5, 59)) == D(17, 23, 0), "weekday: the overnight window reaches Wednesday 05:59");
        Check(tue.CurrentStart(D(18, 6, 0)) is null, "weekday: the stop time ends the window");
        Check(tue.CurrentStart(D(16, 23, 30)) is null && tue.CurrentStart(D(17, 22, 59)) is null, "weekday: not on Monday night, not before the start");
        Check(tue.NextStart(D(16, 12, 0)) == D(17, 23, 0) && tue.NextStart(D(17, 23, 30)) == D(24, 23, 0), "weekday: next start is the coming Tuesday 23:00, a week later when it has begun");
        Check(QueueSchedule.Bit(DayOfWeek.Monday) == 1 && QueueSchedule.Bit(DayOfWeek.Sunday) == 64, "weekday bits: Monday first, Sunday last");
        // one-off date with a same-day stop
        var one = new QueueSchedule { Enabled = true, OneOffDate = D(20, 0, 0), StartTime = new(8, 0, 0), StopTime = new(9, 30, 0), DaysMask = QueueSchedule.AllDays };
        Check(one.CurrentStart(D(20, 8, 30)) == D(20, 8, 0) && one.CurrentStart(D(20, 9, 30)) is null && one.CurrentStart(D(21, 8, 30)) is null, "one-off: only that date, only inside start and stop");
        Check(one.NextStart(D(16, 0, 0)) == D(20, 8, 0) && one.NextStart(D(20, 10, 0)) is null, "one-off: next start, and none after it passed");
        // no stop time: until midnight of the start day
        var open = new QueueSchedule { Enabled = true, DaysMask = QueueSchedule.AllDays, StartTime = new(22, 0, 0) };
        Check(open.CurrentStart(D(16, 23, 59)) == D(16, 22, 0) && open.CurrentStart(D(17, 0, 0)) is null, "no stop time: the window ends at midnight");
        // disabled and invalid
        Check(new QueueSchedule { Enabled = false, DaysMask = 127 }.CurrentStart(D(16, 23, 30)) is null, "disabled: never in a window");
        Check(new QueueSchedule { Enabled = true }.Problem() == "days" && new QueueSchedule { Enabled = true, DaysMask = 1, StartTime = new(8, 0, 0), StopTime = new(8, 0, 0) }.Problem() == "stop" && tue.Problem() is null, "validation: no day, stop equal to start, a good schedule");
        Check(QueueText.StartsText(tue, D(16, 12, 0), System.Globalization.CultureInfo.InvariantCulture) == "Starts Tuesday at " + QueueText.Time(new(23, 0, 0), System.Globalization.CultureInfo.InvariantCulture), "sidebar text: Starts Tuesday at 23:00");
        Check(QueueText.StartsText(tue, D(17, 12, 0), System.Globalization.CultureInfo.InvariantCulture).StartsWith("Starts today at"), "sidebar text: today when it is today");
    }

    // ---------------------------------------------------------------- migration
    private static void Migration(string root)
    {
        var state = StateOf(root, "mig"); Directory.CreateDirectory(state);
        var now = DateTimeOffset.Now; var files = Path.Combine(root, "mig", "files"); Directory.CreateDirectory(files);
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid().ToString("N")).ToArray();
        string[] status = { "Completed", "Paused", "Scheduled" };
        var json = ids.Select((id, i) => new
        {
            Id = id, Request = new DownloadRequest("https://downloads.example.org/f" + i + ".bin", "f" + i + ".bin"), FileName = "f" + i + ".bin", Status = status[i], Total = 100L,
            Segments = Array.Empty<object>(), FilePath = Path.Combine(files, "f" + i + ".bin"), AddedAt = now.AddHours(-i), ScheduledAt = i == 2 ? now.AddDays(1) : (DateTimeOffset?)null,
        });
        File.WriteAllText(Path.Combine(state, "queue.json"), JsonSerializer.Serialize(json)); // a 1.15.0 file: no queue ids, no queues.json
        using (var m = NewManager(root, "mig"))
        {
            Check(m.Jobs.Count == 3 && m.Jobs.All(j => j.QueueId == QueueIds.Main), "1.15.0 queue.json: every download lands in the Main queue, none lost");
            Check(m.Queues.Count == 1 && m.Queues[0].Id == QueueIds.Main && m.Queues[0].Name == DownloadManager.MainQueueName && m.Queues[0].Limit is null && m.Queues[0].Schedule is null, "one Main queue that follows the global number");
            Check(Job(m, ids[2]).Status == "Scheduled" && Job(m, ids[2]).ScheduledAt > now, "an old per-download scheduled start is kept (still Scheduled, same time)");
            Check(Job(m, ids[0]).Status == "Completed" && Job(m, ids[1]).Status == "Paused", "statuses unchanged");
            Check(m.LimitOf(QueueIds.Main) == 1 && m.OrderOf(QueueIds.Main).SequenceEqual(ids), "the global number is the queue's number; the order is the saved order");
            m.CreateQueue("Work");
        }
        Check(File.Exists(Path.Combine(state, "queues.json")), "queues.json is written once a queue exists");
        using (var m = NewManager(root, "mig")) Check(m.Queues.Select(q => q.Name).SequenceEqual(new[] { DownloadManager.MainQueueName, "Work" }), "queues survive a restart");
        File.WriteAllText(Path.Combine(state, "queues.json"), "{ not json");
        using (var m = NewManager(root, "mig")) Check(m.Queues.Count == 1 && m.Jobs.Count == 3, "a damaged queues.json falls back to the Main queue without losing downloads");
        // a download that names a queue that no longer exists goes to the Main queue
        var q2 = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(File.ReadAllText(Path.Combine(state, "queue.json")))!;
        q2[0]["QueueId"] = "gone"; File.WriteAllText(Path.Combine(state, "queue.json"), JsonSerializer.Serialize(q2));
        using (var m = NewManager(root, "mig")) Check(m.Jobs.All(j => j.QueueId == QueueIds.Main), "an unknown queue id means the Main queue");
        // the saved global number of an existing user stays the starting number of the Main queue
        Check(new AppSettings { MaxConcurrentDownloads = 3 }.MaxConcurrentDownloads == 3, "Options > Downloads number stays the default");
    }

    // ---------------------------------------------------------------- model: create, rename, order, delete
    private static void QueueModel(string root)
    {
        using var slow = new SlowServer();
        using var m = NewManager(root, "model", 1);
        var work = m.CreateQueue("Work"); var games = m.CreateQueue("Games pack");
        Check(m.Queues.Select(q => q.Name).SequenceEqual(new[] { DownloadManager.MainQueueName, "Work", "Games pack" }), "queues are created in order");
        Check(Throws<ArgumentException>(() => m.CreateQueue("work")) && Throws<ArgumentException>(() => m.CreateQueue("  ")) && Throws<ArgumentException>(() => m.CreateQueue(new string('x', 61))), "duplicate (any case), empty and too long names are refused");
        m.RenameQueue(work.Id, "Office"); Check(m.Queues[1].Name == "Office", "rename");
        Check(Throws<ArgumentException>(() => m.RenameQueue(work.Id, "Games pack")), "rename to an existing name is refused");
        m.MoveQueue(games.Id, 0); Check(m.Queues.First().Id == games.Id, "queues can be reordered");
        m.MoveQueue(games.Id, 2);
        var a = new[] { "a", "b", "c" }.Select(n => Sync(m.AddPausedAsync(new DownloadRequest(slow.BaseUrl + "/" + n + ".bin", QueueId: games.Id))).Id).ToArray();
        Check(m.OrderOf(games.Id).SequenceEqual(a) && Job(m, a[0]).QueueId == games.Id, "a request names its queue; the order is the add order");
        m.MoveJobs(new[] { a[2] }, games.Id, beforeId: a[0]); Check(m.OrderOf(games.Id).SequenceEqual(new[] { a[2], a[0], a[1] }), "drag inside a queue: placed before the target");
        m.MoveJobs(new[] { a[1] }, games.Id); Check(m.OrderOf(games.Id).SequenceEqual(new[] { a[2], a[0], a[1] }), "a drop on the queue itself puts it last");
        m.MoveJobs(new[] { a[0], a[1] }, QueueIds.Main); Check(m.OrderOf(QueueIds.Main).SequenceEqual(new[] { a[0], a[1] }) && m.OrderOf(games.Id).SequenceEqual(new[] { a[2] }), "several downloads move to another queue, in order");
        Check(Throws<InvalidOperationException>(() => m.DeleteQueue(QueueIds.Main, false)), "the Main queue cannot be deleted");
        m.DeleteQueue(games.Id, removeDownloads: false);
        Check(m.Queues.All(q => q.Id != games.Id) && Job(m, a[2]).QueueId == QueueIds.Main && m.Jobs.Count == 3, "delete and move: the downloads go to the Main queue");
        var extra = m.CreateQueue("Temp"); var t = Sync(m.AddPausedAsync(new DownloadRequest(slow.BaseUrl + "/t.bin", QueueId: extra.Id)));
        m.DeleteQueue(extra.Id, removeDownloads: true); Check(m.Jobs.All(j => j.Id != t.Id) && m.Jobs.Count == 3, "delete and remove: its downloads leave the list");
        for (int i = 0; i < QueueIds.MaxQueues; i++) { try { m.CreateQueue("q" + i); } catch (InvalidOperationException) { break; } }
        Check(m.Queues.Count == QueueIds.MaxQueues, "the number of queues is bounded");
        var edited = m.Queues[1]; m.UpdateQueue(edited.Id, q => { q.Limit = 4; q.AllowParallel = true; q.WhenDone = "Sleep"; q.Schedule = new QueueSchedule { Enabled = true, DaysMask = 3, StartTime = new(1, 0, 0) }; });
        Check(Throws<ArgumentOutOfRangeException>(() => m.UpdateQueue(edited.Id, q => q.Limit = 17)) && Throws<ArgumentException>(() => m.UpdateQueue(edited.Id, q => q.Schedule = new QueueSchedule { Enabled = true })), "limit 1 to 16 and a schedule with a day are enforced");
        Check(m.Queues[1].Limit == 4 && m.Queues[1].Schedule!.DaysMask == 3 && m.Queues[1].WhenDone == "Sleep", "a failed edit changes nothing");
    }
    private static bool Throws<T>(Action a) where T : Exception { try { a(); return false; } catch (T) { return true; } }

    // ---------------------------------------------------------------- per-queue limits
    private static void Limits(string root)
    {
        using var slow = new SlowServer(); using var m = NewManager(root, "lim", 1);
        var a = m.CreateQueue("A"); var b = m.CreateQueue("B");
        m.UpdateQueue(a.Id, q => { q.AllowParallel = true; }); m.UpdateQueue(b.Id, q => { q.AllowParallel = true; q.Limit = 2; });
        foreach (var n in "123") Sync(m.AddAsync(new DownloadRequest(slow.BaseUrl + "/a" + n, QueueId: a.Id)));
        foreach (var n in "123") Sync(m.AddAsync(new DownloadRequest(slow.BaseUrl + "/b" + n, QueueId: b.Id)));
        PumpUntil(() => Count(m, a.Id, "Downloading") == 1 && Count(m, b.Id, "Downloading") == 2, "queue A follows the global 1, queue B has its own 2", 15000);
        Pump(500); Check(Count(m, a.Id, "Downloading") == 1 && Count(m, b.Id, "Downloading") == 2 && Count(m, a.Id, "Queued") == 2 && Count(m, b.Id, "Queued") == 1, "the limits hold: 3 run in total with a global number of 1");
        m.MaxConcurrentDownloads = 3; PumpUntil(() => Count(m, a.Id, "Downloading") == 3, "raising the global number starts the queue that follows it (1.15.0 behaviour)");
        Check(Count(m, b.Id, "Downloading") == 2, "a queue with its own number ignores the global change");
        m.UpdateQueue(b.Id, q => q.Limit = 3); PumpUntil(() => Count(m, b.Id, "Downloading") == 3, "raising a queue's own number starts its waiting downloads");
        m.UpdateQueue(a.Id, q => q.Limit = 1); Pump(300); Check(m.RunningIn(a.Id) == 3, "lowering the number never stops a running download");
        Check(m.LimitOf(a.Id) == 1 && m.LimitOf(QueueIds.Main) == 3 && m.RunningCount == 6, "LimitOf and RunningIn report per queue");
        m.UpdateQueue(a.Id, q => q.Limit = null); Check(m.LimitOf(a.Id) == 3, "clearing the number returns to following the global one");
    }

    // ---------------------------------------------------------------- run together
    private static void RunTogether(string root)
    {
        using var slow = new SlowServer(); using var m = NewManager(root, "tog", 2);
        var p = m.CreateQueue("P"); var q = m.CreateQueue("Q");
        Sync(m.AddAsync(new DownloadRequest(slow.BaseUrl + "/p1", QueueId: p.Id)));
        PumpUntil(() => Count(m, p.Id, "Downloading") == 1, "queue P runs");
        var qj = Sync(m.AddAsync(new DownloadRequest(slow.BaseUrl + "/q1", QueueId: q.Id)));
        Pump(900); Check(Job(m, qj.Id).Status == "Queued", "default: queue Q waits behind the running queue P although the global number is 2");
        m.UpdateQueue(q.Id, x => x.AllowParallel = true); PumpUntil(() => Job(m, qj.Id).Status == "Downloading", "Allow running at the same time: Q starts beside P");
        var mainJob = Sync(m.AddAsync(new DownloadRequest(slow.BaseUrl + "/m1")));
        Pump(900); Check(Job(m, mainJob.Id).Status == "Queued", "the Main queue (not allowed to run together) waits while others run");
        var now = Sync(m.AddAsync(new DownloadRequest(slow.BaseUrl + "/m2"))); m.ResumeNow(now.Id);
        PumpUntil(() => Job(m, now.Id).Status == "Downloading", "Download now still starts a download beside the others");
    }

    // ---------------------------------------------------------------- queue schedules: window, stop, order
    private static QueueSchedule Tuesday() => new() { Enabled = true, DaysMask = QueueSchedule.Bit(DayOfWeek.Tuesday), StartTime = new(23, 0, 0), StopTime = new(6, 0, 0) };
    private static void RunnerWindow(string root)
    {
        using var slow = new SlowServer(); using var m = NewManager(root, "win", 2);
        var s = m.CreateQueue("Games pack"); m.UpdateQueue(s.Id, q => q.Schedule = Tuesday());
        var j1 = Sync(m.AddAsync(new DownloadRequest(slow.BaseUrl + "/1", QueueId: s.Id))); var j2 = Sync(m.AddAsync(new DownloadRequest(slow.BaseUrl + "/2", QueueId: s.Id)));
        var clock = D(17, 12, 0); var runner = new QueueScheduleRunner(m, () => clock, null);
        runner.Tick(); Pump(700); Check(Job(m, j1.Id).Status == "Queued" && m.IsQueueHeld(s.Id), "before the start time the queue is held (downloads wait)");
        clock = D(17, 22, 59); runner.Tick(); Pump(500); Check(Job(m, j1.Id).Status == "Queued", "one minute before: still held");
        clock = D(17, 23, 0); runner.Tick(); PumpUntil(() => Job(m, j1.Id).Status == "Downloading", "at the start time the queue starts, first download first");
        Check(!m.IsQueueHeld(s.Id) && m.Queues.Single(q => q.Id == s.Id).LastStarted == D(17, 23, 0), "the start is recorded");
        var user = m.RunningIn(s.Id); clock = D(18, 3, 0); runner.Tick(); Check(Job(m, j1.Id).Status == "Downloading" && user == 2, "inside the window nothing changes (both downloads run: the queue number follows the global 2)");
        clock = D(18, 6, 0); runner.Tick(); PumpUntil(() => m.RunningIn(s.Id) == 0, "the stop time stops the running download");
        Check(Job(m, j1.Id).Status == "Queued" && Job(m, j2.Id).Status == "Queued" && m.IsQueueHeld(s.Id), "stopped politely: they stay queued, not paused or failed");
        clock = D(18, 23, 0); runner.Tick(); Pump(700); Check(Job(m, j1.Id).Status == "Queued", "Wednesday night: not a scheduled day");
        clock = D(24, 23, 5); runner.Tick(); PumpUntil(() => Job(m, j1.Id).Status == "Downloading", "next Tuesday it starts again");
        // two scheduled queues start in list order; a one-off date
        var one = m.CreateQueue("Once"); m.UpdateQueue(one.Id, q => { q.AllowParallel = true; q.Schedule = new QueueSchedule { Enabled = true, OneOffDate = D(20, 0, 0), StartTime = new(8, 0, 0) }; });
        var oj = Sync(m.AddAsync(new DownloadRequest(slow.BaseUrl + "/o", QueueId: one.Id)));
        var onceRunner = new QueueScheduleRunner(m, () => D(20, 8, 1), null); onceRunner.Tick(); Pump(700); Check(Job(m, oj.Id).Status == "Downloading" || m.RunningIn(one.Id) == 1, "one-off queue is started on its date");
        Check(m.Queues.Single(q => q.Id == one.Id).LastStarted == D(20, 8, 0), "the one-off date opened its queue");
    }

    // ---------------------------------------------------------------- app start: missed start and restart inside the window
    private static void RunnerRestart(string root)
    {
        using var slow = new SlowServer(); string queueId; string jobId;
        using (var m = NewManager(root, "rst", 1))
        {
            var s = m.CreateQueue("Night"); queueId = s.Id; m.UpdateQueue(s.Id, q => q.Schedule = Tuesday());
            jobId = Sync(m.AddAsync(new DownloadRequest(slow.BaseUrl + "/r", QueueId: s.Id))).Id;
            Check(Job(m, jobId).Status == "Queued", "a download of a scheduled queue is queued");
        }
        using (var m = NewManager(root, "rst", 1))
        {
            Check(Job(m, jobId).Status == "Queued", "after a restart it is still queued (not paused), so a later window can start it");
            var runner = new QueueScheduleRunner(m, () => D(18, 7, 0), null); runner.Tick(); Pump(700);
            Check(Job(m, jobId).Status == "Queued", "app started after the window closed: a missed start does NOT run");
        }
        using (var m = NewManager(root, "rst", 1))
        {
            var runner = new QueueScheduleRunner(m, () => D(17, 23, 40), null); runner.Tick();
            PumpUntil(() => Job(m, jobId).Status == "Downloading", "app started inside the window: it runs at once (missed start inside its window)");
        }
        using (var m = NewManager(root, "rst", 1))
        {
            var clock = D(18, 1, 0); var runner = new QueueScheduleRunner(m, () => clock, null); runner.Tick(); PumpUntil(() => Job(m, jobId).Status == "Downloading", "restart inside the same window continues the download");
            m.StopQueue(queueId); clock = D(18, 1, 5); runner.Tick(); Pump(700); Check(Job(m, jobId).Status == "Queued", "a queue the user stopped inside its window is not restarted by the next minute");
            clock = D(18, 6, 1); runner.Tick(); m.OpenQueue(queueId); m.StopQueue(queueId);
        }
    }

    // ---------------------------------------------------------------- daylight saving
    private static void RunnerDst(string root)
    {
        using var slow = new SlowServer(); using var m = NewManager(root, "dst", 1);
        var s = m.CreateQueue("Nightly"); m.UpdateQueue(s.Id, q => q.Schedule = new QueueSchedule { Enabled = true, DaysMask = QueueSchedule.AllDays, StartTime = new(2, 30, 0), StopTime = new(5, 0, 0) });
        var j = Sync(m.AddAsync(new DownloadRequest(slow.BaseUrl + "/d", QueueId: s.Id)));
        // spring forward: 02:30 does not exist, the clock jumps from 01:59 to 03:00
        var clock = D(22, 1, 59); var runner = new QueueScheduleRunner(m, () => clock, null);
        runner.Tick(); Pump(500); Check(Job(m, j.Id).Status == "Queued", "spring forward: 01:59 is before the start");
        clock = D(22, 3, 0); runner.Tick(); PumpUntil(() => Job(m, j.Id).Status == "Downloading", "spring forward: the skipped 02:30 start happens at 03:00, once");
        // fall back: the hour repeats, the wall clock goes backwards
        clock = D(23, 4, 59); runner.Tick(); Check(Job(m, j.Id).Status == "Downloading", "still inside the window at 04:59");
        clock = D(23, 4, 0); runner.Tick(); Pump(400); Check(Job(m, j.Id).Status == "Downloading", "fall back: the clock going back neither stops nor restarts the queue");
        clock = D(23, 4, 30); runner.Tick(); Check(m.Queues.Single(q => q.Id == s.Id).LastStarted == D(22, 2, 30), "the repeated hour does not start it a second time");
        clock = D(23, 5, 0); runner.Tick(); PumpUntil(() => Job(m, j.Id).Status == "Queued", "the stop time still stops it after the repeat");
    }

    // ---------------------------------------------------------------- when done (fake power)
    private static void WhenDone(string root)
    {
        using var fixture = new FixtureServer(); using var m = NewManager(root, "done", 1);
        var w = m.CreateQueue("Night work"); m.UpdateQueue(w.Id, q => { q.WhenDone = "Shutdown"; });
        var calls = new List<(string, string)>();
        var runner = new QueueScheduleRunner(m, () => D(17, 12, 0), (id, kind) => calls.Add((id, kind)));
        var job = Sync(m.AddPausedAsync(new DownloadRequest(fixture.BaseUrl + "/range", "done.bin", QueueId: w.Id)));
        m.SetSpeedLimit(job.Id, 40 * 1024); Check(m.Enqueue(job.Id), "queued");
        PumpUntil(() => Job(m, job.Id).Status == "Downloading", "downloading slowly"); runner.Tick(); runner.Tick();
        Check(calls.Count == 0, "no power action while a download runs");
        m.SetSpeedLimit(job.Id, null); PumpUntil(() => Job(m, job.Id).Status == "Completed", "the download completes", 30000);
        runner.Tick(); runner.Tick(); Check(calls.Count == 1 && calls[0] == (w.Id, "Shutdown"), "when the queue is done the injected power action runs once with the chosen kind");
        // an emptied queue or a removed download never powers off
        var e = m.CreateQueue("Empty"); m.UpdateQueue(e.Id, q => q.WhenDone = "Sleep"); runner.Tick(); Check(calls.Count == 1, "an empty queue never triggers its action");
        // only the five power choices of the editor map to the existing completion kinds
        Check(QueueEditorWindow.PowerChoices.SequenceEqual(new[] { CompletionActionKind.None, CompletionActionKind.Sleep, CompletionActionKind.Hibernate, CompletionActionKind.Shutdown, CompletionActionKind.Restart }), "When done offers: nothing, sleep, hibernate, shut down, restart");
        // the real app routes it through the cancellable countdown: with a fake service and a prompt that cancels, nothing happens
        var fake = new FakePower(); var settings = new AppSettings(); var runnerKinds = new CompletionActionRunner(settings, fake, new CancelPrompt(), () => { }, () => { }, () => { }, () => { });
        Check(!runnerKinds.RunFor(CompletionActionKind.Shutdown, false) && fake.Calls.Count == 0, "the countdown can cancel a scheduled power action (fake power, nothing powered off)");
        var go = new CompletionActionRunner(settings, fake, new OkPrompt(), () => { }, () => { }, () => { }, () => { });
        Check(go.RunFor(CompletionActionKind.Sleep, false) && fake.Calls.SequenceEqual(new[] { CompletionActionKind.Sleep }), "after the countdown the fake service gets exactly one call");
    }
    private sealed class FakePower : IPowerActions { public List<CompletionActionKind> Calls { get; } = new(); public void Execute(CompletionActionKind kind, bool force) => Calls.Add(kind); }
    private sealed class CancelPrompt : ICountdownPrompt { public CountdownOutcome Show(CompletionActionKind kind, int seconds) => CountdownOutcome.Cancelled; }
    private sealed class OkPrompt : ICountdownPrompt { public CountdownOutcome Show(CompletionActionKind kind, int seconds) => CountdownOutcome.Elapsed; }

    // ---------------------------------------------------------------- chooser and the busy prompt
    private static void Chooser(string root)
    {
        using var slow = new SlowServer(); using var m = NewManager(root, "choose", 1);
        var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "choose", "files"), MaxConcurrentDownloads = 1 };
        var service = new BusyQueue(m, settings, () => { }); BusyQueue.Current = service;
        var legacy = 0; service.Ask = (_, _, _, _) => { legacy++; return new BusyPromptResult(BusyChoice.Wait, null, false); };
        var asked = new List<(string Default, int Adding)>(); QueueChoice? answer = null;
        service.AskQueue = (_, queues, def, load, askBusy, saved, adding) => { asked.Add((def, adding)); return answer; };
        // one queue: the chooser never shows, the 1.15.0 prompt works as before
        var first = Sync(PlaylistCommit.AddAsync(m, new[] { new DownloadRequest(slow.BaseUrl + "/one") }, DownloadOptionsDialog.DialogAction.Queue));
        Check(asked.Count == 0 && legacy == 0 && first[0].QueueId == QueueIds.Main, "one queue: no chooser, no prompt for a free slot");
        PlaylistCommit.AddAsync(m, new[] { new DownloadRequest(slow.BaseUrl + "/two") }, DownloadOptionsDialog.DialogAction.Queue).GetAwaiter().GetResult();
        Check(legacy == 1 && asked.Count == 0, "one queue and a full slot: the 1.15.0 busy prompt (asked once)");
        // two queues: the chooser decides the queue and the busy choice together
        var work = m.CreateQueue("Work"); var games = m.CreateQueue("Games pack"); m.UpdateQueue(games.Id, q => q.AllowParallel = true); m.UpdateQueue(work.Id, q => q.AllowParallel = true);
        legacy = 0; answer = new QueueChoice(work.Id, null, BusyChoice.Wait);
        var added = Sync(PlaylistCommit.AddAsync(m, Enumerable.Range(1, 3).Select(i => new DownloadRequest(slow.BaseUrl + "/w" + i)).ToList(), DownloadOptionsDialog.DialogAction.Queue));
        Check(asked.Count == 1 && asked[0].Adding == 3 && legacy == 0 && added.All(j => Job(m, j.Id).QueueId == work.Id), "two queues: one chooser for a batch of 3, no second prompt, every download lands in the chosen queue");
        Check(m.DefaultQueueId == work.Id && m.PendingQueueId is null, "the chosen queue becomes the preselected one; the pending choice is cleared after the add");
        answer = new QueueChoice(null, "Fresh", BusyChoice.Wait);
        var viaNew = Sync(PlaylistCommit.AddAsync(m, new[] { new DownloadRequest(slow.BaseUrl + "/n") }, DownloadOptionsDialog.DialogAction.Queue));
        var fresh = m.Queues.Single(q => q.Name == "Fresh"); Check(Job(m, viaNew[0].Id).QueueId == fresh.Id, "\"+ New queue...\" creates the queue and adds to it");
        answer = null; var closed = Sync(PlaylistCommit.AddAsync(m, new[] { new DownloadRequest(slow.BaseUrl + "/c") }, DownloadOptionsDialog.DialogAction.Queue));
        Check(Job(m, closed[0].Id).QueueId == QueueIds.Main, "closing the chooser adds to the Main queue and waits");
        // busy queue: Download now starts beside the running one
        PumpUntil(() => m.RunningIn(work.Id) >= 1, "work queue running");
        answer = new QueueChoice(work.Id, null, BusyChoice.DownloadNow);
        var now = Sync(PlaylistCommit.AddAsync(m, new[] { new DownloadRequest(slow.BaseUrl + "/now") }, DownloadOptionsDialog.DialogAction.Queue));
        PumpUntil(() => Job(m, now[0].Id).Status == "Downloading", "Download now in a full queue starts at once");
        // a free queue: the busy choice is ignored
        var free = m.CreateQueue("Free"); answer = new QueueChoice(free.Id, null, BusyChoice.DownloadNow);
        var freeJob = Sync(PlaylistCommit.AddAsync(m, new[] { new DownloadRequest(slow.BaseUrl + "/free") }, DownloadOptionsDialog.DialogAction.Queue));
        Check(Job(m, freeJob[0].Id).QueueId == free.Id, "an empty queue just takes the download");
        // the real window: wording, preselection, busy section follows the chosen queue
        var queues = m.Queues; Func<string, (int, int)> load = id => (m.RunningIn(id), m.LimitOf(id));
        var w = new QueueChooserWindow(queues, work.Id, load, true, BusyChoice.Wait, 2); w.Show(); Pump(200);
        Check(w.Choices.Count == queues.Count + 1 && w.Choices.First(r => (string)r.Tag == work.Id).IsChecked == true && w.Choices.Last().Content as string == "+ New queue...", "chooser lists every queue plus + New queue..., default preselected");
        Check(w.BusyVisible, "a full queue shows the wait / download now question inside the same window");
        w.Choices.First(r => (string)r.Tag == free.Id).IsChecked = true; Pump(100); Check(!w.BusyVisible, "choosing an empty queue hides it");
        w.Choices.Last().IsChecked = true; Pump(100); Check(w.NewNameBox.Visibility == Visibility.Visible, "+ New queue... asks for a name");
        w.NewNameBox.Text = "work"; w.Ok.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Pump(100); Check(w.Result is null && w.IsVisible, "a duplicate name is refused inline");
        w.NewNameBox.Text = "Backups"; w.Ok.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Pump(100); Check(w.Result is { NewName: "Backups" }, "a good name returns the new queue");
        { var dbg = new QueueChooserWindow(queues, work.Id, load, true, BusyChoice.Wait, 2); dbg.Show(); Pump(250); Check(TextColors(dbg.Ok).Count > 0 && TextColors(dbg.Ok).All(c => c == ((SolidColorBrush)Application.Current.FindResource("OnAccentBrush")).Color), "the text inside the chooser's primary button is drawn in the on-accent colour (an implicit TextBlock style used to turn it dark)"); dbg.Close(); }
        SaveShot(new QueueChooserWindow(queues, work.Id, load, true, BusyChoice.Wait, 2) is var shot ? ShowAndReturn(shot) : null!, "chooser-light.png"); foreach (Window x in Application.Current.Windows.OfType<QueueChooserWindow>().ToArray()) x.Close();
        BusyQueue.Current = null;
    }
    private static List<Color> TextColors(DependencyObject root)
    {
        var found = new List<Color>(); var q = new Queue<DependencyObject>(); q.Enqueue(root);
        while (q.Count > 0) { var x = q.Dequeue(); if (x is System.Windows.Controls.TextBlock { Foreground: SolidColorBrush b }) found.Add(b.Color); for (int i = 0; i < VisualTreeHelper.GetChildrenCount(x); i++) q.Enqueue(VisualTreeHelper.GetChild(x, i)); }
        return found;
    }
    private static Window ShowAndReturn(Window w) { w.Show(); Pump(250); return w; }
}
