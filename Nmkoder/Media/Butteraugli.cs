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
    /// Scores Butteraugli between two files through VapourSynth, the same way <see cref="Ssimulacra2"/>
    /// scores its metric: no ffmpeg build computes it (the "butteraugli" string a BtbN binary carries
    /// belongs to libaom's --tune enum, not to a filter), and the plugins are already in this app's
    /// bundle for the AV1AN tab's Target Butteraugli mode - Vship, staged into the autoload folder per
    /// machine by <see cref="VshipStager"/> once its GPU check passes, and the julek plugin as the CPU
    /// fallback, present since it was first bundled for the day av1an calls it by its right name.
    /// <para/>
    /// The number is the per-frame *maximum* butteraugli distance (the INF norm), pooled as a mean over
    /// frames, at an intensity of 203 nits - which is exactly the quantity av1an's butteraugli-inf
    /// targets, so a score here reads on the same scale as the Target Butteraugli box. Both facts were
    /// read out of the pinned sources rather than assumed: Vship v4.0.2 writes _BUTTERAUGLI_INFNorm and
    /// defaults intensity_multiplier to 203; julek r3 writes _FrameButteraugli, which is libjxl's
    /// ButteraugliDistance and therefore the max of the diff map, but defaults intensity_target to 80 -
    /// so 203 is passed explicitly to both, or the two backends would disagree by a scale factor.
    /// <para/>
    /// julek refuses anything but RGB input, where Vship converts internally - so the julek path
    /// converts first, with the same recipe Vship's own toRGBS uses (Bicubic to RGBS, BT.709 matrix
    /// above 650 lines and BT.601 below, limited range in), which is what keeps a machine that scores
    /// on CPU comparable with one that scores on GPU.
    /// <para/>
    /// Unlike the others, Butteraugli measures distortion: 0 is identical and the number grows as the
    /// picture degrades, which is why <see cref="CrfLadder.LowerIsBetter"/> exists and every comparison
    /// against its anchor flips.
    /// </summary>
    class Butteraugli
    {
        /// <summary> The result of one scoring run: the pooled distance, the frames it was measured
        /// over, and why there is no score when there is not. </summary>
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
        /// Whether this machine can score Butteraugli at all - VapourSynth present, with a Vship or a
        /// julek that actually computes a frame. Cached for the session once it comes back true or
        /// false; an unanswered probe (no Python anywhere, a timeout) is not cached, so a plugin
        /// dropped in while the app is open is noticed without a restart. Callers that care which
        /// backend runs should have called <see cref="VshipStager.Reconcile"/> first, since what is in
        /// the autoload folder is what this probes.
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
                ? "VapourSynth with the julek plugin (or a working Vship) is needed to score Butteraugli, and it could not " +
                  "be found in this build or on your PATH."
                : "Butteraugli is scored through VapourSynth's Vship or julek plugins, which this app bundles on Windows " +
                  "only. Install VapourSynth with one of them to score it here, or pick VMAF or XPSNR instead.";
        }

        #region Probe

        /// <summary>
        /// Renders one frame of Butteraugli over a blank clip against itself, which answers every way
        /// the chain can be absent at once - no Python, no vapoursynth module, neither plugin, a julek
        /// whose DLL will not load - as a rendered frame or an error, rather than as the scoring run
        /// failing on the first real pair minutes into a ladder. Presence is not loadability here for
        /// the same reason it is not in the QTGMC, Vship and SSIMULACRA2 probes.
        /// </summary>
        private static async Task<bool?> Probe()
        {
            (string python, bool bundled) = VsPython.ResolvePython();

            if (python.IsEmpty())
            {
                // Not cached - see IsAvailableAsync. No Python anywhere the app looked: neither the
                // Windows bundle nor a system python3/python on PATH.
                _reason = "No Python could be found to run the Butteraugli scorer - not the Windows bundle, " +
                    "and no python3 or python on your PATH.";
                return null;
            }

            string dir = Paths.GetSessionDataPath();
            string script = Path.Combine(dir, "butter_probe.py");

            try
            {
                File.WriteAllText(script, ProbeScript);
                // Background like the QTGMC and Vship probes: short (a blank clip, one frame), and it
                // must not be killed by Stop the way a real scoring run must be.
                var result = await VsPython.Run(python, bundled, $"{script.Wrap()}", 90000, NmkoderProcess.ProcessType.Background);

                if (result == null)
                {
                    _reason = "the Butteraugli check did not answer in time.";
                    return null;
                }

                string output = result.Value.output;

                if (output.Contains("BUTTER_OK"))
                {
                    Logger.Log($"Butteraugli scoring is available ({python}).", true);
                    _reason = "";
                    return true;
                }

                _reason = SummarizeError(output);
                Logger.Log($"Butteraugli is not available: {_reason}", true);
                return false;
            }
            catch (Exception e)
            {
                _reason = e.Message;
                Logger.Log($"Butteraugli probe failed: {e.Message}\n{e.StackTrace}", true);
                return null;
            }
        }

        /// <summary> Turns the probe's stderr into one line naming the missing piece. </summary>
        private static string SummarizeError(string output)
        {
            string o = (output ?? "").ToLowerInvariant();

            // "bundled" is deliberately not said - the Python may be a system one found on PATH off
            // Windows, and the fix is the same either way: make 'import vapoursynth' work.
            if (o.Contains("no module named 'vapoursynth'") || o.Contains("no module named vapoursynth"))
                return "Python was found but cannot import VapourSynth. Install VapourSynth (with the julek or Vship " +
                    "plugin) so \"import vapoursynth\" works, or pick VMAF or XPSNR instead.";

            if (o.Contains("butter_no_plugin") || o.Contains("no attribute 'julek'") || o.Contains("no attribute 'butteraugli'"))
                return "VapourSynth is present but neither Vship nor the julek plugin (which compute Butteraugli) is " +
                    "loaded. Install one so \"core.julek\" or \"core.vship\" exists, or pick VMAF or XPSNR instead.";

            string firstError = (output ?? "").SplitIntoLines().LastOrDefault(x => x.Contains("Error") || x.Contains("error"));
            return firstError.IsNotEmpty() ? firstError.Trim().Trunc(200) : "the VapourSynth check produced no score.";
        }

        // Kept apart from the scoring script so the probe stays cheap - a blank clip, one frame, no
        // file to index. hasattr rather than a bare call so a missing plugin is BUTTER_NO_PLUGIN
        // rather than an AttributeError traceback the message would have to parse.
        private static readonly string ProbeScript = @"import sys
try:
    import vapoursynth as vs
except Exception as e:
    sys.stderr.write('no module named vapoursynth: %s\n' % e)
    sys.exit(2)

core = vs.core

" + ScorerSnippet + @"

try:
    clip = core.std.BlankClip(width=64, height=64, format=vs.YUV420P8, length=1, color=[128, 128, 128])
    scored = scorer(clip, clip)
    f = scored.get_frame(0)
    val = read_score(f.props)
    if val is None:
        raise RuntimeError('no butteraugli frame property was written')
    sys.stdout.write('BUTTER_OK %f\n' % float(val))
except SystemExit:
    raise
except RuntimeError as e:
    if 'BUTTER_NO_PLUGIN' in str(e):
        sys.stderr.write('BUTTER_NO_PLUGIN\n')
        sys.exit(3)
    sys.stderr.write('BUTTER_FAIL: %s\n' % e)
    sys.exit(4)
except Exception as e:
    sys.stderr.write('BUTTER_FAIL: %s\n' % e)
    sys.exit(4)
";

        #endregion

        #region Scoring

        /// <summary>
        /// The backend preference and the frame-property read, shared verbatim by the probe and the
        /// scoring script so what is probed is what runs.
        /// <para/>
        /// Vship first - it is the GPU plugin, an order of magnitude faster, and the autoload folder
        /// only holds it on a machine whose GPU passed its check - then julek. The intensity is 203 on
        /// both, said explicitly: it is Vship's own default but julek's is 80, and the two must agree
        /// or the CPU and GPU paths would score the same pair differently. to_rgbs mirrors Vship's
        /// internal conversion (Bicubic, BT.709 above 650 lines / BT.601 below, limited in, full out)
        /// for the same reason. The property read tries Vship v4's name, then julek's, then the single
        /// name older Vship builds wrote.
        /// </summary>
        private const string ScorerSnippet = @"def to_rgbs(c):
    f = c.format
    if f.color_family == vs.RGB and f.sample_type == vs.FLOAT and f.bits_per_sample == 32:
        return c
    if f.color_family == vs.RGB:
        return core.resize.Bicubic(c, format=vs.RGBS)
    matrix = 1 if c.height > 650 else 6
    return core.resize.Bicubic(c, format=vs.RGBS, matrix_in=matrix, transfer_in=1, primaries_in=1,
                               range_in=0, transfer=1, primaries=1, range=1)


def scorer(a, b):
    v = getattr(core, 'vship', None)
    if v is not None and hasattr(v, 'BUTTERAUGLI'):
        return v.BUTTERAUGLI(reference=a, distorted=b, intensity_multiplier=203.0)
    j = getattr(core, 'julek', None)
    if j is not None and hasattr(j, 'Butteraugli'):
        return j.Butteraugli(reference=to_rgbs(a), distorted=to_rgbs(b), intensity_target=203.0)
    raise RuntimeError('BUTTER_NO_PLUGIN')


def read_score(props):
    for name in ('_BUTTERAUGLI_INFNorm', '_FrameButteraugli', '_BUTTERAUGLI'):
        try:
            return props[name]
        except KeyError:
            pass
    return None
";

        /// <summary>
        /// The pooled Butteraugli distance of <paramref name="distortedPath"/> against
        /// <paramref name="referencePath"/>, as a mean over frames of the per-frame maximum (INF norm).
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
            (string python, bool bundled) = VsPython.ResolvePython();

            if (python.IsEmpty())
                return new Score { Problem = GetUnavailableReason() };

            string cacheDir = Path.Combine(Paths.GetSessionDataPath(), "vsindex");
            Directory.CreateDirectory(cacheDir);
            string script = Path.Combine(Paths.GetSessionDataPath(), "butter_score.py");

            try
            {
                File.WriteAllText(script, BuildScoreScript(referencePath, distortedPath, cacheDir));
                // Primary, not Background: this is minutes of compute a Stop must be able to kill, the
                // same as the ffmpeg scoring runs and the SSIMULACRA2 one beside it.
                var result = await VsPython.Run(python, bundled, $"{script.Wrap()}", timeoutMs, NmkoderProcess.ProcessType.Primary);

                if (result == null)
                    return new Score { Problem = "the Butteraugli scoring run did not finish in time." };

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
            string line = (output ?? "").SplitIntoLines().LastOrDefault(x => x.StartsWith("NMKODER_BUTTER "));

            if (line == null)
                return new Score { Problem = "" };

            string[] parts = line.Substring("NMKODER_BUTTER ".Length).Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int frames))
                return new Score { Value = value, Frames = frames };

            return new Score { Problem = "the Butteraugli score line could not be parsed." };
        }

        private static string BuildScoreScript(string referencePath, string distortedPath, string cacheDir)
        {
            return @"import sys
import vapoursynth as vs

core = vs.core
REF = " + VsPython.PyString(referencePath) + @"
DIST = " + VsPython.PyString(distortedPath) + @"
CACHE = " + VsPython.PyString(cacheDir) + @"


" + VsPython.OpenVideoSnippet + @"

" + ScorerSnippet + @"

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
    val = read_score(f.props)
    if val is None:
        continue
    total += float(val)
    count += 1

sys.stdout.write('NMKODER_BUTTER %.6f %d\n' % ((total / count) if count else 0.0, count))
";
        }

        #endregion
    }
}
