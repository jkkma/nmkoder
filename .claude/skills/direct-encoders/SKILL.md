---
name: direct-encoders
description: The full record of Quick Convert driving the encoder binaries directly - SvtAv1EncApp, aomenc, vpxenc, x264 and x265 launched behind an ffmpeg y4m pipe with a second ffmpeg muxing the result. Success judged by artifacts rather than the chain's exit status, mkvmerge containerising raw Annex B because no ffmpeg route can timestamp it, the pixel aspect AV1 and IVF cannot carry and the -aspect the mux states instead, VSPipe's A0:0 header and setsar at the head of every VapourSynth-fed chain, the aomenc/vpxenc stdin prompt and --disable-warning-prompt, av1an's silent -y, the post-filter keyframe interval, two-pass stats stems, and the headless harness that verified it. Load for a Quick Convert encode on a binary encoder, IBinaryEncoder, VideoEncodersDirect, BuildDirectCommand, y4m or yuv4mpegpipe, Annex B, IVF, pipe_video_timed, mkvmerge on the direct path, GetMuxAspectArgs, GetPipeSarFilter, GetPipeColorParamsAsync, GetNoPromptArg, GetKeyIntArg, GetPostFilterRate, GetMuxInputArgs, or any bundled tool that prompts on stdin.
user-invocable: false
---

# Driving the encoder binaries directly - the full record

CLAUDE.md's `## Driving the encoder binaries directly` section is the digest: it carries the
rules that have to hold whatever you are doing in this area, and points here. This is the whole
of it - every measurement, every trap, and the account of what was got wrong and why it looked
right.

It was moved out of CLAUDE.md **verbatim**, byte for byte, so nothing below is paraphrased and
nothing was dropped - in the second split, on 3 September 2026, after the file had grown back by
58 KB in the fortnight since the first. That is also why passages below that say "this file"
mean **CLAUDE.md**, where this text used to sit - they were not rewritten, because rewriting them
would have been the one way to lose something in the move.

CLAUDE.md remains the authority on the project and its conventions govern this file too. A new
finding here goes here, in the house style `.claude/skills/record-finding` sets out; a new *rule*
that can be broken from outside this area goes in the CLAUDE.md digest as well.

## Driving the encoder binaries directly

**Quick Convert drives the standalone encoder binaries now** - `SvtAv1EncApp`, `aomenc`, `vpxenc`,
`x264`, `x265`, the same ones `bin/av1an/enc/` already carries for av1an, and the same five slots of
the dropdown the ffmpeg libraries used to hold. NVENC stays on ffmpeg, having no CLI equivalent at
all, and so do GIF, PNG, JPEG and stream copy. A codec whose binary is not on the machine **refuses**
the run naming it and `bin/av1an/enc`, the way a missing mkvmerge or grav1synth is handled - macOS
bundles no encoders, and vpxenc's bundling is best-effort, so this is not a hypothetical. There is
deliberately no fallback to ffmpeg's library for the same codec: an encode that quietly ran on a
different encoder than the one picked would be worse than the message. The Lib* five stay in the enum
and the codebase for the CRF ladder, which deliberately measures ffmpeg's own encoders and persists
the enum's numeric values.

`VideoEncodersDirect.cs` holds the five encoder classes and `IBinaryEncoder`; `QuickConvert.Run`
branches on `CodecUtils.GetBinaryCodec` and builds the chain in `BuildDirectCommand`. What follows is
what was established by running it.

The shape is `ffmpeg (decode + filters) -f yuv4mpegpipe -strict -1 - | <encoder> <io> <settings>` per
pass - the pixel format conversion rides the pipe side, and `-strict -1` is what lets the y4m muxer
write its "non-official" formats, the 10-bit ones among them - then a second ffmpeg muxing the
elementary stream with the audio, subtitles, chapters and metadata that never went down the pipe. The
encoded video goes in as the **last** `-i`, which is the trick `DeinterlacePipeInput` already uses:
every input the stream maps were built against keeps its index, so the metadata and chapter *source*
indices stay right - and the maps put the encoded video at the position the first video track held in
`TrackList.GetMappedStreams`, so the output stream order is the one `GetMetadataArgs` numbers its
`-metadata:s:N` against. QTGMC composes as a third stage in front (`vspipe | ffmpeg | encoder`),
exactly as it prefixes the single command.

**The trim has to reach both halves, and the mux's copy cannot be the same spelling.** The pipe
commands carry the ordinary input/output trim, so the video is cut exactly as the single command cut
it. The mux then has to cut the audio to match - but its output-side seek would also apply to the
encoded video input, taking the section's own length off the front of a video that was already
trimmed. So `TrimSettings.GetMuxInputArgs` turns all three modes into an input-side `-ss` in front of
each *original* input (never the encoded one), where ffmpeg's default `accurate_seek` keeps a decoded
- which is to say re-encoded - stream sample-exact, and `GetMuxOutputArgs` is the duration alone.

**The chain's exit status is the mux's, and that is why success is judged by artifacts.** `&&` stops
the chain on any nonzero step, so encoder and mux failures do surface - but a decode ffmpeg dying
mid-file upstream of the encoder is invisible: the encoder sees an ordinary end of stream and every
later step finishes normally over a truncated video. Each pass's decode ffmpeg therefore writes a
`-progress` file whose final `progress=end` is its completion marker - the same idea as
`Qtgmc.ReadRunProblem` one pipe further up - and the run checks those, then that the mux holds every
mapped stream. The encoder's own stderr goes to a log file, not the live stream: its progress spam is
not ffmpeg's format, its vocabulary trips `FfmpegOutputHandler.LooksLikeTrouble`, and on failure the
log's tail is quoted in the report. ffmpeg's stderr stays live, which is what keeps the progress bar
fed - producer-side progress, slightly ahead of the encoder, which is fine for a bar. The scratch
files (intermediate, stats stem and its `.cutree`/`.mbtree` siblings, logs, markers) live in the
session folder and are deleted on success, because that folder is only emptied at the *next* launch
and the intermediate is the whole video.

**aomenc and vpxenc will stop and ask a question at values their own argument rows offer, and a launched
child never answers.** Both accept `min-q` and `max-q` at 0-63, defaulting to 0 and 63 respectively -
which is what `AomAv1.json` and `Vpx.json` state, and 63 apart, so nobody meets this by leaving the
rows alone; set `min-q` within 8 of `max-q` and the binary prints `Warning: Bad quantizer values… should
differ by at least 8.` followed by `1 encoder configuration warning(s). Continue? (y to continue)` and
reads a byte from stdin. Measured on the 2.8.78 bundle (`AV1 Encoder v3.14.1`, `VP9 Encoder
v1.15.2-151-gd98e70839`); `--min-q=55 --max-q=63` is clean and `56` asks, so the boundary is exactly the
documented 8 - and it is the **only** configuration warning either binary has, one `Continue?` string and
one `Bad quantizer values` string apiece and nothing else feeding the tally.

**What that costs depends on what is on stdin, and this file used to name the wrong one of the two.** It
said the encoder "hangs indefinitely… so there is no exit code, no artifact and nothing in the log to
judge: the run simply never ends". That is true only of a stdin that is open and *silent* - measured,
both binaries are still blocked at 25 s against a pipe given the y4m header and then nothing - and it is
not what this app produces. Their stdin here is the y4m the frames arrive on, so the prompt read is
satisfied by frame data, which is not `y`, and the encoder **exits 1 in ~130 ms having written nothing**;
the chain's `&&` then stops before the mux. Measured through the app's own command shape under cmd.exe
and sh alike: rc=1, 0.07 s, no intermediate. So the failure is loud rather than invisible -
`settings.Problem` is set and `GetDirectRunProblemAsync` appends `GetEncoderLogTail`, which is where the
`Continue? (y to continue)` line surfaces in the message. The artifact checks in this section were never
blind to it, and the sentence that said they were - that they "all run after a process that never
exits" - was a consequence of the wrong half rather than a separate finding.

**The AV1AN tab reaches the same two binaries from the same rows, and av1an passes no suppression flag
of its own** - `--disable-warning-prompt` does not appear anywhere in the bundled `av1an.exe`
(`0.5.2-unstable (rev 805dad6)`, toolchain 2.8.78), read out of its strings. Measured on a one-chunk
encode: the chunk dies as `encoder crashed: exit code: 1` with the prompt quoted in av1an's own stderr,
is retried once, and is then given up on with nothing written. So this was one bug on two tabs.

`CodecUtils.GetNoPromptArg` is the one place the flag is decided, for both of them: `aomenc` and
`vpxenc` get `--disable-warning-prompt` and every other encoder gets `""`, so nothing else grows an
argument. It is **looked up rather than written unconditionally** - both binaries refuse a command over
an unrecognised option outright (`Error: Unrecognized option`, rc=1, nothing written), so an unguarded
flag would trade this bug for a worse one on a build without it - and `AvProcess.ToolKnowsFlagOrIsUnknown`
errs the right way by construction: a binary that cannot be found or run gets the benefit of the doubt
and the flag goes out anyway. The lookup is async where `GetArgs` is not, so each tab resolves it in its
own `Run` and carries it in the argument dictionary under `CodecUtils.NoPromptKey`; the av1an classes
write it inside av1an's `-v "…"` string, that being what reaches the binary. The control is that it
changes nothing where no warning fires - `--min-q=55 --max-q=63` with the flag and without it is
**byte-identical** output on both encoders, same length and same SHA-256.

Verified by running it, through the real `MainWindow` and `RunTask.Start` rather than a model of them:
13 scenarios, 138 assertions, no failures, nothing over 3.5 s against a 180 s budget - a timeout being
the only thing that could have told "fixed" from "still hanging". Both encoders on both tabs with the
prompting pair; the non-prompting pair as a control; two-pass, where the flag has to appear in **both**
pass commands and does; and `DirectSvtAv1`, `DirectX264`, `DirectX265` and the AV1AN tab's SVT-AV1 as
leak controls, where it must not appear and does not - those three binaries' `--help` *was* read and
genuinely lacks the flag, so the name check and the guard each keep it away independently. The negative
control is the app's own logged command with only `--disable-warning-prompt` deleted: aomenc rc=1 at
0.09 s with no `.ivf` written, av1an rc=1 at 0.63 s having failed the chunk three times. Both detection
paths were made to fire rather than assumed - an out-of-range `min-q=99` produced a reported failure, and
a 1 s budget on a 2.9 s run produced a timeout. The flag lands after the last app-written argument and
eleven-plus tokens clear of aomenc's and vpxenc's sole positional `-`.

**Not verified: the hang, through the app.** Every failure reproduced here is the fast `exit 1` shape,
which is what this app's stdin arrangement produces; the blocked-past-25 s case needs a producer that
goes quiet and stays quiet, which nothing here can generate. Nor were batch mode, Muxing Mode, filters or
trims exercised - one 3 s 320x240 fixture throughout - and two-pass was run on aomenc only. NVENC is not
an `IBinaryEncoder`, so `QuickConvert.Run`'s `directEnc != null` never sets the key for it: read rather
than measured.

This is the same rule this file already records for grav1synth - *prompts interactively without `-y`* -
reappearing on two more bundled binaries, and reachable from the argument rows' own stated range rather
than from anything exotic. **Assume a bundled CLI tool prompts until it has been shown not to**, and
pass its suppression flag when launching it.

**Every launched tool inherits this app's stdin, and that one fact decides hang against fail-fast.**
`RedirectStandardInput` appears nowhere in the codebase, and `OsUtils.SetStartInfo` redirects stdout and
stderr only - so a tool gets whatever stdin the app has. From Explorer that is a `WinExe`'s non-console
handle; from a terminal, or the visible-console debug mode, it is a real console someone could type into;
and an encoder on the far side of a `|` gets the pipe instead, which is the case above. A prompt is
therefore never merely cosmetic and never reliably fatal: it is three different failures depending on how
the app was started.

**The rest of the bundled toolchain was surveyed against that, and the aomenc/vpxenc pair is the only
gap that was open.** Every tool that can prompt already gets its flag at every launch site: ffmpeg's `-y`
is hardcoded into `AvProcess`'s own `beforeArgs` and written again on each of the three chained ffmpegs
`BuildDirectCommand` composes; av1an's is at both its launch sites; grav1synth's is on all five calls
that write a file. mkvmerge, mkvextract, mkvinfo, `SvtAv1EncApp`, x264, x265 and VSPipe **have no prompt
at all** - measured silent-stdin, EOF and PTY, all completing in 0.10-0.25 s, and none of the seven
contains a `[y/N]`-shaped string. (mkvmerge overwrites silently and has no such flag to pass.) The
bundled `python.exe` blocks forever given no script argument, being a REPL; all four call sites always
pass one. Measured on the 2.8.78 toolchain, with a `ffmpeg` overwrite prompt as the positive control -
it hangs on a PTY at 12 s without `-y` and returns in 0.07 s with it.

**av1an's `-y` is the one whose absence would be silent, so both of its call sites now say so.** Given an
output path that already exists, av1an asks `Output <f> exists. Do you want to overwrite it? [y/N]:`,
takes the default, and **exits 0** - measured twice, status 0 with the existing file byte-for-byte
untouched and `Not overwriting, aborting.` its last word. `RunAv1an` hands that 0 straight to `Av1an.Run`,
whose retry path is gated on `exitCode != 0`, so the encode would be reported as finished having never
run. That is the `Av1anMemory` out-of-memory trap under another name, and the comments at
`Av1an.Run` and `Av1anSceneDetect.TryPrepareScenesFileAsync` exist so neither `-y` reads as boilerplate.

Two launch sites were **not** covered and are named rather than assumed: `OcrProcess` runs SubtitleEdit,
which is not bundled at all, and is the one place a *modal dialog* rather than a stdin prompt is
plausible - it needs a machine with SubtitleEdit installed; and `PackageBuild` runs `7za`, also not
bundled and reachable only through the maintainer-only `-package=` command line.

A tone-mapped encode swaps `MediaFile.ColorData` for `ToneMapConfig.GetOutputColorData` around the
`GetArgs` calls - the same swap `Av1an.Run` makes, because these encoders are told their colour by
flag where ffmpeg's libraries read it off the frames; without it the output is SDR pixels tagged PQ
and BT.2020. The direct classes are also handed `GetVideoSourceFile()` rather than the loaded file,
which in Muxing Mode is a different file - possibly an audio file, with no colour and no frame rate
to derive a keyframe interval from.

**These are the same binaries the AV1AN tab drives and deliberately not the same argument builders.**
`VideoEncodersBin` writes an *av1an* command - `-e svt-av1 --force -v "…"` with the encoder's parameters
inside a string av1an splits again - where these write the command line the binary is launched with.
av1an owns the input, the output, the chunking and the pixel format; all four are this app's to state
when it is the one launching the encoder, so the two cannot share a builder.

**y4m carries the frame size, the rate and the range and nothing else.** Measured, the header is
`YUV4MPEG2 W320 H240 F24:1 Ip A1:1 C420mpeg2 XYSCSS=420MPEG2 XCOLORRANGE=LIMITED` - no primaries, no
transfer curve, no matrix. So colour has to be handed to the encoder by flag in each one's own spelling,
which is the same reason the av1an classes do it, and why `ColorDataUtils`' aom and x264 name tables are
load-bearing here too.

**The one thing y4m *does* carry that the encoder still cannot pass on is the pixel aspect, and that
shipped stretching every anamorphic source from 2.8.44 to 2.8.77.** The `A1:1` in that header is real -
measured on a 720x480 NTSC capture it reads `A8:9`, so the shape reaches the binary intact - but **AV1
has no sample-aspect field in its sequence header and IVF has none either**, so `SvtAv1EncApp` and
`aomenc` cannot emit it however they are fed, and the mux then copied an elementary stream that never
knew. Measured end to end through `QuickConvert.Run()` on a 720x480 SAR 8:9 source with no filters at
all: **8:9 / 4:3 in, 1:1 / 3:2 out**, which plays horizontally stretched. vpxenc loses it the same way.
x264 and x265 do **not** - raw Annex B carries the SAR in its SPS VUI and mkvmerge reads it back out,
measured 8:9 / 4:3 through the whole route - so this is the IVF/AV1 pair and VP9, not the direct path as
such.

What let it ship is a premise in `QuickConvertUi.ResolveScaledFrame` that stopped being true underneath
it. It leaves an anamorphic source un-squeezed and says why: *"ffmpeg carries its aspect flag through to
the output"*. That held while this tab handed frames to an ffmpeg encoder inside one command, and stopped
holding the moment the tab began launching encoder binaries itself - the identical constraint the AV1AN
tab has always had, which is why that tab de-squeezes instead (`Av1anFrame.Desqueezing`) and is not
affected. The comment even named the difference between the two tabs as deliberate; it was, and then one
half of it changed.

`QuickConvertUi.GetMuxAspectArgs` states the display aspect on the mux instead, `-aspect W:H` worked out
from the encoded frame and the pixel shape `ResolveScaledFrame` already tracks. **Stated rather than
baked in, and that is the difference from the AV1AN tab's answer**: av1an muxes its own output where this
mux is ours, so the shape can be recorded rather than resampled - no scale filter, no quality cost, and
the frame stays the size the encoder was tuned for. Emitted for every direct encoder rather than only the
ones that need it: on the x264/x265 path it restates the ratio their own VUI already carried, which is a
no-op by construction. An unresolved automatic crop is the one case it abstains on, the encoded frame not
being known until the crop is - a ratio worked out from the wrong frame states, precisely, a shape the
file does not have, where saying nothing leaves it as it was.

**A separate and wider fault sat behind it: VSPipe's y4m header reads `A0:0`, so anything fed through
VapourSynth lost the pixel aspect before ffmpeg's filters ever saw it - for every encoder, not just the
AV1 pair.** Measured on an 8:9 fixture: `vspipe | ffmpeg -c:v libx264` gave `N/A`, where the same ffmpeg
reading the file directly gives 8:9 / 4:3. VapourSynth has no SAR on a clip to write, so `A0:0` is honest
rather than a bug in VSPipe; it is a loss only because everything downstream then reads the frame as
square. `Qtgmc.GetPipeSarFilter` is the one statement of the repair - a `setsar` built from
`VideoStream.Sar`, **read off the source file and never off the pipe, the pipe being precisely where it
no longer is** - and it goes at the *head* of each chain, which is what makes one filter the whole fix:
ffmpeg's scale adjusts SAR to hold the display aspect and crop and pad carry it through, so everything
below behaves as it does un-piped. Measured through a real VSPipe producer, which is the only thing that
shows any of this: `setsar=8/9` at the head gives 8:9 / 4:3, and with an app-style
`scale=640:480,setsar=1/1` under it, 640x480 at 1:1 and **DAR 4:3** - the de-squeeze lands right rather
than being disturbed by the filter above it.

Three chains carry it, and `setparams` could not have: that filter takes field_mode, range and the three
colour properties and **has no aspect of any kind**. `CadenceRepair` (whose `_cfr.mkv` is explicitly a
deliverable for something else to encode, so the loss propagated), `DeinterlacePass` (the Deinterlace
utility - only where `plan.UsesPipe`, since reading the file directly ffmpeg already knows the shape),
and Quick Convert's filter chain. Verified by running the real methods against real fixtures, before and
after: both utilities wrote `N/A` on master and `8:9 / 4:3` after, and Quick Convert's chain came back
`-filter_complex "[0:0]setsar=8/9[vf]"` where it had been empty.

**`setparams` has since grown from five options to seven, and the load-bearing half is intact.**
Re-checked against ffmpeg `N-126264-g007cd1fd43-20260825` (toolchain 2.8.78) it now takes field_mode,
range, the three colour properties, and additionally `chroma_location` and `alpha_mode` - while a grep
for `aspect|sar|dar` over its option table still returns nothing, so the sentence above holds and
`setsar` is still the only way to restate the pixel aspect. What the growth is worth noting for is that
`chroma_location` is a **fifth** property y4m loses on the VapourSynth pipe, which
`Qtgmc.GetPipeColorParamsAsync` could now restate and does not.

**The AV1AN tab is not affected, and for two independent reasons** - worth writing down because it looks
like it should be. It has no QTGMC at all (`DeinterlaceUi.Av1anModes` omits it), so nothing on that tab
pipes VapourSynth into ffmpeg; and it de-squeezes anamorphic sources anyway (`Av1anFrame.Desqueezing`),
so the shape is in the pixels before av1an ever sees them.

**The verification turned up a second loss on the same pipe: `DeinterlacePass` was dropping the colour
tags as well.** Measured on a fixture stating bt470m/bt470m/bt470bg, its output came back `unknown` for
all three - the same y4m loss `CadenceRepair` already repaired for itself and this pass never had, on the
utility whose output is a file people keep. `Qtgmc.GetPipeColorParamsAsync` is now the one place the
four properties are probed and written, called by both; CadenceRepair's own copy is gone rather than left
beside it. Measured after: `bt470m / bt470m / bt470bg`, range `tv`, out of both passes.

**The field order is deliberately not part of that helper, and that is the whole reason it returns
properties rather than a finished filter.** A cadence repair hands on woven fields and must say so; a
deinterlace emits progressive frames and must not - asserting a field order on its output would be a lie
the next deinterlacer acts on. Measured, the two chains come out as they should:

```
CadenceRepair    setsar=8/9,setparams=field_mode=tff:color_primaries=bt470m:color_trc=bt470m:colorspace=bt470bg:range=tv
DeinterlacePass  setsar=8/9,setparams=color_primaries=bt470m:color_trc=bt470m:colorspace=bt470bg:range=tv
```

**A source that states nothing is left stating nothing**, which is the case worth checking because the
failure is silent in the other direction: on a square-pixel fixture with no colour at all the chain comes
out `setparams=range=tv` and no `setsar` - only the one property the file actually carries - where
asserting `unknown` would state ignorance as though it were a measurement.

**`DenoisePass` is not affected**, though the comment on `DeinterlacePass.GetSubtitleArgs` pointing at it
makes it look as though it should be: it reads its input with `-i` and runs hqdn3d over it, with no pipe
anywhere, so ffmpeg knows the colour and the aspect natively. What the two share is track carriage, not
a y4m producer.

**Raw Annex B cannot be given correct timestamps by any ffmpeg route, and mkvmerge is what
containerises it - for every output container.** The intermediates are `.ivf` for SVT-AV1, aomenc and
vpxenc, whose IVF header carries the frame rate and mux straight in; and raw Annex B for x264 and
x265, which is where the trap lives. Read back with `-framerate N` the packets have no timestamps:
Matroska refuses them outright ("Timestamps are unset in a packet for stream 0" / a current master's
"Can't write packet with unknown timestamp" - match the shape, not the sentence - plus a **796-byte
stub**, the `File.Exists` trap this file warns about twice), and the muxers that will stamp them - the
MP4 family - write **pts equal to dts in decode order, with no reordering info**. The frames stay in
the right sequence (the decoder orders by POC; PSNR-matching output position N to source frame N is a
clean diagonal), but on any stream with B-frames the timestamps put them at the wrong ticks, and a
pts-honouring player duplicates one frame and drops another at every mini-GOP. Measured through the
app's own output on a frame-numbered source: at 30fps screen ticks the viewer saw frames **0, 0, 1, 3,
4, 5** - and the container ran 67 ms long. That shipped from 2.8.44 to 2.8.67 as the MKV route (raw →
MP4 intermediate → mux) *and* the MP4-direct route (raw straight to the mux), because the measurement
that blessed them compared **the two routes against each other** - "same PTS and DTS in presentation
order" was true, both carrying the same wrong stamps - and never against the source's timing. The
fps-tick render (`fps=30` samples by pts, like a player) is the check that catches it; frame-content
matching and duration checks do not. `-fflags +genpts`, `+igndts`, `-fps_mode`, an output-side `-r`
and the `setts` bitstream filter (packet index in decode order) are all wrong the same way.

**mkvmerge parses the stream's own reordering and writes real presentation timestamps** - measured, 0
non-monotonic frames where the MP4 route had 60 of 150, the fps-tick check reads 0,1,2,3,4,5, and a
fractional `--default-duration 0:24000/1001fps` comes out exactly. So `BuildDirectCommand` runs it on
the raw stream into `pipe_video_timed.mkv` whatever the output container, the final mux copies from
that, and `QuickConvert.Run` refuses up front, naming MKVToolNix, when a raw-Annex-B codec is picked
with no mkvmerge to call - the same invisible-failure argument as a missing encoder, and it is real
off Windows, where MKVToolNix is not bundled. `Containers.StampsUntimedPackets` is deleted with its
one caller; a comment where it sat says why. x264 *can* mux Matroska itself where its build has the
muxer (also measured clean), but that is a build option rather than a promise, x265 has no muxer at
all, and both encoders taking the same route is worth more than saving x264 the step.

**"Comes out exactly" is the stream's rate and not the frames', and the difference is Matroska's 1 ms
timestamp scale.** Measured on 24000/1001 through to MP4: packet durations come back as 84x672 and
36x656 ticks of a 1/16000 timebase - 42 ms and 41 ms alternating - where an exactly-spaced track is
uniform, so anything that reads durations calls the output variable frame rate. `r_frame_rate` and the
total are exact (24000/1001, 5.005000 over 120 frames), so nothing drifts and no frame sits more than
half a millisecond off its true tick, which is three orders under the dup-and-drop it replaces.
`--timestamp-scale -1` does **not** fix it - measured, the same 656/672 split - so this is the price of
routing through Matroska rather than a flag anyone forgot. Expect it before re-measuring it as a bug.

**mkvmerge exits 1 for warnings and its output is usable at that status**, so the chain swallows its
exit code (`|| ver > nul` on Windows, `|| true` elsewhere, **parenthesised** - written bare, `A && B ||
ok` runs `ok` when *A* fails and hands the mux a chain that died upstream) and
`GetDirectRunProblemAsync` judges the artifact instead, which is what every other mkvmerge caller here
already does. Measured against the bundled v93: a warning run exits 1 having written a complete file, a
clean one 0, a real failure 2. Left on `&&`, one line of advice about a perfectly good file threw away
the whole encode with the mux never run. That check is asked *before* the exit code, and only where the
intermediate is non-empty - without it the encoder or the pipe above it is what died, and blaming the
containerise step for that is the misattribution this file already warns about in the other direction.

**`--disable-track-statistics-tags` goes out with it**, because mkvmerge writes those tags by default
and the mux copies them straight through: measured, an MKV output carried `BPS`, `DURATION`,
`NUMBER_OF_FRAMES`, `NUMBER_OF_BYTES`, `_STATISTICS_WRITING_APP=mkvmerge v93.0` and a
`_STATISTICS_WRITING_DATE_UTC` of the moment it ran. The date is the WebM SegmentUID trap under another
name - two otherwise identical encodes differ - and the rest describe the intermediate rather than the
file, on the video track alone, and only for a Matroska output, MP4 keeping just `language` and
`handler_name`. Nobody asked for any of it.

**It is right here and wrong on the AV1AN tab, so do not unify the two mkvmerge calls over it.** This
step *creates* the file, so tags describing an intermediate are noise. `Av1an.AttachEncodeSettings`
remuxes a **finished av1an output**, and av1an muxes with mkvmerge itself - measured, its output already
carries all six tags - so passing the flag there would strip what the encode put in. Same flag, same
binary, opposite calls, because one is writing a new file and the other is amending someone else's.

**The rate on the mkvmerge command is the post-filter rate** (`QuickConvertUi.GetPostFilterRate`): an
fps resample or a bob changes what the frames leave the chain at, and the raw stream knows nothing a
demuxer could check against. Left unreadable, the flag is omitted and the VUI timing x264/x265 wrote
from the y4m header governs - the same number by construction, both coming from the post-filter rate.

**So is the keyframe interval, and it was not.** `CodecUtils.GetKeyIntArg` multiplied the *source*
rate by the configured seconds, so an encode whose frames leave at twice that rate - which is every
bob deinterlace - got half the GOP it asked for: 29.97 x 10 = 300 frames, which in a 59.94 fps
output is five seconds, not ten. That is one place rather than two, so it is fixed by handing it
the rate the frames actually arrive at; it takes a `rateOverride` now, which Quick Convert fills
from `QuickConvertUi.GetPostFilterRate` and the AV1AN tab from `Av1anUi.GetPostFilterRate` (added
to match, the same answer `Av1anUi.GetFrame` already worked the resize out against). The CRF ladder
passes nothing and is right to: it runs no deinterlacer, so the source rate *is* the post-filter one.

Pre-existing and uniform rather than new, which is worth saying because fixing the field-rate read
above is what exposed it - on a capture stating a field rate the old arithmetic happened to land on
the right answer for the wrong reason, and correcting the rate made that file behave like every
other interlaced one. Measured through the real command: `--keyint 480` before, 300 after the rate
fix alone, 480 again once the GOP was given the post-filter rate - and 480 on a clean CFR
interlaced source too, where it had always been 300.

Two-pass runs the pipe twice against one stats stem - `--pass N --stats` on x264, x265 and SVT,
`--passes=2 --pass=N --fpf=` on aomenc and vpxenc, which also want `--passes=1` stating for a
single-pass run - with the first pass writing its bitstream to the same intermediate the second
overwrites, `/dev/null` and `NUL` not being the same word. The first cut of this was verified out of
the built assembly against the real binaries - all five, CRF and target-bitrate, 34 checks - before
the run was wired to it; the wired-up tab's own verification is the harness described at the end of
this section.

**Verified by running it, through the real controls rather than a model of them.** A headless
Avalonia harness constructs the real `MainWindow`, loads fixtures through `FileList.HandleFiles`,
drives the actual boxes and grids, starts each encode through `RunTask.Start`, and judges the
outputs with ffprobe - against real x264, x265, aomenc, vpxenc, the shipped SVT-AV1-HDR binary
pulled back out of the published linux-x64 release, and the BtbN master ffmpeg the app bundles.
94 checks across 17 scenarios, no failures. What passed, frame-exact and stream-complete: all five
encoders in CRF and in two-pass target bitrate (whole-file bitrate landing at video target plus the
audio's share); 10-bit through x264, x265 and SVT; an HDR source tone-mapped through DirectX265
coming out tagged bt709/bt709 with zero HDR side data, which is the ColorData swap doing its job;
a crop+resize+borders chain landing exactly on `GetEncodedFrameSize`'s prediction;
all three trim modes with the audio length matching the video, frame mode exact at 96/96; a
23.976-to-30 resample whose containerise rate, frame count and duration all came out right; titles,
languages, chapters, dispositions, an attachment and a copied-audio track surviving the mux; muxing
mode carrying video from one file and audio from another; a two-file batch; and GIF's palette
graph, stream copy and NVENC untouched on the single-command path - NVENC asserted by command shape
in the log, this box having no GPU. The refusals were run, not just read: a missing binary (with
the process PATH squeezed - a real x265 on the user's PATH rightly satisfies the availability
check, which is the check working), a second ticked video track, Measured-from-source, and a grain
table against a *mainline* SvtAv1EncApp swapped in for the run, which the encode asks per run. The
`vspipe | ffmpeg | encoder` three-stage shape was proven with a producer stand-in - ffmpeg reading
one pipe on stdin while writing y4m on stdout - since no web session has VapourSynth; a real QTGMC
run through the pipe remains a real-machine check.

**The grain table path is field-verified on both AV1 encoders now.** grav1synth measured a real
table off a grainy fixture, the tab was driven through Grain table file mode, and `grav1synth
inspect` read film grain back out of both finished outputs - aomenc's through `--film-grain-table`,
and SVT's through `--fgs-table` on the shipped SVT-AV1-HDR binary. That closes half of the gap the
grain section has carried since the row was written: `--fgs-table` acceptance is no longer a
real-machine-only check, the PSY binary being extractable from a published release. A full av1an
measured-grain run still is one.

**The harness paid for itself before the checks did: it found a crash on master.** The ffmpeg
ignore-path scan takes the text between a command line's last two double quotes, and on Linux the
paths are single-quoted - so what it finds is whatever double-quoted value comes last, and the
metadata grid writes `language=""` for a track that has none. That empty extraction made
`String.Replace` throw inside the output reader and took the process down mid-encode, on the old
single command as much as the new chain; the scan skips empty extractions now.

