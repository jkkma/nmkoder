#!/usr/bin/env bash
# Per-session repair for a Claude Code on the web container: the git ref the environment
# snapshot left stale, and the environment variables the rest of the session wants set.
#
# **It installs nothing.** The toolchain - the .NET SDK and the FFmpeg build this project
# measures against - belongs to the environment's setup script, `.claude/setup.sh`, which runs
# once when the environment is created and is snapshotted with it. That is the only place
# either is installed now. This hook used to carry a second copy of the SDK install as a
# fallback, on the grounds that a setup script fails silently; the duplication is gone at the
# user's request, and what is left of that argument is the check at the bottom, which reports
# a missing toolchain rather than papering over it. A session that starts without one says so
# in its first line instead of failing at the first build.
set -euo pipefail

# Only the web containers need any of this; leave local machines alone.
if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

PROJECT_DIR="${CLAUDE_PROJECT_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"

# Everything below logs here rather than to the terminal. A SessionStart hook's output is
# injected into the model's context, so only the summary and real failures are printed.
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
#
# This half cannot move to the setup script, and that is the point of keeping the hook: the
# staleness *is* the snapshot ageing, so it has to be repaired per session rather than once.
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

# Keep the build quiet for the rest of the session; the release workflow opts out of
# Avalonia's telemetry the same way. SessionStart also fires on resume, clear and compact,
# so only add what is not in the file already.
if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
  for var in DOTNET_CLI_TELEMETRY_OPTOUT DOTNET_NOLOGO AVALONIA_TELEMETRY_OPTOUT; do
    grep -q "^export ${var}=" "$CLAUDE_ENV_FILE" 2>/dev/null && continue
    echo "export ${var}=1" >> "$CLAUDE_ENV_FILE"
  done
fi

# One line for the whole hook on the path where nothing went wrong, and a named problem where
# something did. Read back what is actually on PATH rather than trusting that the setup script
# ran: it ends in a zero exit whatever happened to it, so a failure there is invisible from
# here and this is the only place it surfaces.
SDK="$(dotnet --version 2>/dev/null || true)"
FF="$(ffmpeg -version 2>/dev/null | head -1 | cut -d' ' -f3 || true)"

if [ -n "$SDK" ] && [ -n "$FF" ]; then
  echo "session-start: .NET SDK ${SDK}, ffmpeg ${FF}"
else
  if [ -z "$SDK" ]; then
    echo "session-start: no .NET SDK on PATH - builds will fail. Run .claude/setup.sh, or fix the environment's setup script"
  fi
  if [ -z "$FF" ]; then
    echo "session-start: no ffmpeg on PATH - anything measured against it cannot run. Run .claude/setup.sh"
  fi
fi

# Explicitly, and not as a formality. Written as `[ -z "$SDK" ] && echo …` the branch above
# reads fine and ends the script on a *false* test whenever that tool is present, which under
# `set -e` is an exit status of 1 - a hook reporting a broken environment by failing session
# startup, which is the one moment it must not. Measured: the missing-SDK path exited 1 before
# this line existed. Keep the `if` blocks and keep this.
exit 0
