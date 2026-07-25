using System;

namespace Nmkoder.Data
{
    /// <summary>
    /// Trim configuration produced by the trim dialog. Used to live as a nested class inside the
    /// WinForms TrimForm; it is plain data, so it belongs with the rest of the model.
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

        public string StartArg
        {
            get
            {
                return TrimMode == Mode.FrameNumbers
                    ? $"select=\"gte(n\\, {StartTime})\""
                    : $"-ss {GetTimeString(TimeSpan.FromMilliseconds(StartTime))}";
            }
        }

        public string DurationArg
        {
            get
            {
                return TrimMode == Mode.FrameNumbers
                    ? $"-vframes {Duration}"
                    : $"-t {GetTimeString(TimeSpan.FromMilliseconds(Duration))}";
            }
        }

        public static string GetTimeString(TimeSpan ts)
        {
            bool ms = ts.Milliseconds != 0;
            return $"{ts.Hours.ToString().PadLeft(2, '0')}:{ts.Minutes.ToString().PadLeft(2, '0')}:{ts.Seconds.ToString().PadLeft(2, '0')}{(ms ? $".{ts.Milliseconds.ToString().PadLeft(3, '0')}" : "")}";
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
