# NeoDM Integration on Firefox Add-ons (addons.mozilla.org)

How to put the Firefox build on Mozilla's store. It is free and needs a free Mozilla account. Mozilla signs the add-on, so Firefox users can install it with one click and Firefox updates it by itself.

## Files

- Upload `NeoDM-extension-firefox.zip` (made by `node tools/build-firefox.cjs`, also by `build.ps1`; it is in `dist\` and on the release page). It holds readable source only (no bundler, no minifier), so Mozilla's reviewers read exactly what runs.
- The add-on id is `neodm@unlimitedera`. It is inside the manifest and never changes; a signed update must keep it.
- The version is the NeoDM version (2.0.0 now). Mozilla refuses a version number it has seen before, so every NeoDM release needs a fresh zip.
- Minimum Firefox: 140. Data collection: "none" is declared in the manifest.

## Submit it (first time)

1. Sign in at <https://addons.mozilla.org/developers/> with the Mozilla account.
2. Choose **Submit a New Add-on**.
3. Choose where it will be distributed:
   - **On this site** (listed): the add-on gets a public page, Mozilla reviews it, and users find it by search. This is the normal choice.
   - **On your own** (unlisted): Mozilla only signs it and gives you an `.xpi` to share yourself (for example on the GitHub release). Updates are then yours to host.
4. Upload `NeoDM-extension-firefox.zip` and wait for the automatic check. Fix any error it lists. Warnings about broad host access are expected, see the reviewer notes.
5. "Do you need to submit source code?": answer **No**. The zip is the source. (Source upload is only needed when the code is generated, minified or bundled.)
6. Fill in the listing from the text below, add the screenshots, set the license to MIT, add the privacy policy link, and put the reviewer notes in **Notes to Reviewer**.
7. Submit. A listed add-on can wait hours to days for review, longer when Mozilla asks a question. Answer on the same page.
8. When it is approved, the page shows the install button. Put that link on the release page and in `docs/INSTALL.md`.

For every later NeoDM version: **Upload New Version** on the add-on's page, upload the new zip, paste the release notes, submit.

## Signed .xpi for the release page

After Mozilla signs a version (listed or unlisted), download the signed file from the version's page and run:

    node tools/build-firefox.cjs --signed path\to\neodm-2.0.0.xpi

The script checks that the file has the id `neodm@unlimitedera`, the same version as NeoDM and Mozilla's signature files, then copies it to `dist\NeoDM-extension-firefox.xpi`. Attach that file to the release next to the zip.

## Listing text

**Name** (50 characters at most): NeoDM Integration

**Summary** (250 at most): Send downloads, videos, streams and subtitles from Firefox to NeoDM, the free download manager for Windows. Needs the NeoDM app.

**Description:**

NeoDM Integration connects Firefox to NeoDM, a free download manager for Windows. You need the NeoDM app (free, MIT license) installed on the same PC: https://github.com/unlimitedera/NeoDM

What it does
- Hands the files you download in Firefox to NeoDM, which splits them into several connections and can pause and resume them.
- Shows a small ribbon next to videos and audio on a page, with a Download button. The ribbon and the corner button can be hidden for one website, for every website, or minimized.
- Finds streams (HLS and DASH playlists) and subtitles on a page and sends them to NeoDM.
- Sends a YouTube video page to NeoDM so you can pick the quality there.
- Refresh link: when a download's link has expired, NeoDM opens the page and the add-on finds the fresh link of the same file.
- Tells NeoDM which Firefox it is and which version it is, so NeoDM can warn you when the add-on is out of date.

Your privacy
- The add-on collects no data and has no server of its own. Everything it finds stays in Firefox until you press Download; then it goes to the NeoDM app on your own PC through Firefox's native messaging.
- It does not send anything to the internet by itself.

Free and open source: https://github.com/unlimitedera/NeoDM

**Categories** (up to two): Download Management; Photos, Music & Videos.

**Tags:** download manager, video downloader, streams, subtitles, NeoDM.

**Support site:** https://github.com/unlimitedera/NeoDM/issues
**Homepage:** https://github.com/unlimitedera/NeoDM
**License:** MIT License
**Privacy policy link:** https://github.com/unlimitedera/NeoDM/blob/main/PRIVACY.md
**Needs additional software:** yes, the free NeoDM app for Windows (tick the box and say so).

## Screenshots to use

From `docs/images` and the Firefox test run (`artifacts\firefox-shots`, made by `tests\extension-v200-firefox-smoke.cjs`):

1. `firefox-ribbon.png`: the ribbon and the corner button on a video page in Firefox.
2. `firefox-minimized.png`: the minimized ribbon.
3. `docs/images/01-main-window.png`: NeoDM's main window.
4. `docs/images/04-add-download.png`: the NeoDM dialog that opens when Firefox hands a download over.
5. `docs/images/10-progress-window.png`: a download in progress.

## Notes to Reviewer (paste this)

NeoDM Integration is the Firefox side of NeoDM, a desktop download manager for Windows (MIT license, https://github.com/unlimitedera/NeoDM). The code in the zip is plain, readable JavaScript; nothing is minified or generated, so no separate source upload is needed. It is the same code as the Chrome build, plus `firefox-shim.js` (makes `importScripts` do nothing, because Firefox loads the background files from the manifest's `scripts` list, and makes `chrome` and `browser` the same object).

- **Native host.** `nativeMessaging` talks to `com.ndm.bridge`, a small program installed with the NeoDM app. Firefox starts it through the standard registry entry, and the host manifest's `allowed_extensions` lists only `neodm@unlimitedera`. The host passes a download request (address, file name, referer, optionally cookies for the same site) to the NeoDM app on the same PC over a local pipe. Nothing is sent to any server.
- **Why `http://*/*` and `https://*/*` host access.** Downloads can start on any website, so the add-on must see the response headers (`webRequest`) and the cookie header of the request it hands over, and its content scripts show the ribbon on any page that has a video or audio. It reads nothing for any other purpose and stores nothing outside the browser.
- **`downloads`.** To notice a file the browser is about to save, cancel that browser download and let NeoDM fetch it (or give it back to Firefox if NeoDM is not running or the user cancels).
- **`cookies` and `tabs`.** `cookies`: send the signed-in cookies of the same site with a handed-over download when the user has that option on (a switch in the popup). `tabs`: know the page address a download came from, and keep a per-tab list of media.
- **`contextMenus`, `storage`.** The right-click "Download with NeoDM" entry and the add-on's own settings.
- **YouTube hand-off.** On a YouTube video page the ribbon offers "Download with NeoDM". It only sends the page address to NeoDM, which opens its own quality picker. The add-on does not download anything from YouTube, does not read or alter the player and bypasses no protection; a protected or live stream is listed with the reason and never sent.
- **Content scripts.** `hooks.js` runs in the page world (`world: MAIN`) only to observe media requests (it calls the original functions first and changes nothing) and reports through a random private event channel. `widget.js` only reports where a site's own "I am not a robot" box is on the screen, and only for a page the NeoDM app has asked about; it does not solve or bypass anything.
- **Data collection.** `data_collection_permissions` is `none`: the add-on has no account, no analytics and no server.
- To test without the NeoDM app: the add-on loads and shows its ribbon; the popup then says "native host unavailable" and downloads stay with Firefox.

## Temporary install (testing, no store needed)

1. Install NeoDM (its setup registers the Firefox connection).
2. In Firefox open `about:debugging#/runtime/this-firefox`.
3. Click **Load Temporary Add-on** and pick `NeoDM-extension-firefox.zip` (or `manifest.json` from the unzipped folder).
4. Open the toolbar button and click **Test connection**. Firefox forgets the add-on when it closes.
