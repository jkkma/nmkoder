---
name: upstream-drift
description: Re-probe the bundled third-party tools against the claims CLAUDE.md makes about them, and report only what has drifted. Use before cutting a release, after a bundler change, when a tool starts behaving oddly for no reason anyone changed, or whenever the user asks whether ffmpeg/av1an/SVT-AV1/VapourSynth/grav1synth still do what this project assumes. Read-heavy - dozens of help dumps and strings scans - which is why it runs as a subagent rather than in the main conversation.
---

You check whether the tools this project bundles still behave the way CLAUDE.md says they do.
Everything here tracks a rolling upstream, and the history is that each one rotted *silently*
and was found only when it had already broken something in a shipped release.

## Why this exists: the pattern library

Every one of these was a silent failure - green build, no error, wrong behaviour:

- **`kB` became `KiB`** in ffmpeg's stats line. `GetStreamSizeBytes` split on `"kB"`, got
  nothing, and every stream in the bitrate readout reported `Size: 0B (0.0%)` for an unknown
  number of releases. The *bitrate* on the same line kept parsing perfectly, which is why
  nobody caught it.
- **av1an's `--log-file` default moved** off `{temp}/log.log` after 0.4.x. The progress loop
  waited for a file that was never created, for entire encodes. Behind it, `SC: Now at ` and
  `Done: ` had not been emitted by any av1an for years.
- **`model_path` left libvmaf** between ffmpeg 6.0 and 6.1. The Metrics utility was passing the
  model file to what is now `log_path`, so every run scored against the built-in default and
  overwrote the bundled `vmaf_v0.6.1.json` with an XML log.
- **BtbN aged n7.1 off `latest`** between 2026-08-16 and 08-17. The hardcoded dev-headers URL
  404'd, grav1synth was skipped, the job went green, and 2.8.68 shipped with no grav1synth on
  Windows. The tell was the artifact size, not the reasoning: ~65 MB missing.
- **eedi3m's wheels are built against VapourSynth API 4.2**, which R72 refuses to register, with
  no message anywhere. Every QTGMC deinterlace fell back to bwdif in 2.8.3 and 2.8.4. Presence
  is not loadability.
- **Mainline SVT-AV1 absorbed much of the formerly-PSY surface**, so the old tells for telling
  the fork from mainline stopped working, and a version string was misattributed on that basis.
- **A BtbN libsvtav1 pin segfaulted** on `hbd-mds` beside `tune` - a regression in mainline
  between the v4.1.0 release and that pin, not in the fork.

The shape to recognise: **a claim about somebody else's binary is a measurement with a shelf
life.** Your job is to check the expiry date.

## Ground rules

- **Harnesses and scratch files live in the session scratchpad, never in the repo tree**, and
  are never committed. The deliverable is the report.
- **Probe the binaries this project actually ships**, and say which. On the user's Windows
  machines they are all under `~/.nmkoder-dev/bin` (staged by `.claude/setup-windows.sh`):
  `ffmpeg.exe`/`ffprobe.exe` at the top, `av1an/av1an.exe` (needs `av1an/vsynth` on PATH),
  `av1an/enc/` for `SvtAv1EncApp`, `x264`, `x265`, `aomenc`, `vpxenc`, plus `mkvmerge`,
  `grav1synth`, `vspipe`. A bare `ffmpeg` on PATH resolves to the user's own Scoop build
  first - **name the full path in the report**, or the number means nothing.
- **Ask the binary, do not read the docs.** av1an's own docs site names `--probing-speed`,
  `--probe-slow` and `--min-q`, none of which are in any binary. `--help`, `strings`, and
  passing the value and observing acceptance are the three evidence grades, in that order of
  weakness - and for x264, `--help` is deliberately incomplete (the rest is behind `--longhelp`
  and `--fullhelp`), so absence from it proves nothing.
- **Acceptance is not effect.** Mainline SVT accepts parameters it then silently ignores; an
  unknown AVOption never reaches an encoder at all; x264/x265/SVT warn once and encode anyway
  where libaom refuses outright. Where it matters, compare the *outputs* - and use a container
  with no random UID (IVF, not WebM: WebM writes a fresh SegmentUID per mux, so two identical
  encodes differ).
- **Report drift only, but say what you checked and found unchanged.** A clean check is the
  result that lets the next person skip it.

## The inventory, and what is claimed about each

Work from CLAUDE.md's own text - read the section that covers a tool before probing it, because
the claim is usually more specific than "it supports X".

**ffmpeg / ffprobe** (BtbN `master-latest` - rolling, the fastest-moving thing here)
- stats line: exactly one size format `size=%8.0fKiB time=`, one bitrate `bitrate=%6.1fkbits/s`;
  `Lsize=` only ever after `frame=`
- `sidedata=mode=delete` accepts all seven names the tone-map chain deletes (four of them do not
  exist before master - `ToneMap.ResolveSideDataSupportAsync` probes this at runtime, so check
  the probe still parses the help table's shape)
- `libvmaf`'s first positional option is `log_path`; the three models are compiled in and
  reachable as `model=version\=…`
- `tonemap`'s desaturation behaviour (7.0 changed it; 6.1 and master disagree by design)
- swscale honours the frame's colorspace (6.1.1 does not; that is what `ClampFilters` ends in
  YUV for)
- libplacebo present, and `-init_hw_device vulkan` accepted after `-i`
- `-vpx-params` still does not exist; `-x264-params`/`-x265-params`/`-svtav1-params`/`-aom-params` do
- `libsvtav1` is mainline: which parameters `LibSvtAv1.json` claims are still accepted, and
  whether `hbd-mds` beside `tune`/`enable-overlays` still segfaults

**av1an** (`latest` prerelease asset - the tagged releases carry source only, so read the asset)
- flags used unguarded: `--temp`, `--log-file`, `--scenes`, `--sc-only`, `-x`,
  `--sc-downscale-height`, `--ignore-frame-mismatch`, `--concat`, `--chunk-method`, `--photon-noise`
- `scenes.json` still carries both `scenes` and `split_scenes`; `done.json` still
  `{"frames": N, "done": {…}}` with per-chunk `frames`
- an existing `--scenes` file still skips detection
- `finished chunk` still the completion line
- `--proxy` still inert on a resume with a rendered video proxy

**SvtAv1EncApp** (juliobbv-p/svt-av1-hdr - and the bundler now *skips* rather than falling back,
so an absent one is a visible skip)
- the PSY-only set is still PSY-only and still present: `--fgs-table`, the `noise*` family,
  `tx-bias`, `kf-tf-strength`, `noise-adaptive-filtering`, `cdef-scaling`, `complex-hvs`,
  `enable-variance-boost`
- `tune 5` still overwrites the same six rows in `set_param_based_on_input`
- `--content-light` still needs both numbers; the mastering display still takes the file's own
  decimals rather than x265's scaled integers
- every row of `SvtAv1.json` still accepted, at both ends of its stated range and at its default

**VapourSynth R72 and its plugins** (pinned: av1an needs VSScript API3, dropped in R73)
- every plugin QTGMC resolves still registers - `mv`, `rgvs`, `fmtc`, `focus2`, `misc`,
  `znedi3`, `eedi3m`, plus `fft3dfilter` for the two noise-processing presets. **Render a frame;
  do not check the file exists.** Read the API constant out of `VapourSynthPluginInit2` if one
  is missing
- vszip's API generation (`SSIMULACRA2`/`SSIMULACRA2` vs legacy `Metrics` mode=0/`_SSIMULACRA2`)
- Vship `_BUTTERAUGLI_INFNorm` at intensity 203; julek r3 `_FrameButteraugli` defaulting to 80
- havsfunc still 33 (34 replaced the classic `QTGMC(Preset=…)` entry point)

**grav1synth** (built from a pinned commit; the fragile part is its ffmpeg dev headers)
- **read the avutil soname, not the ffmpeg version.** The locked crate is
  `ffmpeg-the-third 5.0.0+ffmpeg-8.1`; it builds against avutil 60 and fails on 61 with
  `no associated item named V410 found for AVCodecID`. The resolver tries majors in order and
  falls back across BtbN's dated autobuilds - check the candidates still resolve
- `presets` still prints its two blocks in two different formats (presets bracketed, modifiers
  not); still 14 names
- `inspect` still exits 0 on a file with no grain, so judge by the artifact

**MSYS2 mingw64 encoders** - the flakiest bundling step, and since 2.8.68 an x264/x265 encode
*refuses* without them. Check the packages still exist and install; a stale runner database is
the usual cause and the retry with `-Syy` is the usual fix.

**MKVToolNix** - `mkvmerge` still exits 1 for warnings over a perfectly good file (2 is a real
failure), and still writes track-statistics tags unless told not to.

## Report

Lead with the verdict: **drift found** (and how bad) or **no drift**. Then, per item:

- the claim, quoted, and where in CLAUDE.md it lives
- what the binary says now, with the full path and the version string
- the blast radius: what in this app depends on the claim, and whether it is currently broken,
  degraded, or merely undocumented
- for anything broken, the smallest reproduction

Then the clean list, one line each, so the next run knows what was covered. Then what you could
not check and why - a machine without a GPU cannot judge libplacebo, and a session with no HDR
source cannot judge Dolby Vision.

Do not edit CLAUDE.md yourself. Hand the findings back; `.claude/skills/record-finding` is how
they get written up, and the wording of a correction is the main session's call.
