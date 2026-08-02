using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.IO;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nmkoder.Utils
{
    class MiscUtils
    {
        public static T ParseEnum<T>(string value, bool ignoreCase = true)
        {
            return (T)Enum.Parse(typeof(T), value, ignoreCase);
        }

        public static string GetScaleFilter(string w, string h, bool logWarnings = true)
        {
            string argW = w.Replace("w", "iw").Replace("h", "ih");
            string argH = h.Replace("w", "iw").Replace("h", "ih");

            if (w.EndsWith("%"))
                argW = $"iw*{((float)w.GetInt() / 100).ToStringDot()}";
            else if (string.IsNullOrWhiteSpace(w))
                argW = "-2";

            if (h.EndsWith("%"))
                argH = $"ih*{((float)h.GetInt() / 100).ToStringDot()}";
            else if (string.IsNullOrWhiteSpace(h))
                argH = "-2";

            string forceDiv = (argW.Contains("*") || argH.Contains("*")) ? ":force_original_aspect_ratio=increase:force_divisible_by=2" : "";

            if (logWarnings && forceDiv.Length > 0 && (argW.Contains("*") && argH.Contains("*")))
                Logger.Log($"Info: Scaling using percentages enforces the original aspect ratio. You cannot use different percentages for width and height.");

            return $"scale={argW}:{argH}{forceDiv},setsar=1:1";
        }

        public static Fraction GetFpsFromString(string str)
        {
            if (string.IsNullOrWhiteSpace(str))
                return new Fraction();

            if (str.Contains("/"))   // Parse fraction
            {
                string[] split = str.Split('/');
                return new Fraction(split[0].GetInt(), split[1].GetInt());
            }

            // Parse float
            return new Fraction(str.TrimNumbers(true).GetFloat());
        }

        /// <summary>
        /// How far apart two frame rates may be and still count as the same one: 0.01%, as a proportion
        /// of the larger.
        /// <para/>
        /// It exists because the app shows a rate two ways - the Track List reads "24000/1001 (~23.976
        /// FPS)" - and typing the readable one back into the Frame Rate box used to be a resample.
        /// 23.976 is not 24000/1001; it is 23.976024 rounded, and an exact comparison called that a
        /// different rate and built a filter for it. The retiming itself is nothing, one frame in a
        /// million, but on the AV1AN tab a filter chain that exists at all is not nothing: it turns on
        /// --ignore-frame-mismatch, it takes the pixel format conversion off VapourSynth, and it puts
        /// every target-quality probe on an unfiltered source. A setting the user reads as "leave it
        /// alone" should not do any of that.
        /// <para/>
        /// 0.01% is ten times finer than the gap this must never close, which is the one between a rate
        /// and its NTSC form: 24 against 23.976 is 0.1% apart, and so are 30/29.97 and 60/59.94. So
        /// pulldown rates stay firmly distinct while a rounded decimal of the same rate does not.
        /// </summary>
        public const double FrameRateTolerance = 0.0001d;

        /// <summary> Whether two frame rates are the same one to within <see cref="FrameRateTolerance"/>.
        /// False for either being zero or having no denominator, which is what an empty or unparseable
        /// Frame Rate box comes out as - "nothing was asked for" is not "the same as the source". </summary>
        public static bool IsSameFrameRate(Fraction a, Fraction b)
        {
            double x = a.Denominator > 0 ? a.ToDouble() : 0d;
            double y = b.Denominator > 0 ? b.ToDouble() : 0d;

            if (x <= 0d || y <= 0d)
                return false;

            return Math.Abs(x - y) <= Math.Max(x, y) * FrameRateTolerance;
        }

        /// <summary> A rate as both forms the app shows - "24000/1001 (23.976 fps)" - for the log line
        /// that announces a resample, where naming only one of them is what caused the confusion in the
        /// first place. </summary>
        public static string DescribeFrameRate(Fraction rate)
        {
            return $"{rate} ({rate.GetFloat().ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} fps)";
        }

        public static float GetAudioBitrateMultiplier(int channels)
        {
            float mult = 1f;

            if (channels == 1) // Mono
                mult = 0.5f;

            if (channels == 2) // Stereo
                mult = 1f;

            if (channels > 4) // 5.1, etc
                mult = 2f;

            if (channels > 6) // 6.1, 7.1, etc
                mult = 2.5f;

            return mult;
        }
    }
}
