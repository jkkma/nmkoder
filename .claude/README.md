# The Claude Code automation layer

What lives under `.claude/`, why each piece earns its place, and what was considered and
deliberately not adopted. CLAUDE.md stays the authority on the project itself; everything
here exists to make its recurring procedures executable instead of re-derived per session.

## In place

**`setup.sh`** - the environment's one installer (see its header): the .NET SDK, the BtbN
ffmpeg the app ships against, and now the measurement toolkit - `x264`, `x265`, `aomenc`,
`vpxenc`, `mkvtoolnix` from the archive, plus the shipped PSY-line `SvtAv1EncApp` and
`grav1synth` extracted from the latest published linux-x64 release. Every step is
presence-gated (a re-run is ~1s) and failure-tolerant, and the final `toolkit:` line lists
what actually landed rather than what was intended.

**`setup-windows.sh`** - the local counterpart, for the laptop and the desktop, where
`setup.sh` (apt-get, `/usr/local/bin`) has never run. `bash .claude/setup-windows.sh` from Git
Bash in the repo: checks the SDK, builds if nothing is built, pulls the *shipped* toolchain out
of the latest published win-x64 zip (~485 MB once, the bundler's own output - PSY-line
SvtAv1EncApp, av1an, VapourSynth, MKVToolNix, grav1synth, BtbN ffmpeg) into `~/.nmkoder-dev/bin`,
hardlinks it into every build output's `bin/` beside `Nmkoder.exe` (the only place the app looks
- its launched-tool PATH is squeezed to `bin/` + `C:\Windows`, so a Scoop encoder is invisible
to it), and appends the four tool folders to the user PATH. Presence-gated, ~5 s on re-run;
re-run after `dotnet clean`, a fresh clone or a worktree. The build's own `BinFiles/` copies
are never overwritten by the release's. Verified by launching the Debug build: its startup probe
rendered a QTGMC frame through the staged VapourSynth and listed grav1synth's presets.

  Two things it had to learn the hard way, both in its header: **Claude Desktop is a packaged
  app with file-system write virtualization on**, so anything a session writes under
  `%LOCALAPPDATA%` (or `%TEMP%`) lands in `…\Packages\Claude_<id>\LocalCache\Local\…` and is
  invisible outside Claude - `fsutil hardlink list <file>` prints the real path, which is how it
  was caught; the profile root, `~/scoop` and the repo are not virtualized, and registry writes
  are not either (the manifest disables that half), which is why the PATH edit is real. And Git
  Bash's `unzip` does not cross `/` with `*` here, so `Nmkoder/bin/*` yields only `bin/`'s
  top-level files - Windows' own `tar.exe` (bsdtar) with a bare `Nmkoder/bin` pattern is what
  extracts the subtree.

  On the laptop the app can simply be launched and screenshotted, and the headless-ui skill
  below - the web container's substitute for a display - is not needed there. Launch
  `Nmkoder/bin/Debug/net10.0-windows10.0.19041.0/win-x64/Nmkoder.exe` (the release's shape,
  App SDK notifications and all; `…/net10.0/Nmkoder.exe` is the cross-platform one), and
  capture its window from pwsh with `SetProcessDPIAware()` called **before** `GetWindowRect` -
  without it, on a scaled display, `GetWindowRect` reports logical pixels and
  `Graphics.CopyFromScreen` reads physical ones, and the PNG is the top-left quarter of the
  window blown up. Its `data/` and `logs/` land beside the exe in that output folder. **A local
  session on the desktop cannot capture the screen** (reported by the user; the cause is not
  established), so there the headless-ui skill stays the way to see the UI - it renders through
  Avalonia.Headless and never touches the display. Its setup line is written for Linux; under Git
  Bash the repo path handed to `__REPO__` has to be a Windows one (`cygpath -m`), which the
  skill now does.

**`hooks/session-start.sh`** (SessionStart) - in a web container, per-session git repair
(unshallow + fast-forward master) and the toolchain report; on a local machine, one
`git pull --ff-only` of the checked-out branch at startup, resume and clear (not compact),
because the user works on a laptop and a desktop in tandem and a session should open on
what the other machine pushed. Fast-forward or nothing - a diverged branch, a dirty file in
the way or no network leaves the tree as it was and says so in the hook's one line. Installs
nothing, on purpose. Verified by running it against throwaway repos on Windows Git Bash:
up to date, ff by 2, ff past an unrelated untracked file, conflicting edit refused, diverged
refused, no upstream, detached HEAD, unresolvable host, blackhole host (curl's own 21s
connect timeout answers before the 45s `timeout` backstop), not a repo - all exit 0.

**`hooks/git-push-guard.sh`** (PreToolUse on Bash) - denies, with the explanation and the
alternative, the two pushes the sandbox's git proxy hangs up on every time: tag pushes
(release.yml's dispatch path creates the tag itself) and remote ref deletions (no
workaround exists; local delete only, and say nothing about branches). Remote sessions
only - `CLAUDE_CODE_REMOTE` gates it, so local machines are untouched. It matches the whole
command text, so a command that merely *quotes* `git push --tags` is denied too; run such
strings from a script file. Battery-tested (24 cases) in `settings.json`'s registration.

**`settings.json`** - registers both hooks and carries a small permissions allowlist
(builds, ffmpeg/ffprobe, read-only git) so local sessions prompt less. Web sessions run
permissive anyway; trim or extend freely.

**Skills** (`skills/*/SKILL.md`) - the four procedures sessions kept rebuilding from
CLAUDE.md prose, distilled to executable form with templates, each validated by running it:

- **cut-release** - the whole cutting-a-release order of operations, hard rules first, plus
  post-release asset and Scoop-manifest verification. (Named `cut-release` because a skill
  directory called `release/` is silently swallowed by `.gitignore`'s `[Rr]elease/` build
  pattern - it happened, on the first commit of this layer.)
- **headless-ui** - render the real UI to PNGs with Avalonia.Headless; templates build
  against the live csproj and rendered all six tabs here.
- **win-compile-check** - compile `#if WINDOWS` code from Linux via EnableWindowsTargeting
  and a compile-assets-only App SDK reference; proven by a negative control (a planted
  error inside the guarded block fails the build).
- **real-binaries** - where every shipped tool comes from in a web session, the egress
  proxy's measured truth table (api.github.com answers only for the attached repo; HTML is
  403; `releases/download` and absolute ranges pass; suffix ranges are 501), and
  `scripts/fetch-zip-member.py`, which pulls one member out of a remote zip by ranged
  requests - the shipped `av1an.exe`, 6 MB out of a 485 MB asset.

**Agents** (`agents/verifier.md`) - a subagent for the verified-by-running-it culture:
builds throwaway harnesses in the scratchpad, measures against the real binaries, reports
numbers compactly. Delegating keeps a few hundred harness commands out of the main
conversation's context.

## Considered and not adopted

- **A build-the-project hook** (PostToolUse on Edit, or on Stop). A full `dotnet build` is
  15-70s here; per-edit would be hostile and per-turn noisy, and sessions already build
  when it matters. Revisit as an async Stop hook only if unbuilt pushes actually start
  happening.
- **A guard on `bucket/nmkoder-avalonia.json`.** The rule ("never hand-edit version, url,
  hash - the workflow rewrites them") is in CLAUDE.md and the release skill; a PreToolUse
  "ask" would also catch legitimate edits to `persist`/`notes`. Add one only if a hand-edit
  ever actually ships.
- **A repo `.mcp.json`.** Every server this project uses already arrives another way -
  `github` (release dispatch) from the platform, and `avalonia_docs` (usage limits per
  CLAUDE.md: `max_results` 1-2, ignore its MVVM rules, prefer the NuGet XML docs for API
  facts) and Microsoft Learn (BCL/App SDK questions) as the user's claude.ai connectors,
  which reach the cloud sessions this app is developed in. A repo copy would double them
  up - and a remote-OAuth entry in `.mcp.json` additionally nags "requires authentication"
  at the start of every web session, since a headless container cannot run the browser
  flow. One was added here briefly and reverted for exactly that.
- **The `fewer-permission-prompts` skill** mines local transcript history for an allowlist;
  a fresh web container has none, so run it from a local session if prompts get annoying.
- **More agents** (release-auditor, doc-checker). Their knowledge lives in the skills,
  which any session or the verifier can follow; a second agent per procedure would just be
  the skill wearing a costume.

One observation, the user's call entirely: CLAUDE.md is ~290 KB and loads into every
session's context whole. The skills here demonstrate the pressure valve if it keeps
growing - a section that is a *procedure* (as opposed to an invariant about the code) can
move to a skill and load only when its task comes up.

## Maintenance

Measured facts in the skills are stamped with the release they were measured on (2.8.60
today: no av1an in the linux tarball, no `model_path` in the shipped av1an, grav1synth's
stale 0.2.0 banner). Re-measure on drift rather than trusting them forever - the bundler
tracks rolling upstreams, which is the same rule CLAUDE.md applies to ffmpeg's stats line.
