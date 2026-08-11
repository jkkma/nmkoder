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
        public string GetOutputArgs(Fraction rate, bool chainKeepsFrameCount = true)
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
            // one that was asked for whichever way the arithmetic rounds.
            //
            // The far end gets the same half frame, and that one is not symmetry for its own sake: a
            // window ending exactly on the last wanted frame loses it. Measured against the bundled
            // ffmpeg, a section of three frames or more starting anywhere but frame 0 came out one
            // frame short - and no arrangement of the two numbers fixed that on its own. Ending half a
            // frame late instead, and letting the count below do the cutting, was frame-for-frame
            // identical to a select=gte(n,X) reference across every section tried: 1 to 598 frames,
            // from the start of the file, the middle and the last frame.
            TimeSpan start = TimeSpan.FromSeconds(Math.Max(0d, StartTime * frame - frame / 2d));
            TimeSpan duration = TimeSpan.FromSeconds((Duration + 0.5d) * frame);

            // The count is what makes the section exactly N frames, and the window above is deliberately
            // half a frame too long so that it never has to be. It can only go out over a chain that
            // hands on as many frames as it took, though: -frames:v counts frames *leaving* the chain,
            // so a bob deinterlacer - which a trim forces, by ruling QTGMC out - or a Frame Rate above
            // the source's hits the limit halfway through the section and cuts it there. Measured on a
            // 29.97 source, frames 240-480 through bwdif=send_field came out as 240 frames covering
            // 4.0s of an 8.0s section, with the audio ending there too.
            //
            // Over such a chain the window is the whole mechanism, and being half a frame long is the
            // right way round to be wrong: the section can carry an extra frame rather than lose the
            // last one, and its count was never going to be N anyway - a bob emits two frames for each
            // one it was given, which is what asking for N *source* frames means there.
            string count = chainKeepsFrameCount ? $" -frames:v {Duration}" : "";
            return $"-ss {GetTimeString(start)} -t {GetTimeString(duration)}{count}";
        }

        /// <summary>
        /// The seek for a mux whose video went down an encoder pipe and arrives already cut - in front
        /// of every *original* input, and never in front of the encoded one, which starts at zero.
        /// <para/>
        /// All three modes become an input-side seek here, where <see cref="GetOutputArgs"/> puts the
        /// two exact ones on the output side. An output-side seek discards everything before its
        /// timestamp from *every* stream, and in this command one of the streams is the encoded video:
        /// it has been cut once already, so seeking the output would take the section's own length off
        /// the front of it a second time. An input seek reaches only the file it is written in front
        /// of, and for the streams that are decoded - which re-encoded audio is - ffmpeg's default
        /// accurate_seek discards up to the exact point, so the audio still starts where the output
        /// seek started it. A copied stream lands on the packet boundary by the seek point instead,
        /// which is the same granularity the old command gave it.
        /// </summary>
        public string GetMuxInputArgs(Fraction rate)
        {
            if (IsUnset)
                return "";

            if (!IsFrameMode)
                return $"-ss {GetTimeString(TimeSpan.FromMilliseconds(StartTime))}";

            double frame = FrameSecs(rate);

            if (frame <= 0d)
                return "";

            // The same half-frame-early window GetOutputArgs opens, spelled with the same arithmetic
            // so the two cannot round to different milliseconds - the audio has to start exactly where
            // the encoded video's first frame sits.
            return $"-ss {GetTimeString(TimeSpan.FromSeconds(Math.Max(0d, StartTime * frame - frame / 2d)))}";
        }

        /// <summary>
        /// The other half of the mux trim: the duration alone, on the output side. No seek - the
        /// inputs were seeked individually - and no -frames:v, the encoded video having been cut to
        /// its count in the pipe already. Frame mode keeps its half-frame-long window, which is the
        /// same audio overhang the single-command trim always had.
        /// </summary>
        public string GetMuxOutputArgs(Fraction rate)
        {
            if (IsUnset)
                return "";

            if (!IsFrameMode)
                return $"-t {GetTimeString(TimeSpan.FromMilliseconds(Duration))}";

            double frame = FrameSecs(rate);

            if (frame <= 0d)
                return "";

            return $"-t {GetTimeString(TimeSpan.FromSeconds((Duration + 0.5d) * frame))}";
        }

        /// <summary>
        /// The same section, stated as the keyframe mode - the input-side seek, which is the only one a
        /// stream copy can carry out. The two exact modes seek the *output*, and over a copy that lands
        /// on the keyframe after the start point rather than the one before it: measured against a
        /// source with keyframes every 48 frames, a 24-frame section asked for at frame 30 came back as
        /// 8 frames beginning at frame 48, the eighteen frames in between simply gone.
        /// <para/>
        /// The unit conversion is the part that cannot be skipped. Frame mode holds frame *numbers* in
        /// the same three fields the time modes hold milliseconds in, so changing the mode on its own
        /// would reinterpret frame 240 as 240 ms - which is why this returns a new settings object built
        /// through the millisecond accessors rather than assigning over the mode.
        /// </summary>
        public TrimSettings AsKeyframeCopy(Fraction rate)
        {
            long start = GetStartMs(rate);
            long end = GetEndMs(rate);

            return new TrimSettings()
            {
                TrimMode = Mode.TimeKeyframe,
                StartTime = start,
                EndTime = end,
                Duration = Math.Max(0, end - start)
            };
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
