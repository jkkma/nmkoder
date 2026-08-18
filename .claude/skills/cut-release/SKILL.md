---
name: cut-release
description: Cut and publish an Nmkoder release end to end, and verify what was published. Use whenever the user asks to release, publish, ship, cut a version, bump the version, tag a release, dispatch the release workflow, or check that a published release/its assets/the Scoop manifest came out right - even if they only say "put out 2.8.x" or "ship it".
---

# Cutting an Nmkoder release

CLAUDE.md's "Cutting a release" section is the authority; this is the executable order of
operations plus the verification commands. Where the two disagree, CLAUDE.md wins. Sessions
run on the user's Windows machines from Git Bash, with `gh` installed and authenticated.

## Hard rules (each one has shipped a mistake before)

- **Patch-only.** The version is 2.8.x and every release steps the last digit: after 2.8.9
  comes 2.8.10. No size of change earns a minor or major bump.
- **Bump on master after the merge, never on the feature branch.** Two branches racing for
  the next number is how 2.5.0 was bumped twice.
- **Never hand-edit `bucket/nmkoder-avalonia.json`'s version, url or hash.** The workflow's
  last step rewrites all three and commits to master.
- **Say nothing about branches in the report.** Delete the local feature branch if wanted;
  leave the remote one alone and do not mention either fact - the user has asked not to hear
  it again. Never claim a remote branch was deleted.

## Steps

1. **Merge the work into master** and push master.

2. **Pick the version from the published releases, not the csproj.** The csproj's number is
   usually the one *already released* - it was bumped in the commit range of the release it
   belongs to. Ask GitHub what is actually out:

   ```bash
   gh release view --repo jkkma/nmkoder --json tagName --jq .tagName
   ```

   The next version is that, patch + 1.

3. **Bump `<Version>` in `Nmkoder/Nmkoder.csproj` on master, committed on its own** with the
   message `Bump version to X.Y.Z` - the generated notes list commit subjects newest first,
   so this line becomes the changelog's first line. Push master.

4. **Dispatch the workflow** with `publish=true`:

   ```bash
   gh workflow run release.yml --repo jkkma/nmkoder --ref master -f version=X.Y.Z -f publish=true
   ```

   Leaving `publish` off or false produces a *draft* nobody can install (and a draft also
   stands the Scoop-manifest step down). The dispatch creates the `vX.Y.Z` tag itself, so
   nothing has to be tagged or pushed by hand; pushing a `v*` tag would trigger the same
   workflow and also publish for real, but the dispatch is the one route to use so that a
   local tag never gets ahead of - or out of step with - what the workflow created. The run
   takes roughly six minutes.

5. **Watch it land.** Find the run id, then poll it:

   ```bash
   gh run list --repo jkkma/nmkoder --workflow release.yml --limit 3
   .claude/skills/cut-release/scripts/watch-run.sh <run-id>      # 0 success, 1 failed, 2 timeout, 3 API unreadable
   ```

   (`gh run watch <run-id>` does the same interactively.) The workflow itself gates the known
   packaging failures (BinFiles present, bin/ffmpeg + bin/ffprobe non-empty on linux/win, QTGMC
   and metric plugins render on win-x64), so a green run means those checks ran - a red run's
   job logs (`gh run view <run-id> --log-failed`) name the gate.

   `watch-run.sh` pipes curl straight into jq and counts consecutive failures, because the
   loop it replaced round-tripped the JSON through a shell `echo` (which mangles the `\n`
   inside commit messages), errored on all 22 of its polls and kept going, reporting nothing,
   while the run had already gone green: **a poller that fails every iteration looks exactly
   like one that is waiting.**

## Verifying the published release

After a green run:

- **The release exists and is not a draft**: `gh release view vX.Y.Z --repo jkkma/nmkoder --json isDraft,assets`.
- **The Scoop manifest moved**: the workflow commits "Point the Scoop manifest at X.Y.Z" to
  master, so `git fetch origin master` and check
  `git show origin/master:bucket/nmkoder-avalonia.json | jq -r .version` - and pull before
  doing anything else on master, or the next push rejects.
- **Spot-check an asset when anything about bundling changed.** A green run is not evidence a
  best-effort tool shipped, and grav1synth has gone missing from win-x64 four times for three
  different reasons: 2.8.31/2.8.32 (aimed at the wrong ffmpeg major), 2.8.65 and 2.8.72 (a transient
  cargo failure a bare re-run fixed) and 2.8.68 (upstream aged the pinned dev-headers asset out, so
  the URL 404'd - permanent until the resolver replaced it). **Read the job's skip reason before
  theorising**; the three look identical from the outside.

  **The transient one has a signature, and it is the only one of the three a bare re-run fixes, so
  it is worth knowing on sight.** 2.8.72's win-x64 log reads `thread 'main' (6080) has overflowed
  its stack`, then `warning: build failed`, then `[skip] grav1synth - cargo build failed` - a crash
  inside the build script, saying nothing about a URL, a version or a header, which is what tells it
  apart from the other two. Measured across three releases inside two hours on an unchanged bundler:
  2.8.71 bundled 28 and skipped 0, 2.8.72 bundled 27 and skipped grav1synth, the 2.8.73 re-run was
  back to 28/0. Re-dispatching is the whole fix - but 2.8.72 was already public by the time the
  asset was checked, so it cost a version number rather than a re-run, which is the argument for
  checking the size before moving on rather than after.

  The cheapest tell is the **asset size**, one API call and no download. grav1synth plus its
  ffmpeg shared DLLs is roughly 65 MB, so compare against the *previous* release rather than
  against a remembered absolute - the healthy size grows release on release (485.6 MB at 2.8.67,
  492.6 MB at 2.8.70), where the drop does not. Measured on the real assets: v2.8.67
  485,618,562 against the broken v2.8.68's 420,408,591, a **65.2 MB** gap, and v2.8.65's broken
  zip landed at 420,398,797 - the same hole from a different cause; and v2.8.72's 420,412,014 against a healthy 492,554,796 at 2.8.71 and 492,558,069 at 2.8.73, a **72.1 MB** gap and its repair.

  ```bash
  gh release view vX.Y.Z --repo jkkma/nmkoder --json assets \
    --jq '.assets[]|select(.name|test("win-x64"))|"\(.name) \(.size)"'
  # compare against the previous release - a ~65 MB drop is grav1synth missing
  ```

  Then look in the archive. For the win-x64 zip, list it - or pull one file out of it - without
  downloading 485 MB:

  ```bash
  url="https://github.com/jkkma/nmkoder/releases/download/vX.Y.Z/Nmkoder-X.Y.Z-win-x64.zip"
  python3 .claude/skills/cut-release/scripts/fetch-zip-member.py "$url"                 # list
  python3 .claude/skills/cut-release/scripts/fetch-zip-member.py "$url" Nmkoder/bin/av1an/av1an.exe  # extract one member
  ```

  Or simply re-run `bash .claude/setup-windows.sh`: it fetches the newest release's `bin/`
  into `~/.nmkoder-dev`, refuses to swap in an incomplete one, and says whether grav1synth
  came along - which stages the just-shipped tools beside the local build at the same time.
  The tar.gz assets have no random access - download one (linux-x64 is ~180 MB) and
  `tar -tzf`. What to look for is what the change touched: `Nmkoder/bin/ffmpeg`,
  `Nmkoder/bin/encoderArgs/{av1an,ffmpeg}/`, `Nmkoder/bin/av1an/enc/SvtAv1EncApp`,
  `Nmkoder/bin/grav1synth`, and never a *folder* under `bin/` with a binary's name.
  (av1an itself ships in the win-x64 zip only - the linux tarball carries no av1an binary,
  measured on 2.8.60, so its absence there is not a bundling regression.)

## If the run fails

Read the failing job's logs (`gh run view <run-id> --log-failed`) before re-dispatching - the
post-publish gates fail for named reasons, and re-running without reading only re-fails. A
dispatch can be repeated with the same version so long as nothing was published; once a public
release exists under the tag, the next attempt is the next patch number.
