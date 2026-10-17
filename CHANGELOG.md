# Changelog

All notable changes to NeoDM are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project uses [Semantic Versioning](https://semver.org/). The rule for keeping this file current is in `docs/VERSIONING.md`.

## [1.4.5] - 2026-10-18

### Fixed
- Downloads on file hosts such as filekeeper.net were never captured. The page submits a hidden form with POST, the server answers 302 with the address of the file, and the browser then fetches that address with GET. The extension treated the whole chain as POST because it kept the method of the first request, so the browser downloaded the file itself and NeoDM showed nothing. The extension now records the method and redirect code of every hop. A 301, 302 or 303 after a POST makes the next hop a GET, so the file is captured. A 307 or 308 keeps the POST and the form body, a form that answers with the file directly cannot be repeated, and both are still left to the browser.
- NeoDM now receives the final address after redirects, and the page that started the download is sent as the referer.
- The capture dialog no longer requests the address to read the file name when the download came from a form POST redirect or the address is a long token address (over 200 characters with a token-like path part). Such addresses can be single use, so the name given by the browser is used.
- The native host and the app accept download addresses up to 8192 characters and reject longer ones. The host limit was 16384 and the app had none.

### Added
- The extension popup has a Recent downloads section with the last 10 capture decisions (time, site, taken or left to the browser, and the reason) and a Clear button. The extension keeps the last 20 in the session storage of the browser, without addresses.
- The extension icon shows an exclamation mark for 2 seconds when a download was left to the browser because capture is off, the site is paused or the file type is turned off.

## [1.4.4] - 2026-10-17

### Fixed
- A selected or hovered row in the connection table of the download window, and in every other list, no longer turns white-grey with unreadable text in dark themes. The cause was that list rows had no template of their own, so Windows drew its default selection gradient. Rows now have themed states for normal, hover, selected, selected while the window is inactive (what you see after clicking away), disabled and keyboard focus.
- Controls that still read Windows colours (column headers, text selection in boxes, tree rows, combo box items, data grids) now follow the theme, because every theme overrides the Windows highlight, inactive highlight, control and control text colours.
- The connection table in the download window kept a fixed height of 190 units and ended in an empty header cell. The table now takes all spare height, the Info column fills the remaining width, and the segment bar and progress bar span the whole window and repaint on resize.
- Windows did not use extra space when they were enlarged or maximised. The download window, Download complete, Remove, Text prompt and the extension notice window can now be resized (Properties already could), and Properties keeps its buttons below the scrolling fields, and their content, buttons and scrolling areas follow. The torrent file list and the main download list give their spare width to one column.
- Hiding the details in the download window now makes the window shorter by the height of the table, showing them again restores that height, and your own manual resize is not undone.
- The tray list no longer shows a dash in parentheses for a download whose size is not known yet.
- Long file names and addresses in the Download complete, Properties, Torrent and main window detail lines are cut with an ellipsis and show the full text in a tooltip.

### Added
- NeoDM remembers the size and position of the download window, Options, Add download, the capture dialog, Properties, the torrent windows, the extension guide and the update window. The values are stored in `window-placement.json` in the state folder. A saved window that would be off the screen, larger than the work area or smaller than its minimum is corrected when it opens, and unreadable entries are dropped. A window closed while maximised reopens maximised with its normal size kept.
- Theme keys `ItemHoverBrush`, `ItemSelectedBrush`, `ItemSelectedInactiveBrush` and `ItemSelectedForegroundBrush` in all eight theme files. With a custom accent the selection colour follows the accent.
- Styles for `ListViewItem`, `TreeViewItem`, `TreeView`, `DataGrid`, `DataGridRow`, `DataGridCell` and `DataGridColumnHeader`. Text selection in boxes uses the accent with its readable foreground.

### Changed
- The download window opens at 640 by 700 and no longer sizes itself to its content when the details are shown.
- While a list has no keyboard focus, its selected row uses `ItemSelectedInactiveBrush`, a neutral shade, instead of the accent shade.
- Tests: the theme suite renders a selected row in every theme, with and without a custom accent, in an inactive and a focused window, and checks the row colour, the absence of near-white (dark) or near-black (light) pixels and the text contrast. It also checks the new keys and the Windows colour overrides in every theme, and extends the contrast matrix to selected, hover and inactive rows. A new layout suite resizes 17 windows (and checks the countdown window at its fixed size) to a small, default and 1.6 times size at 100, 125 and 150 percent work areas and checks that the main area grows, nothing is clipped and the corner button keeps its position. It also covers the download window (table fill, header cell, hide and show details), the fill column rule and the placement store. `NEODM_SHOTS=<dir>` also saves the download window with a selected row for Midnight and Dark at the default and an enlarged size.

## [1.4.3] - 2026-10-17

### Added
- Help > Browser extension install guide opens a window with numbered steps for loading the extension in Chrome, Edge or Brave (the extension is not in a browser store yet). A browser selector swaps the extensions page address (`chrome://extensions`, `edge://extensions`, `brave://extensions`) and offers Copy and Open this page, which starts the browser found through its App Paths entry or default install folder, or says that the browser is missing. The steps start with removing an old NeoDM Integration, then Developer mode, Load unpacked, the extension folder, a check of the version and the Test connection button, and what to do after an update. Step 3 shows the real extension folder (the installed `extension\unpacked` folder, else the development copy, else a reinstall message) with Copy path and Open folder. Four small screenshots are included. The same guide is on GitHub in `docs/EXTENSION-INSTALL.md` (with its screenshots), linked from the README, and Help > About has an install guide button. The README, the guide and the window say that the Chrome Web Store listing is waiting for approval and that the manual install is for the time until then.
- Extension version check. The extension sends its manifest version as the optional field `extVersion` with every capture, ribbon, popup and context menu download. The native host accepts only 2 to 4 numeric parts of 1 to 4 digits (at most 24 characters), forwards it to the app, and rejects anything else. NeoDM compares it with its own version. When the extension sends no version, or an older or newer one, the capture dialog shows a banner such as "Your browser extension is version 1.3.3, NeoDM is 1.4.3. Update the extension so captured downloads and the media list keep working." with Show install guide and Dismiss buttons, and a standalone notice window is used when no capture dialog is on screen. The banner never delays or blocks the download. It appears once per app session for each pair of versions, and "Don't remind me about this version" is saved in `settings.json` (`MutedExtensionVersions`, no schema change).
- The host's reply to the popup's Test connection includes `appVersion`, read from the file version of `NeoDM.exe`, and the popup shows "NeoDM app 1.4.3, extension 1.4.2. Open Help > Browser extension install guide in NeoDM." when the two differ. Older hosts and older extensions keep working because both fields are optional.
- Tests: a version compare table, the banner and notice window, once-per-session and saved dismissal, guide window checks in every theme (images, folder cases, browser selector, copy, Help menu), the host self-test for `extVersion`, and extension tests for `extVersion` on every request path and the popup line. `NEODM_SHOTS=<dir>` also saves the guide window for Midnight and Light.

## [1.4.2] - 2026-10-17

### Fixed
- The media ribbon listed one video several times while it buffered, once for each downloaded piece. Pieces of the same file are now one entry: requests that differ only in range, segment-number or cache-busting parameters (for example `range`, `bytestart`, `byteend`, `sq`, `rn`, `rbuf`, `_`) or in a `/range/0-1048575/` path part are grouped, and the entry shows the full size from `Content-Range`. A partial response whose total size is unknown is not listed. Numbered fragments of segmented streams (`seg-12.mp4`, `frag_00042.m4v`, `init.mp4`) are hidden, and the extension popup, which now lists the media of the current tab, explains why with one row: "Segmented stream, cannot be saved as one file". A video the page plays directly is listed first. Downloading an entry sends the URL without range parameters and retries once with the original URL if NeoDM cannot start it.

## [1.4.1] - 2026-10-16

### Changed
- Wording improvements across the app, extension and documentation.

## [1.4.0] - 2026-10-16

### Added
- Themes in Options > Appearance: a gallery of selectable cards, each with a small live swatch of the theme (window chrome, sidebar, list rows, progress, accent button). Picking a card applies it instantly to every open window (no restart); Save keeps it, and Cancel or closing the window restores the saved theme. Nine themes: Light and Dark (unchanged), System (follows the Windows light/dark app setting, re-checked when Windows reports a settings change), and new Midnight (flat near-black charcoal, 8 px cards, hairline borders, soft violet accent with a gradient primary button and progress bars, roomier rounded toolbar buttons), Graphite (neutral grey, teal), Ocean (deep blue, cyan), Forest (dark green, mint), Rose (dark plum, pink) and Paper (warm sepia light, brown). All colours and the rounded-card styling are original.
- Accent colour: nine presets plus a custom `#RRGGBB` box (live preview, validation, the `#` is optional) and "Reset accent". The override recolours buttons, progress bars, selection and focus rings in any theme; the accent is nudged until it is readable (4.5:1) and the text on it is picked automatically.
- Themes are resource dictionaries with the same keys (a test diffs the key sets of every theme); shape and density are theme-level keys (`ControlRadius`, `CardRadius`, `ControlPadding`, `CommandPadding`, `CommandMinHeight`) and the primary fill is `AccentFillBrush`, so a theme can change radius and density without touching templates.
- Themed check boxes and radio buttons (the box is no longer a bright system square in dark themes) and a themed `ListView` background.
- Settings: `Theme` accepts any known theme id (unknown values load as Light; the old `Light`/`Dark` values keep working, no schema change) and new optional `AccentOverride` (`#RRGGBB`). Windows 11 title bars follow dark-type themes, and the branded themes also colour the caption, border and title text.
- Tests: new theme suite (registry, key parity, contrast matrix for every theme and ten extreme accents, System resolution with an injected OS provider, apply/revert, persistence and legacy migration, Options Appearance UI, per-theme pixel checks, per-theme render of the main window, Options, progress window and small dialogs). The existing contrast, menu, tab, combo and tooltip checks now run for every theme. `NEODM_SHOTS=<dir>` saves a populated main-window screenshot per theme (`docs/images/themes`).

### Changed
- Options has an Appearance tab after General (General keeps a shortcut button); the old Light/Dark radio buttons are gone.

## [1.3.3] - 2026-10-16

### Fixed
- The capture dialog ignored its "Save to" folder. Root cause: the dialog exposed `SavePath` but the job was added with the bare request, so the engine's own category resolver chose the folder. `DownloadRequest` has a new optional `TargetDirectory`; the dialog sets it whenever you typed or browsed a folder or picked a category, and the engine saves into exactly that folder for Start now, Add to queue and Schedule: created if missing, proven writable up front (a folder that cannot be written keeps the dialog open with the reason), collision-safe unique name, never overwrites, kept across name refinement and restarts (persisted with the job). If you leave the path untouched the behaviour is unchanged (category folder of the final extension; the pre-fill follows the detected name and now honours the category-folder setting). The progress window shows the real final path.
- A capture dialog left open for more than 5 minutes made the extension restart the download in the browser, so a later Start in NeoDM duplicated it. The native host now sends keep-alive frames (`{"pending":true}` every 15 s) to extensions that ask for them (`keepAlive:true` in the request), the extension waits without limit while they arrive (60 s idle limit, re-armed by every frame) and restarts the download only when NeoDM fails or goes silent. The app no longer times out waiting for the user (the pipe also had a 30 s ceiling) and serves several captures at once. If the browser side hangs up while the dialog is open, the dialog is locked with an explanation and cannot add a duplicate. Old extension + new host/app and new extension + old host keep working (one reply frame, legacy 330 s limit).

## [1.3.2] - 2026-10-16

### Fixed
- Captured downloads sometimes saved with a random name and no extension, so the file did not open until renamed. Root cause: the extension sent an empty file name, the app never read the server's `Content-Disposition`, took the name from the ORIGINAL address (not the one after redirects), ignored the `Content-Type`, and filled the dialog from the last path segment only. The name is now decided in this order: (1) the name the browser itself decided (`downloads` item file name and content type, forwarded as new optional `fileName` / `mime` fields; the wire contract stays backward compatible and the native host validates both strictly, 255 / 100 characters, no path or control characters); (2) `Content-Disposition` (`filename*` UTF-8 percent-encoding beats `filename`; quoted or not; path parts removed); (3) the final address after redirects (percent-decoded last segment, query and fragment ignored) or a name in the query (`filename=`, `file=`, `name=`, `fn=`, `response-content-disposition=`); (4) a missing or script-like extension (`.php`, `.aspx`, `.cgi`, `.html` ...) is repaired from the `Content-Type` (built-in table: archives, programs, documents, video, audio, images, text, torrent ...; `octet-stream` adds nothing; a valid extension is never replaced) and, as a last resort, from the first bytes (zip, rar, 7z, pdf, exe, mkv/webm, mp4, mp3, png, jpeg, gif, gzip); (5) a GUID-like, empty or dots-only name becomes `<host>-download.<ext>`. The download folder (category) follows the FINAL extension.
- The same resolver runs when a transfer starts, so links added without the capture dialog (Add URL, bulk import, context menu, media ribbon) get correct names too. A name you typed, or the browser's own name, is kept; collisions still get `name (1).ext`.
- The capture dialog no longer brings up the whole app. It is an independent top-level window (no owner, its own taskbar button with the NeoDM icon and the file name as title) that opens above the browser and never shows, restores or activates the main window, even if that is minimised, hidden to the tray or closed to the tray. The progress window opened by "Start Download" is independent in the same way. Several captures at once are several separate dialogs (no modal on modal).

### Added
- Pre-download check: when the capture dialog opens, NeoDM looks at the link in the background (HEAD, then a ranged GET of the first bytes if HEAD is refused or does not settle the extension; redirects followed, proxy, referer and cookies honoured, 8 s limit) and fills the "File name" box with the real name unless you already edited it. The dialog shows the detected size and type, and moves the category / folder along with the final extension unless you changed them.
- Tray menu section: every waiting capture dialog and every open progress window is listed in the tray icon's right-click menu (`\u2022 Download options: <file>`, `\u2022 Downloading: <file> (42%)`); clicking one restores and activates that window. Left-click or double-click on the tray icon restores a waiting capture dialog first, else the main window. The tray icon stays visible while such a window is open. The section is hidden when nothing is open.
- The extension remembers the browser's own file name from `downloads.onDeterminingFilename` (no `suggest()` call, so the browser is never held up).

### Changed
- `DownloadRequest` has an optional `AutoName` flag (a name the engine may still refine); explicit names are never touched. Redirect hops keep the request method.

## [1.3.1] - 2026-10-16

### Fixed
- Dark mode: the tabs of the per-download progress window (Download status / Speed Limiter / Options on completion) and of the Options window (General / Save To / Downloads / Proxy / Torrents) were bright white or grey with unreadable text, and the selected tab was pure white. Tab headers are now themed in both themes: unselected = sidebar colour with muted text, hover = subtle highlight with full-contrast text, selected = surface colour with full-contrast text and an accent underline, themed keyboard focus ring, readable disabled state, themed content border. Tests render the headers in both themes (no near-white background) and check text contrast of at least 4.5:1 for selected, unselected and hover (3:1 for disabled).

### Added
- "Show this window when the download finishes": per-download checkbox in the progress window's "Options on completion" tab, and a global setting Options > Downloads > "Open the download window when a download finishes" (`ShowProgressOnCompletion`, on by default; settings schema 4 switches it on once for existing settings). When a download completes and its window was closed, hidden or minimised, the same single window for that download opens again (or is restored), comes to the front and shows the Complete state with Open / Open folder / Close. An already open window just shows Complete without taking focus. A download's own choice overrides the global one both ways; it is stored with the other per-download completion options (kept across restarts with "Remember this choice"). Downloads that were already complete when NeoDM started never open a window.
- Exactly one completion window: when this window is shown for a job it replaces the separate "download complete" dialog for that job. If the option is off for the job, the existing "Show download complete dialog" behaviour is unchanged; its greying rules for the other completion options (Exit NeoDM, Turn off computer) are unchanged too.
- "Change save location...": next to the read-only "Save To:" path of the "Options on completion" tab. It opens a folder picker (a themed text prompt if the system dialog is unavailable) and applies the folder to this download only, through the new `DownloadManager.MoveTo(id, folder)`. A finished download's file is moved (only that file). An unfinished download keeps its partial data in NeoDM's state folder (addressed by part id), so nothing partial has to move: the job's target folder is switched, a running transfer keeps running and finishes in the new folder, and the folder is created and write-tested first. A name already used in the new folder gets a unique name ("name (1).ext"); nothing is ever overwritten. A missing drive, a folder without write permission or an invalid path gives a clear message and leaves the job and files unchanged; choosing the current folder does nothing. The Save To text, the list and Properties follow. Torrent rows have the button disabled with a tooltip pointing to Options > Torrents.

### Changed
- Settings schema is now 4 (adds `ShowProgressOnCompletion`).

## [1.3.0] - 2026-10-15

### Fixed
- Browser capture now starts the download only in NeoDM. Before, the browser download was paused and cancelled only after NeoDM had acknowledged it, so it could keep going (or leave a file) while the options dialog was open. Now the extension cancels and erases the browser download (partial file, finished file and history entry) the moment it is created, and only then asks NeoDM. Chosen approach: `downloads.onCreated` plus `cancel` / `removeFile` / `erase`; no extra permission and no `onDeterminingFilename` or blocking `webRequest`, which would not make the cancel any earlier.
- Pressing Cancel in NeoDM's capture dialog now discards the download on purpose (the app reply carries `cancelled`). If NeoDM is unreachable or fails, the extension restarts the browser download from the same URL so nothing is lost.
- The capture dialog and the progress window of a captured download now open in front of the browser and every other window: foreground rights are passed from the native host to the app, then the window attaches to the foreground input queue, goes topmost briefly and, if Windows still refuses, the taskbar button flashes. The dialog is no longer owned by a minimised main window.
- Dark mode: menus and right-click menus no longer show white border or inner lines; menu items, separators, check marks, highlight and disabled text are fully themed in both themes.
- Dark mode: the sidebar and every other list, text box, combo box popup and grid use a thin themed scroll bar (rounded thumb, transparent track, hover and pressed states) instead of the Windows one.
- Dark mode: drop-down lists, combo boxes, tooltips and column headers are no longer bright white with unreadable text; hover, selected and disabled states have at least 4.5:1 text contrast (3:1 for disabled text), checked by tests for both themes.
- Scroll bars no longer touch or cover text boxes at the right edge of Options (all five tabs), Add download, the capture dialog, speed/schedule, Properties, progress and completion windows; content keeps a gutter wider than the scroll bar. Windows are limited to the usable screen area so nothing is clipped at 100%, 125% or 150% display scaling, and tall windows scroll instead of running off the screen.
- Options no longer has "Remember this choice for future sessions": what you pick under "When all downloads finish" in Options is saved permanently. The remember choice stays where a choice is temporary: the "Options on completion" tab of a download (unremembered per-download choices are no longer written to disk).

### Changed
- Completion sound is on by default. Existing settings are switched on once (settings schema 3); after that your choice in Options is kept. Each download can override it in its "Options on completion" tab (new "Play sound when done"), and a download muted there stays silent.
- Extension: capture of browser downloads is on by default for new installs and, once, for existing users who never chose (an explicit off is kept). The extension version follows the app (1.3.0).

### Added
- Extension: file-type preferences in the popup (Video, Audio, Images, GIF, Archives, Programs/installers, Documents, Other). They apply to the media ribbon and to download capture. Default: Video, Audio, Archives, Programs, Documents, Other on; Images and GIF off. Images and GIFs are only detected while ticked.
- Extension: session pause for "this site" or "all sites" (kept in `storage.session`, so it resets when the browser closes). While paused neither capture nor the ribbon trigger; the popup shows the state and has a one-click re-enable.
- Self-updater: NeoDM checks GitHub (`api.github.com/repos/unlimitedera/NeoDM/releases/latest`) 20 seconds after start and then every 24 hours, at most once per 12 hours across restarts, silently when offline or rate limited. A themed update window shows the release notes, "View full changelog" and "Release page" links and the buttons Update now, Remind me later and Skip this version. Update now downloads `NeoDM-Setup-*.msi`, verifies it against the release's `SHA256SUMS.txt` (refusing a mismatch, a missing entry or a file that is not an MSI) and runs `msiexec /i <msi> /passive` elevated; the installer closes and restarts NeoDM. Proxy settings from Options are used. See `docs/UPDATES.md`.
- Help > Check for updates, an About window with the GitHub repository link, the version and a "Check for updates" button, and Options > General > "Check for updates automatically" (on by default) with a way to forget a skipped version.
- `build-installer.ps1` also writes a versioned `NeoDM-Setup-<version>.msi` and `SHA256SUMS.txt` next to the MSI, ready to publish as a release the updater can use.

## [1.2.1] - 2026-10-15

### Fixed
- Installer: setup failed with error 2762 while installing. The running-app check scheduled a script action before the install transaction started.

### Changed
- Installer: a running NeoDM (and its browser host) is now closed automatically, with no prompt and no manual step, both on install and on in-place upgrade. Unfinished downloads are interrupted and can be resumed.

## [1.2.0] - 2026-10-15

### Added
- Sidebar has a separate TORRENTS section (All torrents, Downloading, Seeding, Completed); the View menu mirrors it.
- Installer: NeoDM starts automatically after install and after upgrade, as the normal user (never elevated). `NO_LAUNCH=1` turns it off.
- Installer: NeoDM icon in Apps & Features, shortcuts and the setup wizard (banner and welcome/finish artwork generated from the icon).
- Settings schema marker (`SettingsSchema`) so one-time default changes can be migrated.

### Changed
- Category folders are on by default for new installs; existing settings that still carry the old default (off) are switched on once, after which your choice in Options is kept.
- The DOWNLOADS section of the sidebar shows only non-torrent items; torrents appear only under TORRENTS.
- Installing a newer MSI upgrades in place (same upgrade code); installing an older one is refused with a clear message. User data in `%LOCALAPPDATA%\NDM` is never touched; the startup entry, native host manifest/config and extension files are refreshed.

### Removed
- The "Connecting" sidebar filter (connecting items stay in All and Downloading).
- The flat "Seeding" and "Torrents" sidebar entries (replaced by the TORRENTS section).

### Fixed
- Add torrent dialog: clicking a file checkbox did not uncheck it (the three-state check box cycled back to checked), so files could only be deselected with Select none. File and folder boxes now toggle both ways; folders toggle their children and the summary and Start button follow.

## [1.1.0] - 2026-10-15

### Added
- Torrents are part of the normal product: magnet links and `.torrent` files, file selection, seeding, Torrents options tab. The installer registers NeoDM for `magnet:` links and `.torrent` files.
- Proxy support (HTTP, HTTPS, SOCKS5, optional credentials stored encrypted, test button).
- Remove dialog with an optional "also delete the file(s) from disk" choice.
- Right-click menu on downloads.
- Category folders: files can be saved to Programs, Compressed, Music, Video and Documents subfolders.
- Tabbed progress window with per-download completion actions.
- Completion actions for when downloads finish (shut down, restart, sleep, hibernate, sign out, run a program and more).
- Installer with a browser extension page (unpacked copy always installed, optional browser policy).
- About window showing the version, the creator and this changelog.
- Single product version and this changelog (`docs/VERSIONING.md`).

### Changed
- Options: the Connection tab was removed. Maximum simultaneous downloads, connections (segments) per download, retries, timeouts and dynamic segmentation now live in the Downloads tab, group "Connections". Existing settings files keep working.

## [1.0.0] - 2026-09-20

### Added
- Download queue with pause, resume, retry and scheduling.
- Segmented download engine with dynamic segmentation and resume.
- Modern interface with light and dark themes.
- Chrome, Edge and Brave extension with a media ribbon for detected audio and video.
- System tray integration and start with Windows.
