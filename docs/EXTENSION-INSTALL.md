# Install the NeoDM browser extension

## Recommended: install from the Chrome Web Store

This is the easiest way. The store installs the extension and keeps it up to date by itself.

[**Install from the Chrome Web Store**](https://chromewebstore.google.com/detail/ojbaibjfbdjodgmfbiajkigpanbdkiid)

Edge and Brave can use the Chrome Web Store too. Edge first asks you to allow extensions from other stores.

1. Open the link above in Chrome, Edge or Brave and press Add to Chrome (Add to Edge, Add to Brave). Accept the permission list.
2. Pin the NeoDM Integration button from the puzzle piece menu in the toolbar, open it and click Test connection. It should say Host connected.
3. NeoDM must be installed and running, version 1.5.6 or newer. Older NeoDM versions only know the manual copy of the extension and the popup shows "native host unavailable" for the store copy. Update NeoDM, then press Test connection again.

The store copy and the extension files in the NeoDM install folder are separate. The store copy is updated by the store, usually within a few hours after a NeoDM release. NeoDM updates only the files in `C:\Program Files\NeoDM\extension`, which exist for the manual install below, and never touches the store copy. Use one copy per browser: if you move from the manual install to the store, remove the manual copy first (step 0 below), because two copies of the extension fight over the same downloads.

If the store copy is older than NeoDM, NeoDM tells you once a day at most, with a button that opens the store page. To update at once, open `chrome://extensions` (`edge://extensions` in Edge, `brave://extensions` in Brave), turn on Developer mode and press Update.

## Manual install (alternative)

Use these steps if you cannot use the store. The browser then loads NeoDM Integration from a folder, and you update it yourself. Chrome, Edge and Brave block extensions that are installed from a file outside their store, so the browser loads the extension from a folder. This takes about a minute and you do it once.

The same guide is in NeoDM under Help > Browser extension install guide. It opens the extensions page for you, shows the real folder on your PC and lets you copy it. Help > Install from the Chrome Web Store opens the store page.

The screenshots are from Chrome. Edge and Brave use the same layout, so they apply to all three browsers.

| Browser | Extensions page |
|---|---|
| Chrome | `chrome://extensions` |
| Edge | `edge://extensions` |
| Brave | `brave://extensions` |

Paste the address into the address bar of the browser you use.

### Step 0. If you already installed NeoDM Integration, remove the old one first.

- Open the extensions page with the address above.
- Find NeoDM Integration.
- Click Remove and confirm.
- Two copies of the extension fight over the same downloads, and the browser keeps loading the folder it was first installed from.

![The NeoDM Integration card with the Remove button and the version and switch highlighted](images/extension-guide/step-extension-card.png)

### Step 1. Turn on Developer mode.

- Turn on Developer mode. In Chrome and Brave the toggle is at the top right of the page. In Edge it is on the left side of the page.

Per browser:

- Turn on Developer mode. In Chrome the toggle is at the top right of the page.
- Turn on Developer mode. In Edge the toggle is on the left side of the page.
- Turn on Developer mode. In Brave the toggle is at the top right of the page.

![The extensions page with the Developer mode toggle highlighted](images/extension-guide/step-developer-mode.png)

### Step 2. Click Load unpacked.

- The button appears after Developer mode is on.

![The Load unpacked button highlighted](images/extension-guide/step-load-unpacked.png)

### Step 3. Choose the NeoDM extension folder.

- Paste the path below into the folder picker, or browse to it, then click Select Folder.

With the installer the folder is `C:\Program Files\NeoDM\extension\unpacked` (or the install folder you chose, then `extension\unpacked`). NeoDM shows the exact path in the guide window, with Copy path and Open folder buttons. If NeoDM cannot find the folder, the guide says: The extension folder was not found. Reinstall NeoDM to restore it.

![The folder picker with the extension folder chosen](images/extension-guide/step-folder-picker.png)

### Step 4. Check that NeoDM Integration shows version 1.5.9 and is switched on.

- Pin it from the puzzle piece menu in the toolbar.
- Open its popup and click Test connection. It should say Host connected.

The version number is the NeoDM version you installed. If the popup also shows a line such as "NeoDM app 1.4.3, extension 1.4.2", the browser still runs an older copy of the extension. Go back to step 0.

### Step 5. After a NeoDM update that changes the extension, install it again.

- Repeat step 0, then step 2. Or click the reload arrow on the NeoDM Integration card.
- Chrome and Edge show a notice about developer mode extensions each time the browser starts. Close it. The extension keeps working.

NeoDM tells you when the extension and the app differ. The capture window then shows a banner with a Show install guide button.

If you installed from the Chrome Web Store you can skip this step: the store updates the extension by itself.

## If something goes wrong

**The store copy is older than NeoDM, or the popup says "native host unavailable" for the store copy.**
The store updates extensions by itself within a few hours after a release; open the extensions page and press Update to get it now. "Native host unavailable" for the store copy means NeoDM is older than 1.5.6 or not installed: install or update NeoDM, restart the browser and press Test connection.

**The browser blocks the extension and mentions its store.**
That is expected for a .crx file. Do not open the .crx. Use Load unpacked with the folder from step 3.

**Test connection says the host is unavailable.**
Install NeoDM with its setup program, which registers the browser connection. Then restart the browser and try again.

**The extension stopped working after I moved or deleted the folder.**
The browser remembers the folder path. Remove the extension and load the folder again from step 3.
