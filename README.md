<div align="center">

<img src="docs/images/logo.png" alt="NeoDM logo" width="96" height="96">

# NeoDM

**A free, open source download manager for Windows.**

Segmented HTTP/HTTPS downloads, torrents and magnet links, and a browser extension for Chrome, Edge and Brave.

[![Latest release](https://img.shields.io/github/v/release/unlimitedera/NeoDM?style=flat-square&color=00695c)](https://github.com/unlimitedera/NeoDM/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue?style=flat-square)](LICENSE)
[![Platform: Windows 10/11](https://img.shields.io/badge/platform-Windows%2010%2F11-0078d4?style=flat-square)](#installation)
[![.NET 8](https://img.shields.io/badge/.NET-8-512bd4?style=flat-square)](https://dotnet.microsoft.com/)
[![Downloads](https://img.shields.io/github/downloads/unlimitedera/NeoDM/total?style=flat-square&color=00695c)](https://github.com/unlimitedera/NeoDM/releases)

[**Get it on the Chrome Web Store**](https://chromewebstore.google.com/detail/ojbaibjfbdjodgmfbiajkigpanbdkiid) · [**Download**](https://github.com/unlimitedera/NeoDM/releases/latest) · [Features](#features) · [Install](#installation) · [Browser extension](#step-2-install-the-browser-extension) · [Extension install guide](docs/EXTENSION-INSTALL.md) · [Build](#building-from-source) · [Changelog](CHANGELOG.md)

<br>

<img src="docs/images/01-main-window.png" alt="NeoDM main window" width="860">

</div>

---

## Overview

- Splits one file across up to 32 connections and gives idle connections new work as others finish.
- Saves resume state, so pause and resume work after a restart. If the file changes on the server, the download restarts instead of being corrupted.
- Handles HTTP/HTTPS downloads, torrents and magnet links, with proxy support, scheduling and speed limits.
- Includes an extension for Chrome, Edge and Brave that catches browser downloads and detects direct video and audio files.
- MIT licensed. No accounts, telemetry, licence checks or ads.
- Runs in the system tray, starts with Windows and can update itself after you confirm.

NeoDM is an open source project created by unlimitedera.

---

## Screenshots

| | |
|---|---|
| ![Main window](docs/images/01-main-window.png) | ![Dark theme](docs/images/02-main-window-dark.png) |
| Main window (light) | Main window (dark) |
| ![Right-click menu](docs/images/03-context-menu.png) | ![Progress window](docs/images/10-progress-window.png) |
| Right-click menu on a download | Per-download progress window |
| ![Add download](docs/images/04-add-download.png) | ![Remove dialog](docs/images/11-remove-dialog.png) |
| Add a download (schedule, speed limit) | Remove dialog with optional file deletion |
| ![Options](docs/images/05-options.png) | ![Downloads options](docs/images/07-options-downloads.png) |
| Options: General | Options: Downloads and connections |
| ![Proxy](docs/images/08-options-proxy.png) | ![Torrent options](docs/images/09-options-torrents.png) |
| Options: Proxy | Options: Torrents |
| ![About](docs/images/06-about.png) | ![Tray menu](docs/images/12-tray-menu.png) |
| About window with version and changelog | Tray icon menu |
| ![Capture dialog](docs/images/13-capture-dialog.png) | ![Extension popup](docs/images/14-extension-popup.png) |
| Capture dialog for a download taken from the browser | Browser extension popup |

---

## Themes

Pick a theme in Options, Appearance: Light, Dark, System (follows Windows), Midnight, Graphite, Ocean, Forest, Rose or Paper. A theme applies to every window at once, and you can set your own accent colour.

| | | |
|---|---|---|
| ![Midnight](docs/images/themes/Midnight.png) | ![Ocean](docs/images/themes/Ocean.png) | ![Forest](docs/images/themes/Forest.png) |
| Midnight | Ocean | Forest |
| ![Rose](docs/images/themes/Rose.png) | ![Graphite](docs/images/themes/Graphite.png) | ![Paper](docs/images/themes/Paper.png) |
| Rose | Graphite | Paper |

## Languages

NeoDM is available in English, Arabic, Spanish, French, German, Brazilian Portuguese, Russian, Turkish, Simplified Chinese and Japanese. Pick the language in Options > General > Language. The default, "System default", follows the Windows display language and falls back to English. A new language applies to every open window at once, without a restart. Arabic mirrors the whole interface (right to left) and keeps URLs, paths and numbers left to right. Numbers use the digits 0-9 in every language.

| | |
|---|---|
| ![Main window in Arabic, Midnight](docs/images/arabic-main-midnight.png) | ![Options with the language list open, Light](docs/images/arabic-options-language-list-light.png) |
| Main window, Midnight | Language list, Light |
| ![Progress window in Arabic, Light](docs/images/arabic-progress-light.png) | ![Capture dialog in Arabic, Midnight](docs/images/arabic-capture-midnight.png) |
| Progress window, Light | Capture dialog, Midnight |

The Arabic translation was written with AI help and has not been reviewed by a native speaker, so Options shows a note under the language list. Corrections are welcome. Other languages are added by one JSON file per language: the app already knows Spanish, French, German, Brazilian Portuguese, Russian, Simplified Chinese, Japanese, Turkish, Hindi, Italian, Korean, Indonesian, Dutch, Polish and Persian, and shows each one in Options as soon as its file exists. See [docs/TRANSLATING.md](docs/TRANSLATING.md) for the steps and [docs/translation-glossary.md](docs/translation-glossary.md) for the terms.

The browser extension follows the browser's UI language (the same ten languages), not the NeoDM setting. The installer and its shortcuts are English in this release.

## Features

### Download engine
- Segmented downloading: one file is split across several connections (1 to 32) to use more of your bandwidth.
- Dynamic segmentation: when a connection finishes early, the largest remaining part is split so idle connections keep working.
- Safe resume: pause and resume survive restarts. The file is validated against the server's identifiers, and a changed file restarts safely instead of being corrupted.
- Automatic fallback to a single stream for servers that do not support ranges.
- Smoothed speed, time remaining, resume capability and live per-connection telemetry.
- Retries, request timeouts, and a persistent queue that survives restarts.
- Concurrent downloads limit (1 to 16), per-download and per-job speed limits, and scheduling for a later time.
- File names: unsafe characters are replaced, and a name collision never overwrites an existing file.

### Interface
- Nine themes in Options > Appearance, shown as a gallery with live swatches and an instant preview: Light, Dark, System (follows Windows), Midnight, Graphite, Ocean, Forest, Rose and Paper. You can also set a custom accent colour. Screenshots are in `docs/images/themes`.
- Ten languages with a language option, live switching and right-to-left layout (see Languages).
- Compact menus and toolbar, sortable download list, and a sidebar with filters (All, Downloading, Paused, Completed, Failed, Queued, Scheduled).
- A separate Torrents section (All torrents, Downloading, Seeding, Completed).
- Search, bulk URL import, Pause all / Resume all, Remove completed.
- Per-download progress window with three tabs: Download status, Speed Limiter and Options on completion. It shows the URL, status, size, downloaded, transfer rate, time left, resume capability and a per-connection table.
- Download complete dialog with Open, Open folder and Close.
- System tray icon. Closing the window keeps NeoDM running in the background. NeoDM starts with Windows, and you can exit it from the tray menu. Finished downloads are listed in the tray menu with the last five files, and a notification appears when a download finishes (Options > Downloads).

### File management
- Right-click menu on downloads: Open, Open with, Open folder, Copy URL, Copy file path, Move / Rename, Properties, Resume, Stop, Refresh address, Redownload, Set speed limit, Schedule, Remove, Delete from disk.
- Remove dialog with an optional checkbox to also delete the downloaded files from disk, or keep them.
- Category folders (on by default): downloads go into Programs, Compressed, Music, Video and Documents subfolders of your Downloads folder, and you can change each location.

### When downloads finish
- Per-download options on completion: show the complete dialog, exit NeoDM, or turn off the computer (shut down, restart, sleep, hibernate, lock, with an optional force flag).
- Global completion actions, including playing a sound, opening the folder, running a program, and power actions.
- Every power action shows a countdown with a Cancel button first.

### Browser integration (Chrome, Edge, Brave)
- Capture downloads started in the browser and send them to NeoDM.
- When a download is captured, a dialog lets you choose Start now, Add to queue or Schedule. It shows where the file will be saved and lets you change the folder. The progress window then stays open until you close or minimise it.
- Media ribbon on video and audio pages that offers to download the media it detects (direct media files; protected or segmented streams are not supported).
- Right-click menu entries to download links and media with NeoDM.
- Talks to the app through a native messaging host over a pipe that only your Windows account can use.
- The extension is Manifest V3 and keeps the same extension ID in every build.

### Compatibility
- Many sites refuse a request that does not look like a browser's. A download taken from the browser is repeated with that browser's own User-Agent, Accept and Accept-Language (together with the cookie and page address, as before). Other downloads use a browser-style User-Agent that names NeoDM at the end. Options > Downloads > Identify as lets you choose Browser, NeoDM or your own text.
- If the first answer is a refusal (400, 403, 405, 406, 409, 416, 429, 501, 503), NeoDM tries up to five times with other shapes of the same request: without the Range header, with the browser identity, with the site's own address as the page, and without a page address. The attempts are shown in the progress window under "Request attempts".
- A server that allows only one connection per link is handled by lowering the number of connections down to one. A link that works only once is not used up by a test request.
- This is ordinary request behaviour, not a way around anything: NeoDM does not hide your IP address, does not rotate identities, does not answer challenges and does not get past sign-in. If a site still says no, use Download in browser instead.

### Torrents
- Magnet links and `.torrent` files, with a file-selection dialog (per-file and per-folder checkboxes, Select all / Select none).
- Seeding, ratio, peers and seeds, per-file priorities, pause and resume, and a details window.
- Options: listening port, DHT, peer exchange, local peer discovery, UPnP / NAT-PMP, encryption, rate limits and connection limits.
- NeoDM registers itself as an available handler for `magnet:` links and `.torrent` files.
- Built on the MIT-licensed MonoTorrent library.

### Proxy
- None, HTTP, HTTPS or SOCKS5, with optional username and password.
- The password is stored encrypted for your Windows account (DPAPI), never as plain text.
- Test connection button, and separate scopes for HTTP/HTTPS downloads and torrent traffic.
- Limits: torrent DHT, local peer discovery, UPnP and UDP trackers are not sent through a proxy. They are switched off while a proxy is active, and incoming torrent connections stay direct.

### Installer
- Windows Installer package (`.msi`) with the NeoDM icon, a Start Menu shortcut and an optional desktop shortcut.
- Starts NeoDM when setup finishes and at every Windows start.
- Upgrades an existing installation in place and keeps your settings and queue. It closes a running NeoDM first.
- Registers the native messaging host for Chrome, Edge and Brave.
- Always puts the extension files (`NeoDM.crx` and an unpacked copy) in the install folder.
- Has an optional browser policy install. It only works on managed PCs. See the browser notes below.

---

## Installation

### Requirements
- Windows 10 or 11, 64-bit.
- Administrator rights for the installer, which installs to Program Files.
- Nothing else. The .NET runtime is bundled.

### Step 1: Install NeoDM
1. Download `NeoDM-Setup-1.5.8.msi` from the [latest release](https://github.com/unlimitedera/NeoDM/releases/latest).
2. Double-click it and follow the wizard: Welcome, license, install folder (default `C:\Program Files\NeoDM`), then the **Browser extension** page.
3. On the **Browser extension** page, leave "Also install through browser policy" unchecked unless you manage your PCs with browser policies. On a normal PC the browsers block that route (see the extension section below).
4. Click Install and accept the Windows permission prompt. NeoDM starts when setup finishes and appears in the system tray.

The installer copies the app, registers the native messaging host for Chrome, Edge and Brave, adds NeoDM to Windows startup, creates a Start Menu shortcut (and an optional desktop shortcut) and puts the browser extension files in `C:\Program Files\NeoDM\extension`.

To update, run the newer installer over the old one. It upgrades in place, closes a running NeoDM, keeps your settings and queue, and starts the new version. It refuses to install an older version over a newer one.

Silent install:

```
msiexec /i NeoDM-Setup-1.5.8.msi /qn
```

Optional properties: `INSTALLFOLDER="D:\NeoDM"`, `RUN_AT_STARTUP=0`, `DESKTOP_SHORTCUT=1`, `INSTALL_POLICY=1`, `NO_LAUNCH=1`.

To uninstall, go to Settings, Apps, Installed apps, NeoDM, Uninstall. Uninstalling never deletes your downloads. It leaves your settings and queue in `%LOCALAPPDATA%\NDM`.

### Browser extension

NeoDM Integration is on the Chrome Web Store: **[Get it on the Chrome Web Store](https://chromewebstore.google.com/detail/ojbaibjfbdjodgmfbiajkigpanbdkiid)**. It works in Chrome, Edge and Brave, and the store keeps it up to date. If you cannot use the store, install it manually with Load unpacked. The guide is [docs/EXTENSION-INSTALL.md](docs/EXTENSION-INSTALL.md). It has four short screenshots: [Developer mode](docs/images/extension-guide/step-developer-mode.png), [Load unpacked](docs/images/extension-guide/step-load-unpacked.png), [the folder picker](docs/images/extension-guide/step-folder-picker.png) and [the extension card](docs/images/extension-guide/step-extension-card.png).

### Step 2: Install the browser extension

The extension lets NeoDM catch downloads from your browser, show the download options dialog and show the media ribbon on video pages. It works in Chrome, Edge and Brave.

**Recommended: [install it from the Chrome Web Store](https://chromewebstore.google.com/detail/ojbaibjfbdjodgmfbiajkigpanbdkiid).** Open the link, press Add to Chrome (Edge and Brave can use the Chrome Web Store too; Edge first asks you to allow extensions from other stores), pin the button and press Test connection. The store updates the extension by itself, usually within a few hours after a NeoDM release. NeoDM 1.5.6 or newer is needed for the store copy to connect.

**Manual install (alternative).** Chrome, Edge and Brave refuse to install an extension that is not from their web store when it is added behind the browser's back on a normal (unmanaged) PC. They show "This extension is not listed in the Chrome Web Store and may have been added without your knowledge" and keep it disabled. If you cannot use the store, Load unpacked works. It takes about a minute and works in all three browsers. The extension files in the NeoDM folder are only for this manual route; NeoDM refreshes them on every update and does not touch the store copy.

The same steps, with screenshots and the Remove step for an older copy, are in [docs/EXTENSION-INSTALL.md](docs/EXTENSION-INSTALL.md) and in NeoDM under Help > Browser extension install guide. Use one copy per browser.

1. Make sure NeoDM is installed and running.
2. Get the extension folder:
   - If you used the installer, use `C:\Program Files\NeoDM\extension\unpacked`. The Start Menu entry "Install NeoDM browser extension" opens this folder and your browsers' extension pages.
   - If you did not use the installer, download `NeoDM-extension-unpacked.zip` from the [latest release](https://github.com/unlimitedera/NeoDM/releases/latest) and unzip it somewhere permanent, such as `C:\NeoDM-extension`. Do not delete or move this folder afterwards, because the browser loads the extension from it.
3. Open the extensions page in your browser:
   - Chrome: `chrome://extensions`
   - Edge: `edge://extensions`
   - Brave: `brave://extensions`
4. Turn on Developer mode (a switch at the top right in Chrome and Brave, at the left side in Edge).
5. Click Load unpacked and select the extension folder from step 2.
6. NeoDM Integration appears in the list, switched on. To keep its button in the toolbar, pin it from the puzzle-piece menu.
7. Repeat steps 3 to 6 for each browser you use.

If you previously saw the "not listed in the Chrome Web Store" block, remove that old disabled entry first, then load the unpacked folder.

Chrome and Edge may show a "Disable developer mode extensions" notice when the browser starts. Dismiss it and keep the extension. Brave usually does not show it.

To check that it works:
1. Click the NeoDM Integration button in the toolbar and press Test connection. It should say "Host connected." If it says the native host is unavailable, make sure NeoDM is installed and running.
2. In the same popup, tick Capture browser downloads to let NeoDM take over your browser's downloads.
3. Download any file. The NeoDM dialog should appear with Start now, Add to queue and Schedule, the save location and a Browse button.
4. Tick Show media ribbon on pages in the popup, then open a page with a video. A NeoDM ribbon appears on supported direct media. Protected or segmented streams are not supported.
5. You can also right-click any link and choose Download link with NeoDM.

Cookies: when you capture a download, NeoDM receives the cookies your browser sent with that download, so the site accepts it (file hosts that tie a link to your browser session need this). They go only to the NeoDM app on your PC, are kept encrypted only while the download runs, and can be turned off in the extension popup ("Pass the browser's session cookies for a download to NeoDM"). The older option Include authentication cookies stays available.

Other ways to install:
- `NeoDM-extension.crx` is the packed extension. It is in the install folder and the release assets. Chrome, Edge and Brave do not accept it by drag and drop on a normal PC. Use it where extensions are deployed by policy.
- The browser policy checkbox on the installer's Browser extension page (or `INSTALL_POLICY=1` for silent installs) writes the browser's force-install policy. It only takes effect on domain-joined or managed PCs, and the browsers then show "Managed by your organization".

Troubleshooting

| Problem | What to do |
|---|---|
| Extension is greyed out with the web store warning | Remove it and use Load unpacked as described above. |
| "Native host unavailable" in the extension | Start NeoDM, then retry. If it persists, reinstall NeoDM so the native host registration is refreshed. |
| Downloads are not captured | Tick Capture browser downloads in the extension popup, and make sure NeoDM is running (check the system tray). |
| Extension disappeared after moving the folder | Browsers load the extension from the folder you picked. Put the folder back, or load the new location. |
| Installer closes NeoDM | This is expected. Unfinished downloads are interrupted and can be resumed. |

## Project layout

| Path | What it is |
|---|---|
| `src/NDM.Core` | Download engine: segmenting, resume, queue, proxy, scheduling. No third-party dependencies. |
| `src/NDM.App` | The WPF desktop application (`NeoDM.exe`). |
| `src/NDM.NativeHost` | Native messaging host that connects the browser extension to the app. |
| `src/NDM.Torrent` | Torrent engine wrapper around MonoTorrent. |
| `extension/` | The Manifest V3 browser extension (source). |
| `installer/` | WiX installer sources and setup scripts. |
| `tools/` | Extension packing (CRX) and icon generation. |
| `tests/` | Engine, torrent and extension tests, plus an end-to-end check. |
| `docs/` | Design, browser, torrent and versioning notes. |
| `CHANGELOG.md` | Version history. |

## Building from source

Requirements: Windows, .NET 8 SDK, Node.js (for the extension tests), PowerShell, and the WiX tool (`dotnet tool install --global wix --version 5.0.2`) for the installer.

```
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1          # build, run every test suite, publish to dist\
powershell -NoProfile -ExecutionPolicy Bypass -File build-installer.ps1 # build artifacts\NeoDM-Setup.msi
```

The extension is packed with `node tools/pack-extension.cjs`. It signs with a private key in `keys/neodm-extension.pem`, which is not part of the repository. Generate your own key if you rebuild the CRX. The extension ID comes from the key in the manifest.

NeoDM checks GitHub for new releases and installs one only after you confirm. `docs/UPDATES.md` describes what is sent and how to turn the check off. Every change bumps the version in `Directory.Build.props` and adds a `CHANGELOG.md` entry, as described in `docs/VERSIONING.md`.

## Data and privacy

Full policy: [PRIVACY.md](PRIVACY.md).

- Settings and the queue live in `%LOCALAPPDATA%\NDM`.
- NeoDM has no telemetry, no accounts and no licence checks.
- Cookies the browser sent with a captured download go only to the NeoDM app on your PC, are kept encrypted while the download runs, never logged, and can be switched off in the extension popup.
- Proxy passwords are encrypted with Windows DPAPI for your account.

## Known limitations

- Windows only.
- HTTPS-proxy connections have not been tested end to end. HTTP and SOCKS5 have.
- Torrent DHT, local peer discovery, UPnP and UDP trackers are not routed through a proxy.
- The media ribbon works on direct media URLs. Encrypted, blob, HLS and DASH streams are not captured.
- The installer's in-place upgrade, silent-mode behaviour and file-type association paths were verified from the package tables but not exercised on a clean machine.
- Chrome, Edge and Brave capture was tested with mocked browser APIs and a real native-host exchange. No automated test drives a real browser session.

## Releases

Current version: 1.5.8

| Version | Date | Highlights |
|---|---|---|
| 1.5.8 | 2026-10-27 | The on-video download button works on every player the extension can see: all frames, page-level hooks tie each stream to its video, the button shows even before a file is known and explains why a video cannot be saved; HLS and DASH streams are listed (saving them comes in 1.6.0). Links, errors and paths in the download windows can be selected and copied. |
| 1.5.7 | 2026-10-27 | Downloads from sites that refused NeoDM with 400 or 403 work: the extension passes the browser's own User-Agent, Accept and Accept-Language, NeoDM tries other request shapes before it fails, lowers the connection count on servers that allow only one, and explains a refusal in plain words. New: Options > Downloads > Identify as. |
| 1.5.6 | 2026-10-23 | The NeoDM extension from the Chrome Web Store works with NeoDM: both the store copy and the manual copy are trusted. The install guide now starts with the store, and the version notice for the store copy explains that the store updates it. |
| 1.5.5 | 2026-10-21 | MediaFire and similar sites give NeoDM the real file: the extension passes the browser session cookies for a captured download (switch in the popup, on by default). |
| 1.5.4 | 2026-10-20 | Downloads from sites such as MediaFire are safe: NeoDM never saves a web page as the file, tells you the size the browser reported, lets you hand a download back to the browser with one button, and the extension remembers sites NeoDM cannot fetch. The doubled save dialog is gone. |
| 1.5.3 | 2026-10-20 | Security hardening: updates are verified with a signed checksum list, the installer only installs under Program Files, cookies are no longer saved as plain text, downloaded files get the Windows internet mark, and the browser extension and capture connection are stricter. |
| 1.5.2 | 2026-10-19 | On feeds that load videos while you scroll (Facebook), the browser extension keeps every video in the list, shows a small NeoDM button on each video (it can be switched off in the popup), and the right-click download sends the video with a real file name instead of the post's web address. |
| 1.5.1 | 2026-10-19 | The browser extension tells you which video is which (title, thumbnail, length, size, "Playing now", outline on the page), has a working right-click download entry for videos, and explains why YouTube videos cannot be captured. |
| 1.5.0 | 2026-10-19 | Language option in Options > General with ten languages including Arabic (right to left), a translation system where a new language is one JSON file, and browser extension texts through `chrome.i18n`. |
| 1.4.8 | 2026-10-19 | Finished downloads show in the tray (window entries, recent list, notification, unseen indicator), and the completion window opens in front of other windows. |
| 1.4.7 | 2026-10-18 | NeoDM checks the browser connection and repairs it (Help > Browser connection), and Options is its own menu before Help. |
| 1.4.6 | 2026-10-18 | Selected text is readable in every text box, theme and accent, and the tray icon shows the number of active downloads with a speed tooltip. |
| 1.4.5 | 2026-10-18 | Downloads that start with a form POST and redirect to the file (such as filekeeper.net) are now captured, and the extension popup lists recent capture decisions with the reason. |
| 1.4.4 | 2026-10-17 | Selected rows in every list follow the theme instead of showing the Windows grey, windows scale their content when resized, and window sizes and positions are remembered. |
| 1.4.3 | 2026-10-17 | Help > Browser extension install guide, and a notice in the capture dialog when the browser extension and NeoDM have different versions. |
| 1.4.2 | 2026-10-17 | The media list shows one entry per video instead of one per downloaded piece, and numbered stream fragments are no longer listed. |
| 1.4.1 | 2026-10-16 | Wording improvements across the app, extension and documentation. |
| 1.4.0 | 2026-10-16 | Theme gallery in Options > Appearance with instant preview: Light, Dark, System, Midnight, Graphite, Ocean, Forest, Rose and Paper, plus a custom accent colour (presets or #RRGGBB) and themed check boxes. |
| 1.3.3 | 2026-10-16 | The capture dialog's "Save to" folder is now the real save folder (Start, Add to queue and Schedule), and the capture dialog no longer times out: the browser download is only restarted if NeoDM really fails, never because you are still deciding. |
| 1.3.2 | 2026-10-16 | Browser-captured downloads open only their own options window (own taskbar button, listed in the tray menu), and file names / extensions are detected from the browser, Content-Disposition, redirects, Content-Type and file signature. |
| 1.3.1 | 2026-10-16 | Dark-mode tabs fixed in the progress and Options windows, the download window re-opens when a download finishes (per download or global), "Change save location..." for a single download. |
| 1.3.0 | 2026-10-15 | Capture happens only in NeoDM (browser download removed first, dialog in front), themed menus/scroll bars/drop-downs, scaling fixes, completion sound on by default, extension file-type filters and session pause, self-updater from GitHub. |
| 1.2.1 | 2026-10-15 | Fixed an installer error (2762); running NeoDM is now closed automatically during install and upgrade. |
| 1.2.0 | 2026-10-15 | Torrents section, torrent file selection fix, category folders on by default, installer icon, auto-launch, in-place upgrades. |
| 1.1.0 | 2026-10-15 | Torrents, proxy, remove-with-delete dialog, right-click menu, category folders, tabbed progress window, completion actions, installer, About window. |
| 1.0.0 | 2026-09-20 | Initial release: queue, segmented engine, modern interface, browser extension with media ribbon, tray. |

See `CHANGELOG.md` for details.

## License

MIT License. Copyright (c) 2026 unlimitedera and NeoDM contributors. Third-party components keep their own licenses (MonoTorrent and its dependencies are MIT licensed).
