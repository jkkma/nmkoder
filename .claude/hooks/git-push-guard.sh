#!/usr/bin/env bash
# PreToolUse guard for the one git failure a web session cannot push through: the sandbox's
# git proxy takes an ordinary branch push and hangs up on anything else. A tag push and a
# remote branch deletion both die with
#
#   send-pack: unexpected disconnect while reading sideband packet
#   fatal: the remote end hung up unexpectedly
#
# every time, so retrying with backoff only burns time - it is a property of the sandbox, not
# of the repository or GitHub. CLAUDE.md's release section says so in prose; this hook says it
# at the moment the command is about to run, before the first hang instead of after the
# fourth. The release workflow's dispatch path exists precisely for the tag case and creates
# the tag itself; a finished branch's remote ref can only be deleted from the branches page.
#
# Local machines are left alone: tag pushes work there, and the guard would only get in the
# way. CLAUDE_CODE_REMOTE is the same switch session-start.sh reads.
set -uo pipefail

# Anything other than a deny must end in exit 0 with no output: a PreToolUse hook that fails
# blocks every Bash call behind it, which is far worse than the hang it guards against.
allow() { exit 0; }

[ "${CLAUDE_CODE_REMOTE:-}" = "true" ] || allow
command -v jq >/dev/null 2>&1 || allow

CMD="$(jq -r '.tool_input.command // empty' 2>/dev/null)" || allow
[ -n "$CMD" ] || allow

# Only commands that reach `git ... push` are of interest. The loose match (anything between
# the two words) is deliberate: `git -C /path push` and compound `git add && git push` both
# have to land here, and a rare false hit only costs the checks below, which are narrow.
printf '%s' "$CMD" | grep -Eq '(^|[^[:alnum:]_])git[^|;&]*[[:space:]]push([[:space:]]|$)' || allow

deny() {
  jq -n --arg reason "$1" '{
    hookSpecificOutput: {
      hookEventName: "PreToolUse",
      permissionDecision: "deny",
      permissionDecisionReason: $reason
    }
  }'
  exit 0
}

REASON_TAIL="It fails identically on every retry ('send-pack: unexpected disconnect while reading sideband packet'), so do not retry with backoff."

# Tag pushes: --tags/--follow-tags, refs/tags/..., or a bare vX.Y[.Z] refspec.
if printf '%s' "$CMD" | grep -Eq -- '--(follow-)?tags([[:space:]]|$)|refs/tags/|push[^|;&]*[[:space:]]v[0-9]+\.[0-9]+(\.[0-9]+)?([[:space:]]|$)'; then
  deny "The sandbox's git proxy hangs up on tag pushes - only ordinary branch pushes go through. $REASON_TAIL Dispatch .github/workflows/release.yml instead (mcp__github__actions_run_trigger, method run_workflow, ref master, inputs version=X.Y.Z and publish=true) - it creates the tag itself. See CLAUDE.md, 'Cutting a release'."
fi

# Remote ref deletions: --delete/-d after push, or an empty-source refspec like `origin :branch`.
# `HEAD:master` is an ordinary push (the colon has a source in front of it) and must pass.
if printf '%s' "$CMD" | grep -Eq -- 'push[^|;&]*[[:space:]](--delete|-d)([[:space:]]|$)|push[^|;&]*[[:space:]]\+?:[[:graph:]]'; then
  deny "The sandbox's git proxy hangs up on remote ref deletions, the same way it does on tag pushes. $REASON_TAIL There is no workaround from a web session: delete the local branch only and leave the remote ref alone (it can only be removed from the repository's branches page by hand). Per CLAUDE.md, do not report the undeleted remote branch to the user, and never claim it was deleted."
fi

allow
