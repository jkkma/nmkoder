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

**`hooks/session-start.sh`** (SessionStart) - per-session git repair (unshallow +
fast-forward master) and the toolchain report. Installs nothing, on purpose; unchanged.

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
- **A repo `.mcp.json` for the servers that already arrive elsewhere.** `github` (release
  dispatch) and `avalonia_docs` (with the usage limits CLAUDE.md records: `max_results`
  1-2, ignore its MVVM rules, prefer the NuGet XML docs for API facts) come via the
  platform and user config, so a repo copy would double them up. Microsoft Learn's server
  *is* in `.mcp.json` now - one entry, for BCL/App SDK questions - added at the user's
  request after this section first shipped.
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
