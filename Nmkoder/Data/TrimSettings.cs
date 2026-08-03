using System;

namespace Nmkoder.Data
{
    /// <summary>
    /// Trim configuration produced by the trim dialog. Used to live as a nested class inside the
    /// WinForms TrimForm; it is plain data, so it belongs with the rest of the model.
    /// <para/>
    /// The three modes differ in what the user types and in how accurate the start point is, and no
    /// longer in the mechanism. Both time modes and the frame mode come out as a seek and a duration:
    /// the keyframe mode seeks the input, which is instant and lands on the nearest keyframe before the
    /// point; the other two seek the output, which decodes and discards its way there and so stops
    /// exactly where it was asked to.
    /// <para/>
    /// Frame mode used to be the exception, and was wrong three ways for it. It emitted a
    /// "select=gte(n,X)" video filter and "-vframes N", so: the kept frames carried their original
    /// timestamps, leaving the output opening on however many seconds of nothing the trim had skipped;
    /// the audio was not cut at either end, because both of those touch video only; and the frames
    /// being counted were the ones coming *out* of the filter chain, so a rate-doubling deinterlacer
    /// ahead of the select halved the point it landed on. Converting the frame numbers to a time here
    /// settles all three - and it is the same conversion the dialog already does to show them.
    /// </summary>
    public class TrimSettings
    {
        public enum Mode { TimeKeyframe, TimeExact, FrameNumbers }

        public Mode TrimMode { get; set; }

        /// <summary> Trim start time in ms (or as frame number). </summary>
        public long StartTime { get; set; }

        /// <summary> Trim duration in ms (or as frame count). </summary>
        public long Duration { get; set; }

        /// <summary> Trim end time in ms (or as frame number). </summary>
        public long EndTime { get; set; }

        public bool IsUnset { get { return (EndTime == StartTime) || (Duration == 0); } }

        public bool IsFrameMode { get { return TrimMode == Mode.FrameNumbers; } }

        /// <summary> One frame, in seconds, or 0 for a rate that says nothing. </summary>
        private static double FrameSecs(Fraction rate)
        {
            double fps = rate.Denominator > 0 ? rate.ToDouble() : 0d;
            return fps > 0d ? 1d / fps : 0d;
        }

        /// <summary> The start point in milliseconds, whichever unit it was configured in. </summary>
        public long GetStartMs(Fraction rate)
        {
            return IsFrameMode ? FramesToMs(StartTime, rate) : StartTime;
        }

        /// <summary> The end point in milliseconds, whichever unit it was configured in. </summary>
        public long GetEndMs(Fraction rate)
        {
            return IsFrameMode ? FramesToMs(EndTime, rate) : EndTime;
        }

        /// <summary> The length in milliseconds, whichever unit it was configured in. </summary>
        public long GetDurationMs(Fraction rate)
        {
            return IsFrameMode ? FramesToMs(Duration, rate) : Duration;
        }

        private static long FramesToMs(long frames, Fraction rate)
        {
            double fps = rate.Denominator > 0 ? rate.ToDouble() : 0d;
            return fps > 0d ? (long)Math.Round(frames * 1000d / fps) : 0;
        }

        /// <summary>
        /// What goes in front of the '-i', which is only ever the keyframe mode's seek: that one is
        /// fast because it is the demuxer skipping ahead, and inexact for the same reason.
        /// </summary>
        public string GetInputArgs(Fraction rate)
        {
            if (IsUnset || TrimMode != Mode.TimeKeyframe)
                return "";

            return $"-ss {GetTimeString(TimeSpan.FromMilliseconds(StartTime))}";
        }

        /// <summary>
        /// What goes after it: the duration in every mode, and the seek too in the two exact ones. An
        /// output-side seek decodes from the start and throws frames away until it arrives, which is
        /// what makes it land on the frame asked for rather than on a keyframe.
        /// </summary>
        public string GetOutputArgs(Fraction rate)
        {
            if (IsUnset)
                return "";

            if (!IsFrameMode)
            {
                string seek = TrimMode == Mode.TimeExact ? $"-ss {GetTimeString(TimeSpan.FromMilliseconds(StartTime))} " : "";
                return $"{seek}-t {GetTimeString(TimeSpan.FromMilliseconds(Duration))}";
            }

            double frame = FrameSecs(rate);

            if (frame <= 0d) // No frame rate to convert with - nothing here can be stated in time
                return "";

            // Half a frame back from where frame X sits, because a seek keeps what is at or after the
            // timestamp it is given and X/rate is a place floating point can land either side of. Half
            // a frame earlier is unambiguously past X-1 and short of X, so the first frame kept is the
            // one that was asked for whichever way the arithmetic rounds. The duration then needs no
            // such margin: it spans from inside the gap before X to inside the gap before X+N.
            TimeSpan start = TimeSpan.FromSeconds(Math.Max(0d, StartTime * frame - frame / 2d));
            TimeSpan duration = TimeSpan.FromSeconds(Duration * frame);

            // The seek and the duration are the whole mechanism, and no "-frames:v" goes with them.
            // Pinning the count looks free and is not: -frames:v counts frames *leaving the filter
            // chain*, so anything that raises the count - a bob deinterlacer, which a trim forces by
            // ruling QTGMC out, or a Frame Rate above the source's - hits the limit halfway and cuts
            // the section short. Measured against the bundled ffmpeg on a 29.97 source: frames 240-480
            // through bwdif=send_field came out as 240 frames covering 4.0s of an 8.0s section, with
            // the audio ending there too, where without the count it is the 480 frames and full 8.0s
            // that were asked for.
            //
            // What the seek and duration alone do NOT give is the last frame of a section that starts
            // mid-file. Measured the same way, and frame-exact at the start - the first frame matches
            // a select=gte(n,X) reference bit for bit - but N frames asked for come out as N-1 for any
            // X greater than zero, because both ends of the window land on a frame boundary and
            // ffmpeg's own rounding drops the one sitting on the far edge. Adding a quarter frame to
            // the far end, measured from the seek actually used, fixes it for every section tried but
            // one, which is not a good enough reason to put new arithmetic into a frame-exact path -
            // so the shortfall stands, documented, rather than being traded for an unproven formula.
            return $"-ss {GetTimeString(start)} -t {GetTimeString(duration)}";
        }

        /// <summary>
        /// "HH:MM:SS" or "HH:MM:SS.mmm", with the hours counted in full rather than taken from the
        /// TimeSpan's own hours *component* - that one runs 0-23 and rolls over into Days, so a 25 hour
        /// point came out as "01:00:00" and a 24 hour one as "00:00:00". ffmpeg reads any number of
        /// hour digits, so nothing downstream needed the wrap.
        /// </summary>
        public static string GetTimeString(TimeSpan ts)
        {
            bool ms = ts.Milliseconds != 0;
            long hours = (long)Math.Floor(Math.Abs(ts.TotalHours));
            return $"{hours.ToString().PadLeft(2, '0')}:{Math.Abs(ts.Minutes).ToString().PadLeft(2, '0')}:{Math.Abs(ts.Seconds).ToString().PadLeft(2, '0')}" +
                $"{(ms ? $".{Math.Abs(ts.Milliseconds).ToString().PadLeft(3, '0')}" : "")}";
        }

        public override string ToString()
        {
            string mode = TrimMode.ToString().Replace("TimeKeyframe", "Time").Replace("TimeExact", "Time").Replace("FrameNumbers", "Frames");

            if (TrimMode != Mode.FrameNumbers)
                return $"{mode} - From {GetTimeString(TimeSpan.FromMilliseconds(StartTime))} to {GetTimeString(TimeSpan.FromMilliseconds(EndTime))} ({GetTimeString(TimeSpan.FromMilliseconds(Duration))})";

            return $"{mode} - From #{StartTime} to #{EndTime} ({Duration} Frames)";
        }
    }
}
