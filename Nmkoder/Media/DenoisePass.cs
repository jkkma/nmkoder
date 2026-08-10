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
    /// **It is lossless, where <see cref="DeinterlacePass"/> beside it is deliberately near-lossless, and
    /// the difference is what the output is for.** That pass writes a file to be looked at or encoded
    /// again, where x264 at CRF 12 is indistinguishable from the source and a tenth of the size. This one
    /// writes a file to be *measured against*: whatever a lossy codec adds is a difference between the two
    /// files that is not grain, and grain is precisely the small high-frequency signal a quantiser
    /// disturbs first. So FFV1, and the size that comes with it - which is the honest cost of the mode and
    /// is stated on the row rather than discovered on a full disk.
    /// </summary>
    class DenoisePass
    {
        /// <summary>
        /// The FFV1 encode both writers of a denoised file share - this pass, and
        /// <see cref="ToneMapPass.RunFusedAsync"/>, which writes the same file as the second output of
        /// the fused tone-map command. One statement, because a denoised file that came out of either
        /// door must be the same kind of file: -level 3 -g 1 is FFV1's intra-only mode - every frame
        /// stands alone, which costs a little size and buys seeking, and both readers of this file seek
        /// it (av1an splits it into chunks and grav1synth is asked for frames one at a time); slicecrc
        /// off, since nothing here is transporting the file anywhere; slices for the threading.
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
        /// Video only, and no other track is carried. The two callers of this file are grav1synth, which
        /// reads the video stream and nothing else, and av1an, which is given the audio separately out of
        /// the original - so copying the audio through here would be writing a second copy of it to disk
        /// for nobody. <c>-map 0:v:0</c> rather than <c>-map 0:v</c> for the same reason the encode does
        /// it: a second video track would be measured against the wrong picture.
        /// </summary>
        public static async Task<string> RunAsync(GrainSynthConfig config, string inPath, string outPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));

            string args = $"-i {inPath.Wrap()} -map 0:v:0 -an -sn -dn " +
                $"-vf {config.GetDenoiseFilter()} {Ffv1Args} " +
                $"{outPath.Wrap()}";

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

            if (IoUtils.GetFilesize(outPath) < 1)
                return "The denoised copy was not written. There is nothing to measure the source's grain against.";

            return "";
        }
    }
}
