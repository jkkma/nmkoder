using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Nmkoder.Media
{
    /// <summary>
    /// Renders a settled <see cref="DeinterlacePlan"/> over one file into a new file, with everything
    /// that is not video copied across untouched.
    /// <para/>
    /// Two callers want exactly this, for different reasons. The Deinterlace utility's output *is*
    /// what was asked for - a progressive copy of a capture, to keep or to feed to something else.
    /// The AV1AN tab's is a step: av1an applies video filters with ffmpeg once per chunk and there is
    /// nowhere in that to put a VapourSynth script, so QTGMC there runs as a pass of its own and av1an
    /// is handed what comes out. The pass is the same either way, which is why it lives here rather
    /// than in either of them.
    /// </summary>
    class DeinterlacePass
    {
        /// <summary>
        /// Near-lossless, and deliberately not lossless: FFV1 on 720x480 at one frame per field runs
        /// to tens of gigabytes an hour, where this is single digits and nothing that reads the result
        /// afterwards - an eye or another encoder - can tell the difference. The preset is x264's own
        /// default, which at standard definition is far quicker than QTGMC in front of it; a slower one
        /// would buy size that neither caller is spending.
        /// </summary>
        private const int Crf = 12;
        private const string Preset = "medium";

        /// <summary> What the finished file will be, for the log line that announces the run. </summary>
        public static string DescribeOutput()
        {
            return $"a near-lossless MKV (x264, CRF {Crf})";
        }

        /// <summary>
        /// Runs <paramref name="plan"/> over <paramref name="inPath"/> into <paramref name="outPath"/>,
        /// and returns why nothing usable came out - or "" when it did. A run the user stopped also
        /// returns "": cancelling is not a failure to report, and every caller is checking
        /// <see cref="RunTask.canceled"/> anyway.
        /// <para/>
        /// <paramref name="name"/> names the VapourSynth script and its log, so two callers cannot
        /// write over one another's. <paramref name="source"/> is the loaded file, whose track layout
        /// the input carries whether or not it is that file itself - which a cut copy is not, and
        /// <paramref name="wholeSource"/> says so: the duration the progress target compares against
        /// is only meaningful where the input really is the whole of what that file reports.
        /// </summary>
        public static async Task<string> RunAsync(DeinterlacePlan plan, string inPath, string outPath, string name,
            MediaFile source, bool wholeSource = true)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));

            string vsLogPath = "";
            string pipe = "";
            string pipeIn = "";
            string videoMap = "0:v:0";

            if (plan.UsesPipe)
            {
                // The same shape the Quick Convert tab uses: the pipe is the last input, so the
                // source keeps index 0 and every other track is still mapped out of it.
                vsLogPath = Path.Combine(Paths.GetSessionDataPath(), $"{name}.log");
                IoUtils.TryDeleteIfExists(vsLogPath);
                string script = Qtgmc.WriteScript(plan, inPath, Path.Combine(Paths.GetSessionDataPath(), $"{name}.vpy"));

                // Before the encode rather than during it: the frames VapourSynth will hand over are
                // the only honest thing to measure this against, and a tape capture whose container
                // lies about its duration is exactly the file this runs on.
                await Qtgmc.SetProgressTargetAsync(script, wholeSource ? source : null);

                if (RunTask.canceled)
                    return "";

                pipe = Qtgmc.BuildVspipeCommand(script, vsLogPath);
                pipeIn = "-f yuv4mpegpipe -thread_queue_size 1024 -i -";
                videoMap = "1:v:0";
            }

            string filter = plan.GetFfmpegFilter();
            string vf = filter.IsEmpty() ? "" : $"-vf {filter}";

            // Everything but the video is copied: re-encoding audio on the way through would cost
            // quality for nothing, whether the result is being kept or encoded again. Data streams are
            // dropped because Matroska stores none.
            string args = $"-i {inPath.Wrap()} {pipeIn} -map {videoMap} -map 0:a? -map 0:s? -map 0:t? " +
                $"-c:v libx264 -crf {Crf} -preset {Preset} {vf} -c:a copy {GetSubtitleArgs(source)} -dn " +
                $"-map_metadata 0 -map_chapters 0 {outPath.Wrap()}";

            var settings = new AvProcess.FfmpegSettings
            {
                Args = args,
                LoggingMode = AvProcess.LogMode.OnlyLastLine,
                ProgressBar = true,
                // Never reported from in here - the VapourSynth verdict below is the better
                // explanation where there is one, and the caller decides how a failure is worded.
                ReportFailure = false,
                PipeFrom = pipe,
                ExtraPathDirs = vsLogPath.IsEmpty() ? new string[0] : Qtgmc.GetPathDirs(),
            };

            await AvProcess.RunFfmpeg(settings);
            FfmpegOutputHandler.overrideTargetDurationMs = -1; // Whatever runs next is not this pass

            if (RunTask.canceled || RunTask.failed) // Already reported, by the user or by the output handler
                return "";

            // ffmpeg reads a script that died as an input that ended, so it finishes cleanly over a
            // file missing the rest of the video - see Qtgmc.ReadRunProblem.
            if (vsLogPath.IsNotEmpty())
            {
                string vsProblem = Qtgmc.ReadRunProblem(vsLogPath);

                if (vsProblem.IsNotEmpty())
                    return $"The deinterlaced video was cut short.\n\n{vsProblem}";
            }

            if (settings.Problem.IsNotEmpty())
                return settings.Problem;

            if (!RunTask.OutputExists(outPath))
                return $"FFmpeg reported no error, but '{Path.GetFileName(outPath)}' was not written.";

            return "";
        }

        /// <summary>
        /// How the subtitles come across: copied, except for mov_text, which is MP4's own text format
        /// and has no place in Matroska at all - asking ffmpeg to copy one there fails the whole
        /// command rather than that one track, so an interlaced MP4 with subtitles would take the
        /// entire pass down with it.
        /// <para/>
        /// Named per stream rather than converting the lot, because copying is what every other format
        /// wants: turning ASS into SRT would throw its styling away for nothing. Output subtitle N is
        /// source subtitle N here - they are mapped in order, out of the one input.
        /// <para/>
        /// Public because <see cref="ToneMapPass"/> writes the same kind of file - every track carried,
        /// video re-rendered - and two copies of this reasoning would drift.
        /// </summary>
        public static string GetSubtitleArgs(MediaFile source)
        {
            var args = new List<string> { "-c:s copy" };

            for (int i = 0; source != null && i < source.SubtitleStreams.Count; i++)
            {
                if ((source.SubtitleStreams[i].Codec ?? "").ToLower() == "mov_text")
                    args.Add($"-c:s:{i} srt");
            }

            return string.Join(" ", args);
        }
    }
}
