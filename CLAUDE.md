# Nmkoder

Media encoding/muxing toolkit. Avalonia UI on .NET 10.

Build with `dotnet build Nmkoder/Nmkoder.csproj`. The SessionStart hook in
`.claude/hooks/` installs the SDK and restores packages, so this works from the
first prompt of a web session.

The project multi-targets, but only on Windows: `net10.0` everywhere, plus
`net10.0-windows10.0.19041.0` when the *host* is Windows. That second framework
is the one carrying the Windows App SDK, and its build runs MSIX tooling
(`MakePri.exe` and friends) - Windows binaries, so a Linux or macOS host cannot
evaluate that TFM at all, not even far enough to compile. Hence the condition on
`TargetFrameworks`, which keeps the command above working everywhere.

The practical consequence for a web session: **nothing under `#if WINDOWS` is
compiled here, so nothing checks it.** To compile-check that code, build a
throwaway project that targets `net10.0-windows10.0.19041.0` with
`EnableWindowsTargeting=true`, `<Compile Include>`s just the files in question
alongside stubs for what they touch, and references
`Microsoft.WindowsAppSDK` with `IncludeAssets="compile"
ExcludeAssets="build;buildTransitive;native;runtime;analyzers"` - excluding the
build assets is what skips the MSIX targets that cannot run. That checks the
code; the *publish* can only be proven by the release workflow's win-x64 job.

## UI conventions

The UI is code-behind, not MVVM. A window is an `.axaml` in `Nmkoder/Views` plus
a partial class holding the logic directly: controls carry `x:Name`, the XAML
wires handlers by name (`Click`, `ValueChanged`, `SelectionChanged`), and the
handler reads and writes those controls. `CropWindow` is the reference shape.

Change handlers are guarded by a load flag - `_ready` in the dialogs,
`_initialized` across the `MainWindow` partials - set false while controls are
populated and true afterwards, because assigning a value (or a range, which
coerces the value) fires the same handlers that would otherwise run against
half-loaded state. Every handler that touches shared state bails out on it.

There is no view model layer, no `CommunityToolkit.Mvvm`, no ReactiveUI.
`{Binding}` appears only inside `DataTemplate`s, resolving against the list item
objects in `Nmkoder/Data/Ui`, and `AvaloniaUseCompiledBindingsByDefault` is
`false` in the csproj - which is what lets those templates, and element
references like `{Binding #SomeControl.Value}`, work with no `x:DataType`
anywhere. Shared styles are inline `Style Selector` rules in `App.axaml` keyed
off style classes (`field`, `dim`, `hint`, `h`, `card`, `panel`, `num`, `icon`,
`accent`, `danger`, `subtle`, `log`, `mono`); a control type used in more than
one window gets a base rule there so its metrics line up.

## The palette

`App.axaml` carries a Discord-style dark palette, and it is the only place
colors belong - a view that needs one references a `Nmkoder*` brush rather than
writing a hex literal. The surfaces are `NmkoderSunken` (#1E1F22, inputs and
lists), `NmkoderBackground` (#2B2D31, the window), `NmkoderPanel` (#313338, the
tab panel) and `NmkoderHover`; text is `NmkoderText` (#DBDEE1, never white),
`NmkoderHeaderText` and `NmkoderMutedText`; the one accent is `NmkoderAccent`
(#79D1C6, a muted aquamarine). The accent is lighter than the text on it would be, so an
accent fill carries `NmkoderOnAccent` (#102726) rather than white - a selected
row, a checked box, the Run button. Nothing in the UI is pure white on pure black except the log box,
which is a terminal and keeps that contrast on purpose - that is what
`Classes="log"` marks.

It reaches Fluent's own controls two ways. `FluentTheme.Palettes` holds a
`ColorPaletteResources` for the `Dark` variant, which repaints the theme's
derived brushes - scrollbars, spinners, disabled states, focus rings - so
nothing falls back to Fluent's black-and-white base. The leaf keys the control
templates bind to directly (`ButtonBackground`, `TextControlBackground`,
`TabItemHeaderForegroundSelected` and the rest) are overridden in
`Application.Resources`, which wins over the theme for every `DynamicResource`
lookup. A key that exists in neither place is one Fluent 12.1 does not define;
the names it does define can be read out of the theme assembly with
`strings -el ~/.nuget/packages/avalonia.themes.fluent/<version>/lib/net10.0/Avalonia.Themes.Fluent.dll`.

There is no display in a web session, but the UI can still be seen: a throwaway
console project referencing `Nmkoder.csproj` plus `Avalonia.Headless` and
`Avalonia.Skia` can `AppBuilder.Configure<Nmkoder.App>().UseSkia().UseHeadless(new
AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting()`,
construct `MainWindow` directly (the lifetime is null, so `App` does not open one
itself), `Show()` it, pump `Dispatcher.UIThread.RunJobs()` for a second or two
while its async startup settles, and save `CaptureRenderedFrame()` to a PNG.
Switching `MainTabs.SelectedIndex` between shots covers every tab. The dialogs
all have parameterless constructors and shoot the same way.

None of that is accidental, and the `avalonia_docs` MCP server will tell you
otherwise: `get_avalonia_expert_rules` prescribes MVVM, compiled bindings with
`x:DataType`, `CommunityToolkit.Mvvm`, and styles split into merged
`ResourceDictionary` files. Those are defensible defaults for a greenfield app
and all four are wrong here. Match the surrounding code.

The same server's `search_avalonia_docs` is worth using, but it returns whole
pages - keep `max_results` at 1-2 or the reply overflows. `lookup_avalonia_api`
returns nothing for every type tried, including its own examples. For
per-member API facts prefer the XML docs shipped in the package
(`~/.nuget/packages/avalonia/<version>/ref/net10.0/*.xml`), which match the
pinned Avalonia version exactly rather than whatever the docs site publishes.

## Cutting a release

`.github/workflows/release.yml` builds and publishes. It runs on either a `v*`
tag push or a manual dispatch.

**Do not push the tag from a Claude Code on the web session.** The sandbox's git
proxy takes an ordinary branch push and hangs up on anything else. A tag push and
a branch deletion fail identically:

```
send-pack: unexpected disconnect while reading sideband packet
fatal: the remote end hung up unexpectedly
Everything up-to-date
```

It fails the same way every time, so retrying with backoff only burns time - this
is a property of the sandbox, not of the repository, the ref, or GitHub. The
workflow's dispatch path exists precisely to work around it and creates the tag
itself.

Deleting a finished branch has no such workaround. `git push --delete` hangs up
as above, and the GitHub MCP server has `create_branch` and `list_branches` but
nothing that removes a ref, so both routes are closed and the branch can only be
deleted from the repository's branches page by hand. Delete the local one, say
the remote is still there and why, and leave it - never report a branch deleted
when only the local copy is gone. The queue of merged `claude/*` branches sitting
on the remote is what that costs, and it costs nothing else.

The steps:

1. Merge the work into `master`.
2. Bump `<Version>` in `Nmkoder/Nmkoder.csproj`, committed on its own as
   "Bump version to X.Y.Z". The generated notes list commit subjects newest
   first, so this becomes the changelog's first line.
3. Push `master`.
4. Dispatch the workflow with `version=X.Y.Z` and `publish=true`. From a Claude
   session that is `mcp__github__actions_run_trigger`, method `run_workflow`,
   `workflow_id: release.yml`, `ref: master`. Leaving `publish` off or false
   produces a *draft* release instead of a public one.

Check the version against the published releases before picking it - the csproj
is bumped in the same commit range as the release it belongs to, so the number
sitting in the file is usually the one already released, not the next one.

**The version is 2.8.x, and every release is a patch step.** Not a default with
a carve-out for big changes - a rule. The next number after 2.8.0 is 2.8.1, then
2.8.2, and on past 2.8.9 to 2.8.10. Do not bump the minor digit, and do not bump
the major one; there is no size of change that earns either.

Nmkoder is an end-user application, not a library. Nothing consumes an API from
it, so semver's minor-versus-patch line - which exists to tell consumers whether
their code still builds - carries no information here, and a digit that carries
no information is one nobody should be spending judgement on. What it did carry
was a count of merged branches: 2.1 through 2.7 went by in twelve releases. That
is what this rule exists to stop.

This file used to say "patch by default" and then hand back a bar to argue about
- a new tab, a reworked UI, a capability the app did not have before. Anything
substantial clears a bar written like that, so 2.8.0 was cut for one tab's
resize control. The bar is gone rather than raised, because the arguing was the
problem.

Bump on `master` after the merge, never on the feature branch - step 2 above
follows step 1 for that reason. Two branches in flight both reaching for the
next number is how 2.5.0 came to be bumped twice, with a 2.4.2 landing in
between them.

The run builds win-x64, linux-x64, osx-x64 and osx-arm64, bundles external tools
via `.github/scripts/bundle-tools.sh`, and composes notes from
`git log --no-merges` since the previous tag. It takes roughly six minutes.

Each RID carries its target framework in the matrix, because win-x64 is the one
built against the Windows App SDK. The win-x64 job is also the only place the
Windows publish is ever exercised - see the build section above.

## Notifications

A finished or failed run notifies when the window is not in the foreground
(`Notifications.ShowIfInBackground`), through the OS and nothing else:
`OsUtils.ShowSystemNotification` uses `notify-send` on Linux, `osascript` on
macOS, and the Windows App SDK on Windows.

There was an Avalonia `WindowNotificationManager` toast beside it once, inherited
from the WinForms build's Tulpep.NotificationWindow. It was drawn *inside* the
app's own window, and the only moment it fired was the one where that window is
minimized or buried, so nobody ever saw it. Do not reach for it as a fallback
when the OS ping fails - it was never the thing doing the work.

Windows used to get only a flashing taskbar button, on the grounds that an
unpackaged app could not raise a notification at all. That has not been true for
a while: `AppNotificationManager.Register()` performs its own COM registration,
so there is no MSIX package, no AppUserModelID and no Start Menu shortcut in
this - only the older WinRT `ToastNotificationManager` ever needed those. The
flash is still the fallback for when the App SDK cannot come up.

Self-contained is not a preference here. Framework-dependent would require every
user to install the App SDK runtime before a notification worked, which a
portable zip cannot ask, so the runtime ships in the build - and that is what
`WindowsAppSDKSelfContained`, `SelfContained` and `EnableMsixTooling` in the
csproj are for. The App SDK's single-file validation additionally *demands*
`IncludeAllContentForSelfExtract`, and that flag does **two** things, both of
which bite:

1. It sweeps `Content` items into the exe. `BinFiles/**` carries
   `ExcludeFromSingleFile="true"` to opt back out, because
   `Paths.GetBinPath()` resolves against the exe's own directory and
   `bundle-tools.sh` writes there too. That metadata is necessary and **not
   sufficient**, which this file used to claim it was: on the Windows build the
   items reach neither the bundle nor the output directory and simply vanish.
   That is how 2.7.2, 2.7.3 and 2.7.4 each shipped a win-x64 zip carrying no
   `bin/iso639.csv` and no `bin/av1an/encoderArgs`, the only symptom being an
   empty argument grid on the AV1AN tab. The `CopyBinFilesToPublishDir` target
   copies them in again after publish, past whatever the single-file and MSIX
   machinery decided. Do not delete it on the grounds that the `Content` item
   above already covers this - it does not, on the one platform that matters.
2. It repoints **`AppContext.BaseDirectory` at the bundle's extraction folder
   under temp**, not at the exe. This one shipped broken in
   2.7.2: `Paths.GetExeDir()` was built on `BaseDirectory`, so `bin/` resolved
   into temp, the bundled ffmpeg and ffprobe were not there, and every file
   loaded scanned as having no media streams - with settings and logs going to
   the same temp folder. `GetExeDir()` now derives from `Environment.ProcessPath`,
   which is the exe under every bundling mode. Never reintroduce
   `AppContext.BaseDirectory` for anything that has to sit beside the exe.

Neither is visible in a normal `dotnet build`, and neither happens on Linux or
macOS, which do not set the flag - but they are not checked the same way, and
assuming they were is what kept the first one shipping.

The `BaseDirectory` one reproduces on linux-x64: publish with
`-p:IncludeAllContentForSelfExtract=true` and run the result, since that effect
is not platform-specific. The vanishing `Content` is **Windows-only and does not
reproduce there** - a linux-x64 publish with the same flag lays the files out
correctly, so a green local check means nothing. Only the release workflow's
win-x64 job can prove that one, which is why it now verifies the six files are
in the publish output and fails the build when they are not.

Inspecting a published archive settles it without downloading one: a zip's
central directory is at its end, so a range request for the last few KB, parsed
for the entry names, lists everything the asset contains.

`WindowsToast` touches App SDK types exclusively from `NoInlining` helper
methods. That is deliberate: the JIT resolves types when it compiles a method,
so an inlined call would throw on a machine with a broken App SDK while
compiling the *caller*, outside the `try` that is meant to catch it.

## The AV1AN tab

`bundle-tools.sh` fetches av1an's latest *release*. Anything that depends on an
av1an feature newer than that release has to check for it at runtime rather than
assume it - av1an rejects an entire command over one unrecognised flag instead of
ignoring it, so an unguarded new flag breaks every encode.
`AvProcess.Av1anSupportsFlag` reads the binary's own `--help` for this.

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

## Deinterlacing

Both encode tabs carry a Deinterlace setting, defaulting to Automatic, which does nothing at
all unless the source really is interlaced. That default is the point: a Hi8 or VHS capture
comes out deinterlaced without anyone having had to know to ask, and a modern download is
left alone.

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

**Two things QTGMC deliberately does not cover.**

A trim, because the trim is ffmpeg's - an input seek, an output duration, or a frame-number
filter - and none of the three reaches the script that reads the source, so the video would
arrive whole while the audio arrived cut. Both together means cutting first; the Cut utility
does that without re-encoding, and the log says so.

The AV1AN tab, for two reasons and the second is the one that would remain even if the first
were solved. av1an applies video filters with ffmpeg once per chunk, and there is nowhere in
that to put a script; and av1an evaluates its input for scene detection, again for every
chunk, and again for every probe a target-quality mode runs - so a filter costing more than
the encoder does would be paid for several times over. That tab gets bwdif or yadif, at the
source frame rate: av1an works out the output's frame rate from the source and hands each
encoder a fixed number of frames per chunk, so one frame per field would write twice the
frames under the source's own rate, and the file would play at half speed.

**Deinterlace For Encoding is the answer to that second one.** The utility runs the same
VapourSynth pipe Quick Convert would, into a near-lossless x264 MKV, audio and subtitles
copied, and then loads the result. QTGMC is paid for exactly once, sequentially, and av1an
gets a progressive file with nothing in front of the encoder.

**Its settings are its own** - `UtilDeinterlace.Settings`, a `Configure…` dialog off its card,
persisted under three `Config.Key` entries, defaulting to QTGMC outright where the tabs default
to Automatic. It read the Quick Convert tab's Deinterlace row until 2.8.6, on the reasoning that
the mode and the preset should be set in one place. That only holds for someone who uses both
tabs, and this utility exists *because* the AV1AN tab cannot run QTGMC - so the person reaching
for it is by definition encoding somewhere else, and was being sent to a tab they do not use to
change a setting that also changes what that tab does. Automatic is likewise right on a tab that
encodes whatever it is given and wrong here, where doing nothing means re-encoding the source
into a copy for no reason.

Feeding av1an a `.vpy` directly is possible and is the wrong trade. Measured rather than
assumed: chunking does not damage a temporal filter - frames 300-319 rendered as a chunk come
out bit-identical to the same frames of a sequential render, and three 240-frame chunks took
1.11 / 1.19 / 1.17 s against 3.41 s for all 720 in one go, so the per-chunk cost is about 2%.
What it costs is the *repeats* above, and three sharp edges: av1an's own source says
"vapoursynth audio is currently unsupported" and skips the audio thread entirely, its Select,
Segment and Hybrid chunk methods call `as_video_path()` which panics on a VapourSynth input,
and seeking into an MPEG program stream is not frame-accurate - `vspipe -s 300` on a `.mpg`
came back with the frame the sequential render calls 298, where the same video remuxed to MKV
landed on 300 exactly.

The utility only takes over the file list when that list held nothing but the source. In
muxing mode the file list *is* the set of inputs, so adding one quietly would change what the
next mux writes, and a batch is stepping through a queue that must not move underneath it -
both are told where the file is instead.
