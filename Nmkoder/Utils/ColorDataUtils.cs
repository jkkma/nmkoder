using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.Media;
using Nmkoder.OS;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Nmkoder.Utils
{
    class ColorDataUtils
    {
        public static async Task<VideoColorData> GetColorData(string path)
        {
            VideoColorData data = new VideoColorData();

            AvProcess.FfprobeSettings settings = new AvProcess.FfprobeSettings() { Args = $"-show_frames -select_streams v:0 -read_intervals \"%+#1\" {path.Wrap()}", LogLevel = "quiet" };
            string infoFfprobe = await AvProcess.RunFfprobe(settings);

            string[] linesFfprobe = infoFfprobe.SplitIntoLines();

            foreach (string line in linesFfprobe)
            {
                if (line.StartsWith("color_transfer="))
                    data.ColorTransfer = GetColorTransfer(line.Split('=').Last());

                else if (line.StartsWith("color_space="))
                    data.ColorMatrixCoeffs = GetMatrixCoeffs(line.Split('=').Last());

                else if (line.StartsWith("color_primaries="))
                    data.ColorPrimaries = GetColorPrimaries(line.Split('=').Last());

                else if (line.StartsWith("color_range="))
                    data.ColorRange = GetColorRange(line.Split('=').Last());

                else if (line.StartsWith("red_x="))
                    data.RedX = line.Contains("/") ? FractionToFloat(line.Split('=').Last()) : line.Split('=').Last();

                else if (line.StartsWith("red_y="))
                    data.RedY = line.Contains("/") ? FractionToFloat(line.Split('=').Last()) : line.Split('=').Last();

                else if (line.StartsWith("green_x="))
                    data.GreenX = line.Contains("/") ? FractionToFloat(line.Split('=').Last()) : line.Split('=').Last();

                else if (line.StartsWith("green_y="))
                    data.GreenY = line.Contains("/") ? FractionToFloat(line.Split('=').Last()) : line.Split('=').Last();

                // X into X, Y into Y. These four were crossed over, and the mkvinfo pass below has
                // them the right way round - so the transposed white point and blue primary only
                // reached the output for a source mkvinfo says nothing about, which is every file
                // that is not Matroska.
                else if (line.StartsWith("blue_x="))
                    data.BlueX = line.Contains("/") ? FractionToFloat(line.Split('=').Last()) : line.Split('=').Last();

                else if (line.StartsWith("blue_y="))
                    data.BlueY = line.Contains("/") ? FractionToFloat(line.Split('=').Last()) : line.Split('=').Last();

                else if (line.StartsWith("white_point_x="))
                    data.WhiteX = line.Contains("/") ? FractionToFloat(line.Split('=').Last()) : line.Split('=').Last();

                else if (line.StartsWith("white_point_y="))
                    data.WhiteY = line.Contains("/") ? FractionToFloat(line.Split('=').Last()) : line.Split('=').Last();

                else if (line.StartsWith("max_luminance="))
                    data.LumaMax = line.Contains("/") ? FractionToFloat(line.Split('=').Last()) : line.Split('=').Last();

                else if (line.StartsWith("min_luminance="))
                    data.LumaMin = line.Contains("/") ? FractionToFloat(line.Split('=').Last()) : line.Split('=').Last();

                else if (line.StartsWith("max_content="))
                    data.MaxCll = line.Contains("/") ? FractionToFloat(line.Split('=').Last()) : line.Split('=').Last();

                else if (line.StartsWith("max_average="))
                    data.MaxFall = line.Contains("/") ? FractionToFloat(line.Split('=').Last()) : line.Split('=').Last();
            }

            string infoMkvinfo = await AvProcess.RunMkvInfo($"{path.Wrap()}", OS.NmkoderProcess.ProcessType.Secondary);

            if (infoMkvinfo.Contains("+ Video track"))
            {
                string[] lines = infoMkvinfo.Split("+ Video track")[1].Split("+ Track")[0].Split("+ Tags")[0].SplitIntoLines();

                foreach (string line in lines)
                {
                    if (line.StartsWith("+ Colour transfer:"))
                        data.ColorTransfer = ValidateNumber(line.Split(':')[1]).GetInt();

                    else if (line.StartsWith("+ Colour matrix coefficients:"))
                        data.ColorMatrixCoeffs = ValidateNumber(line.Split(':')[1]).GetInt();

                    else if (line.StartsWith("+ Colour primaries:"))
                        data.ColorPrimaries = ValidateNumber(line.Split(':')[1]).GetInt();

                    else if (line.StartsWith("+ Colour range:"))
                        data.ColorRange = ValidateNumber(line.Split(':')[1]).GetInt();

                    else if (line.StartsWith("+ Red colour coordinate x:"))
                        data.RedX = ValidateNumber(line.Split(':')[1]);

                    else if (line.StartsWith("+ Red colour coordinate y:"))
                        data.RedY = ValidateNumber(line.Split(':')[1]);

                    else if (line.StartsWith("+ Green colour coordinate x:"))
                        data.GreenX = ValidateNumber(line.Split(':')[1]);

                    else if (line.StartsWith("+ Green colour coordinate y:"))
                        data.GreenY = ValidateNumber(line.Split(':')[1]);

                    else if (line.StartsWith("+ Blue colour coordinate y:"))
                        data.BlueY = ValidateNumber(line.Split(':')[1]);

                    else if (line.StartsWith("+ Blue colour coordinate x:"))
                        data.BlueX = ValidateNumber(line.Split(':')[1]);

                    else if (line.StartsWith("+ White colour coordinate y:"))
                        data.WhiteY = ValidateNumber(line.Split(':')[1]);

                    else if (line.StartsWith("+ White colour coordinate x:"))
                        data.WhiteX = ValidateNumber(line.Split(':')[1]);

                    else if (line.StartsWith("+ Maximum luminance:"))
                        data.LumaMax = ValidateNumber(line.Split(':')[1]);

                    else if (line.StartsWith("+ Minimum luminance:"))
                        data.LumaMin = ValidateNumber(line.Split(':')[1]);

                    else if (line.StartsWith("+ Maximum content light:"))
                        data.MaxCll = ValidateNumber(line.Split(':')[1]);

                    else if (line.StartsWith("+ Maximum frame light:"))
                        data.MaxFall = ValidateNumber(line.Split(':')[1]);
                }
            }

            await ReadDolbyVision(path, data);

            return data;
        }

        /// <summary>
        /// Fills in <see cref="VideoColorData.DvProfile"/> and
        /// <see cref="VideoColorData.DvBlCompatId"/> off the stream's Dolby Vision configuration record.
        /// <para/>
        /// **This is a second ffprobe rather than two more flags on the one above, and that is not
        /// tidiness - it is the one arrangement that is safe.** The record is *stream* side data, so
        /// <c>-show_frames</c> never prints it; and adding <c>-show_streams</c> to that command puts the
        /// <c>[STREAM]</c> section **after** <c>[FRAME]</c> - measured against the bundled build, frames
        /// at line 1 and streams at line 59 - where the loop above keeps whichever match it sees last.
        /// The stream section's own <c>color_transfer</c> would therefore win, and this file's own notes
        /// already record a Matroska reading <c>unknown</c> from <c>-show_streams</c> and
        /// <c>smpte2084</c> from <c>-show_frames</c>. So the cheap-looking repair is the one that would
        /// stop every such file reading as HDR at all. Leave that command alone.
        /// <para/>
        /// The keys were read out of the binary rather than assumed: ffprobe's DOVI block prints
        /// <c>dv_version_major</c>, <c>dv_version_minor</c>, <c>dv_profile</c>, <c>dv_level</c>, the
        /// three present flags, <c>dv_bl_signal_compatibility_id</c> and <c>dv_md_compression</c>.
        /// <c>dv_profile</c> does not appear in the binary's string table on its own because it is
        /// shared as the tail of <c>s-&gt;cfg.dv_profile</c>, which is what suffix-merging does and is
        /// worth knowing before reading a <c>strings</c> dump as evidence of absence.
        /// <para/>
        /// A file with no Dolby Vision prints none of them and leaves the defaults, so the cost of this
        /// on the ordinary file is one process that finds nothing. It never throws: a probe that fails
        /// leaves the file reading as plain HDR10, which is what it was before this existed.
        /// </summary>
        private static async Task ReadDolbyVision(string path, VideoColorData data)
        {
            try
            {
                AvProcess.FfprobeSettings settings = new AvProcess.FfprobeSettings() { Args = $"-show_streams -select_streams v:0 {path.Wrap()}", LogLevel = "quiet" };
                string info = await AvProcess.RunFfprobe(settings);

                foreach (string line in info.SplitIntoLines())
                {
                    if (line.StartsWith("dv_profile="))
                        data.DvProfile = line.Split('=').Last().GetInt();

                    else if (line.StartsWith("dv_bl_signal_compatibility_id="))
                        data.DvBlCompatId = line.Split('=').Last().GetInt();
                }
            }
            catch (Exception e)
            {
                Logger.Log($"Could not read Dolby Vision data: {e.Message}", true);
            }
        }

        /// <summary>
        /// <see cref="GetColorData"/> keyed by path, for callers that have a path rather than a
        /// <see cref="MediaFile"/> to hang the answer on - which is every preview extractor.
        /// <para/>
        /// The stored value is the **Task** rather than its result, which is what makes this single
        /// flight: <see cref="Media.FfmpegExtract.ExtractThumbs"/> launches its frames all at once, so a
        /// cache holding finished answers would have every one of them miss together and start its own
        /// probe. Handing each the same in-flight task costs one.
        /// <para/>
        /// Keyed on the file's length and last write as well as its path, for the reason
        /// <c>GetVideoInfo</c>'s own cache had to learn: a temp file at a fixed path, rewritten per run,
        /// is otherwise answered from the previous run's reading.
        /// </summary>
        public static Task<VideoColorData> GetColorDataCached(string path)
        {
            string key;

            try
            {
                FileInfo info = new FileInfo(path);
                key = $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            }
            catch
            {
                key = path;
            }

            lock (colorDataCache)
            {
                if (!colorDataCache.TryGetValue(key, out Task<VideoColorData> task))
                {
                    task = GetColorData(path);
                    colorDataCache[key] = task;
                }

                return task;
            }
        }

        private static readonly Dictionary<string, Task<VideoColorData>> colorDataCache = new Dictionary<string, Task<VideoColorData>>();

        private static string FractionToFloat(string fracString)
        {
            string[] fracNums = fracString.Split('/');
            return ((float)fracNums[0].GetInt() / (float)fracNums[1].GetInt()).ToString("0.#######", new CultureInfo("en-US"));
        }

        private static string ValidateNumber(string numStr)
        {
            return Double.Parse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture).ToString("0.#######", new CultureInfo("en-US"));
        }

        /// <summary> H.273 transfer characteristics, the two that mean high dynamic range. 16 is PQ,
        /// which HDR10, HDR10+ and Dolby Vision all sit on; 18 is HLG. Both are what ffprobe prints as
        /// smpte2084 and arib-std-b67 and what <see cref="GetColorTransfer"/> turns back into these. </summary>
        public const int TransferPq = 16, TransferHlg = 18;

        /// <summary> H.273 colour primaries for BT.2020/BT.2100, and for BT.709. </summary>
        public const int PrimariesBt2020 = 9, PrimariesBt709 = 1;

        /// <summary> H.273 matrix coefficients for BT.2020 non-constant luminance, which is the matrix
        /// BT.2100 specifies alongside the PQ and HLG curves. </summary>
        public const int MatrixBt2020Ncl = 9;

        /// <summary> The value every one of these carries when the file says nothing - what
        /// <see cref="GetColorPrimaries"/>, <see cref="GetMatrixCoeffs"/> and
        /// <see cref="GetColorTransfer"/> all fall through to for a name they do not recognise. </summary>
        public const int Unspecified = 2;

        /// <summary> H.273 transfer and matrix for BT.709 - what a tone-mapped file becomes. </summary>
        public const int TransferBt709 = 1, MatrixBt709 = 1;

        /// <summary> Limited/TV range, in this app's own numbering rather than H.273's - see
        /// <see cref="GetColorRange"/>, where 0 is unspecified, 1 is tv and 2 is pc. Every encoder that
        /// is handed a range converts from these three, and none of them share the numbering. </summary>
        public const int RangeLimited = 1;

        /// <summary>
        /// Whether this file's colour says it is HDR, which is the question behind the whole tone-mapping
        /// row.
        /// <para/>
        /// The transfer curve decides it and nothing else does. Wide *gamut* is a different property -
        /// BT.2020 primaries with an ordinary BT.709 transfer is a colour space, not a dynamic range, and
        /// tone-mapping is a luminance operation with nothing to say about it. So primaries 9 on its own
        /// is deliberately not enough; a gamut conversion for such a file is what the Color Format and
        /// the encoders' own colour arguments are for.
        /// </summary>
        public static bool IsHdr(VideoColorData d)
        {
            return d != null && (d.ColorTransfer == TransferPq || d.ColorTransfer == TransferHlg);
        }

        /// <summary> "HDR10 (PQ)" or "HLG", with the gamut named when it is the wide one - the phrase the
        /// tone-mapping row and its log lines both use, so a file is described one way everywhere. </summary>
        public static string DescribeHdr(VideoColorData d)
        {
            if (!IsHdr(d))
                return "";

            string curve = d.ColorTransfer == TransferPq ? "HDR10 (PQ)" : "HLG";
            string what = d.ColorPrimaries == PrimariesBt2020 ? $"{curve}, BT.2020" : curve;

            // Named where it is there, because it is the one property of an HDR file this app treats
            // differently and the readout is the only place a user finds out that it did.
            return HasDolbyVision(d) ? $"{what} + Dolby Vision {DescribeDolbyVisionProfile(d)}" : what;
        }

        /// <summary> Whether the file carries a Dolby Vision configuration record at all. Everything
        /// else here is about *which* one, and answers nothing for a file that has none. </summary>
        public static bool HasDolbyVision(VideoColorData d)
        {
            return d != null && d.DvProfile > 0;
        }

        /// <summary>
        /// The profile as people write it - <c>8.1</c>, <c>8.4</c>, <c>10.0</c>, <c>5</c>, <c>7</c>.
        /// <para/>
        /// Two profiles have a sub-profile, and it *is* the base layer compatibility id rather than a
        /// second field, so the dot is that number. Profile 8 is the one everybody meets: 8.1 is the
        /// HDR10-compatible one, 8.2 SDR, 8.4 HLG. Profile 10 is the same arrangement over AV1, and it
        /// is dotted here for a reason the others do not have - <b>10.0 is the one that gets refused
        /// and 10.1 is not</b>, both being "profile 10" to anyone reading the bare number, and the
        /// refusal <see cref="UI.Tasks.ToneMapUi.GetProblem"/> writes names this string. Telling
        /// somebody their profile 10 file cannot be tone-mapped, where a profile 10 file of the other
        /// kind maps perfectly well, is a message that cannot be acted on.
        /// <para/>
        /// The two are dotted on different conditions, which is not an inconsistency. A profile 8
        /// declaring 0 is malformed - its whole definition is that the id says which readable signal
        /// the base layer is - so it is written bare rather than as a "8.0" no such file should carry.
        /// A profile 10 declaring 0 is ordinary and means IPT-PQ-c2, so it keeps its dot.
        /// <para/>
        /// Every other profile is written as its bare number, which is how they are named: profile 7
        /// is "7" where its compatibility id is 6, a definitional value that says nothing a reader of
        /// the profile number does not already know.
        /// </summary>
        public static string DescribeDolbyVisionProfile(VideoColorData d)
        {
            if (!HasDolbyVision(d))
                return "";

            if (d.DvProfile == DvProfileSingleLayer && d.DvBlCompatId > 0)
                return $"8.{d.DvBlCompatId}";

            if (d.DvProfile == DvProfileAv1 && d.DvBlCompatId >= 0)
                return $"10.{d.DvBlCompatId}";

            return d.DvProfile.ToString();
        }

        /// <summary>
        /// Whether this file's pictures are wrong without the Dolby Vision RPU being applied - which is
        /// profile 5 and nothing else the app is likely to meet.
        /// <para/>
        /// Every other profile in circulation carries a base layer that is an ordinary HDR10 or HLG
        /// signal, which is the whole point of the compatibility id: a decoder that ignores the RPU
        /// shows the graded HDR picture, just without the dynamic metadata refining it. Profile 5's base
        /// layer is **IPT-PQ-c2**, which is not YCbCr at all - read as though it were, the colours come
        /// out the notorious magenta and green.
        /// <para/>
        /// So this is a property of the bitstream rather than a guess about a tool: a chain that does no
        /// RPU reshaping cannot produce the right picture from one of these, whatever else it does
        /// right. <see cref="UI.Tasks.ToneMapUi.GetProblem"/> refuses over it where the CPU chain would
        /// be doing the work, and lets it through where libplacebo would, since that one *does* apply
        /// the RPU - FFmpeg parses it and <c>vf_libplacebo</c>'s <c>apply_dolbyvision</c> hands it to
        /// the reshape shader, with no libdovi in the build required or present. Nothing warns on that
        /// path: it used to, back when the dependency was thought to be libdovi and so unknowable from
        /// here, and the warning went when the hedge did. <c>ToneMapUi.LogDolbyVision</c> states which
        /// of the two ran, on both paths, and carries what is measured there and what is not.
        /// Which of the two is doing the work is not only the machine's answer: the AV1AN tab always
        /// maps on the CPU, so a profile 5 file is refused there on every machine.
        /// <para/>
        /// **A compatibility id of 0 is not always a declaration, and this used to read it as one.** The
        /// rule was <c>profile == 5 || compat == 0</c>, on the reasoning that "either field can be the one
        /// a file states" - but ffprobe prints <c>dv_bl_signal_compatibility_id=0</c> for a field that was
        /// never written just as it does for a declared 0, and the nibble was carved out of a
        /// <c>reserved = 0</c> field in a later revision of the spec, so a record written before it
        /// existed reads as 0 at its full 24 bytes with nothing malformed about it. Where the profile's
        /// own definition fixes the base layer, the profile is therefore the authority and the nibble is
        /// not: the three lists below are that, and the fall-through keeps the old conservative default
        /// for a profile nobody here has enumerated.
        /// <para/>
        /// **Nobody was hitting it, and the honest reason to have changed it is the comment rather than
        /// the behaviour.** The shape this was suspected of refusing is the ordinary UHD Blu-ray remux,
        /// and profile 7 does not carry 0: FFmpeg's own FATE reference output records real ffprobe
        /// readings of three real profile-7 samples, all <c>dv_bl_signal_compatibility_id=6</c>
        /// ("Blu-ray"), at <c>dv_version_major=1</c>. Neither <c>mkvmerge</c> nor an ffmpeg MP4-to-MKV
        /// remux zeroes the nibble - both measured, the id survives intact - so there is no ordinary
        /// route to the wrongly-refused shape either.
        /// <para/>
        /// **Exactly where the two rules part company was recomputed rather than recalled**, by driving
        /// this method and the old expression over every (profile, compat id) pair the app can hold -
        /// profiles 0-10 and 20 against ids -1, 0, 1, 2, 4 and 6, which is 72 rows - by reflection into
        /// the built assembly. 15 differ, in two families, and the split is the point:
        /// <list type="bullet">
        /// <item>**Profiles 1 and 3, at every id (10 rows): the old rule let them through and this one
        /// refuses them.** Both are codec-string-<c>n</c> "no base layer" profiles like 5, so refusing
        /// them is the fix rather than a side effect - a test on <c>profile == 5</c> alone missed
        /// them.</item>
        /// <item>**Profiles 2, 4, 6, 7 and 9 declaring 0 (5 rows): the old rule refused them and this
        /// one does not.** These are the readable-base-layer profiles meeting an undeclared nibble,
        /// which is what the whole rewrite is for.</item>
        /// </list>
        /// An earlier version of this paragraph named "profile 2 or 7 with an undeclared nibble ... and
        /// a profile 8 declaring 0" as the differing set. Profile 8 declaring 0 does **not** differ -
        /// measured, both rules refuse it, since 8 is in neither list and falls through to the nibble -
        /// and the list also missed profiles 4, 6 and 9 on the same footing as 2 and 7, and the
        /// profiles 1 and 3 family entirely. Recompute this rather than editing the sentence if either
        /// list changes.
        /// <para/>
        /// The one thing the old rule's second clause genuinely bought is kept: it is what catches AV1
        /// profile 10 declaring 0, which is the same IPT-PQ-c2 base layer under another codec and which
        /// a test on the profile number alone would let through. Measured on the same sweep: 10 with an
        /// id of 0 is refused by both rules, and 10.1 by neither.
        /// </summary>
        public static bool HasUnusableBaseLayer(VideoColorData d)
        {
            if (!HasDolbyVision(d))
                return false;

            if (DvProfilesWithoutBaseLayer.Contains(d.DvProfile))
                return true;

            if (DvProfilesWithReadableBaseLayer.Contains(d.DvProfile))
                return false;

            return d.DvBlCompatId == DvBlCompatNone;
        }

        /// <summary> The profiles carrying no cross-compatible base layer at all, whatever their
        /// compatibility nibble says - the three whose codec string ends in <c>n</c> for "none":
        /// <c>dvav.pen</c> (1), <c>dvhe.den</c> (3) and <c>dvhe.stn</c> (5). Only 5 is in circulation;
        /// the other two never shipped to consumers and cost nothing to name. Profile 0 belongs with
        /// them by name and is deliberately absent, being unreachable: <see cref="HasDolbyVision"/>
        /// tests <c>DvProfile &gt; 0</c>, so a file declaring it reads as carrying no Dolby Vision at
        /// all. </summary>
        private static readonly int[] DvProfilesWithoutBaseLayer = { 1, 3, DvProfileIpt };

        /// <summary> The profiles whose base layer is an ordinary SDR or HDR10 picture by definition, so
        /// no reading of the compatibility nibble can make one unviewable: <c>dvhe.der</c> (2),
        /// <c>dvhe.dtr</c> (4), <c>dvhe.dth</c> (6), <c>dvhe.dtb</c> (7 - the UHD Blu-ray dual-layer
        /// shape, and the common one) and <c>dvav.se</c> (9). 8 is deliberately not here: its
        /// sub-profile *is* the nibble, so there the field is the answer. Neither is 10 or 20, whose
        /// base layers this session could not settle - 20 is stereoscopic MV-HEVC rather than the AV1
        /// profile it is easily mistaken for - so both keep the conservative default. </summary>
        private static readonly int[] DvProfilesWithReadableBaseLayer = { 2, 4, 6, 7, 9 };

        /// <summary>
        /// SVT-AV1's two HDR static metadata flags, built from what the file itself declares, or "" where
        /// it declares nothing.
        /// <para/>
        /// **This exists because y4m carries no side data, so the encoder cannot learn any of it.** The
        /// four colour tags are handed over by flag for exactly that reason; the mastering display and the
        /// light levels are the same argument and were simply never finished. Measured against the shipped
        /// SvtAv1EncApp with no flags at all: a source declaring a mastering display and MaxCLL 9978
        /// encodes to an output with **zero** HDR side data on it. The file still says PQ and BT.2020, so
        /// it plays - and a display handed no mastering metadata falls back to its own assumption, which
        /// is the crushed-mid-tone case this app already documents from the other direction. Nothing about
        /// it looks wrong until it is played on real hardware.
        /// <para/>
        /// **The units are the file's own decimals, not x265's scaled integers, and getting that wrong is
        /// silent.** Measured against the shipped binary: <c>G(0.265,0.690)…L(4000,0.005)</c> reads back
        /// out of the bitstream as exactly those values, where x265's <c>G(13250,34500)…L(40000000,50)</c>
        /// spelling is clipped to 1.0 on every coordinate and 6445568 nits of luminance, behind one
        /// <c>Svt[warn]: Invalid mastering display info will be clipped</c> on the encoder's stderr - which
        /// av1an collects per chunk into a log <c>HandleTempFolder</c> deletes on a successful run. That is
        /// the same silence the grain collisions hide in. What <see cref="GetColorData"/> already parses is
        /// the decimal form, off ffprobe's fractions and mkvinfo alike, so nothing is converted here.
        /// <para/>
        /// **A tone-mapped encode suppresses this for free, and that is why the source has to be the passed
        /// -in colour data rather than the file.** Both callers run inside the swap
        /// <see cref="Data.ToneMapConfig.GetOutputColorData"/> makes, which hands back the four BT.709 tags
        /// and *nothing else* - every coordinate, luminance and light level empty - so this returns "" and
        /// an SDR output cannot end up declaring the HDR grade it no longer has.
        /// <para/>
        /// <paramref name="wrapValues"/> is the difference between the two tabs and is not cosmetic. The
        /// mastering display's parentheses are shell syntax on Linux and macOS, so the Quick Convert path,
        /// which launches the binary itself, has to wrap them; the AV1AN path must **not**, because
        /// everything there ends up inside av1an's own <c>-v "…"</c> string, whose double quotes already
        /// protect them and which is split again on whitespace before it reaches the encoder - a quote of
        /// this app's own would be one more layer than that split accounts for. It is the same split the
        /// grain table's bare path is written for.
        /// <para/>
        /// All ten mastering-display fields are required together, since the flag takes one string and a
        /// partial one describes nothing. <c>--content-light</c> needs **both** numbers - measured, a lone
        /// value is <c>Error: Invalid parameter 'content-light'</c> and the encode does not start - so a
        /// file stating only MaxCLL is given a MaxFALL of 0, which is the "unknown" the field already means
        /// and which the binary accepts.
        /// </summary>
        public static string GetSvtHdrMetadataArgs(VideoColorData d, bool wrapValues)
        {
            if (d == null)
                return "";

            List<string> args = new List<string>();
            string[] display = { d.GreenX, d.GreenY, d.BlueX, d.BlueY, d.RedX, d.RedY, d.WhiteX, d.WhiteY, d.LumaMax, d.LumaMin };

            if (display.All(v => !string.IsNullOrWhiteSpace(v)))
            {
                string md = $"G({d.GreenX},{d.GreenY})B({d.BlueX},{d.BlueY})R({d.RedX},{d.RedY})" +
                    $"WP({d.WhiteX},{d.WhiteY})L({d.LumaMax},{d.LumaMin})";

                args.Add($"--mastering-display {(wrapValues ? Shell.WrapArg(md) : md)}");
            }

            if (!string.IsNullOrWhiteSpace(d.MaxCll))
            {
                string cll = $"{d.MaxCll},{(string.IsNullOrWhiteSpace(d.MaxFall) ? "0" : d.MaxFall)}";
                args.Add($"--content-light {(wrapValues ? Shell.WrapArg(cll) : cll)}");
            }

            return string.Join(" ", args);
        }

        /// <summary> Dolby Vision profile 5 - single layer, IPT-PQ-c2, no ordinary signal underneath it.
        /// See <see cref="HasUnusableBaseLayer"/> for why it is the one profile named here. </summary>
        public const int DvProfileIpt = 5;

        /// <summary> Profile 8, the single-layer one whose sub-profile is its compatibility id. </summary>
        public const int DvProfileSingleLayer = 8;

        /// <summary> Profile 10, the AV1 one - the same single-layer arrangement as 8, sub-profile and
        /// all, over a different codec, so 10.0 is the IPT-PQ-c2 base layer that profile 5 is over
        /// HEVC. Not to be confused with profile 20, which is stereoscopic MV-HEVC rather than the
        /// second AV1 profile the number looks like. </summary>
        public const int DvProfileAv1 = 10;

        /// <summary> Base layer compatibility id 0. 1 is HDR10, 2 is SDR, 4 is HLG and 6 is Ultra HD
        /// Blu-ray, all of them readable; 0 is the value for "no cross-compatibility" - **and equally
        /// the value for a field that was never written**, which is why
        /// <see cref="HasUnusableBaseLayer"/> only believes it for a profile whose own definition does
        /// not settle the question. </summary>
        public const int DvBlCompatNone = 0;

        /// <summary>
        /// The peak brightness in nits the file itself declares, or 0 where it declares none.
        /// <para/>
        /// MaxCLL first, because it is a measurement of *this* content - the brightest pixel anyone
        /// actually encoded - where the mastering display's maximum luminance only describes the monitor
        /// the grade was checked on, and is very often a round 1000 or 4000 on content that never gets
        /// near it. Either is far better than assuming, and both are already parsed by
        /// <see cref="GetColorData"/> off ffprobe's frame side data and mkvinfo alike.
        /// <para/>
        /// Either is skipped where it sits on <see cref="PqCeilingNits"/>, which is where the argument
        /// above stops holding: a number at the top of the format is not a measurement of anything. See
        /// that constant for what trusting one costs.
        /// <para/>
        /// This matters because **ffmpeg's own tone-mapper does not read either of them.** Measured
        /// against a current BtbN master build: the same PQ ramp with and without a MaxCLL of 1000 and a
        /// mastering display of 1000 nits tone-maps to byte-identical output, so the filter's automatic
        /// peak detection is not reaching the container's metadata at all. A file's declared peak only
        /// changes the result if it is passed in, which is what <see cref="Data.ToneMapConfig"/> does
        /// with this.
        /// </summary>
        public static double GetDeclaredPeakNits(VideoColorData d)
        {
            if (d == null)
                return 0;

            foreach (string value in new[] { d.MaxCll, d.LumaMax })
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double nits)
                    && nits > 0 && nits < PqCeilingNits)
                {
                    return nits;
                }
            }

            return 0;
        }

        /// <summary>
        /// The top of the PQ curve, and so the largest number either field can hold - which is exactly
        /// what makes a value sitting on it useless. **A peak declared at the ceiling is the format's
        /// maximum, not a measurement**, so it is skipped and the next candidate answers instead.
        /// <para/>
        /// The case this was written for is an ordinary UHD Blu-ray rip: x265 wrote
        /// <c>cll=10000,258</c> beside <c>master-display … L(40000000,50)</c> - MaxCLL at the ceiling,
        /// MaxFALL a measured-looking 258, and a mastering display of 4000 nits, which is the brightest
        /// the grade can ever have been checked on. Taken at face value that put <c>npl</c> at 2666.7
        /// and **crushed the whole picture**: measured on PQ patches through this app's own chain, 203
        /// nits - BT.2408's SDR reference white, where the graded picture's white belongs - came out at
        /// 23.2% of the SDR range against 33.6% off the mastering display's 4000, and 100 nits at 17.2%
        /// against 25.2%. The top of the output range was simply unreachable: the file's own 4000-nit
        /// peak only reached 69.7%, so nearly a third of the range went unused on a file that never
        /// exceeded its own mastering display.
        /// <para/>
        /// It applies to <see cref="VideoColorData.LumaMax"/> as well as MaxCLL, and deliberately: a
        /// mastering display declared at 10000 nits is a monitor that does not exist, so that field is
        /// the same non-measurement under another name. With both at the ceiling the run falls through
        /// to <see cref="Data.ToneMapConfig.AssumedPeakNits"/>, which is the right place to land - that
        /// constant's own note already argues that assuming 10000 crushes every mid-tone in the far
        /// commoner case, and this is that case arriving through the file rather than through the
        /// default.
        /// <para/>
        /// Only the tone map reads this. What gets *written* back out by
        /// <see cref="SetColorData"/> is the field itself, so a file's own MaxCLL is still carried
        /// across untouched - this decides what to roll off to, not what the file says.
        /// </summary>
        public const double PqCeilingNits = 10000;

        /// <summary>
        /// Muxes <paramref name="d"/> onto <paramref name="path"/> in place.
        /// <para/>
        /// <paramref name="colorSpace"/> covers the four tags every video has - matrix, transfer,
        /// primaries, range - and <paramref name="hdr"/> the mastering display and light levels that
        /// only HDR carries. They are the dialog's two checkboxes, which used to be stored and then
        /// read by nothing at all, so both sets went out however they were left.
        /// </summary>
        public static async Task SetColorData(string path, VideoColorData d, bool colorSpace = true, bool hdr = true)
        {
            try
            {
                if (!colorSpace && !hdr)
                {
                    Logger.Log("Neither color space nor HDR data is selected for transfer, so there is nothing to write. Tick one in the utility's settings.");
                    return;
                }

                if (!AvProcess.IsToolAvailable("mkvmerge"))
                {
                    RunTask.Fail($"Color data is written with mkvmerge, which is not installed.\n\n{AvProcess.MkvToolNixInstallAdvice()}");
                    return;
                }

                string tmpPath = IoUtils.FilenameSuffix(path, ".tmp");

                List<string> args = new List<string>();

                args.Add($"-o {tmpPath.Wrap()}");

                if (colorSpace)
                {
                    args.Add($"--colour-matrix 0:{d.ColorMatrixCoeffs}");
                    args.Add($"--colour-transfer-characteristics 0:{d.ColorTransfer}");
                    args.Add($"--colour-primaries 0:{d.ColorPrimaries}");
                    args.Add($"--colour-range 0:{d.ColorRange}");
                }

                if (hdr)
                {
                    // Each flag is guarded by the values it actually prints. The chromaticity line
                    // takes all six coordinates and the white point its own two - both used to be
                    // gated on RedX alone, so a file carrying a white point and no primaries lost it,
                    // and one with primaries and no white point had mkvmerge handed "0:," to reject.
                    bool haveChroma = new[] { d.RedX, d.RedY, d.GreenX, d.GreenY, d.BlueX, d.BlueY }.All(x => !string.IsNullOrWhiteSpace(x));
                    bool haveWhite = !string.IsNullOrWhiteSpace(d.WhiteX) && !string.IsNullOrWhiteSpace(d.WhiteY);

                    if (!string.IsNullOrWhiteSpace(d.LumaMax)) args.Add($"--max-luminance 0:{d.LumaMax}");
                    if (!string.IsNullOrWhiteSpace(d.LumaMin)) args.Add($"--min-luminance 0:{d.LumaMin}");
                    if (haveChroma) args.Add($"--chromaticity-coordinates 0:{d.RedX},{d.RedY},{d.GreenX},{d.GreenY},{d.BlueX},{d.BlueY}");
                    if (haveWhite) args.Add($"--white-colour-coordinates 0:{d.WhiteX},{d.WhiteY}");
                    if (!string.IsNullOrWhiteSpace(d.MaxCll)) args.Add($"--max-content-light 0:{d.MaxCll}");
                    if (!string.IsNullOrWhiteSpace(d.MaxFall)) args.Add($"--max-frame-light 0:{d.MaxFall}");
                }

                // Only "-o tmp" so far, so this would remux the file to say nothing about it - which
                // costs a full copy of it and a delete of the original to end up where it started.
                // HDR data on its own is the way in: a source that has none leaves every flag above
                // unset, and there is no reason to touch the target over that.
                if (args.Count < 2)
                {
                    Logger.Log($"'{Path.GetFileName(path)}' was left alone - the source carries none of the data selected for transfer.");
                    return;
                }

                args.Add($"{path.Wrap()}");

                await AvProcess.RunMkvMerge(string.Join(" ", args), OS.NmkoderProcess.ProcessType.Primary, true);

                if (!File.Exists(tmpPath))
                {
                    RunTask.Fail("Muxing the color data in failed - mkvmerge wrote nothing. Its own output is in mkvmerge.txt, in the log folder.");
                    return;
                }

                int filesizeDiffKb = (int)((Math.Abs(new FileInfo(path).Length - new FileInfo(tmpPath).Length)) / 1024);
                double filesizeFactor = (double)(new FileInfo(tmpPath).Length) / (double)(new FileInfo(path).Length);
                Logger.Log($"{MethodBase.GetCurrentMethod().DeclaringType}: Filesize ratio of remuxed file against original: {filesizeFactor}", true);

                if (filesizeDiffKb > 1024 && (filesizeFactor < 0.95d || filesizeFactor > 1.05d))
                {
                    Logger.Log($"Warning: Output file size differs by >1MB is not within 5% of the original file's size! Won't delete original to be sure.");
                }
                else
                {
                    File.Delete(path);
                    File.Move(tmpPath, path);
                }
            }
            catch (Exception e)
            {
                Logger.Log($"SetColorData Error: {e.Message}\n{e.StackTrace}");
            }
        }

        // These three read what ffprobe prints, and ffprobe's vocabulary is not the one the
        // int-to-string functions further down emit - it spells transfer 18 "arib-std-b67" where they
        // say "bt2100", 13 "iec61966-2-1" against "srgb", primaries 6 "smpte170m" against "bt601", and
        // matrix 10 "bt2020c" against "bt2020". Checking only for this file's own names is what made
        // an HLG or sRGB file read back as Unspecified - and then get muxed into the target as
        // Unspecified, which is worse than not reading it. Both vocabularies are accepted here; the
        // names below stay as they are because the encoders are given those.
        // Every spelling was read out of the bundled ffprobe rather than assumed, by tagging a file
        // with each value in turn and probing it back.

        public static int GetColorPrimaries(string s) // Defined by the "Color primaries" section of ISO/IEC 23091-4/ITU-T H.273
        {
            s = s.Trim().ToLower();
            if (s == "bt709") return 1;
            if (s == "bt470m") return 4;
            if (s == "bt470bg") return 5;
            if (s == "bt601" || s == "smpte170m") return 6;
            if (s == "smpte240m") return 7;
            if (s == "film") return 8;
            if (s == "bt2020") return 9;
            if (s == "smpte428" || s == "smpte428_1") return 10;
            if (s == "smpte431") return 11;
            if (s == "smpte432") return 12;
            // EBU 3213-E is 22 and is deliberately not read here. The string table below has no entry
            // for it, so aomenc and x264 would be given nothing either way, and the two encoders that
            // take this number raw cannot use it: x265 refuses --colorprim 22 outright ("Color
            // Primaries must be unknown, bt709, ... smpte-eg-432"), which fails the encode. Falling
            // through to Unspecified is what it did before and is the only value that works.
            return 2; // Fallback: 2 = Unspecified
        }

        public static int GetColorTransfer(string s) // Defined by the "Transfer characteristics" section of ISO/IEC 23091-4/ITU-T H.273
        {
            s = s.Trim().ToLower();
            if (s == "bt709") return 1;
            if (s == "gamma22" || s == "bt470m") return 4;
            if (s == "gamma28" || s == "bt470bg") return 5; // BT.470 System B, G (historical)
            if (s == "bt601" || s == "smpte170m") return 6; // BT.601
            if (s == "smpte240m") return 7; // SMPTE 240 M
            if (s == "linear") return 8; // Linear
            if (s == "log100") return 9; // Logarithmic (100 : 1 range)
            if (s == "log316") return 10; // Logarithmic (100 * Sqrt(10) : 1 range)
            if (s == "iec61966-2-4" || s == "iec61966_2_4") return 11; // IEC 61966-2-4
            if (s == "bt1361" || s == "bt1361e") return 12; // BT.1361
            if (s == "srgb" || s == "iec61966-2-1" || s == "iec61966_2_1") return 13; // SRGB
            if (s == "bt2020-10" || s == "bt2020_10") return 14; // BT.2020 10-bit systems
            if (s == "bt2020-12" || s == "bt2020_12") return 15; // BT.2020 12-bit systems
            if (s == "smpte2084") return 16; // SMPTE ST 2084, ITU BT.2100 PQ
            if (s == "smpte428" || s == "smpte428_1") return 17; // SMPTE ST 428
            if (s == "bt2100" || s == "arib-std-b67") return 18; // BT.2100 HLG, ARIB STD-B67
            return 2; // Fallback: 2 = Unspecified
        }

        public static int GetMatrixCoeffs(string s) // Defined by the "Matrix coefficients" section of ISO/IEC 23091-4/ITU-T H.27
        {
            s = s.Trim().ToLower();
            // "gbr" is deliberately not mapped to 0 (Identity). ffprobe prints it for an RGB pixel
            // format, which describes how the frames decode rather than a tag the file is carrying -
            // and every encode here converts to YUV first, so signalling Identity on the result would
            // describe planes that are no longer GBR. SVT-AV1 and x265 are handed this number raw.
            // Unspecified is what it fell through to before, and it is the honest answer.
            if (s == "bt709") return 1;
            if (s == "fcc") return 4; // US FCC 73.628
            if (s == "bt470bg") return 5; // BT.470 System B, G (historical)
            if (s == "bt601" || s == "smpte170m") return 6; // BT.601
            if (s == "smpte240m") return 7; // SMPTE 240 M
            if (s == "ycgco" || s == "ycocg") return 8; // YCgCo
            if (s == "bt2020ncl" || s == "bt2020nc") return 9; // BT.2020 non-constant luminance, BT.2100 YCbCr
            if (s == "bt2020" || s == "bt2020c" || s == "bt2020cl") return 10; // BT.2020 constant luminance
            if (s == "smpte2085") return 11; // SMPTE ST 2085 YDzDx
            if (s == "chroma-derived-nc") return 12; // Chromaticity-derived non-constant luminance
            if (s == "chroma-derived-c") return 13; // Chromaticity-derived constant luminance
            if (s == "ictcp") return 14; // BT.2100 ICtCp
            return 2; // Fallback: 2 = Unspecified
        }

        public static int GetColorRange(string s) // Defined by the "Matrix coefficients" section of ISO/IEC 23091-4/ITU-T H.27
        {
            s = s.Trim().ToLower();
            if (s == "tv") return 1; // TV
            if (s == "pc") return 2; // PC/Full
            return 0; // Fallback: Unspecified
        }

        #region aomenc spellings

        // aomenc takes colour by name as well, and its vocabulary is a third one - neither H.273's
        // nor x264's. It rejects a name it does not know outright, printing its usage text and
        // encoding nothing, so a wrong spelling kills every chunk of an av1an run rather than being
        // ignored the way an unknown *number* would be.
        //
        // FormatForAom stood here and rewrote two names, which left seven wrong: "gamma22",
        // "gamma28", "linear", "smpte240m", "iec61966-2-4", "fcc" and "smpte428" are all ordinary
        // tags a real file carries, and each one failed the encode. Widening the ffprobe tables above
        // to read HLG and BT.2020 CL made two more reachable ("bt2100" and "bt2020" for matrix 10),
        // which is what turned this up.
        //
        // Every entry below was read out of `aomenc --help` and then confirmed against the binary by
        // passing it, including the pass-through names - the table is what aomenc accepts, not what
        // it is documented to.

        public static string GetColorPrimariesStringAom(int n) => GetColorPrimariesString(n) switch
        {
            "smpte240m" => "smpte240",
            "smpte428" => "xyz",
            var s => s,
        };

        public static string GetColorTransferStringAom(int n) => GetColorTransferString(n) switch
        {
            "gamma22" => "bt470m",
            "gamma28" => "bt470bg",
            "smpte240m" => "smpte240",
            "linear" => "lin",
            "iec61966-2-4" => "iec61966",
            "bt2020-10" => "bt2020-10bit",
            "bt2020-12" => "bt2020-12bit",
            "bt2100" => "hlg",
            var s => s,
        };

        public static string GetColorMatrixCoeffsStringAom(int n) => GetColorMatrixCoeffsString(n) switch
        {
            "fcc" => "fcc73",
            "smpte240m" => "smpte240",
            "bt2020" => "bt2020cl",
            var s => s,
        };

        #endregion

        #region x264 spellings

        // x264 takes colour by name and rejects anything it does not recognise, and it spells
        // several of these differently to the aom-style names below. Translating only where they
        // differ keeps the tables above as the single source; anything with no x264 equivalent
        // stays empty, so the flag is left off rather than failing the encode over a tag.

        public static string GetColorPrimariesStringX264(int n) => GetColorPrimariesString(n) switch
        {
            "bt601" => "smpte170m",
            var s => s,
        };

        public static string GetColorTransferStringX264(int n) => GetColorTransferString(n) switch
        {
            "gamma22" => "bt470m",
            "gamma28" => "bt470bg",
            "bt601" => "smpte170m",
            "bt1361" => "bt1361e",
            "srgb" => "iec61966-2-1",
            "bt2100" => "arib-std-b67",
            var s => s,
        };

        public static string GetColorMatrixCoeffsStringX264(int n) => GetColorMatrixCoeffsString(n) switch
        {
            "bt601" => "smpte170m",
            "ycgco" => "YCgCo",
            "bt2020ncl" => "bt2020nc",
            "bt2020" => "bt2020c",
            var s => s,
        };

        #endregion

        #region Get string from int

        public static string GetColorPrimariesString(int n)
        {
            switch (n)
            {
                case 1: return "bt709";
                case 4: return "bt470m";
                case 5: return "bt470bg";
                case 6: return "bt601";
                case 7: return "smpte240m";
                case 8: return "film";
                case 9: return "bt2020";
                case 10: return "smpte428";
                case 11: return "smpte431";
                case 12: return "smpte432";
            }

            return "";
        }

        public static string GetColorTransferString(int n)
        {
            switch (n)
            {
                case 1: return "bt709";
                case 4: return "gamma22"; // "bt470m"
                case 5: return "gamma28"; // "bt470bg"
                case 6: return "bt601"; // "smpte170m"
                case 7: return "smpte240m";
                case 8: return "linear";
                case 11: return "iec61966-2-4";
                case 12: return "bt1361";
                case 13: return "srgb";
                case 14: return "bt2020-10";
                case 15: return "bt2020-12";
                case 16: return "smpte2084";
                case 17: return "smpte428";
                case 18: return "bt2100";
            }

            return "";
        }

        public static string GetColorMatrixCoeffsString(int n)
        {
            switch (n)
            {
                case 1: return "bt709";
                case 4: return "fcc";
                case 5: return "bt470bg";
                case 6: return "bt601";
                case 7: return "smpte240m";
                case 8: return "ycgco";
                case 9: return "bt2020ncl";
                case 10: return "bt2020";
            }

            return "";
        }

        public static string GetColorRangeString(int n)
        {
            switch (n)
            {
                case 1: return "tv";
                case 2: return "pc";
            }

            return "";
        }

        #endregion

        #region Get friendly name from int

        // H.273 numbers System M 4 and System B, G 5, for primaries and transfer alike - and the
        // string tables above already agree ("bt470m" is 4, "bt470bg" is 5). These two name tables
        // had the pair the other way round, so the readout contradicted the file's own value and
        // mislabelled every PAL or SECAM source. The matrix names below were already right.

        public static string GetColorPrimariesName(int n)
        {
            switch (n)
            {
                case 1: return "BT.709";
                case 2: return "Unspecified";
                case 4: return "BT.470 System M (historical)";
                case 5: return "BT.470 System B, G (historical)";
                case 6: return "BT.601";
                case 7: return "SMPTE 240";
                case 8: return "Generic film (color filters using illuminant C)";
                case 9: return "BT.2020, BT.2100";
                case 10: return "SMPTE 428 (CIE 1921 XYZ)";
                case 11: return "SMPTE RP 431-2";
                case 12: return "SMPTE EG 432-1";
                case 22: return "EBU Tech. 3213-E";
            }

            return "Unknown";
        }

        public static string GetColorTransferName(int n)
        {
            switch (n)
            {
                case 1: return "BT.709";
                case 2: return "Unspecified";
                case 4: return "BT.470 System M (historical)";
                case 5: return "BT.470 System B, G (historical)";
                case 6: return "BT.601";
                case 7: return "SMPTE 240 M";
                case 8: return "Linear";
                case 9: return "Logarithmic (100 : 1 range)";
                case 10: return "Logarithmic (100 * Sqrt(10) : 1 range)";
                case 11: return "IEC 61966-2-4";
                case 12: return "BT.1361";
                case 13: return "sRGB or sYCC";
                case 14: return "BT.2020 10-bit systems";
                case 15: return "BT.2020 12-bit systems";
                case 16: return "SMPTE ST 2084, ITU BT.2100 PQ";
                case 17: return "SMPTE ST 428";
                case 18: return "BT.2100 HLG, ARIB STD-B67";
            }

            return "Unknown";
        }

        public static string GetColorMatrixCoeffsName(int n)
        {
            switch (n)
            {
                case 0: return "Identity (GBR)";
                case 1: return "BT.709";
                case 2: return "Unspecified";
                case 4: return "US FCC 73.628";
                case 5: return "BT.470 System B, G (historical)";
                case 6: return "BT.601";
                case 7: return "SMPTE 240 M";
                case 8: return "YCgCo";
                case 9: return "BT.2020 non-constant luminance, BT.2100 YCbCr";
                case 10: return "BT.2020 constant luminance";
                case 11: return "SMPTE ST 2085 YDzDx";
                case 12: return "Chromaticity-derived non-constant luminance";
                case 13: return "Chromaticity-derived constant luminance";
                case 14: return "BT.2100 ICtCp";
            }

            return "Unknown";
        }

        public static string GetColorRangeName(int n)
        {
            switch (n)
            {
                case 0: return "Unspecified";
                case 1: return "TV (Limited)";
                case 2: return "PC (Full)";
            }

            return "Unknown";
        }

        #endregion
    }
}
