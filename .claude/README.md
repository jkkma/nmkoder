# The Claude Code automation layer

What lives under `.claude/`, why each piece earns its place, and what was considered and
deliberately not adopted. CLAUDE.md stays the authority on the project itself; everything
here exists to make its recurring procedures executable instead of re-derived per session.

Development happens on the user's two Windows machines - a laptop and a desktop, worked in
tandem - from Git Bash. It used to happen in Claude Code on the web's Linux containers as
well, and this layer was first built for those; the container-only pieces (`setup.sh`, the
`git-push-guard` hook, the `real-binaries` and `win-compile-check` skills) were removed in
August 2026 when that stopped. What is left is what a local machine uses.

## In place

**`setup-windows.sh`** - the one installer. `bash .claude/setup-windows.sh` from Git Bash in
the repo: checks the SDK, builds if nothing is built, pulls the *shipped* toolchain out of the
latest published win-x64 zip (~485 MB once, the bundler's own output - PSY-line SvtAv1EncApp,
av1an, VapourSynth, MKVToolNix, grav1synth, BtbN ffmpeg) into `~/.nmkoder-dev/bin`, hardlinks it
into every build output's `bin/` beside `Nmkoder.exe` (the only place the app looks - its
launched-tool PATH is squeezed to `bin/` + `C:\Windows`, so a Scoop encoder is invisible to it),
and appends the four tool folders to the user PATH. Presence-gated, ~5 s on re-run; re-run after
`dotnet clean`, a fresh clone or a worktree. The build's own `BinFiles/` copies are never
overwritten by the release's. Verified by launching the Debug build: its startup probe rendered
a QTGMC frame through the staged VapourSynth and listed grav1synth's presets.

  Two things it had to learn the hard way, both in its header: **Claude Desktop is a packaged
  app with file-system write virtualization on**, so anything a session writes under
  `%LOCALAPPDATA%` (or `%TEMP%`) lands in `…\Packages\Claude_<id>\LocalCache\Local\…` and is
  invisible outside Claude - `fsutil hardlink list <file>` prints the real path, which is how it
  was caught; the profile root, `~/scoop` and the repo are not virtualized, and registry writes
  are not either (the manifest disables that half), which is why the PATH edit is real. And Git
  Bash's `unzip` does not cross `/` with `*` here, so `Nmkoder/bin/*` yields only `bin/`'s
  top-level files - Windows' own `tar.exe` (bsdtar) with a bare `Nmkoder/bin` pattern is what
  extracts the subtree.

**`hooks/session-start.sh`** (SessionStart) - one `git pull --ff-only` of the checked-out
branch at startup, resume and clear (not compact), so a session opens on what the other
machine pushed. Fast-forward or nothing - a diverged branch, a dirty file in the way or no
network leaves the tree as it was and says so in the hook's one line. Installs nothing, on
purpose. Verified by running it against throwaway repos on Windows Git Bash: up to date, ff by
2, ff past an unrelated untracked file, conflicting edit refused, diverged refused, no upstream,
detached HEAD, unresolvable host, blackhole host (curl's own 21s connect timeout answers before
the 45s `timeout` backstop), not a repo - all exit 0.

**`settings.json`** - registers the hook and carries a small permissions allowlist (builds,
ffmpeg/ffprobe, read-only git) so sessions prompt less. Trim or extend freely.

**Skills** (`skills/*/SKILL.md`) - two procedures sessions kept rebuilding from CLAUDE.md
prose, distilled to executable form with templates, each validated by running it:

- **cut-release** - the whole cutting-a-release order of operations, hard rules first, with
  `gh` for the dispatch and the watch, plus post-release asset and Scoop-manifest
  verification. Its `scripts/fetch-zip-member.py` lists or pulls one file out of a remote zip
  by ranged requests - the shipped `av1an.exe`, 6 MB out of a 485 MB asset - for spot-checking
  what a just-published release carries; re-running `setup-windows.sh` is the other way, and
  stages the new tools beside the local build while it is at it. (Named `cut-release` because
  a skill directory called `release/` is silently swallowed by `.gitignore`'s `[Rr]elease/`
  build pattern - it happened, on the first commit of this layer.)
- **headless-ui** - render the real UI to PNGs with Avalonia.Headless; the template builds
  against the live csproj (under Git Bash its `__REPO__` goes through `cygpath -m`, or MSBuild
  cannot resolve the `/c/...` path). **This is the desktop's way to see the UI** - a local
  session there cannot capture the screen (reported by the user; cause not established) - and
  the only way on either machine to force hover/press/focus states or measure control geometry.

  On the laptop the app can simply be launched and screenshotted. Launch
  `Nmkoder/bin/Debug/net10.0-windows10.0.19041.0/win-x64/Nmkoder.exe` (the release's shape,
  App SDK notifications and all; `…/net10.0/Nmkoder.exe` is the cross-platform one), and
  capture its window from pwsh with `SetProcessDPIAware()` called **before** `GetWindowRect` -
  without it, on a scaled display, `GetWindowRect` reports logical pixels and
  `Graphics.CopyFromScreen` reads physical ones, and the PNG is the top-left quarter of the
  window blown up. Its `data/` and `logs/` land beside the exe in that output folder.

**Agents** (`agents/verifier.md`) - a subagent for the verified-by-running-it culture:
builds throwaway harnesses in the scratchpad, measures against the shipped binaries in
`~/.nmkoder-dev/bin`, reports numbers compactly. Delegating keeps a few hundred harness
commands out of the main conversation's context.

**Evals** (`evals/`) - the benchmark suite for the two skills, and how to run a pass.

## Considered and not adopted

- **A build-the-project hook** (PostToolUse on Edit, or on Stop). A full `dotnet build` is
  15-70s; per-edit would be hostile and per-turn noisy, and sessions already build when it
  matters. Revisit as an async Stop hook only if unbuilt pushes actually start happening.
- **A guard on `bucket/nmkoder-avalonia.json`.** The rule ("never hand-edit version, url,
  hash - the workflow rewrites them") is in CLAUDE.md and the release skill; a PreToolUse
  "ask" would also catch legitimate edits to `persist`/`notes`. Add one only if a hand-edit
  ever actually ships.
- **A repo `.mcp.json`.** The `avalonia_docs` server (usage limits per CLAUDE.md:
  `max_results` 1-2, ignore its MVVM rules, prefer the NuGet XML docs for API facts) and
  Microsoft Learn (BCL/App SDK questions) arrive as the user's own connectors, and `gh`
  covers everything the GitHub server did. A repo copy would double them up.
- **The `fewer-permission-prompts` skill** mines local transcript history for an allowlist;
  run it from a session if prompts get annoying.
- **More agents** (release-auditor, doc-checker). Their knowledge lives in the skills,
  which any session or the verifier can follow; a second agent per procedure would just be
  the skill wearing a costume.

One observation, the user's call entirely: CLAUDE.md is ~290 KB and loads into every
session's context whole. The skills here demonstrate the pressure valve if it keeps
growing - a section that is a *procedure* (as opposed to an invariant about the code) can
move to a skill and load only when its task comes up.

## Maintenance

Measured facts in the skills are stamped with the release they were measured on (2.8.66
today: grav1synth back in the win-x64 zip after 2.8.65's transient miss; no av1an in the
linux tarball as of 2.8.60). Re-measure on drift rather than trusting them forever - the
bundler tracks rolling upstreams, which is the same rule CLAUDE.md applies to ffmpeg's stats
line.
