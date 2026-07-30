# Nmkoder

Media encoding/muxing toolkit. Avalonia UI on .NET 10.

Build with `dotnet build Nmkoder/Nmkoder.csproj`. The SessionStart hook in
`.claude/hooks/` installs the SDK and restores packages, so this works from the
first prompt of a web session.

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
proxy takes branch pushes but hangs up on tag pushes:

```
send-pack: unexpected disconnect while reading sideband packet
fatal: the remote end hung up unexpectedly
Everything up-to-date
```

It fails identically every time, so retrying with backoff only burns time - this
is a property of the sandbox, not of the repository, the tag, or GitHub. The
workflow's dispatch path exists precisely to work around it and creates the tag
itself.

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

The run builds win-x64, linux-x64, osx-x64 and osx-arm64, bundles external tools
via `.github/scripts/bundle-tools.sh`, and composes notes from
`git log --no-merges` since the previous tag. It takes roughly six minutes.

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
