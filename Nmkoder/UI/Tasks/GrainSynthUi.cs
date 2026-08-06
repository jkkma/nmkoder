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
    /// The AV1AN Video tab's Grain Synthesis row.
    /// <para/>
    /// One dropdown owns every way this app can put grain in an AV1 file, which is the point of it rather
    /// than a side effect - see <see cref="GrainSynthConfig"/> for why a row that was a spinner and a
    /// checkbox became a mode selector. Each mode brings its own control beside the box and nothing else
    /// is on screen, so the row is the same height whichever is picked.
    /// <para/>
    /// It lives in the tab's third column, with the resize, the borders, the deinterlacer and the tone
    /// map, because it now has a readout - and a readout in a middle column draws over the column beside
    /// it. That is also where it belongs by subject: two of these modes denoise the picture before it is
    /// encoded, which is a filter on the video like the rest of that column, and the three that do not are
    /// exactly the ones the readout has to warn about.
    /// </summary>
    class GrainSynthUi
    {
        private static MainWindow Form { get { return Program.MainWin; } }

        /// <summary> What the tab opens on, every session - nothing on this tab is saved. Off rather than
        /// Encoder: grain synthesis is a deliberate trade of grain accuracy against bitrate, and an
        /// encoder that quietly denoised every source would be making it for people. </summary>
        public const GrainSynthMode DefaultMode = GrainSynthMode.Off;

        public static void Init()
        {
            Form.Av1anGrainModeBox.SetItems(GrainSynthConfig.AllModes.Select(m => (object)GrainSynthConfig.GetLabel(m)),
                Array.IndexOf(GrainSynthConfig.AllModes, DefaultMode));
            FillPresetBox();
            ApplyControlVisibility();

            // The binary's own list, which grew between its last crates.io release and the source this
            // bundles from. Fire-and-forget: the fallback list is already in the box, and a name that is
            // merely missing from an older build would be refused by it with the whole command.
            _ = Task.Run(async () =>
            {
                await Grav1synth.LoadPresetsAsync();
                Avalonia.Threading.Dispatcher.UIThread.Post(FillPresetBox);
            });
        }

        private static void FillPresetBox()
        {
            string picked = Form.Av1anGrainPresetBox.GetText();
            int index = Math.Max(0, Array.IndexOf(GrainSynthConfig.Presets, picked));
            Form.Av1anGrainPresetBox.SetItems(GrainSynthConfig.Presets.Select(p => (object)p), index);
        }

        /// <summary> The mode the box is asking for, floored the way every other index read on this tab is
        /// - a box with nothing selected is the default rather than an exception. </summary>
        private static GrainSynthMode GetMode()
        {
            return GrainSynthConfig.AllModes[Form.Av1anGrainModeBox.SelectedIndex.Clamp(0, GrainSynthConfig.AllModes.Length - 1)];
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

            return new GrainSynthConfig
            {
                Mode = GetMode(),
                Strength = Form.Av1anGrainSynthStrengthUpDown.Value.AsInt(),
                Denoise = Form.Av1anGrainSynthDenoiseBox.IsChecked == true,
                DenoiseStrength = Form.Av1anGrainDenoiseStrengthUpDown.Value.AsInt(),
                TablePath = (Form.Av1anGrainTableBox.Text ?? "").Trim(),
                Preset = Form.Av1anGrainPresetBox.GetText(),
                Iso = Form.Av1anGrainIsoUpDown.Value.AsInt(),
                Chroma = Form.Av1anGrainChromaBox.IsChecked == true,
            };
        }

        /// <summary> Only the two AV1 encoders have anything to synthesise grain with. </summary>
        public static bool IsRowRelevant(CodecUtils.Av1anCodec codec)
        {
            return codec == CodecUtils.Av1anCodec.SvtAv1 || codec == CodecUtils.Av1anCodec.AomAv1;
        }

        /// <summary>
        /// Which control sits beside the dropdown, and whether the row is live at all. Everything is
        /// hidden rather than disabled, because each mode's control is meaningless in the others - a
        /// greyed-out ISO box beside a grain table would be describing a setting that is not being
        /// ignored so much as not asked for.
        /// </summary>
        public static void ApplyControlVisibility()
        {
            bool relevant = IsRowRelevant(Av1anUi.GetCurrentCodecV());
            GrainSynthMode mode = relevant ? GetMode() : GrainSynthMode.Off;

            Form.Av1anGrainModeBox.IsEnabled = relevant;
            Form.Av1anGrainEncoderPanel.IsVisible = mode == GrainSynthMode.Encoder;
            Form.Av1anGrainMeasuredPanel.IsVisible = mode == GrainSynthMode.Measured;
            Form.Av1anGrainTablePanel.IsVisible = mode == GrainSynthMode.Table;
            Form.Av1anGrainPresetPanel.IsVisible = mode == GrainSynthMode.Preset;
            Form.Av1anGrainIsoPanel.IsVisible = mode == GrainSynthMode.PhotonNoise;

            // The Denoise box follows the strength beside it as well as the encoder: both AV1 encoders
            // read their denoise flag only where they are synthesising grain at all - aomenc's
            // --enable-dnl-denoising applies "when denoise-noise-level is enabled", and SVT-AV1 answers
            // one set against --film-grain 0 with "ignored when film grain is off". At a strength of 0 it
            // was a tickable box that did nothing. What it is ticked to is left alone rather than cleared,
            // so a strength dropped to 0 and put back brings the choice back with it.
            Form.Av1anGrainSynthDenoiseBox.IsEnabled = Form.Av1anGrainSynthStrengthUpDown.Value.AsInt() > 0;

            RefreshInfo();
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

            if (config.Mode == GrainSynthMode.Encoder)
                return GrainDelivery.EncoderAnalysis;

            if (config.UsesTable && codec == CodecUtils.Av1anCodec.SvtAv1 && !HasSpace(config.TablePath))
                return GrainDelivery.EncoderTable;

            return GrainDelivery.PostApply;
        }

        /// <summary>
        /// The delivery the encode will really use, which needs the encoder binary's own answer about
        /// <c>--fgs-table</c>.
        /// <para/>
        /// Only SVT-AV1 is asked, and only about that flag, for the reason
        /// <see cref="EncoderArgPresets.Av1anEncoderName"/> already records: SvtAv1EncApp's <c>--help</c>
        /// prints its whole token table where the others print a short list, so a missing flag means
        /// something there and nothing anywhere else. aomenc has <c>--film-grain-table</c> and is
        /// deliberately not given it here - this app cannot confirm the flag against the binary the way it
        /// can for SVT, and the post-apply route reaches the same output without having to.
        /// </summary>
        public static async Task<GrainDelivery> ResolveDeliveryAsync(GrainSynthConfig config, CodecUtils.Av1anCodec codec, string tablePath)
        {
            if (!config.Runs || !IsRowRelevant(codec))
                return GrainDelivery.None;

            if (config.Mode == GrainSynthMode.Encoder)
                return GrainDelivery.EncoderAnalysis;

            if (config.UsesTable && codec == CodecUtils.Av1anCodec.SvtAv1 && !HasSpace(tablePath) &&
                await AvProcess.EncoderKnowsFlagOrIsUnknown("svt-av1", "--fgs-table"))
                return GrainDelivery.EncoderTable;

            return GrainDelivery.PostApply;
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

            if (config.NeedsGrav1synth(delivery) && !Grav1synth.IsAvailable())
                return Grav1synth.DescribeMissing();

            // A table mode that came out as post-apply is worth a line either way, because both reasons
            // for it are invisible from the UI: an SVT-AV1 without --fgs-table is a mainline build rather
            // than the PSY line this project bundles, and a path with a space in it cannot be written into
            // av1an's encoder arguments at all. Neither stops the encode - the grain still reaches the
            // output - so this is a note rather than a refusal.
            if (config.UsesTable && delivery == GrainDelivery.PostApply && codec == CodecUtils.Av1anCodec.SvtAv1)
                Logger.Log(HasSpace(tablePath)
                    ? $"The grain table's path contains a space, which cannot be passed through av1an's encoder " +
                        $"arguments - so grav1synth writes the grain into the finished encode instead."
                    : $"This SVT-AV1 build has no --fgs-table, so the grain table is written into the finished " +
                        $"encode by grav1synth instead of being applied by the encoder. That is a mainline build; the " +
                        $"one this project bundles is the PSY line, which has it.");

            return await Task.FromResult("");
        }

        /// <summary> Brings the readout up to date and enables or disables the row. Touches nothing that
        /// blocks, so it is safe from any handler. </summary>
        public static void RefreshInfo()
        {
            try
            {
                CodecUtils.Av1anCodec codec = Av1anUi.GetCurrentCodecV();
                GrainSynthConfig config = GetAv1anConfig();

                // A mode that cannot run says so here instead of describing what it would have done. The
                // shape this is for is Grain table file with no file picked yet, which is where every user
                // of that mode starts: without this the row read "Table '' · the source's own grain is
                // coded too", which describes an encode that the run would refuse a moment later.
                string problem = config.GetProblem();

                if (problem.IsNotEmpty())
                {
                    Form.Av1anGrainInfoLabel.Text = problem;
                    return;
                }

                string note = config.GetNote(GetLikelyDelivery(config, codec));
                string cost = DescribeCost(config);

                Form.Av1anGrainInfoLabel.Text = cost.IsEmpty() ? note : $"{note} · {cost}";
            }
            catch (Exception e)
            {
                Logger.Log($"Failed to describe the grain synthesis setting: {e.Message}", true);
            }
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
        private static string DescribeCost(GrainSynthConfig config)
        {
            if (config.Mode != GrainSynthMode.Measured || !config.Runs)
                return "";

            MediaFile file = TrackList.current?.File;
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

        /// <summary> The table file picker, off the row's own browse button. </summary>
        public static async Task PickTableAsync()
        {
            string[] paths = await Pickers.PickFiles(Form, "Pick a film grain table", allowMultiple: false);

            if (paths == null || paths.Length < 1 || paths[0].IsEmpty())
                return;

            Form.Av1anGrainTableBox.Text = paths[0];
            RefreshInfo();
        }
    }
}
