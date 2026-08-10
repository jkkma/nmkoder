using Nmkoder.Data.Codecs;
using Nmkoder.Extensions;
using Nmkoder.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Nmkoder.Data
{
    /// <summary>
    /// The arithmetic behind the CRF ladder, kept apart from the task that runs it so the numbers can
    /// be checked without encoding anything - which is how the sample placement and the extrapolation
    /// below were checked.
    /// </summary>
    public class CrfLadder
    {
        /// <summary>
        /// What a rung is scored with.
        /// <para/>
        /// VMAF and XPSNR are ffmpeg filters. SSIMULACRA2 is not, and cannot be: libvmaf has no such
        /// feature extractor at all - its feature_extractor_list in 3.2.0 runs psnr, adm, vif, motion,
        /// ssim, ms_ssim, ciede, psnr_hvs and cambi, and the string "ssimulacra" is in no file of the
        /// repository or of ffmpeg's, so "libvmaf=feature=name=ssimulacra2" fails the graph on every
        /// build there is. It is scored through VapourSynth's vszip plugin instead - see
        /// <see cref="Nmkoder.Media.Ssimulacra2"/> - which is the same plugin, and the same mechanism,
        /// the AV1AN tab's Target SSIMULACRA2 mode uses. That plugin is bundled on Windows only, so this
        /// metric refuses the run with a plain reason where it cannot compute.
        /// <para/>
        /// The numeric values persist as the saved setting, so append rather than reorder; the dialog
        /// drives its dropdown from an explicit display order and does not rely on enum==index.
        /// </summary>
        public enum Metric { Vmaf, Xpsnr, None, Ssimulacra2 }

        /// <summary> The metrics as the dialog lists them - VMAF first as the default, SSIMULACRA2
        /// beside it, XPSNR as the second opinion, then Nothing. Order here is display order; the enum's
        /// numeric order is the on-disk order and must not move. </summary>
        public static readonly Metric[] MetricOrder = { Metric.Vmaf, Metric.Ssimulacra2, Metric.Xpsnr, Metric.None };

        /// <summary> The metric's label, for the dialog, the results column and the log. </summary>
        public static string MetricName(Metric m)
        {
            switch (m)
            {
                case Metric.Vmaf: return "VMAF";
                case Metric.Ssimulacra2: return "SSIMULACRA2";
                case Metric.Xpsnr: return "XPSNR";
                default: return "Nothing (size only)";
            }
        }

        /// <summary>
        /// The score at or above which a rung reads as "hard to tell from the source", used to pick the
        /// recommended CRF. VMAF 95 and SSIMULACRA2 80 are the app's own anchors - the AV1AN tab's
        /// target-quality defaults - where 80 is "imperceptible side by side". XPSNR has no fixed
        /// ceiling, so no single threshold is honest for it, and it returns 0: the results window draws
        /// no recommendation there, only the table.
        /// </summary>
        public static double GoodScore(Metric m)
        {
            if (m == Metric.Vmaf) return 95;
            if (m == Metric.Ssimulacra2) return 80;
            return 0;
        }

        /// <summary>
        /// One section of the source, and what came out of encoding it.
        /// <para/>
        /// <see cref="Ms"/> is measured off the cut rather than taken from the length that was asked
        /// for, and that is not a nicety: a stream copy cannot begin between keyframes, so the cut runs
        /// from the keyframe at or before the start point and <c>-t</c> is then counted from the start
        /// point itself - the pre-roll is kept on top. Measured against a source with keyframes exactly
        /// every 2s: a 10s section asked for at 20.0s came out 12.08s, at 20.5s 10.58s, at 21.0s
        /// 11.08s. Every number below divides by this, so taking the requested length on trust would
        /// overstate the bitrate by whatever the pre-roll happened to be - 21% in the first of those.
        /// </summary>
        public class Sample
        {
            public int Index;
            public long StartMs;
            /// <summary> The cut's real length, probed after it was written. </summary>
            public long Ms;
            public string Path = "";
        }

        /// <summary> One CRF, pooled over every sample. </summary>
        public class Rung
        {
            public int Crf;
            /// <summary> Every sample encoded at this CRF, in sample order. </summary>
            public List<RungSample> Samples = new List<RungSample>();

            public long Bytes => Samples.Sum(x => x.Bytes);
            public long Ms => Samples.Sum(x => x.Ms);
            public TimeSpan EncodeTime => TimeSpan.FromMilliseconds(Samples.Sum(x => x.EncodeMs));

            /// <summary>
            /// The pooled score, which is the mean over frames rather than over samples - the samples
            /// are not the same length (see <see cref="Sample.Ms"/>), so weighting them equally would
            /// let a short one carry as much of the answer as a long one. At a constant frame rate,
            /// weighting by duration is weighting by frames, which is how libvmaf pools its own.
            /// </summary>
            public double Score
            {
                get
                {
                    var scored = Samples.Where(x => x.Scored && x.Ms > 0).ToList();
                    return scored.Count < 1 ? 0 : scored.Sum(x => x.Score * x.Ms) / scored.Sum(x => x.Ms);
                }
            }

            public bool Scored => Samples.Any(x => x.Scored);

            /// <summary> Video bitrate over the sampled sections, which is the one measurement here
            /// that involves no extrapolation at all. </summary>
            public int Kbps => Ms > 0 ? (int)Math.Round(Bytes * 8d / (Ms / 1000d) / 1000d) : 0;

            public long BytesPerMinute => Ms > 0 ? (long)Math.Round(Bytes / (Ms / 60000d)) : 0;

            /// <summary> What a whole source of this length would come to at this CRF. </summary>
            public long ProjectedBytes(long sourceMs) => Ms > 0 ? (long)Math.Round(Bytes * (sourceMs / (double)Ms)) : 0;
        }

        public class RungSample
        {
            public int SampleIndex;
            public long Bytes;
            public long Ms;
            public long EncodeMs;
            public double Score;
            public bool Scored;
        }

        /// <summary> Everything a finished run has to say, for the results window and the log. </summary>
        public class Result
        {
            public string FileName = "";
            public long SourceMs;
            /// <summary> The source's own size, for saying what a projection is as a share of it.
            /// Captured with the rest of the run rather than read back when the window opens - the
            /// loaded file can be a different one by then, and a percentage of the wrong file is
            /// worse than no percentage. 0 where it could not be read. </summary>
            public long SourceBytes;
            public string EncoderName = "";
            public string Preset = "";
            public string PixelFormat = "";
            public Metric ScoredWith = Metric.None;
            public string VmafModel = "";
            public List<Sample> Samples = new List<Sample>();
            public List<Rung> Rungs = new List<Rung>();

            /// <summary> How much of the source was actually encoded, as a share of it. </summary>
            public double SampledFraction => SourceMs > 0 ? Samples.Sum(x => x.Ms) / (double)SourceMs : 0;
        }

        // The head and tail a film spends on a distributor's logo and its credits, which are the least
        // representative frames in it and the ones a fixed percentage over-weights on a long source:
        // 5% of a two-hour film is six minutes, where the credits are one or two. Capped, so the skip
        // is a proportion on a short clip and a constant on anything feature-length.
        private const double EdgeSkipFraction = 0.05;
        private const long MaxEdgeSkipMs = 60_000;

        /// <summary>
        /// Where the samples go: spread evenly across the source, each one centred in its own share of
        /// it, with the credits at either end left out where there is room to leave them out.
        /// <para/>
        /// Returns fewer sections than asked for when the source cannot hold that many, and one
        /// covering the whole file when it is shorter than a single section - a two-minute clip is a
        /// legitimate thing to point this at, and refusing it would send the user to work out a sample
        /// length by hand for no reason.
        /// </summary>
        public static List<Sample> PlanSamples(long sourceMs, int count, int sectionSecs)
        {
            var samples = new List<Sample>();
            long sectionMs = Math.Max(1000, sectionSecs * 1000L);

            if (sourceMs <= 0)
                return samples;

            if (sourceMs <= sectionMs)
            {
                samples.Add(new Sample { Index = 0, StartMs = 0, Ms = sourceMs });
                return samples;
            }

            count = Math.Max(1, count);

            // Trimming the ends is only worth doing while what is left still holds the sections. A
            // three-minute clip with a 10s skip either end has plenty of room; a 40-second one asked
            // for three 10s sections does not, and there the whole file is the span.
            long skip = (long)Math.Min(sourceMs * EdgeSkipFraction, MaxEdgeSkipMs);
            long spanStart = skip;
            long spanMs = sourceMs - 2 * skip;

            if (spanMs < count * sectionMs)
            {
                spanStart = 0;
                spanMs = sourceMs;
            }

            // And the count itself gives way where even the whole file cannot hold it, rather than
            // producing sections that overlap and count the same frames twice.
            count = (int)Math.Max(1, Math.Min(count, spanMs / sectionMs));
            double sliceMs = spanMs / (double)count;

            for (int i = 0; i < count; i++)
            {
                // Centred in its slice, so the first section does not start on the very first frame
                // and the last does not run off the end.
                long start = (long)Math.Round(spanStart + (i + 0.5) * sliceMs - sectionMs / 2d);
                start = Math.Max(0, Math.Min(start, sourceMs - sectionMs));
                samples.Add(new Sample { Index = i, StartMs = start, Ms = sectionMs });
            }

            return samples;
        }

        /// <summary>
        /// The CRF values to try when the box is empty: the encoder's own default, and a step either
        /// side of it. Which step depends on the scale - one point of an x264 CRF is a bigger move than
        /// one point of an AV1 CRF, the two scales being 0-51 and 0-63 - so it is taken as a share of
        /// the range rather than as a number of points, and comes out as 4 for x264 and x265 and 5 for
        /// the AV1 and VP9 encoders.
        /// </summary>
        internal static int[] DefaultCrfs(IEncoder enc)
        {
            if (enc == null)
                return new[] { 22, 26, 30 };

            int step = Math.Max(1, (int)Math.Round((enc.QMax - enc.QMin) * 0.08));
            int mid = enc.QDefault;
            // Never 0, which every encoder here reads as lossless rather than as a very high quality -
            // a rung that encodes losslessly says nothing about the ones beside it and takes the
            // longest to produce.
            int low = Math.Max(Math.Max(1, enc.QMin), mid - step);
            int high = Math.Min(enc.QMax, mid + step);
            return new[] { low, mid, high }.Distinct().OrderBy(x => x).ToArray();
        }

        /// <summary> The maximum number of rungs a run will encode, so a comma-separated box cannot
        /// turn into an overnight job by accident. </summary>
        public const int MaxRungs = 8;

        /// <summary>
        /// The CRF values a text box is asking for, cleaned up: parsed, deduplicated, sorted and held
        /// inside the encoder's own range. An empty or unreadable box falls back to
        /// <see cref="DefaultCrfs"/> rather than refusing, since the defaults are what most runs want.
        /// </summary>
        internal static int[] ParseCrfs(string text, IEncoder enc)
        {
            var values = (text ?? "").Split(new[] { ',', ';', ' ', '\t', '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => int.TryParse(x.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : -1)
                .Where(x => x >= 0)
                .Select(x => enc == null ? x : x.Clamp(enc.QMin, enc.QMax))
                .Distinct().OrderBy(x => x).Take(MaxRungs).ToArray();

            return values.Length > 0 ? values : DefaultCrfs(enc);
        }

        /// <summary> The values as the box shows them. </summary>
        public static string Format(IEnumerable<int> crfs)
        {
            return string.Join(", ", crfs);
        }

        /// <summary>
        /// A projected size, said as the estimate it is. The uncertainty here is the sampling and not
        /// the arithmetic: half a minute of a two-hour film is half a percent of it, so which half
        /// minute it was moves the answer far more than anything roundable does. Measured for scale,
        /// the container overhead this does not subtract is 0.2-0.9% of a sample's bytes.
        /// </summary>
        public static string DescribeProjection(long bytes)
        {
            return $"~{FormatUtils.Bytes(bytes)}";
        }
    }
}
