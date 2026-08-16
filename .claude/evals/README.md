# Skill evals

`skills-evals.json` is the benchmark suite for the two project skills - two realistic tasks
per skill, each with the assertions it is graded on. It exists so a future session can
re-benchmark the skills after editing them, instead of reinventing test cases; the prompts
are written to be safe to run (read-only against GitHub, repo tree untouched, release steps
prepared-not-executed).

It used to cover four skills. `win-compile-check` and `real-binaries` were removed when
development moved off the cloud environments and onto the user's two Windows machines
(August 2026) - the first because a Windows host compiles the `#if WINDOWS` code in an
ordinary build, the second because `.claude/setup-windows.sh` puts every shipped binary on
the machine. Their evals went with them; the baseline numbers below still name them.

## How to run a pass

Per skill-creator's protocol, adapted to this repo:

1. For each eval, spawn a runner subagent per configuration - `with_skill` (told to read
   and follow the named skill) and, when a baseline is wanted, `without_skill` (barred
   from reading `.claude/skills/` or invoking project skills; CLAUDE.md stays in play,
   which is the deliberate baseline here - it measures the skills' marginal value over
   the doc, so expect outcome parity and judge on cost and errors).
2. Every runner works in the session scratchpad, never the repo tree; capture each task
   notification's `total_tokens`/`duration_ms` immediately (they are not persisted).
3. Grade each run against the eval's assertions - evidence first, artifacts inspected
   directly (view the PNGs; `git status` for cleanliness claims), no partial credit.
4. Aggregate with skill-creator's `scripts/aggregate_benchmark.py` (layout:
   `iteration-N/eval-<name>/<config>/run-1/{outputs,grading.json,timing.json}`) and
   render `eval-viewer/generate_review.py --static` for the user.

One trap met the first time: keep grading.json's `timing` key empty so the aggregator reads
real token counts from the sibling timing.json.

## Baseline numbers (for comparison, measured 2026-08-12 on v2.8.60, in a cloud session)

Iteration 1, 8 evals x with/without skill, one run each, claude-fable-5: pass rate 100%
in both arms (33/33 assertions); with-skill averaged 309s / 172.7k tokens / 18.4 calls
per run against the baseline's 452s / 191.1k / 24.1, with 1 error against 5. Iteration 2
(after folding the benchmark's lessons back into headless-ui and real-binaries):
quickconvert 5/5 at cost parity with a faithful first-launch frame; svt-line 4/4 at
198s / 13 calls / 161.1k tokens against its iteration-1 499s / 32 / 194.6k - the
provenance re-download eliminated by the skill's digest table.
