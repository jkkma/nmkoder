#!/usr/bin/env bash
# PreToolUse(Bash|PowerShell) hook: ask before a commit or push whose text would identify the user.
#
# The rule is the user's own, set 16 August 2026, for a public repository: nothing that ties the
# repo to the person rather than to the GitHub identity - the Windows username, which sits in
# every absolute path on both machines and so arrives in pasted harness output, log excerpts and
# command lines; the hostname; a personal email address. A pushed commit is permanent and
# indexed, so this is a check-before-commit rule, and until now it lived in a memory file that a
# session has to remember to apply. A hook applies it whether or not anything remembered, and it
# fires for a subagent's commands too, since those pass through the same event.
#
# What this file does NOT contain is the strings themselves. It is tracked, so spelling the
# username here would commit the very thing the hook exists to keep out. The username and the
# hostname are read from the environment at run time - USERNAME and COMPUTERNAME are set in every
# Git Bash on Windows and inherited by the harness; on a machine where one is unset that half of
# the check simply does not run. Anything else - a personal address, a handle - goes in
# ~/.nmkoder-dev/identifying-patterns, one extended regex per line, blank lines and # comments
# ignored, CRLF tolerated. It lives beside the staged toolchain for the reason the toolchain
# does: ~/.nmkoder-dev is outside the repo and outside Claude Desktop's write virtualization, so
# a file put there from a session is one the user's own shell can see. No file, no extra
# patterns; the setup script does not create it because it cannot know what to put in it, and it
# is per machine. The one pattern spelled out is the shape of a Windows profile path with a name
# in it - [drive]:\Users\<name>\ - which never belongs in a commit whatever the name; a path with
# the name elided (`C:/Users/…`, `C:\Users\<you>\`) does not match it, so the record's own
# examples of the shape stay quiet.
#
# Where it looks. For `git commit`: the staged diff, plus the working-tree diff when the command
# carries -a/--all (which stages at commit time), plus the command's own text with absolute paths
# removed from it - so a message typed with the name in it is caught, while a `git -C <absolute
# path> commit`, which the Bash tool's prefer-absolute-paths habit produces, is not asked about
# every time. For `git push`: everything the push would publish - the diff and the messages from
# the upstream to HEAD, or from origin/master when the branch has no upstream yet, as on a first
# `push -u`. For `gh pr create/edit` and `gh release create/edit`: the command text, since the body
# is typed there. Only ADDED lines are grepped (^\+), so a line already in the tree cannot re-trip
# it and a deletion is never reported. Case-insensitive, because capitalisation is not what makes
# a name identifying.
#
# It asks rather than denies: "ask" puts the matching lines in front of the user, who may know
# a hit is a false positive - answer yes and it goes through. Every path exits 0; a hook that
# cannot read its input, or a git that will not answer, is not a reason to block a commit.
#
# The command is read out of the tool input with grep/sed rather than jq (none on a Windows
# machine), and the JSON string is matched in full - (\\.|[^"\\])* - so a command containing
# quotes is not cut off at the first one, which the file_path grep in warn-if-gitignored.sh can
# afford and this one cannot: `git -C "…" commit` would otherwise lose its verb. The cwd is read
# the same way, so a commit made in a worktree is checked against that worktree's index rather
# than the main checkout's. The verb match allows up to three plain tokens between `git` and
# `commit` (`-C path`, `-c key=val`) and none containing | ; or &, so `git log | grep commit` is
# not a commit.
#
# Limits, stated rather than implied: a commit made by a script this cannot see through
# (`bash commit.sh`), a `git -C <elsewhere>` pointing at another repository, a path pasted into
# a commit message in its full form (the path stripping above takes it out before the scan), and
# text typed by hand into a GitHub form are all outside it. So is anything committed from the
# user's own shell. Verified by running it against a throwaway repo under Git Bash - see
# .claude/README.md for the cases.
set -uo pipefail

PROJECT_DIR="${CLAUDE_PROJECT_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"

INPUT=""
[ -t 0 ] || INPUT="$(cat 2>/dev/null || true)"
[ -n "$INPUT" ] || exit 0

# One JSON string value by key, unescaped just enough to read: \" and \\ only.
json_str() {
  printf '%s' "$INPUT" \
    | grep -o -E "\"$1\"[[:space:]]*:[[:space:]]*\"(\\\\.|[^\"\\\\])*\"" \
    | head -n1 \
    | sed -E "s/^\"$1\"[[:space:]]*:[[:space:]]*\"//; s/\"\$//; s/\\\\\"/\"/g; s/\\\\\\\\/\\\\/g"
}

CMD="$(json_str command)"
[ -n "$CMD" ] || exit 0

# git <up to three plain tokens> <verb>, where a plain token has no | ; or & in it.
has_git_verb() {
  printf '%s' "$CMD" | grep -q -E "(^|[^[:alnum:]_.-])git[[:space:]]+([^[:space:]|;&]+[[:space:]]+){0,3}$1([[:space:]]|$)"
}
KIND=""
has_git_verb commit && KIND="commit"
has_git_verb push && KIND="${KIND:+$KIND+}push"
printf '%s' "$CMD" | grep -q -E "(^|[^[:alnum:]_.-])gh[[:space:]]+(pr|release)[[:space:]]+(create|edit)([[:space:]]|$)" && KIND="${KIND:+$KIND+}gh"
[ -n "$KIND" ] || exit 0

CWD="$(json_str cwd | tr '\\' '/')"
[ -n "$CWD" ] && [ -d "$CWD" ] || CWD="$PROJECT_DIR"
git -C "$CWD" rev-parse --is-inside-work-tree >/dev/null 2>&1 || exit 0
gitq() { git -C "$CWD" "$@" 2>/dev/null; }

# The patterns: a profile path with a real name in it, the machine's own two names, the file.
esc_re() { printf '%s' "$1" | sed 's/[][\.*^$+?(){}|/]/\\&/g'; }
PAT='[A-Za-z]:[\\/]+Users[\\/]+[A-Za-z0-9_.-]+[\\/]'
[ -n "${USERNAME:-}" ] && PAT="$PAT|$(esc_re "$USERNAME")"
[ -n "${COMPUTERNAME:-}" ] && PAT="$PAT|$(esc_re "$COMPUTERNAME")"
PFILE="${HOME:-${USERPROFILE:-}}/.nmkoder-dev/identifying-patterns"
if [ -f "$PFILE" ]; then
  EXTRA="$(tr -d '\r' < "$PFILE" | grep -v -E '^[[:space:]]*(#|$)' | paste -sd'|' - 2>/dev/null || true)"
  [ -n "$EXTRA" ] && PAT="$PAT|$EXTRA"
fi

# Absolute paths out of a command line before it is scanned: a message is checked, a -C is not.
strip_paths() { sed -E "s#[A-Za-z]:[\\\\/][^[:space:]\"']*##g; s#/[a-z]/[Uu]sers/[^[:space:]\"']*##g"; }

# What would be published, every line of it prefixed + so one grep serves all of it.
TEXT=""
case "$KIND" in *commit*)
  TEXT="$(gitq diff --cached --no-color --no-ext-diff)"
  if printf '%s' "$CMD" | grep -q -E -- '(^|[[:space:]])(-a|--all|-[a-zA-Z]*a[a-zA-Z]*)([[:space:]]|$)'; then
    TEXT="$TEXT
$(gitq diff --no-color --no-ext-diff)"
  fi
  ;;
esac
case "$KIND" in *push*)
  RANGE=""
  UP="$(gitq rev-parse --abbrev-ref --symbolic-full-name '@{upstream}')"
  if [ -n "$UP" ]; then RANGE="$UP...HEAD"
  elif gitq rev-parse --verify -q origin/master >/dev/null; then RANGE="origin/master...HEAD"
  fi
  if [ -n "$RANGE" ]; then
    TEXT="$TEXT
$(gitq diff --no-color --no-ext-diff "$RANGE")
$(gitq log --format=%B "$RANGE" | sed 's/^/+/')"
  fi
  ;;
esac
TEXT="$TEXT
+$(printf '%s' "$CMD" | strip_paths)"

HITS="$(printf '%s\n' "$TEXT" | grep -i -E "^\+.*($PAT)" | grep -v -E '^\+\+\+ ' | head -n 6 | cut -c1-160 | tr -d '\000-\037')"
[ -n "$HITS" ] || exit 0
N="$(printf '%s\n' "$HITS" | grep -c .)"

REASON="This $KIND would publish text that identifies the user - $N added line(s) match the username, the hostname, a profile path with a name in it, or a pattern from ~/.nmkoder-dev/identifying-patterns. First matches: $(printf '%s' "$HITS" | tr '\n' ' ' | sed 's/  */ /g') Scrub them to ~, %USERPROFILE% or a repo-relative path first (the rule is in CLAUDE.md and the never-commit-identifying-info memory). If this is a false positive, answer yes and it goes through."

esc() { printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'; }
printf '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"ask","permissionDecisionReason":"%s"}}\n' "$(esc "$REASON")"
exit 0
