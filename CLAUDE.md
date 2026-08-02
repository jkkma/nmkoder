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

**av1an's target quality probes never see the `-f` filters.** `Encoder::probe_cmd` composes the
probe's ffmpeg pipe out of nothing but the probing-rate `select`, and the chunk's own source
command carries no filters either, so a resize, a crop or a deinterlace is invisible to the
quantizer search - it settles on the value that hits the target at the *source's* size and that
value is then used on chunks encoded at another. Nothing here can fix that, so the tab says so
whenever a target mode meets a filter chain, naming both sizes when the frame changes size.

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

Quick Convert has the same tile count and no such pass to hang it on: its scale boxes are free
text handed to ffmpeg, and it builds its codec arguments per pass, right beside the filter chain.
`QuickConvertUi.GetEncodedFrameSize` therefore resolves only what can be stated with certainty -
a plain pair of numbers, or a lone number with the other side derived by ffmpeg's own `-2`
arithmetic - and returns `Size.Empty` for a percentage or an expression, which leaves the encoder
on the source's size exactly where it always was. It does not apply the crop either: resolving an
automatic one costs those ten probes, and there is nowhere here to spend them once. A tile count
worked out from a size that is not the real one is the thing being fixed, so guessing is worse
than abstaining.

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

**Nothing on the AV1AN Video tab is saved.** The encoder, the container, the quality mode and its
value, the preset, the colour format, grain synthesis, the frame rate, the resize, the crop, the trim
and the deinterlace all start each session at their defaults - SVT-AV1 into MKV, then whatever
selecting that encoder writes into the rest - and `LoadAv1anEncodeSettings` restores none of them. It
is down to the Audio & Tracks rows, the two custom-argument boxes and the filter grid; `LoadConfigAv1an`
keeps the audio codec and the Av1an Options tab. Those settings describe a job rather than a
preference, and every way they go wrong is expensive and quiet: a QTGMC left armed spends hours and
tens of gigabytes on a progressive source, a resize left on 720p halves a 4K encode nobody meant to
shrink, a CRF picked for a grainy film is the wrong number for line art. Reset On New File already
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

## Deinterlacing

**Both encode tabs hide the Deinterlace row until the loaded file is known to be interlaced**, and
the setting behind it defaults to QTGMC at Very Slow. A Hi8 or VHS capture therefore arrives with
the best deinterlacer there is already selected, and nothing else ever shows the control at all -
`DeinterlaceUi.IsRowRelevant` is the one question both halves of that are asked, and it is false for
no file, for a progressive one, and for a file whose scan type has not been measured yet.
`AnalyzeInBackground` calls `RefreshInfo` when the verdict lands, so the row appears a moment after
loading on the files that need it.

**Those two changes are only safe together, and `ModeInEffect` is the join.** An engine picked by
name deinterlaces whatever it is handed - `Deinterlace.ResolveAsync` consults the scan verdict only
for Automatic - so a default of QTGMC behind a hidden row would have put an hours-long pass on every
progressive file with nothing on screen to explain it. So while the row is hidden both tabs report
**Automatic** whatever their box says, and Automatic is the one mode that is safe without knowing
anything: it asks the verdict, does nothing to progressive video, and still cleans up a file whose
scan type had not been measured when the encode started, because `ResolveAsync` waits for that
answer itself.

What it costs is the way past a container that lies about its own scan type. A file flagged
progressive is believed rather than scanned - see below - and forcing an engine by name was how that
was overruled, which cannot be done through a row that is not there. The Deinterlace Video utility
takes no notice of any of this and deinterlaces what it is given, so that is where a mis-flagged
file goes.

**An engine picked by name must not outlive the file it was picked for.** What made that a trap was
that the mode was sticky and nothing cleared it: it was saved per tab and restored at startup, so a
QTGMC picked for a tape was still armed days later, and on the AV1AN tab that is a full pass over the
video into a near-lossless intermediate before av1an starts. 2.8.12 shipped that - a progressive 1080p
WEB-DL got hours of QTGMC Very Slow and 47.952 fps of interpolated fields, with nothing wrong anywhere
in the detection, which had read it correctly and said so on screen. Hiding the row is what closes
that case for good; the resets below still matter for the file the row *is* shown for.

`ResetSettingsOnNewFile.ResetDeinterlace` is the other half, on by default beside Trim and Crop - the
three whose value describes the file that was just replaced rather than how the user likes to
encode. `DeinterlaceUi.ResetModes` puts both tabs back to `DefaultMode` and touches neither the preset
nor the field doubling, which say *how* to deinterlace rather than *whether*. Only where a person
loaded the file: a batch clears each one with `resetSettings: false`, so a stack of tapes keeps the
engine picked for it.

The startup half of that trap is closed at the other end now - the AV1AN tab restores nothing across
sessions at all, so its mode is the default on every launch whatever was picked last time. Quick
Convert's is still saved, and still relies on this reset, because deinterlacing there is one filter
in a chain rather than a pass of its own.

The default is stated in exactly two places and nowhere else: `DeinterlaceUi.DefaultMode` for the
engine, `Qtgmc.DefaultPreset` for the preset. The second is not only a default - it is also the
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
the loaded file's lands here too, and neither is a fault in the run.

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
ffmpeg's - an input seek, an output duration, or a frame-number filter - and none of the three
reaches the script that reads the source, so the video would arrive whole while the audio
arrived cut. Both together means cutting first; the Cut utility does that without re-encoding,
and the log says so. The AV1AN tab's trim is not ffmpeg's - it cuts a copy before av1an starts -
so there the two compose, and `Av1an.Run` runs the cut first and QTGMC over what it produced.

**QTGMC cannot run inside av1an, so the AV1AN tab renders it in front.** av1an applies video
filters with ffmpeg once per chunk and there is nowhere in that to put a script; and it
evaluates its input for scene detection, again for every chunk, and again for every probe a
target-quality mode runs, so a filter costing more than the encoder would be paid for several
times over. `Av1an.RenderDeinterlacedInput` therefore runs `DeinterlacePass` over the whole
video into `{tempDir}.deint.mkv` - beside the temp folder, where the trimmed input goes, because
av1an empties its own temp folder at startup - and av1an is given that. Paid for exactly once,
sequentially, and the encoder gets a progressive, seekable, frame-accurate file.

That is why **one frame per field is offered for QTGMC there and for nothing else**. The pass
runs before av1an, so the doubled rate is simply the rate of the file av1an opens; a filter
*inside* av1an emitting one frame per field would write twice the frames its chunking expects
under the source's own rate, and the file would play at half speed. A QTGMC that falls back to
bwdif - no VapourSynth, an RGB source - falls back into exactly that position, so `Av1an.Run`
clears `DoubleRate` on any plan that is not the pipe. Do not remove that line.

**Automatic on the AV1AN tab stays on bwdif**, where Automatic everywhere else reaches for
QTGMC. Automatic's whole job is to be the setting nobody thinks about, and starting an
hours-long pass and a tens-of-gigabytes intermediate is not that. The expensive engine is the
one you pick by name - `DeinterlaceUi.Av1anAutoQtgmcProblem` is how that is said, through the
same `QtgmcUnavailableHere` field the tabs use for their real impossibilities.

That is a statement about Automatic, not about what the tab opens on, and the two have come apart:
the default is QTGMC now, so an interlaced file loaded on the AV1AN tab gets the expensive pass
unless someone changes the row. Automatic is still the mode a hidden row reports, which is what
keeps a progressive file clear of it - and still bwdif when a person picks it deliberately.

Both tabs' dropdowns are `DeinterlaceUi.AllModes` in one order, and the Quick Convert box saves
its index - so entries may be appended to that list but not reordered. The AV1AN box saved the
mode's **name** for a while, because adding QTGMC in its proper place moved Bwdif and Yadif down
one and a saved index of 2 would have started an unwanted QTGMC pass for someone who had picked
Bwdif. That box now saves nothing at all, its whole tab starting each session at the defaults, so
the migration that read the old integer is gone with it.

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

**The Deinterlace Video utility exports a file and stops.** It shares the pass - `DeinterlacePass`
is the one place the near-lossless x264 MKV with its audio and subtitles copied is written - and
its output is the deliverable rather than a step on the way to a tab. Until 2.8.10 it was that
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
