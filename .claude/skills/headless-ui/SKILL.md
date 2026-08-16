---
name: headless-ui
description: Render Nmkoder's real UI to PNGs without capturing the screen - screenshot the MainWindow tabs, open dialogs, force hover/press/focus states, and measure rendered control geometry. The way to see the UI on the desktop, whose sessions cannot capture the screen (on the laptop, launching the app and screenshotting its window is simpler - see .claude/README.md). Use whenever a change touches .axaml or view code-behind and needs to be seen or measured, or the user asks to screenshot, look at, verify, or check the layout of any window, tab, dialog, control, style, or palette change.
---

# Seeing the UI without capturing the screen

A local session on the desktop cannot capture the screen, but the UI can still be rendered for
real: a throwaway console project referencing `Nmkoder.csproj` plus `Avalonia.Headless` and
`Avalonia.Skia` draws through the same Skia the app ships, so what comes out is the actual
pixels - CLAUDE.md's UI conventions were all verified this way, and it is also the only way to
force hover/press/focus states or measure control geometry on either machine. **Look at the
PNGs, do not just confirm they were written.**

## Setup (once per session, in your scratchpad directory - never in the repo tree)

```bash
H=<your-scratchpad-dir>/uiharness && mkdir -p "$H"
cp .claude/skills/headless-ui/assets/Harness.csproj .claude/skills/headless-ui/assets/Program.cs "$H/"
AV=$(grep -oP '(?<=Include="Avalonia" Version=")[^"]+' Nmkoder/Nmkoder.csproj)
REPO=$(cygpath -m "$(pwd)" 2>/dev/null || pwd)   # Windows path under Git Bash, plain pwd elsewhere
sed -i "s|__AVALONIA_VERSION__|$AV|; s|__REPO__|$REPO|" "$H/Harness.csproj"
dotnet run --project "$H" -- "$H/shots"
```

The Avalonia version is read out of the app's csproj so the harness cannot drift from the
pinned 12.1.x - a headless package from another minor renders another theme. The `cygpath` is
because under Git Bash `$(pwd)` is `/c/Users/...` - a path MSBuild cannot resolve inside a
csproj, so the ProjectReference silently points at nothing; `cygpath -m` turns it into
`C:/Users/...` and is a no-op on any other shell. Verified rendering all six tabs from Git Bash
on Windows. On a Windows host `Nmkoder.csproj` multi-targets; the harness is `net10.0`, so the
ProjectReference builds and loads the `net10.0` flavour, which is fine - the UI is the same in
both.

The template's `InitAppState()` runs `Paths.Init()` and `Config.Init()` by reflection (both
classes are app-internal) before any window is constructed - the same order `Program.Main`
runs them in - so the frame is a faithful first launch: without them every shot carries
`Failed to save settings to '': Value cannot be null` lines in the visible log box. The
config this writes lands in a `data/` folder beside the harness exe, in scratch, deleted
with `bin/`; the app's real state is never touched. If those lines still appear in a shot,
the init didn't run - that is a harness fault, not an app finding. Verified working end to
end: the template builds clean against the real project and renders all six tabs with an
empty log box, the AV1AN tab showing its documented session defaults.

## What the template does, and the knobs that matter

- `AppBuilder.Configure<Nmkoder.App>().UseSkia().UseHeadless(new
  AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting()` -
  `UseHeadlessDrawing = false` is what routes drawing through Skia; true renders nothing.
- The lifetime is null, so `App` opens no window itself: construct `MainWindow` (or any
  dialog - they all have parameterless constructors) directly and `Show()` it.
- **Pump the dispatcher** (`Dispatcher.UIThread.RunJobs()` in a loop) for a second or two
  after showing and after every state change - the window's async startup and every
  `ValueChanged` cascade settle on that queue, and a frame captured too early shows
  half-loaded state.
- `MainTabs.SelectedIndex` switches tabs between shots; `CaptureRenderedFrame()` saves the
  PNG. Resize with `win.Width`/`win.Height` to check the wrap points (the Video tabs'
  three-column behaviour lives at specific widths - see CLAUDE.md).

## States nobody can click headless

Hover, press and focus are reachable by forcing pseudo-classes on the template part:

```csharp
((IPseudoClasses)control.Classes).Set(":pointerover", true);   // also ":pressed", ":focus"
```

This is the only way to see what a restyled control does before shipping it. Check disabled
states too (`control.IsEnabled = false`) - the palette's `BaseLow` history in CLAUDE.md is
what happens when only the enabled state is looked at.

## Measuring instead of eyeballing

Alignment claims are checked by geometry, not by eye: translate both controls to window
coordinates and compare -

```csharp
var p = control.TranslatePoint(new Point(0, 0), win) ?? default;
// p.X / p.Y, plus control.Bounds.Width/Height, in window space
```

Runtime property reads (`VerticalAlignment`, `Margin`) beat trusting a style selector - a
later matching style wins silently, and printing the value is how that was caught.

## Cleanup

The harness is scratch: never commit it, never leave it in the repo tree. Send the PNGs to
the user when the point is for them to see the result.
