using Nmkoder.Extensions;
using System;
using System.Collections.Generic;

namespace Nmkoder.Data
{
    /// <summary>
    /// How much memory an av1an run will ask of the machine, and whether it fits.
    /// <para/>
    /// This exists because <b>running out does not report itself as running out</b>. av1an holds three
    /// processes per worker - the source pipe, the ffmpeg that applies this tab's <c>-f</c> chain, and
    /// the encoder - and when the machine cannot feed them all, the OS kills one of the first two. The
    /// encoder then reaches the end of a short stream, finishes normally and exits <b>0</b>, and av1an
    /// counts its output and fails the chunk over the difference:
    /// <code>
    /// WARN encode_chunk: Encoder failed (on chunk 1):
    /// encoder crashed: exit code: 0
    /// stdout:
    ///         FRAME MISMATCH: chunk 1: 47/239 (actual/expected frames)
    /// source pipe stderr:
    ///         Error: fwrite() call failed when writing frame: 49, plane: 0, errno: 32
    /// </code>
    /// Not one word of which says "memory" - errno 32 is EPIPE, the *downstream* process having gone.
    /// The chunk is then retried to <c>--max-tries</c> and the run eventually gives up, hours in, having
    /// encoded each doomed chunk several times over. Reproduced here rather than inferred: ten of these
    /// pipelines run at once on a host with less RAM than they want produced three short chunks, with
    /// the kernel logging <c>Out of memory: Killed process (ffmpeg)</c> and the encoder beside it
    /// exiting 0 - the report's exact shape.
    /// <para/>
    /// <b>Every constant below was measured</b>, by running the real process at three frame sizes and
    /// fitting a line through the peak RSS, rather than reasoned out of buffer counts. They are all for
    /// <b>10-bit</b>, which is what this tab's HDR and AV1 work is; an 8-bit encode is lighter, so the
    /// estimate errs high there. The fits were close to straight - SVT-AV1 came out at 672, 678 and 623
    /// MB per megapixel at 720p, 1080p and 1440p - so a base plus a slope is the whole model, and a
    /// second digit of precision would be inventing one.
    /// </summary>
    static class Av1anMemory // Internal, as CodecUtils is - Av1anCodec is in the signatures below
    {
        /// <summary>
        /// Peak RSS per megapixel of the frame the <i>encoder is handed</i> - which is the resize's
        /// output and not the file's own size, and why <see cref="Av1anFrame.Encoded"/> is what gets
        /// passed in. Measured at 720p and 1080p, 10-bit, two threads, at the preset this tab opens each
        /// encoder on.
        /// <para/>
        /// The spread is the point: SVT-AV1 wants two to three times what the others do, which is the
        /// same fact <see cref="UI.Tasks.Av1anUi.ApplyWorkerCount"/> already acts on by giving it two
        /// workers fewer, now with a number behind it. aomenc is not in the list because it could not be
        /// measured the same way - see <see cref="DefaultMbPerMegapixel"/>.
        /// </summary>
        private static readonly Dictionary<CodecUtils.Av1anCodec, int> encoderMbPerMegapixel =
            new Dictionary<CodecUtils.Av1anCodec, int>
        {
            { CodecUtils.Av1anCodec.SvtAv1, 605 }, // 539 without grain synthesis; the denoiser adds ~12%
            { CodecUtils.Av1anCodec.X264, 397 },
            { CodecUtils.Av1anCodec.X265, 275 },
            { CodecUtils.Av1anCodec.Vpx, 194 },
        };

        /// <summary> What an encoder that was not measured is assumed to want. Deliberately the middle
        /// of the measured spread rather than the top of it: this figure only decides whether a warning
        /// appears, and one that cried wolf on every aomenc run would be turned off by whoever met it
        /// twice. </summary>
        private const int DefaultMbPerMegapixel = 400;

        /// <summary> The part of an encoder's footprint that is not the frames - its own code, tables
        /// and thread stacks. Small next to the slope at any real resolution, and carried anyway so the
        /// estimate does not read as zero for a tiny frame. </summary>
        private const int EncoderBaseMb = 90;

        /// <summary> The process that decodes the source and pipes y4m: vspipe for the VapourSynth chunk
        /// methods, ffmpeg for the rest. Measured decoding 10-bit HEVC, which is the heavy case and the
        /// one this warning is for. VapourSynth keeps a frame cache of its own on top of this, so the
        /// figure is a floor for the chunk methods that use it. </summary>
        private const int SourceBaseMb = 60;
        private const int SourceMbPerMegapixel = 51;

        /// <summary> The ffmpeg that applies the <c>-f</c> chain, per megapixel of the frame going
        /// <i>into</i> it - the source's, since every filter on this tab that shrinks the frame runs
        /// inside this same process. </summary>
        private const int FilterBaseMb = 60;
        private const int FilterMbPerMegapixel = 13;

        /// <summary>
        /// The same, where the chain carries a 32-bit float step. Four times the slope, and it is not
        /// subtle: a tone map converts to <c>gbrpf32le</c>, which is 12 bytes a pixel against a 10-bit
        /// 4:2:0 frame's 3, and it does so at the source's size because the roll-off belongs above the
        /// downscale. Measured on a 3840x2076 source: 508 MB against 160 MB for the same chain with the
        /// tone map taken out.
        /// </summary>
        private const int FloatFilterMbPerMegapixel = 55;

        /// <summary> Left to the OS, the app itself and whatever else the user has open. A fifth of the
        /// machine or 2 GB, whichever is more: the fraction is what matters on a large machine and the
        /// floor is what stops a small one booking all of itself. </summary>
        private static long GetReservedMb(long totalMb)
        {
            return Math.Max(2048, totalMb / 5);
        }

        /// <summary>
        /// How much room over the estimate a run has to have before it is called safe.
        /// <para/>
        /// It is here because <b>the estimate is a floor, not a bound</b>, and the report this was
        /// written for is exactly the case that shows why: eleven workers on a 4K HDR source come to
        /// 24.8 GB against the 25.6 GB a 32 GB machine would be credited with, so a bare comparison
        /// calls that fine - and it is not fine, it is the run that failed. Three things the figures
        /// cannot see all push the same way: the VapourSynth chunk methods hold a frame cache above the
        /// decoder measured in <see cref="SourceMbPerMegapixel"/>, aomenc is not measured at all, and
        /// what else the user has open is not this app's to know. A run landing inside a rounding of the
        /// ceiling is one to mention, because the arithmetic is not precise enough to tell it from one
        /// landing just over.
        /// </summary>
        private const double RequiredHeadroom = 1.25;

        /// <summary> The machine's memory, or 0 where it cannot be read - which is not a failure worth
        /// reporting, only a reason to say nothing. <c>TotalAvailableMemoryBytes</c> is the physical
        /// memory, or the container's limit where there is one, and needs no per-platform call. </summary>
        public static long GetTotalMb()
        {
            try
            {
                return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary> A float step is detected from the built chain rather than from the tone-map row, so
        /// a custom filter that converts to float counts too. Every float pixel format ffmpeg names ends
        /// in <c>f32</c>. </summary>
        public static bool ChainConvertsToFloat(string vf)
        {
            return (vf ?? "").Contains("f32");
        }

        /// <summary> What one worker wants, in MB: its encoder, the ffmpeg applying the filter chain
        /// where there is one, and the process decoding the source for it. </summary>
        public static int PerWorkerMb(CodecUtils.Av1anCodec codec, Size encoded, Size source, string vf)
        {
            double encodedMp = Math.Max(0, (double)encoded.Width * encoded.Height) / 1_000_000d;
            double sourceMp = Math.Max(0, (double)source.Width * source.Height) / 1_000_000d;

            if (!encoderMbPerMegapixel.TryGetValue(codec, out int perMp))
                perMp = DefaultMbPerMegapixel;

            double total = EncoderBaseMb + encodedMp * perMp;
            total += SourceBaseMb + sourceMp * SourceMbPerMegapixel;

            if ((vf ?? "").IsNotEmpty())
                total += FilterBaseMb + sourceMp * (ChainConvertsToFloat(vf) ? FloatFilterMbPerMegapixel : FilterMbPerMegapixel);

            return (int)Math.Round(total);
        }

        /// <summary> The most workers that fit with <see cref="RequiredHeadroom"/> to spare, at least 1 -
        /// a machine that cannot hold even one worker is not something this can fix by suggesting
        /// zero. </summary>
        public static int GetFittingWorkers(int perWorkerMb, long usableMb)
        {
            return perWorkerMb <= 0 ? 1 : Math.Max(1, (int)(usableMb / (perWorkerMb * RequiredHeadroom)));
        }

        /// <summary>
        /// The warning, or "" where the run fits or the machine's memory could not be read.
        /// <para/>
        /// A warning and not a refusal, unlike the crop and frame-size checks on this tab, because those
        /// state a certainty and this states an estimate: the constants are measured but the machine's
        /// spare memory is not this app's to know, and a run stopped by a wrong guess costs more than the
        /// one it would have saved. It says the number to change instead, which is the whole point of
        /// saying anything before the hours are spent rather than after.
        /// </summary>
        /// <param name="machineMb"> The machine's memory, or 0 to read it. Only ever passed to check the
        /// arithmetic against machine sizes the checking machine does not have. </param>
        public static string GetProblem(int workers, CodecUtils.Av1anCodec codec, Size encoded, Size source, string vf, long machineMb = 0)
        {
            long totalMb = machineMb > 0 ? machineMb : GetTotalMb();

            if (totalMb <= 0 || workers < 1 || encoded.IsEmpty)
                return "";

            long usableMb = Math.Max(0, totalMb - GetReservedMb(totalMb));
            int perWorkerMb = PerWorkerMb(codec, encoded, source, vf);
            long wantedMb = (long)perWorkerMb * workers;

            if (wantedMb * RequiredHeadroom <= usableMb)
                return "";

            int fits = GetFittingWorkers(perWorkerMb, usableMb);
            // "More than" and "nearly all of" are the same warning, because the estimate cannot tell
            // them apart - see RequiredHeadroom. Saying which it is anyway, rather than rounding both
            // into the louder one, is what keeps the sentence honest for whoever checks the arithmetic.
            string against = wantedMb > usableMb
                ? $"more than the {Gb(usableMb)} this machine is likely to have spare of its {Gb(totalMb)}"
                : $"nearly all of the {Gb(usableMb)} this machine is likely to have spare of its {Gb(totalMb)}";

            return $"Warning: {workers} workers on this file will want roughly {Gb(wantedMb)} of memory - about " +
                $"{Gb(perWorkerMb)} each for the {CodecUtils.GetCodec(codec).FriendlyName} instance, the process decoding " +
                $"{source} for it{((vf ?? "").IsNotEmpty() ? $", and the ffmpeg applying this tab's filters{(ChainConvertsToFloat(vf) ? " (which convert to 32-bit float at the source's size)" : "")}" : "")} - " +
                $"which is {against}. av1an does not report running out as running out: the process feeding a worker is " +
                $"killed, its encoder finishes early on a short stream and exits 0, and the chunk fails as a frame " +
                $"mismatch and is retried until the run gives up. {fits} worker{(fits == 1 ? "" : "s")} would fit - the " +
                $"Workers box is on the Av1an Options tab. This is an estimate, so it is worth watching rather than obeying.";
        }

        /// <summary> One decimal of GB, which is the precision the constants behind it support. </summary>
        private static string Gb(long mb)
        {
            return $"{mb / 1024d:0.0} GB";
        }
    }
}
