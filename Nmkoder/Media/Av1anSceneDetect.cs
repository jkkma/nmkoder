using Newtonsoft.Json.Linq;
using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.OS;
using Nmkoder.UI.Tasks;
using Nmkoder.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nmkoder.Media
{
    /// <summary>
    /// Runs av1an's scene detection ahead of the encode, split across parallel slices of the input,
    /// and hands the encode the finished list via --scenes so it skips its own pass.
    /// <para/>
    /// Scene detection is the one phase of an av1an run the workers cannot help with - it is what
    /// creates the chunks they work on, so it runs first, alone, one decode pipeline reading every
    /// frame of the source at the source's own size. On a 4K film that is minutes of one busy
    /// pipeline while the rest of the machine idles, and on a fast preset it can cost as much wall
    /// clock as the encode itself. The phase resists av1an's own chunk parallelism by construction,
    /// but not parallelism as such: frame-exact slices of the input can each be detected on their
    /// own, concurrently, and the scene lists concatenated with their frame numbers offset - a scene
    /// never spans a slice boundary, so the merge is arithmetic rather than judgement. The one cost
    /// is that each boundary starts a scene whether or not the picture cut there, which is an extra
    /// chunk boundary - the same thing av1an's own -x does to a long scene.
    /// <para/>
    /// The slices are VapourSynth scripts (LWLibavSource plus a [a:b] trim) rather than cut files,
    /// because a stream-copy cut is not frame-exact - it ends two frames late on any source with
    /// B-frames, see UtilCut - and the offsets here have to be exact or the merged list describes
    /// frames that are not where it says they are. lsmash and nothing else, because the scene list's
    /// frame numbers must be the ones the encode's own chunking will use: this is why the caller
    /// only engages the feature for the LSMASH chunk method, and why the fallback source plugins
    /// Qtgmc's scripts carry are deliberately absent here. The vspipe --info call that supplies the
    /// frame count also builds the index, once, so the concurrent slices read a warm cache instead
    /// of racing to write one.
    /// <para/>
    /// Everything here is opportunistic: any missing piece - the Detection Slices box set to 1, an
    /// av1an without --sc-only or --scenes, no vspipe, a source too short to be worth splitting, a
    /// slice run failing, a merged list that does not tile the video exactly - abandons the attempt
    /// and returns "", and the encode runs with av1an's own in-run detection exactly as it would
    /// have. Nothing here ever fails the run.
    /// <para/>
    /// What could not be verified in the session that wrote this is av1an's loading side: that a
    /// --scenes file which already exists makes it skip detection is its documented behaviour, but
    /// there is no av1an binary in a web session to run. Av1an.Run therefore treats the handed-over
    /// list as revocable - if av1an exits nonzero without having encoded a single chunk, it retries
    /// once without the file - so the worst a wrong assumption costs is one failed startup, logged.
    /// </summary>
    class Av1anSceneDetect
    {
        /// <summary> Ceiling on the machine-derived <see cref="DefaultSliceCount"/>, not on the box:
        /// the pass's wall clock falls as 1/K, so each step up buys less than the one before, while
        /// every slice is another full-size decode pipeline reading the same file - memory on any
        /// machine, seek thrash on a spinning disk. Eight is where that trade is called for a
        /// default; anyone who wants more can type more. </summary>
        const int DefaultMaxSlices = 8;

        /// <summary> How many cores one detection pipeline is booked at, for the default. Began as
        /// a guess of four; the first field report halved it - a 4K60 file split four ways left its
        /// machine at about 25% CPU, so a pipeline keeps one to two cores busy, the decode and the
        /// analysis being nearer serial than the guess assumed. Two rather than one books headroom
        /// for heavier codecs, and mild oversubscription is only timeslicing - the same
        /// right-way-round-to-be-wrong the thread plan's rounding argues. </summary>
        const int CoresPerSlice = 2;

        /// <summary> The most the Detection Slices box offers, restated here so no caller can fan
        /// out further than the box allows: the XAML maximum coerces what the box shows, this
        /// clamps what actually runs. </summary>
        public const int HardMaxSlices = 16;

        /// <summary> Under this many frames per slice - twenty seconds of 60 fps video - the
        /// process start and index open each slice pays eat into what the parallelism saves. </summary>
        const int MinFramesPerSlice = 1200;

        /// <summary>
        /// First-run default for the Detection Slices box: half the logical cores, clamped to 1-8.
        /// On anything under four cores that works out to 1, which switches the pre-pass off - the
        /// same stand-down those machines had before the box existed. Consulted by Config's default
        /// table and written into the config once, the same pattern as Av1an.GetDefaultThreadPlan;
        /// after that the number is the user's, and this is never read again.
        /// </summary>
        public static int DefaultSliceCount()
        {
            return Math.Clamp(Environment.ProcessorCount / CoresPerSlice, 1, DefaultMaxSlices);
        }

        /// <summary>
        /// How many slices actually run for a request against a source: never more than asked,
        /// never more than the box can express, and never so many that a slice drops under
        /// <see cref="MinFramesPerSlice"/> - past that point the process start and index open per
        /// slice are the work. Anything under 2 means "do not split": one slice of everything is
        /// av1an's own sequential pass with extra steps in front of it.
        /// </summary>
        internal static int ResolveSliceCount(int requestedSlices, int frames)
        {
            return Math.Min(requestedSlices.Clamp(0, HardMaxSlices), frames / MinFramesPerSlice);
        }

        /// <summary> Same figure and same reasoning as Qtgmc.InfoTimeoutMs: what --info spends its
        /// time on is the source plugin indexing the file, which is minutes for a big file off a
        /// slow disk, and a timeout firing mid-index throws that work away. Stop kills the process
        /// long before this, since it runs as Secondary. </summary>
        const int InfoTimeoutMs = 15 * 60 * 1000;

        /// <summary>
        /// Detects scenes for <paramref name="inPath"/> across parallel av1an --sc-only runs and
        /// writes the merged list to <see cref="Av1anUi.GetScenesFilePath"/>, returning that path -
        /// or "" wherever any piece of it is not available, in which case nothing has changed for
        /// the caller. <paramref name="scDownscaleArg"/> and <paramref name="keyIntArg"/> are the
        /// exact argument strings the encode itself will carry, so the slices detect at the same
        /// resolution and subdivide long scenes at the same -x the encode expects.
        /// <paramref name="requestedSlices"/> is the Detection Slices box as the caller snapshotted
        /// it; 1 is the box's way of switching the pre-pass off.
        /// </summary>
        public static async Task<string> TryPrepareScenesFileAsync(string inPath, string tempDir, string scDownscaleArg, string keyIntArg, int requestedSlices)
        {
            NmkdStopwatch sw = new NmkdStopwatch();
            string scratchDir = "";
            var slices = new List<(int index, Process proc, Task<(int exitCode, string output)> run, string scenesPath)>();

            try
            {
                // The user's own off switch, checked ahead of everything else it would gate.
                if (requestedSlices < 2)
                {
                    Logger.Log("Parallel scene detection: the Detection Slices box is set to 1, so av1an detects in-run.", true);
                    return "";
                }

                // The flags are old (0.4.x era), but av1an refuses a whole command over one it does
                // not know, and a help text that could not be read says nothing either way - so
                // unlike the encode's own flag checks, an unknown help here means standing down:
                // this feature is an optimization, and "maybe" is not grounds for an extra pass.
                if (!await AvProcess.Av1anHelpKnown() ||
                    !await AvProcess.Av1anSupportsFlag("--sc-only") || !await AvProcess.Av1anSupportsFlag("--scenes"))
                {
                    Logger.Log("Parallel scene detection: this av1an does not advertise --sc-only/--scenes, so av1an detects in-run.", true);
                    return "";
                }

                string vspipe = Qtgmc.GetVspipePath();

                if (vspipe.IsEmpty())
                {
                    Logger.Log("Parallel scene detection: VSPipe is not available to slice the input, so av1an detects in-run.", true);
                    return "";
                }

                string av1anPath = GetAv1anPath();

                if (av1anPath.IsEmpty())
                    return ""; // The encode's own launcher names this loudly moments later - no point saying it twice

                scratchDir = Path.Combine(Paths.GetSessionDataPath(), $"scdet-{Path.GetFileName(tempDir)}");
                Directory.CreateDirectory(scratchDir);

                // The count comes from the same lsmash that will read the slices, and asking for it
                // builds the index - which is what makes launching the slices concurrently safe: they
                // find a warm cache instead of racing to write one.
                RunTask.ReportProgress("Indexing the source for scene detection...");
                int frames = await GetFrameCountAsync(vspipe, inPath, scratchDir);

                if (RunTask.canceled || RunTask.failed)
                    return "";

                int sliceCount = ResolveSliceCount(requestedSlices, frames);

                if (sliceCount < 2)
                {
                    Logger.Log($"Parallel scene detection: {(frames < 1 ? "the frame count could not be read" : $"{frames} frames is too short to be worth splitting")}, so av1an detects in-run.", true);
                    return "";
                }

                // A hand-set count is respected up to the floor above; where the file cannot carry
                // it, the reduction is said out loud, because the number on the tab is the user's.
                if (sliceCount < requestedSlices.Clamp(0, HardMaxSlices))
                    Logger.Log($"The file is {frames} frames, so scene detection runs {sliceCount} slices rather than the " +
                        $"{requestedSlices} configured - under {MinFramesPerSlice} frames a slice is process startup " +
                        $"rather than parallelism.");

                // Boundary frames, computed here and nowhere else: bounds[i] is both where slice i
                // starts and the offset its scene list is shifted by in the merge.
                int[] bounds = GetSliceBounds(frames, sliceCount);

                // Announced only now that it is actually happening - every stand-down above is a
                // debug line, but from here on an abandonment has to be said out loud, because the
                // user has been told the pass is running.
                Logger.Log($"Detecting scene changes in {sliceCount} parallel slices of ~{frames / sliceCount} frames each, " +
                    $"so av1an can skip the sequential scene detection pass it would otherwise start with.");

                string logFileArg = await AvProcess.Av1anSupportsFlag("--log-file") ? "--log-file {0} " : "";

                for (int i = 0; i < sliceCount; i++)
                {
                    string script = Path.Combine(scratchDir, $"slice{i}.vpy");
                    File.WriteAllText(script, BuildSliceScript(inPath, bounds[i], i == sliceCount - 1 ? -1 : bounds[i + 1]));

                    string scenesPath = Path.Combine(scratchDir, $"slice{i}.scenes.json");
                    // Its own temp and log per slice, inside the scratch folder, so no two runs
                    // share state and nothing lands in ./logs beside the binary - the same litter
                    // Av1an.GetLogFileArgs exists to prevent.
                    string sliceArgs = $"-y --sc-only --split-method av-scenechange -m lsmash " +
                        $"--scenes {scenesPath.Wrap()} " +
                        $"{scDownscaleArg} {keyIntArg} ".Replace("  ", " ") +
                        string.Format(logFileArg, Path.Combine(scratchDir, $"slice{i}.log").Wrap()) +
                        $"--temp {Path.Combine(scratchDir, $"temp{i}").Wrap()} " +
                        $"-i {script.Wrap()} -o {Path.Combine(scratchDir, $"slice{i}.mkv").Wrap()}";

                    slices.Add((i, StartSlice(av1anPath, sliceArgs, out Task<(int, string)> run), run, scenesPath));
                }

                int done = 0;
                RunTask.ReportProgress($"Detecting scenes ({done}/{sliceCount} slices done)...");
                var pending = slices.Select(s => s.run).ToList();

                while (pending.Count > 0)
                {
                    var finished = await Task.WhenAny(pending);
                    pending.Remove(finished);

                    if (RunTask.canceled || RunTask.failed)
                        return ""; // Stop has already killed the processes (they run as Secondary)

                    var slice = slices.First(s => s.run == finished);
                    (int exitCode, string output) = finished.Result;

                    if (exitCode != 0 || IoUtils.GetFilesize(slice.scenesPath) < 1)
                    {
                        Logger.Log($"Parallel scene detection did not work out - slice {slice.index + 1} " +
                            $"{(exitCode != 0 ? $"exited with code {exitCode}" : "wrote no scene list")}, so av1an " +
                            $"does its own scene detection instead. The encode is unaffected.");
                        Logger.Log($"Slice {slice.index + 1} output:\n{output.Trim().Trunc(1200)}", true);
                        return "";
                    }

                    done++;
                    RunTask.ReportProgress($"Detecting scenes ({done}/{sliceCount} slices done)...");
                }

                string merged = MergeScenesFiles(slices.OrderBy(s => s.index).Select(s => s.scenesPath).ToList(), bounds, frames, out string problem);

                if (problem.IsNotEmpty())
                {
                    Logger.Log($"Parallel scene detection did not work out - {problem} - so av1an does its own " +
                        $"scene detection instead. The encode is unaffected.");
                    return "";
                }

                string outPath = Av1anUi.GetScenesFilePath(tempDir);
                File.WriteAllText(outPath, merged);
                int sceneCount = SceneCount(merged);
                Logger.Log($"Scene detection finished in {FormatUtils.Time(sw.ElapsedMs)} - {sceneCount} scenes " +
                    $"from {sliceCount} parallel slices. av1an is given the list and skips its own pass.");
                return outPath;
            }
            catch (Exception e)
            {
                Logger.Log($"Parallel scene detection could not run ({e.Message}), so av1an does its own scene detection instead.", true);
                Logger.Log($"{e.StackTrace}", true);
                return "";
            }
            finally
            {
                // A slice still running is only ever the leftovers of an abandoned attempt - the
                // success path has awaited every one - and the scratch folder holds nothing worth
                // keeping either way: the merged list has already been written beside the temp
                // folder, and the index cache lives in the session's shared vsindex.
                foreach (var slice in slices)
                {
                    try
                    {
                        if (!slice.proc.HasExited)
                            OsUtils.KillProcessTree(slice.proc.Id);
                    }
                    catch { }
                }

                if (scratchDir.IsNotEmpty())
                    IoUtils.TryDeleteIfExists(scratchDir);
            }
        }

        /// <summary> Frame indices where the slices meet: bounds[i] to bounds[i+1] is slice i, and
        /// bounds[i] is the offset its scene numbers are shifted by. Long arithmetic because
        /// frames * sliceCount overflows int at a little over two hours of 8K... cheap insurance. </summary>
        static int[] GetSliceBounds(int frames, int sliceCount)
        {
            int[] bounds = new int[sliceCount + 1];

            for (int i = 0; i <= sliceCount; i++)
                bounds[i] = (int)((long)frames * i / sliceCount);

            return bounds;
        }

        /// <summary> av1an, resolved the way AvProcess.RunAv1an resolves it - the bundled folders
        /// first, then PATH - so the slices run the same binary the encode will. </summary>
        static string GetAv1anPath()
        {
            string dir = Path.Combine(Paths.GetBinPath(), "av1an");
            var dirs = new[] { dir, Path.Combine(dir, "enc"), Path.Combine(dir, "vsynth"), Paths.GetBinPath() }
                .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Shell.PathSeparator));
            string resolved = Shell.ResolveExecutable("av1an", dirs);
            return File.Exists(resolved) ? resolved : "";
        }

        /// <summary> The input's frame count as lsmash counts it, or -1. Asking builds the index. </summary>
        static async Task<int> GetFrameCountAsync(string vspipe, string inPath, string scratchDir)
        {
            string script = Path.Combine(scratchDir, "info.vpy");
            File.WriteAllText(script, BuildSliceScript(inPath, 0, -1));

            var result = await Qtgmc.RunVspipe(vspipe, $"--info {script.Wrap()} -", InfoTimeoutMs, NmkoderProcess.ProcessType.Secondary);

            if (result == null || result.Value.exitCode != 0)
            {
                Logger.Log($"vspipe --info {(result == null ? "did not answer in time" : $"failed (exit {result.Value.exitCode}): {result.Value.output.Trim().Trunc(600)}")}", true);
                return -1;
            }

            string line = result.Value.output.SplitIntoLines().Select(x => x.Trim())
                .FirstOrDefault(x => x.StartsWith("Frames:", StringComparison.Ordinal)) ?? "";
            return line.IsEmpty() ? -1 : line.Substring("Frames:".Length).Trim().GetInt();
        }

        /// <summary>
        /// A script reading frames <paramref name="from"/> up to (excluding) <paramref name="to"/>
        /// of the source, or to the end for a negative <paramref name="to"/>.
        /// <para/>
        /// lsmash only, no fallback to another source plugin: the frame numbers in the scene list
        /// have to be the ones av1an's own LSMASH chunking will use, and a different indexer can
        /// count differently - which is also why the caller only engages this for that chunk method.
        /// The cache goes to the session's shared vsindex like Qtgmc's scripts, so it is warmed once
        /// by the --info call and read by every slice; a build too old to know cachedir falls back
        /// to indexing beside the source, which every slice then still shares.
        /// </summary>
        static string BuildSliceScript(string sourcePath, int from, int to)
        {
            string cacheDir = Path.Combine(Paths.GetSessionDataPath(), "vsindex");
            Directory.CreateDirectory(cacheDir);

            var sb = new StringBuilder();
            sb.AppendLine("# Written by Nmkoder for one encode's parallel scene detection - it is rewritten every run.");
            sb.AppendLine("import vapoursynth as vs");
            sb.AppendLine("core = vs.core");
            sb.AppendLine($"SOURCE = {PyString(sourcePath)}");
            sb.AppendLine($"CACHE_DIR = {PyString(cacheDir)}");
            sb.AppendLine();
            sb.AppendLine("try:");
            sb.AppendLine("    clip = core.lsmas.LWLibavSource(source=SOURCE, cachedir=CACHE_DIR)");
            sb.AppendLine("except Exception:");
            sb.AppendLine("    clip = core.lsmas.LWLibavSource(source=SOURCE)");
            sb.AppendLine(to < 0 ? $"clip = clip[{from}:]" : $"clip = clip[{from}:{to}]");
            sb.AppendLine("clip.set_output()");
            return sb.ToString();
        }

        /// <summary> A path as a Python string literal - same two characters Qtgmc.PyString escapes,
        /// for the same reason. </summary>
        static string PyString(string value)
        {
            return "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        /// <summary> Launches one av1an --sc-only run, hidden, with the environment RunAv1an gives
        /// the real one. Secondary rather than Primary, like every process a task waits on: Stop has
        /// to reach it or the task sits blocked on a slice nobody can cancel. </summary>
        static Process StartSlice(string av1anPath, string args, out Task<(int exitCode, string output)> run)
        {
            string dir = Path.Combine(Paths.GetBinPath(), "av1an");
            Process proc = OsUtils.NewProcess(true, NmkoderProcess.ProcessType.Secondary, av1anPath);
            OsUtils.SetPathVar(proc, new[] { Path.Combine(dir, "vsynth"), Path.Combine(dir, "enc"), Paths.GetBinPath() });
            proc.StartInfo.WorkingDirectory = Directory.Exists(dir) ? dir : Paths.GetBinPath();
            proc.StartInfo.Arguments = args;
            Logger.Log($"Running: av1an {args}", true, false, "av1an");
            proc.Start();
            proc.PriorityClass = ProcessPriorityClass.BelowNormal;
            run = WaitForSliceAsync(proc);
            return proc;
        }

        /// <summary> Both pipes at once - reading one to the end first deadlocks as soon as the
        /// other fills its buffer, the same caveat av1an's help call documents. A killed slice
        /// resolves normally here: the pipes end and the exit code is whatever the kill left. </summary>
        static async Task<(int exitCode, string output)> WaitForSliceAsync(Process proc)
        {
            Task<string> stdout = proc.StandardOutput.ReadToEndAsync();
            Task<string> stderr = proc.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdout, stderr);
            await proc.WaitForExitAsync();

            int exitCode;
            try { exitCode = proc.ExitCode; }
            catch { exitCode = -1; }

            return (exitCode, $"{stdout.Result}\n{stderr.Result}");
        }

        /// <summary>
        /// Concatenates the slices' scene lists with their frame numbers offset, and validates the
        /// result the strictest way available: every list must tile its slice exactly, and the
        /// merged lists must tile [0, totalFrames) with no gap and no overlap. Anything surprising
        /// is a reason to hand back a problem rather than a file - a wrong scene list fed to av1an
        /// is chunks in the wrong places, which nothing downstream would ever say out loud.
        /// <para/>
        /// The first slice's document is the template, so top-level fields this method does not
        /// know about survive - the reader of this file is the same binary that wrote the slices,
        /// which is what keeps the schema self-consistent without this app modelling all of it.
        /// scenes and split_scenes are merged the same way (the second is the first after -x
        /// subdivides long scenes; subdivision is per-scene, and a scene never spans a slice, so
        /// merging the subdivided lists equals subdividing the merged list - the slices are run
        /// with the encode's own -x to keep that true). A file without split_scenes is an older
        /// av1an; then the merged file carries none either, matching what that binary expects.
        /// </summary>
        static string MergeScenesFiles(List<string> paths, int[] bounds, int totalFrames, out string problem)
        {
            problem = "";
            JObject template = null;
            var scenes = new JArray();
            var splitScenes = new JArray();
            bool haveSplitScenes = true;

            for (int i = 0; i < paths.Count; i++)
            {
                JObject root = JObject.Parse(File.ReadAllText(paths[i]));
                int expectedFrames = bounds[i + 1] - bounds[i];
                int sliceFrames = (int?)root["frames"] ?? -1;

                // The one disagreement that must stop everything: the slice's own idea of its length
                // not matching the arithmetic means the offsets are wrong for every scene after it.
                if (sliceFrames != expectedFrames)
                {
                    problem = $"slice {i + 1} reports {sliceFrames} frames where its cut is {expectedFrames}";
                    return "";
                }

                template = template ?? root;

                if (!AppendOffsetScenes(scenes, root["scenes"] as JArray, bounds[i], i, "scenes", ref problem))
                    return "";

                if (root["split_scenes"] is JArray sliceSplit)
                {
                    if (!AppendOffsetScenes(splitScenes, sliceSplit, bounds[i], i, "split_scenes", ref problem))
                        return "";
                }
                else
                {
                    haveSplitScenes = false;
                }
            }

            if (!TilesExactly(scenes, totalFrames, out string why))
            {
                problem = $"the merged scene list does not cover the video exactly ({why})";
                return "";
            }

            if (haveSplitScenes && !TilesExactly(splitScenes, totalFrames, out why))
            {
                problem = $"the merged split_scenes list does not cover the video exactly ({why})";
                return "";
            }

            template["scenes"] = scenes;
            template["frames"] = totalFrames;

            if (haveSplitScenes)
                template["split_scenes"] = splitScenes;
            else
                template.Remove("split_scenes");

            return template.ToString();
        }

        /// <summary> Appends one slice's scenes to the merged array with their frames offset,
        /// cloning each so nothing is moved between documents. False (with the reason) for a scene
        /// missing the two frame fields - a schema this method does not understand well enough to
        /// shift. </summary>
        static bool AppendOffsetScenes(JArray target, JArray sliceScenes, int offset, int sliceIndex, string arrayName, ref string problem)
        {
            if (sliceScenes == null || sliceScenes.Count < 1)
            {
                problem = $"slice {sliceIndex + 1} has no '{arrayName}' list";
                return false;
            }

            foreach (JToken token in sliceScenes)
            {
                if (!(token is JObject scene) || scene["start_frame"] == null || scene["end_frame"] == null)
                {
                    problem = $"slice {sliceIndex + 1} holds a scene without start_frame/end_frame";
                    return false;
                }

                JObject clone = (JObject)scene.DeepClone();
                clone["start_frame"] = (int)clone["start_frame"] + offset;
                clone["end_frame"] = (int)clone["end_frame"] + offset;
                target.Add(clone);
            }

            return true;
        }

        /// <summary> Whether the scenes cover [0, totalFrames) contiguously - starts at 0, each
        /// scene starts where the last ended, ends at the total. This is also a check of the
        /// assumption that end_frame is exclusive; were it not, the very first pair would fail here
        /// rather than feeding av1an a list off by one per scene. </summary>
        static bool TilesExactly(JArray scenes, int totalFrames, out string why)
        {
            why = "";
            int position = 0;

            foreach (JToken token in scenes)
            {
                int start = (int?)token["start_frame"] ?? -1;
                int end = (int?)token["end_frame"] ?? -1;

                if (start != position || end <= start)
                {
                    why = $"a scene runs {start}-{end} where frame {position} was expected next";
                    return false;
                }

                position = end;
            }

            if (position != totalFrames)
            {
                why = $"the list ends at frame {position} of {totalFrames}";
                return false;
            }

            return true;
        }

        static int SceneCount(string mergedJson)
        {
            try { return (JObject.Parse(mergedJson)["scenes"] as JArray)?.Count ?? 0; }
            catch { return 0; }
        }
    }
}
