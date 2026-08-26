#!/bin/sh
# Poll a release workflow run until it finishes. Prints one line per poll so it is
# visibly alive, and fails loudly rather than spinning when the run cannot be read.
#
#   watch-run.sh <run-id> [poll-seconds] [max-polls]
#
# Exit: 0 success · 1 the run concluded non-success · 2 timed out still running
#       3 the run could not be read repeatedly (the poller's own failure, not the run's)
#
# THREE TRAPS THIS EXISTS TO AVOID, each of which cost a release session real time:
#
# 1. **A poller that fails every iteration looks exactly like one that is waiting.** The
#    first version errored on all 22 of its polls and kept going, reporting nothing, while
#    the run had long since gone green. Consecutive failures are counted and abort the loop.
#
# 2. **Never round-trip the JSON through `echo`.** A `j=$(curl ...); echo "$j" | jq` turns
#    every \n inside a JSON string - commit messages are full of them - into a real newline
#    under any shell whose echo expands escapes, and jq dies on "control characters must be
#    escaped". Stripping control characters is NOT the fix; the response contains none and
#    the shell was creating them. Nothing here builds a JSON string in a variable at all.
#
# 3. **Do not swallow the reader's stderr, and do not assume the tools are there.** The
#    version this replaces ran `curl -fsSL "$API" | jq -r '…' 2>/dev/null`, and on a machine
#    with no jq installed that is: jq's "command not found" discarded by the redirect, curl
#    left writing into a pipe nobody reads, and the only visible symptom
#    `curl: (23) Failure writing output to destination, passed 1370 returned 1273` on every
#    poll. It then announced "the API is unreachable" - about a missing package, with the
#    network and the API both perfectly fine. Measured on the user's own desktop mid-release:
#    curl alone fetched the run in one call, and `command -v jq` came back empty.
#
#    So the dependency is `gh`, which the release procedure already requires for the dispatch
#    and the verification and which carries its own jq (`--jq` is gojq, built in - no external
#    binary), it is checked for before the loop rather than discovered inside it, and a failed
#    poll prints what the tool actually said instead of a guess about why.
set -u

RUN="${1:?usage: watch-run.sh <run-id> [poll-seconds] [max-polls]}"
EVERY="${2:-30}"
MAX="${3:-60}"
REPO="${NMKODER_REPO:-jkkma/nmkoder}"

# Before the loop, because a missing tool is not a transient failure and retrying it five
# times over two and a half minutes only delays saying so.
if ! command -v gh >/dev/null 2>&1; then
    echo "gh is not on PATH, and this watches the run through it - the same gh the dispatch and"
    echo "the release verification use. Install GitHub CLI, or watch the run with:"
    echo "  https://github.com/$REPO/actions/runs/$RUN"
    exit 3
fi

err=$(mktemp 2>/dev/null || echo "${TMPDIR:-/tmp}/watch-run.$$")
trap 'rm -f "$err"' EXIT

i=0
fails=0

while [ "$i" -lt "$MAX" ]; do
    # gh's own --jq, so there is no external jq to be missing and no shell variable holding
    # JSON. stderr goes to a file rather than to /dev/null: it is the only thing that says
    # why a poll failed, which is the whole of trap 3.
    if line=$(gh run view "$RUN" --repo "$REPO" --json status,conclusion \
                --jq '"\(.status) \(.conclusion // "-")"' 2>"$err"); then
        [ -n "$line" ] || line=""
    else
        line=""
    fi

    if [ -z "$line" ]; then
        fails=$((fails + 1))
        echo "$(date +%H:%M:%S)  poll $i: could not read the run ($fails in a row): $(head -n 1 "$err")"

        if [ "$fails" -ge 5 ]; then
            echo "GIVING UP: 5 consecutive failures reading run $RUN of $REPO. The run's real state is"
            echo "UNKNOWN - this is the poller failing, not the run. What gh last said is above."
            exit 3
        fi
    else
        fails=0
        status=${line% *}
        conclusion=${line#* }
        echo "$(date +%H:%M:%S)  poll $i: $status/$conclusion"

        if [ "$status" = "completed" ]; then
            echo "RUN $RUN COMPLETED: $conclusion"
            [ "$conclusion" = "success" ] && exit 0
            exit 1
        fi
    fi

    i=$((i + 1))
    sleep "$EVERY"
done

echo "TIMED OUT after $MAX polls - run $RUN is still going. It is not stuck merely because this stopped watching."
exit 2
