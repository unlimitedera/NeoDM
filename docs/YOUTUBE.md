# Desktop YouTube downloads

NeoDM 1.8.0 accepts a single YouTube video page through desktop **Add URL**. Save only public, non-DRM videos that you own or have permission to download. Both the manually loaded and Chrome Web Store packages can send a page link to the same desktop quality dialog. Neither variant extracts YouTube media in the browser, and no new permission is added.

## Extension variants and distribution

- `extension/`, `NeoDM.crx`, `NeoDM-extension-manual.crx` and `NeoDM-extension-manual-unpacked.zip` are the manually loaded, non-store edition. The manifest version name labels it "manually loaded". Use Developer mode and Load unpacked. Off-store CRX installation may be blocked on unmanaged browsers. Remove the old manually loaded copy and load the new unpacked folder after updating.
- `NeoDM-extension-store.zip` is built only with `node tools/build-store-zip.cjs`. The builder omits the development manifest key and labels the package "store". It includes the same handoff code, popup, context-menu and on-video actions as the manual package. Tests execute the actual generated ZIP and verify page-link handoff. Use this explicitly labeled store package for any user-controlled submission.
- Google's official Chrome Web Store troubleshooting lists facilitating YouTube downloads as a reason for rejection or removal: https://developer.chrome.com/docs/webstore/troubleshooting. Forwarding a link does not exempt an extension from that policy. These are separate build-time packages, not hidden runtime behavior, reviewer detection or remote switches. Building a store variant does not guarantee store approval.

In either edition, open a supported watch, shorts or youtu.be page, open the popup and click **Send YouTube link to NeoDM**. Alternatively click the on-video Send to NeoDM button or use the dedicated right-click entry on a supported video-page link. The action sends the canonical https watch URL only, plus protocol/version metadata. It does not read or forward cookies, headers, playlist text, media fragments or browser profiles. The desktop opens its independent quality window without reading qualities or installing tools automatically. Choose there, or Cancel. Queue paused saves the chosen job without starting it.

The popup rechecks the active tab before sending. If the page changed, reopen it and try again. A pending link cannot open a second dialog. Missing or older native hosts fail without a file-download fallback. NeoDM 1.7.1 or newer must be running; restart it after updating. If the browser connection closes, the dialog cannot submit. Existing desktop Add URL also accepts embed links; the extension handoff intentionally accepts only watch, shorts and short-link pages.

## Use

1. Enter a watch, shorts, embed or youtu.be video link in Add URL. Since 1.11.0 NeoDM also opens this window by itself when you paste, drop or import a YouTube video link, and Add URL shows a note as soon as the text looks like one. A playlist link opens the Playlist window (see Playlists below). A watch link that carries both a video and a playlist asks which one you want. Channel links are not accepted. Tracking and playlist query fields on a single video link are discarded.
2. Press **Step 1: Load video info**. The download buttons stay off until this is done, and their tooltip says so. While the helper reads the metadata a progress panel shows a percentage bar, a plain line for the current stage (preparing the tools, contacting YouTube, solving the player challenge, reading the formats, building the list), the seconds elapsed and a Cancel button. The helper prints its own progress lines when it runs with `--verbose` (for example "Downloading webpage", "Downloading player", "Solving JS challenges", "Downloading m3u8 information"), and NeoDM moves the stage from those lines. The helper reports no percentage, so the bar is an estimate: it follows a smoothed time curve, never passes 95 percent before the answer arrives, and shows 100 only when the list is ready. The panel says so. An error shows the plain message with a Copy button. The helper is asked only for the title, availability and the format list (id, container, codecs, height, bitrate, size), never the full video record, so the answer stays small even for videos with many caption languages. Answers over 4 MiB or in an unexpected shape show the metadata error. A live, upcoming, protected or private video shows its own message. If the helper fails for an unrecognised reason, the dialog adds a selectable Details line with its last error line. Cancel closes the dialog and cancels the request. Missing developer bundles report a clear error and are never downloaded at runtime.
3. Pick a video quality, or enable **Audio only** and choose an audio bitrate. The video picker contains only video formats. MP3 conversion is available only in audio-only mode.
4. Choose the file name and save folder. The default follows your configured Video or Music category. An edited folder is kept exactly. The bundled ffmpeg merges separate tracks or converts MP3, without an install prompt.
5. **Download now** starts the job at once. **Add to queue** queues it and it starts when a queue slot is free. **Schedule...** uses the date from Add URL, or shows a time box of its own. The speed limit from Add URL applies to all three. The old "paused" behaviour is still there in the job's context menu (Pause and Resume).

## Playlists (1.11.0)

Paste or drop a playlist link (`youtube.com/playlist?list=...`, or a watch link when you choose the whole playlist) and the Playlist window opens. The extension popup and the right-click entry on a playlist link send the playlist link to the same window (NeoDM 1.11.0 or newer must be running).

- Only real, finite playlists are accepted: list ids that start with PL, UU or OLAK5uy_ and have the right length. Mixes (RD...), Watch Later, Liked videos and anything malformed are refused with a plain message, because they are endless or need a sign-in.
- **Step 1: Load playlist** reads the list with the helper's flat listing (title, id, length, channel, availability, thumbnail address). Sizes and formats are NOT read for every video: that would take hours. The helper's answer is bounded per line and in total, and at most 500 entries are kept; a longer playlist says so.
- The window is a media version of a torrent file selection. Each row has a checkbox, a thumbnail (about 120 by 68), the title, channel and length. Private, deleted, members-only, live and otherwise unavailable entries are listed with a badge, switched off and unchecked. The header shows the playlist title, the number of entries, the selected count and the total length. Select all, Select none and Invert act on the rows shown by the filter box.
- Options at the top apply to every selected video: Video quality (Best, 2160p, 1440p, 1080p, 720p, 480p, 360p), Audio quality (Best, 256, 192, 128, 64 kb/s) and Audio only with Convert to MP3. Each row can override them with its own Audio only box and a quality list (Default, a quality, or Audio only); an overriding row shows a small badge. A quality that a video does not have resolves to the closest one it has, when the job starts.
- The footer has the folder (a sub-folder named after the playlist, cleaned for Windows, with a number added when it already holds files), the package name, an optional numbering prefix (01 - Title), and **Downloads at once** (1 to 4, default 1, also in Options > Downloads). The same three buttons as the video window follow: Download now, Add to queue, Schedule...
- Each selected video becomes its own job in one package. It reads its real formats when it starts and keeps its own error if that fails. Jobs of one package start with a short gap and never more than the chosen number at once. A rate-limit answer from YouTube fails that job with its message and pauses the rest of the package instead of retrying quickly. The package and its settings survive a restart.
- The thumbnails are loaded one request each from YouTube's image servers (see PRIVACY.md), only for rows that are on screen, at most 4 at a time, https only, no redirects, 300 KB each, kept in a small bounded cache in the state folder and shown as a neutral tile while loading or when a picture is missing.

A watch link in a list of several video links offers to open the videos one by one or to queue them all with one shared quality.

## Queue behavior

Jobs use the existing download queue and its concurrency limit. Pause stops the owned helper process and its children. Resume restarts it with the same selection and lets the helper reuse partial files where supported. The UI leaves byte-range resume support unknown because this is not a promise that every YouTube format resumes by byte offset. Partial data is deleted when the job is removed. A completed file uses the existing unique-name publication and internet-zone marking.

Progress combines the selected tracks, preserving metadata sizes, exact helper totals, explicitly marked estimates and fragment counts. The second track does not reset the first track's bytes. Unknown totals are indeterminate. Merging and conversion are separate phases, and 100 percent is reserved for final publication. The details show helper phases instead of HTTP connection placeholders. Speed limits are passed when a helper starts. After changing a limit, pause and resume to apply it. Proxy settings for ordinary downloads do not currently apply to YouTube helper requests . Ambient proxy credentials are not inherited by the helper. A network that requires the configured app proxy is therefore not supported on this path yet.

Bulk import does not silently choose a YouTube quality or install a tool. It reports that you must use Add URL.

## Limits and tools

No sign-in, browser-cookie import, user extractor config, plugin loading, remote EJS download, automatic tool update, PO-token override, DRM bypass or access-restriction workaround is offered. Live, previously live and upcoming streams are not accepted. Sign-in, restricted access, rate limits and unsupported videos return copyable messages, not instructions to bypass them. YouTube changes can break a pinned version. A new NeoDM release is required to update the pins.

The app resolves its pinned helper bundle beside the installed executable. Runtime downloads, install prompts and removal of bundled files are disabled. Writable caches and temporary files remain in app state. NeoDM source is MIT, the standalone extractor is GPLv3+, Deno is MIT and the chosen ffmpeg build is GPLv2+. Exact corresponding-source and dependency notice verification is still required before redistribution. An explicitly chosen PATH ffmpeg is supported only for ordinary streams, not as a replacement for the trusted YouTube bundle.

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


Version 1.8.0 packaging is blocked pending exact helper corresponding-source and notice verification. See `BUNDLE-VERIFICATION.md`. Do not distribute an incomplete developer layout.

Both extension packages now include handoff transparently. Chrome Web Store rejection or removal remains a risk. No compliance or approval claim is made. Do not submit a misleading listing; the user controls submission, and no dashboard submission is performed by this work.
