# Third-party components

NeoDM is released under the MIT licence (see `LICENSE`). This page lists programs that NeoDM can fetch or start on your behalf but does not ship inside the installer.

## ffmpeg (optional helper tool, downloaded only on request)

Streaming downloads (HLS and DASH, see `docs/STREAMS.md`) sometimes arrive as separate picture and sound tracks, or as transport streams that you want as an MP4 file. Joining them without re-encoding needs a muxer. NeoDM uses ffmpeg for that. The tool is **not** part of the installer and **not** redistributed by NeoDM. It is downloaded only when you press the install button for it, to `%LOCALAPPDATA%\NDM\tools\ffmpeg\`, and it is started as a separate program (NeoDM does not link against it).

| | |
|---|---|
| Project | FFmpeg, https://ffmpeg.org |
| Windows build used | gyan.dev "essentials" release build, https://www.gyan.dev/ffmpeg/builds/ |
| Version | 9.0.2 |
| Download address (pinned, https only) | https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip |
| Archive size | 114768076 bytes |
| Archive SHA-256 | `60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba` (equals the value published at the same address with `.sha256` appended) |
| Program taken from the archive | `ffmpeg-9.0.2-essentials_build/bin/ffmpeg.exe`, 105423872 bytes |
| ffmpeg.exe SHA-256 | `3256173f3f8bffd7df12227c68adf68025edb1832273a9530688a7bb1ed8edec` |
| Licence of this build | GNU General Public License version 3 (the essentials build is configured with GPL components) |
| Source of this build | https://github.com/FFmpeg/FFmpeg/commit/946fcce07b (the commit named in the archive's README); the build configuration is in the archive's README and printed by `ffmpeg -version` |

Source code offer: the complete corresponding source code of the program that NeoDM downloads is available from the FFmpeg project (https://ffmpeg.org/download.html and the commit above) and from the build's publisher (https://www.gyan.dev/ffmpeg/builds/). NeoDM only downloads the build on your request; if you want the source for the build you received, those addresses are where to ask. NeoDM itself stays MIT licensed because it does not contain or link ffmpeg code.

How NeoDM treats the download is described in `SECURITY.md` ("Helper tool download and execution"): one pinned https address on one host, size limits, SHA-256 of the archive and of the program checked against the values above (compiled into the app), and the hash checked again every time before the program is started.

### Updating the pin

1. Pick a versioned release at the publisher, download it and compare its SHA-256 with the published `.sha256` file.
2. Extract `bin/ffmpeg.exe` and compute its SHA-256 and size.
3. Change `ToolManager.FfmpegPin` (address, version, archive size and hash, entry name, program size and hash) and this page together. The test "pinned constants are well formed and documented" fails when they disagree.
4. Run the opt-in real test (`dotnet run --project tests/NDM.Tests -c Release -- --real-ffmpeg`).

A build that is no longer served at the pinned address makes the install button fail with a clear message; nothing is installed in that case.


## YouTube tools, optional desktop downloads (1.7.0)

The desktop Add URL dialog can download the following tools only after explicit consent. They are separate processes, not linked into NeoDM or included in its installer or extension. NeoDM's own code remains MIT. These external binaries have their own licenses.

| Tool | Official pinned asset | Size | SHA-256 |
|---|---|---:|---|
| yt-dlp 2026.10.26, standalone Windows x64 with EJS | https://github.com/yt-dlp/yt-dlp/releases/download/2026.10.26/yt-dlp.exe | 23575744 | `f5389d3698ecde4ef06f4e6b1ce0294fe9ac1396228c8990b8d3416d6d177a7d` |
| Deno 2.9.8, archive | https://github.com/denoland/deno/releases/download/v2.9.8/deno-x86_64-pc-windows-msvc.zip | 42917472 | `cba84b26e0df5ae7f74fc66b86a95b866872a86292ebd84fa1e959927edd93bf` |
| Deno executable extracted from `deno.exe` only | Same archive | 98081568 | `924397800533d3eb0ed0c889b20ad70e18b970d93acd8135bc043f425e528266` |

The first two hashes match the official GitHub release asset digest fields, queried on 2026-11-01. Both assets were downloaded and hashed independently, and the extracted Deno executable was hashed on disk. `YoutubeService.ExtractorPin` and `RuntimePin` hold these exact values. Pin changes require a new NeoDM release and deliberate verification, never a helper self-update.

The yt-dlp source is Unlicense. Its standalone PyInstaller executable includes GPLv3+ components and the combined executable is GPLv3+, not merely Unlicense. The bundled EJS package is Unlicense and includes MIT astring and ISC meriyah components. Upstream licensing and corresponding source: https://github.com/yt-dlp/yt-dlp/blob/2026.10.26/README.md#license and https://github.com/yt-dlp/yt-dlp/tree/2026.10.26 and https://github.com/yt-dlp/ejs. See the official EJS setup guide: https://github.com/yt-dlp/yt-dlp/wiki/EJS. Official standalone executables include EJS; NeoDM disables remote EJS downloads and plugins.

Deno is MIT licensed, with bundled third-party notices and source at https://github.com/denoland/deno/tree/v2.9.8 and https://github.com/denoland/deno/blob/v2.9.8/LICENSE.md. No runtime from PATH is used for YouTube extraction. The optional ffmpeg GPL build above is needed for merging separate tracks or MP3 conversion, not for saving an original audio track. Its existing explicit PATH option is unchanged.

GitHub asset redirects are restricted to https on github.com or release-assets.githubusercontent.com. Tools are stored below the app state folder in `tools/yt-dlp` and `tools/deno`. Close active YouTube jobs before removing those tool folders. No tool binary is redistributed in the NeoDM release assets.
