---
name: real-binaries
description: Get the real tool binaries Nmkoder ships and measures against - SvtAv1EncApp (PSY line), av1an, grav1synth, x264, x265, aomenc, vpxenc, mkvmerge - into a web session, and know which fetch routes the sandbox's egress proxy allows. Use whenever a session needs to measure, probe --help, run strings on, or encode with a real encoder or tool binary, or a needed tool is missing from PATH, or a download from GitHub/api.github.com is failing with 403s.
---

# Getting the real binaries into a session

The project's rule is to measure against the binaries users get, not whatever a
distribution packages. What a web session has, and how to get what it lacks:

| Tool | Shipped from | In a session |
|---|---|---|
| ffmpeg, ffprobe | BtbN `master-latest` | installed by `.claude/setup.sh` (same build) |
| SvtAv1EncApp | juliobbv-p/svt-av1-hdr release | extracted from the app's own published release by setup.sh |
| av1an | rust-av/Av1an release | **win-x64 zip only** - pull `av1an.exe` with the script below and `strings` it |
| grav1synth | built by bundle-tools.sh | extracted from the published release by setup.sh |
| x264, x265, aomenc, vpxenc, mkvmerge/mkvextract/mkvinfo | (win bundles / user PATH) | apt, installed by setup.sh |

If the environment snapshot predates those setup.sh steps, run the fetch blocks below inline.

## What the egress proxy allows (measured, not guessed)

- `https://github.com/<any repo>/releases/download/...` **passes**, redirects included -
  this is how the BtbN ffmpeg arrives. *Absolute* range requests (`bytes=a-b`) pass too;
  a *suffix* range (`bytes=-N`) is answered with **501 Unsupported client range**, so
  "grab the last N bytes" has to be spelled as size-probe-then-absolute-range.
- `https://api.github.com/repos/jkkma/nmkoder/...` **passes** (the repo is attached to the
  session). For **any other repo** the API answers a proxy error telling you to use
  `add_repo` - so release-asset *discovery* for svt-av1-hdr, Av1an, BtbN etc. is closed
  unless the repo is attached.
- `https://github.com/<repo>/releases/latest` and other **HTML pages are 403**. Do not
  burn retries on them.

The consequence: the reliable source for the shipped SvtAv1EncApp/av1an/grav1synth is **the
app's own latest release tarball** - discoverable via the attached repo's API, downloadable
via the always-open `releases/download` path, and it carries the exact binaries users run.

## Fetch block: shipped tools out of the published release

```bash
VER=$(curl -fsSL https://api.github.com/repos/jkkma/nmkoder/releases/latest | jq -r '.tag_name | ltrimstr("v")')
# Fallback if the API is unreachable: the csproj's <Version> is usually the released one.
[ -n "$VER" ] || VER=$(grep -oP '(?<=<Version>)[^<]+' Nmkoder/Nmkoder.csproj)
curl -fsSL -o /tmp/nmk.tar.gz "https://github.com/jkkma/nmkoder/releases/download/v${VER}/Nmkoder-${VER}-linux-x64.tar.gz"
tar -xzf /tmp/nmk.tar.gz -C /tmp Nmkoder/bin/av1an/enc/SvtAv1EncApp Nmkoder/bin/grav1synth
install -m 0755 /tmp/Nmkoder/bin/av1an/enc/SvtAv1EncApp /tmp/Nmkoder/bin/grav1synth /usr/local/bin/
```

(~180 MB download; grav1synth is in linux-x64 releases from 2.8.33 on, SvtAv1EncApp
wherever svt-av1-hdr had published an asset - `tar -tzf` first if a member errors. The
linux tarball carries **no av1an** - measured on 2.8.60, `bin/av1an/` holds only `enc/` -
because rust-av publishes no linux release binary for the bundler to take.)

## Fetch block: one file out of the win-x64 zip, without the 485 MB

`scripts/fetch-zip-member.py` reads a remote zip's central directory with ranged requests
and fetches only the member asked for - which is how the *shipped* av1an becomes readable
from a session:

```bash
url="https://github.com/jkkma/nmkoder/releases/download/v${VER}/Nmkoder-${VER}-win-x64.zip"
python3 .claude/skills/real-binaries/scripts/fetch-zip-member.py "$url"                      # list entries
python3 .claude/skills/real-binaries/scripts/fetch-zip-member.py "$url" Nmkoder/bin/av1an/av1an.exe av1an.exe
strings av1an.exe | grep -F 'finished chunk'      # help text and format strings, no Windows needed
```

Measured on the 2.8.60 asset: the listing and extraction work through the proxy, and the
shipped av1an's strings answer the standing questions - its libvmaf templates are
`libvmaf=log_path=...` with the model passed as `model='...'`, i.e. **no `model_path`**, so
`--vmaf-path` does not overwrite the bundled model file the way the app's own old bug did;
and `finished chunk`, `logs/av1an.log`, `sc-downscale-height` and `ignore-frame-mismatch`
are all present, matching what CLAUDE.md's progress and scene-detection sections rely on.
Re-run against the current release rather than trusting those readings forever - the
bundler tracks av1an's rolling prerelease.

## Fetch block: the apt tools

```bash
apt-get update -qq && apt-get install -y x264 x265 aom-tools vpx-tools mkvtoolnix
```

These are for driving the *CLI* encoders the way Quick Convert does. The distro versions
are close to the MSYS2 ones the win bundle carries, but they are still not the shipped
binaries - say which binary a measurement used.

## Caveats worth knowing before measuring

- **`grav1synth --version` says 0.2.0 and that is not the crates.io 0.2.0** - the fork
  never bumped the Cargo version, so the pinned-commit build the bundler compiles carries
  the same banner as the crates.io release CLAUDE.md documents as a different, broken
  program. Judge by provenance (it came out of the release tarball) and by `--help`: the
  real build has `diff`, `--preset` and `--iso`, which the crates.io one lacks. Verified on
  the 2.8.60 extraction.
- **Confirm which SVT-AV1 line a binary is** before trusting a measurement:
  `SvtAv1EncApp --help | grep -c 'fgs-table'` - the PSY line has it, mainline does not, and
  CLAUDE.md's grain and preset sections hinge on the difference. The `libsvtav1` inside the
  BtbN ffmpeg is *mainline*; the standalone binary is the fork. Both facts are load-bearing.
- **Not obtainable here**: the VapourSynth R72 toolchain and its plugins (bundled for
  Windows only - QTGMC/vszip/Butteraugli computation is proven by the release workflow's
  win-x64 job, not in a session), any real GPU (the libplacebo probe rightly refuses
  lavapipe; NVENC paths are asserted by command shape only), and mkvtoolnix's *bundled*
  Windows build.
- For a tool fetched by hand, the app resolves tools against `bin/` first
  (`OsUtils.SetPathVar`) - putting a binary on the session PATH serves harnesses and direct
  measurement, not the launched app's lookup. `AvProcess.IsToolAvailable` is the app-side
  question.
