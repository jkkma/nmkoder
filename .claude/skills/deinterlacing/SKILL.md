---
name: deinterlacing
description: The full record of Nmkoder's deinterlacing and of its trim and cut handling, which live together because a trim is the one thing QTGMC cannot compose with. Covers interlace detection and the field-gap test, QTGMC through VapourSynth and its preset-dependent plugin set, the per-tab defaults and resets, why the AV1AN tab has no QTGMC and why DoubleRate must be set false explicitly, all three trim modes and what each seek actually lands on, the keyframe-snapping dialog, the stream-copy cut's two extra frames, and the Deinterlace Video utility. Load for deinterlace, interlaced, QTGMC, bwdif, yadif, idet, field order or VapourSynth plugins - and for trim, cut, seek, -ss, keyframe or TrimSettings work.
user-invocable: false
---

# Deinterlacing - the full record

CLAUDE.md's `## Deinterlacing` section is the digest: it carries the rules that have to hold
whatever you are doing in this area, and points here. This is the whole of it - every
measurement, every trap, and the account of what was got wrong and why it looked right.

It was moved out of CLAUDE.md **verbatim**, byte for byte, so nothing below is paraphrased and
nothing was dropped. That is also why passages below that say "this file" mean **CLAUDE.md**,
where this text used to sit - they were not rewritten, because rewriting them would have been
the one way to lose something in the move.

CLAUDE.md remains the authority on the project and its conventions govern this file too. A new
finding here goes here, in the house style `.claude/skills/record-finding` sets out; a new *rule*
that can be broken from outside this area goes in the CLAUDE.md digest as well.

## Deinterlacing

**Both encode tabs hide the Deinterlace row for a file with no fields worth discussing**, and on
Quick Convert the setting behind it defaults to QTGMC at Very Slow. A Hi8 or VHS capture therefore
arrives with the best deinterlacer there is already selected, and a modern download never shows the
control at all. The AV1AN tab offers no QTGMC and defaults to Automatic - see the section on it
below, and `DeinterlaceUi.Av1anModes`.

`DeinterlaceUi.IsRowRelevant` decides which a file is, and it is true for two different reasons. One
is the obvious one: the verdict says interlaced. The other is that the file's fields were actually
**measured** - `Scanned`, which `InterlaceDetect` only sets for a file whose container says nothing
about its scan type - and there the row appears whatever the measurement concluded. That second
clause is the escape hatch: the counters are the part of the verdict that can be wrong, and a capture
that scored just under the line is exactly where a person can see combing that the scan missed. It
does **not** cover a container that lies outright, because a file flagged progressive is believed
rather than measured and never reaches the scan - that one still goes to the Deinterlace Video
utility, which deinterlaces whatever it is given.

**Showing the row is not arming it, and two separate things see to that.** An engine picked by name
deinterlaces whatever it is handed - `Deinterlace.ResolveAsync` consults the verdict only for
Automatic - so a QTGMC default reaching a progressive file would start an hours-long pass on it.
`ApplyScanVerdict` puts the mode on Automatic for anything not called interlaced, so a row that
appears over progressive video appears switched off, reading "this file is progressive, so nothing
will be deinterlaced" until somebody picks an engine in it. And `ModeInEffect` reports Automatic
whenever the row is off screen entirely, whatever the box behind it says - which covers the gap
between a file being loaded and its scan landing, since Automatic is the one mode that is safe
without knowing anything: `ResolveAsync` waits for the verdict itself.

`ApplyScanVerdict` runs where the verdict is *measured* rather than every time a file is looked at,
so re-selecting an already-scanned file in the list keeps an engine picked by hand.

**Its two halves are not the same kind of thing, and 2.8.14 shipped them treated as one.** Demoting a
file that is *not* interlaced to Automatic is a safety measure and happens whatever the settings say -
Automatic does nothing to progressive video, so nothing is taken away by it. Selecting `DefaultMode`
for a file that *is* interlaced is a preference, and it happens only where Reset On New File is set to
clear the deinterlace mode. Treating that half as unconditional meant a user who had turned the reset
off to keep bwdif for a queue of tapes had every file of it moved back to QTGMC - an hours-long pass
each on the AV1AN tab, and the exact surprise the whole feature exists to prevent, arriving through a
different door.

**The safe half has to be undone as well as done, and for a while it was only done.** Demoting a
progressive file to Automatic is right for that file and wrong for the rest of the queue: the
interlaced files after it take the early return above, which keeps whatever is in the box rather than
putting anything back, so one progressive file among a stack of tapes ran everything after it on
Automatic - the setting the queue was configured not to use. `DeinterlaceUi` remembers the engine a
*person* picked, per tab, and reinstates it wherever the early return fires; `writingModeBoxes` is what
keeps the demotion from being recorded as the choice it is meant to be undoing, the same shape as
`Av1anUi.writingWorkerCount`. `ResetModes` clears the memory as well as the boxes, a reset being
somebody asking for the default back.

**And the encode settles the verdict before it reads the box.** `AnalyzeInBackground` is
fire-and-forget so that loading a file does not wait on a few hundred frames being decoded, but a
batch starts each encode the moment its file is loaded - so in 2.8.14 the two raced, and which engine
ran depended on who won: a file with a container flag answered fast enough for its verdict to land
first, while one needing a real scan had its encode read the *previous* file's mode.
`DeinterlaceUi.EnsureScanVerdictAsync` is what both `Av1an.Run` and `QuickConvert.Run` call first, and
it costs one await of an answer they are about to wait for anyway - `Deinterlace.ResolveAsync` asks
for the same verdict a moment later.

**An engine picked by name must not outlive the file it was picked for.** What made that a trap was
that the mode was sticky and nothing cleared it: it was saved per tab and restored at startup, so a
QTGMC picked for a tape was still armed days later, and on the AV1AN tab that is a full pass over the
video into a lossless intermediate before av1an starts. 2.8.12 shipped that - a progressive 1080p
WEB-DL got hours of QTGMC Very Slow and 47.952 fps of interpolated fields, with nothing wrong anywhere
in the detection, which had read it correctly and said so on screen. Hiding the row is what closes
that case for good; the resets below still matter for the file the row *is* shown for.

`ResetSettingsOnNewFile.ResetDeinterlace` is the other half, on by default beside Trim and Crop - the
three whose value describes the file that was just replaced rather than how the user likes to
encode. `DeinterlaceUi.ResetModes` puts each tab back to its own default - `DefaultMode` for Quick
Convert, `Av1anDefaultMode` for the AV1AN tab - and touches neither the preset nor the field
doubling, which say *how* to deinterlace rather than *whether*. Only where a person loaded the file:
a batch clears each one with `resetSettings: false`.

That setting is also what `ApplyScanVerdict` reads before it selects an engine for an interlaced file,
which is what makes turning it off mean something across a queue: a stack of tapes keeps the engine
picked for it, where with it on each file gets the default the verdict asks for.

The startup half of that trap is closed at the other end now - the AV1AN tab restores nothing across
sessions at all, so its mode is the default on every launch whatever was picked last time. Quick
Convert's is still saved, and still relies on this reset, because deinterlacing there is one filter
in a chain rather than a pass of its own.

The default is stated in exactly three places and nowhere else: `DeinterlaceUi.DefaultMode` for Quick
Convert's engine, `DeinterlaceUi.Av1anDefaultMode` for the AV1AN tab's, and `Qtgmc.DefaultPreset` for
the preset. The last is not only a default - it is also the
fallback for an empty preset box and, through `Qtgmc.NeedsNoisePlugins`, the thing that decides which
plugin set has to be present, since Very Slow is one of the two presets that turn QTGMC's noise
processing on and pull in `fft3dfilter`. Moving it moves what the probe and the release check verify.

A default added to that list has to reach the configs that already exist, and defaulting on a first
run does not - a setting added after a list was written is missing from that list in exactly the way
it is missing on a first run. `Load` therefore defaults **anything the saved list does not name**,
which covers both, and a setting the user turned off is saved as `False`, which names it, which is
what keeps it off. Adding one to `onByDefault` is the whole change; the old first-run-only branch
would have shipped this fix to nobody who already had the app.

**What decides "interlaced" is two things, in order.** The container's own field-order flag
is free - ffprobe has already reported it by the time a file is loaded - and for the formats
this matters most for it is not a guess: an MPEG-2 tape capture writes `tt` in its sequence
header, DV writes `bb`. A flag that says nothing is the case worth spending time on, and
there `InterlaceDetect` decodes a few hundred frames through ffmpeg's `idet` filter. A flag
that says `progressive` is *believed* rather than checked, because checking it would put a
multi-second scan in front of loading any modern video to catch a case one click settles.

The `idet` reading needs two conditions, and the first is the one doing the work: interlaced
video has *one* field order for the whole file, so a real interlaced source puts essentially
every combed frame in the same bucket, while idet's false positives split roughly evenly.
Measured here on a 720x480 synthetic pattern with no fields in it at all, idet reported
TFF 79 / BFF 96 / progressive 27 - which "more combed frames than progressive ones" alone
calls interlaced, and which the three-quarters-one-way rule rejects. The genuinely interlaced
clips scored 202/0 and 0/202. The second condition is a volume bar, and it is deliberately
low: idet cannot see combing in a frame that does not move, so a tape with quiet stretches
scores well under half.

**Counting combed frames cannot settle it on its own, and a third condition is what makes the
answer trustworthy.** A vertical pan over fine horizontal detail shifts the picture by a
fraction of a line per frame, which frame by frame is indistinguishable from two woven fields -
and unlike the fine detail the three-quarters rule catches, a pan holds one direction, so the
false comb comes out *consistently one way round*, which is the very thing that rule reads as
proof. Both count-based clauses fall for it: measured here, a 720x480 pan scored BFF 167 /
progressive 283 (through the volume bar) and another BFF 120 / progressive 73 (through
"more combed than progressive"). Every deinterlacer this app can reach was being started on
progressive video.

`InterlaceDetect.MeasureFieldGaps` asks the one question a pan answers differently. Split every
frame into its two fields and measure each field against the field before it: in interlaced
video every one of those gaps is half a frame of time and they all come out the same size,
while in progressive video the two fields of a frame are the *same* instant - they differ only
by the one line of vertical offset - and the next pair straddles a frame boundary carrying all
of the motion, so the gaps alternate small, large, small, large. Combing is deliberately not
what is measured; that is the point, since nothing about fine detail changes when the two
fields turn out to have been shot at the same moment.

The numbers, all measured rather than picked: genuinely interlaced 1.00-1.02 at 720x480, 1080i
and 192x144 alike, a tape whose first 25 seconds are a still caption 1.03, 3:2 pulldown 1.02, a
file weaved one way for half its length and the other way for the rest 1.27-1.88, and a genuine
source measured at the *wrong* parity 2.41. Progressive sources that idet calls combed sit at
6.9 and up. The threshold is 4, near the midpoint of what is left, and it leans towards keeping
whatever the frame counts decided because the two errors are not symmetric - overturning a real
tape leaves the combing in, where letting a false positive through only softens a progressive
file.

Three things it must keep doing. It is asked **only** where the counts already said interlaced,
and it can only ever overturn that, so a file it cannot measure keeps the count-based answer.
It is given **the parity idet just reported**, because `separatefields` hands over the fields in
the order the frame claims and the wrong one pairs each field with the neighbour on the wrong
side - that is where the 2.41 comes from, and it is why the threshold sits above it. And it
gives up rather than guessing when there is no motion to measure (`MinFieldMotion`): a still
sample is one zero divided by another, which is exactly the quiet tape the volume bar exists
for. The pass is narrowed to 256 pixels wide first, horizontally only, so every line the fields
are made of survives untouched - it is both cheaper and cleaner that way, since averaging across
the width takes horizontal noise out of the difference.

**QTGMC runs in VapourSynth, so ffmpeg cannot call it.** `Qtgmc` writes a `.vpy` and the
command becomes `vspipe … | ffmpeg …`, with the pipe added as the *last* `-i` so that every
input already on the command line keeps the number the stream maps were built against; only
the first video track is remapped, to `{pipe}:v:0`. The pipe goes in *front* of ffmpeg on
purpose - the shell reports the last command's status, and ffmpeg has to stay last for a
failed encode to still read as one.

Which means a VapourSynth failure is invisible to ffmpeg: a script that dies two thirds of
the way through is end-of-stream on stdin, and ffmpeg finishes normally and exits 0 over a
file missing its last hour. VSPipe's stderr therefore goes to a log file which
`Qtgmc.ReadRunProblem` checks for VSPipe's own "Output N frames in …" line afterwards - once
per pass, which is why a two-pass encode expects two of them.

**The progress bar is measured against `vspipe --info`, not against the container.** ffmpeg's
`time=` is scaled by the duration ffprobe read out of the file, and a tape capture's duration is
whatever its capture card's timestamps claimed: a 3.3 GB MPEG-PS reporting 59:56 was still
encoding at 01:18:36, so the bar sat on 100% for twenty minutes of an encode that was working
perfectly. VapourSynth is not guessing - its source plugin has indexed the file by the time
`--info` answers, so the frame count it reports is a count, and it is exactly the frames that
will come down the pipe. `Qtgmc.SetProgressTargetAsync` asks before the encode and installs
`frames × den / num` as `FfmpegOutputHandler.overrideTargetDurationMs`. Read the rate as the
fraction and not as the decimal beside it - 59.940 is not 60000/1001, and over a couple of
hundred thousand frames that rounding is seconds.

The indexing this pays for is not added work: the encode's own VSPipe would do it moments later,
and every source plugin in `WriteScript` is told to cache its index - so it moves that step in
front of the encode rather than adding one, which also gives the pause before a QTGMC encode
something to say for itself. It cannot collide with a trim, either, because a trim rules QTGMC
out. Nothing about it is load-bearing: an answer that does not arrive leaves the bar measuring
against the file's own duration, which is where it was already.

That still leaves every path VapourSynth is not on, so the bar is also made to admit it.
Once `time=` is past the target by more than `FfmpegOutputHandler.TargetToleranceMs`, the run has
*proved* the target wrong - an encode cannot write more of a file than there is - and the bar
goes indeterminate with the footer reading "01:18:36 in, past the 59:56 this file claims" rather
than pinning at 100%. The tolerance is not zero because a muxer pads the last frame and a target
worked out from a frame count is rounded to begin with. The log line that goes with it names a
wrong duration as the *usual* cause rather than the only one: a mux whose longest track is not
the loaded file's lands here too, and neither is a fault in the run. When the target is an
override rather than the file's duration - a cut's section, a pass's measured length - the line
and the footer say "measured against"/"expected" instead of blaming the file, which was wrong on
both override paths.

**The final stats line is exempt from that judgement, and the reason is the muxer's flush.** The
line carrying `Lsize=` reports the last timestamp *flushed*, not the last encoded: a stream copy
with sparse subtitle tracks - a remux's forced PGS tracks hold seconds of packets across a whole
film - buffers the other streams against them in the interleave queue and drains it at the end.
Measured on a UHD remux's AV1AN trim: every progress line at 04:00, the final line at 48:32, so
the "passed the duration" message fired on a cut that was exactly right, on every remux trim. A
genuinely wrong duration trips the check on ordinary mid-run lines long before the final one, so
skipping the final line's verdict loses nothing - it only stops the copy's last flush reading as
a broken file.

**The plugin set is not guesswork, and it is pinned to havsfunc 33.** 33 is the last release
carrying the classic `QTGMC(Preset=…)`; 34 replaced it with vs-jetpack's builder API and a
dependency tree many times the size. What 33 resolves on its default path was established by
building the graph and rendering a frame: `mv`, `rgvs`, `fmtc`, `focus2`, `misc`, `znedi3`
and `eedi3m`, plus the `havsfunc`, `vsutil` and `mvsfunc` scripts. Two of those are not
obvious and both were found the hard way - `eedi3m` because `QTGMC_Interpolate` builds an
eedi3 partial *before* it looks at EdiMode, so it is resolved even on the NNEDI3 path, and
`znedi3` specifically rather than nnedi3 or nnedi3cl because that is the name it calls.
`focus2` (TemporalSoften2) in turn refuses to run without `misc`. Grepping for `core.<ns>.`
finds none of these: havsfunc reaches most plugins as `clip.<ns>.<Func>()` method chains.

**The plugin set depends on the preset, and checking one preset is checking half of it.**
havsfunc turns QTGMC's noise processing on for `Placebo` and `Very Slow` and no other preset -
that is a literal `Preset in ['placebo', 'very slow']` in its own source - and the default
`NoisePreset` then selects `fft3dfilter` as the denoiser. So there are two plugin sets: the
seven above, and those seven plus `fft3dfilter`. 2.8.6 and everything before it shipped without
the denoiser, because the probe, the release check and the bundle list were all written from a
`Very Fast` render. A Very Slow encode died two seconds in on "there is no attribute or
namespace named fft3dfilter". `Qtgmc.NeedsNoisePlugins` is the one place that mapping lives;
the probe caches a verdict per set, not per session, and the release check renders both.

That second set was found by tracing rather than by rendering, because there is no VapourSynth
in a web session: a stand-in `vapoursynth` module that records every namespace touched, with
havsfunc walking its own graph-building code over it, run for all eight presets. Validated by
reproducing the known `Very Fast` answer before trusting the rest. It reports `rgvs` as unused
by QTGMC (every `rgvs` call in havsfunc 33 is in some other function) - that is left required
anyway, since requiring too much only costs a spurious fallback and the real-render evidence
that put it there cannot be re-run here.

None of that is load-bearing for the build. `Qtgmc.IsAvailableAsync` builds a QTGMC graph
over a blank clip and renders a frame of it, at the preset that is about to run, through the
same VSPipe the encode would use - so a missing plugin, a Python that cannot import havsfunc,
or a znedi3 whose weights file did not travel with it all come out as a fallback to bwdif
naming what is wrong, rather than as ffmpeg complaining about invalid data on stdin ten
minutes in.

**Every plugin here also has to satisfy the core's API version, and that is a separate
question from whether it downloaded.** A plugin hands `configPlugin` the API it was built
against, and a core older than that refuses to register it - with no message, because
autoload reports nothing either way. VapourSynth is pinned to R72 (av1an needs VSScript API3,
dropped in R73), R72 speaks API 4.0 and 4.1, and **API 4.2 arrived in R74**. So a plugin built
against 4.2 is simply absent at runtime however perfect the file is.

That is what shipped in 2.8.3 and 2.8.4. `vapoursynth-eedi3` is built against 4.2 in *every*
wheel it has ever published - 9.0, 9.1 and 10.0 alike - so `eedi3m` never registered and every
QTGMC deinterlace on those builds fell back to bwdif. eedi3m now comes from the `r8` GitHub
release asset instead, the last Windows binary upstream attached to one and still API 4.0,
pinned to its published SHA256 because that tag will not move again. It carries no `EEDI3CL`,
which nothing asks for unless QTGMC is called with `opencl=True`.

**Packaging metadata is not the signal - the binary is.** `vapoursynth-fmtconv` also declares
`vapoursynth>=74` and its DLL is API 4.0, so it loads fine; the wheels' `requires_dist` says
nothing about what the binary needs. Read the constant out of the first bytes of the plugin's
`VapourSynthPluginInit2` (`0x00040001` little-endian for 4.1) - find the export's RVA in the
PE export directory, which is data-directory entry **0**; entry 1 is the imports.

**Presence is not loadability, and checking presence is what let this ship.** The published
zip was inspected for 2.8.4 - the DLL was there, valid PE, right architecture, right export,
right namespace strings - and every one of those checks passed on a plugin that never loaded.
Two things guard it now. The release workflow's win-x64 job runs the bundled VapourSynth
against a real QTGMC graph and fails the build if it cannot render a frame; and when the
probe finds a namespace missing it now calls `LoadPlugin` on the unregistered files by hand,
purely to collect the reason autoload swallows, so the app says "VapourSynth refused a plugin
QTGMC needs (eedi3m) - … requires API R4.2" rather than the flatly misleading "missing".

**A trim is the one thing QTGMC does not cover on the Quick Convert tab**, because that trim is
ffmpeg's - a seek and a duration on the command line - and neither reaches the script that reads the
source, so the video would arrive whole while the audio arrived cut. Both together means cutting
first; the Cut utility does that without re-encoding, and the log says so. That is the only tab the
pairing can arise on: the AV1AN tab has no QTGMC to compose with its trim.

**All three trim modes are a seek and a duration, and the modes differ only in what the user types
and how exact the start is.** The keyframe mode seeks the input; the other two seek the output,
which decodes and discards its way there and so stops where it was asked to.
`TrimSettings.GetInputArgs` and `GetOutputArgs` are the only place that mapping lives.

**What the input-side seek lands on is decided by the *codec*, not by the mode, and this file used to
say otherwise.** It read "lands on the keyframe before the point", which is true of a copy and false
of everything else: `accurate_seek` is on by default, so in front of a re-encode ffmpeg seeks to the
keyframe and then decodes and discards up to the point. Measured against the bundled build on a
source with keyframes every 48 frames, a section asked for at frame 30 begins on **frame 30** through
a re-encode - identical to what the exact mode's output-side seek produces, and through MKV/H.264 and
MP4/HEVC alike - and on **frame 0** through `-c copy`, there being no decode to discard with. So the
keyframe mode is not the inexact one; it is the *fast* one, seeking rather than decoding the file
from its start, and for a re-encode it is exact in all three modes.

**That is what decides which shape the trim dialog takes, and it is the codec rather than the tab.**
`CutWindow.SectionIsCopied` is the one statement of it: the AV1AN trim and the Cut utility always
copy, and the Quick Convert trim does whenever its video codec is Copy. A copied section gets the
snapping dialog - no mode row, and the start point moved back onto the keyframe on its own - because
the copy begins there whatever the field says, so refusing the move only leaves the dialog describing
a section that is not the one produced. A re-encoded section must **never** be snapped: it begins
exactly where it was set, so moving the start point back would put up to a whole GOP of video the
user did not ask for at the front of the output. The Quick Convert row kept a manual Snap button for
both cases until this, and in the re-encode case that button was a foot-gun sitting under a keyframe
note that described a cut ffmpeg was not making.

**The mode row goes with it, and that is not tidiness.** Over a copy the two exact modes seek the
*output*, which lands on the keyframe **after** the start point: measured, a 24-frame section asked
for at frame 30 came back as 8 frames beginning at frame 48, the eighteen in between simply gone. So
a copy is offered the keyframe mode alone, and `BuildResult` writes that mode whatever the box was
last left on. A section set while an encoder was picked can still outlive a switch to Copy, which is
the "a hidden control still holds a value" shape this tab has met before -
`QuickConvertUi.CoerceTrimToKeyframeCopy` puts it back into the mode a copy can carry out and names
it in the log, overruled rather than refused because the keyframe section is what the copy produces
regardless. It goes through `TrimSettings.AsKeyframeCopy`, which converts the units: frame mode holds
frame *numbers* in the fields the time modes hold milliseconds in, so changing the mode on its own
would read frame 240 as 240 ms.

**The mode labels were reworded off the same measurement, because two of the three were selling the
wrong thing.** They read "Time (snap to keyframe - fast, no re-encode needed)" and "Time (exact -
slower, requires re-encoding)", which offered accuracy as the reason to pay for the slow one - and
the row is only shown for a re-encode, where all three are exact. So all three say "exact" now and
the parenthetical states the mechanism instead: the first seeks straight to the point, the other two
decode the video from its start and throw it away until they arrive. Measured, that is 0.07s against
0.30s seeking to 55s of a 60s file, and on a feature it is the entire runtime in front of the start
point for the same frame. The two time modes were checked for agreement at four points on an MPEG
program stream as well as on MKV/H.264 and MP4/HEVC - identical every time, so the slow one is a
fallback for a source that seeks badly rather than the accurate one. The `ModeBox` tooltip carries
that reasoning, the labels having no room for it, and each label was measured against the box it sits
in: the longest is 436px in a 578px control.

`TrimSettings.Mode.TimeKeyframe` keeps its name, which is now the exception rather than the rule - it
names what the seek does over a *copy*. The class doc and `GetInputArgs` both used to state the old
belief outright ("inexact for the same reason") and now carry the measurement instead; do not restore
either, and do not read the enum's name as a description of what a re-encode does.

Verified headless through the real dialog and the real controls, 35 checks: all four shapes (Quick
Convert re-encode and copy, AV1AN, the Cut utility) for their mode row, snap button, keyframe note,
snapped-or-not start point and built mode; every one of the three re-encode modes leaving the start
point alone and showing no keyframe note; the coercion firing for frame mode against Copy with
frames 30-54 becoming 1250-2250 ms, standing down for a re-encode, and saying nothing where the mode
was already the keyframe one; and every mode label measured against its dropdown.

Frame mode was the exception and was wrong three ways for being one. It emitted a
`select=gte(n,X)` video filter plus `-vframes N`, so the kept frames carried their original
timestamps and the output opened on however many seconds the trim had skipped; the audio was cut at
neither end, since both of those touch video only; and the frames being counted were the ones coming
*out* of the chain, so a rate-doubling deinterlacer above the select halved the point it landed on.
It converts to a time now - the same conversion the dialog does to display it. Both ends of the
window sit half a frame outside the section: a seek keeps what is at or after its timestamp, and
`X/rate` is a place floating point can land either side of, so the early margin is what makes the
first frame the one asked for. The late margin is not symmetry - a window ending exactly on the last
wanted frame *loses* it, measured against the bundled ffmpeg for any section of three frames or more
starting anywhere but frame 0, and no arrangement of the two numbers fixed that on its own.
`-frames:v` does the cutting instead, and the generous window means it never has to reach.

**That count only goes out over a chain that hands on as many frames as it took**, which
`QuickConvertUi.ChainKeepsFrameCount` is what decides. `-frames:v` counts frames *leaving* the chain,
so a bob deinterlacer - which a trim makes likely, by ruling QTGMC out - or a frame rate above the
source's hits the limit halfway through the section and ends it there: frames 240-480 of a 29.97
source through `bwdif=send_field` came out as 240 frames covering 4.0s of an 8.0s section, audio
included. Without the count the window governs, and being half a frame long is the right way round to
be wrong there: the section carries an extra frame rather than losing the last one, and its count was
never going to be N anyway, since a bob emits two frames for every one it is given.

**The stream-copy cut - the Cut utility, and the AV1AN tab's trim - ends two frames late, and
`-frames:v` is not the fix however much it looks like one.** `UtilCut.CopySection` is `-ss`/`-t` over
`-c copy`, and ffmpeg decides where to stop from the packets' *decode* order, so a source with
B-frames hands over everything whose DTS is inside the window - including the frames whose PTS is
past it. Measured against an x265 source with `bframes=4` and b-pyramid: 100, 200 and 500 frames
asked for came back as 102, 202 and 502, every time, and shortening the window by half a frame or by
a whole one changed nothing. That is the +2 a user sees in the log, and the frames are real,
contiguous, and from their own source - the section is 83ms longer than asked, not damaged.

Adding `-frames:v N` beside the `-t` does make the count exact and can take a hole out of the middle
of the picture doing it, which is a worse fault than an extra frame. It truncates in decode order, so
it is only clean where N lands on a mini-GOP boundary: cutting 200 frames from frame 0 came out
contiguous, and cutting 150 from a mid-file keyframe dropped the two frames *before* the last one -
150 frames, exact count, and a stutter at the end. The Quick Convert trim's own `-frames:v` is not
the same case and is safe: that one runs over a re-encode, where the frames leave the chain in
display order. Leave the copy as it is.

**A trim is checked against the file before the encode starts**, through `UtilCut.ResolveSection`,
which all three of the Cut utility, the AV1AN tab and Quick Convert now ask. A trim outlives the file
it was set for and a batch does not clear it, so one section runs against every file in the queue;
where it starts past the end of a shorter one, ffmpeg seeks past everything there is and writes an
empty file without complaining. `ResolveSection` reads the section through the millisecond accessors
rather than off the fields, because in frame mode those hold frame numbers and comparing a frame
count against a duration compares nothing.

**QTGMC is not on the AV1AN tab at all, and the reason is arithmetic rather than taste.** av1an
applies video filters with ffmpeg once per chunk and there is nowhere in that to put a script; and it
evaluates its input for scene detection, again for every chunk, and again for every probe a
target-quality mode runs, so a filter costing more than the encoder would be paid for several times
over. The tab therefore used to render the whole video through QTGMC into a lossless
`{tempDir}.deint.mkv` and hand av1an that - one serial pass, then the parallel encode.

**On the sources QTGMC exists for that is strictly the slower shape.** A tape capture is standard
definition, so QTGMC is the bottleneck and the encoder is not: pass plus encode is always more than
Quick Convert's `vspipe | ffmpeg | encoder`, which overlaps the two, and the pass writes the largest
temporary file this app produces to get there. `DeinterlaceUi.Av1anModes` is `AllModes` without
QTGMC, `DeinterlaceUi.Av1anQtgmcProblem` is the standing reason, and a tape that wants QTGMC goes to
Quick Convert or through the Deinterlace Video utility and then to this tab. `Av1an.RenderDeinterlacedInput`,
the QTGMC preset box, the field-doubling box and `DeinterlacePass`'s lossless branch went with it.

**One frame per field went too, and the trap it leaves behind bit immediately.** That option only
ever made sense with the pass in front, whose doubled rate is simply the rate of the file av1an
opens; a filter *inside* av1an emitting one frame per field writes twice the frames its chunking
expects under the source's own rate, and the file plays at half speed. `Av1an.Run` used to clear
`DoubleRate` for any plan that was not the pipe, and this file used to say "do not remove that line".
Removing the checkbox is not the same as removing the setting: **`DeinterlaceRequest.DoubleRate`
defaults to `true`**, so `GetAv1anRequest` merely *omitting* it asks for exactly the forbidden thing -
measured through the real controls, `bwdif=mode=send_field` in av1an's per-chunk chain. It is set to
`false` explicitly there now, with a comment saying why the omission is not enough. A field whose
default is the dangerous value has to be written, not left out.

**Automatic on that tab has always resolved to bwdif** and still does, through the same
`QtgmcUnavailableHere` field the tabs use for their real impossibilities - so removing the entry
changed nothing about how Automatic behaves, only about what can be picked beside it. What did
change is the tab's default: `DeinterlaceUi.Av1anDefaultMode` is Automatic where Quick Convert's
`DefaultMode` is still QTGMC, so `ApplyScanVerdict` now selects per tab. Automatic rather than Bwdif
outright because the two do the same thing to an interlaced file and Automatic also does nothing to
a progressive one, which is the safer of the two to leave sitting in a box.

Verified headless through the real `MainWindow`, which is how the `DoubleRate` default above was
caught rather than shipped: the two boxes hold what they should, the AV1AN tab opens on Automatic and
Quick Convert on QTGMC, the preset and field-doubling controls are gone from the window, and **every
one of the four AV1AN entries resolves through the real `Deinterlace.ResolveAsync` to a plan whose
`UsesPipe` is false** - which is the property the deleted pass existed to serve, so it is the one
worth asserting per entry rather than once.

The Quick Convert dropdown is `DeinterlaceUi.AllModes` and saves its index - so entries may be
appended to that list but not reordered. The AV1AN dropdown is `Av1anModes`, its own array rather
than an index into the other, so nothing on that tab can select an engine it will not run; both
`ModeOf` and `ModeInEffect` take the array to read a box against, because the two are different
lengths and reading one against the other would name the wrong engine. Removing QTGMC could not
disturb a saved index there because that box saves nothing at all, its whole tab starting each
session at the defaults - which is also what retired the name-versus-index migration it once needed,
from when adding QTGMC in its proper place moved Bwdif and Yadif down one.

Feeding av1an a `.vpy` directly is possible and is still the wrong trade. Measured rather than
assumed: chunking does not damage a temporal filter - frames 300-319 rendered as a chunk come
out bit-identical to the same frames of a sequential render, and three 240-frame chunks took
1.11 / 1.19 / 1.17 s against 3.41 s for all 720 in one go, so the per-chunk cost is about 2%.
What it costs is the *repeats* above, and three sharp edges: av1an's own source says
"vapoursynth audio is currently unsupported" and skips the audio thread entirely, its Select,
Segment and Hybrid chunk methods call `as_video_path()` which panics on a VapourSynth input,
and seeking into an MPEG program stream is not frame-accurate - `vspipe -s 300` on a `.mpg`
came back with the frame the sequential render calls 298, where the same video remuxed to MKV
landed on 300 exactly.

**The Deinterlace Video utility exports a file and stops.** `DeinterlacePass` is the one place a
deinterlaced MKV with its audio and subtitles copied is written, near-lossless x264 for this
utility's deliverable. It carried a lossless FFV1 shape beside it, for the AV1AN tab's intermediate;
that tab runs its deinterlacers inside av1an now, so `UtilDeinterlace` is the only caller left and
x264 the only shape - its own doc says so where the split used to be explained. Its output is the
deliverable rather than a step on the way to a tab. Until 2.8.10 it was that
step, and loaded its own result into the file list to make the AV1AN tab reachable; the tab
reaches QTGMC itself now, so the loading is gone, along with the muxing-mode and batch carve-outs
that guarded it. A utility that exports a file has no business rearranging the file list on the
way out. The Cut utility is the same shape and always was, which is worth keeping true of both:
utilities write a file, the encode tabs' own Trim and Deinterlace settings apply during an encode,
and neither reads the other's.

**Its settings are its own** - `UtilDeinterlace.Settings`, a `Configure…` dialog off its card,
persisted under three `Config.Key` entries, defaulting to QTGMC outright where the tabs default
to Automatic. It read the Quick Convert tab's Deinterlace row until 2.8.6, on the reasoning that
the mode and the preset should be set in one place. That only holds for someone who uses both,
and the defaults do not want to agree anyway: Automatic is right on a tab that encodes whatever
it is given and wrong here, where doing nothing means writing a re-encoded copy for no reason.

