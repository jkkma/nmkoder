using Nmkoder.Data.Colors;
using Nmkoder.Extensions;
using Nmkoder.IO;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nmkoder.Data.Codecs.Video
{
    #region x26x

    class Libx264 : IEncoder
    {
        public Streams.Stream.StreamType Type { get; } = Streams.Stream.StreamType.Video;
        public string Name { get { return GetType().Name; } }
        public string FriendlyName { get; } = "H.264 / AVC (x264)";
        public string[] Presets { get; } = new string[] { "veryslow", "slower", "slow", "medium", "fast", "faster", "veryfast", "superfast" };
        public int PresetDefault { get; } = 2;

        public List<PixelFormats> ColorFormats { get; } = new List<PixelFormats>() { PixelFormats.Yuv420P8, PixelFormats.Yuv422P8, PixelFormats.Yuv444P8, PixelFormats.Yuv420P10, PixelFormats.Yuv422P10, PixelFormats.Yuv444P10 };
        public int ColorFormatDefault { get; } = 0;
        public int QMin { get; } = 0;
        public int QMax { get; } = 51;
        public int QDefault { get; } = 18;
        public string QInfo { get; } = "CRF (0-51 - Lower is better)";
        public string PresetInfo { get; } = "Slower = Better compression";

        public bool SupportsTwoPass { get; } = true;
        public bool ForceTwoPass { get; } = false;
        public bool DoesNotEncode { get; } = false;
        public bool IsFixedFormat { get; } = false;
        public bool IsSequence { get; } = false;

        public CodecArgs GetArgs(Dictionary<string, string> encArgs = null, MediaFile mediaFile = null, Pass pass = Pass.OneOfOne)
        {
            bool vbr = encArgs.ContainsKey("qMode") && (UI.Tasks.QuickConvert.QualityMode)encArgs["qMode"].GetInt() != UI.Tasks.QuickConvert.QualityMode.Crf;
            string g = CodecUtils.GetKeyIntArg(mediaFile, Config.GetInt(Config.Key.DefaultKeyIntSecs));
            string q = encArgs.ContainsKey("q") ? encArgs["q"] : QDefault.ToString();
            string preset = encArgs.ContainsKey("preset") ? encArgs["preset"] : Presets[PresetDefault];
            string pixFmt = encArgs.ContainsKey("pixFmt") ? encArgs["pixFmt"] : PixFmtUtils.GetFormat(ColorFormats[ColorFormatDefault]).Name;
            string rc = vbr ? $"-b:v {(encArgs.ContainsKey("bitrate") ? encArgs["bitrate"] : "0")}k" : (q.GetInt() > 0 ? $"-crf {q}" : "-qp 0");
            string p = pass == Pass.OneOfOne ? "" : (pass == Pass.OneOfTwo ? "-pass 1" : "-pass 2");
            string cust = encArgs.ContainsKey("custom") ? encArgs["custom"] : "";
            // The Advanced tab's grid, as one "-x264-params" holding x264's own parameter names
            string adv = encArgs.ContainsKey("advanced") ? FfmpegEncoderArgs.Render(nameof(Libx264), encArgs["advanced"]) : "";
            return new CodecArgs($"-c:v libx264 {p} {rc} -preset {preset} {g} -pix_fmt {pixFmt} {adv} {cust}");
        }
    }

    class Libx265 : IEncoder
    {
        public Streams.Stream.StreamType Type { get; } = Streams.Stream.StreamType.Video;
        public string Name { get { return GetType().Name; } }
        public string FriendlyName { get; } = "H.265 / HEVC (x265)";
        public string[] Presets { get; } = new string[] { "veryslow", "slower", "slow", "medium", "fast", "faster", "veryfast", "superfast" };
        public int PresetDefault { get; } = 3;
        public List<PixelFormats> ColorFormats { get; } = new List<PixelFormats>() { PixelFormats.Yuv420P8, PixelFormats.Yuv422P8, PixelFormats.Yuv444P8, PixelFormats.Yuv420P10, PixelFormats.Yuv422P10, PixelFormats.Yuv444P10 };
        public int ColorFormatDefault { get; } = 0;
        public int QMin { get; } = 0;
        public int QMax { get; } = 51;
        public int QDefault { get; } = 22;
        public string QInfo { get; } = "CRF (0-51 - Lower is better)";
        public string PresetInfo { get; } = "Slower = Better compression";

        public bool SupportsTwoPass { get; } = true;
        public bool ForceTwoPass { get; } = false;
        public bool DoesNotEncode { get; } = false;
        public bool IsFixedFormat { get; } = false;
        public bool IsSequence { get; } = false;

        public CodecArgs GetArgs(Dictionary<string, string> encArgs = null, MediaFile mediaFile = null, Pass pass = Pass.OneOfOne)
        {
            bool vbr = encArgs.ContainsKey("qMode") && (UI.Tasks.QuickConvert.QualityMode)encArgs["qMode"].GetInt() != UI.Tasks.QuickConvert.QualityMode.Crf;
            string g = CodecUtils.GetKeyIntArg(mediaFile, Config.GetInt(Config.Key.DefaultKeyIntSecs));
            string q = encArgs.ContainsKey("q") ? encArgs["q"] : QDefault.ToString();
            string preset = encArgs.ContainsKey("preset") ? encArgs["preset"] : Presets[PresetDefault];
            string pixFmt = encArgs.ContainsKey("pixFmt") ? encArgs["pixFmt"] : PixFmtUtils.GetFormat(ColorFormats[ColorFormatDefault]).Name;
            string rc = vbr ? $"-b:v {(encArgs.ContainsKey("bitrate") ? encArgs["bitrate"] : "0")}k" : (q.GetInt() > 0 ? $"-crf {q}" : "");
            string cust = encArgs.ContainsKey("custom") ? encArgs["custom"] : "";

            // Every x265 parameter this encode uses goes into one "-x265-params", the Advanced tab's
            // grid included. It has to be one: a second "-x265-params" does not add to the first, it
            // replaces it outright - measured - so the pass number and the lossless flag were each a
            // whole parameter list of their own, and whichever came last would have been the only one
            // to survive. Two of these three could not collide before the grid existed, which is why
            // they were written as separate options and got away with it.
            List<string> x265 = new List<string>();

            if (pass != Pass.OneOfOne)
                x265.Add(pass == Pass.OneOfTwo ? "pass=1" : "pass=2");

            if (!vbr && q.GetInt() <= 0)
                x265.Add("lossless=1");

            if (encArgs.ContainsKey("advanced"))
                x265.AddRange(FfmpegEncoderArgs.Pairs(encArgs["advanced"]));

            string x265Params = x265.Count > 0 ? $"-x265-params {FfmpegEncoderArgs.ParamsList(x265)}" : "";
            return new CodecArgs($"-c:v libx265 {x265Params} {rc} -preset {preset} {g} -pix_fmt {pixFmt} {cust}");
        }
    }

    #endregion

    #region NVENC

    class H264Nvenc : IEncoder
    {
        public Streams.Stream.StreamType Type { get; } = Streams.Stream.StreamType.Video;
        public string Name { get { return GetType().Name; } }
        public string FriendlyName { get; } = "H.264 / AVC (NVIDIA NVENC)";
        public string[] Presets { get; } = new string[] { "p5", "p4", "p3", "p2", "p1" };
        public int PresetDefault { get; } = 0;
        public List<PixelFormats> ColorFormats { get; } = new List<PixelFormats>() { PixelFormats.Yuv420P8, PixelFormats.Yuv444P8 };
        public int ColorFormatDefault { get; } = 0;
        public int QMin { get; } = 0;
        public int QMax { get; } = 51;
        public int QDefault { get; } = 18;
        public string QInfo { get; } = "CRF (0-51 - Lower is better)";
        public string PresetInfo { get; } = "Higher = Better compression";

        public bool SupportsTwoPass { get; } = false;
        public bool ForceTwoPass { get; } = false;
        public bool DoesNotEncode { get; } = false;
        public bool IsFixedFormat { get; } = false;
        public bool IsSequence { get; } = false;

        public CodecArgs GetArgs(Dictionary<string, string> encArgs = null, MediaFile mediaFile = null, Pass pass = Pass.OneOfOne)
        {
            bool vbr = encArgs.ContainsKey("qMode") && (UI.Tasks.QuickConvert.QualityMode)encArgs["qMode"].GetInt() != UI.Tasks.QuickConvert.QualityMode.Crf;
            string q = encArgs.ContainsKey("q") ? encArgs["q"] : QDefault.ToString();
            int br = encArgs.ContainsKey("bitrate") ? encArgs["bitrate"].GetInt() : 0;
            string preset = encArgs.ContainsKey("preset") ? encArgs["preset"] : Presets[PresetDefault];
            string pixFmt = encArgs.ContainsKey("pixFmt") ? encArgs["pixFmt"] : PixFmtUtils.GetFormat(ColorFormats[ColorFormatDefault]).Name;
            string rc = vbr ? $"-b:v {br}k -minrate {br / 4}k -maxrate {br * 2}k -bufsize {br}k" : (q.GetInt() > 0 ? $"-b:v 0 -cq {q}" : "-tune lossless");
            string cust = encArgs.ContainsKey("custom") ? encArgs["custom"] : "";
            // One AVOption per filled-in row - NVENC has no parameter list of its own. It goes after
            // the rate control, so a "tune" set in the grid wins over the lossless one above, which is
            // the only place the two can name the same option.
            string adv = encArgs.ContainsKey("advanced") ? FfmpegEncoderArgs.Render(nameof(H264Nvenc), encArgs["advanced"]) : "";
            return new CodecArgs($"-c:v h264_nvenc {rc} -preset {preset} -pix_fmt {pixFmt} {adv} {cust}");
        }
    }

    class H265Nvenc : IEncoder
    {
        public Streams.Stream.StreamType Type { get; } = Streams.Stream.StreamType.Video;
        public string Name { get { return GetType().Name; } }
        public string FriendlyName { get; } = "H.265 / HEVC (NVIDIA NVENC)";
        public string[] Presets { get; } = new string[] { "p7", "p6", "p5", "p4", "p3", "p2", "p1" };
        public int PresetDefault { get; } = 0;
        public List<PixelFormats> ColorFormats { get; } = new List<PixelFormats>() { PixelFormats.Yuv420P8, PixelFormats.Yuv444P8, PixelFormats.P010 };
        public int ColorFormatDefault { get; } = 0;
        public int QMin { get; } = 0;
        public int QMax { get; } = 51;
        public int QDefault { get; } = 22;
        public string QInfo { get; } = "CRF (0-51 - Lower is better)";
        public string PresetInfo { get; } = "Higher = Better compression";

        public bool SupportsTwoPass { get; } = false;
        public bool ForceTwoPass { get; } = false;
        public bool DoesNotEncode { get; } = false;
        public bool IsFixedFormat { get; } = false;
        public bool IsSequence { get; } = false;

        public CodecArgs GetArgs(Dictionary<string, string> encArgs = null, MediaFile mediaFile = null, Pass pass = Pass.OneOfOne)
        {
            bool vbr = encArgs.ContainsKey("qMode") && (UI.Tasks.QuickConvert.QualityMode)encArgs["qMode"].GetInt() != UI.Tasks.QuickConvert.QualityMode.Crf;
            string q = encArgs.ContainsKey("q") ? encArgs["q"] : QDefault.ToString();
            int br = encArgs.ContainsKey("bitrate") ? encArgs["bitrate"].GetInt() : 0;
            string preset = encArgs.ContainsKey("preset") ? encArgs["preset"] : Presets[PresetDefault];
            string pixFmt = encArgs.ContainsKey("pixFmt") ? encArgs["pixFmt"] : PixFmtUtils.GetFormat(ColorFormats[ColorFormatDefault]).Name;
            string rc = vbr ? $"-b:v {br}k -minrate {br / 4}k -maxrate {br * 2}k -bufsize {br}k" : (q.GetInt() > 0 ? $"-b:v 0 -cq {q}" : "-tune lossless");
            string cust = encArgs.ContainsKey("custom") ? encArgs["custom"] : "";
            // As for H.264 above, and after the rate control for the same reason
            string adv = encArgs.ContainsKey("advanced") ? FfmpegEncoderArgs.Render(nameof(H265Nvenc), encArgs["advanced"]) : "";
            return new CodecArgs($"-c:v hevc_nvenc {rc} -preset {preset} -pix_fmt {pixFmt} {adv} {cust}");
        }
    }

    #endregion

    #region Google/AOM

    class LibVpx : IEncoder
    {
        public Streams.Stream.StreamType Type { get; } = Streams.Stream.StreamType.Video;
        public string Name { get { return GetType().Name; } }
        public string FriendlyName { get; } = "VP9 (VPX)";
        public string[] Presets { get; } = new string[] { "0", "1", "2", "3", "4", "5", "6" };
        public int PresetDefault { get; } = 3;
        public List<PixelFormats> ColorFormats { get; } = new List<PixelFormats>() { PixelFormats.Yuv420P8, PixelFormats.Yuv422P8, PixelFormats.Yuva420P8, PixelFormats.Yuv444P8, PixelFormats.Yuv420P10, PixelFormats.Yuv422P10, PixelFormats.Yuv444P10 };
        public int ColorFormatDefault { get; } = 0;
        public int QMin { get; } = 0;
        public int QMax { get; } = 63;
        public int QDefault { get; } = 24;
        public string QInfo { get; } = "CRF (0-63 - Lower is better)";
        public string PresetInfo { get; } = "Lower = Better compression";

        public bool SupportsTwoPass { get; } = true;
        public bool ForceTwoPass { get; } = true;
        public bool DoesNotEncode { get; } = false;
        public bool IsFixedFormat { get; } = false;
        public bool IsSequence { get; } = false;

        public CodecArgs GetArgs(Dictionary<string, string> encArgs = null, MediaFile mediaFile = null, Pass pass = Pass.OneOfOne)
        {
            bool vbr = encArgs.ContainsKey("qMode") && (UI.Tasks.QuickConvert.QualityMode)encArgs["qMode"].GetInt() != UI.Tasks.QuickConvert.QualityMode.Crf;
            string q = encArgs.ContainsKey("q") ? encArgs["q"] : QDefault.ToString();
            string preset = encArgs.ContainsKey("preset") ? encArgs["preset"] : Presets[PresetDefault];
            string pixFmt = encArgs.ContainsKey("pixFmt") ? encArgs["pixFmt"] : PixFmtUtils.GetFormat(ColorFormats[ColorFormatDefault]).Name;
            string rc = vbr ? $"-b:v {(encArgs.ContainsKey("bitrate") ? encArgs["bitrate"] : "0")}k" : $"-crf {q}";
            string g = CodecUtils.GetKeyIntArg(mediaFile, Config.GetInt(Config.Key.DefaultKeyIntSecs));
            string p = pass == Pass.OneOfOne ? "" : (pass == Pass.OneOfTwo ? "-pass 1" : "-pass 2");
            // Rows first, then columns - GetTilingArgs takes them in that order, and every other encoder
            // here passes them that way. Swapped, a 4K frame asked for two tile *rows* and one column
            // where it wanted the opposite: fewer columns than the width can use, and a horizontal split
            // a 2160-line frame does not need.
            string tiles = CodecUtils.GetTilingArgs(CodecUtils.GetEncodedFrameSize(encArgs, mediaFile), "-tile-rows ", "-tile-columns ");
            string cust = encArgs.ContainsKey("custom") ? encArgs["custom"] : "";
            // One AVOption per filled-in row: libvpx-vp9 has no parameter list of its own, which is
            // also why its argument JSON names ffmpeg's spellings rather than vpxenc's
            string adv = encArgs.ContainsKey("advanced") ? FfmpegEncoderArgs.Render(nameof(LibVpx), encArgs["advanced"]) : "";
            return new CodecArgs($"-c:v libvpx-vp9 {p} {rc} {tiles} -row-mt 1 -cpu-used {preset} {g} -pix_fmt {pixFmt} {adv} {cust}");
        }
    }

    class LibSvtAv1 : IEncoder
    {
        public Streams.Stream.StreamType Type { get; } = Streams.Stream.StreamType.Video;
        public string Name { get { return GetType().Name; } }
        public string FriendlyName { get; } = "AV1 (SVT-AV1)";
        public string[] Presets { get; } = new string[] { "0", "1", "2", "3", "4", "5", "6", "7", "8" };
        // 4 rather than 7. This is the encoder Quick Convert opens on and the tab restores nothing, so
        // these two numbers are what every session starts at - which makes them worth setting to what
        // someone would actually encode with rather than to something fast enough to demonstrate.
        public int PresetDefault { get; } = 4;
        public List<PixelFormats> ColorFormats { get; } = new List<PixelFormats>() { PixelFormats.Yuv420P8, PixelFormats.Yuv420P10 };
        public int ColorFormatDefault { get; } = 1;
        public int QMin { get; } = 0;
        public int QMax { get; } = 63;
        public int QDefault { get; } = 30;
        public string QInfo { get; } = "CRF (0-50 - Lower is better)";
        public string PresetInfo { get; } = "Lower = Better compression";

        public bool SupportsTwoPass { get; } = true;
        public bool ForceTwoPass { get; } = false;
        public bool DoesNotEncode { get; } = false;
        public bool IsFixedFormat { get; } = false;
        public bool IsSequence { get; } = false;

        public CodecArgs GetArgs(Dictionary<string, string> encArgs = null, MediaFile mediaFile = null, Pass pass = Pass.OneOfOne)
        {
            bool vbr = encArgs.ContainsKey("qMode") && (UI.Tasks.QuickConvert.QualityMode)encArgs["qMode"].GetInt() != UI.Tasks.QuickConvert.QualityMode.Crf;
            if (vbr && pass == Pass.OneOfTwo) Logger.Log($"WARNING: The 2-Pass implementation of SVT-AV1 is experimental. It might crash or produce inaccurate results.");
            string q = encArgs.ContainsKey("q") ? encArgs["q"] : QDefault.ToString();
            string preset = encArgs.ContainsKey("preset") ? encArgs["preset"] : Presets[PresetDefault];
            string pixFmt = encArgs.ContainsKey("pixFmt") ? encArgs["pixFmt"] : PixFmtUtils.GetFormat(ColorFormats[ColorFormatDefault]).Name;
            // "-b:v" on its own is what selects VBR here. There used to be a "-rc vbr" in front of it
            // and it never reached SVT: measured, "ffmpeg -h encoder=libsvtav1" lists preset, crf and
            // qp and no rc at all, so the name matched another encoder's option class and was
            // discarded - every bitrate encode logging "Codec AVOption rc (Override the preset
            // rate-control) has not been used for any stream" on its way past. Removing it changes no
            // output, which is measured too, because it was never applied to begin with.
            string rc = vbr ? $"-b:v {(encArgs.ContainsKey("bitrate") ? encArgs["bitrate"] : "0")}k" : $"-qp {q}";
            string g = CodecUtils.GetKeyIntArg(mediaFile, Config.GetInt(Config.Key.DefaultKeyIntSecs), "-g ", vbr ? 255 : 480); // SVT can't do GOP size >255 in VBR mode
            string p = pass == Pass.OneOfOne ? "" : (pass == Pass.OneOfTwo ? "-pass 1" : "-pass 2");
            string tiles = ""; // TEMP DISABLED AS IT SEEMS TO SLOW THINGS DOWN // CodecUtils.GetTilingArgs(mediaFile.VideoStreams.FirstOrDefault().Resolution, "-tile_rows ", "-tile_columns ");
            string cust = encArgs.ContainsKey("custom") ? encArgs["custom"] : "";
            // The Advanced tab's grid, as one "-svtav1-params". Note that the SVT-AV1 behind this is
            // the one compiled into ffmpeg, not the svt-av1-hdr binary bundle-tools.sh fetches for
            // av1an - so its list is a shorter one, and is written against what ffmpeg's library takes.
            string adv = encArgs.ContainsKey("advanced") ? FfmpegEncoderArgs.Render(nameof(LibSvtAv1), encArgs["advanced"]) : "";
            return new CodecArgs($"-c:v libsvtav1 {p} {rc} -preset {preset} {g} {tiles} -pix_fmt {pixFmt} {adv} {cust}");
        }
    }

    class LibAomAv1 : IEncoder
    {
        public Streams.Stream.StreamType Type { get; } = Streams.Stream.StreamType.Video;
        public string Name { get { return GetType().Name; } }
        public string FriendlyName { get; } = "AV1 (AOM-AV1)";
        public string[] Presets { get; } = new string[] { "0", "1", "2", "3", "4", "5", "6" };
        public int PresetDefault { get; } = 6;
        public List<PixelFormats> ColorFormats { get; } = new List<PixelFormats>() { PixelFormats.Yuv420P8, PixelFormats.Yuv422P8, PixelFormats.Yuv444P8, PixelFormats.Yuv420P10, PixelFormats.Yuv422P10, PixelFormats.Yuv444P10 };
        public int ColorFormatDefault { get; } = 3;
        public int QMin { get; } = 0;
        public int QMax { get; } = 63;
        public int QDefault { get; } = 20;
        public string QInfo { get; } = "CRF (0-63 - Lower is better)";
        public string PresetInfo { get; } = "Lower = Better compression";

        public bool SupportsTwoPass { get; } = true;
        public bool ForceTwoPass { get; } = false;
        public bool DoesNotEncode { get; } = false;
        public bool IsFixedFormat { get; } = false;
        public bool IsSequence { get; } = false;

        public CodecArgs GetArgs(Dictionary<string, string> encArgs = null, MediaFile mediaFile = null, Pass pass = Pass.OneOfOne)
        {
            bool vbr = encArgs.ContainsKey("qMode") && (UI.Tasks.QuickConvert.QualityMode)encArgs["qMode"].GetInt() != UI.Tasks.QuickConvert.QualityMode.Crf;
            string g = CodecUtils.GetKeyIntArg(mediaFile, Config.GetInt(Config.Key.DefaultKeyIntSecs));
            string q = encArgs.ContainsKey("q") ? encArgs["q"] : QDefault.ToString();
            string preset = encArgs.ContainsKey("preset") ? encArgs["preset"] : Presets[PresetDefault];
            string pixFmt = encArgs.ContainsKey("pixFmt") ? encArgs["pixFmt"] : PixFmtUtils.GetFormat(ColorFormats[ColorFormatDefault]).Name;
            string grain = encArgs.ContainsKey("grainSynthStrength") ? encArgs["grainSynthStrength"] : "0";
            //string denoise = encArgs.ContainsKey("grainSynthDenoise") ? (encArgs["grainSynthDenoise"].GetBool() ? "1" : "0") : "0";
            string tiles = CodecUtils.GetTilingArgs(CodecUtils.GetEncodedFrameSize(encArgs, mediaFile), "-tile-rows ", "-tile-columns ");
            string rc = vbr ? $"-b:v {(encArgs.ContainsKey("bitrate") ? encArgs["bitrate"] : "0")}k" : $"-crf {q} -b:v 0";
            string p = pass == Pass.OneOfOne ? "" : (pass == Pass.OneOfTwo ? "-pass 1" : "-pass 2");
            string cust = encArgs.ContainsKey("custom") ? encArgs["custom"] : "";
            // The Advanced tab's grid, as one "-aom-params". This is the one encoder here that refuses
            // the whole encode over a parameter it does not know rather than warning and carrying on.
            string adv = encArgs.ContainsKey("advanced") ? FfmpegEncoderArgs.Render(nameof(LibAomAv1), encArgs["advanced"]) : "";
            return new CodecArgs($"-c:v libaom-av1 {p} {rc} -cpu-used {preset} -row-mt 1 -denoise-noise-level {grain} {tiles} {g} -pix_fmt {pixFmt} {adv} {cust}");
        }
    }

    #endregion

    #region Image Formats

    class Gif : IEncoder
    {
        public Streams.Stream.StreamType Type { get; } = Streams.Stream.StreamType.Video;
        public string Name { get { return GetType().Name; } }
        public string FriendlyName { get; } = "GIF [Animated GIF]";
        public string[] Presets { get; } = new string[] { };
        public int PresetDefault { get; }
        public List<PixelFormats> ColorFormats { get; }
        public int ColorFormatDefault { get; }
        // 3 rather than 0, and rather than the 2 palettegen's own option range starts at: this chain
        // leaves reserve_transparent at its default, and measured, "max_colors=2 is only allowed
        // without reserving a transparent color slot" - so 2 parses and then fails to build the
        // filter graph. A 0 or a 1 was refused outright ("out of range [2 - 256]"), which the
        // spinner let anyone reach, since it took its floor from here.
        public int QMin { get; } = 3;
        public int QMax { get; } = 256;
        public int QDefault { get; } = 128;
        public string QInfo { get; } = "Color Palette Size (Higher is better)";
        public string PresetInfo { get; } = "Higher = Better compression";

        public bool SupportsTwoPass { get; } = false;
        public bool ForceTwoPass { get; } = false;
        public bool DoesNotEncode { get; } = false;
        public bool IsFixedFormat { get; } = true;
        public bool IsSequence { get; } = false;

        public CodecArgs GetArgs(Dictionary<string, string> encArgs = null, MediaFile mediaFile = null, Pass pass = Pass.OneOfOne)
        {
            string q = encArgs.ContainsKey("q") ? encArgs["q"] : QDefault.ToString();
            string cust = encArgs.ContainsKey("custom") ? encArgs["custom"] : "";
            return new CodecArgs($"-f gif -gifflags -offsetting {cust}", $"split[s0][s1];[s0]palettegen={q}[p];[s1][p]paletteuse=dither=floyd_steinberg");
        }
    }

    class Jpg : IEncoder
    {
        public Streams.Stream.StreamType Type { get; } = Streams.Stream.StreamType.Video;
        public string Name { get { return GetType().Name; } }
        public string FriendlyName { get; } = "JPEG [Image Sequence]";
        public string[] Presets { get; } = new string[] { };
        public int PresetDefault { get; }
        public List<PixelFormats> ColorFormats { get; } = new List<PixelFormats>() { PixelFormats.Yuv420P8, PixelFormats.Yuv422P8, PixelFormats.Yuv444P8 };
        public int ColorFormatDefault { get; } = 0;
        public int QMin { get; } = 1;
        public int QMax { get; } = 31;
        public int QDefault { get; } = 3;
        public string QInfo { get; } = "Quality (1-31 - Lower is better)";
        public string PresetInfo { get; }

        public bool SupportsTwoPass { get; } = false;
        public bool ForceTwoPass { get; } = false;
        public bool DoesNotEncode { get; } = false;
        public bool IsFixedFormat { get; } = true;
        public bool IsSequence { get; } = true;

        public CodecArgs GetArgs(Dictionary<string, string> encArgs = null, MediaFile mediaFile = null, Pass pass = Pass.OneOfOne)
        {
            string q = encArgs.ContainsKey("q") ? encArgs["q"] : QDefault.ToString();
            string cust = encArgs.ContainsKey("custom") ? encArgs["custom"] : "";
            // The Color Format dropdown is offered for this encoder and had nowhere to go, so picking
            // 4:2:2 or 4:4:4 wrote the same 4:2:0 JPEGs as leaving it alone
            string pixFmt = encArgs.ContainsKey("pixFmt") ? encArgs["pixFmt"] : PixFmtUtils.GetFormat(ColorFormats[ColorFormatDefault]).Name;
            return new CodecArgs($"-c:v mjpeg -qmin 1 -q:v {q} -pix_fmt {pixFmt} {cust}");
        }
    }

    class Png : IEncoder
    {
        public Streams.Stream.StreamType Type { get; } = Streams.Stream.StreamType.Video;
        public string Name { get { return GetType().Name; } }
        public string FriendlyName { get; } = "PNG [Image Sequence]";
        public string[] Presets { get; } = new string[] { };
        public int PresetDefault { get; }
        public List<PixelFormats> ColorFormats { get; } = new List<PixelFormats>() { PixelFormats.Rgb24, PixelFormats.Rgba, PixelFormats.Rgb48, PixelFormats.Rgba64 };
        public int ColorFormatDefault { get; } = 0;
        public int QMin { get; } = 0;
        public int QMax { get; } = 0;
        public int QDefault { get; } = 0;
        public string QInfo { get; }
        public string PresetInfo { get; }

        public bool SupportsTwoPass { get; } = false;
        public bool ForceTwoPass { get; } = false;
        public bool DoesNotEncode { get; } = false;
        public bool IsFixedFormat { get; } = true;
        public bool IsSequence { get; } = true;

        public CodecArgs GetArgs(Dictionary<string, string> encArgs = null, MediaFile mediaFile = null, Pass pass = Pass.OneOfOne)
        {
            string cust = encArgs.ContainsKey("custom") ? encArgs["custom"] : "";
            // As for JPEG above: the dropdown offers RGB24, RGBA, RGB48 and RGBA64 for this encoder, and
            // every one of them wrote the same file until the value was passed on. Alpha is the one that
            // shows - a source with a transparent layer lost it whatever was picked.
            string pixFmt = encArgs.ContainsKey("pixFmt") ? encArgs["pixFmt"] : PixFmtUtils.GetFormat(ColorFormats[ColorFormatDefault]).Name;
            return new CodecArgs($"-c:v png -compression_level 3 -pix_fmt {pixFmt} {cust}");
        }
    }

    #endregion

    #region Mux

    class CopyVideo : IEncoder
    {
        public Streams.Stream.StreamType Type { get; } = Streams.Stream.StreamType.Video;
        public string Name { get { return GetType().Name; } }
        public string FriendlyName { get; } = "Copy Video Without Re-Encoding";
        public string[] Presets { get; } = new string[] { };
        public int PresetDefault { get; }
        public List<PixelFormats> ColorFormats { get; }
        public int ColorFormatDefault { get; }
        public int QMin { get; }
        public int QMax { get; }
        public int QDefault { get; }
        public string QInfo { get; } = "Does not alter quality.";
        public string PresetInfo { get; }

        public bool SupportsTwoPass { get; } = false;
        public bool ForceTwoPass { get; } = false;
        public bool DoesNotEncode { get; } = true;
        public bool IsFixedFormat { get; } = false;
        public bool IsSequence { get; } = false;

        public CodecArgs GetArgs(Dictionary<string, string> encArgs = null, MediaFile mediaFile = null, Pass pass = Pass.OneOfOne)
        {
            string cust = encArgs.ContainsKey("custom") ? encArgs["custom"] : "";
            return new CodecArgs($"-c:v copy {cust}");
        }
    }

    class StripVideo : IEncoder
    {
        public Streams.Stream.StreamType Type { get; } = Streams.Stream.StreamType.Video;
        public string Name { get { return GetType().Name; } }
        public string FriendlyName { get; } = "Disable (Strip Video)";
        public string[] Presets { get; } = new string[] { };
        public int PresetDefault { get; }
        public List<PixelFormats> ColorFormats { get; }
        public int ColorFormatDefault { get; }
        public int QMin { get; }
        public int QMax { get; }
        public int QDefault { get; }
        public string QInfo { get; }
        public string PresetInfo { get; }

        public bool SupportsTwoPass { get; } = false;
        public bool ForceTwoPass { get; } = false;
        public bool DoesNotEncode { get; } = true;
        public bool IsFixedFormat { get; } = false;
        public bool IsSequence { get; } = false;

        public CodecArgs GetArgs(Dictionary<string, string> encArgs = null, MediaFile mediaFile = null, Pass pass = Pass.OneOfOne)
        {
            string cust = encArgs.ContainsKey("custom") ? encArgs["custom"] : "";
            return new CodecArgs($"-vn {cust}");
        }
    }

    #endregion
}
