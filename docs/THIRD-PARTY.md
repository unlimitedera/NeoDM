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
