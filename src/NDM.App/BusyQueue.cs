using System.IO;
using System.Windows;
using NDM.Core;

namespace NDM.App;

public enum BusyChoice { Wait, DownloadNow }

/// <summary>What the busy-queue prompt returned. NewLimit is set when the user pressed Apply on the "Downloads at once" box.</summary>
public sealed record BusyPromptResult(BusyChoice Choice, int? NewLimit, bool Remember);

/// <summary>
/// 1.15.0: the question asked when a download is added to the queue while the running downloads already fill every slot.
/// Only user actions (Add to queue in a dialog, row menu, link grabber, playlist) go through <see cref="BeforeAddAsync"/>; scheduled starts,
/// auto-resume at startup and the clipboard watcher never call it and use the saved settings. With no service installed (tests, headless) nothing is asked.
/// </summary>
public sealed class BusyQueue
{
    /// <summary>Installed by the app at startup; null = never ask.</summary>
    public static BusyQueue? Current { get; set; }
    /// <summary>Shows the prompt (owner, running, adding, limit); tests replace it. Returns null when the window was closed (= wait in the queue, nothing remembered).</summary>
    public Func<Window?, int, int, int, BusyPromptResult?> Ask { get; set; } = BusyQueuePrompt.Show;

    private readonly DownloadManager manager; private readonly AppSettings settings; private readonly Action save;
    public BusyQueue(DownloadManager manager, AppSettings settings, Action save) { this.manager = manager; this.settings = settings; this.save = save; }

    /// <summary>The trigger table: ask only when something is being added, the user wants to be asked, and every slot is taken.</summary>
    public static bool ShouldAsk(bool askSetting, int running, int limit, int adding) => adding > 0 && askSetting && running >= limit;
    public static BusyChoice Saved(AppSettings s) => s.BusyQueueAction == "now" ? BusyChoice.DownloadNow : BusyChoice.Wait;

    /// <summary>Decides for <paramref name="count"/> downloads about to be queued (one question for a whole batch). Call before adding; on the UI thread.</summary>
    public BusyChoice BeforeAddAsyncSync(int count, Window? owner = null)
    {
        int running = manager.RunningCount, limit = manager.MaxConcurrentDownloads;
        if (count <= 0 || running < limit) return BusyChoice.Wait; // a free slot: the queue starts it by itself
        if (!settings.AskWhenQueueBusy) return Saved(settings);
        BusyPromptResult? result;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess()) result = dispatcher.Invoke(() => Ask(owner ?? ActiveWindow(), running, count, limit));
        else result = Ask(owner ?? ActiveWindow(), running, count, limit);
        if (result is null) return BusyChoice.Wait;
        if (result.NewLimit is { } n && n is >= 1 and <= 16) { settings.MaxConcurrentDownloads = n; manager.MaxConcurrentDownloads = n; }
        if (result.Remember) { settings.AskWhenQueueBusy = false; settings.BusyQueueAction = result.Choice == BusyChoice.DownloadNow ? "now" : "wait"; }
        if (result.NewLimit is not null || result.Remember) { try { save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
        return result.Choice;
    }
    public Task<BusyChoice> BeforeAddAsync(int count, Window? owner = null) => Task.FromResult(BeforeAddAsyncSync(count, owner));

    /// <summary>After the jobs were added: "Download now" starts exactly these jobs, beside the running ones; the saved count stays.</summary>
    public void AfterAdd(BusyChoice choice, IEnumerable<DownloadJob> jobs)
    {
        if (choice != BusyChoice.DownloadNow) return;
        foreach (var job in jobs) { try { manager.ResumeNow(job.Id); } catch (KeyNotFoundException) { } }
    }

    private static Window? ActiveWindow() => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible) ?? Application.Current?.MainWindow;

    /// <summary>Convenience for the add paths: asks if needed, runs the add, applies the choice.</summary>
    public static async Task<T> GateAsync<T>(int count, Func<Task<T>> add, Func<T, IEnumerable<DownloadJob>> jobsOf, Window? owner = null)
    {
        var service = Current; var choice = service is null ? BusyChoice.Wait : service.BeforeAddAsyncSync(count, owner);
        var result = await add();
        service?.AfterAdd(choice, jobsOf(result));
        return result;
    }
}
