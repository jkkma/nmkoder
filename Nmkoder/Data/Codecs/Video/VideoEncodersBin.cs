using Nmkoder.Data.Colors;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Utils;
using System.Collections.Generic;
using System.Linq;

namespace Nmkoder.Data.Codecs.Video
{
    class AomAv1 : IEncoder
    {
        public Streams.Stream.StreamType Type { get; } = Streams.Stream.StreamType.Video;
        public string Name { get { return GetType().Name; } }
        public string FriendlyName { get; } = "AV1 (AOM)";
        public string[] Presets { get; } = new string[] { "0", "1", "2", "3", "4", "5", "6" };
        public int PresetDefault { get; } = 6;
        public List<PixelFormats> ColorFormats { get; } = new List<PixelFormats>() { PixelFormats.Yuv420P8, PixelFormats.Yuv422P8, PixelFormats.Yuv444P8, PixelFormats.Yuv420P10, PixelFormats.Yuv422P10, PixelFormats.Yuv444P10 };
        public int ColorFormatDefault { get; } = 3;
        public int QMin { get; } = 0;
        public int QMax { get; } = 63;
        public int QDefault { get; } = 20;
        public string QInfo { get; } = "CRF (0-63 - Lower is better)";
        public string PresetInfo { get; } = "Lower = Better compression";

        public bool SupportsTwoPass { get; } = false;
        public bool ForceTwoPass { get; } = false;
        public bool DoesNotEncode { get; } = false;
        public bool IsFixedFormat { get; } = false;
        public bool IsSequence { get; } = false;

        public CodecArgs GetArgs(Dictionary<string, string> encArgs = null, MediaFile mediaFile = null, Pass pass = Pass.OneOfOne)
        {
            string g = CodecUtils.GetKeyIntArg(mediaFile, Config.GetInt(Config.Key.DefaultKeyIntSecs), "");
            bool targetQual = encArgs.ContainsKey("qMode") && (UI.Tasks.Av1an.QualityMode)encArgs["qMode"].GetInt() != UI.Tasks.Av1an.QualityMode.Crf;
            string q = targetQual ? "0" : encArgs.ContainsKey("q") ? encArgs["q"] : QDefault.ToString();
            string preset = encArgs.ContainsKey("preset") ? encArgs["preset"] : Presets[PresetDefault];
            string pixFmt = encArgs.ContainsKey("pixFmt") ? encArgs["pixFmt"] : PixFmtUtils.GetFormat(ColorFormats[ColorFormatDefault]).Name;
            string grain = encArgs.ContainsKey("grainSynthStrength") ? encArgs["grainSynthStrength"] : "0";
            string thr = encArgs.ContainsKey("threads") ? encArgs["threads"] : "0";
            string denoise = encArgs.ContainsKey("grainSynthDenoise") ? (encArgs["grainSynthDenoise"].GetBool() ? "1" : "0") : "0";
            string tiles = CodecUtils.GetTilingArgs(CodecUtils.GetEncodedFrameSize(encArgs, mediaFile), "--tile-rows=", "--tile-columns=");
            string adv = encArgs.ContainsKey("advanced") ? encArgs["advanced"] : "";
            string colors = "";

            if (mediaFile != null && mediaFile.ColorData != null)
            {
                string prims = ColorDataUtils.GetColorPrimariesStringAom(mediaFile.ColorData.ColorPrimaries);
                string transfer = ColorDataUtils.GetColorTransferStringAom(mediaFile.ColorData.ColorTransfer);
                string matrixCoeffs = ColorDataUtils.GetColorMatrixCoeffsStringAom(mediaFile.ColorData.ColorMatrixCoeffs);
                colors = $"{(prims != "" ? $"--color-primaries={prims}" : "")} {(transfer != "" ? $"--transfer-characteristics={transfer}" : "")} {(matrixCoeffs != "" ? $"--matrix-coefficients={matrixCoeffs}" : "")}";

                if (mediaFile.ColorData.ColorPrimaries == 9) // HDR
                    colors += " --deltaq-mode=5 --enable-chroma-deltaq=1";
            }

            // No keyframe interval means the file had no video stream to work one out from. Leaving the
            // flag in without its value would make aomenc read the next argument as the interval.
            string kf = $"--disable-kf{(g.IsNotEmpty() ? $" --kf-min-dist=12 --kf-max-dist={g}" : "")}";

            // The same one-of-two the SVT-AV1 branch below makes, in aomenc's own spelling. Measured
            // against aomenc 3.8.2: --film-grain-table beats --denoise-noise-level outright - an encode
            // with both carries a grain table byte-identical to the one with the table alone - so sending
            // a strength beside a table would be sending a number that is silently discarded.
            string grainArgs = encArgs.ContainsKey("grainTable")
                ? $"--film-grain-table={encArgs["grainTable"]}"
                : $"--enable-dnl-denoising={denoise} --denoise-noise-level={grain}";

            // --end-usage=q stays even in the target quality modes: av1an's search only injects
            // --cq-level, which aomenc ignores unless constant quality rate control is selected.
            return new CodecArgs($" -e aom -v \" --end-usage=q {(!targetQual ? $"--cq-level={q}" : "")} --cpu-used={preset} {kf} " +
                    $"{grainArgs} {colors} --threads={thr} {tiles} {adv} \" --pix-format {pixFmt}");
        }
    }

    class SvtAv1 : IEncoder
    {
        public Streams.Stream.StreamType Type { get; } = Streams.Stream.StreamType.Video;
        public string Name { get { return GetType().Name; } }
        public string FriendlyName { get; } = "AV1 (SVT-AV1)";
        public string[] Presets { get; } = new string[] { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12" };
        public int PresetDefault { get; } = 4;
        public List<PixelFormats> ColorFormats { get; } = new List<PixelFormats>() { PixelFormats.Yuv420P8, PixelFormats.Yuv420P10 };
        public int ColorFormatDefault { get; } = 1;
        public int QMin { get; } = 0;
        public int QMax { get; } = 63;
        public int QDefault { get; } = 35;
        public string QInfo { get; } = "CRF (0-63 - Lower is better)";
        public string PresetInfo { get; } = "Lower = Better compression";

        public bool SupportsTwoPass { get; } = false;
        public bool ForceTwoPass { get; } = false;
        public bool DoesNotEncode { get; } = false;
        public bool IsFixedFormat { get; } = false;
        public bool IsSequence { get; } = false;

        public CodecArgs GetArgs(Dictionary<string, string> encArgs = null, MediaFile mediaFile = null, Pass pass = Pass.OneOfOne)
        {
            string g = CodecUtils.GetKeyIntArg(mediaFile, Config.GetInt(Config.Key.DefaultKeyIntSecs), "");
            bool targetQual = encArgs.ContainsKey("qMode") && (UI.Tasks.Av1an.QualityMode)encArgs["qMode"].GetInt() != UI.Tasks.Av1an.QualityMode.Crf;

            string q = targetQual ? "0" : encArgs.ContainsKey("q") ? encArgs["q"] : QDefault.ToString();
            string preset = encArgs.ContainsKey("preset") ? encArgs["preset"] : Presets[PresetDefault];
            string pixFmt = encArgs.ContainsKey("pixFmt") ? encArgs["pixFmt"] : PixFmtUtils.GetFormat(ColorFormats[ColorFormatDefault]).Name;
            string grain = encArgs.ContainsKey("grainSynthStrength") ? encArgs["grainSynthStrength"] : "0";
            string denoise = encArgs.ContainsKey("grainSynthDenoise") ? (encArgs["grainSynthDenoise"].GetBool() ? "1" : "0") : "0";
            string thr = encArgs.ContainsKey("threads") ? encArgs["threads"] : "0";
            string tiles = ""; // TEMP DISABLED AS IT SEEMS TO SLOW THINGS DOWN // = CodecUtils.GetTilingArgs(mediaFile.VideoStreams.FirstOrDefault().Resolution, "--tile-rows ", "--tile-columns ");
            string adv = encArgs.ContainsKey("advanced") ? ToSpaceSeparated(encArgs["advanced"]) : "";
            string colors = "";

            if (mediaFile != null && mediaFile.ColorData != null)
            {
                int range = mediaFile.ColorData.ColorRange == 2 ? 1 : 0; // SVT range is 0 (tv) and 1 (full), not 0 (unspecified), 1 (tv), 2 (full) like in VideoColorData
                colors = $"--color-primaries {mediaFile.ColorData.ColorPrimaries} --transfer-characteristics {mediaFile.ColorData.ColorTransfer} --matrix-coefficients {mediaFile.ColorData.ColorMatrixCoeffs} --color-range {range}";
                // The mastering display and light levels, for the same reason as the four tags above:
                // y4m carries no side data, so this is the only way the encoder can learn them. Written
                // unwrapped because it lands inside av1an's own -v "…" string - see the helper.
                colors = $"{colors} {ColorDataUtils.GetSvtHdrMetadataArgs(mediaFile.ColorData, wrapValues: false)}".Trim();
            }
            
            string keyint = g.IsNotEmpty() ? $"--keyint {g}" : ""; // No video stream to work an interval out from

            // The Grain Synthesis row writes one of these two and never both - SVT takes --fgs-table over
            // --film-grain and says so only in a warning nothing here reads, so sending a strength beside a
            // table would be sending a number that is silently discarded. The denoise flag goes with the
            // strength for the same reason: it is read only on the --film-grain path.
            // The path is written bare rather than quoted: everything in here ends up inside av1an's own
            // -v "…" string, which is split again before it reaches the encoder, and a quote of this app's
            // own would be one more layer than that split accounts for. GrainSynthUi.ResolveDeliveryAsync
            // keeps a path with a space in it away from this argument entirely for the same reason.
            string grainArgs = encArgs.ContainsKey("grainTable")
                ? $"--fgs-table {encArgs["grainTable"]}"
                : $"--film-grain {grain} --film-grain-denoise {denoise}";

            return new CodecArgs($" -e svt-av1 --force -v \" --preset {preset} {(!targetQual ? $"--crf {q}" : "")} {keyint} --lp {thr} {grainArgs} {colors} {tiles} {adv} \" --pix-format {pixFmt}");
        }

        /// <summary>
        /// The advanced grid's "--key=value" arguments in the "--key value" form SVT-AV1 expects. Only
        /// the first '=' separates the two: replacing every one of them broke any value containing one,
        /// which is how SVT's own grouped parameters are written.
        /// </summary>
        private static string ToSpaceSeparated(string args)
        {
            return string.Join(" ", (args ?? "").Split(' ').Select(a =>
            {
                int at = a.IndexOf('=');
                return at < 0 ? a : $"{a.Substring(0, at)} {a.Substring(at + 1)}";
            }));
        }
    }

    class Vpx : IEncoder
    {
        public Streams.Stream.StreamType Type { get; } = Streams.Stream.StreamType.Video;
        public string Name { get { return GetType().Name; } }
        public string FriendlyName { get; } = "VP9 (VPX)";
        public string[] Presets { get; } = new string[] { "0", "1", "2", "3", "4", "5", "6" };
        public int PresetDefault { get; } = 3;
        public List<PixelFormats> ColorFormats { get; } = new List<PixelFormats>() { PixelFormats.Yuv420P8, PixelFormats.Yuva420P8, PixelFormats.Yuv444P8, PixelFormats.Yuv420P10, PixelFormats.Yuv444P10 };
        public int ColorFormatDefault { get; } = 3; // 10-bit 4:2:0 - VP9 encodes 8-bit sources better in profile 2
        public int QMin { get; } = 0;
        public int QMax { get; } = 50;
        public int QDefault { get; } = 20;
        public string QInfo { get; } = "CRF (0-50 - Lower is better)";
        public string PresetInfo { get; } = "Lower = Better compression";

        public bool SupportsTwoPass { get; } = false;
        public bool ForceTwoPass { get; } = false;
        public bool DoesNotEncode { get; } = false;
        public bool IsFixedFormat { get; } = false;
        public bool IsSequence { get; } = false;

        public CodecArgs GetArgs(Dictionary<string, string> encArgs = null, MediaFile mediaFile = null, Pass pass = Pass.OneOfOne)
        {
            string g = CodecUtils.GetKeyIntArg(mediaFile, Config.GetInt(Config.Key.DefaultKeyIntSecs), "");
            bool targetQual = encArgs.ContainsKey("qMode") && (UI.Tasks.Av1an.QualityMode)encArgs["qMode"].GetInt() != UI.Tasks.Av1an.QualityMode.Crf;
            string q = targetQual ? "0" : encArgs.ContainsKey("q") ? encArgs["q"] : QDefault.ToString();
            string preset = encArgs.ContainsKey("preset") ? encArgs["preset"] : Presets[PresetDefault];
            string thr = encArgs.ContainsKey("threads") ? encArgs["threads"] : "0";
            string pixFmt = encArgs.ContainsKey("pixFmt") ? encArgs["pixFmt"] : PixFmtUtils.GetFormat(ColorFormats[ColorFormatDefault]).Name;
            bool is420 = !(pixFmt.Contains("444") || pixFmt.Contains("422"));
            int b = pixFmt.Split('p').LastOrDefault().GetInt();
            b = (b > 0) ? b : 8; // Make bit depth default to 8 if it was detected as 0 (e.g. when using yuv420p which does not explicitly specify 8-bit)
            int p = b > 8 ? (is420 ? 2 : 3) : (is420 ? 0 : 1); // Profile 0: 4:2:0 8-bit | Profile 1: 4:2:2/4:4:4 8-bit | Profile 2: 4:2:0 10/12-bit | Profile 3: 4:2:2/4:4:4 10/12-bit
            string tiles = CodecUtils.GetTilingArgs(CodecUtils.GetEncodedFrameSize(encArgs, mediaFile), "--tile-rows=", "--tile-columns=");
            string adv = encArgs.ContainsKey("advanced") ? encArgs["advanced"] : ""; // vpxenc takes --flag=value, as written

            string kf = g.IsNotEmpty() ? $"--kf-max-dist={g}" : ""; // No video stream to work an interval out from

            // As with aomenc, --end-usage=q has to be set for av1an's injected --cq-level to apply
            return new CodecArgs($" -e vpx --force -v \" --codec=vp9 --profile={p} --bit-depth={b} --end-usage=q {(!targetQual ? $"--cq-level={q}" : "")} --cpu-used={preset} {kf} " +
                    $"--threads={thr} --row-mt=1 {tiles} {adv} \" --pix-format {pixFmt}");
        }
    }

    class X264 : IEncoder
    {
        public Streams.Stream.StreamType Type { get; } = Streams.Stream.StreamType.Video;
        public string Name { get { return GetType().Name; } }
        public string FriendlyName { get; } = "H.264 / AVC (x264)";
        public string[] Presets { get; } = new string[] { "veryslow", "slower", "slow", "medium", "fast", "faster", "veryfast", "superfast", "ultrafast" };
        public int PresetDefault { get; } = 3;
        public List<PixelFormats> ColorFormats { get; } = new List<PixelFormats>() { PixelFormats.Yuv420P8, PixelFormats.Yuv422P8, PixelFormats.Yuv444P8, PixelFormats.Yuv420P10, PixelFormats.Yuv422P10, PixelFormats.Yuv444P10 };
        public int ColorFormatDefault { get; } = 0;
        public int QMin { get; } = 0;
        public int QMax { get; } = 51;
        public int QDefault { get; } = 20;
        public string QInfo { get; } = "CRF (0-51 - Lower is better)";
        public string PresetInfo { get; } = "Slower = Better compression";

        public bool SupportsTwoPass { get; } = false;
        public bool ForceTwoPass { get; } = false;
        public bool DoesNotEncode { get; } = false;
        public bool IsFixedFormat { get; } = false;
        public bool IsSequence { get; } = false;

        public CodecArgs GetArgs(Dictionary<string, string> encArgs = null, MediaFile mediaFile = null, Pass pass = Pass.OneOfOne)
        {
            bool targetQual = encArgs.ContainsKey("qMode") && (UI.Tasks.Av1an.QualityMode)encArgs["qMode"].GetInt() != UI.Tasks.Av1an.QualityMode.Crf;
            string q = targetQual ? "0" : encArgs.ContainsKey("q") ? encArgs["q"] : QDefault.ToString();
            string preset = encArgs.ContainsKey("preset") ? encArgs["preset"] : Presets[PresetDefault];
            string pixFmt = encArgs.ContainsKey("pixFmt") ? encArgs["pixFmt"] : PixFmtUtils.GetFormat(ColorFormats[ColorFormatDefault]).Name;
            int bitDepth = FormatUtils.GetBitDepthFromPixelFormat(pixFmt);
            string thr = encArgs.ContainsKey("threads") ? encArgs["threads"] : "0";
            string adv = encArgs.ContainsKey("advanced") ? encArgs["advanced"] : "";
            string colors = "";

            if (mediaFile != null && mediaFile.ColorData != null)
            {
                string prims = ColorDataUtils.GetColorPrimariesStringX264(mediaFile.ColorData.ColorPrimaries);
                string transfer = ColorDataUtils.GetColorTransferStringX264(mediaFile.ColorData.ColorTransfer);
                string matrix = ColorDataUtils.GetColorMatrixCoeffsStringX264(mediaFile.ColorData.ColorMatrixCoeffs);
                string range = ColorDataUtils.GetColorRangeString(mediaFile.ColorData.ColorRange); // x264 speaks tv/pc
                colors = $"{(prims != "" ? $"--colorprim {prims}" : "")} {(transfer != "" ? $"--transfer {transfer}" : "")} " +
                    $"{(matrix != "" ? $"--colormatrix {matrix}" : "")} {(range != "" ? $"--range {range}" : "")}";
            }

            // 8-bit is x264's own default, and a build without high bit depth support rejects the
            // flag outright, so it is only worth sending when something other than 8 is wanted.
            string depth = bitDepth > 8 ? $"--output-depth {bitDepth}" : "";

            // No --keyint: av1an cuts the scenes itself and sets "--keyint infinite --scenecut 0"
            // for x264, so every chunk already opens on a keyframe. Sending an interval here would
            // override that and put more of them inside the chunks.
            return new CodecArgs($" -e x264 --force -v \" {(!targetQual ? $"--crf {q}" : "")} --preset {preset} --threads {thr} {depth} {colors} {adv} \" --pix-format {pixFmt}");
        }
    }

    class X265 : IEncoder
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
        public int QDefault { get; } = 20;
        public string QInfo { get; } = "CRF (0-51 - Lower is better)";
        public string PresetInfo { get; } = "Slower = Better compression";

        public bool SupportsTwoPass { get; } = false;
        public bool ForceTwoPass { get; } = false;
        public bool DoesNotEncode { get; } = false;
        public bool IsFixedFormat { get; } = false;
        public bool IsSequence { get; } = false;

        public CodecArgs GetArgs(Dictionary<string, string> encArgs = null, MediaFile mediaFile = null, Pass pass = Pass.OneOfOne)
        {
            string g = CodecUtils.GetKeyIntArg(mediaFile, Config.GetInt(Config.Key.DefaultKeyIntSecs), "");
            bool targetQual = encArgs.ContainsKey("qMode") && (UI.Tasks.Av1an.QualityMode)encArgs["qMode"].GetInt() != UI.Tasks.Av1an.QualityMode.Crf;
            string q = targetQual ? "0" : encArgs.ContainsKey("q") ? encArgs["q"] : QDefault.ToString();
            string preset = encArgs.ContainsKey("preset") ? encArgs["preset"] : Presets[PresetDefault];
            string pixFmt = encArgs.ContainsKey("pixFmt") ? encArgs["pixFmt"] : PixFmtUtils.GetFormat(ColorFormats[ColorFormatDefault]).Name;
            int bitDepth = FormatUtils.GetBitDepthFromPixelFormat(pixFmt);
            string thr = encArgs.ContainsKey("threads") ? encArgs["threads"] : "0";
            string adv = encArgs.ContainsKey("advanced") ? encArgs["advanced"] : "";
            string colors = "";

            if (mediaFile != null && mediaFile.ColorData != null)
            {
                string range = mediaFile.ColorData.ColorRange == 2 ? "full" : "limited"; // x265 range is "limited" (tv) and "full", not 0 (unspecified), 1 (tv), 2 (full) like in VideoColorData
                colors = $"--colorprim {mediaFile.ColorData.ColorPrimaries} --transfer {mediaFile.ColorData.ColorTransfer} --colormatrix {mediaFile.ColorData.ColorMatrixCoeffs} --range {range}";
            }

            string keyint = g.IsNotEmpty() ? $"--keyint {g}" : ""; // No video stream to work an interval out from
            string depth = bitDepth > 0 ? $"--output-depth {bitDepth}" : ""; // Unrecognised pixel format - let x265 pick

            // --pools, not --frame-threads. x265 is the one encoder here with no --threads at all, and
            // its frame threads are the number of frames encoded *concurrently* - the worker pool
            // underneath them is one thread per core whatever F is set to, so "Threads per Worker" was
            // the only setting on this tab that did not limit any threads. Eight workers on a sixteen
            // core machine ran eight sixteen-thread pools. --pools *is* that pool, and x265 derives its
            // own frame thread count from the size of it, which is why F is no longer sent.
            string pools = thr.GetInt() > 0 ? $"--pools {thr}" : "";

            return new CodecArgs($" -e x265 --force -v \" {(!targetQual ? $"--crf {q}" : "")} --preset {preset} {keyint} {pools} {depth} {colors} {adv} \" --pix-format {pixFmt}");
        }
    }
}
