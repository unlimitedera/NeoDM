# How to install NeoDM

This guide is for everyone, including people who have never installed a program from GitHub. It takes about five minutes.

> **The one rule: download only the file named `NeoDM-Setup-1.14.0.msi` and ignore every other file on the release page.**

## Which file do I download?

The release page lists many files. Most people need only the first one.

| File | What it is | Do I need it? |
| --- | --- | --- |
| `NeoDM-Setup-1.14.0.msi` | The installer. It installs NeoDM and the connection to your browser. | **Yes. This is the only one most people need.** |
| `NeoDM-extension-store.zip` | The browser extension package that is sent to the Chrome Web Store. | No. Only the store owner uses it. |
| `NeoDM-extension-manual-unpacked.zip` | The browser extension as a folder, for installing by hand with "Load unpacked". | Only if you cannot use the Chrome Web Store. |
| `NeoDM-extension-unpacked.zip` | The same folder under its older name. | No. Use the file above. |
| `NeoDM-extension-manual.crx` | The extension as a single packed file. | No. Browsers block installing these by hand. |
| `NeoDM-extension.crx` | The same packed file under its older name. | No. |
| `SHA256SUMS.txt` | A list of fingerprints, used to check that a download is not damaged. | Only if you want to check the file. |
| `SHA256SUMS.txt.sig` | A signature for the fingerprint list. NeoDM uses it to check its own updates. | No. |
| `NeoDM-helper-sources-1.8.0.zip` | Source code files for some helper tools that come with NeoDM. | No. It is there for people who want the source. |

If you downloaded everything by mistake, nothing is harmed. Delete the extra files and keep the MSI.

## Step by step

1. Open the releases page: <https://github.com/unlimitedera/NeoDM/releases/latest>
2. Scroll down to the **Assets** list. If you only see a few items, click **Show all assets**.
3. Click **NeoDM-Setup-1.14.0.msi**. Your browser downloads it. If it asks whether to keep the file, choose **Keep**.
4. Open your **Downloads** folder and double-click `NeoDM-Setup-1.14.0.msi`.
5. Windows may show a blue screen called **Windows protected your PC**. This is explained in the next section. Click **More info**, then **Run anyway**.
6. If Windows asks "Do you want to allow this app to make changes?", click **Yes**. The installer needs administrator permission to install for all users.
7. In the installer, click **Next**, accept the license, click **Next**, then **Install**.
8. Click **Finish**. NeoDM starts.

### The "Windows protected your PC" screen

Windows shows this for any program from a publisher it does not know well. NeoDM is free and open source, and its installer is not yet signed with a paid code signing certificate. Those certificates cost money every year, so the screen appears. It does not mean the file is infected.

If you want to be sure the file is the real one:

1. Download `SHA256SUMS.txt` from the same release page and keep it next to the MSI.
2. Open **Command Prompt** in your Downloads folder and run: `certutil -hashfile NeoDM-Setup-1.14.0.msi SHA256`
3. Compare the long code it prints with the code written next to `NeoDM-Setup-1.14.0.msi` in `SHA256SUMS.txt`. They must be identical. If they differ, delete the file and download it again.

## Where is NeoDM after installing?

- Open the **Start menu** and type **NeoDM**.
- While it runs you also see its icon in the tray, near the clock. If you do not see it, click the small arrow next to the clock.

## Add the browser extension

The extension lets Chrome, Edge and Brave hand downloads to NeoDM. The installer already connects NeoDM to your browser. You only need to add the extension itself.

**Chrome (easiest):**

1. Open the Chrome Web Store page: <https://chromewebstore.google.com/detail/ojbaibjfbdjodgmfbiajkigpanbdkiid>
2. Click **Add to Chrome**, then **Add extension**.

**Edge and Brave:** both can install from the Chrome Web Store. Open the same link. In Edge, allow extensions from other stores when it asks, then click **Get**. In Brave, click **Add to Brave**.

**If you cannot use the store (manual way):**

1. On the release page, download `NeoDM-extension-manual-unpacked.zip`.
2. Right-click it, choose **Extract All**, and extract it to a folder you will keep, for example `Documents\NeoDM-extension`. Do not delete this folder later, the browser reads it every time.
3. Open `chrome://extensions` (in Edge `edge://extensions`, in Brave `brave://extensions`).
4. Turn on **Developer mode** (top right).
5. Click **Load unpacked** and pick the extracted folder, the one that contains a file called `manifest.json`.

There are pictures for these steps in [EXTENSION-INSTALL.md](EXTENSION-INSTALL.md).

## Check that it works

1. Click the NeoDM icon in your browser toolbar (use the puzzle piece menu to pin it). The popup should say it is connected.
2. Click a download link for a file on a website you trust. The NeoDM window where you choose the folder and start the download should appear.

## Update NeoDM

NeoDM tells you quietly when a new version exists (a small note in the status bar). Click it to update. You can also check yourself in **Options > General** with **Check now**, or repeat the steps above with the newest MSI. Your downloads and settings are kept. The Chrome Web Store updates the extension by itself.

## Uninstall NeoDM

1. Open **Settings > Apps > Installed apps**.
2. Find **NeoDM**, click the three dots, choose **Uninstall**, and confirm.
3. To remove the extension, open your browser's extensions page and click **Remove** on NeoDM Integration.

## Troubleshooting

- **Windows blocked the installer and there is no "Run anyway".** Click **More info** first, the button appears after that. If your PC is managed by a school or company, an administrator may have turned it off.
- **My antivirus warns about the file.** New programs without a paid signature are sometimes flagged by mistake. Check the file with `SHA256SUMS.txt` as described above. If the codes match, you have the real file, and you can ask your antivirus to allow it.
- **The extension says "not connected".** Close NeoDM from the tray icon (right-click, Exit), start it again from the Start menu, then close all browser windows and open the browser again.
- **The extension says it is outdated.** Update it from the Chrome Web Store page. For a manual install, download the newest `NeoDM-extension-manual-unpacked.zip`, extract it over the old folder, and click the reload arrow on the extension card.
- **Still stuck?** Open an issue at <https://github.com/unlimitedera/NeoDM/issues> and say which Windows version and browser you use.
