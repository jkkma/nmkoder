using Nmkoder.Extensions;
using Nmkoder.IO;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nmkoder.Utils
{
    class FormatUtils
    {
        public static string Bytes(long sizeBytes)
        {
            try
            {
                string[] suf = { "B", "KB", "MB", "GB", "TB", "PB", "EB" };
                if (sizeBytes == 0)  return "0" + suf[0];
                long bytes = Math.Abs(sizeBytes);
                int place = Convert.ToInt32(Math.Floor(Math.Log(bytes, 1024)));
                double num = Math.Round(bytes / Math.Pow(1024, place), 1);
                return ($"{Math.Sign(sizeBytes) * num} {suf[place]}");
            }
            catch
            {
                return "N/A B";
            }
        }

        public static string Bitrate(int kbits)
        {
            if (kbits > 1024 * 10)
                return $"{((float)kbits / 1024).ToString("0.##")} mbps";
            else
                return $"{kbits} kbps";
        }

        public static string Time(long milliseconds)
        {
            return Time(TimeSpan.FromMilliseconds(milliseconds));
        }

        public static string Time(TimeSpan span, bool allowMs = true)
        {
            if (span.TotalHours >= 1f)
                return span.ToString(@"hh\:mm\:ss");

            if (span.TotalMinutes >= 1f)
                return span.ToString(@"mm\:ss");

            if (span.TotalSeconds >= 1f || !allowMs)
                return span.ToString(@"ss".TrimStart('0').PadLeft(1, '0')) + "s";

            return span.ToString(@"fff").TrimStart('0').PadLeft(1, '0') + "ms";
        }

        public static string TimeSw(Stopwatch sw)
        {
            long elapsedMs = sw.ElapsedMilliseconds;
            return Time(elapsedMs);
        }

        public static long TimestampToSecs(string timestamp, bool hasMilliseconds = true)
        {
            try
            {
                string[] values = timestamp.Split(':');
                int hours = int.Parse(values[0]);
                int minutes = int.Parse(values[1]);
                int seconds = int.Parse(values[2].Split('.')[0]);
                long secs = hours * 3600 + minutes * 60 + seconds;

                if (hasMilliseconds)
                {
                    int milliseconds = int.Parse(values[2].Split('.')[1].Substring(0, 2)) * 10;

                    if (milliseconds >= 500)
                        secs++;
                }

                return secs;
            }
            catch (Exception e)
            {
                Logger.Log($"TimestampToSecs({timestamp}) Exception: {e.Message}", true);
                return 0;
            }
        }

        public static long TimestampToMs(string timestamp, bool hasMilliseconds = true)
        {
            try
            {
                string[] values = timestamp.Split(':');
                int hours = int.Parse(values[0]);
                int minutes = int.Parse(values[1]);
                int seconds = int.Parse(values[2].Split('.')[0]);
                long ms = 0;

                if (hasMilliseconds)
                {
                    int milliseconds = int.Parse(values[2].Split('.')[1].Substring(0, 2)) * 10;
                    ms = hours * 3600000 + minutes * 60000 + seconds * 1000 + milliseconds;
                }
                else
                {
                    ms = hours * 3600000 + minutes * 60000 + seconds * 1000;
                }

                return ms;
            }
            catch (Exception e)
            {
                Logger.Log($"MsFromTimeStamp({timestamp}) Exception: {e.Message}", true);
                return 0;
            }
        }

        public static string SecsToTimestamp(long seconds)
        {
            return (new DateTime(1970, 1, 1)).AddSeconds(seconds).ToString("HH:mm:ss");
        }

        public static string MsToTimestamp(long milliseconds)
        {
            return (new DateTime(1970, 1, 1)).AddMilliseconds(milliseconds).ToString("HH:mm:ss");
        }

        public static string Ratio(long numFrom, long numTo)
        {
            float ratio = ((float)numFrom / (float)numTo) * 100f;
            return ratio.ToString("0.00") + "%";
        }

        public static float RatioFloat(long numFrom, long numTo)
        {
            double ratio = ((float)numFrom / (float)numTo) * 100f;

            if (ratio < 0f)
                ratio = 0f;

            return (float)ratio;
        }

        public static int RatioInt(long numFrom, long numTo)
        {
            double ratio = Math.Round(((float)numFrom / (float)numTo) * 100f);
            return ((int)ratio).Clamp(0, int.MaxValue);
        }

        public static string RatioIntStr(long numFrom, long numTo)
        {
            double ratio = Math.Round(((float)numFrom / (float)numTo) * 100f);
            return ratio + "%";
        }

        public static string ConcatStrings(string[] strings, char delimiter = ',', bool distinct = false)
        {
            string outStr = "";

            strings = strings.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            if (distinct)
                strings = strings.Distinct().ToArray();

            for (int i = 0; i < strings.Length; i++)
            {
                outStr += strings[i];
                if (i + 1 != strings.Length)
                    outStr += delimiter;
            }

            return outStr;
        }

        public static Nmkoder.Data.Size ParseSize(string str)
        {
            try
            {
                string[] values = str.Split('x');
                return new Nmkoder.Data.Size(values[0].GetInt(), values[1].GetInt());
            }
            catch
            {
                return new Nmkoder.Data.Size();
            }
        }

        public static string BeautifyFfmpegStats(string line)
        {
            return line.Remove("q=-0.0").Remove("q=-1.0").Remove("size=N/A").Remove("bitrate=N/A").Replace("frame=", "Frame: ")
                    .Replace("fps=", "FPS: ").Replace("q=", "QP: ").Replace("time=", "Time: ").Replace("speed=", "Relative Speed: ")
                    .Replace("bitrate=", "Bitrate: ").Replace("Lsize=", "Size: ").Replace("size=", "Size: ").TrimWhitespaces();
        }

        public static string CapsIfShort(string codec, int capsIfShorterThan = 5)
        {
            if (codec.Length < capsIfShorterThan)
                return codec.ToUpper();
            else
                return codec.ToTitleCase();
        }

        /// <summary>
        /// A path as one argument *inside a filtergraph* - for "subtitles=", "libvmaf=model_path=" and
        /// the like, where the value sits among the colons the filter splits its own options on.
        /// <para/>
        /// Single-quoted, which is ffmpeg's own quoting and covers everything the graph parser would
        /// otherwise read as syntax: spaces, colons, commas, square brackets, semicolons. It used to be
        /// double-quoted, and ffmpeg has no such thing - so the quotes became part of the filename and
        /// the burn-in failed on every path, except that the surrounding shell happened to strip them
        /// again for a path with no space in it. A path *with* one broke the command outright, the
        /// unquoted middle of it being read as another argument.
        /// <para/>
        /// Forward slashes on Windows too, and no escaping of the drive-letter colon: inside quotes it
        /// is an ordinary character, and the backslashes that used to escape it would now be literal.
        /// <para/>
        /// An apostrophe in the path is not solvable here and is refused before the run instead - see
        /// QuickConvertUi.GetBurnInProblem. It is quoted the way ffmpeg documents ('it'\''s'), and
        /// measured against ffmpeg 6.1 and the bundled build alike that does not survive the second
        /// unescaping pass the filter's own option parser makes; neither does any other spelling tried.
        /// </summary>
        public static string GetFilterPath(string path)
        {
            return $"'{path.Replace(@"\", @"/").Replace("'", @"'\''")}'";
        }

        public static int GetBitDepthFromPixelFormat(string pixFmt)
        {
            pixFmt = pixFmt.ToLower();
            if (pixFmt.MatchesWildcard("yuv*p")) return 8;
            if (pixFmt.MatchesWildcard("*p10?e")) return 10;
            if (pixFmt.MatchesWildcard("*p12?e")) return 12;
            if (pixFmt.MatchesWildcard("*p16?e")) return 16;
            return 0;
        }
    }
}
