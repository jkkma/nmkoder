using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.OS;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nmkoder.Media
{
    /// <summary>
    /// Scores SSIMULACRA2 between two files through VapourSynth, which is the only way this app can:
    /// libvmaf has no SSIMULACRA2 feature extractor, so no ffmpeg build computes it (see the note in
    /// <see cref="Nmkoder.Data.CrfLadder"/>). The AV1AN tab already scores its Target SSIMULACRA2 mode
    /// through the same plugin - vszip (com.julek.vszip), bundled on Windows only - so this is that
    /// capability brought to the Sample Encodes utility rather than a new dependency.
    /// <para/>
    /// The plumbing is the same shape as <see cref="VshipStager.ProbeDll"/> and the QTGMC probe: the
    /// bundled embeddable Python (bin/av1an/vsynth/python.exe) runs a script with the vapoursynth
    /// module on its path, and its stdout is parsed. vspipe cannot do this job - it renders frames and
    /// discards their properties, and SSIMULACRA2's score is a per-frame float property.
    /// <para/>
    /// None of this can be exercised in a web session - there is no VapourSynth, and no vszip - so the
    /// script text, the parse and the wiring are what is checked here, and the release workflow's
    /// win-x64 job renders a real SSIMULACRA2 frame through the bundled plugin, the same way it proves
    /// the QTGMC toolchain. What a real machine has to confirm is that an encode of a source scores
    /// lower than the source against itself, and that the number tracks CRF.
    /// </summary>
    class Ssimulacra2
    {
        /// <summary> The result of one scoring run: the pooled score, the frames it was measured over,
        /// and why there is no score when there is not. </summary>
        public struct Score
        {
            public double Value;
            public int Frames;
            public string Problem;
            public bool Ok => Problem.IsEmpty() && Frames > 0;
        }

        private static bool? _available;
        private static string _reason = "";
        private static readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Whether this machine can score SSIMULACRA2 at all - VapourSynth present, with a vszip (or
        /// vship) that actually computes a frame. Cached for the session once it comes back true or
        /// false; an unanswered probe (no bundled Python, a timeout) is not cached, so a plugin
        /// dropped in while the app is open is noticed without a restart, exactly as the QTGMC probe
        /// leaves its "not found" verdict uncached.
        /// </summary>
        public static async Task<bool> IsAvailableAsync()
        {
            if (_available != null)
                return _available == true;

            await gate.WaitAsync();

            try
            {
                if (_available != null)
                    return _available == true;

                bool? verdict = await Probe();

                if (verdict == null)
                    return false;

                _available = verdict;
                return verdict == true;
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary> Why scoring is unavailable, for a message. Empty once a probe has said it can run,
        /// and a plain platform sentence before any probe has been made off Windows. </summary>
        public static string GetUnavailableReason()
        {
            if (_reason.IsNotEmpty())
                return _reason;

            return Shell.IsWindows
                ? "VapourSynth with the vszip plugin is needed to score SSIMULACRA2, and it could not be found in this build or on your PATH."
                : "SSIMULACRA2 is scored through VapourSynth's vszip plugin, which this app bundles on Windows only. Install " +
                  "VapourSynth and vszip through your package manager to score it here, or pick VMAF or XPSNR instead.";
        }

        private static string VsynthDir()
        {
            return Path.Combine(Paths.GetBinPath(), "av1an", "vsynth");
        }

        /// <summary> The bundled embeddable Python, or "" when there is none - which off Windows there
        /// never is, the portable VapourSynth being a Windows-only bundle. </summary>
        private static string PythonPath()
        {
            string resolved = Shell.ResolveExecutable("python", new[] { VsynthDir() });
            return File.Exists(resolved) ? resolved : "";
        }

        #region Probe

        /// <summary>
        /// Renders one frame of SSIMULACRA2 over a blank clip against itself, which answers every way
        /// the chain can be absent at once - no Python, no vapoursynth module, no vszip, a vszip whose
        /// DLL will not load - as a rendered frame or an error, rather than as the scoring run failing
        /// on the first real pair minutes into a ladder. Presence is not loadability here for the same
        /// reason it is not in the QTGMC and Vship probes.
        /// </summary>
        private static async Task<bool?> Probe()
        {
            string python = PythonPath();

            if (python.IsEmpty())
            {
                // Not cached - see IsAvailableAsync. Off Windows the reason is the platform; on it,
                // a missing bundle or PATH entry.
                _reason = Shell.IsWindows
                    ? "VapourSynth is not bundled with this build and Python is not on your PATH, so SSIMULACRA2 cannot be scored."
                    : "";
                return null;
            }

            string dir = Paths.GetSessionDataPath();
            string script = Path.Combine(dir, "ssimu2_probe.py");

            try
            {
                File.WriteAllText(script, ProbeScript);
                var result = await RunPython(python, $"{script.Wrap()}", 90000);

                if (result == null)
                {
                    _reason = "the SSIMULACRA2 check did not answer in time.";
                    return null;
                }

                string output = result.Value.output;

                if (output.Contains("SSIMU2_OK"))
                {
                    Logger.Log($"SSIMULACRA2 scoring is available ({python}).", true);
                    _reason = "";
                    return true;
                }

                _reason = SummarizeError(output);
                Logger.Log($"SSIMULACRA2 is not available: {_reason}", true);
                return false;
            }
            catch (Exception e)
            {
                _reason = e.Message;
                Logger.Log($"SSIMULACRA2 probe failed: {e.Message}\n{e.StackTrace}", true);
                return null;
            }
        }

        /// <summary> Turns the probe's stderr into one line naming the missing piece. </summary>
        private static string SummarizeError(string output)
        {
            string o = (output ?? "").ToLowerInvariant();

            if (o.Contains("no module named 'vapoursynth'") || o.Contains("no module named vapoursynth"))
                return "the bundled Python cannot import VapourSynth.";

            if (o.Contains("ssimu2_no_plugin") || o.Contains("no attribute 'vszip'") || o.Contains("no attribute 'ssimulacra2'") || o.Contains("no attribute 'metrics'"))
                return "VapourSynth is present but the vszip plugin (which computes SSIMULACRA2) is not loaded.";

            string firstError = (output ?? "").SplitIntoLines().LastOrDefault(x => x.Contains("Error") || x.Contains("error"));
            return firstError.IsNotEmpty() ? firstError.Trim().Trunc(200) : "the VapourSynth check produced no score.";
        }

        // Kept apart from the scoring script so the probe stays cheap - a blank clip, one frame, no
        // file to index. hasattr rather than a bare call so a missing plugin is SSIMU2_NO_PLUGIN
        // rather than an AttributeError traceback the message would have to parse.
        private const string ProbeScript = @"import sys
try:
    import vapoursynth as vs
except Exception as e:
    sys.stderr.write('no module named vapoursynth: %s\n' % e)
    sys.exit(2)

core = vs.core

def scorer(a, b):
    v = getattr(core, 'vship', None)
    if v is not None and hasattr(v, 'SSIMULACRA2'):
        return v.SSIMULACRA2(reference=a, distorted=b)
    z = getattr(core, 'vszip', None)
    if z is None:
        sys.stderr.write('SSIMU2_NO_PLUGIN\n')
        sys.exit(3)
    if hasattr(z, 'SSIMULACRA2'):
        return z.SSIMULACRA2(reference=a, distorted=b)
    if hasattr(z, 'Metrics'):
        return z.Metrics(reference=a, distorted=b, mode=0)
    sys.stderr.write('SSIMU2_NO_PLUGIN\n')
    sys.exit(3)

try:
    clip = core.std.BlankClip(width=64, height=64, format=vs.YUV420P8, length=1, color=[128, 128, 128])
    scored = scorer(clip, clip)
    f = scored.get_frame(0)
    props = f.props
    try:
        val = props['SSIMULACRA2']
    except KeyError:
        val = props['_SSIMULACRA2']
    sys.stdout.write('SSIMU2_OK %f\n' % float(val))
except SystemExit:
    raise
except Exception as e:
    sys.stderr.write('SSIMU2_FAIL: %s\n' % e)
    sys.exit(4)
";

        #endregion

        #region Scoring

        /// <summary>
        /// The pooled SSIMULACRA2 of <paramref name="distortedPath"/> against <paramref name="referencePath"/>,
        /// as a mean over frames.
        /// <para/>
        /// The two files are opened frame-for-frame through the same LSMASH source the QTGMC path uses,
        /// so there is no timestamp alignment to get wrong - the scorer trims both to the shorter frame
        /// count and compares frame i against frame i. In the CRF ladder the reference is a lossless cut
        /// and the distorted file is an encode of that exact cut, so the two are the same pictures and
        /// the alignment is trivially right; this method does not assume that, only that the two are
        /// meant to be compared frame by frame.
        /// </summary>
        public static async Task<Score> ScoreAsync(string referencePath, string distortedPath, int timeoutMs = 1_800_000)
        {
            string python = PythonPath();

            if (python.IsEmpty())
                return new Score { Problem = GetUnavailableReason() };

            string cacheDir = Path.Combine(Paths.GetSessionDataPath(), "vsindex");
            Directory.CreateDirectory(cacheDir);
            string script = Path.Combine(Paths.GetSessionDataPath(), "ssimu2_score.py");

            try
            {
                File.WriteAllText(script, BuildScoreScript(referencePath, distortedPath, cacheDir));
                var result = await RunPython(python, $"{script.Wrap()}", timeoutMs);

                if (result == null)
                    return new Score { Problem = "the SSIMULACRA2 scoring run did not finish in time." };

                string output = result.Value.output;
                Score parsed = Parse(output);

                if (parsed.Ok)
                    return parsed;

                // A run that produced nothing usable - name what went wrong rather than a bare zero.
                return new Score { Problem = parsed.Problem.IsNotEmpty() ? parsed.Problem : SummarizeError(output) };
            }
            catch (Exception e)
            {
                return new Score { Problem = e.Message };
            }
        }

        /// <summary>
        /// Reads the one line the script is built to print. Kept public and separate from the process so
        /// it can be checked against known output without VapourSynth, which is the half of this that a
        /// web session can verify.
        /// </summary>
        public static Score Parse(string output)
        {
            string line = (output ?? "").SplitIntoLines().LastOrDefault(x => x.StartsWith("NMKODER_SSIMU2 "));

            if (line == null)
                return new Score { Problem = "" };

            string[] parts = line.Substring("NMKODER_SSIMU2 ".Length).Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int frames))
                return new Score { Value = value, Frames = frames };

            return new Score { Problem = "the SSIMULACRA2 score line could not be parsed." };
        }

        private static string BuildScoreScript(string referencePath, string distortedPath, string cacheDir)
        {
            // The prop name differs by plugin generation - new vszip and vship expose "SSIMULACRA2",
            // legacy vszip "_SSIMULACRA2" - so each frame is read by trying both, which is what av1an
            // does and what keeps this working across a vszip a user brought themselves.
            return @"import sys
import vapoursynth as vs

core = vs.core
REF = " + PyString(referencePath) + @"
DIST = " + PyString(distortedPath) + @"
CACHE = " + PyString(cacheDir) + @"


def open_video(path):
    attempts = [
        lambda: core.lsmas.LWLibavSource(source=path, cachedir=CACHE),
        lambda: core.lsmas.LWLibavSource(source=path),
    ]
    problems = []
    for attempt in attempts:
        try:
            return attempt()
        except Exception as e:
            problems.append(str(e))
    raise RuntimeError('no source plugin could open %r: %s' % (path, '; '.join(problems)))


def scorer(a, b):
    v = getattr(core, 'vship', None)
    if v is not None and hasattr(v, 'SSIMULACRA2'):
        return v.SSIMULACRA2(reference=a, distorted=b)
    z = getattr(core, 'vszip', None)
    if z is None:
        raise RuntimeError('SSIMU2_NO_PLUGIN')
    if hasattr(z, 'SSIMULACRA2'):
        return z.SSIMULACRA2(reference=a, distorted=b)
    if hasattr(z, 'Metrics'):
        return z.Metrics(reference=a, distorted=b, mode=0)
    raise RuntimeError('SSIMU2_NO_PLUGIN')


ref = open_video(REF)
dist = open_video(DIST)
n = min(ref.num_frames, dist.num_frames)
if n < 1:
    raise RuntimeError('one of the clips has no frames')
ref = ref[:n]
dist = dist[:n]
scored = scorer(ref, dist)

total = 0.0
count = 0

try:
    frames = scored.frames(close=True)
except TypeError:
    frames = scored.frames()

for f in frames:
    props = f.props
    try:
        val = props['SSIMULACRA2']
    except KeyError:
        try:
            val = props['_SSIMULACRA2']
        except KeyError:
            continue
    total += float(val)
    count += 1

sys.stdout.write('NMKODER_SSIMU2 %.6f %d\n' % ((total / count) if count else 0.0, count))
";
        }

        #endregion

        #region Process plumbing

        /// <summary>
        /// Runs the bundled Python with the vsynth folder as both the working directory and the sole
        /// PATH entry, so the embeddable interpreter finds its own DLLs and the vapoursynth module -
        /// the same launch <see cref="VshipStager.ProbeDll"/> makes, and for the same reasons.
        /// </summary>
        private static async Task<(int exitCode, string output)?> RunPython(string python, string args, int timeoutMs)
        {
            string vsynth = VsynthDir();

            System.Diagnostics.Process proc = OsUtils.NewProcess(true, NmkoderProcess.ProcessType.Background, python);
            OsUtils.SetPathVar(proc, new[] { vsynth });

            if (Directory.Exists(vsynth))
                proc.StartInfo.WorkingDirectory = vsynth;

            proc.StartInfo.Arguments = args;
            Logger.Log($"Running: {python} {args}", true);
            proc.Start();

            // Both pipes at once - reading one to the end first deadlocks as soon as the other fills
            // its buffer, the caveat the av1an help call, the QTGMC runner and the Vship probe all note.
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

        private static string PyString(string value)
        {
            return "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        #endregion
    }
}
