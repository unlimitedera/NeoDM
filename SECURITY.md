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
- Cookies a browser sends with a download are stored in the download list protected with Windows DPAPI for the current account (not as plain text) and removed when the download ends. The Referer of a download is not passed to another site on a redirect, and a redirect from https to http is refused.
- Finished downloads get the Windows "downloaded from the internet" mark (Zone.Identifier) unless you switch that off in Options > Downloads.
- Capture dialogs warn when the address is on this computer or the local network. A page can choose the address of a download, so check it before you accept.
- The browser connection pipe is created as the first instance with current-user-only access; at most 8 capture dialogs wait at once.
- The extension keeps the `tabs` permission: the popup and the right-click entries read the address and title of the current tab. Chrome also gives those fields to extensions that have host access to the page, and the extension has it for http and https, so the permission is probably redundant; removing it was not tried in a browser matrix, so it stays rather than risk breaking the popup on some page.
- Not covered: Authenticode signing (needs a purchased certificate); confining the file paths saved in `queue.json` (an attacker who can edit that file already runs as you); a warning before a torrent overwrites an existing file (library behaviour).
