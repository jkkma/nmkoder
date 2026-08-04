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

**A repainted palette entry can make a disabled control the loudest thing on the
screen.** Fluent fills several disabled states from `BaseLow`, and this palette
hands `BaseLow` the neutral *button* grey (#4E5058) - so where the theme means
"fade this out" the control came out brighter than the sunken field around it.
That is what put a light grey square on the end of every `NumericUpDown` sitting
at its minimum or maximum: the chevron that had just stopped working was the
most prominent thing in the row, and a disabled one showed two. The spinner's
own leaf keys (`RepeatButtonBackgroundDisabled` and friends) are overridden for
it, and anything else reaching for `BaseLow` will need the same. Check a control
disabled, not only enabled.

A `NumericUpDown` is worth knowing the shape of, because almost nothing about it
answers to the outer control: it templates a `ButtonSpinner`, which carries the
border, the corner radius, the *minimum height* and two `RepeatButton`s, and the
number itself lives in a second, borderless `TextBox` inside that. So the height
comes from the spinner's own theme rather than the `NumericUpDown` style beside
it, hover and focus land on the inner box (an accent ring around the digits, in
a field that stayed unmarked), and the hairlines between the parts are the
buttons' own left border taken from the field's - which is how a focused field
drew accent lines down its middle. The buttons' template binds no `CornerRadius`
at all, so a hover fill is square unless the `ContentPresenter` inside is given
one; `Border.ClipToBounds` will not round it for you, having a rectangular clip.

There is no display in a web session, but the UI can still be seen: a throwaway
console project referencing `Nmkoder.csproj` plus `Avalonia.Headless` and
`Avalonia.Skia` can `AppBuilder.Configure<Nmkoder.App>().UseSkia().UseHeadless(new
AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting()`,
construct `MainWindow` directly (the lifetime is null, so `App` does not open one
itself), `Show()` it, pump `Dispatcher.UIThread.RunJobs()` for a second or two
while its async startup settles, and save `CaptureRenderedFrame()` to a PNG.
Switching `MainTabs.SelectedIndex` between shots covers every tab. The dialogs
all have parameterless constructors and shoot the same way. States nobody can
click in a headless session are reachable too - `((IPseudoClasses)control.Classes)
.Set(":pointerover", true)` on a template part renders the hover, the press or
the focus, which is the only way to see what a restyled control does before
shipping it.

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

## Reading what the tools print

**ffmpeg's stats line is not a stable format, and two parts of it have already
moved.** The key is `size=` while muxing and `Lsize=` on the final line of a run
that has a frame counter, and the unit was spelled `kB` for years and is `KiB`
now - both meaning the same 1024 bytes. `FfmpegUtils.GetStreamSizeBytes` was
written as `Split("size= ")[1].Split("kB")[0]`, and the failure is quieter than
"it did not match": `Lsize=` still *contains* `size= `, so the first split
happened and handed on the rest of the line, and only the `kB` split came back
empty-handed. What reached `GetInt` was therefore the whole tail - `235KiB
time=00:00:05.93 bitrate=…` - which strips to a digit string far past `int`'s
range, so `TryParse` fails and it returns its fallback of 0. Every stream in the
bitrate readout reported `Size: 0B (0.0%)`, and every total with it, from
whenever the bundled ffmpeg crossed that rename. The *bitrate* on the same line
kept parsing perfectly, which is most of why nobody caught it: the readout looked
like a file full of empty tracks rather than like a broken parse. Match the shape
of the line, not one spelling of it.

`bundle-tools.sh` takes BtbN's `master-latest`, so the ffmpeg underneath this app
moves continuously and a format it prints today is not a promise about next
month. What it prints *now* was read out of the binary's own strings rather than
guessed: exactly one size format, `size=%8.0fKiB time=`, and one bitrate format,
`bitrate=%6.1fkbits/s`. A 1.9 GB stream still printed KiB, so the larger prefixes
in the regexes are defensive rather than something you can produce. They are not
scaled alike, which is the part to be careful with: a size is binary, matching
its KiB/MiB spelling, while a bitrate is ffmpeg's own division by 1000 and so
decimal.

`Lsize=` never *starts* a line - it only appears after `frame=`, and an audio-only
run's final line begins `size=`. Measured across a video stream copy, an audio
stream copy and an audio encode, which is why `FfmpegOutputHandler`'s gate on
`frame=`/`size=` covers both and does not need widening. That one looks like a
gap and is not.

**`File.Exists` is not a test of whether ffmpeg wrote something.** It creates the
output file before it writes the header, so a codec the container refuses leaves
a stub behind and the check passes - measured at 291 bytes for `-map 0:s -c copy`
out of a mov_text MP4 into Matroska, a file ffprobe answers "End of file" to.
`OcrUtils` used exactly that check as its guard against exactly that case, and it
had never once fired. Count the streams in the result instead, and ask
**uncached**: `GetVideoInfo`'s cache is keyed on path, size and command with no
timestamp in it, which a temp file at a fixed path rewritten every run defeats.

**MKVToolNix is bundled for win-x64 alone**, so mkvmerge, mkvextract and mkvinfo
are routinely absent on Linux and macOS - and a missing binary is not a failure
any caller here can see. The command goes through a shell, which writes "command
not found" to a stream nothing reads and exits, so the caller finds out only by
noticing the file it wanted was never written, and then reports whatever it has
to say about *that*. Concat announced "Could not find file" naming a temp path
the user had never heard of; av1an's attachment step deleted the encoded
`audio.mkv` before checking mkvmerge had written its replacement, so every Linux
and macOS encode with an MKV output finished silently without sound.
`AvProcess.IsToolAvailable` is the question to ask first. It searches the PATH the
tool will be *launched* with rather than this process's own, because
`OsUtils.SetPathVar` keeps only `bin/` and `C:\Windows` on Windows - checking the
full PATH would vouch for an mkvmerge in Program Files that the launcher then
cannot resolve, which is the same mystery under a new name.

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

The growth is a multiple of **four**, not two. Half of a multiple of four is even, so one rounding
makes the frame even *and* both offsets land on the chroma grid - where ffmpeg's own pad filter would
otherwise relocate an odd offset silently, putting the picture a pixel off the middle it was centred
in. Nothing is added under four pixels: two a side is not a bar, and growing a source that is already
the target shape to within a rounding is exactly the surprise a batch must not spring. The mod-2 pad
above the scale is untouched and still needed - a letterbox only grows the height, so an odd width
stays odd.

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
blackness of the bars read back out of the frame; then 1152 chains built by the tab itself - 8 sources
including two anamorphic DVDs and a genuinely odd 641x481, against crops, resize presets, exact
pad/stretch, anamorphic correction off, a frame-rate resample, and every scale-box form - each run
through ffmpeg and compared against the size the tab said the encoder would get. No mismatches. Note
that x264 silently produces 640x480 for a 641x481 source, so the odd-frame case needs FFV1 to exist at
all.

**Nothing on the AV1AN Video tab is saved.** The encoder, the container, the quality mode and its
value, the preset, the colour format, grain synthesis, the frame rate, the resize, the crop, the trim,
the borders and the deinterlace all start each session at their defaults - SVT-AV1 into MKV, then whatever
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
gives the chunk count and `done.json` the finished one, both in the folder this app names itself with
`--temp` - so `Av1anOutputHandler` parses no av1an log line at all, and that is the point.

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

**av1an's own log is put in the temp folder rather than left to that default.**
`Av1an.GetLogFileArgs` names it, beside `--temp` and for the same reasons: the folder only exists once
the run has one, and both flags sit ahead of the `-i` that `SaveJson` starts saving from, so a resume
sets its own instead of writing into the previous attempt's. Left to av1an, the log went to
`./logs/av1an.log` *relative to the working directory*, which is `bin/av1an` - so every encode dropped
a dated file beside the binary, in a folder nothing here knew about and nothing ever cleared. In the
temp folder it lives exactly as long as the run's other state: `HandleTempFolder` keeps that folder
when the encode failed, which is when the log is worth reading. Nothing parses it, so the file name is
not load-bearing - as well, since av1an appended its own `.log` to this value until 0.4.x and does not
now.

**Nothing under av1an's Scene Detection heading goes out for Split Method "None".** It is `-x` and
nothing else there, so `--sc-downscale-height` named a resolution for a pass that never ran.
`Av1anUi.SceneDetectionEnabled` is the one statement of which entry is which, and
`Av1an.GetScDownscaleHeightArg` also drops the flag where the height works out to 0, since 0 is not
inert: av1an skips the downscale only when the height it is given is *above* the source's, so a zero
reaches ffmpeg as `scale=-2:'min(0,ih)'` and is refused.

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

## The Quick Convert command

**One ffmpeg command line is built in `QuickConvert.Run`, and the order things are worked out in is
load-bearing.** The encoder's arguments and the filter chain come first, because the stream maps have to
know whether there is a filtergraph for the first video track to be read out of - and that is not a
question the encoder answers on its own. GIF contributes its entire `palettegen`/`paletteuse` graph
through `CodecArgs.ForcedFilters`, so a probe made without the codec arguments could not see it: the
source was mapped directly, past a graph whose output then went nowhere, and ffmpeg refuses that
outright ("Filter paletteuse:default has an unconnected output"). **GIF could not be produced at all**
unless some other filter happened to be configured. `TrackList.GetMapArgs` is handed the answer now,
which also stops it building the whole chain - autocrop probes included - to ask whether it was empty.

Those forced filters go **last**, not first. A palette describes the frames it is generated from, and
run first it quantised the source and then let the scale, the crop and the burnt-in subtitles work on
the paletted result, which the muxer re-quantised again.

**A hidden control still holds a value, and three of them reached the command.** The container box is
hidden for GIF, JPEG and PNG and kept whatever was last selected: the extension came off it, so an
animated GIF was written as `clip.mkv` - and the overwrite check looked at that same name, so it was
never checking the file about to be written. Its muxer's private options went out too. The Quality Mode
box is disabled for the same three and kept a Target Bitrate, so `GetVideoArgsFromUi` sent a bitrate
where those encoders read a `q`: the palette size and the JPEG quality did nothing at all. Fixing only
half of that is worse than neither - the spinner's *range* comes from the mode as well, so a palette
size left sitting at 1500 reaches ffmpeg out of range and kills the encode.
`QuickConvertUi.GetEffectiveQualityMode` is the one answer all three of those read.

**`-metadata:s:N` names an output stream, and the ticked tracks are not it.** A container that cannot
hold a data or attachment track has it dropped; `-vn`, `-an` and `-sn` take a stripped kind out at the
far end. Either way every title and language after such a track landed one stream too late - onto the
subtitles, usually. `TrackList.GetMappedStreams` is the single list of what actually reaches the output,
and both the maps and the metadata are built from it. The last index of all matches no stream and ffmpeg
ignores it in silence, which is most of why this looked like it worked.

**Input-side arguments belong in front of every `-i`.** ffmpeg reads a `-ss` there as belonging to the
input that follows it, so a keyframe trim placed once at the head of the command seeked the first file
and left every other one starting from the top - which in Muxing Mode is a video that begins a minute in
playing against audio that does not. `GetInputFilesString` takes them as a per-input prefix.

**Two-pass must name its own `-passlogfile`.** ffmpeg's default is `ffmpeg2pass-N.log` in the working
directory, which is wherever the app happened to be launched from: an install the user cannot write to
failed the first pass outright, and every run that did work left a log and an x264 mbtree file beside
the exe. It goes in the session folder with the rest of the run's scratch data. Measured against
libx265 as well, which honours the flag - it does not need `stats=` in `-x265-params`.

**Target Filesize is a division, and both of its numbers were wrong.** The duration was the file's own
whatever the Trim said, so a 100 MB target on a two-hour source cut to five minutes wrote twenty-odd
times the size asked for. And the audio was booked at whatever the Bitrate spinner held - a box that is
*disabled* for a copied track, so a 1536 kbps DTS track was costed at 128 and the video was handed 1.4
Mbps the audio then took back. A copied track's own bitrate is already parsed and is the right number;
FLAC cannot be predicted at all and is estimated from the source. Nothing here or in ffmpeg compares the
result against the target, so both failures were silent.

**Quoting a path is not enough on Linux or macOS.** sh expands `$var` and backticks inside double
quotes, so a file named `My $HOME clip.mkv` reached ffmpeg as a path that does not exist and one with
backticks in its name ran what was between them. `Shell.WrapArg` is the encoding that survives: single
quotes, with the two characters they cannot carry handled by leaving the quoted run and coming back.
`EscapeExpansions` is **not** the answer here even though it looks like it - it works for the av1an
launch script, which is written to a file, and cannot work through `BuildArguments`, which doubles every
backslash, so the single `\$` it would need is not a string that layer can produce. The encoding was
measured rather than reasoned out, round-tripping `$`, backticks, both quote characters, single and
double backslashes, `%`, `&`, `!`, `;`, newlines, spaces and parentheses through .NET's argument parsing
and sh. Windows keeps its plain double quotes; cmd has no single-quoting and what it expands is a
different question.

**Burning in a text subtitle track has two quoting layers and had neither.** The path was double-quoted
inside the already-quoted `-filter_complex`, and ffmpeg has no double-quoting at all - so the quotes
became part of the filename, except that the surrounding shell happened to strip them again for a path
with no space in it. A path *with* one broke the command outright. `FormatUtils.GetFilterPath`
single-quotes at ffmpeg's level and `GetVideoFilterArgs` wraps the whole graph at the shell's.

**An apostrophe in that path is refused before the run rather than escaped.** ffmpeg's own quoting for
one - `'it'\''s'` - does not survive the second unescaping its filter's option parser makes, and neither
does any other spelling: what comes back is a complaint about a filename with the apostrophe missing and
`:si=0` stuck on the end. Measured against ffmpeg 6.1 across five encodings, quoted and unquoted.
`GetBurnInProblem` names the file and the setting instead. Bitmap tracks are unaffected - they are a
filtergraph input mapped by stream index, with no filename in the graph.

**The burn-in runs after the crop and the scale**, where it used to run before all of them: a crop
taking black bars off took the subtitles in them with it, an anamorphic source came out with stretched
text, and a downscale rendered the lines large and then shrank them. Before the borders, so they stay
inside the picture. And the track is indexed against **the loaded file**, not the file the video comes
from - the dropdown lists the loaded file's subtitle tracks, and in Muxing Mode those are two different
files.

**Muxing Mode is where "the loaded file" and "the file being encoded" come apart**, and the video chain
was built from the first of those. For the ordinary shape of a mux - a video file and an audio file -
that is a file with no video track in it, so the whole chain was silently dropped.
`QuickConvertUi.GetVideoSourceFile` delegates to `DeinterlaceUi.GetQuickConvertSourceFile` so the
geometry and the deinterlacer cannot pick different files.

**A per-stream ffmpeg option needs the stream's *type* in its specifier, not just a number.** A bare
`:0` means output stream 0, which in any output with video is the video - so Opus's
`-mapping_family 1`, re-emitted per track by `GetAudioArgsForEachStream`, was matched against the video
encoder, found no such option there and dropped, while the audio streams never matched at all. ffmpeg
says so in a line nothing here reads ("Codec AVOption mapping_family … has not been used for any
stream"). The two `args.Add` calls above it always wrote `-b:a:N` and `-ac:a:N` correctly; only the
extra-args loop did not. An audio-only output is where stream 0 happens to *be* the audio, which is
where this worked and where it mattered least.

**The per-track audio configuration and the dropdown that points at it are one setting.**
`AudioConfiguration` refuses to hand its entries to any file but the one they were made on, and
`SetAsMainFile` clears them outright - but nothing moved the "Configure each track separately" box, so
`GetAudioArgsForEachStream` found `perTrack` set and the configuration null, skipped both override
branches in silence, and encoded every track at the global spinner's bitrate with the Configure… button
still on screen. A batch met this on every file including the first, since the queue loads each one
through the same method. The box is reset where the data is, dismissing the dialog puts it back too,
and a mismatch that reaches the arguments anyway now leaves a line in the log.

That dialog also seeded its rows from each source track's own channel count, so confirming it - and it
is opened *automatically* the moment the mode is switched - overwrote a downmix already picked on the
Channels dropdown, that dropdown then being ignored because every row is written into the configuration
whether it was edited or not. The rows start from what the dropdown asks for now, the bitrate is scaled
to that same layout, and the dropdown is disabled while a configuration governs it rather than being
left looking as though it still does something.

**Refilling a dropdown loses what was selected in it.** The subtitle burn-in and the metadata/chapter
source lists are rebuilt on every change to the file list, so adding an unrelated file to the queue
turned a chosen burn-in track back to "Disabled" and put the metadata source back on the first file.
`SetItemsIfChanged` leaves the box alone when the entries have not actually changed. The metadata grid
had the same shape of bug from the other end: it was rebuilt from entries nothing had written yet, so
ticking a track in the Track List threw away whatever had just been typed into it.

## Deinterlacing

**Both encode tabs hide the Deinterlace row for a file with no fields worth discussing**, and the
setting behind it defaults to QTGMC at Very Slow. A Hi8 or VHS capture therefore arrives with the
best deinterlacer there is already selected, and a modern download never shows the control at all.

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
video into a near-lossless intermediate before av1an starts. 2.8.12 shipped that - a progressive 1080p
WEB-DL got hours of QTGMC Very Slow and 47.952 fps of interpolated fields, with nothing wrong anywhere
in the detection, which had read it correctly and said so on screen. Hiding the row is what closes
that case for good; the resets below still matter for the file the row *is* shown for.

`ResetSettingsOnNewFile.ResetDeinterlace` is the other half, on by default beside Trim and Crop - the
three whose value describes the file that was just replaced rather than how the user likes to
encode. `DeinterlaceUi.ResetModes` puts both tabs back to `DefaultMode` and touches neither the preset
nor the field doubling, which say *how* to deinterlace rather than *whether*. Only where a person
loaded the file: a batch clears each one with `resetSettings: false`.

That setting is also what `ApplyScanVerdict` reads before it selects an engine for an interlaced file,
which is what makes turning it off mean something across a queue: a stack of tapes keeps the engine
picked for it, where with it on each file gets the default the verdict asks for.

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
ffmpeg's - a seek and a duration on the command line - and neither reaches the script that reads the
source, so the video would arrive whole while the audio arrived cut. Both together means cutting
first; the Cut utility does that without re-encoding, and the log says so. The AV1AN tab's trim is
not ffmpeg's - it cuts a copy before av1an starts - so there the two compose, and `Av1an.Run` runs
the cut first and QTGMC over what it produced.

**All three trim modes are a seek and a duration, and the modes differ only in what the user types
and how exact the start is.** The keyframe mode seeks the input, which is instant and lands on the
keyframe before the point; the other two seek the output, which decodes and discards its way there
and so stops where it was asked to. `TrimSettings.GetInputArgs` and `GetOutputArgs` are the only
place that mapping lives.

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

**A trim is checked against the file before the encode starts**, through `UtilCut.ResolveSection`,
which all three of the Cut utility, the AV1AN tab and Quick Convert now ask. A trim outlives the file
it was set for and a batch does not clear it, so one section runs against every file in the queue;
where it starts past the end of a shorter one, ffmpeg seeks past everything there is and writes an
empty file without complaining. `ResolveSection` reads the section through the millisecond accessors
rather than off the fields, because in frame mode those hold frame numbers and comparing a frame
count against a duration compares nothing.

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
the default is QTGMC now, so a file *measured as interlaced* on the AV1AN tab gets the expensive pass
unless someone changes the row. Nothing weaker than that selects it - a scan that says progressive
lands on Automatic, and so does a hidden row - and Automatic is still bwdif when a person picks it
deliberately.

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
