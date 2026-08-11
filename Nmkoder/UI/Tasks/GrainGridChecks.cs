using Nmkoder.Data;
using Nmkoder.Extensions;
using System;
using System.Globalization;
using System.Linq;

namespace Nmkoder.UI.Tasks
{
    /// <summary>
    /// The Advanced-grid checks that travel with the Grain Synthesis row, shared by both encode tabs
    /// the way the grid machinery itself is - and for the same reason it moved out of the AV1AN
    /// partial: two copies would have drifted the moment either tab was touched. The subject became
    /// one binary when Quick Convert moved to the standalone encoders: both tabs now drive SVT-AV1
    /// through the same SvtAv1.json rows, and its precedence rules do not care which tab wrote the
    /// command line.
    /// <para/>
    /// Each tab passes its own grid lookup and keeps its own codec gate - every check here is for
    /// SVT-AV1 alone, aomenc's argument list carrying no grain rows at all, and each grid is reloaded
    /// per encoder so no other encoder can be holding one of these rows.
    /// </summary>
    static class GrainGridChecks
    {
        /// <summary>
        /// The Advanced grid rows that keep the *source's* grain rather than replacing it, and are
        /// therefore working against a Grain Synthesis mode that denoises the picture first.
        /// <para/>
        /// Read out of CLAUDE.md's own account of them: <c>noise-adaptive-filtering</c>,
        /// <c>noise-norm-strength</c>, <c>ac-bias</c> and <c>tune 5</c> are grain retention, a
        /// different mechanism from the two synthesisers - which is exactly why they never appeared in
        /// the collision check, and exactly why they need one of their own where the row denoises.
        /// </summary>
        private static readonly string[] GrainRetentionArgs =
        {
            "tune",
            "noise-adaptive-filtering",
            "noise-norm-strength",
            "ac-bias",
        };

        /// <summary>
        /// What SVT-AV1's tune 5 assigns for itself, and the values it assigns. Read out of the fork's
        /// own source rather than its documentation, which describes the bundle without saying when it
        /// is applied - <c>set_param_based_on_input</c>, after the whole command line has been parsed,
        /// so the order the flags are written in cannot save a row set beside it.
        /// <para/>
        /// <c>complex-hvs</c> is in this list now. It used to be named in the message and left out of
        /// the check, because the parameter list carried no row for it and there was nothing for the tune
        /// to overwrite; the row exists, so the hole is closed. A 0 typed there against tune 5 is
        /// overwritten exactly like the five beside it.
        /// </summary>
        private static readonly (string Arg, string Value)[] FilmGrainTuneOverrides =
        {
            ("enable-tf", "0"),
            ("enable-cdef", "0"),
            ("enable-restoration", "0"),
            ("complex-hvs", "1"),
            ("ac-bias", "4.00"),
            ("tx-bias", "1"),
        };

        /// <summary>
        /// The rows tune 5 leaves alone and strands. Each only acts on a filter the tune has switched
        /// off: cdef-scaling is read only where cdef_level is not 0, the two tf strengths have no
        /// temporal filtering left to strengthen, and noise-adaptive-filtering sets nothing but the
        /// "back off on a noisy frame" flags for CDEF and restoration, both already off.
        /// </summary>
        private static readonly string[] FilmGrainTuneInert =
        {
            "cdef-scaling",
            "tf-strength",
            "kf-tf-strength",
            "noise-adaptive-filtering",
        };

        /// <summary>
        /// Why an Advanced grid row will not do what it says beside the Grain Synthesis row, or "" if
        /// none is in its way.
        /// <para/>
        /// SVT-AV1 has three ways to be asked for film grain and takes exactly one of them, in this
        /// order: <c>--fgs-table</c> switches <c>--noise</c> off, and either of them switches
        /// <c>--film-grain</c> off - the first in <c>app_config.c</c>, the other two in
        /// <c>enc_handle.c</c>'s <c>set_param_based_on_input</c>, each with an <c>SVT_WARN</c> printed
        /// to the encoder's own stderr. On the AV1AN tab av1an collects that into a per-chunk log it
        /// deletes on success; on Quick Convert it lands in an encoder log nobody reads unless the run
        /// fails. Either way the losing setting simply had no effect, which is why this is said first.
        /// <para/>
        /// The row owns two of those three - it writes whichever of <c>--film-grain</c> and
        /// <c>--fgs-table</c> the mode calls for and never both - so what is left to collide is the
        /// grid's <c>noise</c>, which sits in the middle of that order, and the grid's own
        /// <c>fgs-table</c> row, which is the same argument written twice.
        /// <para/>
        /// Denoise is the half worth naming wherever the row loses. SVT reads its denoise flag only on
        /// the <c>--film-grain</c> path, so a row that displaces the strength drops the denoising with
        /// it: the grain then lands on top of the grain already in the picture instead of replacing
        /// it, which is the opposite of what ticking the box asks for.
        /// </summary>
        public static string GetGrainSynthProblem(Func<string, string> argValue, GrainSynthConfig config, GrainDelivery delivery)
        {
            string owned = config.GetOwnedEncoderArg(delivery);

            // Its value is a path rather than a number, so any of it counts. There is deliberately no
            // check for a grid row named film-grain: the shipped list has none, and the Argument
            // column is read-only, so one cannot be typed either.
            bool table = argValue("fgs-table").IsNotEmpty();
            int noise = argValue("noise").GetInt();

            // Every mode this row offers writes one of the two arguments, so there is nothing here
            // that can be running without something for the grid to collide with.
            if (owned.IsEmpty())
                return "";

            // The same argument written twice: not two settings colliding so much as two spellings of
            // one, and neither this app nor SVT's warning can say which the user meant.
            if (owned == "fgs-table" && table)
                return "Note: the Advanced tab has an fgs-table row filled in, and the Grain Synthesis row is " +
                    "writing fgs-table for itself - so the two are both on the command line and which one wins " +
                    "is not something this app decides. Clear that row and set the table on the Grain Synthesis row.";

            if (noise < 1 && !(owned == "film-grain" && table))
                return "";

            // Precedence, not preference: fgs-table beats noise beats film-grain, so which of the two
            // settings survives depends entirely on which pair has met.
            bool tableWins = owned == "film-grain" && table;
            string winner = tableWins ? "the table" : owned == "fgs-table" ? "the grain table" : "--noise";
            string loser = tableWins || owned == "film-grain" ? $"Grain Synthesis {config.Strength}" : $"noise ({noise})";

            string subject = tableWins
                ? "fgs-table applies a grain table from a file instead of analysing the source, and SVT-AV1 " +
                    "takes one of the three rather than several"
                : $"noise ({noise}) is SVT-AV1's own second grain synthesiser, and it takes one of the three " +
                    $"rather than several";

            // Only where the strength is the thing being dropped: a table does not denoise either, but
            // nothing about a table mode ever promised to.
            string denoise = owned == "film-grain" && config.Denoise
                ? $" Denoise goes with it: {winner} does not denoise the source, so the grain being " +
                    $"synthesised lands on top of the grain already in the picture rather than replacing it."
                : "";

            return $"Note: the Advanced tab's {subject} - so {loser} is dropped and {winner} runs " +
                $"instead.{denoise} Clear whichever of the two you did not mean.";
        }

        /// <summary>
        /// Why the Advanced grid's grain *retention* rows are pulling against the Grain Synthesis row,
        /// or "" when they are not.
        /// <para/>
        /// This is not an argument collision - nothing is overwritten and no warning is printed
        /// anywhere. Retention makes the encoder's filters and transforms stop averaging the source's
        /// own grain away; synthesis takes that grain out of the picture and describes it instead.
        /// They are alternatives, so retention rows set beside a mode that denoises spend bitrate and
        /// encoding time protecting texture that is no longer in the frames, and the grain that comes
        /// back is the synthesised description rather than the film's.
        /// <para/>
        /// A note rather than a refusal, because the encode is not broken by it. <c>tune</c> is
        /// reported only at 5, its film grain bundle - the other tunes are not retention settings.
        /// </summary>
        /// <param name="denoiseClause"> Which mechanism took the grain out, since the two tabs do it
        /// differently and the user should be sent looking at the right one - a pass of the AV1AN
        /// tab's own, a filter in Quick Convert's chain, or the encoder's flag on either. </param>
        public static string GetGrainRetentionProblem(Func<string, string> argValue, GrainSynthConfig config, string denoiseClause)
        {
            var set = GrainRetentionArgs
                .Select(a => (Arg: a, Value: argValue(a)))
                .Where(a => a.Value.IsNotEmpty() && (a.Arg != "tune" || a.Value == "5"))
                .Select(a => $"{a.Arg} {a.Value}")
                .ToList();

            if (set.Count < 1)
                return "";

            return $"Note: the Advanced tab is set to keep the source's own grain ({string.Join(", ", set)}), " +
                $"and Grain Synthesis is replacing it - {denoiseClause}. Those rows cost bitrate and encoding time " +
                $"protecting texture that has been taken out of the frames, and the grain in the output is the " +
                $"synthesised one either way. Retention and synthesis are alternatives: clear those rows, or " +
                $"set Grain Synthesis to No grain synthesis.";
        }

        /// <summary>
        /// Why rows set beside SVT-AV1's tune 5 will not do what they say, or "" if none are.
        /// <para/>
        /// Tune 5 is a bundle rather than a preference, and it wins: the six values it sets are
        /// assigned after the command line has been read, so a grid row naming one of them is
        /// overwritten with only an <c>SVT_WARN</c> to say so. Four more rows survive the bundle and
        /// are stranded by it, their filters having been switched off.
        /// <para/>
        /// A row set to what the tune sets anyway is not reported. It is not being overruled in any
        /// sense the user can act on, and naming it would send someone to clear a row that agrees with
        /// the encode.
        /// <para/>
        /// No content preset sets <c>tune 5</c> - the one that did has been removed - so this cannot
        /// fire from a preset at all. It is for a row typed by hand, which is the same division
        /// <c>GetUnsupportedAdvancedArgsProblem</c> draws.
        /// </summary>
        public static string GetFilmGrainTuneProblem(Func<string, string> argValue)
        {
            if (argValue("tune") != "5")
                return "";

            var overwritten = FilmGrainTuneOverrides
                .Select(o => (o.Arg, o.Value, Set: argValue(o.Arg)))
                .Where(o => o.Set.IsNotEmpty() && !IsSameArgValue(o.Set, o.Value))
                .Select(o => $"{o.Arg} {o.Set}")
                .ToList();

            var inert = FilmGrainTuneInert.Where(a => argValue(a).IsNotEmpty()).ToList();

            if (overwritten.Count < 1 && inert.Count < 1)
                return "";

            string s = "Note: tune is set to 5, SVT-AV1's film grain bundle, which sets enable-tf 0, " +
                "enable-cdef 0, enable-restoration 0, complex-hvs 1, ac-bias 4.00 and tx-bias 1 for " +
                "itself - and it does so after the whole command line has been read, so a row beside it " +
                "does not win.";

            if (overwritten.Count > 0)
            {
                bool one = overwritten.Count == 1;
                s += $" {string.Join(", ", overwritten)} {(one ? "is" : "are")} therefore overwritten and " +
                    $"{(one ? "does" : "do")} nothing.";
            }

            if (inert.Count > 0)
            {
                bool one = inert.Count == 1;
                s += $" {string.Join(", ", inert)} {(one ? "is" : "are")} left as set, but {(one ? "it" : "each")} " +
                    $"only acts on a filter the tune has switched off, so {(one ? "it does" : "they do")} " +
                    $"nothing either.";
            }

            return s + $" Clear {(overwritten.Count + inert.Count == 1 ? "that row" : "those rows")}, or pick another tune.";
        }

        /// <summary>
        /// Whether two argument values ask for the same thing. Compared as text first, then as numbers
        /// where both are numbers, so 4 and 4.00 are one value - the tune above is stated to two
        /// decimal places and nobody types it that way.
        /// </summary>
        private static bool IsSameArgValue(string a, string b)
        {
            if (a.Trim() == b.Trim())
                return true;

            return float.TryParse(a.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                float.TryParse(b.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float y) && x == y;
        }
    }
}
