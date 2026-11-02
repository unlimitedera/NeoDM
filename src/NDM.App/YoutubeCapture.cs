using System.Windows;
using NDM.Core;

namespace NDM.App;

/// <summary>Explicit page-link handoff. No inspection or job starts before the user chooses.</summary>
public static class YoutubeCapture
{
    public static async Task HandleAsync(NativeBridge.BrowserDownloadRequest request, DownloadManager manager, AppSettings settings, TrayWindowList windows,
        Action<YoutubeOptionsDialog>? onDialog = null, Action<string>? showProgress = null,
        Func<DownloadRequest, YoutubeOptionsDialog>? dialogFactory = null)
    {
        YoutubeOptionsDialog? dialog = null;
        try
        {
            if (request.Gone.IsCancellationRequested) { request.Respond(new(false, Error: "The browser connection was lost before you decided.")); return; }
            var seed = new DownloadRequest(NDM.Protocol.YoutubePageLink.Canonical(request.Url));
            dialog = dialogFactory?.Invoke(seed) ?? new YoutubeOptionsDialog(seed, settings.DownloadDirectory, manager.Youtube, manager.Tools);
            dialog.Modeless = true; dialog.Owner = null; dialog.ShowInTaskbar = true; dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            dialog.Closed += (_, _) => closed.TrySetResult(dialog.Accepted);
            windows.Add(dialog, TrayWindowKind.Options, () => "YouTube");
            dialog.ContentRendered += (_, _) => { if (dialog.IsVisible) WindowActivation.BringToFront(dialog); };
            using var gone = request.Gone.Register(() => dialog.Dispatcher.BeginInvoke(new Action(dialog.MarkExpired)));
            onDialog?.Invoke(dialog);
            dialog.Show();
            if (!await closed.Task || dialog.Request is null) { request.Respond(new(false, Error: "Download cancelled in NeoDM.", Cancelled: true)); return; }
            if (request.Gone.IsCancellationRequested) { request.Respond(new(false, Error: "The browser connection was lost before you decided.")); return; }
            var job = await manager.AddPausedAsync(dialog.Request);
            if (!dialog.QueueOnly) { manager.Resume(job.Id); showProgress?.Invoke(job.Id); }
            request.Respond(new(true, job.Id));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            try { dialog?.Close(); } catch (InvalidOperationException) { }
            request.Respond(new(false, Error: "The download could not be saved. Check NeoDM and try again."));
        }
    }
}
