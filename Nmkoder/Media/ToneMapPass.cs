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
    /// as <see cref="DeinterlacePass"/> beside it. <see cref="UI.Tasks.Av1anUi.ToneMapRendersInFront"/>
    /// is the gate, and it is libplacebo alone: the zscale chain is stateless and stays per-chunk.
    /// <para/>
    /// libplacebo is here because a filter with temporal state cannot run inside av1an, which applies
    /// its <c>-f</c> chain in an ffmpeg it starts and stops around every chunk - here that state is
    /// peak detection's history. libplacebo can only learn a file's real brightness by measuring it as
    /// it goes: there is no option to hand it a peak, it reads only the mastering-display side data
    /// otherwise, and av1an's y4m pipes carry no side data at all, so inside av1an it always assumed
    /// the 10000-nit PQ ceiling and every tone-mapped encode came out at its darkest possible reading.
    /// Run per chunk with detection on instead, the history restarts at every boundary: measured, a
    /// restart mid-ramp steps the exposure by 6 code values and takes ~23 frames to converge, a
    /// visible pump at 26 places in a film. One continuous pass has neither problem, and it buys one
    /// thing beside: the target-quality probes score the SDR frames actually being encoded, where
    /// per-chunk filters are invisible to them.
    /// <para/>
    /// **The output is near-lossless x264, and the settings were chosen by measuring what survives
    /// them** - this file is only ever *encoded*, av1an decoding it and AV1 coming out. High-frequency
    /// energy of heavy grain through CRF 12 is 90.5% at <c>veryfast</c> (the preset's trellis 0, not
    /// the CRF - even CRF 3 veryfast only reaches 96.4%), 98.5% at <c>medium</c>, and **100% at
    /// <c>fast</c> with <c>-tune grain</c>**, with tone values band-identical: transparent to the
    /// encoder that reads it, at about a tenth of the source's size. Lossless FFV1 replaced this for
    /// a while at the user's request, and the user traded it back after living with the cost: even
    /// rendered at the encode's frame size (the folded geometry, see
    /// <see cref="Data.Av1anFrame.GeometryInPass"/> - before that fold a 4K film scaled to 1080p
    /// paid lossless rates for four times the pixels the encoder ever saw, ~40 GB for a five-minute
    /// test clip), lossless 1080p is still a temporary file in the tens of gigabytes per film.
    /// <para/>
    /// **The CRF stepped down from 12 to 6 at the user's request, and the whole ladder was measured
    /// before the number moved** - the same harness as the original choice, heavy synthetic grain at
    /// 1080p10 through these exact settings. Grain retention and the tone bands are flat across every
    /// rung (99.9-100.2%, bands exact to the code value), so PSNR and size discriminate: 48.5 dB at
    /// CRF 12, then 50.5 / 52.4 / 54.4 / 56.3 / 58.1 / 60.0 at 10 / 8 / 6 / 4 / 2 / 0, against sizes
    /// of 0.70 / 0.76 / 0.81 / 0.88 / 0.95 / 1.02 / 1.11 of the *same frames as lossless FFV1*. The
    /// last two ratios are the finding: on the heavy-grain content where this intermediate is biggest,
    /// x264's bottom rungs cost more disk than FFV1 while still being lossy - CRF 0 at 10 bits is not
    /// lossless (high-bit-depth x264 shifts its QP scale, the lossless point is a negative QP ffmpeg's
    /// wrapper refuses, and framemd5 against this build confirms it again) - so everything below 6
    /// pays lossless-class disk for a lossy file and is dominated by FFV1 outright. 6 is the deepest
    /// rung clearly cheaper than the lossless option: +5.9 dB over the measured-transparent 12, at
    /// 1.25x its size on the worst case, with encode speed flat across the ladder.
    /// <para/>
    /// There was a fused shape beside this one, writing a denoised copy as a second output for the
    /// grain measurement, and it had to be lossless where this is not: grav1synth diffed the two
    /// frame for frame, so a lossy reference would have put the quantizer's noise into the grain
    /// table as though it were grain. It went with the AV1AN tab's measuring modes - see
    /// <see cref="Data.GrainSynthConfig.EncodeModes"/> - which is what leaves this pass free to be
    /// the cheap x264 in every case rather than in most of them.
    /// </summary>
    class ToneMapPass
    {
        private const int Crf = 6;
        private const string Preset = "fast";
        private const string Tune = "grain";

        /// <summary> What the finished file will be, for the log line that announces the run. </summary>
        public static string DescribeOutput()
        {
            return $"a near-lossless SDR MKV (x264, CRF {Crf}, tuned for grain)";
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
                $"-c:v libx264 -crf {Crf} -preset {Preset} -tune {Tune} -c:a copy {DeinterlacePass.GetSubtitleArgs(source)} -dn " +
                $"-map_metadata 0 -map_chapters 0 {outPath.Wrap()}";

            string problem = await RunAndJudgeAsync(args, outPath);

            if (problem.IsNotEmpty())
                return problem;

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
