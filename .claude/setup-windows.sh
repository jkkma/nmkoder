#!/usr/bin/env bash
# Local Windows dev setup for Nmkoder - the counterpart of .claude/setup.sh, which is the web
# container's installer and is Linux-only (apt-get, /usr/local/bin) and so has never run on a
# laptop or desktop. Run this once per machine, from Git Bash, in the repo:
#
#     bash .claude/setup-windows.sh
#
# and again after `dotnet clean`, after a fresh clone or worktree, or to pick up a newer release
# of the shipped tools. Every step is presence-gated, so a re-run is seconds.
#
# What it does, and why each half is needed:
#
#  1. Checks the .NET SDK the csproj targets is on PATH, and builds the project if it has not
#     been built here yet - a dev setup that ends with a built app is the point of it.
#
#  2. Puts the *shipped* toolchain beside the built exe. The app finds its tools in `bin/` next
#     to Nmkoder.exe (Paths.GetBinPath) and squeezes the launched tools' PATH to that folder plus
#     C:\Windows (OsUtils.GetPathVar) - so a Scoop-installed encoder is invisible to it, and a dev
#     build without a staged `bin/` has no ffmpeg of its own, no av1an, no encoders, no mkvmerge:
#     Quick Convert refuses every direct-encoder codec, the AV1AN tab cannot start, and the rest
#     falls back to whatever ffmpeg the machine has. The build itself only lays out `BinFiles/`
#     (encoderArgs and iso639.csv) there.
#
#     The tools come out of the latest published win-x64 release zip, which is the bundler's own
#     output - the exact binaries users get, PSY-line SvtAv1EncApp and all - rather than from a
#     package manager: Scoop's svt-av1 is mainline, which this project deliberately does not ship
#     (see CLAUDE.md, "this project ships the PSY line or nothing"), and running bundle-tools.sh
#     locally would want MSYS2, cargo and the rest of the runner image. ~485 MB once, then cached.
#
#     The cache lives at ~/.nmkoder-dev/bin and is hardlinked (not copied) into every build output
#     that has an Nmkoder.exe, so the two TFMs' Debug folders and the cache share one set of bytes,
#     `dotnet clean` and `rm -rf bin` cost nothing but a re-run of this script, and a worktree's
#     build gets the same tools by running it there. The build's own BinFiles copies are never
#     overwritten by the release's - a working tree that has edited an encoderArgs JSON must test
#     that edit, not the released one.
#
#     **Why the profile root and not %LOCALAPPDATA%.** Claude Desktop is a packaged (MSIX) app with
#     file-system write virtualization on: anything a session writes under AppData\Local lands in
#     C:\Users\<you>\AppData\Local\Packages\Claude_<id>\LocalCache\Local\... and is invisible to a
#     process launched outside Claude - measured here with `fsutil hardlink list`, which prints the
#     real NTFS path. The profile root, ~/scoop and the repo are not virtualized. (Registry write
#     virtualization is *disabled* in that package's manifest, which is why step 3 works from a
#     session at all.)
#
#  3. Appends the cache's tool folders to the user PATH - bin, bin\av1an, bin\av1an\enc and
#     bin\av1an\vsynth - so SvtAv1EncApp, av1an, x264/x265/aomenc/vpxenc, mkvmerge, grav1synth
#     and VSPipe answer from any shell, which is what .claude/setup.sh's /usr/local/bin installs
#     buy a web session. vsynth is on the list because av1an.exe panics without VSScript.dll
#     ("VSScript API not available") - it is the same PATH AvProcess.RunAv1an composes for it, and
#     with it `av1an --version` lists every plugin and encoder the release carries. Appended, not
#     prepended: a Scoop ffmpeg or python already on PATH keeps answering to its name (vsynth
#     carries an embedded python.exe and a 7z.exe), and the shipped BtbN ffmpeg - the one to
#     measure against per CLAUDE.md - is always at ~/.nmkoder-dev/bin/ffmpeg.exe. A shell has to
#     be reopened (and Claude Desktop restarted) to see the change.
#
# No `set -e`, for the same reason setup.sh has none: every step reports its own failure and the
# summary at the end reads back what is actually there. The exit code is honest, though - nothing
# runs this at session startup, so a run that could not stage the toolchain exits 1.
set -uo pipefail

log() { echo "setup-windows: $*"; }

case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*) ;;
  *) log "this is the Windows setup; on Linux the environment runs .claude/setup.sh"; exit 0 ;;
esac

PROJECT_DIR="${CLAUDE_PROJECT_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
CSPROJ="$PROJECT_DIR/Nmkoder/Nmkoder.csproj"
BINFILES="$PROJECT_DIR/Nmkoder/BinFiles"
[ -f "$CSPROJ" ] || { log "no Nmkoder/Nmkoder.csproj under $PROJECT_DIR - run this from the repo"; exit 1; }

# The cache: profile root, not AppData - see the header. Override to relocate it.
DEV_ROOT="${NMKODER_DEV_ROOT:-$HOME/.nmkoder-dev}"
CACHE="$DEV_ROOT/bin"
STAMP="$DEV_ROOT/bin.version"
mkdir -p "$DEV_ROOT"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

FAILED=0

# ---------------------------------------------------------------------------------------
# 1. .NET SDK and a first build
# ---------------------------------------------------------------------------------------
MAJOR="$(sed -n 's/.*<TargetFramework>net\([0-9][0-9]*\)\..*/\1/p' "$CSPROJ" | head -1)"
MAJOR="${MAJOR:-10}"
if dotnet --list-sdks 2>/dev/null | grep -q "^${MAJOR}\."; then
  log ".NET ${MAJOR} SDK present ($(dotnet --version 2>/dev/null))"
else
  log "no .NET ${MAJOR} SDK on PATH - install it (winget install Microsoft.DotNet.SDK.${MAJOR}) and re-run"
  FAILED=1
fi

# Both TFMs build on a Windows host (net10.0 and the Windows App SDK one), so the exe lands in
# Nmkoder/bin/Debug/net10.0/ and Nmkoder/bin/Debug/net10.0-windows10.0.19041.0/win-x64/. Either
# runs; the win-x64 one is the shape the release ships (notifications and all).
if [ "$FAILED" -eq 0 ] && ! find "$PROJECT_DIR/Nmkoder/bin" -maxdepth 4 -name Nmkoder.exe -print -quit 2>/dev/null | grep -q .; then
  log "no build output yet - building (first build restores packages, a minute or so)"
  if ! dotnet build "$CSPROJ" >"$DEV_ROOT/build.log" 2>&1; then
    log "build failed - see $DEV_ROOT/build.log"
    FAILED=1
  fi
fi

# ---------------------------------------------------------------------------------------
# 2. The shipped toolchain
# ---------------------------------------------------------------------------------------
# Which release: NMKODER_TOOLS_VERSION pins one; otherwise the latest published. `gh` first
# (installed and authenticated on the user's machines), api.github.com by curl otherwise - a
# local machine reaches it, unlike the web sandbox setup.sh describes.
latest_version() {
  local v=""
  if command -v gh >/dev/null 2>&1; then
    v="$(gh release view --repo jkkma/nmkoder --json tagName --jq .tagName 2>/dev/null)"
  fi
  if [ -z "$v" ]; then
    v="$(curl -fsSL --max-time 30 https://api.github.com/repos/jkkma/nmkoder/releases/latest 2>/dev/null \
      | sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -1)"
  fi
  printf '%s' "${v#v}"
}

# The tools whose absence cripples a tab, checked by file rather than by trusting the stamp - and
# asked of a freshly extracted tree before it replaces the cache, so a partial extraction can never
# be judged a success on the strength of one file (the first cut did exactly that, on ffmpeg.exe
# alone, and deleted the zip over an av1an/ that had not come out).
toolchain_complete() {
  local d="${1:-$CACHE}"
  [ -f "$d/ffmpeg.exe" ] && [ -f "$d/ffprobe.exe" ] && [ -f "$d/mkvmerge.exe" ] \
    && [ -f "$d/av1an/av1an.exe" ] && [ -f "$d/av1an/enc/SvtAv1EncApp.exe" ] \
    && [ -f "$d/av1an/enc/x264.exe" ] && [ -f "$d/av1an/vsynth/VSPipe.exe" ]
}

VER="${NMKODER_TOOLS_VERSION:-$(latest_version)}"
if [ -z "$VER" ]; then
  if toolchain_complete; then
    log "could not reach GitHub for the latest release - keeping the cached $(cat "$STAMP" 2>/dev/null || echo unknown) toolchain"
  else
    log "could not reach GitHub for the latest release and nothing is cached - toolchain skipped"
    FAILED=1
  fi
elif toolchain_complete && [ "$(cat "$STAMP" 2>/dev/null)" = "$VER" ]; then
  log "shipped toolchain ${VER} already cached at $CACHE"
else
  ZIP="$DEV_ROOT/dl/Nmkoder-${VER}-win-x64.zip"
  URL="https://github.com/jkkma/nmkoder/releases/download/v${VER}/Nmkoder-${VER}-win-x64.zip"
  mkdir -p "$DEV_ROOT/dl"
  if [ -s "$ZIP" ]; then
    log "using the already-downloaded $ZIP"
  else
    log "downloading the ${VER} win-x64 release (~485 MB) - the exact binaries users get"
    # A progress bar only where somebody is watching; captured output otherwise fills with it.
    PROGRESS="-sS"; [ -t 2 ] && PROGRESS="--progress-bar"
    if ! curl -fL --retry 3 --retry-delay 2 $PROGRESS -o "$ZIP.part" "$URL"; then
      log "download failed: $URL"
      rm -f "$ZIP.part"
    else
      mv "$ZIP.part" "$ZIP"
    fi
  fi

  if [ -s "$ZIP" ]; then
    # Only bin/ is wanted: the zip also carries the 277 MB single-file exe and the pdbs. The entry
    # names are forward-slash (checked on 2.8.66). Windows' own tar.exe - bsdtar, in System32 on
    # every supported Windows - reads zips and takes a directory pattern as "this and everything
    # under it"; Git Bash's unzip does *not*, its `*` stopping at `/` on this build (measured:
    # `unzip zip 'Nmkoder/bin/*'` yields bin/'s top-level files and none of av1an/), so it is not
    # used. Extract beside the cache and swap only once the tree is complete, so a failed or partial
    # extraction cannot leave a half-cache and a re-run finds either the old complete one or nothing.
    TMP="$DEV_ROOT/bin.new"
    TAR="$(cygpath -u "${SYSTEMROOT:-C:/Windows}")/System32/tar.exe"
    rm -rf "$TMP"; mkdir -p "$TMP"
    if "$TAR" -xf "$(cygpath -w "$ZIP")" -C "$(cygpath -w "$TMP")" Nmkoder/bin 2>"$DEV_ROOT/extract.log" \
      && toolchain_complete "$TMP/Nmkoder/bin"; then
      rm -rf "$CACHE"
      mv "$TMP/Nmkoder/bin" "$CACHE" && printf '%s' "$VER" > "$STAMP"
      rm -rf "$TMP" "$ZIP" "$DEV_ROOT/extract.log"
      # ~485 MB shows the win-x64 job bundled grav1synth; ~420 MB is the shape that did not
      # (2.8.65). Say which this one is rather than leaving it to be found from the tab.
      if [ -f "$CACHE/grav1synth.exe" ]; then
        log "shipped toolchain ${VER} cached at $CACHE (grav1synth included)"
      else
        log "shipped toolchain ${VER} cached at $CACHE - NOTE: no grav1synth in this release; pin an older one with NMKODER_TOOLS_VERSION if the Film Grain utility matters"
      fi
    else
      log "extracting a complete bin/ from $ZIP failed - see $DEV_ROOT/extract.log; is the download whole? (delete the zip to re-download)"
      rm -rf "$TMP"
      FAILED=1
    fi
  fi
fi

# ---------------------------------------------------------------------------------------
# Stage the cache into every build output beside an Nmkoder.exe
# ---------------------------------------------------------------------------------------
# Hardlinks (`cp -al`), so the outputs and the cache are one set of bytes; a plain copy if the
# volume refuses (a cache relocated to another drive). Each top-level entry of the cache replaces
# what was staged before, which is what makes a re-run after a toolchain update idempotent - except
# the entries the build lays out itself from BinFiles/, which the working tree owns.
stage_into() {
  local outbin="$1" e name mode="linked" n=0
  mkdir -p "$outbin"
  for e in "$CACHE"/* "$CACHE"/.[!.]*; do
    [ -e "$e" ] || continue
    name="$(basename "$e")"
    [ -e "$BINFILES/$name" ] && continue
    rm -rf "${outbin:?}/$name"
    if ! cp -al "$e" "$outbin/" 2>/dev/null; then
      mode="copied"
      rm -rf "${outbin:?}/$name"
      cp -a "$e" "$outbin/" || { log "  could not stage $name into $outbin"; return 1; }
    fi
    n=$((n + 1))
  done
  log "  $mode $n entries into $outbin"
}

STAGED=0
if toolchain_complete; then
  while IFS= read -r exe; do
    stage_into "$(dirname "$exe")/bin" && STAGED=$((STAGED + 1))
  done < <(find "$PROJECT_DIR/Nmkoder/bin" -maxdepth 6 -name Nmkoder.exe 2>/dev/null)
  [ "$STAGED" -gt 0 ] || log "no Nmkoder.exe under Nmkoder/bin to stage into - build first, then re-run"
fi

# ---------------------------------------------------------------------------------------
# 3. User PATH
# ---------------------------------------------------------------------------------------
# Read raw (DoNotExpandEnvironmentNames) and written back with the same value kind, so a PATH
# that carries %VAR% references keeps them; [Environment]::SetEnvironmentVariable would flatten
# those, and setx truncates at 1024 characters. The WM_SETTINGCHANGE broadcast is what lets
# newly launched programs see it without a logoff.
if toolchain_complete && command -v pwsh >/dev/null 2>&1; then
  WIN_CACHE="$(cygpath -w "$CACHE")"
  ADDED="$(pwsh -NoProfile -NonInteractive -Command "
    \$k = Get-Item HKCU:\Environment
    \$raw = [string]\$k.GetValue('Path', '', 'DoNotExpandEnvironmentNames')
    \$kind = try { \$k.GetValueKind('Path') } catch { 'ExpandString' }
    \$parts = @(\$raw -split ';' | Where-Object { \$_ -ne '' })
    \$want = @('$WIN_CACHE', '$WIN_CACHE\av1an', '$WIN_CACHE\av1an\enc', '$WIN_CACHE\av1an\vsynth')
    \$add = @(\$want | Where-Object { \$p = \$_.TrimEnd('\\'); -not (\$parts | Where-Object { \$_.TrimEnd('\\') -ieq \$p }) })
    if (\$add.Count -gt 0) {
      \$new = ((\$parts + \$add) -join ';')
      Set-ItemProperty -Path HKCU:\Environment -Name Path -Value \$new -Type \$kind
      Add-Type -Namespace W -Name N -MemberDefinition '[DllImport(\"user32.dll\", SetLastError=true, CharSet=CharSet.Auto)] public static extern IntPtr SendMessageTimeout(IntPtr h, uint m, UIntPtr w, string l, uint f, uint t, out UIntPtr r);'
      [UIntPtr]\$r = [UIntPtr]::Zero
      [void][W.N]::SendMessageTimeout([IntPtr]0xffff, 0x1A, [UIntPtr]::Zero, 'Environment', 2, 5000, [ref]\$r)
    }
    \$add.Count
  " 2>/dev/null | tr -d '\r' | tail -1)"
  case "$ADDED" in
    0)  log "user PATH already carries the toolchain folders" ;;
    [1-9]*) log "appended $ADDED folder(s) under $WIN_CACHE to the user PATH - open a new shell (and restart Claude Desktop) to see it; undo in Settings > Environment Variables" ;;
    *)  log "could not update the user PATH (pwsh reported nothing) - add $WIN_CACHE, $WIN_CACHE\\av1an, $WIN_CACHE\\av1an\\enc and $WIN_CACHE\\av1an\\vsynth yourself if you want the tools on it" ;;
  esac
fi

# ---------------------------------------------------------------------------------------
# Report what is actually there
# ---------------------------------------------------------------------------------------
SDK="$(dotnet --version 2>/dev/null || echo none)"
FF="$("$CACHE/ffmpeg.exe" -version 2>/dev/null | head -1 | cut -d' ' -f3 || echo none)"
log "ready: .NET SDK ${SDK}, shipped ffmpeg ${FF:-none}, toolchain $(cat "$STAMP" 2>/dev/null || echo none) staged into ${STAGED} build output(s)"

TOOLS=""
for t in ffmpeg ffprobe mkvmerge grav1synth av1an/av1an av1an/enc/SvtAv1EncApp av1an/enc/x264 av1an/enc/x265 av1an/enc/aomenc av1an/enc/vpxenc av1an/vsynth/VSPipe; do
  [ -f "$CACHE/$t.exe" ] && TOOLS="$TOOLS $(basename "$t")"
done
log "toolkit:${TOOLS:- none}"

[ "$SDK" = none ] && log "WARNING: no .NET SDK on PATH - builds will fail"
toolchain_complete || { log "WARNING: the shipped toolchain is not staged - the app has no encoders, av1an or ffmpeg of its own"; FAILED=1; }

exit "$FAILED"
