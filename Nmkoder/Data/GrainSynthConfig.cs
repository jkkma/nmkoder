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

        /// <summary> Measured off this source: denoise it and diff the two with grav1synth to get a
        /// table. The accurate way to describe a source's grain, and much the most expensive - about
        /// 3.7 fps at 1080p, so a working day for a feature. **Utility only**: it is the Film Grain
        /// utility's Measure operation, whose output feeds <see cref="Table"/> on either encode
        /// tab. </summary>
        Measured,

        /// <summary> A grain table file the user already has. </summary>
        Table,

        /// <summary> One of grav1synth's built-in film stock tables. Read out of grav1synth as a table
        /// when the encode starts (<see cref="Media.Grav1synth.MakePresetTableAsync"/>) and delivered
        /// through the encoder exactly as <see cref="Table"/> is, so it costs no pass over the video and
        /// no rewrite of the output. </summary>
        Preset,

        /// <summary> Photon noise at a given ISO, synthesised by grav1synth from the frame size and the
        /// transfer curve. **Utility only**, for the same reason as Preset. </summary>
        PhotonNoise,
    }

    /// <summary>
    /// How a grain description reaches the output, which is not something the user picks - it is decided
    /// from what the encoder in front of them can actually do.
    /// <para/>
    /// There are only two, and there is deliberately no third for "grav1synth rewrites the finished file".
    /// **This row is what the encoder does while it encodes; the Film Grain utility is what is done to a
    /// file afterwards** - the same division Cut and Deinterlace Video draw against the encode tabs' own
    /// Trim and Deinterlace settings. A mode that can only be delivered by rewriting the output is not a
    /// mode this row should offer, and a table an encoder cannot take is a refusal here rather than a
    /// silent detour through another tool.
    /// </summary>
    public enum GrainDelivery
    {
        /// <summary> Nothing to deliver. </summary>
        None,

        /// <summary> The encoder analyses the source itself, from a strength. </summary>
        EncoderAnalysis,

        /// <summary> The encoder is handed the table: SVT-AV1's <c>--fgs-table</c>, aomenc's
        /// <c>--film-grain-table</c>. </summary>
        EncoderTable,
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
    /// What the table modes add is a description this app did not have to guess at: one measured earlier
    /// and kept, or one of grav1synth's built-in film stocks. All three reach the encoder the same way, as
    /// its own grain-table parameter, so all three cost the encode nothing beyond the encode.
    /// <para/>
    /// What is still not here is grain written into a *finished* file - the photon noise, and every one of
    /// these applied after the fact. That is the Film Grain utility, because this row is what the encoder
    /// does while it encodes. The film stock presets used to be filed under that heading too and are not,
    /// for the reason <see cref="EncodeModes"/> gives: a preset is a table, and only the way grav1synth
    /// hands it over made it look like a post-pass.
    /// </summary>
    public class GrainSynthConfig
    {
        public GrainSynthMode Mode = GrainSynthMode.Off;

        /// <summary> <see cref="GrainSynthMode.Encoder"/>: SVT-AV1's <c>--film-grain</c> (0-50) or
        /// aomenc's <c>--denoise-noise-level</c>. </summary>
        public int Strength = 0;

        /// <summary>
        /// The encoder-analysis strength from which the readout names the denoise as a cost of its own.
        /// Both AV1 encoders read the strength on a 0-50 scale and denoise at the same number when their
        /// denoise flag is set - SVT-AV1's <c>--film-grain</c> drives its own denoiser, and aomenc's
        /// <c>--denoise-noise-level</c> *is* the denoiser's level - so the top of the scale is maximum
        /// smoothing of the real picture as well as maximum synthetic grain, and nothing else on screen
        /// says the two rise together. Ordinary grainy-film settings sit well under this line; from here
        /// up the smoothing is the louder half of the trade.
        /// </summary>
        public const int HeavyDenoiseStrength = 30;

        /// <summary>
        /// Whether the picture being encoded has the source's grain taken out of it first, which is where
        /// the bitrate saving comes from and is a different mechanism in each of the two modes that offer
        /// it. Under <see cref="GrainSynthMode.Encoder"/> it is the encoder's own denoise flag; under
        /// <see cref="GrainSynthMode.Table"/> it is this app's denoise pass, the same one
        /// <see cref="GrainSynthMode.Measured"/> runs, because no encoder will denoise for a table.
        /// <para/>
        /// **On by default, and it took a user asking "why would you ever want to add film grain on top
        /// of film grain" to notice it should be.** It defaulted off, on the argument that a table might
        /// be there to put grain onto a source that never had any, where denoising destroys picture for
        /// nothing. That case is real and it is the minority, and the majority case is the one this whole
        /// row exists for: AV1 grain synthesis saves bitrate *only* where the picture being coded has had
        /// its grain taken out, so the old default shipped the one shape of the feature that costs bitrate
        /// instead of saving it - coding the source's grain and then synthesising more over it. This
        /// file's own note on Table mode already called that out in those words while defaulting to it.
        /// <para/>
        /// A tick is not a decision taken away: somebody adding grain to clean footage unticks it and the
        /// readout says which of the two they are getting either way.
        /// </summary>
        public bool Denoise = true;

        /// <summary> How hard this app's own denoise pass runs, for the two modes that use it. See
        /// <see cref="GetDenoiseFilter"/> for what the number means - and note that a table measured at
        /// one strength describes grain that was removed at that strength, so reusing it wants the same
        /// number. </summary>
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

        /// <summary>
        /// What both encode tabs' rows offer: the modes an encoder carries out while it encodes, for one
        /// number or one table and no pass over the video. Two of the six are missing on purpose and the
        /// enum keeps all six because the Film Grain utility uses this same class to say where its grain
        /// comes from.
        /// <para/>
        /// **<see cref="GrainSynthMode.Preset"/> is here, and the rule it looked like it broke is the
        /// reason it fits.** A film stock preset was utility-only on the grounds that grav1synth writes it
        /// into a finished bitstream, which no encoder can be asked to do - and that is true of
        /// <c>grav1synth apply</c> and not of the preset. The preset itself is an ordinary grain table:
        /// <c>apply</c> onto a throwaway stub and <c>inspect</c> back off it yields one, the round trip
        /// costs a fraction of a second, and nothing about the result depends on the stub - tables read off
        /// a 320x240 clip and a 1920x1080 one are byte-identical bar the final segment's end tick, which
        /// this app extends itself. From there it is <see cref="Table"/> in every respect: the same
        /// delivery, the same refusals, the same encoder parameter. What the row does not do is what it
        /// never did - rewrite the output afterwards.
        /// <para/>
        /// <see cref="GrainSynthMode.PhotonNoise"/> stays out because it is genuinely not a fixed table:
        /// grav1synth synthesises it from the frame size and the transfer curve, so it is the utility's.
        /// <see cref="GrainSynthMode.Measured"/> is out for a different reason again: it *was* an encode
        /// mode on the AV1AN tab, made of a lossless denoise render and a grav1synth diff running in front
        /// of av1an - hours of single-threaded measuring before the parallel encode began, on every run of
        /// it. Measuring is a thing to do once per source, not once per encode, and the utility's Measure
        /// operation is where that belongs; its table then feeds <see cref="GrainSynthMode.Table"/> here at
        /// no cost. Quick Convert had already refused the mode outright, having no measuring pass, so this
        /// left one place it could be selected and one place it could not.
        /// </summary>
        public static readonly GrainSynthMode[] EncodeModes =
            { GrainSynthMode.Off, GrainSynthMode.Encoder, GrainSynthMode.Table, GrainSynthMode.Preset };

        /// <summary> Whether anything happens at all. Encoder mode at a strength of 0 is Off spelled
        /// differently - both encoders read 0 as "leave the source alone" - so it is reported as such
        /// rather than being carried around as a mode that does nothing. </summary>
        public bool Runs
        {
            get { return Mode != GrainSynthMode.Off && (Mode != GrainSynthMode.Encoder || Strength > 0); }
        }

        /// <summary> Whether this mode reaches the encoder as a grain *table* - measured, supplied, or
        /// read out of grav1synth's own presets - as against a strength the encoder analyses the source
        /// with. All three are the same argument to the encoder and are refused in the same places, which
        /// is why they answer one question rather than being tested for by name. </summary>
        public bool UsesTable
        {
            get
            {
                return Runs && (Mode == GrainSynthMode.Measured || Mode == GrainSynthMode.Table ||
                    Mode == GrainSynthMode.Preset);
            }
        }

        /// <summary> Whether the table has to be built before the encode rather than already existing on
        /// disk, which is exactly the preset: it is read out of grav1synth by a stub round trip, not
        /// picked in a file browser. Cheap - a 64x64 clip and two bitstream passes over it - but it can
        /// fail, so it happens where a failure can still stop the run. </summary>
        public bool NeedsPresetTable
        {
            get { return Runs && Mode == GrainSynthMode.Preset; }
        }

        /// <summary> Whether the picture handed to the encoder is denoised, which is the difference
        /// between saving bitrate and merely adding grain - see <see cref="GetNote"/>. </summary>
        public bool DenoisesSource
        {
            get
            {
                return Runs && (Mode == GrainSynthMode.Measured ||
                    ((Mode == GrainSynthMode.Encoder || Mode == GrainSynthMode.Table ||
                        Mode == GrainSynthMode.Preset) && Denoise));
            }
        }

        /// <summary> Whether this app has to denoise the picture itself, which is the half of
        /// <see cref="DenoisesSource"/> the encoder will not do - it reads its own denoise flag only on
        /// the strength path. It is one <c>hqdn3d</c> entry in a filter chain on **both** tabs now:
        /// Quick Convert's single command, and the per-chunk <c>-f</c> chain av1an runs. It was called
        /// <c>NeedsDenoisePass</c> while the AV1AN tab's only denoiser was a lossless whole-film render
        /// in front of av1an - that pass belonged to the measuring mode, which is the utility's now, and
        /// a table needs no denoised *file*, only denoised frames. </summary>
        public bool NeedsDenoiseFilter
        {
            get
            {
                return Runs && (Mode == GrainSynthMode.Measured ||
                    ((Mode == GrainSynthMode.Table || Mode == GrainSynthMode.Preset) && Denoise));
            }
        }

        /// <summary> Whether grav1synth has to measure a table, as against being handed one. </summary>
        public bool NeedsMeasurement
        {
            get { return Runs && Mode == GrainSynthMode.Measured; }
        }

        /// <summary> Whether grav1synth has to be present for this mode to run at all: the mode that
        /// measures a table, and the one that reads a built-in preset out of the tool. A table the user
        /// already has needs no tool, and neither does a strength. </summary>
        public bool NeedsGrav1synth()
        {
            return Runs && (Mode == GrainSynthMode.Measured || Mode == GrainSynthMode.Preset);
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

        /// <summary> Whether this mode is one the Film Grain utility owns rather than the encode row.
        /// Nothing on either row can select one; it is checked so that a config or a caller from
        /// elsewhere cannot smuggle one in. </summary>
        public bool IsUtilityOnly
        {
            get { return Mode == GrainSynthMode.PhotonNoise || Mode == GrainSynthMode.Measured; }
        }

        /// <summary> Why a utility-only mode is not an encode setting, worded for the mode - the two
        /// reasons are not the same, and telling somebody that measuring "writes grain into a finished
        /// file" would send them to the wrong operation. Both name the Film Grain utility, which is
        /// where each of them actually lives. </summary>
        public string DescribeUtilityOnly()
        {
            if (Mode == GrainSynthMode.Measured)
                return "Measuring a source's grain is a pass of its own - a lossless denoised copy of the whole " +
                    "video, then a single-threaded diff over every pixel of both, which is hours for a feature. " +
                    "It is worth doing once per source rather than once per encode, so it is the Film Grain " +
                    "utility's Measure operation on the Utilities tab. Point Grain table file at the table it " +
                    "writes, and the encode itself costs nothing extra.";

            return $"'{GetLabel(Mode)}' writes grain into a file that is already encoded, which is not something an " +
                $"encoder can be asked to do. Use the Film Grain utility on the Utilities tab.";
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
                    // "of 50" because the number means nothing without its scale: both encoders read
                    // it 0-50, and a 50 typed as "more grain" is also asking for the heaviest denoise
                    // the encoder has - which is what the clause below calls out where it applies.
                    parts.Add($"The encoder measures the source itself, at {Strength} of 50");
                    break;
                case GrainSynthMode.Measured:
                    parts.Add($"Denoised ({GetDenoiseFilter()}) and measured by grav1synth");
                    break;
                case GrainSynthMode.Table:
                    parts.Add($"Table '{Path.GetFileName(TablePath)}'" +
                        (Denoise ? $", source denoised ({GetDenoiseFilter()}) to match it" : ""));
                    break;
                case GrainSynthMode.Preset:
                    // Named as grav1synth's, because it is: the encoder is handed that tool's own table
                    // rather than analysing anything. "handed to the encoder" is the clause that separates
                    // this from the Film Grain utility's identically-named presets, which write the same
                    // grain into a file that has already been encoded.
                    parts.Add($"grav1synth's '{Preset}' film stock table, handed to the encoder" +
                        (Denoise ? $", source denoised ({GetDenoiseFilter()}) first" : ""));
                    break;
            }

            // The heavy-strength variant qualifies the coded-clean clause rather than replacing it:
            // it is still the saving mechanism at work, and what changes up here is only that the
            // denoise doing it stops being free - see HeavyDenoiseStrength.
            bool heavyDenoise = Mode == GrainSynthMode.Encoder && Denoise && Strength >= HeavyDenoiseStrength;

            parts.Add(!DenoisesSource
                ? "the source's own grain is coded too, so this adds grain rather than saving"
                : heavyDenoise
                    ? "the picture is coded clean - and the denoise runs at that strength too, which this high smooths real detail out with the grain"
                    : "the picture is coded clean, so the grain costs bytes instead of bitrate");

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
        /// <summary> Whether a denoised copy has to be rendered before av1an starts. </summary>
        public bool NeedsDenoiseFilter { get { return Config.NeedsDenoiseFilter; } }

        /// <summary> Whether that copy also has to be measured against the source. </summary>
        public bool NeedsMeasurement { get { return Config.NeedsMeasurement; } }
    }
}
