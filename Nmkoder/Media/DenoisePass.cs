using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using System.IO;
using System.Threading.Tasks;

namespace Nmkoder.Media
{
    /// <summary>
    /// Writes a denoised copy of a video, for the one caller that needs one: the Film Grain utility's
    /// Measure operation, which diffs the source against this copy to find out what the grain in it is
    /// and writes the table out.
    /// <para/>
    /// **It is lossless, because its output is measured against, and grain is precisely the small
    /// high-frequency signal a quantiser disturbs first.** A file that is only ever *encoded* can be
    /// near-lossless once that is measured to be transparent - the AV1AN tab's tone-map pass was,
    /// x264 CRF 6 fast -tune grain carrying grain energy and tone values through intact, before that
    /// pass was removed outright - but what this pass writes feeds grav1synth's frame-for-frame
    /// diff, and a codec's additions there would be read as grain and written into the table.
    /// <para/>
    /// The copy carries the source's audio, subtitles and chapters. They are free - stream copies -
    /// and they are what makes a kept denoised file (the utility's "keep the denoised video too") a
    /// video someone can actually encode afterwards rather than a silent one. That mattered more when
    /// the AV1AN tab encoded this file directly in its Measured mode: av1an takes every non-video
    /// track from its own <c>-i</c> input, and this pass writing video only made every Measured
    /// encode silent. That mode is gone - see <see cref="Data.GrainSynthConfig.EncodeModes"/> - and
    /// the tracks stay for the hand-run version of the same workflow.
    /// </summary>
    class DenoisePass
    {
        /// <summary>
        /// The FFV1 encode this pass writes: -level 3 -g 1 is FFV1's intra-only mode, every frame
        /// standing alone, which costs a little size and buys seeking - and grav1synth asks for frames
        /// one at a time. slicecrc off, since nothing here is transporting the file anywhere; slices
        /// for the threading.
        /// </summary>
        public const string Ffv1Args = "-c:v ffv1 -level 3 -g 1 -slices 16 -slicecrc 0 -threads 0";

        /// <summary> What the finished file will be, for the log line that announces the run. </summary>
        public static string DescribeOutput()
        {
            return "a lossless FFV1 MKV";
        }

        /// <summary>
        /// Denoises <paramref name="inPath"/> into <paramref name="outPath"/> with the config's filter,
        /// and returns why nothing usable came out - or "" when it did. A run the user stopped also
        /// returns "", as <see cref="DeinterlacePass.RunAsync"/> does: cancelling is not a failure to
        /// report and every caller checks <see cref="RunTask.canceled"/> anyway.
        /// <para/>
        /// The source is diffed against this copy exactly as it sits, so nothing here renders geometry:
        /// the utility measures the file it was given. The AV1AN tab used to fold its resize in here,
        /// and needed a second output to diff against once it did - the input no longer being the same
        /// frame size as the denoised copy. Both went with the tab's measuring modes.
        /// </summary>
        public static async Task<string> RunAsync(GrainSynthConfig config, string inPath, string outPath, MediaFile source)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));

            string args = $"-i {inPath.Wrap()} -map 0:v:0 -map 0:a? -map 0:s? -map 0:t? " +
                $"-vf {config.GetDenoiseFilter()} {Ffv1Args} -c:a copy {DeinterlacePass.GetSubtitleArgs(source)} -dn " +
                $"-map_metadata 0 -map_chapters 0 {outPath.Wrap()}";

            return await RunAndJudgeAsync(args, outPath,
                "The denoised copy was not written. There is nothing to measure the source's grain against.");
        }

        /// <summary> Runs the command and judges the named artifact rather than the exit code, the
        /// standard this pipeline holds every pass to. </summary>
        private static async Task<string> RunAndJudgeAsync(string args, string outPath, string missingMessage)
        {
            var settings = new AvProcess.FfmpegSettings
            {
                Args = args,
                LoggingMode = AvProcess.LogMode.OnlyLastLine,
                ProgressBar = true,
                ReportFailure = false, // The caller words this one - it is a step of an encode, not the encode
            };

            await AvProcess.RunFfmpeg(settings);

            if (RunTask.canceled || RunTask.failed)
                return "";

            if (settings.Problem.IsNotEmpty())
                return settings.Problem;

            if (IoUtils.GetFilesize(outPath) < 1)
                return missingMessage;

            return "";
        }
    }
}
