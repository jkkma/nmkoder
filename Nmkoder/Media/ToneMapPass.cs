using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using System.IO;
using System.Threading.Tasks;

namespace Nmkoder.Media
{
    /// <summary>
    /// Renders the libplacebo tone map over one whole file into an SDR copy, for the AV1AN tab - the
    /// same shape as <see cref="DeinterlacePass"/> beside it, and for the same underlying reason: a
    /// filter with temporal state cannot run inside av1an, which applies its <c>-f</c> chain in an
    /// ffmpeg it starts and stops around every chunk.
    /// <para/>
    /// For QTGMC the state is VapourSynth's; here it is peak detection's history. libplacebo can only
    /// learn a file's real brightness by measuring it as it goes - there is no option to hand it a peak,
    /// it reads only the mastering-display side data otherwise, and av1an's y4m pipes carry no side
    /// data at all, so inside av1an it always assumed the 10000-nit PQ ceiling and every tone-mapped
    /// encode came out at its darkest possible reading. Run per chunk with detection on instead, the
    /// history restarts at every boundary: measured, a restart mid-ramp steps the exposure by 6 code
    /// values and takes ~23 frames to converge, which is a visible pump at 26 places in a film. One
    /// continuous pass has neither problem, and it buys two things beside: the target-quality probes
    /// score the SDR frames actually being encoded (per-chunk filters are invisible to them), and the
    /// grain measurement pass downstream measures the picture the encoder will get.
    /// <para/>
    /// Near-lossless x264 like the deinterlace pass, not lossless like the denoise pass, because the
    /// output is encoded again rather than measured against. The preset is faster than that pass's
    /// <c>medium</c>: this one routinely runs at UHD sizes, where medium costs hours for size nothing
    /// downstream would notice, and the GPU filter in front of it is quick.
    /// </summary>
    class ToneMapPass
    {
        private const int Crf = 12;
        private const string Preset = "veryfast";

        /// <summary> What the finished file will be, for the log line that announces the run. </summary>
        public static string DescribeOutput()
        {
            return $"a near-lossless SDR MKV (x264, CRF {Crf})";
        }

        /// <summary>
        /// Tone-maps <paramref name="inPath"/> into <paramref name="outPath"/> with
        /// <paramref name="config"/>'s libplacebo chain, and returns why nothing usable came out - or ""
        /// when it did. A run the user stopped also returns "", as the sibling passes do: cancelling is
        /// not a failure to report, and every caller checks <see cref="RunTask.canceled"/> anyway.
        /// <para/>
        /// <paramref name="source"/> is the loaded file, whose track layout the input carries whether or
        /// not it is that file itself - a cut or deinterlaced copy is not, but its tracks are the same
        /// ones, and the subtitle handling reads them.
        /// <para/>
        /// The output pins 10 bits (<c>format=yuv420p10le</c> on the filter itself), whatever the
        /// negotiation between libplacebo and x264 would have picked: this SDR intermediate is about to
        /// be encoded 10-bit by av1an, and dropping to 8 in between would put banding into every
        /// gradient the roll-off just compressed.
        /// </summary>
        public static async Task<string> RunAsync(ToneMapConfig config, VideoColorData srcColor, string inPath, string outPath, MediaFile source)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));

            string filter = config.GetFilterArgs(srcColor);

            if (filter.IsEmpty())
                return "Nothing to tone-map - the chain came out empty.";

            // Spliced onto the chain's own ":range=tv" tail rather than passed as an output -pix_fmt,
            // because the two are not the same statement: inside the filter, libplacebo itself renders
            // and dithers to 10 bits, where an output option lets the format negotiation pick the
            // filter's output first and convert after - and a negotiation that lands on 8 bits there
            // would bake the banding in before the conversion back up.
            filter = filter.Replace(":range=tv", ":range=tv:format=yuv420p10le");

            // The device argument sits where the probe proved it works - after the input, in front of
            // the filter - and everything that is not video is copied through, the deinterlace pass's
            // reasoning: re-encoding audio on the way through would cost quality for nothing.
            string args = $"-i {inPath.Wrap()} -map 0:v:0 -map 0:a? -map 0:s? -map 0:t? " +
                $"{ToneMapConfig.DeviceArgs} -vf \"{filter}\" " +
                $"-c:v libx264 -crf {Crf} -preset {Preset} -c:a copy {DeinterlacePass.GetSubtitleArgs(source)} -dn " +
                $"-map_metadata 0 -map_chapters 0 {outPath.Wrap()}";

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

            // The failure libplacebo is known for exits 0 having written nothing - the probe's own
            // reason for muxing to md5 - so the artifact is judged, not the exit code.
            if (!RunTask.OutputExists(outPath))
                return $"FFmpeg reported no error, but '{Path.GetFileName(outPath)}' was not written.";

            return "";
        }
    }
}
