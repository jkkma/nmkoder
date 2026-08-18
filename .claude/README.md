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

**`hooks/warn-if-gitignored.sh`** (PreToolUse on Write) - asks before creating a file git has
been told to ignore, naming the `.gitignore` line that matched. The stock Visual Studio
`.gitignore` ignores `[Bb]in/`, `[Ll]og/`, `[Ll]ogs/`, `[Dd]ebug/` and `[Rr]elease/`, and this
project uses every one of those words for something of its own - which is why the tracked tool
data lives under `BinFiles/`, and why the release skill is called `cut-release` after
`.claude/skills/release/` was swallowed on this layer's first commit. The write succeeds, `git
status` says nothing, and the file is absent from the commit; there is no error anywhere.
Verified by running it, nine cases: the `release/` path and `logs/`, `Nmkoder/bin/` fire
(quoting `.gitignore:22`, `:32`, `:29`); an ordinary source path, a tracked file, a scratchpad
path outside the repo, an escaped `"file_path"` decoy inside the written content, garbage input
and empty stdin are all silent. Every path exits 0. It cannot see a file written by a Bash
heredoc, which its header says outright rather than implying coverage it does not have.

**`hooks/warn-if-unpushed.sh`** (Stop) - the other end of `session-start.sh`. That one closes
the two-machine gap where a session *opens* on a clone the other has moved past; nothing closed
the end where a session *finishes* with commits never pushed, which is the state that makes the
other machine's next pull a no-op over stale code. One local `git rev-list --count` and no
fetch. Silent at zero and silent again at a count already reported for this session, so it
speaks once when a commit lands rather than on every turn; the marker sits in `.git/`, keyed on
the session id, cleared when the count returns to zero and swept after a week. It reports rather
than pushes - a push is a decision. Verified against throwaway repos, ten cases: ahead 0, first
ahead, repeat, a second session, the count changing, after a push, committing again, detached
HEAD, no upstream, not a repo. All exit 0.

**`settings.json`** - registers the three hooks and carries a permissions allowlist so sessions
prompt less: builds, the bundled tools the verifier runs (ffmpeg/ffprobe, mkvmerge, vspipe,
av1an, grav1synth and the five encoders), read-only git including the `check-ignore`/`ls-files`
the hook above needs, and **read-only `gh` only**. `gh workflow run`, `gh release create/edit`,
`git push` and `git tag` are deliberately absent: those publish, and the release procedure is
the one place a prompt is worth its cost. Trim or extend freely.

  It also carries an **`enabledPlugins` block that switches four marketplace plugins off for
  this repo and this repo only** - vercel, cloudflare, google-cloud-storage and supabase. Their
  skill descriptions are advertised into every session whether or not anything can use them, and
  measured with `claude plugin details`, that is **vercel ~2,950 tokens over 30 skills,
  cloudflare ~2,135 over 15, google-cloud-storage ~1,085 over 4 and supabase ~634 over 2** -
  ~6.8k always-on, on top of CLAUDE.md's ~99k, for a .NET/Avalonia/ffmpeg desktop app none of
  them can reach. (mapbox, ~1,578 over 19 skills, is off at *user* scope instead, having no repo
  where it applies.) `claude-code-setup` stays on at ~139.

  **Project scope rather than user scope is the whole point of the entry**, and it is not
  tidiness: those four are in real use in another repository, so switching them off globally
  would break work elsewhere to save context here. Project scope also travels - it is in a
  tracked file, so the desktop gets it on the next pull, where `settings.local.json` would stop
  at whichever machine ran the command. Reverse any of them with
  `claude plugin enable <name>@claude-plugins-official --scope project`.

  Written by `claude plugin disable --scope project`, which rewrites the whole file and reorders
  its top-level keys; the hooks and the allowlist survive that intact, but re-check them after
  running it rather than assuming.

**Skills** (`skills/*/SKILL.md`) - three procedures sessions kept rebuilding from CLAUDE.md
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

- **record-finding** - the house style for writing a measurement, a corrected belief or a
  post-mortem into CLAUDE.md, which the file demonstrates everywhere and states nowhere: name
  the binary every number came from, say why the wrong answer looked right, run the control,
  vary one thing at a time, correct a claim in place while keeping the old belief visible, label
  evidence grade, name what is not verified, name the one place in code the rule lives, and give
  every deliberate removal its reason so it does not read as an oversight. Plus the section map
  and the commit-subject convention. 66 of the last 200 commits touched CLAUDE.md and a dozen
  were CLAUDE.md alone, so this is the most repeated non-code task in the repository.

**Agents** - two, and they answer different questions:

- **`verifier`** - the verified-by-running-it culture as a subagent: builds throwaway harnesses
  in the scratchpad, measures against the shipped binaries in `~/.nmkoder-dev/bin`, reports
  numbers compactly. Delegating keeps a few hundred harness commands out of the main
  conversation's context.
- **`upstream-drift`** - re-probes the bundled tools against the claims CLAUDE.md makes about
  them and reports only what has moved. `verifier` proves a claim about *our* code; this asks
  whether *their* binaries still behave as documented, which is a different question with its
  own history: `kB` became `KiB` and every stream reported `Size: 0B`; av1an's `--log-file`
  default moved and the progress bar sat still for whole encodes; `model_path` left libvmaf and
  every metrics run overwrote the bundled model with an XML log; BtbN aged n7.1 off `latest` and
  2.8.68 shipped without grav1synth on Windows. Each was silent, green, and found only after it
  had broken something. The brief carries the inventory (ffmpeg, av1an, SvtAv1EncApp,
  VapourSynth and its plugins, grav1synth's dev headers, MSYS2, MKVToolNix) with the specific
  claim made about each, plus the evidence-grade rules - ask the binary not the docs, acceptance
  is not effect, presence is not loadability, read the avutil soname not the ffmpeg version.
  Worth a run before a release or after a bundler change. It reports; it does not edit CLAUDE.md.

**Evals** (`evals/`) - the benchmark suite, and how to run a pass. It covers `cut-release` and
`headless-ui`, two tasks each. **`record-finding` has none yet**, said here rather than left to
be assumed from the directory: its output is prose whose quality is a judgement call, so the
assertions want writing carefully - a with/without pair on "record this measurement in
CLAUDE.md", graded on provenance, the why-it-looked-right clause, and correcting in place
rather than appending, is the shape.

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
- **A live-documentation server (context7 and its kind)**, which is the one worth refusing
  actively rather than merely skipping. CLAUDE.md's rule is to prefer the XML docs shipped in
  `~/.nuget/packages/avalonia/<version>/ref/net10.0/*.xml` precisely because they match the
  pinned 12.1.0 exactly; a docs service answers for whatever the site publishes, which is the
  failure this project has already been bitten by - av1an's own docs site names three flags
  that are in no binary. Ask the binary; that is what `upstream-drift` is for.
- **The `fewer-permission-prompts` skill** mines local transcript history for an allowlist;
  run it from a session if prompts get annoying.
- **More agents per procedure** (release-auditor, doc-checker). Their knowledge lives in the
  skills, which any session or the verifier can follow; a second agent per procedure would
  just be the skill wearing a costume. `upstream-drift` clears that bar and is the shape that
  does: it is an open-ended investigation rather than a procedure, and it is read-heavy -
  dozens of help dumps and strings scans whose bulk is exactly what agent context isolation
  buys. That is the test to apply to the next one.

## The CLAUDE.md split

This paragraph used to be an observation and a proposal; it is now a record of what was done.

CLAUDE.md was **365,522 bytes - 61,573 words, roughly 99k tokens** - loaded into every session's
context whole, before a word is typed, and four sections carried 63% of it. Those four are now
**reference skills**: `tone-mapping` (896 lines), `av1an-tab` (875), `grain-synthesis` (633),
`deinterlacing` (469). CLAUDE.md is **143,062 bytes, ~38.7k tokens - 61% smaller**, and the four
`##` headings are still there carrying a digest apiece.

**The bodies were moved verbatim, byte for byte, and that was the whole method.** Nothing was
paraphrased, condensed or dropped, because paraphrasing 2,873 lines of measurements is exactly
how a trap gets lost. Proven rather than asserted, and worth re-running after any later move:
each skill body sha256-matches the CLAUDE.md slice it came from, each retained region
sha256-matches its original, and the two add up - **1,707 retained + 2,873 moved = 4,580, the
original line count exactly**, with the heading list unchanged and line endings still CRLF.

**What stays in CLAUDE.md is the invariant that can be broken from outside its own area**, and
that division is the whole safety argument: a trap that does not load is a trap re-shipped, so
"do not restore a mainline SVT-AV1 fallback in `bundle-tools.sh`" (a bundler edit),
"`GetPreparedInputs` must keep matching `.denoised.`" (a cleanup edit that otherwise leaks
lossless copies of whole films), "`DeinterlaceRequest.DoubleRate` defaults to `true`" and "swap
`MediaFile.ColorData` around `GetArgs` or the output is SDR pixels tagged PQ" are all still in
the file that always loads. The measurements and the history behind them are in the skill. **Do
not move a rule out of a digest on the grounds that the skill already carries it** - that is the
case the digest exists for.

The descriptions are what decide loading, so they are written in the vocabulary a task arrives
in rather than as titles - and two sections turned out to have buried subjects that had to be
named there or the skill would never fire: **the trim and cut material lives under
Deinterlacing** (21 mentions - it is there because a trim is what QTGMC cannot compose with), and
**the crop/resize/borders geometry lives under the AV1AN tab** (60+) though Quick Convert shares
it. Check for that before splitting anything else out.

One accepted cost, recorded rather than fixed: 23 passages inside the moved bodies say "this
file" meaning CLAUDE.md. Each skill's preamble says so. Rewriting them would have broken the
byte-identity that makes the move checkable.

## Maintenance

Measured facts in the skills are stamped with the release they were measured on (2.8.66
today: grav1synth back in the win-x64 zip after 2.8.65's transient miss; no av1an in the
linux tarball as of 2.8.60). Re-measure on drift rather than trusting them forever - the
bundler tracks rolling upstreams, which is the same rule CLAUDE.md applies to ffmpeg's stats
line.
