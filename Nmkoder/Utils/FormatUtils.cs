using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.OS;
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
        /// Forward slashes on Windows, and the drive-letter colon escaped, because the quotes above are
        /// consumed by the graph parser rather than passed on: ffmpeg unescapes twice, and the second
        /// pass - the filter's own option parser, splitting on ':' - sees a bare string. So "C:/x.mkv"
        /// reached it as the filename "C" followed by "/x.mkv" as the *next* positional option, which
        /// for "subtitles=" is original_size, and every burn-in on Windows died on "Unable to parse
        /// original_size". A backslash inside the quotes is literal to the first pass and an escape to
        /// the second, which is the one spelling that survives both. That makes this Windows-only in
        /// practice, though a colon is a legal character in a Linux filename and broke it there too.
        /// <para/>
        /// An apostrophe needs one level more than ffmpeg documents, for the same reason the colon does.
        /// The documented spelling closes the quoted run, escapes, and reopens - 'it'\''s' - which is
        /// right for a value the graph parser hands on whole, and this one is unescaped again after it:
        /// the '\' is eaten by the first pass and the bare apostrophe reopens a quote to the second, so
        /// the path came back missing its apostrophe with ":si=0" stuck on the end. Written 'it'\\\''s -
        /// close-quote, "\\" (a literal backslash to the first pass, the escape to the second), "\'" (a
        /// literal apostrophe to the first, the escaped apostrophe to the second), reopen-quote - it
        /// survives both. This was refused before the run until 2.8.23, on the finding that no spelling
        /// worked; that was measured of one-level spellings only, which is what the colon turned out to
        /// be as well.
        /// <para/>
        /// '=' is escaped for the same reason and is a separate character: that second pass splits a
        /// key from its value on '=' before it splits options on ':', so a path holding one - a folder
        /// called "Season=1", a file called "Movie=Extended.mkv" - came back as "Option not found"
        /// naming everything up to it. It survived a spot check only because an *earlier* colon or
        /// space in the same path changes where that scan starts, which is why a Windows path, always
        /// carrying a drive colon, hid this one while every colon-free path met it.
        /// <para/>
        /// The backslash is the one character handled by platform. On Windows it is the separator and
        /// nothing else - the filename cannot contain one - so it becomes a slash, which ffmpeg reads
        /// as an ordinary character and Windows accepts as a separator. Everywhere else it is a legal
        /// filename character, and substituting it there pointed the filter at a path that does not
        /// exist ("Unable to open .../back/slash.mkv"), so it is escaped like the rest.
        /// <para/>
        /// A UNC path survives that substitution and does not need a case of its own, which is worth
        /// recording because it looks as though it would. Windows normalisation turns every forward
        /// slash into a backslash and keeps the leading pair - "a series of slashes that follow the
        /// first two slashes are collapsed into a single slash" - and identifies a UNC path by two
        /// *separators* rather than two backslashes, so //NAS/Media/clip.mkv round-trips to
        /// \\NAS\Media\clip.mkv. ffmpeg does not leave that to chance either: on Windows it runs the
        /// name through GetFullPathNameW itself before opening it. A "\\?\" path is demoted to an
        /// ordinary one by the same substitution, since only the canonical backslash form skips
        /// normalisation - it still opens, and nothing here can produce one anyway, MediaFile's
        /// ImportPath being FileInfo.FullName.
        /// <para/>
        /// The replacements are ordered, and that is what keeps them unambiguous: whichever branch runs
        /// first leaves no backslash behind that this method did not write, so every one after it is an
        /// escape rather than data. Writing any of them before it would have their own backslashes
        /// substituted or doubled in turn.
        /// <para/>
        /// Measured, not reasoned out - end to end through Shell.WrapArg, Shell.BuildArguments, .NET's
        /// argument parsing, sh and ffmpeg, against both ffmpeg 6.1 and a current BtbN master build,
        /// over paths carrying spaces, $, backticks, %, &amp;, !, ';', ',', '=', square brackets, a
        /// double quote, an apostrophe, a literal backslash and a trailing space or tab, checked by the
        /// frames differing from the same chain with no burn-in in it.
        /// </summary>
        public static string GetFilterPath(string path)
        {
            // A backslash the caller meant, before any this method adds - see the note above.
            string p = Shell.IsWindows ? path.Replace(@"\", @"/") : path.Replace(@"\", @"\\");

            p = p.Replace(":", @"\:").Replace("=", @"\=").Replace("'", @"'\\\''");

            return $"'{EscapeTrailingWhitespace(p)}'";
        }

        /// <summary>
        /// Escapes a trailing space, tab or newline, which the second unescaping pass would otherwise
        /// trim off the end of the value. It trims back only as far as the last escape or quote it saw,
        /// and by then the quotes are gone - so a file called "ep06.mkv " arrived as "ep06.mkv" and
        /// could not be opened, while the same space anywhere else in the path was never at risk. Any
        /// escape does the job, so the character is written back with a backslash in front of it.
        /// <para/>
        /// Runs after the other replacements rather than before, which costs nothing and is one less
        /// thing to reason about: none of them can add or remove trailing whitespace, and a path ending
        /// in an apostrophe ends in a quote once they have run.
        /// </summary>
        private static string EscapeTrailingWhitespace(string value)
        {
            int end = value.Length;

            // ffmpeg's own set, from libavutil's WHITESPACES - not char.IsWhiteSpace, which would
            // escape characters that pass through untouched anyway.
            while (end > 0 && " \t\r\n".Contains(value[end - 1]))
                end--;

            if (end == value.Length)
                return value;

            return value.Substring(0, end) + string.Concat(value.Substring(end).Select(c => $@"\{c}"));
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
