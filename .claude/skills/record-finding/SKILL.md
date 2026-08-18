---
name: record-finding
description: Write a measurement, a corrected belief or a shipped-bug post-mortem into CLAUDE.md in this project's house style. Use after measuring something against a real binary, after finding that a claim in CLAUDE.md is wrong, after removing something on purpose that will look like an oversight later, or whenever the user says to record, document, note, write up or capture a finding.
---

# Recording a finding in CLAUDE.md

CLAUDE.md is not a description of the code - it is the record of what has been *measured* about
it and what has *gone wrong* in it. Nearly every paragraph is a bug that shipped, and the
paragraph exists so the same reasoning is not made twice. 66 of the last 200 commits touched
it; a dozen of those were CLAUDE.md alone ("Record the grav1synth header outage…", "Correct the
aomenc finding, and measure the stub length properly").

The style is consistent and nowhere stated, so sessions re-derive it from the surrounding prose
every time. This is that style, written down.

## Before writing anything: is it a finding?

Not everything measured belongs in the file. It belongs if it is **surprising, expensive, and
not recoverable from the code**:

- A number that was measured against a real binary and that something now depends on.
- A belief that turned out to be wrong - especially one that *looked* right.
- A deliberate omission that will read as an oversight (a flag not passed, an encoder not
  covered, a fallback not restored).
- An upstream behaviour that can rot underneath us.

It does **not** belong if the code already says it. "`GetCropProblem` validates the crop" is
readable from `GetCropProblem`. What belongs is why the four edges are held as *pairs*, and the
batch that carried a 140-line letterbox crop from a 1080p file onto a 480p one.

The file is ~365 KB and loads into every session whole. A paragraph that repeats the code costs
every future session and buys nothing.

## The nine rules

1. **Measure, do not reason.** Every number names the binary or build it came from - "measured
   against x265 3.5 rather than read out of its documentation", "the shipped SvtAv1EncApp pulled
   out of the published 2.8.57 linux tarball". A number with no provenance cannot be re-checked
   and will be believed forever.

2. **Say why it looked right.** This is the part that makes an entry re-readable. `Lsize=` still
   *contains* `size= `, so the first split succeeded and only the second failed. A Windows path
   always carries a drive colon, so `=` went unescaped and passed. The disguise is the finding.

3. **Name the control.** Four grain tables aborting looks like "tables are the problem" and is
   equally consistent with "this command shape always fails" - and the two are told apart by one
   run without a table. If a claim was established without a control, say so or go and run it.

4. **Vary one thing at a time.** `npl=100 + peak` was compared against `npl=peak, no peak`, so a
   bad anchor condemned a good parameterisation and the third combination - the right one - was
   never tried. Where two knobs land on the same curve, this is the failure mode.

5. **Correct in place, and keep the wrong belief visible.** Do not silently delete a claim that
   turned out to be false: the next session will re-derive it from the same evidence. Write
   "this file used to say X" and then what the evidence actually supports. The `peak=` entry and
   the apostrophe-escaping entry are the models.

6. **Label evidence grade.** "Measured", "read out of the binary's own strings", "exact-revision
   source, which is documentation-grade and labelled as such", "reasoned from this file's own
   established limit rather than measured for this argument". A session must be able to tell
   which claims it may build on and which it should re-check first.

7. **Say what is not verified.** Every large section ends with one. A gap that is named is a gap
   somebody can close; a gap that is implied gets filled in with an assumption.

8. **Name the one place in code the rule lives.** "`CropConfig` is that place, so the dialog's
   readout, the frame the resize is measured against and the filter that runs cannot disagree."
   That sentence is what stops the rule being reimplemented in a second place.

9. **Write the negative rule with its reason.** "Do not restore either fallback." "Do not put
   `hbd-mds` into `LibSvtAv1.json`, however measured-present it looks." "Do not reintroduce
   `AppContext.BaseDirectory` for anything that has to sit beside the exe." Without the reason,
   the next reader repairs what looks like an omission.

## Stamping

Anything that tracks a rolling upstream carries the version it was measured against, because
`bundle-tools.sh` takes BtbN's `master-latest`, av1an's `latest` prerelease and MSYS2's current
packages - none of which are promises about next month. Name the release the measurement was
made on where it can drift (`.claude/agents/upstream-drift.md` is what re-checks these).

## Where it goes

Sections are areas of the app, not kinds of fact, and a finding goes in the section whose
subject it is - beside what it contradicts or qualifies, not appended at the end:

`UI conventions` · `The palette` · `Cutting a release` · `The Scoop bucket` · `Notifications` ·
`Reading what the tools print` · `The file list and the track list` · `The AV1AN tab` ·
`The Advanced tab, on both encode tabs` · `Driving the encoder binaries directly` ·
`The Quick Convert command` · `The VMAF model was never a model` · `The CRF ladder` ·
`Deinterlacing` · `Grain synthesis` · `Tone mapping` · `Loudness normalization` ·
`Nothing on the Quick Convert tab is saved either`

A finding that fits none of them is a new `##` section. A finding that contradicts an existing
paragraph edits that paragraph - see rule 5.

## Voice

Bold the claim, then pay for it. The opening sentence of an entry states the surprising thing
outright - "**`File.Exists` is not a test of whether ffmpeg wrote something.**" - and the rest
is the evidence. Prose, not bullets; em-dash asides; no hedging where something was measured and
no confidence where it was not. Write "measured" and "reproduced" only where that happened.

## Committing

Subject line is imperative and names the finding, not the file. From the log:

- `Record the grav1synth header outage and the resolver that replaces the pinned URL`
- `Correct the aomenc finding, and measure the stub length properly`
- `Judge the mkvmerge containerise step by its artifact rather than its exit code`
- `Record what three ffmpeg builds say about the tone-map chain`

Not `Update CLAUDE.md`, not `docs`. A doc-only change still says what was learned.

Findings that came with a code change go in the same commit as that change. A finding recorded
on its own is its own commit.

## Checklist

- [ ] Not recoverable from the code
- [ ] Every number names the binary or build it was measured against
- [ ] Says why the wrong answer looked right
- [ ] The control was run, or its absence is stated
- [ ] Evidence grade labelled where it is not a direct measurement
- [ ] What is not verified is named
- [ ] The one place in code the rule lives is named
- [ ] Any removal carries its reason, so it does not read as an oversight
- [ ] Stamped with a version if it tracks a rolling upstream
- [ ] Placed in the section whose subject it is; contradicted claims edited, not stacked
- [ ] Commit subject names the finding
