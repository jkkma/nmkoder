#!/usr/bin/env bash
# Installs the .NET SDK Nmkoder targets into a Claude Code on the web container, so
# "dotnet build Nmkoder.sln -c Release" works from the first prompt of a session.
#
# The SDK comes from the Ubuntu archive rather than the usual dot.net installer script.
# That script redirects to builds.dotnet.microsoft.com, which the sandbox's egress proxy
# refuses with a 403, so the download fails before it starts. The archive is reachable
# and its dotnet-sdk-10.0 package carries the same SDK.
#
# An environment's setup script is the better place for that install - it runs once and is
# snapshotted, where this runs on every session - so the environment behind this repository
# carries one. The install stays here anyway, because a setup script covers one environment
# and has to end in "|| true" (a non-zero exit fails session startup), so a failed one is
# silent. This is what a contributor's environment, a routine, or a broken setup script
# falls back to.
set -euo pipefail

# Only the web containers start out without .NET; leave local machines to their own install.
if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

PROJECT_DIR="${CLAUDE_PROJECT_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"

# Follow the project's own target framework, so a TFM bump does not leave this behind.
MAJOR="$(sed -n 's/.*<TargetFramework>net\([0-9][0-9]*\)\..*/\1/p' "$PROJECT_DIR/Nmkoder/Nmkoder.csproj" | head -1)"
MAJOR="${MAJOR:-10}"

# Containers run as root; keep working if some other environment does not. -E rides along
# with sudo so DEBIAN_FRONTEND survives into apt.
SUDO=""
[ "$(id -u)" -eq 0 ] || SUDO="sudo -E"

export DEBIAN_FRONTEND=noninteractive
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

# Everything below logs here rather than to the terminal. A SessionStart hook's output is
# injected into the model's context, and apt alone is ninety lines of Get:/Unpacking noise
# spent before the first prompt, so only the summary and real failures are printed.
LOG="$(mktemp -t session-start.XXXXXX.log)"

# The container's clone is part of the environment snapshot and is reused by every session
# taken from it, so the local master ref is as old as that snapshot - a week and 111 commits
# in the case that prompted this - while the session's own branch is checked out at the
# current tip. Nothing else moves it, and it is only ever noticed at merge time.
#
# The clone is also shallow, which is the worse half. Ancestry across a graft boundary answers
# "no", so a master that is merely behind reads as divergent, `git merge --ff-only` refuses it,
# and `git log origin/master` is a truncated list that a search through it then misses commits
# in - all three of which happened, and led to a stale ref being reported as a rewritten
# history. Unshallowing costs one fetch per container and settles it.
if [ -d "$PROJECT_DIR/.git" ]; then
  # --unshallow is an error on a complete repository rather than a no-op, hence the guard.
  [ -f "$PROJECT_DIR/.git/shallow" ] \
    && { git -C "$PROJECT_DIR" fetch --quiet --unshallow origin >>"$LOG" 2>&1 || true; }
  git -C "$PROJECT_DIR" fetch --quiet origin master >>"$LOG" 2>&1 || true

  # Only where master is not what is checked out, and only where it is strictly behind. A
  # master carrying commits of its own is somebody's unpushed work and is left exactly where
  # it is; fast-forwarding a ref nothing is standing on cannot touch the working tree.
  HEAD_BRANCH="$(git -C "$PROJECT_DIR" symbolic-ref --quiet --short HEAD || true)"
  if [ "$HEAD_BRANCH" != "master" ] \
    && git -C "$PROJECT_DIR" merge-base --is-ancestor master origin/master 2>/dev/null \
    && [ "$(git -C "$PROJECT_DIR" rev-parse master)" != "$(git -C "$PROJECT_DIR" rev-parse origin/master)" ]; then
    BEHIND="$(git -C "$PROJECT_DIR" rev-list --count master..origin/master)"
    git -C "$PROJECT_DIR" update-ref refs/heads/master origin/master
    echo "session-start: local master was ${BEHIND} commits behind, fast-forwarded to origin/master"
  fi
fi

if ! dotnet --list-sdks 2>/dev/null | grep -q "^${MAJOR}\."; then
  # Reaching here means the setup script did not run or did not work. Say so, since the
  # install it should have made free is now being paid for at the start of this session.
  echo "session-start: .NET ${MAJOR} SDK missing, installing it (the setup script did not)"
  # The preloaded package index points at .debs the mirror has already superseded, so
  # every download 404s without this refresh. Blocked third-party PPAs only warn.
  $SUDO apt-get update -qq >>"$LOG" 2>&1 || true
  $SUDO apt-get install -y "dotnet-sdk-${MAJOR}.0" >>"$LOG" 2>&1 \
    || echo "session-start: installing the SDK failed, output in $LOG"
fi

# Restore now so the packages land in the container's NuGet cache while it is still being
# built, rather than on the first build of every session.
dotnet restore "$PROJECT_DIR/Nmkoder.sln" >>"$LOG" 2>&1 \
  || echo "session-start: restoring packages failed, output in $LOG"

# Keep the build quiet for the rest of the session; the release workflow opts out of
# Avalonia's telemetry the same way. SessionStart also fires on resume, clear and compact,
# so only add what is not in the file already.
if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
  for var in DOTNET_CLI_TELEMETRY_OPTOUT DOTNET_NOLOGO AVALONIA_TELEMETRY_OPTOUT; do
    grep -q "^export ${var}=" "$CLAUDE_ENV_FILE" 2>/dev/null && continue
    echo "export ${var}=1" >> "$CLAUDE_ENV_FILE"
  done
fi

# One line for the whole hook on the path where nothing went wrong. Report what is actually
# there rather than assuming the install above worked, so a broken session says it is broken
# instead of announcing a version it does not have.
VERSION="$(dotnet --version 2>/dev/null || true)"
if [ -n "$VERSION" ]; then
  echo "session-start: .NET SDK ${VERSION} ready"
else
  echo "session-start: no .NET SDK on PATH, builds will fail - output in $LOG"
fi
