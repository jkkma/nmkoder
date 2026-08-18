#!/usr/bin/env bash
# Stop hook: say so when the branch has commits the other machine cannot see yet.
#
# This is the other end of hooks/session-start.sh. That one closes the gap at the moment a
# session begins - a session opening on whichever machine sat idle is opening on a clone the
# other has already moved past - by fast-forwarding the checked-out branch. But the gap has two
# ends and only one of them was closed: nothing notices when work *finishes* here and is never
# pushed, which is precisely the state that makes the other machine's next pull a no-op over
# stale code. The laptop and the desktop are worked in tandem, so that state is not rare.
#
# One local command, `git rev-list --count @{u}..HEAD`, and no network: this must never be the
# reason a turn feels slow, and a fetch would make it one.
#
# It is silent at zero, which is the normal state, and silent again at a count it has already
# reported for this session - so it speaks once when a commit lands rather than on every turn
# from then on, which is what would make it noise. The marker lives in .git/ (a real path from
# `git rev-parse --git-dir`, so a worktree gets its own), keyed on the session id, and is
# cleared as soon as the count comes back to zero so a later commit reports again. Stale markers
# from old sessions are swept on the way past - they are a few bytes each, but nothing else
# would ever remove them.
#
# It reports rather than pushes. A push is a decision, and a hook that made it would be
# publishing work at the end of every turn.
#
# `systemMessage` is the channel: it puts the line in front of the user without blocking the
# stop or feeding anything back to the model. Every path exits 0.
set -uo pipefail

PROJECT_DIR="${CLAUDE_PROJECT_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"

INPUT=""
[ -t 0 ] || INPUT="$(cat 2>/dev/null || true)"
SID="$(printf '%s' "$INPUT" \
  | grep -o '"session_id"[[:space:]]*:[[:space:]]*"[^"]*"' \
  | head -n1 \
  | sed 's/^"session_id"[[:space:]]*:[[:space:]]*"//; s/"$//')" || true
# A session id is a uuid, but nothing here should be able to write outside .git/ on a surprise.
SID="$(printf '%s' "${SID:-nosession}" | tr -c 'A-Za-z0-9._-' '_')"

# One rev-parse for all three, because this runs at the end of every turn and a git process is
# ~30 ms on this machine. It exits 128 as a whole for the cases that should end this hook
# anyway - not a repository, a detached HEAD, or a branch with no upstream - so the three
# separate guards that used to stand here are the same single check.
# Read line by line rather than splitting one line on whitespace: the git dir is a path, and a
# clone under a directory with a space in it would otherwise land in the wrong variable.
{ read -r GITDIR; read -r BRANCH; read -r UPSTREAM; } <<EOF
$(git -C "$PROJECT_DIR" rev-parse --absolute-git-dir --abbrev-ref HEAD --abbrev-ref '@{upstream}' 2>/dev/null)
EOF
[ -n "${GITDIR:-}" ] && [ -n "${BRANCH:-}" ] && [ -n "${UPSTREAM:-}" ] || exit 0

MARKER="${GITDIR}/claude-unpushed-${SID}"

AHEAD="$(git -C "$PROJECT_DIR" rev-list --count "${UPSTREAM}..HEAD" 2>/dev/null)" || exit 0
case "$AHEAD" in ''|*[!0-9]*) exit 0 ;; esac

if [ "$AHEAD" -eq 0 ]; then
  rm -f "$MARKER" 2>/dev/null || true
  exit 0
fi

# Sweep markers left by sessions that have since ended; only this session's is live.
find "$GITDIR" -maxdepth 1 -name 'claude-unpushed-*' -mtime +7 -delete 2>/dev/null || true

LAST="$(cat "$MARKER" 2>/dev/null || true)"
[ "$LAST" = "$AHEAD" ] && exit 0
printf '%s' "$AHEAD" > "$MARKER" 2>/dev/null || true

if [ "$AHEAD" -eq 1 ]; then N="1 commit"; else N="${AHEAD} commits"; fi
MSG="${BRANCH} is ${N} ahead of ${UPSTREAM} - push before switching machines, or the other one's session-start pull has nothing to fast-forward to."

esc() { printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'; }
printf '{"systemMessage":"%s"}\n' "$(esc "$MSG")"
exit 0
