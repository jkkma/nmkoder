using Nmkoder.Extensions;
using Nmkoder.OS;
using System.Collections.Generic;
using System.Linq;

namespace Nmkoder.Data.Codecs
{
    /// <summary>
    /// How the Quick Convert Advanced tab's filled-in rows are spelled on the command line ffmpeg
    /// actually gets. The grid hands every encoder the same "key=value key=value" string - see
    /// <see cref="UI.Tasks.EncoderArgs.BuildPairs"/> - and this is the one place that says what each
    /// encoder does with it.
    /// <para/>
    /// There are two spellings and the split is not a matter of taste. x264, x265, SVT-AV1 and libaom
    /// each expose their whole parameter table through a single ffmpeg option holding a ":"-separated
    /// list, so a row is a "key=value" inside it. libvpx-vp9 has no such option - measured against the
    /// bundled build, there is no "-vpx-params" and never has been - so its parameters are reached as
    /// ordinary AVOptions, one "-key value" each, and NVENC is the same. That is also why VP9's
    /// argument list names ffmpeg's own AVOption spellings rather than vpxenc's: they are not the same
    /// vocabulary, and half of vpxenc's names have no AVOption behind them at all.
    /// <para/>
    /// <b>A second "-x265-params" replaces the first outright rather than adding to it</b>, which is
    /// measured and is why <c>Libx265</c> merges its own pass and lossless settings into the one list
    /// this builds instead of emitting a second option beside it. The same is true of the other three.
    /// <para/>
    /// What an encoder does with a parameter it does not know differs too, and is worth knowing before
    /// reading a bug report: x264, x265 and SVT-AV1 print one warning line and encode anyway, so a
    /// setting can go missing in silence; libaom refuses the encode naming the parameter; and an
    /// unknown AVOption is not even an encoder question - ffmpeg dies parsing its own arguments. The
    /// argument column of the grid is read-only, so a name that is not in the shipped list cannot be
    /// typed in the first place; what this governs is a list that has drifted from the ffmpeg the user
    /// is running.
    /// </summary>
    static class FfmpegEncoderArgs
    {
        /// <summary>
        /// The ffmpeg option carrying this encoder's whole parameter list, or "" for an encoder that
        /// has none and takes each parameter as an AVOption of its own.
        /// </summary>
        public static string ParamsFlag(string encoderName)
        {
            switch (encoderName)
            {
                case nameof(Video.Libx264): return "-x264-params";
                case nameof(Video.Libx265): return "-x265-params";
                case nameof(Video.LibSvtAv1): return "-svtav1-params";
                case nameof(Video.LibAomAv1): return "-aom-params";
                default: return "";
            }
        }

        /// <summary> The "key=value" entries of a pairs string, in order, blanks dropped. </summary>
        public static List<string> Pairs(string pairs)
        {
            return (pairs ?? "").Split(' ').Where(p => p.IsNotEmpty() && p.Contains('=')).ToList();
        }

        /// <summary>
        /// The command-line fragment for a pairs string, or "" where nothing is set. Not used by
        /// <c>Libx265</c>, which has settings of its own to merge into the same option and builds
        /// the list itself.
        /// </summary>
        public static string Render(string encoderName, string pairs)
        {
            List<string> entries = Pairs(pairs);

            if (entries.Count < 1)
                return "";

            string flag = ParamsFlag(encoderName);

            if (flag.IsNotEmpty())
                return $"{flag} {ParamsList(entries)}";

            return string.Join(" ", entries.Select(e =>
            {
                int at = e.IndexOf('=');
                return $"-{e.Substring(0, at).TrimStart('-')} {Quote(e.Substring(at + 1))}";
            }));
        }

        /// <summary>
        /// The ":"-joined body of a parameter option, quoted as one argument. Quoted because a value
        /// is not always a bare number: x265's master-display is written "G(13250,34500)B(...)...",
        /// and parentheses are the shell's rather than the value's on Linux and macOS.
        /// </summary>
        public static string ParamsList(IEnumerable<string> entries)
        {
            return Shell.WrapArg(string.Join(":", entries));
        }

        /// <summary> How one argument is written on the command line, for the grid's tooltip and its
        /// right-click window - the point of an Advanced tab being that it says what it is sending. </summary>
        public static string Spell(string encoderName, string argument)
        {
            string flag = ParamsFlag(encoderName);
            string name = (argument ?? "").TrimStart('-');
            return flag.IsNotEmpty() ? $"{flag} {name}=…" : $"-{name}";
        }

        /// <summary> An AVOption's value, quoted only where the shell would otherwise read part of it.
        /// A pairs string cannot carry a space, so this is about metacharacters and nothing else. </summary>
        private static string Quote(string value)
        {
            bool plain = value.IsNotEmpty() && value.All(c => char.IsLetterOrDigit(c) || "._-+,:/=".Contains(c));
            return plain ? value : Shell.WrapArg(value);
        }
    }
}
