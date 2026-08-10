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
deleted from the repository's branches page by hand. Delete the local one and
leave the remote alone - **and do not report either fact.** The user has asked
not to hear about it again: it is the same sentence every release, about a
housekeeping detail they never asked for, and the queue of merged `claude/*`
branches sitting on the remote costs nothing. Never claim a branch *was*
deleted when only the local copy is gone; just say nothing about branches.

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

**A web container's clone is snapshotted with the environment and shallow, so its local
`master` is as old as that snapshot and its history is truncated.** Both are repaired by
`.claude/hooks/session-start.sh` now, which unshallows and fast-forwards the ref; the comment
there says why each half matters. What it is worth knowing anyway is the failure it produced,
because the shallow half does not look like a stale clone: ancestry across a graft boundary
answers "no", so a `master` that is only behind reads as *divergent*, `git merge --ff-only`
refuses it, and a search of `git log origin/master` misses commits that are on the remote.
That was reported once as a rewritten remote history, which it was not. `git fetch --unshallow`
before believing any of it.

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
`scoop bucket add nmkoder https://github.com/jkkma/nmkoder` and then
`scoop install nmkoder-avalonia` is all a Windows user needs. Scoop finds a bucket
by its `bucket/` directory, and one manifest per app is the whole of it.

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
`ScoopInstaller/Extras`, which is not a repository a session here can reach.

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
someone to clear a row that matches the encode is worse than saying nothing. `complex-hvs` is in the
message but not in the check: the tune sets it and the parameter list has no row for it, so there is
nothing to overwrite.

**The Denoise box beside it follows the strength as well as the encoder.** Both AV1 encoders read a
denoise flag only where they are synthesising grain at all - aomenc's `--enable-dnl-denoising`
applies "when denoise-noise-level is enabled", and SVT-AV1 answers one set against `--film-grain 0`
with "ignored when film grain is off" - so at a strength of 0 it was a tickable box that did
nothing. `Av1anUi.ApplyGrainDenoiseEnabled` is the one statement of that, called from
`VidEncoderSelected` and from the strength box's own handler. It does not *clear* the tick, only
disable it: a strength dropped to 0 and put back should bring the choice back with it.

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
more than it read, which is what `--ignore-frame-mismatch` is for.

**Every constant in `Av1anMemory` was measured**, by running the real process at three frame sizes and
fitting a line through the peak RSS. The fits are near-straight - SVT-AV1 came out at 672, 678 and 623 MB
per megapixel at 720p, 1080p and 1440p - so a base plus a slope is the whole model, and a third digit
would be inventing one. The spread between encoders is the part worth knowing: **SVT-AV1 wants two to
three times what the others do** (605 MB/MP against x264's 397, x265's 275 and VP9's 194, all 10-bit),
which is the same fact `ApplyWorkerCount` already acts on by giving it two workers fewer. A float step in
the chain is the other big term: a tone map converts to `gbrpf32le` at the *source's* size, 12 bytes a
pixel against a 10-bit 4:2:0 frame's 3, measured at 508 MB for a 3840x2076 source against 160 MB for the
same chain without it. The reported encode comes to 2.3 GB a worker and 24.8 GB for eleven.

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

**Where a tone-map or grain pass follows, the detection runs alongside it rather than after it.**
Those passes change pixels, never the frame count or order, so a list detected on their *input*
indexes their output frame for frame - and the two phases the workers cannot help with then hide
behind each other, the detection disappearing entirely into a grain measurement's hours. The
overlap starts after the deinterlacer on purpose: a bob writes one frame per field, which renumbers
everything behind it. The invariant carries a tripwire, `Av1anSceneDetect.DurationsMatchAsync` - a
header-cost duration comparison rather than a packet count, which would mean reading every byte of
a file that is now hundreds of gigabytes - that discards the list and lets av1an detect in-run if a
pass ever changes the timing; it catches the structural regressions (a doubled rate, a dropped
tail) and accepts that a single-frame drift would slip through to cost av1an its final chunk. On a
failed run the overlapped slices are wound down before the temp folder goes -
`SettleSceneDetectionAsync`, which has to kill them itself because `RunTask.Fail`, unlike `Cancel`,
kills no processes.

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
and the load-skips-detection behaviour is the part still to watch a real encode for: the log
should say "skips its own pass" and chunks should start within seconds of av1an launching.

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
the encode actually skipping its own pass - is still only watched for, so the retry stays.

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

The lists are filed apart - `bin/encoderArgs/av1an` against `bin/encoderArgs/ffmpeg`, keyed by the encoder
class name - and the values under a key each, because they are **different vocabularies rather than
different files**. The AV1AN tab drives standalone binaries and names their CLI parameters; Quick Convert
drives ffmpeg and names what the *wrapper* takes, which for VP9 and NVENC is not the same set of names at
all. Both folders are in the release workflow's post-publish check, for the reason the csproj gives.

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

**The SVT-AV1 behind Quick Convert is not the one behind the AV1AN tab.** `bundle-tools.sh` fetches
svt-av1-hdr as `SvtAv1EncApp`, which is av1an's encoder and nothing else's; Quick Convert runs `libsvtav1`
*inside* BtbN's ffmpeg, a library the bundler does not choose. Measured against that build - which reports
`SVT-AV1 Encoder Lib v4.1.0` - `hbd-mds`, `luminance-qp-bias`, `chroma-qm-min`, `ac-bias`, `max-tx-size`,
`variance-boost-curve` and `adaptive-film-grain` are all there, while `noise-adaptive-filtering`,
`tx-bias`, `cdef-scaling`, `kf-tf-strength`, `sharp-tx`, `alt-ssim-tuning`, `fgs-table` and the whole
`noise*` family are not. So `SvtAv1.json` and `LibSvtAv1.json` are two files on purpose and the second is
the shorter one. Do not "fix" it by pointing Quick Convert at the PSY list: those rows would be accepted
by ffmpeg, dropped by the library with a warning nothing here reads, and encode as though they had never
been set.

**Four rows now name an argument the Grain Synthesis row also writes, and the grid wins all four.**
`denoise-noise-level` and `enable-dnl-denoising` on libaom, `film-grain` and `film-grain-denoise` on
libsvtav1. On libaom that precedence is measured rather than arranged: the grid's `-aom-params
denoise-noise-level=N` beats the AVOption `LibAomAv1.GetArgs` writes - the files differ, and with
denoising on, `enable-dnl-denoising=0` changes the output again. On libsvtav1 both copies land in the
one `":"`-joined `-svtav1-params`, which ffmpeg parses with `av_dict_parse_string` into an AVDictionary
that replaces an equal key unless asked for `AV_DICT_MULTIKEY` - so the later entry is the one handed to
the library, and `GetArgs` appends the grid *after* the row deliberately to give both encoders the same
rule rather than a wash between them. That half is ffmpeg's documented dictionary behaviour rather than
something measured here; the libaom half is measured. Worth re-checking with a real encode.
`QuickConvertUi.GetGrainSynthProblem` names whichever pair has met, because from the outside the
number that runs is not the one the row is showing. Every other row is a parameter the app does not set.

Two of those four predate the row and were the whole of AV1 grain synthesis on this tab: with no
control to own it, `LibAomAv1.GetArgs` sent a hardcoded `-denoise-noise-level 0` and `enable-dnl-denoising`
sat in the grid beside it unable to do anything, that parameter only applying where the denoiser is on.
The partner row was added rather than the orphan deleted, which is why the grid can still reach all of
it - a grain setting typed there is still a supported thing to do, it is only no longer the only door.

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

**x264 and x265 get content presets; the other ffmpeg encoders do not, and the SVT-AV1 ones do not
carry over.** `EncoderArgPresets` is keyed by encoder name and the preset row hides itself for a name it
does not know, so an encoder without a considered set simply has no row rather than a bad one. The
SVT-AV1 presets are the AV1AN tab's and are written for parameters the library above does not have, so
they are not offered on the other tab at all. The two that were added were verified value by value
against the library inside the bundled ffmpeg - there is no runtime check to catch them the way there is
for av1an's, `Av1anEncoderName` answering "" for everything but SVT-AV1, so a wrong value there would be
dropped in silence.

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

**Whether av1an has the same bug is not known and is worth one check.** `model_path` left libvmaf
between ffmpeg 6.0 and 6.1 - 6.0 lists `model_path log_path log_fmt …`, 6.1 and everything since start
at `log_path` - so if av1an still builds `libvmaf=model_path=…` out of `--vmaf-path`, target-quality
encodes score against the built-in default and write an XML log over `bin/vmaf_v0.6.1.json`, which is
this same fault reached through av1an instead. There is no av1an binary in a web session to ask, so this
is unverified; `strings` on the bundled one at release time settles it.

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
one commit, on the strength of the measured note above that the v4.1.0 library has it - and the current
BtbN build (`v4.1.0-279-gd3c4cb394` when checked) **segfaults** the moment hbd-mds is set beside `tune`
(any value) or `enable-overlays`, at preset 4 and 8, 8- and 10-bit alike, while every parameter alone
runs fine. Found by running the translated presets against the real binary before shipping them, and
bisected to those pairs by leave-one-out. The shipped SvtAv1EncApp (`SVT-AV1-HDR v4.1.0-19-g8b4b9f562`,
pulled back out of the published 2.8.46 linux tarball and run) takes the same combinations cleanly - so
the AV1AN tab's presets, which pair `hbd-mds 1` with `tune 0` on that binary, are unaffected. With no
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
video into a lossless intermediate before av1an starts. 2.8.12 shipped that - a progressive 1080p
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
is the one place a deinterlaced MKV with its audio and subtitles copied is written, near-lossless
x264 for this utility's deliverable and lossless FFV1 for the AV1AN tab's intermediate, the split
its own doc explains - and its output is the deliverable rather than a step on the way to a tab. Until 2.8.10 it was that
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

## Grain synthesis

**The Grain Synthesis row is a mode selector that owns every way this app can put grain in an AV1
file, and that ownership is the point of it.** It was a strength spinner and a Denoise box, and what
made it worth changing was not that grav1synth exists - it is that there were already *three* ways to
ask SVT-AV1 for grain and they silently overrode each other. `--film-grain` sat on the row while
`--noise` and `--fgs-table` sat in the Advanced grid, and SVT takes exactly one of the three, in
`set_param_based_on_input`, with an `SVT_WARN` that goes to the encoder's stderr - which av1an collects
per chunk into a log `HandleTempFolder` deletes on a successful run. `GetGrainSynthProblem` existed to
report that collision after the fact. One control that writes at most one of them cannot express it.

The five modes are in `GrainSynthMode`, and what separates them is not how the grain looks:

| Mode | Where the description comes from | Cost |
|---|---|---|
| Encoder analysis | the encoder, from a strength | one number |
| Measured from source | grav1synth diffing the source against a denoised copy | a lossless intermediate and a full extra pass |
| Grain table file | a table the user already has | nothing, or the denoise pass on request |

**The row is what the encoder does while it encodes, and nothing else.** Grain written into a file that is
already encoded - a film stock preset, photon noise, or a table applied afterwards - is the Film Grain
utility's job and is not on this row at all. That is the division CLAUDE.md already states for Cut and
Deinterlace Video: utilities write a file, the tabs' own settings apply during an encode, and neither
reads the other's. `GrainSynthConfig.EncodeModes` is the row's list; the enum keeps `Preset` and
`PhotonNoise` because the utility uses this same class to say where its grain comes from.

**Both encode tabs carry the row, and the only thing that differs between them is which binary is
behind it.** `GrainSynthUi` drives both the way `ToneMapUi` and `DeinterlaceUi` do - one `Init`, one
`RefreshInfo` writing both readouts, per-tab config getters - so the modes, the panels, the readout
and the refusals are one implementation rather than two that drift. The AV1AN tab drives standalone
encoders, where SVT-AV1 is the svt-av1-hdr build this project bundles and has `--fgs-table`; Quick
Convert drives the libraries inside ffmpeg, where SVT-AV1 is mainline and has none.
`GrainSynthUi.GetTableFlag(VideoCodec)` is the one statement of that, and everything downstream reads
it: which delivery is likely, what the readout says, and what `Run` refuses.

**So Quick Convert offers all four modes and can carry out two of them.** Encoder analysis works on
both its AV1 encoders - `film-grain`/`film-grain-denoise` into `-svtav1-params`, `-denoise-noise-level`
/`-enable-dnl-denoising` as AVOptions on libaom - and the two table modes are **refused**, naming the
Film Grain utility as the way to put that table into the finished file. That is the same refusal the
AV1AN tab gives against a mainline SVT-AV1, and it is stated in the readout the moment the mode is
picked rather than only at Run, because on this tab the answer needs no binary: which parameters a
library inside ffmpeg has is settled by that build. Offering a mode this tab cannot deliver is
deliberate - the row was moved whole, and a mode that silently vanished per encoder would be the
setting-dropped-without-saying-so failure the rest of this file keeps arguing against.

`film-grain-table` is the one thing that could change that. aomenc takes it, and whether it survives
ffmpeg's `-aom-params` to reach libaom **has not been measured** - so `LibAomAv1.json` has no row for
it and `GetTableFlag` does not claim it, libaom being the one encoder here that refuses the whole
encode over a parameter it does not know. Returning the flag from that one method is the entire change
needed to light both table modes up on this tab once it has been measured.

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

The round trip was measured again after the solo tone-map intermediate went back to x264, because
that intermediate now sits between the source and the encoder's grain analysis, and "transparent on
grain energy" had only been measured on the intermediate alone. Through libsvtav1 (mainline
v4.1.0-279, the bundled ffmpeg's) and dav1d, on heavy synthetic grain at `film-grain=50` with
denoise: source HF energy 861.8, through the x264 intermediate 863.6, the AV1 with decoder
synthesis on 731.2 direct and **738.4 via the intermediate** - within 1% of each other - with the
coded picture itself at 11.3 (denoised clean, the grain living only in the table) and a no-synth
control at 13.2 (an encode without the feature strips the grain, which is the point of it). The
same session asserted the app's side of the contract out of the built assembly: SVT gets
`--film-grain 50 --film-grain-denoise 1` or `--fgs-table <path>` and never both, aomenc its
`--enable-dnl-denoising`/`--denoise-noise-level` or `--film-grain-table=` pair, table over strength
on each. What no web session can run is the bundled SvtAv1EncApp itself, so the `--fgs-table`
acceptance and a full av1an measured-grain run stay real-machine checks, as they always were.

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
this is `DenoisePass` either way, and `NeedsDenoisePass` and `NeedsMeasurement` are separate questions
now: Table with the tick does the first and not the second.

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
denoises - Measured from source, or Encoder analysis with Denoise ticked - and `tune` only at 5; a
strength with Denoise unticked is *consistent* with retention and says nothing.

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
a number that is silently discarded.

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

### The passes, and where they sit

`RenderDenoisedInput` is the third of the AV1AN tab's input passes, after the trim and the deinterlace
and on whatever they left: the grain has to be measured on the frames that will be encoded. It writes
`{tempDir}.denoised.mkv` and `{tempDir}.grain.tbl` beside the temp folder, exactly as the trimmed and
deinterlaced inputs are and for the same reason - av1an empties its own temp folder at startup, and a
resume must find both rather than spend the hours again. Both are deleted together on a failure: a
denoised file with no table beside it would be reused by the next resume as though it had been
measured, and encoded with no grain description at all.

**"The frames that will be encoded" includes their size, and for an SDR source that took a third
file to make true.** With a tone map in front the fused pass splits after the folded geometry, so
both halves of the measurement are the encoded frame; without one, the denoise pass renders the
geometry itself - and the raw input then no longer matches the denoised copy's frame size, so
grav1synth has nothing legal to diff against. `DenoisePass.RunWithReferenceAsync` therefore writes
a geometried copy of the input as a second output of the same command - `{tempDir}.grainref.mkv`,
video-only, split off before the denoiser so the pair differs by exactly the removed grain - and
the diff runs between the two renders. The reference is deleted the moment the diff succeeds: its
one reader is done, and it is a lossless file the length of the film. It is in `GetPreparedInputs`
anyway, for the run that dies mid-measurement, and the repair path re-makes it alone -
`RunReferenceAsync`, a geometry-only render - when a crash took it but left the denoised half. Both
this pass and the kept-file reuse guard share the tone-map pass's size check, because with geometry
baked into files a resume that changed the resize would otherwise encode at the wrong size with a
wrong-domain table beside it.

**The denoised copy carries the source's audio, subtitles and chapters, because av1an has no other
supply of them - and for as long as it was video-only, every Measured-mode encode was silent.**
av1an takes every non-video track from its `-i` input: the `-a` arguments are applied to that file,
and the attachment step waits on the `audio.mkv` its audio ffmpeg writes from that file. The
denoised file is that input in Measured mode, and `DenoisePass` wrote it `-map 0:v:0 -an` on the
strength of a comment claiming av1an "is given the audio separately out of the original" -
machinery that does not exist anywhere in this app. Found by reading the `-a` flow while folding
the geometry, not by a report: the mode is hours long and new enough that nobody had filed one. The
fused pass's second output had inherited the same shape and is fixed the same way; the tracks are
stream copies, and the disk they cost is the price of the output having sound.

**`DenoisePass` is lossless, and what began as its distinction is the whole pipeline's now: the
difference is what an output is for.** `DeinterlacePass` stays near-lossless x264 only for the
Deinterlace Video utility's deliverable, a file to be looked at, where CRF 12 is indistinguishable
and a tenth of the size; every AV1AN input pass - deinterlace included - is lossless FFV1. This one writes a file to be *measured
against*, and whatever a lossy codec adds is a difference between the two files that is not grain -
grain being precisely the small high-frequency signal a quantiser disturbs first. Hence FFV1, and a
temporary file larger than the source, which the row says before the run rather than a full disk saying
it during one.

**The denoiser is hqdn3d and spatial only**, its temporal halves pinned to 0. hqdn3d's temporal filter
is not motion compensated, so on anything that moves it blends the previous frame into the current one
and the difference between the two files there is a ghost rather than grain. It is not the best
denoiser ffmpeg has - nlmeans and bm3d both are - and it is the only one whose speed survives a whole
film, which this pass has to.

**The table is kept and the denoised copy is not.** `SaveMeasuredGrainTable` copies a measured table
beside the encode as `<output>.grain.tbl` before the temp data goes, because it is the one thing in there
worth more than the encode it belongs to: it took hours to measure, it is a few tens of kilobytes, and it
describes the source's grain rather than that encode's bitstream - so it is the input to every later
encode of the same film through the row's Grain table file mode. One caveat travels with it now that the
measurement happens at the encoded frame: the table fits later encodes with the same geometry and
tone-map settings, not any encode of the file, and the kept-table log line says so. The denoised
intermediate goes with the rest of the scratch
data, and `GetPreparedInputs` had to be taught about it: it matched `.trim.` and `.deint.` only, so every
measured encode leaked a lossless FFV1 copy of the whole video - the largest file this app writes - onto
the disk for good.

`ApplyGrainToOutput` runs after av1an and writes beside the output before replacing it, rather than in
place: this is a bitstream rewrite of a file that may have taken hours, from a young tool that says
itself that some videos fail to take grain properly, and the failure worth guarding against is the one
that leaves neither the original nor a working copy.

**A resume that replays a saved av1an command cannot post-apply**, because the saved command is av1an's
arguments and nothing else - there is nowhere in it for a grain setting to live, and the row on screen
describes the next encode rather than that one. It logs that rather than producing a file quietly
without grain. Resuming *with current settings* rebuilds the command and works normally.

### The Film Grain utility

The card is the same tool with the encode taken out of it, and it owns everything done to a file that is
already encoded: a table measured off a source before committing to the encode that will use it, a table
read back out of somebody else's encode, and grain written onto or stripped off a finished file - the film
stock presets and the photon noise among them, which is why the row does not offer either. `UtilFilmGrain` holds the four
operations; a utility that writes a file and stops, like Cut and Deinterlace Video beside it, with its own
settings and nothing reaching the encode tabs.

**One card and four operations rather than four cards.** Three of them take about as long as a remux, so a
card each would give the Utilities tab three more rows for something almost nobody does twice, and they act
on the same loaded file through the same binary.

**Measure is the odd one out twice over.** It is the only operation that does not need an AV1 input - it
diffs decoded frames, so it reads the grain off a ProRes master or a DVD rip perfectly well - and it is the
only one that costs anything, which is why the dialog states the estimate for the loaded file before the
operation is picked rather than after. The other three read and rewrite an AV1 bitstream and say so plainly
when handed anything else.

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

The `--fgs-table` path could not be exercised here: there is no SvtAv1EncApp in a web session, and the
libsvtav1 inside ffmpeg has no such option. What was measured end to end is the rest of the chain - the
denoise pass as `DenoisePass` builds it, the diff, an SVT-AV1 encode of the denoised file, `apply` with
the resulting table, and an `inspect` round trip reading the grain back out. The table format is aom's
own `filmgrn1`, which is what the parameter takes.

## Tone mapping

**Both encode tabs hide the Tone Mapping row for a file that is not HDR**, and unlike the
Deinterlace row above it, the setting behind it defaults to doing nothing. `ColorDataUtils.IsHdr`
decides which a file is and reads the **transfer curve alone** - 16 (PQ) or 18 (HLG). Wide *gamut*
is deliberately not enough: BT.2020 primaries under an ordinary BT.709 transfer is a colour space,
not a dynamic range, and tone-mapping is a luminance operation with nothing to say about it.

The default is Off because the other reason to load an HDR file is to re-encode it *as* HDR, which
is most of what this app's 10-bit AV1 encoding is for. Deinterlacing an interlaced file is what
almost everyone wants and that row opens armed; converting HDR to SDR is a choice, and an
irreversible one. So the row's whole job at rest is to say the file is HDR and that it will stay
that way. `ToneMapUi.ModeInEffect` reports Off whenever the row is off screen, which is what makes
hiding it safe rather than merely tidy - a curve left selected behind a hidden row would otherwise
convert a file nobody was looking at.

**`MediaFile.ColorData` is now filled in when a file loads**, on a background task beside the
interlace scan. Before this it was assigned in exactly one place - `Av1an.cs`, at encode time - so
Quick Convert had no colour data at all and nothing outside that one method could ask whether a
file was HDR.

### There are two backends, and the machine picks

libplacebo is the better tone-mapper and is used wherever a real GPU is behind it;
`ToneMapConfig`'s zscale chain is the fallback and is what every machine without one still gets.
`ToneMapUi.ResolveBackendAsync` settles which, once, at the start of each encode - the answer is a
property of the machine rather than a preference, and one decided halfway through would be a
different picture in the second half of the file. It is logged either way, because from the outside
a fallback is invisible: the same settings simply produce a slightly different picture than they did
on another machine.

Measured on PQ patches against a file declaring a 4000-nit mastering display, at 100 and 203 nits:
libplacebo's `hable` gives 115/143 where the zscale chain gives 108/144, and its top lands on **235**
- the nominal white of a limited-range signal - where the zscale chain runs to 247 and spends its
brightest highlights in the superwhite a player clips. **The curve names map straight across** -
libplacebo has `hable`, `mobius` and `reinhard` under those names - so those three entries mean the
same thing whichever backend they land on.

**Spline is the fourth entry and libplacebo's alone.** Mapping the names across is honest and buys
very little: hable against hable is about seven code values, so the better backend changed almost
nothing for the pick everybody uses. What is worth having is libplacebo's own default curve, and it
had no way to be selected - measured, `tonemapping=spline` is byte-identical to what its `auto`
chooses, and gives **129/152** at 100 and 203 nits against hable's 115/143. Appended to the enum
rather than slotted in beside the curve it beats, and labelled "Spline (GPU)" rather than left
looking like a fourth equal choice, because it is the one entry that cannot run everywhere: without
a usable GPU the zscale chain has nothing like it, so `GetCurveName` falls back to hable and
`ResolveBackendAsync` warns. **The log is the only place that can be said** - the readout is drawn
when the file loads, and which backend runs is not known until the encode starts.

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
answer and takes ~23 frames to converge - a visible pump, at up to one place per chunk. So Quick
Convert runs the filter inline, its chain being one ffmpeg over the whole file (two-pass runs it
identically twice), and **the AV1AN tab renders it in front as a pass of its own** -
`Media/ToneMapPass`, called from `Av1an.RenderToneMappedInput` - exactly the QTGMC argument one
filter over: a filter with temporal state cannot run inside av1an, which starts and stops the `-f`
ffmpeg around every chunk.

**The AV1AN tab's per-chunk chain had a second, quieter reason to lose libplacebo: it never saw the
metadata at all.** av1an feeds the `-f` ffmpeg through y4m pipes, and y4m carries no side data - so
a libplacebo in that chain read neither MaxCLL nor the mastering display for any file ever, and
priced everything for the 10000-nit ceiling: measured through the real pipe shape, 126/148 at
100/203 nits, the darkest reading of all. `Av1anUi.GetVideoFilterArgs` therefore puts only the
zscale chain in `-f` (whose peak is a number in the string, immune to the pipe) and carries no
Vulkan device argument any more; the pass's own command has both.

**The zscale chain renders in front too when a grain denoise pass follows, and
`Av1anUi.ToneMapRendersInFront` is the one statement of the whole decision.** The chain is
stateless, so per-chunk is normally fine for it - but the grain passes run on *files*, before av1an
starts, so a tone map still sitting inside av1an means the grain is measured on HDR frames while
the encoder receives SDR ones. A grain table's amplitudes live in its file's own signal domain:
measured off PQ and synthesised onto BT.709, the grain comes out wrong-strength, worst in what
used to be the highlights. The GPU path closed that mismatch by construction the day the pass
existed; the gate closes it for machines without one, at the cost of the pass those machines were
otherwise spared - paid only where a grain pass was already being paid for. Encoder-analysis grain
(`--film-grain N`) needs none of this per-chunk or in front: the encoder analyses the frames it is
handed, which are post-chain either way. On the zscale pass the command carries no Vulkan device
argument - that machine has none, and asking ffmpeg to create one fails the pass - and the 10-bit
pinning is a trailing `format=yuv420p10le` filter, zscale having no format option of its own.

The pass writes `{tempDir}.tonemap.mkv` beside the temp folder like the trim and QTGMC passes and
for their reason - av1an empties its temp at startup - sits after the deinterlace and before the
grain denoise (grain must be measured on the SDR frames being encoded), is reused by a resume, and
is in `GetPreparedInputs` so it is cleaned with the rest. Output pinned to 10-bit inside the filter
itself (`format=yuv420p10le` on libplacebo), because an output-side `-pix_fmt` lets the negotiation
land on 8 bits first and convert up after, baking banding in.

**The pass renders the tab's geometry too - the crop, the mod-2 pad, the resize or de-squeeze, and
the borders - and that fold is what sizes the intermediate to the encode instead of the source.**
Written at the source's frame, lossless FFV1 pays for pixels the encoder never sees: the resize
still sat in av1an's `-f`, so a 4K film scaled to 1080p rendered a 4K intermediate that every chunk
then scaled down - four times the pixels, reported as ~40 GB of tonemap.mkv for a *five-minute*
test clip. `Av1anFrame.GeometryInPass` is the statement of where the geometry runs and
`Av1anUi.BuildGeometryFilters` the one builder both homes share, so the two cannot drift; the pass
appends the chain after the tone map and the side-data deletes, which is the order the per-chunk
chain ran it in, and the fold was measured to change nothing but the size - the folded output is
framemd5-identical to the two-step it replaces. Two filters stay per-chunk on purpose. A bwdif in
`-f` blocks the fold entirely (the condition is the deinterlace filter string being empty), because
a deinterlacer must see whole fields and the pass runs first - geometry stays behind it, at the
source's size, exactly as before. And the fps resample never folds: it changes the frame *count*,
and the scene-detection overlap's whole invariant is that the passes change pixels, never count or
order - the slices index the pass's input. (Custom filter rows also stay per-chunk, and their order
holds: they always ran after the geometry, and the pass runs before the chain.)

Everything downstream of the fold moves in the same direction, and two of the moves are checked
guards rather than free wins. With `-f` empty the per-chunk filter ffmpeg disappears entirely -
`--pix-format-converter vs-resize` takes over, so each worker is two processes instead of three -
and the memory estimate prices the worker's decode at the encoded size
(`Av1anMemory.GetProblem`'s source argument), where it used to warn a 32 GB machine off a 1080p
encode for the 4K decodes it no longer does. The target-quality probes score the pass's output, so
`GetFilteredTargetQualityNote`'s size clause stands down when the geometry folded - it would
otherwise claim the probes run at a size they no longer do. The fused pass splits after the
geometry, so a measured grain table now lives in the *encoded* frame's domain rather than the
source's - and the standalone denoise pass folds the same way for SDR sources (see the grain
section's account of the reference file), so no measured table is in the wrong domain any more. And the resume guard exists because reuse got a new way to be wrong: a resume
with current settings re-reads the resize, and a kept file with the *old* geometry baked in would
be scaled twice or not at all - so `RenderToneMappedInput` ffprobes the kept file's frame size
against what this run expects (folded: `frame.Encoded`; unfolded: the source's), and a mismatch
re-renders, taking the denoised sibling and the grain table with it, since both were measured
against frames that are no longer the ones being encoded.

**The solo pass writes the measured-transparent x264 and the fused pass writes lossless FFV1, and
the whole history is worth keeping because every turn of it was either measured or the user's own
call.** The first cut shipped x264 CRF 12 `veryfast` on the claim that nothing downstream would
notice, and a measurement contradicted it: heavy grain keeps 90.5% of its high-frequency energy
through that (the preset's trellis 0, not the CRF - even CRF 3 veryfast only reaches 96.4%), 98.5%
through medium, and **100% through `fast` with `-tune grain`** - which is what 2.8.49 shipped,
measured transparent on grain energy and tone values alike at about a tenth of the source's size.
The user then chose lossless FFV1 over it - the intermediate is the file av1an encodes, so its
generation is the ceiling on the final picture - and traded the solo pass back to the x264 after
living with what lossless costs in practice: the first 4K test clip wrote ~40 GB of tonemap.mkv
for five minutes of video, and even with the geometry fold above taking three quarters of that
away, a lossless film is a temporary file in the tens of gigabytes. **The fused pair stays FFV1,
and that half is not a preference**: the graph splits into two independent encoders and grav1synth
then diffs the tone-mapped file against its denoised sibling, so a lossy reference would put the
quantizer's noise into the grain table as though it were grain - precisely the small
high-frequency signal a quantiser disturbs first. The solo shape has no diff, and the one way a
solo x264 file meets a measurement - the repair path - derives the denoised copy *from* that
file's own decoded frames, so both sides share one generation and the difference is still exactly
the grain. **x264's own lossless mode is not an FFV1 under another name: measured, 10-bit `-qp 0`
is not lossless** - high-bit-depth x264 shifts its QP scale, so 0 is no longer the lossless point -
and ffmpeg's wrapper refuses the negative QP that scale would need, where FFV1 round-trips
bit-exact. The fused outputs and the denoised file share `DenoisePass.Ffv1Args` for that reason;
the solo pass's x264 line lives in `ToneMapPass` beside the measurements that chose it.

**The tone map and the grain denoise render as one fused command when both would run** -
`ToneMapPass.RunFusedAsync`, gated exactly where the two passes meet: the graph splits after the
whole tone-map chain and writes the tone-mapped file and its denoised copy as two outputs of one
ffmpeg, so the film is decoded and tone-mapped once where the separate passes cost two. With both
outputs lossless, the grain measurement is exact - grav1synth diffs the rendered frames themselves,
no encode generation between. Failure semantics are the pair's: both files or neither, because a
resume that found one half would mistake a dead fused run for a finished pass -
`RenderToneMappedInput` deletes both on any failure, and `RenderDenoisedInput` keys its reuse on
the files rather than on the resume flag, so a denoised file the fused pass wrote is recognised on
a fresh run, and one missing its grain table skips only the render and still gets measured. The
separate `DenoisePass` remains as the repair path (a resume that kept the tone-mapped file but lost
the denoised half denoises from disk without re-rendering) and as the Film Grain utility's pass,
which is why the FFV1 statement lives on it. A failed pass fails the encode the way a
failed QTGMC pass does - the probe has already proven libplacebo renders on this machine, so a
failure here is the machine changing mid-run, not a normal path. Two things it buys beside
correctness: the target-quality probes score the SDR frames actually being encoded (per-chunk
filters are invisible to them), and a resume replays it for free.

**The probe is `ToneMap.GetLibplaceboProblem`, and asking only whether the device came up is not
enough.** Three things have to hold. The filter has to be in this ffmpeg - BtbN's builds carry it,
a distribution's may not. A Vulkan device has to come up, which libplacebo will not arrange for
itself: without `-init_hw_device vulkan` it fails with "Found no suitable device, giving up" and
then "Failed creating Vulkan device!", and **ffmpeg carries on and exits 0 having written nothing**.
And that device has to be a real GPU - measured against Mesa's lavapipe, libplacebo initialises
perfectly and then takes 8.4-13.1s over 48 frames of 1080p where the zscale chain takes 0.9-1.8s and
a plain pixel format conversion takes 0.27s. A software rasteriser passes every check except the one
that matters, and the cost lands hours into an encode.

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
**after the `-i`** - and with libplacebo now running in front of av1an rather than inside it, the
question of av1an's handling of the token is moot: only Quick Convert's command (through
`ToneMapConfig.GetDeviceArgs`) and the pass's own carry it, both after their `-i`, and the probe
places it in the same position so that what is tested is what ships.

macOS remains the platform with no Vulkan at all without MoltenVK, and bundles no ffmpeg either - the
probe simply answers "no" there and the zscale chain runs, which is what those users already had.

Verified by running it: every chain the real `ToneMapConfig` builds for both backends across four
colour-data shapes and all three curves - 24 of them - rendered through ffmpeg in both tabs' command
shapes, composed with a crop, a scale and a pad, each landing on the predicted frame size and tagged
bt709/bt709/bt709 limited. libplacebo hands software frames back to the filters after it, so the
geometry needs no `hwdownload` and none is emitted. The probe itself was run through the real code
against lavapipe and correctly refused it. The geometry fold was verified the same way, through the
real `ToneMapPass` out of the built assembly: the folded pass and the folded fused pass both land on
the encoded frame size at 10 bits, the pass geometry string is the real `ResizeConfig`'s own chain,
an unfolded frame hands back "", and the folded output is framemd5-identical to rendering the pass
at the source's size and scaling afterwards - same filters, same order, one process instead of two.
That identity proof lives on the fused FFV1 shape on purpose - two x264 encodes at different sizes
are not bit-comparable, so the lossless pair is where a filter-ordering difference would have
nowhere to hide - and the codec split itself is asserted beside it: solo outputs probe as h264
(High 10, encoded size, folded and unfolded), fused outputs as ffv1. The denoise pass's shapes are
proven the same way: the two-output render's reference is framemd5-identical to a geometry-only
render of the input and its denoised half to denoise-of-geometry, both at the encoded size, the
reference video-only and the denoised copy carrying the input's audio - as does the fused tone-map
pass's second output, which is the assertion that Measured encodes have sound again.

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
(the autocrop's shape, seconds of decoding), `signalstats` YMAX per frame, and the PQ curve back to
nits, depth and range handled off ffprobe's own report of the decoded format. It runs in
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

### Where it sits in the chain

Second, right after the deinterlacer and **before both subtitle burn-ins** - which on Quick Convert
means ahead of the bitmap overlay, which has to precede all the geometry. Subtitles are graphics
drawn to BT.709 white: composited into an HDR frame and tone-mapped afterwards they are dragged
through a gamut conversion and a highlight roll-off written for the picture. Measured on yellow
subtitle text, (240, 236, 95) burnt in before the tone-map against (232, 232, 71) after it - the
blue channel a third higher, which is the yellow washing out.

Ahead of the crop and the scale costs more than it needs to, this being the one filter here whose
cost is per pixel. It is paid anyway: tone-mapping before and after a downscale was measured at a
maximum difference of 3 code values out of 255, so the position is a subtitle question and not a
picture one.

### The output colour, and the trap on the AV1AN tab

On Quick Convert nothing has to be said about the output at all. The final `zscale` retags the
frames as it goes, so the file comes out tagged bt709/bt709/bt709 - verified in the real
`-filter_complex`/`[vf]`/`-map`/`-pix_fmt` command shape, not just as a bare `-vf`.

**The HDR side data is a separate matter, and "the chain drops it" was an encoder-dependent
observation mistaken for a chain property.** Through libsvtav1 nothing carries frame side data into
the file, so it looked dropped; through libx265 - a wrapper that maps mastering-display and
light-level side data straight to encoder parameters - both chains produced an SDR BT.709 file
declaring a 4000-nit mastering display and a MaxCLL of 9978. `ToneMapConfig.HdrSideDataDeletes` now
ends both chains: four `sidedata=mode=delete` filters taking out the mastering display, the light
levels and both Dolby Vision entries, the last because an RPU describing the reshaping of frames
that have since been tone-mapped is not merely stale but wrong, and the x265 wrapper can write RPUs
too. Verified through x265 on both backends: zero HDR side data entries on the output, band values
unchanged.

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

### What it does not cover

A stream copy builds no filter chain, so the Quick Convert box is disabled for one and
`ToneMapUi.GetQuickConvertConfig` reports Off - a copy of an HDR file is the one way to keep it
exactly as it is, which is an ordinary thing to want, so the row stays on screen saying the file is
HDR and only the curve is taken away. av1an's target-quality probes are two stories now: on the GPU
path the tone map is baked into the input the pass renders, so the probes score the real SDR
frames; the zscale chain still runs per chunk inside `-f`, invisible to them like every other
filter there, and the existing note covers it by counting the chain.

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
0), every branch of `GetEffectivePeakNits`, both chains' filter strings, and the real
`ToneMapPass.RunAsync` end to end - output tagged bt709/limited, 10-bit, zero HDR side data, and
band-for-band identical to the continuous peak-detection reference. The chunk-seam number came from
rendering a 120-frame brightness ramp whole and again from frame 60 and comparing a constant band
frame by frame; the y4m stripping from piping the strip through av1an's exact pipe shape.

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
would move the others to the wrong loudness.

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

The numbers those two pull in are the encoders' own: `LibSvtAv1.QDefault` is 30 and its `PresetDefault`
is 4, `Opus.QDefault` is already 128, and `InitQuickConvert` puts the channel box on stereo. `LibSvtAv1`
is Quick Convert's alone - the AV1AN tab drives SVT through `VideoEncodersBin.SvtAv1` - so moving those
two numbers moves nothing on the other tab.

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
