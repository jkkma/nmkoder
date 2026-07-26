#!/usr/bin/env bash
# Downloads the external tools Nmkoder shells out to and stages them in the portable
# build's "bin" folder, which is where Paths.GetBinPath() looks first.
#
# Every download is best-effort: a dead upstream URL prints a warning and is skipped
# rather than failing the release. Check the summary at the end to see what landed.
set -uo pipefail

RID="${1:?usage: bundle-tools.sh <rid> <bin-dir>}"
BIN="${2:?usage: bundle-tools.sh <rid> <bin-dir>}"

mkdir -p "$BIN"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

BUNDLED=()
SKIPPED=()

note_ok()   { BUNDLED+=("$1"); echo "  [ok]   $1"; }
note_skip() { SKIPPED+=("$1"); echo "  [skip] $1 - $2"; }

# Fetch a URL to a file. Returns non-zero (quietly) if the download fails.
fetch() {
  curl -fsSL --retry 3 --retry-delay 2 --connect-timeout 20 -o "$2" "$1"
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
    local src
    src="$(find "$WORK/ff" -type f \( -name "$tool" -o -name "$tool.exe" \) -print -quit)"
    if [ -n "$src" ]; then
      cp "$src" "$BIN/"
      chmod +x "$BIN/$(basename "$src")" 2>/dev/null || true
      found=$((found + 1))
    fi
  done

  if [ "$found" -eq 2 ]; then note_ok "ffmpeg + ffprobe"; else note_skip "ffmpeg" "binaries not found in archive"; fi
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
    local src
    src="$(find "$WORK/mkv" -type f -name "$tool.exe" -print -quit)"
    if [ -n "$src" ]; then cp "$src" "$BIN/"; found=$((found + 1)); fi
  done

  # mkvmerge needs its sibling DLLs from the same directory.
  local mkvdir
  mkvdir="$(find "$WORK/mkv" -type f -name 'mkvmerge.exe' -print -quit)"
  if [ -n "$mkvdir" ]; then
    find "$(dirname "$mkvdir")" -maxdepth 1 -name '*.dll' -exec cp {} "$BIN/" \; 2>/dev/null || true
  fi

  if [ "$found" -gt 0 ]; then note_ok "mkvtoolnix ($found tools)"; else note_skip "mkvtoolnix" "binaries not found"; fi
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

  if [ "$got" -gt 0 ]; then note_ok "vmaf models ($got)"; else note_skip "vmaf models" "download failed"; fi
}

# ─────────────────────────── Licence notice ───────────────────────────
# ffmpeg and MKVToolNix ship as GPL binaries; GPL requires pointing users at their sources.
write_notice() {
  cat > "$BIN/THIRD-PARTY.txt" <<'EOF'
Nmkoder invokes the following programs as separate processes. They are bundled here
for convenience and remain under their own licences.

  ffmpeg / ffprobe   GPL-3.0-or-later (GPL build)
                     Source: https://ffmpeg.org/download.html
                     Build:  https://github.com/BtbN/FFmpeg-Builds

  mkvmerge / mkvextract / mkvinfo (MKVToolNix)
                     GPL-2.0-only
                     Source: https://mkvtoolnix.download/source.html

  VMAF models        BSD-2-Clause-Patent
                     Source: https://github.com/Netflix/vmaf

Nmkoder itself is GPL-3.0. See LICENSE in the repository root.
EOF
  note_ok "THIRD-PARTY.txt"
}

echo "Bundling external tools for $RID into $BIN"
bundle_ffmpeg
bundle_mkvtoolnix
bundle_vmaf_models
write_notice

echo
echo "Bundled: ${#BUNDLED[@]} | Skipped: ${#SKIPPED[@]}"
[ "${#SKIPPED[@]}" -gt 0 ] && printf 'Not bundled: %s\n' "${SKIPPED[*]}"

# Never fail the release over an unavailable third-party download.
exit 0
