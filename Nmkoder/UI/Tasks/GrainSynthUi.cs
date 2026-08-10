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
    /// **The two tabs drive different binaries and that is the whole of the difference between them.** The
    /// AV1AN tab drives standalone encoders, where SVT-AV1 is the svt-av1-hdr build this project bundles
    /// and has <c>--fgs-table</c>; Quick Convert drives the libraries compiled into ffmpeg, where SVT-AV1
    /// is mainline and has no such parameter. <see cref="GetTableFlag(CodecUtils.VideoCodec)"/> is the one
    /// statement of that, and everything downstream of it - which modes can run, what the readout says,
    /// what the encode refuses - follows from that single answer rather than from a second copy of this
    /// logic.
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

            return Read(Form.Av1anGrainModeBox, Form.Av1anGrainSynthStrengthUpDown, Form.Av1anGrainSynthDenoiseBox,
                Form.Av1anGrainTableDenoiseBox, Form.Av1anGrainDenoiseStrengthUpDown, Form.Av1anGrainTableBox);
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

        /// <summary> One row's controls, read into a config. </summary>
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
                    ? tableDenoise.IsChecked == true
                    : encoderDenoise.IsChecked == true,
                DenoiseStrength = denoiseStrength.Value.AsInt(),
                TablePath = (tableBox.Text ?? "").Trim(),
            };
        }

        /// <summary> Only the two AV1 encoders have anything to synthesise grain with. </summary>
        public static bool IsRowRelevant(CodecUtils.Av1anCodec codec)
        {
            return codec == CodecUtils.Av1anCodec.SvtAv1 || codec == CodecUtils.Av1anCodec.AomAv1;
        }

        /// <summary> The same question on Quick Convert, whose AV1 encoders are the libraries inside
        /// ffmpeg rather than the standalone binaries. A stream copy falls out of this too, being neither
        /// of them - and a copy builds no encoder arguments at all. </summary>
        public static bool IsRowRelevant(CodecUtils.VideoCodec codec)
        {
            return codec == CodecUtils.VideoCodec.LibSvtAv1 || codec == CodecUtils.VideoCodec.LibAomAv1;
        }

        /// <summary>
        /// Which control sits beside the dropdown, and whether the row is live at all. Everything is
        /// hidden rather than disabled, because each mode's control is meaningless in the others - a
        /// greyed-out ISO box beside a grain table would be describing a setting that is not being
        /// ignored so much as not asked for.
        /// </summary>
        public static void ApplyControlVisibility()
        {
            Apply(IsRowRelevant(Av1anUi.GetCurrentCodecV()), Form.Av1anGrainModeBox, Form.Av1anGrainEncoderPanel,
                Form.Av1anGrainMeasuredPanel, Form.Av1anGrainDenoiseLabel, Form.Av1anGrainTablePanel,
                Form.Av1anGrainTableDenoiseBox, Form.Av1anGrainSynthDenoiseBox, Form.Av1anGrainSynthStrengthUpDown);

            Apply(IsRowRelevant(QuickConvertUi.GetCurrentCodecV()), Form.EncGrainModeBox, Form.EncGrainEncoderPanel,
                Form.EncGrainMeasuredPanel, Form.EncGrainDenoiseLabel, Form.EncGrainTablePanel,
                Form.EncGrainTableDenoiseBox, Form.EncGrainSynthDenoiseBox, Form.EncGrainSynthStrengthUpDown);

            RefreshInfo();
        }

        private static void Apply(bool relevant, ComboBox modeBox, StackPanel encoderPanel, StackPanel measuredPanel,
            TextBlock denoiseLabel, StackPanel tablePanel, CheckBox tableDenoise, CheckBox encoderDenoise, NumericUpDown strength)
        {
            GrainSynthMode mode = relevant ? GetMode(modeBox) : GrainSynthMode.Off;

            modeBox.IsEnabled = relevant;
            encoderPanel.IsVisible = mode == GrainSynthMode.Encoder;
            // The strength belongs to the pass, and the pass runs for Measured always and for Table on
            // request - so it follows the pass rather than the mode.
            measuredPanel.IsVisible = mode == GrainSynthMode.Measured ||
                (mode == GrainSynthMode.Table && tableDenoise.IsChecked == true);

            // The strength names itself in Measured, where nothing else on the row says what it is. Under
            // Table the tickbox immediately to its left is already labelled Denoise, and two of the word
            // in a row reads as two settings.
            denoiseLabel.IsVisible = mode == GrainSynthMode.Measured;
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
        /// The same, for Quick Convert - and here the answer is not a guess at all, which is the one place
        /// the two tabs genuinely differ. Whether an av1an-driven binary has <c>--fgs-table</c> depends on
        /// which SVT-AV1 is on the machine, so the AV1AN version above assumes the bundled one and lets the
        /// encode ask; whether the library compiled into ffmpeg has it is settled by that build and stated
        /// once in <see cref="GetTableFlag(CodecUtils.VideoCodec)"/>. So a table that cannot be delivered
        /// is known here, before the encode and while the readout is being drawn.
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
        /// The same for the libraries inside ffmpeg, which is **"" for both of them** - so Quick Convert
        /// can analyse the source but cannot be handed a table, and the two table modes refuse there.
        /// <para/>
        /// The two are absent for different reasons and only one of them could change. libsvtav1 is
        /// *mainline* SVT-AV1 - BtbN's ffmpeg pins <c>AOMediaCodec/SVT-AV1</c>, not the PSY fork av1an gets
        /// - and <c>fgs-table</c> is one of the parameters this project has measured absent from it, which
        /// is why <c>LibSvtAv1.json</c> has no row for it either. libaom's <c>film-grain-table</c> is a
        /// different case: aomenc takes it, and whether it survives ffmpeg's <c>-aom-params</c> to reach
        /// the library **has not been measured**, so it is not claimed. This file's rule is the project's:
        /// a parameter is shipped once it has been passed to the real binary and seen to be accepted, and
        /// libaom is the one encoder here that refuses the whole encode over a parameter it does not know.
        /// <para/>
        /// Returning the flag here is the entire change needed to light the two table modes up on this tab
        /// once that has been measured - <see cref="GetLikelyDelivery(GrainSynthConfig, CodecUtils.VideoCodec)"/>,
        /// the readout, the refusal and <c>LibAomAv1.GetArgs</c> all read this one answer.
        /// </summary>
        private static string GetTableFlag(CodecUtils.VideoCodec codec)
        {
            return "";
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
        /// The same on Quick Convert, where it is a fact about the ffmpeg build rather than a question for
        /// a binary - so it needs no await and can be asked while the readout is drawn.
        /// <para/>
        /// The space check does not carry over: an ffmpeg argument goes through <c>Shell.WrapArg</c> and
        /// survives a space perfectly well. It is av1an's re-splitting of one quoted string that cannot.
        /// </summary>
        private static string GetTableDeliveryProblem(CodecUtils.VideoCodec codec)
        {
            if (GetTableFlag(codec).IsNotEmpty())
                return "";

            return $"{CodecUtils.GetCodec(codec).FriendlyName} on this tab is the library compiled into FFmpeg, " +
                $"which has no parameter for applying a grain table" +
                (codec == CodecUtils.VideoCodec.LibSvtAv1
                    ? " - fgs-table belongs to the svt-av1-hdr build the AV1AN tab drives, not to the mainline " +
                        "SVT-AV1 inside FFmpeg"
                    : "") + ".";
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
                return $"'{GrainSynthConfig.GetLabel(config.Mode)}' writes grain into a finished file, which is the " +
                    $"Film Grain utility's job rather than an encode setting. Use the Utilities tab.";

            if (config.NeedsGrav1synth() && !Grav1synth.IsAvailable())
                return Grav1synth.DescribeMissing();

            if (!config.UsesTable)
                return "";

            string cannot = await GetTableDeliveryProblem(config, codec, tablePath);

            return cannot.IsEmpty() ? "" : DescribeUndeliverableTable(cannot);
        }

        /// <summary>
        /// The same for Quick Convert. It awaits nothing - every question this tab has to ask is answered
        /// by the build rather than by a binary - but it is kept async so both tabs' runs read alike, and
        /// so a future measured <c>film-grain-table</c> can put a probe back here without moving callers.
        /// </summary>
        public static Task<string> GetProblemAsync(GrainSynthConfig config, CodecUtils.VideoCodec codec)
        {
            if (!config.Runs)
                return Task.FromResult("");

            string problem = config.GetProblem();

            if (problem.IsNotEmpty())
                return Task.FromResult(problem);

            if (config.IsUtilityOnly)
                return Task.FromResult($"'{GrainSynthConfig.GetLabel(config.Mode)}' writes grain into a finished file, " +
                    $"which is the Film Grain utility's job rather than an encode setting. Use the Utilities tab.");

            if (config.NeedsGrav1synth() && !Grav1synth.IsAvailable())
                return Task.FromResult(Grav1synth.DescribeMissing());

            if (!config.UsesTable)
                return Task.FromResult("");

            string cannot = GetTableDeliveryProblem(codec);

            return Task.FromResult(cannot.IsEmpty() ? "" : DescribeUndeliverableTable(cannot));
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

                // Quick Convert knows one thing the AV1AN tab cannot know until the encode starts: whether
                // its encoder can be handed a table at all. That is the difference between a mode that will
                // work and one the run is going to refuse, so the readout says it rather than describing an
                // encode that is not going to happen.
                CodecUtils.VideoCodec encCodec = QuickConvertUi.GetCurrentCodecV();
                GrainSynthConfig enc = GetQuickConvertConfig();
                string undeliverable = enc.UsesTable ? GetTableDeliveryProblem(encCodec) : "";

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

            if (undeliverable.IsNotEmpty())
                return $"{undeliverable} Use Encoder analysis, or apply the table afterwards with the Film Grain utility.";

            string note = config.GetNote(delivery);
            string cost = DescribeCost(config, file);

            return cost.IsEmpty() ? note : $"{note} · {cost}";
        }

        /// <summary>
        /// What the Measured mode is about to spend, for the file in front of the user, and only for that
        /// mode - it is the only one with a cost worth a sentence.
        /// <para/>
        /// It is the same argument the Deinterlace row's QTGMC tooltip makes and it is a good deal
        /// sharper here: grav1synth's diff runs at about 7.2 megapixels a second, single-threaded, so a
        /// feature film at 1080p is a working day of measuring before av1an is started. Somebody who can
        /// see that number before they press Run will trim the source, or pick Encoder analysis, or decide
        /// it is worth it - and any of those three is better than finding out at hour two.
        /// </summary>
        private static string DescribeCost(GrainSynthConfig config, MediaFile file)
        {
            if (config.Mode != GrainSynthMode.Measured || !config.Runs)
                return "";

            VideoStream v = file?.VideoStreams.FirstOrDefault();

            if (v == null || file.DurationMs < 1 || v.Rate.GetFloat() <= 0)
                return "costs a lossless intermediate and a full extra pass";

            long frames = (long)(file.DurationMs / 1000d * v.Rate.GetFloat());
            TimeSpan estimate = Grav1synth.EstimateDiffTime(frames, v.Resolution);

            return $"~{FormatUtils.Time(estimate, allowMs: false)} to measure, plus a lossless intermediate";
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
