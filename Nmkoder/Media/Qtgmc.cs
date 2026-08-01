using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.OS;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nmkoder.Media
{
    /// <summary>
    /// Runs QTGMC - the motion-compensated deinterlacer everyone means when they say "the good one" -
    /// by writing a VapourSynth script and letting VSPipe feed its frames to ffmpeg.
    /// <para/>
    /// ffmpeg cannot call QTGMC itself. QTGMC is a Python function built on a handful of VapourSynth
    /// plugins (mvtools for the motion search, znedi3 for the field interpolation, RemoveGrain and
    /// fmtconv underneath both), and the only way to get its output into an encoder is to evaluate the
    /// script and pipe the frames across. That is what <see cref="BuildVspipeCommand"/> produces: a
    /// `vspipe … | ffmpeg …` pair where ffmpeg reads the deinterlaced video from stdin and everything
    /// else - audio, subtitles, metadata - still comes from the file itself.
    /// <para/>
    /// None of it can be assumed present. The Windows build bundles VapourSynth for av1an already and
    /// the release adds QTGMC's plugins beside it, but a bundling step is best-effort, and on Linux
    /// and macOS VapourSynth is whatever the user installed. So the whole chain is asked once per
    /// session - by building a QTGMC graph over a blank clip and actually rendering a frame of it -
    /// and a machine that cannot do it falls back to ffmpeg's own bwdif with the reason said out loud.
    /// </summary>
    class Qtgmc
    {
        /// <summary> havsfunc's own preset names, slowest first. Its documented default is "Slower";
        /// this app defaults to "Medium" because the sources this runs on are usually hour-long tape
        /// captures, where the difference between the two is hours of wall clock. </summary>
        public static readonly string[] Presets = { "Placebo", "Very Slow", "Slower", "Slow", "Medium", "Fast", "Faster", "Very Fast" };
        public const string DefaultPreset = "Medium";

        /// <summary> This session's verdict: true/false once the chain has been asked, null while it
        /// has not been - or when the question went unanswered, which is asked again rather than
        /// acted on, exactly as the Vship GPU probe treats a timeout. </summary>
        private static bool? available;

        /// <summary> Why QTGMC cannot run here, in a sentence, or "" when it can. </summary>
        public static string UnavailableReason { get; private set; } = "";

        /// <summary> This session's answer if there is one, null while there is not - for the UI,
        /// which describes what will happen without being allowed to spend a probe finding out. </summary>
        public static bool? KnownAvailability { get { return available; } }

        /// <summary> Guards the probe so two callers arriving at once cannot both run it - a batch
        /// resolving its first file while the UI is still describing the loaded one. </summary>
        private static readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Whether QTGMC can actually be run on this machine. Cached for the session once a definite
        /// answer comes back; a probe that could not be run at all is left unanswered so a VapourSynth
        /// installed halfway through a session is still found.
        /// </summary>
        public static async Task<bool> IsAvailableAsync()
        {
            if (available != null)
                return available == true;

            await gate.WaitAsync();

            try
            {
                if (available != null)
                    return available == true;

                bool? verdict = await Probe();

                if (verdict == null)
                    return false; // Unanswered - not cached, so the next encode asks again

                available = verdict;
                return verdict == true;
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary> Where VSPipe lives, or "" when it is nowhere to be found. The bundled portable
        /// VapourSynth sits beside av1an's; anything else comes off PATH. </summary>
        public static string GetVspipePath()
        {
            string resolved = Shell.ResolveExecutable("vspipe", GetSearchDirs());
            return File.Exists(resolved) ? resolved : "";
        }

        /// <summary> Where to look for VSPipe, and what a VSPipe launched directly needs on its PATH:
        /// the bundled folders first, because the portable build keeps Python and the VapourSynth
        /// core beside the executable rather than installing them, then whatever is on PATH. </summary>
        public static string[] GetSearchDirs()
        {
            string av1an = Path.Combine(Paths.GetBinPath(), "av1an");
            return new[] { Path.Combine(av1an, "vsynth"), av1an, Paths.GetBinPath() }
                .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Shell.PathSeparator))
                .ToArray();
        }

        /// <summary> The folder VSPipe should run in and find its libraries through, or "" when
        /// VapourSynth is installed on the system rather than bundled. </summary>
        public static string GetVsynthDir()
        {
            string dir = Path.Combine(Paths.GetBinPath(), "av1an", "vsynth");
            return Directory.Exists(dir) ? dir : "";
        }

        /// <summary>
        /// The directories a shell running <see cref="BuildVspipeCommand"/> has to have on its PATH
        /// for the bare name "vspipe" to resolve. Both of them matter: the portable build keeps
        /// Python and the VapourSynth core beside the executable, and on Windows
        /// <see cref="OsUtils.GetPathVar"/> deliberately throws the inherited PATH away - so an
        /// installed VapourSynth is only reachable if the folder it sits in is named here.
        /// </summary>
        public static string[] GetPathDirs()
        {
            string exe = GetVspipePath();
            return new[] { GetVsynthDir(), exe.IsEmpty() ? "" : Path.GetDirectoryName(exe) }
                .Where(x => x.IsNotEmpty()).Distinct().ToArray();
        }

        #region Probe

        /// <summary>
        /// Builds a QTGMC graph over a tiny blank clip and renders one frame of it. Deliberately not a
        /// list of files to look for: what matters is whether the script evaluates and produces a
        /// frame, and that question covers the source plugins' dependencies, a Python that cannot
        /// import havsfunc, a znedi3 whose weights file did not travel with it, and every other way
        /// the chain comes apart - each of which would otherwise surface as ffmpeg complaining about
        /// invalid data on stdin, minutes into an encode.
        /// </summary>
        private static async Task<bool?> Probe()
        {
            string vspipe = GetVspipePath();

            if (vspipe.IsEmpty())
            {
                UnavailableReason = Shell.IsWindows
                    ? "VapourSynth is not bundled with this build and VSPipe is not on your PATH"
                    : "VapourSynth is not installed - VSPipe is not on your PATH";
                // Not cached as a verdict: looking for the executable again costs a handful of
                // File.Exists calls, and a VapourSynth installed while the app is open should not
                // need a restart to be noticed.
                return null;
            }

            string dir = Paths.GetSessionDataPath();
            string script = Path.Combine(dir, "qtgmc_probe.vpy");
            string frame = Path.Combine(dir, "qtgmc_probe.y4m");

            try
            {
                File.WriteAllText(script, BuildProbeScript());
                IoUtils.TryDeleteIfExists(frame);

                var result = await RunVspipe(vspipe, $"-c y4m -s 0 -e 0 {script.Wrap()} {frame.Wrap()}", 90000);

                if (result == null)
                {
                    UnavailableReason = "the VapourSynth check did not answer in time";
                    return null;
                }

                if (result.Value.exitCode == 0)
                {
                    Logger.Log($"QTGMC is available ({vspipe}).", true);
                    UnavailableReason = "";
                    return true;
                }

                UnavailableReason = SummarizeVsError(result.Value.output);
                Logger.Log($"QTGMC check failed (exit {result.Value.exitCode}): {result.Value.output.Trim().Trunc(1200)}", true);
                return false;
            }
            catch (Exception e)
            {
                Logger.Log($"QTGMC check could not be run: {e.Message}", true);
                UnavailableReason = $"the VapourSynth check could not be run ({e.Message})";
                return null;
            }
            finally
            {
                IoUtils.TryDeleteIfExists(frame);
            }
        }

        /// <summary>
        /// Turns VapourSynth's output into one sentence. Its errors arrive as Python tracebacks, whose
        /// last line is the only part worth putting in front of anyone; the two failures that are
        /// really "this was never installed" are named outright, because the traceback for those says
        /// nothing a user could act on.
        /// </summary>
        private static string SummarizeVsError(string output)
        {
            if (output.Contains("No module named 'havsfunc'"))
                return "the havsfunc script package, which provides QTGMC, is not installed for this VapourSynth";

            if (output.Contains("No module named 'vsutil'") || output.Contains("No module named 'mvsfunc'"))
                return "havsfunc's own dependencies (vsutil, mvsfunc) are not installed for this VapourSynth";

            if (output.Contains("QTGMC_MISSING_PLUGINS"))
                return $"VapourSynth is missing the plugins QTGMC needs ({output.Split("QTGMC_MISSING_PLUGINS").Last().SplitIntoLines().First().Trim()})";

            string last = output.SplitIntoLines().Select(x => x.Trim()).LastOrDefault(x => x.IsNotEmpty()) ?? "";
            return last.IsEmpty() ? "the VapourSynth check failed" : $"VapourSynth said: {last.Trunc(200)}";
        }

        private static string BuildProbeScript()
        {
            // The plugin check is explicit and comes first because its message is the useful one: the
            // traceback from a missing mvtools is a line deep inside havsfunc about an attribute that
            // does not exist, naming neither the plugin nor what to do about it.
            //
            // The list is what havsfunc 33's QTGMC actually resolves on the default path, established
            // by building the graph rather than by reading it - grepping finds only half of them,
            // since havsfunc reaches most plugins as clip.<ns>.<Func>() method chains. Two are not
            // obvious: eedi3m because QTGMC_Interpolate builds an eedi3 partial before it looks at
            // EdiMode, so it is resolved even on the NNEDI3 path, and znedi3 by that name rather than
            // nnedi3 or nnedi3cl, because that is the one it calls when opencl is off. focus2
            // (TemporalSoften2) in turn refuses to run without misc.
            return @"import sys
import vapoursynth as vs

core = vs.core

needed = ['mv', 'rgvs', 'fmtc', 'focus2', 'misc', 'znedi3', 'eedi3m']
missing = [n for n in needed if not hasattr(core, n)]

if missing:
    print('QTGMC_MISSING_PLUGINS ' + ', '.join(missing), file=sys.stderr)
    raise RuntimeError('missing plugins: ' + ', '.join(missing))

import havsfunc

clip = core.std.BlankClip(width=160, height=120, format=vs.YUV420P8, length=4, fpsnum=30000, fpsden=1001, color=[128, 128, 128])
clip = core.std.SetFieldBased(clip, 2)
clip = havsfunc.QTGMC(clip, Preset='Very Fast', TFF=True, FPSDivisor=1, opencl=False)
clip.set_output()
";
        }

        #endregion

        #region Script

        /// <summary>
        /// Writes the script this encode will be fed through and returns its path. Regenerated per run
        /// rather than kept, because everything in it - the source, the field order, the preset - is
        /// this run's, and in a batch it is a different file every time.
        /// </summary>
        public static string WriteScript(DeinterlacePlan plan, string sourcePath, string scriptPath)
        {
            var sb = new StringBuilder();
            string cacheDir = Path.Combine(Paths.GetSessionDataPath(), "vsindex");
            Directory.CreateDirectory(cacheDir);

            sb.AppendLine("# Written by Nmkoder for one encode - it is rewritten every run, so edits do not survive.");
            sb.AppendLine("import vapoursynth as vs");
            sb.AppendLine("import havsfunc");
            sb.AppendLine();
            sb.AppendLine("core = vs.core");
            sb.AppendLine($"SOURCE = {PyString(sourcePath)}");
            sb.AppendLine($"CACHE_DIR = {PyString(cacheDir)}");
            sb.AppendLine($"TFF = {(plan.TopFieldFirst ? "True" : "False")}");
            sb.AppendLine($"PRESET = {PyString(plan.QtgmcPreset)}");
            sb.AppendLine($"FPS_DIVISOR = {(plan.DoubleRate ? 1 : 2)}");
            sb.AppendLine();
            // Index files are written beside the source by default, which would leave .lwi litter in
            // the user's own folders; every plugin here can be told to put them somewhere else, and
            // each is tried again without that argument in case this build of it cannot.
            sb.AppendLine(@"def open_video(path):
    attempts = [
        ('lsmas', lambda: core.lsmas.LWLibavSource(source=path, cachedir=CACHE_DIR)),
        ('lsmas', lambda: core.lsmas.LWLibavSource(source=path)),
        ('bestsource', lambda: core.bs.VideoSource(source=path, cachepath=CACHE_DIR)),
        ('bestsource', lambda: core.bs.VideoSource(source=path)),
        ('ffms2', lambda: core.ffms2.Source(source=path)),
    ]
    problems = []

    for name, attempt in attempts:
        try:
            return attempt()
        except Exception as e:
            problems.append('%s: %s' % (name, e))

    raise RuntimeError('No VapourSynth source plugin could open the file.\n  ' + '\n  '.join(problems))


clip = open_video(SOURCE)
# Said rather than inferred: the source plugin passes through whatever the file claims, and the
# whole reason a file reaches this script is that what it claims cannot be relied on.
clip = core.std.SetFieldBased(clip, 2 if TFF else 1)
clip = havsfunc.QTGMC(clip, Preset=PRESET, TFF=TFF, FPSDivisor=FPS_DIVISOR, opencl=False)
clip.set_output()");

            Directory.CreateDirectory(Path.GetDirectoryName(scriptPath));
            File.WriteAllText(scriptPath, sb.ToString());
            Logger.Log($"Wrote QTGMC script to '{scriptPath}':\n{sb}", true);
            return scriptPath;
        }

        /// <summary>
        /// The `vspipe … |` half of the command, ready to be put in front of an ffmpeg invocation.
        /// VSPipe's own diagnostics go to a file rather than into ffmpeg's output: they are on stderr,
        /// which the shell leaves interleaved with ffmpeg's, and a VapourSynth traceback landing in
        /// the middle of an encode's progress lines is neither readable there nor available
        /// afterwards, when it is the only thing that explains a truncated encode.
        /// <para/>
        /// Named rather than given as a path, and that is not a style choice: `cmd /C` strips the
        /// first quote of a command line that begins with one, along with the last quote anywhere in
        /// it, whenever the line holds more than two - which every command here does. A full path in
        /// front would therefore arrive at cmd with its opening quote gone and the output file's
        /// closing quote gone with it. <see cref="GetPathDirs"/> is what makes the bare name resolve.
        /// </summary>
        public static string BuildVspipeCommand(string scriptPath, string logPath, bool append = false)
        {
            return $"vspipe -c y4m {scriptPath.Wrap()} - 2{(append ? ">>" : ">")}{logPath.Wrap()} | ";
        }

        /// <summary>
        /// What VSPipe made of the run, as a sentence, or "" if it is not complaining. Worth reading,
        /// because ffmpeg cannot tell the difference between "the script stopped early" and "the video
        /// ended": both are end-of-stream on stdin, so a VapourSynth error two thirds of the way
        /// through leaves ffmpeg finishing normally and exiting 0 over a file that stops there.
        /// <para/>
        /// A clean run ends with VSPipe's own "Output N frames in X seconds"; one such line per run is
        /// the whole test, which is why <paramref name="runs"/> is 2 for a two-pass encode - the first
        /// pass's success would otherwise vouch for a second pass that never finished. Only asked of
        /// runs that were not cancelled and did not already fail: killing ffmpeg breaks the pipe under
        /// VSPipe, and its complaint about that says nothing about the script.
        /// </summary>
        public static string ReadRunProblem(string logPath, int runs = 1)
        {
            try
            {
                if (!File.Exists(logPath))
                    return "";

                string log = File.ReadAllText(logPath);

                if (log.IsEmpty())
                    return "";

                Logger.Log($"vspipe output:\n{log.Trim()}", true);

                if (log.SplitIntoLines().Count(x => x.Contains("Output ") && x.Contains(" frames in ")) >= runs)
                    return "";

                string last = log.SplitIntoLines().Select(x => x.Trim()).LastOrDefault(x => x.IsNotEmpty()) ?? "";
                return last.IsEmpty() ? "" : $"VapourSynth did not finish: {last.Trunc(300)}";
            }
            catch (Exception e)
            {
                Logger.Log($"Could not read the VapourSynth log: {e.Message}", true);
                return "";
            }
        }

        /// <summary> A path as a Python string literal. Backslashes and quotes are the only characters
        /// a file path can carry that Python would read as syntax. </summary>
        private static string PyString(string value)
        {
            return "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        #endregion

        /// <summary> Runs VSPipe directly - no shell, so nothing expands what is in a file name - and
        /// hands back its exit code and combined output, or null if it had to be killed. </summary>
        private static async Task<(int exitCode, string output)?> RunVspipe(string exePath, string args, int timeoutMs)
        {
            Process proc = OsUtils.NewProcess(true, NmkoderProcess.ProcessType.Background, exePath);
            string vsynth = GetVsynthDir();
            OsUtils.SetPathVar(proc, GetSearchDirs());

            if (vsynth.IsNotEmpty())
                proc.StartInfo.WorkingDirectory = vsynth;

            proc.StartInfo.Arguments = args;
            Logger.Log($"Running: {exePath} {args}", true);
            proc.Start();

            // Both pipes at once - reading one to the end first deadlocks as soon as the other fills
            // its buffer, the same caveat av1an's help call and the Vship probe document.
            Task<string> stdout = proc.StandardOutput.ReadToEndAsync();
            Task<string> stderr = proc.StandardError.ReadToEndAsync();
            Task both = Task.WhenAll(stdout, stderr);

            if (await Task.WhenAny(both, Task.Delay(timeoutMs)) != both)
            {
                OsUtils.KillProcessTree(proc.Id);
                return null;
            }

            await proc.WaitForExitAsync();
            return (proc.ExitCode, $"{stdout.Result}\n{stderr.Result}");
        }
    }
}
