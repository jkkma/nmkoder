---
name: av1an-tab
description: The full record of Nmkoder's AV1AN tab and the frame geometry both encode tabs share - av1an flag guards, the worker and thread plan, Av1anMemory and the out-of-memory failure that reports itself as exit code 0, parallel scene detection, the progress bar read from av1an's temp folder, target-quality probes not seeing the filters, colour handed to encoders by name, and the crop/resize/borders chain. Load for av1an, chunks, workers, scene detection, concat or chunk method - and for crop, resize, borders, anamorphic, SAR, letterbox/pillarbox or frame-size work on either encode tab (CropConfig, ResizeConfig, BorderConfig, Av1anFrame).
user-invocable: false
---

# The AV1AN tab - the full record

CLAUDE.md's `## The AV1AN tab` section is the digest: it carries the rules that have to hold
whatever you are doing in this area, and points here. This is the whole of it - every
measurement, every trap, and the account of what was got wrong and why it looked right.

It was moved out of CLAUDE.md **verbatim**, byte for byte, so nothing below is paraphrased and
nothing was dropped. That is also why passages below that say "this file" mean **CLAUDE.md**,
where this text used to sit - they were not rewritten, because rewriting them would have been
the one way to lose something in the move.

CLAUDE.md remains the authority on the project and its conventions govern this file too. A new
finding here goes here, in the house style `.claude/skills/record-finding` sets out; a new *rule*
that can be broken from outside this area goes in the CLAUDE.md digest as well.

## The AV1AN tab

`bundle-tools.sh` fetches av1an's latest *release*. Anything that depends on an
av1an feature newer than that release has to check for it at runtime rather than
assume it - av1an rejects an entire command over one unrecognised flag instead of
ignoring it, so an unguarded new flag breaks every encode.
`AvProcess.Av1anSupportsFlag` reads the binary's own `--help` for this.

**av1an passes on nothing that would stop the encoder it drives from asking a question, so a
prompt is this tab's to suppress.** `--disable-warning-prompt` does not appear anywhere in the
bundled `av1an.exe` (`0.5.2-unstable (rev 805dad6)`, toolchain 2.8.78), read out of its strings -
and aomenc and vpxenc both prompt over a `min-q` within 8 of `max-q`, which `AomAv1.json` and
`Vpx.json` offer as an ordinary 0-63 pair. Measured on a one-chunk encode with `--min-q=56
--max-q=63`: the chunk dies as `encoder crashed: exit code: 1` with `Continue? (y to continue)`
quoted in av1an's own stderr, is retried once, and is then given up on with nothing written. So
av1an *reports* it plainly - better than the direct path did - and the encode is dead either way.
`CodecUtils.GetNoPromptArg` decides the flag for both tabs; `Av1an.Run` resolves it (the lookup is
async, `GetArgs` is not) into `CodecUtils.NoPromptKey`, and `AomAv1`/`Vpx` write it **inside**
av1an's `-v "…"` string, that string being the only thing that reaches the binary. CLAUDE.md's
"Driving the encoder binaries directly" carries the rest of the measurements, the Quick Convert
half among them - **including av1an's own `-y`, whose absence is silent**: given an existing output
path av1an asks, takes the default, and exits **0**, which `Av1an.Run`'s `exitCode != 0` gate cannot
see. Both `-y`s (`Av1an.Run`, `Av1anSceneDetect.TryPrepareScenesFileAsync`) carry a comment saying so; do not
read either as boilerplate.

The same trap applies one level down, to the encoders av1an drives - and for SVT-AV1 the
answer is a policy, not just a guard: **this project ships the PSY line or nothing.**
`bundle-tools.sh` takes `SvtAv1EncApp` only from `juliobbv-p/svt-av1-hdr`. Mainline
`AOMediaCodec/SVT-AV1` used to sit behind it in `SVTAV1_REPOS`, and MSYS2's mainline package
used to fill in on Windows; both are gone, because both substituted a mainline binary under
the same filename with nothing saying so. A release with no PSY build is now a visible skip.
Do not restore either fallback. macOS bundles no encoder and no longer suggests Homebrew's
`svt-av1`, which is mainline.

It still has to be checked at runtime, because a user's own `PATH` is not something the
bundler controls. PSY-only parameters (`noise-adaptive-filtering`, `kf-tf-strength`,
`tx-bias`, `noise*`, `cdef-scaling`…) cannot be assumed present, and SVT rejects the whole
command over one it does not know. `AvProcess.EncoderKnowsFlagOrIsUnknown` asks the encoder
binary, and the Advanced tab's content presets drop what it does not have - which keeps the
encode alive, and the log says which parameters went and that it means the binary is mainline.

Those presets are written for the PSY line and deliberately do not compensate for mainline:
a value carried only to make them half-work there is a no-op on every build they are actually
for. `enable-qm` was one and has been removed. Do not add one back. (Mainline defaults
`--enable-qm` and variance boost *off* where the PSY line has them on, so on mainline some
parameters are accepted and then quietly do nothing. That is not a thing to work around.)

**SVT-AV1 has three ways to ask for film grain and takes exactly one of them.** The Video tab's
Grain Synthesis row now owns two of the three itself - see "Grain synthesis" below, which is why that
row became a mode selector - so what is left to collide is a row typed by hand in the grid beside it.
The row writes `--film-grain` or `--fgs-table`; the Advanced tab's Film Grain & Noise group holds `noise`,
which is svt-av1-hdr's own second synthesiser (0-200, and its help says 50 is roughly a
`--film-grain 50`), and `fgs-table`, which applies a table from a file. They do not compose.
`--fgs-table` switches `--noise` off, and either of them switches `--film-grain` off - the first
in `app_config.c`, the other two in `enc_handle.c`'s `set_param_based_on_input`, each with an
`SVT_WARN` nobody here ever sees: that goes to the encoder's stderr, which av1an collects per
chunk into a log `HandleTempFolder` deletes on a successful run. So the box was set, ignored, and
never mentioned.

Denoise is the half with teeth, and the reason `Av1anUi.GetGrainSynthProblem` says so rather than
leaving the number to speak for itself. `film_grain_denoise_apply` is only read inside
`apply_denoise_2d`, which `svt_aom_picture_pre_processing_operations` only reaches on the
`--film-grain` branch - so the checkbox is dropped along with the strength, and neither of the
other two paths touches the source. Someone who asked for "denoise, then synthesise" gets
synthetic grain laid over the grain that was already there, which is the opposite of the setting.

`aomenc` is clean here: `AomAv1.json` carries no grain rows at all, and the grid is rebuilt per
encoder, so only SVT-AV1 can be holding one. No content preset sets either of the two rows that
collide - SVT-AV1 has two presets and neither reaches into that group at all - so clicking one
cannot cause this. `adaptive-film-grain` is a genuine companion to the box rather
than a rival - `apply_denoise_2d` reads it directly - and `noise-chroma`, `noise-chroma-from-luma`
and `noise-size` are `--noise`'s own satellites, reset with a warning when it is 0, so they are
only reachable *through* the collision above. `noise-adaptive-filtering`, `noise-norm-strength`,
`ac-bias` and `tune 5` are grain *retention*, a different mechanism, and do not conflict.

**`tune 5` is a bundle that overwrites six other rows, and it wins over whatever they hold.** It sets
`enable-tf 0`, `enable-cdef 0`, `enable-restoration 0`, `complex-hvs 1`, `ac-bias 4.00` and `tx-bias 1`
- in `set_param_based_on_input`, which runs *after* the whole command line has been parsed, so the
order the flags are written in cannot save a row set beside it. The only notice is an `SVT_WARN`,
which goes to the encoder's stderr, which av1an collects per chunk into a log `HandleTempFolder`
deletes on a successful run: the same silence the grain collision above hides in. Four more rows go
inert with them rather than being overwritten - `cdef-scaling` (read only where `cdef_level` is not 0),
`tf-strength` and `kf-tf-strength` (no temporal filtering left to strengthen) and
`noise-adaptive-filtering` (it sets nothing but the two "back off on a noisy frame" flags for CDEF and
restoration, and both filters are already off). **No content preset sets `tune 5`** - the SVT-AV1
Grainy Film / 35mm Scan one did and has been removed, see "Grain synthesis" below - so a 5 in that row
was typed by hand. Read out of the fork's own source rather than its documentation, which describes the
bundle without saying it is applied last.

`Av1anUi.GetFilmGrainTuneProblem` is for the row typed by hand beside it, and it separates the two
halves rather than lumping them: one is overwritten, the other is left exactly as set and stranded, and
a user told the wrong one will go looking for the wrong thing. A row set to what the tune sets anyway
is **not** reported - `ac-bias 4` against the tune's `4.00` is agreement, not a collision, and sending
someone to clear a row that matches the encode is worse than saying nothing. `complex-hvs` used to be
in the message and not in the check, there being no row for the tune to overwrite; the row exists now
and the check covers it, so all six of the bundle's assignments are reported alike.

Two rows were added to `SvtAv1.json` with it, both read out of the shipped binary's own `--help` and
then passed to it: `complex-hvs` (0-1, default 0) is the encoder's most expensive perceptual model,
and the one tune 5 sets for itself; `enable-variance-boost` (0-1) is the switch for a family the grid
already carried three shaping parameters for - `variance-boost-strength`, `variance-octile` and
`variance-boost-curve` all did nothing without it and there was no way to turn it off. This build
ships it **on** where mainline SVT-AV1 defaults it off, which is one more place a parameter written
for one line does not describe the other.

**The Denoise box beside it follows the strength as well as the encoder.** Both AV1 encoders read a
denoise flag only where they are synthesising grain at all - aomenc's `--enable-dnl-denoising`
applies "when denoise-noise-level is enabled", and SVT-AV1 answers one set against `--film-grain 0`
with "ignored when film grain is off" - so at a strength of 0 it was a tickable box that did
nothing. **`GrainSynthUi.Apply` is the one statement of that** - `encoderDenoise.IsEnabled =
strength > 0`, reached from `ApplyControlVisibility` for both tabs' rows. (This paragraph said
`Av1anUi.ApplyGrainDenoiseEnabled` until 2.8.70, which was right while the row was the AV1AN
tab's alone; the row moved into `GrainSynthUi` when Quick Convert grew one - see "Both encode
tabs carry the row" in the grain-synthesis skill - and the sentence did not move with it. The
behaviour never changed, only where it is written.) It does not *clear* the tick, only disable
it: a strength dropped to 0 and put back should bring the choice back with it. The readout
states the strength against its 0-50 scale, and from `GrainSynthConfig.HeavyDenoiseStrength` (30) up
names the denoise as a cost of its own - both encoders denoise at the number the strength sets, so the
top of the scale is maximum smoothing of the real picture as well as maximum synthetic grain, and
nothing else on screen said the two rise together.

**An advanced row naming a parameter the binary does not have refuses the encode, and only for
SVT-AV1.** The grid is filled from a JSON list written against the build this project bundles,
while the binary that runs is a build-time accident - the bundle falls back, macOS bundles no
encoder, and a user's own PATH is not the bundler's to control - and an encoder refuses the whole
command over one parameter it does not know. `Av1anUi.GetUnsupportedAdvancedArgsProblem` asks
before the run and names what it found; on SVT-AV1 that is a mainline binary against a PSY-line
list, which the message says outright.

It **refuses** where `GetApplicablePresetValues` **drops**, and the difference is who chose the
value. A preset is a bundle that stays useful with one entry taken out, and the dropping happens as
it is applied, in front of whoever just clicked it. A grid row was typed by hand on a run that has
already started, where quietly dropping a setting is the failure the check exists to prevent - the
same argument `QuickConvertUi.GetBorderProblem` makes.

**Asking is only sound where `--help` lists everything, which is why no other encoder is asked.**
SvtAv1EncApp's `--help` prints its whole token table. x264's does not: it is the short list by
design, with the rest behind `--longhelp` and `--fullhelp`, so half of `X264.json` - `qpstep`,
`merange`, `partitions`, `ipratio` and the like - would come back unsupported from a binary holding
every one of them, and a legitimate encode would be refused. `EncoderArgPresets.Av1anEncoderName`
returning "" for everything but SVT-AV1 is where that limit lives; do not widen it to the other four
on the grounds that the map is obviously incomplete. The others are simply unverified, which for
this question is the same answer as x264's.

**"Threads per Worker" is the encoder's own thread count, and each encoder spells that
differently.** The box writes one number into `GetVideoArgsFromUi`'s `threads` entry and every
encoder's `GetArgs` picks its own flag out of it: `--threads=` for aomenc and vpxenc, `--threads`
for x264, `--lp` for SVT-AV1, `--pools` for x265. x265 is the one to be careful with, because it
has no `--threads` at all and the obvious-looking neighbour is not the same thing:
`--frame-threads` is how many frames are encoded *concurrently*, and the worker pool underneath
them is one thread per core whatever F is set to. So through 2.8.20 that box was the only setting
on the tab that limited no threads - eight workers on a sixteen-core machine ran eight
sixteen-thread pools, and the machine was oversubscribed eightfold on the one encoder where the
number looked like it was being respected. `--pools` is the pool; x265 derives its own frame
thread count from the size of it, which is why F is no longer sent alongside.

Measured against x265 3.5 rather than read out of its documentation, because the two flags look
interchangeable and are not: on a four-core machine `--frame-threads 2` still reports "Thread
pool created using 4 threads", where `--pools 2` reports 2. `--threads` is not a flag it has.

**The two boxes' first-run defaults are one decision, not two.** `Av1an.GetDefaultThreadPlan`
returns both, because what has to track the machine is their *product*: workers on its own says
nothing about how much of the CPU is booked. Through 2.8.20 they were a computed worker count
(`ceil(cores × 0.4)`, capped at 32) and a literal `2` in `Config`, sitting in different files, whose
product landed near 0.8 threads per core by coincidence - and nothing would have noticed if either
moved. The cap is a *memory* guard, since a worker holds an encoder instance and its frames, but
with the thread count pinned it was silently a CPU cap too: past 80 logical processors - where
`ceil(0.4c)` first reaches 32 - nothing took up the slack it gave away, so a 128-core machine
booked half of itself and a 192-core one a third. Threads takes the remainder now. Do not put a
literal back in `Config` for either key.

This reaches a **fresh config only**, which is worth knowing before reading a bug report against it.
`Config.Get` consults a default where the key is missing and not otherwise, so an existing
installation keeps the worker count it already has - including the inflated one the float artifact
below was producing. That is deliberate: a saved count may have been tuned by hand, and nothing here
can tell that apart from an untouched default, so overwriting it would take a setting away from
whoever had bothered to pick one.

The split rounds up, so the first counts past the cap come out a little over budget rather than
under (88 cores books 96 threads). That is the right way round to be wrong: av1an's chunks are
independent, so mild oversubscription is timeslicing, where rounding to nearest gives 96 cores a
thread count of 2 and leaves a third of the machine idle - the very hole being closed.

**The 0.4 in that function is a double and must stay one.** It was `0.4f` multiplied against a
`(double)` core count, so the widened float came out a hair above the exact value and the ceiling
read that as a whole extra worker - on every core count where the product is exact, which is every
multiple of 5. A 20-thread machine defaulted to 9 workers instead of 8 and a 10-thread one to 5
instead of 4. The `(double)` cast that caused it was never needed: int times double is already
double.

The `Value` on both `NumericUpDown`s in the XAML is a designer placeholder and agrees with nothing.
`LoadConfigAv1an` fills them from the config on every launch, and reading a key that is absent
writes its default first, so what a user sees always comes from `GetDefaultThreadPlan`. A comment
there says so; do not "reconcile" those literals with it.

**Running out of memory does not report itself as running out of memory, and that is the whole reason
`Av1anMemory` exists.** A worker is three processes - the source pipe, the ffmpeg applying this tab's
`-f` chain, and the encoder - and when the machine cannot feed them all, the OS kills one of the first
two. The encoder then reaches the end of a truncated stream, finishes normally and **exits 0**, and
av1an counts its output and fails the chunk over the difference:

```
WARN encode_chunk: Encoder failed (on chunk 1):
encoder crashed: exit code: 0
stdout:
        FRAME MISMATCH: chunk 1: 47/239 (actual/expected frames)
source pipe stderr:
        Error: fwrite() call failed when writing frame: 49, plane: 0, errno: 32
```

Not one word of which says memory. `errno 32` is EPIPE - the *downstream* process having gone - and
"encoder crashed: exit code: 0" is av1an's phrase for a frame count it did not like, not for a crash.
The chunk is retried to `--max-tries` and the run gives up hours in, each doomed chunk having been
encoded several times first. Reported against a 4K HDR film cut to four minutes: four of the eleven
in-flight chunks died inside 45 seconds. Reproduced rather than inferred - ten of these pipelines run
at once on a host with less RAM than they want produced three short chunks, with the kernel logging
`Out of memory: Killed process (ffmpeg)` and the encoder beside it exiting 0.

**And none of it reached the screen.** Every encode logs through `LogMode.OnlyLastLine`, which rewrites
the last row on each progress line, and `Logger`'s `Warning` is replaceable by design - a damaged source
prints scores of them and pinning each would bury the log. So av1an's `Encoder failed (on chunk 1)` was
overwritten within a fraction of a second, every time, and a run that had already lost four chunks read
as a healthy one sitting at 4%. `Av1anOutputHandler.NoteChunkFailure` says what a short chunk means, once
per run in a line that is not replaced, and `ReportChunkFailures` tallies the rest when the run ends.
Short and long are opposite faults and kept apart: short is the frames stopping, long is a filter writing
more than it read, which is what `--ignore-frame-mismatch` is for. In the visible-console mode those
lines are never seen live - no output is redirected there - so the failure-tail read described in the
scene-detection section (`ReadFailureTail`) feeds the same explainer from av1an's log file after the
exit code lands; its once-per-run guard is what lets the piped mode see a line twice for free.

**Every constant in `Av1anMemory` was measured**, by running the real process at three frame sizes and
fitting a line through the peak RSS. The fits are near-straight - SVT-AV1 came out at 672, 678 and 623 MB
per megapixel at 720p, 1080p and 1440p - so a base plus a slope is the whole model, and a third digit
would be inventing one. The spread between encoders is the part worth knowing: **SVT-AV1 wants two to
three times what the others do** (605 MB/MP against x264's 397, x265's 275 and VP9's 194, all 10-bit),
which is the same fact `ApplyWorkerCount` already acts on by giving it two workers fewer. A float step in
the chain is the other big term: a tone map converts to `gbrpf32le`, 12 bytes a pixel against a 10-bit
4:2:0 frame's 3, measured at 508 MB for a 3840x2076 source against 160 MB for the same chain without it.
The reported encode comes to 2.3 GB a worker and 24.8 GB for eleven.

**That float slope is charged against the *encoded* frame, not the source's**, which is where the tone
map now runs - see "Where it sits in the chain" for why it moved below the scale. The rest of the chain
stays on the source's size, the decode and every filter down to the scale still handling source-size
frames. It is not a small correction on the case it was written for: measured on 4K to 1080p, 636 MB
peak RSS with the roll-off above the scale against 337 MB below it, of which this model predicts 261.
A custom filter converting to float *above* the scale is under-counted by that split, which is the
right way round to be wrong for a floor - the alternative charged every tone-mapped downscale for
pixels nothing has held since the roll-off moved.

`RequiredHeadroom` is why it does not simply compare the two numbers. The estimate is a **floor**: the
VapourSynth chunk methods hold a frame cache above the decoder that was measured, aomenc could not be
measured at all, and what else the user has open is not this app's to know. Eleven workers at 24.8 GB
against the 25.6 GB a 32 GB machine is credited with *passes* a bare comparison - and that is the run
that failed. It warns rather than refusing, unlike the crop and frame-size checks beside it, because
those state a certainty and this states an estimate; a run stopped by a wrong guess costs more than the
one it would have saved. It names the worker count that fits, which is the only actionable thing in it.

Do not reach for `MaxDefaultWorkers` as the fix for a report like this. That cap is a default and
`Config.Get` consults a default only where the key is missing, so it reaches nobody who has already run
the app once - which is everybody who can file the report.

`--set-thread-affinity` is **not** what that box means, and there used to be a
`Av1anUi.GetThreadAffArgs` building it that nothing called - so the flag has never reached av1an,
and reinstating it is not a fix for anything above. Affinity *pins* each worker to N cores rather
than telling the encoder how many threads to start: it leaves cores idle on a machine whose count
is not a multiple of the pin size, and it stops the OS moving a worker off a core something else
wants. The comment where it used to sit says so; do not read its absence as an oversight.

**Colour goes to these encoders by name, and no two of them spell it alike.**
`ColorDataUtils` holds the values as H.273 integers and hands SVT-AV1 and x265 exactly that,
but aomenc and x264 want names - and their lists differ from each other *and* from the one
this file's own `Get*String` functions emit. aomenc refuses a name it does not know outright,
printing its usage text and encoding nothing, so one wrong spelling kills every chunk of the
run rather than being ignored the way an unknown integer would be.

`FormatForAom` rewrote two names and was wrong for **eleven of the thirty-two** the app can
emit: `gamma22`, `gamma28`, `linear`, `smpte240m`, `iec61966-2-4`, `fcc`, `smpte428` and the
rest are all ordinary tags a real file carries, and every one of them failed an aom encode
outright. Three per-kind tables replace it, mirroring the x264 trio beside them. Each entry
was read out of `aomenc --help` and *then* confirmed by passing it to the binary - including
the names that pass straight through - because what a tool documents and what it accepts are
two questions, and this file's whole history with av1an says to ask the second one.

**The mastering display and the light levels go by flag too, and for eighteen months they went
nowhere.** The four tags above are handed over as numbers because y4m carries no side data; the
static HDR metadata is the same argument and was simply never finished, so **every HDR-preserving
encode this app has ever made came out declaring no peak brightness at all.** Measured against the
shipped SvtAv1EncApp with nothing passed: a source carrying a mastering display and MaxCLL 9978
encodes to an output with **zero** HDR side data on it. The file still says PQ and BT.2020, so it
plays - and a display handed no mastering metadata falls back to its own assumption, which is the
crushed-mid-tone case this file already documents from the other direction. Nothing about it looks
wrong until it is played on real hardware, which is why nobody reported it.

`ColorDataUtils.GetSvtHdrMetadataArgs` closes it for SVT-AV1 on both tabs. **The units are the
file's own decimals, not x265's scaled integers, and getting that wrong is silent** - measured,
`G(0.265,0.690)…L(4000,0.005)` reads back out of the bitstream as exactly those values, where
x265's `G(13250,34500)…L(40000000,50)` spelling is clipped to 1.0 on every coordinate and 6445568
nits of luminance behind one `Svt[warn]: Invalid mastering display info will be clipped`, on the
encoder's stderr, which av1an collects per chunk into a log `HandleTempFolder` deletes on a
successful run. What `GetColorData` already parses is the decimal form, off ffprobe's fractions and
mkvinfo alike, so nothing is converted.

Three things about it are load-bearing. **A tone-mapped encode suppresses it for free**, because both
callers sit inside the `GetOutputColorData` swap, which hands back the four BT.709 tags and nothing
else - so the helper reads empty coordinates and emits "". **The quoting differs by tab and is not
cosmetic**: the mastering display's parentheses are shell syntax off Windows, so Quick Convert, which
launches the binary itself, wraps them, where the AV1AN tab must not - everything there lands inside
av1an's own `-v "…"` string, whose quotes already protect them and which is split again on
whitespace, the same split the grain table's bare path is written for. And **`--content-light` needs
both numbers**: measured, a lone value is `Error: Invalid parameter 'content-light'` and the encode
does not start, so a file stating only MaxCLL is given a MaxFALL of 0, which is the "unknown" that
field already means.

**x265 is deliberately not covered.** `X265.json` already carries `master-display` and `max-cll` as
hand-typed grid rows, so automatic passthrough would be the same argument written twice - and its
format is the scaled-integer one, a conversion that wants measuring against a real x265 binary. The
linux release ships only `SvtAv1EncApp` under `bin/av1an/enc`, so that measurement could not be made
here. Do it before wiring x265, and take the manual rows out in the same change rather than leaving
both.

Verified by running it: 25 checks through the built assembly and the shipped SVT-AV1-HDR binary
pulled out of the published 2.8.57 linux tarball - both encoder classes' own `GetArgs` carrying the
flags with the right quoting, the binary accepting the AV1AN tab's generated string without clipping
it, and ffprobe reading `red_x=44564/65536`, `white_point_x=20493/65536`, `max_luminance=1024000/256`,
`max_content=9978` and `max_average=279` back out of the bitstream, against a no-flag control that
carries none of it.

**ffprobe's printed names are a fourth vocabulary**, and reading them with this file's own is
what made HLG unreadable. It prints `arib-std-b67` where these tables say `bt2100`,
`iec61966-2-1` against `srgb`, `smpte170m` against `bt601` for primaries, and `bt2020c`
against `bt2020` for matrix. All four came back as 2, Unspecified - and the Color Data utility
then *muxed that 2 in*, writing "unspecified" over a tag that was already there, which is
worse than failing to read it. Round-tripping through the file's own names is clean, which is
exactly why this looked right in isolation. The vocabulary was established by tagging a file
with every value in turn and probing it back out of the bundled ffprobe; do that again rather
than trusting either list from memory.

**Two of ffprobe's values are read as Unspecified on purpose**, because SVT-AV1 and x265 are
handed these numbers raw: `gbr`, which it prints for an RGB pixel format that every encode
here converts away from before writing a frame, so signalling Identity would describe planes
that no longer exist; and `ebu3213`, which x265 refuses as `--colorprim 22` ("Color Primaries
must be unknown, bt709, … smpte-eg-432") and fails the encode over. Neither has a name in the
string tables either, so aomenc and x264 are given nothing and lose nothing. Do not add them.

**av1an's target quality probes never see the `-f` filters.** `Encoder::probe_cmd` composes the
probe's ffmpeg pipe out of nothing but the probing-rate `select`, and the chunk's own source
command carries no filters either, so a resize, a crop or a deinterlace is invisible to the
quantizer search - it settles on the value that hits the target at the *source's* size and that
value is then used on chunks encoded at another. Nothing here can fix that, so the tab says so
whenever a target mode meets a filter chain, naming both sizes when the frame changes size.

**"The filters set on this tab" was the wrong unit for that note, and it is written as "still running
per chunk" instead.** On this tab the two now coincide - with the tone-map pass gone, everything the
tab applies runs per chunk and every one of them is invisible to the search - but the per-chunk
wording is the one that states *why*, and it is the one a pass returning to this tab would need. That
pass is the shape to recognise: its output **was** av1an's input, so the tone map and the geometry
folded in with it were baked in and the probes did see them, which is what the size clause's old
`!frame.GeometryInPass` guard was for. Anything put back in front of av1an has to be taken back out
of this note in the same change. `GetFilteredTargetQualityNote` still takes the source colour, and
that is unrelated to any of it: `Av1an.Run` may already have swapped the file's colour for the one
the *encoder* is told about, so asking "is this run tone mapping" against the file answers no.

**The tone map earns its own clause, because it is the largest of these effects and the only one whose
direction cannot be predicted.** Measured through the app's own chain and the shipped SVT-AV1-HDR
binary on two PQ sources: the probes score the HDR frames **2-3 VMAF points high** on bright content
and **4-7 points low** on dark content - **+5.5 and -9.5 CRF steps** against a VMAF 95 target, where
the resize skew this note has always named is **1.7 to 2.2 steps** on the same harness. The sign
follows which way the roll-off moves the picture's mean (428.6 to 564.8 on the bright fixture, 313.0 to
243.3 on the dark one), so it belongs to the content and **no fixed offset corrects it** - which is why
the clause states both directions rather than warning about one. Re-rendering the dark fixture with the
bright one's gentler roll-off halved the gap without flipping it, so the peak sets the magnitude and
the content sets the sign. The clause used to end by pointing at a GPU machine where the probes would
see the tone map, and does not now: `ForceCpuChain` is this tab's standing policy, so the zscale chain
runs per chunk on every machine there is and there is no such run to point at. A frame rate resample
gets a line too, for the same reason the tone map does: it changes no size, so the size clause never
covered it.

**Nothing in any av1an fixes this rather than describing it, and `--proxy` is the one that looks like
it does.** Read out of the shipped binary (`0.5.2-unstable`, rev `7df934d`, hash-matched to the release
asset): of its 63 options exactly two touch filters, `--ffmpeg` (which reaches the chunk encode alone,
at `context.rs` 570/630/658) and `--vmaf-filter`. `--proxy` substitutes a different input for scene
detection *and* target quality, and unlike `--vmaf-filter` it is symmetric - both the probe encode and
the metric reference read it - so it clears the bar on paper. It is still not adoptable here. It wants
the filtered frames to **exist**, as a file or a `.vpy`, which is a render pass this tab has no room
for and which VapourSynth could not express the zscale tone map or bwdif in anyway; and **on a resume
with a rendered video proxy it goes wholly inert** - `vs_proxy_script` is built only when the proxy is
VapourSynth or the chunk method is a VS one *and* it is not a resume, so the probes go quietly back to
the unfiltered source with no error and no warning, on a tab that supports resume. Two objections that
look plausible are *not* the reason and should not be repeated: av1an **does** enforce the frame count
(`settings.rs::validate` bails with "Input and Proxy do not have the same number of frames!"), and the
"filtered reference against unfiltered probe encode" hazard is unreachable, since av1an refuses the
VapourSynth-scored metrics on non-VS chunk methods before a chunk exists.

Three flags on av1an's docs site - `--probing-speed`, `--probe-slow`, `--min-q`/`--max-q` - are **in no
binary**; `--probe-video-params` and `--qp-range` replaced them, and the site is stale. `--probe-res`
scales what the metric is *computed* at, not what the probe *encodes*, so a 4K probe rescaled to 1080p
for scoring is still a 4K encode. None of this was run - there is no av1an that executes in a web
session, the release binary being a Windows PE - so it is exact-revision source plus the binary's own
strings, which is documentation-grade and labelled as such.

`--vmaf-filter` is not the way out and was actively making it worse. It filters the *reference*
VMAF is scored against while the probe stays unfiltered, so passing this tab's chain compared a
filtered reference with an unfiltered encode: with a resize, a sharp probe against a softened
downscale-and-back-up reference, scoring far under the truth and dragging the quantizer down with
it. Where the chain also changed the aspect ratio - an anamorphic de-squeeze, which runs on its
own with Resize on "No resizing", or a crop, or an exact size that pads - the two feeds came out
different sizes after av1an's own scale to `--vmaf-res` and libvmaf refused them outright
("input width must match"), minutes into a run. Do not put it back.

**The frame the encoder is handed is not the file's own size**, and things built from it have to
say which they mean. `Av1anUi.ResolveFrameAsync` settles the geometry - source, less the crop,
then the resize or the de-squeeze - before the encoder's arguments are built, because the tile
count is a property of the frame being encoded: four tile columns are right for a 4K source and
wrong for the 720p it is being scaled to. It resolves the automatic crop too, which is ten ffmpeg
probes and a line in the log, which is why the answer is carried in an `Av1anFrame` rather than
worked out again wherever it is wanted.

**Quick Convert now holds its resize the same way, and that is what made the rest of its geometry
answerable.** It used to be two free-text boxes handed straight to ffmpeg, and what that cost was never
the typing: nothing downstream could say what frame the encoder would get, so the tile count fell back
on the source's size for any percentage or expression, the black bars refused to run at all against
one, no frame-pixel-limit check was possible, and neither an upscale nor a dropped anamorphic shape had
anywhere to be mentioned. `MiscUtils.GetScaleFilter` also rewrote every `w` in the box to `iw`, so
ffmpeg's own `iw/2` went out as `iiw/2`; it is deleted, and the comment where it sat says why.

The dropdown, the dialog and `ResizeConfig` are shared with the AV1AN tab. **One difference is
deliberate and must stay: with no resize configured, nothing here de-squeezes an anamorphic source.**
ffmpeg carries the aspect flag through to the output, where av1an's encoders are handed bare frames and
cannot - so `ResolveScaledFrame` returns the source's own SAR in that case, and the readout says the
file still plays at its display shape rather than promising dimensions it will not have.

`GetEncodedFrameSize` is therefore exact wherever the crop is. It still does not resolve an *automatic*
crop: that costs ten ffmpeg probes and it is asked once per pass ahead of the filter chain that will run
them again, where the AV1AN tab can put it behind a single resolve pass and this cannot.

The other half of that saving is `FfmpegUtils`' own cache, keyed by the file's path, length and last
write, so asking twice costs one detection - and the tab no longer builds its whole chain a second time
just to ask whether there is one. `GetMapArgs` is *told* whether a filtergraph exists rather than
working it out, which is both cheaper and the only way it can be right: the chain is not a function of
the encoder alone, GIF contributing its entire palette graph through `CodecArgs.ForcedFilters`. Asking
without those is what made GIF impossible to produce at all - the source was mapped directly past a
graph whose output then went nowhere, and ffmpeg refuses that outright.

Verified the way the AV1AN geometry was: 480 chains built by the tab itself - 5 sources including an
anamorphic PAL DVD, a portrait clip and a genuinely odd 641x481, against 2 crops, 4 border targets and
12 resize settings - each run through ffmpeg and the output frame compared against what
`GetEncodedFrameSize` said the encoder would get. No mismatches.

**A crop is four edges, and the rectangle they come to is worked out in one place.** `CropConfig` is
that place, so the dialog's readout, the frame the resize is measured against and the filter that runs
cannot disagree. It enforces two things that used to reach ffmpeg exactly as typed:

The result stays inside the frame. Every box was clamped against the *whole* dimension on its own, so
Left 1000 and Right 1000 on a 1920 frame was something the dialog would let you confirm, and it came
out as `crop=-80:1080:1000:0`. The way in is rarely a typo - the four edges outlive the file they were
set for, and `RunTask` clears each file with `resetSettings: false`, so a batch carries a 140-line
letterbox crop from a 1080p file onto a 480p one. Both tabs now refuse the encode through
`CropConfig.GetProblem`, naming the file and the numbers, rather than letting av1an meet it one chunk
at a time; the dialog holds each *pair* of opposing edges instead of each edge, and shrinks a crop
that arrives too big for the file proportionally, so a symmetric one stays symmetric.

The result is even on both axes. 4:2:0 has one chroma sample per 2x2 block, so an odd width or height
is refused by x264, x265 and SVT-AV1 alike, and an odd offset is silently moved by ffmpeg's own crop
filter - which puts the file a pixel away from what the dialog drew. The dialog steps in twos but a
typed 3 got through. The offset rounds up and the size rounds down, so alignment never re-exposes a
sliver of the bar being removed.

**The mod-2 pad runs after the crop**, on both tabs, and is decided from what the crop leaves rather
than from the source. Before, an odd source with a crop padded to 720x406 and then took an odd
rectangle out of it - odd again, and measured against a frame the pad had already moved. With the
crop's own rounding above, a cropped frame is even before the pad is asked, which leaves the pad doing
what it was written for: an odd source with no crop on it.

**The dropdown's box presets enlarge a source smaller than their target**, so "2160p (4K)" means
3840x2160 for a 1080p file rather than handing the file back unchanged. `ResizePresets.Box` is where
that is set, on the `AllowUpscale` flag a hand-built `ResizeConfig` still defaults to off. What it
costs is said rather than refused: the readout carries the clause, and `Av1anUi.LogResize` repeats it
per file, which is the only place a batch of mixed resolutions shows which files were grown. The
percentage entries take no part in it - they are proportions, and a percentage over 100 was always an
upscale asked for outright.

That combination is what made `GetNote` return two clauses rather than one. A de-squeezed DVD scaled
up to 1080p is being un-squashed *and* enlarged, with neither implying the other, and the de-squeeze
clause sits above the upscale one - so before this the readout stated the shape and said nothing
about the cost. Nothing else in that list pairs up: every other clause answers "what is happening to
the frame", where being enlarged is a price.

**The Borders row pads out to a target aspect ratio, and which bars it needs is not a setting.** A
picture wider than the target gets them along the top and bottom, a narrower one gets them down the
sides, and one already that shape gets none and no filter at all - so a 2.39:1 film and a 4:3 capture
both reach 16:9 off one dropdown entry, which is the whole reason the target is held as a *ratio* and
not as a frame size. `BorderConfig.Compute` is the one place that comparison is made; a target frame
size is already available on the other row, an exact resize with "Letterbox with black bars" scaling
to reach a named WxH. This does not scale at all.

It runs **last of the geometry, after the crop and after the resize**, and that ordering is the point:
a scaler run over a hard black edge rings, so bars added before a scale come out neither black nor
straight-edged. `Av1anFrame.Encoded` is therefore the *padded* frame, which is what the tile count and
the frame-pixel-limit refusal want - a pillarboxed 4:3 capture is 1920 across where the picture in it
is 1440. `Av1anFrame.Scaled` carries the pre-pad size, because three log lines mean "what the scale
produced" rather than "what the encoder gets", and one of them names the aspect ratio the file will
play at.

**The frame is rounded to even and the offset to even, and they are two roundings rather than one.**
The *growth* used to be rounded to a multiple of four, on the reasoning that half of a multiple of
four is even, so a single rounding made the frame even *and* put both offsets on the chroma grid -
where ffmpeg's own pad filter would otherwise relocate an odd offset silently, putting the picture a
pixel off the middle it was centred in. What that misses is that the frame's own size is the thing
being asked for. A 1.85:1 4K film scaled to 1080p is 1920x1038, and 16:9 bars on it want exactly 42
pixels, which is not a multiple of four - so the entry that says "16:9" produced **1920x1082**, two
pixels past the frame it names and a ratio that is no longer the one on the dropdown. That was
reported against a 3840x2076 Blu-ray, which is the ordinary shape of a widescreen film rather than
anything odd: any picture whose gap to the target is 2 mod 4 lands there.

Rounding the frame to the nearest *even* size is always within a pixel of the ratio and exactly on it
wherever the target size is even, which 1080 off a 1920 picture is. The offset is then rounded down to
even on its own, and the pixel that leaves over goes onto the far bar - 20px above and 22px below -
which `BorderPad.FarBar` already reported and which the readout and the encode log already name. A
pixel of asymmetry nobody can see is worth a frame that is the shape it claims. The old odd-frame
repair at the end of `PadAxis` is gone with it, being structural now rather than a fix-up: a size
rounded to even cannot arrive odd.

Nothing is added under four pixels still: two a side is not a bar, and growing a source that is
already the target shape to within a rounding is exactly the surprise a batch must not spring. The
mod-2 pad above the scale is untouched and still needed - a letterbox only grows the height, so an odd
width stays odd.

The frame handed to it is square-pixel almost everywhere, a resize and the de-squeeze that runs in its
place both ending in `setsar=1:1`, but **not quite everywhere**, so the SAR is taken into account
rather than assumed away. Quick Convert with both scale boxes empty is the case: nothing there
un-squeezes a DVD, ffmpeg carries its aspect flag through to the output, and bars measured against the
stored 720x480 would be measured against a shape nobody ever sees. `pad` does not change the SAR, so
nothing here appends a `setsar` of its own.

**Quick Convert refuses rather than guessing, and rather than silently skipping.** `GetBorderProblem`
stops the run and names the setting, the way a crop too big for its file does; `GetFrameSizeProblem`
does the same for a frame past `ResizeConfig.MaxFramePixels`, which this tab could not ask at all while
its resize was free text. Both stand down for a stream-copy codec, which builds no chain - and where
the dropdowns are disabled, so a target left over from another codec is one the user cannot reach.
`GetCropProblem` stands down there too now; it used to cancel the run over a crop that would never have
been applied.

**The bars are measured against the frame the mod-2 pad leaves, not the one that went into it.** An odd
source with borders on had them worked out from the odd size, so the pad they asked for came to an odd
frame no encoder here accepts. `GetCroppedSourceSize` rounds up the same way the chain does, which is
what keeps the readout, the check and the filters measuring one frame; a crop's own result is already
even, so that step only ever reaches an odd source with no crop on it.

An **automatic** crop is still left alone everywhere, costing ten probes - so `GetEncodedFrameSize`
abstains outright for one rather than naming a size that will not be the one.

The geometry was checked by running it rather than by reading it. 141 pad filters rendered through
ffmpeg across 32 source shapes and 5 targets, with the output size, the evenness, the centring and the
blackness of the bars read back out of the frame; and again for the rounding above - 770 chains built
by the real `ResizeConfig` and `BorderConfig`, 14 source shapes including two anamorphic DVDs and an
odd 641x481, against 11 resize settings and all 5 targets, each rendered over a white source so that
`cropdetect` reads the picture's own rectangle back out of the frame and can be compared with the `X`,
`Y` and `Input` the code predicted, alongside the output size from ffprobe and the distance from the
ratio the target names. No mismatches. And 1152 chains built by the tab itself - 8 sources including
two anamorphic DVDs and a genuinely odd 641x481, against crops, resize presets, exact pad/stretch,
anamorphic correction off, a frame-rate resample, and every scale-box form - each run through ffmpeg
and compared against the size the tab said the encoder would get. No mismatches.

The odd-frame case is the one that needs care in the harness rather than in the app, and it bites at
both ends. x264 silently produces 640x480 for a 641x481 source, so the output has to be FFV1 for the
case to exist at all; and 4:2:0 cannot carry an odd frame *going in* either, swscale rounding
641x481 down to 640x480 before any filter in the chain runs - so the source has to be fed as 4:4:4,
and a run that forgets reads as a pad that quietly lost two pixels.

**Nothing on the AV1AN Video tab is saved.** The encoder, the container, the quality mode and its
value, the preset, the colour format, grain synthesis, the frame rate, the resize, the crop, the trim,
the borders and the deinterlace all start each session at their defaults - SVT-AV1 into MKV, then whatever
selecting that encoder writes into the rest - and `LoadAv1anEncodeSettings` restores none of them. It
is down to the Audio & Tracks rows and the filter grid - not the Advanced
tab's argument grid, which stopped being saved when Quick Convert's did; `LoadConfigAv1an`
keeps the audio codec and the Av1an Options tab. Those settings describe a job rather than a
preference, and every way they go wrong is expensive and quiet: a resize left on 720p halves a 4K
encode nobody meant to shrink, a CRF picked for a grainy film is the wrong number for line art, a
grain table from another source describes grain this one does not have. Reset On New File already
made that argument for Trim, Crop and Deinterlace; this carries it to the whole tab and to the
boundary that is even easier to lose track of, which is a session that ended days ago.

The encoder had to move rather than merely stop being restored, and this is the part to be careful
with: what made SVT-AV1 the default was `Config`'s default *for the saved value*, so dropping the
restore on its own would have opened every session on the first entry of the enum, which is aomenc -
and dragged the whole tab with it, since the quality scale, the preset list, the colour formats and
the Advanced tab's rows are all rebuilt per encoder. `Av1anUi.Init` names SVT-AV1 where the box is
filled instead, and that is now the only statement anywhere of what the tab opens as.

Not writing them matters as much as not reading them: a value saved and never restored is one the
next person to touch that method will restore, reasonably enough, and the setting then comes back
from whatever session last happened to write it. Keys from before this are still sitting in existing
config files - do not wire one back up on the strength of finding it there.

**SVT-AV1 encodes with two workers fewer than the other encoders av1an drives.** It loads a core far
harder than they do, so the count that keeps aomenc or x265 busy oversubscribes the machine on this
one and every worker then runs slower than it would have with the machine to itself - which is only
visible while an encode is running, since the box was set once at first launch and looked right ever
after. `Av1anUi.ApplyWorkerCount` writes the reduced number into the Workers box on selecting SVT-AV1
and puts the full number back on selecting anything else, so what is on screen is what runs -
`Av1an.Run` reads that box and nothing else, and so does the progress bar's ETA, which parses `-w`
back off the command.

The number that gets **saved is the baseline** - `Av1anUi.WorkerBaseline`, the count for an encoder
that is not SVT-AV1 - and that is the part to be careful with. The tab opens on SVT-AV1, so the box
is almost always showing the reduced count; storing the box as it stands would have the next session
take that for the baseline and reduce it again, two workers per launch until it hit the floor. That
is why this one control does not go through `ConfigParser.SaveGuiElement` like every other row of
`SaveConfigAv1an`.

A hand edit states the count for the encoder in front of the user, so `WorkerCountEdited` adds the
penalty back to get the baseline: type 4 under SVT-AV1 and 4 is what comes back on re-selecting it
and next session, with the other encoders on 6. It adds the *penalty* rather than however much of it
was applied, which is the difference at the floor - a baseline of 2 shows 1 because the box stops
there, and the next number typed into it can afford both workers. That floor is not hypothetical:
the Workers half of `Av1an.GetDefaultThreadPlan` bottoms out at 2, so anything up to a four-core
machine meets it on its very first launch.

`writingWorkerCount` is what keeps the two apart - the box's `ValueChanged` fires for this class's
own writes too, and without the guard every encoder switch would read its own write as a hand edit
and walk the baseline up by two. `lastWorkerCodec` is the other half of the same problem, holding the
log line back for the startup call and for a step between two encoders that read the same count.

**The resize dialog's anamorphic switch is warned about rather than overridden.** Off, the targets
measure the stored pixels and nothing bakes the display shape in - and there is nowhere else for it
to live, since av1an hands its encoders bare frames and a chain ending in `setsar=1:1` drops the
flag anyway. So a 16:9 DVD comes out playing as 3:2. That is a defensible thing to ask for, on an
archival re-encode that will be flagged later, so it is not taken away; what was wrong was saying
nothing. `ResizeConfig.GetNote` now leads with it - ahead of every other clause, because it is the
only one describing a file whose *shape* is wrong rather than a size nobody asked for - and the
encode logs it too. The case that made this worth doing is a target the source already meets: the
resize is a no-op, no filter runs at all, and the readout used to answer "already this size, so it
is left alone", which is true of the pixels and wrong about the picture.

That clause is no longer quite first. **A frame ffmpeg will not scale to at all comes before it**,
because a run that cannot start has no shape to be wrong about. `ResizeConfig.MaxFramePixels` is
where the line sits and `ExceedsFrameLimit` is what asks; the note leads with it and `Av1an.Run`
refuses the encode rather than letting av1an discover it one chunk at a time as "Picture size WxH
is invalid", which names neither the resize that asked for it nor the box to change. Two settings
reach it from the dialog without going anywhere strange: both target boxes at their own maximum is
16384x16384, and 800% - also that box's maximum - of a 4K source is 30720x17280.

The limit is measured, and ffmpeg's own boundary is not a clean one: 4096x64000 is refused at
262.1 MP while 16384x16128 is accepted at 264.2 MP, so where it falls depends on the frame's shape
as well as its area. 260 MP sits under the whole overlap. Nothing legitimate is near it - 8K UHD is
33 MP, 16K is 133, and SVT-AV1 stops at 16384x8704 - so being under the line is not a promise the
size will encode, only that ffmpeg will produce it. The encoders have their own much lower ceilings
and say so clearly when they are hit; this one is caught here because it does not.

The geometry either side of that was checked by running it rather than by reading it: 31 source
shapes, 8 of them anamorphic, against 36 resize settings, with each case's predicted size compared
against what ffmpeg actually made of the same filter chain. 1049 chains, no mismatches - and the
same again through `ResolveFrameAsync` on real files carrying real SAR flags, which is the path
that also covers the de-squeeze with no resize configured and the mod-2 pad on an odd source.

**av1an fails a chunk whose frame count is not the one it expected**, retries it to `--max-tries`,
then shuts the worker down and with it the run. A frame rate change is that mismatch by
construction and on every chunk - writing a different number of frames than came in is the whole
point of the filter - so the Frame Rate box killed any av1an encode it was used on, hours in, each
doomed chunk having been encoded four times first. `--ignore-frame-mismatch` is av1an's own answer:
its concat step reads the flag as "an FPS changing filter might have been applied" and stops
forcing the source's rate onto the output, which is the other half of what a resampled encode
needs. It goes out whenever `Av1anFrame.ResamplesFrameRate`, behind the usual help-text check.

**Whether the box asks for a different rate at all is decided with a tolerance, and must stay that
way.** `MiscUtils.IsSameFrameRate` calls two rates the same within 0.01%, because the app shows a rate
two ways - the Track List reads `24000/1001 (~23.976 FPS)` - and typing the readable one back was an
exact-comparison mismatch that built a filter. The retiming that produced was nothing, one frame in a
million; what it cost was everything a non-empty chain costs on this tab, which is
`--ignore-frame-mismatch`, the pixel format conversion coming off VapourSynth, and every
target-quality probe measuring an unfiltered source. The same trap sat one level along, where `59.94`
did not match the 60000/1001 a bobbed 29.97i source arrives at. 0.01% is ten times finer than the gap
it must never close - a rate and its NTSC form are 0.1% apart, 24 against 23.976 and 30 against 29.97
- so pulldown rates stay distinct while a rounded decimal of the same rate does not.

Both tabs also log the resample per file, naming the source rate in both forms and saying so when that
source rate is the doubled one a bob produces. That box has no readout of its own - the resize and the
deinterlace both have one - so before this a rate left over from another file, or typed with a digit
out of place, reached the end of an encode without ever being mentioned. Quick Convert's second pass
builds the same chain as the first and is asked for it with `quiet: true`, so this and the de-squeeze
line land in the log once rather than twice.

**The AV1AN progress bar is measured from av1an's temp folder, never from its output.** `scenes.json`
gives the chunk count and the video's frame count, `done.json` what has finished, both in the folder this
app names itself with `--temp` - so `Av1anOutputHandler` parses no av1an log line at all, and that is the
point.

**What it counts is frames, and counting chunks instead is what made the ETA fiction.** Chunks are not
the same size - `-x` caps them and nothing makes them uniform - and av1an's default chunk order is
*longest first*, so the chunks that finish are the big ones and a chunk count understates the progress
for most of a run. Measured against a real encode: 36 of 304 chunks done is 12% of the queue and 25% of
the video, which is what av1an's own bar said, and the remaining time extrapolated from the chunk count
came to 37 minutes against av1an's 12. Both numbers reproduce exactly from `done.json`'s own contents,
which is how that was settled rather than by argument.

`done.json` is `{"frames": 34048, "done": {"00000": {"frames": 240, "size_bytes": …}, …}}` - the total,
then one entry per finished chunk carrying that chunk's frames. Summing those is what av1an does for its
own bitrate and size estimates (`update_progress_bar_estimates`), so it is the same arithmetic and not a
reimplementation of it. The total is read from `scenes.json` where possible, that file existing as soon
as scene detection is done where `done.json` says nothing until the first chunk lands; `done.json`'s own
`frames` is the fallback, and a total of 0 - an av1an too old to write one - falls back to the chunk
count rather than reporting nothing.

It reads a little low and the amount is bounded. av1an's bar also counts the part-encoded frames of the
chunks *in flight*, which it learns from each encoder's stderr as it goes and never writes down, so
nothing in the temp folder can see them. That is one part-chunk per worker at any moment: a roughly
constant offset rather than a growing one, worth a few percent on the readout and very little on the
ETA. Do not go back to parsing av1an's stderr to close it - the rot described below is what that costs.

The `<` in front of the ETA went with the chunk count and belongs only to it. Longest-first means
seconds-per-chunk only ever falls, so a chunk-based estimate is a ceiling; a frame rate carries no such
bias, so the frame-based estimate is printed as the estimate it is.

Everything it used to read had rotted away underneath it, silently, one release at a time.
`--log-file` stopped defaulting to `{temp}/log.log` after 0.4.x and now defaults to `./logs/av1an.log`
with the date appended, so the file the loop waited for was never created and it sat in that wait for
the entire encode: no percentage, no chunk count, no "Scene detection…", nothing. Behind that sat two
more, either of which would have done the same job on its own - "SC: Now at " and "Done: " are lines
no av1an since 0.4.0 emits, a finished chunk having been `finished chunk 00001: …` for years. None of
it was visible from here, because a progress bar that never moves looks exactly like an encode that
has not got going yet.

`scenes.json` holds **two** arrays, and the one to count is `split_scenes`. `scenes` is what detection
found; `split_scenes` is that list after `-x` subdivides the long ones, and the chunk queue is built
from it - identical when `-x` changes nothing, longer when it does, and av1an writes both. So counting
every `"start_frame"` in the file, which is what this did, came to `scenes + split_scenes`: double the
real count at best. `scenes` is still read as a fallback for a file written before av1an grew the
second array, which a resume is where you meet.

Checking any of this by reading av1an's source is checking the wrong av1an. `bundle-tools.sh` takes
the newest release asset that matches, and av1an's own tagged releases (v0.5.1, v0.5.2) carry source
archives only - the binary comes from the rolling `latest` prerelease, which is nowhere near either
tag. Download that asset and read the strings out of it; the help text names the log default and
`finished chunk` is right there beside it.

**Reading av1an's temp folder is safe; writing into it is not, and the encode-settings attachment did
exactly that for as long as it existed.** The progress bar above is the good case - it only ever reads.
The attachment step was the bad one: a fire-and-forget `Task.Run` started before `RunAv1an` that waited
for `done.json` to say `audio_done`, slept 500 ms, then **deleted and replaced `audio.mkv`** inside the
folder av1an owns. It raced av1an's own concat step, and the event it waited for is not the event that
bounds it - what bounds it is av1an *consuming* audio.mkv, which nothing outside av1an can observe.

**Measured, the window is the video encode's own duration.** Polling av1an's temp folder from the shell
on a 3 s fixture, `audio_done` to audio.mkv being taken: **246 ms for SVT-AV1 preset 12, 1450 ms for VP9,
2005 ms for aomenc**; on a 30 s fixture, 1163 / 13067 / 18219 ms. Audio is extracted in the first ~350 ms
whatever the encoder, so the whole of the rest is the video. The step needed its 500 ms sleep plus up to
a 500 ms poll interval plus an mkvmerge run, so it lost outright on a short SVT-AV1 encode and was
marginal on the others.

**So it looked codec-specific and was not - that is the control worth keeping.** The report behind this
was four runs where AOM (2.87 s) and VP9 (2.68 s) carried the attachment and SVT-AV1 (2.37 s) did not,
which reads as "SVT-AV1 drops it". Varying only the speed preset settles it: SVT-AV1 goes from **250 ms
at preset 12 to 1568 ms at preset 2**, and aomenc from **1958 ms at cpu-used 9 to 12104 ms at cpu-used
2** - a 6x swing on each, on one codec, with nothing else changed. SVT-AV1 was simply the fastest
configuration in the sample.

**The missing attachment was the mild half.** `File.Delete` then `File.Move` on a file av1an may open at
any moment leaves a window in which audio.mkv does not exist, and av1an's mux is the thing that opens
it. The comment that used to sit in that method guarded the same loss from *one* cause - no mkvmerge to
call, which is every Linux and macOS build - without noticing the race could produce it too, and it had
the stakes right: losing that text file is not worth a second's thought, losing the audio is the encode.

`Av1an.AttachEncodeSettings` does it to the **finished output** instead, awaited after `succeeded` and
before `SetWorking(false)`, and `IsAudioDone`/`IsAv1anRunning` are deleted rather than left behind -
`audio_done` reads like a usable signal for "av1an has finished with audio.mkv", which is precisely the
belief that made the old step wrong. It costs one mkvmerge remux of the output - a copy, no re-encode -
and needs the output's size in free space while it runs.

**What the run pays is the whole method rather than the remux, and this file first said otherwise.** It
quoted 0.23 s for 178 MB, which is mkvmerge alone on a warm file and understates what the encode waits
for. Measured end to end through the real method: **371-442 ms** on outputs of 183 KB to 4.5 MB and
**535-552 ms** on 187 MB, of which mkvmerge is ~300 ms and the two uncached `GetStreamCount` probes are
most of the remainder at 76-89 ms each. Cross-checked from outside against the same encode into MP4,
where the method returns on the extension - 1781 ms against 2137-2260 ms into MKV, so +356 to +479 ms.
Still negligible beside the encode it follows; the point is that the probes are a third of it and they
are what make the result judgeable, so quoting the mux alone prices the wrong thing.

Two details there are measured rather than obvious, and both are traps in the other direction.
**`--disable-track-statistics-tags` must not be passed** - the opposite of the call Quick Convert's
containerise step makes - because av1an muxes with mkvmerge itself, so its output *already* carries BPS,
DURATION, NUMBER_OF_FRAMES, NUMBER_OF_BYTES and the `_STATISTICS_` pair, and the flag would strip tags
the encode put there. (The remux does regenerate `_STATISTICS_WRITING_DATE_UTC` - measured 05:55:31 to
05:57:13 across an 11 s gap - which is honest, the file being genuinely rewritten then.) And **the result
is judged by stream count, not by size**: a remux can come out *smaller* than its input despite gaining
an attachment - measured, 186,437,450 bytes against 186,443,976, mkvmerge rewriting the seek head more
compactly than ffmpeg had - so a "not smaller than the original" check would reject good output. It is
judged by artifact rather than by mkvmerge's exit code for the reason recorded elsewhere: that is 1 for
warnings over a file it has written perfectly well.

**A missing mkvmerge is now a deterministic skip rather than a timing-dependent one**, logged at debug
level: MKVToolNix is bundled for win-x64 alone, so that is every Linux and macOS build, every time, and
a visible line per encode about a text file nobody asked for is noise. A mux that *fails* with mkvmerge
present is logged visibly, that one being unexpected.

Verified by running it, through the real `MainWindow` and `RunTask.Start`: 15 encodes, 383 checks, no
failures. Three repeats each of SVT-AV1 at preset 12, aomenc and vpxenc on a 3 s fixture - the shape
that used to lose - all carrying the attachment, plus the 30 s fixture at presets 12 and 4, an MP4
output that correctly attaches nothing and writes no text file at all, and a run in the visible-console
mode that is the shipped Windows default. `mkvextract` read the attachment back out of every MKV and
its text was machine-compared against the `-v "…"` payload the app logged for that run: 13 of 13 exact.

**The control is a real av1an run rather than an inference**: for four of them the logged command was
replayed with only `--temp`, `--log-file` and `-o` repointed, so what av1an writes with no attach step
at all could be compared with the app's output. Two streams against three; video and audio packet
payloads md5-identical, and the packet pts/dts/duration/size md5-identical with them, so the remux
moves nothing; the size delta is the attachment and nothing else (248014 → 248301, 3963733 → 3964020).
The statistics tags come out **byte-identical** between control and output on all four, with only
`_STATISTICS_WRITING_DATE_UTC` differing - which is the direct evidence that
`--disable-track-statistics-tags` is not being passed, rather than an argument that it should not be.

A `FileSystemWatcher` over av1an's temp root across all 13 instrumented runs saw **zero `.tmp` events**
and exactly one create and one delete of `audio.mkv` per run, the delete landing 3-5 ms *after* the
attach step had finished with the output - that being `HandleTempFolder` removing the directory. So
nothing writes into av1an's temp folder any more, measured rather than read.

**Four branches are unexercised and are named rather than implied**: the `succeeded == false` gate (no
av1an failure was staged), the `IsToolAvailable` stand-down (mkvmerge is present on the machines this
runs on), the `after != before + 1` failure message and its cleanup (every mux came out right), and a
resume. No WebM output either.

**Two faults were still in that method after all of the above, and both are about the *output's own
name* rather than about av1an.** The verification behind this section is extensive and never touched
either, because every fixture it used was called `out.mkv` and every run was on Windows.

**The three paths on the mkvmerge line were `.Wrap()`, which is plain double quotes on every
platform** - and `AvProcess.RunMkvMerge` goes through `Shell.BuildArguments` and a shell, so off
Windows this is the exact case CLAUDE.md's Quick Convert quoting note is about. Two of the three are
the user's own: the output is whatever they typed, and the session folder sits under it on a portable
install. Measured through Git Bash's `sh`, the same `sh -c` `BuildArguments` composes: `"…/My $HOME
`id` clip.mkv"` reaches the tool with the home directory substituted for `$HOME` and the whole of
`id`'s output - uid, gid and groups - spliced in where the backticks were, so `$HOME` expanded and
`id` **executed**, where the single-quoted form arrives whole and opens. So off Windows with a
user-installed mkvmerge, an output named like that lost the
attachment - reported as "0 of the 3 expected streams came out of the mux", which names the mux and
not the name - and ran whatever was between the backticks. It is `Shell.WrapArg` now, matching
`QuickConvert.BuildDirectCommand`'s own mkvmerge call. **The whole fault is invisible from Windows**:
the two encodings are byte-identical there, checked on the real methods, so this is a fix nothing on
either development machine can exercise and the sh measurement above is what stands behind it.

**And the replace step was `File.Delete(outPath)` then `File.Move(tmpOutPath, outPath)` inside a catch
that deletes `tmpOutPath`** - so a move that threw after the delete succeeded left the encode nowhere
and the handler removed the only remaining copy. `AddSubtitlesToMp4` had the same two lines and the
same catch. Both go through `ReplaceWithRewritten` (one `File.Move` with `overwrite`, a same-volume
rename, since the replacement is always a sibling) and `DiscardRewritten` (deletes only while the
original is still there); the rule and the two instances of the shape left elsewhere in the tree are
under CLAUDE.md's `Replacing a finished output`.

Verified by running it, through the real private methods out of the built assembly and the real
bundled tools (`mkvmerge v93.0 'Goblu'`, ffmpeg `N-126264-g007cd1fd43-20260825`, toolchain 2.8.79):
26 checks, no failures. An AV1+Opus MKV called ``My $HOME `id` clip.mkv`` gains exactly one attachment
stream (2 -> 3, 20886 -> 26398 bytes), `mkvextract` reads the text back as the encoder and the `-v
"…"` payload, no `.attach` file is left, and the logged mkvmerge command carries the full name; an
ordinary `plain.mkv` is the control. **The discriminating failure case is the move failing while the
original is deletable** - the replacement held open without `FileShare.Delete` - and on identical
fixtures the shipped helper keeps the 20886-byte encode where the old two lines leave it **gone**.
The old cleanup deletes the only copy where `DiscardRewritten` keeps it. Through the whole method,
with the output itself held open, both `AttachEncodeSettings` and `AddSubtitlesToMp4` come back with
the encode byte-identical and the replacement cleaned up. **Not exercised**: the sh branch (unreachable
on Windows - hence the standalone `sh` measurement), and the `after != before + 1` branch, which is
still the same gap as above.

**av1an's own log is put in the temp folder rather than left to that default.**
`Av1an.GetLogFileArgs` names it, beside `--temp` and for the same reasons: the folder only exists once
the run has one, and both flags sit ahead of the `-i` that `SaveJson` starts saving from, so a resume
sets its own instead of writing into the previous attempt's. Left to av1an, the log went to
`./logs/av1an.log` *relative to the working directory*, which is `bin/av1an` - so every encode dropped
a dated file beside the binary, in a folder nothing here knew about and nothing ever cleared. In the
temp folder it lives exactly as long as the run's other state: `HandleTempFolder` keeps that folder
when the encode failed, which is when the log is worth reading.

**A failed run reads it, so the name `av1an.log` is load-bearing now.** With the visible console -
the Windows default (`Av1anCmdVisible`) - the app wires no output redirection at all, and the launch
script's window closes itself five seconds after a failure: an encode that died mid-run was reported
as "exit code 1" with the actual error gone unread, which is exactly how a real one came back as an
undiagnosable bug report. `Av1anOutputHandler.ReadFailureTail` reads the log's tail after a nonzero
exit (from the first WARN/ERROR marker in the last stretch, so a `FRAME MISMATCH` body keeps its
"Encoder failed" header), quotes it in the failure message with the log's full path, and runs the
lines through `NoteChunkFailure` so the out-of-memory explanation fires in console mode too. An
av1an old enough to append its own `.log` to the value (0.4.x and earlier did) just makes the read
come back empty, which costs the quote and nothing else.

**The launch script puts `bin/av1an` on its own PATH as well as `CD`ing into it, because cmd only
resolves a bare `av1an` from the current directory while `NoDefaultCurrentDirectoryInExePath` is
unset.** That is a documented Windows setting - hardening guides recommend it, and a parent process
can hand it down; Claude Code's own shell tools do, which is how it was met. With it set, every
visible-console encode died at startup with `'av1an' is not recognized` and exit code 9009 (cmd's
"command not found"), while the piped mode, which launches av1an by full path, ran fine - and the
`--scenes` retry fired for it, correctly, as an unrelated startup failure that fails the same way
twice. Reproduced by running the script with the variable set and unset, from three different
`bin/av1an` folders; fixed by adding the folder to the script's PATH list, and re-run under the
variable to a finished encode. The same shape is worth remembering for any other script that names
a bundled tool bare after a `cd`.

**Nothing under av1an's Scene Detection heading goes out for Split Method "None".** It is `-x` and
nothing else there, so `--sc-downscale-height` named a resolution for a pass that never ran.
`Av1anUi.SceneDetectionEnabled` is the one statement of which entry is which, and
`Av1an.GetScDownscaleHeightArg` also drops the flag where the height works out to 0, since 0 is not
inert: av1an skips the downscale only when the height it is given is *above* the source's, so a zero
reaches ffmpeg as `scale=-2:'min(0,ih)'` and is refused.

**Every downscale tier from 1080 up lands on 720 now, and the height is rounded down to even.** The
table in `GetScDownscaleHeightFor` used to send 900 for a 2160 source and 1440 for 4320, so the two
sources whose detection pass is slowest were the only ones analyzed above 720p - and detection still
decodes at the source's own size whatever this flag says, so the analysis height is the one part of
its cost the flag can lower. The evening matters on its own: av1an hands the number to a scale filter
feeding 4:2:0 frames, and the 900 tier's multiplier came out at 637, an odd height ffmpeg refuses.

**Scene detection is the one phase of an av1an run the workers cannot help with - it is what creates
the chunks they work on - so av1an runs it first, alone, one decode pipeline over every frame while
the rest of the machine idles.** On a 4K60 file that pass ran at ~35 fps, about as long as the encode
itself on a fast preset. `Media/Av1anSceneDetect` is the answer: frame-exact slices of the input as
VapourSynth scripts (`LWLibavSource` plus `[a:b]`), one `av1an --sc-only --scenes <file>` per slice
in parallel, the lists concatenated with their frame numbers offset, and the encode handed the merged
file via `--scenes`, which av1an loads instead of detecting when it already exists. A scene never
spans a slice, so the merge is arithmetic; each boundary starts a scene whether or not the picture
cut there, which costs one extra chunk boundary - the same thing `-x` does to a long scene. The
slices carry the encode's own `--sc-downscale-height` and `-x` strings so the `split_scenes`
subdivision they write is the one the encode expects - subdivision is per-scene, so merging the
subdivided lists equals subdividing the merged list, but only while both commands agree on `-x`.
That is why `Av1an.Run` snapshots both strings into locals and splices the same values into both
commands rather than reading the boxes twice.

**Nothing runs between the detection and the encode any more, so the list is detected on the very
file av1an opens.** There used to be an overlap here: the tone-map and grain passes changed pixels,
never the frame count or order, so a list detected on their *input* indexed their output frame for
frame, and detection ran alongside them - the two phases the workers cannot help with hiding behind
each other. Those passes are gone (no intermediate pass on this tab may be an encode - see the tone
mapping section), and the overlap scaffolding in `Run` went with them: `SettleSceneDetectionAsync`,
the early-start branch, and the duration tripwire's call site. `Av1anSceneDetect.DurationsMatchAsync`
itself stays with the slices, uncalled - it is the tripwire any returning render step needs, a
header-cost duration comparison whose doc says what it catches - and the slices, the merge and the
gates are untouched. What must survive any such return is the ordering rule the overlap lived by:
a bob writes one frame per field, which renumbers everything behind it, so nothing that changes
frame count or order may sit between the file the detection read and the file av1an chunks.

**The LSMASH gate is load-bearing, and physical cuts are not an alternative.** The scene list's
frame numbers must be the ones the encode's chunking counts, and a different indexer can count
differently - so the slice scripts open the source through lsmash and nothing else (none of the
ffms2/bestsource fallbacks Qtgmc's scripts carry), and `Run` only engages the feature when the
chunk method is LSMASH. The `vspipe --info` call that supplies the frame count also builds the
index, once, into the session's shared vsindex - which is what makes launching the slices
concurrently safe, since they read a warm cache instead of racing to write one. The main run then
indexes the file a second time regardless ("Generating VapourSynth cache file"), because av1an
writes its own load script with its own cache path in its own temp folder and nothing here can
point it at ours - a real cost, tens of seconds on a big file, and small against the minutes the
parallel pass saves. Cutting the video
into real files instead would poison the offsets: a stream-copy cut ends two frames late on any
B-frame source (see the Cut utility's note), and an offset wrong by two is wrong for every scene
after it.

**Everything about it is opportunistic, and the safety is two layers deep.** Any missing piece -
flags absent from av1an's help, no vspipe, a source under a couple of thousand frames, fewer than
two detection pipelines' worth of cores, a slice run failing, a merged list that does not tile
`[0, frames)` exactly - abandons with a log line, and the encode runs with av1an's own in-run
detection exactly as before. And because the loading side could not be verified here - there is no
av1an binary in a web session; that an existing `--scenes` file skips detection is its documented
behaviour, and documented is not measured - the handed-over list is revocable: an av1an that exits
nonzero without one finished chunk, on a command that carried `--scenes`, is retried once without
it (`AnyChunkEncoded` is the line between "refused at startup" and "died mid-work"). That caps what
a wrong assumption can cost at one failed startup. The merge itself *was* verified by running it,
through the real methods out of the built assembly over av1an-shaped scene files: offsets, exact
tiling, unknown top-level fields surviving via the template, and the abandon paths - a slice
reporting a length other than its cut, a gap in a list, a scene without its frame fields, files
without `split_scenes` (merged without inventing one), and ranges that read as inclusive. Of the
binary's half, `--sc-only` over `.vpy` slices is field-confirmed - see the constants note below -
and the load-skips-detection behaviour is now measured too, on the shipped av1an (0.5.2-unstable,
rev 7df934d) through the real `MainWindow`: a 2700-frame 720p fixture, 2 slices, 5 scenes in 3 s,
and av1an went from "Generating VapourSynth cache file" straight to `Queue 11 Workers 6` with no
detection pass of its own - the chunk queue was up 4 s after launch (the lwi index being the whole
of that), the first chunk finished at 12 s, the run in 37 s, and the 11 keyframes in the output
include the slice boundary at frame 1350. The retry stays as
insurance against a different binary, not because this one is in doubt.

The merged list lives at `{tempDir}.scenes.json` - `Av1anUi.GetScenesFilePath` - beside the temp
folder like every prepared input and for the same reasons: av1an empties its temp at startup, and a
replayed resume names the path in its saved arguments, so it must survive a failed run.
`GetPreparedInputs` knows the suffix, so it is cleaned up with the rest. A resume never re-runs the
pre-pass: replaying saved arguments carries the kept sidecar, and a resume with new settings may
have changed the trim - a kept list would then describe frames that are no longer the ones being
encoded - so that path sends no `--scenes` at all and av1an falls back on its own temp state.
`Av1anOutputHandler.ReadScenesFile` reads the sidecar when the temp folder holds no scenes.json,
because whether av1an still writes its own copy there when `--scenes` is given is that binary's
business, and without a total the progress bar would sit on "Scene detection..." for the whole
encode. **The slice count is the Detection Slices box on the Av1an Options tab**, saved with its
neighbours; 1 is the off switch, and av1an then detects in-run. Its first-run default is
machine-derived the way the worker plan's is - `Av1anSceneDetect.DefaultSliceCount`, half the
logical cores clamped to 1-8, consulted by `Config`'s default table and written once, with no
literal in `Config` and the box's XAML `Value` a designer placeholder exactly like the Workers
box's. The per-pipeline booking began at four cores, a guess, and the first field report halved
it: a 4K60 file split four ways left its machine at about 25% CPU, so a detection pipeline keeps
one to two cores busy - nearer serial per slice than the guess assumed - which is why the default
is cores over two. Eight caps only the default; the box goes to 16 by hand, and a hand-set count
is respected up to the floor of 1200 frames per slice (`ResolveSliceCount`), which reduces with a
visible log line naming both numbers - the number on the tab is the user's, so a reduction is not
quiet. Machines under four cores default to 1, the same stand-down they had before the box
existed. That report also settled the launch half of what could not be run
here: av1an accepted the `.vpy` slices and ran `--sc-only` over them in parallel. The load half -
the encode actually skipping its own pass - was measured afterwards on a local machine (see the
paragraph above); the retry stays as insurance rather than as an open question.

**The Concat Method dropdown offers what the container box can actually produce, which is two
entries.** av1an has a third, `ivf`, and it was on that list without ever being able to run: IVF is a
bare video stream - no audio, no subtitles, and only VP8, VP9 or AV1 - while the container box offers
MKV, WebM and MP4, so every pairing ended somewhere. MKV and WebM had `Av1an.Run` cancel the encode
and name another method to pick; MP4 was the worse half, because `GetConcatMethodArgs` answers
`-c ffmpeg` for it *before* it reads the dropdown at all, so IVF was quietly swapped out and nothing
said so. Removed on the same grounds DGDecNV is absent from the chunk methods - an option that always
failed. It was the last entry, so no saved index moved, and a config still carrying its 2 falls out of
`LoadComboxIndex`'s range and lands on MKVMerge.

That MP4 override says so now, the way the H.265 one already did: a setting picked and then overruled
is one the log should name. And `GetConcatMethodName` falls back to the default for a box with nothing
selected, where it used to hand back "" and put a bare `-c` on the command line - the chunk method and
the chunk order both floor their index, and this was the one that did not.

The other two settings on that tab were checked at the same time and are correct, which is worth
recording so it is not re-derived: every entry of both was rendered headless and its argument compared
against av1an's own `ChunkMethod` and `ChunkOrdering` names (the `strum` serializations in
`av1an-core/src/lib.rs`, not the labels), and the saved index was confirmed to survive a restart. The
chunk method box is filled from the enum, so its index *is* the value; both are carried into a resume,
since all three sit after the `-i` that `SaveJson` starts saving from.

