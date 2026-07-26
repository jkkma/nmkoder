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

- Encode video using [av1an](https://github.com/master-of-zen/Av1an) and supported encoders
- Video Formats: **H265 (x265), VP9 (VPX), AV1 (AOM or SVT-AV1)**
- Quality Modes: Either use a **constant quality** or target a **VMAF** score (experimental)
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

| | ffmpeg / ffprobe | MKVToolNix | av1an + SVT-AV1 | aomenc + vpxenc + x265 | VapourSynth | VMAF models |
|---|---|---|---|---|---|---|
| win-x64 | bundled | bundled | bundled | bundled | bundled | bundled |
| linux-x64 | bundled | use package manager | bundled | use package manager | use package manager | bundled |
| osx-x64 / osx-arm64 | `brew install ffmpeg` | `brew install mkvtoolnix` | `brew install av1an svt-av1` | `brew install aom libvpx x265` | `brew install vapoursynth` | bundled |

The AV1AN tab's toolchain is staged in the layout the app runs it from:

```
bin/av1an/av1an[.exe]        av1an itself
bin/av1an/vsynth/            VapourSynth + embedded Python (VSPipe)
bin/av1an/vsynth/vs-plugins/ L-SMASH-Works and FFMS2, for the matching chunk methods
bin/av1an/enc/               SvtAv1EncApp, aomenc, vpxenc and x265
```

`vsynth` and `enc` are prepended to av1an's `PATH`, so nothing needs installing system-wide.

Tool downloads are best-effort: an unreachable upstream is reported and skipped rather than
failing the release, and the workflow's job summary lists exactly what each build shipped.
Binaries are resolved from each project's latest release at build time, except aomenc,
vpxenc and x265, which upstream does not publish for Windows and so come from MSYS2's
mingw64 packages. Override the sources with the `AV1AN_REPO`, `SVTAV1_REPOS`,
`VAPOURSYNTH_REPO`, `LSMASH_REPO`, `FFMS2_REPO`, `PYTHON_EMBED_VERSIONS`,
`MSYS2_ENCODERS`, `MSYS2_ROOT` and `MKVTOOLNIX_VERSION` environment variables.

The chunk method dropdown's other entries are not covered: DGDecNV needs a licensed
DGDecNV install, and BestSource is not bundled - drop its DLL into `vs-plugins/` to use it.

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
