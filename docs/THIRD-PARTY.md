# Third-party components

NeoDM is released under the MIT licence (see `LICENSE`). This page records the helper pins. Version 1.8.0 uses an installed bundle without runtime downloads. The installer verifies exact source and notice material as described in `BUNDLE-VERIFICATION.md`. Publish `NeoDM-helper-sources-1.8.0.zip` beside the installer in the same release before distributing the binaries.

## ffmpeg

Streaming downloads (HLS and DASH, see `docs/STREAMS.md`) sometimes arrive as separate picture and sound tracks, or as transport streams that you want as an MP4 file. Joining them without re-encoding needs a muxer. NeoDM uses ffmpeg for that. The tool runs as a separate process. Version 1.8.0 resolves it from the immutable installed helper bundle. The installer refuses missing or changed source and dependency material.

| | |
|---|---|
| Project | FFmpeg, https://ffmpeg.org |
| Windows build used | NevermindNilas/TAS-FFMPEG shared release |
| Version | 9.0.2-tas |
| Download address (pinned, https only) | https://github.com/NevermindNilas/TAS-FFMPEG/releases/download/v9.0.2/tas-ffmpeg-9.0.2-win64.zip |
| Archive size | 35544612 bytes |
| Archive SHA-256 | `148dfe8595ebf10a217d7e56fef732bcb2fabd3e33dfc4a37accf76f864ba19b`, matching the official release asset digest |
| Program taken from the archive | `tas-ffmpeg-9.0.2-win64/bin/ffmpeg.exe`, 510976 bytes |
| ffmpeg.exe SHA-256 | `9a765b60a13aff4b777d380e3bd9249ad1f420dbdd28141771190ff6c49de7a4` |
| Licence of this build | GNU General Public License version 2 or later, configured with GPL components and without nonfree or version3 |
| Source of this build | The supplier's `tas-ffmpeg-9.0.2-corresponding-source.tar.xz` from the same v9.0.2 release, 374513656 bytes, SHA-256 `539994be99dfbe0a6aa4d56f4d6d024dee92f655f146172d1935177377b0b1ee` |

The source archive retains the source trees, dependency archives, patches, build scripts, environment definitions and versions lock. The binary archive retains configuration, license texts and a manifest with hashes for all runtime files. NeoDM ships ffmpeg.exe, ffprobe.exe and seven private DLLs. Every runtime file has a compiled size and SHA-256 pin in `FfmpegRuntime.cs` or `ToolManager.cs`, checked through read handles held for the process lifetime. Missing or changed private DLLs cannot fall back to PATH. The source archive is retained unchanged in the helper-source release asset.

How NeoDM treats the download is described in `SECURITY.md` ("Helper tool download and execution"): one pinned https address on one host, size limits, SHA-256 of the archive and of the program checked against the values above (compiled into the app), and the hash checked again every time before the program is started.

### Updating the pin

1. Pick a versioned release with complete corresponding source, download both archives and compare their SHA-256 values with the official asset digests.
2. Extract all private runtime files and compute their SHA-256 values and sizes. Compare them with the supplier manifest and inspect their imported libraries.
3. Change `ToolManager.FfmpegPin`, `FfmpegRuntime.cs`, `bundled-tools.lock.json` and this page together. The provisioning gate compares compiled and build-time pins. The test "pinned constants are well formed and documented" fails when the main pin and docs disagree.
4. Run the opt-in real test (`dotnet run --project tests/NDM.Tests -c Release -- --real-ffmpeg`).

A missing pinned publisher asset blocks build-time preparation. Missing or modified installed helpers fail clearly without runtime replacement downloads.


## YouTube tools

The desktop uses these exact tools from the installed bundle. They are separate processes and are not linked into NeoDM. Their licenses are distinct from the MIT license for NeoDM source. No tool is downloaded by the installed app.

| Tool | Official pinned asset | Size | SHA-256 |
|---|---|---:|---|
| yt-dlp 2026.10.26, standalone Windows x64 with EJS | https://github.com/yt-dlp/yt-dlp/releases/download/2026.10.26/yt-dlp.exe | 23575744 | `f5389d3698ecde4ef06f4e6b1ce0294fe9ac1396228c8990b8d3416d6d177a7d` |
| Deno 2.9.8, archive | https://github.com/denoland/deno/releases/download/v2.9.8/deno-x86_64-pc-windows-msvc.zip | 42917472 | `cba84b26e0df5ae7f74fc66b86a95b866872a86292ebd84fa1e959927edd93bf` |
| Deno executable extracted from `deno.exe` only | Same archive | 98081568 | `924397800533d3eb0ed0c889b20ad70e18b970d93acd8135bc043f425e528266` |

The first two hashes match the official GitHub release asset digest fields, queried on 2026-11-01. Both assets were downloaded and hashed independently, and the extracted Deno executable was hashed on disk. `YoutubeService.ExtractorPin` and `RuntimePin` hold these exact values. Pin changes require a new NeoDM release and deliberate verification, never a helper self-update.

The yt-dlp source is Unlicense. Its standalone PyInstaller executable includes GPLv3+ components and the combined executable is GPLv3+, not merely Unlicense. The bundled EJS package is Unlicense and includes MIT astring and ISC meriyah components. Upstream licensing and corresponding source: https://github.com/yt-dlp/yt-dlp/blob/2026.10.26/README.md#license and https://github.com/yt-dlp/yt-dlp/tree/2026.10.26 and https://github.com/yt-dlp/ejs. See the official EJS setup guide: https://github.com/yt-dlp/yt-dlp/wiki/EJS. Official standalone executables include EJS; NeoDM disables remote EJS downloads and plugins.

Deno is MIT licensed, with third-party components and source at https://github.com/denoland/deno/tree/v2.9.8 and https://github.com/denoland/deno/blob/v2.9.8/LICENSE.md. Its official ZIP does not contain a generated notice aggregate. NeoDM retains a reconstructed notice/source superset from the exact source release and Cargo.lock, including all 1048 checksum-verified crates, native V8/ICU and Rust notices, source copyright statements and declared license terms. See `BUNDLE-VERIFICATION.md` for the method and boundaries. No runtime from PATH is used for YouTube extraction. The ffmpeg GPL build above is needed for merging separate tracks or MP3 conversion, not for saving an original audio track. Its existing explicit PATH option for ordinary streams is unchanged.

Build preparation verifies official asset digests and extracted executable/DLL hashes. Runtime execution verifies installed runtime bytes through held read handles. Runtime caches and temporary files stay in app state, not Program Files. See `BUNDLE-VERIFICATION.md` for the source-material review and release gate.

## Microsoft.Web.WebView2 (1.0.2792.45)

NeoDM 1.12.0 uses the Microsoft.Web.WebView2 NuGet package for the in-app login browser (Settings, site logins). Only the managed wrapper assemblies (`Microsoft.Web.WebView2.Core.dll`, `Microsoft.Web.WebView2.Wpf.dll`) and the 64-bit `WebView2Loader.dll` are shipped. The WebView2 runtime itself is not bundled: it is the system Microsoft Edge WebView2 Runtime. When it is missing, NeoDM shows a plain message and the rest of the app works unchanged. The package is distributed under the Microsoft BSD-style licence below (package `LICENSE.txt`, unchanged).

```
Copyright (C) Microsoft Corporation. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

   * Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.
   * Redistributions in binary form must reproduce the above
copyright notice, this list of conditions and the following disclaimer
in the documentation and/or other materials provided with the
distribution.
   * The name of Microsoft Corporation, or the names of its contributors 
may not be used to endorse or promote products derived from this
software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```
