# Nmkoder
Video encoding, muxing, and analysis GUI built with [Avalonia UI](https://avaloniaui.net/) on .NET 10, wrapping FFmpeg, FFprobe, and av1an.

![](https://i.imgur.com/c8XtSlG.png)



## Features

#### Input

- Supports all formats that ffmpeg can decode
- Either use **"Muxing Mode"** to either convert a single file or merge multiple files into one, or **"Batch Processing Mode"** to run an action on each file 
- Supports image sequence inputs (PNG/WEBP/JPEG/BMP) without requiring sequential filenames (FPS needs to be set manually)

#### Track List

- View codec, language, title and more (depending on stream type) of selected media stream
- Enable or disable streams with checkboxes - Disabled streams will not be included when encoding/muxing
- Re-order streams

#### Convert (FFmpeg)

- Encode video using ffmpeg and its encoder plugins
- Video Formats: **H264 (x264 or NVENC), H265 (x265 or NVENC), VP9, AV1**
- Image Formats: Animated **GIF**, **PNG** Sequence, **JPEG** Sequence
- Audio Formats: **AAC, Opus, Vorbis, E-AC-3, MP3, FLAC**
- Text-based Subtitle Formats: Mov_Text for MP4/MOV, SRT for MKV, WebVTT for WEBM
- All media types also have the option to **strip** (remove) or **copy** (mux without re-encoding) instead of encoding
- Set metadata (title and language) for each track
- Encoder Options: Set quality and speed/effort aka preset, set color format
- Quality Modes: Either use a **constant quality**, target **bitrate**, or target **filesize**
- Video Options: Resample frame rate, **resize** either using absolute or relative numbers, manually or **automatically crop** black bars
- Audio Options: Set quality and channels/layout
- Subtitle Options: Optionally **burn in** a subtitle track

#### AV1AN Chunked Encoding

- Encode video using [av1an](https://github.com/rust-av/Av1an) and supported encoders
- Video Formats: **H265 (x265), VP9 (VPX), AV1 (AOM or SVT-AV1)**
- Quality Modes: Either use a **constant quality** or target a **VMAF**, **SSIMULACRA2**, **Butteraugli**
  or **XPSNR** score (experimental; SSIMULACRA2 needs a VapourSynth metric plugin, bundled on Windows,
  XPSNR is scored by ffmpeg, and Butteraugli currently needs the GPU plugin Vship - see below)
- Same audio and video options as FFmpeg encoding
- Set AV1 film **grain synthesis** (disabled for H265/VP9 as this is exclusive to AV1)
- Av1an Options: Change splitting method, chunk creation method, amount of workers, and more
- Encodes can be stopped and resumed at any time

#### Utilities

- Utilities are "shortcuts" for actions that normally require long (and/or multiple) CLI commands
- Read Bitrates: Calculates stream size and average bitrate for each stream
- Get Metrics: Calculate quality metrics like **VMAF**, SSIM, PSNR
- Transfer Color Metadata: Copy color properties and HDR metadata from one file to another (e.g. from Bluray Remux to an encode)
- Concatenate Into Single MKV: Merge any amount of any compatible video format into a single MKV (e.g. for chunked encoding)
- Show Bitrate Chart: Samples the bitrate across the entire video and shows a graph allowing you to see where bitrate is higher or lower

## Compatibility

- Requires the [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (or publish self-contained, see below).
- Windows 10/11 64-bit is the primary target. Since the move from WinForms to Avalonia the app also builds and runs natively on Linux and macOS.
- `ffmpeg`, `ffprobe`, `mkvmerge` and `av1an` are looked up in the `bin` folder next to the executable first, then on `PATH`.

## Releases

Portable builds are produced by `.github/workflows/release.yml`. Push a `v*` tag to publish a
release, or run the workflow manually to get a draft.

Each archive is self-contained (no .NET install required) and `.github/scripts/bundle-tools.sh`
stages the external tools into `bin/`:

| | ffmpeg / ffprobe | MKVToolNix | av1an | VapourSynth | SVT-AV1, aomenc, x265 | vpxenc | VMAF models |
|---|---|---|---|---|---|---|---|
| win-x64 | bundled | bundled | bundled | bundled | bundled | see below | bundled |
| linux-x64 | bundled | use package manager | use package manager | use package manager | use package manager | use package manager | bundled |
| osx-x64 / osx-arm64 | `brew install ffmpeg` | `brew install mkvtoolnix` | `brew install av1an` | `brew install vapoursynth` | `brew install svt-av1 aom x265` | `brew install libvpx` | bundled |

Only Windows gets the av1an toolchain. av1an publishes prebuilt binaries for Windows only,
VapourSynth's portable build is Windows-only, and the encoders come from MSYS2's mingw64
packages, so Linux and macOS builds carry ffmpeg and the VMAF models and leave the rest to
the package manager. One thing the package managers do not cover: Target SSIMULACRA2 scores
its probes through the [vszip](https://github.com/dnjulek/vapoursynth-zip) VapourSynth plugin
(or the GPU-accelerated [vship](https://github.com/Line-fr/Vship)), which has to be installed
into VapourSynth's plugin directory by hand on Linux and macOS - without it, that quality mode
fails once av1an starts probing. Target Butteraugli needs vship specifically, for the reason
below. Target XPSNR needs no plugin: av1an scores it with ffmpeg's `xpsnr` filter, present in
the bundled ffmpeg and in any FFmpeg from 7.1 on.

The caveat on Butteraugli: every av1an release to date calls the CPU scoring plugin
([julek](https://github.com/dnjulek/vapoursynth-julek-plugin)) by the wrong function name
(`butteraugli` where the plugin registers `Butteraugli`), so that path fails at probe time no
matter what is installed. Until av1an fixes the invoke, Target Butteraugli works only through
[vship](https://github.com/Line-fr/Vship); the app stops the encode up front when the bundled
plugin folder holds no Vship, and warns where there is no such folder to check.

The AV1AN tab's toolchain is staged in the layout the app runs it from:

```
bin/av1an/av1an[.exe]        av1an itself
bin/av1an/vsynth/            VapourSynth + embedded Python (VSPipe)
bin/av1an/vsynth/vs-plugins/ BestSource, L-SMASH-Works and FFMS2, for the matching chunk methods,
                             vszip, which scores Target SSIMULACRA2 probes, and julek, staged
                             for Butteraugli until av1an can call it (see the caveat above)
bin/av1an/enc/               SvtAv1EncApp, aomenc and x265
```

`vsynth` and `enc` are prepended to av1an's `PATH`, so nothing needs installing system-wide.

Tool downloads are best-effort: an unreachable upstream is reported and skipped rather than
failing the release, and the workflow's job summary lists exactly what each build shipped.
Binaries are resolved from each project's releases at build time, except SvtAv1EncApp,
aomenc and x265, which upstream does not publish for Windows and so come from MSYS2's
mingw64 packages. Override the sources with the `AV1AN_REPO`, `SVTAV1_REPOS`,
`VAPOURSYNTH_REPO`, `LSMASH_REPO`, `FFMS2_REPO`, `BESTSOURCE_REPO`, `VSZIP_REPO`,
`VSZIP_TAG`, `VSJULEK_REPO`, `VSJULEK_TAG`, `PYTHON_EMBED_VERSIONS`, `MSYS2_ENCODERS`,
`MSYS2_ROOT`, `GH_RELEASE_SCAN` and `MKVTOOLNIX_VERSION` environment variables.

**vpxenc comes from a third-party build.** No project publishes a prebuilt Windows
vpxenc: the WebM project ships source only, ShiftMediaProject builds the library rather
than the CLI, and MSYS2's `libvpx` package leaves the encoder out. Windows builds
therefore take <https://jeremylee.sh/bins/vpx.7z>, the build the av1an ecosystem uses,
and stage the `vpxenc.exe` inside it.

That is one person's server with no signed provenance, so what arrives is checked rather
than trusted: the bundler verifies the staged file is actually a Windows executable, and
`bin/THIRD-PARTY.txt` records the URL it came from. The site publishes a SHA1 for each
binary - set `VPXENC_SHA1` to the one listed for `vpxenc.exe` to pin the build to one
that has been looked at, and anything else is rejected rather than shipped. It is left
unpinned by default because the build rolls forward and a stale hash would reject every
future one.

Note that the site's certificate does not validate. Downloads are made with certificate
verification on, so if that is still true when a release is built, vpxenc is reported as
a failed download and skipped rather than fetched insecurely.

Point `VPXENC_URL` at a different build (a bare `.exe` or an archive containing one) to
override the source, or set it empty (`VPXENC_URL=`) to skip vpxenc entirely. Without
vpxenc the AV1AN tab's VP9 entry has no encoder behind it; the regular encoding tab's VP9
support goes through the bundled ffmpeg and is unaffected either way.

DGDecNV is the one chunk method left uncovered - it needs a licensed DGDecNV install.

`bin/iso639.csv`, the language table that names audio and subtitle tracks, is not
downloaded - it lives in `Nmkoder/BinFiles` and every build copies it into `bin`.
Regenerate it with `.github/scripts/gen-iso639.py` when the ISO registers move.

## Building

```
dotnet build Nmkoder.sln -c Release
```

To produce a self-contained, single-file build for a given platform:

```
dotnet publish Nmkoder/Nmkoder.csproj -c Release -r win-x64   --self-contained -p:PublishSingleFile=true
dotnet publish Nmkoder/Nmkoder.csproj -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
dotnet publish Nmkoder/Nmkoder.csproj -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true
```
