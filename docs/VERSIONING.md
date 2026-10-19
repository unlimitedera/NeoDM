# Versioning and changelog

NeoDM uses [Semantic Versioning](https://semver.org/) (`MAJOR.MINOR.PATCH`).

## Where the version lives

`Directory.Build.props` holds `<Version>`. These are derived from it:

- the app and host assembly versions (`AssemblyVersion`, `FileVersion`, `InformationalVersion`); Help > About reads the informational version;
- the browser extension `manifest.json` version: `build.ps1` syncs it and repacks `dist/NeoDM.crx` when it changes;
- the installer: `build-installer.ps1` reads the same version for the MSI `ProductVersion` (shown in Apps & Features) and refuses to run if the extension or `dist/app` disagree.

## Steps for every change

1. Add an entry to `CHANGELOG.md` (Keep a Changelog style: Added / Changed / Fixed / Removed) under a new `## [x.y.z] - YYYY-MM-DD` heading.
2. Bump `<Version>` in `Directory.Build.props` (and `AssemblyVersion`, `FileVersion`, `InformationalVersion` to match): patch for fixes, minor for features, major for breaking changes.
3. Update the version in `docs/EXTENSION-INSTALL.md` (step 4 names the version to look for) and in the README release table and `Current version` line.
4. Run `build.ps1` (tests, publish, extension sync, forbidden-name scan) and then `build-installer.ps1` to rebuild the MSI.

The app embeds `CHANGELOG.md` as a resource and shows it in Help > About. The extension tests fail if the manifest version or the changelog's top entry does not match `Directory.Build.props`.
