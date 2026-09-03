---
name: sweep-encoder-args
description: Pass every value the encoder argument lists state - each row's examples, range ends and stated default in Nmkoder/BinFiles/encoderArgs - to the real binaries (SvtAv1EncApp, aomenc, vpxenc, x264, x265, and the ffmpeg-side lists through ffmpeg) and report what refuses, with the record's two harness-artifact classes handled by the script rather than re-diagnosed. Use after a toolchain refresh or a bundle-tools.sh change, after editing any encoderArgs JSON, when a user reports an Advanced-grid value being refused, or whenever a task says argument list, encoder args, row check, refused value, X264.json, SvtAv1.json, LibSvtAv1.json or "run the rows through the binary".
---

# Sweeping the encoder argument lists

`scripts/sweep.py` is the check CLAUDE.md's "The Advanced tab" section describes - every row of
every list passed to the real binary, then every number a row states - as one reproducible script.
The record did that check twice by hand with two different extractors, and the second could not
reproduce the first's count (459 candidate values against 583); a session re-deriving it a third
time would have reproduced neither. Here the extraction is code, so the count is a property of the
lists and the rules, not of whoever ran it.

```bash
python .claude/skills/sweep-encoder-args/scripts/sweep.py --dry-run            # what would run, per row
python .claude/skills/sweep-encoder-args/scripts/sweep.py --out "$SCRATCH/sweep"
python .claude/skills/sweep-encoder-args/scripts/sweep.py --enc X265 --only ref,rdoq-level
python .claude/skills/sweep-encoder-args/scripts/sweep.py --ffmpeg --out "$SCRATCH/sweep-ff"
```

It writes `sweep-report.md` and `sweep-runs.json` (every command line, verdict and message) into
the output directory, prints one line per list, and exits 1 if anything was refused. Run it in the
scratchpad; about six minutes of encoder time for the five CLI lists, a few minutes of wall clock on
the desktop with `--jobs 4`.

## What a value is, and what it is not

The rules are the whole point, because they are what made the earlier counts disagree:

- **Every example value** in the row's examples column - what the grid offers.
- **The two ends of a numeric range** written at the head of the short description: `0-5`,
  `-7 to 7`, `0.0-8.0`, `1 to 6.2`; or an `up to 250`.
- **Every token of a head that is purely an enumeration**: `psnr, ssim, iq, ssimulacra2`,
  `flat or jvt`, `dia, hex, umh, esa or tesa`, `64, 32 or 16`. So x265's six tunes and x264's eight,
  which the record says were run by hand, are swept.
- **The `(default X)` token**, where X is a number, an enumerated token or one of the examples.
  `(default varies by preset)`, `(default follows rd; ...)` and `(blank ...)` contribute nothing.
- **Nothing after `(default`** is ever read as a value. That is the rule that keeps x265's
  `rdoq-level` from contributing the `4-6` that belongs to `rd` - the second of the record's two
  harness artifacts, handled by construction.
- A head that is prose - `Float above 1.0`, `alpha:beta, each -6 to 6`, `the -intra variants and
  more`, `kbit/s` - contributes nothing beyond the examples. Rows whose head starts `Path to` are
  skipped and listed; their examples are illustrative paths.

`--dry-run` prints the values per row so a count can be reconciled against a previous run before
anything is encoded.

## The two artifact classes, and how they are handled

**Pairs.** SVT-AV1 range-checks `qm-min` against `qm-max` (and the chroma pair, `min-qp`/`max-qp`;
aomenc and vpxenc `min-q`/`max-q`; x264 and x265 `qpmin`/`qpmax`), so passing one end of a row's
range on its own is refused for crossing the partner's default. The script moves the partner to the
same value whenever the value alone would cross it - `--qm-min 15 --qm-max 15`, exactly the record's
re-check - and lists every such run as paired. A paired run that is still refused is a real finding.

**Defaults.** The stated default is encoded and compared byte-for-byte against a blank run, but
only where the encoder is deterministic: the script runs the blank twice first, and SVT-AV1 gives
two different files unless `--lp 1` is set, which is why that is in its base arguments (measured:
preset 4 and 10, identical with it, different without). A difference is reported as information,
not a fault - defaults move with the speed preset, and the rows say so in words where they do.

## What the verdicts mean

- **accepted** - exit 0, an output past 256 bytes, no error line. SVT-AV1 writes a 32-byte stub
  and exits 1 on a refusal; x265 writes nothing; aomenc refuses outright. Exit codes alone are not
  the test, artifacts are.
- **refused** - anything else, with the first error-ish line of stderr, or the last line.
- **accepted-with-message** - accepted, but a line matched the error vocabulary. Read them; an
  ordinary log line can carry the word.
- **dropped-silently** (ffmpeg mode only) - exit 0 and a file, but `Error parsing option`,
  `Unknown option`, `Invalid parameter` or `has not been used for any stream` on stderr: the
  parameter never reached the library. That is the silent half-apply CLAUDE.md documents for the
  params-style encoders, made visible.

## Base arguments, and why those

The presets Quick Convert opens on: SVT-AV1 4 with `--lp 1`, aomenc `--cpu-used=6`, vpxenc
`--cpu-used=3`, x264 and x265 `medium`; CRF rate control; `--row-mt=1`; and
`--disable-warning-prompt` for aomenc and vpxenc, whose `min-q` within 8 of `max-q` otherwise asks
a question on stdin - which the script closes, so a build lacking the flag fails fast rather than
hanging. A base argument is dropped when the row under test is that parameter.

The source is a 320x240 24 fps 8-bit 4:2:0 3 s y4m the script generates, the shape the record's
sweeps used (`test-fixtures` makes the same file as `y4m-small`). **A refusal naming the profile,
the bit depth or the chroma format may be the fixture, not the row** - `--profile high10` against
an 8-bit encode, say. Say so in the write-up rather than correcting a row for it, or re-run that row
with `--source` pointing at a 10-bit or 4:4:4 y4m.

## The ffmpeg lists and the GPU

`--ffmpeg` sweeps `encoderArgs/ffmpeg` through the shipped ffmpeg with the spelling
`FfmpegEncoderArgs` gives each encoder - one `-x264-params`/`-x265-params`/`-svtav1-params`/
`-aom-params` list, one `-key value` AVOption per row for libvpx and NVENC - into raw elementary
streams and IVF, never Matroska or WebM (a fresh SegmentUID per mux would make every default
comparison read as broken). The two NVENC lists run only with `--gpu`: they hold the GPU, and the
standing rule is to ask the user before anything does. The `ask-before-gpu-load` hook will stop the
command anyway; answer it after asking. The NVENC path has not been exercised by this script.

## The first full run, for the next one to reconcile against

3 September 2026, the 2.8.79 toolchain (SVT-AV1-HDR v4.1.0-20-g0bed4090b, AV1 Encoder v3.14.1,
vpxenc v1.15.2-151-gd98e70839, x264 0.165.3222M, x265 4.3+1-e9b8812), the generated 320x240 y4m,
`--jobs 4`, about six minutes of encoder time:

| list | rows | values | accepted | paired | default vs blank |
|---|---|---|---|---|---|
| SvtAv1 | 44 (3 path rows skipped) | 151 | 151 | 3 | 40 identical |
| AomAv1 | 21 | 75 | 75 | 1 | 21 identical |
| Vpx | 22 | 76 | 76 | 0 | 22 identical |
| X264 | 31 | 140 | 140 | 0 | 26 identical |
| X265 | 34 | 146 | 146 | 0 | 27 identical, `max-merge 2` and `limit-refs 3` differ |

588 runs, none refused. The two x265 differences are the preset moving a default at `medium`, the
thing CLAUDE.md's "defaults move with the speed preset" paragraph describes, and are information.
The paired runs were SVT-AV1's `qm-max 0`, `qm-min 15`, `chroma-qm-max 0` and aomenc's `qm-max 0`,
each accepted with the partner moved.

`--ffmpeg --enc Libx264,LibVpx`, 216 runs: libx264 clean at 143; libvpx's `lossless 1` refused
with `Error while opening encoder - maybe incorrect parameters`. Checked against the base before
being called the row's: it fails beside `-crf 30 -b:v 0` and beside `-crf 30` alone, and works with
`-crf 0 -b:v 0` or with no CRF - so the row should say lossless wants CRF 0. Nothing in the app puts
a `LibVpx.json` row beside a CRF today (that list is the CRF ladder's, which has no grid), which is
why it is recorded rather than corrected in the same change. The NVENC lists were not run.

## Reading a result into the record

A refused value that is neither paired nor fixture-dependent is a row fault, and the rule for the
fix is CLAUDE.md's: **a row states what the parser accepts, and a "leave it blank" behaviour is said
in words rather than offered as a value.** Correct the row, re-run `--only` that row, and write the
finding up with `record-finding` - naming the binary version the report prints, the source, the
preset, and which of the counts changed. Keep the report's totals in the write-up so the next run has
a number to reconcile against.
