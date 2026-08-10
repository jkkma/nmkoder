using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using System.IO;
using System.Threading.Tasks;

namespace Nmkoder.Media
{
    /// <summary>
    /// Renders the tone map over one whole file into an SDR copy, for the AV1AN tab - the same shape
    /// as <see cref="DeinterlacePass"/> beside it. Which chain it renders is the config's own
    /// <see cref="ToneMapConfig.UseLibplacebo"/>, and the two arrive here for different reasons;
    /// <see cref="UI.Tasks.Av1anUi.ToneMapRendersInFront"/> is where both are stated.
    /// <para/>
    /// libplacebo is here because a filter with temporal state cannot run inside av1an, which applies
    /// its <c>-f</c> chain in an ffmpeg it starts and stops around every chunk. For QTGMC the state is
    /// VapourSynth's; here it is peak detection's history. libplacebo can only learn a file's real
    /// brightness by measuring it as it goes - there is no option to hand it a peak, it reads only the
    /// mastering-display side data otherwise, and av1an's y4m pipes carry no side data at all, so
    /// inside av1an it always assumed the 10000-nit PQ ceiling and every tone-mapped encode came out
    /// at its darkest possible reading. Run per chunk with detection on instead, the history restarts
    /// at every boundary: measured, a restart mid-ramp steps the exposure by 6 code values and takes
    /// ~23 frames to converge, a visible pump at 26 places in a film. One continuous pass has neither
    /// problem, and it buys two things beside: the target-quality probes score the SDR frames actually
    /// being encoded (per-chunk filters are invisible to them), and the grain passes downstream
    /// measure the picture the encoder will get.
    /// <para/>
    /// The zscale chain is stateless and normally stays per-chunk; it lands here exactly when a grain
    /// denoise pass follows, because that pass runs on a file before av1an starts - so with the tone
    /// map still inside av1an the grain would be measured on HDR frames while the encoder received SDR
    /// ones, and a grain table's amplitudes live in its file's own signal domain.
    /// <para/>
    /// Near-lossless x264 like the deinterlace pass, not lossless like the denoise pass, because the
    /// output is encoded again rather than measured against. **The settings were chosen by measuring
    /// what survives them, and the thing measured was grain** - this file is what av1an encodes, so
    /// texture the intermediate loses is texture the final encode cannot have, and the grain passes
    /// downstream measure this very file. High-frequency energy of heavy synthetic grain through CRF
    /// 12: <c>veryfast</c> keeps 90.5% - the preset's trellis 0 and thin analysis, not the CRF, since
    /// even CRF 3 veryfast only reaches 96.4% - <c>medium</c> keeps 98.5%, and <c>fast</c> with
    /// <c>-tune grain</c> keeps 100% while sitting a whole preset step quicker than medium at the UHD
    /// sizes this pass routinely runs at. So: fast, tuned for grain, unconditionally - a clean
    /// source pays a little bitrate on a temporary file, and fidelity is this file's entire job.
    /// </summary>
    class ToneMapPass
    {
        private const int Crf = 12;
        private const string Preset = "fast";
        private const string Tune = "grain";

        /// <summary> What the finished file will be, for the log line that announces the run. </summary>
        public static string DescribeOutput()
        {
            return $"a near-lossless SDR MKV (x264, CRF {Crf})";
        }

        /// <summary>
        /// Tone-maps <paramref name="inPath"/> into <paramref name="outPath"/> with whichever chain
        /// <paramref name="config"/> builds, and returns why nothing usable came out - or ""
        /// when it did. A run the user stopped also returns "", as the sibling passes do: cancelling is
        /// not a failure to report, and every caller checks <see cref="RunTask.canceled"/> anyway.
        /// <para/>
        /// <paramref name="source"/> is the loaded file, whose track layout the input carries whether or
        /// not it is that file itself - a cut or deinterlaced copy is not, but its tracks are the same
        /// ones, and the subtitle handling reads them.
        /// <para/>
        /// The output pins 10 bits, whatever the negotiation with x264 would have picked: this SDR
        /// intermediate is about to be encoded 10-bit by av1an, and dropping to 8 in between would put
        /// banding into every gradient the roll-off just compressed. How it is pinned differs by
        /// backend, and the difference is deliberate - see the comment on it below.
        /// </summary>
        public static async Task<string> RunAsync(ToneMapConfig config, VideoColorData srcColor, string inPath, string outPath, MediaFile source)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));

            string filter = config.GetFilterArgs(srcColor);

            if (filter.IsEmpty())
                return "Nothing to tone-map - the chain came out empty.";

            // For libplacebo, spliced onto the chain's own ":range=tv" tail rather than passed as an
            // output -pix_fmt, because the two are not the same statement: inside the filter,
            // libplacebo itself renders and dithers to 10 bits, where an output option lets the format
            // negotiation pick the filter's output first and convert after - and a negotiation that
            // lands on 8 bits there would bake the banding in before the conversion back up. The
            // zscale chain gets a trailing format filter instead - zscale has no format option, and
            // the requirement placed directly downstream makes zimg's own final conversion produce
            // 10 bits, with the format filter itself left a no-op.
            filter = config.UseLibplacebo
                ? filter.Replace(":range=tv", ":range=tv:format=yuv420p10le")
                : $"{filter},format=yuv420p10le";

            // The device argument only for the backend that needs a device - on the zscale path this
            // machine has no usable Vulkan, and asking ffmpeg to create one would fail the pass over
            // an option its filters never read. It sits where the probe proved it works - after the
            // input, in front of the filter - and everything that is not video is copied through, the
            // deinterlace pass's reasoning: re-encoding audio would cost quality for nothing.
            string device = config.UseLibplacebo ? $"{ToneMapConfig.DeviceArgs} " : "";

            string args = $"-i {inPath.Wrap()} -map 0:v:0 -map 0:a? -map 0:s? -map 0:t? " +
                $"{device}-vf \"{filter}\" " +
                $"-c:v libx264 -crf {Crf} -preset {Preset} -tune {Tune} -c:a copy {DeinterlacePass.GetSubtitleArgs(source)} -dn " +
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
