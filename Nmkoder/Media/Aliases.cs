using Nmkoder.Data;
using Nmkoder.IO;
using Nmkoder.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nmkoder.Media
{
    class Aliases
    {
        #region ISO-639 Languages

        public static List<IsoLanguage> languages = new List<IsoLanguage>();

        public class IsoLanguage
        {
            public string Family { get; set; }
            public string EnglishName { get; set; }
            public string NativeName { get; set; }
            public string[] IsoCodes { get; set; }
        }

        private static void LoadLangsIfNotLoaded()
        {
            if (languages == null || languages.Count == 0)
                LoadLangsFromCsv();
        }

        private static void LoadLangsFromCsv()
        {
            string csvPath = Path.Combine(Paths.GetBinPath(), "iso639.csv");
            languages.Clear();

            try
            {
                bool headerSkipped = false;

                foreach (string line in File.ReadLines(csvPath))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#"))
                        continue;

                    if (!headerSkipped)
                    {
                        headerSkipped = true;
                        continue;
                    }

                    string[] fields = ParseCsvLine(line);

                    if (fields.Length < 6)
                        continue;

                    string[] codes = new string[] { fields[3], fields[4], fields[5] }.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray(); // Strip empty code fields
                    languages.Add(new IsoLanguage() { Family = fields[0], EnglishName = fields[1], NativeName = fields[2], IsoCodes = codes });
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error loading language list: {ex.Message}");
                Logger.Log($"Stack Trace: {ex.StackTrace}", true);
            }
        }

        /// <summary>
        /// Splits one CSV record, honouring double-quoted fields and doubled quotes.
        /// Replaces VisualBasic's TextFieldParser, which is Windows-flavoured legacy API surface.
        /// </summary>
        private static string[] ParseCsvLine(string line)
        {
            var fields = new List<string>();
            var current = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') // Escaped quote
                        {
                            current.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        current.Append(c);
                    }
                }
                else if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == ',')
                {
                    fields.Add(current.ToString().Trim());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }

            fields.Add(current.ToString().Trim());
            return fields.ToArray();
        }

        public static IsoLanguage GetLanguage(string isoCode)
        {
            LoadLangsIfNotLoaded();

            foreach (IsoLanguage lang in languages)
                if (lang.IsoCodes.Contains(isoCode))
                    return lang;

            return new IsoLanguage() { Family = "Unknown", EnglishName = "Unknown", NativeName = "Unknown", IsoCodes = new string[] { isoCode } };
        }

        public static string GetLanguageString(string isoCode, bool includeNativeName = true, bool includeIsoCodes = true)
        {
            return GetLanguageString(GetLanguage(isoCode), includeNativeName, includeIsoCodes);
        }

        public static string GetLanguageString(IsoLanguage lang, bool includeNativeName = true, bool includeIsoCodes = true)
        {
            return $"{lang.EnglishName}{(includeNativeName && lang.NativeName != lang.EnglishName ? $"/{lang.NativeName}" : "")}{(includeIsoCodes ? $" ({string.Join("/", lang.IsoCodes)})" : "")}";
        }

        #endregion

        #region Codec Names

        public static string GetNicerCodecName (string codecName)
        {
            string lower = codecName.ToLower();

            if (lower.StartsWith("hdmv_pgs")) return "PGS";
            if (lower.StartsWith("subrip")) return "SRT";
            if (lower.StartsWith("dvd_subtitle")) return "DVD Subtitles";
            if (lower == "webvtt") return "WebVTT";
            if (lower == "truehd") return "TrueHD";
            if (lower == "opus") return "Opus";
            if (lower == "pcm_bluray") return "Blu-ray PCM";
            if (lower.StartsWith("pcm")) return codecName.Replace("_", " ").ToUpper();
            if (lower == "vc1") return "VC-1";
            if (lower == "mjpeg") return "MJPEG";
            if (lower == "mpeg4") return "MPEG-4";
            if (lower == "mpeg2video") return "MPEG-2";
            if (lower == "msmpeg4v3") return "MS MPEG-4 V3";
            if (lower == "prores") return "ProRes";
            if (lower == "dnxhd") return "DNxHD";
            if (lower == "binkvideo") return "Bink Video";
            if (lower.StartsWith("binkaudio")) return $"Bink Audio {lower.Split('_').Last().ToUpperInvariant()}";
            if (lower == "dnxhd") return "DNxHD";
            if (lower == "timed_id3") return "Timed ID3";
            if (lower == "text") return "Text";
            if (lower == "rawvideo") return "Raw Video";
            if (lower == "msrle") return "MS RLE";
            if (lower == "wmav2") return "WMAV2";
            if (lower == "wmapro") return "WMA Pro";
            if (lower == "dvb_teletext") return "DVB Teletext";
            if (lower == "dvb_subtitle") return "DVB Subtitles";

            return FormatUtils.CapsIfShort(codecName, 5);
        }

        #endregion
    }
}
