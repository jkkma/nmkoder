using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.OS;
using Nmkoder.Utils;
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
        /// see <see cref="DefaultPreset"/> for what this app opens on and why. </summary>
        public static readonly string[] Presets = { "Placebo", "Very Slow", "Slower", "Slow", "Medium", "Fast", "Faster", "Very Fast" };
        /// <summary> The preset every QTGMC setting in the app opens on. Very Slow is one step under
        /// Placebo and is the point at which QTGMC turns its noise processing on - see
        /// <see cref="NeedsNoisePlugins"/>, which is why this value decides which plugins have to be
        /// there. Slow by any measure: this is a deinterlacer that is already the expensive option, and
        /// the preset says how much of that to spend. </summary>
        public const string DefaultPreset = "Very Slow";

        /// <summary>
        /// The presets that turn QTGMC's noise processing on, which needs a plugin none of the others
        /// touch. havsfunc 33 sets NoiseProcess itself, from the preset name and nothing else - only
        /// Placebo and Very Slow get it - and the default NoisePreset then picks fft3dfilter as the
        /// denoiser. So the plugin set QTGMC needs has two shapes, not one, and which one applies is
        /// decided by this list.
        /// <para/>
        /// 2.8.6 and everything before it checked one shape and shipped the other's plugin missing:
        /// the probe rendered at Very Fast, passed, and then a Very Slow encode died two seconds in
        /// on "there is no attribute or namespace named fft3dfilter". Anything that asks whether
        /// QTGMC works has to ask about the preset that is going to run.
        /// </summary>
        private static readonly string[] NoiseProcessingPresets = { "Placebo", "Very Slow" };

        /// <summary> Whether <paramref name="preset"/> pulls in the denoiser plugin on top of the
        /// plugins every preset needs. </summary>
        public static bool NeedsNoisePlugins(string preset)
        {
            return NoiseProcessingPresets.Contains((preset ?? "").Trim(), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary> One probe's answer. Two of these are kept - the plugin set has two shapes - so a
        /// session that uses both a fast preset and a slow one asks twice and no more. </summary>
        private class Verdict
        {
            public bool? Available;
            public string Reason = "";
        }

        private static readonly Verdict basePlugins = new Verdict();
        private static readonly Verdict withNoisePlugins = new Verdict();

        private static Verdict VerdictFor(string preset)
        {
            return NeedsNoisePlugins(preset) ? withNoisePlugins : basePlugins;
        }

        /// <summary> Why QTGMC cannot run at this preset, in a sentence, or "" when it can. </summary>
        public static string GetUnavailableReason(string preset)
        {
            return VerdictFor(preset).Reason;
        }

        /// <summary> The answer for this preset if there is one, null while there is not - for the UI,
        /// which describes what will happen without being allowed to spend a probe finding out. </summary>
        public static bool? GetKnownAvailability(string preset)
        {
            return VerdictFor(preset).Available;
        }

        /// <summary> Guards the probe so two callers arriving at once cannot both run it - a batch
        /// resolving its first file while the UI is still describing the loaded one. </summary>
        private static readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Whether QTGMC can actually be run on this machine at <paramref name="preset"/>. Cached once
        /// a definite answer comes back, per plugin set rather than per preset - there are only two -
        /// and a probe that could not be run at all is left unanswered so a VapourSynth installed
        /// halfway through a session is still found.
        /// </summary>
        public static async Task<bool> IsAvailableAsync(string preset)
        {
            Verdict cached = VerdictFor(preset);

            if (cached.Available != null)
                return cached.Available == true;

            await gate.WaitAsync();

            try
            {
                if (cached.Available != null)
                    return cached.Available == true;

                bool? verdict = await Probe(preset, cached);

                if (verdict == null)
                    return false; // Unanswered - not cached, so the next encode asks again

                cached.Available = verdict;
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

        /// <summary> Where the bundled build keeps its plugins, when there is one. Only the probe uses
        /// this, and only to have somewhere to look when a namespace is missing and no plugin loaded
        /// from anywhere - so there is no loaded one left to ask where its neighbours are. </summary>
        public static string[] GetPluginDirs()
        {
            string dir = GetVsynthDir();
            dir = dir.IsEmpty() ? "" : Path.Combine(dir, "vs-plugins");
            return Directory.Exists(dir) ? new[] { dir } : new string[0];
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
        private static async Task<bool?> Probe(string preset, Verdict verdict)
        {
            string vspipe = GetVspipePath();

            if (vspipe.IsEmpty())
            {
                verdict.Reason = Shell.IsWindows
                    ? "VapourSynth is not bundled with this build and VSPipe is not on your PATH"
                    : "VapourSynth is not installed - VSPipe is not on your PATH";
                // Not cached as a verdict: looking for the executable again costs a handful of
                // File.Exists calls, and a VapourSynth installed while the app is open should not
                // need a restart to be noticed.
                return null;
            }

            // The probe runs at the preset that is going to run, because the plugins QTGMC resolves
            // depend on it. Named in the file names too, so a Very Fast probe and a Very Slow one do
            // not overwrite each other's script mid-flight.
            bool noise = NeedsNoisePlugins(preset);
            string tag = noise ? "noise" : "base";
            string dir = Paths.GetSessionDataPath();
            string script = Path.Combine(dir, $"qtgmc_probe_{tag}.vpy");
            string frame = Path.Combine(dir, $"qtgmc_probe_{tag}.y4m");

            try
            {
                File.WriteAllText(script, BuildProbeScript(preset));
                IoUtils.TryDeleteIfExists(frame);

                var result = await RunVspipe(vspipe, $"-c y4m -s 0 -e 0 {script.Wrap()} {frame.Wrap()}", 90000);

                if (result == null)
                {
                    verdict.Reason = "the VapourSynth check did not answer in time";
                    return null;
                }

                if (result.Value.exitCode == 0)
                {
                    Logger.Log($"QTGMC is available at {preset} ({vspipe}).", true);
                    verdict.Reason = "";
                    return true;
                }

                verdict.Reason = SummarizeVsError(result.Value.output, noise);
                Logger.Log($"QTGMC check failed at {preset} (exit {result.Value.exitCode}): {result.Value.output.Trim().Trunc(1200)}", true);
                return false;
            }
            catch (Exception e)
            {
                Logger.Log($"QTGMC check could not be run: {e.Message}", true);
                verdict.Reason = $"the VapourSynth check could not be run ({e.Message})";
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
        private static string SummarizeVsError(string output, bool noise)
        {
            if (output.Contains("No module named 'havsfunc'"))
                return "the havsfunc script package, which provides QTGMC, is not installed for this VapourSynth";

            if (output.Contains("No module named 'vsutil'") || output.Contains("No module named 'mvsfunc'"))
                return "havsfunc's own dependencies (vsutil, mvsfunc) are not installed for this VapourSynth";

            if (output.Contains("QTGMC_MISSING_PLUGINS"))
            {
                string names = output.Split("QTGMC_MISSING_PLUGINS").Last().SplitIntoLines().First().Trim();
                string rejected = DescribeRejectedPlugins(output, names);

                // "Missing" on its own is what sent 2.8.3 and 2.8.4 out the door believing the bundle
                // was complete: the file was there, correctly named and correctly built, and the core
                // had simply turned it down. Say which of the two it was.
                string what = rejected.IsEmpty()
                    ? $"VapourSynth has no plugin providing what QTGMC needs ({names})"
                    : $"VapourSynth refused a plugin QTGMC needs ({names}) - {rejected}";

                // Only the two slowest presets denoise, so a plugin missing only for them is worth
                // pointing at a preset the machine can actually run rather than at bwdif.
                return noise && names.Contains("fft3dfilter")
                    ? $"{what}; only Placebo and Very Slow need it, so a faster QTGMC preset would still run"
                    : what;
            }

            string last = output.SplitIntoLines().Select(x => x.Trim()).LastOrDefault(x => x.IsNotEmpty()) ?? "";
            return last.IsEmpty() ? "the VapourSynth check failed" : $"VapourSynth said: {last.Trunc(200)}";
        }

        /// <summary>
        /// What the probe found when it loaded the unregistered plugin files by hand, in a clause, or
        /// "" when every one of them was simply absent.
        /// <para/>
        /// One is quoted, not all: they share a cause far more often than not, and the full list is in
        /// the log either way. The one picked is a file named after a namespace that went missing -
        /// eedi3m out of EEDI3m.dll - because the folder holds plugins nothing here asked for, and a
        /// Vship staged for a GPU this machine does not have would otherwise be the line quoted at
        /// someone whose actual problem is somewhere else entirely.
        /// </summary>
        private static string DescribeRejectedPlugins(string output, string missingNames)
        {
            string[] lines = output.SplitIntoLines()
                .Where(x => x.Contains("QTGMC_PLUGIN_REJECTED"))
                .Select(x => x.Split("QTGMC_PLUGIN_REJECTED").Last().Trim())
                .Where(x => x.IsNotEmpty()).ToArray();

            if (lines.Length < 1)
                return "";

            string[] names = missingNames.Split(',').Select(x => x.Trim()).Where(x => x.IsNotEmpty()).ToArray();
            string best = lines.FirstOrDefault(line => names.Any(n => line.Split(':').First().Contains(n, StringComparison.OrdinalIgnoreCase))) ?? lines[0];

            int rest = lines.Length - 1;
            string more = rest < 1 ? "" : rest == 1 ? " (and one other plugin file)" : $" (and {rest} other plugin files)";
            return $"{best.Trunc(220)}{more}";
        }

        private static string BuildProbeScript(string preset)
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
            //
            // fft3dfilter is the one entry that is not constant. QTGMC's noise processing is what
            // needs it, havsfunc turns that on for Placebo and Very Slow and no other preset, and the
            // graph is therefore a different shape depending on which one is asked for - so the probe
            // builds the preset that is actually going to run rather than a fixed cheap one. Checking
            // Very Fast and running Very Slow is exactly how 2.8.6 shipped with this plugin absent.
            //
            // "Missing" then gets asked a second question, because it turned out to cover two very
            // different situations and to name only the innocent one. A namespace is absent either
            // because no such plugin is installed, or because one is installed and the core refused
            // it - which is what shipped in 2.8.3 and 2.8.4, where a bundled EEDI3m.dll built against
            // VapourSynth API 4.2 was turned down by an API 4.1 core, silently, since autoload
            // reports nothing at all. LoadPlugin does report it, so a missing namespace is followed
            // by loading the unregistered files by hand purely to collect the reason.
            string dirs = string.Join(", ", GetPluginDirs()
                .Select(x => $"'{x.Replace("\\", "\\\\").Replace("'", "\\'")}'"));

            string noise = NeedsNoisePlugins(preset) ? ", 'fft3dfilter'" : "";
            string safePreset = (preset ?? DefaultPreset).Replace("\\", "").Replace("'", "");

            return $@"import os, sys
import vapoursynth as vs

core = vs.core

needed = ['mv', 'rgvs', 'fmtc', 'focus2', 'misc', 'znedi3', 'eedi3m'{noise}]
missing = [n for n in needed if not hasattr(core, n)]

if missing:
    print('QTGMC_MISSING_PLUGINS ' + ', '.join(missing), file=sys.stderr)

    # Where the plugins that did load came from, which is where the ones that did not will be
    # too. Discovered rather than assumed, so this works for a system VapourSynth as well as the
    # bundled one; the bundled folder is named as well, for the case where nothing loaded at all
    # and there is no loaded plugin left to ask.
    seen, folders = set(), [{dirs}]

    for plugin in core.plugins():
        path = getattr(plugin, 'plugin_path', '') or ''
        if path:
            seen.add(os.path.normcase(path))
            folders.append(os.path.dirname(path))

    for folder in folders:
        try:
            names = sorted(os.listdir(folder))
        except OSError:
            continue

        for name in names:
            if not name.lower().endswith(('.dll', '.so', '.dylib')):
                continue

            full = os.path.join(folder, name)

            if os.path.normcase(full) in seen:
                continue

            seen.add(os.path.normcase(full))

            try:
                core.std.LoadPlugin(path=full)
            except Exception as problem:
                print('QTGMC_PLUGIN_REJECTED ' + name + ': ' + ' '.join(str(problem).split()), file=sys.stderr)

    raise RuntimeError('missing plugins: ' + ', '.join(missing))

import havsfunc

clip = core.std.BlankClip(width=160, height=120, format=vs.YUV420P8, length=4, fpsnum=30000, fpsden=1001, color=[128, 128, 128])
clip = core.std.SetFieldBased(clip, 2)
clip = havsfunc.QTGMC(clip, Preset='{safePreset}', TFF=True, FPSDivisor=1, opencl=False)
clip.set_output()
";
        }

        #endregion

        #region Script

        /// <summary>
        /// The source open every VapourSynth script here shares - the plugin fallback list, the
        /// render check and the length check. One copy, so the deinterlace and the cadence repair
        /// cannot come to disagree about which plugin opened a file or on what terms.
        /// <para/>
        /// Expects SOURCE, CACHE_DIR, FPS_NUM, FPS_DEN and EXPECT_MS defined above it. FPS_NUM = 0
        /// turns the rate conversion off, which is what a caller doing its own decimation wants:
        /// that one needs every coded frame, padding included, because the padding is what it is
        /// there to identify.
        /// </summary>
        public const string OpenVideoPy = @"def open_video(path):
    # Constructing a source is not opening one, which is the same lesson the plugin probe learned
    # from eedi3m and it bites again here for a different reason: bestsource accepts fpsnum on this
    # file and reports a correct frame count, then fails every single get_frame with 'file has
    # frames with unknown timestamps' - a capture with a damaged PTS among its frames, which is
    # exactly the file this conversion exists for. So each attempt has to render before it counts.
    # One frame, not all of them: it is what separates a plugin that cannot do the job from one
    # that can, and VSPipe's own 'Output N frames' line is what catches a source that dies partway.
    def usable(name, build, kwargs, check_length=False):
        try:
            clip = build(kwargs)
            clip.get_frame(0)
        except Exception as e:
            problems.append('%s: %s' % (name, e))
            return None

        # A frame count is not a duration, and rendering frame 0 does not check one. lsmas takes
        # fpsnum on some damaged files and answers with a clip of 25 frames whose frame 0 renders
        # perfectly - measured, on a 30-minute capture where it *refused* the same argument on a
        # 30 s cut of itself, so neither an exception nor a rendered frame separates the good
        # answer from the absurd one. Only the length does. The band is deliberately wide: it is
        # here to catch a conversion that collapsed, not to adjudicate a few frames either way,
        # and a container whose own duration is somewhat wrong must still pass it.
        if check_length and EXPECT_MS > 0:
            secs = clip.num_frames * clip.fps_den / float(clip.fps_num)
            ratio = secs / (EXPECT_MS / 1000.0)

            if ratio < 0.5 or ratio > 2.0:
                problems.append('%s: gave %d frames = %.1fs, %.4gx the %.1fs the file reports - rejected'
                                % (name, clip.num_frames, secs, ratio, EXPECT_MS / 1000.0))
                return None

        print('Nmkoder: opened through %s - %d frames at %d/%d' % (name, clip.num_frames, clip.fps_num, clip.fps_den), file=sys.stderr)
        return clip

    # Index files are written beside the source by default, which would leave .lwi litter in the
    # user's own folders; every plugin here can be told to put them somewhere else, and each is
    # tried again without that argument in case this build of it cannot.
    def builders(order):
        both = {
            'lsmas': [lambda kw: core.lsmas.LWLibavSource(source=path, cachedir=CACHE_DIR, **kw),
                      lambda kw: core.lsmas.LWLibavSource(source=path, **kw)],
            'ffms2': [lambda kw: core.ffms2.Source(source=path, cachefile=CACHE_DIR + '/ffms2.ffindex', **kw),
                      lambda kw: core.ffms2.Source(source=path, **kw)],
            'bestsource': [lambda kw: core.bs.VideoSource(source=path, cachepath=CACHE_DIR, **kw),
                           lambda kw: core.bs.VideoSource(source=path, **kw)],
        }
        return [(name, build) for name in order for build in both[name]]

    problems = []

    # Rate-converted first, and every plugin gets a turn at it before any of them is opened plain -
    # falling back to a plain open of the preferred plugin would keep that plugin and quietly lose
    # the conversion, which is the whole bug. The order within it is measured on the capture this
    # was written for rather than inherited: lsmas refuses fpsnum outright there ('returned zero or
    # negative frame count'), bestsource takes it and then cannot render a frame, ffms2 converts and
    # renders. The plain list below keeps the app's ordinary preference, no conversion being at stake.
    if FPS_NUM > 0 and FPS_DEN > 0:
        for name, build in builders(['lsmas', 'ffms2', 'bestsource']):
            clip = usable('%s at %d/%d' % (name, FPS_NUM, FPS_DEN), build, {'fpsnum': FPS_NUM, 'fpsden': FPS_DEN}, check_length=True)

            if clip is not None:
                return clip

    # A clip of the wrong length still beats no encode at all, and the line each attempt prints is
    # what says which of the two happened. Reaching here on a file that needed converting means the
    # output will be as long as the frame count rather than as long as the recording, so the reasons
    # go to stderr too - they are the difference between 'no plugin could' and 'every plugin lied'.
    if FPS_NUM > 0 and FPS_DEN > 0:
        print('Nmkoder: no source plugin could rebuild this file at %d/%d, so it is opened as it '
              'stands and the output will be as long as its frame count:' % (FPS_NUM, FPS_DEN), file=sys.stderr)

        for problem in problems:
            print('  ' + problem, file=sys.stderr)

    for name, build in builders(PLAIN_ORDER):
        clip = usable(name + ' at its own rate', build, {})

        if clip is not None:
            return clip

    raise RuntimeError('No VapourSynth source plugin could open the file.\n  ' + '\n  '.join(problems))";

        /// <summary>
        /// Writes the script this encode will be fed through and returns its path. Regenerated per run
        /// rather than kept, because everything in it - the source, the field order, the preset - is
        /// this run's, and in a batch it is a different file every time.
        /// </summary>
        public static string WriteScript(DeinterlacePlan plan, string sourcePath, string scriptPath)
        {
            var sb = new StringBuilder();
            // The plan's own file rather than the loaded one: in muxing mode the video comes from
            // a different file, and the plan is already the thing that names which.
            Fraction rate = plan.File?.VideoStreams?.FirstOrDefault()?.Rate ?? new Fraction(0, 1);
            string cacheDir = Path.Combine(Paths.GetSessionDataPath(), "vsindex");
            Directory.CreateDirectory(cacheDir);

            sb.AppendLine("# Written by Nmkoder for one encode - it is rewritten every run, so edits do not survive.");
            sb.AppendLine("import vapoursynth as vs");
            sb.AppendLine("import havsfunc");
            sb.AppendLine("import sys");
            sb.AppendLine();
            sb.AppendLine("core = vs.core");
            sb.AppendLine($"SOURCE = {PyString(sourcePath)}");
            sb.AppendLine($"CACHE_DIR = {PyString(cacheDir)}");
            sb.AppendLine($"TFF = {(plan.TopFieldFirst ? "True" : "False")}");
            sb.AppendLine($"PRESET = {PyString(plan.QtgmcPreset)}");
            sb.AppendLine($"FPS_DIVISOR = {(plan.DoubleRate ? 1 : 2)}");
            // The rate this file's whole frames arrive at, handed to the source plugin so it rebuilds
            // the clip at that rate rather than at one frame per coded picture.
            //
            // VapourSynth has no variable-rate clip. A source plugin opened plain hands over every
            // coded frame and calls the result constant-rate, so a capture that padded itself with
            // duplicate frames - a TBC covering for timing slips - comes out as long as its frame
            // count instead of as long as its timeline, and the audio, which is muxed from the file
            // and knows nothing of this, stops partway through. Measured on an NTSC capture: 15319
            // coded frames across a 364.7 s recording, which VapourSynth called 511.1 s, so QTGMC's
            // output ran 1.40x long and the audio ended at 71% of the picture. ffmpeg performs this
            // conversion by default when it writes a rate-carrying container, which is exactly why
            // bwdif and yadif were never affected and only the one engine that leaves ffmpeg was.
            //
            // Asked for unconditionally, because on a file that is already constant-rate at this rate
            // it is a no-op - measured, 300 frames in and 300 frames out - so there is no case that
            // wants the plain open in preference and therefore no detection to get wrong.
            sb.AppendLine($"FPS_NUM = {(rate.GetFloat() > 0.01f ? rate.Numerator : 0)}");
            sb.AppendLine($"FPS_DEN = {(rate.GetFloat() > 0.01f ? rate.Denominator : 0)}");
            // What the converted open is checked against - see open_video. Only where the script is
            // reading the plan's own file: DeinterlacePass also feeds this a *cut* of it, whose length
            // is not the length that file reports, and checking one against the other would throw away
            // a perfectly good open. Zero turns the check off rather than failing it.
            bool sourceIsThePlansFile = plan.File != null && sourcePath == plan.File.ImportPath;
            sb.AppendLine($"EXPECT_MS = {(sourceIsThePlansFile ? plan.File.DurationMs : 0)}");
            // The order the plain opens are tried in. Unchanged for this script; the cadence repair
            // asks for a different one, for the reason CadenceRepair gives.
            sb.AppendLine("PLAIN_ORDER = ['lsmas', 'bestsource', 'ffms2']");
            sb.AppendLine();
            sb.AppendLine(OpenVideoPy);
            sb.AppendLine();
            sb.AppendLine(@"clip = open_video(SOURCE)
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
        /// <summary>
        /// The filter that puts back the pixel aspect a VapourSynth pipe drops, or "" where the
        /// source's pixels are square and there is nothing to put back.
        /// <para/>
        /// <b>VSPipe's y4m header states no aspect at all.</b> Measured, it reads
        /// <c>YUV4MPEG2 C420 W720 H480 F30000:1001 Ip A0:0</c> - where ffmpeg's own y4m muxer writes the
        /// real value, <c>A8:9</c> on a 720x480 NTSC capture. VapourSynth has no SAR on a clip to write,
        /// so <c>A0:0</c> is honest rather than a bug in VSPipe; it is only a loss because everything
        /// downstream then reads the frame as square. Measured through a real VSPipe producer, which is
        /// the only thing that shows it: an 8:9 fixture piped into libx264 comes back <c>N/A</c>, where
        /// the same ffmpeg reading the same file directly gives 8:9 / 4:3. So a 4:3 tape came out
        /// playing as 3:2, stretched, from every engine that leaves ffmpeg.
        /// <para/>
        /// It belongs at the <b>head</b> of the chain, and that is what makes one filter the whole
        /// repair: ffmpeg's scale filter adjusts the SAR to hold the display aspect, and crop and pad
        /// carry it through untouched, so restoring it at the top leaves every filter below behaving
        /// exactly as it does on the un-piped path - including the app's own scales, which end in
        /// <c>setsar=1:1</c> and are therefore unaffected by what went in above them.
        /// <para/>
        /// <b>Read off the source file, never off the pipe</b> - the pipe is precisely where it no
        /// longer is. That is the same reason the deinterlace script takes its frame rate from
        /// <see cref="Data.Streams.VideoStream.Rate"/> and the cadence repair its colour from ffprobe.
        /// <para/>
        /// A filter of its own rather than another property on the <c>setparams</c> call
        /// <see cref="CadenceRepair"/> already emits, because that filter cannot express it: it takes
        /// field_mode, range, color_primaries, color_trc and colorspace, and no aspect of any kind.
        /// </summary>
        public static string GetPipeSarFilter(Data.Streams.VideoStream vs)
        {
            return vs != null && AspectRatio.IsAnamorphic(vs.Sar) ? $"setsar={vs.Sar.Width}/{vs.Sar.Height}" : "";
        }

        /// <summary>
        /// The colour a y4m pipe drops, probed off <paramref name="sourcePath"/> and written as
        /// <c>setparams</c> properties - empty where the file states none, which is not a failure.
        /// <para/>
        /// <b>The frame's own properties beat the output AVOptions, which is why this cannot be done with
        /// flags.</b> Measured through a real VSPipe producer on a capture declaring bt470m/bt470m/bt470bg
        /// tv: <c>-color_primaries bt470m -color_trc bt470m -colorspace bt470bg -color_range tv</c> comes
        /// back <b>unknown, unknown, bt470bg, tv</b> - two of four honoured and two dropped in silence -
        /// and the same four written numerically (4/4/5/1) give exactly the same thing. Written as
        /// setparams properties all four survive. An earlier fix here changed only the spelling and
        /// shipped believing that had settled it; the spelling was never what decided it. The same
        /// command reading the source as a <i>file</i> tags all four correctly, so this is confined to
        /// piped y4m and a test that does not use the real pipe is worthless.
        /// <para/>
        /// Probed rather than taken off <see cref="Data.MediaFile.ColorData"/>, which is populated lazily
        /// and may not have been asked for yet; and off a path rather than a <see cref="Data.MediaFile"/>
        /// because the input is not always that file - <see cref="DeinterlacePass"/> is handed cuts. The
        /// names ffprobe prints and the values setparams accepts come out of the same libavutil tables,
        /// so the round trip is lossless by construction.
        /// <para/>
        /// <b>Asked for as <c>key=value</c> in one call rather than as four bare values, and that is a fix
        /// for a silent failure rather than a tidy-up.</b> ffprobe's diagnostics share the stream that
        /// carries its answer, so with <c>nokey=1</c> nothing distinguishes the value from a complaint -
        /// and taking the first non-empty line took the complaint. Measured on an NTSC capture cut
        /// mid-audio-frame, which makes ffprobe print <c>[mp2 @ …] Header missing</c> first: that line
        /// failed the character test below, so *every* colour property was dropped and the run wrote a
        /// file tagged <c>unknown</c> while reporting success. A <c>key=</c> prefix cannot be confused
        /// with a diagnostic, which makes this robust whatever the log level - the sibling call in
        /// <see cref="Av1anSceneDetect"/> survives only because it is set to <c>quiet</c>, and a
        /// correctness that rests on a log level is one line away from being lost.
        /// <para/>
        /// Colour only, deliberately: the field order is the caller's business. A cadence repair hands on
        /// woven fields and has to say so, where a deinterlace pass emits progressive frames and must
        /// not - so <c>field_mode</c> is added by whoever wants it rather than assumed here.
        /// </summary>
        public static async Task<List<string>> GetPipeColorParamsAsync(string sourcePath)
        {
            var wanted = new[] { ("color_primaries", "color_primaries"), ("color_transfer", "color_trc"),
                                 ("color_space", "colorspace"), ("color_range", "range") };
            var setParams = new List<string>();

            if (sourcePath.IsEmpty())
                return setParams;

            var probe = new AvProcess.FfprobeSettings
            {
                Args = $"-select_streams v:0 -show_entries stream={string.Join(",", wanted.Select(x => x.Item1))} " +
                    $"-of default=noprint_wrappers=1 {sourcePath.Wrap()}",
                LogLevel = "error",
            };

            string[] probed = (await AvProcess.RunFfprobe(probe)).SplitIntoLines();

            foreach (var pair in wanted)
            {
                // Last rather than first: one stream is asked for, so one line per key is expected, and
                // preferring the last costs nothing if that holds and picks the real answer over a
                // diagnostic that happened to be shaped like one if it ever does not.
                string value = probed.Select(x => x.Trim())
                    .Where(x => x.StartsWith($"{pair.Item1}=", StringComparison.Ordinal))
                    .Select(x => x.Substring(pair.Item1.Length + 1).Trim())
                    .LastOrDefault() ?? "";

                // "unknown" and "N/A" are ffprobe saying the source states nothing, and every setparams
                // property defaults to `auto` - keep whatever came in - so leaving it off is exactly
                // right: asserting `unknown` would state ignorance as though it were a measurement. The
                // character test guards a value being spliced into a filter graph, where a `:` or an `=`
                // would change the graph's shape rather than fail.
                if (value.IsNotEmpty() && value != "unknown" && value != "N/A" && value.All(c => char.IsLetterOrDigit(c) || c == '-'))
                    setParams.Add($"{pair.Item2}={value}");
            }

            return setParams;
        }

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

        #region Measuring the output

        /// <summary>
        /// How long VSPipe is allowed to take to say how long its output is. Generous, because what
        /// it spends the time on is the source plugin indexing the file - minutes for an hour of
        /// MPEG-2 off a slow disk - and a timeout firing in the middle of that throws the work away
        /// rather than saving any, leaving the encode to index the same file over again. Stop kills
        /// it long before this, since it runs as a Secondary process.
        /// </summary>
        private const int InfoTimeoutMs = 15 * 60 * 1000;

        /// <summary>
        /// Measures what this script will produce and points the progress bar at that, rather than at
        /// what the file says about itself.
        /// <para/>
        /// The container's duration is the wrong ruler for the files this feature exists for. An
        /// analogue capture is timestamped by whatever captured it and ffprobe reads the duration
        /// straight back out of those timestamps, so a capture whose clock stalled or restarted
        /// leaves a file reporting an hour and holding rather more - and everything scaled against
        /// that is wrong along with it. The bar reaches 100% with a third of the encode still to run
        /// and then stays there, which is what this is here to stop.
        /// <para/>
        /// VapourSynth has no such problem. Its source plugin has indexed the file by the time
        /// <c>--info</c> answers, so the frame count it reports is a count rather than an estimate,
        /// and it is exactly the set of frames that will come down the pipe. The indexing is not an
        /// added cost either: the encode's own VSPipe would do it moments later, and every source
        /// plugin in <see cref="WriteScript"/> is told to cache its index - so this moves that step
        /// in front of the encode instead of adding one, and gives the app something to say while it
        /// happens, which it previously spent staring at an empty bar.
        /// <para/>
        /// Never throws and never fails a run: an answer that does not arrive leaves the bar
        /// measuring against the source's own duration, which is where it was already.
        /// </summary>
        public static async Task SetProgressTargetAsync(string scriptPath, MediaFile source)
        {
            try
            {
                // Named where there is a file to name it after. There is not when what is being read
                // is a cut copy of one, whose length is not the length that file reports - which is
                // also why the comparison below is skipped in that case rather than made against it.
                string named = source == null ? "the video" : $"'{source.Name.Trunc(40)}'";
                Logger.Log($"Reading {named} through VapourSynth to measure it. This indexes the " +
                    $"source, which the encode then reuses.");
                RunTask.ReportProgress("Indexing the source for VapourSynth...");

                long durationMs = await GetOutputDurationMsAsync(scriptPath);

                if (durationMs < 1)
                {
                    Logger.Log("VSPipe did not say how long its output will be, so progress is measured against the " +
                        "duration the file reports.", true);
                    return;
                }

                FfmpegOutputHandler.overrideTargetDurationMs = durationMs;
                long claimedMs = source == null ? 0 : source.DurationMs;

                // Worth putting in front of the user rather than only in the debug log, because it is
                // a fact about their file and not about this app: the output will not be the length
                // the source claims, and nothing else would ever explain why.
                //
                // It used to say outright that the container's duration was wrong, which is one of the
                // two ways this happens and was written from the one that had been seen - a capture
                // card's timestamps under-reporting a 3.3 GB program stream. The other is a capture
                // padded with duplicate frames, where the container is right and the frame count is
                // the inflated side: measured, 76080 coded frames across a 30:12 recording, which
                // VapourSynth reads as 42:18. open_video converts that away where a plugin can, so
                // reaching here means it could not, and which of the two it is cannot be told from
                // these two numbers alone - the audio is what separates them. So it names the
                // disagreement and stops there rather than blaming either side.
                if (claimedMs > 0 && Math.Abs(durationMs - claimedMs) > FfmpegOutputHandler.TargetToleranceMs(claimedMs))
                {
                    Logger.Log($"Note: '{source.Name.Trunc(40)}' reports a duration of {FormatUtils.Time(claimedMs)}, but " +
                        $"VapourSynth reads {FormatUtils.Time(durationMs)} of video in it. What this writes will be " +
                        $"{FormatUtils.Time(durationMs)} long and progress is measured against that; if the audio ends " +
                        $"early, the file's frame count is the wrong half and its timestamps need repairing first.");
                }
                else
                {
                    Logger.Log($"VapourSynth will produce {FormatUtils.Time(durationMs)} of video.", true);
                }
            }
            catch (Exception e)
            {
                // Measuring the encode is not the encode. Anything that goes wrong here costs a
                // progress bar and must not cost the run.
                Logger.Log($"Could not measure what the QTGMC script will produce: {e.Message}", true);
            }
        }

        /// <summary> How long the video this script produces will be, in milliseconds, or -1 where
        /// VSPipe would not say - it not being installed, the script not evaluating, or an output
        /// whose length or frame rate is not fixed, which a QTGMC graph's never is. </summary>
        public static async Task<long> GetOutputDurationMsAsync(string scriptPath)
        {
            string vspipe = GetVspipePath();

            if (vspipe.IsEmpty())
                return -1;

            // "-" is the output file. --info does not write frames to it and current VSPipe does not
            // ask for one at all, but older ones refuse to run without it, and "-" is stdout - which
            // is where the report goes anyway.
            var result = await RunVspipe(vspipe, $"--info {scriptPath.Wrap()} -", InfoTimeoutMs,
                NmkoderProcess.ProcessType.Secondary);

            if (result == null)
            {
                Logger.Log("vspipe --info did not answer in time.", true);
                return -1;
            }

            if (result.Value.exitCode != 0)
            {
                Logger.Log($"vspipe --info failed (exit {result.Value.exitCode}): {result.Value.output.Trim().Trunc(600)}", true);
                return -1;
            }

            return ParseInfoDurationMs(result.Value.output);
        }

        /// <summary>
        /// The duration in a <c>vspipe --info</c> report, which gives it as a frame count over a
        /// frame rate - "Frames: 215584" and "FPS: 60000/1001 (59.940 fps)" - and says "Variable"
        /// for either where the clip does not have one.
        /// <para/>
        /// Read as the exact fraction rather than as the decimal in the brackets: 59.940 is not
        /// 60000/1001, and over a couple of hundred thousand frames that rounding is seconds.
        /// </summary>
        private static long ParseInfoDurationMs(string info)
        {
            string[] lines = info.SplitIntoLines().Select(x => x.Trim()).ToArray();
            long frames = ReadField(lines, "Frames:").GetLong();
            string[] rate = ReadField(lines, "FPS:").Split(' ').First().Split('/');

            if (frames < 1 || rate.Length != 2 || rate[0].GetLong() < 1 || rate[1].GetLong() < 1)
            {
                Logger.Log($"vspipe --info said nothing usable about the output's length:\n{info.Trim().Trunc(600)}", true);
                return -1;
            }

            return frames * 1000L * rate[1].GetLong() / rate[0].GetLong();
        }

        /// <summary> What one line of a VSPipe report says after its label, or "" when there is no
        /// such line. </summary>
        private static string ReadField(string[] lines, string label)
        {
            string line = lines.FirstOrDefault(x => x.StartsWith(label, StringComparison.Ordinal)) ?? "";
            return line.Substring(Math.Min(line.Length, label.Length)).Trim();
        }

        #endregion

        /// <summary> Runs VSPipe directly - no shell, so nothing expands what is in a file name - and
        /// hands back its exit code and combined output, or null if it had to be killed.
        /// <para/>
        /// The process type decides what Stop reaches: the probe is Background because it runs off
        /// the UI while a file is being described and nothing is waiting on it, while anything a task
        /// waits for has to be Secondary or pressing Stop leaves the task blocked on it. </summary>
        public static async Task<(int exitCode, string output)?> RunVspipe(string exePath, string args, int timeoutMs,
            NmkoderProcess.ProcessType procType = NmkoderProcess.ProcessType.Background)
        {
            Process proc = OsUtils.NewProcess(true, procType, exePath);
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
