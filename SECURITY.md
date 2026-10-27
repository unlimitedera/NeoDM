# Security policy

## Supported versions
Only the latest release receives security fixes.

## Reporting a vulnerability
Do not open a public issue for security problems. Use the "Report a vulnerability" option on the repository's Security tab on GitHub (private advisory). Include what you found, how to reproduce it, and the NeoDM version.

We acknowledge reports within a few days. Give us time to fix the issue before you disclose it.

## Scope notes
- The browser extension talks to the app only through a native messaging host. The host accepts requests only from the NeoDM extension and connects to the app over a current-user-only pipe.
- Proxy passwords are stored encrypted with Windows DPAPI for the current account.
- The extension's signing key is never part of this repository.
- Updates: the updater installs only a release whose `SHA256SUMS.txt` carries a valid signature (`SHA256SUMS.txt.sig`, ECDSA P-256) under the public key built into the app, whose installer matches the signed hash, whose MSI says it is the NeoDM product in the released version and newer than the running one, and which was downloaded from the official GitHub release address (redirects only to GitHub's file hosts over https). The verified file is held open until the installer takes over. The private signing key (`keys/neodm-update-signing.pem`) is never in the repository; losing or leaking it is handled as described in `docs/UPDATES.md`. The MSI and the executables are not Authenticode-signed because the project has no code-signing certificate.
- Installer: it only installs under Program Files, because setup runs helper scripts from the install folder with administrator rights. The folder is set to administrator-only write access and the helper refuses links.
- When the browser extension captures a download it forwards the Cookie header the browser sent to that exact download address (extension setting "Pass the browser's session cookies for a download to NeoDM", on by default). The header is read with `webRequest` and kept in the extension worker's memory only: at most 8192 characters per header, 200 headers, 60 seconds, never in extension storage or in the capture log. It travels only over Native Messaging to the app on the same PC; the native host and the app refuse control characters and values over 32768 characters. The app drops it on a redirect to another origin or from https to http.
- The extension also forwards the browser's User-Agent, Accept and Accept-Language for a captured download. The helper and the app accept them only as printable ASCII (512, 512 and 128 characters at most) and refuse control characters, so nothing but plain text can reach a request header. They are held in the extension's memory only and are not written to its storage or its log. NeoDM's compatibility attempts change only the shape of the request (Range, identity, referer); they never change the address or the route of the connection, never answer challenges and never bypass a sign-in.
- Since 1.5.9 the extension holds the `cookies` permission as a required permission (with the existing all-sites host access). For a download that has no captured Cookie header it asks `chrome.cookies.getAll` for the exact request URL, builds a Cookie header (8192 characters at most, no control characters), keeps it in memory only and passes it to the app over Native Messaging. It is on by default and can be turned off in the popup ("Include authentication cookies"); turning it off stops the lookups, it does not remove the permission.
- Cookies a browser sends with a download are stored in the download list protected with Windows DPAPI for the current account (not as plain text) and removed when the download ends. The Referer of a download is not passed to another site on a redirect, and a redirect from https to http is refused.
- Finished downloads get the Windows "downloaded from the internet" mark (Zone.Identifier) unless you switch that off in Options > Downloads.
- Capture dialogs warn when the address is on this computer or the local network. A page can choose the address of a download, so check it before you accept.
- The browser connection pipe is created as the first instance with current-user-only access; at most 8 capture dialogs wait at once.
- The extension keeps the `tabs` permission: the popup and the right-click entries read the address and title of the current tab. Chrome also gives those fields to extensions that have host access to the page, and the extension has it for http and https, so the permission is probably redundant; removing it was not tried in a browser matrix, so it stays rather than risk breaking the popup on some page.
- Not covered: Authenticode signing (needs a purchased certificate); confining the file paths saved in `queue.json` (an attacker who can edit that file already runs as you); a warning before a torrent overwrites an existing file (library behaviour).

## Browser extension: page-world hooks and frames (1.5.8)

The extension runs a content script in every frame of every http(s) page, and a second script (`hooks.js`) in the page's own JavaScript world (`"world": "MAIN"`, `document_start`). The second script exists so each media request and stream can be tied to the video element that uses it.

What the hooks do: they wrap `fetch`, `XMLHttpRequest`, `URL.createObjectURL`, `MediaSource.addSourceBuffer`, `SourceBuffer.appendBuffer`, the `src` and `srcObject` of media elements, `play`, `load` and `setMediaKeys`. Each wrapper calls the original first with the original `this` and arguments and returns what it returned. Names, lengths and `toString` of the wrapped functions are kept. Any internal error, or a prototype the page made unwritable, switches all hooks to pass-through and is reported once. The hooks never copy media bytes (only counts), read playlist text only up to 512 KB, and keep capped tables.

Threat model:

- The hooks cannot reach any extension API. They talk to the isolated content script through one private channel: a `CustomEvent` whose name and token are random values the content script made for this page load and handed over in a data attribute of `<html>` (read once, then removed). The content script ignores every message without that token, with the wrong shape, or larger than 64 KB. It never evaluates anything from a message and builds only plain data (addresses, counts, a playlist summary).
- A page can read that token (it runs in the same world as the hooks) and can send fake messages. The most a page can do with that is fake detections for its own videos: an entry in the list, a wrong quality label, or a hidden entry. It cannot start a download: a download needs a click on the closed shadow root button or the popup, is checked again by the worker (the sender must be this extension and an http(s) frame of the tab, or an about:blank/srcdoc frame with an http(s) origin), and every address still goes through the same URL validation (http or https, no credentials, GET only) before the native host sees it.
- Sub-frames may send only the media actions (`media-list`, `media-dom`, `media-event`, `media-hooks`, `media-download`) with their own tab; every other action stays limited to the popup. Messages from a page frame larger than 200 KB are dropped. There is no `externally_connectable`.
- The button and the picker live in a closed shadow root; page scripts cannot read or click them (events they dispatch have `isTrusted` false and are ignored).
- Playlists and protected content: streams are listed and never sent to the app before it can handle them. NeoDM does not try to get around DRM; a protected video is shown as "cannot save".
- Limits: players inside closed shadow roots cannot be found; requests made inside Web Workers are not seen by the hooks; browser pages and PDF viewers cannot be entered by the extension at all.
