---
name: headless-ui
description: Render Nmkoder's real UI to PNGs in a session with no display - screenshot the MainWindow tabs, open dialogs, force hover/press/focus states, and measure rendered control geometry. Use whenever a change touches .axaml or view code-behind and needs to be seen or measured, or the user asks to screenshot, look at, verify, or check the layout of any window, tab, dialog, control, style, or palette change.
---

# Seeing the UI without a display

There is no display in a web session, but the UI can still be rendered for real: a throwaway
console project referencing `Nmkoder.csproj` plus `Avalonia.Headless` and `Avalonia.Skia`
draws through the same Skia the app ships, so what comes out is the actual pixels - CLAUDE.md's
UI conventions were all verified this way. **Look at the PNGs, do not just confirm they were
written.**

## Setup (once per session, in your scratchpad directory - never in the repo tree)

```bash
H=<your-scratchpad-dir>/uiharness && mkdir -p "$H"
cp .claude/skills/headless-ui/assets/Harness.csproj .claude/skills/headless-ui/assets/Program.cs "$H/"
AV=$(grep -oP '(?<=Include="Avalonia" Version=")[^"]+' Nmkoder/Nmkoder.csproj)
sed -i "s|__AVALONIA_VERSION__|$AV|; s|__REPO__|$(pwd)|" "$H/Harness.csproj"
dotnet run --project "$H" -- "$H/shots"
```

The Avalonia version is read out of the app's csproj so the harness cannot drift from the
pinned 12.1.x - a headless package from another minor renders another theme.

Two lines of `Failed to save settings to '': Value cannot be null` on stdout (and in the log
box) are the app's config path resolving against the harness's own output directory - they
are cosmetic here and not a finding. Verified working end to end: the template builds clean
against the real project and renders all six tabs, with the AV1AN tab showing its documented
session defaults.

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
