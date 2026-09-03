---
name: test-fixtures
description: Generate the named synthetic sources Nmkoder's measurements are run against - an interlaced capture, a padded-cadence capture, a keyframe-every-2s H.264, a scenecut=0 PQ clip with a movable bright event, PQ and HLG sources with side data, anamorphic SAR shapes, stereo and 5.1 loudness steps, the three-minute CRF-ladder source and the sweep's small y4m - with one script, each self-checked against the shipped ffmpeg. Use before building any harness that needs a control source or a fixture, whenever a task says fixture, synthetic source, test clip, testsrc, lavfi, sample file, control, or names one of the shapes; and run its --check after every toolchain refresh.
---

# Test fixtures

`scripts/make-fixture.sh` builds the sources the record keeps describing in prose - forty-odd
mentions of "fixture" across CLAUDE.md and the reference skills, and not one of them written as
a command - so a harness starts from a known file instead of re-deriving the recipe.

```bash
bash .claude/skills/test-fixtures/scripts/make-fixture.sh --list
bash .claude/skills/test-fixtures/scripts/make-fixture.sh --check all "$SCRATCH/fx"
bash .claude/skills/test-fixtures/scripts/make-fixture.sh keyframe-2s "$SCRATCH/fx"
EVENT_AT=2.75 bash .claude/skills/test-fixtures/scripts/make-fixture.sh --check scenecut-0 "$SCRATCH/fx"
```

Fixtures go in the scratchpad, never in the repo tree. The script picks the shipped ffmpeg at
`~/.nmkoder-dev/bin/ffmpeg.exe` when it is there and says which binary it used in its first line;
a harness quotes that line, since a bare `ffmpeg` on these machines is the user's Scoop build.
`FFMPEG=/path/to/ffmpeg` overrides it.

## The shapes

| shape | file | for |
|---|---|---|
| `interlaced` | interlaced-29.97i.ts | 720x480 29.97i MPEG-2, TFF, real field motion. Deinterlacing, `InterlaceDetect`, idet, QTGMC's source plugins. |
| `padded-cadence` | padded-cadence.ts | The interlaced shape with ~40% duplicate pictures and jittered stamps over the true 10 s. Cadence repair, `GetFramerate`, the unpadded-file refusal. |
| `keyframe-2s` | keyframe-2s.mkv | 40 s H.264, a keyframe exactly every 2 s, B-frames. The CRF ladder's pre-roll, the stream-copy cut, keyframe trims. |
| `scenecut-0` | scenecut-0-pq.mkv | 4 s dark PQ clip, three white frames at `EVENT_AT`, `scenecut=0`, keyint 48. The peak scan - sweep the event, do not place it once. |
| `hdr-pq` | hdr-pq.mkv | PQ BT.2020 10-bit with mastering display and MaxCLL 1000 / MaxFALL 400. `ToneMapConfig`, `IsHdr`, `GetDeclaredPeakNits`, previews. |
| `hdr-hlg` | hdr-hlg.mkv | The same tagged HLG, no mastering display. |
| `anamorphic-16x9` | anamorphic-16x9.mkv | 720x480 with SAR 32:27. The IVF/AV1 aspect loss, `GetMuxAspectArgs`, `GetPipeSarFilter`. |
| `anamorphic-4x3` | anamorphic-4x3.mkv | 720x480 with SAR 8:9, the NTSC capture shape. |
| `loudness-stereo` | loudness-stereo.flac | 20 s, second half 26 dB under the first. One-pass vs two-pass loudnorm, the trim going with the measurement. |
| `loudness-5.1` | loudness-5.1.flac | The same step in 5.1. The channel conversion inside the filter, `GetOutputChannelCount`. |
| `crf-ladder` | crf-ladder-3min.mkv | Three minutes changing content every minute, keyframe every 2 s. The Sample Encodes utility. |
| `y4m-small` | y4m-small.y4m | 320x240 24 fps 8-bit 4:2:0, 72 frames. The direct-encoder chain and the argument sweep. |

## What --check asserts, and why it is the point

Every recipe was measured against the shipped ffmpeg (BtbN `N-126264-g007cd1fd43-20260825`, the
2.8.78/2.8.79 toolchain) on 2026-09-03, and `--check` is those probes run again: field order and
idet's verdict, keyframe positions on even seconds, the event frames bright and their neighbours
dark with no keyframe on them, PQ/HLG tags and the side data present or absent, SAR and DAR, the
two loudness halves 24-28 LU apart, the three ladder segments distinct, the y4m header. The
bundler tracks a rolling ffmpeg, and this script is where a recipe that stops meaning what it
meant fails first. Run `--check all` after `setup-windows.sh` restages the toolchain.

## Three things the build taught, all drift

- **`-top 1` is gone from this ffmpeg** ("Codec AVOption top ... is not a encoding option"), and
  `-field_order tt` writes `field_order=bb` into MPEG-TS - the wrong parity, the trap the
  cadence-repair skill records for `-flags +ilme+ildct`. What tags a field correctly is the frame
  property: `setfield=tff`, or `tinterlace`, which sets it as it weaves. Measured: `-field_order tt`
  gives bb in .ts and .mpg and bt in .mkv; `setfield=tff` gives tt everywhere.
- **A frame-coded synthetic MPEG-2 does not reproduce a real capture's `r_frame_rate=60000/1001`
  against `avg_frame_rate=30000/1001`** (CLAUDE.md, "Reading what the tools print"): mpeg2video
  writes frame pictures only, and that signature comes from field pictures. The `padded-cadence`
  shape shows the disagreement anyway - its jittered stamps push `r_frame_rate` above
  `avg_frame_rate` the way field pictures do, 48000/1001 against 30000/1001 on this build (the
  figure follows the jitter pattern; the check asserts only that the two differ) - and the plain
  `interlaced` shape asserts `r == avg` in its own check so nobody mistakes it for the capture
  case. A test of the `GetFramerate` rule uses the padded file.
- **Interlacing is a property of the picture, not of a tag.** `setfield=tff` alone on progressive
  frames had idet call 49 of 90 frames TFF and 30 BFF; the shape here weaves a 59.94p source into
  29.97i with `tinterlace=interleave_top`, and idet then calls all 120 frames TFF.

## What the padded shape is and is not

It is a model: every picture once, again 40% of the time by a fixed LCG (so the file is
byte-identical on every machine), encoded constant-rate with no B-frames, then the stamps rewritten
by the `setts` bitstream filter - the coded pictures spread over the true recording length with a
±8 ms jitter derived from the index. The mpeg2video encoder cannot write those stamps itself; its
timebase is the frame rate, and a VFR source through it died with `EINVAL`. What that gives is the
observable the utility keys on - 408 coded frames over 10.009 s, 1.36x its length, VFR stamps that
track true time locally - not a measured TBC. The record's 95-minute capture stays the reference for
anything length-dependent (the cadence-repair skill: "validate a length-dependent fault at length"),
and a 6.99 s drift needs that file, not this one.

## Adding a shape

One `build_<fn>` and one `check_<fn>` function and a line in `SHAPES`, with the check written as
the property the shape exists to have, not as "it built". Name the record area it serves in the
description. Measure the recipe before committing it and note the ffmpeg build in the header.
