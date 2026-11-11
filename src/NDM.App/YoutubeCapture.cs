using System.IO;
using System.Windows;
using NDM.Core;

namespace NDM.App;

/// <summary>Explicit page-link handoff. No inspection or job starts before the user chooses.</summary>
public static class YoutubeCapture
{
    /// <summary>Playlist handoff (1.11.0): the playlist page address opens the Playlist dialog as its own window; nothing is read or started before the user chooses.</summary>
    public static async Task HandlePlaylistAsync(NativeBridge.BrowserDownloadRequest request, DownloadManager manager, AppSettings settings, TrayWindowList windows,
        Action<PlaylistOptionsDialog>? onDialog = null, Action? saveSettings = null, Func<ThumbnailLoader?>? thumbnailFactory = null)
    {
        PlaylistOptionsDialog? dialog = null; ThumbnailLoader? thumbnails = null;
        try
        {
            if (request.Gone.IsCancellationRequested) { request.Respond(new(false, Error: "The browser connection was lost before you decided.")); return; }
            var url = NDM.Protocol.YoutubePageLink.CanonicalPlaylist(request.Url);
            thumbnails = thumbnailFactory is { } make ? make() : new ThumbnailLoader(App.StateDirectory, () => settings.ProxyFor(ProxyPurpose.App));
            dialog = new PlaylistOptionsDialog(url, settings.DownloadDirectory, manager.Youtube, settings, thumbnails: thumbnails);
            dialog.Modeless = true; dialog.Owner = null; dialog.ShowInTaskbar = true; dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            dialog.Closed += (_, _) => closed.TrySetResult(dialog.Accepted);
            windows.Add(dialog, TrayWindowKind.Options, () => "YouTube playlist");
            dialog.ContentRendered += (_, _) => { if (dialog.IsVisible) WindowActivation.BringToFront(dialog); };
            using var gone = request.Gone.Register(() => dialog.Dispatcher.BeginInvoke(new Action(dialog.MarkExpired)));
            onDialog?.Invoke(dialog);
            dialog.Show();
            if (!await closed.Task || dialog.Requests.Count == 0) { request.Respond(new(false, Error: "Download cancelled in NeoDM.", Cancelled: true)); return; }
            if (request.Gone.IsCancellationRequested) { request.Respond(new(false, Error: "The browser connection was lost before you decided.")); return; }
            try { saveSettings?.Invoke(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            var jobs = await PlaylistCommit.AddAsync(manager, dialog.Requests, dialog.UserAction, dialog.ScheduledAt);
            request.Respond(jobs.Count > 0 ? new(true, jobs[0].Id) : new(false, Error: "The download could not be saved. Check NeoDM and try again."));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            try { dialog?.Close(); } catch (InvalidOperationException) { }
            request.Respond(new(false, Error: "The download could not be saved. Check NeoDM and try again."));
        }
        finally { thumbnails?.Dispose(); }
    }

    public static async Task HandleAsync(NativeBridge.BrowserDownloadRequest request, DownloadManager manager, AppSettings settings, TrayWindowList windows,
        Action<YoutubeOptionsDialog>? onDialog = null, Action<string>? showProgress = null,
        Func<DownloadRequest, YoutubeOptionsDialog>? dialogFactory = null)
    {
        YoutubeOptionsDialog? dialog = null;
        try
        {
            if (request.Gone.IsCancellationRequested) { request.Respond(new(false, Error: "The browser connection was lost before you decided.")); return; }
            if (request.Playlist) { await HandlePlaylistAsync(request, manager, settings, windows, onDialog: null, saveSettings: null); return; }
            var seed = new DownloadRequest(NDM.Protocol.YoutubePageLink.Canonical(request.Url));
            dialog = dialogFactory?.Invoke(seed) ?? new YoutubeOptionsDialog(seed, settings.DownloadDirectory, manager.Youtube, manager.Tools, settings: settings, thumbnails: ThumbnailLoader.VideoWindowFactory(() => settings.ProxyFor(ProxyPurpose.App)));
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
            var job = await YoutubeCommit.AddAsync(manager, dialog.Request, dialog.UserAction, dialog.ScheduledAt) ?? throw new InvalidOperationException();
            if (YoutubeCommit.StartsNow(dialog.UserAction)) showProgress?.Invoke(job.Id);
            request.Respond(new(true, job.Id));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            try { dialog?.Close(); } catch (InvalidOperationException) { }
            request.Respond(new(false, Error: "The download could not be saved. Check NeoDM and try again."));
        }
    }
}
