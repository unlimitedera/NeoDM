// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Security.Cryptography;
using NDM.Core;

namespace NDM.App;

/// <summary>2.0.0: the refreshes the app is waiting on. The app opens the download's page in the browser with a one-time code after the address; the extension recognises that tab,
/// asks the app (through the native host) whether the code is on this list, looks for the fresh address on the page and reports it. Only pages on this list are ever probed.</summary>
public sealed class RefreshLinkWatch
{
    public sealed class Entry
    {
        public required string Nonce, JobId, Page, Old;
        public long Until;
        public bool Contact, SignIn, NotFound;
        public NativeBridge.ExtensionRefreshReport? Found;
        public Action<string>? Note;
    }
    public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;
    private readonly List<Entry> _entries = new();
    public long UnixMs() => (long)(Now().ToUniversalTime() - DateTime.UnixEpoch).TotalMilliseconds;
    public Entry Register(string jobId, string page, string old, TimeSpan lifetime, Action<string>? note = null)
    {
        var entry = new Entry { Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), JobId = jobId, Page = page, Old = old, Until = UnixMs() + (long)lifetime.TotalMilliseconds, Note = note };
        lock (_entries) { _entries.RemoveAll(e => e.JobId == jobId); _entries.Add(entry); }
        return entry;
    }
    public void Remove(Entry entry) { lock (_entries) _entries.Remove(entry); }
    /// <summary>What the app answers the host with: one entry per refresh in progress.</summary>
    public IReadOnlyList<NativeBridge.RefreshWatchEntry> List()
    {
        lock (_entries)
        {
            long now = UnixMs(); _entries.RemoveAll(e => e.Until <= now);
            return _entries.Select(e => new NativeBridge.RefreshWatchEntry(e.Nonce, e.Page, e.Old, e.Until)).ToList();
        }
    }
    /// <summary>True when the report belongs to a refresh in progress.</summary>
    public bool Report(NativeBridge.ExtensionRefreshReport report)
    {
        lock (_entries)
        {
            var entry = _entries.FirstOrDefault(e => e.Until > UnixMs() && string.Equals(e.Nonce, report.Nonce, StringComparison.Ordinal));
            if (entry is null) return false;
            entry.Contact = true;
            switch (report.State)
            {
                case "signin": entry.SignIn = true; entry.Note?.Invoke("refresh: the page needs a sign-in"); break;
                case "notfound": entry.NotFound = true; entry.Note?.Invoke("refresh: nothing found on the page"); break;
                case "found": entry.Found = report; entry.SignIn = false; entry.Note?.Invoke("refresh: a fresh address arrived"); break;
            }
            return true;
        }
    }
}

public static class RefreshLinkHub { public static RefreshLinkWatch Watch { get; set; } = new(); }

public enum RefreshOutcome { Refreshed, NoPage, NoExtension, NeedsSignIn, NotFound, Cancelled, NotApplicable }

/// <summary>What the person sees while a refresh runs.</summary>
public enum RefreshStage { Opening, Reading, SignIn }

/// <summary>One refresh of an expired link: open the page, wait for the extension, give the SAME job the fresh address (parts kept) and resume it.</summary>
public sealed class RefreshLinkRun
{
    /// <summary>Opens the address in the person's browser. Tests record it.</summary>
    public Action<string>? Launcher { get; set; }
    /// <summary>How long the extension gets to answer first (no answer = not installed, not running, or too old).</summary>
    public TimeSpan ContactWait { get; set; } = TimeSpan.FromSeconds(40);
    /// <summary>How long the page gets in total (a sign-in takes time).</summary>
    public TimeSpan TotalWait { get; set; } = TimeSpan.FromMinutes(5);
    public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (t, c) => Task.Delay(t, c);
    public Action<string>? Note { get; set; }
    public RefreshLinkWatch Watch { get; set; } = RefreshLinkHub.Watch;

    /// <summary>True when the failure is one a fresh link can cure and the job knows its page.</summary>
    public static bool CanRefresh(DownloadJob job) =>
        job.Status == "Failed" && !job.IsYoutube && job.Stream == StreamKind.None && RefreshLink.ExpiryStatus(job.ErrorStatus)
        && job.ErrorCode == CompatibilityLadder.HttpStatusError && RefreshLink.CleanPage(job.PageUrl) is not null;

    public async Task<RefreshOutcome> RunAsync(DownloadManager manager, string jobId, Action<RefreshStage>? stage, CancellationToken token)
    {
        var job = manager.Jobs.FirstOrDefault(j => j.Id == jobId);
        if (job is null || job.Status == "Completed" || job.IsYoutube || job.Stream != StreamKind.None) return RefreshOutcome.NotApplicable;
        var page = RefreshLink.CleanPage(job.PageUrl);
        if (page is null) return RefreshOutcome.NoPage;
        var entry = Watch.Register(jobId, page, job.Url, ContactWait + TotalWait, Note);
        try
        {
            var open = RefreshLink.OpenAddress(page, entry.Nonce)!;
            try { Launcher?.Invoke(open); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException) { Note?.Invoke("refresh: the page could not be opened"); return RefreshOutcome.NoExtension; }
            stage?.Invoke(RefreshStage.Opening);
            var started = Now(); var lastStage = RefreshStage.Opening;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (entry.Found is { } found)
                {
                    if (!RefreshLink.Acceptable(job.Url, page, found.Url!)) { Note?.Invoke("refresh: the address was refused"); return RefreshOutcome.NotFound; }
                    manager.RefreshFromBrowser(jobId, new BrowserGrab(found.Url!, null, found.Referer ?? page, found.Cookie, found.UserAgent, found.Accept, found.AcceptLanguage));
                    manager.Resume(jobId);
                    return RefreshOutcome.Refreshed;
                }
                if (entry.NotFound) return RefreshOutcome.NotFound;
                var next = entry.SignIn ? RefreshStage.SignIn : entry.Contact ? RefreshStage.Reading : RefreshStage.Opening;
                if (next != lastStage) { lastStage = next; stage?.Invoke(next); }
                var elapsed = Now() - started;
                if (!entry.Contact && elapsed > ContactWait) return RefreshOutcome.NoExtension;
                if (elapsed > ContactWait + TotalWait || (entry.Contact && elapsed > TotalWait)) return entry.SignIn ? RefreshOutcome.NeedsSignIn : RefreshOutcome.NotFound;
                await Delay(TimeSpan.FromMilliseconds(250), token);
            }
        }
        catch (OperationCanceledException) { return RefreshOutcome.Cancelled; }
        finally { Watch.Remove(entry); }
    }
}
