using Nmkoder.Data.Colors;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.UI.Tasks;
using Nmkoder.OS;
using Nmkoder.Utils;
using System.Collections.Generic;
using System.Linq;

namespace Nmkoder.Data.Codecs.Video
{
    /// <summary>
    /// An encoder Quick Convert launches as its own process rather than reaching through ffmpeg, fed
    /// raw frames on stdin by an ffmpeg that does the decoding and the filtering.
    /// <para/>
    /// **These are the same binaries the AV1AN tab drives and not the same arguments.** The classes in
    /// <c>VideoEncodersBin</c> build an *av1an* command - <c>-e svt-av1 --force -v "…"</c>, with the
    /// encoder's own parameters inside a quoted string av1an splits again - where these build the
    /// command line the binary itself is launched with. The two could not be shared: av1an owns the
    /// input, the output, the chunking and the pixel format, and every one of those is this app's to
    /// state when it is the one launching the encoder.
    /// <para/>
    /// The pipeline is <c>ffmpeg … -f yuv4mpegpipe - | &lt;encoder&gt; &lt;io&gt; &lt;settings&gt;</c>,
    /// then a second ffmpeg muxing the elementary stream with the audio, subtitles, chapters and
    /// metadata that never went down the pipe. y4m carries the frame size, the rate and the range and
    /// **nothing else** - measured, the header is
    /// <c>YUV4MPEG2 W320 H240 F24:1 Ip A1:1 C420mpeg2 XYSCSS=420MPEG2 XCOLORRANGE=LIMITED</c> - so the
    /// primaries, the transfer curve and the matrix have to be handed to the encoder by flag, in each
    /// one's own spelling, exactly as the av1an classes already do.
    /// </summary>
    interface IBinaryEncoder : IEncoder
    {
        /// <summary> The executable, as <see cref="Media.AvProcess.IsToolAvailable"/> would look for it. </summary>
        string ToolName { get; }

        /// <summary>
        /// Which parameter list in <c>bin/encoderArgs/av1an/</c> describes this binary's own CLI.
        /// <para/>
        /// It is the AV1AN tab's list because it is the same binary: those files name the parameters
        /// <c>SvtAv1EncApp</c> and friends actually take, where <c>bin/encoderArgs/ffmpeg/</c> names
        /// what ffmpeg's *wrapper* takes, and the two are different vocabularies rather than different
        /// spellings. Feeding an ffmpeg list to a CLI binary is silent on three of the five encoders -
        /// x264, x265 and SVT-AV1 warn and encode anyway - so this is not something to leave to a
        /// filename coincidence. Named separately from <see cref="IEncoder.Name"/> because the class is
        /// <c>DirectX264</c> and the list has always been <c>X264.json</c>.
        /// </summary>
        string ArgListName { get; }

        /// <summary> What the encoder writes: the extension of the elementary stream or bare container
        /// it produces before anything else is muxed into it. </summary>
        string StreamExt { get; }

        /// <summary>
        /// Whether ffmpeg can copy the encoder's output into the final container as it stands.
        /// <para/>
        /// **False means raw Annex B, and that is not a detail.** No ffmpeg route can stamp one
        /// correctly: read back with <c>-framerate N</c> its packets have no timestamps, Matroska
        /// refuses them outright, and the muxers that stamp them (the MP4 family) write pts in
        /// *decode* order - frames in the right sequence carrying the wrong times, a dup-and-drop
        /// judder at every mini-GOP once B-frames reorder. The <c>setts</c> bitstream filter has the
        /// same fault by construction. So a false here means mkvmerge containerises the stream before
        /// the mux, whatever the output container, and the run refuses up front when there is no
        /// mkvmerge - see <c>QuickConvert.BuildDirectCommand</c>, where the measurements live.
        /// </summary>
        bool StreamCarriesTiming { get; }

        /// <summary> How this encoder is told to read the pipe and where to write, which is the half of
        /// the command line that is not a setting. </summary>
        string GetIoArgs(string outPath);

        /// <summary>
        /// The rate-control pass flags, given the file the first pass writes its statistics to.
        /// <para/>
        /// **All five take a two-pass run and they spell it two ways**, measured rather than read:
        /// x264, x265 and SvtAv1EncApp take <c>--pass N --stats FILE</c>, while aomenc and vpxenc take
        /// <c>--passes=2 --pass=N --fpf=FILE</c> and need <c>--passes=1</c> stating explicitly for a
        /// single-pass run. x265 writes a second file beside the one it is given (<c>FILE.cutree</c>),
        /// so the stats path is a *stem* to clean up rather than one file.
        /// <para/>
        /// The first pass writes its bitstream to the same intermediate the second pass overwrites,
        /// rather than to a null sink - <c>/dev/null</c> and <c>NUL</c> are not the same word, and there
        /// is nothing to be gained by discovering that on Windows.
        /// </summary>
        string GetPassArgs(Pass pass, string statsPath);
    }

    /// <summary> Shared spellings for the direct encoders. </summary>
    static class DirectEncoderUtils
    {
        /// <summary> Whether a bitrate rather than a quality level is being targeted. Quick Convert's
        /// modes, not av1an's - this tab has no target-quality search. </summary>
        public static bool IsVbr(Dictionary<string, string> encArgs)
        {
            return encArgs != null && encArgs.ContainsKey("qMode") &&
                (UI.Tasks.QuickConvert.QualityMode)encArgs["qMode"].GetInt() != UI.Tasks.QuickConvert.QualityMode.Crf;
        }

        public static int Kbps(Dictionary<string, string> encArgs)
        {
            return encArgs != null && encArgs.ContainsKey("bitrate") ? encArgs["bitrate"].GetInt() : 0;
        }

        public static string Get(Dictionary<string, string> encArgs, string key, string fallback = "")
        {
            return encArgs != null && encArgs.ContainsKey(key) ? encArgs[key] : fallback;
        }

        /// <summary> The "--pass N --stats FILE" spelling, which x264, x265 and SvtAv1EncApp share. </summary>
        public static string StatsPassArgs(Pass pass, string statsPath)
        {
            if (pass == Pass.OneOfOne)
                return "";

            return $"--pass {(pass == Pass.OneOfTwo ? 1 : 2)} --stats {Shell.WrapArg(statsPath)}";
        }

        /// <summary> The "--passes=N --pass=N --fpf=FILE" spelling aomenc and vpxenc share. Unlike the
        /// three above, these two want the *total* pass count stating even for a single-pass run. </summary>
        public static string FpfPassArgs(Pass pass, string statsPath)
        {
            if (pass == Pass.OneOfOne)
                return "--passes=1";

            return $"--passes=2 --pass={(pass == Pass.OneOfTwo ? 1 : 2)} --fpf={Shell.WrapArg(statsPath)}";
        }

        /// <summary> The advanced grid's "--key=value" pairs in the "--key value" form SVT-AV1, x264 and
        /// x265 take. Only the first '=' separates the two, because a value can hold one - which is how
        /// SVT's own grouped parameters are written. Copied in behaviour from
        /// <c>VideoEncodersBin.SvtAv1.ToSpaceSeparated</c>, which the AV1AN tab uses for the same lists. </summary>
        public static string ToSpaceSeparated(string args)
        {
            return string.Join(" ", (args ?? "").Split(' ').Select(a =>
            {
                int at = a.IndexOf('=');
                return at < 0 ? a : $"{a.Substring(0, at)} {a.Substring(at + 1)}";
            }));
        }
    }

    class DirectSvtAv1 : IBinaryEncoder
    {
        public Streams.Stream.StreamType Type { get; } = Streams.Stream.StreamType.Video;
        public string Name { get { return GetType().Name; } }
        public string FriendlyName { get; } = "AV1 (SVT-AV1)";
        public string ToolName { get; } = "SvtAv1EncApp";
        public string ArgListName { get; } = "SvtAv1";
        // IVF carries the frame rate in its header, so the mux needs to be told nothing.
        public string StreamExt { get; } = "ivf";
        public bool StreamCarriesTiming { get; } = true;
        public string[] Presets { get; } = new string[] { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12" };
        public int PresetDefault { get; } = 4;
        public List<PixelFormats> ColorFormats { get; } = new List<PixelFormats>() { PixelFormats.Yuv420P8, PixelFormats.Yuv420P10 };
        public int ColorFormatDefault { get; } = 1;
        public int QMin { get; } = 0;
        public int QMax { get; } = 63;
        public int QDefault { get; } = 30;
        public string QInfo { get; } = "CRF (0-63 - Lower is better)";
        public string PresetInfo { get; } = "Lower = Better compression";

        public bool SupportsTwoPass { get; } = true;
        public bool ForceTwoPass { get; } = false;
        public bool DoesNotEncode { get; } = false;
        public bool IsFixedFormat { get; } = false;
        public bool IsSequence { get; } = false;

        public string GetIoArgs(string outPath)
        {
            return $"-i stdin -b {Shell.WrapArg(outPath)}";
        }

        public string GetPassArgs(Pass pass, string statsPath)
        {
            return DirectEncoderUtils.StatsPassArgs(pass, statsPath);
        }

        public CodecArgs GetArgs(Dictionary<string, string> encArgs = null, MediaFile mediaFile = null, Pass pass = Pass.OneOfOne)
        {
            bool vbr = DirectEncoderUtils.IsVbr(encArgs);
            string g = CodecUtils.GetKeyIntArg(mediaFile, Config.GetInt(Config.Key.DefaultKeyIntSecs), "", 480, QuickConvertUi.GetPostFilterRate());
            string q = DirectEncoderUtils.Get(encArgs, "q", QDefault.ToString());
            string preset = DirectEncoderUtils.Get(encArgs, "preset", Presets[PresetDefault]);
            string adv = DirectEncoderUtils.ToSpaceSeparated(DirectEncoderUtils.Get(encArgs, "advanced"));
            // --rc 1 is VBR; --tbr is in kbps here, matching the spinner.
            string rc = vbr ? $"--rc 1 --tbr {DirectEncoderUtils.Kbps(encArgs)}" : $"--crf {q}";
            string keyint = g.IsNotEmpty() ? $"--keyint {g}" : "";
            string colors = "";

            if (mediaFile != null && mediaFile.ColorData != null)
            {
                // SVT's range is 0 (tv) / 1 (full), not VideoColorData's 0 (unspecified) / 1 / 2.
                int range = mediaFile.ColorData.ColorRange == 2 ? 1 : 0;
                colors = $"--color-primaries {mediaFile.ColorData.ColorPrimaries} " +
                    $"--transfer-characteristics {mediaFile.ColorData.ColorTransfer} " +
                    $"--matrix-coefficients {mediaFile.ColorData.ColorMatrixCoeffs} --color-range {range}";
                // The mastering display and light levels, for the same reason as the four tags above.
                // Wrapped here where the AV1AN path writes them bare: this command launches the binary
                // itself, so the mastering display's parentheses are the shell's to read - see the helper.
                colors = $"{colors} {ColorDataUtils.GetSvtHdrMetadataArgs(mediaFile.ColorData, wrapValues: true)}".Trim();
            }

            // One of the two and never both: SVT takes --fgs-table over --film-grain and says so only in
            // a warning written to its own stderr. The same choice the AV1AN tab's SvtAv1 class makes.
            string grainArgs = encArgs != null && encArgs.ContainsKey("grainTable")
                ? $"--fgs-table {Shell.WrapArg(encArgs["grainTable"])}"
                : $"--film-grain {DirectEncoderUtils.Get(encArgs, "grainSynthStrength", "0")} " +
                  $"--film-grain-denoise {(DirectEncoderUtils.Get(encArgs, "grainSynthDenoise", "False").GetBool() ? 1 : 0)}";

            return new CodecArgs($"--preset {preset} {rc} {keyint} {grainArgs} {colors} {adv}");
        }
    }

    class DirectAomAv1 : IBinaryEncoder
    {
        public Streams.Stream.StreamType Type { get; } = Streams.Stream.StreamType.Video;
        public string Name { get { return GetType().Name; } }
        public string FriendlyName { get; } = "AV1 (AOM)";
        public string ToolName { get; } = "aomenc";
        public string ArgListName { get; } = "AomAv1";
        public string StreamExt { get; } = "ivf";
        public bool StreamCarriesTiming { get; } = true;
        public string[] Presets { get; } = new string[] { "0", "1", "2", "3", "4", "5", "6", "7", "8" };
        public int PresetDefault { get; } = 6;
        public List<PixelFormats> ColorFormats { get; } = new List<PixelFormats>() { PixelFormats.Yuv420P8, PixelFormats.Yuv422P8, PixelFormats.Yuv444P8, PixelFormats.Yuv420P10, PixelFormats.Yuv422P10, PixelFormats.Yuv444P10 };
        public int ColorFormatDefault { get; } = 3;
        public int QMin { get; } = 0;
        public int QMax { get; } = 63;
        public int QDefault { get; } = 30;
        public string QInfo { get; } = "CQ level (0-63 - Lower is better)";
        public string PresetInfo { get; } = "Lower = Better compression";

        public bool SupportsTwoPass { get; } = true;
        public bool ForceTwoPass { get; } = false;
        public bool DoesNotEncode { get; } = false;
        public bool IsFixedFormat { get; } = false;
        public bool IsSequence { get; } = false;

        // The trailing "-" is the input: aomenc reads y4m from stdin under that name.
        public string GetIoArgs(string outPath)
        {
            return $"--ivf -o {Shell.WrapArg(outPath)} -";
        }

        public string GetPassArgs(Pass pass, string statsPath)
        {
            return DirectEncoderUtils.FpfPassArgs(pass, statsPath);
        }

        public CodecArgs GetArgs(Dictionary<string, string> encArgs = null, MediaFile mediaFile = null, Pass pass = Pass.OneOfOne)
        {
            bool vbr = DirectEncoderUtils.IsVbr(encArgs);
            string g = CodecUtils.GetKeyIntArg(mediaFile, Config.GetInt(Config.Key.DefaultKeyIntSecs), "", 480, QuickConvertUi.GetPostFilterRate());
            string q = DirectEncoderUtils.Get(encArgs, "q", QDefault.ToString());
            string preset = DirectEncoderUtils.Get(encArgs, "preset", Presets[PresetDefault]);
            string adv = DirectEncoderUtils.Get(encArgs, "advanced"); // aomenc takes --flag=value, as the list writes it
            string tiles = CodecUtils.GetTilingArgs(CodecUtils.GetEncodedFrameSize(encArgs, mediaFile), "--tile-rows=", "--tile-columns=");
            string rc = vbr
                ? $"--end-usage=vbr --target-bitrate={DirectEncoderUtils.Kbps(encArgs)}"
                : $"--end-usage=q --cq-level={q}";
            string kf = g.IsNotEmpty() ? $"--kf-max-dist={g}" : "";
            string colors = "";

            if (mediaFile != null && mediaFile.ColorData != null)
            {
                // aomenc takes names rather than H.273 integers, and refuses one it does not know by
                // printing its usage and encoding nothing - see ColorDataUtils' three aom tables.
                string prims = ColorDataUtils.GetColorPrimariesStringAom(mediaFile.ColorData.ColorPrimaries);
                string transfer = ColorDataUtils.GetColorTransferStringAom(mediaFile.ColorData.ColorTransfer);
                string matrix = ColorDataUtils.GetColorMatrixCoeffsStringAom(mediaFile.ColorData.ColorMatrixCoeffs);
                colors = $"{(prims.IsNotEmpty() ? $"--color-primaries={prims}" : "")} " +
                    $"{(transfer.IsNotEmpty() ? $"--transfer-characteristics={transfer}" : "")} " +
                    $"{(matrix.IsNotEmpty() ? $"--matrix-coefficients={matrix}" : "")}";
            }

            // Measured against aomenc 3.8.2: --film-grain-table beats --denoise-noise-level outright, so
            // a strength sent beside a table would be a number silently discarded.
            string grainArgs = encArgs != null && encArgs.ContainsKey("grainTable")
                ? $"--film-grain-table={Shell.WrapArg(encArgs["grainTable"])}"
                : $"--denoise-noise-level={DirectEncoderUtils.Get(encArgs, "grainSynthStrength", "0")} " +
                  $"--enable-dnl-denoising={(DirectEncoderUtils.Get(encArgs, "grainSynthDenoise", "False").GetBool() ? 1 : 0)}";

            // Resolved by the run rather than written here, the lookup being async - see
            // CodecUtils.GetNoPromptArg. Without it a min-q within 8 of max-q, which this encoder's own
            // argument rows offer, has aomenc ask "Continue? (y to continue)" and read a byte of the
            // y4m as the answer, exiting 1 with nothing written.
            string noPrompt = DirectEncoderUtils.Get(encArgs, CodecUtils.NoPromptKey);

            return new CodecArgs($"{rc} --cpu-used={preset} {kf} --row-mt=1 {grainArgs} {colors} {tiles} {noPrompt} {adv}");
        }
    }

    class DirectVpx : IBinaryEncoder
    {
        public Streams.Stream.StreamType Type { get; } = Streams.Stream.StreamType.Video;
        public string Name { get { return GetType().Name; } }
        public string FriendlyName { get; } = "VP9 (VPX)";
        public string ToolName { get; } = "vpxenc";
        public string ArgListName { get; } = "Vpx";
        public string StreamExt { get; } = "ivf";
        public bool StreamCarriesTiming { get; } = true;
        public string[] Presets { get; } = new string[] { "0", "1", "2", "3", "4", "5", "6", "7", "8" };
        public int PresetDefault { get; } = 3;
        public List<PixelFormats> ColorFormats { get; } = new List<PixelFormats>() { PixelFormats.Yuv420P8, PixelFormats.Yuv444P8, PixelFormats.Yuv420P10, PixelFormats.Yuv444P10 };
        public int ColorFormatDefault { get; } = 2;
        public int QMin { get; } = 0;
        public int QMax { get; } = 63;
        public int QDefault { get; } = 30;
        public string QInfo { get; } = "CQ level (0-63 - Lower is better)";
        public string PresetInfo { get; } = "Lower = Better compression";

        public bool SupportsTwoPass { get; } = true;
        public bool ForceTwoPass { get; } = false;
        public bool DoesNotEncode { get; } = false;
        public bool IsFixedFormat { get; } = false;
        public bool IsSequence { get; } = false;

        public string GetIoArgs(string outPath)
        {
            return $"--ivf -o {Shell.WrapArg(outPath)} -";
        }

        public string GetPassArgs(Pass pass, string statsPath)
        {
            return DirectEncoderUtils.FpfPassArgs(pass, statsPath);
        }

        public CodecArgs GetArgs(Dictionary<string, string> encArgs = null, MediaFile mediaFile = null, Pass pass = Pass.OneOfOne)
        {
            bool vbr = DirectEncoderUtils.IsVbr(encArgs);
            string g = CodecUtils.GetKeyIntArg(mediaFile, Config.GetInt(Config.Key.DefaultKeyIntSecs), "", 480, QuickConvertUi.GetPostFilterRate());
            string q = DirectEncoderUtils.Get(encArgs, "q", QDefault.ToString());
            string preset = DirectEncoderUtils.Get(encArgs, "preset", Presets[PresetDefault]);
            string adv = DirectEncoderUtils.Get(encArgs, "advanced"); // vpxenc takes --flag=value too
            string pixFmt = DirectEncoderUtils.Get(encArgs, "pixFmt", PixFmtUtils.GetFormat(ColorFormats[ColorFormatDefault]).Name);
            string tiles = CodecUtils.GetTilingArgs(CodecUtils.GetEncodedFrameSize(encArgs, mediaFile), "--tile-rows=", "--tile-columns=");

            // Profile 0: 4:2:0 8-bit | 1: 4:2:2/4:4:4 8-bit | 2: 4:2:0 10/12-bit | 3: 4:2:2/4:4:4 10/12-bit.
            // The same derivation VideoEncodersBin.Vpx makes; vpxenc will not pick it from the y4m.
            bool is420 = !(pixFmt.Contains("444") || pixFmt.Contains("422"));
            int depth = pixFmt.Split('p').LastOrDefault().GetInt();
            depth = depth > 0 ? depth : 8;
            int profile = depth > 8 ? (is420 ? 2 : 3) : (is420 ? 0 : 1);

            string rc = vbr
                ? $"--end-usage=vbr --target-bitrate={DirectEncoderUtils.Kbps(encArgs)}"
                : $"--end-usage=q --cq-level={q}";
            string kf = g.IsNotEmpty() ? $"--kf-max-dist={g}" : "";
            // The same prompt aomenc has, from the same shared vpx argument handling, reachable from
            // the same min-q/max-q rows - see CodecUtils.GetNoPromptArg.
            string noPrompt = DirectEncoderUtils.Get(encArgs, CodecUtils.NoPromptKey);

            return new CodecArgs($"--codec=vp9 --profile={profile} --bit-depth={depth} {rc} " +
                $"--cpu-used={preset} {kf} --row-mt=1 {tiles} {noPrompt} {adv}");
        }
    }

    class DirectX264 : IBinaryEncoder
    {
        public Streams.Stream.StreamType Type { get; } = Streams.Stream.StreamType.Video;
        public string Name { get { return GetType().Name; } }
        public string FriendlyName { get; } = "H.264 / AVC (x264)";
        public string ToolName { get; } = "x264";
        public string ArgListName { get; } = "X264";
        // Raw Annex B. x264 can mux Matroska itself where its build has the muxer, but that is a build
        // option rather than a promise - and the containerise step below is needed for x265 regardless,
        // so both Annex B encoders take the same route rather than each taking its own.
        public string StreamExt { get; } = "264";
        public bool StreamCarriesTiming { get; } = false;
        public string[] Presets { get; } = new string[] { "veryslow", "slower", "slow", "medium", "fast", "faster", "veryfast", "superfast", "ultrafast" };
        public int PresetDefault { get; } = 3;
        public List<PixelFormats> ColorFormats { get; } = new List<PixelFormats>() { PixelFormats.Yuv420P8, PixelFormats.Yuv422P8, PixelFormats.Yuv444P8, PixelFormats.Yuv420P10, PixelFormats.Yuv422P10, PixelFormats.Yuv444P10 };
        public int ColorFormatDefault { get; } = 0;
        public int QMin { get; } = 0;
        public int QMax { get; } = 51;
        public int QDefault { get; } = 20;
        public string QInfo { get; } = "CRF (0-51 - Lower is better)";
        public string PresetInfo { get; } = "Slower = Better compression";

        public bool SupportsTwoPass { get; } = true;
        public bool ForceTwoPass { get; } = false;
        public bool DoesNotEncode { get; } = false;
        public bool IsFixedFormat { get; } = false;
        public bool IsSequence { get; } = false;

        public string GetIoArgs(string outPath)
        {
            return $"--demuxer y4m -o {Shell.WrapArg(outPath)} -";
        }

        public string GetPassArgs(Pass pass, string statsPath)
        {
            return DirectEncoderUtils.StatsPassArgs(pass, statsPath);
        }

        public CodecArgs GetArgs(Dictionary<string, string> encArgs = null, MediaFile mediaFile = null, Pass pass = Pass.OneOfOne)
        {
            bool vbr = DirectEncoderUtils.IsVbr(encArgs);
            string g = CodecUtils.GetKeyIntArg(mediaFile, Config.GetInt(Config.Key.DefaultKeyIntSecs), "", 480, QuickConvertUi.GetPostFilterRate());
            string q = DirectEncoderUtils.Get(encArgs, "q", QDefault.ToString());
            string preset = DirectEncoderUtils.Get(encArgs, "preset", Presets[PresetDefault]);
            string adv = DirectEncoderUtils.ToSpaceSeparated(DirectEncoderUtils.Get(encArgs, "advanced"));
            string pixFmt = DirectEncoderUtils.Get(encArgs, "pixFmt", PixFmtUtils.GetFormat(ColorFormats[ColorFormatDefault]).Name);
            int bitDepth = FormatUtils.GetBitDepthFromPixelFormat(pixFmt);
            string rc = vbr ? $"--bitrate {DirectEncoderUtils.Kbps(encArgs)}" : $"--crf {q}";
            string keyint = g.IsNotEmpty() ? $"--keyint {g}" : "";
            // 8-bit is x264's own default and a build without high bit depth support refuses the flag,
            // so it only goes out when something other than 8 is wanted.
            string depth = bitDepth > 8 ? $"--output-depth {bitDepth}" : "";
            string colors = "";

            if (mediaFile != null && mediaFile.ColorData != null)
            {
                string prims = ColorDataUtils.GetColorPrimariesStringX264(mediaFile.ColorData.ColorPrimaries);
                string transfer = ColorDataUtils.GetColorTransferStringX264(mediaFile.ColorData.ColorTransfer);
                string matrix = ColorDataUtils.GetColorMatrixCoeffsStringX264(mediaFile.ColorData.ColorMatrixCoeffs);
                string range = ColorDataUtils.GetColorRangeString(mediaFile.ColorData.ColorRange); // x264 speaks tv/pc
                colors = $"{(prims.IsNotEmpty() ? $"--colorprim {prims}" : "")} {(transfer.IsNotEmpty() ? $"--transfer {transfer}" : "")} " +
                    $"{(matrix.IsNotEmpty() ? $"--colormatrix {matrix}" : "")} {(range.IsNotEmpty() ? $"--range {range}" : "")}";
            }

            return new CodecArgs($"{rc} --preset {preset} {keyint} {depth} {colors} {adv}");
        }
    }

    class DirectX265 : IBinaryEncoder
    {
        public Streams.Stream.StreamType Type { get; } = Streams.Stream.StreamType.Video;
        public string Name { get { return GetType().Name; } }
        public string FriendlyName { get; } = "H.265 / HEVC (x265)";
        public string ToolName { get; } = "x265";
        public string ArgListName { get; } = "X265";
        // x265 has no muxer at all - "infile can be YUV or Y4M" and the output is a raw bitstream.
        public string StreamExt { get; } = "265";
        public bool StreamCarriesTiming { get; } = false;
        public string[] Presets { get; } = new string[] { "veryslow", "slower", "slow", "medium", "fast", "faster", "veryfast", "superfast", "ultrafast" };
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

        public string GetIoArgs(string outPath)
        {
            return $"--y4m --input - --output {Shell.WrapArg(outPath)}";
        }

        public string GetPassArgs(Pass pass, string statsPath)
        {
            return DirectEncoderUtils.StatsPassArgs(pass, statsPath);
        }

        public CodecArgs GetArgs(Dictionary<string, string> encArgs = null, MediaFile mediaFile = null, Pass pass = Pass.OneOfOne)
        {
            bool vbr = DirectEncoderUtils.IsVbr(encArgs);
            string g = CodecUtils.GetKeyIntArg(mediaFile, Config.GetInt(Config.Key.DefaultKeyIntSecs), "", 480, QuickConvertUi.GetPostFilterRate());
            string q = DirectEncoderUtils.Get(encArgs, "q", QDefault.ToString());
            string preset = DirectEncoderUtils.Get(encArgs, "preset", Presets[PresetDefault]);
            string adv = DirectEncoderUtils.ToSpaceSeparated(DirectEncoderUtils.Get(encArgs, "advanced"));
            string pixFmt = DirectEncoderUtils.Get(encArgs, "pixFmt", PixFmtUtils.GetFormat(ColorFormats[ColorFormatDefault]).Name);
            int bitDepth = FormatUtils.GetBitDepthFromPixelFormat(pixFmt);
            string rc = vbr ? $"--bitrate {DirectEncoderUtils.Kbps(encArgs)}" : $"--crf {q}";
            string keyint = g.IsNotEmpty() ? $"--keyint {g}" : "";
            string depth = bitDepth > 0 ? $"--output-depth {bitDepth}" : "";
            string colors = "";

            if (mediaFile != null && mediaFile.ColorData != null)
            {
                // x265 takes the H.273 integers, but spells the range out.
                string range = mediaFile.ColorData.ColorRange == 2 ? "full" : "limited";
                colors = $"--colorprim {mediaFile.ColorData.ColorPrimaries} --transfer {mediaFile.ColorData.ColorTransfer} " +
                    $"--colormatrix {mediaFile.ColorData.ColorMatrixCoeffs} --range {range}";
            }

            return new CodecArgs($"{rc} --preset {preset} {keyint} {depth} {colors} {adv}");
        }
    }
}
