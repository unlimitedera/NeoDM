using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;
using NDM.Tests;

internal static partial class V1132Tests
{
    // ------------------------------------------------------------------ item 3: Open on an installer
    // Cause (reproduced with a real Process.Start of a downloaded .msi and .exe that carry the internet mark): Windows shows "Open File - Security Warning" from inside the
    // launch call and the call does not return until it is answered. The click handler ran it on the UI thread, so the window froze and the close came late or not at all.
    // Fix: the launch runs on a worker thread; success means "the request was accepted" (no error inside the grace period, or still waiting on a Windows prompt).
    /// <summary>The click is raised inside the dispatcher, as a real click is, so the handler's awaits resume on the UI thread.</summary>
    private static void ClickOpen(Window w) => Dispatcher.CurrentDispatcher.BeginInvoke(() => Field<Button>(w, "OpenButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)));
    private static Func<string> Notice(Window w) { var e = w.FindName("Notice"); return () => e is TextBox t ? t.Text : ((TextBlock)e!).Text; }
    private static void OpenInstaller(string root)
    {
        var priorPrompt = DownloadFileActions.Prompt; var priorLauncher = DownloadFileActions.Launcher; var priorGrace = DownloadFileActions.OpenGrace;
        DownloadFileActions.Prompt = (_, _, _) => new OpenConfirmResult(true, false); // the confirmation window itself is covered elsewhere
        using var server = new FixtureServer();
        var plain = new AppSettings { MarkDownloadedFiles = false };
        using var manager = new DownloadManager(Path.Combine(root, "o-files"), Path.Combine(root, "o-mstate"), OptionsHelper.CreateDownloadOptions(plain));
        DownloadJob Fetch(string name)
        {
            var added = manager.AddAsync(new(server.BaseUrl + "/range", name)).GetAwaiter().GetResult();
            var end = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < end && manager.Jobs.Single(j => j.Id == added.Id).Status is not ("Completed" or "Failed")) Thread.Sleep(30);
            var job = manager.Jobs.Single(j => j.Id == added.Id); Check(job.Status == "Completed", name + " downloaded"); return job;
        }
        try
        {
            var msi = Fetch("setup.msi");
            DownloadFileActions.OpenGrace = TimeSpan.FromMilliseconds(300);
            var release = new ManualResetEventSlim(false); var launched = new List<string>(); Exception? toThrow = null; TimeSpan block = TimeSpan.Zero;
            DownloadFileActions.Launcher = p => { launched.Add(p); if (block > TimeSpan.Zero) release.Wait(block); if (toThrow is not null) throw toThrow; };

            Window Complete(bool keep) { var w = new DownloadCompleteWindow(msi, new AppSettings { KeepCompletionWindowOpen = keep }, () => { }) { ShowInTaskbar = false }; w.Show(); Pump(); return w; }
            Window Progress(bool keep) { var w = new DownloadProgressWindow(manager, msi.Id, new JobCompletionStore(), new AppSettings { KeepCompletionWindowOpen = keep }) { ShowInTaskbar = false, Left = 30, Top = 30 }; w.Show(); Pump(200); return w; }
            var windows = new (string Name, Func<bool, Window> Make)[] { ("complete dialog", Complete), ("progress window (finished)", Progress) };
            foreach (var (name, make) in windows)
                foreach (var keep in new[] { false, true })
                {
                    string label = $"{name}, keep open {(keep ? "on" : "off")}";
                    // (a) the launch request is accepted at once
                    toThrow = null; block = TimeSpan.Zero; launched.Clear();
                    var w = make(keep); bool closed = false; w.Closed += (_, _) => closed = true;
                    ClickOpen(w); PumpUntil(() => launched.Count == 1, label + ": the file is launched"); Pump(150);
                    Check(closed == !keep, label + ": accepted launch " + (keep ? "keeps" : "closes") + " the window");
                    if (!closed) w.Close();
                    // (b) the launch waits on a Windows prompt (security warning, elevation): the UI stays alive and the request counts as accepted after the grace period
                    block = TimeSpan.FromSeconds(8); release.Reset(); launched.Clear();
                    w = make(keep); closed = false; w.Closed += (_, _) => closed = true;
                    int ticks = 0; var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) }; timer.Tick += (_, _) => ticks++; timer.Start();
                    ClickOpen(w); Pump(100);
                    Check(!closed && ticks >= 1, label + ": while Windows waits for an answer the window is not frozen (" + ticks + " timer ticks)");
                    if (!keep) PumpUntil(() => closed, label + ": the window closes once the request counts as accepted (still waiting on the prompt)", 4000);
                    else { Pump(600); Check(!closed, label + ": keep-open stays open"); }
                    timer.Stop(); release.Set(); Pump(100); if (!closed) w.Close();
                    // (c) the launch fails: the window stays with its message
                    block = TimeSpan.Zero; launched.Clear();
                    foreach (Exception failure in new Exception[] { new Win32Exception(1223, "The operation was canceled by the user"), new IOException("no program"), new InvalidOperationException("odd"), new System.Runtime.InteropServices.COMException("com", unchecked((int)0x80004005)) })
                    {
                        toThrow = failure; w = make(keep); closed = false; w.Closed += (_, _) => closed = true;
                        var shown = Notice(w);
                        ClickOpen(w); PumpUntil(() => shown().Length > 0, label + $": {failure.GetType().Name} shows a message", 4000); Pump(100);
                        Check(!closed, label + $": {failure.GetType().Name} keeps the window open"); w.Close();
                    }
                    toThrow = null;
                }
            release.Set();

            // The real shell, harmless targets: a program that exits at once (no internet mark) closes the window; a file Windows cannot run keeps it open with the message.
            DownloadFileActions.Launcher = priorLauncher;
            var exe = Fetch("quick.exe"); File.Copy(Path.Combine(Environment.SystemDirectory, "whoami.exe"), exe.FilePath, true);
            var wr = new DownloadCompleteWindow(exe, new AppSettings(), () => { }) { ShowInTaskbar = false }; wr.Show(); Pump(); bool realClosed = false; wr.Closed += (_, _) => realClosed = true;
            ClickOpen(wr); PumpUntil(() => realClosed, "real shell: opening a program that exits at once closes the complete dialog", 15000);
            var broken = Fetch("broken.exe"); // the fixture payload is not a program
            var wb = new DownloadCompleteWindow(broken, new AppSettings(), () => { }) { ShowInTaskbar = false }; wb.Show(); Pump(); bool brokenClosed = false; wb.Closed += (_, _) => brokenClosed = true;
            var brokenNotice = Notice(wb);
            ClickOpen(wb); PumpUntil(() => brokenNotice().Length > 0, "real shell: a file Windows cannot run shows the failure message", 15000); Pump(100);
            Check(!brokenClosed, "real shell: a failed open keeps the window open"); wb.Close();
        }
        finally { DownloadFileActions.Prompt = priorPrompt; DownloadFileActions.Launcher = priorLauncher; DownloadFileActions.OpenGrace = priorGrace; }
    }
}
