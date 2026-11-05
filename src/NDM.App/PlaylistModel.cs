using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using NDM.App.Localization;
using NDM.Core;

namespace NDM.App;

/// <summary>The shared options of the Playlist dialog (apply to every selected video unless its row overrides them).</summary>
public sealed class PlaylistOptions : INotifyPropertyChanged
{
    private string quality = "best"; private int audioKbps; private bool audioOnly, mp3;
    /// <summary>"best", "2160", "1440", "1080", "720", "480", "360".</summary>
    public string Quality { get => quality; set => Set(ref quality, YoutubePreset.Qualities.Contains(value) && value != "audio" ? value : "best"); }
    /// <summary>0 = best, else 256, 192, 128, 64 kb/s.</summary>
    public int AudioKbps { get => audioKbps; set => Set(ref audioKbps, YoutubePreset.AudioRates.Contains(value) ? value : 0); }
    public bool AudioOnly { get => audioOnly; set => Set(ref audioOnly, value); }
    public bool Mp3 { get => mp3; set => Set(ref mp3, value); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; PropertyChanged?.Invoke(this, new(name)); }
}

/// <summary>One video row. Override: null follows the global options; otherwise "best", a height, or "audio". Unavailable rows are never selected.</summary>
public sealed class PlaylistRow : INotifyPropertyChanged
{
    private readonly PlaylistOptions options;
    private bool selected; private string? overrideQuality; private ImageSource? thumbnail; private bool thumbnailFailed;
    public YoutubePlaylistEntry Entry { get; }
    public int Position { get; }
    public PlaylistRow(YoutubePlaylistEntry entry, int position, PlaylistOptions options)
    {
        Entry = entry; Position = position; this.options = options; selected = entry.Available;
        options.PropertyChanged += (_, _) => { Raise(nameof(AudioOnly)); Raise(nameof(HasOverride)); Raise(nameof(QualityText)); };
    }
    public string Title => Entry.Title;
    public string Channel => Entry.Channel;
    public bool Available => Entry.Available;
    public string DurationText => Entry.DurationSeconds <= 0 ? "" : PlaylistModel.FormatDuration(Entry.DurationSeconds);
    public string Subtitle => string.Join("  ·  ", new[] { Entry.Channel, DurationText }.Where(p => p.Length > 0));
    public string StateKey => Entry.State switch
    {
        YoutubeEntryState.Private => "playlist.state.private", YoutubeEntryState.Deleted => "playlist.state.deleted", YoutubeEntryState.Members => "playlist.state.members",
        YoutubeEntryState.Live => "playlist.state.live", YoutubeEntryState.Unavailable => "playlist.state.unavailable", _ => ""
    };
    public bool IsSelected { get => selected; set { value &= Available; if (selected == value) return; selected = value; Raise(); Selection?.Invoke(); } }
    internal Action? Selection { get; set; }
    public string? OverrideQuality
    {
        get => overrideQuality;
        set { if (value is not null && (!YoutubePreset.Qualities.Contains(value))) value = null; if (overrideQuality == value) return; overrideQuality = value; Raise(); Raise(nameof(OverrideChoice)); Raise(nameof(AudioOnly)); Raise(nameof(HasOverride)); Raise(nameof(OverrideVisibility)); Raise(nameof(QualityText)); Selection?.Invoke(); }
    }
    public bool HasOverride => overrideQuality is not null;
    /// <summary>For the row's quality drop-down: "" = default (follow the global choice), else the preset.</summary>
    public string OverrideChoice { get => overrideQuality ?? ""; set => OverrideQuality = string.IsNullOrEmpty(value) ? null : value; }
    public double RowOpacity => Available ? 1 : 0.55;
    public string StateText => StateKey.Length == 0 ? "" : Loc.T(StateKey);
    public Visibility StateVisibility => StateKey.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility OverrideVisibility => HasOverride ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>Effective audio-only for this row. Setting it overrides the global choice (setting it back to the global value clears the override).</summary>
    public bool AudioOnly
    {
        get => overrideQuality is not null ? overrideQuality == "audio" : options.AudioOnly;
        set
        {
            if (value == AudioOnly) return;
            OverrideQuality = value == options.AudioOnly ? null : value ? "audio" : "best";
        }
    }
    /// <summary>The quality this row will use, as a preset.</summary>
    public YoutubePreset Preset
    {
        get
        {
            var q = overrideQuality ?? (options.AudioOnly ? "audio" : options.Quality);
            return new YoutubePreset(q, options.AudioKbps);
        }
    }
    public bool EffectiveMp3 => Preset.AudioOnly && options.Mp3;
    public string QualityText => Preset.AudioOnly ? "audio" : Preset.Quality;
    public ImageSource? Thumbnail { get => thumbnail; set { thumbnail = value; Raise(); Raise(nameof(HasThumbnail)); } }
    public bool HasThumbnail => thumbnail is not null;
    public bool ThumbnailFailed { get => thumbnailFailed; set { thumbnailFailed = value; Raise(); } }
    internal bool ThumbnailRequested { get; set; }
    internal CancellationTokenSource? ThumbnailCancel { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

/// <summary>The list, its counters and the conversion to jobs. No UI types except the thumbnail image source.</summary>
public sealed class PlaylistModel
{
    public PlaylistOptions Options { get; } = new();
    public string Title { get; }
    public int Total { get; }
    public bool Truncated { get; }
    public IReadOnlyList<PlaylistRow> Rows { get; }
    public event Action? Changed;

    public PlaylistModel(YoutubePlaylist playlist)
    {
        Title = playlist.Title; Total = playlist.Total; Truncated = playlist.Truncated;
        Rows = playlist.Entries.Select((e, i) => new PlaylistRow(e, i + 1, Options) { Selection = () => Changed?.Invoke() }).ToArray();
        Options.PropertyChanged += (_, _) => Changed?.Invoke();
    }

    public string Filter { get; private set; } = "";
    /// <summary>Rows whose title or channel contains the filter text (case-insensitive); everything for an empty filter.</summary>
    public IEnumerable<PlaylistRow> Visible => Filter.Length == 0 ? Rows : Rows.Where(r => r.Title.Contains(Filter, StringComparison.CurrentCultureIgnoreCase) || r.Channel.Contains(Filter, StringComparison.CurrentCultureIgnoreCase));
    public void SetFilter(string text) { Filter = (text ?? "").Trim(); Changed?.Invoke(); }

    public int AvailableCount => Rows.Count(r => r.Available);
    public int SelectedCount => Rows.Count(r => r.IsSelected);
    public int TotalSeconds => Rows.Where(r => r.IsSelected).Sum(r => r.Entry.DurationSeconds);
    /// <summary>Select all/none/invert act on the rows that are shown (the filter applies); unavailable rows stay off.</summary>
    public void SelectAll() => Apply(r => true);
    public void SelectNone() => Apply(r => false);
    public void Invert() => Apply(r => !r.IsSelected);
    private void Apply(Func<PlaylistRow, bool> value) { foreach (var r in Visible.ToArray()) { var v = value(r); if (r.Available) r.IsSelected = v; } Changed?.Invoke(); }

    public static string FormatDuration(int seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds) : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", t.Minutes, t.Seconds);
    }

    /// <summary>One YouTube job per selected video, in playlist order. Formats are NOT looked up here: each job carries a preset and resolves it when it starts.</summary>
    public IReadOnlyList<DownloadRequest> BuildRequests(string folder, string package, int slots, bool numbered)
    {
        var list = new List<DownloadRequest>();
        foreach (var row in Rows.Where(r => r.IsSelected && r.Available))
        {
            var preset = row.Preset; var selection = YoutubeSelection.FromPreset(preset, row.EffectiveMp3);
            var title = DownloadFileNames.Sanitize(row.Title);
            var stem = numbered ? YoutubePlaylists.Numbered(row.Position, Rows.Count, title) : title;
            var extension = preset.AudioOnly ? (row.EffectiveMp3 ? "mp3" : "m4a") : "mp4";
            list.Add(new DownloadRequest(row.Entry.Url, DownloadFileNames.Sanitize(stem) + "." + extension, TargetDirectory: folder, Youtube: selection, Package: package, PackageSlots: Math.Clamp(slots, 1, 4)));
        }
        return list;
    }
}
