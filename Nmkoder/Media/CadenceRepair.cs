using Nmkoder.Data;
using Nmkoder.Data.Streams;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nmkoder.Media
{
    /// <summary>
    /// Rebuilds a capture that padded itself with duplicate frames, writing a constant-rate copy
    /// whose frame count matches its own recording.
    /// <para/>
    /// The file this exists for states one rate and carries another: measured on an NTSC MPEG-PS,
    /// 76080 coded frames across a 30:12 recording, which is 41.98 fps of 29.97 fps content - a TBC
    /// covering for timing slips by repeating frames, and stamping them with timestamps that are
    /// non-monotonic 3332 times in six minutes and carry one <c>N/A</c> among them.
    /// <para/>
    /// Both rate conversions this app already has are driven by those timestamps - ffmpeg's
    /// <c>-fps_mode cfr</c> for the ffmpeg deinterlacers, and the source plugin's <c>fpsnum</c> for
    /// QTGMC - so both inherit the damage. Measured on 15319 coded frames decimated to 10936: the
    /// timestamp-driven result leaves 560 adjacent-identical pairs, 5.12% of its own output, and
    /// since the total count is right each one means a real frame was discarded to make room. That
    /// is 12.8% of the padding identified wrongly, about 1.5 visible hitches per second.
    /// <para/>
    /// This ignores the timestamps entirely - they are the damaged part - and decides by content.
    /// </summary>
    class CadenceRepair
    {
        /// <summary> How far the frame count may exceed the recording before this is worth running.
        /// A file already at its own rate has nothing to repair, and the run is refused rather than
        /// spent writing a re-encoded copy of it. </summary>
        public const double PaddingThreshold = 1.02;

        /// <summary>
        /// The frames a correctly-timed copy would hold: the recording's length at the rate whole
        /// frames arrive at.
        /// <para/>
        /// The duration is the container's, which for this class of file is the half that is
        /// <i>right</i> - measured, its audio agrees with it to a quarter of a second where the frame
        /// count is 40% out. That is the opposite of the case the progress-bar note in the
        /// deinterlacing record describes, where the container under-reported and the frame count was
        /// sound, and the two cannot be told apart from these numbers alone - which is why this is a
        /// utility somebody chooses rather than something applied on the way past.
        /// </summary>
        public static int TargetFrames(MediaFile file)
        {
            VideoStream vs = file?.VideoStreams?.FirstOrDefault();

            if (vs == null || file.DurationMs < 1 || vs.Rate.GetFloat() < 0.01f)
                return 0;

            return (int)Math.Round(file.DurationMs / 1000.0 * vs.Rate.GetFloat());
        }

        /// <summary>
        /// Writes the script that does the decimation. The metrics pass runs at script-evaluation
        /// time - a full decode before a single frame is handed on - which is the reason this is a
        /// utility that writes a file rather than something an encode tab does on its way through.
        /// </summary>
        public static string WriteScript(MediaFile file, string sourcePath, string scriptPath)
        {
            var sb = new StringBuilder();
            VideoStream vs = file.VideoStreams.First();
            string cacheDir = Path.Combine(Paths.GetSessionDataPath(), "vsindex");
            Directory.CreateDirectory(cacheDir);

            sb.AppendLine("# Written by Nmkoder for one repair - it is rewritten every run, so edits do not survive.");
            sb.AppendLine("import vapoursynth as vs");
            sb.AppendLine("import sys");
            sb.AppendLine();
            sb.AppendLine("core = vs.core");
            sb.AppendLine($"SOURCE = {PyString(sourcePath)}");
            sb.AppendLine($"CACHE_DIR = {PyString(cacheDir)}");
            sb.AppendLine($"TARGET = {TargetFrames(file)}");
            // The output rate is deliberately *not* called FPS_NUM/FPS_DEN. Those two names belong to
            // open_video, where they mean "rebuild the clip at this rate" - so naming the output rate
            // that way hands the padded file to the very timestamp-driven conversion this exists to
            // replace, and it comes back already decimated with nothing left to do. Caught by running
            // it: the script opened 900 frames of a 1259-frame file and reported nothing to decimate.
            sb.AppendLine($"OUT_FPS_NUM = {vs.Rate.Numerator}");
            sb.AppendLine($"OUT_FPS_DEN = {vs.Rate.Denominator}");
            // Zero turns off both the rate-converted opens and the length check inside open_video.
            // The whole point here is to open the file exactly as it stands, every padded frame
            // included, and then decide what to drop by looking at the pictures.
            sb.AppendLine("FPS_NUM = 0");
            sb.AppendLine("FPS_DEN = 0");
            sb.AppendLine("EXPECT_MS = 0");
            sb.AppendLine();
            sb.AppendLine(Qtgmc.OpenVideoPy);
            sb.AppendLine();
            sb.AppendLine(ScriptBody);

            Directory.CreateDirectory(Path.GetDirectoryName(scriptPath));
            File.WriteAllText(scriptPath, sb.ToString());
            Logger.Log($"Wrote cadence repair script to '{scriptPath}':\n{sb}", true);
            return scriptPath;
        }

        /// <summary> Kept as its own constant so the Python is readable as Python, and so the two
        /// numbers it is built on - the cycle and the carry - sit beside the reasoning for them. </summary>
        private const string ScriptBody = @"clip = open_video(SOURCE)
n = clip.num_frames
ratio = TARGET / float(n)
print('Nmkoder: %d coded frames, %d wanted - keeping %.5f of them' % (n, TARGET, ratio), file=sys.stderr)

if TARGET < 1 or TARGET >= n:
    print('Nmkoder: nothing to decimate', file=sys.stderr)
    clip = core.std.AssumeFPS(clip, fpsnum=OUT_FPS_NUM, fpsden=OUT_FPS_DEN)
    clip.set_output()
else:
    # How different each frame is from the one before it. This is the whole signal: the padding is
    # repeated pictures, and the timestamps that would say where it is are the damaged part of the
    # file. Nothing here needs an absolute threshold, only the ordering inside a window - which
    # matters, because there is no gap in the distribution to put a threshold in: measured, the
    # frame at the cut scores 0.002966 against 0.002752 for the one below it, a ratio of 1.08.
    stats = core.std.PlaneStats(clip, clip[0] + clip[:-1])
    diff = [f.props['PlaneStatsDiff'] for f in stats.frames()]
    diff[0] = 1e9                     # frame 0 has no predecessor and is never a duplicate

    # The cycle is the file's own cadence rather than a constant. Measured on the capture this was
    # written for, the keep-ratio is 0.71388, whose best small-denominator approximation is 5/7 - so
    # a 7-frame cycle dropping 2 lands on the padding's own rhythm. Small denominators win near-ties
    # because the cycle is what bounds the timing drift: measured, cycle 7 holds it to 1.9 frames
    # (64 ms) where cycle 60 reaches 4.5 (149 ms).
    def pick_cycle(r, maxden=30):
        best, err = 7, 1e9

        for den in range(2, maxden + 1):
            num = int(round(r * den))

            if num <= 0 or num >= den:
                continue

            e = abs(r - num / float(den))

            if e < err - 1e-12:
                err, best = e, den

        return best

    CYCLE = pick_cycle(ratio)
    print('Nmkoder: decimating in cycles of %d' % CYCLE, file=sys.stderr)

    # Drop the lowest-difference frames inside each cycle, carrying the rounding forward so the
    # total is exact and the local rate cannot wander.
    #
    # Deciding per cycle rather than over the whole file is load-bearing and not tidiness. Dropping
    # the globally-most-duplicate frames is what minimises the total difference thrown away, and it
    # strips a static stretch wholesale - so the picture runs ahead for the rest of the file while
    # the length still comes out right. Measured on 15319 frames: global greedy drifts 29 frames out
    # of step, 968 ms, against 64 ms for this. Both leave zero duplicate pairs behind, so the drift
    # is the only thing that tells them apart - do not 'simplify' this into a global sort.
    drop, kept_exact, kept_actual = [], 0.0, 0

    for s in range(0, n, CYCLE):
        e = min(s + CYCLE, n)
        kept_exact += (e - s) * ratio
        keep_here = int(round(kept_exact)) - kept_actual
        dn = (e - s) - keep_here

        if dn > 0:
            drop.extend(sorted(range(s, e), key=lambda i: diff[i])[:dn])

        kept_actual += max(keep_here, 0)

    out = core.std.DeleteFrames(clip, sorted(drop))
    print('Nmkoder: dropped %d, left %d frames at %d/%d' % (len(drop), out.num_frames, OUT_FPS_NUM, OUT_FPS_DEN), file=sys.stderr)
    out = core.std.AssumeFPS(out, fpsnum=OUT_FPS_NUM, fpsden=OUT_FPS_DEN)
    out.set_output()";

        /// <summary>
        /// How many pictures the file actually carries, asked of VapourSynth rather than of ffprobe.
        /// <para/>
        /// Every route to this number that reads the container is answering from the timestamps, which
        /// on the file this exists for are the broken half - and ffprobe's <c>nb_frames</c> is simply
        /// <c>N/A</c> on an MPEG program stream. A source plugin has indexed the file by the time
        /// <c>--info</c> answers, so its count is a count. Returns 0 when it cannot be had, which the
        /// caller reads as "cannot tell" rather than as "not padded".
        /// </summary>
        public static async Task<int> ProbeCodedFramesAsync(MediaFile file)
        {
            VideoStream vs = file.VideoStreams.First();
            var sb = new StringBuilder();
            string cacheDir = Path.Combine(Paths.GetSessionDataPath(), "vsindex");
            Directory.CreateDirectory(cacheDir);

            sb.AppendLine("import vapoursynth as vs");
            sb.AppendLine("import sys");
            sb.AppendLine("core = vs.core");
            sb.AppendLine($"SOURCE = {PyString(file.ImportPath)}");
            sb.AppendLine($"CACHE_DIR = {PyString(cacheDir)}");
            sb.AppendLine("FPS_NUM = 0");     // plain opens only - see WriteScript
            sb.AppendLine("FPS_DEN = 0");
            sb.AppendLine("EXPECT_MS = 0");
            sb.AppendLine();
            sb.AppendLine(Qtgmc.OpenVideoPy);
            sb.AppendLine();
            sb.AppendLine($"clip = core.std.AssumeFPS(open_video(SOURCE), fpsnum={vs.Rate.Numerator}, fpsden={vs.Rate.Denominator})");
            sb.AppendLine("clip.set_output()");

            string path = Path.Combine(Paths.GetSessionDataPath(), "cadence-probe.vpy");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, sb.ToString());

            long ms = await Qtgmc.GetOutputDurationMsAsync(path);
            return ms < 1 ? 0 : (int)Math.Round(ms / 1000.0 * vs.Rate.GetFloat());
        }

        /// <summary>
        /// Runs the repair over <paramref name="file"/> into <paramref name="outPath"/>, returning why
        /// nothing usable came out - or "" when it did, a run the user stopped included.
        /// <para/>
        /// The same shape as <see cref="DeinterlacePass"/>: VapourSynth writes y4m into ffmpeg, the
        /// pipe is the last input so the source keeps index 0 and every other track is still mapped
        /// out of it, and a script that dies partway is invisible to ffmpeg - which is what
        /// <see cref="Qtgmc.ReadRunProblem"/> is read for afterwards.
        /// </summary>
        public static async Task<string> RunAsync(MediaFile file, string outPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));

            string vsLogPath = Path.Combine(Paths.GetSessionDataPath(), "cadence.log");
            IoUtils.TryDeleteIfExists(vsLogPath);
            string script = WriteScript(file, file.ImportPath, Path.Combine(Paths.GetSessionDataPath(), "cadence.vpy"));

            await Qtgmc.SetProgressTargetAsync(script, file);

            if (RunTask.canceled)
                return "";

            // The output is still interlaced - this repairs the *cadence* and nothing else, so the
            // fields are handed on woven exactly as they arrived and the file is still something to
            // deinterlace afterwards. y4m carries no field order, so x264 has to be told: measured,
            // an interlaced encode written this way comes back as field_order=tb, which
            // InterlaceDetect.ParseFfprobeFieldOrder reads as top-field-first, the same as tt.
            // **y4m has no field order and VapourSynth declares the opposite of the truth**, which is
            // what makes this two arguments rather than one. VSPipe's header reads
            // `YUV4MPEG2 C420 W720 H480 F30000:1001 Ip` - `Ip` is *progressive* - so ffmpeg marks
            // every frame progressive on the way in and the encoder is told nothing by x264's own
            // flag alone. Measured against a tt source through a real VSPipe producer:
            //
            //   -x264-params tff=1                       -> field_order=progressive
            //   -flags +ilme+ildct -x264-params tff=1    -> field_order=bt   (the wrong parity)
            //   -vf setfield=tff -x264-params tff=1      -> field_order=tb   (right)
            //
            // So `setfield` re-asserts what VapourSynth threw away and x264's tff/bff turns
            // interlaced encoding on. The middle row is the one to remember: it looks like it worked
            // and leaves the next deinterlace running at the wrong parity on a file this utility had
            // just repaired. The same trap waits for anything else piping interlaced VapourSynth
            // frames into ffmpeg.
            bool bff = file.VideoStreams.First().FieldOrder == FieldOrder.BottomFieldFirst;
            string interlace = $"-vf setfield={(bff ? "bff" : "tff")} -x264-params {(bff ? "bff=1" : "tff=1")}";

            string args = $"-i {file.ImportPath.Wrap()} -f yuv4mpegpipe -thread_queue_size 1024 -i - " +
                $"-map 1:v:0 -map 0:a? -map 0:s? -map 0:t? " +
                $"-c:v libx264 -crf {Crf} -preset {Preset} {interlace} " +
                $"-c:a copy {DeinterlacePass.GetSubtitleArgs(file)} -dn " +
                $"-map_metadata 0 -map_chapters 0 {outPath.Wrap()}";

            var settings = new AvProcess.FfmpegSettings
            {
                Args = args,
                LoggingMode = AvProcess.LogMode.OnlyLastLine,
                ProgressBar = true,
                ReportFailure = false,
                PipeFrom = Qtgmc.BuildVspipeCommand(script, vsLogPath),
                ExtraPathDirs = Qtgmc.GetPathDirs(),
            };

            await AvProcess.RunFfmpeg(settings);
            FfmpegOutputHandler.overrideTargetDurationMs = -1;

            if (RunTask.canceled || RunTask.failed)
                return "";

            string vsProblem = Qtgmc.ReadRunProblem(vsLogPath);

            if (vsProblem.IsNotEmpty())
                return $"The repaired video was cut short.\n\n{vsProblem}";

            if (settings.Problem.IsNotEmpty())
                return settings.Problem;

            if (!RunTask.OutputExists(outPath))
                return $"FFmpeg reported no error, but '{Path.GetFileName(outPath)}' was not written.";

            return "";
        }

        /// <summary> Near-lossless for the reason <see cref="DeinterlacePass"/> gives: this output is
        /// a deliverable that something else will deinterlace and encode, so it wants to be cheap to
        /// store and indistinguishable from the source, not bit-exact. </summary>
        private const int Crf = 12;
        private const string Preset = "medium";

        private static string PyString(string value)
        {
            return "r'" + (value ?? "").Replace("'", "\\'") + "'";
        }
    }
}
