using Nmkoder.Data;
using Nmkoder.Data.Streams;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace Nmkoder.Media
{
    /// <summary>
    /// Works out whether a file holds interlaced video, which is what decides whether the Deinterlace
    /// setting's Automatic mode does anything.
    /// <para/>
    /// Two sources of truth, in that order. The container's own field-order flag is free - ffprobe has
    /// already reported it by the time a file is loaded - and for the formats this matters most for it
    /// is reliable: an MPEG-2 capture off a Hi8 or VHS tape says "tt" in the sequence header, DV says
    /// "bb", and neither is guessing. A flag that says nothing at all is the case worth spending time
    /// on, and there frames are decoded and measured with ffmpeg's idet filter.
    /// <para/>
    /// A flag that says "progressive" is believed rather than checked. It is wrong occasionally - a
    /// badly remuxed capture can carry it - but checking every file that claims to be progressive
    /// would put a multi-second scan in front of loading any modern video, to catch a case the user
    /// can settle in one click by picking QTGMC outright.
    /// </summary>
    class InterlaceDetect
    {
        /// <summary> How many points across the file to sample, and how many frames at each. Enough
        /// that an interlaced source cannot hide behind a static opening, cheap enough to sit in the
        /// path of loading a file. </summary>
        private const int SampleCount = 3;
        private const int FramesPerSample = 240;

        /// <summary>
        /// How lopsided the two alternating field gaps have to be before a combed-looking file is
        /// called progressive after all - see <see cref="MeasureFieldGaps"/>.
        /// <para/>
        /// Measured rather than picked. Genuinely interlaced sources come out at 1.00-1.02, a file
        /// weaved one way for half its length and the other way for the rest at 1.88, and a genuine
        /// source measured at the wrong parity - the one way this could overturn a correct answer -
        /// at 2.41. Progressive sources that idet calls combed sit at 6.9 and up. Four is about the
        /// midpoint of what is left between 2.41 and 6.9, and the error it guards against is not
        /// symmetric: overturning a real tape means Automatic silently leaves the combing in, where
        /// letting a false positive through means a progressive file gets softened, so the number
        /// leans towards keeping whatever the frame counts decided.
        /// </summary>
        private const double UnevenFieldGapRatio = 4;

        /// <summary>
        /// How much field-to-field difference a sample needs before that ratio means anything. A
        /// still frame differs from the one before it by nothing at all, so a sample with no motion
        /// in it is one zero divided by another - and a tape that happens to be static wherever it
        /// was sampled keeps whatever the frame counts made of it.
        /// </summary>
        private const double MinFieldMotion = 0.5;

        /// <summary> ffprobe's field_order values. "tb"/"bt" describe a coded/displayed order
        /// mismatch, which is still interlaced, and still tells the deinterlacer which field to
        /// show first - that is the displayed order, which is the first letter. </summary>
        public static FieldOrder ParseFfprobeFieldOrder(string value)
        {
            switch ((value ?? "").Trim().ToLower())
            {
                case "tt":
                case "tb": return FieldOrder.TopFieldFirst;
                case "bb":
                case "bt": return FieldOrder.BottomFieldFirst;
                case "progressive": return FieldOrder.Progressive;
                default: return FieldOrder.Unknown;
            }
        }

        /// <summary>
        /// The file's scan type, measured once and kept on the file. Safe to call repeatedly and from
        /// anywhere - a batch asks for it again per file, and the loaded file has usually been asked
        /// already by the time an encode starts.
        /// </summary>
        public static async Task<InterlaceInfo> GetAsync(MediaFile file, bool quiet = false)
        {
            if (file == null)
                return new InterlaceInfo();

            if (file.Interlacing != null)
                return file.Interlacing;

            InterlaceInfo info = await Analyze(file, quiet);
            file.Interlacing = info;
            return info;
        }

        private static async Task<InterlaceInfo> Analyze(MediaFile file, bool quiet)
        {
            VideoStream vs = file.VideoStreams.FirstOrDefault();

            if (vs == null)
                return new InterlaceInfo { Evidence = "the file has no video track" };

            // An image sequence is a folder of stills reaching ffmpeg through a generated concat
            // list. There are no fields in it, and scanning one would only be a slow way to say so.
            if (file.IsDirectory)
                return new InterlaceInfo { Order = FieldOrder.Progressive, Evidence = "image sequences have no fields" };

            if (vs.FieldOrder == FieldOrder.TopFieldFirst || vs.FieldOrder == FieldOrder.BottomFieldFirst)
                return new InterlaceInfo { Order = vs.FieldOrder, Evidence = "the file says so itself" };

            if (vs.FieldOrder == FieldOrder.Progressive)
                return new InterlaceInfo { Order = FieldOrder.Progressive, Evidence = "the file says so itself" };

            return await ScanFrames(file, quiet);
        }

        /// <summary>
        /// Decodes a few stretches of the file through ffmpeg's idet filter and reads its verdict off
        /// the summary it prints when it is done. idet counts every frame it saw into one of four
        /// buckets, and the "Multi frame detection" line is the one worth reading - it decides using
        /// the frames either side rather than a frame on its own, which is what stops a still shot
        /// from reading as progressive.
        /// </summary>
        private static async Task<InterlaceInfo> ScanFrames(MediaFile file, bool quiet)
        {
            var info = new InterlaceInfo { Scanned = true };

            try
            {
                if (!quiet)
                    Logger.Log($"'{file.Name.Trunc(40)}' does not say whether it is interlaced - checking a few hundred frames...", false);

                NmkdStopwatch sw = new NmkdStopwatch();
                long tff = 0, bff = 0, progressive = 0, undetermined = 0, repeated = 0, notRepeated = 0;
                List<long> offsets = GetSampleOffsets(file);

                foreach (long at in offsets)
                {
                    string output = await GetVideoInfo.GetFfmpegOutputAsync(file.ImportPath, $"-ss {at}",
                        $"-an -sn -dn -vf idet -frames:v {FramesPerSample} -f null -", "", false, OS.NmkoderProcess.ProcessType.Secondary);

                    foreach (string line in output.SplitIntoLines().Where(x => x.Contains("Multi frame detection:")))
                    {
                        tff += ReadCount(line, "TFF:");
                        bff += ReadCount(line, "BFF:");
                        progressive += ReadCount(line, "Progressive:");
                        undetermined += ReadCount(line, "Undetermined:");
                    }

                    foreach (string line in output.SplitIntoLines().Where(x => x.Contains("Repeated Fields:")))
                    {
                        notRepeated += ReadCount(line, "Neither:");
                        repeated += ReadCount(line, "Top:") + ReadCount(line, "Bottom:");
                    }
                }

                long interlaced = tff + bff;
                long dominant = Math.Max(tff, bff);
                long counted = interlaced + progressive + undetermined;

                if (counted < 1)
                {
                    info.Evidence = "nothing could be decoded to check";
                    Logger.Log($"idet returned no counts for '{file.Name.Trunc(40)}' - treating it as progressive.", true);
                    return info;
                }

                // Three conditions, and the first two are the ones the frame counts can answer.
                //
                // Interlaced video has *one* field order, for the whole file - so a real interlaced
                // source puts essentially every combed frame in the same bucket. idet's false
                // positives do not: fine horizontal detail looks like combing either way round, so
                // the frames land in TFF and BFF in roughly equal numbers. Measured on a 720x480
                // synthetic pattern with no fields in it at all, idet reported TFF 79 / BFF 96 /
                // progressive 27 - which "more combed frames than progressive ones" alone would have
                // called interlaced, and which the three-quarters rule below rejects outright. The
                // genuinely interlaced test clips scored 202/0 and 0/202.
                //
                // The second is a volume bar, and it is low because idet cannot see combing in a
                // frame that does not move: a static shot reads as progressive or undetermined
                // whatever the source is, so a tape with quiet stretches scores well under half.
                bool consistentOrder = dominant > 0 && dominant * 4 >= interlaced * 3;
                bool combed = consistentOrder && (interlaced > progressive || interlaced * 5 > counted);
                double gapRatio = -1;

                // Everything above measures how combed the frames look, and a vertical pan over fine
                // detail looks exactly that combed - the picture shifts by a fraction of a line per
                // frame, which frame by frame is indistinguishable from two woven fields. The rule
                // above does not catch it either: a pan holds one direction, so the false comb comes
                // out consistently one way round, which is the very thing that rule takes as proof.
                // MeasureFieldGaps asks the one question that separates them.
                //
                // Only asked where the answer would otherwise be "interlaced", and it can only ever
                // overturn that: a file it cannot measure keeps the verdict the frame counts gave.
                if (combed)
                {
                    gapRatio = await MeasureFieldGaps(file, bff <= tff, offsets);

                    if (gapRatio >= UnevenFieldGapRatio)
                        combed = false;
                }

                info.Order = combed ? (bff > tff ? FieldOrder.BottomFieldFirst : FieldOrder.TopFieldFirst) : FieldOrder.Progressive;

                // Pulldown repeats a field to carry 24 fps film in a 30 fps stream. It is worth
                // saying out loud because deinterlacing such a source is not what it wants - undoing
                // the pulldown is - but it is not worth refusing over: QTGMC on telecined film still
                // produces something watchable, just at twice the frames it needs.
                info.Telecined = info.Interlaced && repeated > 0 && repeated * 5 > (repeated + notRepeated);

                info.Evidence =
                    info.Interlaced ? $"{dominant} of {counted} sampled frames are combed" :
                    gapRatio >= UnevenFieldGapRatio ? $"{dominant} of {counted} sampled frames look combed, but its fields are not evenly " +
                        $"spaced in time - that is vertical motion over fine detail rather than interlacing" :
                    interlaced < 1 ? $"none of {counted} sampled frames are combed" :
                    !consistentOrder ? $"{interlaced} of {counted} sampled frames look combed, but not consistently one way round" :
                    $"only {interlaced} of {counted} sampled frames look combed";
                Logger.Log($"idet on '{file.Name.Trunc(40)}': TFF {tff}, BFF {bff}, progressive {progressive}, " +
                    $"undetermined {undetermined}, repeated fields {repeated}/{repeated + notRepeated}" +
                    $"{(gapRatio < 0 ? "" : $", field gap ratio {gapRatio.ToString("0.00", CultureInfo.InvariantCulture)}")} [T = {sw}]", true);
            }
            catch (Exception e)
            {
                // Not knowing is reported as progressive: this only ever gates Automatic, and quietly
                // deinterlacing a file nothing could measure would be the worse of the two mistakes.
                info.Order = FieldOrder.Unknown;
                info.Evidence = $"the check failed ({e.Message})";
                Logger.Log($"Interlace detection failed for '{file.Name}': {e.Message}", true);
            }

            return info;
        }

        /// <summary>
        /// Where to sample. Spread across the file rather than taken from the front: an opening title
        /// card is a still image, and a still image has no field difference to find - measured on a
        /// capture whose first 25 seconds are a caption, where reading only the front sees 245
        /// undetermined frames and nothing else, and would leave a genuinely interlaced tape alone.
        /// A file too short to spread over is read once from the start, since a second sample would
        /// only be the same frames again.
        /// </summary>
        private static List<long> GetSampleOffsets(MediaFile file)
        {
            long durationSec = Math.Max(0, file.DurationMs / 1000);

            if (durationSec <= 8)
                return new List<long> { 0 };

            return Enumerable.Range(1, SampleCount).Select(i => durationSec * i / (SampleCount + 1)).ToList();
        }

        /// <summary>
        /// How evenly this file's fields are spaced in time, as the ratio between the larger and the
        /// smaller of the two gaps that alternate down a field stream - or -1 where there was not
        /// enough motion to tell, which is not an answer and must not be read as one.
        /// <para/>
        /// This is the one question here that a vertical pan answers differently from a tape. Split
        /// every frame into its two fields and measure each field against the field before it. In
        /// interlaced video every one of those gaps is half a frame of time, so they all come out
        /// about the same size. In progressive video the two fields of a frame are the *same*
        /// instant - they differ only by the one line of vertical offset between them - while the
        /// next pair straddles a frame boundary and carries all of the motion, so the gaps alternate
        /// small, large, small, large. Combing is not what is being measured, which is the point:
        /// fine horizontal detail fools every measure of combing there is, and none of it changes
        /// when the two fields of a frame turn out to have been shot at the same moment.
        /// <para/>
        /// Which of the two alternating gaps is the within-frame one depends on the field parity, and
        /// reading that backwards would invert the answer - so it is never asked. The ratio between
        /// the larger and the smaller says what is needed and says it the same way round either way.
        /// </summary>
        private static async Task<double> MeasureFieldGaps(MediaFile file, bool topFieldFirst, List<long> offsets)
        {
            try
            {
                // The parity idet just reported, because separatefields hands over the two fields in
                // the order the frame claims: told the wrong one, it pairs each field with the
                // neighbour on the wrong side, and an evenly spaced source measures as an uneven one
                // (2.41 against the 1.0 it should be - which is why the threshold sits above that).
                string parity = topFieldFirst ? "tff" : "bff";
                double[] sums = new double[2];
                int[] counts = new int[2];

                foreach (long at in offsets)
                {
                    // Narrowed first, and only horizontally - every line the fields are made of
                    // survives untouched, so the thing being measured is unaffected, while the
                    // pixels signalstats has to average drop by whatever the source's width is over
                    // 256. It comes out cheaper *and* cleaner: averaging across the width takes the
                    // horizontal noise out of the difference, which on the files measured here moved
                    // progressive sources from 4.9 to 7.9 and left interlaced ones where they were.
                    string output = await GetVideoInfo.GetFfmpegOutputAsync(file.ImportPath, $"-ss {at}",
                        $"-an -sn -dn -vf scale=256:ih,setfield={parity},separatefields,signalstats," +
                        $"metadata=print:key=lavfi.signalstats.YDIF -frames:v {FramesPerSample * 2} -f null -",
                        "", false, OS.NmkoderProcess.ProcessType.Secondary);

                    List<double> gaps = ReadFieldGaps(output);

                    // From 1, because the first field of a sample has nothing before it to be measured
                    // against and whatever was printed for it describes nothing.
                    for (int i = 1; i < gaps.Count; i++)
                    {
                        sums[i % 2] += gaps[i];
                        counts[i % 2]++;
                    }
                }

                if (counts[0] < 1 || counts[1] < 1)
                    return -1;

                double a = sums[0] / counts[0];
                double b = sums[1] / counts[1];
                double larger = Math.Max(a, b);
                double smaller = Math.Min(a, b);

                if (larger < MinFieldMotion)
                    return -1;

                // Identical fields within a frame is progressive video and nothing else - a tape's
                // two fields are half a frame apart and never match to the last digit.
                return smaller < 0.0001 ? double.MaxValue : larger / smaller;
            }
            catch (Exception e)
            {
                Logger.Log($"Could not measure the field spacing of '{file.Name}': {e.Message}", true);
                return -1;
            }
        }

        /// <summary> The YDIF values metadata=print wrote, in order. signalstats measures it as the
        /// mean absolute difference between a frame and the one before it, which down a stream of
        /// separated fields is the gap between one field and the field before it. </summary>
        private static List<double> ReadFieldGaps(string output)
        {
            var values = new List<double>();
            const string key = "signalstats.YDIF=";

            foreach (string line in output.SplitIntoLines())
            {
                int at = line.IndexOf(key, StringComparison.OrdinalIgnoreCase);

                if (at < 0)
                    continue;

                // Invariant, because ffmpeg writes "1.774" wherever it runs and a machine whose own
                // decimal separator is a comma would otherwise read that as one thousand seven hundred.
                if (double.TryParse(line.Substring(at + key.Length).Trim(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double value))
                    values.Add(value);
            }

            return values;
        }

        /// <summary> One of idet's "Label: 1234" counters out of a summary line. </summary>
        private static long ReadCount(string line, string label)
        {
            int at = line.IndexOf(label, StringComparison.OrdinalIgnoreCase);

            if (at < 0)
                return 0;

            string rest = line.Substring(at + label.Length).TrimStart();
            string digits = new string(rest.TakeWhile(char.IsDigit).ToArray());
            return digits.IsEmpty() ? 0 : digits.GetInt();
        }
    }
}
