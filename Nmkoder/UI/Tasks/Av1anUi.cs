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

        public static void Init()
        {
            // Load video codecs
            Form.Av1anCodecBox.SetItems(Enum.GetValues<CodecUtils.Av1anCodec>().Select(c => (object)CodecUtils.GetCodec(c).FriendlyName));
            ConfigParser.LoadComboxIndex(Form.Av1anCodecBox);

            // Load quality modes
            Form.Av1anQualModeBox.SetItems(Enum.GetValues<Av1an.QualityMode>()
                .Select(qm => (object)qm.ToString().Replace("Crf", "CRF").Replace("TargetVmaf", "Target VMAF")), 0);

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
                    InitAudioChannels(TrackList.current?.File.AudioStreams.FirstOrDefault()?.Channels);

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
            if (IsUsingVmaf())
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
        /// The parameters documented for an encoder, as [argument, value, description] rows. Values
        /// come through blank: the list is there to be read and filled in, and only rows with a
        /// value reach the command line. An encoder with no file simply has nothing to show.
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
                    rows.Add(new EncoderArgRow(arg[0], arg[1], arg[2]));
            }

            return rows;
        }

        public static void LoadAdvancedArgsGrid(IEncoder enc)
        {
            Form.Av1anArgRows.Clear();

            foreach (EncoderArgRow row in ReadEncoderArgRows(enc))
                Form.Av1anArgRows.Add(row);
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

        public static async Task<string> GetVideoFilterArgs(CodecArgs codecArgs = null)
        {
            List<string> filters = new List<string>();

            if (codecArgs != null && codecArgs.ForcedFilters != null)
                filters.AddRange(codecArgs.ForcedFilters);

            if (TrackList.current.File.VideoStreams.Count < 1)
                return "";

            VideoStream vs = TrackList.current.File.VideoStreams.First();
            Fraction fps = GetUiFps();

            if (fps.GetFloat() > 0.01f && vs.Rate.GetFloat() != fps.GetFloat()) // Check Filter: Framerate Resampling
                filters.Add($"fps=fps={fps}");

            if ((vs.Resolution.Width % 2 != 0) || (vs.Resolution.Height % 2 != 0)) // Check Filter: Pad for mod2
                filters.Add(FfmpegUtils.GetPadFilter(2));

            string scaleW = (Form.Av1anScaleBoxW.Text ?? "").Trim().ToLower();
            string scaleH = (Form.Av1anScaleBoxH.Text ?? "").Trim().ToLower();
            string cropMode = Form.Av1anCropBox.GetText().ToLower();

            if (cropMode.Contains("manual") && CurrentCrop != null) // Check Filter: Manual Crop
                filters.Add($"crop={CurrentCrop.GetFilterArgs(vs.Resolution)}");

            if (cropMode.Contains("auto")) // Check Filter: Autocrop
                filters.Add(await FfmpegUtils.GetCurrentAutoCrop(TrackList.current.File.ImportPath, false));

            if (!string.IsNullOrWhiteSpace(scaleW) || !string.IsNullOrWhiteSpace(scaleH)) // Check Filter: Scale
                filters.Add(MiscUtils.GetScaleFilter(scaleW, scaleH));

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

        public static string GetSplittingMethodArgs()
        {
            return $"--split-method {(Form.Av1anOptsSplitModeBox.SelectedIndex == 0 ? "none" : "av-scenechange")}";
        }

        public static string GetChunkGenMethod()
        {
            return $"-m {Form.Av1anOptsChunkModeBox.GetText().ToLower().Trim()}";
        }

        /// <summary> The selected chunk method. The dropdown is filled from the enum, so index is value. </summary>
        public static Av1an.ChunkMethod GetCurrentChunkMethod()
        {
            return (Av1an.ChunkMethod)Math.Max(0, Form.Av1anOptsChunkModeBox.SelectedIndex);
        }

        /// <summary> The chunk methods that read the source through vspipe, and so run a VapourSynth script at all. </summary>
        private static readonly Av1an.ChunkMethod[] VapourSynthChunkMethods =
            { Av1an.ChunkMethod.BestSource, Av1an.ChunkMethod.LSMASH, Av1an.ChunkMethod.FFMS2 };

        /// <summary>
        /// Which converter av1an should reach the chosen pixel format with. By default it pipes the
        /// decoded frames through a second ffmpeg process to convert them; "vs-resize" instead has the
        /// VapourSynth script it already generates do it with resize.Bicubic, which drops that process
        /// from every chunk and puts the resampling through zimg rather than swscale.
        /// <para/>
        /// Returns "" - leaving av1an on ffmpeg - whenever any condition av1an attaches to the flag is
        /// unmet, and each one has to be checked here rather than left to av1an. Selecting vs-resize is
        /// what makes it skip building the converting ffmpeg pipe, so asking for it where the script
        /// cannot deliver does not fall back to ffmpeg: it hands the encoder whatever the source
        /// already was, or fails the chunk outright.
        /// </summary>
        public static async Task<string> GetPixelFormatConverterArgs(string pixFmt, bool hasFfmpegFilters)
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

            Av1an.ChunkMethod chunkMethod = GetCurrentChunkMethod();

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

            // -1 leaves the choice to the preset and 0 is all 8-bit, so neither is asking the input for
            // something it does not have. 1 is all 10-bit, 2 hybrid - both want 10-bit samples.
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

        public static void InitAdvFilterGrid()
        {
            Form.Av1anFilterRows.Clear();
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

            // Stopped by a bad setting or an error rather than by the user. Not their decision to make,
            // and they may well want to fix whatever it was and carry on from the chunks already done.
            if (!canceledByUser)
            {
                Logger.Log($"Keeping the temp folder so this encode can be resumed ({FormatUtils.Bytes(IoUtils.GetDirSize(dir, true))} in '{Path.GetFileName(dir)}').");
                return;
            }

            // Stopping an encode is not the same as abandoning it, and only the user knows which they
            // meant. The chunks are worth however long they took, so this one is worth asking about.
            string size = FormatUtils.Bytes(IoUtils.GetDirSize(dir, true));
            int chunks = IoUtils.GetFileInfosSorted(Path.Combine(dir, "encode"), false, "*.*").Where(x => x.Length >= 1024).Count();
            string msg = $"This encode has been canceled.\n\nKeep its temporary files so it can be resumed later? " +
                $"They are {size} and hold {chunks} encoded video chunk{(chunks == 1 ? "" : "s")}.\n\n" +
                $"Choosing No deletes them, and the encode would have to start over from the beginning.";

            var result = await UiUtils.ShowMessageBox(msg, "Resume this encode later?", UiUtils.MessageButtons.YesNo);

            if (result == UiUtils.DialogResult.Yes)
            {
                Logger.Log($"Keeping the temp folder so this encode can be resumed ({size} in '{Path.GetFileName(dir)}').");
                return;
            }

            DeleteTempFolder(dir);
        }

        /// <summary> Removes a temp folder along with the resume arguments saved beside it. </summary>
        public static void DeleteTempFolder(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                return;

            Logger.Log($"Deleting temp folder '{Path.GetFileName(dir)}' ({FormatUtils.Bytes(IoUtils.GetDirSize(dir, true))}).", true);
            IoUtils.TryDeleteIfExists(dir);
            IoUtils.DeleteIfExists(dir + ".json");
        }

        public static bool IsUsingVmaf()
        {
            return Form.Av1anQualModeBox.SelectedIndex == 1;
        }
    }
}
