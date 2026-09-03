# Skill evals

`skills-evals.json` is the benchmark suite for the three project skills - two realistic tasks
per skill, six in all, each with the assertions it is graded on. It exists so a future session
can re-benchmark the skills after editing them, instead of reinventing test cases; the prompts
are written to be safe to run (read-only against GitHub, repo tree untouched, release steps
prepared-not-executed, findings drafted rather than filed).

**The `record-finding` pair is graded on the write-up, not on the finding.** Its two prompts
*supply* the raw material - a measurement with a named binary, a belief that looked right, a
control that was never run - so the grader is judging provenance, the why-it-looked-right
clause, correcting in place, and routing, none of which need the finding to be true. That is
also what keeps them safe: both say "draft only", so a pass leaves the repo tree untouched,
and "edited no tracked file" is one of the assertions rather than an assumption. Eval 4 is a
measurement, which belongs in the skill that owns the area; eval 5 pairs a rule that must go in
**both** the skill and the CLAUDE.md digest with something that should not be recorded at all,
which is the discriminator - the without-skill arm tends to record both and to file the
measurement in CLAUDE.md.

It used to cover four skills. `win-compile-check` and `real-binaries` were removed when
development moved off the cloud environments and onto the user's two Windows machines
(August 2026) - the first because a Windows host compiles the `#if WINDOWS` code in an
ordinary build, the second because `.claude/setup-windows.sh` puts every shipped binary on
the machine. Their evals went with them; the baseline numbers below still name them.

`test-fixtures` and `sweep-encoder-args` (September 2026) have no evals yet. Both are scripts
that check themselves - `make-fixture.sh --check all` asserts the property each shape exists to
have (36 checks on the 2.8.79 toolchain) and `sweep.py --dry-run` prints the values it would
run (588 over 152 rows) - so a benchmark of the skill would be measuring whether a session
reaches for the script, which is a triggering question rather than an outcome one.

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
