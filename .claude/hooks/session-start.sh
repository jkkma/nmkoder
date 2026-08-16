#!/usr/bin/env bash
# Per-session repair for a Claude Code on the web container: the git ref the environment
# snapshot left stale, and the environment variables the rest of the session wants set. On a
# local machine it does one different thing - a fast-forward pull - and nothing else; see the
# block below.
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

PROJECT_DIR="${CLAUDE_PROJECT_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"

# A local machine gets exactly one thing from this hook, and it is not the repair below: a
# `git pull --ff-only` of the checked-out branch. The user works on a laptop and a desktop in
# tandem, so a session opening on whichever machine sat idle is opening on a clone the other
# has already moved past, and the first edit lands on stale code. Nothing else here reaches a
# local machine - the ref repair, the env file and the toolchain report are all the web
# container's, and the early return at the end of this block keeps them there.
#
# The hook's stdin is a JSON object whose "source" says which SessionStart this is - startup,
# resume, clear or compact. The pull runs for the first three, which are the moments a session
# begins or begins again, and not for compact, which fires mid-turn: a working tree that moves
# under a running edit is the one thing an automatic pull must never do. There is no jq on a
# Windows machine, so a sed reads the field, and a source it cannot read is treated as a
# startup - pulling is the safe default; the check exists only to keep compact out.
#
# --ff-only is the whole safety story. It never writes a merge commit and never rebases: a
# branch that has diverged, a dirty file the pull would overwrite, no upstream, or no network
# each leave the tree exactly as it was, and the one line printed says which. That line is
# injected into the session's context, so it is what the session reads before touching a
# file - and a session that starts with no `session-start:` line at all is one where the hook
# did not run, which is its own instruction to pull by hand. Every path exits 0: a pull that
# could not happen is a fact to report, not a reason to fail startup. `timeout` bounds a hung
# network under the harness's own 60s hook limit wherever the machine has it (Git for Windows
# and Linux do; a bare macOS does not, and runs unbounded).
if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  INPUT=""
  [ -t 0 ] || INPUT="$(cat 2>/dev/null || true)"
  SOURCE="$(printf '%s\n' "$INPUT" | sed -n 's/.*"source"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n1)"
  [ "$SOURCE" = "compact" ] && exit 0

  git -C "$PROJECT_DIR" rev-parse --is-inside-work-tree >/dev/null 2>&1 || exit 0
  BRANCH="$(git -C "$PROJECT_DIR" symbolic-ref --quiet --short HEAD 2>/dev/null || true)"
  if [ -z "$BRANCH" ]; then
    echo "session-start: HEAD is detached, so nothing was pulled"
    exit 0
  fi
  UPSTREAM="$(git -C "$PROJECT_DIR" rev-parse --abbrev-ref --symbolic-full-name '@{upstream}' 2>/dev/null || true)"
  if [ -z "$UPSTREAM" ]; then
    echo "session-start: ${BRANCH} has no upstream, so nothing was pulled"
    exit 0
  fi

  BOUND=""
  command -v timeout >/dev/null 2>&1 && BOUND="timeout 45"
  BEFORE="$(git -C "$PROJECT_DIR" rev-parse HEAD)"
  # --no-rebase beside --ff-only so the outcome does not depend on anybody's pull.rebase:
  # fast-forward or nothing, whatever the machine's config says.
  if OUT="$(GIT_TERMINAL_PROMPT=0 $BOUND git -C "$PROJECT_DIR" pull --no-rebase --ff-only 2>&1)"; then
    AFTER="$(git -C "$PROJECT_DIR" rev-parse HEAD)"
    if [ "$BEFORE" = "$AFTER" ]; then
      echo "session-start: ${BRANCH} is up to date with ${UPSTREAM}"
    else
      N="$(git -C "$PROJECT_DIR" rev-list --count "${BEFORE}..${AFTER}")"
      echo "session-start: pulled ${N} commit(s) into ${BRANCH} from ${UPSTREAM} (${BEFORE:0:7}..${AFTER:0:7})"
    fi
  else
    # git's own reason, in one line: the first fatal:/error: line where there is one (a
    # diverged branch, a file in the way and an unreachable remote all print one), else the
    # last non-empty line, else the timeout's silence.
    WHY="$(printf '%s\n' "$OUT" | grep -m1 -E '^(fatal|error):' || printf '%s\n' "$OUT" | grep -v '^[[:space:]]*$' | tail -n1 || true)"
    [ -n "$WHY" ] || WHY="no output - the pull timed out"
    echo "session-start: git pull --ff-only did NOT update ${BRANCH} (${WHY}) - the tree is as it was; pull by hand before editing"
  fi
  exit 0
fi

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
