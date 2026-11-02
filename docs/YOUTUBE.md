# Desktop YouTube downloads

NeoDM 1.8.0 accepts a single YouTube video page through desktop **Add URL**. Save only public, non-DRM videos that you own or have permission to download. Both the manually loaded and Chrome Web Store packages can send a page link to the same desktop quality dialog. Neither variant extracts YouTube media in the browser, and no new permission is added.

## Extension variants and distribution

- `extension/`, `NeoDM.crx`, `NeoDM-extension-manual.crx` and `NeoDM-extension-manual-unpacked.zip` are the manually loaded, non-store edition. The manifest version name labels it "manually loaded". Use Developer mode and Load unpacked. Off-store CRX installation may be blocked on unmanaged browsers. Remove the old manually loaded copy and load the new unpacked folder after updating.
- `NeoDM-extension-store.zip` is built only with `node tools/build-store-zip.cjs`. The builder omits the development manifest key and labels the package "store". It includes the same handoff code, popup, context-menu and on-video actions as the manual package. Tests execute the actual generated ZIP and verify page-link handoff. Use this explicitly labeled store package for any user-controlled submission.
- Google's official Chrome Web Store troubleshooting lists facilitating YouTube downloads as a reason for rejection or removal: https://developer.chrome.com/docs/webstore/troubleshooting. Forwarding a link does not exempt an extension from that policy. These are separate build-time packages, not hidden runtime behavior, reviewer detection or remote switches. Building a store variant does not guarantee store approval.

In either edition, open a supported watch, shorts or youtu.be page, open the popup and click **Send YouTube link to NeoDM**. Alternatively click the on-video Send to NeoDM button or use the dedicated right-click entry on a supported video-page link. The action sends the canonical https watch URL only, plus protocol/version metadata. It does not read or forward cookies, headers, playlist text, media fragments or browser profiles. The desktop opens its independent quality window without reading qualities or installing tools automatically. Choose there, or Cancel. Queue paused saves the chosen job without starting it.

The popup rechecks the active tab before sending. If the page changed, reopen it and try again. A pending link cannot open a second dialog. Missing or older native hosts fail without a file-download fallback. NeoDM 1.7.1 or newer must be running; restart it after updating. If the browser connection closes, the dialog cannot submit. Existing desktop Add URL also accepts embed links; the extension handoff intentionally accepts only watch, shorts and short-link pages.

## Use

1. Enter a watch, shorts, embed or youtu.be video link in Add URL. Playlist and channel links are not accepted. Tracking and playlist query fields on a video link are discarded.
2. Press **Grab download info**. A loading indicator stays visible while the bundled helper reads metadata. Cancel closes the dialog and cancels the request. Missing developer bundles report a clear error and are never downloaded at runtime.
3. Pick a video quality, or enable **Audio only** and choose an audio bitrate. The video picker contains only video formats. MP3 conversion is available only in audio-only mode.
4. Choose the file name and save folder. The default follows your configured Video or Music category. An edited folder is kept exactly. The bundled ffmpeg merges separate tracks or converts MP3, without an install prompt.
5. **Add / schedule** uses the date and speed limit from Add URL. **Queue paused** saves the job without starting it. Resume starts it when its schedule permits.

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
