#!/usr/bin/env bash
# SessionStart hook: one `git pull --ff-only` of the checked-out branch, and nothing else.
#
# The user works on a laptop and a desktop in tandem, so a session opening on whichever machine
# sat idle is opening on a clone the other has already moved past, and the first edit lands on
# stale code. This closes that gap at the moment a session begins. It installs nothing - the
# toolchain is `.claude/setup-windows.sh`'s, run by hand once per machine - and it never merges
# or rebases.
#
# (It used to carry a second, web-container half - unshallowing a snapshotted clone, fast-
# forwarding a stale master ref, writing an env file, reporting the toolchain. Development moved
# off the cloud environments in August 2026 and that half went with them.)
#
# The hook's stdin is a JSON object whose "source" says which SessionStart this is - startup,
# resume, clear or compact. The pull runs for the first three, which are the moments a session
# begins or begins again, and not for compact, which fires mid-turn: a working tree that moves
# under a running edit is the one thing an automatic pull must never do. There is no jq
# guaranteed on a Windows machine, so a sed reads the field, and a source it cannot read is
# treated as a startup - pulling is the safe default; the check exists only to keep compact out.
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
#
# Checked under Git Bash specifically: `core.autocrlf=true` leaves this file CRLF in the working
# copy, which that bash strips transparently - measured, not assumed.
set -euo pipefail

PROJECT_DIR="${CLAUDE_PROJECT_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"

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
