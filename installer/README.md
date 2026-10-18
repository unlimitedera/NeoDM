# NeoDM installer

Per-machine Windows Installer package (`artifacts\NeoDM-Setup.msi`, WiX 5). It asks for elevation once (UAC) and installs to `C:\Program Files\NeoDM`.

## Build
```
dotnet tool install --global wix --version 5.0.2
wix extension add -g WixToolset.UI.wixext/5.0.2
wix extension add -g WixToolset.Util.wixext/5.0.2
powershell -File build-installer.ps1        # needs dist\app, dist\host, dist\NeoDM.crx (run build.ps1 first)
```
`build-installer.ps1 -AppExe <name>` builds with a different app executable name (the default is `NeoDM.exe`). The script prints the SHA-256.

## What it installs
- App, `host\NDM.NativeHost.exe` + `bridge-config.json` + `com.ndm.bridge.json` (allowed origin `chrome-extension://hohmmojlhbkelkobobpkochpdempcihd/`)
- `extension\NeoDM.crx` and `extension\unpacked\` (always kept), `LICENSE.txt` (MIT)
- Start Menu: NeoDM, "Install NeoDM browser extension"; optional desktop shortcut; an Apps & Features entry
- Registers NeoDM for `magnet:` links and `.torrent` files (HKLM Capabilities / Open with; the default can still be changed in Windows; uninstall removes it)
- HKLM `Run\NeoDM` (startup), HKLM `NativeMessagingHosts\com.ndm.bridge` for Chrome, Edge, Brave

## Browser registration cleanup
On install and upgrade `configure.ps1` also deletes a per-user `HKCU\SOFTWARE\{Google\Chrome, Microsoft\Edge, BraveSoftware\Brave-Browser, Chromium}\NativeMessagingHosts\com.ndm.bridge` key when the manifest file it names no longer exists. A per-user key overrides the machine key written by the MSI, so a stale one made the browser report "host unreachable". Keys that point to an existing manifest are left alone, and no other host key is touched. The running app does the same check every few minutes (Help > Browser connection) and can register a manifest under `%LOCALAPPDATA%\NDM\host` for the current user.

## Silent install
`msiexec /i NeoDM-Setup.msi /qn [INSTALLFOLDER="D:\NeoDM"] [RUN_AT_STARTUP=0] [DESKTOP_SHORTCUT=1] [INSTALL_POLICY=1] [NO_LAUNCH=1]`

## Upgrade, running app, launch
- Upgrade: a newer MSI with the same UpgradeCode upgrades in place (`MajorUpgrade`). An older one is refused ("A newer version of NeoDM is already installed..."). The installer never touches settings and download state in `%LOCALAPPDATA%\NDM`. Every install or upgrade rewrites the host manifest, bridge config, update.xml, startup entry and extension files.
- Running app: before it touches any files, a pre-install action closes NeoDM and its browser host with no prompt. This also happens before the old version is removed in an upgrade. You can resume interrupted downloads.
- Launch: after an install or upgrade (not after uninstall), setup starts NeoDM once through the user's Explorer shell, so it runs non-elevated in the interactive session. `NO_LAUNCH=1` skips this. Setup also skips it when msiexec runs as SYSTEM, because there is no interactive user.
- Artwork: the icon comes from `src\NDM.App\Assets\NDM.ico` (Apps & Features, shortcuts). `build-installer.ps1` generates the wizard banner and welcome bitmaps from it.

## Browser extension
Chrome, Edge and Brave refuse to auto-install a non-store extension on a PC that is not domain or MDM managed (`chrome://policy` shows the forcelist entry as `[BLOCKED]`). So the installer does not write a policy by default. The finish page checkbox and the Start Menu shortcut "Install NeoDM browser extension" open the extension folder and each installed browser's extensions page. Enable Developer mode and use Load unpacked on `extension\unpacked`.

`INSTALL_POLICY=1` (off by default) also writes `ExtensionInstallForcelist` value `7421` for each detected browser. It only takes effect on managed machines, and those browsers then show "Managed by your organization". Uninstall removes only that value and any policy keys it leaves empty, never other policies.

## Installer extension page
The wizard has a "Browser extension" page with an unchecked box "Also install through browser policy". Checking it is the same as `INSTALL_POLICY=1` (the CRX policy route, effective only on managed PCs). The unpacked folder and NeoDM.crx are always installed.

## Updates
NeoDM can update itself from GitHub releases by running this MSI (`msiexec /i <msi> /passive`, elevated). The installer closes the running NeoDM and starts the new one, so the in-app update needs no manual step. `build-installer.ps1` also writes `artifacts\NeoDM-Setup-<version>.msi` and `artifacts\SHA256SUMS.txt`; upload both to the release. The package is not Authenticode-signed: the updater checks the SHA-256 in `SHA256SUMS.txt` from the same release (see `docs/UPDATES.md`). The only network traffic the app starts itself is the check to api.github.com and, on request, the download from github.com; the checkbox in Options > General turns the checks off.
