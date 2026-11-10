using System.IO;
using System.Windows;
using NDM.App.Localization;
using NDM.Core;

namespace NDM.App;

public partial class MainWindow
{
    /// <summary>Called with the YouTube video dialog right before it is shown, so tests can drive or close it.</summary>
    public Action<YoutubeOptionsDialog>? YoutubeDialogCreated { get; set; }
    /// <summary>Called with the Playlist dialog right before it is shown.</summary>
    public Action<PlaylistOptionsDialog>? PlaylistDialogCreated { get; set; }
    /// <summary>Creates the thumbnail loader of a Playlist dialog; tests replace it so no picture is fetched from the network.</summary>
    public Func<ThumbnailLoader?>? ThumbnailLoaderFactory { get; set; }

    /// <summary>The small "this video or the whole playlist" question for a watch address that carries both. 0 = video, 1 = playlist, -1 = cancelled.</summary>
    public static int AskVideoOrPlaylist(Window? owner) =>
        ChoiceWindow.Ask(owner, Loc.T("youtube.ask_title"), Loc.T("youtube.ask_message"), Loc.T("youtube.ask_video"), Loc.T("youtube.ask_playlist"), Loc.T("common.btn.cancel"));

    /// <summary>Opens the YouTube video dialog for an address and adds the chosen job (Download now, Add to queue or Schedule).</summary>
    public async Task<DownloadJob?> OpenYoutubeVideoAsync(DownloadRequest seed, DateTimeOffset? schedule = null, long? speedLimit = null)
    {
        var youtube = new YoutubeOptionsDialog(seed, _settings.DownloadDirectory, _manager.Youtube, _manager.Tools, settings: _settings, initialSchedule: schedule, thumbnails: ThumbnailLoader.VideoWindowFactory(() => _settings.BuildProxy())) { Owner = this };
        YoutubeDialogCreated?.Invoke(youtube);
        if (youtube.ShowDialog() != true || youtube.Request is null) return null;
        var job = await YoutubeCommit.AddAsync(_manager, youtube.Request, youtube.UserAction, youtube.ScheduledAt, speedLimit);
        Refresh();
        return job;
    }

    /// <summary>Opens the Playlist dialog and adds one job per chosen video as one package.</summary>
    public async Task<IReadOnlyList<DownloadJob>> OpenYoutubePlaylistAsync(string playlistUrl, DateTimeOffset? schedule = null)
    {
        using var thumbnails = ThumbnailLoaderFactory is { } make ? make() : new ThumbnailLoader(App.StateDirectory, () => _settings.BuildProxy());
        var dialog = new PlaylistOptionsDialog(playlistUrl, _settings.DownloadDirectory, _manager.Youtube, _settings, thumbnails: thumbnails, initialSchedule: schedule) { Owner = this };
        PlaylistDialogCreated?.Invoke(dialog);
        if (dialog.ShowDialog() != true || dialog.Requests.Count == 0) return Array.Empty<DownloadJob>();
        try { _saveSettings(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var jobs = await PlaylistCommit.AddAsync(_manager, dialog.Requests, dialog.UserAction, dialog.ScheduledAt);
        Notice.Text = Loc.T("playlist.notice.added", jobs.Count, dialog.Package);
        Refresh(true);
        return jobs;
    }

    /// <summary>The same as <see cref="RouteYoutubeAsync"/> but blocks other Add windows meanwhile and never throws.</summary>
    public async Task RouteYoutubeGuardedAsync(IReadOnlyList<string> urls, DateTimeOffset? schedule = null)
    {
        if (_adding) return;
        _adding = true;
        try { await RouteYoutubeAsync(urls, schedule); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Refresh(); Notice.Text = Loc.T("main.notice.add_unfinished"); }
        finally { _adding = false; }
    }

    /// <summary>Routes YouTube links from any entry point (paste, drop, link grabber, bulk import) to the right dialog. One link opens its dialog; several videos offer
    /// "one by one" or "queue all with one quality"; playlists open one after another. A watch address with a list asks first.</summary>
    public async Task RouteYoutubeAsync(IReadOnlyList<string> urls, DateTimeOffset? schedule = null)
    {
        var videos = new List<string>(); var playlists = new List<string>();
        foreach (var raw in urls.Distinct(StringComparer.Ordinal))
        {
            var plan = YoutubeRouting.Plan(raw);
            switch (plan.Action)
            {
                case YoutubeAction.OpenVideo: videos.Add(plan.VideoUrl!); break;
                case YoutubeAction.OpenPlaylist: playlists.Add(plan.PlaylistUrl!); break;
                case YoutubeAction.AskVideoOrPlaylist:
                    var answer = AskVideoOrPlaylist(this);
                    if (answer == 0) videos.Add(plan.VideoUrl!); else if (answer == 1) playlists.Add(plan.PlaylistUrl!);
                    break;
                case YoutubeAction.Refused: Notice.Text = plan.Message ?? ""; break;
            }
        }
        if (videos.Count > 1)
        {
            if (YoutubeBulkWindow.Ask(this, videos.Count) is not { } choice) videos.Clear();
            else if (choice.Queue)
            {
                var requests = videos.Select(v => new DownloadRequest(v, choice.Preset.AudioOnly ? "YouTube audio." + (choice.Mp3 ? "mp3" : "m4a") : "YouTube video.mp4", Youtube: YoutubeSelection.FromPreset(choice.Preset, choice.Mp3))).ToList();
                var result = await _manager.AddManyAsync(requests, startPaused: false);
                Notice.Text = Loc.T("youtube.bulk_queued", result.Added.Count);
                Refresh(true); videos.Clear();
            }
        }
        foreach (var video in videos) await OpenYoutubeVideoAsync(new DownloadRequest(video), schedule);
        foreach (var playlist in playlists) await OpenYoutubePlaylistAsync(playlist, schedule);
    }
}
