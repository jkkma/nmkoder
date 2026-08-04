using Nmkoder.Data;
using Nmkoder.Data.Codecs;
using Nmkoder.Data.Colors;
using Nmkoder.Data.Streams;
using Nmkoder.Data.Ui;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.Media;
using Nmkoder.OS;
using Nmkoder.Utils;
using Nmkoder.Views;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Stream = Nmkoder.Data.Streams.Stream;

namespace Nmkoder.UI.Tasks
{
    partial class QuickConvertUi : QuickConvert
    {
        private static MainWindow Form { get { return Program.MainWin; } }

        public static CropConfig CurrentCrop;
        public static TrimSettings CurrentTrim;

        /// <summary> The aspect ratio to pad out to with black bars, or an unset configuration for
        /// none. Never null; the dropdown is the only thing that moves it. </summary>
        public static BorderConfig CurrentBorders = new BorderConfig();

        /// <summary>
        /// The deinterlacing settled for the run whose arguments are being built. Resolved once in
        /// <see cref="QuickConvert.Run"/> rather than asked for again here: working it out can mean
        /// decoding a few hundred frames to see whether the source is interlaced at all, and the
        /// filter arguments are built several times over the course of one run.
        /// </summary>
        public static DeinterlacePlan CurrentDeinterlace = new DeinterlacePlan();

        /// <summary>
        /// Where the VapourSynth pipe sits among the '-i' arguments, or -1 when this run has none.
        /// QTGMC hands ffmpeg its frames on stdin, so the first video track is mapped from that input
        /// instead of from the file the rest of the tracks come out of.
        /// </summary>
        public static int DeinterlacePipeInput = -1;

        public static new void Init()
        {
            // Load video codecs
            Form.EncVidCodecsBox.SetItems(Enum.GetValues<CodecUtils.VideoCodec>().Select(c => (object)CodecUtils.GetCodec(c).FriendlyName));
            ConfigParser.LoadComboxIndex(Form.EncVidCodecsBox);

            // Load quality modes
            Form.EncQualModeBox.SetItems(Enum.GetValues<QualityMode>()
                .Select(qm => (object)qm.ToString().Replace("Crf", "CRF").Replace("TargetKbps", "Target Bitrate (Kbps)").Replace("TargetMbytes", "Target Filesize (MB)")), 0);

            // Load audio codecs
            Form.EncAudCodecBox.SetItems(Enum.GetValues<CodecUtils.AudioCodec>().Select(c => (object)CodecUtils.GetCodec(c).FriendlyName));
            ConfigParser.LoadComboxIndex(Form.EncAudCodecBox);

            // Load subtitle codecs
            Form.EncSubCodecBox.SetItems(Enum.GetValues<CodecUtils.SubtitleCodec>().Select(c => (object)CodecUtils.GetCodec(c).FriendlyName));
            ConfigParser.LoadComboxIndex(Form.EncSubCodecBox);

            // Load containers
            Form.FfmpegContainerBox.SetItems(Enum.GetNames<Containers.Container>().Select(c => (object)c.ToUpper()));
            ConfigParser.LoadComboxIndex(Form.FfmpegContainerBox);

            // Filled once - the entries name a shape rather than a size, so a new file changes
            // nothing in them; what this file comes out as is on the line underneath. The saved
            // index is what is restored, so entries may be appended to BorderPresets.All but not
            // reordered.
            // Filled on "No borders" and left there. Anything saved is restored later, by
            // LoadQuickConvertSettings with the rest of the persisted settings - through
            // RestoreIndexIfSaved, which leaves this default standing for a config that predates the
            // setting. Reading it here as well would create the key and defeat that.
            Form.EncBordersBox.SetItems(BorderPresets.All.Select(p => (object)p.Name), 0);

            Form.EncAudConfModeBox.SelectedIndex = 0;
        }

        public static void InitFile(string path = "")
        {
            try
            {
                if (path.IsNotEmpty())
                {
                    Form.FfmpegOutputBox.Text = UiData.GetDefaultOutPath(path);
                    ValidateContainer();
                }

                RefreshFileListRelatedOptions();
            }
            catch (Exception e)
            {
                Logger.Log($"Failed to initialize media file: {e.Message}\n{e.StackTrace}");
            }
        }

        #region Resize

        /// <summary>
        /// The resize to apply, held as intent rather than as pixels - the same object the AV1AN tab
        /// carries, and now built by the same dropdown and the same dialog. Never null.
        /// <para/>
        /// It replaced two free-text boxes handed straight to ffmpeg, and what those cost was not the
        /// typing: nothing downstream could say what frame the encoder would get, so the tile count fell
        /// back on the source's size, the black bars refused to run at all against a percentage, no
        /// frame-limit check was possible, and neither an upscale nor a dropped anamorphic shape had
        /// anywhere to be mentioned. They also mangled ffmpeg's own spellings - MiscUtils.GetScaleFilter
        /// rewrote every "w" in the box to "iw", so a typed "iw/2" went out as "iiw/2" and the encode
        /// failed on a syntax the box had invited.
        /// </summary>
        public static ResizeConfig CurrentResize = new ResizeConfig();

        /// <summary> Set while the dropdown is being refilled, because doing that raises the very
        /// SelectionChanged that would then read the new selection back over what is being shown. </summary>
        private static bool _loadingResizeBox;

        /// <summary>
        /// The file whose video this encode is about - the one the first ticked video track came from,
        /// which in Muxing Mode need not be the file the Track List is showing. Delegated so the geometry
        /// and the deinterlacer cannot pick different files; see
        /// <see cref="DeinterlaceUi.GetQuickConvertSourceFile"/>.
        /// </summary>
        public static MediaFile GetVideoSourceFile()
        {
            return DeinterlaceUi.GetQuickConvertSourceFile();
        }

        /// <summary> The video stream every geometry setting is measured against, or null when there is
        /// none. Read off <see cref="GetVideoSourceFile"/> rather than the loaded file, which in Muxing
        /// Mode is a different file with a different frame in it. </summary>
        public static VideoStream GetVideoSourceStream()
        {
            return GetVideoSourceFile()?.VideoStreams.FirstOrDefault();
        }

        /// <summary> The frame the resize is measured against: the source, less a manual crop and the
        /// mod-2 pad. An automatic crop is measured by sampling the video with ffmpeg, far too slow to
        /// do while filling a dropdown, so its bars are still in this number and the readout says so. </summary>
        public static Size GetResizeSourceSize()
        {
            VideoStream vs = GetVideoSourceStream();
            return vs == null ? Size.Empty : GetCroppedSourceSize(vs);
        }

        public static Size GetResizeSar()
        {
            return GetVideoSourceStream()?.Sar ?? Size.Empty;
        }

        /// <summary>
        /// Refills the dropdown, whose entries name what each target produces *for the loaded file* -
        /// "1080p (Full HD) — 1920x804" against a 2.39:1 film. Called whenever the frame those targets
        /// are measured against moves, which is a file being loaded or a crop being set, and pointedly
        /// not the user picking an entry: clearing a dropdown's own items from inside its
        /// SelectionChanged throws.
        /// </summary>
        public static void RefreshResizeBox()
        {
            Size storage = GetResizeSourceSize();
            Size sar = GetResizeSar();

            try
            {
                _loadingResizeBox = true;
                Form.EncResizeBox.SetItems(ResizePresets.All.Select(p => (object)ResizePresets.GetLabel(p, storage, sar)),
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

        /// <summary> The parts that change when the selection does: the button beside the box, and the
        /// line under it. Touches no collection, so it is safe to call from a SelectionChanged. </summary>
        public static void UpdateResizeReadout()
        {
            Form.EncResizeConfBtn.IsVisible = ResizePresets.Get(ResizePresets.IndexFor(CurrentResize)).Key == ResizePresets.CustomKey;
            Form.EncResizeInfoLabel.Text = GetResizeInfoText(GetResizeSourceSize(), GetResizeSar());

            // The bars are added to what the resize leaves, so everything that moves this readout moves
            // that one too. Called from here rather than beside each of those, as on the AV1AN tab.
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
                // Custom names no target of its own, so picking it changes nothing but where the
                // settings are edited: whatever was selected stays in force and becomes the dialog's
                // starting point.
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

        /// <summary> The line under the dropdown: the size, the ratio it works out to, and whichever
        /// caveat applies. </summary>
        private static string GetResizeInfoText(Size storage, Size sar)
        {
            if (CurrentResize == null || CurrentResize.Mode == ResizeMode.Disabled)
            {
                // Unlike the AV1AN tab, nothing here un-squeezes an anamorphic source when no resize is
                // configured, and that is right: ffmpeg carries the aspect flag through to the output,
                // where av1an's encoders are handed bare frames and cannot. Said out loud all the same,
                // because "its own resolution" describes the stored pixels rather than the picture.
                if (!storage.IsEmpty && AspectRatio.IsAnamorphic(sar))
                {
                    Size display = AspectRatio.GetDisplaySize(storage, sar);
                    return $"{storage.Width}x{storage.Height} · kept as it is, with its {sar.Width}:{sar.Height} pixel shape - " +
                        $"so it still plays as {AspectRatio.Describe(display.Width, display.Height)}";
                }

                return "The source is encoded at its own resolution.";
            }

            if (TrackList.current != null && GetVideoSourceStream() == null)
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

            if (Form.EncCropModeBox.GetText().ToLower().Contains("auto"))
                text += " · before autocrop; the final size is measured when the encode starts";

            return text;
        }

        /// <summary> Said per file at encode time, because that is the only place a batch shows which
        /// files were resized and by how much - the readout above describes the loaded one alone. </summary>
        private static void LogResize(Size scaleInput, Size sar, Size result, bool quiet)
        {
            string source = AspectRatio.IsAnamorphic(sar) && CurrentResize.CorrectAspect
                ? $"{scaleInput.Width}x{scaleInput.Height} ({sar.Width}:{sar.Height} pixels, so {AspectRatio.Describe(AspectRatio.GetDisplaySize(scaleInput, sar).Width, AspectRatio.GetDisplaySize(scaleInput, sar).Height)})"
                : $"{scaleInput.Width}x{scaleInput.Height}";

            string note = CurrentResize.IsUpscale(scaleInput, sar)
                ? " - larger than the source, which invents no detail and costs bitrate"
                : "";

            Logger.Log($"Resizing {source} to {result.Width}x{result.Height} " +
                $"({AspectRatio.Describe(result.Width, result.Height)}){note}.", quiet);
        }

        #endregion

        /// <summary> Acts on the user picking an entry of the borders dropdown. </summary>
        public static void BorderPresetSelected(int index)
        {
            if (index < 0)
                return;

            CurrentBorders = BorderPresets.Get(index).Build();
            UpdateBordersReadout();
        }

        /// <summary>
        /// The line under the borders dropdown: the frame the bars leave, the ratio that comes to,
        /// and which bars they are. Rewritten whenever anything it describes moves - the file, the
        /// crop, the resize boxes, the target itself - because every one of those changes the answer,
        /// and the whole point of the line is that letterbox against pillarbox is visible before the
        /// encode rather than after it.
        /// </summary>
        public static void UpdateBordersReadout()
        {
            Form.EncBordersInfoLabel.Text = GetBordersInfoText();
        }

        private static string GetBordersInfoText()
        {
            if (CurrentBorders == null || !CurrentBorders.IsSet)
                return "The frame keeps whatever shape it has.";

            VideoStream vs = GetVideoSourceStream();

            if (TrackList.current != null && vs == null)
                return "No video track - no bars will be added.";

            if (vs == null)
                return $"Padded out to {CurrentBorders.Label} - the bars are worked out per file when the encode starts.";

            (Size scaled, Size sar) = ResolveScaledFrame(GetCroppedSourceSize(vs), vs.Sar);
            BorderPad pad = CurrentBorders.Compute(scaled, sar);
            string text;

            if (!pad.Runs)
            {
                text = $"{scaled.Width}x{scaled.Height} · already {CurrentBorders.Label}, so no bars are added";
            }
            else if (ResizeConfig.ExceedsFrameLimit(pad.Frame))
            {
                return $"{pad.Frame.Width}x{pad.Frame.Height} · too large to encode - FFmpeg will not produce a frame of " +
                    $"{(double)pad.Frame.Width * pad.Frame.Height / 1_000_000d:0.#} megapixels, so nothing would be written";
            }
            else
            {
                text = $"{pad.Frame.Width}x{pad.Frame.Height} · {AspectRatio.Describe(pad.Frame.Width, pad.Frame.Height)} · " +
                    $"{pad.Describe()}, around an unscaled {scaled.Width}x{scaled.Height} picture";
            }

            // An automatic crop moves the ratio the bars are picked by, and measuring one means ten
            // ffmpeg probes - far too slow for a line rewritten on every keystroke - so its bars are
            // still in the frame above and this says so rather than naming a shape that will not be
            // the one. A manual crop is applied, being four numbers already to hand.
            if (Form.EncCropModeBox.GetText().ToLower().Contains("auto"))
                text += " · before autocrop; the bars are measured when the encode starts";

            return text;
        }

        /// <summary> The frame the scale filter is handed, as far as it can be known without running
        /// anything: the source, less a manual crop, rounded up the way the mod-2 pad in the chain
        /// rounds it - so the readout, the border check and the filters all measure the same frame.
        /// A crop's own result is already even, so that last step only reaches an odd source. </summary>
        private static Size GetCroppedSourceSize(VideoStream vs)
        {
            Size size = vs.Resolution;

            if (Form.EncCropModeBox.GetText().ToLower().Contains("manual") && CurrentCrop != null
                && CurrentCrop.IsSet && CurrentCrop.FitsInside(vs.Resolution))
            {
                size = CurrentCrop.GetCroppedSize(vs.Resolution);
            }

            return new Size(size.Width + (size.Width % 2), size.Height + (size.Height % 2));
        }

        public static void RefreshFileListRelatedOptions()
        {
            RefreshSubtitleBurnInBox();
            RefreshMetadataAndChapterOptions();
            // The resize targets are named for what they produce for *this* file, so the list is refilled
            // per file. It refreshes the borders readout on its way out, that frame being what the bars
            // are measured against.
            RefreshResizeBox();
            LoadMetadataGrid();
        }

        public static void VidEncoderSelected(int index)
        {
            if (index < 0)
                return;

            CodecUtils.VideoCodec c = (CodecUtils.VideoCodec)index;
            IEncoder enc = CodecUtils.GetCodec(c);
            Form.FfmpegContainerBox.IsVisible = !enc.IsFixedFormat; // Disable container selection for fixed formats (GIF, PNG etc)
            bool noRateControl = c == CodecUtils.VideoCodec.Gif || c == CodecUtils.VideoCodec.Png || c == CodecUtils.VideoCodec.Jpg;
            Form.EncVidQualityBox.IsEnabled = !enc.DoesNotEncode && enc.QMin != enc.QMax;
            Form.EncQualModeBox.IsEnabled = !enc.DoesNotEncode && !noRateControl;
            Form.EncVidPresetBox.IsEnabled = !enc.DoesNotEncode && enc.Presets != null && enc.Presets.Length > 0;
            Form.EncVidColorsBox.IsEnabled = !enc.DoesNotEncode && enc.ColorFormats != null && enc.ColorFormats.Count > 0;
            Form.EncVidFpsBox.IsEnabled = !enc.DoesNotEncode;
            Form.EncResizeBox.IsEnabled = Form.EncResizeConfBtn.IsEnabled = !enc.DoesNotEncode;
            Form.EncCropModeBox.IsEnabled = !enc.DoesNotEncode;
            // A stream copy builds no filter chain at all, so the bars would go nowhere
            Form.EncBordersBox.IsEnabled = !enc.DoesNotEncode;
            Form.QInfoLabel.Text = enc.QInfo;
            Form.PresetInfoLabel.Text = enc.PresetInfo;
            LoadQualityLevel(enc);
            LoadPresets(enc);
            LoadColorFormats(enc);
            ValidateContainer();
        }

        public static void AudEncoderSelected(int index)
        {
            if (index < 0)
                return;

            CodecUtils.AudioCodec c = (CodecUtils.AudioCodec)index;
            IEncoder enc = CodecUtils.GetCodec(c);

            Form.EncAudChannelsBox.IsEnabled = !(c == CodecUtils.AudioCodec.CopyAudio || c == CodecUtils.AudioCodec.StripAudio);
            Form.EncAudQualUpDown.IsEnabled = enc.QDefault >= 0 && Math.Abs(enc.QMin - enc.QMax) > 0;
            LoadAudBitrate(enc);
            ValidateContainer();
        }

        #region Load Video Options

        static void LoadQualityLevel(IEncoder enc)
        {
            // The spinner is the encoder's own scale whenever the rate control is CRF - and for the
            // fixed formats it always is, whatever the mode box says, because they have no rate control
            // and the box is disabled over whatever was last picked in it. Left out of this, a Target
            // Bitrate selected under H.264 kept its 10-100000 range across the switch to GIF, so the
            // palette size spinner sat on 1500 and reached ffmpeg as "palettegen=1500" - out of range,
            // and the encode dies before a frame is written.
            if (GetEffectiveQualityMode(enc) != QualityMode.Crf)
                return;

            Form.EncVidQualityBox.SetRange(enc.QMin, enc.QMax > 0 ? enc.QMax : 100);

            if (enc.QDefault >= 0)
                Form.EncVidQualityBox.SetValueClamped(enc.QDefault);
        }

        /// <summary>
        /// The rate control this encoder will actually be given, which is not always what the Quality
        /// Mode box shows: GIF, JPEG and PNG have none, so VidEncoderSelected disables that box - and a
        /// disabled box keeps whatever was last selected in it rather than going back to CRF.
        /// <para/>
        /// One answer, read by the spinner's range, by the spinner's default and by
        /// <see cref="QuickConvert.Run"/> when it decides whether to send a "q" or a bitrate. They
        /// disagreed, which is how the palette size and the JPEG quality came to be ignored.
        /// </summary>
        public static QualityMode GetEffectiveQualityMode(IEncoder enc = null)
        {
            enc = enc ?? CodecUtils.GetCodec(GetCurrentCodecV());

            return enc.IsFixedFormat ? QualityMode.Crf : (QualityMode)Math.Max(0, Form.EncQualModeBox.SelectedIndex);
        }

        static void LoadPresets(IEncoder enc)
        {
            Form.EncVidPresetBox.SetItems(
                (enc.Presets ?? new string[0]).Select(p => (object)p.ToTitleCase()),
                enc.Presets != null && enc.Presets.Length > 0 ? enc.PresetDefault : -1);
        }

        static void LoadColorFormats(IEncoder enc)
        {
            Form.EncVidColorsBox.SetItems(
                (enc.ColorFormats ?? new List<PixelFormats>()).Select(p => (object)PixFmtUtils.GetFormat(p).FriendlyName),
                enc.ColorFormats != null && enc.ColorFormats.Count > 0 ? enc.ColorFormatDefault : -1);
        }

        #endregion

        #region Load Audio Options

        static void LoadAudBitrate(IEncoder enc)
        {
            Form.EncAudQualUpDown.SetValueClamped(enc.QDefault >= 0 ? enc.QDefault : 0);
        }

        #endregion

        public static void ValidateContainer()
        {
            if (Form.FfmpegContainerBox.SelectedIndex < 0)
                return;

            ValidatePath();
        }

        /// <summary>
        /// Where this encode's output goes, with nothing created on the way: the file itself for an
        /// ordinary encode, and the folder for an image sequence.
        /// <para/>
        /// The extension comes from the container box, except for the three formats that write one of
        /// their own. GIF, JPEG and PNG hide that box and keep whatever was last selected in it, so
        /// taking its extension anyway named an animated GIF "clip.mkv" - a file no player opens by
        /// double-clicking and no other tool recognises. The sequence encoders were already excused
        /// from it; GIF, which writes a single file, was not.
        /// </summary>
        public static string GetOutputPath(IEncoder vCodec)
        {
            if (!vCodec.IsFixedFormat)
                return UiData.GetOutPath();

            string basePath = UiData.GetOutPath(includeExtension: false);
            return vCodec.IsSequence ? basePath : $"{basePath}.{GetFixedFormatExtension()}";
        }

        /// <summary> The first folder of this name that does not exist yet, numbered the way
        /// IoUtils.GetAvailableFilename numbers files - which only ever looks for a file, so a sequence's
        /// destination folder is stepped aside here instead. </summary>
        private static string GetAvailableFolder(string preferred)
        {
            string dir = Path.GetDirectoryName(preferred) ?? "";
            string name = Path.GetFileName(preferred);

            for (int i = 1; i <= 9999; i++)
            {
                string candidate = Path.Combine(dir, $"{name} ({i})");

                if (!Directory.Exists(candidate) && !File.Exists(candidate))
                    return candidate;
            }

            return preferred;
        }

        /// <summary> "gif", "jpeg", "png" - the format's own name, read off the codec dropdown's entry. </summary>
        public static string GetFixedFormatExtension()
        {
            return Form.EncVidCodecsBox.GetText().Split(' ')[0].ToLower();
        }

        /// <summary>
        /// Steps the output filename aside when something is already there, rather than letting ffmpeg
        /// overwrite it - it is always run with -y. Only does anything once RunningTask is set, since
        /// that is what makes UiData.GetOutPath resolve to a real path.
        /// <para/>
        /// Asked about the path the encode will actually write, which for the fixed formats is not the
        /// one the container box describes - a GIF was checked against a name ending in ".mkv", so the
        /// file it was about to overwrite was never the one it looked at.
        /// </summary>
        public static void ValidatePath()
        {
            if (TrackList.current == null)
                return;

            IEncoder vCodec = CodecUtils.GetCodec(GetCurrentCodecV());
            string taken = GetOutputPath(vCodec);

            if (taken.IsEmpty())
                return;

            // A sequence writes into a folder rather than to a file, and the check only ever looked for
            // a file - so re-exporting one dropped its frames in among the last run's, and where the new
            // export was shorter the old frames past its end stayed there. The result then passed every
            // check afterwards, the folder being full either way.
            if (vCodec.IsSequence ? !Directory.Exists(taken) : !File.Exists(taken))
                return;

            string free = vCodec.IsSequence ? GetAvailableFolder(taken) : IoUtils.GetAvailableFilename(taken);
            // The box holds the path without the extension, which is added back on the way out
            Form.FfmpegOutputBox.Text = vCodec.IsSequence ? free : Path.ChangeExtension(free, null);
            Logger.Log($"'{Path.GetFileName(taken)}' already exists - saving as '{Path.GetFileName(GetOutputPath(vCodec))}' instead.");
        }

        #region Get Current Codec

        public static CodecUtils.VideoCodec GetCurrentCodecV()
        {
            return (CodecUtils.VideoCodec)Math.Max(0, Form.EncVidCodecsBox.SelectedIndex);
        }

        public static CodecUtils.AudioCodec GetCurrentCodecA()
        {
            return (CodecUtils.AudioCodec)Math.Max(0, Form.EncAudCodecBox.SelectedIndex);
        }

        public static CodecUtils.SubtitleCodec GetCurrentCodecS()
        {
            return (CodecUtils.SubtitleCodec)Math.Max(0, Form.EncSubCodecBox.SelectedIndex);
        }

        public static Containers.Container GetCurrentContainer()
        {
            return (Containers.Container)Math.Max(0, Form.FfmpegContainerBox.SelectedIndex);
        }

        #endregion

        public static Dictionary<string, string> GetVideoArgsFromUi(bool vbr)
        {
            Dictionary<string, string> dict = new Dictionary<string, string>();

            if (vbr)
                dict.Add("bitrate", GetVideoKbps().ToString());
            else
                dict.Add("q", Form.EncVidQualityBox.Value.AsInt().ToString());

            dict.Add("preset", Form.EncVidPresetBox.GetText().ToLower());

            IEncoder enc = CodecUtils.GetCodec(GetCurrentCodecV());

            if (enc.ColorFormats != null && Form.EncVidColorsBox.SelectedIndex >= 0)
                dict.Add("pixFmt", PixFmtUtils.GetFormat(enc.ColorFormats[Form.EncVidColorsBox.SelectedIndex]).Name);

            dict.Add("qMode", Form.EncQualModeBox.SelectedIndex.ToString());

            return dict;
        }

        private static int GetVideoKbps()
        {
            QualityMode mode = (QualityMode)Math.Max(0, Form.EncQualModeBox.SelectedIndex);

            if (mode == QualityMode.TargetKbps)
                return Form.EncVidQualityBox.Value.AsInt();

            if (mode == QualityMode.TargetMbytes)
                return BitrateCalculation.GetTargetSizeKbps(CodecUtils.GetCodec(GetCurrentCodecA()));

            return 0;
        }

        public static Dictionary<string, string> GetAudioArgsFromUi()
        {
            Dictionary<string, string> dict = new Dictionary<string, string>();
            dict.Add("bitrate", Form.EncAudQualUpDown.Value.AsInt().ToString());
            dict.Add("ac", Form.EncAudChannelsBox.GetText().Split(' ')[0].Trim());
            return dict;
        }

        #region Load Media Info Into UI Where Needed

        public static void RefreshSubtitleBurnInBox()
        {
            if (RunTask.runningBatch)
                return;

            List<object> items = new List<object> { "Disabled" };

            if (TrackList.current != null && TrackList.current.File != null)
            {
                for (int i = 0; i < TrackList.current.File.SubtitleStreams.Count; i++)
                {
                    bool zeroIdx = Config.GetBool(Config.Key.UseZeroIndexedStreams);
                    var stream = TrackList.current.File.SubtitleStreams[i];

                    List<string> parts = new List<string>();
                    parts.Add($"#{(zeroIdx ? i : i + 1).ToString().PadLeft(2, '0')}");
                    parts.Add(stream.Language.ToUpper().Trunc(6));
                    parts.Add(stream.Title.Trunc(25));
                    items.Add($"{string.Join(" - ", parts.Where(x => !string.IsNullOrWhiteSpace(x)))} ({Aliases.GetNicerCodecName(stream.Codec).Trunc(12)})");
                }
            }

            Form.EncSubBurnBox.SetItemsIfChanged(items, 0);
        }

        #endregion

        #region Metadata Tab

        public static void RefreshMetadataAndChapterOptions()
        {
            if (RunTask.runningBatch)
                return;

            List<object> items = new List<object> { "None" };
            List<string> filePaths = FileList.Items.Select(x => x.File.SourcePath).ToList();

            if (RunTask.currentFileListMode == RunTask.FileListMode.Mux)
            {
                for (int i = 0; i < filePaths.Count; i++)
                {
                    string ext = Path.GetExtension(filePaths[i]);
                    items.Add($"#{i + 1} ({Path.GetFileNameWithoutExtension(filePaths[i]).Trunc(30 - ext.Length) + ext})");
                }
            }
            else
            {
                items.Add("#1 - Current Input File");
            }

            // Use 1st file if there is one, otherwise select "None"
            int select = items.Count > 1 ? 1 : 0;
            Form.EncMetaCopySource.SetItemsIfChanged(items, select);
            Form.EncMetaChapterSource.SetItemsIfChanged(items, select);
        }

        public static void LoadMetadataGrid()
        {
            FileListEntry curr = TrackList.current;

            if (RunTask.runningBatch || curr == null)
                return;

            // What is in the grid now, before it is rebuilt from the entries behind it. Those entries
            // are only written when something asks for them - at encode time - so anything typed into
            // the grid and not yet encoded was thrown away by the next rebuild, and ticking a track in
            // the Track List is a rebuild.
            SaveMetadata();
            Logger.Log($"Reloading metadata grid.", true);

            var rows = new List<MetadataRow>
            {
                new MetadataRow("Output File", curr.TitleEdited ?? curr.Title, curr.LanguageEdited ?? curr.Language)
            };

            foreach (StreamListEntry entry in TrackList.Items.Where(x => x.IsChecked))
            {
                string title = string.IsNullOrWhiteSpace(entry.TitleEdited) ? entry.Title : entry.TitleEdited;
                string lang = string.IsNullOrWhiteSpace(entry.LanguageEdited) ? entry.Language : entry.LanguageEdited;
                rows.Add(new MetadataRow(entry.GetString(false, false), title, lang, entry));
            }

            UiUtils.ReplaceAll(Form.MetadataRows, rows);
        }

        public static void SaveMetadata()
        {
            if (TrackList.current == null)
                return;

            Logger.Log($"Saving metadata.", true);

            foreach (MetadataRow row in Form.MetadataRows)
            {
                string title = row.Title?.Trim();
                string lang = row.Language?.Trim();

                if (row.Stream == null)
                {
                    TrackList.current.TitleEdited = title ?? "";
                    TrackList.current.LanguageEdited = lang ?? "";
                }
                else
                {
                    row.Stream.TitleEdited = title;
                    row.Stream.LanguageEdited = lang;
                }
            }
        }

        public static string GetMetadataArgs()
        {
            SaveMetadata();
            // The tracks that reach the output, not the ticked ones: every index below names an output
            // stream, and a container that cannot hold the data or attachment tracks has them dropped
            // from the maps. Counting the ticked tracks instead put every title and language after such
            // a track onto the wrong one - a font attachment ahead of the audio in a Matroska source
            // being the way in - while the last index of all matched no stream and was ignored in
            // silence, which is why this looked like it worked.
            List<StreamListEntry> checkedEntries = TrackList.GetMappedStreams();

            int metaFileIndex = (Form.EncMetaCopySource.SelectedIndex - 1).Clamp(-1, int.MaxValue);
            int chapFileIndex = (Form.EncMetaChapterSource.SelectedIndex - 1).Clamp(-1, int.MaxValue);

            #region Attachment Metadata (Only needed when doing -map_metadata -1)

            string argsAttachmentData = "";

            if (metaFileIndex == -1 && checkedEntries.Any(x => x.Stream.Type == Stream.StreamType.Attachment)) // When stripping all other metadata, we must still add attachment data as muxing attachments without filename doesn't seem to be possible
            {
                for (int i = 0; i < FileList.Items.Count; i++)
                {
                    MediaFile file = FileList.Items[i].File;

                    if (checkedEntries.Any(x => x.MediaFile.SourcePath == file.SourcePath && x.Stream.Type == Stream.StreamType.Attachment))
                        argsAttachmentData += $"-map_metadata:s:t {i}:s:t "; // Trailing space: two files' worth ran together into one unparseable argument
                }
            }

            #endregion

            #region Dispositions (Set default track)

            List<string> argListDispositions = new List<string>();

            int defaultAudio = Form.TrackListDefaultAudioBox.SelectedIndex;
            int defaultSubs = Form.TrackListDefaultSubsBox.SelectedIndex - 1;

            int relIdxAud = 0;
            int relIdxSub = 0;

            foreach (StreamListEntry entry in checkedEntries)
            {
                if (entry.Stream.Type == Stream.StreamType.Audio)
                {
                    argListDispositions.Add($"-disposition:a:{relIdxAud} {(defaultAudio == relIdxAud ? "default" : "0")}");
                    relIdxAud++;
                }

                if (entry.Stream.Type == Stream.StreamType.Subtitle)
                {
                    argListDispositions.Add($"-disposition:s:{relIdxSub} {(defaultSubs == relIdxSub ? "default" : "0")}");
                    relIdxSub++;
                }
            }

            string argsDispo = string.Join(" ", argListDispositions);

            #endregion

            #region Track Titles/Languages from Grid

            string argsMetaGrid = "";

            // The grid describes one loaded file's tracks, and a batch deliberately never fills it -
            // LoadMetadataGrid bails out for the same reason the track list is read-only there.
            // Applying it anyway meant every output in the queue was written with an empty title and
            // language on the file and on every track it carried.
            if (Form.EncMetaApplyGrid.IsChecked == true && !RunTask.runningBatch)
            {
                List<string> argListMetaGrid = new List<string>();

                argListMetaGrid.Add($"-metadata title=\"{TrackList.current.TitleEdited}\"");
                argListMetaGrid.Add($"-metadata language=\"{TrackList.current.LanguageEdited}\"");

                for (int i = 0; i < checkedEntries.Count; i++)
                {
                    StreamListEntry entry = checkedEntries[i];
                    argListMetaGrid.Add($"-metadata:s:{i} title=\"{entry.TitleEdited}\"");
                    argListMetaGrid.Add($"-metadata:s:{i} language=\"{entry.LanguageEdited}\"");
                }

                argsMetaGrid = string.Join(" ", argListMetaGrid);
            }

            #endregion

            return $"-map_metadata {metaFileIndex} " +
                $"{argsAttachmentData} " +
                $"-map_chapters {chapFileIndex} " +
                $"{argsMetaGrid} " +
                $"{argsDispo}";
        }

        #endregion

        #region Get Args

        public static string GetMuxingArgs()
        {
            return Containers.GetMuxingArgs(GetCurrentContainer());
        }

        /// <summary>
        /// Where a file sits among the '-i' arguments, which is what a filtergraph stream reference has
        /// to name. Batch mode passes a single input and it is always the current file; muxing passes
        /// the whole file list in order, and the file being encoded need not be the first of them.
        /// Resolved the same way <see cref="TrackList.GetMapArgs"/> resolves it for -map, so a filter
        /// and the maps alongside it cannot end up pointing at different inputs.
        /// </summary>
        private static int GetInputFileIndex(MediaFile file)
        {
            FileListEntry entry = FileList.Items.FirstOrDefault(x => x.File == file);

            if (RunTask.currentFileListMode == RunTask.FileListMode.Batch || entry == null)
                return 0;

            return FileList.Items.IndexOf(entry);
        }

        /// <summary>
        /// The track to burn in, as an index into <paramref name="file"/>'s subtitle streams, or -1 for
        /// none. The dropdown is filled from whichever file was loaded when it was last refreshed and a
        /// batch run deliberately leaves it alone - rebuilding it would reset the selection and turn
        /// burn-in off for the rest of the queue - so during a batch it describes the wrong file. The
        /// position it names is applied to each file in turn, and files without that many subtitle
        /// tracks are left alone rather than taken as far as an index that does not exist.
        /// </summary>
        private static int GetBurnInSubtitleIndex(MediaFile file, bool quiet)
        {
            int index = Form.EncSubBurnBox.SelectedIndex - 1; // Entry 0 is "Disabled"

            if (index < 0 || file == null)
                return -1;

            if (index >= file.SubtitleStreams.Count)
            {
                bool zeroIdx = Config.GetBool(Config.Key.UseZeroIndexedStreams);
                int shown = zeroIdx ? index : index + 1;
                string has = file.SubtitleStreams.Count == 1 ? "only has 1 subtitle track" : $"has {file.SubtitleStreams.Count} subtitle tracks";

                if (!quiet)
                    Logger.Log($"Not burning in subtitle track #{shown.ToString().PadLeft(2, '0')}: '{file.Name.Trunc(40)}' {has}.");

                return -1;
            }

            return index;
        }

        /// <summary>
        /// Seconds of seek applied to the input before the filter chain sees a frame, which is what
        /// restarts frame timestamps at zero. Only the keyframe trim mode seeks the input: the exact
        /// mode seeks the output, and the frame-number mode selects inside the chain, so both leave
        /// the source's own timestamps on the frames.
        /// </summary>
        private static float GetInputSeekOffsetSecs()
        {
            if (CurrentTrim == null || CurrentTrim.IsUnset || CurrentTrim.TrimMode != TrimSettings.Mode.TimeKeyframe)
                return 0f;

            return CurrentTrim.StartTime / 1000f;
        }

        /// <summary> The source's frame rate, which the frame-number trim mode is stated in and has to
        /// be converted out of. Zero where there is no video to ask, which leaves that mode emitting
        /// nothing rather than guessing at a rate. </summary>
        private static Fraction GetSourceRate()
        {
            return GetVideoSourceStream()?.Rate ?? Fraction.Zero;
        }

        public static string GetMiscInputArgs()
        {
            List<string> args = new List<string>();

            if (CurrentTrim != null && !CurrentTrim.IsUnset)
                args.Add(CurrentTrim.GetInputArgs(GetSourceRate()));

            return string.Join(" ", args.Where(x => x.IsNotEmpty()));
        }

        /// <summary>
        /// Whether the video chain hands on as many frames as it takes in, which decides whether the
        /// frame-number trim may pin its count with -frames:v - that option counts frames coming *out*
        /// of the chain, so anything multiplying them cuts the section short instead of ending it.
        /// <para/>
        /// Two things here do multiply: a deinterlacer emitting one frame per field, which a trim makes
        /// likely by ruling QTGMC out and falling back to bwdif, and a frame rate resample. A resample
        /// downwards divides rather than multiplies and could keep the count, but it is not worth the
        /// distinction: the duration governs either way and an exact count means nothing once the
        /// frames are not the source's own.
        /// </summary>
        private static bool ChainKeepsFrameCount()
        {
            if (CurrentDeinterlace != null && CurrentDeinterlace.DoublesFrameRate)
                return false;

            VideoStream vs = GetVideoSourceStream();
            Fraction fps = GetUiFps();

            if (vs == null || fps.GetFloat() <= 0.01f)
                return true;

            return MiscUtils.IsSameFrameRate(Deinterlace.GetEffectiveSourceRate(vs, CurrentDeinterlace), fps);
        }

        public static string GetMiscOutputArgs()
        {
            List<string> args = new List<string>();

            if (CurrentTrim != null && !CurrentTrim.IsUnset)
                args.Add(CurrentTrim.GetOutputArgs(GetSourceRate(), ChainKeepsFrameCount()));

            return string.Join(" ", args.Where(x => x.IsNotEmpty()));
        }

        /// <summary>
        /// The frame the encoder will be handed, or <see cref="Size.Empty"/> where it cannot be stated
        /// here - which leaves whoever asked to fall back on the source's own size.
        /// <para/>
        /// It exists for the tile count, which belongs to the frame being encoded and not to the file it
        /// came from: four tile columns are right for a 4K source and wrong for the 1080p it is being
        /// scaled to. Since the resize became a target rather than two boxes of free text, this is exact
        /// wherever the crop is - where before, a percentage or an ffmpeg expression left it unanswered
        /// and the encoder tiled the source's size.
        /// <para/>
        /// An automatic crop is still not applied. Resolving one means ten ffmpeg probes and a visible
        /// progress bar, and this is asked once per pass ahead of the filter chain that will run them
        /// again - where the AV1AN tab can put that behind a single resolve pass and this cannot. A
        /// manual crop costs four integers and is applied.
        /// </summary>
        public static Size GetEncodedFrameSize()
        {
            VideoStream vs = GetVideoSourceStream();

            if (vs == null || Form.EncCropModeBox.GetText().ToLower().Contains("auto"))
                return Size.Empty;

            (Size scaled, Size sar) = ResolveScaledFrame(GetCroppedSourceSize(vs), vs.Sar);

            return CurrentBorders.IsSet ? CurrentBorders.Compute(scaled, sar).Frame : scaled;
        }

        /// <summary>
        /// What the scale leaves when handed <paramref name="scaleInput"/>, and the shape that frame's
        /// pixels then have.
        /// <para/>
        /// Exact, the resize being held as an intent - which is the whole reason it stopped being two
        /// text boxes. The pixel shape is square wherever a scale runs, that filter ending in
        /// setsar=1:1, and the source's own where none does: with no resize configured nothing here
        /// un-squeezes an anamorphic source, because ffmpeg carries its aspect flag through to the
        /// output. That is the one place this tab and the AV1AN tab differ, and deliberately - av1an's
        /// encoders are handed bare frames and cannot keep the flag, so there it always de-squeezes.
        /// </summary>
        private static (Size Frame, Size Sar) ResolveScaledFrame(Size scaleInput, Size sar)
        {
            if (CurrentResize == null || CurrentResize.Mode == ResizeMode.Disabled)
                return (scaleInput, sar);

            Size result = CurrentResize.Compute(scaleInput, sar);

            return result.IsEmpty ? (scaleInput, sar) : (result, new Size(1, 1));
        }

        /// <summary> Why the configured crop cannot run on the loaded file, or "" when it can - the
        /// question <see cref="QuickConvert.Run"/> asks before it builds anything. The AV1AN tab answers
        /// the same one out of <see cref="Av1anFrame.CropProblem"/>, where it is settled in the pass
        /// that resolves the rest of the geometry. </summary>
        public static string GetCropProblem()
        {
            VideoStream vs = GetVideoSourceStream();

            if (vs == null || CurrentCrop == null || !CurrentCrop.IsSet || !Form.EncCropModeBox.GetText().ToLower().Contains("manual"))
                return "";

            // A stream copy builds no filter chain, so there is no crop to be impossible - and the
            // dropdown is disabled for one, which makes a rectangle left over from another codec a
            // setting the user cannot currently reach. Refusing the run over it is a trap, and the
            // border check beside this one has excused it since it was written.
            if (CodecUtils.GetCodec(GetCurrentCodecV()).DoesNotEncode)
                return "";

            return CurrentCrop.GetProblem(vs.Resolution);
        }

        /// <summary>
        /// Why the configured borders cannot run on the loaded file, or "" when they can - asked by
        /// <see cref="QuickConvert.Run"/> before it builds anything, the way the crop is.
        /// <para/>
        /// The bars are additive, so an extreme source padded towards an extreme ratio can reach a frame
        /// FFmpeg refuses to produce at all - and that is the only way left for them to fail. It used to
        /// also have to abstain over a resize it could not read, the boxes then being free text; the
        /// frame the bars go around is exact now.
        /// <para/>
        /// Refused rather than silently skipped: dropping a setting the user picked, on a run they
        /// started, is the failure this whole shape of check exists to avoid.
        /// <para/>
        /// A manual crop is applied first, an automatic one is not - measuring that means ten ffmpeg
        /// probes, which is not a thing to spend before a run has started. The limit is one nothing
        /// legitimate comes within two orders of magnitude of, so a crop's worth of pixels either way
        /// does not decide it.
        /// </summary>
        public static string GetBorderProblem()
        {
            VideoStream vs = GetVideoSourceStream();

            if (vs == null || CurrentBorders == null || !CurrentBorders.IsSet)
                return "";

            // A stream copy builds no filter chain, so there are no bars to be impossible - and the
            // dropdown is disabled for one, which means a target left over from another codec is a
            // setting the user cannot currently reach. Refusing a run over that would be a trap.
            if (CodecUtils.GetCodec(GetCurrentCodecV()).DoesNotEncode)
                return "";

            (Size scaled, Size sar) = ResolveScaledFrame(GetCroppedSourceSize(vs), vs.Sar);
            Size result = CurrentBorders.Compute(scaled, sar).Frame;

            if (ResizeConfig.ExceedsFrameLimit(result))
            {
                return $"the borders bring the frame to {result.Width}x{result.Height}, which is " +
                    $"{(double)result.Width * result.Height / 1_000_000d:0.#} megapixels - more than FFmpeg will " +
                    $"produce, so no frame would be written";
            }

            return "";
        }

        /// <summary>
        /// Why the frame this run would produce is one FFmpeg will not scale to, or "" when it is not.
        /// The AV1AN tab has asked this since the resize dialog was written; this tab could not, its
        /// resize being free text, and the failure arrived instead as "Picture size WxH is invalid" from
        /// inside ffmpeg - naming neither the setting that asked for it nor the box to change.
        /// <para/>
        /// Two settings reach it without going anywhere strange: both target boxes at their own maximum
        /// is 16384x16384, and 800% - also that box's maximum - of a 4K source is 30720x17280.
        /// </summary>
        public static string GetFrameSizeProblem()
        {
            VideoStream vs = GetVideoSourceStream();

            if (vs == null || CodecUtils.GetCodec(GetCurrentCodecV()).DoesNotEncode)
                return "";

            (Size scaled, Size sar) = ResolveScaledFrame(GetCroppedSourceSize(vs), vs.Sar);
            Size frame = CurrentBorders.IsSet ? CurrentBorders.Compute(scaled, sar).Frame : scaled;

            if (!ResizeConfig.ExceedsFrameLimit(frame))
                return "";

            // Named by whichever setting asked for it: the bars are additive, so they can carry a frame
            // over on their own, and pointing at the resize would send the user to the wrong control.
            string culprit = CurrentBorders.IsSet && !scaled.Equals(frame)
                ? "Pick a smaller resize target, or switch the borders off."
                : "Pick a smaller target in the resize dialog.";

            return $"The encode would be {frame.Width}x{frame.Height}, which is " +
                $"{(double)frame.Width * frame.Height / 1_000_000d:0.#} megapixels - more than FFmpeg will scale to, " +
                $"so no frame would be written.\n\n{culprit}";
        }

        public static async Task<string> GetVideoFilterArgs(IEncoder vCodec, CodecArgs codecArgs = null, bool quiet = false)
        {
            // The file the video is mapped from, which in Muxing Mode is not necessarily the one the
            // Track List is showing. Built from the loaded file, the whole chain was measured against a
            // frame belonging to another video - or dropped altogether where that file had no video
            // track at all, which is the ordinary shape of a mux: a video file and an audio file.
            MediaFile currFile = GetVideoSourceFile();
            List<string> filters = new List<string>();

            if (currFile == null || currFile.VideoStreams.Count < 1 || (vCodec != null && vCodec.DoesNotEncode))
                return "";

            // Deinterlacing comes before everything else, because everything else is measured against
            // a whole frame: a crop rectangle, a scale, a burnt-in subtitle. QTGMC contributes nothing
            // here - it runs in VapourSynth ahead of ffmpeg and its frames arrive already deinterlaced.
            string deinterlace = CurrentDeinterlace.GetFfmpegFilter();

            if (deinterlace.IsNotEmpty())
                filters.Add(deinterlace);

            VideoStream vs = currFile.VideoStreams.First();
            Fraction fps = GetUiFps();
            // What the frames actually arrive at, which a bob has already doubled. Compared against
            // the file's own rate instead, asking for 29.97 out of a bobbed 29.97i source would match
            // and add no filter at all, leaving the output at 59.94.
            Fraction sourceRate = Deinterlace.GetEffectiveSourceRate(vs, CurrentDeinterlace);

            // No trim filter of any kind. The frame-number mode used to put a "select" here, which cut
            // the video and nothing else, counted frames coming out of the deinterlacer above rather
            // than frames of the source, and left the kept ones carrying their original timestamps.
            // It is a seek and a duration now, like the other two modes - see TrimSettings.

            // Compared with a tolerance rather than exactly - see MiscUtils.FrameRateTolerance, which is
            // there because "23.976" and 24000/1001 are the same rate written two ways and this used to
            // build a filter between them.
            if (fps.GetFloat() > 0.01f && !MiscUtils.IsSameFrameRate(sourceRate, fps)) // Check Filter: Framerate Resampling
            {
                filters.Add($"fps=fps={fps}");

                // Said out loud because nothing else says it: this box has no readout of its own, so a
                // rate left over from another file, or typed with a digit out of place, used to reach
                // the end of an encode without ever being mentioned.
                Logger.Log($"Resampling the frame rate from {MiscUtils.DescribeFrameRate(sourceRate)} to " +
                    $"{MiscUtils.DescribeFrameRate(fps)}" +
                    $"{(CurrentDeinterlace != null && CurrentDeinterlace.DoublesFrameRate ? " - the source rate here is the doubled one, from one frame per field" : "")}. " +
                    $"Frames are duplicated or dropped to fit; the running time stays the same.", quiet);
            }

            string cropMode = Form.EncCropModeBox.GetText().ToLower();
            Size scaleInput = vs.Resolution; // What the scale filter is handed, once a crop has taken its share

            // Left out rather than clamped when it does not fit: the run refuses first, through
            // GetCropProblem, so reaching here with a bad crop means some other caller asking what the
            // chain would be - which is a question, not an encode.
            if (cropMode.Contains("manual") && CurrentCrop != null && CurrentCrop.IsSet
                && CurrentCrop.FitsInside(vs.Resolution)) // Check Filter: Manual Crop
            {
                filters.Add($"crop={CurrentCrop.GetFilterArgs(vs.Resolution)}");
                scaleInput = CurrentCrop.GetCroppedSize(vs.Resolution);
            }

            if (cropMode.Contains("auto")) // Check Filter: Autocrop
            {
                string autoCrop = await FfmpegUtils.GetCurrentAutoCrop(currFile.ImportPath, quiet);
                filters.Add(autoCrop);
                scaleInput = FfmpegUtils.ParseCropSize(autoCrop, scaleInput);
            }

            // After the crop, and measured against what the crop leaves: a rectangle is measured against
            // the frame the file has, so padding first moves the picture out from under it - and an odd
            // rectangle taken out of a padded frame is odd again, which is what the pad exists to stop.
            if ((scaleInput.Width % 2 != 0) || (scaleInput.Height % 2 != 0)) // Check Filter: Pad for mod2
            {
                filters.Add(FfmpegUtils.GetPadFilter(2));
                // What leaves the pad, not what went into it. Everything measured from here down - the
                // side a lone scale box derives, and the bars the borders add - is measured against the
                // frame that actually arrives, and this one grew by a pixel. An odd source with borders
                // on had them worked out from the odd size, so the pad they asked for came to an odd
                // frame that no encoder here accepts.
                scaleInput = new Size(scaleInput.Width + (scaleInput.Width % 2), scaleInput.Height + (scaleInput.Height % 2));
            }

            // The resize is a target rather than a pair of filter arguments, so the pixels it comes to
            // are worked out here, against the frame the crop and the pad leave. An anamorphic source is
            // de-squeezed as part of it - ResizeConfig measures its targets against the display size and
            // ends in setsar=1:1 - which is how a lone width used to turn a 4:3 DVD into a squashed 3:2
            // one. With no resize configured nothing de-squeezes, and that is right here: ffmpeg carries
            // the aspect flag through to the output, where av1an's encoders cannot.
            if (CurrentResize != null && CurrentResize.Mode != ResizeMode.Disabled
                && !CurrentResize.IsNoOp(scaleInput, vs.Sar)) // Check Filter: Resize
            {
                Size result = CurrentResize.Compute(scaleInput, vs.Sar);

                if (!result.IsEmpty)
                {
                    filters.Add(CurrentResize.GetFilterArgs(scaleInput, vs.Sar));
                    LogResize(scaleInput, vs.Sar, result, quiet);
                }
            }

            // After the crop and the scale, and before the bars. It used to run ahead of all three, and
            // each of those was wrong in its own way: a crop taking black bars off took the subtitles
            // sitting in them with it, an anamorphic source had its text de-squeezed along with the
            // picture and came out stretched, and a downscale rendered the lines at the source's size
            // and then shrank them, which is softer than rendering them at the size they end up. Ahead
            // of the borders rather than after, so the lines stay inside the picture.
            // The *loaded* file, not the video's: the dropdown above lists that file's subtitle tracks
            // and the number picked in it indexes them. In Muxing Mode those are two different files -
            // a video file and the file the subtitles came from - and reading the video's tracks with an
            // index chosen from the other file's list burns in whichever track happens to sit at that
            // position, or none at all.
            AddBurnInFilters(filters, TrackList.current?.File, quiet);

            // Last of the geometry, and measured against what the scale leaves: the bars go around
            // the finished picture rather than being scaled along with it, since a scaler run over a
            // hard black edge rings and the bars would come out neither black nor straight-edged.
            // A frame the boxes above leave unreadable never gets here - GetBorderProblem refuses the
            // run first - so reaching it with one means some other caller asking what the chain would
            // be, which is a question rather than an encode.
            if (CurrentBorders.IsSet) // Check Filter: Borders to a target aspect ratio
            {
                (Size scaled, Size scaledSar) = ResolveScaledFrame(scaleInput, vs.Sar);
                BorderPad pad = scaled.IsEmpty ? BorderPad.None(scaled) : CurrentBorders.Compute(scaled, scaledSar);

                if (pad.Runs)
                {
                    filters.Add(pad.GetFilterArgs());
                    Logger.Log($"Adding {pad.Describe()} to {pad.Input.Width}x{pad.Input.Height} " +
                        $"({AspectRatio.Describe(pad.Input.Width, pad.Input.Height)}), which brings the frame to " +
                        $"{pad.Frame.Width}x{pad.Frame.Height} ({AspectRatio.Describe(pad.Frame.Width, pad.Frame.Height)}). " +
                        $"The picture is not scaled - only the frame around it grows.", quiet);
                }
            }

            filters.AddRange(GetCustomFilters());

            // Last of everything, because the one encoder that has any is GIF, whose forced filters are
            // its whole palettegen/paletteuse graph - and a palette describes the frames it is generated
            // from. Run first, which is where these used to go, it quantised the source and then let the
            // scale, the crop and the burnt-in subtitles work on the paletted result, which the GIF muxer
            // then re-quantised with a palette of its own.
            if (codecArgs != null && codecArgs.ForcedFilters != null)
                filters.AddRange(codecArgs.ForcedFilters);

            filters = filters.Where(x => x.Trim().Length > 2).ToList(); // Strip empty filters

            if (filters.Count < 1)
                return "";

            string mapArgs = TrackList.GetMapArgs(true, false, false);
            string[] mapSplit = mapArgs.Split("-map ");

            if (mapSplit.Length < 2)
                return "";

            string firstVideoMap = mapSplit[1];
            string filterChain = "";

            for (int i = 0; i < filters.Count; i++)
            {
                bool first = i == 0;
                bool last = i == filters.Count - 1;

                filterChain += $"[{(first ? firstVideoMap : "vf")}]{filters[i]}";
                filterChain += $"[vf]{(last ? "" : ";")}";
            }

            // Quoted, because a chain of two or more filters is joined by semicolons and this command
            // line is handed to a shell: sh reads an unquoted ';' as the end of the command, so the
            // graph reached ffmpeg cut off at the first one - with a dangling [vf] label it then
            // refused - and the rest was run as a command of its own. Any two filters at once did it,
            // a crop with a scale among them. cmd does not split on ';', so this only ever showed on
            // Linux and macOS.
            //
            // Through WrapArg rather than a pair of double quotes, which stop sh splitting the graph
            // but not sh *rewriting* it: a burnt-in subtitle names the source file inside the chain, so
            // a '$' or a backtick anywhere in that path was expanded, and the second of those ran what
            // was between them. Single quotes leave the graph alone, and every filter path inside it is
            // quoted again at ffmpeg's own level by FormatUtils.GetFilterPath.
            return $"-filter_complex {Shell.WrapArg(filterChain)}";
        }

        /// <summary> The burnt-in subtitle track's filter, if one is selected and this file has it.
        /// <paramref name="currFile"/> is the file the burn-in dropdown was filled from, which need not
        /// be the one the video is read out of. </summary>
        private static void AddBurnInFilters(List<string> filters, MediaFile currFile, bool quiet)
        {
            int subIndex = currFile == null ? -1 : GetBurnInSubtitleIndex(currFile, quiet);

            if (subIndex < 0)
                return;

            if (currFile.SubtitleStreams[subIndex].Bitmap)
            {
                // Read as a filtergraph input, so it has to name where the file sits among the '-i'
                // arguments rather than assuming it is the first of them. Being an input of its own
                // it is seeked along with the video, so it needs no timestamp correction.
                filters.Add($"[{GetInputFileIndex(currFile)}:s:{subIndex}]overlay=shortest=1");
                return;
            }

            string burnIn = $"subtitles={FormatUtils.GetFilterPath(currFile.ImportPath)}:si={subIndex}";

            // This filter re-reads the source and picks its lines by frame timestamp. Seeking
            // the input restarts those timestamps at zero, so left alone it renders from the
            // top of the file: the wrong lines, or past the last cue no lines at all. Put the
            // frames back on the source's clock to render, then take them off again.
            float seekOffset = GetInputSeekOffsetSecs();

            if (seekOffset > 0f)
                burnIn = $"setpts=PTS+{seekOffset.ToStringDot()}/TB,{burnIn},setpts=PTS-{seekOffset.ToStringDot()}/TB";

            filters.Add(burnIn);
        }

        /// <summary>
        /// Why the selected subtitle track cannot be burnt in, or "" when it can - asked by
        /// <see cref="QuickConvert.Run"/> alongside the crop and border checks.
        /// <para/>
        /// One thing can go wrong that no amount of escaping here fixes: the "subtitles" filter is given
        /// the source's path inside the filtergraph, and an apostrophe in it does not survive. ffmpeg's
        /// own quoting for one - closing the quoted run, escaping, reopening - is undone twice on the way
        /// to the filter's option parser and comes back as neither the path nor an error about it: what
        /// ffmpeg reports is that it cannot open a filename with the apostrophe missing and ":si=0" stuck
        /// on the end. Measured against every spelling of it, quoted and unquoted alike.
        /// <para/>
        /// So it is said here instead, where the file and the setting can both be named, and where it
        /// costs nothing - the alternative is ffmpeg failing a second into the run over a path the user
        /// has to work out for themselves. Bitmap tracks are unaffected: they are a filtergraph input
        /// mapped by stream index, with no filename in the graph at all.
        /// </summary>
        public static string GetBurnInProblem()
        {
            MediaFile file = TrackList.current?.File;
            int subIndex = file == null ? -1 : GetBurnInSubtitleIndex(file, quiet: true);

            if (subIndex < 0 || file.SubtitleStreams[subIndex].Bitmap || !file.ImportPath.Contains('\''))
                return "";

            return $"the subtitles are burnt in by re-reading '{file.Name}', and FFmpeg cannot be given a path with " +
                $"an apostrophe in it inside a filter - so the burn-in would fail as soon as the encode started.\n\n" +
                $"Rename the file without the apostrophe, or set Burn Subtitles back to \"Disabled\".";
        }

        private static List<string> GetCustomFilters()
        {
            return Form.EncFilterRows.Select(x => x.Filter).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        }

        #endregion

        public static Fraction GetUiFps()
        {
            return MiscUtils.GetFpsFromString(Form.EncVidFpsBox.Text);
        }
    }
}
