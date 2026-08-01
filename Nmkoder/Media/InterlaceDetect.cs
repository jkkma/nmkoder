using Nmkoder.Data;
using Nmkoder.Data.Streams;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Utils;
using System;
using System.Collections.Generic;
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
                long durationSec = Math.Max(0, file.DurationMs / 1000);

                for (int i = 0; i < SampleCount; i++)
                {
                    // Spread across the file rather than taken from the front: an opening title card is
                    // a still image, and a still image has no field difference to find.
                    long at = durationSec > 8 ? durationSec * (i + 1) / (SampleCount + 1) : 0;
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

                    if (durationSec <= 8) // The whole file fits in one sample; a second one would read it again
                        break;
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

                // Two conditions, and the first is the one that matters.
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

                if (consistentOrder && (interlaced > progressive || interlaced * 5 > counted))
                    info.Order = bff > tff ? FieldOrder.BottomFieldFirst : FieldOrder.TopFieldFirst;
                else
                    info.Order = FieldOrder.Progressive;

                // Pulldown repeats a field to carry 24 fps film in a 30 fps stream. It is worth
                // saying out loud because deinterlacing such a source is not what it wants - undoing
                // the pulldown is - but it is not worth refusing over: QTGMC on telecined film still
                // produces something watchable, just at twice the frames it needs.
                info.Telecined = info.Interlaced && repeated > 0 && repeated * 5 > (repeated + notRepeated);

                info.Evidence =
                    info.Interlaced ? $"{dominant} of {counted} sampled frames are combed" :
                    interlaced < 1 ? $"none of {counted} sampled frames are combed" :
                    !consistentOrder ? $"{interlaced} of {counted} sampled frames look combed, but not consistently one way round" :
                    $"only {interlaced} of {counted} sampled frames look combed";
                Logger.Log($"idet on '{file.Name.Trunc(40)}': TFF {tff}, BFF {bff}, progressive {progressive}, " +
                    $"undetermined {undetermined}, repeated fields {repeated}/{repeated + notRepeated} [T = {sw}]", true);
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
