# Desktop YouTube downloads

NeoDM 1.7.0 accepts a single YouTube video page through desktop **Add URL**. Save only public, non-DRM videos that you own or have permission to download. The browser extension still does not capture YouTube and has no new YouTube action or permission.

## Use

1. Enter a watch, shorts, embed or youtu.be video link in Add URL. Playlist and channel links are not accepted. Tracking and playlist query fields on a video link are discarded.
2. In the YouTube window, approve the optional tools and press **Read qualities / install tools**. The official standalone yt-dlp executable includes EJS. The app also installs a pinned Deno runtime. Nothing is downloaded before you agree. The combined downloads are about 67 MB.
3. Pick a video quality and a separate audio quality where needed. Audio-only entries keep the original audio without conversion. The MP3 checkbox is available for audio-only entries and converts the audio.
4. Choose the file name and save folder. Separate video and audio tracks are merged into MKV without re-encoding. MP3 conversion and merging require ffmpeg; the app asks before installing it. Original audio and video with its own audio do not need that tool.
5. **Add / schedule** uses the date and speed limit from Add URL. **Queue paused** saves the job without starting it. Resume starts it when its schedule permits.

## Queue behavior

Jobs use the existing download queue and its concurrency limit. Pause stops the owned helper process and its children. Resume restarts it with the same selection and lets the helper reuse partial files where supported. The UI leaves byte-range resume support unknown because this is not a promise that every YouTube format resumes by byte offset. Partial data is deleted when the job is removed. A completed file uses the existing unique-name publication and internet-zone marking.

Progress comes from the helper's downloaded and total byte counters. Separate tracks have separate counters, so the bar may restart when the next track begins. Merging has no byte estimate. Speed limits are passed when a helper starts. After changing a limit, pause and resume to apply it. Proxy settings for ordinary downloads do not currently apply to YouTube helper requests or the optional GitHub tool downloads. Ambient proxy credentials are not inherited by the helper. A network that requires the configured app proxy is therefore not supported on this path yet.

Bulk import does not silently choose a YouTube quality or install a tool. It reports that you must use Add URL.

## Limits and tools

No sign-in, browser-cookie import, user extractor config, plugin loading, remote EJS download, automatic tool update, PO-token override, DRM bypass or access-restriction workaround is offered. Live, previously live and upcoming streams are not accepted. Sign-in, restricted access, rate limits and unsupported videos return copyable messages, not instructions to bypass them. YouTube changes can break a pinned version. A new NeoDM release is required to update the pins.

The helper and runtime are optional local tools. Their immutable versions, hashes, upstream source and licenses are in [THIRD-PARTY.md](THIRD-PARTY.md). NeoDM's own source remains MIT; the standalone extractor is GPLv3+ and Deno is MIT. The optional ffmpeg build is GPLv3. Removing `tools/yt-dlp` and `tools/deno` below the state folder after active jobs have stopped removes those tools. The existing ffmpeg options and its explicit PATH choice are unchanged.

## Verification

The normal engine suite uses a fake local extractor process and fixture tool downloads. The WPF suite drives the real dialog and queue with deterministic metadata and fixture installs. No real internet tool download occurs in these tests:

```text
dotnet run --project tests/NDM.Tests -c Release -- --only YouTube
dotnet run --project src/NDM.App/Tests/Youtube/NDM.App.YoutubeTests.csproj -c Release
```

The explicit network test downloads and verifies official tools into an isolated Hermes scratch directory. It uses the public Blender Foundation Big Buck Bunny sample, not a browser account or browser profile, and inspects the saved output with the pinned ffmpeg tool:

```text
dotnet run --project tests/NDM.Tests -c Release -- --real-youtube
```

The complete release gate is `build.ps1` followed by `build-installer.ps1`. Successful compilation alone does not verify real YouTube delivery, the installed MSI or store approval.
