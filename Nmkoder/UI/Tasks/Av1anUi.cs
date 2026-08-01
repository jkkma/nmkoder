using Newtonsoft.Json;
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
        /// <see cref="Av1an.Run"/>. Always an ffmpeg filter here - see
        /// <see cref="DeinterlaceUi.Av1anQtgmcProblem"/> for why QTGMC is not on offer. </summary>
        public static DeinterlacePlan CurrentDeinterlace = new DeinterlacePlan();

        public static void Init()
        {
            // Load video codecs
            Form.Av1anCodecBox.SetItems(Enum.GetValues<CodecUtils.Av1anCodec>().Select(c => (object)CodecUtils.GetCodec(c).FriendlyName));
            ConfigParser.LoadComboxIndex(Form.Av1anCodecBox);

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
            bool av1 = c == CodecUtils.Av1anCodec.AomAv1 || c == CodecUtils.Av1anCodec.SvtAv1;
            Form.Av1anGrainSynthStrengthUpDown.IsEnabled = Form.Av1anGrainSynthDenoiseBox.IsEnabled = av1; // Only AV1 has grain synth
            LoadQualityLevel(enc);
            LoadPresets(enc);
            LoadColorFormats(enc);
            LoadAdvancedArgsGrid(enc);
        }

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
        /// The advanced grid's rows as encoder arguments, skipping every row the user has not filled
        /// in. The grid is preloaded with an encoder's documented parameters so they can be read and
        /// edited in place, so most rows are blank most of the time - passing those would put a
        /// valueless flag on the command line and fail the encode before it started.
        /// </summary>
        public static string BuildAdvancedArgs(IEnumerable<EncoderArgRow> rows)
        {
            return string.Join(" ", rows
                .Where(x => x.Argument.IsNotEmpty() && x.Value.IsNotEmpty())
                .Select(x => $"--{x.Argument.Trim().TrimStart('-')}={x.Value.Trim()}"));
        }

        /// <summary>
        /// The parameters documented for an encoder, as [argument, value, description, category,
        /// details, examples] rows. Values come through blank: the list is there to be read and
        /// filled in, and only rows with a value reach the command line. An encoder with no file
        /// simply has nothing to show. The category names the tab the row appears under; a row
        /// without one - the format before categories existed - is grouped as "Other" rather than
        /// dropped. Details and examples feed the right-click window and may be absent.
        /// </summary>
        public static List<EncoderArgRow> ReadEncoderArgRows(IEncoder enc)
        {
            List<EncoderArgRow> rows = new List<EncoderArgRow>();
            string jsonPath = Path.Combine(Paths.GetBinPath(), "av1an", "encoderArgs", enc.Name + ".json");

            if (!File.Exists(jsonPath))
                return rows;

            List<string[]> args;

            try
            {
                args = JsonConvert.DeserializeObject<List<string[]>>(File.ReadAllText(jsonPath));
            }
            catch (Exception e)
            {
                Logger.Log($"Error loading advanced arg JSON: {e.Message}");
                args = new List<string[]>();
            }

            foreach (string[] arg in args ?? new List<string[]>())
            {
                if (arg.Length >= 3)
                {
                    rows.Add(new EncoderArgRow(arg[0], arg[1], arg[2], arg.Length >= 4 ? arg[3] : "",
                        arg.Length >= 5 ? arg[4] : "", arg.Length >= 6 ? arg[5] : ""));
                }
            }

            return rows;
        }

        public static void LoadAdvancedArgsGrid(IEncoder enc)
        {
            Form.Av1anArgRows.Clear();
            ReadSavedArgs().TryGetValue(enc.Name, out Dictionary<string, string> saved);

            foreach (EncoderArgRow row in ReadEncoderArgRows(enc))
            {
                if (saved != null && saved.TryGetValue(row.Argument, out string value))
                    row.Value = value;

                Form.Av1anArgRows.Add(row);
            }

            Form.LoadAv1anArgCategoryTabs();
            Form.LoadAv1anArgPresets(enc.Name);
        }

        /// <summary>
        /// Values typed into the advanced argument grid, kept per encoder. The rows themselves are
        /// rebuilt from the encoder's JSON every time it is selected, which is what used to throw
        /// the values away - on a restart and on every switch between encoders.
        /// </summary>
        public static void SaveAdvancedArgs(IEncoder enc)
        {
            if (enc == null)
                return;

            Dictionary<string, Dictionary<string, string>> all = ReadSavedArgs();

            // Blank rows are the normal state, and storing them would grow the config with nothing
            Dictionary<string, string> filled = Form.Av1anArgRows
                .Where(r => r.Argument.IsNotEmpty() && r.Value.IsNotEmpty())
                .GroupBy(r => r.Argument.Trim())
                .ToDictionary(g => g.Key, g => g.Last().Value.Trim());

            if (filled.Count > 0)
                all[enc.Name] = filled;
            else
                all.Remove(enc.Name);

            Config.Set(Config.Key.Av1anEncoderArgs, JsonConvert.SerializeObject(all));
        }

        private static Dictionary<string, Dictionary<string, string>> ReadSavedArgs()
        {
            try
            {
                string json = Config.Get(Config.Key.Av1anEncoderArgs);

                if (json.IsEmpty())
                    return new Dictionary<string, Dictionary<string, string>>();

                return JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string>>>(json)
                    ?? new Dictionary<string, Dictionary<string, string>>();
            }
            catch (Exception e)
            {
                // A hand-edited or truncated entry should cost the saved values, not the whole tab
                Logger.Log($"Failed to read saved encoder arguments: {e.Message}", true);
                return new Dictionary<string, Dictionary<string, string>>();
            }
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

            dict.Add("grainSynthStrength", Form.Av1anGrainSynthStrengthUpDown.Value.AsInt().ToString());
            dict.Add("grainSynthDenoise", (Form.Av1anGrainSynthDenoiseBox.IsChecked == true).ToString());
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

            if (fps.GetFloat() > 0.01f && sourceRate.GetFloat() != fps.GetFloat()) // Framerate Resampling
                frame.FpsFilter = $"fps=fps={fps}";

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

            // Padding an odd source to mod 2 is what stops it reaching an encoder that will not take one.
            // A resize makes it redundant - every size computed below is a multiple of 2 - and dropping it
            // also takes away its one sharp edge, which is that it runs *ahead* of a crop whose rectangle
            // was measured against the unpadded frame.
            frame.Padding = !frame.Resizing && !frame.Desqueezing
                && ((vs.Resolution.Width % 2 != 0) || (vs.Resolution.Height % 2 != 0));

            string cropMode = Form.Av1anCropBox.GetText().ToLower();

            if (cropMode.Contains("manual") && CurrentCrop != null) // Manual Crop
            {
                frame.CropFilters.Add($"crop={CurrentCrop.GetFilterArgs(vs.Resolution)}");
                frame.ScaleInput = new Size(CurrentCrop.GetCroppedWidth(vs.Resolution), CurrentCrop.GetCroppedHeight(vs.Resolution));
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

            frame.Encoded = frame.ScaleInput;

            if (frame.Resizing && !CurrentResize.IsNoOp(frame.ScaleInput, vs.Sar))
                frame.Encoded = OrKeep(CurrentResize.Compute(frame.ScaleInput, vs.Sar), frame.ScaleInput);
            else if (frame.Desqueezing)
                frame.Encoded = OrKeep(ResizeConfig.DesqueezeOnly().Compute(frame.ScaleInput, vs.Sar), frame.ScaleInput);
            else if (frame.Padding && frame.CropFilters.Count < 1)
                // The pad runs ahead of the crop, so it only decides the frame's size when there is no
                // crop behind it to take a rectangle of its own out of the padded picture.
                frame.Encoded = new Size(RoundUpToEven(frame.ScaleInput.Width), RoundUpToEven(frame.ScaleInput.Height));

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
        /// </summary>
        public static string GetVideoFilterArgs(Av1anFrame frame, CodecArgs codecArgs = null)
        {
            List<string> filters = new List<string>();

            if (frame == null || frame.Source.IsEmpty || TrackList.current.File.VideoStreams.Count < 1)
                return "";

            // First in the chain, because the crop and the resize below it are both measured against a
            // whole frame rather than against a pair of fields.
            string deinterlace = CurrentDeinterlace.GetFfmpegFilter();

            if (deinterlace.IsNotEmpty())
                filters.Add(deinterlace);

            if (codecArgs != null && codecArgs.ForcedFilters != null)
                filters.AddRange(codecArgs.ForcedFilters);

            if (frame.ResamplesFrameRate) // Check Filter: Framerate Resampling
                filters.Add(frame.FpsFilter);

            if (frame.Padding) // Check Filter: Pad for mod2
                filters.Add(FfmpegUtils.GetPadFilter(2));

            filters.AddRange(frame.CropFilters); // Check Filter: Manual Crop / Autocrop

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
                    Logger.Log($"De-squeezing {frame.ScaleInput.Width}x{frame.ScaleInput.Height} ({frame.Sar.Width}:{frame.Sar.Height} pixels) to " +
                        $"{frame.Encoded.Width}x{frame.Encoded.Height} - av1an's encoders take bare frames and no aspect flag, so the shape " +
                        $"has to be baked into the pixels to survive. Configure a resize to control the size.");
                }
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
            Size result = frame.Encoded;
            string source = AspectRatio.IsAnamorphic(frame.Sar) && CurrentResize.CorrectAspect
                ? $"{frame.ScaleInput.Width}x{frame.ScaleInput.Height} at {frame.Sar.Width}:{frame.Sar.Height} pixels"
                : $"{frame.ScaleInput.Width}x{frame.ScaleInput.Height}";

            Logger.Log($"Resizing {source} to {result.Width}x{result.Height} ({AspectRatio.Describe(result.Width, result.Height)}).");

            // Not clamped to, only mentioned: silently growing a frame to something the user did not ask
            // for is worse than an encoder saying no. SVT-AV1 refuses anything under 64 in either
            // direction, x265 anything under 16; the other encoders take whatever they are given.
            if (result.Width < 64 || result.Height < 64)
                Logger.Log($"Warning: {result.Width}x{result.Height} is very small - SVT-AV1 will not encode a frame under 64 pixels on either side.");
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
        }

        /// <summary>
        /// Acts on the user picking an entry - and only on the user: refilling the list raises the same
        /// event, and acting on that would read the freshly selected entry back over what is being shown,
        /// as well as committing an unrelated save on every file load.
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
            Form.SaveAv1anEncodeSettings();
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

        #region Resize Persistence

        public static void SaveResizeConfig()
        {
            Config.Set(Config.Key.Av1anResize, JsonConvert.SerializeObject(CurrentResize));
        }

        /// <summary>
        /// Restores the saved resize, or - for a config written before this tab had one - translates
        /// whatever the two scale text boxes it replaced were holding.
        /// </summary>
        public static void LoadResizeConfig()
        {
            string json = Config.Get(Config.Key.Av1anResize);

            if (json.IsEmpty())
            {
                CurrentResize = MigrateOldScaleBoxes();
                // Written back straight away rather than left to the next save, so the translation - and
                // the log line that goes with the cases it cannot translate - happens exactly once.
                SaveResizeConfig();
                return;
            }

            try
            {
                CurrentResize = JsonConvert.DeserializeObject<ResizeConfig>(json) ?? new ResizeConfig();
            }
            catch (Exception e)
            {
                Logger.Log($"Failed to read the saved resize settings: {e.Message}", true);
                CurrentResize = new ResizeConfig();
            }
        }

        /// <summary>
        /// The old UI was two free-text boxes fed straight to an ffmpeg scale filter, so a saved value can
        /// be a number, a percentage, or an expression like "iw/2". The first two have an exact equivalent
        /// here and are carried over; an expression has none, and rather than approximate it the setting is
        /// dropped with a line saying where the same thing can still be written by hand.
        /// </summary>
        private static ResizeConfig MigrateOldScaleBoxes()
        {
            // Asked of the cache rather than of Config.Get, which writes a default for any key it does not
            // find - so a fresh install would be given two dead scale entries by the act of looking.
            string w = ReadOldScaleBox("Av1anScaleBoxW");
            string h = ReadOldScaleBox("Av1anScaleBoxH");

            if (w.IsEmpty() && h.IsEmpty())
                return new ResizeConfig();

            ResizeConfig migrated = TranslateOldScaleBoxes(w, h);

            if (migrated == null)
            {
                Logger.Log($"The saved AV1AN resize ('{w}' x '{h}') is an ffmpeg expression, which the new resize " +
                    $"tool has no equivalent for, so it has been cleared. A scale filter can still be written out in full on the Advanced tab.");
                return new ResizeConfig();
            }

            // Custom rather than a preset: the old boxes held a size, and no size is one of the targets in
            // the list. Left keyless it would select "No resizing" while a resize was in force.
            migrated.PresetKey = ResizePresets.CustomKey;
            return migrated;
        }

        /// <summary> The pair of old values as a resize, or null where there is no equivalent. </summary>
        private static ResizeConfig TranslateOldScaleBoxes(string w, string h)
        {
            if (w.EndsWith("%") || h.EndsWith("%"))
            {
                // Read as a float and rounded: GetInt strips the dot rather than the fraction, so "12.5%"
                // came out as 125% - an upscale, off a value that asked to shrink the picture eightfold.
                int percent = (w.EndsWith("%") ? w : h).TrimEnd('%').GetFloat().RoundToInt();
                return percent > 0 ? new ResizeConfig { Mode = ResizeMode.Percent, Percent = percent } : null;
            }

            if (IsPlainNumber(w) && IsPlainNumber(h))
                return new ResizeConfig { Mode = ResizeMode.Exact, Fill = ResizeFill.Stretch, TargetWidth = w.GetInt(), TargetHeight = h.GetInt() };

            if (IsPlainNumber(h) && IsAutoOrEmpty(w))
                return new ResizeConfig { Mode = ResizeMode.Height, TargetHeight = h.GetInt() };

            if (IsPlainNumber(w) && IsAutoOrEmpty(h))
                return new ResizeConfig { Mode = ResizeMode.Width, TargetWidth = w.GetInt() };

            return null;
        }

        private static string ReadOldScaleBox(string key)
        {
            return Config.cachedValues.TryGetValue(key, out string value) ? (value ?? "").Trim().ToLower() : "";
        }

        private static bool IsPlainNumber(string s)
        {
            return s.Length > 0 && s.All(char.IsDigit) && s.GetInt() > 0;
        }

        /// <summary> Blank, or one of the negative values ffmpeg reads as "work this one out from the other" -
        /// which is exactly what the Width and Height modes do. </summary>
        private static bool IsAutoOrEmpty(string s)
        {
            return s.IsEmpty() || s == "-1" || s == "-2";
        }

        #endregion

        #region Get Args

        public static string GetSplittingMethodArgs()
        {
            return $"--split-method {(Form.Av1anOptsSplitModeBox.SelectedIndex == 0 ? "none" : "av-scenechange")}";
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

            string value = Form.Av1anArgRows
                .Where(x => (x.Argument ?? "").Trim().TrimStart('-').ToLower() == "hbd-mds")
                .Select(x => (x.Value ?? "").Trim())
                .FirstOrDefault(x => x.IsNotEmpty()) ?? "";

            // 0 leaves the choice to the preset, so it is not asking the input for something it does
            // not have. 1 is all 10-bit, 2 hybrid - both want 10-bit samples.
            if (value != "1" && value != "2")
                return "";

            return $"Note: hbd-mds is set to {value}, which asks for {(value == "1" ? "all of" : "part of")} the mode decision " +
                $"at 10-bit, but SVT-AV1 only does that on a 10-bit input and the Color Format is 8-bit ({pixFmt}). " +
                $"Pick a 10 bit Color Format for it to have any effect.";
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
            // mkvmerge writes Matroska and nothing else. Pointed at an .mp4 it does not refuse - it
            // writes a Matroska file under that name - so MP4 goes out through ffmpeg whatever the
            // dropdown says, rather than producing a file that lies about what it is.
            if (IsMp4Output())
                return "-c ffmpeg";

            string chosen = GetConcatMethodName();

            // ffmpeg cannot join raw HEVC chunks back up, and av1an refuses the pairing outright rather
            // than discovering it at the end. Correcting the one setting beats failing the whole encode.
            if (vCodec == CodecUtils.Av1anCodec.X265 && chosen != "mkvmerge")
            {
                Logger.Log($"Note: H.265 can only be concatenated by mkvmerge, so that is being used rather than {chosen}.");
                return "-c mkvmerge";
            }

            return $"-c {chosen}";
        }

        /// <summary> av1an's name for the selected concatenation method. </summary>
        public static string GetConcatMethodName()
        {
            return Form.Av1anOptsConcatModeBox.GetText().ToLower().Trim();
        }

        /// <summary>
        /// Whether the IVF concatenator is selected. IVF is a bare video stream - no audio, no
        /// subtitles, and only VP8, VP9 or AV1 - so none of the containers on offer describes what it
        /// would actually write.
        /// </summary>
        public static bool IsUsingIvfConcat()
        {
            return GetConcatMethodName() == "ivf";
        }

        /// <summary> av1an's ChunkOrdering values, in the same order as the dropdown items. </summary>
        private static readonly string[] ChunkOrders = { "long-to-short", "short-to-long", "sequential", "random" };

        public static string GetChunkOrderArgs()
        {
            // Mapped by index instead of parsed from the label - av1an rejects anything outside this list
            return $"--chunk-order {ChunkOrders[Form.Av1anOptsChunkOrderBox.SelectedIndex.Clamp(0, ChunkOrders.Length - 1)]}";
        }

        public static string GetThreadAffArgs()
        {
            return $"--set-thread-affinity {Form.Av1anThreadsUpDown.Value.AsInt()}";
        }

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

        /// <summary> Removes a temp folder along with the resume arguments, and the trimmed input,
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

            foreach (string path in GetTrimmedInputs(dir))
                IoUtils.DeleteIfExists(path);
        }

        /// <summary>
        /// Where a trimmed run keeps the copy of its input that av1an is actually given: beside the
        /// temp folder, the way the resume arguments are, rather than inside it. av1an empties its
        /// own temp folder at startup whenever it is not resuming, so the one file its command has
        /// to be able to read is the one file that cannot live in there.
        /// </summary>
        public static string GetTrimmedInputPath(string tempDir, string ext)
        {
            return $"{tempDir}.trim{ext}";
        }

        /// <summary> Whatever GetTrimmedInputPath wrote for this temp folder, in any container. </summary>
        private static IEnumerable<string> GetTrimmedInputs(string tempDir)
        {
            try
            {
                return Directory.EnumerateFiles(Path.GetDirectoryName(tempDir), $"{Path.GetFileName(tempDir)}.trim.*").ToList();
            }
            catch (Exception e)
            {
                Logger.Log($"Could not look for a trimmed input beside '{Path.GetFileName(tempDir)}': {e.Message}", true);
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
