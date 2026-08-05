using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.Media;
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

            return data;
        }

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
            return d.ColorPrimaries == PrimariesBt2020 ? $"{curve}, BT.2020" : curve;
        }

        /// <summary>
        /// The peak brightness in nits the file itself declares, or 0 where it declares none.
        /// <para/>
        /// MaxCLL first, because it is a measurement of *this* content - the brightest pixel anyone
        /// actually encoded - where the mastering display's maximum luminance only describes the monitor
        /// the grade was checked on, and is very often a round 1000 or 4000 on content that never gets
        /// near it. Either is far better than assuming, and both are already parsed by
        /// <see cref="GetColorData"/> off ffprobe's frame side data and mkvinfo alike.
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
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double nits) && nits > 0)
                    return nits;
            }

            return 0;
        }

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
                    RunTask.Fail("Color data is written with mkvmerge, which is not installed. It ships with the Windows build; on Linux and macOS install MKVToolNix from your package manager (e.g. 'apt install mkvtoolnix' or 'brew install mkvtoolnix').");
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
