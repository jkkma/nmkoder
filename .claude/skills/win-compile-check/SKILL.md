---
name: win-compile-check
description: Compile-check Nmkoder's Windows-only code (#if WINDOWS, Windows App SDK, WindowsToast) from a Linux or macOS session. Use whenever a change touches code behind #if WINDOWS or the net10.0-windows target - which an ordinary `dotnet build` on this host never compiles - or the user asks whether the Windows build still compiles.
---

# Compile-checking `#if WINDOWS` code off Windows

`dotnet build Nmkoder/Nmkoder.csproj` on this host evaluates only `net10.0`: the
`net10.0-windows10.0.19041.0` framework exists only on a Windows host because its build runs
MSIX tooling (MakePri.exe and friends - Windows binaries). So **nothing under `#if WINDOWS`
is compiled or checked here**, and an edit to it that builds green locally proves nothing.

The check that works: a throwaway project targeting the Windows TFM with
`EnableWindowsTargeting=true`, compiling *just the files in question* plus stubs for what
they touch, referencing `Microsoft.WindowsAppSDK` compile-assets-only - excluding the build
assets is what skips the MSIX targets that cannot run here. That checks the code; the
*publish* is only ever proven by the release workflow's win-x64 job.

## Current inventory

`#if WINDOWS` lives in exactly one file today: `Nmkoder/OS/WindowsToast.cs`. Re-grep before
relying on that (`grep -rl '#if WINDOWS' Nmkoder/`), and add any new file to the check.

## Setup (in the scratchpad, never the repo tree)

```bash
W="$SCRATCHPAD/wincheck" && mkdir -p "$W"
cp .claude/skills/win-compile-check/assets/WinCheck.csproj "$W/"
SDK=$(grep -oP '(?<=Include="Microsoft.WindowsAppSDK" Version=")[^"]+' Nmkoder/Nmkoder.csproj)
sed -i "s|__WINAPPSDK_VERSION__|$SDK|; s|__REPO__|$(pwd)|" "$W/WinCheck.csproj"
dotnet build "$W/WinCheck.csproj"
```

The App SDK version is read out of the app's csproj so the check compiles against the API
surface that actually ships.

## Stubs

The listed files compile in isolation, so anything they reference from the rest of the app
(a `Logger`, a `Paths`) needs a minimal stub in a `Stubs.cs` beside the csproj - declare
just the members the file under check calls, in the same namespaces. Keep stubs honest:
matching signatures, no bodies beyond `{ }`/`=> default`. If the file leans on Avalonia
types, add the same `Avalonia` PackageReferences the app pins instead of stubbing them.

## Reading the result

- A **compile error in the file under check** is the finding - report it against the real
  file, not the harness.
- An error in a stub means the stub is wrong; fix the stub, not the app.
- A green build here says the Windows *code* compiles. It says nothing about the publish -
  the single-file + App SDK interactions (vanishing Content items, BaseDirectory
  redirection) only reproduce in the release workflow's win-x64 job, and CLAUDE.md's
  Notifications section is the history of why.

The `WindowsToast` pattern to preserve while editing: App SDK types are touched exclusively
from `NoInlining` helper methods, so a machine with a broken App SDK throws inside the `try`
that catches it rather than while JIT-compiling the caller.
