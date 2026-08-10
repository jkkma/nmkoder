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
    /// **Lossless FFV1, like every AV1AN input pass now, at the user's own
    /// request: the intermediate is the file av1an encodes, so its generation is the ceiling on the
    /// final picture, and losslessness takes the generation out of the chain entirely.** It costs a
    /// temporary file several times the source's size, which the announce log says out loud - and
    /// what keeps that cost sane is the folded geometry: the caller appends the tab's crop, resize
    /// and borders to this pass's chain (see <see cref="Data.Av1anFrame.GeometryInPass"/>), so the
    /// file is written at the encode's frame size rather than the source's. Before that fold, a 4K
    /// film scaled to 1080p paid lossless rates for four times the pixels the encoder ever saw -
    /// ~40 GB for a five-minute test clip. The
    /// history is worth keeping because the fallback knob is real: x264 CRF 12 with <c>-tune grain</c>
    /// at <c>fast</c> measured 100% of grain high-frequency energy retained and tone values
    /// band-identical, at about a tenth of the size - that is the measured-transparent choice if the
    /// disk cost ever has to come back down, and the codec line below is the whole change. x264's own
    /// lossless mode is not an option here, and that was measured rather than assumed: 10-bit
    /// <c>-qp 0</c> comes back with differing frame hashes - high-bit-depth x264 shifts its QP scale,
    /// so 0 is no longer the lossless point - and ffmpeg's wrapper refuses the negative QP that scale
    /// would need. FFV1 round-trips bit-exact, and it is the codec the denoised file beside this one
    /// already uses, so the encode arguments are <see cref="DenoisePass.Ffv1Args"/>, stated once.
    /// </summary>
    class ToneMapPass
    {
        /// <summary> What the finished file will be, for the log line that announces the run. </summary>
        public static string DescribeOutput()
        {
            return "a lossless SDR FFV1 MKV";
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
        /// <paramref name="extraFilters"/> is the tab's geometry - crop, pad, resize, borders - when
        /// the caller folds it into this pass (<see cref="Data.Av1anFrame.GeometryInPass"/>), appended
        /// after the whole tone-map chain in the order the per-chunk chain would have run it. That is
        /// what sizes the lossless intermediate to the *encode* rather than the source: written at 4K
        /// for a 1080p encode it carries four times the pixels the encoder ever sees, which a report
        /// measured at ~40 GB for a five-minute test clip.
        /// <para/>
        /// The output pins 10 bits, whatever the negotiation with x264 would have picked: this SDR
        /// intermediate is about to be encoded 10-bit by av1an, and dropping to 8 in between would put
        /// banding into every gradient the roll-off just compressed. How it is pinned differs by
        /// backend, and the difference is deliberate - see the comment on it below. The geometry runs
        /// after the pin and changes no format: swscale, crop and pad all hand on the 10-bit frames
        /// they are given.
        /// </summary>
        public static async Task<string> RunAsync(ToneMapConfig config, VideoColorData srcColor, string inPath, string outPath,
            MediaFile source, string extraFilters = "")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));

            string filter = PrepareFilter(config, srcColor, extraFilters);

            if (filter.IsEmpty())
                return "Nothing to tone-map - the chain came out empty.";

            string args = $"-i {inPath.Wrap()} -map 0:v:0 -map 0:a? -map 0:s? -map 0:t? " +
                $"{GetDeviceArgs(config)}-vf \"{filter}\" " +
                $"{DenoisePass.Ffv1Args} -c:a copy {DeinterlacePass.GetSubtitleArgs(source)} -dn " +
                $"-map_metadata 0 -map_chapters 0 {outPath.Wrap()}";

            string problem = await RunAndJudgeAsync(args, outPath);

            if (problem.IsNotEmpty())
                return problem;

            return "";
        }

        /// <summary>
        /// The fused shape: the tone map rendered once, with the denoised copy the grain passes need
        /// written as a second output of the same command - one source decode and one tone-map render
        /// where the separate passes cost two, which at UHD sizes is the pass this saves. The graph
        /// splits *after* the whole tone-map chain (format pinning included), so both outputs carry
        /// identical SDR frames, and with both outputs lossless the grain measurement downstream is
        /// exact: the diff reference *is* the rendered frames, with no encode generation between.
        /// <para/>
        /// Both files or neither: the caller deletes the pair on any failure, because each half is the
        /// other's reason to be trusted - a denoised file without its tone-mapped sibling would be
        /// diffed against nothing, and a resume that found one half would mistake a dead fused run for
        /// a finished pass. The separate <see cref="RunAsync"/> and <see cref="DenoisePass.RunAsync"/>
        /// stay as the repair path for exactly that resume: a kept tone-mapped file with the denoised
        /// half missing is denoised from disk without re-rendering the tone map.
        /// </summary>
        public static async Task<string> RunFusedAsync(ToneMapConfig config, VideoColorData srcColor, GrainSynthConfig grain,
            string inPath, string outPath, string denoisedOutPath, MediaFile source, string extraFilters = "")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));

            // The geometry sits before the split on purpose: both outputs must carry it, because the
            // denoised copy is the grain measurement's other half and grav1synth diffs the two frame
            // for frame - and the encoder is handed one of them, so measuring at any other size would
            // put the table in the wrong domain.
            string filter = PrepareFilter(config, srcColor, extraFilters);

            if (filter.IsEmpty())
                return "Nothing to tone-map - the chain came out empty.";

            string graph = $"[0:v:0]{filter},split=2[tm][dn];[dn]{grain.GetDenoiseFilter()}[den]";

            // Output options bind to the output that follows them, so the tone-mapped file keeps the
            // exact shape RunAsync gives it - every track carried, metadata and chapters included -
            // and the denoised one keeps DenoisePass's: video only, FFV1.
            string args = $"-i {inPath.Wrap()} {GetDeviceArgs(config)}-filter_complex \"{graph}\" " +
                $"-map \"[tm]\" -map 0:a? -map 0:s? -map 0:t? " +
                $"{DenoisePass.Ffv1Args} -c:a copy {DeinterlacePass.GetSubtitleArgs(source)} -dn " +
                $"-map_metadata 0 -map_chapters 0 {outPath.Wrap()} " +
                $"-map \"[den]\" -an -sn -dn {DenoisePass.Ffv1Args} {denoisedOutPath.Wrap()}";

            string problem = await RunAndJudgeAsync(args, outPath);

            if (problem.IsNotEmpty())
                return problem;

            // A stopped run is not a failure to word - same convention as the artifact judge itself.
            if (RunTask.canceled || RunTask.failed)
                return "";

            if (!RunTask.OutputExists(denoisedOutPath))
                return $"FFmpeg reported no error, but '{Path.GetFileName(denoisedOutPath)}' was not written.";

            return "";
        }

        /// <summary>
        /// The chain with its output depth pinned and any folded geometry appended, or "" where the
        /// config builds none.
        /// <para/>
        /// For libplacebo the pin is spliced onto the chain's own ":range=tv" tail rather than passed
        /// as an output -pix_fmt, because the two are not the same statement: inside the filter,
        /// libplacebo itself renders and dithers to 10 bits, where an output option lets the format
        /// negotiation pick the filter's output first and convert after - and a negotiation that lands
        /// on 8 bits there would bake the banding in before the conversion back up. The zscale chain
        /// gets a trailing format filter instead - zscale has no format option, and the requirement
        /// placed directly downstream makes zimg's own final conversion produce 10 bits, with the
        /// format filter itself left a no-op.
        /// <para/>
        /// The geometry goes last, after the pin and after the side-data deletes the chains end with:
        /// the per-chunk chain it moved out of also ran it on this pass's finished output, so last is
        /// the order that keeps the pixels identical - and the deletes touch metadata, not frames, so
        /// nothing re-adds what they removed.
        /// </summary>
        private static string PrepareFilter(ToneMapConfig config, VideoColorData srcColor, string extraFilters)
        {
            string filter = config.GetFilterArgs(srcColor);

            if (filter.IsEmpty())
                return "";

            filter = config.UseLibplacebo
                ? filter.Replace(":range=tv", ":range=tv:format=yuv420p10le")
                : $"{filter},format=yuv420p10le";

            return extraFilters.IsNotEmpty() ? $"{filter},{extraFilters}" : filter;
        }

        /// <summary> The device argument only for the backend that needs a device - on the zscale path
        /// this machine has no usable Vulkan, and asking ffmpeg to create one would fail the pass over
        /// an option its filters never read. It sits where the probe proved it works: after the input,
        /// in front of the filter. </summary>
        private static string GetDeviceArgs(ToneMapConfig config)
        {
            return config.UseLibplacebo ? $"{ToneMapConfig.DeviceArgs} " : "";
        }


        /// <summary> Runs the command and judges the named artifact. The failure libplacebo is known
        /// for exits 0 having written nothing - the probe's own reason for muxing to md5 - so the
        /// artifact is judged, not the exit code. </summary>
        private static async Task<string> RunAndJudgeAsync(string args, string outPath)
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

            if (!RunTask.OutputExists(outPath))
                return $"FFmpeg reported no error, but '{Path.GetFileName(outPath)}' was not written.";

            return "";
        }
    }
}
