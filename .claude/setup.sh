#!/usr/bin/env bash
# Environment setup for Nmkoder: the .NET SDK the project targets, the FFmpeg it ships
# against, and the measurement toolkit (the CLI encoders, MKVToolNix, and the shipped
# SvtAv1EncApp/av1an/grav1synth out of the latest published release). Runs once when the
# environment is created and is snapshotted with it, so every session taken from that
# snapshot starts with all of it already in place.
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
# Measurement toolkit
# ---------------------------------------------------------------------------------------
# The other binaries this project's sessions keep needing to measure against. Two sources,
# chosen by what the sandbox's egress proxy actually allows (probed, not guessed): direct
# `github.com/<repo>/releases/download/...` URLs pass for any repository - that is how the
# BtbN ffmpeg above arrives - while api.github.com answers only for the repository attached
# to the session, and github.com HTML pages are refused outright. So release-asset discovery
# on svt-av1-hdr or rust-av/Av1an is closed from here, and the shipped binaries come out of
# this project's own published release instead - which is better anyway, being the exact
# binaries users run rather than whatever upstream published since.

# The CLI encoders Quick Convert drives, and the MKVToolNix trio that is bundled for win-x64
# alone and therefore routinely absent here. Distribution builds, so a measurement against
# them should say so - but a session that needs mkvmerge or a second x264 wants them present,
# not installable.
if command -v x264 >/dev/null 2>&1 && command -v x265 >/dev/null 2>&1 \
  && command -v aomenc >/dev/null 2>&1 && command -v vpxenc >/dev/null 2>&1 \
  && command -v mkvmerge >/dev/null 2>&1; then
  log "encoder CLIs + mkvtoolnix already present"
else
  log "installing encoder CLIs + mkvtoolnix from the archive"
  # Same stale-index problem as the SDK install above: refresh or every download 404s.
  $SUDO apt-get update -qq >>"$LOG" 2>&1 || true
  $SUDO apt-get install -y x264 x265 aom-tools vpx-tools mkvtoolnix >>"$LOG" 2>&1 \
    || log "installing encoder CLIs failed, output in $LOG"
fi

# The tools only ever seen here as "no binary in a web session": the PSY-line SvtAv1EncApp
# and grav1synth, taken from the latest published linux-x64 release - ~180 MB once per
# environment, then snapshotted. av1an is tried too but expected absent: the linux tarball
# carries no av1an (measured on 2.8.60 - rust-av publishes no linux release binary), and the
# attempt stays because a future release that does carry one lands here for free. Sessions
# that need the *shipped* av1an's help text pull av1an.exe out of the win-x64 zip with
# .claude/skills/real-binaries/scripts/fetch-zip-member.py and read its strings.
# The presence gate deliberately leaves av1an out: it is expected absent (see above), so
# gating on it would have every re-run download 180 MB to end where it started.
SHIP_DIR="/usr/local/bin"
if [ -x "$SHIP_DIR/SvtAv1EncApp" ] && [ -x "$SHIP_DIR/grav1synth" ]; then
  log "shipped tools (SvtAv1EncApp, grav1synth) already present"
else
  # The API names the truly-latest release; the csproj's <Version> is the fallback, and per
  # CLAUDE.md it is usually the number already released, which is exactly what is wanted.
  VER="$(curl -fsSL --max-time 30 https://api.github.com/repos/jkkma/nmkoder/releases/latest 2>>"$LOG" \
    | sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"v\{0,1\}\([^"]*\)".*/\1/p' | head -1)"
  if [ -z "$VER" ]; then
    for csproj in "${CLAUDE_PROJECT_DIR:-}/Nmkoder/Nmkoder.csproj" /home/user/nmkoder/Nmkoder/Nmkoder.csproj ./Nmkoder/Nmkoder.csproj; do
      [ -f "$csproj" ] || continue
      VER="$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' "$csproj" | head -1)"
      [ -n "$VER" ] && break
    done
  fi
  if [ -n "$VER" ]; then
    log "extracting shipped tools from the ${VER} linux-x64 release"
    TMP="$(mktemp -d)"
    SHIP_URL="https://github.com/jkkma/nmkoder/releases/download/v${VER}/Nmkoder-${VER}-linux-x64.tar.gz"
    if curl -fsSL --retry 3 --retry-delay 2 -o "$TMP/nmk.tar.gz" "$SHIP_URL" >>"$LOG" 2>&1; then
      # One extraction for all three; a member missing from the archive (bundling is
      # best-effort by design) errors the tar without stopping the ones that are there.
      tar -xzf "$TMP/nmk.tar.gz" -C "$TMP" \
        Nmkoder/bin/av1an/enc/SvtAv1EncApp Nmkoder/bin/av1an/av1an Nmkoder/bin/grav1synth \
        >>"$LOG" 2>&1 || true
      for member in Nmkoder/bin/av1an/enc/SvtAv1EncApp Nmkoder/bin/av1an/av1an Nmkoder/bin/grav1synth; do
        tool="$(basename "$member")"
        [ -x "$SHIP_DIR/$tool" ] && continue
        if [ -s "$TMP/$member" ]; then
          $SUDO install -m 0755 "$TMP/$member" "$SHIP_DIR/$tool" >>"$LOG" 2>&1 \
            || log "could not install $tool into $SHIP_DIR"
        else
          log "$tool was not in the ${VER} release archive"
        fi
      done
    else
      log "downloading the ${VER} release tarball failed, output in $LOG"
    fi
    rm -rf "$TMP"
  else
    log "could not work out the latest release version - shipped tools skipped"
  fi
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

# The toolkit is best-effort, so the report lists what actually landed rather than what the
# steps above intended - a missing entry here is the first anyone hears of a failed step.
TOOLS=""
for t in x264 x265 aomenc vpxenc mkvmerge SvtAv1EncApp av1an grav1synth; do
  command -v "$t" >/dev/null 2>&1 && TOOLS="$TOOLS $t"
done
log "toolkit:${TOOLS:- none}"

[ "$SDK" = none ] && log "WARNING: no .NET SDK on PATH - builds will fail"
[ "$FF" = none ] && log "WARNING: no ffmpeg on PATH - the app falls back to the system one at runtime"

# Always zero: a non-zero exit from a setup script fails session startup, and a half-installed
# environment that starts is worth more than one that refuses to.
exit 0
