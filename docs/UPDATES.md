# Updates

NeoDM can tell you when a new release is on GitHub and install it for you. This page lists each step.

## What happens

1. Check. About 20 seconds after NeoDM starts, then every 24 hours while it runs, NeoDM sends one anonymous request:
   `GET https://api.github.com/repos/unlimitedera/NeoDM/releases/latest` with the header `User-Agent: NeoDM/<version>`. There is a 10 second timeout, no login and no cookies. NeoDM uses the proxy from Options > Proxy. The time of the last check is stored, so NeoDM asks at most once per 12 hours even if you restart it often (this also keeps NeoDM under GitHub's anonymous rate limit).
2. Compare. The release tag (`v1.2.3`) is compared with the running version as a semantic version (1.10.0 is newer than 1.9.0). Drafts, pre-releases and tags that are not plain `x.y.z` are ignored.
3. Offer. If the release is newer and you have not skipped it, an update window opens in front of your other windows: "NeoDM x.y.z is available (you have a.b.c)", the release notes, links to the full changelog and the release page, and the buttons Update now, Remind me later and Skip this version. Offline, rate limited (HTTP 403/429), a missing release (404) or an unreadable answer: nothing is shown for automatic checks.
4. Update now. NeoDM downloads `SHA256SUMS.txt`, `SHA256SUMS.txt.sig` and `NeoDM-Setup-*.msi` from the release, with a progress bar, and checks them in this order. Any failure deletes the files, installs nothing and shows a message:
   1. Download addresses. A download may only start at `https://github.com/unlimitedera/NeoDM/releases/download/...`. Redirects are followed by NeoDM itself, one hop at a time, and only to `objects.githubusercontent.com` or `release-assets.githubusercontent.com` over https (an http redirect, another host or more than 5 hops is refused). The installer download is cut off at the size the release declares for it, and never above 200 MB.
   2. Signature. `SHA256SUMS.txt.sig` (base64, ECDSA P-256 over SHA-256, DER form) must be a valid signature of `SHA256SUMS.txt` under the public key built into NeoDM (`src/NDM.App/UpdateTrust.cs`). A missing, unreadable or wrong signature refuses the update. Only then is the list used.
   3. Hash. The SHA-256 of the MSI must equal the entry for that file name in the signed list. A missing entry is refused.
   4. Held file. The MSI is written to a new folder with a random name below `%LOCALAPPDATA%\NDM\updates` and then opened with sharing that allows reading only. The hash is computed through that open handle and the handle stays open until NeoDM exits, which is after the installer has taken over. Another program cannot write to, replace, rename or delete the file in that time, so what was checked is what `msiexec` reads. NeoDM hashes the held file once more right before it starts `msiexec`.
   5. Package identity. The version (`ProductVersion`) and `UpgradeCode` are read from the MSI's Property table. The version must equal the release tag, must be newer than the running version, and the `UpgradeCode` must be NeoDM's. A file that is not a Windows Installer package is refused as well.
   6. Start. `msiexec /i <file> /passive` is started elevated. Windows shows its permission (UAC) prompt; if you decline, NeoDM tells you and nothing changes.
   The installer closes the running NeoDM and starts the new version when it finishes. Partial and old update files are removed on the next start.

You can also use Help > Check for updates... or the Check for updates button in Help > About NeoDM at any time. These always answer ("You are up to date" or the update window), even for a skipped version.

## Settings

- Options > General > Check for updates automatically is on by default. When it is off, NeoDM makes no update request on its own.
- Skip this version remembers the version. Automatic checks stay quiet until a newer one appears. Options shows the skipped version with a button to forget it.

## Security

- The MSI itself is not Authenticode-signed (that needs a purchased certificate, which the project does not have). The trust anchor is the release signature described above: a key pair that only the maintainer holds, with the public half compiled into every NeoDM. Someone who can only change files in the GitHub release (or on the way, or in `%LOCALAPPDATA%`) cannot make NeoDM install their file, because they cannot produce a valid `SHA256SUMS.txt.sig`. Someone who holds the private key can, so it is kept off the repository and off the internet.
- A release without `SHA256SUMS.txt.sig` cannot be installed by the updater (the update window says so). Releases before 1.5.3 have no signature, so the update from 1.5.2 or older to 1.5.3 is the last one that has to be installed by hand (download the MSI from the release page; the SHA-256 of the file is in `SHA256SUMS.txt`). From 1.5.3 on the in-app update verifies the signature.
- NeoDM downloads and installs nothing until you press Update now.

### Signing key handling

- The private key is `keys\neodm-update-signing.pem` (ECDSA P-256, PKCS#8 PEM). `keys/` and `*.pem` are in `.gitignore`. The file is readable only by the account that made it. Back it up somewhere safe and private (for example an encrypted drive): if it is lost, no new release can be accepted by installed copies, and the only way out is a new NeoDM release installed by hand with a new public key. If it leaks, treat it as a compromise: make a new key pair, replace `PublicKeySpkiBase64` in `UpdateTrust.cs`, publish a new release that users install by hand, and revoke trust in the old key by that release.
- `build-installer.ps1` calls `tools/sign-release.cjs` right after it writes `artifacts\SHA256SUMS.txt`. The tool signs the file (node `crypto`, ECDSA, SHA-256, DER signature), writes `artifacts\SHA256SUMS.txt.sig` (base64) and verifies the result. It refuses to sign when the key does not match the public key embedded in the app, so a wrong key cannot produce a release that no installed copy would accept. It never prints the private key.
- Generating a new pair (the public key printed is the only output to keep):
  `node -e "const c=require('crypto'),f=require('fs');const k=c.generateKeyPairSync('ec',{namedCurve:'P-256'});f.writeFileSync('keys/neodm-update-signing.pem',k.privateKey.export({type:'pkcs8',format:'pem'}),{mode:0o600});console.log(k.publicKey.export({type:'spki',format:'der'}).toString('base64'))"`
  then put that text into `UpdateTrust.PublicKeySpkiBase64`.

## Privacy

The only network traffic NeoDM starts by itself is the check to `api.github.com` and, after you press Update now, the download from `github.com` (GitHub redirects to its release storage). GitHub sees your IP address and the `NeoDM/<version>` user agent, as with any web request. Switch the checkbox off to stop the checks. NeoDM sends no analytics and no identifiers.

## Publishing a release the updater can use

Upload these assets to the GitHub release (the tag must be `vX.Y.Z`):

- `NeoDM-Setup-X.Y.Z.msi` (`build-installer.ps1` writes it to `artifacts\`; the unversioned `NeoDM-Setup.msi` name is accepted too)
- `SHA256SUMS.txt` (written by `build-installer.ps1`; lines of the form `<sha256> *NeoDM-Setup-X.Y.Z.msi`)
- `SHA256SUMS.txt.sig` (written by `build-installer.ps1` through `tools/sign-release.cjs`). **Upload it with the other two files.** The updater refuses a release that lacks it.

Check a release before publishing (prints OK or an error, never the key):
`node -e "const c=require('crypto'),f=require('fs');const pub=/PublicKeySpkiBase64\s*=\s*\"([^\"]+)\"/.exec(f.readFileSync('src/NDM.App/UpdateTrust.cs','utf8'))[1];const d=f.readFileSync('artifacts/SHA256SUMS.txt');const sig=Buffer.from(f.readFileSync('artifacts/SHA256SUMS.txt.sig','utf8').trim(),'base64');console.log(c.verify('sha256',d,{key:c.createPublicKey({key:Buffer.from(pub,'base64'),format:'der',type:'spki'}),dsaEncoding:'der'},sig)?'signature OK':'signature INVALID')"`
