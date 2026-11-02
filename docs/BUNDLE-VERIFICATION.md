# Bundled helper verification for 1.8.0

The installed app resolves yt-dlp, Deno and ffmpeg from `tools/<name>/<name>.exe` beside the app. It checks compiled sizes and SHA-256 values through read handles before every launch. The ffmpeg bundle also includes ffprobe and seven private DLLs. All nine runtime files stay locked against writes and deletion until the process finishes. Missing, changed or unlisted private libraries fail closed, rather than falling back to DLLs on PATH. The app does not download a replacement. Temporary files and Deno runtime state remain under the writable app state directory.

## Build preparation

Run `python tools/provision-bundled-tools.py --prepare`. The yt-dlp and Deno pins are unchanged. FFmpeg now uses the TAS-FFMPEG shared release with verified matching corresponding source. Official GitHub asset digests are checked before fresh downloads. Archives and every extracted runtime file are independently checked. Prepared binaries stay under ignored `artifacts/helper-bundle`, not in Git.

Run `python tools/provision-bundled-tools.py --verify --output dist/app` after restoring the reviewed source material under `artifacts/helper-bundle`. The installer invokes this check and refuses missing or modified material. There is no override switch. The source lock records exact bytes, coverage and review boundaries, not a legal certification flag.

## Reviewed distribution material

The source and license collection is recorded in `tools/bundled-source.lock.json`. It retains the supplier FFmpeg source archive, exact yt-dlp generated notices extracted from the pinned executable, 21 requirement-hash Python source distributions, CPython 3.14.10 with ten external sources, custom PyInstaller sources and recipes, EJS preferred source and exact npm dependencies, and all nine native curl build inputs. The curl DLL embedded in yt-dlp matches the official curl-impersonate 2.3.1 library byte for byte. The FFmpeg source and binary archives have identical versions.lock bytes and matching upstream FFmpeg source digests.

Deno material includes the exact official source release, Cargo.lock and all 1048 registry source crates verified against its checksums. Original crate notices and source copyright lines are retained with declared license terms and author metadata. V8 notices are collected from its exact gitlink revisions where available. Additional current upstream notice copies are labeled as supplements. Rust 1.95 and vendored JavaScript notices are included. This is a reconstructed, over-inclusive dependency inventory, not an official generated binary SBOM. Deno does not publish a generated notice asset for this release. Some crates omit license text; common declared license texts and their actual source attributions are retained without inventing copyright owners or years. MPL-covered source is included in the source asset.

The gate compares compiled pins with `bundled-tools.lock.json`. The source lock's `toolHashes` bind the three primary helpers, and `runtimeHashes` bind all eleven executable/DLL files. Each source/notice file has a relative `path`, `size` and `sha256`. The gate checks these bytes before packaging. Source archives, notices, retrieval evidence and collection scripts are packaged reproducibly in `NeoDM-helper-sources-1.8.0.zip` with a fixed ZIP timestamp and sorted file list. Already-compressed archives are stored unchanged. The ZIP contains its own material manifest and binary pins. Only helpers and license/source-access notices go into the MSI, not a second copy of the large source archive.

## Distribution obligations and boundaries

- Publish the source ZIP beside the binary in the same release before distributing the binary. The installed `licenses/SOURCE-ACCESS-1.8.0.md` identifies the exact source asset and release URL. Local preparation is not publication.
- Preserve original licenses and copyright notices. The About credits and folder button are shortcuts to the notices, not substitutes for them.
- GPLv3 sections 1 and 6(d), and GPLv2 section 3(a), distinguish corresponding source from general-purpose tools and qualifying system libraries. Do not invent a requirement to distribute the source of the Windows OS or every general-purpose compiler. Optional linked codec libraries are covered by the supplier source archive.
- The unmodified upstream helpers have not been independently rebuilt byte for byte. Source-input correspondence and ordinary app-build reproducibility are separate claims.

Primary references are the pinned yt-dlp README and generated notices, Deno v2.9.8 LICENSE.md and source asset, TAS-FFMPEG's matching binary/source manifests and licensing notes, and the GNU GPL texts. Retrieval records include failed probes and the notice reconstruction method. No unsupported written offer or fabricated legal approval is used.

## Verification boundaries

Deterministic suites use injected fixture tools without internet. A build-time preparation run is not a runtime install, an MSI installation, or a public-site download test. Keep those claims separate. Do not install the MSI or touch the user's queue, browser profiles or registry while verifying this work.
