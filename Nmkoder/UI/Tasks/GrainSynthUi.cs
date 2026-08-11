using Avalonia.Controls;
using Nmkoder.Data;
using Nmkoder.Data.Streams;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Media;
using Nmkoder.Utils;
using Nmkoder.Views;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Nmkoder.UI.Tasks
{
    /// <summary>
    /// The Grain Synthesis row, which both encode tabs carry.
    /// <para/>
    /// One dropdown owns every way this app can put grain in an AV1 file, which is the point of it rather
    /// than a side effect - see <see cref="GrainSynthConfig"/> for why a row that was a spinner and a
    /// checkbox became a mode selector. Each mode brings its own control beside the box and nothing else
    /// is on screen, so the row is the same height whichever is picked.
    /// <para/>
    /// It lives in each tab's third column, with the resize, the borders, the deinterlacer and the tone
    /// map, because it has a readout - and a readout in a middle column draws over the column beside
    /// it. That is also where it belongs by subject: two of these modes denoise the picture before it is
    /// encoded, which is a filter on the video like the rest of that column, and the ones that do not are
    /// exactly the ones the readout has to warn about.
    /// <para/>
    /// **Both tabs drive the same standalone binaries now, and what still separates them is the
    /// pipeline, not the encoder.** The AV1AN tab has passes in front of av1an - which is what Measured
    /// from source is made of - where Quick Convert is a command chain with no measuring pass, so that
    /// one mode refuses there and points at where a table comes from. The two per-tab
    /// <c>GetTableFlag</c> overloads are the one statement of which codecs take a table and how each
    /// spells it, and everything downstream - which modes can run, what the readout says, what the
    /// encode refuses - follows from that single answer rather than from a second copy of this logic.
    /// </summary>
    class GrainSynthUi
    {
        private static MainWindow Form { get { return Program.MainWin; } }

        /// <summary> What both tabs open on, every session - neither saves anything. Off rather than
        /// Encoder: grain synthesis is a deliberate trade of grain accuracy against bitrate, and an
        /// encoder that quietly denoised every source would be making it for people. </summary>
        public const GrainSynthMode DefaultMode = GrainSynthMode.Off;

        public static void Init()
        {
            int mode = Array.IndexOf(GrainSynthConfig.EncodeModes, DefaultMode);
            Form.Av1anGrainModeBox.SetItems(GrainSynthConfig.EncodeModes.Select(m => (object)GrainSynthConfig.GetLabel(m)), mode);
            Form.EncGrainModeBox.SetItems(GrainSynthConfig.EncodeModes.Select(m => (object)GrainSynthConfig.GetLabel(m)), mode);
            ApplyControlVisibility();
        }

        /// <summary> The mode a box is asking for, floored the way every other index read on these tabs is
        /// - a box with nothing selected is the default rather than an exception. </summary>
        private static GrainSynthMode GetMode(ComboBox box)
        {
            return GrainSynthConfig.EncodeModes[box.SelectedIndex.Clamp(0, GrainSynthConfig.EncodeModes.Length - 1)];
        }

        /// <summary>
        /// What the row is asking for. Off for anything but an AV1 encoder whatever the box says: x264,
        /// x265 and vpxenc have no grain synthesis at all, the row is disabled for them, and a mode left
        /// selected behind a disabled control is the one way this could reach an encode nobody pointed it
        /// at - the same argument <see cref="ToneMapUi.ModeInEffect"/> makes for a hidden row.
        /// </summary>
        public static GrainSynthConfig GetAv1anConfig()
        {
            if (!IsRowRelevant(Av1anUi.GetCurrentCodecV()))
                return new GrainSynthConfig();

            // No table denoise and no denoise strength: this tab runs no denoise pass at all. What
            // Quick Convert does with one hqdn3d entry in a chain it already builds costs av1an a
            // lossless copy of the film in front of the encode, so the tick is Quick Convert's - see
            // GrainSynthConfig.EncodeModes.
            return Read(Form.Av1anGrainModeBox, Form.Av1anGrainSynthStrengthUpDown, Form.Av1anGrainSynthDenoiseBox,
                tableDenoise: null, denoiseStrength: null, tableBox: Form.Av1anGrainTableBox);
        }

        /// <summary>
        /// Quick Convert's setting. Same shape as the AV1AN tab's, and the same reason for the guard: the
        /// row is disabled for a codec that cannot synthesise grain, and a mode left selected behind a
        /// disabled control must not reach the command.
        /// </summary>
        public static GrainSynthConfig GetQuickConvertConfig()
        {
            if (!IsRowRelevant(QuickConvertUi.GetCurrentCodecV()))
                return new GrainSynthConfig();

            return Read(Form.EncGrainModeBox, Form.EncGrainSynthStrengthUpDown, Form.EncGrainSynthDenoiseBox,
                Form.EncGrainTableDenoiseBox, Form.EncGrainDenoiseStrengthUpDown, Form.EncGrainTableBox);
        }

        /// <summary> One row's controls, read into a config. <paramref name="tableDenoise"/> and
        /// <paramref name="denoiseStrength"/> are null on the AV1AN tab, which has neither control and
        /// so can never ask for the denoise pass those two describe. </summary>
        private static GrainSynthConfig Read(ComboBox modeBox, NumericUpDown strength, CheckBox encoderDenoise,
            CheckBox tableDenoise, NumericUpDown denoiseStrength, TextBox tableBox)
        {
            GrainSynthMode mode = GetMode(modeBox);

            return new GrainSynthConfig
            {
                Mode = mode,
                Strength = strength.Value.AsInt(),
                // Two controls, because they sit in two panels and mean two mechanisms - the encoder's own
                // denoise flag under Encoder, this app's denoise pass under Table. Read per mode so a tick
                // left in the panel that is off screen cannot reach the encode.
                Denoise = mode == GrainSynthMode.Table
                    ? tableDenoise != null && tableDenoise.IsChecked == true
                    : encoderDenoise.IsChecked == true,
                DenoiseStrength = denoiseStrength == null ? 4 : denoiseStrength.Value.AsInt(),
                TablePath = (tableBox.Text ?? "").Trim(),
            };
        }

        /// <summary> Only the two AV1 encoders have anything to synthesise grain with. </summary>
        public static bool IsRowRelevant(CodecUtils.Av1anCodec codec)
        {
            return codec == CodecUtils.Av1anCodec.SvtAv1 || codec == CodecUtils.Av1anCodec.AomAv1;
        }

        /// <summary> The same question on Quick Convert, whose AV1 encoders are the same standalone
        /// binaries now - the Direct* pair, not ffmpeg's libraries. A stream copy falls out of this too,
        /// being neither of them - and a copy builds no encoder arguments at all. </summary>
        public static bool IsRowRelevant(CodecUtils.VideoCodec codec)
        {
            return codec == CodecUtils.VideoCodec.DirectSvtAv1 || codec == CodecUtils.VideoCodec.DirectAomAv1;
        }

        /// <summary>
        /// Which control sits beside the dropdown, and whether the row is live at all. Everything is
        /// hidden rather than disabled, because each mode's control is meaningless in the others - a
        /// greyed-out ISO box beside a grain table would be describing a setting that is not being
        /// ignored so much as not asked for.
        /// </summary>
        public static void ApplyControlVisibility()
        {
            // The AV1AN row has no denoise-pass controls at all, so it passes nulls where Quick Convert
            // passes the tick and the strength beside it.
            Apply(IsRowRelevant(Av1anUi.GetCurrentCodecV()), Form.Av1anGrainModeBox, Form.Av1anGrainEncoderPanel,
                measuredPanel: null, Form.Av1anGrainTablePanel,
                tableDenoise: null, Form.Av1anGrainSynthDenoiseBox, Form.Av1anGrainSynthStrengthUpDown);

            Apply(IsRowRelevant(QuickConvertUi.GetCurrentCodecV()), Form.EncGrainModeBox, Form.EncGrainEncoderPanel,
                Form.EncGrainMeasuredPanel, Form.EncGrainTablePanel,
                Form.EncGrainTableDenoiseBox, Form.EncGrainSynthDenoiseBox, Form.EncGrainSynthStrengthUpDown);

            RefreshInfo();
        }

        private static void Apply(bool relevant, ComboBox modeBox, StackPanel encoderPanel, StackPanel measuredPanel,
            StackPanel tablePanel, CheckBox tableDenoise, CheckBox encoderDenoise, NumericUpDown strength)
        {
            GrainSynthMode mode = relevant ? GetMode(modeBox) : GrainSynthMode.Off;

            modeBox.IsEnabled = relevant;
            encoderPanel.IsVisible = mode == GrainSynthMode.Encoder;

            // The strength belongs to the denoise pass, which is Quick Convert's alone and runs there
            // only for a table with the tick set. It used to accompany Measured too, and carried a
            // "Denoise" label of its own for that mode, where under Table the tickbox immediately to
            // its left already says the word - with Measured gone the label had nothing left to name.
            if (measuredPanel != null)
                measuredPanel.IsVisible = mode == GrainSynthMode.Table && tableDenoise.IsChecked == true;

            tablePanel.IsVisible = mode == GrainSynthMode.Table;

            // The Denoise box follows the strength beside it as well as the encoder: both AV1 encoders
            // read their denoise flag only where they are synthesising grain at all - aomenc's
            // --enable-dnl-denoising applies "when denoise-noise-level is enabled", and SVT-AV1 answers
            // one set against --film-grain 0 with "ignored when film grain is off". At a strength of 0 it
            // was a tickable box that did nothing. What it is ticked to is left alone rather than cleared,
            // so a strength dropped to 0 and put back brings the choice back with it.
            encoderDenoise.IsEnabled = strength.Value.AsInt() > 0;
        }

        /// <summary>
        /// How the grain would reach the output as things stand, worked out without asking a binary
        /// anything - which is what the readout can afford and the encode cannot.
        /// <para/>
        /// It assumes an SVT-AV1 that has <c>--fgs-table</c>, which the bundled build does and mainline
        /// does not. <see cref="ResolveDeliveryAsync"/> is the one that actually asks, before the encode,
        /// and where the two disagree it is the encode's answer that is logged. Nothing in the readout's
        /// most important clause depends on this - whether the picture is coded clean is a property of the
        /// mode, not of how the description travels.
        /// </summary>
        public static GrainDelivery GetLikelyDelivery(GrainSynthConfig config, CodecUtils.Av1anCodec codec)
        {
            if (!config.Runs || !IsRowRelevant(codec))
                return GrainDelivery.None;

            return config.Mode == GrainSynthMode.Encoder ? GrainDelivery.EncoderAnalysis : GrainDelivery.EncoderTable;
        }

        /// <summary>
        /// The same, for Quick Convert - the same guess for the same reason now that this tab drives the
        /// standalone binaries too: it assumes the bundled SVT-AV1, and whether the one actually on the
        /// machine is mainline is the encode's question. What stays knowable here is the structure -
        /// a codec with no table flag at all delivers nothing, whatever binary is behind it.
        /// </summary>
        public static GrainDelivery GetLikelyDelivery(GrainSynthConfig config, CodecUtils.VideoCodec codec)
        {
            if (!config.Runs || !IsRowRelevant(codec))
                return GrainDelivery.None;

            if (config.Mode == GrainSynthMode.Encoder)
                return GrainDelivery.EncoderAnalysis;

            return GetTableFlag(codec).IsNotEmpty() ? GrainDelivery.EncoderTable : GrainDelivery.None;
        }

        /// <summary>
        /// What the encoder calls its "apply this grain table" parameter, or "" where it has none.
        /// <para/>
        /// Both AV1 encoders av1an drives have one and they are spelled differently, which is the only
        /// reason this exists. aomenc's was measured against 3.8.2 rather than assumed: a table passed
        /// through <c>--film-grain-table</c> comes back out of the encode intact, and beats
        /// <c>--denoise-noise-level</c> where both are set. SVT-AV1's is PSY-line only, which is what the
        /// help check below is for - mainline has no such parameter and refuses the whole command over it.
        /// </summary>
        private static string GetTableFlag(CodecUtils.Av1anCodec codec)
        {
            switch (codec)
            {
                case CodecUtils.Av1anCodec.SvtAv1: return "--fgs-table";
                case CodecUtils.Av1anCodec.AomAv1: return "--film-grain-table";
                default: return "";
            }
        }

        /// <summary>
        /// The same for Quick Convert's Direct* pair, which are the very binaries the map above names -
        /// the tab pipes frames into <c>SvtAv1EncApp</c> and <c>aomenc</c> now, so the flags carry over
        /// rather than being re-measured. While the tab drove ffmpeg's libraries this returned "" for
        /// both: <c>fgs-table</c> is PSY-line only and libsvtav1 is mainline, and whether
        /// <c>film-grain-table</c> survived <c>-aom-params</c> was never measured. Neither limit
        /// applies to a binary this app launches itself, and SVT's remaining uncertainty - the binary
        /// on the machine may still be mainline - is the encode-time question
        /// <see cref="GetProblemAsync(GrainSynthConfig, CodecUtils.VideoCodec)"/> asks of its help text.
        /// </summary>
        private static string GetTableFlag(CodecUtils.VideoCodec codec)
        {
            switch (codec)
            {
                case CodecUtils.VideoCodec.DirectSvtAv1: return "--fgs-table";
                case CodecUtils.VideoCodec.DirectAomAv1: return "--film-grain-table";
                default: return "";
            }
        }

        /// <summary>
        /// The delivery the encode will really use, which needs the encoder binary's own answer about
        /// <c>--fgs-table</c>.
        /// <para/>
        /// There are two answers, not three: this row is what the encoder does while it encodes. Whether the
        /// encoder can take the table at all is <see cref="GetTableDeliveryProblem"/>'s question, and a "no"
        /// there stops the encode rather than routing round it.
        /// </summary>
        public static async Task<GrainDelivery> ResolveDeliveryAsync(GrainSynthConfig config, CodecUtils.Av1anCodec codec, string tablePath)
        {
            if (!config.Runs || !IsRowRelevant(codec))
                return GrainDelivery.None;

            if (config.Mode == GrainSynthMode.Encoder)
                return GrainDelivery.EncoderAnalysis;

            return GrainDelivery.EncoderTable;
        }

        /// <summary>
        /// Whether the encoder in front of us will take the table, and why not when it will not.
        /// <para/>
        /// This used to have somewhere to fall back to and now does not, which is the point: a table the
        /// encoder cannot be handed is a refusal, naming the Film Grain utility as the way to put that
        /// grain into the output afterwards. Quietly rewriting the finished file instead would be this row
        /// doing the utility's job without saying so.
        /// </summary>
        private static async Task<string> GetTableDeliveryProblem(GrainSynthConfig config, CodecUtils.Av1anCodec codec, string tablePath)
        {
            string flag = GetTableFlag(codec);

            if (flag.IsEmpty())
                return $"{CodecUtils.GetCodec(codec).FriendlyName} cannot be handed a grain table.";

            if (HasSpace(tablePath))
                return "the grain table's path contains a space, and everything this app sends an av1an-driven " +
                    "encoder goes inside one quoted string that av1an splits again on the way to the binary - a " +
                    "value with a space in it does not survive that split. Move the table somewhere without one";

            if (!await AvProcess.EncoderKnowsFlagOrIsUnknown(codec == CodecUtils.Av1anCodec.SvtAv1 ? "svt-av1" : "aom", flag))
                return $"this SVT-AV1 build has no {flag} - it is a parameter of the PSY line (svt-av1-hdr), which " +
                    $"is what this project bundles, and not of mainline SVT-AV1";

            return "";
        }

        /// <summary>
        /// The structural half of the same question on Quick Convert: what can be known without asking a
        /// binary anything, which is what the readout can afford - the encode's own check,
        /// <see cref="GetProblemAsync(GrainSynthConfig, CodecUtils.VideoCodec)"/>, asks the binary too.
        /// What is structural is the codec: one with no table parameter cannot take a table however it is
        /// spelled.
        /// <para/>
        /// The AV1AN overload's space check does not carry over in the other direction either: these
        /// encoders are launched by this app with the path as one <c>Shell.WrapArg</c> argument, which
        /// survives a space. It is av1an's re-splitting of one quoted string that cannot.
        /// </summary>
        private static string GetQuickConvertModeProblem(GrainSynthConfig config, CodecUtils.VideoCodec codec)
        {
            if (!config.Runs)
                return "";

            if (config.UsesTable && GetTableFlag(codec).IsEmpty())
                return DescribeUndeliverableTable($"{CodecUtils.GetCodec(codec).FriendlyName} has no parameter for " +
                    $"applying a grain table");

            return "";
        }

        /// <summary>
        /// Whether a path cannot be written into av1an's encoder arguments.
        /// <para/>
        /// Everything this app sends an av1an-driven encoder goes inside one <c>-v "…"</c> string that
        /// av1an splits again on its way to the binary, and a value with a space in it does not survive
        /// that split - the same limit the Advanced grid has always had, which is why its rows are one
        /// space-separated <c>key=value</c> list. A table is the first setting here whose value is a path,
        /// so it is the first that meets it in ordinary use: "C:\My Encodes\grain.tbl" is not an odd thing
        /// to have. Rather than quote through a layer that cannot be checked from here, such a table is
        /// written into the finished encode by grav1synth instead, which takes the path as one argument
        /// of its own and needs no splitting at all.
        /// </summary>
        private static bool HasSpace(string path)
        {
            return (path ?? "").Contains(' ');
        }

        /// <summary>
        /// Why this cannot run, or "" when it can. Asked before the encode starts, because every
        /// alternative is worse: a mainline SVT-AV1 handed <c>--fgs-table</c> rejects the whole command
        /// once per chunk, and a missing grav1synth is a "command not found" written to a stream nothing
        /// reads, hours in and after the encode has already finished.
        /// </summary>
        public static async Task<string> GetProblemAsync(GrainSynthConfig config, GrainDelivery delivery, CodecUtils.Av1anCodec codec, string tablePath)
        {
            if (!config.Runs)
                return "";

            string problem = config.GetProblem();

            if (problem.IsNotEmpty())
                return problem;

            // Nothing on the row can select a utility-only mode, so this is a guard against one arriving
            // from somewhere else rather than something a user can see.
            if (config.IsUtilityOnly)
                return config.DescribeUtilityOnly();

            if (config.NeedsGrav1synth() && !Grav1synth.IsAvailable())
                return Grav1synth.DescribeMissing();

            if (!config.UsesTable)
                return "";

            string cannot = await GetTableDeliveryProblem(config, codec, tablePath);

            return cannot.IsEmpty() ? "" : DescribeUndeliverableTable(cannot);
        }

        /// <summary>
        /// The same for Quick Convert, and genuinely async now: the tab drives the standalone binaries,
        /// so a table mode has to ask the binary in front of it about the flag exactly as the AV1AN
        /// overload does - the bundled SVT-AV1 has <c>--fgs-table</c> and a user's own may be mainline,
        /// which refuses the whole command over it. The Measured refusal comes first, being structural:
        /// this tab has no measuring pass, however capable the binary -
        /// <see cref="GetQuickConvertModeProblem"/> says where to get the table instead. grav1synth is
        /// not asked after: the one mode that needs it is that refused one.
        /// </summary>
        public static async Task<string> GetProblemAsync(GrainSynthConfig config, CodecUtils.VideoCodec codec)
        {
            if (!config.Runs)
                return "";

            string problem = config.GetProblem();

            if (problem.IsNotEmpty())
                return problem;

            if (config.IsUtilityOnly)
                return config.DescribeUtilityOnly();

            string modeProblem = GetQuickConvertModeProblem(config, codec);

            if (modeProblem.IsNotEmpty())
                return modeProblem;

            if (!config.UsesTable)
                return "";

            string encoderName = codec == CodecUtils.VideoCodec.DirectSvtAv1 ? "svt-av1" : "aom";

            if (!await AvProcess.EncoderKnowsFlagOrIsUnknown(encoderName, GetTableFlag(codec)))
                return DescribeUndeliverableTable($"this SVT-AV1 build has no {GetTableFlag(codec)} - it is a " +
                    $"parameter of the PSY line (svt-av1-hdr), which is what this project bundles, and not of " +
                    $"mainline SVT-AV1");

            return "";
        }

        /// <summary> The refusal both tabs give for a table the encoder in front of them will not take.
        /// It names the Film Grain utility, which produces the same grain in the same output by rewriting
        /// the finished file - the step this row deliberately does not take for itself. </summary>
        private static string DescribeUndeliverableTable(string cannot)
        {
            return $"This encode cannot be given the grain table, because {cannot}\n\nEncode without grain " +
                $"synthesis, then put the table into the finished file with the Film Grain utility on the " +
                $"Utilities tab - that is the same grain in the same output, applied afterwards instead of by " +
                $"the encoder. Encoder analysis is the other way round: it needs no table and works on every " +
                $"AV1 build.";
        }

        /// <summary> Brings both tabs' readouts up to date. Touches nothing that blocks, so it is safe
        /// from any handler. </summary>
        public static void RefreshInfo()
        {
            try
            {
                GrainSynthConfig av1an = GetAv1anConfig();
                Form.Av1anGrainInfoLabel.Text = Describe(av1an, GetLikelyDelivery(av1an, Av1anUi.GetCurrentCodecV()),
                    TrackList.current?.File, "");

                // Quick Convert knows one thing the AV1AN tab cannot know until the encode starts:
                // whether the codec in front of it takes a table at all. That is the difference between
                // a mode that will work and one the run is going to refuse, so the readout says it
                // rather than describing an encode that is not going to happen. What is not asked here
                // is the binary: that costs a process launch, so whether this SVT-AV1 is mainline stays
                // the encode's question, exactly as it is on the AV1AN tab.
                CodecUtils.VideoCodec encCodec = QuickConvertUi.GetCurrentCodecV();
                GrainSynthConfig enc = GetQuickConvertConfig();
                string undeliverable = GetQuickConvertModeProblem(enc, encCodec);

                Form.EncGrainInfoLabel.Text = Describe(enc, GetLikelyDelivery(enc, encCodec),
                    DeinterlaceUi.GetQuickConvertSourceFile(), undeliverable);
            }
            catch (Exception e)
            {
                Logger.Log($"Failed to describe the grain synthesis setting: {e.Message}", true);
            }
        }

        /// <summary> One row's readout: why it cannot run, or what it is going to do and what that costs. </summary>
        private static string Describe(GrainSynthConfig config, GrainDelivery delivery, MediaFile file, string undeliverable)
        {
            // A mode that cannot run says so here instead of describing what it would have done. The
            // shape this is for is Grain table file with no file picked yet, which is where every user
            // of that mode starts: without this the row read "Table '' · the source's own grain is
            // coded too", which describes an encode that the run would refuse a moment later.
            string problem = config.GetProblem();

            if (problem.IsNotEmpty())
                return problem;

            // The message carries its own way out - Encoder analysis, the utility, or where to measure
            // a table - so nothing is appended to it here.
            if (undeliverable.IsNotEmpty())
                return undeliverable;

            // No cost clause any more: every mode this row offers costs the encode nothing beyond the
            // encode. What used to be here was the Measured estimate - grav1synth's diff runs at about
            // 7.2 megapixels a second, single-threaded, so a 1080p feature was a working day of it -
            // and that number now belongs where the measuring does, on the Film Grain utility's dialog.
            return config.GetNote(delivery);
        }

        /// <summary>
        /// The plan as the row stands, for anything asking outside a run - the command preview, mostly.
        /// It uses <see cref="GetLikelyDelivery"/>, so it can be wrong about a mainline SVT-AV1 in exactly
        /// one way: it shows <c>--fgs-table</c> on a binary that has none. The encode asks the binary.
        /// </summary>
        public static GrainPlan GetPreviewPlan()
        {
            GrainSynthConfig config = GetAv1anConfig();

            return new GrainPlan
            {
                Config = config,
                Delivery = GetLikelyDelivery(config, Av1anUi.GetCurrentCodecV()),
                TablePath = config.Mode == GrainSynthMode.Table ? config.TablePath : "",
            };
        }

        /// <summary>
        /// Quick Convert's plan, settled for a run. It needs no async counterpart the way the AV1AN tab's
        /// does: nothing here has to ask a binary, so what the readout showed and what the encode uses are
        /// the same answer worked out the same way.
        /// </summary>
        public static GrainPlan GetQuickConvertPlan()
        {
            GrainSynthConfig config = GetQuickConvertConfig();

            return new GrainPlan
            {
                Config = config,
                Delivery = GetLikelyDelivery(config, QuickConvertUi.GetCurrentCodecV()),
                TablePath = config.Mode == GrainSynthMode.Table ? config.TablePath : "",
            };
        }

        /// <summary> The table file picker, off each row's own browse button. </summary>
        public static async Task PickTableAsync(bool av1anTab)
        {
            string[] paths = await Pickers.PickFiles(Form, "Pick a film grain table", allowMultiple: false);

            if (paths == null || paths.Length < 1 || paths[0].IsEmpty())
                return;

            if (av1anTab)
                Form.Av1anGrainTableBox.Text = paths[0];
            else
                Form.EncGrainTableBox.Text = paths[0];

            RefreshInfo();
        }
    }
}
