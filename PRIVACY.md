# Privacy policy

NeoDM (the Windows download manager) and NeoDM Integration (the browser extension) are open source
projects created by unlimitedera. Last updated: 2026-10-20.

## Summary
- NeoDM has no accounts, no analytics, no advertising and no telemetry.
- Nothing about you or your browsing is sent to the NeoDM author or to any server run by the project.
- The browser extension only talks to the NeoDM app installed on your own computer.

## The browser extension (NeoDM Integration)
What the extension reads, locally in your browser:
- The address of a download the browser starts, its file name and type, and the page it came from (so NeoDM can name the file and send the right referrer).
- The address of direct video or audio files found on pages you visit, so the media ribbon can offer a download.
- The address of the active tab, to offer a per-site pause.

What it stores: your settings (capture on or off, file types, ribbon on or off, the two cookie options) in the browser's extension storage, and a temporary per-session pause that the browser clears when it closes.

What it sends, and where: when you download something, the extension sends the items above to the NeoDM desktop app on your own computer through the browser's Native Messaging feature. The extension never sends them over the internet.

Cookies, session cookies of a download (on by default): when you capture a download, NeoDM receives the cookies your browser sent with that download, so the site accepts the download. They go only to the NeoDM app on your PC, are kept encrypted only while the download runs, and can be turned off in the popup ("Pass the browser's session cookies for a download to NeoDM"). The extension reads them from the request the browser itself made for that download (the Cookie header sent to that exact address), keeps them in memory for at most 60 seconds, and never writes them to storage or to its log. Without this, some sites (file hosts that tie a link to your session) answer NeoDM with a web page instead of the file.

Cookies, authentication cookies (off by default): if you turn on "Include authentication cookies", the browser asks for the cookies permission. The extension then also looks up the cookies of the site you are downloading from, for downloads where no cookie was seen in the request. The same rules apply: local NeoDM app only, not logged.

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
