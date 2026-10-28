# Streaming downloads (HLS and DASH)

Many sites do not serve a video as one file. They serve a small text file (a playlist or manifest) that lists hundreds of short pieces, and the player fetches the pieces one after the other. NeoDM can fetch those pieces itself, in parallel, and save the result as a normal video file. This page describes what the download engine supports, what it refuses, how pieces are joined and what limits apply. (Version 1.6.0 added the engine; the app windows and the browser extension use it from the same release.)

## What is supported

| | HLS (`.m3u8`, RFC 8216) | DASH (`.mpd`, ISO/IEC 23009-1) |
|---|---|---|
| Kind of stream | VOD (a finished list) | static presentations |
| Qualities | master playlist variants (bandwidth, resolution, codecs, frame rate); separate audio renditions | video representations and audio representations |
| Pieces | `EXTINF` list, `EXT-X-BYTERANGE`, `EXT-X-MAP` initialization section (also with a byte range) | `SegmentTemplate` (`$RepresentationID$ $Number$ $Bandwidth$ $Time$`, `%0Nd` formats, `startNumber`, `timescale`, `duration`, `SegmentTimeline` with `r` repeats), `SegmentList`, one-file representations (`SegmentBase`, `BaseURL`) |
| Several parts | pieces from several `EXT-X-DISCONTINUITY` sections are joined in order | several periods are joined in order (they must share the same initialization section) |
| Delivery encryption | AES-128 (CBC, full piece) with a key address on http(s); explicit IV, or the IV derived from the sequence number; key rotation | none |
| Container | MPEG-TS, fragmented MP4, AAC/MP3 audio | fragmented MP4, WebM, MPEG-TS |

Addresses are resolved against the playlist or manifest address (`../`, query strings and tokens are kept as written). The content decides what an address is: a link that ends in `.bin` but starts with `#EXTM3U` is handled as a playlist.

## What is refused, and why

| Refusal | Code | Why |
|---|---|---|
| Live streams (HLS without `EXT-X-ENDLIST` and not `VOD`, DASH `type="dynamic"`) | `stream-live` | there is no end to download |
| DRM: `METHOD=SAMPLE-AES` or `SAMPLE-AES-CTR`, a `KEYFORMAT` other than `identity`, `skd://` keys, Widevine, PlayReady or FairPlay markers, any DASH `ContentProtection` | `stream-drm` | NeoDM does not work around content protection. A plain AES-128 key that anyone can fetch over http(s) is delivery encryption, not DRM, and is supported |
| Anything else it cannot do: other URL schemes, subtitle-only lists, unknown encryption methods, a stream that changes its initialization section half way, DASH `xlink` | `stream-unsupported` | cannot be joined into one file safely |
| Unreadable or hostile input (missing `#EXTM3U`, bad numbers, XML with a DTD, deep nesting, more than 200000 pieces, a line over 65536 characters, input over 32 MB) | `stream-malformed` | the parsers return a typed error and never throw anything else |
| A piece or key that moves from https to plain http | `stream-downgrade` | the encryption of the page is not given up silently |
| A piece answers 404 or 410 | `stream-segment-missing` (with the piece number) | the list points at something that is gone |

The codes are stored on the job (`DownloadJob.ErrorCode`). The engine's messages are English; the app shows its own localized text for each code.

When a plain download of an address returns a playlist or manifest, the plain download is refused as before (it never saves the text as the file) and the failure now carries `StreamDetected = true` and `DetectedStream` (HLS or DASH). A music list (`#EXTM3U` with only `#EXTINF` lines) is not a stream and still downloads as a file.

## How a stream download runs

1. `InspectStreamAsync(address, ...)` reads the playlist or manifest and returns what it offers: variants (id, bandwidth, size, frame rate, codecs, audio group), audio tracks (language, name) and the duration. Live and protected streams fail here with a `StreamException`.
2. `AddStreamAsync(...)` starts a job. Without a choice it takes the highest bandwidth variant and the default audio of that variant.
3. The pieces are fetched in parallel (default 8 connections, 1 to 16 in `DownloadOptions.StreamConnections`). Each piece is tried up to three times with a growing pause. A 429 or 503 halves the number of connections.
4. Each finished piece is a file in the job's work folder `%LOCALAPPDATA%\NDM\<job id>.parts`, and a map of finished pieces (`stream.json`, with the size of each piece) is saved about once a second and when the job stops. After a pause or a restart of the app, pieces whose file is there with the recorded size are kept and never fetched again; the playlist is read again and must match the saved map (pieces compared by path, byte range and encryption, ignoring query strings that carry changing tokens), otherwise the job starts over.
5. When all pieces are there they are joined, the result is published like any download (collision-safe name, Mark of the Web, the work folder removed), and the job is complete.

Progress: `SegmentsDone` and `SegmentsTotal` count pieces (the initialization section counts as one). The total size is an estimate (bandwidth x duration) until pieces have arrived, then it is extrapolated per track, and at the end it is exact. The time left follows the rate at which pieces finish. The speed limit of a job covers all of its connections. Pause, resume, schedule, queue order, the number of simultaneous jobs and removal behave as for files; removing a job deletes its work folder.

Free space: before the first piece is requested the engine needs the estimated size twice (pieces plus the joined copy); without an estimate it keeps a floor of 100 MB.

### Request identity

Playlists, keys and pieces are requested with the same identity as a file: the User-Agent, Accept, Accept-Language and Referer the browser used (or the engine's defaults) and, for the manifest's own origin only, the Cookie. A piece or key on another origin does not get the cookie. Pieces are requested without a `Range` header unless the playlist gives a byte range. Until something has worked, a refusal (403 and the other statuses of the 1.5.7 ladder) is answered with the same ladder as for files: site root as referer, then no referer. Once a piece has been received, a 401 or 403 stops the download at once with a plain sentence (the link or sign-in has probably expired; open the page again and add the video again).

## Output and joining

| Stream | Result |
|---|---|
| HLS with MPEG-TS pieces, one track | the pieces joined as `<name>.ts` |
| HLS or DASH with fragmented MP4 (initialization section plus pieces), one track | joined as `<name>.mp4` (or `.m4a` for sound only); a valid fragmented MP4 (`ftyp`, `moov`, then `moof`/`mdat`) |
| DASH, one file per representation | a normal ranged download of that file |
| separate picture and sound | joined by ffmpeg (copy, never re-encoded) into one `<name>.mp4` |
| transport stream when MP4 is wanted (`PreferMp4`) | rewrapped by ffmpeg into `<name>.mp4` |

Without ffmpeg, picture and sound are saved as two files, `<name> (video only).mp4` and `<name> (audio only).m4a` (`.ts`/`.aac`/`.webm` as the container requires), and the job carries the note `needs-muxer`; a transport stream wanted as MP4 is saved as `.ts` with the same note. If ffmpeg runs and its result is refused (error exit, timeout, empty output, or a duration more than 2 % away from the manifest's when the tool reports one), the tracks are saved separately with the note `mux-failed`. The job still counts as complete: nothing downloaded is lost.

The output name comes from the page title or file name given, otherwise from the playlist address (a generic last part such as `index` or `master` is skipped), with the usual file name sanitizing. The extension follows the container.

### The ffmpeg tool

`DownloadManager.Tools` (a `ToolManager`) installs, checks and runs ffmpeg. It is optional and never bundled: the pinned build is downloaded on request (see `docs/THIRD-PARTY.md` for the address, size, hashes and licence) to `%LOCALAPPDATA%\NDM\tools\ffmpeg\`.

- Install: https only, the pinned host only (redirects must stay on it), the archive size and SHA-256 must equal the compiled-in values, only `ffmpeg.exe` is taken out (entry names with `..`, absolute names and too many entries are refused; the entry size, the compression ratio and the bytes actually written are limited), the extracted program's SHA-256 is checked while writing and again from the disk, and recorded in `ffmpeg.json` beside it.
- Run: the program is opened for reading without write or delete sharing, its hash is checked through that handle and compared with the record and with the pinned value, and only then is it started, with the handle kept for the whole run. Arguments come from fixed templates (`FfmpegCommands`) and each is one argument (`ProcessStartInfo.ArgumentList`, no shell). Input files have fixed names in the work folder (`video.mp4`, `audio.m4a`, `out.mp4`); nothing a site or user typed ends up in a command line. A timeout (default 15 minutes) kills the process and its children.
- "Use ffmpeg from PATH" is off by default (`ToolManager.UseFromPath`). When the user turns it on and the pinned tool is not installed, `ffmpeg.exe` from PATH is used without a hash check.

## In the app

- **Opening the stream window.** A capture request from the extension that carries `stream` (`hls`, `dash` or `auto`), Add URL with an address whose path ends in `.m3u8` or `.mpd` (the query is ignored, case does not matter), and "Download as stream" on a failed plain download all open the same window. It is a separate top-level window like the download dialog, listed in the tray menu while it waits, and comes to the front. At most eight capture windows wait at once.
- **Reading.** The window first says "Reading the playlist..." with a progress bar and Cancel. The playlist is read with the referer, cookie, User-Agent, Accept and Accept-Language of the request. Closing the window cancels the read.
- **Failures.** A live stream ("This is a live stream. NeoDM saves finished videos only."), a protected video ("This video is protected (DRM) and cannot be saved."), an unsupported or damaged playlist, and an HTTP answer are each explained in plain words. The text can be selected and copied; the only button is Close. There is no "download in the browser instead" for streams.
- **Choices.** File name (from the page title or the address), quality (sorted by resolution and rate, the best preselected, for example "1080p, 5.0 Mbit/s, about 1.2 GB"), sound track when the sound is separate (the default one preselected, or no sound), file type (MP4 recommended, or TS which needs no tool) when both are possible, folder with Browse and the category folders (Video by default), and a line with the estimated size and length.
- **The video tool.** When the choice needs ffmpeg and it is not installed, the window says so, with "Install ffmpeg now" (progress, Cancel, a result that can be copied), "Continue without it" and a link to the licence page (https://ffmpeg.org/legal.html). Without the tool NeoDM saves two files (picture and sound) or a TS file.
- **Starting.** Start now, Add to queue and Schedule add the stream through the same engine call; the browser gets the answer `ok` with the job id, or `cancelled` when you cancel or close the window. If the browser or the page goes away while the window waits, the window says so and cannot add anything.
- **The list.** A stream shows "Stream" in the list and an estimated size that becomes the final size. The progress window shows "Pieces: n of m", a bar and the step (downloading pieces, joining pieces, merging with the video tool), the request attempts and the plain-words errors (a missing piece names its number).
- **Two files.** When a finished stream was saved as two files because the tool is missing, the completion window says so, lists both files and offers "Install ffmpeg and join them" (installs, then joins without re-encoding). If joining failed it says "The files could not be joined; they are saved separately."
- **Options.** Options > Downloads > "Video tool (ffmpeg)" shows the status, installs and removes the tool, offers "Use ffmpeg found in PATH" (off by default, not checked against the fingerprint) and sets the number of stream connections (1 to 16, default 8).
- **The extension.** An HLS or DASH video on a page gets "Download stream" in the button, the ribbon, the popup list, the picker and the context menu. Only the playlist address is sent (never its text), with the title as file name, the referer, the cookies and the browser identity as for a file. Protected and live streams are listed with their reason and never sent.

## Limits

- VOD only; no DRM; no live; no subtitles (they are ignored); no re-encoding, ever.
- Piece size is not limited by memory (pieces stream to disk), but a manifest or playlist is limited to 32 MB of text and 200000 pieces.
- Several DASH periods must use the same initialization section; ads inserted as periods with other tracks are not handled.
- A job that only needs two files because ffmpeg is missing keeps both until you install the tool and download again; moving such a job moves only the main file.
- Cross-origin pieces work, but an https stream whose pieces are on plain http is refused.

## For developers

Public types in `NDM.Core`: `StreamKind`, `StreamError`, `StreamCodes`, `StreamException`, `StreamManifest`, `VideoVariant`, `AudioTrack`, `StreamSelection`, `StreamPlan`, `TrackPlan`, `StreamSegment`, `HlsParser`, `DashParser`, `StreamOutput`, `StreamNaming`, `StreamSniffer`, `StreamSecurity`, `ToolManager`, `ToolPin`, `FfmpegCommands`. `DownloadRequest` got the optional `StreamKind`, `StreamVariantId`, `StreamAudioId` and `PreferMp4`; `DownloadJob` got `Stream`, `SegmentsDone`, `SegmentsTotal`, `StreamNote`, `StreamDetected`, `DetectedStream`, `ErrorSegment` and `AdditionalFiles`. `queue.json` keeps working in both directions: old files restore, and the new fields are optional.

Tests: `tests/NDM.Tests` (`StreamParserTests`, `StreamEngineTests`, `ToolTests`) run in `build.ps1` and use only local servers and a stand-in program for ffmpeg. One extra test is opt-in because it downloads the real tool (about 115 MB):

```
dotnet run --project tests/NDM.Tests/NDM.Tests.csproj -c Release -- --real-ffmpeg
```

It installs the pinned build into a scratch folder, generates HLS and DASH fixtures with it, downloads them with the engine and checks the results with ffmpeg again.
