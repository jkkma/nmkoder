---
name: tone-mapping
description: The full record of Nmkoder's HDR-to-SDR tone mapping - the two backends (the zscale CPU chain and libplacebo), the measured peak scan and why denser sampling is the wrong axis, the bounded end of the chain, the output colour swap, the Dolby Vision profile 5 refusal, and the tone-mapped previews. Load before touching anything HDR - tone map, PQ, HLG, BT.2020, npl or peak, hable/mobius/reinhard/spline, ToneMapConfig, ToneMapUi, ToneMap, ColorDataUtils, mastering display, MaxCLL, HDR side data, zscale, libplacebo - or a thumbnail or preview of an HDR file.
user-invocable: false
---

# Tone mapping - the full record

CLAUDE.md's `## Tone mapping` section is the digest: it carries the rules that have to hold
whatever you are doing in this area, and points here. This is the whole of it - every
measurement, every trap, and the account of what was got wrong and why it looked right.

It was moved out of CLAUDE.md **verbatim**, byte for byte, so nothing below is paraphrased and
nothing was dropped. That is also why passages below that say "this file" mean **CLAUDE.md**,
where this text used to sit - they were not rewritten, because rewriting them would have been
the one way to lose something in the move.

CLAUDE.md remains the authority on the project and its conventions govern this file too. A new
finding here goes here, in the house style `.claude/skills/record-finding` sets out; a new *rule*
that can be broken from outside this area goes in the CLAUDE.md digest as well.

## Tone mapping

**Both encode tabs hide the Tone Mapping row for a file that is not HDR**, and like the Deinterlace
row above it, the setting behind it now opens armed. `ColorDataUtils.IsHdr` decides which a file is
and reads the **transfer curve alone** - 16 (PQ) or 18 (HLG). Wide *gamut* is deliberately not
enough: BT.2020 primaries under an ordinary BT.709 transfer is a colour space, not a dynamic range,
and tone-mapping is a luminance operation with nothing to say about it.

**The two tabs default to different curves, and that is forced rather than chosen.** Quick Convert
opens on Spline, libplacebo's own default and the best of the set. The AV1AN tab cannot offer Spline
at all - it never runs libplacebo, so `ToneMapConfig.Av1anModes` does not carry the entry - so it
opens on **Mobius**, the curve measured closest to what Spline produces. `ToneMapUi.DefaultMode` and
`ToneMapUi.Av1anDefaultMode` are the two statements of that; one constant cannot serve both, since
the AV1AN box has no Spline entry to select.

Mobius was measured rather than picked. On a 4K HDR10 source (PQ, MaxCLL 1529, mastering display
4000 nits) against a real Spline render of the same file, compared at a matched 1920x804 with the
letterbox cropped off: as encoded, Mobius scores **VMAF 67.9 / SSIM 0.9884** against Reinhard's
60.4 / 0.9836 and Hable's 44.2 / 0.9614. Reinhard takes raw PSNR-y (28.3 against 27.7) and that is
an exposure artifact rather than a better match - its mean luma lands 4.7 code values from Spline's
where Mobius lands 10.0 - because equalising the exposure flips it: gain-matched, Mobius wins every
metric at **VMAF 90.2 / PSNR-y 36.6 / SSIM 0.9931**, against Reinhard's 87.5 / 33.5 / 0.9869 and
Hable's 79.4 / 31.6 / 0.9841. A straight-line fit to Spline says the same thing - Mobius R2 0.9916
against Reinhard's 0.9876 and Hable's 0.9766 - so Mobius tracks Spline's *shape* best rather than
merely sitting at the right level. **None of the three is close in absolute terms**: Spline detects
the peak per frame where this tab is pinned to one static roll-off, worth some 31 code values, so
this is the best available stand-in and not a match.

**The old default was Off, and the argument for it is worth keeping in view rather than losing.**
Tone-mapping is destructive and irreversible, and the other reason to load an HDR file is to
re-encode it *as* HDR, which is most of what this app's 10-bit AV1 encoding is for. What contains
that now is the row's own relevance test: it exists only for a file that really is HDR, so no SDR
source is touched by either default, and the readout states the conversion on screen before
anything is encoded. `ToneMapUi.ModeInEffect` still reports Off whenever the row is off screen,
which is what makes hiding it safe rather than merely tidy - a curve left selected behind a hidden
row would otherwise convert a file nobody was looking at.

**`MediaFile.ColorData` is now filled in when a file loads**, on a background task beside the
interlace scan. Before this it was assigned in exactly one place - `Av1an.cs`, at encode time - so
Quick Convert had no colour data at all and nothing outside that one method could ask whether a
file was HDR.

### There are two backends, and which tab it is decides who picks

libplacebo is the better tone-mapper; `ToneMapConfig`'s zscale chain is what runs without it. On
Quick Convert the machine picks: libplacebo wherever a real GPU is behind it, the zscale chain on
every machine without one. On the AV1AN tab nothing picks, because the answer is policy:
`ToneMapUi.GetAv1anConfig` sets `ToneMapConfig.ForceCpuChain` unconditionally, so that tab always
runs the zscale chain, per chunk inside av1an's `-f`. That is the user's rule for the whole tab -
**no intermediate pass that is itself an encode** - and libplacebo there meant exactly such a pass,
a full x264 re-encode of the film in front of av1an; see "The pass that used to run in front"
below. `ToneMapUi.ResolveBackendAsync` still settles the answer once per encode and says so in the
log - on Quick Convert because the answer is a property of the machine and one decided halfway
through would be a different picture in the second half of the file, and on the AV1AN tab because
a policy nobody states is indistinguishable from a fallback. For a `ForceCpuChain` config it
probes nothing - a probe whose answer would be discarded is a process launch for nothing - and on
every zscale path it measures the file's real peak, which is what keeps that chain within a few
code values of the GPU result.

Measured on PQ patches against a file declaring a 4000-nit mastering display, at 100 and 203 nits:
libplacebo's `hable` gives 115/143 where the zscale chain gives 108/144, and its top lands on **235**
- the nominal white of a limited-range signal - where the zscale chain ran to 247 and spent its
brightest highlights in the superwhite a player clips. That last clause is history now: the chain ends
in a bounded format and its top lands on 235 too - see "The chain ends bounded" below, which is also
where the superwhite came from in the first place. **The curve names map straight across** -
libplacebo has `hable`, `mobius` and `reinhard` under those names - so those three entries mean the
same thing whichever backend they land on.

**Those two libplacebo figures reproduce exactly on real hardware - and only with peak detection
*off*, which is not how the chain ships.** Re-measured on an RTX 5080 against the 2.8.78 bundle, on
a fixture built to this passage's description (10/100/203/400/1000-nit PQ patches through the ST 2084
inverse, lossless x265, MDL 4000): `peak_detect=0` gives hable **115/143** and spline **129/152** at
100 and 203 nits, matching the recorded numbers to the code value. With `peak_detect=1` - what
`ToneMapConfig` actually runs - the same fixture gives hable 131/163 and spline 115/152, because
detection re-exposes to the content's own 1000-nit peak rather than the declared 4000. **So these
figures describe the parameterisation this chain no longer uses**, and they should be read as a
curve-versus-curve comparison rather than as what a user gets. The "top lands on 235" clause is the
opposite way round: it is a *detection-on* result, and it reproduced as one - all four curves put the
content peak at 940 of 1023, which is 235 in eight bits, exactly as the reference-white passage below
describes.

Recorded rather than corrected, because what is wrong is the missing parameter and not the numbers:
this passage predates detection being switched on, and it never said which setting it was measured
under. Say which, when a measurement depends on a default that can move. The fixture recipe is in the
first sentence above, so the next re-check does not have to guess at it - the failure the HLG figures
below are still sitting in.

**The "CPU chain (no pass)" tick is gone because it stopped being optional.** `ForceCpuChain` began
as that tick, one direction of override only - forcing the zscale chain where the probe would have
said libplacebo, buying the structural things (no render pass in front of av1an, no intermediate on
disk) at a picture cost of a few code values. The user then made it the rule: the tick, its handler
and its readout clauses are deleted, and `ForceCpuChain` is the AV1AN tab's standing policy, set
unconditionally where the config is read. What the tick could never quite do, the policy does for
free - the readout states the CPU chain outright, the backend no longer being an encode-time
question on that tab. Everything downstream still branches on `UseLibplacebo`, which is why the
policy is one flag rather than a second pipeline. The direction that was never offered still is
not: on Quick Convert the probe's "no" is a measurement (a software Vulkan device tone-maps at a
tenth of the speed), not a preference to argue with, and there is no tick there either - its
libplacebo is one filter in a chain it runs inline, so the pass-and-intermediate trade never
existed on that tab.

**Spline is the fourth entry, libplacebo's alone - which now means Quick Convert's alone.** Mapping
the names across is honest and buys very little: hable against hable is about seven code values, so
the better backend changed almost nothing for the pick everybody uses. What is worth having is
libplacebo's own default curve, and it had no way to be selected - measured, `tonemapping=spline`
is byte-identical to what its `auto` chooses, and gives **129/152** at 100 and 203 nits against
hable's 115/143. Appended to the enum rather than slotted in beside the curve it beats, and
labelled "Spline (GPU)" because it is the one entry that cannot run everywhere. The AV1AN tab
cannot run it at all any more, so its dropdown does not offer it: `ToneMapConfig.Av1anModes` is
`AllModes` without Spline, its own list rather than an index into the other in exactly the way
`DeinterlaceUi.Av1anModes` is, and `ToneMapUi.ModeInEffect` takes the list the box was filled from,
the two being different lengths. Neither box saves its index, so no saved setting moved. On Quick
Convert the fallback story is unchanged: without a usable GPU `GetCurveName` falls back to hable
and `ResolveBackendAsync` warns, and **the log is the only place that can be said** - the readout
is drawn when the file loads, and which backend runs is not known there until the encode starts.

**`peak_detect` is on, and the only place the truth about a file's brightness can reach libplacebo
at all.** It was off for a while, for determinism, and the cost of off was measured on synthetic
patches whose content actually reached the metadata's peak - where it is a few code values. On a
real catalogue film it is most of the picture, which is what "tone mapping comes out darker than
the source" reports turn out to be: the ordinary UHD Blu-ray declares a 4000-nit mastering display
and a MaxCLL near the format ceiling over frames that top out around 600 nits, and a static mapping
priced for the metadata spends the top two thirds of the SDR range on highlights that never come.
Measured on content shaped like the reported file (frames to 610 nits, MDL 4000, MaxCLL 9978):
detection off put 100 nits at 129, reference white at 152 and the film's brightest pixel at a dull
186, where detection - and mpv, which is what the user compares against - puts them at **139, 176
and 235**. The film's own peak reaching full SDR white instead of grey is the whole complaint.

**And with detection off there is no other door.** All measured against the current BtbN build:
libplacebo reads the mastering display and nothing else - a MaxCLL of 610 and one of 9978 tone-map
byte-identically, so the app's own declared-peak logic can never reach it; the filter has no option
that takes a peak as a number; no ffmpeg filter can *write* side data (`sidedata` only deletes);
and stripping the side data is worse, because bare PQ is assumed to peak at the 10000-nit format
ceiling. The zscale chain takes its peak as a number in the filter string, which is why it has the
scan below instead.

**What detection asks in exchange is a continuous run, and that is a hard requirement, not a
preference.** Its history restarts wherever the stream does, and a restart mid-scene steps the
exposure: measured, a chunk boundary in a brightness ramp lands 6 code values off the continuous
answer and takes ~23 frames to converge - a visible pump, at up to one place per chunk. Quick
Convert meets the requirement for free, its chain being one ffmpeg over the whole file (two-pass
runs it identically twice), which is why it is the tab that runs libplacebo at all. av1an starts
and stops the `-f` ffmpeg around every chunk - exactly the QTGMC argument one filter over - so
meeting it on the AV1AN tab meant a whole-file render pass in front, which is the pass the user
removed.

**The per-chunk chain had a second, quieter reason it could never carry libplacebo: it would never
see the metadata at all.** av1an feeds the `-f` ffmpeg through y4m pipes, and y4m carries no side
data - so a libplacebo in that chain read neither MaxCLL nor the mastering display for any file
ever, and priced everything for the 10000-nit ceiling: measured through the real pipe shape,
126/148 at 100/203 nits, the darkest reading of all. The zscale chain is immune, its peak arriving
as a number in the filter string rather than as metadata on the frames - half of why it is the one
chain that can live per chunk - and `Av1anUi.GetVideoFilterArgs` carries no Vulkan device argument
for the same reason.

### The pass that used to run in front, and what has to come back with it

**The AV1AN tab rendered libplacebo as a pass of its own over the whole file - `Media/ToneMapPass`,
called from `Av1an.RenderToneMappedInput` - and both are deleted at the user's request: no
intermediate pass on that tab may be an encode, and this one was a full x264 re-encode of the film
before av1an could start, hours on a feature where the trim's stream copy costs seconds.**
`Av1anUi.ToneMapRendersInFront` was the statement of when it ran (libplacebo and nothing else),
`{tempDir}.tonemap.mkv` was where it went, a resume reused it after ffprobing its frame size
against the rebuilt command's geometry, and a failed pass failed the encode. All of it is gone;
`ToneMapConfig.ForceCpuChain` is the standing policy that keeps libplacebo off the tab, and the
`.tonemap.` suffix stays in `GetPreparedInputs` because earlier releases wrote such files and that
list's deletes are the last chance to take them along.

What the pass earned is recorded here against something like it returning, because every piece was
measured and each is a thing the per-chunk chain simply does not have. libplacebo's peak detection
measured every frame, where the CPU chain gets a sampled scan. The target-quality probes scored the
SDR frames actually being encoded, the pass baking its SDR into av1an's input - per-chunk filters
are invisible to them, so today a target-quality tone-mapped encode probes the HDR source again
(the standing note covers it by counting the chain). The tab's geometry folded into the pass
(`Av1anFrame.GeometryInPass`, both homes sharing `Av1anUi.BuildGeometryFilters`), sizing the
intermediate to the encode rather than the source - written at 4K for a 1080p encode it had paid
for four times the pixels, ~40 GB for a five-minute clip - with the folded output measured
framemd5-identical to the two-step it replaced; a bwdif in `-f` blocked the fold (a deinterlacer
must see whole fields, and the pass ran first), the fps resample never folded (it changes frame
*count*, the one thing nothing between detection and encode may do), and with `-f` emptied by the
fold each worker dropped from three processes to two while `Av1anMemory` priced its decode at the
encoded size. The intermediate itself was the measured-transparent x264 - CRF 6 `fast`
`-tune grain`, 10-bit pinned *inside* the filter because an output-side `-pix_fmt` lets the
negotiation land on 8 bits first and bake banding in - a number walked down a measured ladder from
CRF 12: grain retention flat at 99.9-100.2% across every rung, so PSNR and size discriminated, and
below 6 x264 pays lossless-class disk while still being lossy, dominated by FFV1 outright (10-bit
`-qp 0` is *not* lossless, high-bit-depth x264 shifting its QP scale - `DenoisePass.Ffv1Args`
still records that for the Film Grain utility's pass, which is the one lossless render left
anywhere in the app). The fused two-output shape that once wrote a denoised copy beside it had to
be lossless where this did not: grav1synth diffed it frame for frame, and a lossy reference reads
quantizer noise as grain. And the grain-domain rule that once pulled even the zscale chain in
front still holds wherever a measuring pass returns: grain measured on HDR frames synthesises
wrong-strength grain onto an SDR encode, worst in what used to be the highlights, so any
measurement must happen on the frames being encoded.

**The probe is `ToneMap.GetLibplaceboProblem`, and asking only whether the device came up is not
enough.** Three things have to hold. The filter has to be in this ffmpeg - BtbN's builds carry it,
a distribution's may not. A Vulkan device has to come up, which libplacebo will not arrange for
itself: without `-init_hw_device vulkan` it fails with "Found no suitable device, giving up" and
then "Failed creating Vulkan device!", and **ffmpeg carries on and exits 0 having written nothing**.
And that device has to be a real GPU - measured against Mesa's lavapipe, libplacebo initialises
perfectly and then takes 8.4-13.1s over 48 frames of 1080p where the zscale chain takes 0.9-1.8s and
a plain pixel format conversion takes 0.27s. A software rasteriser passes every check except the one
that matters, and the cost lands hours into an encode.

**The probe has now been shown to say *yes*, which until 2.8.78 it never had.** Everything above
establishes that it correctly refuses lavapipe; nothing established that it accepts a real GPU, no
session having had one. Run on an RTX 5080 (driver 616.56) against the 2.8.78 bundle's ffmpeg
`N-126264-g007cd1fd43-20260825`, all three gates pass: `-filters` carries ` libplacebo ` with the
spaces the match requires, the probe's exact command renders `MD5=`, and the device line reads
`Device 0 selected: NVIDIA GeForce RTX 5080 (discrete) (0x2c02)` - `(discrete)` where the refusal
looks for `(software)`. So `GetLibplaceboProblem` returns `""` and Quick Convert runs libplacebo on
this machine. The `-init_hw_device vulkan` position after the `-i` was re-confirmed on real hardware
at the same time, that having been measured only where no device could actually come up.

**libplacebo's cost is a flat startup rather than a per-frame rate, and on a fast CPU that makes it
the *slower* backend on anything short.** Measured on the same box (Ryzen 9950X, 16C/32T) over 1080p
PQ at three lengths, net of the decode:

| frames | libplacebo | zscale chain |
|---|---|---|
| 48 | 0.70s (69 fps) | 0.09s (533 fps) |
| 240 | 0.70s (343 fps) | 0.35s (686 fps) |
| 720 | 0.77s (935 fps) | 1.15s (626 fps) |

libplacebo is **constant** at ~0.7s however many frames it is given - Vulkan device creation and
shader compilation, paid once - with a marginal per-frame cost too small to measure at these
lengths, where the zscale chain is linear at ~1.6 ms/frame. So the crossover is about **440 frames,
18 seconds of 1080p**: below it libplacebo loses, by 7.8x at 48 frames, and above it wins and keeps
widening. Every real encode is far past that, so the shipped pick is right - but "the better
tone-mapper is also the faster one" is not true in general and is false for exactly the short clips
a person tests with.

**This does not weaken the software-rasteriser refusal, and the distinction is the whole point.**
lavapipe's 8.4-13.1s over 48 frames is 12-19x this GPU's *entire* fixed cost, and it is per-frame
slowness rather than startup - which is why it "lands hours into an encode" where a real GPU's 0.7s
never does. A threshold on wall-clock would still be the wrong probe; what the timing says is only
that the device-type test is measuring the right thing.

ffmpeg's own Vulkan setup prints what it chose at verbose level - `Device 0 selected: llvmpipe (LLVM
20.1.2, 256 bits) (software) (0x0)`, where the parenthesised word is `VK_PHYSICAL_DEVICE_TYPE_CPU`
spelled out - so the probe reads that rather than timing itself, which would need a threshold that
holds on every machine this runs on. Positive evidence is required in both directions: a device line
that cannot be found is a "no", since falling back wrongly costs the chain this app shipped with
where going ahead wrongly costs the 10x. The frame is checked too, by muxing it to `md5`, because
the failure being guarded against exits 0 - an exit code proves nothing and neither would a file's
existence.

**One thing this file used to give as a blocker is not one.** `-init_hw_device vulkan` is a global
option, and the note here said the AV1AN tab could not place it because av1an composes its own
per-chunk command with this app contributing only what goes inside `-f`. Measured, ffmpeg accepts it
**after the `-i`** - and with libplacebo now Quick Convert's alone, the question of av1an's handling
of the token is moot: only Quick Convert's command (through `ToneMapConfig.GetDeviceArgs`) carries
it, after its `-i`, and the probe places it in the same position so that what is tested is what
ships.

macOS remains the platform with no Vulkan at all without MoltenVK, and bundles no ffmpeg either - the
probe simply answers "no" there and the zscale chain runs, which is what those users already had.

Verified by running it: every chain the real `ToneMapConfig` builds for both backends across four
colour-data shapes and all three curves - 24 of them - rendered through ffmpeg in both tabs' command
shapes, composed with a crop, a scale and a pad, each landing on the predicted frame size and tagged
bt709/bt709/bt709 limited. libplacebo hands software frames back to the filters after it, so the
geometry needs no `hwdownload` and none is emitted. The probe itself was run through the real code
against lavapipe and correctly refused it. The geometry fold had the same treatment while it
existed - folded output framemd5-identical to rendering at the source's size and scaling after,
proven on the fused FFV1 shape because two x264 encodes at different sizes are not bit-comparable -
and that record went to the historical section above with the pass it verified.

### The exposure is a constant and the peak is the file's, and mixing the two is what made it dark

Every version of this chain on the internet uses `npl=100`, which is also what leaving it unset
does. Measured, that **clips everything above about 374 nits to flat white**: on a 1000-nit HDR10
master every specular highlight, practical light and window is gone. So the file's peak has to
reach the chain somehow. **Which of the two numbers carries it is the whole question**, and putting
it in `npl` - which is what the first cut of this row did - is what a bug report of "tone mapping
comes out quite dark" turns out to be.

`npl` is an *exposure*: it says how many nits linear 1.0 stands for, so it scales the entire signal.
Deriving it from the peak therefore darkens the picture end to end rather than compressing only the
highlights it describes. Measured on PQ patches, 100 nits came out at 112 of 255 for a grade
declaring 1000 and at **71 for one declaring 4000** - the same picture at 63% of the luminance, on
the strength of a number about its brightest pixel. The reported file was an ordinary UHD Blu-ray
rip, where a 4000-nit mastering display is the commonest thing there is.

`ToneMapConfig.AnchorNits` is that exposure and is now a constant, 266.667 - what the old chain
already used for a file declaring nothing, so the commonest file is anchored where it always was,
and the value measured closest to libplacebo on a 1000-nit source: **126/169 at 100 and 203 nits
against its 129/170**, where an anchor of 100 gives 173/214 and one of 150 gives 140/203.

The file's peak goes to `GetTonemapPeak` instead - the tonemap filter's own `peak`, in units of the
anchor, which is the point the curve maps to SDR white and so the level everything clips from. That
makes the readout's "highlights above X clip to white" exact rather than a rule of thumb, and it
leaves a declared peak costing what it should: 100 and 203 nits against a declared
1000/2000/4000/9999 come out at 126/169, 115/153, 108/144 and 104/139. A few code values, where the
old chain charged half the picture.

Floored at 1 - a white point no lower than the anchor - because under that this stops being a
roll-off and becomes an exposure boost: a declared 203 nits puts BT.2408 reference white at 252, and
a declared 100 puts 100 nits itself at 235.

**The declared peak was the next lie down, and the answer to it is to read the frames.** The
reported case is the ordinary shape of a UHD Blu-ray remux: MaxCLL 9978 - twenty-two nits under
the format ceiling, so it clears `PqCeilingNits` and reads as an honest measurement - beside a
4000-nit mastering display and a MaxFALL of 279, over a film whose frames measure about 610 nits.
Rolled off for 9978, 100 nits lands at 104 and reference white at 139, where a player that measures
the signal puts them at 139 and 176. PQ is absolute - a code value *is* a luminance - so
`ToneMap.MeasurePeakNitsAsync` reads the honest number straight off the file: a dozen sampled spots
(the autocrop's shape - what that actually costs is the section below, and it is not the "seconds of
decoding" this used to claim), `signalstats` YMAX per frame, and the PQ curve back to nits, depth and
range handled off ffprobe's own report of the decoded format. It runs in
`ResolveBackendAsync` only where the zscale chain will do the work - libplacebo measures every
frame itself - and only for PQ, HLG being relative with no peak passed at all.

`ToneMapConfig.GetEffectivePeakNits` is the formula: **twice the measured peak, never above the
declared one, never below the raw measurement, and the declared value alone where nothing was
measured.** The doubling is priced two ways. It is sampling insurance - the brightest frame of all
is likely brighter than the brightest frame a dozen spots saw - and it is where this chain's
shoulder behaves: at the exact measured peak the roll-off runs 400 nits to 252 and hard-clips
everything past 500 into superwhite, where at twice it lands 220/235/242 against libplacebo's
212/227/235 across the same bands, within a few code values of the reference everywhere. On the
reported shape that is 122 at 100 nits against yesterday's 104, with the film's peak at 242 instead
of 203. The cap is the declared value because a doubled sample past the file's own stated ceiling
describes frames the format says do not exist; the floor is the raw measurement because a file
declaring *less* than its frames hold is lying the other way, and the frames are the authority. A
failed scan returns 0 and the declared behaviour runs unchanged - the scan can only ever improve on
it, never block an encode. The log states measured, declared and effective per file, because the
readout is drawn at file load where none of this is known yet.

**That formula pins the answer above declared/2, which is the first thing to check before trying to
measure better.** `effective = max(min(M*2, D), M)`: for M between D/2 and D the answer is exactly D
whatever M was, and above D it is M. So on a file whose declaration is honest and whose content
reaches the top half of it, **no amount of scanning changes a single code value** - verified on a
fixture declaring 1000 nits and measuring 1004, where eight different scan variants render
byte-identically. The scan earns its place only on files that *overstate*, which is precisely the
reported UHD Blu-ray shape (MaxCLL 9978 needs M above 4989 before the cap binds), and there it always
matters.

### The scan is a dozen seeks, and the two ways of improving it are both worse

**"Seconds of decoding" was wrong about its own cost, and the cost is not where it looks.** The scan
decodes fewer than eighty frames and takes **82-96 s on 4K at a 10 s GOP**, 27-29 s at the ordinary
2-4 s ones and 8-14 s at 1080p - because every one of its twelve points pays a fresh process, a seek,
and `accurate_seek`'s decode-and-discard from the preceding keyframe, about keyint/2 frames. The bill
therefore tracks the **GOP length**, not the frame count. Batching the points into one process saves
nothing (a 60-input concat measured 8.4 s at n=12 and 13.7 s at n=20), so the seek is the cost, not
the launch, and seek cost does not grow with depth either (7.06-8.01 s per seek whether 10 s or 230 s
into the file). What it does *not* do is grow with the film's length - measured flat at 27.4-29.1 s
across 30 to 240 s of 4K - which is what keeps a feature the same price as a clip and is the property
any replacement has to beat.

**It also reads one more frame per point than it says.** `metadata=mode=print` is a filter and
`-frames:v` bounds the *output*, so the filter is handed one frame past the limit and prints its
`YMAX` too: measured on the shipped command shape, `-frames:v 5` prints 6 lines, and 1, 3 and 15 print
2, 4 and 16. So the scan reads 72 frames, not 60. Free accuracy, nothing depends on the number - but
the constant does not describe what happens.

**Denser sampling is the wrong axis.** 24x5, 48x5, 12x15 and 60x1 cost 2.3-4.6x the wall clock on a
4K file. 12x15 finds nothing that 12x5 does not - more frames at the same twelve places is the least
useful direction, the failure being *placement* rather than depth - and none of the evenly-spaced
variants can find a 2-second flash, a 0.2 s window at each of 60 points being unable to land on it.
Worth knowing which lever is cheap if it is ever revisited, though: another *point* costs a seek and
half a GOP of decoding, where another *frame at an existing point* costs one frame.

**A whole-file keyframe pass looks unanswerable and is a trap. This is the part worth remembering.**
`ffmpeg -skip_frame nokey -i F -vf signalstats,metadata=mode=print -f null -` reads the true peak on
six fixtures at 15-34x the speed of the current scan, never seeking at all. **That result was an
artifact of how the fixtures were built.** Each of them changed brightness with a *hard cut*, so
x265's scene detector had already put an IDR on the bright frame - `flash.mkv` carries keyframes at
exactly t=100.000 and t=102.000, the burst's own boundaries. The pass was reading the encoder's marks,
not finding peaks.

Swept across every position in the file rather than hand-placed, over 203 fixtures, it fails wherever
the encoder has not marked the bright frame for it: a 2.5 s in-shot rise under a fixed GOP, **0 of 30**
placements found, median 0.10x the true peak; a 0.25 s flash, **0 of 60**, never once above the base
level; real cuts plus an in-shot rise, 250 nits against a true 1251 in 10 of 11. **The structural
reason is one line: keyframes land at the cuts, so the pass samples the opening frame of every shot,
and a shot's brightest frame is rarely its first.** Open-GOP is not the explanation and was checked -
`-skip_frame nokey` does yield CRA frames, confirmed by NAL type (21, with RASL behind it), and it
changes nothing. Only at a realistic cut rate - a cut every ~3.3 s, so 27-34 samples - does it draw
level, within 1-2 code values after the headroom, and it is never ahead.

**Its failure direction is the bad one, which is what settles it.** A miss drives `GetTonemapPeak` to
its floor of 1.0, so SDR white becomes the anchor's 266.667 nits and everything above it clips: 203
nits reads **232 where the truth is 153**, and the 400- and 1000-nit bands both hard-clip to 255. That
is worse than having no scan at all, which is merely too dark (104/139) but keeps every highlight
distinguishable. And the speed inverts on exactly the files this feature is for: the pass is linear in
duration where the scan is flat, the measured crossover is 5.8 min of video at a 2 s GOP and 10.5 min
at 4 s, and a 2 h 4K feature costs it **10.6 min at 2 s and 5.7 min at 4 s** against ~28 s. The 15-34x
is real only on a clip well inside the crossover.

**The hybrid - `max(keyframe pass, sampled scan)` - is the only defensible form and still is not worth
it.** Strictly better than either by construction, and measured it beat the sampled scan alone in 0 of
60 flash placements and 0 of 11 mixed ones, the pass contributing nothing at all there; where it did
win the margin was slight, and it costs the sum of both.

Two things generalise past this feature. A fixture whose brightness changes only at cuts **cannot
test a keyframe-based measurement at all** - the encoder's scene detector has already answered the
question - so sweep the event across positions rather than placing it, and build at least one fixture
with `scenecut=0`. And what no synthetic fixture here can settle is whether real film's brightest
frame tends to sit at a cut; the physical argument that sunrises, explosions, lamps and lightning are
all within-shot events is reasoning, not measurement, and one real UHD HDR remux through all three
methods would settle it in a minute.

**And the deeper reason neither was worth it: a better maximum is not a better picture.** Measured
against libplacebo's own per-frame detection over a whole file, on content with an outlier, hitting
the *true* peak comes out ~14 code values **darker** than missing it across 95% of the runtime -
because a static roll-off built for a two-second event prices the entire film for it. libplacebo does
not face this trade, re-exposing per scene; the zscale chain cannot. So the room left is in what
`GetEffectivePeakNits` does with the number - a **percentile** rather than a maximum, which twelve
samples cannot support and a denser scan could - and not in finding the maximum more reliably. That
was not measured and is the next thing to try. It is a change to the formula, not to the scan.

**Both numbers have to be passed because the filter will not read either off the file.** The same PQ
ramp with and without a MaxCLL of 10000 and a 4000-nit mastering display tone-maps to byte-identical
output. The reason is worth knowing rather than filing under "metadata is unreliable", because it is
this app's own doing: by the time `tonemap` runs, the zscale above it has retagged the frame
`linear`, so the filter takes its fallback for a non-PQ transfer, which is a flat **10**. That is
what the chain was silently running on - a white point of ten times npl, i.e. 2.667x whatever peak
had been declared, on every file since the row was written.

**A peak declared at 10000 nits is the format's ceiling, not a measurement, and taking one at face
value crushed the whole picture.** `ColorDataUtils.GetDeclaredPeakNits` prefers MaxCLL because it
measures *this* content where the mastering display only describes the monitor - and that argument
stops holding at the top of the PQ curve, which is the largest number the field can hold and so what
gets written when nobody measured. The case is an ordinary UHD Blu-ray rip: x265 wrote
`cll=10000,258` beside `master-display … L(40000000,50)`, i.e. MaxCLL at the ceiling, a
measured-looking MaxFALL of 258, and a 4000-nit mastering display - the brightest the grade can ever
have been checked on. Under the npl-scaling above that put the exposure at 2666.7 and, measured on PQ
patches through this app's own chain, **203 nits came out at 23.2% of the SDR range against 33.6%**
off the mastering display's 4000, with 100 nits at 17.2% against 25.2%. 203 is BT.2408's reference
white, where the graded picture's white belongs.

**That evidence belongs to the chain it was measured on, and the guard now buys far less**, which is
worth knowing before reaching for it as the fix for a dark encode: with the peak going to the
roll-off rather than the exposure, taking the ceiling at face value costs 104/139 at 100 and 203
nits against the mastering display's 108/144 - four code values, where it used to be most of the
picture. It stays because it is still true that a number sitting on the format's maximum measures
nothing, and because a peak overstated by 2.5x still compresses highlights that were never there.

`PqCeilingNits` is the one statement of that, and it is applied to the mastering display as well as
to MaxCLL - a monitor declared at 10000 nits does not exist, so that field is the same
non-measurement under another name. With both at the ceiling it falls through to `AssumedPeakNits`,
which is where it belongs: that constant's own note already argues that assuming 10000 crushes every
mid-tone in the far commoner case, and this is that case arriving through the file rather than
through the default. An honest MaxCLL still wins over the mastering display, 9999 is still trusted,
and only the tone map reads any of it - `SetColorData` writes the file's own MaxCLL back out
untouched. Do not widen this guard to catch near-ceiling values like the reported 9978: where the
line would sit is unanswerable, and the measured scan above makes it moot - a lying declared peak
is now only ever the *cap* on a measurement, not the answer.

**This file used to say that an explicit `peak=` had been measured head to head and was worse, and
the measurement was sound while the conclusion was not** - which is the trap to avoid repeating. What
was compared was `npl=100` with a `peak=` against `npl` scaled by the peak, and `npl=100` really is
too bright (100 nits at 181 against 110, near this harness's 173 for the same pairing). The anchor
was doing that, not the mechanism: the two were varied together, so a bad anchor condemned a good
parameterisation, and the third combination - the old anchor *with* the peak passed - was never
tried. It is the best of the three. **Vary one at a time**, especially where both numbers land on the
same curve.

hable is still the closest of the three curves to libplacebo; mobius and reinhard are offered
because they are brighter and some sources want that.

### setparams, and the option that looks like the answer

The chain states its input colour with `setparams` rather than relying on what the decoder exposes.
A file whose tags live only in the container is otherwise refused outright with "code 3074: no path
between colorspaces" - and that is a state a Matroska file reaches easily, one written here reading
`color_transfer=unknown` from `-show_streams` and `smpte2084` from `-show_frames` on the same file.

**zscale's own `transferin`/`primariesin`/`matrixin` do not do this job.** They exist, they are
documented, and measured they leave the same file failing with the same error. `setparams` fixes it.
Stating tags that were already correct changes no pixel.

**A value read as Unspecified has to be filled in, and only that value.** `GetSourceStatement` states
what it has a name for and omits the rest, which is right for a value the frame still carries and
fatal for Unspecified, where the frame carries nothing either - measured, a PQ file stating its
transfer and neither its matrix nor its primaries fails with the same 3074 the filter is there to
prevent, and it is legal and ordinary (an HEVC stream with a partial VUI, a Matroska carrying only a
transfer element). So an HDR source that says nothing is told it is BT.2100, which is not a guess:
PQ and HLG are defined by it, and it specifies BT.2020 primaries and the non-constant-luminance
BT.2020 matrix alongside them. A *stated* value this app has no name for is still omitted rather
than overridden - substituting there would replace the file's own answer with this one's.

That one shipped past the first round of verification, and how is worth recording: every chain was
run over correctly tagged clips, so the clip's own frame tags supplied exactly what the omission had
left out and the broken chain passed. **A filter that states what a frame already says is not being
tested by a frame that says it.** The untagged sources are in the check now.

### The chain ends bounded, because otherwise the geometry decided the picture

**The zscale chain emits values past 1.0, and `desat` is what puts them there.** The curve does map
`GetTonemapPeak` to SDR white - but only with the filter's desaturation off. Measured on a PQ source
declaring 1000 nits (linear 3.766 at the anchor, passed as `peak=3.75`), `tonemap` returns **1.002 at
`desat=0` and 1.405 at ffmpeg's default of 2**. The default is not a thing to fix: it is what puts 100
and 203 nits on 126 and 169 of 255 against libplacebo's 129 and 170, where `desat=0` gives 147 at 203
nits and is nowhere near the reference. So the overshoot is the price of the calibration this file
already documents, and the only open question was where it lands.

**It landed wherever the geometry put it, which is the bug.** The last zscale hands on `gbrpf32le`, and
whether the out-of-range values survived depended on what came after: with no geometry, or with border
bars alone, zimg carried them into superwhite; with a resize below the chain, swscale destroyed them.
Measured on band centres, 10-bit limited Y, a 1000-nit band: **1023** for the tone map alone and for
tone map + borders, **943** for tone map + resize. One setting, two pictures, told apart by a resize
that has nothing to do with luminance. An upscale looked like an exception at maxY 1014 and was not -
that is bicubic ringing at a band edge, and at band centres it clamps like every other resize, which is
the reason to sample centres and never the frame maximum.

**"Which filter converts" is the wrong question, and a rule built on it makes a prediction that
fails.** Measured in isolation on synthetic float: a resample with no format change clips, a format
change with no resample clips, and only a genuine no-op - same size *and* same format - passes -0.5 and
4.0 through intact. swscale quantises float onto the k/65535 lattice and clips to exactly [0.0, 1.0] on
entry whatever it is then asked to do (0.9999 lands on 65531, 1.0001 on 65535), and nothing changes it:
every `flags=` from neighbor to spline, every `-intent`, every `-sws_backends`, with `out_range=full`
only moving the same clip to 0/1023. The prediction that fails is putting a zscale after a swscale
resize - zimg then performs the conversion and the superwhite is *still* gone, swscale having destroyed
it upstream while resampling. So it is swscale doing any work at all, not swscale doing the conversion.

`ToneMapConfig.ClampFilters` ends the chain with
**`format=gbrp16le,zscale=matrix=bt709:range=tv,format=yuv444p16le`** and settles it. All seven geometry shapes now measure identically - 505/677/912/940 at 100/203/400/1000
nits - with every pad at Y=64 and every predicted frame size exact. Clamping is the right half of the
two: it is what the reference implementation does, and superwhite in a stream tagged limited range is
detail a conformant player discards after paying bits to encode it. `gbrp16le` rather than a YUV format
because geometry may still follow and pinning 4:2:0 here would subsample the chroma before a scale
rather than after it; sixteen bits of gamma-encoded RGB is past what any output carries, so the bound
costs no precision.

**The zscale after it is not redundant, and dropping it moves the whole picture.** Its job is to claim
the RGB to YUV for zimg, because swscale's own limited-range conversion runs **hot**: measured on
integer input, swscale gives 64/284/504/724/943 where zimg gives 64/283/502/721/940, and the gain
scales with depth - +1 at 8-bit, **+3 at 10-bit**, +14 at 12-bit, +219 at 16-bit, consistent with
scaling the 8-bit limited gain by (2^n-1)/255 instead of 2^(n-8). Left to swscale the chain reads
506/680/915 where the calibration `AnchorNits` was chosen to hit is 505/677/912 - the 126 and 169 at
100 and 203 nits measured against libplacebo's 129/170, which would have become 127 and 170. Measured,
the pair together move the six saturated patches by **0/1023** against the pre-clamp chain where the
format alone moves them by 2. **The third filter is what closes the BT.601 hazard** - see it above -
and it takes swscale's hot conversion off the resize path for good with it: Quick Convert with a resize
read 506/680/915 and now reads the same 505/677/912 as every other shape. The three together cost
nothing, 3.90s against 4.14s on 24 frames of 4K, and were verified alongside the subtitle burn-in that
sits below the tone map on that tab. The libplacebo chain carries none of them - that backend maps to nominal white itself, so it
has no out-of-range values to disagree about, and there is no GPU in a web session to measure one on.

**The overshoot is a property of ffmpeg 7.0 and later rather than of the chain**, which is worth knowing
before reading an old measurement against a new one. `tonemap`'s desaturation path changed in 7.0 and
the new one *raises* in-gamut highlights: measured on the same fixture through three builds, the linear
values entering the filter are bit-identical while 203/400/1000 nits leave it at 0.42517/0.92387/1.40489
on master and 7.0.2 against **0.35508/0.59142/1.00194** on 6.1.1 - which is `hable(sig)/hable(peak)` by
construction, reaching SDR white exactly at the declared peak. With `desat=0` all three agree to five
decimals. So on 6.1.x a correctly-declared file has essentially nothing above 1.0 for a resize to clamp
and the divergence shrinks to content brighter than the declared peak.

**HLG overshoots further, and the bound covers it too.** That path passes neither `npl` nor `peak`, and
`tonemap` with no peak defaults to **10**: measured, HLG signal 0.90 and 1.00 leave the filter at
**1.07939** and **1.18833**, so reference white at 0.75 sits at **0.78862** and everything above it was
superwhite.

**Those three numbers are the corrected ones. This passage used to give 1.08348 / 1.19111 / 0.79709,
about 1% high, and no fixture or chain that could produce them has been found.** Ruled out one at a
time against the 2.8.78 bundle: it is not the build - BtbN `N-126264-g007cd1fd43-20260825` and gyan.dev
9.0.1 agree to all five decimals; not the range - a fixture built correctly for full and for limited
gives identical results (reinterpreting one fixture's codes as the other range does move it, which is a
different question and not this one); not the primaries conversion, which is a no-op on neutral patches;
not `npl`, whose whole sweep moves the values *down* from here and whose 100 reproduces the no-npl case
exactly, confirming zscale's default; and not the curve, hable being the only one of the three within
reach of these numbers at all - mobius gives 1.01594 and reinhard 1.02824 at signal 0.90. The old
numbers are therefore recorded as **not reproducible**, rather than as a build difference.

**The fixture recipe, which is the thing that was actually missing:** three flat patches at HLG signal
0.75, 0.90 and 1.00, written as 16-bit full-range `yuv444p16le` (code `round(s*65535)`, chroma 32768),
tagged with
`setparams=color_trc=arib-std-b67:color_primaries=bt2020:colorspace=bt2020nc:range=pc`, through the
HLG half of `ToneMapConfig.GetFilterArgs` -
`zscale=transfer=linear,format=gbrpf32le,zscale=primaries=bt709,tonemap=tonemap=hable` - tapped by
rendering straight to `gbrpf32le` and averaging an 8x8 patch of the G plane at each band's centre.

**Only the 0.90 point moves with the fixture, and it moves by exactly that fixture's own
quantisation** - which is worth knowing before reading two measurements of it as a disagreement. Signal
0.90 is not representable in any of these encodings: 8-bit and 10-bit limited both land on 197/219 =
788/876 = 0.899543 and give **1.07875**, where 16-bit or an exact 0.90 gives **1.07939**. The two ends
are bit-exact everywhere - 0.75 is 657/876 and 1.00 is nominal white - and come back as 0.78862 and
1.18833 from every fixture tried. So quote the bit depth with the middle number, or quote only the ends.

What survives untouched is everything the passage is *for*: HLG overshoots past 1.0 at both 0.90 and
1.00, reference white at 0.75 lands below it, and the bound below is what catches the difference.

The two converters also disagree about where 1.0 itself lands, which is why the resize ceiling is 943 and
not 940: measured on constant float patches, swscale clamps at 1.0 and writes **943** for every input from
1.0 to 10.0, where zimg writes 940 for 1.0 and runs to the 10-bit ceiling above it.

**The hypothesis this was found under was that the float link corrupts the matrix, and it does not.**
Worth recording so it is not investigated twice. The mechanism is real - the conversion genuinely moves
to the resize's swscale, `[Parsed_scale_13] fmt:gbrpf32le csp:gbr range:unknown -> fmt:yuv420p10le
csp:bt709 range:tv` - but on the shipped build swscale picks **bt709 at every output size**, 3840x2160
down to 320x240, so there is no BT.601 resolution heuristic to fall into. Measured, six saturated
patches at 320x240 against the zimg reference: worst chroma delta **2/1023**, where a 601-vs-709 mix-up
is tens to over a hundred. The clamp does not reopen it: with `format=gbrp16le` and no geometry the
conversion moves to `auto_scale_0`, logging `fmt:gbrp16le csp:gbr range:pc -> fmt:yuv420p10le
csp:bt709 range:tv`, and the same six patches come back within **2/1023** of the pre-clamp zimg
reference on all of no geometry, borders and resize. Neutral bands would not have caught that - a
matrix is invisible on greys, so colour patches are the check.

**On ffmpeg 6.1.1 the hypothesis is exactly right, and it is closed by ending the chain in YUV.** That
build's swscale does *not* honour the frame's colorspace: its log line carries no `csp:`/`range:` fields
at all, and the artifact reads correctly only as BT.601 - the red patch decoding to (195.4, 0.2, 0.0) on
BT.601 against (212.2, 19.0, 0) on BT.709, a **28.2 of 255** error, confirmed from the other side by
forcing `out_color_matrix=bt601` on master and landing one code value away. Both the swscale fix and the
tonemap change arrived in **7.0**.

For a while what kept that off users was not a fix but the `sidedata` incompatibility below - the build
that would get the matrix wrong refused the chain outright - and that was an accident of ordering rather
than a defence, worth nothing against a build carrying the newer enum with an older swscale. Probing the
sidedata types removed even that, since the chain now runs on 6.1.1. So the hazard is closed properly
instead: `ClampFilters` ends in `yuv444p16le`, the matrix is applied by zimg while the frame is still
RGB, and everything downstream is YUV to YUV where there is no matrix to get wrong. **The exposure was
Quick Convert's**, whose geometry sits below the tone map - measured on 6.1.1, a resize there produced
BT.601 while the AV1AN tab, whose geometry now sits above, was already correct. Verified on 6.1.1 in
Quick Convert's own shape: the red patch comes back (206, 434, 854), decoding as BT.709 to
(194.6, 0, 0.1) against a truth of (194.6, 0, 0).

**Four of the seven `sidedata` deletes do not exist before master, so a fallback ffmpeg refuses the
whole chain.** `DOVI_RPU_BUFFER`, `DOVI_METADATA`, `DYNAMIC_HDR_VIVID` and `AMBIENT_VIEWING_ENVIRONMENT`
are absent from 6.1.1 and 7.0.2 alike, and the graph dies on the first of them with "Undefined constant
or missing '(' in 'DOVI_METADATA'" having written nothing. This is precisely what `HdrSideDataDeletes`'
own comment predicts, arriving through the PATH fallback rather than through a bad name: on Ubuntu 24.04
- the current LTS, and the likeliest machine to be running its own ffmpeg - **every tone-mapped encode
failed before it started.** It also means the BT.601 hazard above cannot be met by any of the three
builds measured, since the one that would get the matrix wrong will not run the chain at all. That is an
accident of ordering rather than a defence, and it is not proof about builds nobody measured: an ffmpeg
carrying the newer enum with an older swscale would hit it.

`ToneMap.ResolveSideDataSupportAsync` is the fix, and it is the shape this codebase already uses for
av1an and SVT flags: ask the binary what it has and emit only that. It reads `-h filter=sidedata` once
per session, matches each name with a space either side - the table is columnar, so a bare Contains
would also match a longer name ending in a shorter one - and fills
`ToneMapConfig.SupportedSideDataTypes`, which `ToneMapUi.ResolveBackendAsync` awaits before either
backend builds a chain. Verified by running it against all three builds: master keeps 7 of 7, and 6.1.1
and 7.0.2 keep 3, dropping exactly the four named above. Then the artifacts rather than the parse - the
full chain writes **nothing** on both older builds where the reduced chain writes a correct frame, and
on master the two are the same size.

Two things about it are deliberate. **An empty parse is a failed probe, not an ffmpeg with no side data
types**: believing the latter would silently stop the chain deleting anything on some future change to
the help text's shape, and a leak nobody is told about is worse than a name that fails loudly - so
nothing parsed means the whole list goes out, exactly as before. And the drop is logged **visibly**
rather than at debug, because what it costs is real: the encode now runs where it used to fail, and the
price is that HDR metadata may survive onto an SDR file wherever the encoder carries frame side data
through, which is the leak `HdrSideDataTypes` exists to prevent and which libx265 was measured doing.
full: dumped, black is exactly **0.00000**, not 0.0627, so `range=tv` on an RGB output is a no-op for
the sample encoding. The geometry is exact alongside it - predicted `Av1anFrame.Encoded` against actual
output size with a tone map in the chain, **14/14** across exact pad/crop/stretch, upscale, anamorphic
de-squeeze and DVD-plus-borders.

### Where it sits in the chain

**On Quick Convert** it is second, right after the deinterlacer and **before both subtitle burn-ins**
- which means ahead of the bitmap overlay, and that one has to precede all the geometry. Subtitles are
graphics drawn to BT.709 white: composited into an HDR frame and tone-mapped afterwards they are
dragged through a gamut conversion and a highlight roll-off written for the picture. Measured on yellow
subtitle text, (240, 236, 95) burnt in before the tone-map against (232, 232, 71) after it - the
blue channel a third higher, which is the yellow washing out. That constraint is what pins it above the
geometry there, and it cannot be traded away: the bitmap overlay's position forces the issue.

**On the AV1AN tab it sits below the scale and above the border bars**, in
`Av1anUi.BuildGeometryFilters`, which is the one place that chain's order is stated. It used to be
second there too, purely to match Quick Convert - and this file and the comment both said that the
reason settling it there, the burn-ins, does not exist on a tab with no subtitle burn-in at all.
Parity was not worth its price. This is the one filter on that tab whose cost is per pixel and it was
being paid at the **source's** size, so a 4K to 1080p encode ran the roll-off over four times the
pixels it had to: measured on 24 frames of 3840x2160 in av1an's own per-chunk command shape, **4.36s
and 636 MB peak RSS above the scale against 1.82s and 337 MB below it**. The memory is the half that
matters, being per worker and on the axis `Av1anMemory` guards - where a machine that cannot hold them
all fails in the unreadable way that class exists to explain.

**It costs no detail, and that is the objection to answer rather than wave at.** Neither position
averages light: above the scale the downscale runs in BT.709 gamma, below it in PQ, and the physically
correct order - average in linear, map after - is what neither of them is. Measured against exactly
that reference, the two come out **equidistant**: PSNR-y 18.40 for both on high-frequency content,
where they sit 45.6 dB from each other. Mean luma matched to 0.1 of 1023 and the fraction of the frame
above Y=900 to 0.1%, across smooth, hard-edged and fractal fixtures - so no systematic brightening and
no highlight population moved. What separates them is 45.6 to 58.8 dB depending on content, which is
the order of an encode's own noise. A tone map is a per-pixel lookup and reads no neighbours, so it can
neither preserve nor destroy detail between them; the scale is the only step that touches detail and it
is identical either way.

**Every pad has to end up below it, and one of them had to be prised loose to manage it.**
`ResizeFill.Pad` emitted its letterbox inside `ResizeConfig.GetFilterArgs` as one string with the scale
it goes around, so an exact-size resize set to letterbox laid its bars down in the source's colour and
they were then tone-mapped - measured at **Y=66 against 64**. The cause is not the roll-off but
ffmpeg's own `pad`, which writes 10-bit black as Y=64 with **U=V=514**, 8-bit 128 scaled by 1023/255
rather than by 4; that 2/1023 of chroma is inert in an SDR signal and becomes 2/1023 of luma once a
tone map reads it as colour. `ResizeConfig.GetScaleFilters` and `GetTrailingFilters` split that string
at the one point a caller needs to insert something, and `GetFilterArgs` is the two joined - verified
byte-identical across all 14 shapes, so every other caller and the 1152 chains behind them are
untouched. All four pads now measure Y=64 exactly: pillarbox, letterbox, resize-with-bars, and the
exact-size letterbox the split exists for.

The mod-2 pad is the one exception and is left alone. It sits above the scale by necessity - it is what
stops an odd source reaching an encoder that will not take one - so nothing can put the roll-off above
it without paying the source-size cost the ordering exists to avoid, and what it adds is a single row
or column on a source with no crop on it.

**Being above the mod-2 pad is also what fixed an odd-sized HDR source failing outright**, which the
old order had shipped for as long as the row has existed. The chain's last zscale refuses a frame whose
dimensions are not divisible by the output's subsampling - "code 1027: image dimensions must be
divisible by subsampling factor" - and it refuses **before** the mod-2 pad had a chance to even the
frame, that pad having sat below the tone map. Measured on a 1920x1081 source: the tone map alone
writes nothing, and so does tone map followed by the mod-2 pad, where mod-2 pad followed by the tone
map writes 1920x1082. It is the *output* request rather than the source's own chroma that decides it -
a 4:4:4 odd source fails identically - so nothing about the file being unusual saved it. Found by
sweeping the geometry matrix over odd fixtures rather than by reasoning: two rows of that sweep came
back "NO OUTPUT" where every other row had numbers.

### The previews were the other half, and they were showing the wrong picture

**A preview of an HDR file drawn without a tone map is not a neutral picture of it - it is wrong, in
the direction that looks like a fault in this app.** PQ code values shown as though they were BT.709
come out washed out and grey, and that is what every thumbnail, the Cut window's scrubber and the
crop preview showed for exactly the files this row exists for. The row itself was answering
correctly the whole time; only the pictures beside it were not. `FfmpegExtract.GetPreviewFilters` is
the fix. It is display-side only and reaches no encode: the previews are the one place in this app
that tone-maps without being asked to, and that is safe precisely because nothing is written from it.

It reuses `ToneMapConfig` rather than composing a chain of its own: a fresh config has
`UseLibplacebo` false and `MeasuredPeakNits` 0, so what comes out is the zscale chain rolled off to
the file's declared peak. Both defaults are right here rather than merely convenient - the GPU probe
is a whole ffmpeg run and the peak scan a dozen more, which is not a thing to spend on a thumbnail,
and neither buys anything at this size. Hable for the curve, being the closest of the three to what
the GPU path would draw.

**After the scale, where the encode chain puts it before everything.** That position is a subtitle
question - graphics composited into an HDR frame get dragged through the roll-off - and there are no
subtitles here; what is left is this filter's cost, which is per pixel and runs through `gbrpf32le`
at 12 bytes of it. Tone-mapping a 4K frame to draw 360 pixels of it is four hundred times the work
for the 3 code values measured above.

`ColorDataUtils.GetColorDataCached` is what keeps it cheap, and it stores the **Task** rather than
the answer: `ExtractThumbs` launches its frames all at once, so a cache holding finished results
would have every one of them miss together and start its own probe. It is keyed on the file's length
and last write beside its path, for the reason `GetVideoInfo`'s own cache had to learn - a temp file
at a fixed path is otherwise answered from the previous run's reading.

Verified against exact PQ patches rather than by eye: a strip of bands at 10, 100, 203, 400 and 1000
nits (code values computed through the ST 2084 inverse), declared at a 1000-nit mastering display.
Untone-mapped - what shipped - those bands read **77 / 130 / 148 / 167 / 192**, the washed-out
picture: 10 nits is a light grey and the file's own peak never reaches white. Through the real chain
they read **51 / 129 / 179 / 248 / 255**. The middle two are the check that matters: this file's own
notes give 126 and 169 for this chain at a declared 1000 nits, and a preview is written full-range
JPEG, where 126 and 169 expand to 128 and 178 - one code value off each, so the preview is doing
exactly what the encode does. All three entry points were then driven through the real
`FfmpegExtract` out of the built assembly, with an SDR control that must stay untouched and the
thumbnail grid that exercises the shared probe.

### The output colour, and the trap on the AV1AN tab

On Quick Convert nothing has to be said about the output *to ffmpeg*: the final `zscale` retags the
frames as it goes, so the file comes out tagged bt709/bt709/bt709 - verified in the real
`-filter_complex`/`[vf]`/`-map`/`-pix_fmt` command shape, not just as a bare `-vf`. The direct
encoder binaries are the exception on that tab now, and they take the AV1AN tab's answer: they are
*told* their colour by flag out of `MediaFile.ColorData`, so `QuickConvert.BuildVideoCodecArgs` makes
the same swap around `GetArgs` that `Av1an.Run` makes - without it a tone-mapped direct encode is SDR
pixels tagged PQ and BT.2020.

**The HDR side data is a separate matter, and "the chain drops it" was an encoder-dependent
observation mistaken for a chain property.** Through libsvtav1 nothing carries frame side data into
the file, so it looked dropped; through libx265 - a wrapper that maps mastering-display and
light-level side data straight to encoder parameters - both chains produced an SDR BT.709 file
declaring a 4000-nit mastering display and a MaxCLL of 9978. `ToneMapConfig.HdrSideDataDeletes` now
ends both chains: `sidedata=mode=delete` filters taking out the mastering display, the light
levels and both Dolby Vision entries, the last because an RPU describing the reshaping of frames
that have since been tone-mapped is not merely stale but wrong, and the x265 wrapper can write RPUs
too. Verified through x265 on both backends: zero HDR side data entries on the output, band values
unchanged.

**The list is seven rather than four, and the three that were missing are the *dynamic* ones.**
HDR10+ (`DYNAMIC_HDR_PLUS`) is the one that is ordinary rather than exotic - a per-scene tone-mapping
curve carried by a good deal of streaming and disc content - and a set of curves describing scene
brightnesses that have since been mapped away is the same staleness the mastering display was deleted
for, one level more specific. `DYNAMIC_HDR_VIVID` is the same thing under another standard, and
`AMBIENT_VIEWING_ENVIRONMENT` describes the room the grade was checked in, which an SDR BT.709 file
has no use for. Every name was read out of `ffmpeg -h filter=sidedata` on the bundled build rather
than guessed: that option takes an enum, a name it does not have fails the filter graph outright, and
on this chain that is *every* tone-mapped encode rather than an edge case. The build prints
`DYNAMIC_HDR_PLUS` at 17, `DYNAMIC_HDR_VIVID` at 25 and `AMBIENT_VIEWING_ENVIRONMENT` at 26.

What can reach an output today is narrower than that list, and it is deliberately not the measure of
it: Quick Convert's direct encoders take y4m, which carries no side data at all, so only the
ffmpeg-library encoders can leak any of it. The original four were added after exactly that
discovery - libsvtav1 dropped them and libx265 wrote them back out, so "the chain drops it" was an
encoder's behaviour mistaken for the chain's - and the ffmpeg underneath this app is BtbN's rolling
master, where a wrapper that gains passthrough next month reopens the hole in a build nobody here
chose. Re-verified after the widening: the seven-delete chain renders (a wrong name would not), and a
PQ source carrying a mastering display and MaxCLL comes out through libx265 tagged
bt709/bt709/bt709/tv with no HDR side data left on it.

The AV1AN tab is the opposite: its encoders are *told* what they are encoding, as H.273 integers out
of `MediaFile.ColorData`. Unaccounted for, a tone-mapped encode produces the worst possible outcome -
SDR pixels in a file tagged PQ and BT.2020, which every player then expands again, and nothing about
it looks wrong until it is played. `Av1an.Run` swaps the colour for `ToneMapConfig.GetOutputColorData`
around the one `GetArgs` call and restores it immediately, rather than assigning it: the field means
"the colour of this file" everywhere else, and `ToneMapUi.IsRowRelevant` reads it to decide whether to
show the row, so leaving BT.709 behind would make an HDR file stop looking like one the moment it had
been encoded once. There is no await between the two, so nothing can observe the swap.

**The range is limited whatever the source was**, because the chain's last filter says `range=tv`
unconditionally. Carried across from the source instead - which is what this did first - a
full-range HDR master had SVT-AV1 told `--color-range 1` and x265 told `--range full` over pixels
that are limited: the blacks lift and the whites clip on every player that believes the tag.
Measured, a source ffprobe reads as `color_range=pc` comes out of this chain as `tv`.

`GetVideoFilterArgs` on that tab therefore takes the source colour as a parameter rather than
reading it off the file - the chain has to describe what is going in and the encoder arguments what
is coming out, so the two cannot share a source.

### Dolby Vision

**`IsHdr` reads the transfer curve and stops there, which is right - and it leaves one property of an
HDR file that this row has to treat differently.** A Dolby Vision file's dynamic metadata rides in an
RPU beside the picture, and what is underneath it depends entirely on the profile: 8.1 is an ordinary
HDR10 signal, 8.4 an HLG one, 7 an HDR10 base with an enhancement layer - all of which a decoder that
ignores the RPU shows perfectly well, just without the per-scene refinement. **Profile 5 is the
exception and the reason any of this is here**: its base layer is IPT-PQ-c2, which is not YCbCr at
all, so read as though it were, the colours come out the notorious magenta and green.

`VideoColorData.DvProfile` and `DvBlCompatId` carry it and `ColorDataUtils.HasUnusableBaseLayer` is the
question the row asks. **A compatibility id of 0 is not always a declaration, and reading it as one is
what this used to get wrong.** The rule was `profile == 5 || compat == 0`, on the reasoning that either
field can be the one a file states - but ffprobe prints `dv_bl_signal_compatibility_id=0` for a field
that was never written just as it does for a declared 0, the nibble having been carved out of a
`reserved = 0` field in a later revision of the spec, so a record written before it existed reads as 0
at its full 24 bytes with nothing malformed about it. Where the profile's own definition fixes the base
layer, the profile is the authority: 1, 3 and 5 are the profiles whose codec string ends in `n` for "no
cross-compatibility" and whose base layer is IPT whatever the nibble says; 2, 4, 6, 7 and 9 are SDR or
HDR10 by definition and cannot be made unviewable by it; 8, 10, 20 and anything unknown fall through to
believing the field, which is what still catches **10.0**, the same IPT base layer under AV1 that a
test on the profile number alone would miss.

**Nobody was hitting it, and the comment was the better reason to change it than the behaviour.** The
shape it was suspected of refusing is the ordinary UHD Blu-ray remux, and profile 7 does not carry 0 -
FFmpeg's own FATE reference output records real ffprobe readings of three real profile-7 samples, all
compatibility id **6** ("Blu-ray"), at `dv_version_major` 1. Neither mkvmerge nor an ffmpeg MP4-to-MKV
remux zeroes the nibble, both measured, so there is no ordinary route to one either.

**Where the two rules actually part company was recomputed** - the code comment and this file used to
give two different tallies of the same fixture run (four rows over 22 fixtures against six over 26),
which is the tell that neither was being checked. Driving `HasUnusableBaseLayer` and the old
`profile == 5 || compat == 0` expression over every pair the app can hold - profiles 0-10 and 20
against ids -1, 0, 1, 2, 4, 6, so 72 rows - through the built assembly: **15 differ, in two families.**
Profiles **1 and 3 at every id** (10 rows) were let through by the old rule and are refused now, which
is the fix rather than a side effect - both are codec-string-`n` "no base layer" profiles like 5, and
`profile == 5` missed them. Profiles **2, 4, 6, 7 and 9 declaring 0** (5 rows) were refused and are
not now, which is what the rewrite is for. **Profile 8 declaring 0 does not differ** - both rules
refuse it - though the old comment named it as one of the differing rows; 8 is in neither list and
falls through to the nibble. Recompute rather than reword if either list changes.
`DescribeDolbyVisionProfile` gained profile 10's
dot in the same change: **10.0 is refused and 10.1 is not**, and both read as "profile 10" in the
refusal that names this string. Profile 8 stays undotted for a declared 0, that one being malformed
rather than meaningful, and profile 0 is unreachable - `HasDolbyVision` tests `DvProfile > 0`.

**The refusal is on the CPU path and only there.** A zscale chain does no reshaping whatever, so it
cannot produce the right picture from a profile 5 file - that is a property of the bitstream rather
than a guess about a tool, which is what earns it a place beside the crop and frame-size refusals
rather than a warning.

**libplacebo is the other half, it is deliberately not refused, and the dependency was never libdovi.**
This file used to hedge that libplacebo "applies an RPU where its build carries libdovi" and that
whether the bundled build carries it was not measurable from here. Both halves were wrong. libplacebo
can be built against libdovi to parse RPUs *itself*, which is how a player uses it - but inside ffmpeg
it never has to, because ffmpeg has already parsed the RPU with its own decoder and attached it to the
frame, and `vf_libplacebo`'s `apply_dolbyvision` option, **on by default**, is what hands it to the
reshape shader. Measured against the bundled build, which carries no libdovi at all (zero `dovi` hits
in `-buildconf`): a real profile 5 RPU rendered with `apply_dolbyvision=0` comes out **bit-identical**
to the same stream stripped of its RPU, and with it on comes out a different picture. The fixture's RPU
carries an *identity* polynomial, so what moved is the IPT interpretation itself rather than a
reshaping curve - which is the stronger proof, since it rules out "only the reshaper fired".

Applied is measured; **correct is not**. Proving the render matches a Dolby-licensed decoder needs a
real profile 5 source and a reference, and no session here can obtain either. `LogDolbyVision` says
what happens rather than hedging about how the binary was configured, and the comment on it marks which
half is which.

**dovi_tool is not a way back in for the CPU path, and the refusal used to say it was.** The message
sent people to "dovi_tool's mode 2". Measured against dovi_tool 2.3.3 and a real generated profile 5
RPU: a P5 stream and its mode 2 and mode 3 conversions decode to **one framemd5**, and this app's own
chain - `GetFilterArgs` as it is built, `gbrpf32le` and all seven side-data deletes included - produces
**byte-identical** output from all three. dovi_tool rewrites the RPU's `ycc_to_rgb`/`rgb_to_lms`
matrices from IPT-PQ-c2 to BT.2020 NCL over samples it does not touch: it swaps the *instruction*, not
the picture. (Mode **3** is the tool's own name for the P5 conversion, not 2, though the two produce
byte-identical files - `To81` and `Profile5To81` map to the same `ConversionMode`.)

It is worse than a no-op twice over, which is why the advice had to go rather than merely be corrected.
On the **GPU** path the conversion destroys the render that works: libplacebo draws a third picture
from the converted file, 11.00 dB from the correct one and 38.59 dB from the base layer. And a
converted file declares **profile 8** once remuxed by anything that writes a `dvcC`, which is the field
this refusal reads - so following that advice turned a loud refusal into a **silent wrong output**,
which is the one outcome worth going out of the way to prevent. The message says that converting first
will not help, and why, in one clause.

**Which route it names instead depends on the tab, and getting that wrong is the same unactionable
advice under a new name.** `GetProblem` is shared, and it keys off `config.ForceCpuChain`: false is
Quick Convert, where the CPU chain means the *machine's* probe found no usable GPU, so another machine
runs libplacebo and reads the file - "tone map it on a machine with a usable GPU". True is the AV1AN
tab, where it is the *tab* rather than the machine, so no machine anywhere tone-maps a profile 5 file
there and the message names Quick Convert. That branch is the merge's own doing: the Dolby Vision work
and the removal of the AV1AN tone-map pass landed from either side of each other, and git merged them
with no conflict, leaving a message that sent AV1AN users to look for hardware that would not have
helped. `ForceCpuChain` is the discriminator rather than a tab flag passed in beside it - exactly one
of the two config getters sets it, and a second way of asking the same question is how the two come to
disagree.

**Bundling dovi_tool was weighed and is not wanted, and it is worth recording that the obstacle is not
the packaging.** It would be the easiest tool in `bundle-tools.sh` and the opposite of grav1synth in
every respect that made grav1synth painful: MIT, prebuilt release assets for all four RIDs (win-x64
3.3 MB exe, linux-x64 4.1 MB `static-pie` musl with no glibc floor, one `lipo` universal macOS binary
covering osx-x64 *and* osx-arm64 - where grav1synth must be compiled, needs ffmpeg dev headers whose
avutil soname the crate accepts on Windows, and is skipped outright on osx-x64), a single self-contained binary with no runtime
libraries, ~3-6 MB per archive. It simply has nothing to do here. Profile 7 FEL/MEL to 8.1 buys nothing
- `HasUnusableBaseLayer` is already false for profile 7, so the tone map already runs correctly, and
the chain deletes all seven HDR side-data kinds anyway. RPU **stripping** is already in-house:
`-bsf:v dovi_rpu=strip=1` leaves the pixels bit-identical with zero DV side data left. ffmpeg's own
`dovi_rpu` bsf offers only `strip` and `compression` and `dovi_split` only picks BL/EL/RPU, so **no
bundled tool can convert a profile** - that part is real, it just is not worth a fourth binary. One
thing to carry forward if it is ever revisited: a *refused* conversion still left a partial output file
behind, so the caller would have to judge the artifact and the tool's own "Done" line, exactly as this
project already does for ffmpeg and grav1synth.

**The probe is a second ffprobe, and that is the one arrangement that is safe rather than a tidiness
failure.** The configuration record is *stream* side data, so `-show_frames` never prints it -
measured, zero occurrences of `dv_profile` in the frames output of a file that has one. And the
obvious repair of adding `-show_streams` to the existing command is the thing that must never be done
to it: measured against the bundled build, the sections come out `[FRAME]` at line 1 and `[STREAM]`
at line 59, and `GetColorData`'s loop keeps whichever match it sees **last** - so the stream section's
own `color_transfer` would win, and this file already records a Matroska reading `unknown` from
`-show_streams` and `smpte2084` from `-show_frames`. The cheap-looking repair is the one that would
stop every such file reading as HDR at all. `ColorDataUtils.ReadDolbyVision` is separate for that
reason and says so.

The keys were read out of the binary before being parsed. ffprobe's DOVI block prints
`dv_version_major`, `dv_version_minor`, `dv_profile`, `dv_level`, the three present flags,
`dv_bl_signal_compatibility_id` and `dv_md_compression`. **`dv_profile` does not appear in a `strings`
dump on its own**, because it is shared as the tail of `s->cfg.dv_profile` - which is what suffix
merging does, and is worth knowing before reading such a dump as evidence of absence.

**A web session cannot obtain a Dolby Vision file, so one was built.** The record is a fixed 24-byte
`dvcC` box in the MP4 sample entry, so `scratchpad`'s `mkdovi.py` injects one into an ordinary HEVC
MP4 and grows the parent boxes - safe because ffmpeg writes `moov` after `mdat`, so nothing shifts a
sample offset. Real ffprobe then prints the whole record, and the app's own `GetColorData` was run
over four of them: profiles 8.1, 8.4, 7 and 5 all read back with the right profile, the right
compatibility id, the right name in the readout and the right answer from `HasUnusableBaseLayer`, with
an un-injected control reading as no Dolby Vision and a Matroska control still reading transfer 16 and
MaxCLL 9978 - which is the regression that mattered, the new probe leaving the old reading alone. 32
checks, no failures. What that does **not** settle is what libplacebo does with a real RPU, the
injected record having no RPU behind it; that is the real-machine check.

### What it does not cover

A stream copy builds no filter chain, so the Quick Convert box is disabled for one and
`ToneMapUi.GetQuickConvertConfig` reports Off - a copy of an HDR file is the one way to keep it
exactly as it is, which is an ordinary thing to want, so the row stays on screen saying the file is
HDR and only the curve is taken away. av1an's target-quality probes never see the tone map: the
zscale chain runs per chunk inside `-f`, invisible to them like every other filter there, and the
standing note covers it by counting the chain. (While the GPU pass existed it was the exception,
baking its SDR into the input the probes scored - gone with the pass.)

Verified by running it rather than by reading it, twice over. The original round: 42 chains built by
the real `ToneMapConfig` across 9 colour-data shapes and all four modes, each run through ffmpeg in
both tabs' command shapes, all parsing, all producing frames, all tagged bt709; a full chain -
deinterlace, tone-map, fps, crop, mod-2 pad, scale, burn-in, borders - composing correctly on both
tabs and landing on the predicted frame size; an SDR-to-PQ-and-back round trip returning every hue
and edge intact. The measured-peak round, against lossless x265 PQ patch strips carrying the
reported file's exact metadata: the current-vs-fixed band tables above; a 19-check harness driving
the real assembly's PQ math (spec anchor points, 10/12-bit, limited and full range), the real
`MeasurePeakNitsAsync` over the strips through the real ffprobe/ffmpeg plumbing (613 nits measured
where 613.1 is the code-value truth, full-range file read on its own scale, missing file answering
0), every branch of `GetEffectivePeakNits`, both chains' filter strings, and - while the pass
still existed - the real `ToneMapPass.RunAsync` end to end, output tagged bt709/limited, 10-bit,
zero HDR side data, and band-for-band identical to the continuous peak-detection reference. The chunk-seam number came from
rendering a 120-frame brightness ramp whole and again from frame 60 and comparing a constant band
frame by frame; the y4m stripping from piping the strip through av1an's exact pipe shape.

