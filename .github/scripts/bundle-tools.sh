#!/usr/bin/env bash
# Downloads the external tools Nmkoder shells out to and stages them in the portable
# build's "bin" folder, which is where Paths.GetBinPath() looks first.
#
# The av1an toolchain goes into the layout AvProcess.RunAv1an() expects:
#
#   bin/av1an/av1an[.exe]          the av1an binary itself
#   bin/av1an/vsynth/              VapourSynth portable + embedded Python, supplies VSPipe
#   bin/av1an/vsynth/vs-plugins/   source plugins, autoloaded by VapourSynth
#   bin/av1an/enc/                 encoders: SvtAv1EncApp, aomenc, vpxenc, x265, x264
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

# Print the download URLs of every asset in <repo>'s recent releases whose filename matches
# <regex>, newest release first. Resolving at build time avoids hardcoding version numbers
# that go stale; scanning several releases rather than just the newest matters because
# projects tag source-only releases in between binary ones. GITHUB_TOKEN, when set, lifts
# the anonymous API rate limit.
gh_api_asset_urls() {
  local repo="$1" endpoint="$2"
  local auth=()
  [ -n "${GITHUB_TOKEN:-}" ] && auth=(-H "Authorization: Bearer $GITHUB_TOKEN")

  curl -fsSL --retry 3 --retry-delay 2 --connect-timeout 20 \
       -H 'Accept: application/vnd.github+json' ${auth[@]+"${auth[@]}"} \
       "https://api.github.com/repos/$repo/$endpoint" 2>/dev/null \
    | grep -o '"browser_download_url"[[:space:]]*:[[:space:]]*"[^"]*"' \
    | sed 's/.*"\(https[^"]*\)"/\1/'
}

# Names that must not be considered for the RID being built, even when they match the
# pattern. A project publishing "Windows_arm64_..." alongside "Windows_x86-64_..." matches
# any sane "windows" pattern with both, and the wrong one staged into an x86-64 build fails
# at runtime with nothing pointing at the cause. Set before try_assets, cleared after.
ASSET_EXCLUDE=""

filter_excluded() {
  if [ -n "${ASSET_EXCLUDE:-}" ]; then
    grep -Evi -- "$ASSET_EXCLUDE"
  else
    cat
  fi
}

# Names to try first among those that match. try_assets takes the first asset that installs,
# so where a project publishes several builds for one platform this decides which, instead
# of leaving it to whatever order the API happens to return them in.
ASSET_PREFER=""

prefer_assets() {
  local all
  all="$(cat)"
  [ -n "$all" ] || return 0

  if [ -n "${ASSET_PREFER:-}" ]; then
    printf '%s\n' "$all" | grep -Ei  -- "$ASSET_PREFER" || true
    printf '%s\n' "$all" | grep -Evi -- "$ASSET_PREFER" || true
  else
    printf '%s\n' "$all"
  fi
}

# Restricts the search to one release instead of the recent ones, for a dependency that is
# pinned to a specific version rather than tracking whatever is newest. Set before
# try_assets, cleared after.
ASSET_RELEASE_TAG=""

gh_release_assets() {
  local repo="$1" pattern="$2" tag="${3:-}"

  # A caller may name the release its asset lives on. That release is looked at first and the
  # usual search still follows, so a pin costs nothing if the asset also turns up elsewhere.
  # Without one, an asset attached to a release once slides out of view as newer releases
  # accumulate past the scan below - which is exactly how vpxenc went missing from 2.0.11.
  #
  # The stable release first, then the recent ones. That order matters: "releases/latest"
  # skips prereleases, while the scan includes them along with rolling nightly tags like
  # av1an's, so a stable binary is preferred and a nightly is still found if it is all
  # that exists.
  {
    [ -n "$tag" ] && gh_api_asset_urls "$repo" "releases/tags/$tag"

    if [ -n "${ASSET_RELEASE_TAG:-}" ]; then
      gh_api_asset_urls "$repo" "releases/tags/$ASSET_RELEASE_TAG"
    else
      gh_api_asset_urls "$repo" "releases/latest"
      gh_api_asset_urls "$repo" "releases?per_page=${GH_RELEASE_SCAN:-8}"
    fi
  } \
    | grep -Ev -- '\.(sha256|sha512|md5|sig|asc|pem|txt|json|pdb|deb|rpm)$' \
    | grep -Ei -- "$pattern" \
    | filter_excluded \
    | prefer_assets
}

# VapourSynth's portable zip stores paths with backslash separators. unzip does not treat
# those as directories, so it writes single files literally named "dir\file" and every
# lookup below misses them. Rebuild the tree they were meant to describe.
normalize_backslash_paths() {
  local root="$1" file rel target
  while IFS= read -r file; do
    rel="${file#"$root"/}"
    case "$rel" in *\\*) ;; *) continue ;; esac
    target="$root/${rel//\\//}"
    mkdir -p "$(dirname "$target")"
    mv "$file" "$target" 2>/dev/null || true
  done < <(find "$root" -depth -name '*\\*')
}

# Unpack an archive by extension. Returns 2 when the file is not an archive at all,
# which is how a bare binary asset (some projects publish one) gets detected.
extract() {
  local file="$1" dest="$2" status=0
  mkdir -p "$dest"
  case "$file" in
    *.zip)
      unzip -qo "$file" -d "$dest"
      status=$?
      # unzip exits 1 for warnings, not failures, and the files are written regardless.
      # VapourSynth's archive trips exactly that with its backslash-separator note, so
      # treating any non-zero as failure silently discards a perfectly good download.
      [ "$status" -le 1 ] && status=0
      ;;
    *.7z)
      command -v 7z >/dev/null 2>&1 || return 1
      7z x -o"$dest" "$file" >/dev/null
      status=$?
      ;;
    *.tar.gz|*.tgz|*.tar.xz|*.tar.bz2|*.tar.zst)
      tar -xf "$file" -C "$dest"
      status=$?

      # GNU tar shells out to xz/zstd to decompress, and the tar on a Windows runner's PATH
      # may have neither beside it - which would silently cost us every project publishing
      # .tar.xz. 7z decompresses to stdout and tar takes the plain archive from there.
      if [ "$status" -ne 0 ] && command -v 7z >/dev/null 2>&1; then
        if 7z x -so "$file" 2>/dev/null | tar -xf - -C "$dest" 2>/dev/null; then
          status=0
        fi
      fi
      ;;
    *) return 2 ;;
  esac

  normalize_backslash_paths "$dest"
  return "$status"
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
  local repo="$1" primary="$2" fallback="$3" handler="$4" tag="${5:-}"
  local urls=() url file dir n=0

  mapfile -t urls < <(gh_release_assets "$repo" "$primary" "$tag")
  if [ "${#urls[@]}" -eq 0 ] && [ -n "$fallback" ]; then
    mapfile -t urls < <(gh_release_assets "$repo" "$fallback" "$tag")
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
  local repo="${AV1AN_REPO:-rust-av/Av1an}" primary fallback

  case "$RID" in
    win-x64)   primary='(av1an\.exe$|windows|win64|win-x64|msvc)'; fallback='\.(zip|7z|exe)$' ;;
    linux-x64) primary='(linux|musl|gnu)';                          fallback='(\.tar\.(gz|xz)|\.zip|/av1an)$' ;;
    *)         note_skip "av1an" "no prebuilt binary for $RID - build with 'cargo install av1an'"; return ;;
  esac

  if try_assets "$repo" "$primary" "$fallback" install_av1an; then
    note_ok "av1an ($LAST_ASSET)"
    note_licence "  av1an              GPL-3.0-only
                     Source: https://github.com/rust-av/Av1an"
  else
    note_skip "av1an" "no usable release binary in $repo for $RID"
  fi
}

# ─────────────────────────── VapourSynth ───────────────────────────
# Pinned rather than tracking the newest release, because av1an and VapourSynth disagree
# about VSScript. av1an builds against the vapoursynth crate, which speaks VSScript API3;
# VapourSynth dropped the API3 entry points from vsscript.dll in R73. Against anything from
# R73 on, av1an panics the moment a vapoursynth chunk method is used:
#
#   panicked at vapoursynth-0.5.6/src/vsscript/environment.rs: VSScript API not available
#
# which is what shipped in 2.0.0 through 2.0.4. Checked by reading the exported symbols out
# of each release's vsscript DLL: R72 and earlier export the 17 vsscript_* functions
# alongside getVSScriptAPI, R73 through R78 export only the latter. R72 is therefore the
# newest usable one, and it carries a cp312-abi3 wheel, which imports on the newer
# embeddable Python staged beside it.
#
# Lift this when av1an ships a build using a VSScript4-capable crate - check by confirming
# a vapoursynth chunk method works against a newer VapourSynth before changing the tag.
VAPOURSYNTH_TAG="${VAPOURSYNTH_TAG:-R72}"

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
  cp -R "$stage"/. "$VSYNTH_DIR"/

  # The portable archive comes in two generations. Up to R77 it carried VSPipe.exe and the
  # runtime DLLs at its root, with a wheel alongside holding just the Python module. From
  # R78 it carries no binaries at all - only doc, launcher .bat files and a wheel that now
  # contains everything, vspipe.exe included, which vspipe.bat expects to find installed
  # under lib\site-packages. Unpacking the wheel therefore has to serve both.
  #
  # Wheels are per interpreter, so prefer the one matching the staged Python, then an
  # abi3/cp38 build, both of which import on any Python new enough to run them.
  local tag whl unpacked="$WORK/whl"
  tag="$(find "$VSYNTH_DIR" -maxdepth 1 -iname 'python3*._pth' -print -quit \
         | sed -nE 's/.*python(3[0-9]+)\._pth$/cp\1/p')"

  whl=""
  [ -n "$tag" ] && whl="$(find "$VSYNTH_DIR" -type f -iname "*apoursynth*-${tag}-*.whl" -print -quit)"
  [ -n "$whl" ]  || whl="$(find "$VSYNTH_DIR" -type f -iname '*apoursynth*abi3*.whl' -print -quit)"
  [ -n "$whl" ]  || whl="$(find "$VSYNTH_DIR" -type f -iname '*apoursynth*cp38*.whl' -print -quit)"
  [ -n "$whl" ]  || whl="$(find "$VSYNTH_DIR" -type f -iname '*apoursynth*.whl' -print -quit)"
  [ -n "$whl" ]  || return 1

  rm -rf "$unpacked"
  unzip -qo "$whl" -d "$unpacked" || return 1

  # A wheel is a zip of the tree pip would install, so unpacking it into site-packages is
  # the install: it satisfies both "import vapoursynth" and the launcher .bat files.
  local site="$VSYNTH_DIR/lib/site-packages"
  mkdir -p "$site"
  cp -R "$unpacked"/. "$site"/

  # Mirror the binaries to the root as well. That folder is the only one the app puts on
  # av1an's PATH, and av1an resolves "vspipe" as an executable rather than through the
  # .bat shim - the DLLs have to travel with it or it will not load.
  #
  # Deep enough to reach a wheel's data directory. R72 keeps the core in one, at
  # vapoursynth-72.data/data/Lib/site-packages/vapoursynth.dll - five levels down, where a
  # shallower search leaves the core buried and VSScript unable to load it, while R78 keeps
  # its binaries at the top of the wheel.
  find "$site" -maxdepth 6 -type f \( -iname '*.exe' -o -iname '*.dll' -o -iname '*.pyd' \) \
    -exec cp {} "$VSYNTH_DIR/" \; 2>/dev/null || true

  # The embeddable interpreter ignores site-packages unless its path file names it.
  local pth
  pth="$(find "$VSYNTH_DIR" -maxdepth 1 -iname 'python3*._pth' -print -quit)"
  if [ -n "$pth" ] && ! grep -qi 'site-packages' "$pth"; then
    printf 'lib\\site-packages\n' >> "$pth"
  fi

  # Whichever generation it was, VSPipe has to be sitting on that PATH now.
  find "$VSYNTH_DIR" -maxdepth 1 -iname 'vspipe.exe' -print -quit | grep -q . || return 1

  # LGPL wants the licence text shipped alongside the binaries, not just referenced.
  find "$unpacked" -type f \( -iname 'COPYING*' -o -iname 'LICENSE*' \) -exec \
    cp {} "$VSYNTH_DIR/VapourSynth-LICENSE.txt" \; 2>/dev/null || true

  # doc, sdk, debug symbols and the remaining wheels are dead weight in a release archive.
  rm -rf "$VSYNTH_DIR/doc" "$VSYNTH_DIR/sdk" "$VSYNTH_DIR/wheel"
  find "$VSYNTH_DIR" -name '*.pdb' -delete 2>/dev/null || true
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

  ASSET_RELEASE_TAG="$VAPOURSYNTH_TAG"

  if try_assets "${VAPOURSYNTH_REPO:-vapoursynth/vapoursynth}" 'portable.*\.(zip|7z)$' '' install_vapoursynth; then
    ASSET_RELEASE_TAG=""
    note_ok "vapoursynth ($LAST_ASSET + Python $PYTHON_EMBED_VERSION)"
    note_licence "  VapourSynth        LGPL-2.1-or-later
                     Source: https://github.com/vapoursynth/vapoursynth

  CPython            Python Software Foundation License 2.0 (embeddable distribution)
                     Source: https://www.python.org/downloads/"
    bundle_vs_source_plugins
    bundle_vs_metric_plugins
    bundle_qtgmc
  else
    ASSET_RELEASE_TAG=""
    note_skip "vapoursynth" "no portable asset in $VAPOURSYNTH_TAG"
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

  [ "$got" -gt 0 ] || return 1

  # Companion runtime libraries - BestSource ships FFmpeg's - go next to VSPipe rather
  # than into vs-plugins: Windows resolves a plugin's own dependencies through the
  # loading process's directory and PATH, both of which point at the vsynth root.
  while IFS= read -r dll; do
    [ -n "$dll" ] || continue
    find "$(dirname "$dll")" -maxdepth 1 -type f -iname '*.dll' ! -iname "$VS_PLUGIN_DLL" \
      -exec cp {} "$VSYNTH_DIR/" \; 2>/dev/null || true
  done <<< "$matches"
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

  # BestSource - the dropdown's first entry, and the slowest but most accurate of the three.
  VS_PLUGIN_DLL='*bestsource*.dll'
  if try_assets "${BESTSOURCE_REPO:-vapoursynth/bestsource}" '(win|x64|msvc).*\.(zip|7z)$' '\.(zip|7z)$' install_vs_plugin; then
    note_ok "vapoursynth plugin: BestSource ($LAST_ASSET)"
    note_licence "  BestSource         MIT, linking FFmpeg's libav* libraries
                     (LGPL-2.1-or-later, bundled beside it)
                     Source: https://github.com/vapoursynth/bestsource"
  else
    note_skip "vapoursynth plugin: BestSource" "no asset with a bestsource DLL"
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

# Pinned rather than tracking the newest release, because releases after R13 publish no
# prebuilt Windows binary - only Zig source - so "latest" would quietly bundle nothing.
# When bumping, confirm a release actually carries a windows-x86_64 asset, that its DLL
# still loads under the VSPipe of $VAPOURSYNTH_TAG, and that av1an still takes its API
# (the bundled av1an speaks both the pre-R7 and R7+ vszip interfaces).
VSZIP_TAG="${VSZIP_TAG:-R13}"

# The julek plugin supplies the butteraugli scoring behind Target Butteraugli, reached as
# com.julek.plugin. r3 is its newest release; the "vapoursynht" spelling in the asset
# names is upstream's own, so the match keys on the win64 suffix instead.
VSJULEK_TAG="${VSJULEK_TAG:-r3}"

# These score the AV1AN tab's VapourSynth-scored quality modes. av1an reaches these
# metrics through VapourSynth, not through ffmpeg, so without the plugin a mode fails at
# probe time: vszip (com.julek.vszip) scores SSIMULACRA2, julek scores butteraugli.
# Target XPSNR needs neither - av1an scores it with the bundled ffmpeg's xpsnr filter.
# julek is staged for the day av1an can actually use it: every av1an release to date
# invokes it as "butteraugli" where the plugin registers "Butteraugli", a case mismatch
# VapourSynth does not forgive, so the app stops Target Butteraugli when it can see that
# Vship is absent, and warns where it cannot look (see the guard in Av1an.Run).
# Vship, the GPU plugin av1an accepts for both plugin-scored metrics (and the only thing
# its butteraugli path calls correctly), ships parked - see bundle_vs_vship below.
bundle_vs_metric_plugins() {
  VS_PLUGIN_DLL='vszip.dll'
  ASSET_RELEASE_TAG="$VSZIP_TAG"

  if try_assets "${VSZIP_REPO:-dnjulek/vapoursynth-zip}" '(windows|win).*x86_64.*\.(zip|7z)$' '' install_vs_plugin; then
    ASSET_RELEASE_TAG=""
    note_ok "vapoursynth plugin: vszip ($LAST_ASSET)"
    note_licence "  vapoursynth-zip    MIT (VapourSynth metric plugin, scores SSIMULACRA2)
                     Source: https://github.com/dnjulek/vapoursynth-zip"
  else
    ASSET_RELEASE_TAG=""
    note_skip "vapoursynth plugin: vszip" "no windows-x86_64 asset in $VSZIP_TAG - Target SSIMULACRA2 needs it"
  fi

  VS_PLUGIN_DLL='julek.dll'
  ASSET_RELEASE_TAG="$VSJULEK_TAG"

  if try_assets "${VSJULEK_REPO:-dnjulek/vapoursynth-julek-plugin}" 'win64.*\.(zip|7z)$' '' install_vs_plugin; then
    ASSET_RELEASE_TAG=""
    note_ok "vapoursynth plugin: julek ($LAST_ASSET)"
    note_licence "  julek-plugin       MIT (VapourSynth metric plugin, scores butteraugli)
                     Source: https://github.com/dnjulek/vapoursynth-julek-plugin"
  else
    ASSET_RELEASE_TAG=""
    note_skip "vapoursynth plugin: julek" "no win64 asset in $VSJULEK_TAG - staged for when av1an can call it"
  fi

  bundle_vs_vship
}

# Vship supplies the GPU scoring behind Target Butteraugli - the only backend av1an's
# butteraugli path calls by its right name - and av1an prefers it for SSIMULACRA2 too
# whenever it is present. Upstream archived its GitHub repository in February 2026 with
# binaries frozen at v4.0.2 ("files will remain there for a little more time") and moved
# to Codeberg, which refuses automated downloads - so this pin must never chase "latest",
# and the DLLs are looked for on this repository's own releases first, where they can be
# mirrored (MIT allows it) before upstream's copies disappear.
VSHIP_TAG="${VSHIP_TAG:-v4.0.2}"
VSHIP_REPO="${VSHIP_REPO:-Line-fr/Vship}"

# Unsigned binaries with no provenance, pinned to the builds that were actually inspected
# (like vpxenc): PE imports checked - the NVIDIA build takes only KERNEL32, the AMD build
# KERNEL32 plus the driver's amdhip64_6.dll. Replacing the DLLs means updating these.
VSHIP_NVIDIA_SHA1="${VSHIP_NVIDIA_SHA1-9285f8601e188e111d97c3b8bfef9d3ba9bb28f1}"
VSHIP_AMD_SHA1="${VSHIP_AMD_SHA1-c1a18dff560a62cd10d80c0afb6ee47114a40127}"

# The release of this repository the mirrored DLLs are attached to, once someone attaches
# them. Named outright for the same reason vpxenc's tag is: an asset attached to one
# release slides out of the recent-release scan as newer releases accumulate. Empty scans
# the recent releases.
VSHIP_ASSET_TAG="${VSHIP_ASSET_TAG-}"

# Set per call by bundle_vs_vship for the handler below.
VSHIP_TARGET_NAME=""
VSHIP_EXPECTED_SHA1=""

# try_assets handler: Vship publishes bare DLLs, not archives, so only the no-archive
# path is accepted, and nothing is staged that is not byte-for-byte the pinned build.
install_vship_dll() {
  local file="$1" dir="$2"
  [ -z "$dir" ] || return 1
  is_windows_exe "$file" || return 1 # DLLs carry the same MZ magic

  if [ -n "$VSHIP_EXPECTED_SHA1" ]; then
    local got want
    got="$(sha1sum "$file" | cut -d' ' -f1 | tr 'A-F' 'a-f')"
    want="$(printf '%s' "$VSHIP_EXPECTED_SHA1" | tr 'A-F' 'a-f')"

    if [ "$got" != "$want" ]; then
      echo "  [warn] vship: $VSHIP_TARGET_NAME SHA1 $got does not match the pinned $want"
      return 1
    fi
  fi

  mkdir -p "$VSYNTH_DIR/vship"
  cp "$file" "$VSYNTH_DIR/vship/$VSHIP_TARGET_NAME"
}

# Parked in vsynth/vship, deliberately OUTSIDE the autoloaded vs-plugins folder: presence
# is all av1an checks, and the NVIDIA build loads on machines with no NVIDIA GPU at all
# (its import table holds nothing beyond KERNEL32), so autoloading it blindly would hand
# scoring to a plugin that then fails every probe. The app stages the right build into
# vs-plugins at runtime, after Vship's own GpuInfo kernel check has passed on the machine
# (VshipStager.Reconcile), and removes it again when the machine stops passing.
bundle_vs_vship() {
  local vendor sha got=0
  for vendor in NVIDIA AMD; do
    VSHIP_TARGET_NAME="libvship_${vendor}.dll"
    sha="$VSHIP_AMD_SHA1"
    [ "$vendor" = "NVIDIA" ] && sha="$VSHIP_NVIDIA_SHA1"
    VSHIP_EXPECTED_SHA1="$sha"

    # This repository's releases first (the durable mirror), then the archived upstream.
    if { [ -n "${GITHUB_REPOSITORY:-}" ] && try_assets "$GITHUB_REPOSITORY" "libvship_${vendor}\.dll$" '' install_vship_dll "$VSHIP_ASSET_TAG"; }; then
      note_ok "vapoursynth plugin: vship $vendor ($LAST_ASSET from $GITHUB_REPOSITORY, parked)"
      got=$((got + 1))
    elif ASSET_RELEASE_TAG="$VSHIP_TAG" && try_assets "$VSHIP_REPO" "libvship_${vendor}\.dll$" '' install_vship_dll; then
      ASSET_RELEASE_TAG=""
      note_ok "vapoursynth plugin: vship $vendor ($LAST_ASSET, parked)"
      got=$((got + 1))
    else
      ASSET_RELEASE_TAG=""
      note_skip "vapoursynth plugin: vship $vendor" "no pinned libvship_${vendor}.dll in ${GITHUB_REPOSITORY:-<unset>} or $VSHIP_REPO $VSHIP_TAG"
    fi
  done

  VSHIP_TARGET_NAME=""
  VSHIP_EXPECTED_SHA1=""

  if [ "$got" -gt 0 ]; then
    # Shipping this text is MIT's one distribution requirement, so it is embedded rather
    # than fetched - the upstream repository is archived and will not serve it forever.
    cat > "$VSYNTH_DIR/vship/Vship-LICENSE.txt" <<'EOF'
MIT License

Copyright (c) 2024 Line

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
EOF
    note_licence "  Vship              MIT (GPU VapourSynth metric plugin; parked in vsynth/vship,
                     staged into vs-plugins per machine once its GPU check passes)
                     Source: https://github.com/Line-fr/Vship"
  fi
}

# ─────────────────────────── QTGMC ───────────────────────────
# QTGMC is the deinterlacer the Quick Convert tab reaches for on an interlaced source - a tape
# or DVD capture, which is what this whole group exists for. It is a Python function rather than
# a plugin, so what has to land here is havsfunc plus every VapourSynth plugin it touches, on top
# of the VapourSynth staged above.
#
# All of it is pinned, and to one version of havsfunc in particular. havsfunc 33 is the last
# release carrying the classic QTGMC(Preset=...) function - 34 replaced it with vs-jetpack's
# builder API and a dependency tree many times this size - so 33 is what Nmkoder's generated
# script is written against, and its plugins are pinned alongside it rather than tracking
# releases that were never tested with it. The set below is not guesswork: it is what havsfunc 33
# actually resolves on the default path, established by running the graph. eedi3m is in it even
# though the default EdiMode is NNEDI3, because QTGMC_Interpolate builds an eedi3 partial before
# it looks at EdiMode, and znedi3 specifically (not nnedi3) because that is the name it calls.
#
# None of it is load-bearing for the rest of the build: Nmkoder.Media.Qtgmc builds a QTGMC graph
# and renders a frame before it uses any of this, once per session, so a piece that failed to
# download shows up as a fallback to bwdif naming what is missing, not as a broken encode.
#
# The other thing every one of these has to satisfy is the VapourSynth *API* version, and that
# is not the same question as whether the download worked. A plugin passes the API it was built
# against to configPlugin, and a core older than that refuses to register it - silently, because
# autoload reports nothing. VapourSynth is pinned to R72 here (see VAPOURSYNTH_TAG, and av1an's
# VSScript API3 requirement behind it), which speaks API 4.0 and 4.1 and rejects 4.2. So a
# version is only pinnable here if its binary is 4.1 or lower, whatever its packaging metadata
# claims: every vapoursynth-eedi3 wheel declares "VapourSynth>=74" and so does vapoursynth-fmtconv,
# yet fmtconv's DLL is API 4.0 and loads fine while eedi3's is 4.2 and never has. Read it out of
# the binary rather than the metadata - the constant sits in the first bytes of the plugin's
# VapourSynthPluginInit2 - and let the release workflow's QTGMC check be the backstop.
HAVSFUNC_VERSION="${HAVSFUNC_VERSION:-33}"
VSUTIL_VERSION="${VSUTIL_VERSION:-0.8.0}"
MVSFUNC_TAG="${MVSFUNC_TAG:-r10}"
MVTOOLS_VERSION="${MVTOOLS_VERSION:-29}"
ZNEDI3_VERSION="${ZNEDI3_VERSION:-3.3}"
FMTCONV_VERSION="${FMTCONV_VERSION:-31}"
REMOVEGRAIN_TAG="${REMOVEGRAIN_TAG:-R1}"
MISCFILTERS_TAG="${MISCFILTERS_TAG:-R2}"
TEMPORALSOFTEN2_TAG="${TEMPORALSOFTEN2_TAG:-v1}"

# eedi3m is the one plugin here that cannot come from PyPI, and the reason is the paragraph
# above rather than anything about the plugin. Every wheel upstream has ever published -
# 9.0, 9.1 and 10.0 alike - is built against API 4.2, so R72 rejects all three: that is what
# shipped in 2.8.3 and 2.8.4, where the missing namespace sent every QTGMC deinterlace to
# bwdif. Downgrading the wheel does not help, because there is no wheel that predates the
# switch. r8 is the last Windows binary upstream attached to a GitHub release, it is API 4.0,
# and it registers the same eedi3m/EEDI3 that havsfunc 33 asks for.
#
# It is a frozen tag with a published hash, so the hash is pinned: this asset is not going to
# roll forward, and a binary arriving under that name with different contents should fail the
# build rather than ship. Clear EEDI3_SHA256 to skip the check when deliberately pointing
# EEDI3_TAG at something else.
#
# The one thing r8 does not carry is EEDI3CL, the OpenCL variant. Nothing asks for it today -
# havsfunc only reaches for it when QTGMC is called with opencl=True, which Nmkoder does not
# do - but a future GPU option would need a source for it that this one is not.
EEDI3_TAG="${EEDI3_TAG:-r8}"
EEDI3_SHA256="${EEDI3_SHA256-fa8515e0aa711ca979a87d812860c8582c7789fd805df2be10760748c0a9c486}"

# The denoiser QTGMC's noise processing runs on, which havsfunc 33 enables for Placebo and Very
# Slow and no other preset - so this was missing from every build up to 2.8.6 without anything
# noticing, because the checks all rendered at a fast preset. R2 is upstream's "first API4
# release" and reads as API 4.0, so R72 takes it; the repository has published nothing since
# 2021, so the hash is pinned as eedi3's is.
FFT3DFILTER_TAG="${FFT3DFILTER_TAG:-R2}"
FFT3DFILTER_SHA256="${FFT3DFILTER_SHA256-ebc2c2d8a437c8ecae656778221882d6fe2b5b7723404dd57b1e2b092962eb09}"

# The download URL of one file from a PyPI release. Pinned to a version and matched on the whole
# file name, because the unversioned /json endpoint lists every past release's files too - so a
# looser match happily returns a five-year-old build of the same package.
pypi_file_url() {
  curl -fsSL --retry 3 --retry-delay 2 --connect-timeout 20 "https://pypi.org/pypi/$1/$2/json" 2>/dev/null \
    | tr '{' '\n' \
    | grep -F "\"filename\":\"$3\"" \
    | grep -oE 'https://files\.pythonhosted\.org/[^"]+' \
    | head -1
}

# Fetch and unpack one PyPI file into <dest>. A wheel is a zip, so nothing beyond unzip is needed.
fetch_wheel() {
  local pkg="$1" ver="$2" file="$3" dest="$4" url status
  url="$(pypi_file_url "$pkg" "$ver" "$file")"
  [ -n "$url" ] || return 1
  fetch "$url" "$WORK/$file" || return 1
  rm -rf "$dest"
  mkdir -p "$dest"
  unzip -qo "$WORK/$file" -d "$dest"
  status=$?
  [ "$status" -le 1 ] || return 1 # unzip exits 1 for warnings, having written the files anyway
}

# A VapourSynth plugin published as a wheel. These carry vapoursynth/plugins/<name>.dll, and
# znedi3's carries its neural network weights beside it - a file the plugin looks for in its own
# directory, so it has to travel into vs-plugins rather than beside VSPipe.
install_wheel_plugin() {
  local pkg="$1" ver="$2" file="$3" stage="$WORK/whlplug" plugins="$VSYNTH_DIR/vs-plugins" got=0 f

  fetch_wheel "$pkg" "$ver" "$file" "$stage" || return 1
  mkdir -p "$plugins"

  while IFS= read -r f; do
    [ -n "$f" ] && cp "$f" "$plugins/" && got=$((got + 1))
  done < <(find "$stage" -type f \( -iname '*.dll' -o -iname '*.bin' \) 2>/dev/null)

  [ "$got" -gt 0 ]
}

# A pure-Python script package, unpacked where the embedded interpreter imports from. The
# .dist-info folders are dropped: nothing here uses pip, so they are only weight.
install_wheel_module() {
  local pkg="$1" ver="$2" file="$3" stage="$WORK/whlmod" site="$VSYNTH_DIR/lib/site-packages"

  fetch_wheel "$pkg" "$ver" "$file" "$stage" || return 1
  rm -rf "$stage"/*.dist-info
  mkdir -p "$site"
  cp -R "$stage"/. "$site"/
}

# install_vs_plugin with the hash checked first, for the plugins pinned to a frozen tag. Worth
# the extra step on those and not on the ones beside them: a package that tracks a version which
# moves would have every future build rejected by a fixed hash, while a tag upstream will not
# touch again either arrives byte-for-byte or has been swapped. Set VS_PLUGIN_SHA256 before
# try_assets, clear it after.
VS_PLUGIN_SHA256=""

install_vs_plugin_pinned() {
  local file="$1" dir="$2" got

  if [ -n "${VS_PLUGIN_SHA256:-}" ]; then
    got="$(sha256sum "$file" 2>/dev/null | cut -d' ' -f1)"
    if [ "$got" != "$VS_PLUGIN_SHA256" ]; then
      echo "  [warn] $(basename "$file") hashes to ${got:-<unreadable>}, expected $VS_PLUGIN_SHA256"
      return 1
    fi
  fi

  install_vs_plugin "$file" "$dir"
}

bundle_qtgmc() {
  local got=0 missing=()

  # Plugins from PyPI, which is where these three publish their Windows builds - mvtools' own
  # GitHub releases carry source archives only.
  if install_wheel_plugin vapoursynth-mvtools "$MVTOOLS_VERSION" "vapoursynth_mvtools-${MVTOOLS_VERSION}-py3-none-win_amd64.whl"; then
    got=$((got + 1)); note_ok "vapoursynth plugin: mvtools $MVTOOLS_VERSION (QTGMC motion search)"
    note_licence "  mvtools            GPL-2.0-or-later (VapourSynth motion plugin, used by QTGMC)
                     Source: https://github.com/dubhater/vapoursynth-mvtools"
  else
    missing+=("mvtools")
  fi

  if install_wheel_plugin vapoursynth-znedi3 "$ZNEDI3_VERSION" "vapoursynth_znedi3-${ZNEDI3_VERSION}-py3-none-win_amd64.whl"; then
    got=$((got + 1)); note_ok "vapoursynth plugin: znedi3 $ZNEDI3_VERSION (QTGMC field interpolation)"
    note_licence "  znedi3             GPL-2.0-or-later (VapourSynth NNEDI3 implementation, used by QTGMC)
                     Source: https://github.com/sekrit-twc/znedi3"
  else
    missing+=("znedi3")
  fi

  # From a release asset rather than a wheel, and pinned to a hash - see EEDI3_TAG above for
  # why this one plugin is sourced differently from the three around it.
  VS_PLUGIN_DLL='EEDI3m.dll'
  ASSET_RELEASE_TAG="$EEDI3_TAG"
  VS_PLUGIN_SHA256="$EEDI3_SHA256"
  if try_assets "${EEDI3_REPO:-HolyWu/VapourSynth-EEDI3}" 'EEDI3-.*\.7z$' '' install_vs_plugin_pinned; then
    got=$((got + 1)); note_ok "vapoursynth plugin: eedi3m $EEDI3_TAG (referenced by QTGMC even on the NNEDI3 path)"
    note_licence "  EEDI3              GPL-3.0-or-later (VapourSynth edge interpolation, referenced by QTGMC)
                     Source: https://github.com/HolyWu/VapourSynth-EEDI3"
  else
    missing+=("eedi3m")
  fi
  ASSET_RELEASE_TAG=""
  VS_PLUGIN_SHA256=""

  # QTGMC's denoiser, and unlike everything else here it is not needed by every preset - havsfunc
  # turns noise processing on for Placebo and Very Slow only. It is still bundled unconditionally,
  # because which preset a user picks is not something a build can know; what is conditional is the
  # runtime check, which asks about the preset that is actually going to run (Media/Qtgmc.cs).
  #
  # Also the only plugin here that brings a companion library - libfftw3f-3.dll, beside it in the
  # archive. install_vs_plugin stages that next to VSPipe rather than into vs-plugins, which is
  # where Windows looks for a plugin's own dependencies. Its MSVCP140/VCRUNTIME140 imports are
  # satisfied by the portable VapourSynth zip, which ships those at its root.
  VS_PLUGIN_DLL='fft3dfilter.dll'
  ASSET_RELEASE_TAG="$FFT3DFILTER_TAG"
  VS_PLUGIN_SHA256="$FFT3DFILTER_SHA256"
  if try_assets "${FFT3DFILTER_REPO:-myrsloik/VapourSynth-FFT3DFilter}" 'FFT3DFilter-.*\.7z$' '' install_vs_plugin_pinned; then
    got=$((got + 1)); note_ok "vapoursynth plugin: fft3dfilter $FFT3DFILTER_TAG (QTGMC noise processing, Placebo and Very Slow)"
    note_licence "  FFT3DFilter        GPL-2.0-or-later (VapourSynth frequency-domain denoiser, used by QTGMC)
                     Source: https://github.com/myrsloik/VapourSynth-FFT3DFilter

  FFTW                GPL-2.0-or-later (libfftw3f, the FFT library FFT3DFilter is built on)
                     Source: https://www.fftw.org/"
  else
    missing+=("fft3dfilter")
  fi
  ASSET_RELEASE_TAG=""
  VS_PLUGIN_SHA256=""

  if install_wheel_plugin vapoursynth-fmtconv "$FMTCONV_VERSION" "vapoursynth_fmtconv-${FMTCONV_VERSION}-py3-none-win_amd64.whl"; then
    got=$((got + 1)); note_ok "vapoursynth plugin: fmtconv $FMTCONV_VERSION (QTGMC's bob)"
    note_licence "  fmtconv            WTFPL (VapourSynth format conversion, used by QTGMC)
                     Source: https://github.com/EleonoreMizo/fmtconv"
  else
    missing+=("fmtconv")
  fi

  # And three that only exist as GitHub release archives. All are frozen upstream - the first two
  # are archived repositories with a single release each - so the tags above will not move.
  VS_PLUGIN_DLL='RemoveGrainVS.dll'
  ASSET_RELEASE_TAG="$REMOVEGRAIN_TAG"
  if try_assets "${REMOVEGRAIN_REPO:-vapoursynth/vs-removegrain}" 'removegrain.*\.(7z|zip)$' '\.(7z|zip)$' install_vs_plugin; then
    got=$((got + 1)); note_ok "vapoursynth plugin: RemoveGrain ($LAST_ASSET)"
    note_licence "  RemoveGrainVS      GPL-2.0-or-later (VapourSynth rgvs plugin, used by QTGMC)
                     Source: https://github.com/vapoursynth/vs-removegrain"
  else
    missing+=("rgvs")
  fi
  ASSET_RELEASE_TAG=""

  VS_PLUGIN_DLL='MiscFilters.dll'
  ASSET_RELEASE_TAG="$MISCFILTERS_TAG"
  if try_assets "${MISCFILTERS_REPO:-vapoursynth/vs-miscfilters-obsolete}" 'miscfilters.*\.(7z|zip)$' '\.(7z|zip)$' install_vs_plugin; then
    got=$((got + 1)); note_ok "vapoursynth plugin: MiscFilters ($LAST_ASSET)"
    note_licence "  MiscFilters        GPL-2.0-or-later (VapourSynth misc plugin; TemporalSoften2 needs it)
                     Source: https://github.com/vapoursynth/vs-miscfilters-obsolete"
  else
    missing+=("misc")
  fi

  VS_PLUGIN_DLL='libtemporalsoften2.dll'
  ASSET_RELEASE_TAG="$TEMPORALSOFTEN2_TAG"
  if try_assets "${TEMPORALSOFTEN2_REPO:-dubhater/vapoursynth-temporalsoften2}" 'win64.*\.(7z|zip)$' '' install_vs_plugin; then
    got=$((got + 1)); note_ok "vapoursynth plugin: TemporalSoften2 ($LAST_ASSET)"
    note_licence "  TemporalSoften2    GPL-2.0-or-later (VapourSynth focus2 plugin, used by QTGMC)
                     Source: https://github.com/dubhater/vapoursynth-temporalsoften2"
  else
    missing+=("focus2")
  fi
  ASSET_RELEASE_TAG=""

  # The scripts themselves. havsfunc imports mvsfunc at module level even though QTGMC never
  # calls it, so it has to be there or nothing imports at all; mvsfunc has no PyPI release, so it
  # comes off its repository at a pinned tag.
  if install_wheel_module havsfunc "$HAVSFUNC_VERSION" "havsfunc-${HAVSFUNC_VERSION}-py3-none-any.whl"; then
    got=$((got + 1)); note_ok "havsfunc $HAVSFUNC_VERSION (the QTGMC function itself)"
    note_licence "  HAvsFunc           Unlicense (public domain; provides QTGMC)
                     Source: https://github.com/HomeOfVapourSynthEvolution/havsfunc"
  else
    missing+=("havsfunc")
  fi

  if install_wheel_module vsutil "$VSUTIL_VERSION" "vsutil-${VSUTIL_VERSION}-py3-none-any.whl"; then
    got=$((got + 1)); note_ok "vsutil $VSUTIL_VERSION (havsfunc dependency)"
    note_licence "  vsutil             MIT (VapourSynth helper functions, havsfunc dependency)
                     Source: https://github.com/Irrational-Encoding-Wizardry/vs-util"
  else
    missing+=("vsutil")
  fi

  if fetch "https://raw.githubusercontent.com/HomeOfVapourSynthEvolution/mvsfunc/${MVSFUNC_TAG}/mvsfunc.py" "$WORK/mvsfunc.py" \
     && mkdir -p "$VSYNTH_DIR/lib/site-packages" && cp "$WORK/mvsfunc.py" "$VSYNTH_DIR/lib/site-packages/"; then
    got=$((got + 1)); note_ok "mvsfunc $MVSFUNC_TAG (havsfunc dependency)"
    note_licence "  mvsfunc            MIT (VapourSynth helper functions, havsfunc dependency)
                     Source: https://github.com/HomeOfVapourSynthEvolution/mvsfunc"
  else
    missing+=("mvsfunc")
  fi

  if [ "${#missing[@]}" -gt 0 ]; then
    note_skip "QTGMC" "incomplete - missing ${missing[*]} - the app will deinterlace with bwdif instead"
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

# svt-av1-hdr continues the PSY line, which psy-ex/svt-av1-psy no longer develops, and is the
# only source tried. Mainline AOMediaCodec/SVT-AV1 used to sit behind it as a fallback, which
# meant a release where svt-av1-hdr had not published an asset yet shipped a mainline binary
# under the same filename, with nothing saying so. That is not a lesser build of the same
# thing: the PSY-only parameters the AV1AN tab's content presets are built from are absent
# there, and several of the ones mainline does accept it defaults off. A release with no PSY
# build is now a visible skip instead. Override with SVTAV1_REPOS to use something else.
SVTAV1_REPOS="${SVTAV1_REPOS:-juliobbv-p/svt-av1-hdr}"

bundle_svtav1() {
  local primary fallback repo
  case "$RID" in
    # svt-av1-hdr publishes per-microarchitecture builds. The x86-64-v3 one is preferred
    # over the bare znver2 one: v3 is a baseline level (AVX2 and friends) that any CPU from
    # roughly 2013 on satisfies, where znver2 alone is tuned for one AMD generation.
    win-x64)   primary='(windows|win64|win-x64|msvc)'; fallback='\.(zip|7z|exe)$'
               ASSET_EXCLUDE='(arm64|aarch64)'; ASSET_PREFER='x86[-_]64-v3' ;;
    linux-x64) primary='(linux|musl|gnu)';             fallback='(\.tar\.(gz|xz)|\.zip)$'
               ASSET_EXCLUDE='(arm64|aarch64)'; ASSET_PREFER='x86[-_]64-v3' ;;
    # No PSY-line build is published for macOS. Homebrew's svt-av1 is mainline, so it is
    # deliberately not suggested here any more - see the SVTAV1_REPOS note above.
    *)         note_skip "svt-av1" "no svt-av1-hdr build for $RID - build it from https://github.com/juliobbv-p/svt-av1-hdr (Homebrew's svt-av1 is mainline, which the AV1AN presets are not written for)"; return ;;
  esac

  for repo in $SVTAV1_REPOS; do
    if try_assets "$repo" "$primary" "$fallback" install_svtav1; then
      ASSET_EXCLUDE=""; ASSET_PREFER=""
      note_ok "svt-av1 ($LAST_ASSET from $repo)"
      note_licence "  SvtAv1EncApp       BSD-3-Clause-Clear + AOM Patent License 1.0
                     Source: https://gitlab.com/AOMediaCodec/SVT-AV1
                     Build:  https://github.com/$repo"
      return
    fi
  done

  ASSET_EXCLUDE=""; ASSET_PREFER=""
  note_skip "svt-av1" "no release binary in: $SVTAV1_REPOS"
}

# ─────────────────────── aomenc + vpxenc + x265 ───────────────────────
# av1an calls these for "-e aom", "-e vpx" and "-e x265" (VideoEncodersBin.AomAv1, .Vpx
# and .X265). None of the three ship Windows binaries of their own, so they come from
# MSYS2's mingw64 packages - the Windows runner image already carries pacman.

# <binary>:<package> pairs. The binary name is the one av1an looks for on PATH.
# SvtAv1EncApp is deliberately absent, though MSYS2 does package one: that package is mainline
# SVT-AV1, and it used to fill in here whenever bundle_svtav1 came up empty on Windows - the
# same silent substitution the SVTAV1_REPOS note above describes. svt-av1 now comes from
# bundle_svtav1's PSY-line build or not at all. encoder_licence still carries its entry, for
# anyone who overrides this list and takes the mainline binary on purpose.
# vpxenc is deliberately absent too: MSYS2's libvpx package ships the library without the CLI,
# which a build confirmed. See bundle_vpxenc.
MSYS2_ENCODERS="${MSYS2_ENCODERS:-aomenc:mingw-w64-x86_64-aom x265:mingw-w64-x86_64-x265 x264:mingw-w64-x86_64-x264}"

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
    x264)   note_licence "  x264               GPL-2.0-or-later
                     Source: https://www.videolan.org/developers/x264.html
                     Build:  https://packages.msys2.org/package/mingw-w64-x86_64-x264" ;;
    x265)   note_licence "  x265               GPL-2.0-or-later
                     Source: https://bitbucket.org/multicoreware/x265_git
                     Build:  https://packages.msys2.org/package/mingw-w64-x86_64-x265" ;;
    SvtAv1EncApp) note_licence "  SvtAv1EncApp       BSD-3-Clause-Clear + AOM Patent License 1.0
                     Source: https://gitlab.com/AOMediaCodec/SVT-AV1
                     Build:  https://packages.msys2.org/package/mingw-w64-x86_64-svt-av1" ;;
  esac
}

bundle_msys2_encoders() {
  # Derived rather than written out, so the skip messages cannot end up naming an encoder this
  # list no longer installs - which is exactly what they did while SvtAv1EncApp was in it.
  local entry names=""
  for entry in $MSYS2_ENCODERS; do names="${names:+$names + }${entry%%:*}"; done

  # Emptied on purpose by whoever overrode the list. Nothing to install, and nothing to name
  # in a skip message - which is what an empty list would otherwise produce.
  [ -z "$names" ] && return

  if [ "$RID" != "win-x64" ]; then
    note_skip "$names" "no portable build for $RID - install the aom, vpx, x264 and x265 tools from your package manager"
    return
  fi

  local root="${MSYS2_ROOT:-/c/msys64}"
  local msys_bash="$root/usr/bin/bash.exe" pacman="$root/usr/bin/pacman.exe"

  if [ ! -x "$pacman" ] || [ ! -x "$msys_bash" ]; then
    note_skip "$names" "MSYS2 not found at $root"
    return
  fi

  local packages=()
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

    # Already staged from a project's own release - that build is the more canonical one.
    [ -f "$ENC_DIR/$tool.exe" ] && continue

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

# ─────────────────────────── vpxenc ───────────────────────────
# No project publishes a prebuilt Windows vpxenc: the WebM project ships source only,
# ShiftMediaProject builds the library rather than the CLI, and MSYS2's libvpx package
# leaves the encoder out. This is the build the av1an ecosystem uses - one person's
# server with no signed provenance, so what lands is checked rather than trusted. Point
# VPXENC_URL elsewhere to use a different build, or set it empty to skip vpxenc entirely.
#
# The archive holds vpxenc.exe and vpxdec.exe; only the encoder is staged. The site
# publishes a SHA1 per binary - set VPXENC_SHA1 to the one shown for vpxenc.exe to pin
# this to a build that was actually looked at. Left unset, nothing is pinned, because the
# build rolls forward and a stale hash would reject every future one.
VPXENC_DEFAULT_URL="https://jeremylee.sh/bins/vpx.7z"

# Preferred over the URL above: a release asset. Attach a vpxenc.exe to any release of this
# repository and every build picks it up from there. That is the durable place for a binary
# upstream does not publish - it is versioned, it is fetched with the token the workflow
# already holds, and it does not expire the way a chat or CDN attachment link does (those
# carry a signed expiry of roughly a day, after which the build silently loses vpxenc again).
# Defaults to the repository being built; point VPXENC_REPO elsewhere to host it separately.
VPXENC_REPO="${VPXENC_REPO-${GITHUB_REPOSITORY:-}}"

# The build that was inspected for this repository: PE32+ console x86-64, identifying as
# libvpx v1.15.2-151-gd98e70839, importing nothing beyond KERNEL32 and the UCRT stubs, so
# the bare executable needs no DLLs staged beside it. Pinned so the asset route ships that
# exact binary and a later swap fails the check rather than going out unnoticed - replacing
# the asset means updating this hash. The URL route below keeps its own optional
# VPXENC_SHA1, unset by default, because that build rolls forward and a fixed hash there
# would reject every future one.
VPXENC_ASSET_SHA1="${VPXENC_ASSET_SHA1-d9d12249316e893ae8198e22c4937e91816db21a}"

# The release that binary is attached to. Named outright because the asset search otherwise
# reaches only the most recent handful of releases: attached once to v2.0.3, it stayed findable
# for seven releases and then slid out of view, and the build quietly fell back to a third-party
# URL whose certificate had expired. Pinned, how often releases go out stops mattering. The
# ordinary search still runs afterwards, so attaching vpxenc.exe to a newer release also works.
VPXENC_ASSET_TAG="${VPXENC_ASSET_TAG-v2.0.3}"

# Where that build came from, for the notice file. libvpx ships no Windows binaries of its
# own, so this is a community build rather than an official one, and saying so is the point
# - a user reading THIRD-PARTY.txt should be able to tell the difference.
VPXENC_CREDIT="${VPXENC_CREDIT:-community build of libvpx v1.15.2-151-gd98e70839, shared in the
                             AV1 community Discord linked from https://www.reddit.com/r/AV1/
                             and mirrored as a release asset of this repository}"

# Windows executables start with "MZ". A plain web server can answer a stale path with a
# 200 and an HTML error page, which would otherwise be staged as though it were vpxenc.
is_windows_exe() {
  [ "$(head -c 2 "$1" 2>/dev/null)" = "MZ" ]
}

# try_assets handler: the asset is either an archive holding vpxenc, or vpxenc itself.
install_vpxenc_asset() {
  local file="$1" dir="$2"

  if [ -n "$dir" ]; then
    install_binary "$dir" vpxenc "$ENC_DIR" || return 1
  else
    is_windows_exe "$file" || return 1
    mkdir -p "$ENC_DIR"
    cp "$file" "$ENC_DIR/vpxenc$EXE" || return 1
    chmod +x "$ENC_DIR/vpxenc$EXE" 2>/dev/null || true
  fi

  [ -f "$ENC_DIR/vpxenc$EXE" ] || return 1
}

# Shared by both sources: nothing is trusted just because it downloaded.
verify_vpxenc() {
  local origin="$1" expected="${2:-}" credit="${3:-$1}"

  if ! is_windows_exe "$ENC_DIR/vpxenc$EXE"; then
    rm -f "$ENC_DIR/vpxenc$EXE"
    note_skip "vpxenc" "$origin did not yield a Windows executable"
    return 1
  fi

  # An unsigned binary with no signed provenance is worth pinning when someone has checked it.
  if [ -n "$expected" ]; then
    local got
    got="$(sha1sum "$ENC_DIR/vpxenc$EXE" 2>/dev/null | cut -d' ' -f1)"
    if [ "$(printf '%s' "$got" | tr 'A-F' 'a-f')" != "$(printf '%s' "$expected" | tr 'A-F' 'a-f')" ]; then
      rm -f "$ENC_DIR/vpxenc$EXE"
      note_skip "vpxenc" "SHA1 $got does not match the pinned $expected"
      return 1
    fi
  fi

  note_ok "vpxenc ($origin)"
  note_licence "  vpxenc             BSD-3-Clause (libvpx)
                     Source: https://chromium.googlesource.com/webm/libvpx/
                     Build:  $credit"
}

bundle_vpxenc() {
  local url="${VPXENC_URL-$VPXENC_DEFAULT_URL}"

  if [ "$RID" != "win-x64" ]; then
    return
  fi

  if [ -f "$ENC_DIR/vpxenc$EXE" ]; then
    return
  fi

  if [ -n "$VPXENC_REPO" ] && try_assets "$VPXENC_REPO" '[Vv]pxenc.*\.(exe|zip|7z)$' '' install_vpxenc_asset "$VPXENC_ASSET_TAG"; then
    verify_vpxenc "$LAST_ASSET from $VPXENC_REPO releases" "$VPXENC_ASSET_SHA1" "$VPXENC_CREDIT" && return
  fi

  if [ -z "$url" ]; then
    note_skip "vpxenc" "VPXENC_URL set empty and no vpxenc release asset in ${VPXENC_REPO:-<unset>}"
    return
  fi

  local file="$WORK/vpxenc-$(basename "$url")"
  if ! fetch "$url" "$file"; then
    note_skip "vpxenc" "download failed ($url)"
    return
  fi

  rm -rf "$WORK/vpx"
  extract "$file" "$WORK/vpx"
  case "$?" in
    0) if ! install_binary "$WORK/vpx" vpxenc "$ENC_DIR"; then
         note_skip "vpxenc" "no vpxenc binary inside $(basename "$url")"
         return
       fi ;;
    2) if ! is_windows_exe "$file"; then
         note_skip "vpxenc" "$url did not return a Windows executable"
         return
       fi
       mkdir -p "$ENC_DIR"
       cp "$file" "$ENC_DIR/vpxenc$EXE" || { note_skip "vpxenc" "could not stage $(basename "$url")"; return; }
       chmod +x "$ENC_DIR/vpxenc$EXE" 2>/dev/null || true ;;
    *) note_skip "vpxenc" "could not extract $(basename "$url")"; return ;;
  esac

  verify_vpxenc "$url" "${VPXENC_SHA1:-}"
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

# ─────────────────────────── grav1synth ───────────────────────────
# Reads and writes the film grain description in an AV1 bitstream, which is what the AV1AN
# tab's Grain Synthesis row needs for every mode but "Encoder analysis". Nmkoder runs it
# out of bin/, like mkvmerge.
#
# **It is built from source, because it has never cut a release.** There are no assets to
# fetch - not one tag, on a repository that has been going for years - so try_assets has
# nothing to work with and this is the one tool here that needs a compiler.
#
# Two details that are not obvious and were both measured:
#
#   1. Do NOT use `cargo install --git`. Cargo fetches a git dependency's submodules, and
#      grav1synth carries dav1d-test-data from code.videolan.org - hundreds of megabytes of
#      conformance clips that the build itself never reads. A plain shallow clone takes no
#      submodules and builds identically.
#   2. Do NOT use the crates.io release either. 0.2.0 is far behind the source: it has no
#      film stock presets, no --replace, no diff filters, and its frame reader assumes the
#      decoder's stride equals the frame width - which is false for most widths, so `diff`
#      dies with "data length mismatch, expected 76800, found 92160" on an ordinary 320x240
#      clip. The commit below handles strides properly.
#
# Pinned rather than tracking main, because this parses an AV1 bitstream and rewrites it in
# place: a regression upstream would be discovered in somebody's finished encode.
GRAV1SYNTH_REPO="${GRAV1SYNTH_REPO:-https://github.com/rust-av/grav1synth}"
GRAV1SYNTH_REV="${GRAV1SYNTH_REV:-1044228cd411672b565e5762a9b3597f4dd163b0}"

# ffmpeg-the-third links against the system ffmpeg, so the build needs its headers and
# import libraries - which is the whole reason this is per-platform. Linux and macOS have a
# package for it; Windows has none, so the dev files come out of the same BtbN build the
# app already ships, in its "shared" flavour, which carries include/ and lib/ beside bin/.
grav1synth_ffmpeg_dev() {
  case "$RID" in
    linux-x64)
      sudo apt-get update -qq >/dev/null 2>&1 || return 1
      sudo apt-get install -y -qq --no-install-recommends \
        libavformat-dev libavcodec-dev libavutil-dev libswscale-dev libavfilter-dev libavdevice-dev >/dev/null 2>&1 || return 1
      ;;
    osx-x64|osx-arm64)
      brew list ffmpeg >/dev/null 2>&1 || brew install ffmpeg >/dev/null 2>&1 || return 1
      ;;
    win-x64)
      local url="https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl-shared.zip"
      fetch "$url" "$WORK/ffdev.zip" || return 1
      extract "$WORK/ffdev.zip" "$WORK/ffdev" || return 1
      local root
      root="$(find "$WORK/ffdev" -type d -name include -print -quit)" || return 1
      [ -n "$root" ] || return 1
      export FFMPEG_DIR="$(dirname "$root")"
      ;;
    *) return 1 ;;
  esac
}

# Whether this runner can produce a binary for the RID being built. Compiling produces a
# binary for the host, so an osx-x64 job on the arm64 macOS runner would quietly ship an
# arm64 grav1synth inside an Intel zip - a skip that says so is far better than that.
grav1synth_arch_matches() {
  local host; host="$(uname -m 2>/dev/null || echo unknown)"

  case "$RID" in
    *-arm64) [ "$host" = "arm64" ] || [ "$host" = "aarch64" ] ;;
    *-x64)   [ "$host" = "x86_64" ] || [ "$host" = "amd64" ] ;;
    *)       return 1 ;;
  esac
}

bundle_grav1synth() {
  command -v cargo >/dev/null 2>&1 || {
    note_skip "grav1synth" "no Rust toolchain on this runner - it has no prebuilt binaries and must be compiled"
    return
  }

  grav1synth_arch_matches || {
    note_skip "grav1synth" "this runner is $(uname -m) and $RID needs another architecture - it is compiled, not downloaded"
    return
  }

  grav1synth_ffmpeg_dev || {
    note_skip "grav1synth" "could not get ffmpeg development headers for $RID"
    return
  }

  local src="$WORK/grav1synth"
  # --depth 1 on the pinned commit rather than a full clone: the history is not wanted and
  # neither, emphatically, are the submodules.
  git init -q "$src" >/dev/null 2>&1 || { note_skip "grav1synth" "git init failed"; return; }
  (
    cd "$src" &&
    git remote add origin "$GRAV1SYNTH_REPO" &&
    git fetch -q --depth 1 origin "$GRAV1SYNTH_REV" &&
    git checkout -q FETCH_HEAD
  ) >/dev/null 2>&1 || { note_skip "grav1synth" "could not fetch $GRAV1SYNTH_REV from $GRAV1SYNTH_REPO"; return; }

  ( cd "$src" && cargo build --release --locked ) || {
    note_skip "grav1synth" "cargo build failed - see the log above"
    return
  }

  install_binary "$src/target/release" grav1synth "$BIN" || {
    note_skip "grav1synth" "cargo reported success but no binary was produced"
    return
  }

  # Windows links against BtbN's *shared* ffmpeg, because that is the only build of it that ships
  # headers and import libraries at all - the plain win64-gpl zip is bin/, doc/ and presets/ and has
  # nothing to link against. So the exe needs those DLLs beside it, and install_binary cannot have
  # brought them: it copies DLLs sitting next to the binary it found, and a cargo build's target
  # directory has none. Without this the binary is built perfectly and cannot start, which is what
  # 2.8.31 shipped - or rather did not ship, the check below having caught it.
  #
  # It costs about 168 MB uncompressed on a Windows download that is already large, and that is the
  # deliberate trade: the alternative is --features ffmpeg_static, which builds ffmpeg from source on
  # every release. All of them go rather than a chosen few - which DLL pulls in which is a property of
  # how BtbN configured that build, not something this script can know, and a missing one is an exe
  # that will not start.
  grav1synth_dlls() {
    [ "$RID" = "win-x64" ] || return 0
    [ -n "${FFMPEG_DIR:-}" ] && [ -d "$FFMPEG_DIR/bin" ] || return 1
    find "$FFMPEG_DIR/bin" -maxdepth 1 -name '*.dll' -exec cp {} "$BIN/" \; 2>/dev/null

    # Checked by naming one of them rather than by "are there any DLLs in bin/" - MKVToolNix installs
    # its own there, so the loose test would pass on a copy that had done nothing.
    ls "$BIN"/avformat*.dll >/dev/null 2>&1
  }

  grav1synth_dlls || {
    rm -f "$BIN/grav1synth$EXE"
    note_skip "grav1synth" "its ffmpeg DLLs could not be copied beside it, and it cannot start without them"
    return
  }

  # Presence is not usability - the same lesson the VapourSynth plugins taught. A binary that
  # cannot find its ffmpeg libraries at runtime prints nothing this app would see, so it is
  # asked to do the one thing every mode needs it to do first.
  # Run *after* the DLLs are in place, so what is tested is the layout that ships rather than the one
  # the runner happens to have. Everything this tool put in bin/ goes with it on a failure: 168 MB of
  # ffmpeg DLLs are dead weight in the zip if the binary they are for is not there.
  "$BIN/grav1synth$EXE" presets >/dev/null 2>&1 || {
    rm -f "$BIN/grav1synth$EXE"
    [ "$RID" = "win-x64" ] && [ -n "${FFMPEG_DIR:-}" ] && [ -d "$FFMPEG_DIR/bin" ] &&
      find "$FFMPEG_DIR/bin" -maxdepth 1 -name '*.dll' -exec sh -c 'rm -f "$1/$(basename "$2")"' _ "$BIN" {} \; 2>/dev/null
    note_skip "grav1synth" "the built binary could not run even with its libraries beside it"
    return
  }

  note_ok "grav1synth ($GRAV1SYNTH_REV)"
  note_licence "  grav1synth         MIT
                     $GRAV1SYNTH_REPO
                     Built from source at $GRAV1SYNTH_REV$([ "$RID" = "win-x64" ] && printf '\n                     Ships the FFmpeg shared libraries it links against (GPL-3.0, see FFmpeg above)')"
}

echo "Bundling external tools for $RID into $BIN"
bundle_ffmpeg
bundle_mkvtoolnix
bundle_av1an
bundle_vapoursynth
bundle_svtav1
bundle_msys2_encoders
bundle_vpxenc
bundle_vmaf_models
bundle_grav1synth
# A tool that skipped may have left its (now empty) destination folder behind.
find "$BIN" -mindepth 1 -type d -empty -delete 2>/dev/null || true
write_notice

echo
echo "Bundled: ${#BUNDLED[@]} | Skipped: ${#SKIPPED[@]}"
[ "${#SKIPPED[@]}" -gt 0 ] && printf 'Not bundled: %s\n' "${SKIPPED[*]}"

# Never fail the release over an unavailable third-party download.
exit 0
