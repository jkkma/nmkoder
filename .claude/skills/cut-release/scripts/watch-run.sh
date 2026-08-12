#!/bin/sh
# Poll a release workflow run until it finishes. Prints one line per poll so it is
# visibly alive, and fails loudly rather than spinning when the API stops answering.
#
#   watch-run.sh <run-id> [poll-seconds] [max-polls]
#
# Exit: 0 success · 1 the run concluded non-success · 2 timed out still running
#       3 the API could not be read repeatedly (the poller's own failure, not the run's)
#
# TWO TRAPS THIS EXISTS TO AVOID, both of which cost a release session real time:
#
# 1. **Never round-trip the JSON through `echo`.** /bin/sh here is dash, whose builtin
#    echo expands backslash escapes - so `j=$(curl ...); echo "$j" | jq` turns every \n
#    inside a JSON string (commit messages are full of them) into a real newline and jq
#    dies with "control characters ... must be escaped". Measured: the same bytes through
#    `printf '%s'` parse fine. curl is piped straight into jq below, which sidesteps it
#    entirely. Stripping control characters is NOT the fix - the response contains none;
#    the shell was creating them.
#
# 2. **A poller that fails every iteration looks exactly like one that is waiting.** The
#    version this replaces errored on all 22 of its polls and kept going, reporting
#    nothing, while the run had long since gone green. Consecutive failures are counted
#    and abort the loop.
set -u

RUN="${1:?usage: watch-run.sh <run-id> [poll-seconds] [max-polls]}"
EVERY="${2:-30}"
MAX="${3:-60}"
API="https://api.github.com/repos/jkkma/nmkoder/actions/runs/$RUN"

i=0
fails=0

while [ "$i" -lt "$MAX" ]; do
    # curl straight into jq - no shell variable, no echo. -f so an HTTP error is a
    # nonzero exit rather than an error page fed to jq.
    line=$(curl -fsSL "$API" | jq -r '"\(.status) \(.conclusion // "-")"' 2>/dev/null) || line=""

    if [ -z "$line" ]; then
        fails=$((fails + 1))
        echo "$(date +%H:%M:%S)  poll $i: could not read the run ($fails in a row)"
        if [ "$fails" -ge 5 ]; then
            echo "GIVING UP: 5 consecutive failures reading $API - the poller is broken or the API is unreachable, and the run's real state is UNKNOWN."
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
