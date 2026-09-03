---
name: record-audit
description: Find the identifiers in CLAUDE.md and the reference skills that no longer name anything in the tree - methods, classes, files, config keys and flags renamed or removed since the sentence was written - and report each with its sentence and a classification, without editing anything. Use after a rename or refactor, before a release, after a CLAUDE.md split or a move between file and skill, when a memory or a skill names a function that has to be checked before it is recommended, or whenever the user asks whether the record still matches the code. Read-heavy - hundreds of identifiers grepped over the tree - which is why it runs as a subagent.
---

You audit the project's record - CLAUDE.md and `.claude/skills/*/SKILL.md`, plus the agents and
the memory files if asked - for names that no longer name anything: a method that was renamed, a
file that was split, a config key that was deleted, a flag that moved. You report; you never edit.

## Why this exists

The record is written as measurements and post-mortems, and nearly every paragraph names the one
place in code where a rule lives - "`Av1an.GetDefaultThreadPlan` returns...", "the check is in
`FfmpegOutputHandler.LooksLikeTrouble`". That is what makes it useful and what makes it rot: the
code moves and the sentence does not. The most recent instance is commit `39a5ccf`, whose subject
ends "fix the stale scene-detect method name" - a name in CLAUDE.md that the code had stopped
using, found by chance. The split of six sections into skills left two meta-documents stale in the
same way (`.claude/README.md`, "The CLAUDE.md split"), and nothing about the move flagged it - the
checksums all passed. And the memory tooling's own rule is that a memory naming a file, function or
flag has to be checked against the tree before it is recommended. All three are this job.

## The pipeline

Work in the scratchpad. Extract every backticked token that looks like a code identifier, then ask
the tree about each one. This is the mechanical half; the classification below is the half that
needs reading.

```bash
R="$(git rev-parse --show-toplevel)"
# Not this file: its placeholder examples are written to look like identifiers.
cat "$R/CLAUDE.md" "$R"/.claude/skills/*/SKILL.md "$R"/.claude/agents/verifier.md "$R"/.claude/agents/upstream-drift.md "$R"/.claude/agents/invariant-reviewer.md | tr -d '\r' \
  | grep -o -E '`[^`]{2,80}`' | tr -d '`' \
  | grep -E '^[A-Z][A-Za-z0-9]*(\.[A-Za-z0-9_]+)+$|^[A-Z][a-z0-9]+([A-Z][A-Za-z0-9]*)+$|^[A-Za-z0-9_.-]+\.(cs|axaml|json|sh|py|yml)$' \
  | sort | uniq -c | sort -rn > ids.txt
while read -r n id; do
  case "$id" in
    *.cs|*.axaml|*.json|*.sh|*.py|*.yml)   # a file name: the index plus the untracked-but-not-ignored, as a path suffix (".Utils.cs" is MainWindow.Utils.cs)
      { git -C "$R" ls-files; git -C "$R" ls-files --others --exclude-standard; } | grep -v '^.claude/worktrees/' | grep -q -F -- "$id" || echo "0 hits (file): $id ($n)";;
    *)                                      # a member or class: its last segment, in the sources and the tool data
      seg="${id##*.}"
      git -C "$R" grep -q -F -- "$seg" -- 'Nmkoder/*.cs' 'Nmkoder/*.axaml' 'Nmkoder/BinFiles' 'Nmkoder/Nmkoder.csproj' '.github' '.claude/*.sh' '.claude/skills/*/scripts' \
        || echo "0 hits: $id ($n)";;
  esac
done < ids.txt
```

What that regex covers, and the report must say so: PascalCase and qualified names and file names.
Single-hump names (`Qtgmc`, `Config`, `Paths`), snake_case and ALL-CAPS (`open_video`,
`PLAIN_ORDER`, `EXPECT_MS`), leading-underscore fields, lowercase-first members and any backticked
path with a slash in it are not extracted, so a clean report is narrower than "every identifier".

Five names come back every run and are the pipeline's, not the record's: `done.json` and
`scenes.json` are av1an's runtime files, `vmaf_v0.6.1.json` is downloaded by the bundler, and
`sweep-runs.json` and `mkdovi.py` are written by scripts or live only in the scratchpad by design.
`git ls-files` cannot see any of those by construction. Dismiss them in one line.

For a qualified name (`Class.Member`) whose last segment does hit, check the pair as well: does the
file that defines `Class` contain a *declaration* of `Member`? `git grep -n -F "Member" --
'Nmkoder/**/Class*.cs'`, or a grep for `class Class` first. A hit that is only inside a comment
does not count - `VideoEncodersBin` passes on comments alone while `VideoEncodersLib` fails, for the
same kind of name, so look at the line. Three notations are legitimate and not misses: File.Class
(`VideoEncodersBin.SvtAv1` is `class SvtAv1` in VideoEncodersBin.cs, the spelling the code's own
comments use), a nested type (`Config.Key`), and an `x:Name` or a style selector in an `.axaml`. A
member that exists somewhere else under the same name is the most misleading kind of hit, because
the simple check passes.

The tree moves while you work - a session edits CLAUDE.md in parallel - so re-run the extraction
immediately before writing the report, take every line number from that final pass, and stamp the
report with HEAD and the time. Keep any widened grep out of `.claude/worktrees/`, which holds whole
checkouts with build output.

The first run (3 September 2026): 601 identifiers, 34 with zero hits, 41 read once the pair check
was added, one stale. Expect a list of that size, and expect most of it to be explained; the count
moves with the tree and with the regex, so do not try to reconcile it exactly.

## Classifying a zero-hit name - read the sentence first

For every hit, read the sentence it sits in and the two or three lines around it (`grep -n -F`
gives the line; read a little of the file). Then put it in exactly one class:

1. **Stale** - the sentence speaks in the present tense about something that no longer exists
   under that name: "the one place is the method", "handled by the helper". This is the finding.
   Say what the name most likely became if a rename is visible (`git log -S'Foo' --oneline --
   Nmkoder | head` shows the commit that removed it from the code - unrestricted, the first hit is
   the commit that wrote the sentence; the diff shows what replaced it) - as a suggestion, not a
   claim. The first run's one finding is the shape to expect: a guard written the day a method
   was added, the method removed the same day in a commit that rewrote 65 lines of CLAUDE.md and
   missed this one, then moved verbatim into a skill - reading as live because it sits under a
   present-tense "must stay" rule.
2. **Deliberately removed and described as such** - the sentence itself says it is gone: "is gone
   rather than left unused", "and the check that called it are gone", "still in existing config
   files; nothing reads them", "went with it". The record keeps these on purpose (the
   `record-finding` rule: give every deliberate removal its reason so it does not read as an
   oversight). Not a finding. List them in one line.
3. **Historical, past tense** - the sentence narrates what used to happen without itself saying
   the thing is gone: "the pass that used to sit in front of av1an", a corrected belief kept
   visible ("this file used to say ..."), a rejected spelling shown beside the one that works. Not
   a finding, unless the same name is also used in the present tense elsewhere. The tie-break with
   class 2 is whether the sentence states the removal (2) or merely tells the past (3); "all of it
   is deleted" is 2.
4. **External** - an OS, framework or third-party name the tree does not contain because it is not
   ours: `CommandLineToArgvW`, `GetWindowRect`, `CommunityToolkit.Mvvm` (named as what the project
   does NOT use), `Avalonia.Headless`, `WindowsAppSdkUndockedRegFreeWinRTInitialize`, Scoop's
   `schema.json`. Not a finding.
5. **Pipeline artifact** - a name the regex caught that is not an identifier, or a file the grep
   scope does not cover (a script under `.claude/skills/*/assets`, a harness file that lives only
   in the scratchpad by design). Widen the check or dismiss it, and say which.

Do not guess a replacement into the record. Do not "fix" anything. If a stale name's replacement
is not visible from the history, say "removed in <commit>; no obvious successor".

## What to report

Findings first, then the rest in one line each:

```
Audited at HEAD <sha> plus the working tree, <time>; line numbers from the final pass.

STALE (n)
- <the name> - <file>:<line> "<the sentence>"
  removed in <commit> (<what replaced it, if visible>). Suggested edit: <one line>.
- ...
Deliberately removed, described as such (n): <names>
Historical, past tense (n): <names>
External (n): <names>
Pipeline artifacts (n): <names>
Checked: N identifiers (PascalCase, qualified and file names only), M zero-hit, K read.
```

Give the file and line for every finding, quote enough of the sentence to find it, and keep the
report to the findings - the user does not need the 500 names that passed.

## Rules

- Read-only. Nothing under the repo tree is edited; the extraction runs in the scratchpad.
- The skills' bodies were moved from CLAUDE.md verbatim and say "this file" meaning CLAUDE.md, and
  they keep old beliefs visible next to corrections by design. A name in a past-tense sentence is
  not stale.
- A digest in CLAUDE.md and the skill it points at may both name the same member; a stale name
  in one is stale in both - report both lines.
- Name the commit (`git log -S`) when you say something was removed; a renamed member is a finding
  with a one-word fix, and that word should be right.
