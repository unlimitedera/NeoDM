# Privacy policy

NeoDM (the Windows download manager) and NeoDM Integration (the browser extension) are open source
projects created by unlimitedera. Last updated: 2026-11-02.

## Summary
- NeoDM has no accounts, no analytics, no advertising and no telemetry.
- Nothing about you or your browsing is sent to the NeoDM author or to any server run by the project.
- The browser extension only talks to the NeoDM app installed on your own computer.

## The browser extension (NeoDM Integration)
What the extension reads, locally in your browser:
- The address of a download the browser starts, its file name and type, and the page it came from (so NeoDM can name the file and send the right referrer).
- The address of direct video or audio files found on pages you visit, so the media ribbon can offer a download.
- The address of the active tab, to offer a per-site pause.

What it stores: your settings (capture on or off, file types, ribbon on or off, the two cookie options, both on by default) in the browser's extension storage, and a temporary per-session pause that the browser clears when it closes.

What it sends, and where: when you download something, the extension sends the items above to the NeoDM desktop app on your own computer through the browser's Native Messaging feature. The extension never sends them over the internet.

Request headers of a download: when you capture a download, the extension also forwards the User-Agent, Accept and Accept-Language your browser sent with it (or, for a button or menu entry, the browser's own User-Agent and language list), so the site sees the same kind of request the browser made. These are not sensitive and go only to the NeoDM app on your PC. They are not stored by the extension, not written to the capture log, and the app keeps them only with the download in its list.

Cookies, session cookies of a download (on by default): when you capture a download, NeoDM receives the cookies your browser sent with that download, so the site accepts the download. They go only to the NeoDM app on your PC, are kept encrypted only while the download runs, and can be turned off in the popup ("Pass the browser's session cookies for a download to NeoDM"). The extension reads them from the request the browser itself made for that download (the Cookie header sent to that exact address), keeps them in memory for at most 60 seconds, and never writes them to storage or to its log. Without this, some sites (file hosts that tie a link to your session) answer NeoDM with a web page instead of the file.

Cookies, authentication cookies (on by default since 1.5.9): the extension has the browser's cookies permission (a required permission; a browser disables an update that adds it until you approve it, once). For downloads where no cookie was seen in the request (the video button, the ribbon, the right-click menu and the popup list) the extension reads the cookies the browser would send to that exact download address and passes them to NeoDM. The same rules apply: local NeoDM app only, kept in memory only, not stored, not logged. You can turn this off at any time in the popup ("Include authentication cookies"); the extension then does not read cookies.

Not collected: passwords, page contents, browsing history, form data and anything from private (incognito) windows.

## The desktop app (NeoDM)
- Your download list and settings are stored on your computer in `%LOCALAPPDATA%\NDM`. Proxy passwords are encrypted with Windows DPAPI for your account.
- The files you download come directly from the servers you chose and, for torrents, from peers.
- Update check: NeoDM asks `api.github.com` for the latest release of this project at start-up and about once a day. It downloads the installer from `github.com` when you choose Update now. GitHub sees your IP address, as with any web request. Turn the check off in Options, General, "Check for updates automatically".
- Torrents: peers and trackers can see your IP address while you share a torrent. Use the proxy settings to hide it.

## Third parties
NeoDM does not share data with third parties. GitHub (updates) and the sites or peers you download from are the only outside parties your traffic reaches.

## Children
NeoDM does not collect data from anyone, including children.

## Changes
Changes to this policy are published in this repository and listed in the changelog.

## Contact
Open an issue at https://github.com/unlimitedera/NeoDM/issues.

## Video streams and the video tool (1.6.0)

- Saving a streaming video (HLS or DASH) sends requests only to the site that serves the video (the playlist, the pieces and the key addresses the playlist names), with the same kind of request your browser makes. Nothing about the video is sent to anyone else.
- The browser extension sends the address of the playlist to NeoDM on your computer, never the playlist text.
- NeoDM downloads the video tool (ffmpeg) only when you click Install (in the stream window, the completion window or Options). It is fetched once from the official build site named in `docs/THIRD-PARTY.md` and checked against a fixed fingerprint; no data about you is sent with that request beyond what any web request contains (your address, as with every download). Removing the tool in Options deletes it.


## Desktop YouTube downloads (1.7.0)

YouTube page reading and downloads happen in the desktop app. Desktop Add URL and both extension packages' explicit page-link handoff open the same desktop quality dialog. The handoff sends only the canonical page URL and protocol/version metadata locally over Native Messaging. This action does not read or send cookies, browser identity headers, page contents, manifest text or media fragments. It never calls the ordinary capture path that can include cookies. The helper connects to YouTube and its media delivery servers, which see your IP address and the video requested. The canonical public page URL, selected format IDs, chosen local file name, folder and job state are saved locally. Metadata media URLs and raw helper diagnostics are not persisted. Error messages use fixed categories so session tokens in helper output cannot reach the saved queue.

yt-dlp, Deno and ffmpeg are resolved from the installed helper bundle. The desktop app does not download or update these tools. Release preparation downloads pinned assets from their official publishers, not from the installed app. The helper is not asked to read browser cookies, profiles, logins or user configuration. No sign-in, DRM or access restriction bypass is offered. See `docs/THIRD-PARTY.md` and `docs/BUNDLE-VERIFICATION.md` for the unresolved redistribution review.

Both extension packages include the popup, context-menu and on-video YouTube page-link handoff with unchanged permissions. The store builder removes only the development manifest key and retains handoff transparently. Both are version 1.8.0. There is no reviewer detection, runtime ID switch or remote flag. The action sends a canonical page URL locally to the desktop quality chooser, without cookies and without starting a download automatically. The Chrome Web Store may reject or remove the package for facilitating YouTube downloads. The user controls submission; no store approval or compliance is claimed.

## YouTube playlists (1.11.0)

The Playlist window shows a small picture for each video. Each picture is one request from your computer to a YouTube image server (`i.ytimg.com`, `i9.ytimg.com` or `img.youtube.com`; since 1.13.0 NeoDM asks for the plain JPEG addresses of the picture and tries up to three sizes, and the single-video YouTube window shows the same kind of picture next to the title), made only for the rows you scroll to, through the proxy you set in Options when there is one. It carries no cookie or sign-in. NeoDM keeps these pictures in a small cache inside its own state folder (at most 600 files and 24 MB, files older than 14 days are removed) and deletes nothing else. The playlist listing itself is read by the bundled helper as described above. The extension's right-click entry and popup send only the playlist address to NeoDM, never a list of videos.

## The NeoDM browser (1.12.0)

When a download fails and the site may need your sign-in, "Open in NeoDM browser" opens a window that uses the Microsoft Edge WebView2 component with its own private profile folder inside NeoDM's state folder (`browser-data`). It never reads or touches the profile of your web browser. The first download you start in that window is cancelled there and handed to the same failed download: the address, the file name, the page address, the browser's User-Agent and the cookies the window holds for the download's own host (at most 100 cookies and 8192 characters). Cookies stay on this computer, go only to that host, are kept in the download queue only in their protected form like other captured cookies and are never written to logs. Options > Downloads > "Clear NeoDM browser data" deletes the profile folder. Without the WebView2 runtime the window says so and offers a link to Microsoft's download page; nothing is downloaded by NeoDM itself.

## Subtitles (1.13.0)

When you open a YouTube video, NeoDM asks the same YouTube helper for the names of the caption languages in one extra helper call (no caption text is read, and nothing is sent anywhere but YouTube). Captions are fetched only when you tick a language and start the download; they come from YouTube and are saved in the download folder (or inside the video). For HLS and DASH streams the subtitle files are fetched from the same website as the stream, with the same identity as the stream download. NeoDM does not keep a list of the languages you choose beyond the job itself.

## The NeoDM browser with tabs (1.13.0)

Since 1.13.0 the NeoDM browser is a general browser window with tabs, opened from Tasks > NeoDM browser (and still from a failed download). It uses the same private profile folder (`browser-data`) and keeps your sign-ins in it between runs until you press Options > Downloads > "Clear NeoDM browser data". A download that starts in any tab is cancelled there and handed to the normal NeoDM capture window with its address, file name, size, the page address, the browser's User-Agent and the cookies the browser holds for the download's own host; the browser stays open. When a page plays a video stream or media file, a bar offers "Download this video" and hands only that address (with the page address and the same host-only cookies) to NeoDM's stream or download window. Addresses you type go to the site you named. Anything that is not a web address is sent as a search to `duckduckgo.com`, only when you press Enter in the address box or on the Home page; NeoDM sends nothing else there. Cookies never go into a log.

## The media ribbon (1.11.0, changed in 1.12.0)

Since 1.12.0 the side ribbon and the on-video button are independent. The side ribbon has a minimize button (kept per website in `ribbonMinimizedHosts`) and its × still opens the three hide options, which now apply to the side ribbon only (`ribbonHiddenHosts`). The on-video button has its own × (no menu, this page view only) and its own popup switch. Both lists hold website names only.

The × on the media ribbon and on the video button opens a menu: hide for now, hide on this website, hide everywhere. The choice is kept in the browser's own extension storage (`chrome.storage.local`, the `storage` permission the extension already had): a list of at most 500 website names (no address, path or query) and two on/off switches. Nothing is sent anywhere. The extension popup lists the hidden websites and lets you remove them or show the ribbon everywhere again.
