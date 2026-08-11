#!/usr/bin/env bash
# Environment setup for Nmkoder: the .NET SDK the project targets, and the FFmpeg it ships
# against. Runs once when the environment is created and is snapshotted with it, so every
# session taken from that snapshot starts with both already in place.
#
# **This is the only place either is installed.** .claude/hooks/session-start.sh used to carry
# a second copy of the SDK install as a fallback and no longer does; it repairs the git ref the
# snapshot leaves stale, which genuinely has to happen per session, and otherwise only reports
# what it finds on PATH.
#
# That report is the whole safety net now, and it is worth understanding why it is needed: this
# script has to end in a zero exit, because a non-zero one fails session startup - so a failure
# here is silent by construction, and the hook's first line is where it surfaces.
#
# Point the environment's setup command at this file (`bash .claude/setup.sh`) rather than
# pasting a copy into the environment's settings, so the two cannot drift.

# No `set -e`. A setup script that exits non-zero fails session startup, so every step below
# reports its own failure and the script ends in `exit 0` regardless.
set -uo pipefail

log() { echo "setup: $*"; }

# Containers run as root; keep working where some other environment does not. -E rides along
# so DEBIAN_FRONTEND and the telemetry opt-outs survive into apt.
SUDO=""
[ "$(id -u)" -eq 0 ] || SUDO="sudo -E"

export DEBIAN_FRONTEND=noninteractive
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

LOG="$(mktemp -t setup.XXXXXX.log)"

# ---------------------------------------------------------------------------------------
# .NET SDK
# ---------------------------------------------------------------------------------------
# From the Ubuntu archive rather than the usual dot.net installer script. That script
# redirects to builds.dotnet.microsoft.com, which the sandbox's egress proxy refuses with a
# 403, so the download fails before it starts. The archive is reachable and its
# dotnet-sdk-N.0 package carries the same SDK.

# Follow the project's own target framework, so a TFM bump does not leave this behind. The
# repository may not be cloned yet when a setup script runs, so this falls back rather than
# failing - and the fallback is the version the project is on today.
MAJOR=""
for csproj in \
  "${CLAUDE_PROJECT_DIR:-}/Nmkoder/Nmkoder.csproj" \
  /home/user/nmkoder/Nmkoder/Nmkoder.csproj \
  ./Nmkoder/Nmkoder.csproj
do
  [ -f "$csproj" ] || continue
  MAJOR="$(sed -n 's/.*<TargetFramework>net\([0-9][0-9]*\)\..*/\1/p' "$csproj" | head -1)"
  [ -n "$MAJOR" ] && break
done
MAJOR="${MAJOR:-10}"

if dotnet --list-sdks 2>/dev/null | grep -q "^${MAJOR}\."; then
  log ".NET ${MAJOR} SDK already present"
else
  log "installing the .NET ${MAJOR} SDK"
  # The preloaded package index points at .debs the mirror has already superseded, so every
  # download 404s without this refresh. Blocked third-party PPAs only warn.
  $SUDO apt-get update -qq >>"$LOG" 2>&1 || true
  $SUDO apt-get install -y "dotnet-sdk-${MAJOR}.0" >>"$LOG" 2>&1 \
    || log "installing the SDK failed, output in $LOG"
fi

# ---------------------------------------------------------------------------------------
# FFmpeg and ffprobe
# ---------------------------------------------------------------------------------------
# BtbN's master-latest GPL build, which is exactly what .github/scripts/bundle-tools.sh puts
# in a release - so anything measured here is measured against the binary users get rather
# than against whatever the distribution packages.
#
# That matters more than it sounds. This project's tone mapping needs libplacebo, zscale,
# tonemap and setparams, and its metrics work needs libvmaf; a distribution ffmpeg routinely
# ships without some of them, and the app's own probes would then report a machine that
# cannot tone-map rather than a toolchain that was never installed properly.
#
# master-latest moves, and that is the intended trade: the app follows it too, so a format
# this build prints today is the format the app is reading today.
FFMPEG_URL="https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-linux64-gpl.tar.xz"
FFMPEG_DIR="/usr/local/bin"

have_filters() {
  local list
  list="$(ffmpeg -hide_banner -filters 2>/dev/null)" || return 1
  for f in libplacebo zscale tonemap setparams sidedata; do
    printf '%s' "$list" | grep -q " ${f} " || return 1
  done
  return 0
}

if command -v ffmpeg >/dev/null 2>&1 && command -v ffprobe >/dev/null 2>&1 && have_filters; then
  log "ffmpeg already present with the filters this project needs"
else
  log "installing ffmpeg + ffprobe (BtbN master-latest, the build the app bundles)"
  TMP="$(mktemp -d)"
  if curl -fsSL --retry 3 --retry-delay 2 -o "$TMP/ff.tar.xz" "$FFMPEG_URL" >>"$LOG" 2>&1 \
    && tar -xf "$TMP/ff.tar.xz" -C "$TMP" >>"$LOG" 2>&1
  then
    # The tarball unpacks to ffmpeg-master-latest-linux64-gpl/bin/{ffmpeg,ffprobe}; find them
    # rather than composing the path, so a rename upstream does not silently install nothing.
    for tool in ffmpeg ffprobe; do
      src="$(find "$TMP" -type f -name "$tool" -perm -u+x | head -1)"
      if [ -n "$src" ]; then
        $SUDO install -m 0755 "$src" "$FFMPEG_DIR/$tool" >>"$LOG" 2>&1 \
          || log "could not install $tool into $FFMPEG_DIR"
      else
        log "$tool was not in the archive"
      fi
    done
  else
    log "downloading ffmpeg failed, output in $LOG"
  fi
  rm -rf "$TMP"
fi

# ---------------------------------------------------------------------------------------
# Warm the NuGet cache
# ---------------------------------------------------------------------------------------
# So the packages land in the snapshot rather than being fetched on the first build of every
# session. Skipped where the repository is not cloned yet, which is the ordinary case for a
# setup script that runs before the source arrives.
for sln in "${CLAUDE_PROJECT_DIR:-}/Nmkoder.sln" /home/user/nmkoder/Nmkoder.sln ./Nmkoder.sln; do
  [ -f "$sln" ] || continue
  log "restoring packages"
  dotnet restore "$sln" >>"$LOG" 2>&1 || log "restoring packages failed, output in $LOG"
  break
done

# ---------------------------------------------------------------------------------------
# Report what is actually there
# ---------------------------------------------------------------------------------------
# Read it back rather than assuming the steps above worked, so a broken environment says so
# at creation time instead of at the first build.
SDK="$(dotnet --version 2>/dev/null || echo none)"
FF="$(ffmpeg -version 2>/dev/null | head -1 | cut -d' ' -f3 || echo none)"
log "ready: .NET SDK ${SDK}, ffmpeg ${FF}"

[ "$SDK" = none ] && log "WARNING: no .NET SDK on PATH - builds will fail"
[ "$FF" = none ] && log "WARNING: no ffmpeg on PATH - the app falls back to the system one at runtime"

# Always zero: a non-zero exit from a setup script fails session startup, and a half-installed
# environment that starts is worth more than one that refuses to.
exit 0
