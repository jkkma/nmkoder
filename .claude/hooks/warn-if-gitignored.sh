#!/usr/bin/env bash
# PreToolUse(Write) hook: ask before creating a file git has been told to ignore.
#
# The .gitignore here is the stock Visual Studio one, which ignores [Bb]in/, [Oo]bj/, [Ll]og/,
# [Ll]ogs/, [Dd]ebug/ and [Rr]elease/ - and this project uses every one of those words for
# something of its own. `bin/` is where the app looks for its bundled tools, which is why the
# tracked copies live under `BinFiles/`; `logs/` is what the app writes beside the exe. The
# collision has already cost one silent loss: the release skill was first written to
# `.claude/skills/release/`, matched `.gitignore:22`, and was gone by the time anything looked
# for it. Measured against this repo rather than remembered:
#
#   $ git check-ignore -v .claude/skills/release/SKILL.md
#   .gitignore:22:[Rr]elease/     .claude/skills/release/SKILL.md
#
# The failure mode is the bad kind - the write succeeds, nothing errors, `git status` says
# nothing, and the file is simply absent from the commit. So this asks first, naming the rule
# and the line of .gitignore that matched, which is the one piece of information that makes the
# surprise legible.
#
# It stands down for a file git already tracks: a force-added path is somebody's deliberate
# choice, and editing it is not the mistake this exists to catch.
#
# What it does NOT cover, on purpose: a file written by a shell heredoc through the Bash tool,
# which no PreToolUse matcher on Write can see. Widening the matcher to Bash would mean parsing
# arbitrary shell for output paths, which is guesswork; the case this catches - creating a new
# directory under .claude/ or the repo root whose name collides with a build pattern - is
# overwhelmingly a Write.
#
# There is no jq guaranteed on a Windows machine, so the JSON is read with grep/sed, exactly as
# hooks/session-start.sh reads its "source" field. `grep -o` takes the FIRST "file_path" match
# rather than sed's greedy last one, and a `"file_path"` appearing inside the written content
# cannot match: JSON escapes the quotes there, so it arrives as \"file_path\" and the pattern
# requires a bare closing quote. JSON also doubles every backslash, so a Windows path arrives as
# C:\\Users\\...; `tr -s` both undoubles and slashifies in one pass, which is safe because a
# backslash cannot be data in a path that carries any.
#
# Every path exits 0. A hook that cannot read its input, or a git that will not answer, is not a
# reason to block a write.
set -uo pipefail

PROJECT_DIR="${CLAUDE_PROJECT_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"

INPUT=""
[ -t 0 ] || INPUT="$(cat 2>/dev/null || true)"
[ -n "$INPUT" ] || exit 0

RAW="$(printf '%s' "$INPUT" \
  | grep -o '"file_path"[[:space:]]*:[[:space:]]*"[^"]*"' \
  | head -n1 \
  | sed 's/^"file_path"[[:space:]]*:[[:space:]]*"//; s/"$//')" || true
[ -n "$RAW" ] || exit 0

GITPATH="$RAW"
case "$RAW" in
  *\\*) GITPATH="$(printf '%s' "$RAW" | tr -s '\\' '/')" ;;
esac

git -C "$PROJECT_DIR" rev-parse --is-inside-work-tree >/dev/null 2>&1 || exit 0
# Already tracked: somebody force-added it, and that was a decision.
git -C "$PROJECT_DIR" ls-files --error-unmatch -- "$GITPATH" >/dev/null 2>&1 && exit 0

# check-ignore exits 1 for "not ignored" and 128 for a path outside the repo. Both are silence.
MATCH="$(git -C "$PROJECT_DIR" check-ignore -v -- "$GITPATH" 2>/dev/null)" || exit 0
[ -n "$MATCH" ] || exit 0

# `.gitignore:22:[Rr]elease/<tab><path>` -> source file, line number, pattern
FIELD="$(printf '%s' "$MATCH" | head -n1 | cut -f1)"
SRC="$(printf '%s' "$FIELD" | cut -d: -f1)"
LINE="$(printf '%s' "$FIELD" | cut -d: -f2)"
PAT="$(printf '%s' "$FIELD" | cut -d: -f3-)"

REASON="git is set to ignore this path: ${SRC} line ${LINE} matches it with \`${PAT}\`. The file would be written and then silently left out of every commit - no error, and nothing in git status. This is the stock Visual Studio .gitignore meeting a project that uses bin/, logs/, debug/ and release/ for its own things; it already swallowed .claude/skills/release/ once, which is why that skill is called cut-release. Write it somewhere tracked, or pick a name that clears the pattern."

# JSON-escape backslashes first, then quotes.
esc() { printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'; }

printf '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"ask","permissionDecisionReason":"%s"}}\n' "$(esc "$REASON")"
exit 0
