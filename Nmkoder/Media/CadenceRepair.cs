using Nmkoder.Data;
using Nmkoder.Data.Streams;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.Utils;
using System;
using System.Collections.Generic;
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
    /// **It uses the timestamps to decide where each frame goes and the content to decide which frame
    /// goes there.** Neither alone is enough, and this file used to say the opposite - that the
    /// timestamps were "the damaged part" and were ignored entirely. They are damaged *individually*,
    /// jittering between 8 and 79 ms where 33 is due and running backwards thousands of times, and
    /// their trend is still the only record of when each picture belongs. Choosing by index instead -
    /// a constant keep-ratio, however carefully carried - assumes the coded frames are spread evenly
    /// through the recording, and they are not: measured on a 95-minute capture, the picture ran up to
    /// <b>6.99 s ahead of its own audio</b> in the middle while both ends lined up. Placing every frame
    /// against its own timestamp instead holds the error to <b>0.054 s</b> over the same file, bounded
    /// by the source's jitter rather than accumulating.
    /// <para/>
    /// <b>The index selection leaves fewer duplicate frames than this does, and that is not a reason to
    /// go back to it.</b> Measured over the same source frames on a 30 s cut, it left 0 adjacent pairs
    /// within 1e-4 and 14 within 1e-3 where this leaves 16 and 83 - it optimises for exactly that
    /// quantity, and pays in the one that ruins a file. Nine percent of frames near their predecessor
    /// is a mild softness; seconds of audio drift is unwatchable. Against a pure nearest-in-time pick,
    /// which is what ffmpeg's own conversion does, the content tie-break wins at every threshold
    /// (16 against 21, 83 against 98) - that is the comparison this selection is entitled to claim.
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
        public static string WriteScript(MediaFile file, string sourcePath, string scriptPath, string timesPath)
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
            // bestsource first here, where the deinterlace script asks for lsmas. All three decode
            // this file identically frame for frame - measured, 0 of 1259 differ between any pair -
            // and all three are exact for the requests DeleteFrames actually makes, which are forward
            // with gaps. They part company the moment a request goes *backwards*: measured on the
            // same file, lsmas raises 'failed to output a video frame', ffms2 answers 971 of 1259
            // requests with the wrong picture and no complaint at all, and bestsource gets every one
            // right. So the correctness of this script rested on nothing but DeleteFrames happening
            // to ask in order - an invariant nobody wrote down and any later filter could break, with
            // the ffms2 failure being the silent kind. Naming the exact plugin costs an index and
            // removes the trap.
            sb.AppendLine("PLAIN_ORDER = ['bestsource', 'lsmas', 'ffms2']");
            sb.AppendLine($"TIMES_FILE = {PyString(timesPath)}");
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
        /// things it balances - the moment a frame belongs at, and which of the frames near that moment
        /// is not a repeat - sit beside the reasoning for them. </summary>
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

    # **Where each output frame goes is decided by time, not by counting.** Choosing by index - a
    # constant keep-ratio, however carefully carried - assumes the coded frames are spread evenly
    # through the recording, and they are not: measured on a 95-minute capture, 41.25 fps on average
    # but varying locally, so index-resampling showed the frame belonging at 933.91 s at 929.85 s and
    # was 6.99 s out at its worst. The picture ran ahead of its own audio by seconds in the middle
    # while both ends lined up, which is why comparing total durations reported success.
    #
    # So: the timestamps say *when* (they are jittery but their trend is the only record of it), and
    # the content says *which* of the frames sitting near that moment to take (so a duplicate is not
    # chosen when a real frame is available). Neither alone is enough - that was the whole mistake.
    times = []

    with open(TIMES_FILE, 'r') as fh:
        for line in fh:
            line = line.strip()

            if line:
                times.append(float(line))

    # ffprobe and the source plugin can disagree by a frame or two about how many pictures a damaged
    # file holds - measured, ffprobe counted 240108 where bestsource and ffmpeg's own decode both said
    # 240107. Insisting on equality threw away a six-minute pass over a one-frame difference at the
    # tail, which cannot move anything: the array is only ever read by binary search on time, so a
    # spare entry past the end is never reached and a missing one costs the last frame its placement.
    # A large disagreement is different - that would mean these timestamps describe another file - so
    # it still refuses past a handful.
    if abs(len(times) - n) > 10:
        raise RuntimeError('timestamps (%d) do not line up with frames (%d)' % (len(times), n))

    if len(times) != n:
        print('Nmkoder: %d timestamps for %d frames - trimming to match' % (len(times), n), file=sys.stderr)

    times = times[:n]
    guess = (times[-1] - times[0]) / max(len(times) - 1, 1)

    while len(times) < n:
        times.append(times[-1] + guess)

    import bisect
    t0 = times[0]
    step = float(OUT_FPS_DEN) / OUT_FPS_NUM
    keep = []
    prev = -1
    worst = 0.0

    for k in range(TARGET):
        want = t0 + k * step
        j = bisect.bisect_left(times, want)

        # Among the frames sitting at that moment, prefer the one that is not a repeat of what came
        # before it. **Half an output step, not two** - the window is a tie-break between frames at
        # the same instant, and any wider it stops being one: allowing two steps let a
        # higher-difference frame be fetched from 67 ms away and pushed the worst placement error to
        # 92 ms, which is inside the range where lip-sync is noticeable. Timing wins, content breaks
        # ties, in that order.
        best, best_diff = None, -1.0
        lo = max(prev + 1, j - 2)
        hi = min(n, j + 3)

        for c in range(lo, hi):
            if abs(times[c] - want) > 0.5 * step:
                continue

            if diff[c] > best_diff:
                best, best_diff = c, diff[c]

        if best is None:
            # Nothing at that instant - the source's own gaps run to 79 ms where a step is 33 - so
            # take whichever frame is nearest it and let the error be the file's rather than ours.
            for c in range(lo, hi):
                if best is None or abs(times[c] - want) < abs(times[best] - want):
                    best = c

        if best is None:                              # nothing in range: take the next frame along
            best = min(max(prev + 1, j), n - 1)

            # The last output slot can want a moment past the last frame there is, and by then every
            # earlier frame has been used. Stopping is right: what is lost is the tail fraction of a
            # second, where advancing off the end of the array is an exception mid-run.
            if best <= prev:
                print('Nmkoder: ran out of source frames at output %d of %d' % (k, TARGET), file=sys.stderr)
                break

        keep.append(best)
        prev = best
        err = abs(times[best] - want)

        if err > worst:
            worst = err

    # The check the first version did not have, and the reason it shipped: this is the timing error
    # over the *whole* file rather than at its ends. One frame is 33 ms; seconds here means drift.
    print('Nmkoder: worst placement error %.3f s (%.2f frames) across %d output frames'
          % (worst, worst * OUT_FPS_NUM / float(OUT_FPS_DEN), len(keep)), file=sys.stderr)

    kept = set(keep)
    drop = [i for i in range(n) if i not in kept]
    out = core.std.DeleteFrames(clip, drop)
    print('Nmkoder: dropped %d, left %d frames at %d/%d' % (len(drop), out.num_frames, OUT_FPS_NUM, OUT_FPS_DEN), file=sys.stderr)
    out = core.std.AssumeFPS(out, fpsnum=OUT_FPS_NUM, fpsden=OUT_FPS_DEN)
    out.set_output()";

        /// <summary>
        /// Writes one presentation timestamp per decoded frame, in the clip's own frame order, and
        /// returns the path - or "" when the file yields none.
        /// <para/>
        /// This is the half the first version of this utility threw away, and throwing it away is what
        /// made it produce a file whose picture ran up to <b>7 seconds</b> ahead of its own audio. The
        /// reasoning was that a padded capture's timestamps are damaged, which is true of them
        /// individually - non-monotonic, jittering between 8 ms and 79 ms where 33 ms is due - and does
        /// not make them worthless, because their *trend* is the only record of when each picture
        /// belongs. Measured on a 95-minute capture: the coded frames are not uniform in time (41.25
        /// fps on average, varying locally), so choosing output frames by index instead put the frame
        /// belonging at 933.91 s at 929.85 s, and the worst case 6.99 s out at 37 minutes in. The
        /// error returns to nearly zero at both ends, which is exactly why comparing total durations
        /// passed it.
        /// <para/>
        /// <c>best_effort_timestamp_time</c> rather than packet PTS because it is emitted once per
        /// *decoded* frame, in presentation order, with ffmpeg's own interpolation where a packet
        /// carries none - so it lines up index-for-index with the clip VapourSynth opens. Measured, the
        /// counts match exactly; raw packet PTS do not, this capture having 3326 frames with no PTS at
        /// all out of 240107.
        /// </summary>
        public static async Task<string> WriteTimestampsAsync(MediaFile file, string path)
        {
            var settings = new AvProcess.FfprobeSettings
            {
                Args = $"-select_streams v:0 -show_entries frame=best_effort_timestamp_time -of csv=p=0 {file.ImportPath.Wrap()}",
                LogLevel = "error",
            };

            string output = await AvProcess.RunFfprobe(settings);
            var times = new List<string>();
            double last = 0;

            foreach (string line in output.SplitIntoLines())
            {
                // A blank line is not a frame. ffprobe's output ends with one, and counting it made
                // the array one longer than the clip - caught by the length check below, which is
                // there precisely because an off-by-one here would misplace every frame after it.
                if (line.Trim().Length == 0)
                    continue;

                string value = line.Trim().TrimEnd(',').Split(',')[0];

                // A frame ffmpeg could not time at all, and the running maximum below: the array has to
                // be non-decreasing for a search over it to mean anything, and 6 steps in 1259 went
                // backwards on the fixture. Flattening them costs at most one frame's placement each.
                if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double t))
                    t = last;

                last = Math.Max(last, t);
                times.Add(last.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture));
            }

            if (times.Count < 2)
                return "";

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, times);
            Logger.Log($"Read {times.Count} frame timestamps spanning {times[times.Count - 1]}s for the cadence repair.", true);
            return path;
        }

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
            // Only a frame count is wanted here, and all three agree on it, so this keeps the cheap
            // order rather than the exact-random-access one the repair itself asks for.
            sb.AppendLine("PLAIN_ORDER = ['lsmas', 'bestsource', 'ffms2']");
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
            // Before the script, because the script cannot place a single frame without it.
            RunTask.ReportProgress("Reading the frame timestamps...");
            string timesPath = await WriteTimestampsAsync(file, Path.Combine(Paths.GetSessionDataPath(), "cadence-times.txt"));

            if (timesPath.IsEmpty())
                return "The frame timestamps could not be read, and without them there is no way to tell where each " +
                    "picture belongs - which is the whole of what this repair decides.";

            string script = WriteScript(file, file.ImportPath, Path.Combine(Paths.GetSessionDataPath(), "cadence.vpy"), timesPath);

            await Qtgmc.SetProgressTargetAsync(script, file);

            if (RunTask.canceled)
                return "";

            // The output is still interlaced - this repairs the *cadence* and nothing else, so the
            // fields are handed on woven exactly as they arrived and the file is still something to
            // deinterlace afterwards.
            //
            // **y4m carries a frame size, a rate and a range and nothing else, so the field order and
            // the colour both have to be re-stated - and that is one problem with one fix, not two.**
            // VSPipe's header reads `YUV4MPEG2 C420 W720 H480 F30000:1001 Ip` - `Ip` is *progressive* -
            // so ffmpeg marks every frame progressive on the way in, and everything the source said
            // about its primaries, transfer and matrix is gone by the time ffmpeg reads the pipe. An
            // NTSC capture declaring bt470m/bt470m/bt470bg came back `unknown` on all three, and the
            // AV1 encode reading that file was then handed `--color-primaries 2
            // --transfer-characteristics 2 --matrix-coefficients 2`, leaving every player to guess the
            // matrix of a file that had said precisely what it was.
            //
            // **The frame's own properties beat the output AVOptions, which is why `-color_primaries`
            // and `-color_trc` cannot do this job.** Measured through this exact pipe shape on that
            // capture, reading primaries/transfer/matrix/range back off the result:
            //
            //   -color_primaries bt470m -color_trc bt470m -colorspace bt470bg -color_range tv
            //                                              -> unknown, unknown, bt470bg, tv
            //   the same four written numerically (4/4/5/1) -> unknown, unknown, bt470bg, tv
            //   -vf setparams=color_primaries=bt470m:...    -> bt470m,  bt470m,  bt470bg, tv
            //
            // Two of the four are honoured and two are dropped in silence, *identically* whether they
            // are spelled as names or as numbers. An earlier fix here changed only the spelling and
            // shipped believing that had settled it - the spelling was never what decided it. The same
            // command reading the source as a **file** rather than through the pipe tags all four
            // correctly, which is what confines this to piped y4m and what makes a test that does not
            // use the real pipe worthless here.
            //
            // Setting them on the frames settles the field order in the same filter, so `setfield` is
            // subsumed rather than kept beside it. Measured against a tt source through a real VSPipe
            // producer:
            //
            //   -x264-params tff=1                          -> field_order=progressive
            //   -flags +ilme+ildct -x264-params tff=1       -> field_order=bt   (the wrong parity)
            //   -vf setparams=field_mode=tff ... tff=1      -> field_order=tb   (right)
            //
            // The middle row is the one to remember: it looks like it worked, and leaves the next
            // deinterlace running at the wrong parity on a file this utility had just repaired. x264's
            // own tff/bff stays beside the filter because it turns interlaced *encoding* on, which is a
            // different question from what the frames are marked as. The same trap waits for anything
            // else piping interlaced VapourSynth frames into ffmpeg.
            bool bff = file.VideoStreams.First().FieldOrder == FieldOrder.BottomFieldFirst;
            var setParams = new List<string> { $"field_mode={(bff ? "bff" : "tff")}" };

            // Probed from the source rather than taken off MediaFile.ColorData, which is populated
            // lazily and may not have been asked for yet. The names ffprobe prints and the values
            // setparams accepts come out of the same libavutil tables, so this round trip is lossless
            // by construction - which is true of *this* filter and was never true of the AVOptions.
            foreach (var pair in new[] { ("color_primaries", "color_primaries"), ("color_transfer", "color_trc"),
                                         ("color_space", "colorspace"), ("color_range", "range") })
            {
                var probe = new AvProcess.FfprobeSettings
                {
                    Args = $"-select_streams v:0 -show_entries stream={pair.Item1} -of default=noprint_wrappers=1:nokey=1 {file.ImportPath.Wrap()}",
                    LogLevel = "error",
                };

                string value = (await AvProcess.RunFfprobe(probe)).SplitIntoLines().FirstOrDefault(x => x.IsNotEmpty())?.Trim() ?? "";

                // "unknown" and "N/A" are ffprobe saying the source states nothing, and every setparams
                // property defaults to `auto` - keep whatever came in - so leaving it off is exactly
                // right: asserting `unknown` would state ignorance as though it were a measurement.
                // The character test guards a value being spliced into a filter graph, where a `:` or
                // an `=` would change the graph's shape rather than fail.
                if (value.IsNotEmpty() && value != "unknown" && value != "N/A" && value.All(c => char.IsLetterOrDigit(c) || c == '-'))
                    setParams.Add($"{pair.Item2}={value}");
            }

            string interlace = $"-vf setparams={string.Join(":", setParams)} -x264-params {(bff ? "bff=1" : "tff=1")}";
            Logger.Log($"Re-stating what y4m drops: setparams={string.Join(":", setParams)}.", true);

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
