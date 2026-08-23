---
name: grain-synthesis
description: The full record of Nmkoder's grain synthesis row on both encode tabs and the Film Grain utility - the three ways SVT-AV1 can be asked for grain and which one wins, film stock presets via a grav1synth stub round trip, brought grain tables, the denoise tick, photon noise, measuring, and everything measured about grav1synth itself including how it is built and bundled. Load for film grain, grain tables, --film-grain, --fgs-table, --film-grain-table, GrainSynthConfig, Grav1synth, hqdn3d denoise - or a bundle-tools.sh change touching grav1synth, its ffmpeg dev headers, or the MSYS2 encoders.
user-invocable: false
---

# Grain synthesis - the full record

CLAUDE.md's `## Grain synthesis` section is the digest: it carries the rules that have to hold
whatever you are doing in this area, and points here. This is the whole of it - every
measurement, every trap, and the account of what was got wrong and why it looked right.

It was moved out of CLAUDE.md **verbatim**, byte for byte, so nothing below is paraphrased and
nothing was dropped. That is also why passages below that say "this file" mean **CLAUDE.md**,
where this text used to sit - they were not rewritten, because rewriting them would have been
the one way to lose something in the move.

CLAUDE.md remains the authority on the project and its conventions govern this file too. A new
finding here goes here, in the house style `.claude/skills/record-finding` sets out; a new *rule*
that can be broken from outside this area goes in the CLAUDE.md digest as well.

## Grain synthesis

**The Grain Synthesis row is a mode selector that owns every way this app can put grain in an AV1
file, and that ownership is the point of it.** It was a strength spinner and a Denoise box, and what
made it worth changing was not that grav1synth exists - it is that there were already *three* ways to
ask SVT-AV1 for grain and they silently overrode each other. `--film-grain` sat on the row while
`--noise` and `--fgs-table` sat in the Advanced grid, and SVT takes exactly one of the three, in
`set_param_based_on_input`, with an `SVT_WARN` that goes to the encoder's stderr - which av1an collects
per chunk into a log `HandleTempFolder` deletes on a successful run. `GetGrainSynthProblem` existed to
report that collision after the fact. One control that writes at most one of them cannot express it.

The modes are in `GrainSynthMode`, and what separates them is not how the grain looks:

| Mode | Where the description comes from | Cost |
|---|---|---|
| Encoder analysis | the encoder, from a strength | one number |
| Grain table file | a table the user already has | nothing, or Quick Convert's denoise filter on request |
| Film stock preset | grav1synth's own built-in tables | a 64x64 stub round trip, under a second |

**The row is what the encoder does while it encodes and costs no pass over the video, and both halves
of that are load-bearing.** Grain written into a file that is already encoded - photon noise, or any
of these applied afterwards - is the Film Grain utility's job and is not on this row at all. That is
the division CLAUDE.md already states for Cut and Deinterlace Video: utilities write a file, the
tabs' own settings apply during an encode, and neither reads the other's.

**The film stock presets used to be on the wrong side of that line, and the rule is what put them
right rather than what kept them out.** They were utility-only on the grounds that grav1synth writes a
preset into a finished bitstream, which no encoder can be asked to do - true of `grav1synth apply`,
and not true of the preset. **A preset is an ordinary grain table.** There is no command that emits
one (`grav1synth --help` offers `inspect`, `apply`, `presets`, `remove`, `diff` and nothing else), but
`apply --preset` onto a throwaway stub and `inspect` back off it yields the table, and from there it
is Grain table file in every respect: same `--fgs-table`/`--film-grain-table` delivery, same refusals,
same collision checks, same argument. `Grav1synth.MakePresetTableAsync` is the round trip and
`GrainSynthConfig.NeedsPresetTable` is who asks for it. What the row still does not do is what it
never did - rewrite the output afterwards.

Four things about that were measured rather than reasoned out, and each is why the stub can be as
cheap as it is:

- **Nothing about the table depends on the stub's frame.** Swept over 14 presets × 13 stub shapes -
  resolution 64x64/320x240/1920x1080, 8- and 10-bit, three source contents - **segment 1's parameter
  block is byte-identical in all 182 round trips**, and resolution, depth and content change nothing
  anywhere in the table. So the stub is 64x64 black. Confirmed from the other end too: tables built
  from a deliberately mismatched stub (64x64/8-bit/60fps/1s) against a matched one produce
  **byte-identical elementary streams** for all 14 presets on a 1920x1080/24fps/10-bit encode.
- **A preset's pattern saturates, and how soon is per preset *and* per frame rate.** Timestamps are
  100ns ticks and frame-aligned; the parameters vary for a few seconds and then **one final segment
  runs to the end of the file** - a 2-hour stub produced 35 segments, the last covering 7191 of the
  7200 seconds. The 8.5s figure this once gave is 16mm's alone: at 30fps ten of the fourteen only
  settle at 10s. `StubSeconds` is **15**, and that is measured at the stub's own 24fps rather than
  carried over - Modern35-3 and Classic35-2 each gain a segment between 10s and 15s there, so 10 would
  have been short, and from 15s to 45s the segment count and the whole parameter-block sequence are
  identical for every preset. `ExtendFinalSegment` then runs that last segment out to 24 hours, which
  restores the shape grav1synth writes for a long file anyway. Without it an encoder that looks the
  table up by timestamp - libaom does, measured: an as-generated table covers 83.3% of a 12s clip -
  would grain the first 15 seconds and leave the rest clean.
- **SVT-AV1 reads the first segment and nothing else.** Given `--fgs-table` it writes one segment for
  the whole encode whose parameters equal the table's segment 1, with a seed of its own (7391 where the
  table's are 21912/54780/10956/32868) - all 14 presets, none rejected, all 14 outputs distinct, and a
  table truncated to 0.1s still grains a 3s clip end to end. So the extension is for aomenc's sake; on
  SVT the table's length cannot matter.
- **The round trip costs about a second.** The 2-hour stub above took 72s to encode, which is why the
  stub is short rather than matched to the source. `apply` and `inspect` are a bitstream rewrite rather
  than a decode and cost ~0.09s **whatever the stub's length** - a 60s 1080p file measures the same as
  a tiny one - so the whole cost is the stub encode.

**All 14 names are genuinely different grain**, at segment 1 and whole-table alike, with no collisions:
the `-1`/`-2`/`-3` modifiers really do change the film stock and no bare name equals a modified form.
They differ structurally rather than only numerically - `p` field 2 is 6/9/7/8 for
Super8/MaxMid/16mm/Classic35, and 16mm-2 carries `sY 13` where 16mm carries `sY 14`.

**Two things about the tail are worth knowing before comparing two tables.** The final segment's
**seed** moves with the stub's duration as well as its end tick (16mm-1: 43824 at 10s, 54780 at 60s),
and the **frame rate** changes the block sequence past segment 1 - four distinct sequences for
24/25/30/60 in 13 of the 14. Neither reaches what SVT encodes, which is segment 1 only; for aomenc it
means the stub's rate decides where the grain changes, and whether a 30fps table on a 24fps encode is
perceptually equivalent was not measured. **Do not re-time by writing `end = -1`**: measured, that
yields no grain at all, silently. `int64` max is safe on both encoders - aomenc's output with it is
byte-identical to an exactly-spanning table - and 24 hours is used because it is as good and reads as
a duration.

Verified end to end through the real code: `MakePresetTableAsync` out of the built assembly produced a
19512-byte table whose first line is `filmgrn1`, 36 segments, final end tick 864000000000 (24h) with
every earlier boundary untouched (the one before it at 8.417s); `SvtAv1EncApp --fgs-table` on that file
produced an AV1 output whose grain, read back with `grav1synth inspect`, is **byte-identical to the
table's segment 1** across all seven parameter lines; 16mm and Super8 give different tables; and an
unknown preset name is refused with nothing written.

**The generated table's path cannot contain a space on the AV1AN tab**, and that is the one place this
is narrower than Grain table file. Everything sent to an av1an-driven encoder goes inside one `-v "…"`
string that av1an splits again on whitespace - the limit this file already records - and the table this
writes lives at `{tempDir}.grain.tbl`, under `Paths.GetAv1anTempPath()`, which is beside the exe. So an
install path with a space refuses, and unlike a user's own table it cannot be moved. The refusal is
worded for that case and names Quick Convert, which launches the encoder itself and quotes the path.
The `{tempDir}` naming buys two things: `GetPreparedInputs` already sweeps a `.grain.` sibling when it
deletes that folder, and a resume replaying its saved command finds the table where the command says,
the path deriving from the same `overrideTempDir` the resume was given.

**av1an's `-v "…"` string is *unescaped* as well as split, and for eight releases that ate every
backslash of the table's path - which is to say both table-bearing grain modes were broken on Windows
outright.** The value was written bare on the reasoning quoted in `VideoEncodersBin`, that a quote of
this app's own would be one layer more than av1an's re-split accounts for. That is true and it is half
the story: av1an splits with a shell-style parser, which *unescapes* on the way, so `C:\Users\…` reached
SvtAv1EncApp as `C:Users…`. The encoder answered `Invalid parameter '--fgs-table'` once per
chunk until av1an gave up on the worker and the run produced nothing.

Both modes get there honestly, which is why neither escaped it: **Film stock preset**'s table is written
by this app at `{tempDir}.grain.tbl` under `Paths.GetAv1anTempPath()`, a Windows path by construction,
and **Grain table file**'s comes from a Windows file dialog, which returns backslashes too. Nothing on
the tab could produce a working table path on Windows.

Measured against the bundled av1an 0.5.2-unstable (rev f9b14ed) and SVT-AV1-HDR v4.1.0-19-g8b4b9f562,
through the real command on one fixture, with the control run: a **single-backslash** path fails as
above and writes nothing; a **doubled-backslash** path encodes (790,301 bytes) and a **forward-slash**
one encodes (790,296). So the parser undoes exactly one level, and both repairs work on Windows.

`FormatUtils.GetAv1anArgPath` is the one place that rule lives, and doubling is what it does rather
than the slash substitution `GetFilterPath` makes on Windows - the two ask different questions. There
the value is read by ffmpeg; here by a shell-style parser. Doubling restores the character the caller
meant on every platform, where substituting a slash would aim at a path that does not exist anywhere a
backslash is legal filename data - the lesson `CreateConcatFile` and `GetVmafPath` already carry.
`Av1anUi.GetVideoArgsFromUi` applies it once, at the single point a table path crosses into that
string, so the two encoder classes cannot drift apart over it.

Verified through the real `MainWindow` and `RunTask.Start` on a 43 s 3836x2072 10-bit HDR cut: before,
no output and `Invalid parameter` once per chunk; after, Film stock preset (16mm) and Grain table file
given a **backslash** path both finish, and `grav1synth inspect` reads back one segment whose seven
parameter lines are byte-identical to the table the app generated, carrying SVT's own seed 7391.
Encoder analysis was never affected, writing a number rather than a path.

**Not fixed, and it is the same parser: a path typed into the Advanced grid.** The grid's values reach
the same `-v "…"` string, so `--fgs-table` typed there by hand, or any other path-valued parameter, is
eaten identically. It was left alone because the grid holds arbitrary values and this app cannot tell
which are paths - escaping every backslash in every value would corrupt the ones that are data. A space
in the table path is still a refusal rather than an escape, for the reason under
`GetTableDeliveryProblem`: the refusal is deliberate and worded per mode, and making spaces silently
work is a change of behaviour rather than a fix.

**Measured from source left later, and for the second half of that rule rather than the first.** It
*was* an encode mode on the AV1AN tab: a lossless denoise render of the whole film and a grav1synth
diff, both in front of av1an, at about 3.7 fps at 1080p - a working day of single-threaded measuring
before the parallel encode began, on every run of it. Measuring is a thing to do once per source, not
once per encode, and the Film Grain utility's Measure operation already did exactly that and stated
its cost up front; its table then feeds Grain table file here for nothing. Quick Convert had refused
the mode outright from the day it was ported, having no measuring pass, so this closed the last place
it could be picked. `GrainSynthConfig.EncodeModes` is what both rows offer; `IsUtilityOnly` covers
`Measured` and `PhotonNoise` - `Preset` left that list when it became a table, see above - and
`DescribeUtilityOnly` words the two apart, since telling somebody that measuring "writes grain into a
finished file" would send them to the wrong operation. `PhotonNoise` stays out on its own merits
rather than by inheritance: grav1synth synthesises it from the frame size and the transfer curve, so
unlike a film stock it is genuinely not a fixed table and a stub round trip would describe the stub.

**Both encode tabs carry the row, and both drive the same binaries now - what still differs is the
pipeline behind it.** `GrainSynthUi` drives both the way `ToneMapUi` and `DeinterlaceUi` do - one
`Init`, one `RefreshInfo` writing both readouts, per-tab config getters - so the modes, the panels,
the readout and the refusals are one implementation rather than two that drift. The two per-tab
`GetTableFlag` overloads are the one statement of which codecs take a table and how each spells it -
`--fgs-table` on SVT-AV1, `--film-grain-table` on aomenc, for the Av1an* and Direct* pairs alike -
and everything downstream reads that: which delivery is likely, what the readout says, and what `Run`
refuses.

**Both tabs carry out all three modes, and the one control only Quick Convert has is the Denoise tick
beside the table.** Encoder analysis works on either tab's AV1 encoders, spelled by the Direct and
Av1an classes themselves. Grain table file works on both, and on Quick Convert with a path containing
spaces - the table travels as one `Shell.WrapArg` argument, where the AV1AN tab still refuses a spaced
path that av1an's one-quoted-string re-split would break. Film stock preset works on both and meets
that same split, with the difference recorded above: its path is this app's own, so a space in it is
the install path rather than anything the user can move. The binary is asked about the flag at encode
time on both, because a user's own SVT-AV1 may be mainline and refuses the whole command over it -
which reaches the presets too, `--fgs-table` being how they travel.

**The Denoise tick is on both tabs now, and the reason it was Quick Convert's alone does not survive
inspection.** The stated asymmetry was the one QTGMC has: on Quick Convert it is one `hqdn3d` entry in
a chain ffmpeg is building anyway, and on the AV1AN tab it was `DenoisePass` rendering a lossless copy
of the entire film before av1an could start. **That pass belonged to the measuring mode, and measuring
is the one thing that needs a denoised *file* - to diff against.** A table needs only denoised
*frames*, and av1an's per-chunk `-f` chain produces those for the price of one filter entry, exactly as
the deinterlacer and the zscale tone map do. So the tick, the strength and the filter are on both tabs,
and `NeedsDenoisePass` is called `NeedsDenoiseFilter` because on neither tab is it a pass any more.

**Chunking is safe because `GetDenoiseFilter` is spatial-only** - its temporal halves pinned to 0, a
choice made for the grav1synth diff's sake (hqdn3d's temporal filter is not motion compensated, so it
blends the previous frame in and the diff reads a ghost as grain). Nothing carries state across a chunk
boundary, so no seam can appear at one. A temporal denoiser could not go in this chain.

It runs **after the geometry**, where Quick Convert and the utility's own pass both put it - the table
describes grain at the frame being encoded - and that is also where it is cheapest, the frame being at
its smallest. The target-quality probes do not see it, like every other filter on that tab, which
`GetFilteredTargetQualityNote` already covers by counting the chain.

**The tick ships ticked, and the old default shipped the one shape of the feature that costs bitrate
instead of saving it.** It defaulted off on the argument that a brought table may be there to put grain
onto a source that never had any - real, and the minority. Measured through the shipped SVT-AV1-HDR
binary on a grainy 640x480 source at CRF 35, against a control with no grain synthesis at all: the
preset table **without** denoise comes to **+0.6%**, which is to say it is *worse than not using the
feature*, and **with** denoise to **−21.9%**. The tick is worth **22.4%** of the bitrate. A user asked
"why would you ever want to add film grain on top of film grain" and the honest answer is that you
would not; this file's own note on Table mode already called that shape "the one shape of this feature
that costs bitrate instead of saving it" while defaulting to it.

The tick is shared by both table modes and sits in its own panel for that reason - it used to live
inside the table panel next to the browse button, and what it describes is one operation whichever
supplied the table, so `EncGrainDenoisePanel`/`Av1anGrainDenoisePanel` hold it and show for Table or
Preset alike with the strength behind it. Measure once in the utility and encode with the table on
either tab; the tick is what reproduces the encode a measured table came from.

**The strength survived the rewrite, and dropping it would have been a regression rather than a
simplification.** `--fgs-table` is a PSY-line parameter - mainline SVT-AV1 does not have it and neither
does the libsvtav1 inside the bundled ffmpeg - where `--film-grain N` is on every build, costs no extra
pass, and denoises the picture itself. It is the right answer for most people and it stays the cheap
default.

**Encoder analysis writes grain that only exists once a decoder synthesises it, and it measures the
frame the filter chain hands over rather than the file.** Both halves come up as "I set 25 and I see
no grain", and neither is a fault. Measured with the real thing - a grainy 4K source, SVT-AV1 at
`--film-grain 25 --film-grain-denoise 1`, high-frequency energy read off the decoded frames: the
source measures 5.16, the app's own 4K-to-1080p downscale hands the encoder 2.14, the coded picture
is **0.42** - as clean as an encode with no grain synthesis at all, 0.45 - and putting the grain back
brings it to 2.07. So the round trip is faithful to what the encoder was given, and two things sit
between that and the source: a decoder that does not apply film grain shows the 0.42, and a resize
has already taken 60% of what there was to measure before SVT ever sees it. `grav1synth inspect`, or
the Film Grain utility's read-the-table operation, is how to tell the two apart on a finished file.

The round trip was measured again when the solo tone-map intermediate went back to x264, that
intermediate then sitting between the source and the encoder's grain analysis where "transparent on
grain energy" had only been measured on the intermediate alone. **No intermediate sits there now** -
the AV1AN tone-map pass is gone and its tab tone-maps per chunk - so the via-the-intermediate half of
what follows describes a path this build does not take, and is kept because it is the measurement
that would have to be redone if any render step is ever put back in front of av1an. Through libsvtav1 (mainline
v4.1.0-279, the bundled ffmpeg's) and dav1d, on heavy synthetic grain at `film-grain=50` with
denoise: source HF energy 861.8, through the x264 intermediate 863.6, the AV1 with decoder
synthesis on 731.2 direct and **738.4 via the intermediate** - within 1% of each other - with the
coded picture itself at 11.3 (denoised clean, the grain living only in the table) and a no-synth
control at 13.2 (an encode without the feature strips the grain, which is the point of it). The
same session asserted the app's side of the contract out of the built assembly: SVT gets
`--film-grain 50 --film-grain-denoise 1` or `--fgs-table <path>` and never both, aomenc its
`--enable-dnl-denoising`/`--denoise-noise-level` or `--film-grain-table=` pair, table over strength
on each. The `--fgs-table` acceptance stopped being a real-machine-only check when Quick Convert's
direct-encoder harness ran it against the shipped SVT-AV1-HDR binary pulled out of a published
release - see "Driving the encoder binaries directly". A full av1an measured-grain run still is one.

**`GrainDelivery` has two values and there is deliberately no third.** A mode either hands the encoder a
strength or hands it a table; a table it cannot take is a **refusal**, naming the utility as the way to
put that grain in afterwards. `GetTableDeliveryProblem` is where that is decided, and it has three
reasons to say no: the encoder has no table parameter at all, its `--help` says this SVT-AV1 is mainline
rather than the PSY line, or the path contains a space - everything sent to an av1an-driven encoder ends
up inside one `-v "…"` string that av1an splits again on the way to the binary, and a value with a space
does not survive that split, which is the same limit the Advanced grid has always had.

An earlier cut of this quietly fell back to rewriting the finished file with grav1synth instead. It
produced the right output and it was the wrong shape: the row would have been doing the utility's job
without saying so, which is exactly what the Cut and Deinterlace division exists to stop. Refusing costs
the user one extra step and tells them what happened.

**A table the user brings can denoise too, and that tick is what makes a saved table worth keeping.**
Table mode runs no pass by default - a table is often there to put grain onto a source that never had
any, where denoising would be destroying picture for nothing. Ticking Denoise runs the same pass Measured
runs, which is the second half of the staged workflow: measure once, keep `<output>.grain.tbl`, and every
later encode of that source is Grain table file + Denoise at the same strength, with the hours of
measuring already paid. Without the tick that second encode codes the source's grain and then synthesises
more on top of it, which is the one shape of this feature that costs bitrate instead of saving it.

No encoder will denoise for a table - SVT reads its denoise flag only on the `--film-grain` path - so
this is always this app's own `hqdn3d`, and `NeedsDenoiseFilter` and `NeedsMeasurement` are separate
questions: a table mode with the tick does the first and not the second.

**The clause the readout exists for is the last one: grain synthesis only saves bitrate where the
picture being coded has had the grain taken out of it.** Measured always does; Encoder analysis and
Grain table file do it only when their Denoise is ticked, and from the outside all of them produce a
grainy-looking AV1 file. Somebody who points the row at a table without ticking Denoise has coded the
source's own grain and then put more on top. Measured on a 6-second 640x480 clip with heavy synthetic grain, SVT-AV1 preset 8 CRF
35: the grainy source encodes to 977,071 bytes, the denoised copy to 743,275, and the table applied to
that comes to 758,560 - a 22% saving with the grain back, against a post-apply on the grainy encode,
which saves nothing at all by construction.

### What can still collide, now that the row owns two of the three

The row writes at most one encoder argument, so the collision it was built to end cannot be expressed.
Three can, and each is reported separately because the fix differs:

**Precedence, not preference.** `--fgs-table` beats `--noise` beats `--film-grain`, so which of a pair
survives depends entirely on which pair has met. A strength on the row against a grid `noise` loses the
strength; a *table* on the row against the same grid row loses the **grid's**, because the table is read
first. `GetGrainSynthProblem` says which way round it went - it was written the wrong way round first,
reporting `--noise` as the winner over a table, which is exactly backwards.

**The same argument written twice.** A table on the row and an `fgs-table` row in the grid both reach
the command line and nothing here decides which SVT reads. That one names the row to clear rather than
predicting a winner.

**Retention against synthesis.** `GetGrainRetentionProblem` is for it. Retention makes the encoder's
filters and transforms stop averaging the source's own grain away; synthesis takes that grain out of the
picture and describes it instead. They are alternatives, and running both means spending bitrate and
encoding time protecting texture that is no longer in the frames. Reported only where the row actually
denoises, which on this tab is now exactly one thing - Encoder analysis with Denoise ticked - and
`tune` only at 5; a strength with Denoise unticked is *consistent* with retention and says nothing.
`Av1anUi.GetGrainRetentionProblem` therefore passes a constant clause where it used to pick between
"the source is denoised before av1an sees it" and the encoder's own flag: the pass that made the first
of those true is gone.

**The SVT-AV1 Grainy Film / 35mm Scan preset is gone**, at the user's request, and it was the only thing
that could raise that warning by being clicked: it set `tune 5` and `noise-norm-strength 2` for exactly
the retention this row now offers to replace, and its own description said it did not touch the Grain
Synthesis box - which was true of the arguments and no longer true of the intent. The check stays,
because the rows can still be typed by hand and because the Anime / Cel Animation preset sets
`noise-adaptive-filtering`, which is on the same list. The x264 and x265 presets of that name are
untouched: neither encoder has grain synthesis at all, so the row is disabled beside them and there is
nothing for retention to contradict.

**Quick Convert's two checks are not these checks, and copying them across would have been wrong twice
over.** Its collision is not three arguments fighting over which grain description SVT reads - the row
writes at most one and the ffmpeg encoders have no `--noise` and no `fgs-table` at all - it is the
*same* argument written by the row and by the grid, which is a different sentence and a different fix.
And its retention list is one entry where this one is four: three of the four are svt-av1-hdr
parameters `LibSvtAv1.json` has no row for, so none can be typed, and the fourth is `tune`, which must
**not** carry over. **`tune 5` is the fork's film grain bundle and mainline's VMAF** - the two argument
lists say so in their own descriptions - so reporting a 5 on that tab as grain retention would be
describing another encoder's parameter to somebody looking at this one. `ac-bias` is what is left, being
the same texture-preserving bias on both builds, and off by default there where the fork ships it at 1.0.

The retention list is `tune 5`, `noise-adaptive-filtering`, `noise-norm-strength` and `ac-bias` - this
file's own account of which parameters are retention rather than synthesis, which is also why none of
them ever appeared in the collision check.

Every combination was exercised headless through the real methods, with the grid rows set and the row's
controls driven: the three above fire, and Encoder-without-Denoise beside `tune 5`, `tune 0` beside
Measured, and a grid `noise` with the row Off correctly say nothing.

### aomenc's half of it, measured against 3.8.2

**aomenc has `--film-grain-table` and it works**, so both AV1 encoders take a table from the row and only
the spelling differs - which matters more now that a table an encoder cannot take stops the encode. Measured: a table passed in comes back out of the encode intact, and an encode with
both `--film-grain-table` and `--denoise-noise-level` produces a grain table byte-identical to the one
with the table alone - the same precedence SVT has, so sending a strength beside a table would be sending
a number that is silently discarded. Re-confirmed against aomenc 3.8.2 across all fourteen film stock
presets, a real `grav1synth diff` table, twelve invocation shapes and a 100,000-segment table: no
rejections. A crash reported here once was that build's WebM muxer rather than anything to do with
tables - see "What is not verified" below.

That is a narrower question than the one `EncoderArgPresets.Av1anEncoderName` refuses to ask, and the
distinction matters: that map refuses x264 because its `--help` is a short list with the rest behind
`--longhelp`, so a grid-wide check would strip parameters the binary has. aomenc prints
`--film-grain-table` in its own `--help` alongside everything else it takes. Asking one binary about one
flag it demonstrably documents is sound; widening the grid-wide map is still not.

**`--tune-content=film` is not grain synthesis, and `AomAv1.json` said it was.** Measured: an encode with
nothing but that flag comes out carrying film grain parameters that are **entirely zero** - the
signalling is on and there is no grain in it. What measures and describes the source's grain is
`--denoise-noise-level`, which is what the row writes. The row's description has been corrected; it had
been telling people that setting `tune-content=film` would denoise the picture and write a grain
description, which would have sent someone to the grid for a job the Grain Synthesis row now owns.

**The first attempt to measure that was invalid and nearly went in as fact.** It went through ffmpeg's
libaom wrapper, which has no `tune-content` option at all - so the encode ran without it and produced no
grain headers, which reads exactly like "the flag does nothing". ffmpeg said so in the line this project
already knows to watch for: "Codec AVOption tune-content … has not been used for any stream". Install
aomenc and ask aomenc.

### grav1synth, and the three things measured about it

`Media/Grav1synth.cs` runs it. Everything in that file was measured against a real build rather than
read out of the README, which is a release behind its own source in several places:

1. **Its prompts are interactive, so every call passes `-y`.** With the output already there and no
   overwrite flag it calls `dialoguer::Confirm`, which from a redirected process - every process this
   app starts - fails with `Error: IO error: not a terminal` and exit 1.
2. **Exit 0 is not success.** `inspect` on a file with no grain logs "No film grain headers found" and
   returns 0 having written nothing. Each call is judged by the artifact and by the tool's own
   "Done, wrote…" line - the same argument this file already makes about ffmpeg and `File.Exists`.
3. **Its progress bar is hidden whenever stderr is not a TTY** (`stderr().is_tty()` in its main), so a
   redirected run prints no percentage ever. There is nothing to parse; the bar goes indeterminate and
   the row says up front how long the run should take.

**The diff runs at about 7.2 megapixels a second, single-threaded.** Measured both ways round: 96
frames of 320x240 in 1.11s and 48 frames of 1920x1080 in 13.05s. That is 3.7 fps at 1080p, so a
100-minute film is **about eleven hours** of measuring before av1an is started, and a 4K feature is a
weekend. That is not a reason to hide the mode - short sources are where most people want a measured
table - but it is every reason the readout states the estimate for the loaded file rather than letting
it be discovered at hour two. `Grav1synth.EstimateDiffTime` is the one statement of that figure.

**`apply` and `remove` carry video, audio, subtitles and chapters and drop attachments** - read out of
its own stream mapping, which skips every medium but those three. On this tab that is a box the user
may well have ticked, so a post-apply on a file with attachments logs the fact.

**`grav1synth presets` prints its two blocks in two different formats, and reading them alike cost nine
of the fourteen names.** A preset is `Super8  (Based on Super 8mm film size)`; a modifier is
`-1  Fujifilm Eterna 500T`, with **no bracket anywhere on the line**. `ParsePresets` required a bracket
on both, so it found zero suffixes, returned the five bare presets, and - any non-empty parse winning
over the fallback - *replaced* `GrainSynthConfig.FallbackPresets`' fourteen with them. Every machine
that actually had grav1synth installed therefore offered `16mm` and not `16mm-1`/`-2`/`-3`, which is
the half of the list naming real film stocks (Kodak Vision3 200T and friends); a machine without the
tool kept all fourteen off the fallback, so the bug made the feature *worse* where the tool was
present. The modifier block is parsed on its own terms now - a leading `-` on the first token, which
also skips the default's untokened description line and the block's trailing `Example:`. Found by
counting the dropdown in a headless render (5 where the fallback has 14) rather than by reading the
parser, which looks right.

**It has never cut a release, so `bundle-tools.sh` builds it**, which makes it the only tool here that
needs a compiler on the runner. Two ways of doing that are wrong and both were tried:

- `cargo install --git` fetches the repository's submodules, and grav1synth carries dav1d-test-data
  from code.videolan.org - conformance clips the build never reads. A shallow clone of the pinned
  commit takes no submodules and builds identically.
- The crates.io release is not the same program. 0.2.0 has no film stock presets, no `--replace` and no
  diff filters, and its frame reader assumes the decoder's stride equals the frame width - so `diff`
  dies with "data length mismatch, expected 76800, found 92160" on an ordinary 320x240 clip. The pinned
  commit copies plane by plane with the real stride.

Pinned rather than tracking main, because this parses an AV1 bitstream and rewrites it: a regression
upstream would be found in somebody's finished encode. The build is skipped, loudly, where the runner's
architecture does not match the RID - compiling produces a host binary, so an osx-x64 job on the arm64
macOS runner would otherwise ship an arm64 binary inside an Intel zip. **osx-x64 therefore has no
grav1synth**, and that is the trade rather than an oversight.

**The Windows build ships ffmpeg's shared libraries beside it, all 168 MB of them.** BtbN's *shared* zip
is the only build of ffmpeg that carries headers and import libraries at all - the plain `win64-gpl` one
is `bin/`, `doc/` and `presets/`, with nothing to link against - so a Windows grav1synth is linked
against DLLs and cannot start without them beside it. `install_binary` could not have brought them: it
copies DLLs sitting next to the binary it found, and a cargo build's target directory has none.

**The dev headers are pinned to FFmpeg 7.1, and that is the part that actually kept Windows broken.**
`ffmpeg-the-third`, the crate grav1synth binds through, does not compile against FFmpeg 8: eleven
errors, the first being `no associated item named V410 found for AVCodecID`. BtbN's `master-latest` is
FFmpeg 8, and that is what this pointed at, so the Windows build failed at `cargo build` and 2.8.31 and
2.8.32 both shipped without the tool. Linux and macOS were never aimed at a rolling upstream - Ubuntu's
dev packages are 6.1 and Homebrew's are 7.x - so both compiled by accident of their package manager.

Reproduced locally rather than inferred, against BtbN's *Linux* shared builds, which carry the same
headers as the Windows ones: master (avutil 61) fails with that exact error, `n7.1-latest` (avutil 59)
builds clean. **A green release is not evidence the tool shipped** - both of those releases were green,
the bundler being best-effort by design, and the only way to know is to look in the archive. 2.8.32
also went out with a fix for the wrong cause: the DLLs were a real requirement and not the reason the
build was failing, which was only visible once the log was read rather than reasoned about.

**And it happened again in 2.8.65, transiently, which is the shape to recognise next time.** That
release's win-x64 job logged `[skip] grav1synth - cargo build failed - see the log above` and
`Bundled: 27 | Skipped: 1`, went green, and shipped an archive with no grav1synth in it - so the Film
Grain utility and the Grain Synthesis row's film stock presets both refused on Windows, cleanly
(`Grav1synth.DescribeMissing`) but entirely. **Nothing had changed**: no commit touched `.github/`
since 2.8.64, the pinned dev-headers URL still returned 200, the win64 shared zip still carried all
eight `.lib` import libraries, and the linux job built and bundled the tool in that same run. Two
plausible causes were tested and **both were wrong** - the crate is the `+ffmpeg-8.1` generation and
looked like it needed 8.x headers, but grav1synth at the pinned rev builds clean against BtbN's
`n7.1` headers locally (avutil 59, cargo 1.94), and the dev zip's layout was intact. A bare re-run
with no change at all produced the tool: 484,073,901 bytes of win-x64 artifact against 418,935,257.
So it was transient, and **the useful diagnostic was the artifact size, not the reasoning** - roughly
65 MB is what grav1synth plus its ffmpeg shared DLLs adds.

**And a third time in 2.8.68, permanently, which is what finally moved the fix from the pin to the
lookup.** BtbN ages a major off their rolling `latest` release once it is old enough, and n7.1 went
between 2026-08-16 and 2026-08-17 - which is exactly 2.8.67 to 2.8.68. The hardcoded
`ffmpeg-n7.1-latest-win64-gpl-shared-7.1.zip` began returning **404**, `grav1synth_ffmpeg_dev`
returned 1, and the job logged `[skip] grav1synth - could not get ffmpeg development headers for
win-x64` and went green. The 65 MB tell was there again: 420,408,591 bytes against 2.8.67's
485,618,562. Their dated autobuilds date the cutover precisely - `autobuild-2026-08-11-13-11` still
carries n7.1 assets, `autobuild-2026-08-17-13-05` carries only n8.1 and n9.0.

**The pin was never the fragility; naming a single asset was.** `grav1synth_ffmpeg_dev` resolves the
version now - each candidate major tried on `latest` first, then across the dated autobuilds, which
keep a major for a while after `latest` drops it - which is the argument `gh_api_asset_urls`' own
comment had already made about hardcoded version numbers going stale. **8.1 leads and master/n9.x are
deliberately absent**: the locked crate is `ffmpeg-the-third 5.0.0+ffmpeg-8.1`, and against BtbN's n8.1
(avutil 60) its build script probes those headers and turns its own `ffmpeg_8_1` feature on, where
avutil 61 is the master the eleven V410 errors above belong to. 7.1 stays behind it and is already out
of the scan window, so 8.1 is what builds today - measured on the runner rather than reasoned about:
a `publish=false` dispatch logged `grav1synth: building against FFmpeg 8.1 development headers`, then
`Compiling grav1synth v0.2.0`, then `[ok] grav1synth`, which is only printed once the DLLs are beside
it and `grav1synth presets` has actually run. `Bundled: 28 | Skipped: 0`, against the broken run's
`27 | 1`, and a 492 MB win-x64 zip against 420 MB.

That measurement is also the correction to a claim two paragraphs up: **"does not compile against
FFmpeg 8" belonged to an older crate**, and the 2.8.65 note's suspicion that "the crate is the
`+ffmpeg-8.1` generation" was right about the crate and wrong to dismiss. What the crate cannot build
against is avutil 61, whatever that is called this month - so read the *avutil soname*, not the ffmpeg
version, when judging a candidate.

**The encoders come from MSYS2 and that is the flakiest step in the script**, which is worth knowing
because it is no longer only the AV1AN tab's problem: since 2.8.44 Quick Convert launches `x264` and
`x265` itself, and since 2.8.68 an x264/x265 encode *refuses* without them. A win-x64 zip missing
`bin/av1an/enc/` is a build with no H.264 or H.265 encoding at all. 2.8.69's first attempt logged
`[skip] aomenc + x265 + x264 - pacman could not install: …` where the run twenty minutes before it had
installed all three, and the live mingw64 database carried every one throughout (aom 3.14.1-2, x264
0.165.r3222, x265 4.3-1) - so the runner image's MSYS2 goes stale between image releases and a rotated
keyring, an out-of-sync mirror or a half-refreshed database all land here. `bundle_msys2_encoders`
retries once with `-Syy`, which forces the refresh that `-Sy` will skip when it thinks the database is
current, and keeps the tail of pacman's own complaint in the skip line - it used to go to `/dev/null`,
which is how a skip reason ends up naming the packages and not one word of why. **Cancel the run rather
than let it publish**: nothing is released until the Publish job finishes, so `gh run cancel` on a bad
build leaves the version number free to use again.

Three things follow. Spot-check `Nmkoder/bin/grav1synth.exe` in the published zip on any release that
matters to it, the way this section already says to. Note there is **no gate** for it the way there is
for ffmpeg - deliberately, since the bundler is best-effort and a flaky upstream would otherwise block
every release - so the check is the person cutting the release, not the workflow. And when it does go
missing, **read the skip reason before theorising**: the three outages so far were three different
causes - wrong ffmpeg major, a transient cargo failure that a bare re-run fixed, and an upstream asset
that had been aged out - and only the log line tells them apart.

A `publish=false` dispatch is the cheap way to test a bundler change before it reaches anyone: it
builds every RID and attaches the archives to a **draft**, which creates no tag (GitHub only tags on
publish) and stands the Scoop-manifest step down. Delete the draft afterwards and cut the real release
normally.

**The binary is copied by hand rather than through `install_binary`, and it is the only tool here that
is.** That helper also takes every DLL sitting beside the binary it found, which is right for a
downloaded release - those are its runtime libraries - and wrong for a cargo target directory, where
they are the build's own proc-macros. 2.8.33 shipped seven of them, 11.5 MB of `clap_derive-22e3bdfb…`
and friends that nothing ever loads.

The DLLs go in before the smoke test, so what is tested is the layout that ships, and they are removed
again with the binary if it still will not run: 168 MB of ffmpeg is dead weight in a zip whose reason
for carrying it is absent. All of them are copied rather than a chosen few - which DLL pulls in which is
a property of how BtbN configured that build, not something this script can know, and a missing one is
an exe that will not start. `--features ffmpeg_static` is the alternative and was rejected: it builds
ffmpeg from source on every release.

### The passes that used to sit here

**The AV1AN tab has no grain passes any more, and this section is kept as the account of what they
cost and what has to be rebuilt if a measuring mode ever comes back.** `RenderDenoisedInput` was the
third of that tab's input passes, after the trim and the deinterlace: it wrote `{tempDir}.denoised.mkv`
and `{tempDir}.grain.tbl`, and `ToneMapPass.RunFusedAsync` wrote the denoised half as the second output
of the tone-map command whenever both would run - one source decode and one render where separate
passes cost two. `SaveMeasuredGrainTable` then kept the table beside the finished encode, since a
measured table took hours and is worth more than the encode it belongs to. All of it is deleted; the
Film Grain utility's Measure operation does the same work, once per source, and states its cost up
front. `DenoisePass` stays as that utility's pass.

Four things about it are worth carrying forward, because each was measured rather than reasoned out
and each would have to be got right again:

**"The frames that will be encoded" includes their size.** A grain table's amplitudes live in its
frames' own domain, so grain measured at the source's size is the wrong grain for the resized frames
it is synthesised onto - which is why the fused pass split *after* the folded geometry, and why an
SDR source with a resize needed a third file (`{tempDir}.grainref.mkv`) for grav1synth to have
something its own size to diff against. Any measurement that feeds an encode has to happen at the
encoded frame.

**The denoised copy carried the source's audio, subtitles and chapters, because av1an had no other
supply of them.** av1an takes every non-video track from its `-i` input: the `-a` arguments apply to
that file, and the attachment step waits on the `audio.mkv` its audio ffmpeg writes from it. That
file was av1an's input in Measured mode, and `DenoisePass` wrote it `-map 0:v:0 -an` on the strength
of a comment claiming av1an "is given the audio separately out of the original" - machinery that does
not exist anywhere in this app - so every Measured-mode encode came out silent. **Anything written
for av1an to encode has to carry the tracks.**

**`DenoisePass` is lossless, and the rule behind that is what an output is *for*.** `DeinterlacePass`
is near-lossless x264 for the Deinterlace Video utility's deliverable, a file to be looked at, where
CRF 12 is indistinguishable and a tenth of the size; the AV1AN tab's tone-map pass was the same for
the same reason, its output being only ever encoded, and that pass is gone. A file to be *measured
against* cannot be: whatever a
lossy codec adds is a difference between the two files that is not grain, grain being precisely the
small high-frequency signal a quantiser disturbs first.

**The denoiser is hqdn3d and spatial only**, its temporal halves pinned to 0. hqdn3d's temporal
filter is not motion compensated, so on anything that moves it blends the previous frame into the
current one and the difference between the two files there is a ghost rather than grain. It is not
the best denoiser ffmpeg has - nlmeans and bm3d both are - and it is the only one whose speed
survives a whole film.

**`GetPreparedInputs` still matches `.deint.`, `.denoised.`, `.grainref.` and `.grain.`, and those
four entries must stay.** Nothing in this build writes them, but earlier releases did, and two of
them are lossless FFV1 - the largest files this app has ever written. Whichever run deletes such a
temp folder is the last chance anything has to take them with it. That list already learned this
lesson once from the other side: `.denoised.` was left off when the grain modes were added, and every
measured encode leaked a lossless copy of the whole video onto the disk for good.

`ApplyGrainToOutput` runs after av1an and writes beside the output before replacing it, rather than in
place: this is a bitstream rewrite of a file that may have taken hours, from a young tool that says
itself that some videos fail to take grain properly, and the failure worth guarding against is the one
that leaves neither the original nor a working copy.

### The Film Grain utility

The card owns everything the encode rows do not: grain written onto or stripped off a file that is
already encoded - the photon noise, and the film stock presets *applied after the fact*, which is a
different operation from the encode row's identically-named mode and still worth having, since it is
the only way to put a film stock onto a file you are not re-encoding - a table read back out of
somebody else's encode, and **measuring**, which is now this card alone. Measure was an encode mode on
the AV1AN tab as well until it became clear it was hours of serial work in front of a parallel encode,
repeated on every run; here it happens once per source and the table feeds either tab's Grain table
file mode. `UtilFilmGrain` holds the four operations; a utility that writes a file and stops, like Cut
and Deinterlace Video beside it, with its own settings and nothing reaching the encode tabs.

**One card and four operations rather than four cards.** Three of them take about as long as a remux, so a
card each would give the Utilities tab three more rows for something almost nobody does twice, and they act
on the same loaded file through the same binary.

**Measure is the odd one out twice over.** It is the only operation that does not need an AV1 input - it
diffs decoded frames, so it reads the grain off a ProRes master or a DVD rip perfectly well - and it is the
only one that costs anything, which is why the dialog states the estimate for the loaded file before the
operation is picked rather than after. That estimate is the whole reason measuring belongs here rather
than on an encode row: grav1synth's diff runs at about 7.2 megapixels a second, single-threaded, so a
1080p feature is a working day, and a number that large should be read before the work starts rather
than met at hour two. The other three read and rewrite an AV1 bitstream and say so plainly when handed
anything else.

The denoised copy Measure produces goes to the session folder and is deleted, unless "Keep the denoised
video too" is ticked, in which case it lands beside the table as `<name>_denoised.mkv`. Off by default,
because it is lossless FFV1 and several times the size of the source and the table is what the operation is
for; on, it is the other half of a hand-run pipeline - the file to encode, with the table to put back
afterwards. The session folder rather than beside the source for the discarded case, so a run that dies
partway does not leave a lossless copy of somebody's film behind.

`ApplySource` is a `GrainSynthConfig` rather than three fields of the utility's own, because
`Grav1synth.ApplyAsync` already reads one - two vocabularies for "where does this grain come from" is how
they come to mean different things.

All four were run against the real binary in the command shapes this builds: `diff` on a denoised pair,
`inspect` round-tripping a table back out of an encode, `apply` from a table, from `--preset 16mm` and from
`--iso 800 --chroma`, and `remove` taking a 758,560-byte grained encode back to 743,282 with `inspect`
afterwards reporting no grain headers.

### What is not verified

**The `--fgs-table` path is no longer on this list**, which this section used to head: the shipped
SvtAv1EncApp was pulled back out of a published linux-x64 release for that measurement (and is
simply on the machine now, at `~/.nmkoder-dev/bin/av1an/enc`), so the whole chain has been run - the denoise pass as `DenoisePass` builds it, the diff, an SVT-AV1
encode of the denoised file, `apply` with the resulting table, an `inspect` round trip, and the film
stock presets' own `MakePresetTableAsync` → `--fgs-table` → `inspect` comparison recorded above. The
table format is aom's own `filmgrn1`, which is what the parameter takes.

**What is still a real-machine check is the AV1AN tab's own delivery**, and it is worth being precise
about which half: the SVT measurements above were made by launching `SvtAv1EncApp` directly, which is
exactly what Quick Convert's `DirectSvtAv1` does, so that tab's path is field-verified. On the AV1AN
tab the same table path lands inside av1an's `-v "…"` string and is re-split on whitespace before it
reaches the binary, and no av1an executes in a web session - so the space refusal is reasoned from
this file's own established limit rather than measured for this argument. A full av1an
*measured-grain* run is likewise still a real-machine check.

**An aomenc grain-table crash was reported here and it was a misattribution - the lesson is the
control, not the crash.** Ubuntu's aomenc 3.8.2 aborts with `*** buffer overflow detected ***` on
every grain table fed to it, which was written up as a pre-existing fault in Grain table file. It is
not a grain-table fault at all: **that build's WebM muxer aborts with no table on the command line
either.** Measured across the three output containers, table and no table: `.webm` aborts in both
cases, `--ivf` and `--obu` succeed in both, and through IVF the grain lands correctly - the output's
parameter block is byte-identical to the table's segment 1. The app never meets it, writing `.ivf`
for aomenc (see "Driving the encoder binaries directly"), and av1an owns the output on the other tab.

What produced the wrong conclusion was running four different *tables* and no run *without* one.
Four tables failing looks like "tables are the problem" and is equally consistent with "this command
shape always fails" - and the two are told apart by the control, which costs one run. A second
opinion disagreeing is what prompted re-testing; the disagreement was real and neither side had it
right, since the tables genuinely do abort under that command and genuinely are not the cause.

Table content *can* crash libaom, but nothing grav1synth writes gets near it: hand-crafted
`ar_coeff_lag=9` aborts with `free(): corrupted unsorted chunks`, `chroma_scaling_from_luma=1` beside
non-zero cb/cr points trips an assert in `bitstream.c`, and scaling-point counts past the array bounds
are a **silent** heap overflow that this build does not trap at all. Across 20,576 `p` lines and 61,728
scaling lines of grav1synth output, sY tops out at 14, sCb and sCr at 10 - exactly aom's limits -
`ar_coeff_lag` is always 3, and there are no declared-versus-actual count mismatches.

One real trap does remain: a table with **CRLF** line endings parses to no grain, rc=0, no warning.
Nothing here writes one, but a table round-tripped through a Windows editor would fail silently.

What the rows themselves were verified against, headless through the real `MainWindow` and the built
assembly: both tabs offer the same four modes and neither offers Measured or Photon noise; both rows'
Denoise ticks ship ticked, and an AV1AN Preset config comes back `Denoise`/`DenoisesSource`/
`NeedsDenoiseFilter` true with `hqdn3d=4:3:0:0` appearing in the real per-chunk chain built by
`GetVideoFilterArgs` over a loaded fixture - and gone again, with the readout back to "adds grain
rather than saving", once the tick is cleared; a Preset config reads as `UsesTable`,
`NeedsPresetTable`, `NeedsGrav1synth`,
not `IsUtilityOnly`, and owning `fgs-table`; `Measured` still reads as `IsUtilityOnly` and its message
names the Measure operation rather than the already-encoded wording photon noise gets; the av1an
command built for SVT-AV1 carries `--fgs-table <path>` inside its `-v "…"` string with no
`--film-grain` strength beside it, and the space refusal fires for a generated path containing one and
stands down for one without; the preset
dropdowns hold all fourteen names, the widest of which (`Modern35-1`) measures 137 against the box's
150; each readout sits below its dropdown sharing a left edge; and a zscale tone map beside a
denoise-wanting grain plan no longer pulls a pass in front. That last check's other half - that
libplacebo still did - is moot rather than merely superseded: there is no pass on this tab at all now,
so no grain plan can pull one, and re-running that assertion would fail on a tab behaving correctly.

