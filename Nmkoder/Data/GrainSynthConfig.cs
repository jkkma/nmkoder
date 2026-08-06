using Nmkoder.Extensions;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Nmkoder.Data
{
    /// <summary>
    /// Where the grain in an AV1 encode comes from. Not saved anywhere - nothing on the AV1AN Video tab
    /// is - so entries may be reordered freely; the tab opens on <see cref="Off"/> every session.
    /// </summary>
    public enum GrainSynthMode
    {
        /// <summary> Nothing is synthesised and nothing is denoised. </summary>
        Off,

        /// <summary> The encoder's own analysis: SVT-AV1's <c>--film-grain</c>, aomenc's
        /// <c>--denoise-noise-level</c>. One number, no extra pass, works on every AV1 build. </summary>
        Encoder,

        /// <summary> Measured off this source: denoise it, diff the two with grav1synth, encode the
        /// denoised picture with the table that came out. The expensive one, and the accurate one. </summary>
        Measured,

        /// <summary> A grain table file the user already has. </summary>
        Table,

        /// <summary> One of grav1synth's built-in film stock tables. </summary>
        Preset,

        /// <summary> Photon noise at a given ISO, synthesised by grav1synth from the frame size and the
        /// transfer curve. </summary>
        PhotonNoise,
    }

    /// <summary>
    /// How a grain description reaches the output, which is not something the user picks - it is decided
    /// from what the encoder in front of them can actually do.
    /// </summary>
    public enum GrainDelivery
    {
        /// <summary> Nothing to deliver. </summary>
        None,

        /// <summary> The encoder analyses the source itself, from a strength. </summary>
        EncoderAnalysis,

        /// <summary> The encoder is handed the table: SVT-AV1's <c>--fgs-table</c>. Preferred wherever it
        /// exists, because the grain is described in the bitstream from the first chunk and nothing has to
        /// touch the finished file. </summary>
        EncoderTable,

        /// <summary> grav1synth rewrites the finished AV1 bitstream. Works on every AV1 encode whatever
        /// the encoder is, which is what makes the presets and the photon noise reachable at all - and
        /// what rescues the table modes on a mainline SVT-AV1 binary, which has no <c>--fgs-table</c>. </summary>
        PostApply,
    }

    /// <summary>
    /// The Grain Synthesis row, which owns every way this app can put film grain in an AV1 file.
    /// <para/>
    /// It used to be a strength spinner and a Denoise box, and the reason it is a mode selector now is not
    /// that grav1synth added options - it is that there were already three ways to ask for grain and they
    /// silently overrode each other. <c>--film-grain</c> sat on this row, while <c>--noise</c> and
    /// <c>--fgs-table</c> sat in the Advanced grid, and SVT-AV1 takes exactly one of the three: the table
    /// switches <c>--noise</c> off, and either of them switches <c>--film-grain</c> off, each with an
    /// <c>SVT_WARN</c> that goes to the encoder's stderr, which av1an collects per chunk into a log
    /// <c>HandleTempFolder</c> deletes on a successful run. <see cref="Av1anUi.GetGrainSynthProblem"/>
    /// existed to report that collision after the fact. One control that owns all three cannot express it.
    /// <para/>
    /// **The strength is still here, and dropping it would have been a regression rather than a
    /// simplification.** <c>--fgs-table</c> is a PSY-line parameter: mainline SVT-AV1 does not have it, and
    /// neither does the libsvtav1 inside the bundled ffmpeg. <c>--film-grain N</c> is on every build, costs
    /// one number and no extra pass, and denoises the picture itself - which is where the bitrate saving
    /// actually comes from. It is the right answer for most people and it stays the cheap default.
    /// <para/>
    /// What the other modes add is the accuracy (a table measured off this scan rather than an encoder's
    /// guess at it), the presets, and photon noise - and, through <see cref="GrainDelivery.PostApply"/>,
    /// a route to all of them that needs nothing of the encoder at all.
    /// </summary>
    public class GrainSynthConfig
    {
        public GrainSynthMode Mode = GrainSynthMode.Off;

        /// <summary> <see cref="GrainSynthMode.Encoder"/>: SVT-AV1's <c>--film-grain</c> (0-50) or
        /// aomenc's <c>--denoise-noise-level</c>. </summary>
        public int Strength = 0;

        /// <summary> <see cref="GrainSynthMode.Encoder"/>: whether the encoder codes the denoised picture
        /// rather than laying synthetic grain over the grain that is already there. </summary>
        public bool Denoise = false;

        /// <summary> <see cref="GrainSynthMode.Measured"/>: how hard the denoise pass in front of the diff
        /// runs. See <see cref="GetDenoiseFilter"/> for what the number means. </summary>
        public int DenoiseStrength = 4;

        /// <summary> <see cref="GrainSynthMode.Table"/>: a grain table the user already has. </summary>
        public string TablePath = "";

        /// <summary> <see cref="GrainSynthMode.Preset"/>: a name out of <see cref="Presets"/>. </summary>
        public string Preset = "";

        /// <summary> <see cref="GrainSynthMode.PhotonNoise"/>: the ISO the noise is modelled at. </summary>
        public int Iso = 400;

        /// <summary> <see cref="GrainSynthMode.PhotonNoise"/>: grain on the chroma planes as well as
        /// luma. grav1synth's own default is luma only. </summary>
        public bool Chroma = false;

        /// <summary>
        /// grav1synth's built-in film stock tables, in the order its own <c>presets</c> subcommand prints
        /// them: the two standalones, then each format preset bare and with its three film stock
        /// modifiers - <c>16mm-3</c> being 16mm shot on Kodak Vision3 200T. The list is read out of the
        /// binary at startup where one is present (<see cref="Media.Grav1synth.LoadPresetsAsync"/>) and
        /// falls back to this, so a build older or newer than the one this was written against still
        /// fills the dropdown rather than emptying it. A name the binary does not know is refused along
        /// with the whole command, which is why this is a copy of one build's output rather than a guess.
        /// </summary>
        public static readonly string[] FallbackPresets =
        {
            "Super8", "MaxMid",
            "16mm", "16mm-1", "16mm-2", "16mm-3",
            "Classic35", "Classic35-1", "Classic35-2", "Classic35-3",
            "Modern35", "Modern35-1", "Modern35-2", "Modern35-3",
        };

        public static string[] Presets = FallbackPresets;

        /// <summary> The dropdown's contents. Short, because the box is 200 wide and the readout under it
        /// has the whole line to say what the pick will do to this file. </summary>
        public static string GetLabel(GrainSynthMode mode)
        {
            switch (mode)
            {
                case GrainSynthMode.Off: return "No grain synthesis";
                case GrainSynthMode.Encoder: return "Encoder analysis";
                case GrainSynthMode.Measured: return "Measured from source";
                case GrainSynthMode.Table: return "Grain table file";
                case GrainSynthMode.Preset: return "Film stock preset";
                case GrainSynthMode.PhotonNoise: return "Photon noise (ISO)";
                default: return mode.ToString();
            }
        }

        public static readonly GrainSynthMode[] AllModes = (GrainSynthMode[])Enum.GetValues(typeof(GrainSynthMode));

        /// <summary> Whether anything happens at all. Encoder mode at a strength of 0 is Off spelled
        /// differently - both encoders read 0 as "leave the source alone" - so it is reported as such
        /// rather than being carried around as a mode that does nothing. </summary>
        public bool Runs
        {
            get { return Mode != GrainSynthMode.Off && (Mode != GrainSynthMode.Encoder || Strength > 0); }
        }

        /// <summary> Whether this mode is a grain *table*, which is the half grav1synth measures or the
        /// user supplies, as against the half an encoder or grav1synth generates for itself. </summary>
        public bool UsesTable
        {
            get { return Runs && (Mode == GrainSynthMode.Measured || Mode == GrainSynthMode.Table); }
        }

        /// <summary> Whether the picture handed to the encoder is denoised, which is the difference
        /// between saving bitrate and merely adding grain - see <see cref="GetNote"/>. </summary>
        public bool DenoisesSource
        {
            get { return Runs && (Mode == GrainSynthMode.Measured || (Mode == GrainSynthMode.Encoder && Denoise)); }
        }

        /// <summary> Whether grav1synth has to be present for this mode to run at all. Encoder mode never
        /// needs it; a table the user supplies only needs it where the encoder cannot take one. </summary>
        public bool NeedsGrav1synth(GrainDelivery delivery)
        {
            return Runs && (Mode == GrainSynthMode.Measured || delivery == GrainDelivery.PostApply);
        }

        /// <summary>
        /// Which of SVT-AV1's three grain arguments this mode writes, if any - so the Advanced grid can be
        /// checked for a row that would collide with it. Encoder mode is <c>--film-grain</c>, the table
        /// modes are <c>--fgs-table</c>, and the generated ones write nothing at all because they never
        /// reach the encoder.
        /// </summary>
        public string GetOwnedEncoderArg(GrainDelivery delivery)
        {
            if (!Runs)
                return "";

            if (Mode == GrainSynthMode.Encoder)
                return "film-grain";

            return delivery == GrainDelivery.EncoderTable ? "fgs-table" : "";
        }

        /// <summary>
        /// The denoise pass that runs in front of the diff, as an ffmpeg filter.
        /// <para/>
        /// hqdn3d, and **spatial only** - the temporal halves of its default 4:3:6:4.5 are set to 0 here on
        /// purpose. hqdn3d's temporal filter is not motion compensated, so on anything that moves it blends
        /// the frame before into the frame at hand, and the difference between the two files at that point
        /// is a ghost of the previous frame rather than the grain this pass exists to measure. A still
        /// source is where a temporal denoiser wins and a still source is not what anyone measures grain
        /// on.
        /// <para/>
        /// The strength scales the two spatial numbers off ffmpeg's own defaults, so 4 *is* the default and
        /// the box's range either side of it is a proportion of it. Chroma is kept at the 3:4 ratio ffmpeg
        /// ships, which is the ratio the filter's own defaults use.
        /// <para/>
        /// It is not the best denoiser ffmpeg has - nlmeans and bm3d both are, by a distance - and it is
        /// the only one whose speed survives a whole film. This pass already costs a full lossless
        /// intermediate and a second decode; putting a 1 fps filter in it would make the mode ornamental.
        /// </summary>
        public string GetDenoiseFilter()
        {
            double luma = 4.0 * DenoiseStrength / 4.0;
            double chroma = 3.0 * DenoiseStrength / 4.0;
            return $"hqdn3d={luma.ToString("0.##", CultureInfo.InvariantCulture)}:" +
                $"{chroma.ToString("0.##", CultureInfo.InvariantCulture)}:0:0";
        }

        /// <summary>
        /// Why this cannot run as configured, or "" when it can. Everything here is answerable without
        /// touching a binary - a missing grav1synth, and an encoder that cannot take a table, are asked
        /// about by <see cref="UI.Tasks.GrainSynthUi"/>, which can await.
        /// </summary>
        public string GetProblem()
        {
            if (!Runs)
                return "";

            if (Mode == GrainSynthMode.Table)
            {
                if (TablePath.IsEmpty())
                    return "Grain Synthesis is set to a grain table file and no file is named. Pick one, or choose another mode.";

                if (!File.Exists(TablePath))
                    return $"The grain table '{TablePath}' does not exist.";

                if (!LooksLikeGrainTable(TablePath))
                    return $"'{Path.GetFileName(TablePath)}' is not a film grain table - a table's first line is 'filmgrn1'. " +
                        "Tables are produced by grav1synth, and by this row's Measured from source mode.";
            }

            if (Mode == GrainSynthMode.Preset && Preset.IsEmpty())
                return "Grain Synthesis is set to a film stock preset and none is picked.";

            return "";
        }

        /// <summary>
        /// Whether a file is a grain table, which is one line of reading rather than a parse. Worth doing
        /// because the mistake it catches is not a typo: the natural thing to point this row at is the
        /// *video* somebody made the table from, and every consumer of a bad table - av1an's encoder, or
        /// grav1synth itself - reports it from inside a run that has already started.
        /// </summary>
        public static bool LooksLikeGrainTable(string path)
        {
            try
            {
                using (StreamReader r = new StreamReader(path))
                    return (r.ReadLine() ?? "").Trim() == "filmgrn1";
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// The readout under the row, and the line the log carries per file. Clauses joined by a middle
        /// dot, as the resize, border and tone map readouts are, and kept to one line - <c>Classes="hint"</c>
        /// sets no TextWrapping, so a longer sentence is clipped at the window's edge rather than wrapped.
        /// <para/>
        /// The clause that matters most is the last one, and it is the reason this row needs a readout at
        /// all: **grain synthesis only saves bitrate where the picture being coded has had the grain taken
        /// out of it.** Two of these modes do that and three do not, and from the outside all five produce
        /// a grainy-looking AV1 file. Somebody who picks a film stock preset expecting the saving that
        /// <c>--film-grain</c> would have given them has instead coded the source's own grain and then put
        /// more on top, and nothing else on screen would have said so.
        /// </summary>
        public string GetNote(GrainDelivery delivery)
        {
            if (!Runs)
                return "";

            List<string> parts = new List<string>();

            switch (Mode)
            {
                case GrainSynthMode.Encoder:
                    parts.Add($"The encoder measures the source itself, at {Strength}");
                    break;
                case GrainSynthMode.Measured:
                    parts.Add($"Denoised ({GetDenoiseFilter()}) and measured by grav1synth");
                    break;
                case GrainSynthMode.Table:
                    parts.Add($"Table '{Path.GetFileName(TablePath)}'");
                    break;
                case GrainSynthMode.Preset:
                    parts.Add($"grav1synth's '{Preset}' film stock");
                    break;
                case GrainSynthMode.PhotonNoise:
                    parts.Add($"Photon noise, ISO {Iso}, {(Chroma ? "luma and chroma" : "luma only")}");
                    break;
            }

            // Only the odd one out is named. Being applied by the encoder is what every mode that can be
            // does, so saying so on each of them costs a clause and tells nobody anything; a rewrite of the
            // finished file is the one that changes what happens after the encode.
            if (delivery == GrainDelivery.PostApply)
                parts.Add("written in afterwards by grav1synth");

            parts.Add(DenoisesSource
                ? "the picture is coded clean, so the grain costs bytes instead of bitrate"
                : "the source's own grain is coded too, so this adds grain rather than saving");

            return string.Join(" · ", parts);
        }

        /// <summary> A copy, for the encode to hold while the UI goes on being edited. </summary>
        public GrainSynthConfig Clone()
        {
            return (GrainSynthConfig)MemberwiseClone();
        }
    }

    /// <summary>
    /// A settled grain setting: what was asked for, how it will reach the output, and where the table is
    /// once there is one.
    /// <para/>
    /// It exists for the same reason <c>Av1anUi.CurrentDeinterlace</c> does. The encoder's arguments are
    /// built from a dictionary that is filled straight off the controls, and by then the questions that
    /// decide what to write - does this binary have <c>--fgs-table</c>, did the measuring pass produce a
    /// table, where did it put it - have all been answered and cannot be asked again from there.
    /// </summary>
    public class GrainPlan
    {
        public GrainSynthConfig Config = new GrainSynthConfig();
        public GrainDelivery Delivery = GrainDelivery.None;

        /// <summary> The table on disk: the user's own file, or the one the measuring pass wrote. </summary>
        public string TablePath = "";

        /// <summary> Whether the encoder is handed a strength to analyse the source with. </summary>
        public bool IsEncoderAnalysis { get { return Delivery == GrainDelivery.EncoderAnalysis; } }

        /// <summary> Whether the encoder is handed the table itself. </summary>
        public bool IsEncoderTable { get { return Delivery == GrainDelivery.EncoderTable && TablePath.IsNotEmpty(); } }

        /// <summary> Whether grav1synth rewrites the finished file. </summary>
        public bool IsPostApply { get { return Delivery == GrainDelivery.PostApply; } }

        /// <summary> Whether a denoised copy has to be rendered before av1an starts. </summary>
        public bool NeedsDenoisePass { get { return Config.Mode == GrainSynthMode.Measured && Config.Runs; } }
    }
}
