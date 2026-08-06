# Nmkoder

Video encoding, muxing and analysis GUI built with [Avalonia UI](https://avaloniaui.net/) on .NET 10,
wrapping FFmpeg, FFprobe and [av1an](https://github.com/rust-av/Av1an). Runs natively on Windows,
Linux and macOS.

This is a fork of [n00mkrad/nmkoder](https://github.com/n00mkrad/nmkoder), which was WinForms on .NET
Framework and Windows-only. The UI has been rebuilt, the app ported to three platforms, and a fair
amount added on top - see [What's new since the fork](#whats-new-since-the-fork).

![](https://i.imgur.com/c8XtSlG.png)

## Download

Portable archives for all four platforms are attached to every
[release](https://github.com/jkkma/nmkoder/releases). Unpack and run: each build is self-contained,
so nothing has to be installed to start it, and the Windows archive carries the entire external
toolchain in `bin/` - ffmpeg, av1an, VapourSynth, the encoders and MKVToolNix. The Linux and macOS
archives carry less and lean on your package manager; see
[what a release bundles](#what-a-release-bundles).

### Scoop

On Windows, [Scoop](https://scoop.sh) can install and update it for you. This repository doubles as
a bucket:

```
scoop bucket add nmkoder https://github.com/jkkma/nmkoder
scoop install nmkoder-avalonia
```

The app is `nmkoder-avalonia` rather than `nmkoder` because Scoop's community `extras` bucket
carries the pre-fork WinForms Nmkoder (1.10.0) under that name, and a bare `scoop install nmkoder`
resolves to it. The longer name is unambiguous, and the two can be installed side by side.

`scoop update nmkoder` moves to the newest release from then on, and the manifest is pointed at each
one as it is published, so there is no lag. Settings and logs (`data` and `logs`) are persisted
across updates; `bin` is not, being the bundled toolchain that every release replaces - anything you
drop in there yourself belongs in a portable copy rather than a Scoop install.

## What's new since the fork

### The application

- **Ported from WinForms to Avalonia on .NET 10**, with native builds for `win-x64`, `linux-x64`,
  `osx-x64` and `osx-arm64`. Linux and macOS are first-class rather than a WINE suggestion.
- **A dark theme throughout**, rather than the system's default control colours.
- **A release pipeline**: every build is published self-contained and portable, with the external
  tools staged into `bin/` and the job summary listing exactly what each archive shipped.
- **The log is its own panel** - per-line severity colouring, copy, save, clear, and a button that
  opens the session's log folder, which also holds the raw ffmpeg, av1an and mkvmerge logs.
- **The window remembers itself**: size, position, open tab and log height, plus the last browsed
  folders and a recent-files list. Quick Convert's encode settings persist too. (The AV1AN Video tab
  deliberately does not - a CRF, a resize or a QTGMC pass left armed from last week is expensive.)

### Deinterlacing

None of this existed before the fork - there was no deinterlacing in the app at all.

- **Sources are checked rather than assumed.** The container's field-order flag is read first - an
  MPEG-2 tape capture or a DV file says what it is - and where it says nothing, a few hundred frames
  are decoded through ffmpeg's `idet`, then measured field-gap by field-gap so that a vertical pan
  over fine detail is not mistaken for combing.
- **The Deinterlace row only appears for files whose fields warrant it**, so a modern download never
  shows the control and a tape capture arrives with the right engine already selected.
- **QTGMC**, the motion-compensated deinterlacer, run through VapourSynth (bundled on Windows) with
  the whole plugin chain it needs, falling back to ffmpeg's bwdif where VapourSynth cannot run it.
  Optionally one frame per field, so none of the motion is thrown away.
- **Both encode tabs deinterlace.** av1an filters each chunk with ffmpeg, which has nowhere to
  evaluate a VapourSynth script, so picking QTGMC there renders the video through it once beforehand
  into a near-lossless intermediate and encodes that.
- **A Deinterlace Video utility** for when the deinterlaced file itself is what you want, with its
  own settings separate from either tab's.

### HDR tone mapping

- **HDR sources can be converted to SDR**, on both encode tabs. The row only appears for a file whose
  transfer curve says PQ or HLG, and it defaults to doing nothing - the other reason to load an HDR
  file is to re-encode it *as* HDR - so it exists to say the file is HDR and let you choose.
- **The roll-off is built around the peak brightness the file declares** (MaxCLL, else its mastering
  display), because FFmpeg's tone-mapper does not read either of them: measured, the same clip with
  and without that metadata tone-maps identically. The widely-copied chain leaves this at its default,
  which clips everything above about 374 nits to flat white - every highlight on a 1000-nit master.
  The readout names the peak used and whether it was declared or assumed.
- It runs **before any crop, scale or burnt-in subtitle**, so subtitles are not dragged through a
  gamut conversion written for the picture, and the output is retagged BT.709 with the HDR metadata
  dropped - including on the AV1AN tab, where the encoders are handed colour as numbers and would
  otherwise write SDR pixels into a file tagged HDR.

### Framing: resize, crop, borders and trim

- **Resize is a dropdown with presets** - 2160p through 360p as boxes the picture is fitted inside,
  75/50/25% proportions, and a Custom dialog for exact sizes with letterbox or stretch - replacing
  two free-text width/height boxes. Underneath it, a per-file readout of the frame the encoder will
  actually be handed.
- Anamorphic sources are de-squeezed where the encoder cannot carry the flag, upscaling is stated
  rather than silent, and a target ffmpeg cannot scale to is refused up front instead of failing one
  chunk at a time.
- **Crop keeps the rectangle inside the frame and on the chroma grid**, and a crop too large for the
  file - the usual way a batch goes wrong, four edges outliving the file they were set for - stops
  the run naming the file and the numbers.
- **Borders**: pad out to a target aspect ratio (16:9, 4:3, 1:1, 9:16, 21:9) without scaling. Which
  bars are needed is worked out per file, so a 2.39:1 film and a 4:3 capture both reach 16:9 from one
  dropdown entry. Applied after the crop and the resize, so a scaler never runs over a hard edge.
- **Trim is picked visually.** The in/out dialog shows the frame at the playhead while you choose the
  section, in the shape LosslessCut made familiar, and the same dialog serves Quick Convert, the
  AV1AN tab (which had no trim at all before) and the lossless Cut utility. All three modes - nearest
  keyframe, exact time, frame numbers - land where they say they do, and a section that does not fit
  the file is caught before the encode rather than producing an empty output.

### The AV1AN tab

- **Target SSIMULACRA2, Target Butteraugli and Target XPSNR** quality modes alongside Target VMAF,
  each checked up front against what the installed av1an and its plugins can actually score.
- **Vship staged per machine** on Windows: the GPU check runs before a metric-targeted encode and
  installs the build this machine passes, so Butteraugli works out of the box and SSIMULACRA2 moves
  to GPU scoring where it can.
- **x264** joins aomenc, SVT-AV1, x265 and vpxenc as an encoder av1an can drive from here.
- **The advanced argument grid is grouped into categories**, and every argument carries a full
  explanation and example values on right-click, rather than a one-line hint in a flat list.
- **Content presets** for the argument grid - Anime / Cel Animation and Game Capture / Gameplay -
  written for the SVT-AV1 PSY line the release bundles. Anything the binary in front of them does not
  support is dropped as the preset is applied, and a parameter typed by hand that the encoder does not
  know stops the run instead of having av1an reject the whole command.
- **Workers and threads-per-worker are derived together** from the core count instead of one being a
  literal, SVT-AV1 runs two workers fewer than the others because it loads a core harder, and
  "Threads per Worker" now actually limits x265 (which has no `--threads`, only `--pools`).
- **Progress is measured from av1an's own temp folder** - chunk counts out of `scenes.json` and
  `done.json` - rather than parsed out of log lines that av1an stopped printing several releases ago.
- Frame-rate resampling no longer kills a run partway through, av1an's log lands in the encode's temp
  folder instead of beside the binary, and a target-quality mode meeting a filter chain says so
  (av1an's probes never see the filters).

### Quick Convert

- **The command is built in one place, in an order that holds together**: stream maps that know
  whether a filtergraph exists, per-input trim arguments, `-metadata:s:N` indices that count the
  streams which actually reach the output, and a two-pass stats log written into the session folder
  rather than the working directory.
- **Target Filesize divides by the right numbers** - the trimmed duration, and a copied track's own
  bitrate rather than whatever the disabled bitrate spinner happened to hold.
- **Per-track audio configuration** seeds from the channel layout you asked for, and the dropdown it
  overrides is disabled while it governs rather than left looking live.
- **Paths survive the shell and the filtergraph.** Burning in a subtitle track from a path containing
  a space, a colon, an apostrophe, an `=`, a `$` or a backtick works on all three platforms; it
  previously failed on most of them, and on Windows on every path there is.

### Audio

- **Loudness normalization (EBU R128)** on the Quick Convert tab, to -14, -16 or -23 LUFS. Each track is
  measured in a pass of its own and then encoded with a single flat gain, which is the part that matters:
  ffmpeg's one-pass `loudnorm` hits the same number by riding the gain, and measured on a source whose
  quiet passage sat 26 dB under its loud one it brought the two to within 1.3 dB of each other. The
  channel conversion runs inside the same filter, because the app's own downmix would otherwise happen
  afterwards and leave a 5.1 source 7.7 dB off the target it asked for.

### Utilities

- **Cut Video** - keep a chosen section, copied out without re-encoding, picked in the same visual
  dialog as the trim.
- **Deinterlace Video** - a deinterlaced copy into a near-lossless MKV with audio and subtitles
  carried across.
- **OCR Bitmap Subtitles** is now a card on the Utilities tab; the code was there before but nothing
  in the UI reached it.

### While a job runs

- **Pause actually pauses**, freezing the whole process tree rather than the launcher, and Stop ends
  a run cleanly.
- **Live progress in the footer** with an ETA, whichever tool is running. QTGMC's progress is
  measured against the frame count VapourSynth reports rather than the duration in the container,
  because a tape capture's duration is whatever its capture card claimed - and when a run proves its
  own target wrong, the bar says so instead of sitting at 100%.
- **The OS notifies you when a run ends** and the window is not in the foreground, through
  `notify-send`, `osascript` or the Windows App SDK.
- **What the encode did to the file size** is reported when it finishes.
- **Shutdown when done**, with a 60-second countdown you can call off.
- **Batch mode works**, with per-file status in the file list and output-name templates
  (`{name}`, `{codec}`, `{crf}`, `{index}`, `{date}` and more) - a batch overwrites the output box
  per file, so the template is the only say you get in what twelve files are called.

### Fixes worth naming

- The Metrics utility had been passing the VMAF model file as libvmaf's *log path*, which scored
  every run against the built-in default and overwrote the bundled model with an XML log - which
  av1an was then pointed at. Models are selected by version now, and no path goes into that filter.
- The bitrate readout reported `Size: 0B (0.0%)` for every stream, ffmpeg having renamed the unit in
  its stats line from `kB` to `KiB`.
- Colour metadata is read using the names ffprobe actually prints and re-spelled per encoder, aomenc
  refusing a name it does not know by encoding nothing at all.
- A missing mkvmerge - routine on Linux and macOS - is detected before it is needed, rather than
  showing up as an av1an encode that quietly finished without audio or a concat that failed naming a
  temp path.
- Animated GIF could not be produced at all unless some other filter happened to be configured.

## Features

#### Input

- Supports all formats that ffmpeg can decode
- Either use **"Muxing Mode"** to convert a single file or merge multiple files into one, or
  **"Batch Processing Mode"** to run an action on each file, with a naming template for the outputs
- Supports image sequence inputs (PNG/WEBP/JPEG/BMP) without requiring sequential filenames
  (FPS needs to be set manually)
- Drag and drop anywhere in the window, an "Add Folder" button, and a recent-files list

#### Track List

- View codec, language, title and more (depending on stream type) of the selected media stream
- Enable or disable streams with checkboxes - disabled streams are not included when encoding/muxing
- Re-order streams, and set the default audio and subtitle track

#### Quick Convert (FFmpeg)

- Encode video using ffmpeg and its encoder plugins
- Video Formats: **H.264 (x264 or NVENC), H.265 (x265 or NVENC), VP9, AV1 (SVT-AV1 or AOM)**
- Image Formats: Animated **GIF**, **PNG** Sequence, **JPEG** Sequence
- Audio Formats: **AAC, Opus, Vorbis, E-AC-3, MP3, FLAC**, globally or configured per track
- Text-based Subtitle Formats: Mov_Text for MP4/MOV, SRT for MKV, WebVTT for WEBM
- All media types also have the option to **strip** (remove) or **copy** (mux without re-encoding)
- Set metadata (title and language) for each track, and copy metadata or chapters from any loaded file
- Encoder Options: set quality and speed/effort aka preset, set colour format
- Quality Modes: a **constant quality**, a target **bitrate**, or a target **filesize**
- Video Options: resample the frame rate, **resize** from presets or an exact size, manually or
  **automatically crop** black bars, pad out to a target **aspect ratio**, and **trim** to a section
  picked while watching the frame you are on
- **HDR to SDR tone mapping** for PQ and HLG sources, with the roll-off built around the peak
  brightness the file declares, and the output retagged BT.709
- **Deinterlacing**, its row shown only for files whose fields warrant it - a tape, DVD or camcorder
  capture - and off screen entirely for a modern progressive download. Uses **QTGMC** through
  VapourSynth where it can (bundled on Windows), otherwise ffmpeg's bwdif, and can output one frame
  per field so none of the motion is thrown away
- Audio Options: set quality and channels/layout
- **Loudness normalization** to a standard target (-14 / -16 / -23 LUFS, EBU R128), measured per track
  first and then applied as one flat gain, so the mix keeps its dynamics - a single-pass normalization
  reaches the same number by riding the gain, which lifts quiet passages by however much they were quiet
- Subtitle Options: optionally **burn in** a subtitle track

#### AV1AN Chunked Encoding

- Encode video using [av1an](https://github.com/rust-av/Av1an) and supported encoders
- Video Formats: **AV1 (SVT-AV1 or AOM), H.265 (x265), H.264 (x264), VP9 (VPX)**
- Quality Modes: a **constant quality**, or target a **VMAF**, **SSIMULACRA2**, **Butteraugli** or
  **XPSNR** score (experimental; SSIMULACRA2 needs a VapourSynth metric plugin, bundled on Windows,
  XPSNR is scored by ffmpeg, and Butteraugli currently needs the GPU plugin Vship, which the Windows
  bundle ships and enables per machine - see below)
- Same audio, metadata and framing options as FFmpeg encoding, trim included
- **Deinterlacing** too, its own setting and including **QTGMC**: av1an filters each chunk with
  ffmpeg, which has nowhere to run a VapourSynth script, so picking QTGMC renders the video through it
  once beforehand - into a near-lossless intermediate that av1an then encodes, optionally at one frame
  per field. Automatic and the ffmpeg deinterlacers run inside av1an as before, at the source frame rate
- **HDR to SDR tone mapping** as well, with the colour the encoder is told about following the
  conversion rather than the source
- Set AV1 film **grain synthesis** (disabled for H.264/H.265/VP9 as this is exclusive to AV1)
- **Advanced encoder arguments** in a grid grouped by category, each with a full explanation and
  example values on right-click, plus content presets for anime and for game capture
- Av1an Options: change the splitting method, chunk creation method, number of workers, and more
- Encodes can be paused and resumed live, or stopped entirely and picked up again later from the
  finished chunks

#### Utilities

- Utilities are "shortcuts" for actions that normally require long (and/or multiple) CLI commands
- **Read Bitrates**: calculates stream size and average bitrate for each stream
- **Get Metrics**: calculate quality metrics like **VMAF**, SSIM, PSNR
- **Transfer Color Metadata**: copy colour properties and HDR metadata from one file to another
  (e.g. from a Bluray remux to an encode)
- **Deinterlace Video**: exports a deinterlaced copy - **QTGMC** over the whole file into a
  near-lossless MKV, with the audio and subtitles copied across. That file is all it produces; nothing
  is loaded back into the file list, and you do not need it in order to encode an interlaced source,
  since both encode tabs deinterlace on their way through. Has its own Deinterlace settings, under
  Configure on its card, separate from either tab's
- **Cut Video**: keep only a chosen section, copied out without re-encoding, with the start and end
  points picked while watching the frame you are on
- **Concatenate Into Single MKV**: merge any amount of any compatible video format into a single MKV
  (e.g. for chunked encoding)
- **Show Bitrate Chart**: samples the bitrate across the entire video and plots it, so you can see
  where bitrate is higher or lower
- **OCR Bitmap Subtitles**: converts selected bitmap-based subtitle tracks into text subtitles

## Compatibility

- Windows 10/11 64-bit is the primary target. Since the move from WinForms to Avalonia the app also
  builds and runs natively on Linux and macOS.
- Released archives are self-contained: no .NET install is required. Building framework-dependent
  yourself needs the [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0).
- `ffmpeg`, `ffprobe`, `mkvmerge` and `av1an` are looked up in the `bin` folder next to the
  executable first, then on `PATH`.

## What a release bundles

Portable builds are produced by `.github/workflows/release.yml`. Push a `v*` tag to publish a
release, or run the workflow manually to get a draft. `.github/scripts/bundle-tools.sh` stages the
external tools into `bin/`:

| | ffmpeg / ffprobe | MKVToolNix | av1an | VapourSynth | SVT-AV1 | aomenc, x264, x265 | vpxenc | VMAF models |
|---|---|---|---|---|---|---|---|---|
| win-x64 | bundled | bundled | bundled | bundled | bundled | bundled | bundled | bundled |
| linux-x64 | bundled | use package manager | bundled | use package manager | bundled | use package manager | use package manager | bundled |
| osx-x64 / osx-arm64 | `brew install ffmpeg` | `brew install mkvtoolnix` | `cargo install av1an` | `brew install vapoursynth` | build from source, see below | `brew install aom x264 x265` | `brew install libvpx` | bundled |

Tool downloads are best-effort: an unreachable upstream is reported and skipped rather than failing
the release, and the workflow's job summary lists exactly what each build shipped.

### SVT-AV1 comes from the PSY line

Nmkoder bundles the [svt-av1-hdr](https://github.com/juliobbv-p/svt-av1-hdr) build, which continues
the SVT-AV1-PSY line, and the AV1AN tab's content presets are written for the parameters only that
line has. Mainline SVT-AV1 - which is what Homebrew's `svt-av1` is - rejects those parameters
outright rather than ignoring them, so Nmkoder drops them and says so in the log. On macOS, build
svt-av1-hdr from source and put `SvtAv1EncApp` on your `PATH` to get the presets as intended. There
is deliberately no mainline fallback: a substituted binary under the same filename is worse than a
visible skip.

### VapourSynth, and what needs it

The portable VapourSynth build is Windows-only, and it is what QTGMC deinterlacing and the
VapourSynth chunk methods run on. On Linux and macOS, install VapourSynth through your package
manager; without it, QTGMC falls back to bwdif and says so.

Target SSIMULACRA2 scores its probes through the
[vszip](https://github.com/dnjulek/vapoursynth-zip) VapourSynth plugin (or the GPU-accelerated
[vship](https://github.com/Line-fr/Vship)), which has to be installed into VapourSynth's plugin
directory by hand on Linux and macOS - without it, that quality mode fails once av1an starts probing.
Target Butteraugli needs vship specifically, for the reason below. Target XPSNR needs no plugin:
av1an scores it with ffmpeg's `xpsnr` filter, present in the bundled ffmpeg and in any FFmpeg from
7.1 on.

**The caveat on Butteraugli:** every av1an release to date calls the CPU scoring plugin
([julek](https://github.com/dnjulek/vapoursynth-julek-plugin)) by the wrong function name
(`butteraugli` where the plugin registers `Butteraugli`), so that path fails at probe time no matter
what is installed. Until av1an fixes the invoke, Target Butteraugli works only through
[vship](https://github.com/Line-fr/Vship). The Windows bundle therefore ships both Vship builds
parked in `vsynth/vship`, outside the autoload folder, and before a metric-targeted encode the app
runs Vship's own GPU check and stages the build this machine passes into `vs-plugins` - so
Butteraugli works out of the box on a capable NVIDIA or AMD GPU, and SSIMULACRA2 moves to GPU scoring
on those same machines, since av1an prefers Vship wherever it sees it. A machine no build passes on is
stopped up front with the working alternatives named. The staged copy carries nmkoder's own file name
(`nmkoder-vship_*.dll`), so a Vship you install into `vs-plugins` yourself - under upstream's names or
any other - is recognised as yours: the app withdraws its own copy and never touches your file. Where
there is no bundled plugin folder to manage (Linux/macOS), the app only warns.

### The staged layout

The AV1AN tab's toolchain is staged in the layout the app runs it from:

```
bin/av1an/av1an[.exe]        av1an itself
bin/av1an/vsynth/            VapourSynth + embedded Python (VSPipe)
bin/av1an/vsynth/vs-plugins/ BestSource, L-SMASH-Works and FFMS2, for the matching chunk methods,
                             vszip, which scores Target SSIMULACRA2 probes, julek, staged
                             for Butteraugli until av1an can call it (see the caveat above),
                             and mvtools, znedi3, EEDI3, fmtconv, RemoveGrain, MiscFilters,
                             TemporalSoften2 and FFT3DFilter, which are what QTGMC deinterlacing
                             is made of (FFT3DFilter only for its two denoising presets)
bin/av1an/vsynth/vship/      Vship's NVIDIA + AMD builds, parked; the app stages the one this
                             machine's GPU passes into vs-plugins, and unstages both when none does
bin/av1an/enc/               SvtAv1EncApp, aomenc, vpxenc, x264 and x265
```

`vsynth` and `enc` are prepended to av1an's `PATH`, so nothing needs installing system-wide.

DGDecNV is the one chunk method left uncovered - it needs a licensed DGDecNV install.

### Overriding the sources

Binaries are resolved from each project's releases at build time, except SvtAv1EncApp, aomenc, x264
and x265, which upstream does not publish for Windows and so come from MSYS2's mingw64 packages.
Override the sources with the `AV1AN_REPO`, `SVTAV1_REPOS`, `VAPOURSYNTH_REPO`, `LSMASH_REPO`,
`FFMS2_REPO`, `BESTSOURCE_REPO`, `VSZIP_REPO`, `VSZIP_TAG`, `VSJULEK_REPO`, `VSJULEK_TAG`,
`VSHIP_REPO`, `VSHIP_TAG`, `PYTHON_EMBED_VERSIONS`, `MSYS2_ENCODERS`, `MSYS2_ROOT`, `GH_RELEASE_SCAN`
and `MKVTOOLNIX_VERSION` environment variables.

### vpxenc has no upstream Windows build

No project publishes a prebuilt Windows vpxenc: the WebM project ships source only,
ShiftMediaProject builds the library rather than the CLI, and MSYS2's `libvpx` package leaves the
encoder out. Windows builds therefore take a community build of libvpx - shared in the AV1 community
Discord, and inspected before it was used here.

It is fetched from a **release asset of this repository** (`VPXENC_REPO`, defaulting to the repo being
built), pinned by SHA1 through `VPXENC_ASSET_SHA1` and looked for on a named tag through
`VPXENC_ASSET_TAG`, since an asset attached once slides out of the most recent handful of releases
after a while and would otherwise stop being found. Replacing the asset means updating that hash;
anything else is rejected rather than shipped.

The fallback is <https://jeremylee.sh/bins/vpx.7z>, the build the av1an ecosystem uses. That is one
person's server with no signed provenance and a certificate that has expired before now - downloads
are made with certificate verification on, so if it does not validate when a release is built, vpxenc
is reported as a failed download and skipped rather than fetched insecurely. `VPXENC_SHA1` pins that
route too, left unset by default because the build rolls forward and a fixed hash would reject every
future one.

Either way nothing is trusted just because it downloaded: the bundler verifies the staged file really
is a Windows executable, and `bin/THIRD-PARTY.txt` records which source it came from and that it is a
community build rather than an official one.

Point `VPXENC_URL` at a different build (a bare `.exe` or an archive containing one) to override the
fallback, or set it empty (`VPXENC_URL=`) to skip that route entirely. Without vpxenc the AV1AN tab's
VP9 entry has no encoder behind it; the Quick Convert tab's VP9 support goes through the bundled
ffmpeg and is unaffected either way.

### Data files

`bin/iso639.csv`, the language table that names audio and subtitle tracks, is not downloaded - it
lives in `Nmkoder/BinFiles` and every build copies it into `bin`. Regenerate it with
`.github/scripts/gen-iso639.py` when the ISO registers move. The same folder carries the AV1AN tab's
per-encoder argument lists (`BinFiles/av1an/encoderArgs`).

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

The project multi-targets `net10.0` everywhere plus `net10.0-windows10.0.19041.0` when the host is
Windows; that second target framework is the one carrying the Windows App SDK, used for notifications.

## Credits and licence

Nmkoder was written by [N00MKRAD](https://github.com/n00mkrad); this is a fork of
[n00mkrad/nmkoder](https://github.com/n00mkrad/nmkoder). Licensed under the GPL-3.0 - see
[LICENSE](LICENSE). The bundled third-party tools keep their own licences, recorded in
`bin/THIRD-PARTY.txt` in every release archive.
