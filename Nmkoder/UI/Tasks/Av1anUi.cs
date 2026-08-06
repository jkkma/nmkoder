using Nmkoder.Data;
using Nmkoder.Data.Codecs;
using Nmkoder.Data.Colors;
using Nmkoder.Data.Streams;
using Nmkoder.Data.Ui;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.Media;
using Nmkoder.Utils;
using Nmkoder.Views;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Nmkoder.UI.Tasks
{
    class Av1anUi
    {
        private static MainWindow Form { get { return Program.MainWin; } }

        public static CropConfig CurrentCrop;

        /// <summary> The section to encode, or null for the whole video. Picked in the cut dialog. </summary>
        public static TrimSettings CurrentTrim;

        /// <summary> The deinterlacing settled for the encode being built, resolved once in
        /// <see cref="Av1an.Run"/>. Either an ffmpeg filter that goes into av1an's '-f' chain, or -
        /// where it is QTGMC - a pass that runs before av1an and replaces its input, since a
        /// VapourSynth script cannot sit inside av1an's per-chunk filtering. </summary>
        public static DeinterlacePlan CurrentDeinterlace = new DeinterlacePlan();

        /// <summary> The grain synthesis settled for the encode being built, resolved once in
        /// <see cref="Av1an.Run"/> - which of the three arguments the encoder is given, or whether
        /// grav1synth writes the grain into the finished file instead, and where the table is. Null
        /// outside an encode, where <see cref="GrainSynthUi.GetPreviewPlan"/> answers instead. </summary>
        public static GrainPlan CurrentGrain = null;

        /// <summary> The tone-map this run is doing, snapshotted by <see cref="Av1an.Run"/> before the
        /// first thing that reads it. Kept here rather than read from the box the way the rest of this
        /// class reads its controls, for the reason the quality mode and the chunk method are
        /// snapshotted: the tab stays editable across Run's awaits - the colour probe, the auto-crop
        /// scan, av1an's --help - and three separate reads of the box could refuse one setting, tag the
        /// output for a second and filter with a third. </summary>
        public static ToneMapConfig CurrentToneMap = new ToneMapConfig();

        public static void Init()
        {
            // Load video codecs. SVT-AV1 rather than the first entry, and stated here rather than left
            // to a saved value: the Video tab restores nothing between sessions, so this is the only
            // place the default encoder is named. Every other control on that tab is filled from
            // whichever one this picks.
            Form.Av1anCodecBox.SetItems(Enum.GetValues<CodecUtils.Av1anCodec>().Select(c => (object)CodecUtils.GetCodec(c).FriendlyName),
                (int)CodecUtils.Av1anCodec.SvtAv1);

            // Load quality modes
            Form.Av1anQualModeBox.SetItems(Enum.GetValues<Av1an.QualityMode>()
                .Select(qm => (object)qm.ToString().Replace("Crf", "CRF").Replace("TargetVmaf", "Target VMAF")
                    .Replace("TargetSsimu2", "Target SSIMULACRA2").Replace("TargetButteraugli", "Target Butteraugli")
                    .Replace("TargetXpsnr", "Target XPSNR")), 0);

            Form.Av1anOptsSplitModeBox.SelectedIndex = 1;

            // Load chunk modes
            Form.Av1anOptsChunkModeBox.SetItems(Enum.GetValues<Av1an.ChunkMethod>().Select(cm => (object)cm));

            // Load audio codecs
            Form.Av1anAudCodecBox.SetItems(Enum.GetValues<CodecUtils.AudioCodec>().Select(c => (object)CodecUtils.GetCodec(c).FriendlyName));
            ConfigParser.LoadComboxIndex(Form.Av1anAudCodecBox);

            // MP4 is appended rather than ordered with the rest: the selected index is what gets
            // saved, so inserting ahead of the others would repoint saved settings.
            Form.Av1anContainerBox.SetItems(new[] { Containers.Container.Mkv, Containers.Container.Webm, Containers.Container.Mp4 }.Select(c => (object)c.ToString().Upper()), 0);

            // Filled once. Unlike the resize list these entries name a shape rather than a size, so
            // there is nothing in them for a new file to change - what this file comes out as is on
            // the line underneath instead. Off is the first entry and where the tab opens, nothing
            // here being restored between sessions.
            Form.Av1anBordersBox.SetItems(BorderPresets.All.Select(p => (object)p.Name), 0);
        }

        public static void InitFile(string path)
        {
            try
            {
                if (path.IsNotEmpty())
                    Form.Av1anOutputPathBox.Text = UiData.GetDefaultOutPath(path);

                if (!RunTask.runningBatch) // Don't load new values into UI in batch mode since we apply the same for all files
                {
                    InitAudioChannels(TrackList.current?.File.AudioStreams.FirstOrDefault()?.Channels);
                    // The resize targets are named for what they produce for *this* file, so the list is
                    // rewritten whenever the file behind it changes - but not while a batch is stepping
                    // through files of its own accord, which is not the user loading one.
                    RefreshResizeBox();
                }

                ValidateContainer();

                // Outside the batch guard above, which is about not overwriting settings: this writes no
                // setting at all. The Grain Synthesis readout names an hours-long estimate worked out from
                // the loaded file's size and length, so it describes the wrong file until it is rewritten -
                // and during a batch it is the only place that estimate is ever seen.
                GrainSynthUi.RefreshInfo();
            }
            catch (Exception e)
            {
                Logger.Log($"Failed to initialize media file: {e.Message}\n{e.StackTrace}");
            }
        }

        public static void InitAudioChannels(int? ch)
        {
            if (ch == null || ch < 1)
            {
                Form.Av1anAudChannelsBox.SelectedIndex = 1;
                return;
            }

            for (int i = 0; i < Form.Av1anAudChannelsBox.ItemCount; i++)
            {
                if (Form.Av1anAudChannelsBox.Items[i].ToString().Split(' ').First().GetInt() == ch)
                    Form.Av1anAudChannelsBox.SelectedIndex = i;
            }
        }

        public static void VidEncoderSelected(int index)
        {
            if (index < 0)
                return;

            CodecUtils.Av1anCodec c = (CodecUtils.Av1anCodec)index;
            IEncoder enc = CodecUtils.GetCodec(c);
            Form.QInfoLabel.Text = enc.QInfo;
            Form.PresetInfoLabel.Text = enc.PresetInfo;
            GrainSynthUi.ApplyControlVisibility(); // Only AV1 has grain synthesis, and each mode its own control
            LoadQualityLevel(enc);
            LoadPresets(enc);
            LoadColorFormats(enc);
            LoadAdvancedArgsGrid(enc);
            ApplyWorkerCount(c);
        }

        #region Worker Count

        /// <summary>
        /// How many workers SVT-AV1 gives up against every other encoder av1an drives. It loads a core
        /// far harder than they do, so the count that keeps them all busy oversubscribes the machine on
        /// this one and every worker then runs slower than it would have with the machine to itself.
        /// </summary>
        public const int SvtAv1WorkerPenalty = 2;

        /// <summary>
        /// The worker count for an encoder that is not SVT-AV1 - the "n" the penalty is measured from,
        /// and the number that is saved. It starts each session at whatever was stored under
        /// <see cref="Config.Key.Av1anOptsWorkerCountUpDown"/>, which on a first launch is the Workers
        /// half of <see cref="Av1an.GetDefaultThreadPlan"/> - the Threads half is settled in the same
        /// call, so a change to either has to be read against the penalty below.
        /// </summary>
        static int workerBaseline;

        /// <summary> Set while this class writes the worker box, so its own ValueChanged handler does
        /// not read that write as a hand edit and move the baseline by the penalty each time. </summary>
        static bool writingWorkerCount;

        /// <summary> The last encoder <see cref="ApplyWorkerCount"/> ran for, so the count is only
        /// announced for a switch the user made - the first call fills a box nobody has looked at yet. </summary>
        static CodecUtils.Av1anCodec? lastWorkerCodec;

        /// <summary> What <c>SaveConfigAv1an</c> writes for the worker box. Saving the box itself
        /// would store the reduced count while SVT-AV1 is selected, and the next session would reduce
        /// that again - two workers gone per launch, for as long as the app is opened. </summary>
        public static int WorkerBaseline => workerBaseline;

        /// <summary> Takes the restored worker count as the baseline. Called where the box is loaded,
        /// rather than left to the ValueChanged below, because a saved value equal to the one the XAML
        /// starts the box at raises no event at all. </summary>
        public static void LoadWorkerBaseline()
        {
            workerBaseline = Form.Av1anOptsWorkerCountUpDown.Value.AsInt();
        }

        /// <summary> What the encoder gives up against the baseline, which is nothing at all for any
        /// encoder but SVT-AV1. </summary>
        static int WorkerPenaltyFor(CodecUtils.Av1anCodec codec)
        {
            return codec == CodecUtils.Av1anCodec.SvtAv1 ? SvtAv1WorkerPenalty : 0;
        }

        /// <summary>
        /// Writes the worker count for the encoder now selected into the box, so what is on screen is
        /// what the command line gets - <see cref="Av1an.Run"/> reads the box and nothing else.
        /// </summary>
        static void ApplyWorkerCount(CodecUtils.Av1anCodec codec)
        {
            var box = Form.Av1anOptsWorkerCountUpDown;
            // A machine without two workers to give up keeps the one it can run. The default worker
            // count bottoms out at 2, so a small enough machine meets this on its very first launch.
            int effective = Math.Max((int)box.Minimum, workerBaseline - WorkerPenaltyFor(codec));
            int before = box.Value.AsInt();

            writingWorkerCount = true;
            box.SetValueClamped(effective);
            writingWorkerCount = false;

            // A number that moved on its own is one to say out loud - but only for a switch, and only
            // where it moved. Encoders other than SVT-AV1 all read the same count, so stepping between
            // two of them says nothing, and the first call of the session is filling the box rather
            // than changing it.
            bool switched = lastWorkerCodec != null && lastWorkerCodec != codec;
            lastWorkerCodec = codec;

            if (!switched || effective == before)
                return;

            Logger.Log(codec == CodecUtils.Av1anCodec.SvtAv1
                ? $"Dropped to {effective} worker{(effective == 1 ? "" : "s")} from {workerBaseline} - SVT-AV1 works a core " +
                  $"much harder than the other encoders, so the count that suits them oversubscribes the machine on it."
                : $"Back to {effective} worker{(effective == 1 ? "" : "s")} - the reduced count is SVT-AV1's alone.");
        }

        /// <summary>
        /// The box was written to. A hand edit states the count for the encoder in front of the user,
        /// so the baseline is worked back out of it: type 4 under SVT-AV1 and 4 is what comes back on
        /// selecting it again and next session, with the other encoders keeping the two it is measured
        /// against. Adding the penalty rather than the amount of it <see cref="ApplyWorkerCount"/> got
        /// to apply is what makes that hold at the floor - a box showing 1 because the baseline had
        /// only one worker to give up is still a box whose next number can afford both.
        /// </summary>
        public static void WorkerCountEdited()
        {
            if (writingWorkerCount)
                return;

            workerBaseline = Form.Av1anOptsWorkerCountUpDown.Value.AsInt() + WorkerPenaltyFor(GetCurrentCodecV());
        }

        #endregion

        public static void AudEncoderSelected(int index)
        {
            if (index < 0)
                return;

            CodecUtils.AudioCodec c = (CodecUtils.AudioCodec)index;
            IEncoder enc = CodecUtils.GetCodec(c);

            Form.Av1anAudChannelsBox.IsEnabled = !(c == CodecUtils.AudioCodec.CopyAudio || c == CodecUtils.AudioCodec.StripAudio);
            Form.Av1anAudQualUpDown.IsEnabled = enc.QDefault >= 0 && Math.Abs(enc.QMin - enc.QMax) > 0;
            LoadAudBitrate(enc);
            ValidateContainer();
        }

        /// <summary>
        /// The encoder's default bitrate, for a stereo track. The channel multiplier is deliberately
        /// left off: it is applied again where the arguments are built, so pre-multiplying it here
        /// encoded a 5.1 track at twice the bitrate the box was showing.
        /// </summary>
        static void LoadAudBitrate(IEncoder enc)
        {
            Form.Av1anAudQualUpDown.SetValueClamped(enc.QDefault >= 0 ? enc.QDefault : 0);
        }

        #region Load Info After Selecting Encoder

        static void LoadQualityLevel(IEncoder enc)
        {
            if (IsUsingTargetQuality()) // The spinner holds a metric score, not this encoder's CRF scale
                return;

            Form.Av1anQualityUpDown.SetRange(enc.QMin, enc.QMax > 0 ? enc.QMax : 100);

            if (enc.QDefault >= 0)
                Form.Av1anQualityUpDown.SetValueClamped(enc.QDefault);
        }

        static void LoadPresets(IEncoder enc)
        {
            Form.Av1anPresetBox.SetItems(
                (enc.Presets ?? new string[0]).Select(p => (object)p.ToTitleCase()),
                enc.Presets != null && enc.Presets.Length > 0 ? enc.PresetDefault : -1);
        }

        static void LoadColorFormats(IEncoder enc)
        {
            Form.Av1anColorsBox.SetItems(
                (enc.ColorFormats ?? new List<PixelFormats>()).Select(p => (object)PixFmtUtils.GetFormat(p).FriendlyName),
                enc.ColorFormats != null && enc.ColorFormats.Count > 0 ? enc.ColorFormatDefault : -1);
        }

        /// <summary>
        /// The advanced grid's rows as the encoder arguments av1an passes on. Shared with Quick
        /// Convert, which spells the same rows differently - see <see cref="EncoderArgs"/>.
        /// </summary>
        public static string BuildAdvancedArgs(IEnumerable<EncoderArgRow> rows)
        {
            return EncoderArgs.BuildCli(rows);
        }

        public static void LoadAdvancedArgsGrid(IEncoder enc)
        {
            EncoderArgs.Load(Form.Av1anArgRows, enc, EncoderArgs.Av1anFolder);
            Form.LoadArgCategoryTabs(Form.Av1anArgs);
            Form.LoadArgPresets(Form.Av1anArgs, enc.Name);
        }

        #endregion

        #region Get Current Codec

        public static CodecUtils.Av1anCodec GetCurrentCodecV()
        {
            return (CodecUtils.Av1anCodec)Math.Max(0, Form.Av1anCodecBox.SelectedIndex);
        }

        public static CodecUtils.AudioCodec GetCurrentCodecA()
        {
            return (CodecUtils.AudioCodec)Math.Max(0, Form.Av1anAudCodecBox.SelectedIndex);
        }

        #endregion

        #region Get Args From UI

        public static Dictionary<string, string> GetVideoArgsFromUi()
        {
            Dictionary<string, string> dict = new Dictionary<string, string>();
            dict.Add("qMode", Form.Av1anQualModeBox.SelectedIndex.ToString());
            dict.Add("q", Form.Av1anQualityUpDown.Value.AsInt().ToString());
            dict.Add("preset", Form.Av1anPresetBox.GetText().ToLower());

            IEncoder enc = CodecUtils.GetCodec(GetCurrentCodecV());

            if (enc.ColorFormats != null && Form.Av1anColorsBox.SelectedIndex >= 0)
                dict.Add("pixFmt", PixFmtUtils.GetFormat(enc.ColorFormats[Form.Av1anColorsBox.SelectedIndex]).Name);

            // The row owns three different arguments and writes at most one of them, so which entries
            // exist here is the plan's answer rather than the controls'. CurrentGrain is what the encode
            // settled on a few lines above this being called, and is the only thing that should ever be
            // read here; the row's own guess is a guard against a future caller that has not resolved one,
            // and differs in that it has not asked the encoder binary about --fgs-table.
            GrainPlan grain = CurrentGrain ?? GrainSynthUi.GetPreviewPlan();

            if (grain.IsEncoderAnalysis)
            {
                dict.Add("grainSynthStrength", grain.Config.Strength.ToString());
                dict.Add("grainSynthDenoise", grain.Config.Denoise.ToString());
            }
            else if (grain.IsEncoderTable)
            {
                dict.Add("fgsTable", grain.TablePath);
            }

            dict.Add("threads", Form.Av1anThreadsUpDown.Value.AsInt().ToString());
            dict.Add("custom", Form.Av1anCustomEncArgsBox.Text ?? "");
            dict.Add("advanced", BuildAdvancedArgs(Form.Av1anArgRows));
            return dict;
        }

        public static Dictionary<string, string> GetAudioArgsFromUi()
        {
            Dictionary<string, string> dict = new Dictionary<string, string>();
            dict.Add("bitrate", Form.Av1anAudQualUpDown.Value.AsInt().ToString());
            dict.Add("ac", Form.Av1anAudChannelsBox.GetText().Split(' ')[0].Trim());
            return dict;
        }

        #endregion

        #region Get Args

        /// <summary>
        /// Works out what this encode's frames will be: the source, less whatever crop is set, then
        /// the resize - or the anamorphic de-squeeze that runs in its place, or the mod-2 pad that
        /// runs when neither does.
        /// <para/>
        /// Split off from <see cref="GetVideoFilterArgs"/> and run ahead of the encoder's own
        /// arguments, because those need the size: the tile count belongs to the frame being encoded
        /// rather than to the file it came from. The crop is resolved here rather than there because
        /// an automatic one has to be measured by sampling the video, which is the whole reason this
        /// is the async half of the pair - and the reason its answer is carried rather than asked for
        /// twice. See <see cref="Av1anFrame"/>.
        /// </summary>
        public static async Task<Av1anFrame> ResolveFrameAsync()
        {
            Av1anFrame frame = new Av1anFrame();
            VideoStream vs = TrackList.current?.File.VideoStreams.FirstOrDefault();

            if (vs == null)
                return frame;

            frame.Source = frame.ScaleInput = frame.Encoded = vs.Resolution;
            frame.Sar = vs.Sar;

            Fraction fps = GetUiFps();
            Fraction sourceRate = Deinterlace.GetEffectiveSourceRate(vs, CurrentDeinterlace);

            // Compared with a tolerance rather than exactly - see MiscUtils.FrameRateTolerance, which is
            // there because "23.976" and 24000/1001 are the same rate written two ways and this used to
            // build a filter between them.
            if (fps.GetFloat() > 0.01f && !MiscUtils.IsSameFrameRate(sourceRate, fps)) // Framerate Resampling
            {
                frame.FpsFilter = $"fps=fps={fps}";

                // Said out loud because nothing else says it. The resize has a readout and a per-file
                // log line; this box has neither, so a rate typed for one file - or typed with a digit
                // out of place - reached the end of an encode without ever being mentioned.
                Logger.Log($"Resampling the frame rate from {MiscUtils.DescribeFrameRate(sourceRate)} to " +
                    $"{MiscUtils.DescribeFrameRate(fps)}" +
                    $"{(CurrentDeinterlace != null && CurrentDeinterlace.DoublesFrameRate ? " - the source rate here is the doubled one, from one frame per field" : "")}. " +
                    $"Frames are duplicated or dropped to fit and the running time stays the same; av1an is told to ignore the chunk frame counts it changes.");
            }

            // The resize is a rule rather than a pair of numbers, so the pixels it comes out to are only
            // settled here: this runs per file, after the crop above the scale filter has been decided,
            // which is what lets one setting mean the right thing for a batch of differently shaped files
            // and for the frame a crop leaves behind rather than the one the file started with.
            frame.Resizing = CurrentResize != null && CurrentResize.Mode != ResizeMode.Disabled
                && !CurrentResize.Compute(vs.Resolution, vs.Sar).IsEmpty;

            // With no resize configured, an anamorphic source still needs its shape restored here:
            // av1an hands its encoders bare frames and muxes without an aspect flag, so a SAR left
            // to "carry through" arrives nowhere, and a 16:9 DVD would come out playing as a
            // squashed 3:2. De-squeezing to the display size is the only way the shape survives
            // this pipeline - which is what the resize dialog's anamorphic switch says in as many
            // words. A custom filter that sets a SAR or DAR itself is the user taking this over,
            // and is left in charge.
            frame.Desqueezing = !frame.Resizing && AspectRatio.IsAnamorphic(vs.Sar)
                && !GetCustomFilters().Any(f => f.Contains("setsar") || f.Contains("setdar"));

            string cropMode = Form.Av1anCropBox.GetText().ToLower();

            if (cropMode.Contains("manual") && CurrentCrop != null && CurrentCrop.IsSet) // Manual Crop
            {
                // Carried rather than acted on, so the run can refuse with a sentence naming the file
                // instead of av1an discovering "Invalid too big or non positive size" one chunk at a
                // time. Reachable without anyone typing anything strange: the four edges outlive the
                // file they were set for, and a batch does not clear them between files.
                frame.CropProblem = CurrentCrop.GetProblem(vs.Resolution);

                if (frame.CropProblem.IsEmpty())
                {
                    frame.CropFilters.Add($"crop={CurrentCrop.GetFilterArgs(vs.Resolution)}");
                    frame.ScaleInput = CurrentCrop.GetCroppedSize(vs.Resolution);
                }
            }

            if (cropMode.Contains("auto")) // Autocrop - the sampling run this method exists to do only once
            {
                string autoCrop = await FfmpegUtils.GetCurrentAutoCrop(TrackList.current.File.ImportPath, false);

                if (autoCrop.IsNotEmpty())
                {
                    frame.CropFilters.Add(autoCrop);
                    frame.ScaleInput = FfmpegUtils.ParseCropSize(autoCrop, frame.ScaleInput);
                }
            }

            // Padding an odd frame to mod 2 is what stops it reaching an encoder that will not take one,
            // and it is measured against what the crop leaves rather than against the source: the pad now
            // runs *after* the crop, because a rectangle taken out of a padded picture is a rectangle
            // measured against the wrong frame. A resize makes it redundant either way - every size that
            // one computes is already even.
            frame.Padding = !frame.Resizing && !frame.Desqueezing
                && ((frame.ScaleInput.Width % 2 != 0) || (frame.ScaleInput.Height % 2 != 0));

            frame.Encoded = frame.ScaleInput;

            bool scaling = frame.Resizing && !CurrentResize.IsNoOp(frame.ScaleInput, vs.Sar);

            if (scaling)
                frame.Encoded = OrKeep(CurrentResize.Compute(frame.ScaleInput, vs.Sar), frame.ScaleInput);
            else if (frame.Desqueezing)
                frame.Encoded = OrKeep(ResizeConfig.DesqueezeOnly().Compute(frame.ScaleInput, vs.Sar), frame.ScaleInput);
            else if (frame.Padding)
                frame.Encoded = new Size(RoundUpToEven(frame.ScaleInput.Width), RoundUpToEven(frame.ScaleInput.Height));

            // Last, and measured against what everything above it leaves: the bars go around the
            // finished picture rather than being scaled along with it, since a scaler run over a hard
            // black edge rings and the bars would come out neither black nor straight.
            //
            // What that frame's pixels are shaped like is a separate question, and the answer is
            // exactly "square wherever one of the two filters above ran", both of them ending in
            // setsar=1:1. Where neither did, the source's own SAR is still in force - which on this
            // tab means either square pixels or a custom filter that has taken the shape over.
            frame.Scaled = frame.Encoded;
            frame.Border = CurrentBorders.Compute(frame.Scaled, scaling || frame.Desqueezing ? new Size(1, 1) : vs.Sar);
            frame.Encoded = frame.Border.Frame;

            return frame;
        }

        /// <summary> <paramref name="computed"/>, or the frame it was computed from where there was nothing to compute. </summary>
        private static Size OrKeep(Size computed, Size fallback)
        {
            return computed.IsEmpty ? fallback : computed;
        }

        private static int RoundUpToEven(int value)
        {
            return value % 2 == 0 ? value : value + 1;
        }

        /// <summary>
        /// The '-vf' argument for the encode, built from the geometry <see cref="ResolveFrameAsync"/>
        /// has already settled. Nothing here has to be measured, which is what lets it be synchronous.
        /// <para/>
        /// <paramref name="sourceColor"/> is passed in rather than read off the file for one reason: by
        /// the time this runs, <see cref="Av1an.Run"/> may have swapped the file's colour for the one the
        /// *encoder* is being told about, which for a tone-mapped encode is BT.709. The chain has to
        /// describe what is going in, and the encoder arguments what is coming out, so the two cannot
        /// share a source.
        /// </summary>
        public static string GetVideoFilterArgs(Av1anFrame frame, VideoColorData sourceColor, CodecArgs codecArgs = null)
        {
            List<string> filters = new List<string>();

            if (frame == null || frame.Source.IsEmpty || TrackList.current.File.VideoStreams.Count < 1)
                return "";

            // First in the chain, because the crop and the resize below it are both measured against a
            // whole frame rather than against a pair of fields.
            string deinterlace = CurrentDeinterlace.GetFfmpegFilter();

            if (deinterlace.IsNotEmpty())
                filters.Add(deinterlace);

            // Second, ahead of the geometry, as it is on Quick Convert - although the reason that settles
            // it there does not exist here, this tab having no subtitle burn-in. Kept in step anyway, so
            // the two tabs cannot produce different pixels from the same settings.
            //
            // It runs inside av1an, once per chunk, rather than as a pass in front of it the way QTGMC
            // does. It can: this is an ordinary ffmpeg filter chain with nothing for VapourSynth to
            // evaluate, and it neither changes the frame count nor the frame size, so av1an's chunking
            // has nothing to disagree with. What it does share with every other filter on this tab is
            // that the target-quality probes never see it - see GetFilteredTargetQualityNote, which
            // covers this one by counting the chain rather than by naming what is in it.
            string toneMapFilter = CurrentToneMap.GetFilterArgs(sourceColor);

            if (toneMapFilter.IsNotEmpty())
            {
                filters.Add(toneMapFilter);
                Logger.Log(CurrentToneMap.GetNote(sourceColor));
            }

            if (codecArgs != null && codecArgs.ForcedFilters != null)
                filters.AddRange(codecArgs.ForcedFilters);

            if (frame.ResamplesFrameRate) // Check Filter: Framerate Resampling
                filters.Add(frame.FpsFilter);

            filters.AddRange(frame.CropFilters); // Check Filter: Manual Crop / Autocrop

            // After the crop, not before it. A crop rectangle is measured against the frame the file
            // actually has, so padding first moved the picture out from under it - and padding an odd
            // source only to crop an odd rectangle out of the result left the encoder with the odd
            // frame the pad existed to prevent.
            if (frame.Padding) // Check Filter: Pad for mod2
                filters.Add(FfmpegUtils.GetPadFilter(2));

            if (frame.Resizing && !CurrentResize.IsNoOp(frame.ScaleInput, frame.Sar)) // Check Filter: Scale
            {
                filters.Add(CurrentResize.GetFilterArgs(frame.ScaleInput, frame.Sar));
                LogResize(frame);
            }
            else if (frame.Desqueezing) // Check Filter: De-squeeze, when no resize will run
            {
                ResizeConfig desqueeze = ResizeConfig.DesqueezeOnly();

                if (!desqueeze.Compute(frame.ScaleInput, frame.Sar).IsEmpty)
                {
                    filters.Add(desqueeze.GetFilterArgs(frame.ScaleInput, frame.Sar));
                    // Scaled rather than Encoded: what the de-squeeze produced, not what the border
                    // bars added after it leave.
                    Logger.Log($"De-squeezing {frame.ScaleInput.Width}x{frame.ScaleInput.Height} ({frame.Sar.Width}:{frame.Sar.Height} pixels) to " +
                        $"{frame.Scaled.Width}x{frame.Scaled.Height} - av1an's encoders take bare frames and no aspect flag, so the shape " +
                        $"has to be baked into the pixels to survive. Configure a resize to control the size.");
                }
            }

            // Said here as well as on the tab, because this is where the file gets written. With the
            // correction off nothing in the chain restores the display size, and there is nowhere else
            // for the shape to live: av1an hands its encoders bare frames and muxes without an aspect
            // flag. Worth spelling out rather than silently obeying, since the commonest way to reach it
            // is a target the source already meets, where no filter runs at all and the tab would
            // otherwise have said the frames were being left alone.
            if (frame.Resizing && !CurrentResize.CorrectAspect && AspectRatio.IsAnamorphic(frame.Sar))
            {
                Size display = AspectRatio.GetDisplaySize(frame.Source, frame.Sar);
                // Scaled rather than Encoded, and pointedly: this sentence names the shape the file
                // will play at, and border bars added after the scale are a different shape again -
                // stating the padded one here would say the resize produced a ratio it did not.
                Logger.Log($"Warning: this resize has anamorphic correction switched off, so the {frame.Sar.Width}:{frame.Sar.Height} " +
                    $"pixel shape is not baked in - and av1an's encoders cannot record it. The output will be " +
                    $"{frame.Scaled.Width}x{frame.Scaled.Height} playing as {AspectRatio.Describe(frame.Scaled.Width, frame.Scaled.Height)} " +
                    $"rather than {AspectRatio.Describe(display.Width, display.Height)}. Switch it back on in the resize dialog to keep the shape.");
            }

            if (frame.Border.Runs) // Check Filter: Borders to a target aspect ratio
            {
                filters.Add(frame.Border.GetFilterArgs());
                LogBorders(frame);
            }

            filters.AddRange(GetCustomFilters());

            filters = filters.Where(x => x.Trim().Length > 2).ToList(); // Strip empty filters

            if (filters.Count > 0)
                return $"-vf {string.Join(",", filters)}";
            else
                return "";
        }

        private static List<string> GetCustomFilters()
        {
            return Form.Av1anFilterRows.Select(x => x.Filter).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        }

        /// <summary> Said out loud at encode time, because with an automatic crop this is the first moment
        /// the numbers exist at all, and in a batch it is different for every file. </summary>
        private static void LogResize(Av1anFrame frame)
        {
            // What the scale filter leaves, which is not what the encoder gets once bars are added
            // around it: this line is about the resize, and saying it produced the padded frame would
            // credit it with a size it had nothing to do with.
            Size result = frame.Scaled;
            string source = AspectRatio.IsAnamorphic(frame.Sar) && CurrentResize.CorrectAspect
                ? $"{frame.ScaleInput.Width}x{frame.ScaleInput.Height} at {frame.Sar.Width}:{frame.Sar.Height} pixels"
                : $"{frame.ScaleInput.Width}x{frame.ScaleInput.Height}";

            Logger.Log($"Resizing {source} to {result.Width}x{result.Height} ({AspectRatio.Describe(result.Width, result.Height)}).");

            // The box presets enlarge a source smaller than their target, so this is reachable without
            // anyone having opened the resize dialog - and in a batch of mixed resolutions it is the
            // small files it happens to, one at a time, where the tab's readout only ever described the
            // file that was loaded. Said rather than prevented: the size asked for is the size delivered.
            if (CurrentResize.IsUpscale(frame.ScaleInput, frame.Sar))
                Logger.Log($"Note: that is larger than the source, so this encode is upscaling. It invents no " +
                    $"detail and the softness it produces costs bitrate - pick a smaller target if that was not the intent.");

            // Not clamped to, only mentioned: silently growing a frame to something the user did not ask
            // for is worse than an encoder saying no. SVT-AV1 refuses anything under 64 in either
            // direction, x265 anything under 16; the other encoders take whatever they are given.
            // Measured on the frame the encoder is handed rather than on the one the scaler leaves,
            // since border bars are part of what it has to take.
            if (frame.Encoded.Width < 64 || frame.Encoded.Height < 64)
                Logger.Log($"Warning: {frame.Encoded.Width}x{frame.Encoded.Height} is very small - SVT-AV1 will not encode a frame under 64 pixels on either side.");
        }

        /// <summary> Said per file at encode time, like the resize line above it and for the same
        /// reasons: with an automatic crop this is the first moment the numbers exist, and across a
        /// batch of differently shaped files one setting produces a different answer for each - a
        /// letterbox for the films in it and a pillarbox for the 4:3 captures. </summary>
        private static void LogBorders(Av1anFrame frame)
        {
            BorderPad pad = frame.Border;

            Logger.Log($"Adding {pad.Describe()} to {pad.Input.Width}x{pad.Input.Height} " +
                $"({AspectRatio.Describe(pad.Input.Width, pad.Input.Height)}), which brings the frame to " +
                $"{pad.Frame.Width}x{pad.Frame.Height} ({AspectRatio.Describe(pad.Frame.Width, pad.Frame.Height)}). " +
                $"The picture is not scaled - only the frame around it grows.");
        }

        #endregion

        #region Resize

        /// <summary> The resize to apply, held as intent rather than as pixels. Never null. </summary>
        public static ResizeConfig CurrentResize = new ResizeConfig();

        /// <summary> Set while the dropdown is being refilled, because doing that raises the very
        /// SelectionChanged that would then read the new selection back over what is being shown. </summary>
        private static bool _loadingResizeBox;

        /// <summary>
        /// The frame the resize will be measured against, as far as this can be known without running
        /// anything: the source, less a manual crop. An automatic crop is measured by sampling the video
        /// with ffmpeg, which is far too slow to do while filling a dropdown, so its bars are still in
        /// this number - and the readout says so rather than showing a size that will not be the one.
        /// </summary>
        public static Size GetResizeSourceSize()
        {
            VideoStream vs = TrackList.current?.File.VideoStreams.FirstOrDefault();

            if (vs == null)
                return Size.Empty;

            if (Form.Av1anCropBox.GetText().ToLower().Contains("manual") && CurrentCrop != null)
                return new Size(CurrentCrop.GetCroppedWidth(vs.Resolution), CurrentCrop.GetCroppedHeight(vs.Resolution));

            return vs.Resolution;
        }

        public static Size GetResizeSar()
        {
            return TrackList.current?.File.VideoStreams.FirstOrDefault()?.Sar ?? Size.Empty;
        }

        /// <summary>
        /// Refills the dropdown, whose entries name what each target produces *for the loaded file* -
        /// "1080p (Full HD) — 1920x804" against a 2.39:1 film - so the list answers the question rather
        /// than restating it. Called whenever the frame those targets are measured against moves, which
        /// is a file being loaded or a crop being set, and pointedly not the user picking an entry:
        /// clearing a dropdown's own items from inside its SelectionChanged throws.
        /// </summary>
        public static void RefreshResizeBox()
        {
            Size storage = GetResizeSourceSize();
            Size sar = GetResizeSar();

            try
            {
                _loadingResizeBox = true;
                Form.Av1anResizeBox.SetItems(ResizePresets.All.Select(p => (object)ResizePresets.GetLabel(p, storage, sar)),
                    ResizePresets.IndexFor(CurrentResize));
            }
            finally
            {
                // In a finally because this is the guard that keeps a refill from being read back as a
                // choice - left stuck on, every subsequent pick would be ignored and the dropdown dead.
                _loadingResizeBox = false;
            }

            UpdateResizeReadout();
        }

        /// <summary>
        /// The parts that change when the selection does: the button beside the box, and the line under
        /// it. Touches no collection, so it is safe to call from a SelectionChanged.
        /// <para/>
        /// Deliberately not skipped during a batch, and neither is <see cref="RefreshResizeBox"/>: the
        /// caller that has to stand back for one is the per-file loop, and that is where the check lives.
        /// Anything that moves <see cref="CurrentResize"/> - Reset On New File among them, which the
        /// Settings tab can fire mid-batch - has to move the control with it, or the tab is left naming a
        /// resize that is no longer set and the next queue runs unresized underneath it.
        /// </summary>
        public static void UpdateResizeReadout()
        {
            Form.Av1anResizeConfBtn.IsVisible = ResizePresets.Get(ResizePresets.IndexFor(CurrentResize)).Key == ResizePresets.CustomKey;
            Form.Av1anResizeInfoLabel.Text = GetResizeInfoText(GetResizeSourceSize(), GetResizeSar());

            // The bars are added to what the resize leaves, so everything that moves this readout -
            // a file, a crop, a resize target - moves that one too. Called from here rather than
            // beside each of those, which is four places and counting. Text only: the borders list
            // names no sizes, so it is filled once and never refilled.
            UpdateBordersReadout();
        }

        /// <summary>
        /// Acts on the user picking an entry - and only on the user: refilling the list raises the same
        /// event, and acting on that would read the freshly selected entry back over what is being shown.
        /// </summary>
        public static void ResizePresetSelected(int index)
        {
            if (_loadingResizeBox || index < 0)
                return;

            ResizePreset preset = ResizePresets.Get(index);

            if (preset.Key == ResizePresets.CustomKey)
            {
                // Custom names no target of its own, so picking it changes nothing but where the settings
                // are edited: whatever was selected stays in force and becomes the dialog's starting
                // point. Building a target here instead would silently apply a 1920x1080 nobody asked
                // for, and would throw away a resize configured by hand on a trip through the dropdown.
                CurrentResize = CurrentResize?.Clone() ?? new ResizeConfig();

                if (CurrentResize.Mode == ResizeMode.Disabled)
                    CurrentResize = preset.Build();

                CurrentResize.PresetKey = ResizePresets.CustomKey;
            }
            else
            {
                CurrentResize = preset.Build();
            }

            UpdateResizeReadout();
        }

        /// <summary> The line under the dropdown: the size, the ratio it works out to, and whichever caveat applies. </summary>
        private static string GetResizeInfoText(Size storage, Size sar)
        {
            if (CurrentResize == null || CurrentResize.Mode == ResizeMode.Disabled)
            {
                // The one thing that still runs without a resize: an anamorphic source is de-squeezed,
                // because the encoders cannot keep its aspect flag - saying "its own resolution" here
                // would promise dimensions the output will not have.
                if (!storage.IsEmpty && AspectRatio.IsAnamorphic(sar))
                {
                    Size desqueezed = ResizeConfig.DesqueezeOnly().Compute(storage, sar);

                    if (!desqueezed.IsEmpty)
                        return $"{desqueezed.Width}x{desqueezed.Height} · {AspectRatio.Describe(desqueezed.Width, desqueezed.Height)} · " +
                            $"de-squeezed from {storage.Width}x{storage.Height}, whose pixels are {sar.Width}:{sar.Height} - " +
                            "the encoder cannot keep the anamorphic flag";
                }

                return "The source is encoded at its own resolution.";
            }

            if (TrackList.current != null && TrackList.current.File.VideoStreams.Count < 1)
                return "No video track - the resize will be skipped.";

            if (storage.IsEmpty)
                return $"{CurrentResize.DescribeTarget()} - the size is worked out per file when the encode starts.";

            Size result = CurrentResize.Compute(storage, sar);

            if (result.IsEmpty)
                return "Nothing configured yet - press Configure… to set a target.";

            string text = $"{result.Width}x{result.Height} · {AspectRatio.Describe(result.Width, result.Height)}";
            string note = CurrentResize.GetNote(storage, sar);

            if (note.Length > 0)
                text += $" · {note}";

            if (Form.Av1anCropBox.GetText().ToLower().Contains("auto"))
                text += " · before autocrop; the final size is measured when the encode starts";

            return text;
        }

        #endregion

        #region Borders

        /// <summary> The aspect ratio to pad out to, or an unset configuration for no bars at all.
        /// Never null. Not saved: nothing on this tab is. </summary>
        public static BorderConfig CurrentBorders = new BorderConfig();

        /// <summary>
        /// The frame the bars are measured against, and the shape of its pixels - the source, less a
        /// manual crop, then whatever the resize or the de-squeeze in its place leaves.
        /// <para/>
        /// The same answer <see cref="ResolveFrameAsync"/> arrives at, minus the one thing that cannot
        /// be had without running ffmpeg: an automatic crop's bars are still in this frame, and the
        /// readout says so rather than showing a shape that will not be the one.
        /// </summary>
        private static (Size Frame, Size Sar) GetBorderInput()
        {
            Size storage = GetResizeSourceSize();
            Size sar = GetResizeSar();

            if (storage.IsEmpty)
                return (Size.Empty, sar);

            // Square pixels come out of either scale filter, both of them ending in setsar=1:1
            if (CurrentResize != null && CurrentResize.Mode != ResizeMode.Disabled && !CurrentResize.IsNoOp(storage, sar))
            {
                Size result = CurrentResize.Compute(storage, sar);

                if (!result.IsEmpty)
                    return (result, new Size(1, 1));
            }

            if (AspectRatio.IsAnamorphic(sar)) // The de-squeeze that runs where no resize does
            {
                Size desqueezed = ResizeConfig.DesqueezeOnly().Compute(storage, sar);

                if (!desqueezed.IsEmpty)
                    return (desqueezed, new Size(1, 1));
            }

            return (storage, sar);
        }

        /// <summary> The line under the dropdown: the frame the bars leave, the ratio that comes to,
        /// and which bars they are. </summary>
        public static void UpdateBordersReadout()
        {
            Form.Av1anBordersInfoLabel.Text = GetBordersInfoText();
        }

        private static string GetBordersInfoText()
        {
            if (CurrentBorders == null || !CurrentBorders.IsSet)
                return "The frame keeps whatever shape it has.";

            if (TrackList.current != null && TrackList.current.File.VideoStreams.Count < 1)
                return "No video track - no bars will be added.";

            (Size input, Size sar) = GetBorderInput();

            if (input.IsEmpty)
                return $"Padded out to {CurrentBorders.Label} - the bars are worked out per file when the encode starts.";

            BorderPad pad = CurrentBorders.Compute(input, sar);
            string text;

            if (!pad.Runs)
            {
                text = $"{input.Width}x{input.Height} · already {CurrentBorders.Label}, so no bars are added";
            }
            else
            {
                // The frame limit is checked here for the reason the resize checks it: this is a size
                // FFmpeg refuses to produce at all, and being told so by the tab beats meeting it from
                // inside av1an as "Picture size WxH is invalid" one chunk at a time. Only reachable by
                // padding something extreme towards an extreme ratio, the bars being additive.
                if (ResizeConfig.ExceedsFrameLimit(pad.Frame))
                    return $"{pad.Frame.Width}x{pad.Frame.Height} · too large to encode - FFmpeg will not produce a frame of " +
                        $"{(double)pad.Frame.Width * pad.Frame.Height / 1_000_000d:0.#} megapixels, so nothing would be written";

                text = $"{pad.Frame.Width}x{pad.Frame.Height} · {AspectRatio.Describe(pad.Frame.Width, pad.Frame.Height)} · " +
                    $"{pad.Describe()}, around an unscaled {input.Width}x{input.Height} picture";
            }

            if (Form.Av1anCropBox.GetText().ToLower().Contains("auto"))
                text += " · before autocrop; the bars are measured when the encode starts";

            return text;
        }

        /// <summary> Acts on the user picking an entry. The list carries no per-file labels, so unlike
        /// the resize box it is filled once and never refilled - which is what lets this be the only
        /// thing that ever moves the selection, and why there is no loading guard beside it. </summary>
        public static void BorderPresetSelected(int index)
        {
            if (index < 0)
                return;

            CurrentBorders = BorderPresets.Get(index).Build();
            UpdateBordersReadout();
        }

        #endregion

        #region Get Args

        /// <summary> Whether the Split Method box is asking av1an to detect scene changes. The other
        /// entry cuts on nothing but '-x', so everything under av1an's Scene Detection heading is inert
        /// for it - which is what the callers of this use it to decide. </summary>
        public static bool SceneDetectionEnabled => Form.Av1anOptsSplitModeBox.SelectedIndex != 0;

        public static string GetSplittingMethodArgs()
        {
            return $"--split-method {(SceneDetectionEnabled ? "av-scenechange" : "none")}";
        }

        /// <summary> Takes the method as a value rather than reading the dropdown, so the caller can
        /// emit the same one it validated - the box stays editable while the arguments are built. </summary>
        public static string GetChunkGenMethod(Av1an.ChunkMethod method)
        {
            return $"-m {method.ToString().ToLower()}";
        }

        /// <summary> The selected chunk method. The dropdown is filled from the enum, so index is value. </summary>
        public static Av1an.ChunkMethod GetCurrentChunkMethod()
        {
            return (Av1an.ChunkMethod)Math.Max(0, Form.Av1anOptsChunkModeBox.SelectedIndex);
        }

        /// <summary> The chunk methods that read the source through vspipe, and so run a VapourSynth script at all. </summary>
        private static readonly Av1an.ChunkMethod[] VapourSynthChunkMethods =
            { Av1an.ChunkMethod.BestSource, Av1an.ChunkMethod.LSMASH, Av1an.ChunkMethod.FFMS2 };

        /// <summary> Whether a chunk method decodes through VapourSynth - which SSIMULACRA2 and
        /// Butteraugli probing require. XPSNR does not: ffmpeg scores it, with any chunk method. </summary>
        public static bool IsVapourSynthChunkMethod(Av1an.ChunkMethod method)
        {
            return VapourSynthChunkMethods.Contains(method);
        }

        /// <summary>
        /// Which converter av1an should reach the chosen pixel format with. By default it pipes the
        /// decoded frames through a second ffmpeg process to convert them; "vs-resize" instead has the
        /// VapourSynth script it already generates do it with resize.Bicubic, which drops that process
        /// from every chunk and puts the resampling through zimg rather than swscale.
        /// <para/>
        /// Returns "" - leaving av1an on ffmpeg - whenever a condition av1an attaches to the flag is
        /// unmet, for two different reasons depending on which condition it is.
        /// <para/>
        /// Where VapourSynth would be the one converting, asking it for something it cannot express
        /// fails the chunk rather than falling back: a format vs.PresetVideoFormat has no name for
        /// raises a KeyError, and an RGB source stops at "Matrix must be specified when converting to
        /// YUV or GRAY from RGB", since neither av1an's script nor this flag supplies one.
        /// <para/>
        /// The chunk method and filter conditions are milder - there the flag is inert rather than
        /// harmful, because those chunks are read by an ffmpeg source command that already carries
        /// -pix_fmt and converts on its own. Passing it anyway still encodes correctly; it is left off
        /// so the command does not name a converter that has no part in how the chunk is read.
        /// </summary>
        public static async Task<string> GetPixelFormatConverterArgs(string pixFmt, bool hasFfmpegFilters, Av1an.ChunkMethod chunkMethod)
        {
            const string flag = "--pix-format-converter";

            // Every reason for staying on ffmpeg is logged quietly. Nothing here is a setting anyone
            // chose - it is picked per encode from what av1an, the chunk method and the source allow -
            // so a visible note would be telling most users, every time, about a thing they did not ask
            // for and an outcome that is not wrong. The log file still says which condition it was.
            if (pixFmt.IsEmpty()) // No color format to convert to - the encoder chose it, and av1an is not being told
                return "";

            // Unreleased as of av1an 0.5.2, which is what gets bundled. An older binary refuses the
            // whole command over an unknown flag, so this cannot simply be passed and left to be ignored.
            if (!await AvProcess.Av1anSupportsFlag(flag))
            {
                Logger.Log($"This av1an has no {flag}, so ffmpeg is converting the pixel format.", true);
                return "";
            }

            // The conversion is a step in a VapourSynth script, so it only exists for the chunk methods
            // that have one. The others decode with ffmpeg, which knows nothing of the setting.
            if (!VapourSynthChunkMethods.Contains(chunkMethod))
            {
                Logger.Log($"ffmpeg is converting the pixel format - VapourSynth can only do it for the " +
                    $"{string.Join(", ", VapourSynthChunkMethods)} chunk methods, and this encode uses {chunkMethod}.", true);
                return "";
            }

            // av1an disregards the setting entirely when it has filters to apply, since those run in the
            // very ffmpeg step that vs-resize exists to remove.
            if (hasFfmpegFilters)
            {
                Logger.Log("ffmpeg is converting the pixel format, because the video filters set on this tab run in the same step.", true);
                return "";
            }

            if (PixFmtUtils.GetVapourSynthPreset(pixFmt).IsEmpty())
            {
                Logger.Log($"ffmpeg is converting the pixel format - VapourSynth has no preset format for {pixFmt}.", true);
                return "";
            }

            // resize.Bicubic has to be told which matrix to take RGB to YUV with, and neither av1an's
            // script nor this flag gives it one, so an RGB source would stop at a VapourSynth error.
            string sourceFmt = (TrackList.current?.File.VideoStreams.FirstOrDefault()?.PixelFormat ?? "").ToLower();

            if (sourceFmt.StartsWith("rgb") || sourceFmt.StartsWith("bgr") || sourceFmt.StartsWith("gbr"))
            {
                Logger.Log($"ffmpeg is converting the pixel format - the source is {sourceFmt}, and VapourSynth needs a color matrix to take RGB to YUV.", true);
                return "";
            }

            // Worth saying out loud, unlike the fallbacks: this is the one outcome that changes which
            // resampler the video actually goes through.
            Logger.Log($"Converting the pixel format to {pixFmt} with VapourSynth's resize rather than ffmpeg.");
            return $"{flag} vs-resize";
        }

        /// <summary>
        /// Why the SVT-AV1 mode decision depth that has been asked for cannot be delivered, or "" if
        /// there is nothing wrong.
        /// <para/>
        /// hbd-mds puts some or all of mode decision at 10-bit precision, which SVT-AV1 only does on a
        /// 10-bit input. Set against an 8-bit color format it is accepted, encodes, and changes nothing
        /// - an outcome indistinguishable from it having worked, on a setting whose whole point is a
        /// quality difference too small to see by eye.
        /// </summary>
        public static string GetHbdModeDecisionProblem(CodecUtils.Av1anCodec vCodec, string pixFmt)
        {
            // The advanced grid is reloaded per encoder, so no other encoder can be carrying this row.
            if (vCodec != CodecUtils.Av1anCodec.SvtAv1)
                return "";

            // 0 means an unrecognised format rather than 8-bit, and guessing at one is not worth a
            // warning that would then be wrong.
            if (FormatUtils.GetBitDepthFromPixelFormat(pixFmt) != 8)
                return "";

            string value = GetAdvancedArgValue("hbd-mds");

            // 0 leaves the choice to the preset, so it is not asking the input for something it does
            // not have. 1 is all 10-bit, 2 hybrid - both want 10-bit samples.
            if (value != "1" && value != "2")
                return "";

            return $"Note: hbd-mds is set to {value}, which asks for {(value == "1" ? "all of" : "part of")} the mode decision " +
                $"at 10-bit, but SVT-AV1 only does that on a 10-bit input and the Color Format is 8-bit ({pixFmt}). " +
                $"Pick a 10 bit Color Format for it to have any effect.";
        }

        /// <summary>
        /// What the advanced grid holds for a parameter, matched without its dashes and without case,
        /// or "" where the row is absent or has not been filled in. The grid is preloaded with every
        /// documented parameter and edited in place, so a blank row and a missing one mean the same
        /// thing - neither reaches the command line.
        /// </summary>
        private static string GetAdvancedArgValue(string argument)
        {
            return Form.Av1anArgRows
                .Where(x => (x.Argument ?? "").Trim().TrimStart('-').ToLower() == argument)
                .Select(x => (x.Value ?? "").Trim())
                .FirstOrDefault(x => x.IsNotEmpty()) ?? "";
        }

        /// <summary>
        /// Why an Advanced grid row will not do what it says beside the Grain Synthesis row, or "" if
        /// none is in its way.
        /// <para/>
        /// SVT-AV1 has three ways to be asked for film grain and takes exactly one of them:
        /// <c>--fgs-table</c> switches <c>--noise</c> off, and either of them switches
        /// <c>--film-grain</c> off, each with an <c>SVT_WARN</c> printed to the encoder's own stderr -
        /// which av1an collects per chunk into a log <c>HandleTempFolder</c> deletes on a successful run,
        /// so from here the losing setting simply had no effect.
        /// <para/>
        /// **The Grain Synthesis row now owns two of those three**, which is most of why it became a mode
        /// selector: the collision between <c>--film-grain</c> and <c>--fgs-table</c> can no longer be
        /// expressed, because one control writes whichever of them the mode calls for and never both. What
        /// is left is the Advanced grid, where either can still be typed by hand beside a row that is
        /// already writing it - and <c>--noise</c>, which lives only there and beats both.
        /// <para/>
        /// Denoise is the half worth naming. SVT reads its denoise flag only on the <c>--film-grain</c>
        /// path, so a row that displaces the strength drops the denoising with it: the grain then lands on
        /// top of the grain already in the picture instead of replacing it, which is the opposite of what
        /// ticking the box asks for.
        /// </summary>
        public static string GetGrainSynthProblem(CodecUtils.Av1anCodec vCodec, GrainSynthConfig config, GrainDelivery delivery)
        {
            // aomenc's argument file carries no grain rows at all, and the grid is reloaded per
            // encoder, so no other encoder can be carrying one of these.
            if (vCodec != CodecUtils.Av1anCodec.SvtAv1 || config == null || !config.Runs)
                return "";

            string owned = config.GetOwnedEncoderArg(delivery);

            if (owned.IsEmpty())
                return ""; // The mode writes nothing to the encoder at all - nothing here can collide

            // --fgs-table is read first and switches --noise off in turn, so where both are set it is the
            // one in force. Its value is a path rather than a number, so any of it counts.
            bool table = GetAdvancedArgValue("fgs-table").IsNotEmpty();
            int noise = GetAdvancedArgValue("noise").GetInt();
            bool filmGrain = GetAdvancedArgValue("film-grain").GetInt() > 0;

            // A hand-typed row naming the very argument this row is writing: not a collision between two
            // settings so much as two spellings of one, and the grid's is the one that loses - both end up
            // on the command line and SVT reads the later of them, which is the grid's.
            if ((owned == "fgs-table" && table) || (owned == "film-grain" && filmGrain))
                return $"Note: the Advanced tab has a {owned} row filled in, and the Grain Synthesis row is " +
                    $"writing {owned} for itself - so the two are both on the command line and which one wins is " +
                    $"not something this app decides. Clear that row and set the grain on the Grain Synthesis row.";

            string subject = "";

            if (owned == "film-grain" && table)
                subject = "fgs-table applies a grain table from a file instead of analysing the source, and " +
                    "SVT-AV1 takes one or the other rather than both";
            else if (noise > 0)
                subject = $"noise ({noise}) is SVT-AV1's own second grain synthesiser, and it takes one of the " +
                    $"three rather than several";

            if (subject.IsEmpty())
                return "";

            string winner = owned == "film-grain" && table ? "the table" : "--noise";

            string denoise = config.Mode == GrainSynthMode.Encoder && config.Denoise
                ? $" Denoise goes with it: {winner} does not denoise the source, so the grain being " +
                    $"synthesised lands on top of the grain already in the picture rather than replacing it."
                : "";

            string dropped = owned == "film-grain" ? $"Grain Synthesis {config.Strength}" : "the grain table";

            return $"Note: the Advanced tab's {subject} - so {dropped} is dropped and " +
                $"{winner} runs instead.{denoise} Clear whichever of the two you did not mean.";
        }

        /// <summary>
        /// What SVT-AV1's tune 5 assigns for itself, and the values it assigns. Read out of the fork's
        /// own source rather than its documentation, which describes the bundle without saying when it
        /// is applied - <c>set_param_based_on_input</c>, after the whole command line has been parsed,
        /// so the order the flags are written in cannot save a row set beside it.
        /// <para/>
        /// It sets <c>complex-hvs 1</c> too, which is not here because the parameter list has no row for
        /// it - there is nothing for the tune to overwrite.
        /// </summary>
        private static readonly (string Arg, string Value)[] FilmGrainTuneOverrides =
        {
            ("enable-tf", "0"),
            ("enable-cdef", "0"),
            ("enable-restoration", "0"),
            ("ac-bias", "4.00"),
            ("tx-bias", "1"),
        };

        /// <summary>
        /// The rows tune 5 leaves alone and strands. Each only acts on a filter the tune has switched
        /// off: <c>cdef-scaling</c> scales a CDEF strength that is never computed, <c>tf-strength</c>
        /// and <c>kf-tf-strength</c> scale a temporal filter that does not run, and
        /// <c>noise-adaptive-filtering</c> backs CDEF and loop restoration off on noisy frames, both
        /// being off already.
        /// </summary>
        private static readonly string[] FilmGrainTuneInert =
        {
            "cdef-scaling",
            "tf-strength",
            "kf-tf-strength",
            "noise-adaptive-filtering",
        };

        /// <summary>
        /// Why rows set beside SVT-AV1's tune 5 will not do what they say, or "" if none are.
        /// <para/>
        /// Tune 5 is a bundle rather than a preference, and it wins: the six values it sets are assigned
        /// after the command line has been read, so a grid row naming one of them is overwritten with
        /// only an <c>SVT_WARN</c> to say so - and that goes to the encoder's stderr, which av1an
        /// collects per chunk into a log <c>HandleTempFolder</c> deletes on a successful run. Four more
        /// rows survive the bundle and are stranded by it, their filters having been switched off.
        /// <para/>
        /// A row set to what the tune sets anyway is not reported. It is not being overruled in any
        /// sense the user can act on, and naming it would send someone to clear a row that agrees with
        /// the encode.
        /// <para/>
        /// The Grainy Film preset sets tune 5 and none of the ten, so this cannot fire from a preset -
        /// it is for a row typed by hand, which is the same division
        /// <see cref="GetUnsupportedAdvancedArgsProblem"/> draws.
        /// </summary>
        public static string GetFilmGrainTuneProblem(CodecUtils.Av1anCodec vCodec)
        {
            // The grid is reloaded per encoder, so no other encoder can be holding these rows.
            if (vCodec != CodecUtils.Av1anCodec.SvtAv1)
                return "";

            if (GetAdvancedArgValue("tune") != "5")
                return "";

            var overwritten = FilmGrainTuneOverrides
                .Select(o => (o.Arg, o.Value, Set: GetAdvancedArgValue(o.Arg)))
                .Where(o => o.Set.IsNotEmpty() && !IsSameArgValue(o.Set, o.Value))
                .Select(o => $"{o.Arg} {o.Set}")
                .ToList();

            var inert = FilmGrainTuneInert.Where(a => GetAdvancedArgValue(a).IsNotEmpty()).ToList();

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
        /// where both are numbers, so 4 and 4.00 are one value - the tune above is stated to two decimal
        /// places and nobody types it that way.
        /// </summary>
        private static bool IsSameArgValue(string a, string b)
        {
            if (a.Trim() == b.Trim())
                return true;

            return float.TryParse(a.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                float.TryParse(b.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float y) && x == y;
        }

        /// <summary>
        /// Why the encode cannot start with the advanced grid as it stands, or "" if it can.
        /// <para/>
        /// The grid is filled from a parameter list written against the build this project bundles -
        /// svt-av1-hdr for SVT-AV1, and whatever was current for the rest - while the binary that
        /// actually runs is a build-time accident: the bundle falls back where no prebuilt exists, macOS
        /// bundles no encoder at all, and a user's own PATH is not something the bundler controls. A
        /// parameter the binary in front of it does not have is not ignored; the whole command is
        /// refused, and not one chunk encodes.
        /// <para/>
        /// This refuses where the content presets' own check (GetApplicablePresetValues) drops, and the
        /// difference is who chose the value. A preset is a bundle that stays useful with one entry
        /// taken out of it, and dropping happens as it is applied, in front of whoever clicked it. A row
        /// here was typed by hand on a run that has already been started, where dropping a setting
        /// silently is the failure this check exists to prevent.
        /// <para/>
        /// Nothing is refused on a failed lookup - <see cref="AvProcess.EncoderKnowsFlagOrIsUnknown"/>
        /// answers false only for a help text that was read and lacked the flag. And nothing is asked
        /// at all outside SVT-AV1: <see cref="EncoderArgPresets.Av1anEncoderName"/> is where that limit
        /// is stated, and x264 - whose <c>--help</c> is the short list - is why it exists.
        /// </summary>
        public static async Task<string> GetUnsupportedAdvancedArgsProblem(CodecUtils.Av1anCodec vCodec)
        {
            IEncoder enc = CodecUtils.GetCodec(vCodec);
            string av1anEncoder = EncoderArgPresets.Av1anEncoderName(enc.Name);

            if (av1anEncoder.IsEmpty())
                return "";

            var unsupported = new List<string>();

            foreach (EncoderArgRow row in Form.Av1anArgRows.Where(x => x.Argument.IsNotEmpty() && x.Value.IsNotEmpty()))
            {
                string arg = row.Argument.Trim().TrimStart('-');

                // Matched with the dashes on, so a parameter is not found inside a longer one's name
                if (!await AvProcess.EncoderKnowsFlagOrIsUnknown(av1anEncoder, $"--{arg}"))
                    unsupported.Add(arg);
            }

            if (unsupported.Count < 1)
                return "";

            bool one = unsupported.Count == 1;

            // Worth naming the cause rather than the symptom, the way the preset path does. For
            // SVT-AV1 there is only one thing this ever means.
            string cause = vCodec == CodecUtils.Av1anCodec.SvtAv1
                ? "That means it is mainline SVT-AV1 rather than the PSY-line build (svt-av1-hdr) this " +
                    "tab's parameter list is written for."
                : "That means it is an older build than that list was written against.";

            return $"{string.Join(", ", unsupported)} {(one ? "is" : "are")} set on the Advanced tab, and the " +
                $"encoder that would run does not have {(one ? "it" : "them")}. An unrecognised parameter is " +
                $"refused as a whole command, so not one chunk would encode.\n\n{cause}\n\n" +
                $"Clear {(one ? "that row" : "those rows")}, or use a build that has {(one ? "it" : "them")}.";
        }

        /// <summary> The chosen output container. The dropdown offers a subset of the enum, in its own order. </summary>
        public static Containers.Container GetCurrentContainer()
        {
            return Enum.TryParse(Form.Av1anContainerBox.GetText().Trim(), true, out Containers.Container c) ? c : Containers.Container.Mkv;
        }

        /// <summary> Whether the chosen container is MP4, which the muxing below has to work around. </summary>
        public static bool IsMp4Output()
        {
            return GetCurrentContainer() == Containers.Container.Mp4;
        }

        /// <summary> Whether the chosen container is WebM, which takes only a subset of what Matroska does. </summary>
        public static bool IsWebmOutput()
        {
            return GetCurrentContainer() == Containers.Container.Webm;
        }

        /// <summary>
        /// Why the source's audio cannot be copied into the chosen container, or "" if it can. av1an
        /// muxes the audio into an intermediate audio.mkv and later copies that file's streams into the
        /// output, so a copied track has to be legal in both. Matroska takes everything, which leaves
        /// the output container to object - and it objects at the final mux, once the encode has
        /// already run.
        /// </summary>
        public static string GetCopiedAudioProblem(CodecUtils.AudioCodec aCodec, Containers.Container container, MediaFile file)
        {
            if (aCodec != CodecUtils.AudioCodec.CopyAudio || file == null)
                return "";

            var unsupported = file.AudioStreams.Where(x => !Containers.CanCopyAudioCodec(container, x.Codec)).ToList();

            if (unsupported.Count < 1)
                return "";

            string name = container.ToString().Upper();
            string codecs = string.Join(", ", unsupported.Select(x => Aliases.GetNicerCodecName(x.Codec ?? "")).Distinct());
            string accepted = string.Join(", ", Containers.GetSupportedAudioCodecs(container).Select(x => CodecUtils.GetCodec(x).FriendlyName));

            return $"{name} cannot store {codecs} audio, so it cannot be copied into it.\n\n" +
                $"{name} accepts {accepted}.\n\n" +
                $"Pick one of those to re-encode the audio, or a different container.";
        }

        /// <summary>
        /// The stream-selection half of av1an's -a parameters. These never reach the muxer that writes
        /// the output file: av1an builds an intermediate audio.mkv out of them and later copies its
        /// streams into whichever container was asked for, so everything named here has to be legal in
        /// Matroska *and* in that container.
        /// </summary>
        public static string BuildMuxArgs(bool copySubs, bool copyData, bool copyAttachments, bool mp4, bool webm, IEnumerable<int> bitmapSubIndices = null)
        {
            // No subtitle codec survives both hops into MP4 - Matroska refuses mov_text, and MP4 refuses
            // both SRT and WebVTT - so for MP4 they have to go. Asking anyway fails the audio step
            // outright, and the encode then finishes with no sound either.
            string subs = copySubs && !mp4 ? (webm ? "-c:s webvtt" : "-c:s copy") : "-sn"; // WebM takes WebVTT and nothing else

            // WebVTT is text, and ffmpeg refuses to turn an image-based track into text, so naming one
            // for that conversion fails the audio step exactly as mov_text did - taking the audio with
            // it. WebM cannot store them in any form, so they are dropped instead of converted.
            IEnumerable<int> dropped = webm && copySubs ? (bitmapSubIndices ?? Enumerable.Empty<int>()) : Enumerable.Empty<int>();
            string bitmapSubs = string.Join(" ", dropped.Select(i => $"-map -0:s:{i}"));

            // Always dropped, whatever the checkbox says. Everything here passes through an
            // intermediate audio.mkv, and Matroska stores no data streams at all - "Only audio, video,
            // and subtitles are supported for Matroska" - so asking to keep them fails that step and
            // takes the audio down with it. The caller says so when the box was ticked.
            string data = "-dn";
            // av1an's own '-map 0' has already taken the attachments, so they only need naming here in
            // order to drop them - mapping them a second time writes every font twice.
            string attachments = copyAttachments && !mp4 && !webm ? "" : "-map -0:t?";
            return string.Join(" ", new[] { subs, bitmapSubs, data, attachments }.Where(x => x.IsNotEmpty()));
        }

        /// <summary> Positions of the image-based tracks among a file's subtitle streams, for "-0:s:N". </summary>
        public static List<int> GetBitmapSubtitleIndices(MediaFile file)
        {
            return GetSubtitleIndices(file, true);
        }

        /// <summary> Positions of the text-based tracks among a file's subtitle streams, for "1:s:N". </summary>
        public static List<int> GetTextSubtitleIndices(MediaFile file)
        {
            return GetSubtitleIndices(file, false);
        }

        private static List<int> GetSubtitleIndices(MediaFile file, bool bitmap)
        {
            var indices = new List<int>();

            for (int i = 0; file != null && i < file.SubtitleStreams.Count; i++)
            {
                if (file.SubtitleStreams[i].Bitmap == bitmap)
                    indices.Add(i);
            }

            return indices;
        }

        public static string GetConcatMethodArgs(CodecUtils.Av1anCodec vCodec)
        {
            string chosen = GetConcatMethodName();

            // mkvmerge writes Matroska and nothing else. Pointed at an .mp4 it does not refuse - it
            // writes a Matroska file under that name - so MP4 goes out through ffmpeg whatever the
            // dropdown says, rather than producing a file that lies about what it is. Said out loud
            // for the same reason the H.265 correction below is: a setting that was picked and then
            // overruled is one the log should name, and this one used to happen in silence.
            if (IsMp4Output())
            {
                if (chosen != "ffmpeg")
                    Logger.Log($"Note: MP4 can only be concatenated by ffmpeg, so that is being used rather than {chosen}.");

                return "-c ffmpeg";
            }

            // ffmpeg cannot join raw HEVC chunks back up, and av1an refuses the pairing outright rather
            // than discovering it at the end. Correcting the one setting beats failing the whole encode.
            if (vCodec == CodecUtils.Av1anCodec.X265 && chosen != "mkvmerge")
            {
                Logger.Log($"Note: H.265 can only be concatenated by mkvmerge, so that is being used rather than {chosen}.");
                return "-c mkvmerge";
            }

            return $"-c {chosen}";
        }

        /// <summary>
        /// av1an's name for the selected concatenation method, which the dropdown's entries are
        /// spelled as. A box with nothing selected falls back to the default rather than to "":
        /// the other two settings on this tab both floor their index, where an empty answer here
        /// would put a bare '-c' on the command line and av1an refuses the whole command over it.
        /// </summary>
        public static string GetConcatMethodName()
        {
            string chosen = Form.Av1anOptsConcatModeBox.GetText().ToLower().Trim();
            return chosen.IsEmpty() ? "mkvmerge" : chosen;
        }

        /// <summary> av1an's ChunkOrdering values, in the same order as the dropdown items. </summary>
        private static readonly string[] ChunkOrders = { "long-to-short", "short-to-long", "sequential", "random" };

        public static string GetChunkOrderArgs()
        {
            // Mapped by index instead of parsed from the label - av1an rejects anything outside this list
            return $"--chunk-order {ChunkOrders[Form.Av1anOptsChunkOrderBox.SelectedIndex.Clamp(0, ChunkOrders.Length - 1)]}";
        }

        // There is no GetThreadAffArgs here any more. It built "--set-thread-affinity N" out of the
        // Threads per Worker box and nothing ever called it, so the flag has never gone out - and it
        // is not the flag that box means. Thread affinity *pins* each worker to N cores, which is a
        // different setting with a different failure mode: on a machine whose core count is not a
        // multiple of the pin size it leaves cores idle, and it stops the OS moving a worker off a
        // core that another process wants. What the box means is the encoder's own thread count,
        // which is what GetVideoArgsFromUi's "threads" entry carries into each encoder's arguments.

        #endregion

        public static void ValidateContainer()
        {
            if (Form.Av1anContainerBox.SelectedIndex < 0)
                return;

            ValidatePath();
        }

        public static void ValidatePath()
        {
            if (TrackList.current == null)
                return;

            if (File.Exists(UiData.GetOutPath()))
                Form.Av1anOutputPathBox.Text = Path.ChangeExtension(IoUtils.GetAvailableFilename(UiData.GetOutPath()), null);
        }

        public static Fraction GetUiFps()
        {
            return MiscUtils.GetFpsFromString(Form.Av1anFpsBox.Text);
        }

        /// <summary>
        /// The unfinished encodes sitting in the av1an temp folder, the one that ran most recently
        /// first. Nothing removes these on its own - a cancelled or crashed encode keeps its folder so
        /// it can be resumed - so this is also what they cost in disk.
        /// </summary>
        public static List<Av1anFolderEntry> GetResumableEncodes()
        {
            List<Av1anFolderEntry> entries = new List<Av1anFolderEntry>();

            try
            {
                foreach (DirectoryInfo dir in new DirectoryInfo(Paths.GetAv1anTempPath()).GetDirectories())
                {
                    // Read one at a time. A single unreadable folder used to take the whole list down
                    // with it, and every other encode then went unmentioned and unresumable.
                    try
                    {
                        entries.Add(new Av1anFolderEntry(dir.FullName));
                    }
                    catch (Exception e)
                    {
                        Logger.Log($"Skipping av1an temp folder '{dir.Name}': {e.Message}", true);
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Log($"Failed to look for resumable encodes: {e.Message}", true);
            }

            return entries.OrderBy(x => x.TimeSinceLastRun.TotalMilliseconds).ToList();
        }

        /// <summary>
        /// Puts the number of resumable encodes on the Resume button. Without it the button reads the
        /// same whether there are none or ten, and opening it is the only way anything says so - an
        /// encode interrupted before a restart is otherwise not mentioned again.
        /// </summary>
        public static void RefreshResumeButton(bool logIfAny = false)
        {
            List<Av1anFolderEntry> pending = GetResumableEncodes();
            Form.Av1anResumeBtn.Content = pending.Count > 0 ? $"Resume… ({pending.Count})" : "Resume…";
            Form.Av1anClearTempBtn.IsEnabled = pending.Count > 0; // Nothing to clear reads better than a dialog saying so

            if (!logIfAny || pending.Count < 1)
                return;

            long bytes = pending.Sum(x => x.ChunkFiles.Sum(f => f.Length));
            Logger.Log($"{pending.Count} unfinished av1an encode{(pending.Count == 1 ? "" : "s")} can be resumed " +
                $"({FormatUtils.Bytes(bytes)} of chunks) - see Resume in the AV1AN tab.");
        }

        /// <summary>
        /// Decides what becomes of an encode's temp folder, by how the encode ended.
        /// <para/>
        /// Finished: deleted without asking. Stopped by an error: kept, since that was not a decision
        /// anyone made. Stopped by the user: asked about, because stopping an encode and abandoning it
        /// are different intentions and the folder is the whole of what Resume works from.
        /// <para/>
        /// 'succeeded' has to mean av1an actually finished and wrote the file. Taking "was not
        /// canceled" for success threw away every finished chunk whenever av1an died on its own - a
        /// crashed encoder, a full disk, a parameter it would not take - which is exactly when
        /// resuming is worth the most.
        /// <para/>
        /// A run that stopped before av1an wrote anything is the other end of that, and is deleted:
        /// there is nothing in the folder to carry on from, so keeping it only puts an entry in the
        /// Resume list offering to continue an encode that never started.
        /// </summary>
        public static async Task HandleTempFolder(string dir, bool succeeded, bool canceledByUser)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                return;

            if (succeeded) // Ran to the end - nothing left to resume from, and asking would be the same answer every time
            {
                DeleteTempFolder(dir);
                return;
            }

            // av1an refusing the command, or not being there to run it, leaves the folder exactly as
            // this run created it. Resuming from that repeats every step from the first, so the offer
            // is worth nothing and the count on the Resume button is worth less than nothing - a batch
            // of twelve that failed the same way used to leave twelve of these, and take a trimmed
            // copy of each input with them.
            if (!HasAnyContent(dir))
            {
                Logger.Log($"Nothing was written to '{Path.GetFileName(dir)}', so there is nothing to resume from - removing it.", true);
                DeleteTempFolder(dir);
                return;
            }

            // Stopped by a bad setting or an error rather than by the user. Not their decision to make,
            // and they may well want to fix whatever it was and carry on from the chunks already done.
            if (!canceledByUser)
            {
                Logger.Log(DescribeKeptFolder(dir));
                return;
            }

            // Stopping an encode is not the same as abandoning it, and only the user knows which they
            // meant. The chunks are worth however long they took, so this one is worth asking about.
            string size = FormatUtils.Bytes(IoUtils.GetDirSize(dir, true));
            int chunks = CountEncodedChunks(dir);
            string msg = $"This encode has been canceled.\n\nKeep its temporary files so it can be resumed later? " +
                $"They are {size} and hold {chunks} encoded video chunk{(chunks == 1 ? "" : "s")}.\n\n" +
                $"Choosing No deletes them, and the encode would have to start over from the beginning.";

            var result = await UiUtils.ShowMessageBox(msg, "Resume this encode later?", UiUtils.MessageButtons.YesNo);

            if (result == UiUtils.DialogResult.Yes)
            {
                Logger.Log(DescribeKeptFolder(dir));
                return;
            }

            DeleteTempFolder(dir);
        }

        /// <summary>
        /// Whether av1an put anything in the folder at all. Directories count as much as files: an
        /// empty 'encode' is av1an having got as far as laying out its temp folder, and everything
        /// from that point on - the scene detection, the audio - is work this cannot see but a resume
        /// would still skip. Only the case where av1an wrote literally nothing is called nothing.
        /// </summary>
        private static bool HasAnyContent(string dir)
        {
            try
            {
                return Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories).Any();
            }
            catch (Exception e)
            {
                Logger.Log($"Could not inspect the temp folder '{Path.GetFileName(dir)}': {e.Message}", true);
                return true; // Not being able to tell is not a reason to delete an encode's chunks
            }
        }

        /// <summary> Encoded video chunks in a temp folder. Sub-kilobyte files in there are av1an's
        /// own bookkeeping rather than video. </summary>
        private static int CountEncodedChunks(string dir)
        {
            return IoUtils.GetFileInfosSorted(Path.Combine(dir, "encode"), false, "*.*").Count(x => x.Length >= 1024);
        }

        /// <summary>
        /// What a kept folder is actually worth, since "so this encode can be resumed" over a folder
        /// with no finished chunks promises more than resuming it delivers.
        /// </summary>
        private static string DescribeKeptFolder(string dir)
        {
            string size = FormatUtils.Bytes(IoUtils.GetDirSize(dir, true));
            int chunks = CountEncodedChunks(dir);

            if (chunks < 1)
                return $"Keeping the temp folder '{Path.GetFileName(dir)}' ({size}) - no video chunks were finished, " +
                    $"so resuming it would repeat most of the work.";

            return $"Keeping the temp folder so this encode can be resumed ({chunks} chunk{(chunks == 1 ? "" : "s")}, {size} in '{Path.GetFileName(dir)}').";
        }

        /// <summary> Removes a temp folder along with the resume arguments, and any prepared input,
        /// saved beside it. </summary>
        public static void DeleteTempFolder(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir))
                return;

            if (Directory.Exists(dir))
            {
                Logger.Log($"Deleting temp folder '{Path.GetFileName(dir)}' ({FormatUtils.Bytes(IoUtils.GetDirSize(dir, true))}).", true);
                IoUtils.TryDeleteIfExists(dir);
            }

            IoUtils.DeleteIfExists(dir + ".json");

            foreach (string path in GetPreparedInputs(dir))
                IoUtils.DeleteIfExists(path);
        }

        /// <summary>
        /// Where a run keeps the copy of its input that av1an is actually given - a trimmed one, a
        /// deinterlaced one, or a trimmed one that was then deinterlaced: beside the temp folder, the
        /// way the resume arguments are, rather than inside it. av1an empties its own temp folder at
        /// startup whenever it is not resuming, so the one file its command has to be able to read is
        /// the one file that cannot live in there.
        /// </summary>
        public static string GetTrimmedInputPath(string tempDir, string ext)
        {
            return $"{tempDir}.trim{ext}";
        }

        /// <summary> Where the QTGMC pass writes the progressive file av1an is given. Always Matroska:
        /// it holds one frame per field at any rate, and every track the pass copies over. </summary>
        public static string GetDeinterlacedInputPath(string tempDir)
        {
            return $"{tempDir}.deint.mkv";
        }

        /// <summary> The denoised copy the Measured grain mode encodes, and the table measured against it.
        /// Beside the temp folder like the two inputs above, and for the same reasons: av1an empties its
        /// own temp folder at startup, and a resume has to be able to find both rather than spend the
        /// hours again. </summary>
        public static string GetDenoisedInputPath(string tempDir)
        {
            return $"{tempDir}.denoised.mkv";
        }

        public static string GetGrainTablePath(string tempDir)
        {
            return $"{tempDir}.grain.tbl";
        }

        /// <summary> Whatever this temp folder's run wrote beside it to feed av1an, in any container.
        /// Both suffixes, because a trimmed *and* deinterlaced run leaves one of each. </summary>
        private static IEnumerable<string> GetPreparedInputs(string tempDir)
        {
            try
            {
                string name = Path.GetFileName(tempDir);
                return Directory.EnumerateFiles(Path.GetDirectoryName(tempDir), $"{name}.*")
                    .Where(f => Path.GetFileName(f).StartsWith($"{name}.trim.") || Path.GetFileName(f).StartsWith($"{name}.deint.")).ToList();
            }
            catch (Exception e)
            {
                Logger.Log($"Could not look for a prepared input beside '{Path.GetFileName(tempDir)}': {e.Message}", true);
                return Enumerable.Empty<string>();
            }
        }

        /// <summary> The selected quality mode. The dropdown is filled from the enum, so index is value. </summary>
        public static Av1an.QualityMode GetCurrentQualityMode()
        {
            return (Av1an.QualityMode)Math.Max(0, Form.Av1anQualModeBox.SelectedIndex);
        }

        /// <summary>
        /// Whether a target quality mode is selected rather than a fixed CRF - the modes where
        /// av1an's probing chooses the quantiser and the quality box holds a metric score.
        /// </summary>
        public static bool IsUsingTargetQuality()
        {
            return GetCurrentQualityMode() != Av1an.QualityMode.Crf;
        }
    }
}
