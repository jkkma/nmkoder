---
name: cut-release
description: Cut and publish an Nmkoder release end to end, and verify what was published. Use whenever the user asks to release, publish, ship, cut a version, bump the version, tag a release, dispatch the release workflow, or check that a published release/its assets/the Scoop manifest came out right - even if they only say "put out 2.8.x" or "ship it".
---

# Cutting an Nmkoder release

CLAUDE.md's "Cutting a release" section is the authority; this is the executable order of
operations plus the verification commands. Where the two disagree, CLAUDE.md wins.

## Hard rules (each one has shipped a mistake before)

- **Patch-only.** The version is 2.8.x and every release steps the last digit: after 2.8.9
  comes 2.8.10. No size of change earns a minor or major bump.
- **Never push a tag or delete a remote branch from a web session.** The sandbox's git proxy
  hangs up on both, identically, every retry (a PreToolUse hook denies these pushes with the
  same explanation). The workflow's dispatch path creates the tag itself.
- **Bump on master after the merge, never on the feature branch.** Two branches racing for
  the next number is how 2.5.0 was bumped twice.
- **Never hand-edit `bucket/nmkoder-avalonia.json`'s version, url or hash.** The workflow's
  last step rewrites all three and commits to master.
- **Say nothing about branches in the report.** Delete the local feature branch if wanted;
  leave the remote one alone and do not mention either fact - the user has asked not to hear
  it again. Never claim a remote branch was deleted.

## Steps

1. **Merge the work into master** and push master (`git push -u origin master` is an
   ordinary branch push and goes through).

2. **Pick the version from the published releases, not the csproj.** The csproj's number is
   usually the one *already released* - it was bumped in the commit range of the release it
   belongs to. Ask GitHub what is actually out:

   ```bash
   curl -fsSL https://api.github.com/repos/jkkma/nmkoder/releases/latest | jq -r .tag_name
   ```

   (or `mcp__github__get_latest_release`). The next version is that, patch + 1.

3. **Bump `<Version>` in `Nmkoder/Nmkoder.csproj` on master, committed on its own** with the
   message `Bump version to X.Y.Z` - the generated notes list commit subjects newest first,
   so this line becomes the changelog's first line. Push master.

4. **Dispatch the workflow**: `mcp__github__actions_run_trigger`, method `run_workflow`,
   `workflow_id: release.yml`, `ref: master`, inputs `version=X.Y.Z` and `publish=true`.
   Leaving `publish` off or false produces a *draft* nobody can install (and a draft also
   stands the Scoop-manifest step down). The run takes roughly six minutes.

5. **Watch it land.** Find the run id, then poll it:

   ```bash
   curl -fsSL "https://api.github.com/repos/jkkma/nmkoder/actions/workflows/release.yml/runs?per_page=3" \
     | jq -r '.workflow_runs[] | "\(.id)  \(.status)/\(.conclusion // "-")  \(.display_title)"'
   .claude/skills/cut-release/scripts/watch-run.sh <run-id>      # 0 success, 1 failed, 2 timeout, 3 API unreadable
   ```

   The workflow itself gates the known packaging failures (BinFiles present, bin/ffmpeg +
   bin/ffprobe non-empty on linux/win, QTGMC and metric plugins render on win-x64), so a
   green run means those checks ran - a red run's job logs name the gate.

   **Do not poll with `mcp__github__actions_list`**: `list_workflow_runs` on this repo
   returns ~342,000 characters and is refused for exceeding the token limit, so it cannot
   be used to watch anything. The REST endpoint above returns the same three rows in a few
   hundred bytes.

   **And do not round-trip the JSON through `echo` in a shell script.** `/bin/sh` here is
   dash, whose builtin `echo` expands backslash escapes - so `j=$(curl ...); echo "$j" | jq`
   turns every `\n` inside a JSON string (commit messages are full of them) into a real
   newline and jq dies with "control characters from U+0000 through U+001F must be
   escaped". Measured: the same bytes through `printf '%s'` parse clean, and the response
   itself contains **zero** control bytes - the shell was creating them, so stripping
   control characters is a fix for the wrong thing. Pipe curl straight into jq.
   `watch-run.sh` does both of these correctly and counts consecutive failures, because the
   loop this replaces errored on all 22 of its polls and kept going, reporting nothing,
   while the run had already gone green: **a poller that fails every iteration looks
   exactly like one that is waiting.**

## Verifying the published release

After a green run:

- **The release exists and is not a draft**: `get_release_by_tag` for `vX.Y.Z`, or
  `curl -fsSL https://api.github.com/repos/jkkma/nmkoder/releases/latest`.
- **The Scoop manifest moved**: the workflow commits "Point the Scoop manifest at X.Y.Z" to
  master, so `git fetch origin master` and check
  `git show origin/master:bucket/nmkoder-avalonia.json | jq -r .version` - and pull before
  doing anything else on master, or the next push rejects.
- **Spot-check an asset when anything about bundling changed.** A green run is not evidence a
  best-effort tool shipped (2.8.31/2.8.32 were green and shipped no grav1synth) - look in the
  archive. For the win-x64 zip, list it - or pull one file out of it - without downloading
  485 MB:

  ```bash
  url="https://github.com/jkkma/nmkoder/releases/download/vX.Y.Z/Nmkoder-X.Y.Z-win-x64.zip"
  python3 .claude/skills/real-binaries/scripts/fetch-zip-member.py "$url"                 # list
  python3 .claude/skills/real-binaries/scripts/fetch-zip-member.py "$url" Nmkoder/bin/av1an/av1an.exe  # extract one member
  ```

  (The script probes the size and uses absolute byte ranges - the sandbox's proxy answers a
  suffix range with 501, so a hand-rolled `curl -r -N` tail grab does not work here.) The
  tar.gz assets have no random access - download one (linux-x64 is ~180 MB) and `tar -tzf`.
  What to look for is what the change touched: `Nmkoder/bin/ffmpeg`,
  `Nmkoder/bin/encoderArgs/{av1an,ffmpeg}/`, `Nmkoder/bin/av1an/enc/SvtAv1EncApp`,
  `Nmkoder/bin/grav1synth`, and never a *folder* under `bin/` with a binary's name.
  (av1an itself ships in the win-x64 zip only - the linux tarball carries no av1an binary,
  measured on 2.8.60, so its absence there is not a bundling regression.)

## If the run fails

Read the failing job's logs (`get_job_logs`) before re-dispatching - the post-publish gates
fail for named reasons, and re-running without reading only re-fails. A dispatch can be
repeated with the same version so long as nothing was published; once a public release
exists under the tag, the next attempt is the next patch number.
