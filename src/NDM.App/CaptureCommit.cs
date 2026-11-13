using NDM.Core;

namespace NDM.App;

/// <summary>Turns an accepted capture dialog into a job. The dialog's request already carries the folder the user confirmed (or null = the engine's
/// category folder), so Start now, Add to queue and Schedule all save exactly where the dialog said.</summary>
public static class CaptureCommit
{
    /// <summary>Null when the dialog was not accepted with a valid action.</summary>
    public static async Task<DownloadJob?> AddAsync(DownloadManager manager, DownloadOptionsDialog dialog)
    {
        if (dialog.Request is null) return null;
        var limit = dialog.SpeedLimit;
        DownloadJob job;
        switch (dialog.UserAction)
        {
            case DownloadOptionsDialog.DialogAction.StartNow:
                job = await manager.AddScheduledAsync(dialog.Request, limit.HasValue ? DateTimeOffset.UtcNow.AddYears(1) : null);
                if (limit.HasValue) { manager.SetSpeedLimit(job.Id, limit); manager.Resume(job.Id); }
                break;
            case DownloadOptionsDialog.DialogAction.Queue:
                var busy = BusyQueue.Current?.BeforeAddAsyncSync(1) ?? BusyChoice.Wait; // 1.15.0: asked before the add, never for a free slot
                job = await manager.AddScheduledAsync(dialog.Request, limit.HasValue ? DateTimeOffset.UtcNow.AddYears(1) : null);
                BusyQueue.Current?.AfterAdd(busy, new[] { job });
                if (limit.HasValue) manager.SetSpeedLimit(job.Id, limit);
                break;
            case DownloadOptionsDialog.DialogAction.Schedule:
                job = await manager.AddScheduledAsync(dialog.Request, limit.HasValue ? DateTimeOffset.UtcNow.AddYears(1) : dialog.ScheduledAt);
                if (limit.HasValue)
                {
                    manager.SetSpeedLimit(job.Id, limit);
                    manager.Schedule(job.Id, dialog.ScheduledAt ?? DateTimeOffset.UtcNow);
                }
                else if (dialog.ScheduledAt.HasValue) manager.Schedule(job.Id, dialog.ScheduledAt.Value);
                break;
            default: return null;
        }
        return job;
    }

    /// <summary>Only downloads that start right now open the window; queued-with-limit and scheduled ones stay quiet.</summary>
    public static bool StartsNow(DownloadOptionsDialog dialog) =>
        dialog.UserAction == DownloadOptionsDialog.DialogAction.StartNow || (dialog.UserAction == DownloadOptionsDialog.DialogAction.Queue && !dialog.SpeedLimit.HasValue);
}
