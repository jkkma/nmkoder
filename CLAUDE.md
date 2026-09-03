# Nmkoder

Media encoding/muxing toolkit. Avalonia UI on .NET 10.

Build with `dotnet build Nmkoder/Nmkoder.csproj`.

Where things live, under `Nmkoder/`: `Views/` is every window - `MainWindow.axaml` with its partials
(`MainWindow.Av1an.cs`, `.QuickConvert.cs`, `.EncoderArgs.cs`, `.FileList.cs`, `.StreamList.cs`,
`.Utils.cs`, `.Settings.cs`, `.Layout.cs`, `.Log.cs`) and the dialogs. `UI/Tasks/` builds each tab's
command (`Av1an.cs` + `Av1anUi.cs`, `QuickConvert.cs` + `QuickConvertUi.cs`, the `*Ui.cs` row drivers)
and holds every Utilities-tab task (`Util*.cs`); `UI/` is the shell around them - `FileList`,
`TrackList`, `Notifications`, `ResetSettingsOnNewFile`. `Media/` drives the tools (`AvProcess`,
`FfmpegCommands`, `Qtgmc`, `CadenceRepair`, `ToneMap`, `Grav1synth`, `Loudnorm`, the metric scorers).
`Data/` is the settings objects and codec tables - the `*Config.cs` files, `TrimSettings`, `Paths`,
and `Codecs/Video/` with `VideoEncodersBin.cs` (av1an's encoders), `VideoEncodersDirect.cs` (the
binaries Quick Convert launches) and `VideoEncodersLib.cs` (ffmpeg's own, kept for the CRF ladder).
`IO/` is config, logging and the packager; `OS/` is process launching, `Shell.WrapArg` and the Windows
toast; `Utils/` is `FormatUtils`, `ColorDataUtils` and the other shared helpers. `BinFiles/` is the
tracked tool data (`encoderArgs/{av1an,ffmpeg}`, `iso639.csv`) that lands in `bin/` beside the exe.

**Development happens on the user's two Windows machines - a laptop and a desktop, worked in
tandem - and nowhere else since August 2026.** Sessions used to run in Claude Code on the web's
Linux containers as well, and this file was written across both; where a passage below says a
thing "could not be measured in a web session" (no av1an that executes, no VapourSynth, no GPU,
no Dolby Vision file to be had), that was the constraint of the time and it is recorded as the
history of how a claim was established. Every shipped binary is on both machines now, at
`~/.nmkoder-dev/bin`, so those measurements can be made here - the note stays because it says
what was and was not run, not because it is still true of the environment.

**A machine is set up by `.claude/setup-windows.sh`, run by hand from Git Bash in the repo** -
once, and again after a `dotnet clean`, a fresh clone or a worktree (~5 s when nothing has
changed). It checks the .NET SDK the csproj targets, builds if nothing is built, and then puts
right the thing a plain `dotnet build` leaves missing: the app looks for its tools in `bin/`
beside the exe (`Paths.GetBinPath`) and squeezes the launched tools' PATH to that folder plus
`C:\Windows` (`OsUtils.GetPathVar`), so a Debug output has `encoderArgs` and `iso639.csv` from
`BinFiles/` and nothing else - no ffmpeg of its own, no av1an, no encoders, no mkvmerge - and
Quick Convert refuses every direct-encoder codec while the AV1AN tab cannot start. The script
takes the *shipped* `bin/` out of the latest published win-x64 zip - the bundler's own output,
so the exact binaries users get, PSY-line SvtAv1EncApp and all, where Scoop's svt-av1 is
mainline and running `bundle-tools.sh` locally would want MSYS2 and cargo - caches it at
`~/.nmkoder-dev/bin`, hardlinks it into every build output beside an `Nmkoder.exe` (never
overwriting the working tree's own `BinFiles/` copies), and appends the four tool folders to the
user PATH. Verified by launching the Debug build: its own startup probe rendered a QTGMC frame
through the staged VapourSynth. `dotnet clean` leaves the staged tools alone; only a deleted
`bin/` costs a re-run.

The BtbN `master-latest` ffmpeg in that folder is what `bundle-tools.sh` puts in a release, so a
measurement against it is a measurement against the binary users get. A bare `ffmpeg` on the
user's PATH resolves to their own Scoop build first - the script *appends* rather than shadows -
so a harness that means the shipped one names `~/.nmkoder-dev/bin/ffmpeg.exe`, and a
measurement says which it used. `av1an.exe` panics without `VSScript.dll` beside it on PATH
("VSScript API not available"); `bin\av1an\vsynth` is on the user PATH for that reason and is
the same PATH `AvProcess.RunAv1an` composes.

**Claude Desktop is a packaged app with file-system write virtualization on**, and that is worth
more than the script. Anything a session writes under `%LOCALAPPDATA%` or `%TEMP%` - the
scratchpad included - lands in `…\AppData\Local\Packages\Claude_<id>\LocalCache\Local\…` and does
not exist for a process launched outside Claude: a tool "installed" there from a session is one
the user's own shell cannot see, with no error anywhere. `fsutil hardlink list <file>` prints the
real NTFS path and is how it was caught. The profile root, `~/scoop`, `~/.nuget` and the repo are
not virtualized, which is why the cache is `~/.nmkoder-dev`; and registry writes are not
virtualized either (the package manifest disables that half), so a user-PATH edit from a session
is real. Git Bash's `unzip` does not cross `/` with `*` on this build - `Nmkoder/bin/*` yields
`bin/`'s top-level files and none of `av1an/` - so the script extracts with Windows' own `tar.exe`
(bsdtar), which takes a bare `Nmkoder/bin` as the whole subtree.

**The session-start hook does one thing: `git pull --ff-only` on the checked-out branch.** A
session opening on whichever machine sat idle is opening on a clone the other has already moved
past. It runs at startup, resume and clear and not on compact - the one `SessionStart` that fires
mid-turn, where a working tree moving under a running edit is exactly what an automatic pull must
not do - and it never merges or rebases: a diverged branch, a dirty file in the way, no upstream or
no network each leave the tree as it was, and the hook's one `session-start:` line says which.
**That line is what a session reads before touching a file, and a session that opens with no such
line is one where the hook did not run - pull by hand.** Every path exits 0. It leans on `sed`
rather than `jq` to read the hook's `source` field, and it was checked under Git Bash
specifically: `core.autocrlf=true` leaves the working copy CRLF, which that bash strips
transparently - measured, not assumed - so a CRLF hook runs there where a stock bash would choke
on it. It installs nothing, on purpose.

**Seeing the UI differs between the two machines.** On the laptop, launch the Debug build and
screenshot its window: `Nmkoder/bin/Debug/net10.0-windows10.0.19041.0/win-x64/Nmkoder.exe` is
the release's shape (App SDK notifications and all; `…/net10.0/Nmkoder.exe` is the
cross-platform one), its `data/` and `logs/` land beside it, and the pwsh capture wants
`SetProcessDPIAware()` before `GetWindowRect`, or a scaled display hands back the top-left
quarter of the window - `GetWindowRect` reports logical pixels and `Graphics.CopyFromScreen`
reads physical ones. A local session on the desktop cannot capture the screen (reported; cause
not established), so there the headless-ui skill - Avalonia.Headless rendering to PNG, never
touching the display - is the way to see it, and it is the only way on either machine to force
hover/press/focus states or measure control geometry. Under Git Bash its `__REPO__` has to be a
Windows path (`cygpath -m`), which the skill's setup line does.

The project multi-targets, but only on Windows: `net10.0` everywhere, plus
`net10.0-windows10.0.19041.0` when the *host* is Windows. That second framework
is the one carrying the Windows App SDK, and its build runs MSIX tooling
(`MakePri.exe` and friends) - Windows binaries, so a Linux or macOS host cannot
evaluate that TFM at all, not even far enough to compile. Hence the condition on
`TargetFrameworks`, which keeps the command above working everywhere - the release
workflow's linux and macOS jobs among them.

On the machines development happens on both TFMs build, so an ordinary `dotnet build`
compiles everything under `#if WINDOWS` and there is nothing special to do to check it.
(From a Linux or macOS host it is not compiled at all; the recipe, should one ever be
needed again, is a throwaway project targeting `net10.0-windows10.0.19041.0` with
`EnableWindowsTargeting=true` that `<Compile Include>`s just the files in question beside
stubs for what they touch and references `Microsoft.WindowsAppSDK` with
`IncludeAssets="compile" ExcludeAssets="build;buildTransitive;native;runtime;analyzers"` -
excluding the build assets is what skips the MSIX targets that cannot run.) The *publish*
is still only proven by the release workflow's win-x64 job.

**Six areas keep their full record in a skill rather than in this file, and each has a digest here
under its own heading.** Tone mapping, the AV1AN tab, grain synthesis and deinterlacing were the
first four, 63% of the document as it then stood - 2,869 of 4,580 lines; driving the encoder
binaries directly and repairing a padded capture followed in a second pass, once they had grown
back into the largest sections left. The document loads into every session whole, before a word is
typed. Their sections now carry what has to hold whatever you are doing, and point at
`.claude/skills/{tone-mapping,av1an-tab,grain-synthesis,deinterlacing,direct-encoders,cadence-repair}`,
which load on any task in their vocabulary. The bodies were moved **verbatim** - byte-identical,
nothing paraphrased and nothing dropped - so the skill is the same text this file used to hold.

The division is the digest's job: **an invariant that can be broken from outside its own area
stays here**, because a trap that does not load is a trap re-shipped, and the measurements and
the history behind it go to the skill. Do not move a rule out of a digest on the grounds that
the skill already says it - that is precisely the case the digest exists for. New findings in
those six areas go in the skill (`.claude/skills/record-finding` is the house style); a new
*rule* that reaches beyond the area goes in both.

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

**Every control is sized to what it can actually show, and that is what decides where the three columns
fit.** The columns were fixed at 320 and 360 and the boxes stretched to fill them, so "MKV" sat in a
320px dropdown while the Resize box, which needs 291 for `1080p (Full HD) — 1920x1038`, was set to 260
and quietly clipping. Measure each box against every item it can hold and the numbers come out
elsewhere: 197 for the encoder list, 199 for the quality modes, 153 for the containers, 260 for the
colour formats, 323 for Quick Convert's codec list - which was in a 320 column, also clipping.

The readouts were the other half. One line and unbounded, they made *themselves* the column's width
rather than the controls: with a 4K HDR file and borders on, the longest measured 644px, the third
column came to 774, and the `WrapPanel` dropped it onto a line of its own on any window under
**1714px**. They wrap now, at the width of the boxes above them, so a readout and its dropdown share a
right edge - checked by translating both to window coordinates and comparing, not by eye.

Three columns are abreast down to a **1340px** window, from 1714. That was measured by walking the
window width down until the columns stopped sitting level, and the same walk is how each step was
judged: bounding the third column 1714 -> 1565, folding the deinterlace row into its own `WrapPanel`
-> 1505, 115px labels -> 1480, right-sizing the dropdowns -> 1340.

**A column that is wider than what it shows reads as a gap, not as a column.** The middle one was 360
wide for two buttons - Crop's Configure and Trim's Clear - that are hidden unless you are using them,
so a hundred pixels of it sat empty next to a 260px dropdown and the third column looked flung out to
the right while the first two looked crowded. The groups were evenly spaced the whole time; it was
the dead space inside one of them. Both rows are `WrapPanel`s now, so those buttons fold underneath
when they appear instead of reserving room year-round, and the column is 300 like the third.

The label column is 125 rather than 115 because "Grain Synthesis" measures 118 with its margin and was
being clipped by the dropdown beside it. Measure the labels, do not eyeball them: it is three pixels.

**A row label is centred in its cell, and that cell is the control *and* the readout under it** - so
every label in that column sat below the dropdown it names, by half the readout, which on a wrapped
three-line one is most of a row. `Grid.form > TextBlock.rowhead` puts it back on the control's own
centre line.

Where that rule *sits* is the part worth remembering. Written as `TextBlock.field.rowhead` next to the
`.field` style it looks right and does nothing at all: `Grid.form > :is(Control)` further down the file
sets `VerticalAlignment` on every direct child of a form grid, and between two matching styles the
later one wins. The check that caught it was printing the label's runtime `VerticalAlignment` and
`Margin` rather than trusting the selector - both classes were on the control and neither of its own
setters had been applied. The 7px top margin is measured against the rendered rows too; the arithmetic
answer of (32 - 17) / 2 came out two pixels low.

The cost is the old comment's reason for one line: a readout is rewritten on every file load, and a
two-line one reflows the rows beneath it. Three columns on an ordinary window is worth more than that,
which is the whole reason the panel wraps at all. A row carrying a second control - Resize's Configure
button, the grain mode's contextual panel, the deinterlacer's preset and checkbox - is a `WrapPanel`
so that control folds under rather than setting the column's width for everyone.

**Both Video tabs are three columns in a `WrapPanel`, and the wrapping is the
whole responsive behaviour.** They were one six-column `Grid` - two label/control
pairs and a filler - which ran the settings off the bottom of the tab into a
scrollbar while the entire right-hand side of the window sat empty beside them.
Three columns do not fit every window: the rows carry readouts a hundred
characters long, so the set wants around 1700px where the window opens at 1360
and may be dragged down to 1040. A `WrapPanel` drops the third column onto a line
of its own when there is no room for it, which is where those rows were anyway,
and it costs no code-behind and no size handler. Measured: three columns abreast
and no scrollbar at 1948px of form width, wrapping to two lines at 1308 and 988,
and shorter in both of those than the two-column layout it replaced.

Each column is its own `Grid` rather than a slice of one, so its rows are sized
by its own content. Sharing rows across columns is what made Quality sit lower
than the Container above it - that row was being stretched by the Resize row's
readout on the far side of the tab.

The column order is load-bearing: **every row with a readout under it goes in the
last column**, whose control column is `Auto` so the text stays on one line, and
which has the rest of the window to run into. A readout in a middle column draws
over the column beside it. The middle column's control cell is 360 rather than
320 for the same reason in miniature - the Crop row is a 260 dropdown and a
Configure button, which overflowed the old 320 cell and got away with it only
because the filler column it spilled into was empty.

**The geometry rows sit together in the middle column now, right below the trim, at the user's
request** - Crop, then Resize, then Borders, the order the chain runs them in, on both Video tabs,
leaving the last column to Grain Synthesis, Deinterlace and Tone Mapping. What makes readout rows
legal in a middle column is the wrap above: a wrapped readout is as wide as its own column and no
wider, so Resize's and Borders' readouts stay inside the 300 cell instead of drawing over the
column beside them - the "every readout row goes last" rule dates from when readouts were unbounded
and set their column's width. Crop's Configure button folds under its 260 dropdown in a `WrapPanel`,
the 300 cell being narrower than the pair. Verified headless through the real window: on both tabs
the crop, resize, borders and frame-rate boxes share the middle grid in rows 3/4/5/1, the grain box
sits in the last grid at row 0, and both tabs were rendered to PNG and looked at.

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

A session on the desktop cannot capture the screen (on the laptop, launch the app and
screenshot it - see the top of this file), but the UI can still be seen for real without a
display, and this is also the only way on either machine to force a hover or measure a
control: a throwaway console project referencing `Nmkoder.csproj` plus `Avalonia.Headless`
and `Avalonia.Skia` can `AppBuilder.Configure<Nmkoder.App>().UseSkia().UseHeadless(new
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

**Use the dispatch path, and let it create the tag.** A `v*` tag push publishes for real
too, but the dispatch takes the version and `publish` as inputs and tags the commit itself, so
nothing has to be tagged by hand and a local tag can never get ahead of, or out of step with,
what the workflow created. (The dispatch path was born of a Claude Code on the web sandbox
whose git proxy hung up on every tag push and every remote branch deletion, identically and
on every retry - `send-pack: unexpected disconnect while reading sideband packet`. Sessions do
not run there any more, and it stays the route because it is the better one.)

**Say nothing about branches in the report.** Delete a finished local branch if wanted and
leave the remote one alone - the user has asked not to hear about it again: it is the same
sentence every release, about a housekeeping detail they never asked for, and the queue of
merged branches sitting on the remote costs nothing. Never claim a branch *was* deleted when
only the local copy is gone; just say nothing about branches.

The steps:

1. Merge the work into `master`.
2. Bump `<Version>` in `Nmkoder/Nmkoder.csproj`, committed on its own as
   "Bump version to X.Y.Z". The generated notes list commit subjects newest
   first, so this becomes the changelog's first line.
3. Push `master`.
4. Dispatch the workflow with `version=X.Y.Z` and `publish=true`:
   `gh workflow run release.yml --repo jkkma/nmkoder --ref master -f version=X.Y.Z -f publish=true`.
   Leaving `publish` off or false produces a *draft* release instead of a public one.
   `.claude/skills/cut-release` carries the whole procedure with the watch and
   verification commands.

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

**`bin/` is what goes on the launched tool's PATH, so a *folder* there occupies a name a
*binary* wants - and one did, for eighteen releases.** The Quick Convert argument lists were
filed at `bin/ffmpeg/encoderArgs`, which made `bin/ffmpeg` a directory; `bundle-tools.sh`
installs the ffmpeg binary at `bin/ffmpeg`; `cp` refused with "cannot overwrite directory
… with non-directory"; and **every linux-x64 archive from 2.8.25 to 2.8.43 shipped with no
ffmpeg in it.** Confirmed against the published asset rather than inferred - `bin/` in the
2.8.43 tarball holds `ffprobe` and `grav1synth` and no `ffmpeg`. Windows escaped it because
`ffmpeg.exe` does not collide with a folder called `ffmpeg`, and macOS bundles no ffmpeg by
design, so this was linux-x64 alone.

Two things kept it invisible, and both are now closed. `install_binary` ended in a
`find … || true`, so the function's exit status was that of the last command and it reported
success unconditionally - the job summary said `[ok] ffmpeg + ffprobe` over a copy that had
failed. It judges its own artifact now, refusing outright when a directory sits on the target's
name (`cp file dir/name` writes *into* the directory rather than failing, so the check has to
come before the copy as well as after). And the release workflow verifies afterwards that
`bin/ffmpeg` and `bin/ffprobe` are non-empty regular files on the two RIDs meant to carry them,
because the bundler's own report had been wrong for eighteen releases and a second pair of eyes
that looks at the folder is worth more than a third assertion from the script.

The lists live at `bin/encoderArgs/{av1an,ffmpeg}/` from 2.8.44. `encoderArgs` is not a tool
and never will be, which is the property that matters - do not file anything under `bin/`
named after a binary the bundler installs.

**A user was unlikely to notice, which is the third reason it lasted.** The app falls back to
whatever `ffmpeg` is on the system PATH, and a Linux machine that wants this app usually has one
- so the symptom was a version drift nobody could see rather than a failure. Extracting 2.8.44
over an install from that range will fail on `bin/ffmpeg`, the old directory being in the way;
that is the one upgrade worth unpacking fresh.

## The Scoop bucket

`bucket/nmkoder-avalonia.json` makes this repository its own Scoop bucket, so
`scoop bucket add ayylmao https://github.com/jkkma/nmkoder` and then
`scoop install nmkoder-avalonia` is all a Windows user needs. Scoop finds a bucket
by its `bucket/` directory, and one manifest per app is the whole of it - so the bucket
carries whatever else is filed in there later, one manifest each.

**The bucket's name is not a property of this repository** - `scoop bucket add <name> <url>`
takes whatever the user types, and nothing here declares one - so the three places that name it
(this file, the README, and the release notes template in `release.yml`) are documentation and
have to be changed together or not at all. It is spelled `ayylmao` because the bucket is the
user's own and is meant to carry their other apps beside this one; it was `nmkoder-avalonia`
before that, matching the app, which is exactly the assumption a bucket holding more than one
app cannot keep.

**The bucket's name and the app's are different things, and renaming one must not rename the
other.** The collision that mattered was the *app* name and the manifest's own name is what
settles it, so `bucket/nmkoder-avalonia.json`, the `scoop install`/`scoop update` lines, the
persist directory and the `WARN Multiple buckets contain manifest 'nmkoder-avalonia'` warning
all keep that name whatever the bucket is called. The bucket name reaches nothing but the three
sentences above. Anyone who added the bucket under an earlier name - `nmkoder`, then
`nmkoder-avalonia` - is unaffected; re-adding under the new name without `scoop bucket rm <old>`
first is the one way to get the same repo attached twice, and Scoop then prints that same
familiar warning.

**The app is not called `nmkoder`, and must not be renamed to it.** Scoop's community
`extras` bucket already carries a `nmkoder` - n00mkrad's pre-fork WinForms 1.10.0, still
sitting at that version - and a bare `scoop install nmkoder` resolves there rather than
here, printing one `WARN Multiple buckets contain manifest 'nmkoder'` line and then
installing the app this fork replaced. That is how it was first shipped, and the person
who tried it got 1.10.0. `scoop install nmkoder/nmkoder` would have worked, but a name
that needs a prefix to mean what it says is one that will be typed without it.

The rename buys the rest for free: Scoop keys both the app directory and the *persist*
directory on the manifest name, so `nmkoder-avalonia` has its own `data` and `logs`
rather than sharing `~/scoop/persist/nmkoder` with the original's, and the two can be
installed side by side.

**Do not hand-edit the version, the URL or the hash.** The release workflow's
last step rewrites all three with `jq` and commits the result to `master`, from
the win-x64 zip it already has in hand - so hashing costs nothing where Scoop's
own autoupdate would re-download 400-odd MB to reach the same number, and the
manifest is never a release behind. It stands down for a draft, which is a
release nobody can install. The `checkver`/`autoupdate` block is kept anyway, as
the manual fallback for a release that somehow arrives another way; it produces
the same three fields.

Two things in it were read off the published asset rather than assumed, and both
would break every install if they were wrong: `extract_dir` is `Nmkoder`, because
`Compress-Archive -Path publish/Nmkoder` puts the folder itself in the zip, and
`Nmkoder.exe` sits at the root of it. The manifest also validates against
ScoopInstaller/Scoop's own `schema.json`, which is worth re-running after an edit.

**`persist` is `data` and `logs`, and `bin` is deliberately not in it.** Scoop
installs each version into its own directory, so without persistence a
`scoop update` would leave the config, the recent files and the window's geometry
behind in the old one - `Paths.GetRootDir()` resolves next to the exe, and a Scoop
app directory is writable, so that is where they land. `bin` is the opposite case:
it is the bundled toolchain, replaced wholesale by every release, and persisting it
would pin whoever installed once to that release's ffmpeg forever. The manifest's
`notes` say so, since dropping a tool into `bin/` is exactly what the README tells
portable users to do.

Submitting to the community `Extras` bucket instead would mean a PR against
`ScoopInstaller/Extras`, a separate repository with its own review queue; the own-bucket
route needs nobody's approval and moves with every release.

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
   `bin/iso639.csv` and no `bin/encoderArgs/av1an`, the only symptom being an
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

**Every RID publishes as one executable, win-x64 included, and the third
consequence of the flag is the price of that.** Nobody sees it until a disk
fills: the extraction in (2) goes to `%TEMP%\.net\Nmkoder\{bundle-id}`, the id is
a content hash so it changes with **every build**, and .NET deletes none of them
ever. So a machine collects one copy of the runtime and the App SDK - about 140
MB - for each release it has run. Measured on a user's machine ten days after
installing: 40 folders, 5.22 GB.

That is what `Program.CleanupBundleExtractions` exists to pay, and the pairing is
the point: **the bundle is affordable only because something reaps it**, so do
not treat that method as housekeeping and do not weaken it to keep a folder
"just in case". It deletes every extraction but the live one on each launch, so
the steady state is one folder rather than one per release and an upgrade
collects its predecessor's. win-x64 did ship as a folder of 224 loose DLLs for a
few releases, between the report above and the cleanup landing; the shape to
recognise is that the fix for the disk filling was never the loose DLLs, which
cost the flag's other two bugs a place to hide and bought a zip nobody wanted.

The extraction is the *whole* bundle on this one RID and the native libraries
alone on the others - measured on linux-x64 bundles of the same build, 123 MB
with the flag against 49 MB without it. Which is also why the flag cannot simply
be waived: the App SDK's check answers to
`WindowsAppSDKSingleFileVerifyConfiguration=false`, but the redirection it is
guarding is real, and an App SDK told to find its native DLLs beside the exe will
not find them there.

**That check's last clause looks unmet by this csproj and is not.** It warns that
a single-file build must set `MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY` before
program entry, which nothing here does by hand - because
`WindowsAppSdkUndockedRegFreeWinRTInitialize` defaults to true wherever
`WindowsAppSDKSelfContained` is set, and that is what generates the initializer
which sets it. The default is keyed off a *different* property in a *different*
package (`Microsoft.WindowsAppSDK.Foundation`'s
`UndockedRegFreeWinRTCommon.targets`), so reading only the file that carries the
warning says the opposite. Do not "fix" this by setting the variable in `Main` -
that runs after the initializer, and the App SDK has already read it.

Two things follow from that and are now load-bearing rather than vestigial: the
csproj's `IncludeAllContentForSelfExtract`, which the shipped build sets, and
`CopyBinFilesToPublishDir`, which puts back what it sweeps up. Both looked
removable while win-x64 was multi-file. Neither is.

`Program.CleanupBundleExtractions` does not ask whether *this* build is bundled
before looking - a non-bundled build is precisely the one that has stale folders
and no live one, which covers both anyone's own multi-file publish and the
releases that shipped that way. `AppContext.BaseDirectory` is
read there on purpose, being the extraction folder itself and so the authority on
where the root is; that is the one question it answers well, and it is the exact
opposite of the rule in (2), so it carries a comment saying so. A folder in use by
another build is skipped rather than deleted - a live instance holds its libraries
open, so the loaded files would refuse to go while the rest went, stripping it of
whatever it had not loaded yet. Verified by running it: a real linux-x64 bundle
published with the flag, three planted folders deleted, its own kept, and one held
under `flock` - which is what .NET's `FileShare.None` takes on Unix - kept and
named in the log.

And verified again across a version step, which is the case the steady state
rests on rather than the planted one: two bundles of the same source differing
only in `-p:Version` extract to *different* ids, and running the 2.8.39 one
freed the 121.7 MB the 2.8.38 one had left, keeping its own. Two publishes at
the same version share an id and reuse the folder, which is worth knowing before
reading a check that says nothing was freed - the id follows the content, so
"nothing to reap" and "the reap did not run" look alike from the outside.

**The other RIDs were not covered by any of this for as long as they have been
bundled, and finding the live folder is what fixes it.** Their bundles extract
only native libraries, so the flag is off, so `AppContext.BaseDirectory` is the
exe's own directory - which is the branch that told the method it was *not*
running from a bundle. It then fell through to a root guessed from `%TEMP%`, and
that guess is Windows-only, so a Linux or macOS machine collected the 49 MB per
release measured above and nothing ever touched it.

`GetLiveExtractionDir` asks **`NATIVE_DLL_SEARCH_DIRECTORIES`** first, which is
where the host tells the runtime to look for native libraries and is therefore
the extraction folder itself. Measured across all three publish shapes: it names
the extraction folder both with the flag and without it, where
`AppContext.BaseDirectory` names it only with, and both come back as the exe's
own directory for a build that is not bundled at all. So it answers on every RID,
and the root is its parent rather than a path composed from a guess.
`BaseDirectory` stays behind it as the documented half of the same question.

**Off Windows, `IsExtractionInUse` is not merely weak - it is blind, and it says
so where it is deleting is safe.** `FileShare.None` is an advisory `flock` on
Unix and the dynamic loader takes no such lock: measured, a `.so` another process
has mapped opens cleanly *and deletes without complaint*, so every folder reads
as free. Unlinking a mapped file does not disturb what that process has already
loaded, which is why this is quiet rather than a crash - it strips it of whatever
it had not got to yet. `OtherInstanceRunning` is the guard instead, asked once of
the process list: any second instance stands the whole sweep down, since which
folder is theirs cannot be told from here. Conservative on purpose, and it costs
nothing that matters - the sweep runs at every launch, so it gets another chance.

The Windows fallback root stays Windows-only, and now for a reason rather than
for want of the path: off Windows the sweep only ever runs where this process's
own folder is known and everything else is a sibling of it, and a build that is
not bundled has no such anchor. That layout was measured too, and it is not what
the old comment here guessed - there is no user name in the middle. The base is
`DOTNET_BUNDLE_EXTRACT_BASE_DIR` where it is set, and otherwise `~/.net`, with
the home directory read out of the password database rather than the
environment: unsetting `HOME` does not move it and `TMPDIR` has no bearing on it.
Note that the env var replaces the base *including* the `.net` segment -
`{var}/Nmkoder/{id}`, not `{var}/.net/Nmkoder/{id}`.

Verified by running it, on real natives-only bundles of two versions: a 2.9.2
build reaped its 2.9.1 predecessor's 49 MB extraction along with two planted
folders and kept its own, where before this it did nothing at all; with a live
process named `Nmkoder` beside it the same build kept everything and said so in
the log, and swept as soon as that process was gone. The win-x64 shape was
re-checked against the same tree - an `IncludeAllContentForSelfExtract` bundle
still finds its own folder, still reaps the rest, and still writes `data` and
`logs` beside the exe.

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
guessed: one size format on the stats line, `size=%8.0fKiB time=`, and one
bitrate format, `bitrate=%6.1fkbits/s`. A 1.9 GB stream still printed KiB, so the
larger prefixes in the regexes are defensive rather than something you can
produce. They are not scaled alike, which is the part to be careful with: a size
is binary, matching its KiB/MiB spelling, while a bitrate is ffmpeg's own
division by 1000 and so decimal.

**This file used to say there was exactly one `%8.0fKiB` format in the binary,
and against `N-126264-g007cd1fd43-20260825` (toolchain 2.8.78) there are two.**
The second is `ffmpeg_enc.c`'s `-vstats` file format, `s_size= %8.0fKiB time=
%0.3f br= %7.1fkbits/s avg_br= %7.1fkbits/s` - and it contains the exact `size= `
token `GetStreamSizeBytes` keys on, so it is the shape that would fool the parser
if it ever reached it. It does not: it is written to a *file* rather than to
stderr, and the app never passes `-vstats` (grepped, zero hits). Recorded because
"exactly one" is the sort of enumeration that gets relied on, and the reason this
one is safe is a property of the app rather than of the format.

**The stats line has also gained a trailing `elapsed=` field the readout does not
rename.** It now ends `speed= 176x elapsed=0:00:00.01`, and
`FormatUtils.BeautifyFfmpegStats` has entries for `frame=`, `fps=`, `q=`, `time=`,
`speed=`, `bitrate=`, `Lsize=` and `size=` and none for `elapsed=`, so the app's
readout ends with a raw `elapsed=0:00:00.01` among renamed fields. Cosmetic, and
**not new to this refresh** - the user's own Scoop ffmpeg (gyan.dev 9.0.1) prints
it too, so it has been showing for a while and was simply never written down. The
size regex and the `frame=`/`size=` gate are both unaffected.

`Lsize=` never *starts* a line - it only appears after `frame=`, and an audio-only
run's final line begins `size=`. Measured across a video stream copy, an audio
stream copy and an audio encode, which is why `FfmpegOutputHandler`'s gate on
`frame=`/`size=` covers both and does not need widening. That one looks like a
gap and is not.

**ffprobe answers "what frame rate is this" twice, and for every NTSC tape capture the two
differ by a factor of two.** `r_frame_rate` is the lowest rate all the timestamps can be expressed
in, which for a field-coded MPEG-2 stream is the *field* rate; `avg_frame_rate` is the frame count
over the duration. Measured on a 720x480 capture: `r_frame_rate=60000/1001` against
`avg_frame_rate=30000/1001`, with every frame reporting `duration=3003`, `interlaced_frame=1` and
`repeat_pict=0` - ordinary 29.97 interlaced content, no telecine. `FfmpegCommands.GetFramerate`
read the first of those, so `VideoStream.Rate` was the field rate, and it is the number every rate
in this app measures itself against: a bob deinterlacer doubled it again and told mkvmerge
`--default-duration 0:120000/1001fps` over a stream that is 59.94, which is half speed with the
audio ending at 71% of the picture. It reads `avg_frame_rate` now and keeps `r_frame_rate` behind
it, that one being what answers when ffprobe cannot measure the other (`0/0` on a stream with no
duration, some piped inputs).

What made it invisible is that **the two agree on every file that is not field-coded**, which is
almost everything anyone loads - a modern download, an MKV remux, an mp4 off a phone all give one
number twice. So the bug needed a source class rather than a source to show up, and inside that
class it needed the deinterlacer to be on before the doubling made it visible. The one line that
now says which number was used is in `GetFramerate` itself, logged only where the two disagree.

Measured against the bundled BtbN build `N-126122-gca821e458a-20260813` (toolchain 2.8.66), and
the control is the same encode of a clean CFR interlaced fixture where `r_frame_rate ==
avg_frame_rate`: unchanged before and after, which is what says the change is confined to the
sources that state a field rate.

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

**ffprobe's diagnostics go to the same stream as its answer, so a value asked for with `nokey=1`
cannot be told from a complaint.** `AvProcess.RunFfprobe` hands back both together, and with
`-of default=noprint_wrappers=1:nokey=1` the answer is a bare token with nothing marking it - so
"the first non-empty line" is the *warning* whenever there is one, and any guard on the value's
shape then rejects it and the caller silently gets nothing. Ask for `key=value` and match on the
`key=` prefix; one call can carry several entries, which is fewer ffprobe runs as well.

Both of the app's `nokey=1` call sites were checked. `Av1anSceneDetect.GetDurationSecondsAsync`
is safe, and **only** because its `LogLevel` is `quiet` - nothing else about it defends against
this. `CadenceRepair`'s colour restatement was at `error` and shipped the failure; what it cost,
and the capture that reproduces it, is under `Repairing a padded capture`. A third such parse
written at `error` would be the same bug again, which is why this is here rather than only there.

## The file list and the track list

**`TrackList.SetAsMainFile` empties the stream list, and every caller has to say what it wants of
that.** It is the one place a file becomes *the loaded file* - it scans the file, points
`TrackList.current` at it, rewrites the format label, resets the per-track audio configuration and
re-inits both encode tabs - and in the middle of all that it clears `TrackList.Items`, because those
streams belong to the file being replaced. Two bugs came out of that clear in one session, and they
are the same bug twice: **something added streams and then something cleared them.**

**Order, first.** `AddTracksFromFile_Click` - the Load Tracks button, and the double-click that calls
it - was written as add-then-promote:

```csharp
await TrackList.AddStreamsToList(entry.File, entry.RowBrush, true);
if (TrackList.current == null) await TrackList.SetAsMainFile(entry);
```

so the first iteration put the streams in and wiped them a moment later. **The three other call sites
were all promote-then-add** (`FileList.HandleFiles`, `ApplyFileListMode`, `RunTask`), which is the
order to keep. Only the first iteration was ever wrong, `current` being non-null from then on, which
is what made it look like a selection bug. The way in is ordinary: dropping several files at once in
Muxing Mode leaves `current` null, `HandleFiles` setting a main file only on its `Items.Count == 1`
path, so the button landed with exactly the state the bug needed.

Two consequences beyond the missing tracks, neither visible from reading it. `AddStreamsToList`
unchecks a second video stream through its own `alreadyHasVidStream` test, so with the first file's
video gone **the *second* file's video became the checked one** and the encode would have used the
wrong video. And the file was scanned twice per press - `AddStreamsToList` initializing it, then
`SetAsMainFile` initializing it again. Measured through the real window, four fixtures of 4, 2, 3 and
1 streams: 10 items with the fix against 6 without, the first file contributing 4 against none.

**Whether to clear at all, second.** `RefreshFileListUi` repairs the case where the loaded file is
taken *out* of the file list, by promoting `FileList.Items[0]`. That path is the one caller that
must **not** clear, and it said so already: `TrackList.Refresh` has just pruned exactly the departed
file's streams and left every other file's alone, and `ClearCurrentFile` is called with its own
`clearStreamList` false to keep them. `SetAsMainFile` then cleared regardless - so removing one file
of several threw away the tracks of the ones that remained. It takes a `clearStreamList` parameter
now, defaulting to the old behaviour so no other caller moves, and the repair passes false.

That leaves the case where the promoted file has no streams in the list at all, which is the ordinary
one - a single file loaded and removed. The repair loads its tracks, because everything else on screen
already says that file is the loaded one. **Left empty it was not merely untidy**: `GetMappedStreams`
returned nothing, `GetMapArgs` returned `""`, and the Run button still offered an encode with no
`-map` arguments in it.

**The obvious one-line fix here is wrong, which is why the shape matters.** Clearing and re-adding
just the promoted file would fix the empty case and silently lose every *other* still-loaded file's
tracks - and their checked states and order with them. Measured across three shapes (one file's tracks
loaded, two, three): 53 checks pass, where the old code fails 21 of them, and the three-file shape is
the one that catches a fix that re-adds rather than preserves - it keeps five rows where a re-adding
fix leaves two, with both files' ticks and ordering byte-identical across the promotion.

Compare stream entries to files by `MediaFile.ImportPath`, not by reference: `MediaFile` has no
`Equals` of its own, and `Refresh`'s own pruning goes by path for the same reason.

**Two states next door to this are pre-existing and were left alone**, both measured on the fixed and
unfixed builds alike, so neither is fallout from the above. A promoted file's *video* can be unticked -
`alreadyHasVidStream` unchecked it when it was added behind another file's video, and the promotion
preserves ticks rather than reconsidering them - so the loaded file can end up mapping audio only.
And a multi-file drop over an already-loaded file leaves the old file's rows in the list:
`HandleFiles` calls `ClearCurrentFile` (whose `clearStreamList` defaults false) and `LoadFiles`, which
reaches `RefreshFileListUi` but never `TrackList.Refresh`, so nothing prunes them.

## The AV1AN tab

The tab that drives av1an: `Av1an.Run` builds the command, `Av1anUi` the tab, `Av1anOutputHandler`
the progress, `Av1anMemory` the headroom estimate, `Av1anSceneDetect` the parallel pre-pass. It
also holds the frame geometry **both** encode tabs share - `CropConfig`, `ResizeConfig`,
`BorderConfig`, resolved once into an `Av1anFrame`.

**The full record is the `av1an-tab` skill**, which loads on any task in this area. Read it before
changing anything here; what follows is only what has to hold whatever you are doing.

- **av1an rejects an entire command over one unrecognised flag, and so do the encoders it drives.**
  Never add one unguarded: `AvProcess.Av1anSupportsFlag` reads av1an's own `--help`, and
  `AvProcess.EncoderKnowsFlagOrIsUnknown` the encoder's.
- **`CodecUtils.GetNoPromptArg` serves this tab as well as Quick Convert, and narrowing it to the
  direct path would break aom and vpx encodes here.** av1an carries no prompt-suppression flag of
  its own, so `--disable-warning-prompt` has to ride inside its `-v "…"` string or a `min-q` within
  8 of `max-q` - which `AomAv1.json` and `Vpx.json` offer - kills every chunk.
- **This project ships the PSY line of SVT-AV1 or nothing.** `bundle-tools.sh` takes `SvtAv1EncApp`
  only from `juliobbv-p/svt-av1-hdr`. Both fallbacks that used to sit behind it substituted a
  mainline binary under the same filename with nothing saying so; a release with no PSY build is a
  visible skip instead. Do not restore either.
- **Nothing on the AV1AN Video tab is saved between sessions**, and not writing them matters as much
  as not reading them. Keys from before that are still sitting in existing config files - do not
  wire one back up on the strength of finding it there.
- **The progress bar is measured from av1an's temp folder** (`scenes.json`, `done.json`), never by
  parsing av1an's stderr, and it counts **frames** rather than chunks. Everything it used to parse
  rotted away underneath it, silently, one release at a time.
- **Read av1an's temp folder, never write into it while av1an is running.** The progress bar only
  reads. The encode-settings attachment used to delete and replace `audio.mkv` there, racing av1an's
  own mux for it - which lost on fast encodes and could have cost the audio track. `done.json`'s
  `audio_done` is not a signal that av1an has finished with that file, and nothing outside av1an is.
  `Av1an.AttachEncodeSettings` amends the finished output instead.
- **The geometry order is crop, then resize or the anamorphic de-squeeze, then borders last** - a
  scaler run over a hard black edge rings. Frames come out even on both axes, and `CropConfig` is
  the one place the rectangle is worked out, so the readout, the frame the resize is measured
  against and the filter that runs cannot disagree.
- **av1an's target-quality probes never see the `-f` filters**, so a filtered encode's quantizer
  search settles on a value measured against the unfiltered source. Anything put back in front of
  av1an changes that note and has to be taken out of it in the same change.
- **The SVT-AV1 content presets are written for the PSY line and deliberately do not compensate
  for mainline.** A value carried only to make them half-work there is a no-op on every build they
  are actually for. `enable-qm` was one and has been removed; do not add one back.
- `Av1an.GetDefaultThreadPlan` returns the worker count and the thread count together, because what
  has to track the machine is their product. The `0.4` in it is a `double` and must stay one.

## The Advanced tab, on both encode tabs

**The per-encoder argument grid is shared and the arguments in it are not.** `MainWindow.EncoderArgs.cs`
holds one implementation of the grouping into category tabs, the preset row and its confirmation, the
right-click help and the save-as-you-type; each tab hands it an `ArgSection` naming its own rows, its own
encoder, its own config key and its own spelling. Two copies would have drifted the moment either tab was
touched, which is the whole reason it moved out of the AV1AN partial rather than being copied into the
Quick Convert one.

**The grid has no per-row tooltip and should not get one back.** It carried the argument's spelling and
the whole of its description - the same description the row is already showing in its third column - and
a tooltip is drawn *over* the rows under the pointer, so reading down the list covered the next few rows
each time it appeared, on a control whose entire job is to be scanned. The full text is a right-click
away and the heading above the grid says so. The preset buttons' own tooltips are a different thing and
stay: they describe what clicking one does, which is nowhere on screen.

**The right-click window sizes its example values to the values.** They were in a fixed 110px column, so
every example that is a path arrived cut off - `/path/to/RPU.bin` as `/path/to/RPU.` - and a truncated
example is unreadable in a way a truncated sentence is not, the value being the thing to copy. The column
is `Auto` now, in a `Grid.IsSharedSizeScope` so the explanations still line up: each row is its own Grid
inside the `ItemsControl`, so an Auto column on its own measures that row alone and the second column
starts somewhere different on every line. It is capped at 340 because x265's `master-display` examples are
69 characters and would take the window; those wrap inside the chip instead.

The lists are filed apart - `bin/encoderArgs/av1an` against `bin/encoderArgs/ffmpeg` - because they are
**different vocabularies rather than different files**: the av1an folder names the CLI parameters the
standalone binaries take, the ffmpeg folder what the *wrapper* takes, which for VP9 and NVENC is not the
same set of names at all. **The folder follows the encoder rather than the tab** now that Quick Convert
drives both kinds - `EncoderArgs.FolderFor` sends an `IBinaryEncoder` to the av1an lists whichever tab is
asking, so `DirectX264` reads `X264.json` while NVENC still reads its ffmpeg list, and the ffmpeg folder's
x264/x265/SVT/VPX/AOM lists remain the CRF ladder's, which deliberately runs ffmpeg's encoders. Both
folders are in the release workflow's post-publish check, for the reason the csproj gives.

**Four of ffmpeg's encoders take their whole parameter table through one option and the rest do not.**
`-x264-params`, `-x265-params`, `-svtav1-params` and `-aom-params` each hold a `:`-separated `key=value`
list; libvpx-vp9 has no such option - measured against the bundled build, there is no `-vpx-params` - and
neither does NVENC, so those two get one `-key value` AVOption per filled-in row. `FfmpegEncoderArgs` is
the one place that split is stated, and it is why `LibVpx.json` names ffmpeg's own spellings: half of
vpxenc's long options have no AVOption behind them.

**A second `-x265-params` replaces the first outright rather than adding to it.** Measured, and true of the
other three. `Libx265` therefore merges `pass=1`/`pass=2` and `lossless=1` into the same list the grid
builds instead of emitting an option beside it - before the grid existed those two could not both appear
(two-pass implies a bitrate, lossless implies CRF), so writing them as separate options got away with it,
and the grid is the third one that would not have.

**What an encoder does with a parameter it does not know is three different things.** x264, x265 and
SVT-AV1 print one warning line and encode anyway, so a setting goes missing in silence; libaom refuses the
encode naming the parameter; and an unknown AVOption never reaches an encoder at all, ffmpeg dying while
parsing its own arguments. The Argument column is read-only, so a name that is not in the shipped list
cannot be typed - what that behaviour governs is a list that has drifted from the ffmpeg being run. It is
also why every row of every list here was passed to the real binary and observed to be accepted rather
than read out of documentation: for three encoders out of five, "it encoded fine" is not evidence.

**`tune` and `profile` are not x264 or x265 *parameters*.** Both are applied by the library's own
preset/profile entry points rather than by its parameter parser, so `-x264-params tune=grain` comes back as
"Error parsing option" and `-x265-params tune=grain` as "Unknown option". They exist only as ffmpeg
AVOptions, which a params-style encoder's grid cannot emit - so neither list has a row for them, and that
is a limit rather than an oversight.

**The SVT-AV1 split runs between the encode tabs and the CRF ladder now, not between the tabs.**
`bundle-tools.sh` fetches svt-av1-hdr as `SvtAv1EncApp`, and both encode tabs drive that binary - the
AV1AN tab through av1an, Quick Convert by launching it. The `libsvtav1` *inside* BtbN's ffmpeg - a
mainline library the bundler does not choose - is the CRF ladder's SVT, and the ladder's preset
translation is what still reads `LibSvtAv1.json`: measured against that build (which reports `SVT-AV1
Encoder Lib v4.1.0`), `hbd-mds`, `luminance-qp-bias`, `chroma-qm-min`, `ac-bias`, `max-tx-size`,
`variance-boost-curve` and `adaptive-film-grain` are all there, while `noise-adaptive-filtering`,
`tx-bias`, `cdef-scaling`, `kf-tf-strength`, `sharp-tx`, `alt-ssim-tuning`, `fgs-table` and the whole
`noise*` family are not. So `SvtAv1.json` and `LibSvtAv1.json` are still two files on purpose, they
simply have different readers than they used to: Quick Convert's grid reads the PSY list now, because
the PSY binary is what runs - guarded by the same runtime mainline check as the AV1AN tab
(`QuickConvertUi.GetUnsupportedAdvancedArgsProblem`), since the binary on a user's machine is still a
build-time accident.

**The grain collision on Quick Convert is the AV1AN tab's now, decided by the encoder rather than by
ffmpeg's option plumbing.** While this tab drove the libraries, the row's grain arguments and the
grid's copies met inside `-svtav1-params`/`-aom-params`, and which won was AVDictionary behaviour;
with the binaries it is SVT's own three-way precedence - fgs-table over noise over film-grain - and
`GrainGridChecks` states it once for both tabs. aomenc's list has no grain rows, so only SVT can
collide. The libaom measurements from the library era (the grid's `-aom-params` beating the AVOption,
`enable-dnl-denoising=0` changing the output again) still describe `LibAomAv1`, which the CRF ladder
runs - they are just no longer reachable from a grid, the ladder having no grain control.

**`LibSvtAv1` used to send a `-rc vbr` that did nothing, and `Gif` used to let the palette size go
below what its own filter accepts.** Neither came from this tab; both were found by running its
argument lists through the real command. `ffmpeg -h encoder=libsvtav1` lists `preset`, `crf` and `qp`
and no `rc` at all, so that name matched another encoder's option class and was discarded, every
bitrate encode logging "Codec AVOption rc … has not been used for any stream" as it went - `-b:v`
alone is what selects VBR, and removing it left the output byte-identical, which is how a flag that
never arrived is supposed to behave. `Gif.QMin` was 0 where the chain needs 3: `palettegen` refuses
0 and 1 as "out of range [2 - 256]", and 2 parses and then fails to build the graph, because this
chain leaves `reserve_transparent` at its default and "max_colors=2 is only allowed without reserving
a transparent color slot". The spinner took its floor from `QMin`, so all three were reachable.

**The params blob is quoted and the AVOption values mostly are not.** x265's `master-display` is written
`G(13250,34500)B(...)...`, and parentheses belong to the shell on Linux and macOS, so the whole `:`-joined
list goes through `Shell.WrapArg`; an AVOption value is quoted only where it holds something the shell
would otherwise read. A value containing a *space* cannot survive either path, the grid handing the
encoders one space-separated `key=value` string - the same limit `BuildCli` has always had on the AV1AN
side, and no parameter either list offers takes one.

**A *backslash* does not survive the AV1AN side either, and that is a second limit rather than the same
one.** av1an's `-v "…"` string is unescaped as well as split, so a typed `C:\path\to\rpu.bin` reaches
the binary as `C:pathtorpu.bin` - measured, and what it cost the grain table's own path is under
`Grain synthesis`. The grid is **deliberately** not escaped for it: `FormatUtils.GetAv1anArgPath` exists
and is applied where this app knows it is handling a path, and the grid is exactly where it does not -
doubling every backslash in every value would corrupt the ones that are data rather than separators.
So a path-valued parameter typed here is a known gap on the AV1AN side, and works on Quick Convert,
which launches the binary itself. Do not "fix" it by running the whole grid through that helper.

**SVT-AV1's content presets are both tabs' now, and the x264/x265 sets are the CRF ladder's - they do
not carry to the Direct pair, and that is measured rather than forgotten.** `EncoderArgPresets` is keyed
by encoder name and the preset row hides itself for a name it does not know, so an encoder without a
considered set simply has no row rather than a bad one. `DirectSvtAv1` shares the AV1AN tab's SVT set
outright - same binary, same `SvtAv1.json` rows, and `Av1anEncoderName` answers for it too, so applying
a preset drops what a mainline binary lacks with the same named log line. The x264/x265 sets stay where
the libraries are: written as library parameters, four of their values are boolean-only flags on the
CLI binaries - x264 has `--no-dct-decimate` and refuses `--dct-decimate 0` outright, and x265's
`--sao`, `--cutree` and `--rc-grain` take no value, so `--sao 0` strands the 0 as a stray argument that
**kills the encode**. This file used to say x265 "only warns about" it, and the warning is still the only
thing it prints - `x265 [warning]: extra unused command arguments given <0>` - but measured against
`4.3+1-e9b8812` in the 2.8.78 bundle it then **exits 1 having written nothing at all**, where `--no-sao`
on the same fixture is rc=0 and 2881 bytes. Same for `--cutree 0` and `--rc-grain 0`. The warning text
is what made the old reading look right: it says "warning", and the exit code is the only place the
difference shows. A value grid cannot express any of that, so a carried-over preset
would quietly apply the opposite of its loudest entries; a Direct x264/x265 set wants writing against
the CLI vocabulary and measuring, not mapping. The library sets were verified value by value against
the library inside the bundled ffmpeg, which the CRF ladder still runs.

**Both encoders' defaults move with the speed preset, which is what makes "a deliberate departure from
the default" a question you cannot answer without saying which preset.** Measured: x265 at `medium` runs
no rate-distortion quantisation at all, where `slower` reports `rdoq=2 psy-rdoq=1.00` - so `rdoq-level=1`
is a real setting on one and near-inert on the other. x264 at `slow` already has `trellis=2`, where
`medium` has 1. Each set was read against the preset its own tab opens on, `slow` for x264 and `medium`
for x265, which is where `trellis=2` came out of the film preset: from `slow` upwards it says nothing,
and below it, it partly undoes the speed the user just asked for.

**x264's `chroma-qp-offset` is not the number that reaches the stream.** The encoder applies an offset of
its own while the psychovisual optimisations are on, so the effective value is -2 with nothing set, -4
with a typed -2 and -6 with a typed -4 - read out of the SEI x264 writes into the bitstream, which is the
only place the effective value appears. The grid row is still a departure and the description is still
right about the parameter; it is the arithmetic that surprises.

**The lists were checked by running them, and the check is worth repeating rather than re-deriving.**
Every row of all seven was passed to the real binary and observed to be accepted; then every number a
row states - each end of its range, and its default - was passed as well, which is what caught SVT-AV1's
`lookahead` offering the -1 that its own parser refuses. For the AVOption encoders the stated range and
default were compared against the table ffmpeg prints for itself. Two traps in doing that again: ffmpeg
reports a boolean default as `false`/`true` where the rows state the `0`/`1` a user types, and libvpx's
AVOptions default to a sentinel `-1` meaning "unset" where the rows state the *effective* default - so
both look like mismatches and are not. Settle the second by encoding with the row blank against the row
set to its stated default and comparing the files, which must be **IVF or another container without a
random UID**: WebM writes a fresh SegmentUID per mux, so two identical encodes differ and every row
reads as broken.

**That check was re-run against the 2.8.78 toolchain, and what fails is the rows' own stated values
rather than the parameters.** 459 values across 152 rows and five CLI binaries; 7 values across 5 rows
were refused, three of them worth knowing about. **All three of those rows have since been corrected**,
and they are three applications of one rule: **a row states what the parser accepts, and a "leave it
blank" behaviour is said in words rather than offered as a value.**

- **`SvtAv1.json`'s `lookahead` offered the `-1` its own parser refuses, and it was the row's first
  example.** `SvtAv1EncApp --lookahead -1` answers `Error: Invalid parameter 'lookahead' with value
  '-1'` and writes a 32-byte stub, where 0, 1, 60 and 120 all encode and 121 errors correctly. The
  binary's own `--help` says `default is -1 [-1: auto, 0-120]`, so the row was faithfully copying a lie
  the binary tells about itself - which is exactly why reading the help is not the check and running it
  is. The first list check caught this and the row was not corrected then; it now reads `0-120 (blank
  lets the encoder choose)`, the `-1` example is gone, and the long description carries the trap.
- **`AomAv1.json`'s `tune` row named five values this aomenc cannot do.** `vmaf`, `vmaf_neg`,
  `vmaf_with_preprocessing` and `vmaf_without_preprocessing` fail with `Error: Tried to set control 24`
  and the hint `try to set -DCONFIG_TUNE_VMAF=1 at the time CMake is run`; `butteraugli` fails the same
  way but with `-DCONFIG_TUNE_BUTTERAUGLI=1` - this file used to give all five the VMAF hint. The MSYS2
  `mingw-w64-x86_64-aom` the bundler installs is built without either. libaom *refuses* an unusable
  parameter rather than warning, so this is a failed encode and not a silent drop. **The tenth value is
  the one that does drop silently: `vmaf_saliency_map` is accepted, exits 0, and gives output
  byte-identical to `--tune=psnr`.** So of the ten `--help` lists, four work (`psnr`, `ssim`, `iq`,
  `ssimulacra2`), five are refused and one is inert; the row now names those four and nothing else, and
  its details say why the other six are not offered. `psnr` being the default is measured rather than
  assumed - an encode with no `--tune` is byte-identical to `--tune=psnr`, where `--tune=ssim` differs.
- **`X265.json`'s `ref` row stated 1-16 and this x265 opens on 1-8.** 9 and above give `x265 [error]:
  x265_encoder_open() failed for Enc`, rc=3 and no output, preceded by `level N detected, but
  NumPocTotalCurr (total references) is non-compliant`. "Independently of frame size" is now measured
  rather than asserted: the 8/9 boundary is identical at 320x240, 640x480 and 1920x1080, and only the
  level x265 derives moves with the picture (2.1, 3.1, 5 at `--ref 9`). **And naming a high level is not
  the workaround this file and the row both called it.** `--ref 16 --level-idc 6.2` does open, which is
  all the earlier note checked - but x265 then prints `Lowering max references to 7 to meet
  numPocTotalCurr requirement`, so it delivers *fewer* references than a plain `--ref 8`. Measured
  identically through `libx265`'s `-x265-params`, so `av1an/X265.json` and `ffmpeg/Libx265.json` carried
  the same wrong clause and both now say 1-8, and that a named level clamps to 7.

**Two more rows were inside that count of five and were never named; the class they belong to is the
same rule from the other end - a `(default <word>)` parenthetical whose word is not a value.**
`X264.json`'s `level` said `(default auto)`, its `crf-max` said `(default off)`, and `X265.json`'s `tune`
said `(default none)`; typing any of the three kills the encode (`x264 [error]: invalid argument: level
= auto`, the same for `crf-max = off`, and `x265 [error]: preset or tune unrecognized`). Milder than
`lookahead`, which offered its bad value as the first example, and the same defect underneath: the value
column is free text, so a bare word standing where a value goes will be typed. All three now say what
blank does instead. `X265.json`'s `rdoq-level` is deliberately left alone - its `(default follows rd; on
at rd 4-6)` is a phrase rather than a bare token, and nobody types "follows".

**Two things a re-run of this sweep reports as refusals which are the harness rather than the rows, and
must not be re-diagnosed as row faults.** SVT-AV1's `qm-min`/`qm-max` and `chroma-qm-min`/`chroma-qm-max`
are *pairs* the binary range-checks against each other (`Svt[error]: Min quant matrix level must not
greater than max quant matrix level`), so passing one value at a time leaves one end of each row's stated
0-15 unreachable while its partner sits at its own default - `--qm-min 0 --qm-max 0` and `--qm-min 15
--qm-max 15` both encode, so the rows are right. And a regex reading "each end of its range" out of
`rdoq-level`'s prose picks up the `4-6` that belongs to the **`rd`** parameter beside it; that row's own
range is 0-2 and all three values encode.

Measured against the 2.8.78 bundle - `SVT-AV1-HDR v4.1.0-20-g0bed4090b`, `AV1 Encoder v3.14.1`, x265
`4.3+1-e9b8812`, x264 `0.165.3222M`, vpxenc `v1.15.2-151-gd98e70839`. x264 came back 100/100 clean and
vpxenc 64/64. The re-check after the corrections was 569 runs over the same five binaries: 563 accepted,
6 refused, all six of them the two artifact classes above and none a row fault. **Not verified**: the
original "7 values across 5 rows" was not reproduced exactly, the extractor used for the re-check being
more liberal (583 candidate values against 459), so the two unnamed rows are identified by class rather
than out of the original run's own record; and the values a row enumerates in prose rather than in its
examples - x265's six tunes, x264's `1b` - were run by hand rather than swept, all of them accepted.

**One latent trap sits behind the same lists.** An unknown `-svtav1-params` key logs `[libsvtav1] Error
parsing option <key>: <val>.` and encodes anyway at rc=0 - consistent with the three-way split above -
but that text contains `"Error "`, which `FfmpegOutputHandler.LooksLikeTrouble` matches. It is not
reachable today, `GetApplicablePresetPairs` filtering against `LibSvtAv1.json` and all 33 rows being
accepted by v4.2.0, so this is recorded as what would surface if a row ever went stale rather than as a
present fault.

**Quick Convert has no custom-argument boxes and is not meant to.** There were two, one for each side of
the ffmpeg command, kept when this tab was ported because the AV1AN tab has its pair on the Av1an Options
tab and this one has no such tab to move them to. They are gone at the user's request, and the removal
went all the way through: `QuickConvert.Run` no longer splices arbitrary text into either end of the
command, `MainWindow` no longer exposes `CustomArgsInBox`/`CustomArgsOutBox`, and `ResetSettingsOnNewFile`
lost the two entries that existed only to clear them.

Nothing else on the tab reaches ffmpeg's input side, so that capability is not hiding elsewhere - the
Custom Video Filters grid below is output-side and filters only.
Existing config files still carry the `EncCustomArgsIn`/`EncCustomArgsOut` keys and a `ResetCustomInArgs`
entry in `ResetSettingsList`; nothing reads them, `ResetSettingsOnNewFile.Load` logs the unknown property
once behind the debug flag, and its next `Save` writes the list back without them.

**The AV1AN tab's own pair went the same way later, also at the user's request.** The Av1an Options tab
is down to the settings av1an itself is driven by - split, chunk and concat method, chunk order,
detection slices, workers and threads. The removal went as far as Quick Convert's: the two rows are out
of the XAML, `Av1an.Run` no longer splices the one box into the av1an command, the `"custom"` entry is
gone from `Av1anUi.GetVideoArgsFromUi` - and with it every `cust` reader in `VideoEncodersBin` *and*
`VideoEncodersLib`, the latter having been dead since the Quick Convert removal, which is exactly the
kind of still-wired remnant somebody later feeds again. Existing configs still carry the
`Av1anCustomArgsBox`/`Av1anCustomEncArgsBox` keys; nothing reads them. Holding Shift on Run still opens
the command in an edit window, which is the escape hatch that replaces both boxes.

## Driving the encoder binaries directly

Quick Convert drives the standalone encoder binaries - `SvtAv1EncApp`, `aomenc`, `vpxenc`, `x264`,
`x265`, the same ones `bin/av1an/enc/` carries for av1an - through an `ffmpeg | encoder` y4m pipe
and a second ffmpeg that muxes the result with everything that never went down the pipe.
`VideoEncodersDirect.cs` holds the five encoder classes and `IBinaryEncoder`;
`QuickConvert.BuildDirectCommand` composes the chain. NVENC, GIF, PNG, JPEG and stream copy stay on
the single ffmpeg command.

**The full record is the `direct-encoders` skill**, which loads on any task in this area. Read it
before changing anything here; what follows is only what has to hold whatever you are doing.

- **A codec whose binary is missing is a refusal naming it and `bin/av1an/enc`, and there is
  deliberately no fallback to ffmpeg's library for the same codec** - an encode that quietly ran on
  a different encoder than the one picked would be worse than the message. The Lib* five stay in
  the enum for the CRF ladder, which persists their numeric values.
- **These are the binaries the AV1AN tab drives and deliberately not the same argument builders.**
  `VideoEncodersBin` writes an av1an `-v "…"` string; these write the command line the binary is
  launched with, and state the input, output, chunking and pixel format av1an would otherwise own.
- **Success is judged by artifacts, never by the chain's exit status**, which is the mux's: a decode
  ffmpeg dying upstream of the encoder is invisible to `&&`. Each pass's decode writes a `-progress`
  file whose `progress=end` is its marker, the mux is checked for every mapped stream, and the
  encoder's own stderr goes to a log file rather than the live stream, its vocabulary tripping
  `FfmpegOutputHandler.LooksLikeTrouble`.
- **Assume a bundled CLI tool prompts until it has been shown not to, and pass its suppression flag
  at every launch site.** Every launched tool inherits this app's stdin - nothing here sets
  `RedirectStandardInput` - so one prompt is a hang from a terminal, a silent failure from Explorer
  and a fast `exit 1` behind a pipe. aomenc and vpxenc ask `Continue? (y to continue)` at a `min-q`
  within 8 of `max-q`, values their own rows offer; `CodecUtils.GetNoPromptArg` is the one place the
  flag is decided, for both tabs, looked up through `AvProcess.ToolKnowsFlagOrIsUnknown` because
  both binaries refuse an unrecognised option outright. av1an's `-y` is the one whose absence is
  silent - it exits 0 having declined to overwrite - and the comments at `Av1an.Run` and
  `Av1anSceneDetect.TryPrepareScenesFileAsync` exist so it never reads as boilerplate.
- **y4m carries the frame size, the rate and the range and nothing else**, so colour is handed to
  the encoder by flag in its own spelling, and a tone-mapped encode swaps `MediaFile.ColorData` for
  `ToneMapConfig.GetOutputColorData` around `GetArgs` exactly as `Av1an.Run` does. The direct
  classes are handed `GetVideoSourceFile()`, not the loaded file - in Muxing Mode a different file.
- **AV1 and IVF have no sample-aspect field, so the AV1 pair and VP9 cannot carry an anamorphic
  source's pixel shape, and 2.8.44 to 2.8.77 shipped every such encode stretched.**
  `QuickConvertUi.GetMuxAspectArgs` states `-aspect W:H` on the mux instead, for every direct
  encoder; `ResolveScaledFrame` leaves the source un-squeezed on purpose, and the AV1AN tab
  de-squeezes (`Av1anFrame.Desqueezing`) because av1an muxes its own output. Do not unify the two.
- **VSPipe's y4m header reads `A0:0`, so anything fed through VapourSynth loses the pixel aspect for
  every encoder.** `Qtgmc.GetPipeSarFilter` - a `setsar` built from `VideoStream.Sar` read off the
  source file, never the pipe - goes at the *head* of each VapourSynth-fed chain (`CadenceRepair`,
  `DeinterlacePass`, Quick Convert); `setparams` has no aspect option. `Qtgmc.GetPipeColorParamsAsync`
  is the one place the four colour properties lost on the same pipe are restated, and the field
  order is deliberately not in it: a cadence repair must assert one and a deinterlace must not.
- **Raw Annex B from x264 and x265 cannot be given correct timestamps by any ffmpeg route**, and is
  containerised by mkvmerge into `pipe_video_timed.mkv` whatever the output container - the MP4
  muxers stamp pts equal to dts in decode order, which duplicates and drops a frame at every
  mini-GOP. `Containers.StampsUntimedPackets` was deleted for this reason, and `QuickConvert.Run`
  refuses up front, naming MKVToolNix, when mkvmerge is absent. mkvmerge exits 1 for warnings over
  a usable file, so its status is swallowed *parenthesised* and the artifact judged.
- **`--disable-track-statistics-tags` goes out on that call and must not go out from
  `Av1an.AttachEncodeSettings`**: this step creates the file, that one amends a finished av1an
  output whose tags the encode put in. Same flag, same binary, opposite calls.
- **The keyframe interval and mkvmerge's rate are the post-filter rate.** `CodecUtils.GetKeyIntArg`
  takes a `rateOverride`, filled from `QuickConvertUi.GetPostFilterRate` and
  `Av1anUi.GetPostFilterRate`, or a bob deinterlace gets half the GOP it asked for. The CRF ladder
  passes nothing and is right to: it runs no deinterlacer.
- **The trim reaches both halves and the mux's copy cannot be the same spelling**:
  `TrimSettings.GetMuxInputArgs` puts an input-side `-ss` in front of each *original* input, never
  the encoded one, and `GetMuxOutputArgs` is the duration alone.
- The intermediate is the whole video, so the run's scratch files live in the session folder and are
  deleted on success rather than left for the next launch. Two-pass writes its first pass to the
  same intermediate the second overwrites - `/dev/null` and `NUL` are not the same word.

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

**That different question has an answer, and the answer is to leave it alone.** cmd expands `%VAR%`
inside double quotes, so a path is not protected from it - but the exposure is narrow and there is no
escape to reach for, which is why `WrapArg`'s Windows branch stays as it is. Narrow, because a command
line passes an **undefined** `%NAME%` through unchanged where a batch file deletes it: `Show%20-%2001.mkv`
and `50% off.mkv` both survive, and only a real variable name between two percents - `%TEMP%`, or a
dynamic `%CD%`/`%DATE%` - is substituted, which then fails loudly naming a path nobody typed. No escape,
because `%%` is a *batch-file* spelling that a command line does not collapse ("Escaping a % character as
%%, the way you can do inside batch files, isn't supported"), and `^` is literal data inside double
quotes.

**So `EscapeExpansions` must not be reached for here, though it looks like the fix.** Doubling the
percents would corrupt every `%`-bearing path *and* break Quick Convert's image sequences, whose output
path is `%8d.<ext>` and goes through `WrapArg`: measured, `%8d.png` writes the numbered frames and
`%%8d.png` fails with "Cannot write more than one file with the same name" leaving one file literally
called `%%8d.png`. `EscapeExpansions` is right where it is used, on the av1an launch *script*, because a
batch file is the one context where `%%` means one percent - and it is needed there for the other half of
the same asymmetry, an undefined `%NAME%` being deleted rather than passed through. The way out, if this
ever matters, is to stop using cmd for the launches that need no shell, the way the av1an launcher
already does - not to escape anything.

**Burning in a text subtitle track has two quoting layers and had neither.** The path was double-quoted
inside the already-quoted `-filter_complex`, and ffmpeg has no double-quoting at all - so the quotes
became part of the filename, except that the surrounding shell happened to strip them again for a path
with no space in it. A path *with* one broke the command outright. `FormatUtils.GetFilterPath`
single-quotes at ffmpeg's level and `GetVideoFilterArgs` wraps the whole graph at the shell's.

**Those single quotes are consumed, not passed on, so the drive-letter colon still had to be escaped.**
ffmpeg unescapes twice: the graph parser strips the quotes - which is what earns them, since a path full
of spaces, commas, semicolons and square brackets survives that pass intact - and hands the bare string
to the filter's *own* option parser, which splits it on `:`. So `subtitles='C:/Users/…/ep06.mkv':si=0`
reached that second pass as `C:/Users/…`, took `C` for the filename and `/Users/…` for the next
positional option, which for this filter is `original_size`. Every burn-in on Windows died on "Unable to
parse original_size" fifty milliseconds in, whatever the path - a colon-free one does not exist there.
A backslash inside the quotes is the one spelling that survives both passes, being literal to the first
and an escape to the second. The comment in `GetFilterPath` used to assert the opposite, that inside
quotes a colon is an ordinary character; it is ordinary to the parser that honours the quotes and not to
the one that never saw them.

That makes this Windows-only in practice but not in principle - a colon is a legal character in a Linux
filename and broke it identically there. The order of the replacements is load-bearing: the escapes have
to be written after the backslashes have been turned into slashes, or their own backslashes are turned
into slashes too.

**`=` is escaped as well, and it is a separate character rather than the same one twice.** That second
pass splits a key from its value on `=` *before* it splits options on `:`, so `Season=1` or
`Movie=Extended.mkv` came back as "Option not found" naming everything up to the `=`. What makes it worth
writing down is how it hid: it only bites when nothing earlier in the path has already moved where that
scan starts, so a *Windows* path - always carrying a drive colon two characters in - passes with `=`
unescaped, and every colon-free path fails. The first cut of this fix therefore measured `=` as passing
and said so here, on the strength of a test path that began `C:/`. Escaping it fixes the colon-free case
and changes nothing about the other.

Measured end to end rather than reasoned out, and through the real code rather than a model of it - a
throwaway console project compiling `Shell.cs` and `FormatUtils.cs` as they are, running each candidate
through `WrapArg`, `BuildArguments`, .NET's argument parsing, sh and ffmpeg, against 6.1 and a current
BtbN master build alike. 18 path shapes: the reported one, spaces, `$`, backticks, `%`, `&`, `!`, `;`,
`,`, `=` both with a drive colon and without, square brackets, a double quote, two colons, and two
colon-free controls. 16 of the 18 failed before and none after. The check is that the frames *differ from
the same chain with no burn-in in it*, because an exit code of 0 only proves ffmpeg ran - `File.Exists`
is not a test of whether ffmpeg wrote something, and neither is this.

**The backslash is the one character handled by platform, and it used to be handled as though every
filesystem were Windows.** There it is the separator and cannot appear in a filename, so turning it into
a slash is right - ffmpeg reads a slash as an ordinary character and Windows takes it as a separator.
Everywhere else it is legal *data* in a filename, and substituting it aimed the filter at a path that
does not exist: `back\slash.mkv` came back as "Unable to open …/back/slash.mkv". It is escaped rather
than substituted off Windows now. That branch is also what keeps the rest unambiguous - whichever half
runs, it leaves no backslash behind that the method did not write itself, so every one after it is an
escape rather than data, which is why it has to run first.

**A UNC path survives that substitution, which is worth recording because it looks as though it would
not.** Windows normalisation turns every forward slash into a backslash and keeps the leading pair - "a
series of slashes that follow the first two slashes are collapsed into a single slash" - and identifies a
UNC path by two *separators* rather than two backslashes, so `//NAS/Media/clip.mkv` round-trips to
`\\NAS\Media\clip.mkv`. ffmpeg does not leave it to chance either, running the name through
`GetFullPathNameW` itself before opening it on Windows; and a doubled leading slash is measured here as
an ordinary file path on both builds, as `-i` and inside the graph alike. A `\\?\` path is demoted to an
ordinary one by the same substitution, since only the canonical backslash form skips normalisation - it
still opens, and `MediaFile.ImportPath` being `FileInfo.FullName` means nothing here can produce one.

**Two other places substituted the same way and had no business doing so off Windows.**
`FfmpegUtils.CreateConcatFile` wrote every entry as `file '<path with \ turned into />'`, so a frame
called `fra\me0001.png` came back as "Impossible to open …/fra/me0001.png" - the whole image sequence
lost over one character, measured on both builds. Nothing was bought by it: the concat demuxer copies a
single-quoted run literally, backslashes included. `Paths.GetVmafPath` did the same to av1an's
`--vmaf-path`. Both carry the platform guard now. This is the shape to look for when a path is being made
"safe" for a command line: the rewrite is a Windows separator fix, and every filesystem that is not
Windows treats a backslash as data.

**A trailing space or tab is escaped too, and only a trailing one.** That second pass trims whitespace
off the end of the value, and it trims back as far as the last escape or quote it saw - which is nowhere,
the quotes having been eaten by the pass before it. So `ep06.mkv ` arrived as `ep06.mkv` and could not be
opened, while the same space in the middle of a path was never at risk. Any escape stops the trim, so the
character is written back with a backslash in front of it. `EscapeTrailingWhitespace` runs last, which
costs nothing and is one less thing to reason about: no other replacement can add or remove trailing
whitespace, and a path ending in an apostrophe ends in a quote by the time they have run. The set is
ffmpeg's own `WHITESPACES` rather than `char.IsWhiteSpace`, which would escape characters that pass
through untouched anyway.

## The VMAF model was never a model

**`libvmaf`'s first positional option is `log_path`. There is no `model_path` on it any more, and the
Metrics utility was passing the model file there.** So `libvmaf='…/bin/vmaf_v0.6.1.json':n_threads=…`
did not select a model - it named the file the run's XML log is written to, and the app aimed that at
its own bundled model. Every metrics run overwrote `bin/vmaf_v0.6.1.json` with `<VMAF version="…">`,
scored against libvmaf's built-in default, and printed a `VMAF score:` line, which is the only thing
`UtilGetMetrics` looks for - so it reported success either way, and the dialog's model dropdown had
never moved a number in its life. Measured against a current BtbN master build: 19101 bytes of JSON to
6438 of XML, and `Av1an.cs` hands the same file to av1an as `--vmaf-path`, so one metrics run left
target-quality encodes pointing at a log.

**That one is worth the space because of how the colon fix reached it.** On Windows the drive colon had
been splitting this value before it could do any harm - `log_path` got `C`, the filter refused to
initialise, and the model file survived by accident. Escaping the colon made the path arrive whole,
which turned a command that failed to parse into one that quietly destroyed a bundled file. Linux and
macOS had been destroying it all along, colon-free paths making the new escape a no-op there. A fix that
unblocks a path is not finished when the parse succeeds: what the value now *reaches* has to be checked
too.

**The model is named by version rather than by file, which keeps a path out of the command entirely.**
`model` takes a `key=value` spec parsed by libvmaf itself, and that is the one place in this app where a
colon has to clear **three** parsers rather than two: ffmpeg's graph parser, the filter's option parser,
then libvmaf's own splitter, which also splits its pairs on `:`. A path is not impossible there - it
comes to *three* backslashes, `path\=…C\\\:/…`, where one is "could not parse model config" and two is a
graph-level error - but a Windows drive letter means the question is never academic, and a count of
backslashes that has to be right across three layers is not what this should rest on. All three models
the dropdown offers are compiled into libvmaf, so `model='version\=vmaf_4k_v0.6.1'` asks for exactly the
same thing: measured on one clip pair, 87.018811, 85.072420 and 92.154843 for the three, each matching
its by-path score, with the files byte-identical afterwards. The `=` inside the spec still needs
escaping past ffmpeg's own option parser. `GetVmafModel` returns `""` for an index the list does not
have, and an empty `model` is an error rather than the default, so it is left off entirely instead.

The bundled `.json` files are still downloaded by `bundle-tools.sh` and still wanted - av1an's
`--vmaf-path` is a path and takes one. `Paths.GetVmafPath` lost its `escape` flag with this: that branch
existed only to feed the positional argument above.

**av1an does not have the same bug - settled by reading the bundled binary.** `model_path` left libvmaf
between ffmpeg 6.0 and 6.1 - 6.0 lists `model_path log_path log_fmt …`, 6.1 and everything since start
at `log_path` - so av1an building `libvmaf=model_path=…` out of `--vmaf-path` would score target-quality
encodes against the built-in default and write an XML log over `bin/vmaf_v0.6.1.json`, which is this
same fault reached through av1an instead. It does not. The string `model_path` does not appear anywhere
in the bundled `av1an.exe` (`0.5.2-unstable (rev 805dad6)`, toolchain 2.8.78), and the template it does
carry is `[distorted][ref]libvmaf=log_fmt='json':eof_action=endall:log_path=<X>:model='<Y>':n_threads=<N>`,
with `path=` and `version=` adjacent as the two `model` spec prefixes. So the log goes to `log_path` and
`--vmaf-path` reaches the model as `model='path=…'` - the right way round, and the bundled JSON is not
overwritten. Read out of the binary's own strings rather than measured through a run, which is the
evidence grade this file asked for when it left the question open.

**A second Windows fault sat behind the same one, and it is the one the burn-in's own history warns
about: ffmpeg's quotes are not the shell's.** `Comparison.Graph` closed its double quotes *before* the
metric filter and left the caller to append it, so anything in that filter sat outside them - protected
only by ffmpeg-level single quotes, which mean nothing to `CommandLineToArgvW`. The comment there
conceded the whole bug while calling it safe: the halves "join into one argument regardless, having no
whitespace between them". An install under `C:\Program Files\...` is exactly the whitespace it assumed
away. `Graph` takes the metric filter as a parameter now and wraps the lot with `Shell.WrapArg`, so
there is no longer an outside to append to.

The layer decides which string you may ask about: the split is a Windows question, so it is asked of
`WrapArg`'s *Windows* branch, through .NET's own `Arguments`-to-argv parser, which follows the same
convention as `CommandLineToArgvW`. Asking it of the Linux encoding answers "split" for both shapes and
means nothing - single quotes are not something that parser has ever honoured. Before: two arguments for
a path with a space, one without. After: one either way.

**An apostrophe needs one level more than ffmpeg documents, and was refused outright until 2.8.23 for
want of it.** The documented spelling closes the quoted run, escapes, and reopens - `'it'\''s'` - which
is right for a value handed on whole, and this one is unescaped *again* after it: the `\` is eaten by
the first pass and the bare apostrophe reopens a quote to the second, so what came back was a complaint
about a filename with the apostrophe missing and `:si=0` stuck on the end. Written `'it'\\\''s` -
close-quote, `\\` (a literal backslash to the first pass, the escape to the second), `\'` (a literal
apostrophe to the first, the escaped one to the second), reopen-quote - it survives both, on ffmpeg 6.1
and a current master build alike, with the frames differing from a no-burn-in control.

The old finding was "no spelling of it works", and it was arrived at honestly: every spelling tried was
one level deep, which is all ffmpeg documents, and one level is exactly what a value unescaped twice
cannot use. The colon had the same shape and the same wrong conclusion written beside it. **A character
that cannot be escaped and a character nobody escaped twice look identical from the outside** - so
before refusing a path again, count the parsers between the string and the thing that reads it.

`GetBurnInProblem` and the `QuickConvert.Run` check that called it are gone rather than left returning
"", with a comment where the check sat: `It's Always Sunny.mkv` burns in now instead of sending the user
away to rename it. Bitmap tracks never reached that check anyway - they are a filtergraph input mapped
by stream index, with no filename in the graph.

**Nothing in the burn-in strips styling, and the log now says so rather than leaving it to be
inferred.** The `subtitles` filter opens the source file itself and hands the track to libass as it
sits there, so the colours, the borders, the `\pos` and the font *names* survive whatever the output
container can hold - the subtitle Codec box, `-sn` and the stream maps all act on the output and reach
none of it. What made that worth a line is the message underneath it: a burn-in into MP4 logs "18
attachment tracks left out: MP4 cannot store attachment streams" a moment later, and read on its own
that is indistinguishable from the fonts having been thrown away *before* the render rather than after.
Measured with the app's own chain - scale to 640x360, burn in, pad to 640x480 - against a plain SRT
control and a no-burn-in control: every styled attribute present and correctly scaled.

**The fonts are the one half that is not carried in the track, and ffmpeg finds them by mimetype and by
nothing else.** `font_mimetypes[]` in `vf_subtitles.c` is the whole list - the same ten entries in 6.1
and in current master - with no fallback on the file extension, and an attachment carrying no
`filename` tag is skipped too. So a font tagged `application/octet-stream` is invisible to the renderer
however plainly it is a font, and libass substitutes: the right words, in the right place, in the wrong
typeface. That is the only way a burn-in here loses styling, and it looks exactly like the styling
having been stripped, which is why `SubtitleFonts` names it in the log instead of letting it be
discovered in the output. ffprobe is no help spotting it either - it derives an attachment's
`codec_name` *from* the mimetype, so the broken ones come back as `unknown` rather than `ttf`.

One of the two reasons is fixable rather than only reportable: libass scans a `fontsdir` of its own, so
`PrepareBurnInFontsAsync` dumps the attachments into the session folder and the filter is given that
directory. The *other* is not, and the two are kept apart for that reason - `-dump_attachment` names its
output after the same `filename` tag ffmpeg wanted in the first place, so a nameless attachment has
nothing to be written out as, and reporting one rescue over both would be claiming a fix that did not
happen. Gated on there being a skipped font, so the ordinary file - mkvmerge writes mimetypes ffmpeg
knows, which is every SubsPlease-shaped release - adds no ffmpeg run and no argument. Settled once in
`QuickConvert.Run` beside the deinterlacing, for the reason written there: the chain is built once per
pass. Measured with the system font removed, so only the attachment could supply it: unrecognised
mimetype without `fontsdir` renders the fallback, with `fontsdir` the frame is byte-identical to the
one rendered with the font installed, and a recognised mimetype is unchanged either way. The directory
goes through `FormatUtils.GetFilterPath` like the filename beside it and was checked through the real
code by reflection into the built assembly, over a folder called `It's a $HOME dir=1 [x]` - it is the
same two parsers, so it is the same escaping.

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

## The CRF ladder

The Sample Encodes utility answers "what CRF for this source" by trying it: a few short sections are cut
out, each is encoded at every CRF on the list, and the report is the bitrate, the size a whole file would
come to, and a quality score per rung. `UtilCrfLadder` runs it, `CrfLadder` holds the arithmetic, and
`CrfLadderWindow`/`CrfLadderResultsWindow` are the settings and the table.

**The reference is the cut, not the source, and that is the whole reason this is exact.** A stream copy
carries the source's own frames, so the file the encoder is given *is* the file the metric scores against
- no seeking, no scaling, no frame alignment, and none of the work `UtilGetMetrics.PrepareComparison` has
to do to put two unrelated files into one frame of reference.

**A section cut losslessly is longer than the length asked for, and the arithmetic must divide by what
came out.** A copy cannot begin between keyframes, so the cut starts at the keyframe at or before the
start point and `-t` is then counted from the start point itself - the pre-roll is kept on top. Measured
against a source with keyframes exactly every 2s: a 10s section asked for at 20.0s came out **12.08s**, at
20.5s 10.58s, at 21.0s 11.08s. The first of those is 21% over, and every per-second figure in the report
divides by it, so `ExtractSamples` probes each cut with ffprobe and `CrfLadder.Sample.Ms` is that number
rather than the setting. A start point sitting exactly on a keyframe still steps back a whole GOP, which
is the surprising half.

**SSIMULACRA2 is not an ffmpeg metric and never can be, which is why it takes a whole second scoring
path.** Measured against a current BtbN master build, `libvmaf=feature='name\=ssimulacra2'` fails the
graph with "problem during vmaf_use_feature", where `psnr`, `float_ssim`, `float_ms_ssim`, `ciede` and
`cambi` all take - and the reason is that **libvmaf has no such feature extractor at all.**
`feature_extractor_list[]` in libvmaf 3.2.0 is psnr, ansnr, adm, vif, motion, moment, ms_ssim, ssim,
ciede, psnr_hvs, cambi, their integer and CUDA variants, and null; the string `ssimulacra` appears in
**zero** files of the whole repository, and in none of ffmpeg's either - there is no `vf_ssimulacra*`
and `vf_libvmaf.c` never mentions it. So no build has it: not BtbN's, not gyan.dev's, not one compiled
by hand. (The `ssimulacra2` string that *is* in a BtbN binary belongs to libaom's `--tune` enum, next to
`qm-psnr`, `vmaf_neg` and `butteraugli` - a different library's list entirely, and an easy one to mistake
for evidence.)

**So it is scored through VapourSynth instead, the same plugin the AV1AN tab's Target SSIMULACRA2 mode
uses.** `bundle-tools.sh` bundles vszip (`com.julek.vszip`, tag R13) on Windows; `Media/Ssimulacra2.cs`
runs the bundled embeddable Python over a script that opens both files through the same LSMASH source
QTGMC uses, calls `core.vszip.SSIMULACRA2(reference, distorted)`, and means over the per-frame
`SSIMULACRA2` float property. vspipe cannot do this - it renders frames and discards their properties -
so this is Python-with-the-module, the shape `VshipStager.ProbeDll` already established. **There are two
vszip API generations and both are handled:** new vszip (and vship) expose `SSIMULACRA2` with the
property `SSIMULACRA2`; legacy vszip is `Metrics` with `mode=0` and the property `_SSIMULACRA2`. The
scoring script tries the functions in that order and reads whichever property is present, which is what
av1an does and what keeps a vszip a user brought themselves working. The plugin does its own RGBS and
linear-light conversion from a resolution-picked matrix, so nothing here has to tag the clips.

**Because it is Windows-and-vszip only, it is a refusal rather than a silent skip.** `Ssimulacra2`
probes once per session by rendering one SSIMULACRA2 frame over a blank clip - presence is not
loadability, the eedi3m lesson - and `UtilCrfLadder.Run` checks that *before* it cuts or encodes
anything, so a machine without vszip is told to pick VMAF or XPSNR rather than encoding the whole grid
and reporting every rung on size alone. `GoodScore` gives it its own recommendation anchor: **80**, the
AV1AN tab's own target-quality default (VMAF's is 95), where 80 is "imperceptible side by side".

**None of the VapourSynth half can be exercised in a web session** - there is no vszip and no
VapourSynth - so what is checked here is the script *generation* (the path escaping round-trips through
a nasty Windows path with backslashes, a space and both quote characters), the score *parse* (locale
independent, last line wins among interleaved stderr), and the wiring (the refusal fires on this Linux
box with a platform reason; the results window draws an `SSIMULACRA2` column and the 80-anchor
recommendation). The computation itself is proven at release time: the win-x64 job renders a real
SSIMULACRA2 frame through the bundled vszip - identical clips near 100, an inverted pair lower and
finite - and fails the build if it cannot, the same way it proves the QTGMC toolchain. What a real
machine still has to confirm is that the number tracks CRF on natural content.

XPSNR is the second opinion, and it is a filter of ffmpeg's own, perceptually weighted where plain PSNR
is not. It prints `XPSNR  y: 30.7897  u: 29.9396  v: 30.8069  (minimum: 29.9396)`, and `inf` for an
identical pair, which is a real answer rather than a parse failure and is reported as one. It has no
fixed ceiling, so `GoodScore` gives it no anchor and the results window draws no recommendation for it -
only the table.

**The metric enum is appended to, never reordered, because its numeric value is the saved setting.**
`Vmaf`, `Xpsnr`, `None` came first and `Ssimulacra2` is `3`; the dialog drives its dropdown from
`CrfLadder.MetricOrder` (display order: VMAF, SSIMULACRA2, XPSNR, Nothing) rather than from enum==index,
so a metric can be slotted into the list anywhere without moving what an existing config restores.

**The model is named by version, and the escaping was checked at the far end rather than at the parse.**
The filter goes through `Shell.WrapArg` exactly as `UtilGetMetrics` builds it, backslash-escaped `=` and
all. That it parses proves nothing on its own - what was measured is that the three models give three
*different* scores through the app's own command shape (88.64 / 86.59 / 92.58 on one pair) and that a
name libvmaf does not have fails rather than falling back.

**ffmpeg's own encoders, not av1an's.** av1an's chunking, scene detection and VapourSynth exist to make a
long encode parallel and cost more than they save on ten seconds, and there is no av1an binary in a web
session to check any of it against. The consequence is stated rather than hidden: the AV1AN tab drives
`SvtAv1EncApp` from svt-av1-hdr where this runs the `libsvtav1` inside ffmpeg, so a number off this ladder
is a starting point there rather than the same setting. The card, the dialog and the results window all
say so.

**Container overhead is not subtracted, and that is a judgement rather than an oversight.** Measured on a
12s video-only MKV it is a fixed ~2.7 KB, which is 0.22% of the bytes at CRF 22 and 0.90% at CRF 40. The
sampling error is two orders of magnitude larger - 33s of a three-minute file is 18.8% of it - so
correcting the smaller one would be false precision dressed as rigour. What the report does instead is
say what it sampled and that the rest of the film is what it cannot know.

**The pooled score is a mean over frames, not over samples.** The sections are not the same length, for
the pre-roll reason above, so weighting them equally would let the shortest one carry as much of the
answer as the longest. At a constant frame rate, weighting by duration is weighting by frames, which is
how libvmaf pools its own.

The settings are the utility's own - encoder, preset, colour format, CRF list, sampling and metric - for
the argument `UtilDeinterlace.Settings` already makes: this one produces a number to type into a tab, so
reading a tab to produce it would be circular. It is excluded from batch mode, like the bitrate chart, and
for the same reason plus one of its own: a CRF that suits one file is the wrong one for the next.

Verified by running it. 468 sample-placement cases across 13 durations, 6 counts and 6 section lengths,
each checked for staying inside the file, not overlapping, not exceeding the count asked for and coming
back in time order; the CRF parsing and defaults for all seven encoders, against out-of-range, duplicated,
unsorted, junk and over-long input; and the pooling and projection arithmetic against numbers whose answer
is known by hand. Then the whole thing end to end through the real methods out of the built assembly, on a
three-minute source whose content changes every minute so the three samples genuinely land on different
material - x264 at CRF 22/26/30 giving 1040/681/375 kbps and VMAF 95.61/92.58/87.14, strictly monotonic in
both, with each rung's per-sample entries measured against the durations the cuts actually had.

**Butteraugli is the fourth metric, and it is SSIMULACRA2's shape with the direction reversed.** No
ffmpeg computes it - the "butteraugli" string in a BtbN binary is libaom's `--tune` enum, the same trap
the SSIMULACRA2 note above records - so `Media/Butteraugli.cs` scores it through VapourSynth: Vship's
`BUTTERAUGLI` where `VshipStager` has staged it (the ladder calls `Reconcile` before the availability
gate for both VapourSynth metrics, exactly as a metric-targeted av1an encode does), else the julek
plugin the bundle already carries staged for av1an's sake - which this feature is the first thing to
actually call. The shared Python plumbing moved to `Media/VsPython.cs` with this; the two metric classes
keep their own probes, scripts and parses.

What it reports is the per-frame *maximum* distance (the INF norm) at 203 nits, pooled as a mean over
frames - the same quantity av1an's `butteraugli-inf` targets, so a number here and the Target
Butteraugli box read on one scale, and `GoodScore`'s anchor is that box's own default of 4. All of it
was read out of the pinned sources rather than assumed: Vship v4.0.2 writes `_BUTTERAUGLI_INFNorm` and
defaults its intensity to 203; julek r3 writes `_FrameButteraugli`, which is libjxl's
`ButteraugliDistance` and therefore the max of the diff map, and defaults its intensity to **80** - so
203 goes out explicitly to both, or the CPU and GPU paths would disagree by a scale factor. julek also
refuses anything but RGB where Vship converts internally, so the julek path converts first with Vship's
own toRGBS recipe (Bicubic to RGBS, BT.709 matrix above 650 lines and BT.601 below, limited in, full
out), which is what keeps a machine that scores on CPU comparable with one that scores on GPU.

**Lower is better, and that flips more than the recommendation line.** `CrfLadder.LowerIsBetter` is the
one statement of it: the results window picks the highest CRF still *under* the anchor, the no-pick
phrasing reads "stayed under", and a genuine 0.0 over real frames is Ok - an identical pair *is* 0, and
the parse must not confuse it with no score coming back. The enum appended `Butteraugli = 4` (numeric
values are the saved setting; `MetricOrder` slots it into the display after SSIMULACRA2), and the
harness asserts the enum values never moved and that `MetricOrder` holds every value exactly once.

**The bundled julek.dll requests API 4.0 and loads on R72** - read out of the published 2.8.46 zip's PE
by the eedi3m method rather than assumed, julek compiling with `VAPOURSYNTH_API_VERSION` from whatever
headers built it, which is exactly how eedi3m shipped broken. The release workflow now renders a real
Butteraugli score through the bundled plugin beside the vszip check (identical pair under 0.5,
white-against-black past it by 5), so loadability is re-proven per release instead of once by hand. The
scoring scripts were also *executed*, not merely compiled: a stub vapoursynth module - the QTGMC
plugin-trace trick - ran the real emitted script under julek-only, vship-only, both and neither,
observing the backend preference, the RGBS conversion happening on the julek path only and with Vship's
exact arguments, the explicit 203, the property fallback order (INFNorm, then julek's, then old Vship's
bare `_BUTTERAUGLI`), the trim to the shorter clip, the mean, and the sentinel. What no session here
can confirm is the number tracking CRF on natural content - the same caveat SSIMULACRA2 carries.

**The Content preset row applies the Advanced tabs' presets to the sample encodes**, at the user's
request, so the ladder measures the encode being planned rather than a vanilla one. x264 and x265 get
their Quick Convert sets, written for exactly the binary this utility runs; SVT-AV1 gets the AV1AN
tab's set, which is written for svt-av1-hdr - a different binary from the libsvtav1 here - so
`UtilCrfLadder.GetApplicablePresetPairs` keeps only the parameters `LibSvtAv1.json` vouches for and the
log names what it drops, because a parameter this library lacks would be accepted by ffmpeg and dropped
by the library with a warning nothing reads: the silent half-apply this file already documents, turned
into a named one. The surviving rows reach the command as the ordinary `encArgs["advanced"]` pairs
string, so `-svtav1-params`, `-x264-params` and x265's merged list are the same code a filled grid
uses. The preset is part of the run's snapshot and of `CrfLadder.Result`, named in the dialog readout
(with the applied-count for SVT), the log, the results subtitle and the copied table - a CRF belongs to
every setting that produced it. The row hides for the encoders without a set, and the choice resets on
an encoder switch like the speed preset beside it.

**Do not put hbd-mds into LibSvtAv1.json, however measured-present it looks.** It was added for exactly
one commit, on the strength of the measured note above that the v4.1.0 library has it - and the BtbN
build crashes on it. **The trigger moved under a library minor-version bump, so what follows is the
second description of it and the first is kept visible.** Against `v4.1.0-279-gd3c4cb394` this file
said it **segfaults** the moment hbd-mds is set beside `tune` (any value) or `enable-overlays`, at
preset 4 and 8, 8- and 10-bit alike, while every parameter alone runs fine. Three of those four clauses
are now false. Re-measured against the 2.8.78 bundle, whose ffmpeg `N-126264-g007cd1fd43-20260825`
carries `SVT-AV1 Encoder Lib v4.2.0-73-gfb0ed7e59`: **8-bit does not crash at all**, being a clean
refusal at encoder open - every preset, every value, with or without a second parameter (`Svt[error]:
Full high bit depth and hybrid 8/10 mode decision are not supported when encoder bit depth is 8`, then
`[libsvtav1] Error setting encoder parameters: bad parameter (0x80001005)` and `AVERROR(EINVAL)`, no
output). **10-bit is where it is accepted and where it now crashes, on hbd-mds *alone*** - `0xC0000005`
at every preset from 5 up, 3/3 deterministic, where presets 2 and 4 encode cleanly and produce a
readable file. `tune` no longer triggers it: `hbd-mds=1` beside `tune` 0, 1 and 2 all encode cleanly at
10-bit preset 4. `enable-overlays=1` still does, at preset 4, 3/3, with `enable-overlays=1` alone fine.
So the axis is no longer "beside a second parameter, at preset 4 and 8" but "10-bit, preset 5 and up,
on its own".

**The conclusion is unchanged and is better supported than when it was written.** A fault that has
grown from "crashes when paired" to "crashes unpaired across most of the preset range", and that
survived a minor-version bump, is not a pin-specific regression to wait out. The row-filtering
rationale is intact too: `hbd-mds` is still a *recognised* key on v4.2.0 rather than an unknown one,
and all 33 current `LibSvtAv1.json` rows are still accepted by it. The shipped SvtAv1EncApp
(`SVT-AV1-HDR v4.1.0-20-g0bed4090b` in the 2.8.78 bundle, run directly) remains completely clean -
`--hbd-mds` 0/1/2 at 8- and 10-bit, and beside `--tune 0` and `--enable-overlays 1` at presets
2/4/5/6/8/10, 18 of 18 - so the AV1AN tab's presets, which pair `hbd-mds 1` with `tune 0` on that
binary, are unaffected. With no
hbd-mds row in the ffmpeg list the translation drops it, named in the log beside
`noise-adaptive-filtering` (the genuinely absent one), and nothing in the app can produce the pairing
through ffmpeg. Anime translates 6 of its 8 rows, Game Capture 7 of 8, and all three encoders' preset
paths were run end to end through the real `Run()` against the real binaries: SVT 557 kbps plain
against 636 (Anime) and 620 (Game), x264 543 against 690 (Grainy Film), x265 609 against 500 (Anime) -
so the rows demonstrably reach the encoders, through all three spellings.

**Whose SVT-AV1 those two version strings belong to was got wrong once, and the receipts are worth
keeping.** The libsvtav1 inside BtbN's ffmpeg is **mainline** - `BtbN/FFmpeg-Builds`'
`scripts.d/50-svtav1.sh` clones `gitlab.com/AOMediaCodec/SVT-AV1` at a pinned commit, and the pin
(`d3c4cb394` when checked) is the very hash in the library's version string - so the hbd-mds segfault
above is a *mainline* regression between the v4.1.0 release and that pin, not the fork's. What made it
look like a fork build is that the old tells have rotted: mainline numbers itself 4.x now, and it has
absorbed a large slice of the formerly-PSY surface - `luminance-qp-bias`, `chroma-qm-min`, the
variance-boost family, `hbd-mds`, `ac-bias` were all measured being accepted through `-svtav1-params`,
and `luminance-qp-bias` visibly moves the encode. svt-av1-hdr today is a thin layer on top: its release
binary calls itself `SVT-AV1-HDR v4.1.0-19`, mainline's own v4.1.0 plus the fork's commits, and what
those commits still carry exclusively is the `noise*` family, `fgs-table`, `tx-bias`,
`noise-adaptive-filtering`, `kf-tf-strength` and friends - exactly the rows `LibSvtAv1.json` lacks and
the preset translation drops. So "ffmpeg's SVT is mainline, av1an's is the hdr fork" is the right
model; the reachable-parameter split the two JSON lists encode is real either way, and it is measured
against the binaries rather than derived from whose tree they come from.

## Deinterlacing

Deinterlacing on both encode tabs and in the Deinterlace Video utility - and the **trim and cut**
handling, which lives with it because a trim is the one thing QTGMC cannot compose with.
`DeinterlaceUi` drives both rows, `Deinterlace.ResolveAsync` picks the engine, `InterlaceDetect`
decides what a file is, `Qtgmc` writes the VapourSynth script, and `TrimSettings`/`UtilCut` do the
cutting.

**The full record is the `deinterlacing` skill**, which loads on deinterlacing *and* on trim work.
Read it before changing anything here; what follows is only what has to hold whatever you are doing.

- **The Quick Convert dropdown saves its index, so `DeinterlaceUi.AllModes` may be appended to but
  never reordered.** The AV1AN dropdown is its own array (`Av1anModes`), so nothing there can select
  an engine it will not run; the two are different lengths, so read a box against the array it was
  filled from or you will name the wrong engine.
- **`DeinterlaceRequest.DoubleRate` defaults to `true`**, so merely *omitting* it asks for one frame
  per field - which inside av1an writes twice the frames its chunking expects, and the file plays at
  half speed. It is set `false` explicitly on that tab. A field whose default is the dangerous value
  has to be written, not left out.
- **QTGMC is not on the AV1AN tab**, and the reason is arithmetic rather than taste: a per-chunk
  filter costing more than the encoder is paid for on every chunk, every probe and the detection
  pass. `DeinterlaceUi.Av1anQtgmcProblem` is the standing reason.
- **The defaults are stated in exactly three places** - `DeinterlaceUi.DefaultMode`,
  `DeinterlaceUi.Av1anDefaultMode` and `Qtgmc.DefaultPreset` - and the last is not only a default:
  it also decides which plugin set has to be present.
- **Presence is not loadability, construction is not loadability, and a rendered frame is not a
  length.** A VapourSynth plugin can be a valid file the core refuses over its API version, with no
  message anywhere; a *source* can construct, report a correct frame count and then fail every
  `get_frame` (bestsource, on a capture with a damaged PTS); and one can render frame 0 perfectly
  over a clip of the wrong length (lsmas, answering a 30-minute capture with 25 frames - which
  shipped in 2.8.71 and was worse than the bug it replaced). `Qtgmc`'s `open_video` checks all
  three, the last against `EXPECT_MS`. An attempt list there may be reordered; it must never go
  back to trusting the constructor, the exception, or the frame count alone.
- **QTGMC's source is opened at the file's true frame rate (`fpsnum`/`fpsden`), and that is
  load-bearing rather than tidy.** VapourSynth has no variable-rate clip, so a plain open hands over
  every coded picture and calls it constant-rate: a capture padded with duplicate frames by its TBC
  then comes out as long as its frame count instead of as long as its recording - measured, 1.4014x
  long, with the audio ending at 71% of the picture. ffmpeg does this conversion by default, which
  is the only reason bwdif and yadif were never affected. The rate comes off the plan's own file, so
  anything that changes what `VideoStream.Rate` means changes this too.
- **A capture padded with duplicate frames is not a deinterlacing fault and cannot be fixed here.**
  Its frame count exceeds its own recording, so every rate conversion - ffmpeg's and VapourSynth's
  alike - chooses from the timestamps alone and never looks at the pictures: measured, 12.8% of the
  padding identified wrongly, ~1.5 hitches a second. The Repair Frame Cadence utility rewrites it
  first; see that section. Note what that does *not* license: the timestamps are damaged frame by
  frame and are still the only record of when each picture belongs, and a repair that discards them
  and resamples by index drifts **seconds** in the middle of a file whose ends line up perfectly.
- **Piping VapourSynth into ffmpeg loses the field order and the colour, and only a filter can put
  them back.** y4m carries a frame size, a rate and a range and nothing else; the frame's own
  properties then beat the output AVOptions, so `-color_primaries`/`-color_trc` are dropped in
  silence while `-colorspace`/`-color_range` are honoured - identically whether spelled as names or
  as numbers. `-vf setparams=field_mode=…:color_primaries=…:color_trc=…:colorspace=…:range=…` sets
  all of it on the frames, which is what survives. Reading the source as a *file* tags everything
  correctly, so a test that skips the real pipe proves nothing.
- **A stream-copy cut ends two frames late on any B-frame source**, and `-frames:v` is not the fix
  however much it looks like one - it truncates in decode order and can take a hole out of the
  middle of the picture. Leave it.

## Repairing a padded capture

The Repair Frame Cadence utility removes the duplicate frames a capture's TBC inserted and writes a
constant-rate copy. `CadenceRepair` holds the VapourSynth script and the run, `UtilRepairCadence`
the task. It has no settings - the recording's own length is the answer - and a file whose frame
count already matches its length is refused rather than copied.

**The full record is the `cadence-repair` skill**, which loads on any task in this area. Read it
before changing anything here; what follows is only what has to hold whatever you are doing.

- **Every other rate conversion in this app - ffmpeg's `-fps_mode cfr`, a source plugin's
  `fpsnum` - decides what to drop from the timestamps alone and never looks at the pictures.** This
  utility is the one that does, which is why a padded capture goes through it first and why the
  deinterlacers cannot be taught to cope instead.
- **The timestamps say *when* an output frame belongs and the content says *which* frame near that
  moment to take; drop either half and the other cannot show the damage.** Index-resampling at a
  constant keep-ratio shipped once and ran 6.99 s ahead of its own audio in the middle of a file
  whose ends lined up exactly. Place by timestamp, break ties by content, and **check the worst
  placement error over every frame - never a comparison of durations or endpoints**, which is
  precisely the signature that passed the bug.
- **The tie-break window is half an output step and must not be widened**: two steps fetched a
  frame from 67 ms away and pushed the worst error to 92 ms, inside lip-sync range.
- **Validate a length-dependent fault at length.** The 30 s cut showed 0.127 s of drift where the
  95-minute capture showed 6.99 s; every check that mattered was run on the short cut and proved
  nothing about the file the utility was written for.
- **The pipe into ffmpeg loses the field order and the colour, and only `setparams` on the frames
  puts them back** - the output AVOptions are dropped in silence for two of the four properties,
  `-flags +ilme+ildct` yields the *wrong parity*, and a test that reads the source as a file rather
  than through the real VSPipe producer proves nothing. The four properties are probed as
  `key=value` in one ffprobe call, never `nokey=1`, or a diagnostic about the *audio* is taken for a
  value and the file is written tagged `unknown` while reporting success.
- **Forward-with-gaps access is exact on all three source plugins and random access is not** -
  lsmas raises, ffms2 answers wrongly in silence, bestsource is right - and across ten samples no
  plugin is safe on every MPEG-2 file. `PLAIN_ORDER` is the one place each script's attempt order is
  stated (bestsource first here, lsmas for the deinterlace script), and `open_video` tries in that
  order with a validating check rather than declaring by extension. That shape must stay.
- **`FPS_NUM`/`FPS_DEN` are `open_video`'s names and mean "rebuild the clip at this rate"** - naming
  a script's *output* rate that way hands the file to the very conversion this script replaces. The
  repair sets both to 0 and calls its own `OUT_FPS_NUM`/`OUT_FPS_DEN`.
- **DGIndex and d2vsource were fetched, run against the capture and deliberately not adopted**: they
  decode it identically to bestsource, remove none of the padding, and fail the same random-access
  test lsmas fails - for a Windows-only 2010 binary and a new intermediate format.

## Grain synthesis

The Grain Synthesis row on both encode tabs - which owns every way this app can put grain into a
file *being* encoded - and the Film Grain utility, which owns everything done to a file that is
already encoded. `GrainSynthConfig` holds the mode, `GrainSynthUi` drives both rows, `Grav1synth`
runs the tool.

**The full record is the `grain-synthesis` skill**, which loads on grain work and on a
`bundle-tools.sh` change touching grav1synth or the MSYS2 encoders. Read it before changing anything
here; what follows is only what has to hold whatever you are doing.

- **SVT-AV1 has three ways to be asked for film grain and takes exactly one of them.** The
  precedence is `--fgs-table` over `--noise` over `--film-grain`, applied in
  `set_param_based_on_input` *after* the whole command line has been parsed - so the order the flags
  are written in cannot save a row set beside it - and the only notice is a warning on the encoder's
  stderr, which av1an collects per chunk into a log a successful run deletes.
- **`GetPreparedInputs` must keep matching `.deint.`, `.denoised.`, `.grainref.` and `.grain.`.**
  Nothing in this build writes the first three, but earlier releases did, and two of them are
  lossless FFV1 - the largest files this app has ever written. Whichever run deletes such a temp
  folder is the last chance anything has to take them with it. That list already learned this from
  the other side: `.denoised.` was left off when the grain modes were added, and every measured
  encode leaked a lossless copy of the whole video onto the disk for good.
- **grav1synth exits 0 having done nothing** on a file with no grain, prints no progress at all when
  stderr is not a TTY, and prompts interactively without `-y`. Judge every call by the artifact and
  by its own "Done, wrote…" line, never by the exit code.
- **`GrainDelivery` has two values and there is deliberately no third.** A mode either hands the
  encoder a strength or hands it a table, and a table the encoder cannot take is a **refusal**
  (`GetTableDeliveryProblem`), naming the utility as the way to put that grain in afterwards. An
  earlier cut quietly fell back to rewriting the finished file with grav1synth instead: it
  produced the right output and it was the wrong shape, the row doing the utility's job without
  saying so.
- **A utility writes a file and stops.** `Measured` and `PhotonNoise` are utility-only
  (`GrainSynthConfig.IsUtilityOnly`); the tabs' rows apply *during* an encode and cost no pass over
  the video. Neither reads the other's settings.
- **A path put into av1an's `-v "…"` string must be backslash-escaped, because that string is
  *unescaped* as well as split.** `FormatUtils.GetAv1anArgPath` is the one place that lives, applied
  by `Av1anUi.GetVideoArgsFromUi`. Written bare - which looked right, a quote of this app's own being
  one layer more than the re-split accounts for - a Windows path reached the encoder with every
  backslash eaten (`C:\Users\…` as `C:Users…`), and **both table-bearing grain modes were
  broken on Windows outright**, the preset's table being generated as a Windows path and a brought
  one coming from a Windows file dialog. Anything else that ever puts a path in that string needs the
  same call; a path typed into the Advanced grid is still eaten, and deliberately, the grid holding
  values this app cannot tell paths from.

## Tone mapping

Converting an HDR file to SDR, on both encode tabs and in the previews. `ToneMapConfig` builds the
chain, `ToneMapUi` drives both rows and settles the backend once per encode, `ToneMap` does the
probing and the peak scan, `ColorDataUtils` reads and writes the colour.

**The full record is the `tone-mapping` skill**, which loads on any HDR task. Read it before
changing anything here; what follows is only what has to hold whatever you are doing.

- **A tone-mapped encode must swap `MediaFile.ColorData` for `ToneMapConfig.GetOutputColorData`
  around the encoder's `GetArgs`** - `Av1an.Run` and `QuickConvert.BuildVideoCodecArgs` both do,
  because encoders told their colour by flag cannot read it off the frames. Without it the output is
  SDR pixels tagged PQ and BT.2020, which every player expands again, and **nothing about it looks
  wrong until it is played**. Swap and restore around the call rather than assigning: the field
  means "the colour of this file" everywhere else, and `ToneMapUi.IsRowRelevant` reads it to decide
  whether to show the row at all.
- **The AV1AN tab's standing policy is that no intermediate pass may itself be an encode**, which is
  why `ToneMapUi.GetAv1anConfig` sets `ToneMapConfig.ForceCpuChain` unconditionally and that tab
  never runs libplacebo. The pass that used to sit in front of av1an was a full x264 re-encode of
  the film; what it earned is recorded in the skill, against anything like it ever returning.
- **The zscale chain has to end bounded** (`ToneMapConfig.ClampFilters` - three filters, none of
  them redundant), or whether the tone map's out-of-range values survive is decided by whatever
  geometry happens to follow it: one setting, two pictures.
- **`HdrSideDataDeletes` is probed against the running ffmpeg**
  (`ToneMap.ResolveSideDataSupportAsync`): four of the seven names do not exist before master, and
  an unprobed list fails **every** tone-mapped encode on an ordinary distribution ffmpeg. An empty
  parse is a failed probe, not an ffmpeg with no side data types - believing the latter would
  silently stop the chain deleting anything.
- **The row is hidden for a file that is not HDR** (`ColorDataUtils.IsHdr`, which reads the transfer
  curve and nothing else), and `ToneMapUi.ModeInEffect` reports Off whenever the row is off screen
  whatever the box behind it says - which is what makes hiding it safe rather than merely tidy.
- **The previews tone-map too** (`FfmpegExtract.GetPreviewFilters`), display-side only, and that is
  safe precisely because nothing is written from it. An HDR file drawn without one is not a neutral
  picture of it - it is wrong, in the direction that looks like a fault in this app.

## Loudness normalization

**Quick Convert can bring every encoded audio track to a standard loudness, and it is two-pass because
one-pass is a compressor.** ffmpeg's `loudnorm` run in a single pass normalizes *dynamically*, riding
the gain as the programme goes: measured against a source whose quiet passage sits 26 dB under its loud
one, it brought the two to within **1.3 dB of each other** - the quiet half lifted by nearly 30 dB -
while reporting that it had hit the target exactly. The same source through the two-pass path kept all
26 dB. Both land on the requested LUFS, so the number gives nothing away. The first pass measures each
track, the second is the encode with those numbers handed back in and `linear=true`.

`Loudnorm.MeasureAsync` is the first pass, one ffmpeg run per ticked audio track, and
`LoudnessConfig.GetFilter` is the `-filter:a:N` value the encode carries.

**The channel conversion has to be inside that filter, ahead of loudnorm.** The app's own channel
control is `-ac:a:N`, which ffmpeg applies *after* the filter chain - so loudnorm normalizes the source
layout and the downmix then moves the level out from under it. Measured on a 5.1 source asked for -16
LUFS: **-23.67 came out, 7.7 dB adrift, silently.** With `aformat=channel_layouts=` in the chain the same
source lands on -16.01, and the true-peak ceiling then applies to the signal actually written rather
than to one that gets mixed down afterwards. `CodecUtils.GetOutputChannelCount` is shared by the
measurement and the encoder arguments so the two cannot drift apart.

**The LRA target is derived from the measurement, not configured.** loudnorm drops to dynamic when the
target is under the track's own loudness range, and ffmpeg's default of 7 would force that on any film
mix, which routinely runs 10 to 25 LU. Taking it from the first pass and rounding up takes the loudness
range off the table, leaving the true-peak ceiling as the only thing that can rule a flat gain out.

**`GainFitsUnderTruePeak` is a necessary condition, not a sufficient one**, and the log says only what
that supports. A gain that would breach -1 dBTP certainly cannot be applied flat; the reverse does not
follow - measured, two sources whose gain fitted comfortably still came out dynamic, both perfectly
stationary noise measuring an LRA of exactly 0.00. Real programme material has not been seen to hit it,
but nothing here claims which mode ffmpeg chose.

**The trim goes with the measurement.** Measured over the whole file where only a section is written,
the numbers describe audio that is not in the output: the test source reads -19.2 LUFS whole and -45.2
for its quiet half alone. `QuickConvert.Run` resolves the section itself rather than reusing the
command's own trim arguments, which are split across the input and output sides by trim mode - loudness
does not need that frame accuracy, only the right span.

Stream copy is not decoded, so the box is disabled for one and `GetLoudnessConfig` reports Off. **The
AV1AN tab does not offer this**, and not by oversight: its audio arguments are deliberately unindexed -
av1an's own `-map 0` carries every track and the tab has one bitrate and one channel count for all of
them - so there is nowhere to put per-track measurements, and one track's numbers applied to all of them
would move the others to the wrong loudness. A file with more than one audio track has that stated
before the run - `Av1anUi.GetMultiTrackAudioNote` names the count, the one treatment every track gets,
and the remux-first way to choose otherwise, because on an ordinary remux that is a dozen tracks in
half a dozen languages and every one of them quietly getting the one setting is expensive and quiet.
FLAC is named without a bitrate, its arguments taking none (`QMax` 0).

Verified by running it: six source/channel/target combinations through the real `LoudnessConfig`,
measured and re-encoded through ffmpeg, every one landing within 0.01 dB of its target - the 5.1 to
stereo downmix included.

## Nothing on the Quick Convert tab is saved either

The same rule the AV1AN Video tab has, widened to a whole tab: the codec, the container, the quality
mode and its value, the preset, the colour format, the frame rate, the resize, the borders, the grain
synthesis, the deinterlacer, the tone-map, the audio codec, bitrate, channels and loudness target, the
subtitle codec, the metadata source and the Advanced tab's argument grid all start each session at
their defaults.
`LoadQuickConvertSettings` restores none of them, `SaveQuickConvertSettings` is gone rather than left
returning early, and the Quick Convert block came out of `LoadUiConfig`/`SaveUiConfig` with it.

The argument is the one already written out for the AV1AN tab. These settings describe a *job* rather
than a preference, and every way they go wrong is expensive and quiet: a resize left on 720p halves an
encode nobody meant to shrink, a CRF picked for a grainy film is the wrong number for line art, a target
bitrate left over from one source is meaningless against the next. Reset On New File already made that
argument for Trim, Crop and Deinterlace; this carries it to the whole tab and to the boundary that is
easiest to lose track of, which is a session that ended days ago.

**The defaults had to move rather than merely stop being restored**, and this is the part to be careful
with - it is the same trap the AV1AN tab hit. What selected an encoder was `Config`'s default for the
*saved* value, so dropping the restore on its own would have opened every session on the first entry of
the enum, which for video is Copy Video Without Re-Encoding and for audio is Copy Audio Without
Re-Encoding - a tab that encodes nothing - and dragged the quality, the preset and the colour formats
with it, since all three are filled per encoder. `QuickConvertUi.Init` names SVT-AV1 and Opus where the
boxes are filled instead, and that is now the only statement anywhere of what the tab opens as.

The numbers those two pull in are the encoders' own: `DirectSvtAv1.QDefault` is 30 and its
`PresetDefault` is 4, `Opus.QDefault` is already 128, and `InitQuickConvert` puts the channel box on
stereo. `DirectSvtAv1` is Quick Convert's alone - the AV1AN tab drives the same binary through
`VideoEncodersBin.SvtAv1` - so moving those two numbers moves nothing on the other tab.

Not writing them matters as much as not reading them, for the reason the AV1AN section gives: a value
saved and never restored is one the next person to touch that method will restore, reasonably enough,
and the setting then comes back from whatever session last happened to write it. Keys from before this
are still sitting in existing config files - do not wire one back up on the strength of finding one there.

The container is MKV, named there for the same reason the two codecs are: it is the one that takes every
codec offered here, it is what the AV1AN tab opens on, and with nothing restored *some* container becomes
the default whether or not anybody chooses one.

**Neither tab's Advanced grid keeps its values, and they are lost on an encoder switch as well as between
sessions.** That is a real cost rather than an oversight. `EncoderArgs.Load` rebuilds the rows from the
encoder's JSON every time an encoder is selected, and the saved store was the only thing carrying typed
values across that rebuild - so dropping it costs both at once. An advanced argument describes the encode
in front of you, and one left over from another source is expensive to have applied and easy not to notice.

The reading and writing were **deleted rather than left unused**: `EncoderArgs.Save` and `ReadSaved` are
gone and `Load` no longer takes a `Config.Key` at all. A store still written and no longer read is exactly
what somebody wires back up later, on the reasonable-looking grounds that the values are already there.
The `Av1anEncoderArgs` and `EncEncoderArgs` entries are still in `Config.Key` and still in existing config
files; nothing reads them.
