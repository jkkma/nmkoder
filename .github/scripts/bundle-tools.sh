#!/usr/bin/env bash
# Downloads the external tools Nmkoder shells out to and stages them in the portable
# build's "bin" folder, which is where Paths.GetBinPath() looks first.
#
# The av1an toolchain goes into the layout AvProcess.RunAv1an() expects:
#
#   bin/av1an/av1an[.exe]          the av1an binary itself
#   bin/av1an/vsynth/              VapourSynth portable + embedded Python, supplies VSPipe
#   bin/av1an/vsynth/vs-plugins/   source plugins, autoloaded by VapourSynth
#   bin/av1an/enc/                 encoders: SvtAv1EncApp, aomenc, vpxenc, x265
#
# vsynth and enc are prepended to av1an's PATH by the app, so nothing needs installing.
#
# Every download is best-effort: a dead upstream URL prints a warning and is skipped
# rather than failing the release. Check the summary at the end to see what landed.
set -uo pipefail

RID="${1:?usage: bundle-tools.sh <rid> <bin-dir>}"
BIN="${2:?usage: bundle-tools.sh <rid> <bin-dir>}"

mkdir -p "$BIN"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

AV1AN_DIR="$BIN/av1an"
VSYNTH_DIR="$AV1AN_DIR/vsynth"
ENC_DIR="$AV1AN_DIR/enc"
EXE=""
[ "$RID" = "win-x64" ] && EXE=".exe"

NOTICE="$WORK/notice.txt"
: > "$NOTICE"

BUNDLED=()
SKIPPED=()
LAST_ASSET=""
VS_PLUGIN_DLL=""

note_ok()   { BUNDLED+=("$1"); echo "  [ok]   $1"; }
note_skip() { SKIPPED+=("$1"); echo "  [skip] $1 - $2"; }

# Record a licence/source block for something that actually got bundled.
note_licence() { printf '%s\n\n' "$1" >> "$NOTICE"; }

# Fetch a URL to a file. Returns non-zero (quietly) if the download fails.
fetch() {
  curl -fsSL --retry 3 --retry-delay 2 --connect-timeout 20 -o "$2" "$1"
}

# Print the download URLs of every asset of <repo>'s latest release whose filename
# matches <regex>, best match first. Resolving at build time avoids hardcoding version
# numbers that go stale. GITHUB_TOKEN, when set, lifts the anonymous API rate limit.
gh_latest_assets() {
  local repo="$1" pattern="$2"
  local auth=()
  [ -n "${GITHUB_TOKEN:-}" ] && auth=(-H "Authorization: Bearer $GITHUB_TOKEN")

  curl -fsSL --retry 3 --retry-delay 2 --connect-timeout 20 \
       -H 'Accept: application/vnd.github+json' ${auth[@]+"${auth[@]}"} \
       "https://api.github.com/repos/$repo/releases/latest" 2>/dev/null \
    | grep -o '"browser_download_url"[[:space:]]*:[[:space:]]*"[^"]*"' \
    | sed 's/.*"\(https[^"]*\)"/\1/' \
    | grep -Ev -- '\.(sha256|sha512|md5|sig|asc|pem|txt|json|pdb|deb|rpm)$' \
    | grep -Ei -- "$pattern"
}

# Unpack an archive by extension. Returns 2 when the file is not an archive at all,
# which is how a bare binary asset (some projects publish one) gets detected.
extract() {
  local file="$1" dest="$2"
  mkdir -p "$dest"
  case "$file" in
    *.zip)                          unzip -qo "$file" -d "$dest" ;;
    *.7z)                           command -v 7z >/dev/null 2>&1 || return 1
                                    7z x -o"$dest" "$file" >/dev/null ;;
    *.tar.gz|*.tgz|*.tar.xz|*.tar.bz2|*.tar.zst) tar -xf "$file" -C "$dest" ;;
    *) return 2 ;;
  esac
}

# Copy an extracted tree into <dest>, stepping through the single versioned wrapper
# directory that most archives nest everything under.
flatten_into() {
  local src="$1" dest="$2" only
  while [ "$(find "$src" -mindepth 1 -maxdepth 1 | wc -l)" -eq 1 ]; do
    only="$(find "$src" -mindepth 1 -maxdepth 1)"
    [ -d "$only" ] || break
    src="$only"
  done
  mkdir -p "$dest"
  cp -R "$src"/. "$dest"/
}

# Copy <name>[.exe] out of an extracted tree into <dest>, along with any DLLs sitting
# next to it. Returns non-zero when the binary is not in the archive.
install_binary() {
  local tree="$1" name="$2" dest="$3" src
  src="$(find "$tree" -type f \( -iname "$name" -o -iname "$name.exe" \) -print -quit)"
  [ -n "$src" ] || return 1

  mkdir -p "$dest"
  cp "$src" "$dest/"
  chmod +x "$dest/$(basename "$src")" 2>/dev/null || true
  find "$(dirname "$src")" -maxdepth 1 -name '*.dll' -exec cp {} "$dest/" \; 2>/dev/null || true
}

# Download the assets of <repo> matching <primary-regex> (falling back to <fallback-regex>
# when nothing matches) and hand each to <handler> until one succeeds. The handler is
# called as `handler <downloaded-file> <extracted-dir>`, where an empty extracted dir means
# the asset was a bare binary rather than an archive. Sets LAST_ASSET on success.
try_assets() {
  local repo="$1" primary="$2" fallback="$3" handler="$4"
  local urls=() url file dir n=0

  mapfile -t urls < <(gh_latest_assets "$repo" "$primary")
  if [ "${#urls[@]}" -eq 0 ] && [ -n "$fallback" ]; then
    mapfile -t urls < <(gh_latest_assets "$repo" "$fallback")
  fi
  [ "${#urls[@]}" -gt 0 ] || return 1

  for url in "${urls[@]}"; do
    n=$((n + 1))
    file="$WORK/$n-$(basename "$url")"
    fetch "$url" "$file" || continue

    dir="$WORK/x$n"
    rm -rf "$dir"
    extract "$file" "$dir"
    case "$?" in
      0) "$handler" "$file" "$dir" || continue ;;
      2) "$handler" "$file" ""     || continue ;;
      *) continue ;;
    esac

    LAST_ASSET="$(basename "$url")"
    return 0
  done

  return 1
}

# ─────────────────────────── ffmpeg + ffprobe ───────────────────────────
# BtbN publishes reproducible GPL builds under a rolling "latest" tag for win64/linux64.
bundle_ffmpeg() {
  local url ext
  case "$RID" in
    win-x64)   url="https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";     ext="zip" ;;
    linux-x64) url="https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-linux64-gpl.tar.xz"; ext="txz" ;;
    *)         note_skip "ffmpeg" "no prebuilt bundle for $RID - install via 'brew install ffmpeg'"; return ;;
  esac

  if ! fetch "$url" "$WORK/ffmpeg.$ext"; then
    note_skip "ffmpeg" "download failed"
    return
  fi

  mkdir -p "$WORK/ff"
  if [ "$ext" = "zip" ]; then
    unzip -qo "$WORK/ffmpeg.zip" -d "$WORK/ff"
  else
    tar -xf "$WORK/ffmpeg.txz" -C "$WORK/ff"
  fi

  # Archives nest everything under a single versioned directory.
  local found=0
  for tool in ffmpeg ffprobe; do
    if install_binary "$WORK/ff" "$tool" "$BIN"; then found=$((found + 1)); fi
  done

  if [ "$found" -eq 2 ]; then
    note_ok "ffmpeg + ffprobe"
    note_licence "  ffmpeg / ffprobe   GPL-3.0-or-later (GPL build)
                     Source: https://ffmpeg.org/download.html
                     Build:  https://github.com/BtbN/FFmpeg-Builds"
  else
    note_skip "ffmpeg" "binaries not found in archive"
  fi
}

# ─────────────────────────── MKVToolNix ───────────────────────────
# Used by the concat utility and av1an's muxing step.
bundle_mkvtoolnix() {
  if [ "$RID" != "win-x64" ]; then
    note_skip "mkvtoolnix" "no portable bundle for $RID - install via package manager"
    return
  fi

  local ver="${MKVTOOLNIX_VERSION:-93.0}"
  local url="https://mkvtoolnix.download/windows/releases/${ver}/mkvtoolnix-64-bit-${ver}.7z"

  if ! fetch "$url" "$WORK/mkv.7z"; then
    note_skip "mkvtoolnix" "download failed (version ${ver})"
    return
  fi

  if ! command -v 7z >/dev/null 2>&1; then
    note_skip "mkvtoolnix" "7z not available on runner"
    return
  fi

  7z x -o"$WORK/mkv" "$WORK/mkv.7z" >/dev/null || { note_skip "mkvtoolnix" "extract failed"; return; }

  local found=0
  for tool in mkvmerge mkvextract mkvinfo; do
    if install_binary "$WORK/mkv" "$tool" "$BIN"; then found=$((found + 1)); fi
  done

  if [ "$found" -gt 0 ]; then
    note_ok "mkvtoolnix ($found tools)"
    note_licence "  mkvmerge / mkvextract / mkvinfo (MKVToolNix)
                     GPL-2.0-only
                     Source: https://mkvtoolnix.download/source.html"
  else
    note_skip "mkvtoolnix" "binaries not found"
  fi
}

# ─────────────────────────── av1an ───────────────────────────
# Drives chunked encoding for the AV1AN tab. Nmkoder runs it out of bin/av1an.
install_av1an() {
  local file="$1" dir="$2"

  if [ -n "$dir" ]; then
    install_binary "$dir" av1an "$AV1AN_DIR"
    return
  fi

  # A bare binary asset - only trust it if the name looks like av1an itself.
  case "$(basename "$file")" in
    *av1an*|*Av1an*|*AV1AN*) ;;
    *) return 1 ;;
  esac

  mkdir -p "$AV1AN_DIR"
  cp "$file" "$AV1AN_DIR/av1an$EXE" || return 1
  chmod +x "$AV1AN_DIR/av1an$EXE" 2>/dev/null || true
}

bundle_av1an() {
  local repo="${AV1AN_REPO:-master-of-zen/Av1an}" primary fallback

  case "$RID" in
    win-x64)   primary='(windows|win64|win-x64|msvc)'; fallback='\.(zip|7z|exe)$' ;;
    linux-x64) primary='(linux|musl|gnu)';             fallback='(\.tar\.(gz|xz)|\.zip|/av1an)$' ;;
    *)         note_skip "av1an" "no prebuilt binary for $RID - build with 'cargo install av1an'"; return ;;
  esac

  if try_assets "$repo" "$primary" "$fallback" install_av1an; then
    note_ok "av1an ($LAST_ASSET)"
    note_licence "  av1an              GPL-3.0-only
                     Source: https://github.com/master-of-zen/Av1an"
  else
    note_skip "av1an" "no usable release binary in $repo for $RID"
  fi
}

# ─────────────────────────── VapourSynth ───────────────────────────
# av1an's vapoursynth chunk methods call VSPipe. The portable archive is not standalone:
# it carries a wheel instead of a Python runtime, so vsynth is assembled from three parts -
# an embeddable CPython, the portable archive on top, then the wheel's module unpacked
# beside them. Skipped as a whole if any part is missing, since VSPipe without Python
# cannot evaluate a script - av1an then falls back to its non-vapoursynth chunk methods.
install_python_embed() {
  local dir="$2"
  [ -n "$dir" ] || return 1
  find "$dir" -maxdepth 2 -iname 'python3*.dll' -print -quit | grep -q . || return 1
  flatten_into "$dir" "$VSYNTH_DIR"
}

bundle_python_embed() {
  # The VapourSynth module is built against the stable ABI (Python 3.8+), so any recent
  # embeddable release works. Newest first; the first one that downloads wins.
  local ver url base="${PYTHON_EMBED_BASE:-https://www.python.org/ftp/python}"
  for ver in ${PYTHON_EMBED_VERSIONS:-3.13.7 3.13.5 3.12.10 3.12.8 3.11.9}; do
    url="${base}/${ver}/python-${ver}-embed-amd64.zip"
    if fetch "$url" "$WORK/python.zip"; then
      rm -rf "$WORK/py"
      if extract "$WORK/python.zip" "$WORK/py" && install_python_embed "" "$WORK/py"; then
        # The embeddable distribution disables site-packages; VapourSynth's module and any
        # plugin scripts live next to python.exe, so let the interpreter import from there.
        find "$VSYNTH_DIR" -maxdepth 1 -name 'python3*._pth' -exec \
          sed -i 's/^#\(import site\)/\1/' {} \; 2>/dev/null || true
        PYTHON_EMBED_VERSION="$ver"
        return 0
      fi
    fi
  done
  return 1
}

install_vapoursynth() {
  local dir="$2" stage="$WORK/vsstage"
  [ -n "$dir" ] || return 1

  rm -rf "$stage"
  flatten_into "$dir" "$stage"
  find "$stage" -maxdepth 1 -iname 'vspipe*' -print -quit | grep -q . || return 1

  cp -R "$stage"/. "$VSYNTH_DIR"/

  # Unpack the wheel next to python.exe: the .pyd is the "vapoursynth" module av1an's
  # scripts import, and vapoursynth.dll sits inside the wheel's data directory.
  #
  # The archive carries one wheel per interpreter, so pick the one matching the Python
  # staged above - falling back to the cp38 wheel, which is the stable-ABI build and
  # therefore imports on any Python 3.8 or newer.
  local tag whl unpacked="$WORK/whl"
  tag="$(find "$VSYNTH_DIR" -maxdepth 1 -iname 'python3*._pth' -print -quit \
         | sed -nE 's/.*python(3[0-9]+)\._pth$/cp\1/p')"

  whl=""
  [ -n "$tag" ] && whl="$(find "$VSYNTH_DIR" -type f -iname "VapourSynth*-${tag}-*.whl" -print -quit)"
  [ -n "$whl" ]  || whl="$(find "$VSYNTH_DIR" -type f -iname 'VapourSynth*-cp38-*.whl' -print -quit)"
  [ -n "$whl" ]  || whl="$(find "$VSYNTH_DIR" -type f -iname 'VapourSynth*.whl' -print -quit)"
  [ -n "$whl" ]  || return 1

  rm -rf "$unpacked"
  unzip -qo "$whl" -d "$unpacked" || return 1
  find "$unpacked" -type f \( -iname '*.pyd' -o -iname '*.dll' \) -exec cp {} "$VSYNTH_DIR/" \; || return 1
  find "$VSYNTH_DIR" -maxdepth 1 -iname 'vapoursynth*.pyd' -print -quit | grep -q . || return 1

  # LGPL wants the licence text shipped alongside the binaries, not just referenced.
  find "$unpacked" -type f \( -iname 'COPYING*' -o -iname 'LICENSE*' \) -exec \
    cp {} "$VSYNTH_DIR/VapourSynth-LICENSE.txt" \; 2>/dev/null || true

  # doc, sdk and the remaining wheels are dead weight in a release archive.
  rm -rf "$VSYNTH_DIR/doc" "$VSYNTH_DIR/sdk" "$VSYNTH_DIR/wheel"
}

bundle_vapoursynth() {
  if [ "$RID" != "win-x64" ]; then
    note_skip "vapoursynth" "portable build is Windows-only - install VapourSynth via your package manager on $RID"
    return
  fi

  mkdir -p "$VSYNTH_DIR"

  if ! bundle_python_embed; then
    note_skip "vapoursynth" "embeddable Python download failed - VSPipe needs it to run scripts"
    rm -rf "$VSYNTH_DIR"
    return
  fi

  if try_assets "${VAPOURSYNTH_REPO:-vapoursynth/vapoursynth}" 'portable.*\.(zip|7z)$' '' install_vapoursynth; then
    note_ok "vapoursynth ($LAST_ASSET + Python $PYTHON_EMBED_VERSION)"
    note_licence "  VapourSynth        LGPL-2.1-or-later
                     Source: https://github.com/vapoursynth/vapoursynth

  CPython            Python Software Foundation License 2.0 (embeddable distribution)
                     Source: https://www.python.org/downloads/"
    bundle_vs_source_plugins
  else
    note_skip "vapoursynth" "no usable portable asset in the latest release"
    rm -rf "$VSYNTH_DIR"
  fi
}

# VapourSynth on its own cannot open a video file - it needs a source plugin, one per
# entry in Nmkoder's chunk method dropdown (Av1an.ChunkMethod). LSMASH is the default and
# FFMS2 the alternative; without either, av1an falls back to non-vapoursynth chunking.
install_vs_plugin() {
  local dir="$2" plugins="$VSYNTH_DIR/vs-plugins" got=0 dll matches wide

  [ -n "$dir" ] || return 1

  matches="$(find "$dir" -type f -iname "$VS_PLUGIN_DLL")"
  [ -n "$matches" ] || return 1

  # Archives carrying both architectures separate them into x86 and x64 directories.
  wide="$(printf '%s\n' "$matches" | grep -Ei '(x64|win64|64bit|amd64)')"
  [ -n "$wide" ] && matches="$wide"

  mkdir -p "$plugins"
  while IFS= read -r dll; do
    [ -n "$dll" ] && cp "$dll" "$plugins/" && got=$((got + 1))
  done <<< "$matches"

  [ "$got" -gt 0 ]
}

bundle_vs_source_plugins() {
  # L-SMASH-Works - what the chunk method dropdown defaults to (Config.av1anOptsChunkMode).
  VS_PLUGIN_DLL='*vslsmashsource*.dll'
  if try_assets "${LSMASH_REPO:-AkarinVS/L-SMASH-Works}" '(release-x86_64|win).*\.(zip|7z)$' '\.(zip|7z)$' install_vs_plugin; then
    note_ok "vapoursynth plugin: L-SMASH-Works ($LAST_ASSET)"
    note_licence "  L-SMASH-Works      GPL-2.0-or-later (VapourSynth source plugin)
                     Source: https://github.com/AkarinVS/L-SMASH-Works"
  else
    note_skip "vapoursynth plugin: L-SMASH-Works" "no asset with a vslsmashsource DLL"
  fi

  # FFMS2 - the other source plugin the dropdown offers.
  VS_PLUGIN_DLL='ffms2.dll'
  if try_assets "${FFMS2_REPO:-FFMS/ffms2}" 'msvc.*\.(7z|zip)$' '\.(7z|zip)$' install_vs_plugin; then
    note_ok "vapoursynth plugin: FFMS2 ($LAST_ASSET)"
    note_licence "  FFMS2              MIT, but the published Windows build statically links
                     a GPL FFmpeg, making the binary GPL-3.0-or-later as distributed
                     Source: https://github.com/FFMS/ffms2"
  else
    note_skip "vapoursynth plugin: FFMS2" "no asset with an ffms2 DLL"
  fi
}

# ─────────────────────────── SVT-AV1 ───────────────────────────
# SvtAv1EncApp is what av1an invokes for "-e svt-av1" (see VideoEncodersBin.SvtAv1).
# Upstream does not always attach binaries to a release, so try the mirrors in order.
install_svtav1() {
  local file="$1" dir="$2"

  if [ -n "$dir" ]; then
    install_binary "$dir" SvtAv1EncApp "$ENC_DIR"
    return
  fi

  case "$(basename "$file")" in
    *SvtAv1EncApp*|*svtav1*|*svt-av1*) ;;
    *) return 1 ;;
  esac

  mkdir -p "$ENC_DIR"
  cp "$file" "$ENC_DIR/SvtAv1EncApp$EXE" || return 1
  chmod +x "$ENC_DIR/SvtAv1EncApp$EXE" 2>/dev/null || true
}

bundle_svtav1() {
  local primary fallback repo
  case "$RID" in
    win-x64)   primary='(windows|win64|win-x64|msvc)'; fallback='\.(zip|7z|exe)$' ;;
    linux-x64) primary='(linux|musl|gnu)';             fallback='(\.tar\.(gz|xz)|\.zip)$' ;;
    *)         note_skip "svt-av1" "no prebuilt binary for $RID - install via 'brew install svt-av1'"; return ;;
  esac

  for repo in ${SVTAV1_REPOS:-AOMediaCodec/SVT-AV1 psy-ex/svt-av1-psy}; do
    if try_assets "$repo" "$primary" "$fallback" install_svtav1; then
      note_ok "svt-av1 ($LAST_ASSET from $repo)"
      note_licence "  SvtAv1EncApp       BSD-3-Clause-Clear + AOM Patent License 1.0
                     Source: https://gitlab.com/AOMediaCodec/SVT-AV1
                     Build:  https://github.com/$repo"
      return
    fi
  done

  note_skip "svt-av1" "no release binary in: ${SVTAV1_REPOS:-AOMediaCodec/SVT-AV1 psy-ex/svt-av1-psy}"
}

# ─────────────────────── aomenc + vpxenc + x265 ───────────────────────
# av1an calls these for "-e aom", "-e vpx" and "-e x265" (VideoEncodersBin.AomAv1, .Vpx
# and .X265). None of the three ship Windows binaries of their own, so they come from
# MSYS2's mingw64 packages - the Windows runner image already carries pacman.

# <binary>:<package> pairs. The binary name is the one av1an looks for on PATH.
MSYS2_ENCODERS="${MSYS2_ENCODERS:-aomenc:mingw-w64-x86_64-aom vpxenc:mingw-w64-x86_64-libvpx x265:mingw-w64-x86_64-x265}"

# List the DLLs an mingw64 executable pulls in from its own prefix. Falls back to the
# handful of runtime libraries those encoders are known to link when ldd is unavailable.
msys_dependencies() {
  local msys_bash="$1" tool="$2" deps

  deps="$("$msys_bash" -lc "ldd /mingw64/bin/$tool.exe" 2>/dev/null \
          | awk '{print $3}' | grep -i '/mingw64/bin/' | sed 's|.*/||')"

  if [ -z "$deps" ]; then
    deps="libaom.dll libvpx.dll libx265.dll libgcc_s_seh-1.dll libwinpthread-1.dll libstdc++-6.dll libssp-0.dll"
  fi

  printf '%s\n' $deps
}

encoder_licence() {
  case "$1" in
    aomenc) note_licence "  aomenc             BSD-2-Clause + AOM Patent License 1.0 (libaom)
                     Source: https://aomedia.googlesource.com/aom/
                     Build:  https://packages.msys2.org/package/mingw-w64-x86_64-aom" ;;
    vpxenc) note_licence "  vpxenc             BSD-3-Clause (libvpx)
                     Source: https://chromium.googlesource.com/webm/libvpx/
                     Build:  https://packages.msys2.org/package/mingw-w64-x86_64-libvpx" ;;
    x265)   note_licence "  x265               GPL-2.0-or-later
                     Source: https://bitbucket.org/multicoreware/x265_git
                     Build:  https://packages.msys2.org/package/mingw-w64-x86_64-x265" ;;
  esac
}

bundle_msys2_encoders() {
  local names="aomenc + vpxenc + x265"

  if [ "$RID" != "win-x64" ]; then
    note_skip "$names" "no portable build for $RID - install the aom, vpx and x265 tools from your package manager"
    return
  fi

  local root="${MSYS2_ROOT:-/c/msys64}"
  local msys_bash="$root/usr/bin/bash.exe" pacman="$root/usr/bin/pacman.exe"

  if [ ! -x "$pacman" ] || [ ! -x "$msys_bash" ]; then
    note_skip "$names" "MSYS2 not found at $root"
    return
  fi

  local entry packages=()
  for entry in $MSYS2_ENCODERS; do packages+=("${entry#*:}"); done

  # -Sy refreshes the image's package database, which is usually months stale.
  if ! "$pacman" -Sy --noconfirm --needed "${packages[@]}" >/dev/null 2>&1; then
    note_skip "$names" "pacman could not install: ${packages[*]}"
    return
  fi

  mkdir -p "$ENC_DIR"

  local tool exe dll got=()
  for entry in $MSYS2_ENCODERS; do
    tool="${entry%%:*}"
    exe="$root/mingw64/bin/$tool.exe"

    if [ ! -f "$exe" ]; then
      note_skip "$tool" "not shipped by ${entry#*:}"
      continue
    fi

    cp "$exe" "$ENC_DIR/" || continue
    got+=("$tool")

    while IFS= read -r dll; do
      [ -n "$dll" ] && [ -f "$root/mingw64/bin/$dll" ] && cp "$root/mingw64/bin/$dll" "$ENC_DIR/"
    done < <(msys_dependencies "$msys_bash" "$tool")

    encoder_licence "$tool"
  done

  if [ "${#got[@]}" -gt 0 ]; then
    note_ok "${got[*]} (MSYS2 mingw64)"
  else
    note_skip "$names" "packages installed but no encoder binaries in $root/mingw64/bin"
  fi
}

# ─────────────────────────── VMAF models ───────────────────────────
# Paths.GetVmafPath() expects these next to the binaries for the metrics utility.
bundle_vmaf_models() {
  local base="https://raw.githubusercontent.com/Netflix/vmaf/master/model"
  local got=0
  for model in vmaf_v0.6.1 vmaf_v0.6.1neg vmaf_4k_v0.6.1; do
    if fetch "$base/$model.json" "$BIN/$model.json"; then
      got=$((got + 1))
    else
      rm -f "$BIN/$model.json"
    fi
  done

  if [ "$got" -gt 0 ]; then
    note_ok "vmaf models ($got)"
    note_licence "  VMAF models        BSD-2-Clause-Patent
                     Source: https://github.com/Netflix/vmaf"
  else
    note_skip "vmaf models" "download failed"
  fi
}

# ─────────────────────────── Licence notice ───────────────────────────
# ffmpeg, MKVToolNix and av1an ship as GPL binaries; GPL requires pointing users at their
# sources. Only what actually landed gets listed.
write_notice() {
  {
    cat <<'EOF'
Nmkoder invokes the following programs as separate processes. They are bundled here
for convenience and remain under their own licences.

EOF
    cat "$NOTICE"
    cat <<'EOF'
Nmkoder itself is GPL-3.0. See LICENSE in the repository root.
EOF
  } > "$BIN/THIRD-PARTY.txt"
  note_ok "THIRD-PARTY.txt"
}

echo "Bundling external tools for $RID into $BIN"
bundle_ffmpeg
bundle_mkvtoolnix
bundle_av1an
bundle_vapoursynth
bundle_svtav1
bundle_msys2_encoders
bundle_vmaf_models
# A tool that skipped may have left its (now empty) destination folder behind.
find "$BIN" -mindepth 1 -type d -empty -delete 2>/dev/null || true
write_notice

echo
echo "Bundled: ${#BUNDLED[@]} | Skipped: ${#SKIPPED[@]}"
[ "${#SKIPPED[@]}" -gt 0 ] && printf 'Not bundled: %s\n' "${SKIPPED[*]}"

# Never fail the release over an unavailable third-party download.
exit 0
