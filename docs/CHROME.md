# NeoDM browser integration (Chrome / Edge / Brave)

> NeoDM Integration has been submitted to the Chrome Web Store and is waiting for approval. Until it is approved, install it manually with Load unpacked, following the guide. See [EXTENSION-INSTALL.md](EXTENSION-INSTALL.md).

Original MV3 extension plus a native messaging host (`com.ndm.bridge`). MIT licensed with this project.

Extension ID: `hohmmojlhbkelkobobpkochpdempcihd` (single source of truth: `tools/extension-id.txt`).

## Signing key and ID

- One RSA-2048 key pair. The public key is in `extension/manifest.json` (`key`), so the ID is the same for unpacked loads and for the CRX. The private key is `keys/neodm-extension.pem`.
- The `.pem` is git-ignored, never inside `extension/`, the CRX or any installer payload. Back it up in a password manager or an offline copy. If it is lost, a new key means a new extension ID: every install of the native host must then be re-registered with the new ID.
- `node tools/pack-extension.cjs --init-key` creates the key only if missing (it never overwrites) and writes the manifest key and `tools/extension-id.txt`.
- `node tools/pack-extension.cjs` builds `dist/NeoDM.crx` (CRX3, `sha256_with_rsa`, deterministic, no browser needed). It refuses to run if pem-ID, manifest-ID and `tools/extension-id.txt` disagree, or if `extension/` contains anything except runtime files (manifest, background/bridge/sniff/content/popup scripts, popup.html/css, icons).
- `scripts/install.ps1` derives the ID from the manifest key, checks it against `tools/extension-id.txt`, and writes it into `bridge-config.json` and the native host manifest `allowed_origins`. It registers the host under HKCU for Chrome, Edge and Brave.

## Installing the extension

Chrome, Edge and Brave block installing a CRX from outside their store. Drag and drop shows "package is invalid" or the extension is disabled, unless Developer mode is used or an enterprise policy (`ExtensionInstallForcelist` / `ExtensionInstallAllowlist` with a hosted update manifest) is configured. So:

1. Build/publish host and app to `dist/host`, `dist/app`; run `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install.ps1`.
2. Open `chrome://extensions` (or `edge://extensions`), enable Developer mode, Load unpacked, select `extension/`. The ID shown must equal the one above.
3. `dist/NeoDM.crx` is the signed distributable (for a policy-based deployment or archiving); it is not a one-click install.

Disable other download-manager extensions to avoid duplicate captures.

The user-facing steps with screenshots are in [EXTENSION-INSTALL.md](EXTENSION-INSTALL.md) and in the app under Help > Browser extension install guide.

### Extension version check

Every request to the host carries the optional field `extVersion`, the extension's manifest version (`ping` too). The host accepts only `^\d{1,4}(\.\d{1,4}){1,3}$` of at most 24 characters, rejects other values and forwards a valid one in the pipe request to the app. Requests without it stay valid, so older extensions keep working. The app compares the version with its own (build metadata ignored). A missing, older or newer extension version shows a banner in the capture dialog (a small notice window when no dialog is shown), once per app session for each pair of versions, unless the user ticked "Don't remind me about this version" (`MutedExtensionVersions` in `settings.json`). The notice never delays the download.

The `ping` reply carries `appVersion`. The host reads it from the file version of `NeoDM.exe` (the `AppPath` in `bridge-config.json`, else `..\app\NeoDM.exe`) and leaves it out when the file is not found, so a ping never starts or wakes the app. The popup shows "NeoDM app X, extension Y. Open Help > Browser extension install guide in NeoDM." after Test connection when the two differ. An older host sends no `appVersion` and the popup shows nothing.

## Media ribbon

`background.js` listens to `chrome.webRequest.onHeadersReceived` (response headers only) and keeps a per-tab list in `chrome.storage.session` (survives MV3 worker restarts; cleared when the tab navigates, including single-page navigation, or closes). The content script (top frame) shows a small ribbon in a closed shadow root: type, quality when known, size, Download with NeoDM, dismiss (×). The toolbar badge shows the number of detected items. Popup toggle "Show media ribbon" turns it off.

What counts as media:

- `Content-Type` `video/*` or `audio/*`, status 200/206, total size >= 512 KB (Content-Length, or the total in Content-Range). HLS/DASH manifests and segments (`.m3u8`, `.mpd`, `video/mp2t`, `.ts`, `.m4s`, DASH/HLS content types) are never offered.
- `googlevideo.com/videoplayback`: per-chunk `range`/`rn`/`rbuf` are stripped so all chunks of a stream are one entry, and `clen` is the size. Only formats known to carry audio and video in one file (itag 18, 22, 37, 59, 43, 45, ...) are shown as a complete Video. Audio-only (e.g. itag 140/251) and video-only (e.g. 137/248, or any unknown itag) streams are shown with that label, marked "not a complete video", and are never presented as the full video.
- Direct `<video>`/`<audio>` `src`/`<source>` URLs (http/https). `blob:`, encrypted (`mediaKeys`) and HLS/DASH sources are skipped.

The ribbon sends only an item id; the worker looks up its own sniffed URL, so a page cannot make the extension download arbitrary URLs. Request sent over the unchanged native contract: `{url, fileName, referer, method:"GET", cookie}`; `fileName` is the sanitized page title + extension (+ quality / "(audio only)" / "(video only)"), empty for DOM sources that already have a file name; `referer` is the tab URL.

Limits: YouTube's highest qualities are DASH (separate audio and video) and are not downloadable without muxing, which NeoDM does not do; only the muxed progressive formats YouTube still serves (e.g. itag 18 or 22, when the site serves them; not verified against live YouTube) can appear as complete videos, and signed `googlevideo` URLs may expire or be refused without the original cookies/headers. DRM (Widevine/EME) content cannot be downloaded. HLS/DASH-only sites are not supported. Ads are fetched the same way and may show up as extra entries. Only the top frame shows the ribbon; media loaded by iframes is still sniffed per tab.

## Capture, timeouts and cancel

Browser-download capture is on by default (popup; existing users who never chose are switched on once, an explicit off is kept). When a download is captured the extension cancels and erases the browser download immediately (`downloads.onCreated`, then `cancel`, `removeFile` when a file already exists, and `erase` for the history entry). The browser download therefore never runs next to NeoDM, and the extension asks NeoDM only after that. The desktop app shows an options dialog (in front of the browser) and replies only after the user decides (up to 5 minutes), so the extension waits 330 s for download requests (12 s for ping) and the host waits 320 s for the app. A durable `{ok:true,id}` ACK means NeoDM owns the download. If the user presses Cancel in NeoDM (`{ok:false,cancelled:true}`) the download is discarded on purpose. If the app is unreachable, times out or answers with another error, the extension restarts the browser download from the same URL with `downloads.download`. The safety rules are unchanged: only direct HTTP(S) GET downloads, never incognito, blob, data or extension-initiated downloads. The popup also has per-type switches (Video, Audio, Images, GIF, Archives, Programs, Documents, Other; images and GIF off by default) that apply to the ribbon and to capture, and a session pause for one site or all sites (`storage.session`, resets when the browser closes). While the native port is open Chrome keeps the service worker alive.

Cookies: two separate things, never logged.
- Session cookies of a captured download (1.5.4, on by default, popup switch "Pass the browser's session cookies for a download to NeoDM", stored as `sessionCookies` in `chrome.storage.local`; existing users get it on at the schema 3 migration, an explicit off is kept). `webRequest.onSendHeaders` with `['requestHeaders', 'extraHeaders']` (without `extraHeaders` Chrome hides the Cookie header; no `cookies` permission is needed and HttpOnly cookies are included) records the Cookie and Referer of every hop of a request next to the hop in the chains table. When the browser download that follows is captured, the Cookie header of the FINAL hop (the address NeoDM will request) is sent as the optional `cookie` field. The header is in memory only: at most 8192 characters (a longer or control-character header is not forwarded, never cut), 200 headers, 60 seconds each. When no Cookie header was seen, nothing is sent from this path. Media paths (popup list, ribbon, video button, right-click menu) do not use it, because those addresses were not requested by the browser as the download; they are not covered by this feature. The native host and the app accept a `cookie` of up to 32768 characters and refuse control characters.
- Authentication cookies (older opt-in): the optional `cookies` permission and the popup option "Include authentication cookies". It is only used when no browser header was seen for the download, or when the session-cookie switch is off.

## Sites NeoDM cannot fetch (1.5.4)

Some sites hand out a download link that works once, only for the browser session, or only with a cookie. The browser has already made its request when the extension cancels the browser download, so NeoDM's own request for the same address can be answered with a web page (an "expired link" page with status 200). The pieces below keep that from ending as a broken file.

- **Browser size.** The capture request carries the optional integer `size`: `DownloadItem.totalBytes`, else `fileSize`, only when it is between 1 and 2^50 (the host and the app both reject other values; absent means unknown, so older extensions and hosts keep working). The capture dialog shows it at once as "Size: 103 MB (size reported by the browser)", and the engine receives it as `ExpectedSize` (with the browser's content type as `ExpectedMime`; both are stored in `queue.json`, older files without them load unchanged).
- **A page is never saved as the file.** For a request that expects a file (a size hint, a browser type that is not HTML, or a file name whose extension is not html, htm, xhtml, shtml, php, asp, aspx, jsp, cfm or cgi) the engine refuses a reply when the content type is `text/html` or `application/xhtml+xml`, when the declared length (Content-Length or the total in Content-Range) is under half of the expected size, or when the first bytes start like an HTML document. The job fails with the error code `wrong-content`, the message "The site sent a web page instead of the file (about 32 KB instead of 103 MB). The link may have expired or may only work in the browser.", nothing is saved under the file name and the temporary data is deleted. A name the user did not choose (an open, automatic name) carries no information and is not judged by its extension. The download window and the right-click menu then offer "Open the page in the browser" (https referer only).
- **Hand back.** The capture dialog has "Download in browser instead". The app replies `{"ok":false,"error":"...","cancelled":false,"handBack":true}`; the native host passes `handBack` through (it must be a boolean). The extension treats it like an error without the error text: it restarts the browser download exactly once (`downloads.download`), shows a neutral status, and adds the site to its list. A link that is really single use has been consumed by the browser's first request, so the restart can receive the same expired page; the extension cannot repair that.
- **Sites left to the browser.** `chrome.storage.local.browserHosts` holds registrable host names only (at most 100, newest kept). `download1336.mediafire.com` and `www.mediafire.com` both map to `mediafire.com`; a small public-suffix rule keeps three labels under common two-level suffixes such as `co.uk` and `com.au`. A capture of a listed site is skipped with the log reason `browser-site` and the browser keeps the download. The popup lists the sites in a collapsed "Sites left to the browser" section with a Remove button each, and shows "Capture this site again" when the current site is listed.
- **Sites found later.** When a captured download fails with `wrong-content` after the dialog is long closed, the app appends the host name to `browser-sites.txt` in its state folder (under a named mutex, at most 100 lines). Each time the extension handles a capture, and when the popup opens or removes a site, it sends `{"action":"browser-sites"}`; the native host answers `{"ok":true,"sites":[...]}` from that file and deletes it, without starting the app. The extension normalises the names, adds them to `browserHosts`, and ignores any failure (an older host just answers with an error).
- **The same download started twice.** A second `onCreated` for the same address while the first is being handled, or within 5 s after it finished, is cancelled and erased silently (log reason `duplicate`): no second dialog and no browser restart. A download that the extension itself restarts is recognised by its address and left alone.
- **The browser's Save as window.** With "Ask where to save each file" on, Chrome 155 opens its Save as window right after the file name is decided, and cancelling the download afterwards does not close it (measured with a real window: the window stayed open next to NeoDM's dialog although the download was cancelled and erased). So `onDeterminingFilename` answers later: the listener returns `true` and the extension calls `suggest()` (no change) only after the capture decision. A captured download is cancelled while the browser is still waiting, so the window never opens; every other outcome releases the browser at once, and an 8 s safety timer releases it whatever happens.
- **Recent downloads** in the popup show the browser size (for example "103 MB") when it is known.

Tests: `tests/extension-v154-tests.cjs` (worker and popup, mocked browser), `tests/NDM.Tests/WrongContentTests.cs` (engine rules against a one-time-link server), `src/NDM.App/Tests/Security/CaptureFactsTests.cs` (wire format, dialog, list), the host `--self-test`, and `tests/extension-v154-browser-smoke.cjs` (real Chrome with `tests/helpers/mediafire-mock.cjs`, run by hand).

## Native contract

Host name `com.ndm.bridge`; stdio frames: 4-byte little-endian UTF-8 length + JSON; max 65,536 bytes. First argument must equal `chrome-extension://<ID>/`. `{"action":"ping"}` returns `{"ok":true,"host":"com.ndm.bridge"}` and has no effect on the queue. Downloads validate http(s) URL/referer, reject credentials in URLs, non-GET, control characters and path-like `fileName`. Pipe `NDM.Downloads` (current user only): request `{"url","fileName","referer","cookie"}`, reply one JSON line `{"ok":true,"id":"..."}` or `{"ok":false,"error":"..."}`. The app saves the job before it acknowledges. Host: 10 s to connect/start the app, 320 s for the user's decision.

## Tests and tools

- `npm ci --prefix tests --ignore-scripts --no-fund --no-audit`, then `node --test tests/extension-tests.cjs tests/extension-advanced-tests.cjs` (or `npm test --prefix tests`).
- Real-browser smoke (Edge/Chrome/Brave if installed, isolated temp profile): `node tests/extension-browser-smoke.cjs`.
- `node tools/make-icons.cjs` regenerates the icons; `dotnet run --project src/NDM.NativeHost -- --self-test` for the host.
- `scripts/uninstall.ps1` removes only the `com.ndm.bridge` HKCU registrations.

## Browser connection check
NeoDM checks the browser side itself (see "Browser connection watchdog" in DESIGN.md). The native host exits when the browser closes its port, so the app cannot keep it running. What can break is the registration (a missing or stale manifest, a stale per-user key that hides the machine key, a manifest without the extension id), the app's pipe listener, or the host failing to start. Help > Browser connection shows each check, `Check now` runs it at once and `Repair` repeats the repairs without the 10 minute limit. Options > General has the checkbox "Check the browser connection automatically".
