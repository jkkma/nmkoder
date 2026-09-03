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

**`hooks/guard-identifying-info.sh`** (PreToolUse on Bash|PowerShell) - asks before a `git commit`,
a `git push` or a `gh pr`/`gh release` create/edit whose *added* lines would identify the user: the
Windows username or the hostname, read from `USERNAME` and `COMPUTERNAME` at run time so the tracked
script never spells them; a profile path with a real name in it (`C:\Users\<name>\`, while the
record's elided `C:/Users/…` and `C:\Users\<you>\` stay quiet); or a pattern from
`~/.nmkoder-dev/identifying-patterns`, a per-machine file beside the staged toolchain that carries
the handle and the personal address. That rule was a memory file until now, applied only when a
session remembered it; a hook applies it every time, and to a subagent's commands too. It reads the
staged diff (plus the working tree for `-a`), the upstream-to-HEAD diff and messages for a push
(origin/master when there is no upstream yet), and the command text with absolute paths stripped -
so a `git -C C:/…/repo commit` is not asked about while a name typed into a message is. It asks
rather than denies, a false positive being answered with yes, and every path exits 0. Verified by
running it against throwaway repos under Git Bash, twenty-seven cases: a clean stage, the username
in either case, the hostname, both file patterns, a profile path, an elided path, an unstaged hit
with and without `-a`, a hit in the message, a name inside a `-C` path, `git log | grep commit`,
push with nothing ahead, push with a hit, `push -u` on a new branch, `gh pr create` with and without
a hit, quotes in the command, a deletion of a line with the name, empty and garbage input, and the
cwd taken from the tool input so a worktree's index is the one checked.

**`hooks/ask-before-gpu-load.sh`** (PreToolUse on Bash|PowerShell) - asks before a command that
would hold the GPU: a GPU word (nvenc, nvdec, libplacebo, vship, cuda, vulkan, hwaccel) *and* a
launcher that can reach one (ffmpeg, ffplay, av1an, vspipe, python, `dotnet run`, an .exe), which
is what keeps `grep -rn nvenc Nmkoder` - a normal thing to type here - from asking. Listing probes
(`-encoders`, `-h encoder=`, `-buildconf`, `--help`, `-version`) are exempt. The 17 August rule
that every GPU load is asked about first lived in a memory file; this is its backstop, and its
header says what it cannot see (a compiled harness, a `.vpy` with Vship inside). Verified against
fifteen crafted tool inputs: seven that must ask, including the PowerShell tool's shape with a
quoted exe path and escaped quotes ahead of the GPU word, and eight that must not, the probes,
a source grep and garbage input among them.

  Both were first written through a Bash heredoc and one landed on disk with its regex mangled -
  the Bash tool collapses a doubled backslash in command text into one, measured with `od -c` -
  so both are written with the Write tool, byte-exact, and the test that caught it is in the cases
  above (the PowerShell shape). A hook script with a backslash in it is checked on disk, not in the
  command that wrote it.

**`settings.json`** - registers the five hooks and carries a permissions allowlist so sessions
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

  Extended on 3 September 2026 to seven more that a .NET/Avalonia/ffmpeg desktop app cannot use,
  each measured with the same command: plugin-dev ~1,704 always-on tokens, mcp-server-dev ~357,
  cwc-makers ~236, project-artifact ~235, agent-sdk-dev ~169, mcp-tunnels ~43, ralph-loop ~42 -
  ~2.8k a session between them. The original four entries name plugins that `claude plugin list`
  no longer shows installed at user scope; they stay, harmless, and the record of why. Kept on:
  `csharp-lsp` (the LSP tool - code navigation without an IDE), `hookify` (though the two rule
  hooks above were written by hand, because hookify's rules are `.local.md` files that would stop
  at whichever machine wrote them, where a tracked script travels), `claude-security`,
  `claude-md-management` (its improver is not to be run on CLAUDE.md, whose measured-record style
  is what `record-finding` exists to protect) and `claude-code-setup`.

  Written by `claude plugin disable --scope project`, which rewrites the whole file and reorders
  its top-level keys; the hooks and the allowlist survive that intact, but re-check them after
  running it rather than assuming.

**Skills** (`skills/*/SKILL.md`) - five procedures sessions kept rebuilding from CLAUDE.md
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

- **test-fixtures** - the named synthetic sources the record describes forty-odd times and never
  as a command, from one script with a `--check`: the interlaced capture (a 59.94p source woven by
  `tinterlace`, so idet calls every frame TFF where a merely tagged one came out 49/30/11), the
  padded-cadence capture (a CFR encode of a duplicated frame list with its stamps rewritten by
  `setts`, since mpeg2video cannot write VFR stamps itself - 408 coded frames over 10.009 s, and
  `r_frame_rate` above `avg_frame_rate` the way a real capture's field pictures put it), the
  keyframe-every-2s H.264, the scenecut=0 PQ clip with a movable three-frame event and no keyframe
  on it, PQ and HLG with and without a mastering display, the two anamorphic SAR shapes, the stereo
  and 5.1 loudness steps 25.7 LU apart, the three-minute ladder source whose thirds differ, and the
  sweep's y4m. Measured against the shipped BtbN `N-126264-g007cd1fd43-20260825`: twelve shapes,
  36 checks, all passing, in twenty seconds. Building it found three things the record did not
  have - `-top 1` is gone from this ffmpeg, `-field_order tt` writes `bb` into MPEG-TS, and a
  frame-coded synthetic cannot reproduce the field-rate `r_frame_rate` signature at all - and two
  things about ffprobe on Windows that a check has to allow for: every line ends CRLF, and a
  MPEG-TS or PS stream entry is printed twice, once under its program. The script header and the
  skill carry all of it.
- **sweep-encoder-args** - the argument-list check CLAUDE.md's Advanced tab section describes,
  with the extractor as code: the record's two hand-run sweeps used two ad-hoc extractors and
  could not reconcile 459 against 583. Every example, the numeric range ends and `up to N` at the
  head of the short description, a purely enumerated head, and a `(default X)` that is a number or
  an enumerated token, with nothing after `(default` ever read - so `rdoq-level` cannot contribute
  `rd`'s 4-6. It pairs the min/max rows itself and compares a stated default against a blank run
  only where two blank runs are identical (SVT-AV1 only with `--lp 1`, measured). 588 values over
  152 rows against the 2.8.79 toolchain, judged by artifacts - a 32-byte SVT stub and an empty x265
  output are refusals whatever the exit code - at the presets Quick Convert opens on, stdin closed.
  `--ffmpeg` runs the ffmpeg lists with the params/AVOption split and `--gpu` gates the NVENC pair
  behind the ask.
  First full run, 3 September 2026: 588 runs, none refused, four paired and all accepted, two x265
  defaults (`max-merge 2`, `limit-refs 3`) differing from a blank run at `medium`; through ffmpeg,
  libx264 clean at 143 runs and libvpx's `lossless 1` refusing beside any CRF above 0 and working at
  CRF 0 - checked against the harness's own base before it was called the row's, and now in
  CLAUDE.md. Its own first run also found the script: three `master-display` examples sanitised
  to one output name, so two parallel runs deleted each other's file - names carry the run index
  now, and the report is written after every list rather than at the end.

**Agents** - four, each answering a different question:

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

- **`record-audit`** - the names in the record that no longer name anything in the tree. Every
  backticked identifier in CLAUDE.md, the skills and the agents, grepped against the sources (a
  file name against the index, a member against the `.cs`/`.axaml` files), then every zero-hit
  name read in its sentence and put in one of five classes - stale, deliberately removed and said
  so, historical past tense, external, pipeline artifact - because a name in a past-tense sentence
  is the record keeping an old belief visible on purpose. The commit before this layer, "fix the
  stale scene-detect method name", is the fault it exists for. Read-only, and it clears this
  file's test for a new agent: the bulk (601 identifiers, 34 zero-hit, 41 read on its first run,
  3 September) is exactly what context isolation buys.
- **`invariant-reviewer`** - a diff read against the standing rules with nothing else in its head:
  the six digests' bullets and the cross-cutting traps, one line each with the member they live in,
  and a method that reads the whole touched file rather than the hunk, since most rules are about
  what a change fails to do. Reports breaks, risks and notes with file and line, and says "no rule
  touched" in one line when that is the answer. This one only partly clears the test - the main
  session already holds the rules - so it is on trial: drop it if it only echoes.

  Both were picked up by the Agent tool within the session that wrote them; their instruction
  files were first exercised through general-purpose subagents on 3 September 2026.
  The reviewer's run, over the two code commits before this layer, found the new mkvmerge command
  in `Av1an.AttachEncodeSettings` quoting the user's output path with `.Wrap()` - plain double
  quotes on every platform - where anything that reaches a shell goes through `Shell.WrapArg`, and
  a delete-then-move on the finished output that a failed move would turn into no output at all.
  Both are filed as a task rather than fixed in this change, and both went into the reviewer's rules
  (a `.Wrap()` clause, an amendment clause), along with what its own critique asked for: which
  revision to read files at for a historical range, that a rule written alongside a change is not
  evidence against it, and that documentation hunks count but their claims are out of scope.
  The audit's run found one stale sentence - the grain-synthesis skill still describes
  `ApplyGrainToOutput` in the present tense, a method added and removed on the same day in August
  and moved into the skill verbatim afterwards - and classified the other 33 zero-hit names as
  removed-and-said-so, historical, external or the pipeline's own. Its critique went into the
  agent: the regex's coverage stated, the five runtime and script files it always reports named,
  the pair check told to look for a declaration rather than a comment, the tie-break between
  "removed and said so" and "past tense" written down, `git log -S` restricted to the sources,
  and the extraction re-run before the report because the tree moved under the first pass.

**Evals** (`evals/`) - the benchmark suite, and how to run a pass. Six tasks, two each for
`cut-release`, `headless-ui` and `record-finding`. The last pair took the shape this file
predicted for it and one correction it did not: because the prose is graded on its *properties*
rather than on whether the finding is true, the prompts **supply** the measurement - a named
binary, a belief that looked right, a control never run - and ask only for a draft, so a pass
leaves the repo tree untouched and "edited no tracked file" is an assertion rather than an
assumption. Writing them is also what caught `record-finding` still routing findings the
pre-split way; see below.

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
- **hookify for the two rule hooks.** Its rules are `.claude/hookify.*.local.md` files - untracked,
  so they would stop at whichever machine wrote them, and the whole reason those hooks exist is the
  two-machine gap. A tracked bash script with its cases in this file is the shape that travels.

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

**A second pass on 3 September 2026 moved two more.** Measured additions had re-accumulated -
the file was back to 202,744 bytes - and `Driving the encoder binaries directly` (389 lines) and
`Repairing a padded capture` (246 lines) were the two largest sections left, so they went the
same way, verbatim and checksummed, into the `direct-encoders` and `cadence-repair` skills.
CLAUDE.md is **158,931 bytes / 2,015 lines** now, six `##` digests among its nineteen headings;
each moved body sha256-matches its pre-split slice, every retained line is byte-identical, and the
endings are still CRLF. The meta-documents that count the skills went in the same change - this
section, `record-finding`'s routing table and section list, `verifier`, and CLAUDE.md's own
preamble - because a skill that documents where things go is invalidated by moving things, the
lesson the first split already records below.

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

**The split left `record-finding` stale, and writing its evals is what caught it.** That skill
was authored the day before, so its "Where it goes" listed all eighteen `##` sections flat and
told a session to file a finding "in the section whose subject it is" - which for the four moved
ones is now a digest, the one place a measurement must not go. Its description said findings go
"into CLAUDE.md" full stop. Both are fixed: it carries the routing table (measurement to the
skill, cross-area rule to both, everything else to CLAUDE.md), the rule that a digest is not a
summary to be kept in sync, and the warning that `Deinterlacing` owns the trim material and
`The AV1AN tab` the geometry. **The shape to recognise: a skill that documents where things go is
invalidated by moving things**, and nothing about the move itself would have flagged it - the
checksums all passed. Check the meta-documentation after any future move.

That prompted an audit of everything else in this layer, and it found one more: **`verifier`**
opened by telling itself to read "the CLAUDE.md sections that touch the area under test - most
harness shapes you will need are already described there", which for the four moved areas is now
a digest carrying rules and no harnesses. That is the worst place in the layer for the fault,
since building harnesses is the whole job, so it now names the four skills, says the digest is
not what it wants, and carries three sample traps with their homes - the `scenecut=0` fixture
rule (`tone-mapping`), the odd-frame 4:4:4-in/FFV1-out requirement (`av1an-tab`), and the
IVF-never-WebM SegmentUID trap, which is an example of a harness trap that stayed in CLAUDE.md.
**`cut-release` and `headless-ui` came back clean on this point** and need no re-checking: they
cite only `Cutting a release`, `UI conventions` and `The palette`, all three retained. Every
factual claim in all three was re-run rather than read - the 11 binary paths under
`~/.nmkoder-dev/bin`, both cut-release scripts, the manifest and csproj and latest release all
agreeing at 2.8.70, the Avalonia-version grep returning 12.1.0, and headless-ui's "all six tabs"
confirmed against `MainTabs` (File List, Track List, AV1AN, Quick Convert, Utilities, Settings)
after a first parse got it wrong.

`cut-release` gained the one thing the audit found missing rather than stale: its
green-run-is-not-evidence list stopped at 2.8.65, so it now carries all three grav1synth
outages, the point that they had three different causes and look identical from outside, and the
asset-size check that catches the class in one API call - measured on the real assets at 65.2 MB
(v2.8.67 485,618,562 against the broken v2.8.68's 420,408,591), and framed as a delta against
the previous release because the healthy absolute grows over time (492.6 MB by 2.8.70).

## Maintenance

Measured facts in the skills are stamped with the release they were measured on (2.8.66
today: grav1synth back in the win-x64 zip after 2.8.65's transient miss; no av1an in the
linux tarball as of 2.8.60). Re-measure on drift rather than trusting them forever - the
bundler tracks rolling upstreams, which is the same rule CLAUDE.md applies to ffmpeg's stats
line.
The two scripts carry their own re-measurement: `make-fixture.sh --check all` (36 checks) and
`sweep.py` (588 values, none refused) were last run on the 2.8.79 toolchain, 3 September 2026,
and either one failing after a refresh is the drift showing itself.
