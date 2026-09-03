---
name: cadence-repair
description: The full record of the Repair Frame Cadence utility - removing the duplicate frames a capture's TBC padded in and writing a constant-rate copy. Placement by timestamp with content only as the tie-break, the index-resampling that shipped 6.99 s of drift while both ends lined up, the half-step tie-break window, the worst-placement-error check that replaces every duration comparison, the y4m loss of field order and colour and setparams as the only repair, the ffprobe key=value probe that a diagnostic cannot fake, the three VapourSynth source plugins' random-access behaviour across ten MPEG-2 and modern samples, and DGIndex/d2vsource fetched, run and rejected. Load for CadenceRepair, UtilRepairCadence, a padded capture, duplicate frames, cadence, TBC, a VHS or Hi8 capture whose frame count does not match its duration, DeleteFrames, PLAIN_ORDER, bestsource, ffms2, lsmas, d2vsource, placement error, or FPS_NUM/FPS_DEN in a VapourSynth script.
user-invocable: false
---

# Repairing a padded capture - the full record

CLAUDE.md's `## Repairing a padded capture` section is the digest: it carries the rules that have
to hold whatever you are doing in this area, and points here. This is the whole of it - every
measurement, every trap, and the account of what was got wrong and why it looked right.

It was moved out of CLAUDE.md **verbatim**, byte for byte, so nothing below is paraphrased and
nothing was dropped - in the second split, on 3 September 2026, after the file had grown back by
58 KB in the fortnight since the first. That is also why passages below that say "this file"
mean **CLAUDE.md**, where this text used to sit - they were not rewritten, because rewriting them
would have been the one way to lose something in the move.

CLAUDE.md remains the authority on the project and its conventions govern this file too. A new
finding here goes here, in the house style `.claude/skills/record-finding` sets out; a new *rule*
that can be broken from outside this area goes in the CLAUDE.md digest as well.

## Repairing a padded capture

The Repair Frame Cadence utility, which removes the duplicate frames a capture inserted and writes a
constant-rate copy. `CadenceRepair` holds the script and the run, `UtilRepairCadence` the task.

**Every rate conversion in this app decides what to drop from the timestamps alone and never looks at
the pictures, which is the half this utility does differently.** ffmpeg's `-fps_mode cfr` and a
VapourSynth source plugin's `fpsnum` are the same idea twice, so the ffmpeg deinterlacers and QTGMC
inherit the same fault. Measured on 15319 coded frames decimated to 10936: the timestamp-only result
leaves **560 adjacent-identical pairs, 5.12% of its own output**, and the total count being right
means each one cost a real frame. That is **12.8% of the padding identified wrongly**, about 1.5
visible hitches a second.

**The content tie-break earns its place against that, and the margin is modest - do not quote it as
though it were the old algorithm's.** Measured on the 30 s cut by running each selection over the same
source frames, with no encode in the way (`PlaneStatsDiff` is normalised 0-1, so the thresholds are
1e-5/1e-4/1e-3 - reading them as 0-255 makes 90% of any file look duplicated):

| selection | kept | <1e-4 | <1e-3 | worst drift |
|---|---|---|---|---|
| shipped: time places, content breaks ties | 900 | 16 | 83 | **0.059 s** |
| tie-break window widened to 2 steps | 899 | 10 | 87 | 0.092 s |
| pure nearest-in-time, no content at all | 901 | 21 | 98 | 0.045 s |
| the old index/cycle selection, content only | 901 | **0** | **14** | 0.127 s |

So the tie-break beats a pure timestamp pick at every threshold - which is the claim worth keeping -
and the **old content-only selection beats both, by a lot.** That is not a reason to go back to it:
it optimises for exactly the thing being counted and pays for it in the one that ruins a file. Nine
percent of frames sitting within 1e-3 of their predecessor is a mild softness; seven seconds of audio
drift is a memory nobody can watch. State the trade rather than claiming the new selection dominates.

**The old selection's drift is 0.127 s here and was 6.99 s on the real file, which is the whole reason
it shipped.** Index-resampling error grows with length; a 30 s fixture cannot show it. Every check
that mattered was run on the short cut, passed, and proved nothing about the 95-minute capture the
utility was written for. **Validate a length-dependent fault at length**, or at minimum report the
placement error, which is a per-frame quantity and does show up on 30 s.

**The timestamps say *when* an output frame belongs and the content says *which* of the frames near
that moment to take. Dropping either half breaks it in a way the other half cannot show.** This
section used to say the timestamps were "the damaged part" and were ignored entirely, and the code
did exactly that - picking frames by index at a constant keep-ratio, in cycles chosen to bound the
local drift. It is wrong, and it shipped to a user: choosing by index assumes the coded frames are
spread evenly through the recording, and a padded capture's are not. Measured on the 95-minute
capture this was written for, the finished file ran **up to 6.99 s ahead of its own audio** at 37
minutes in, and −4.06 s at 16% - converging to ~0 at **both ends**. Placing every frame against its
own timestamp, with the content used only to break ties among the frames at that instant, holds the
worst error to **0.054 s** over that file and **0.059 s** on a 30 s cut - bounded by the source's own
jitter rather than accumulating. The timestamps are damaged *individually* - jittering between 8 and
79 ms where 33 is due, and running backwards thousands of times - and that is not the same as
worthless: their trend is the only record of when each picture belongs.

**So the check is the worst placement error over every frame, never a comparison of durations.** The
endpoints agreeing is precisely the signature of the bug: total length, frame count and end-to-end
sync were all exact while the middle was seconds out, so every check that looked at the ends passed
and the file was handed over as verified. The number the script prints is the largest gap between
where a kept frame belongs and where it was put, across the whole file; one frame is 33 ms, so
anything reading in seconds is drift.

**The tie-break window is half an output step and must not be widened.** It exists to prefer a real
frame over a repeat among the frames sitting at the same instant - timing first, content second.
Measured, allowing two steps let a higher-difference frame be fetched from 67 ms away and pushed the
worst placement error to 92 ms, which is inside the range where lip-sync is noticeable.

**y4m carries a frame size, a rate and a range and nothing else, so the field order *and* the colour
are both lost piping VapourSynth into ffmpeg - one problem with one fix.** VSPipe's header reads
`... F30000:1001 Ip` - `Ip` is *progressive* - so ffmpeg marks every frame progressive on the way in.
Measured against a `tt` source through a real VSPipe producer: `-x264-params tff=1` alone gives
`field_order=progressive`; `-flags +ilme+ildct -x264-params tff=1` gives **`bt`, the wrong parity**;
`-vf setparams=field_mode=tff -x264-params tff=1` gives `tb`, which is right. The middle row is the
one to remember - it looks like it worked, and leaves the next deinterlace running at the wrong parity
on a file just "repaired". The same four flags read `tb` for all of them when the y4m comes from
ffmpeg rather than VSPipe, so a test that does not use the real producer proves nothing.

**The colour goes the same way, and the output AVOptions cannot put it back - the frame's own
properties beat them.** Measured through the repair's exact pipe shape on a capture declaring
bt470m/bt470m/bt470bg tv, reading primaries/transfer/matrix/range back off the result:
`-color_primaries bt470m -color_trc bt470m -colorspace bt470bg -color_range tv` gives **unknown,
unknown, bt470bg, tv** - two of four honoured and two dropped in silence - and the same four written
numerically (4/4/5/1) gives exactly the same thing.
`-vf setparams=color_primaries=bt470m:color_trc=bt470m:colorspace=bt470bg:range=tv` gives all four.
The same command reading the source as a **file** rather than through the pipe tags all four
correctly, so this is specific to piped y4m and a test that skips the pipe proves nothing here either.
`setparams` sets the field order too, which is why it replaces `setfield` rather than sitting beside
it. What it costs to get wrong is downstream and quiet: the AV1 encode reading a repaired file was
handed `--color-primaries 2 --transfer-characteristics 2 --matrix-coefficients 2`, leaving every
player to guess the matrix of a file that had said precisely what it was.

**A fix that changes only the spelling is not a fix, and this one shipped as one.** The first cut of
the colour repair read the values as ffprobe's *names* rather than as `VideoColorData`'s numbers, on
the theory that ffmpeg was refusing the numeric spelling - and the finding was written up that way,
confidently, with the half-tagged output as its evidence. The numbers and the names behave
identically; the spelling was never what decided it. What settled it was running the AVOption and
`setparams` forms against each other **through the real pipe**, which is a different question from
whether the command parses.

**The colour repair was got wrong a third time, and this time nothing about the command was wrong -
the value never arrived.** The four properties are probed off the source and re-stated through
`setparams`, and each was asked for *bare* (`-of default=noprint_wrappers=1:nokey=1`), taking the
first non-empty line of what came back. ffprobe writes its diagnostics to the same stream as its
answer, so a bare value is not distinguishable from a complaint - and on a capture cut mid-audio-frame
`[mp2 @ 0000026f1f808e80] Header missing` arrives **first**. That line failed the character guard that
exists to keep a `:` or an `=` out of the filter graph, so all four properties were dropped,
`setparams=field_mode=tff` went out alone, and the repair wrote a file tagged `unknown` for primaries,
transfer and matrix **while reporting success** - the "every player left to guess the matrix" outcome
this section already warns about, reached from the other end rather than through the AVOptions.

Why it survived every earlier check is the disguise: the *same file* probes clean when nothing
complains, so the bug needs a source ffprobe has something to say about, and the complaint is about the
**audio** while the values being lost are the video's. The fix is `key=value` in one call rather than
four bare values, matched on the `key=` prefix - a diagnostic cannot be shaped like one. That is robust
whatever the log level, which is the point: the only other `nokey=1` call in the app,
`Av1anSceneDetect.GetDurationSecondsAsync`, is safe **solely** because its `LogLevel` is `quiet`, and a
correctness resting on a log level is one edit away from being lost. Do not re-narrow this to a
`LogLevel` change on those grounds.

Verified by running the real repair on the file that reproduced it, against the bundled BtbN build:
`setparams=field_mode=tff` and `unknown/unknown/unknown/tv` before, and
`setparams=field_mode=tff:color_primaries=bt470m:color_trc=bt470m:colorspace=bt470bg:range=tv` with
`bt470m/bt470m/bt470bg/tv` after, `field_order=tb` still preserved from a `tt` source; plus the clean
probe, which yields the same four values, and a file that genuinely states no colour, which yields four
`unknown` lines and is correctly left alone. Not verified: a diagnostic that happens to *begin*
`color_space=` would still fool the prefix match, and nothing has been seen to produce one.

**The three source plugins decode this file identically and answer a *backwards* request three
different ways, one of them silently wrong.** Measured on the same 1259-frame capture: sequentially
all three are exact and agree with each other frame for frame, 0 of 1259 differing between any pair;
asked for frames forward-with-gaps - which is all `DeleteFrames` ever does - all three are exact
again; asked for frames out of order, **lsmas raises "failed to output a video frame", ffms2 answers
971 of 1259 requests with the wrong picture and no complaint at all, and bestsource gets every one
right.** So "which plugin" is free on a sequential read and decisive on a random one, and the failure
that matters is ffms2's, being the quiet one.

The repair therefore names `bestsource` first through `PLAIN_ORDER` while the deinterlace script keeps
`lsmas`. Not because the repair was wrong - it was measured correct on lsmas, byte for byte the same
output - but because its correctness rested on nothing except `DeleteFrames` happening to ask in
order, which is an invariant nobody had written down and any later filter could break. `PLAIN_ORDER`
is the one place that choice is stated, per script. Note that bestsource reports this file's rate as
`4680000/117031` where lsmas says `30000/1001`; only the frame *count* is read here and `AssumeFPS`
states the output rate, so it changes nothing - expect it in the log rather than re-diagnosing it.

**Measured across ten downloaded samples, no source plugin is safe on MPEG-2 and all three are
perfect on everything else - and the reason first given for ffms2's wrongness was wrong.** This file
used to say the divergence was "measured on the same file", with the guess offered alongside it that
ffms2's silent errors were down to that capture's damaged timestamps and that a clean MKV would be
fine. Half of that survives. Sources: ffmpeg's own sample archive (`dvd.mpeg`, `broken-ntsc.mpg`,
`interlaced/burosch1.mpg`, `mpeg2_field_encoding.ts`, a VOB) and test-videos.co.uk (H.264 MP4, VP9
WebM, AV1 MP4), plus an MKV remuxed from the first, first 600 frames of each, forward-with-gaps and
true-random against a sequential reference:

| sample | lsmas | ffms2 | bestsource |
|---|---|---|---|
| MP4/H.264, MKV/H.264, WebM/VP9, MP4/AV1 | clean | clean | clean |
| `dvd.mpeg` (progressive PS) | clean | clean | clean, but **1 frame of 600 decodes differently from both others** |
| `burosch1.mpg` (interlaced PS) | clean | **132 of 600 wrong on random access** | clean |
| `broken-ntsc.mpg` | **refuses**: "repeat requested for 1438 frames by input video, but unable to obey" | clean | clean |
| `mpeg2_field_encoding.ts` | clean | clean | **refuses**: "No frame returned for frame number 30" |
| the padded capture | random access errors | 446 of 600 wrong | clean |

So the *class* is right and the *cause* was not: ffms2's silent wrongness is not a symptom of damaged
timestamps, because `burosch1.mpg` is an ordinary test pattern and shows it, while the VOB - also
interlaced MPEG-2 - does not. And bestsource is not the universal answer the paragraph above implies:
it is the only one that refuses a field-encoded transport stream outright, and the only one that
disagrees with the other two about a frame of a clean DVD stream. Each of the three has at least one
hard failure or silent error somewhere in five MPEG-2 samples, and never on the same file as another.

**Which is the argument for trying rather than declaring.** A StaxRip-style table keyed on extension
cannot express this: `.mpg` alone wants bestsource on one file, ffms2 on the next and lsmas on a
third. Only the ordered attempt list with a validating check copes, and that is what `open_video` is.

The operational half: **forward-with-gaps access is exact on all three plugins across all ten
samples**, 0 wrong in every cell. That is the only pattern `DeleteFrames` produces, so the cadence
repair is safe whichever plugin wins its fallback - which is what makes naming bestsource first a
defence against a *future* filter rather than a fix for a present bug.

**The one disagreement that looked alarming is an off-by-one, and it was settled by asking ffmpeg.**
`mpeg2_field_encoding.ts` had lsmas and ffms2 returning different pictures for 30 of its 41 frames,
with bestsource - the obvious tie-break - refusing the file, and *its own suggested remedy of
`threads=1` does not help*, nor does d2vsource, which refuses it too. The reference is therefore
ffmpeg itself, per-frame luma through `signalstats`: it decodes **31 frames from the stream's 41
packets**, the leading ones being undecodable ("Invalid frame dimensions 0x0", "ac-tex damaged").
Both plugins report 41 - the packet count - and pad the head with held copies of the first picture,
lsmas with 11 and ffms2 with 12. At those offsets each reproduces **30 of ffmpeg's 31 frames
exactly**, differing only on the final partial one, and they match *each other* on 40 of 41 at a
shift of one. So neither decodes anything wrongly; they disagree by a single frame about where real
content begins in a damaged stream, and both overstate its length.

**Getting that answer needed the right tolerance, which is the trap worth keeping.** The content is
nearly static - every frame's luma average lies between 0.2624 and 0.2707 - so the first comparison,
run at 0.002, manufactured agreement out of neighbouring frames and reported lsmas matching 31/31
against ffms2's 30/31, which reads as "lsmas is right and ffms2 is not". At 1e-5, which is still
fifty times the rounding error of the four decimal places `signalstats` prints, both come out at
30/31 and the real relationship - a one-frame offset - appears. On near-constant material a loose
tolerance does not blur a comparison, it invents one.

**DGIndex and d2vsource were fetched, run against this file and deliberately not adopted** - which is
worth writing down, because "why does this not use the proper MPEG-2 source the way StaxRip does" is a
reasonable question to ask later. StaxRip's default route for an `.mpg` really is a different one: a
demuxer in `Demux.vb` (`InputExtensions = {mpg, mpeg, vob, m2ts, m2v, mts, m2t}`, `InputFormats =
{mpeg2}`, active by default where its eac3to and D2V Witch siblings set `.Active = False`) runs DGIndex
into a `.d2v`, and the source-filter table then maps `d2v` to `core.d2v.Source`, so the `*` default of
ffms2 never sees the file.

Measured here with DGIndex 1.5.8 (GPL-2, `rlaphoenix/DGIndex`) indexing in 0.4 s and d2vsource 1.3
(LGPL-2.1) loading cleanly into R72: **it decodes this file identically to what is already bundled -
0 of 1259 frames differ from bestsource** - reports the same 1259 coded frames, so it removes none of
the padding and the repair is needed either way, and it **fails the same random-access test lsmas
fails** ("Seek pattern broke d2vsource! Please send a sample"). Sequential decode is 3850 fps against
bestsource's 2896, which on the full file is 20 s against 26 s inside an hour-long encode.

The one thing it does better is read `Frame_Rate=29970 (30000/1001)` and `Field_Operation=0` out of the
MPEG-2 sequence header rather than from timestamps - genuinely the right way round, and exactly nothing
this app needs, since `avg_frame_rate` already answers that (see `Reading what the tools print`). So the
cost is a Windows-only 2010 binary, a 15.8 MB plugin, a per-file pre-index step and a new intermediate
format, against no measurable gain. D2V Witch, the open-source indexer, is worse still to bundle: it
ships as a bare exe needing Qt5 plus ffmpeg 4.x shared libraries (`avutil-56`, where the bundle carries
`avutil-61`).

**`FPS_NUM`/`FPS_DEN` are `open_video`'s names and mean "rebuild the clip at this rate".** Naming a
script's *output* rate that way hands the padded file to the very conversion the script exists to
replace: caught by running it, the repair opened 900 frames of a 1259-frame file and reported nothing
to decimate. The repair script sets both to 0 and calls its own rate `OUT_FPS_NUM`/`OUT_FPS_DEN`.

**It is a utility rather than something an encode tab does on the way past**, for the reason the
Deinterlace Video utility gives plus two of its own: the metrics pass is a full extra decode, and one
repaired file fixes every downstream path rather than one. The judgement is also not one to make
silently - deciding a file's frame count is wrong and its container right is a claim about somebody's
capture, and `CadenceRepair.TargetFrames` records that the opposite case exists and cannot be told
apart from these two numbers alone. It has no settings; the recording's own length is the answer. A
file whose frame count already matches its length is **refused** rather than copied.

Verified through the real `MainWindow` and `RunTask`, on two cuts of the capture it was written for.
A 30 s cut: 1259 coded frames → 900 at 30000/1001, worst placement error **0.059 s**, all four colour
properties (`bt470m`/`bt470m`/`bt470bg`, range `tv`) round-tripping from source to output,
`field_order=tb` preserved from a `tt` source, 30.03 s of video against 29.98 s of audio. A 6-minute
cut: 15305 → 11060, worst placement error **0.054 s**, same colour and field order, 369.035 s of video
against a 369.041 s source - and it exercised the timestamp-count mismatch path on the way (ffprobe
counted 15307 where bestsource decoded 15305, trimmed rather than refused). Feeding the repaired file
back in is refused, `1x`, no output written, which is the round trip proving the output is genuinely
constant-rate. The full 95-minute run was made and its worst placement error was 0.054 s across
172,024 frames.

What is **not** verified: nothing here establishes that the *real* frames were themselves captured at
even instants - a frame count matching the recording only proves none were lost - and the placement
error is measured against the source's own timestamps, so it bounds this utility's contribution to the
drift and not the capture hardware's.

