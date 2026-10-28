using System.IO;
using NDM.App;
using NDM.Core;

/// <summary>Shared by the window suites (Layout, Manage, Localization, Themes): a fake video tool and ready-made stream manifests for the stream dialog. Nothing here touches the network or the real tool.</summary>
internal sealed class FakeVideoTool : IVideoTool
{
    public bool Installed, FromPath;
    public string? VersionText = "9.0.2";
    public long Bytes;
    public int InstallCalls, UninstallCalls;
    /// <summary>Completes the install when set (the test decides when); null = installs at once.</summary>
    public TaskCompletionSource? Gate;
    public Exception? Failure;
    public bool IsInstalled => Installed;
    public bool IsAvailable => Installed || FromPath;
    public string? Version => Installed ? VersionText : null;
    public long InstalledBytes => Installed ? (Bytes > 0 ? Bytes : 123_000_000) : 0;
    public bool UseFromPath { get => FromPath; set => FromPath = value; }
    public void Uninstall() { UninstallCalls++; Installed = false; }
    public async Task InstallAsync(IProgress<(long Done, long Total)> progress, CancellationToken cancellation)
    {
        InstallCalls++;
        progress.Report((10, 100));
        if (Gate is not null) await Gate.Task.WaitAsync(cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (Failure is not null) throw Failure;
        progress.Report((100, 100));
        Installed = true;
    }
}

internal static class StreamFixtures
{
    public const string HlsUrl = "https://video.example.org/shows/e1/master.m3u8";

    /// <summary>A master playlist: three qualities, two separate sound tracks (English is the default), one hour 42 minutes.</summary>
    public static StreamManifest Separate() => new()
    {
        Kind = StreamKind.Hls, Url = HlsUrl, Duration = new TimeSpan(1, 42, 1), PieceContainer = StreamContainer.Fmp4,
        Variants = new[]
        {
            new VideoVariant("v360", 800_000, 640, 360, 25, "avc1.4d401e", "aud"),
            new VideoVariant("v1080", 5_000_000, 1920, 1080, 25, "avc1.640028", "aud"),
            new VideoVariant("v720", 2_500_000, 1280, 720, 25, "avc1.4d401f", "aud"),
        },
        AudioTracks = new[]
        {
            new AudioTrack("a-ar", "ar", "Arabic", 128_000, "mp4a.40.2", "aud"),
            new AudioTrack("a-en", "en", "English", 128_000, "mp4a.40.2", "aud", true),
        }
    };

    /// <summary>One media playlist of transport stream pieces that already carries its sound: no variants, no separate sound.</summary>
    public static StreamManifest Single() => new()
    {
        Kind = StreamKind.Hls, Url = "https://video.example.org/v/index.m3u8", Duration = TimeSpan.FromMinutes(3), PieceContainer = StreamContainer.Ts
    };

    /// <summary>A DASH manifest: picture and sound tracks, MP4 output only.</summary>
    public static StreamManifest Dash() => new()
    {
        Kind = StreamKind.Dash, Url = "https://video.example.org/d/manifest.mpd", Duration = TimeSpan.FromMinutes(12), PieceContainer = StreamContainer.Fmp4,
        Variants = new[] { new VideoVariant("d1", 3_000_000, 1280, 720, 30, "avc1", null), new VideoVariant("d2", 1_000_000, 854, 480, 30, "avc1", null) },
        AudioTracks = new[] { new AudioTrack("da", "en", "English", 96_000, "mp4a", null, true) }
    };

    public static DownloadRequest Seed(string url = HlsUrl, string? fileName = "Episode 1") => new(url, fileName, "https://video.example.org/shows/e1", "sid=1", false, null, null, null, "UA/1", "*/*", "en");

    /// <summary>A dialog whose playlist read ends at once with <paramref name="manifest"/>, or fails with <paramref name="failure"/>.</summary>
    public static StreamOptionsDialog Dialog(StreamManifest? manifest, IVideoTool? tool = null, Exception? failure = null, string? downloadDirectory = null, Func<string, string>? autoFolder = null, DownloadRequest? seed = null) =>
        new(seed ?? Seed(), downloadDirectory ?? Path.Combine(Path.GetTempPath(), "ndm-stream-fixture"),
            (_, _) => failure is not null ? Task.FromException<StreamManifest>(failure) : Task.FromResult(manifest!), tool ?? new FakeVideoTool(), autoFolder);

    /// <summary>A dialog still reading: the playlist read waits until the returned source is completed or the token is cancelled.</summary>
    public static StreamOptionsDialog Reading(out TaskCompletionSource<StreamManifest> gate, out Func<CancellationToken?> seenToken, IVideoTool? tool = null, string? downloadDirectory = null)
    {
        var source = new TaskCompletionSource<StreamManifest>(TaskCreationOptions.RunContinuationsAsynchronously); CancellationToken? token = null;
        gate = source; seenToken = () => token;
        return new(Seed(), downloadDirectory ?? Path.Combine(Path.GetTempPath(), "ndm-stream-fixture"),
            (_, ct) => { token = ct; ct.Register(() => source.TrySetCanceled(ct)); return source.Task; }, tool ?? new FakeVideoTool());
    }
}
