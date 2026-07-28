using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PF = Nmkoder.Data.Colors.PixelFormats;

namespace Nmkoder.Data.Colors
{

    public class PixFmtUtils
    {
        /// <summary>
        /// The names VapourSynth's vs.PresetVideoFormat knows, by the ffmpeg pixel format each one
        /// corresponds to. Deliberately a copy of av1an's own FFPixelFormat::to_vapoursynth_string
        /// table rather than a wider one: this is the lookup its "--pix-format-converter vs-resize"
        /// path performs, and a format missing from it fails the chunk instead of converting.
        /// </summary>
        private static readonly Dictionary<string, string> VapourSynthPresets = new Dictionary<string, string>()
        {
            { "gray", "GRAY8" }, { "gray8", "GRAY8" }, { "gray10le", "GRAY10" },
            { "gray12le", "GRAY12" }, { "gray12l", "GRAY12" },
            { "yuv420p", "YUV420P8" }, { "yuv420p10le", "YUV420P10" }, { "yuv420p12le", "YUV420P12" },
            { "yuv422p", "YUV422P8" }, { "yuv422p10le", "YUV422P10" }, { "yuv422p12le", "YUV422P12" },
            { "yuv444p", "YUV444P8" }, { "yuv444p10le", "YUV444P10" }, { "yuv444p12le", "YUV444P12" },
            { "yuvj420p", "YUV420P8" }, { "yuvj422p", "YUV422P8" }, { "yuvj444p", "YUV444P8" },
        };

        /// <summary>
        /// The VapourSynth preset format matching an ffmpeg pixel format, or "" where VapourSynth has
        /// no preset for it - which is what makes the difference between a conversion VapourSynth can
        /// be asked for and one that has to be left to ffmpeg.
        /// </summary>
        public static string GetVapourSynthPreset(string ffmpegName)
        {
            return VapourSynthPresets.TryGetValue((ffmpegName ?? "").Trim().ToLower(), out string preset) ? preset : "";
        }

        public static PixelFormat GetFormat(PF fmt)
        {
            string name = "yuv420p";

            if (fmt == PF.Yuv420P8) name = "yuv420p";
            else if (fmt == PF.Yuva420P8) name = "yuva420p";
            else if (fmt == PF.Yuv420P10) name = "yuv420p10le";
            else if (fmt == PF.Yuv422P8) name = "yuv422p10le";
            else if (fmt == PF.Yuv422P10) name = "yuv422p10le";
            else if (fmt == PF.Yuv444P8) name = "yuv444p";
            else if (fmt == PF.Yuv444P10) name = "yuv444p10le";
            else if (fmt == PF.P010) name = "p010le";
            else if (fmt == PF.Rgb24) name = "rgb24";
            else if (fmt == PF.Rgba) name = "rgba";
            else if (fmt == PF.Rgb48) name = "rgb48be";
            else if (fmt == PF.Rgba64) name = "rgba64be";
            else name = "NOT_IMPLEMENTED";

            string channels = "yuv";

            if (fmt == PF.Rgb24 || fmt == PF.Rgb48) channels = "rgb";
            else if (fmt == PF.Rgba || fmt == PF.Rgba64) channels = "rgba";
            else if (fmt == PF.Yuva420P8) channels = "yuva";

            int depth = 8;

            if (fmt == PF.Yuv420P10 || fmt == PF.Yuv422P10 || fmt == PF.Yuv444P10 || fmt == PF.P010) depth = 10;
            else if (fmt == PF.Rgb48 || fmt == PF.Rgba64) depth = 16;

            int[] subsampling = null;

            if (fmt == PF.Yuv420P8 || fmt == PF.Yuva420P8 || fmt == PF.Yuv420P10 || fmt == PF.P010) subsampling = new int[] { 4, 2, 0 };
            else if (fmt == PF.Yuv422P8 || fmt == PF.Yuv422P10) subsampling = new int[] { 4, 2, 2 };
            else if (fmt == PF.Yuv444P8 || fmt == PF.Yuv444P10) subsampling = new int[] { 4, 4, 4 };

            return new PixelFormat(name, channels, depth, subsampling);
        }
    }
}
