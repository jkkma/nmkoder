using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using System.IO;
using System.Threading.Tasks;

namespace Nmkoder.Media
{
    /// <summary>
    /// Writes a denoised copy of a video, for the one caller that needs one: the Grain Synthesis row's
    /// Measured mode, which diffs the source against this copy to find out what the grain in it is, and
    /// then encodes this copy so that the grain is described rather than coded.
    /// <para/>
    /// **It is lossless, because its outputs are measured against, and grain is precisely the small
    /// high-frequency signal a quantiser disturbs first.** A file that is only ever *encoded* can be
    /// near-lossless once that is measured to be transparent - see <see cref="ToneMapPass"/>'s solo
    /// shape - but everything this pass writes feeds grav1synth's frame-for-frame diff, and a codec's
    /// additions there would be read as grain and written into the table.
    /// <para/>
    /// **The denoised copy carries the source's audio, subtitles and chapters, and that is not
    /// generosity - it is av1an's whole supply of them.** This file is what av1an encodes in Measured
    /// mode, and av1an takes every non-video track from its own <c>-i</c> input: the <c>-a</c>
    /// arguments are applied to that file, and the attachment step waits on the <c>audio.mkv</c> its
    /// audio ffmpeg writes *from that file*. This pass used to write video only, on a comment claiming
    /// av1an "is given the audio separately out of the original" - machinery that does not exist - so
    /// every Measured-mode encode came out with no audio, no subtitles and no chapters. The tracks are
    /// stream copies; the disk they cost is the price of the output having sound.
    /// <para/>
    /// The reference the diff needs is the other half of the shape. When a tone-map pass ran in front,
    /// its output is the reference and this pass writes one file. When this pass renders the tab's
    /// geometry itself (<see cref="RunWithReferenceAsync"/> - an SDR source with a resize, where no
    /// tone-map pass exists to fold the geometry into), the raw input is no longer the same frame size
    /// as the denoised copy, so the same command writes a geometried reference beside it: one decode,
    /// two outputs, exactly the fused tone-map command's shape. The reference is video-only - its one
    /// reader is grav1synth - and the caller deletes it the moment the diff has succeeded.
    /// </summary>
    class DenoisePass
    {
        /// <summary>
        /// The FFV1 encode every writer of a measured-against file shares - this pass, and
        /// <see cref="ToneMapPass.RunFusedAsync"/>, which writes the same denoised file as the second
        /// output of the fused tone-map command. One statement, because a denoised file that came out
        /// of either door must be the same kind of file: -level 3 -g 1 is FFV1's intra-only mode -
        /// every frame stands alone, which costs a little size and buys seeking, and both readers of
        /// this file seek it (av1an splits it into chunks and grav1synth is asked for frames one at a
        /// time); slicecrc off, since nothing here is transporting the file anywhere; slices for the
        /// threading.
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
        /// <paramref name="geometryFilters"/> is the tab's geometry when this pass is the one rendering
        /// it and no diff reference is wanted - a user-supplied table with Denoise ticked, where the
        /// denoised file must still be the encoded frame but nothing will be measured against it. It
        /// runs ahead of the denoiser, which is the order the whole pipeline keeps: the grain being
        /// removed has to be the grain of the frames being encoded.
        /// </summary>
        public static async Task<string> RunAsync(GrainSynthConfig config, string inPath, string outPath,
            MediaFile source, string geometryFilters = "")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));

            string vf = geometryFilters.IsNotEmpty()
                ? $"{geometryFilters},{config.GetDenoiseFilter()}"
                : config.GetDenoiseFilter();

            string args = $"-i {inPath.Wrap()} -map 0:v:0 -map 0:a? -map 0:s? -map 0:t? " +
                $"-vf {vf} {Ffv1Args} -c:a copy {DeinterlacePass.GetSubtitleArgs(source)} -dn " +
                $"-map_metadata 0 -map_chapters 0 {outPath.Wrap()}";

            return await RunAndJudgeAsync(args, outPath,
                "The denoised copy was not written. There is nothing to measure the source's grain against.");
        }

        /// <summary>
        /// The two-output shape: the geometry rendered once, with the diff's reference and the denoised
        /// copy split off the same frames - one source decode where separate renders would cost two.
        /// The split sits after the geometry and before the denoiser, so both outputs are the encoded
        /// frame and differ by exactly the grain the denoiser removed - which is what makes the table
        /// grav1synth measures between them live in the encoded frame's domain rather than the
        /// source's. Both files or neither, judged here and deleted together by the caller on any
        /// failure, for the fused tone-map pass's reason: half a pair reads as a finished pass to the
        /// next resume.
        /// </summary>
        public static async Task<string> RunWithReferenceAsync(GrainSynthConfig config, string inPath,
            string refPath, string outPath, string geometryFilters, MediaFile source)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));

            string graph = $"[0:v:0]{geometryFilters},split=2[ref][dn];[dn]{config.GetDenoiseFilter()}[den]";

            // Output options bind to the output that follows them: the reference is video-only (its
            // one reader is grav1synth), the denoised copy carries the tracks (its reader is av1an,
            // which takes them from nowhere else - see the class note).
            string args = $"-i {inPath.Wrap()} -filter_complex \"{graph}\" " +
                $"-map \"[ref]\" -an -sn -dn {Ffv1Args} {refPath.Wrap()} " +
                $"-map \"[den]\" -map 0:a? -map 0:s? -map 0:t? {Ffv1Args} " +
                $"-c:a copy {DeinterlacePass.GetSubtitleArgs(source)} -dn " +
                $"-map_metadata 0 -map_chapters 0 {outPath.Wrap()}";

            string problem = await RunAndJudgeAsync(args, outPath,
                "The denoised copy was not written. There is nothing to measure the source's grain against.");

            if (problem.IsNotEmpty() || RunTask.canceled || RunTask.failed)
                return problem;

            if (!RunTask.OutputExists(refPath))
                return $"FFmpeg reported no error, but the grain reference '{Path.GetFileName(refPath)}' was not written.";

            return "";
        }

        /// <summary>
        /// The reference alone, for the repair path: a run that died between the render and the end of
        /// a measurement that takes hours has the denoised copy on disk and the reference gone with the
        /// crash - so only the cheap half is re-made, a geometry-only render with no denoiser in it.
        /// </summary>
        public static async Task<string> RunReferenceAsync(string inPath, string refPath, string geometryFilters)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(refPath));

            string args = $"-i {inPath.Wrap()} -map 0:v:0 -an -sn -dn " +
                $"-vf {geometryFilters} {Ffv1Args} {refPath.Wrap()}";

            return await RunAndJudgeAsync(args, refPath,
                "The grain reference was not written. There is nothing to measure the source's grain against.");
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
