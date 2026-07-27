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
                    Form.Av1anOutputPathBox.Text = Path.ChangeExtension(path, null);

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

        static void LoadAudBitrate(IEncoder enc)
        {
            int channels = Form.Av1anAudChannelsBox.GetText().Split(' ')[0].GetInt();
            decimal value = enc.QDefault >= 0 ? (enc.QDefault * MiscUtils.GetAudioBitrateMultiplier(channels)).RoundToInt() : 0;
            Form.Av1anAudQualUpDown.SetValueClamped(value);
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

        /// <summary> Whether the chosen container is MP4, which the muxing below has to work around. </summary>
        public static bool IsMp4Output()
        {
            return Form.Av1anContainerBox.GetText().Trim().Lower() == "mp4";
        }

        public static string GetConcatMethodArgs()
        {
            // mkvmerge writes Matroska and nothing else. Pointed at an .mp4 it does not refuse - it
            // writes a Matroska file under that name - so MP4 goes out through ffmpeg whatever the
            // dropdown says, rather than producing a file that lies about what it is.
            if (IsMp4Output())
                return "-c ffmpeg";

            return $"-c {Form.Av1anOptsConcatModeBox.GetText().ToLower().Trim()}";
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
                Form.Av1anOutputPathBox.Text = Path.ChangeExtension(IoUtils.GetAvailableFilename(UiData.GetOutPath(), ".av1an"), null);
        }

        public static Fraction GetUiFps()
        {
            return MiscUtils.GetFpsFromString(Form.Av1anFpsBox.Text);
        }

        public static async Task AskDeleteTempFolder(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir))
                return;

            int minKbytes = 4; // If the temp folder is smaller than this, delete it without asking
            var dirSize = IoUtils.GetDirSize(dir, true);

            if (RunTask.currentFileListMode == RunTask.FileListMode.Batch || RunTask.runningBatch || !Directory.Exists(Path.Combine(dir, "split")) || !File.Exists(Path.Combine(dir, "scenes.json")) || dirSize < minKbytes * 1024)
            {
                Logger.Log($"Temp folder has no scene detection data or is <{minKbytes}kb, deleting without asking", true);
                IoUtils.TryDeleteIfExists(dir);
                IoUtils.DeleteIfExists(dir + ".json");
                return;
            }

            string size = FormatUtils.Bytes(dirSize);
            string chunks = $"{IoUtils.GetFileInfosSorted(Path.Combine(dir, "encode"), false, "*.*").Where(x => x.Length >= 1024).Count()} encoded video chunks";
            string msg = $"Av1an has finished.\nDo you want to delete the temporary folder of this encode? It's {size} and contains {chunks}.";

            var result = await UiUtils.ShowMessageBox(msg, "Delete av1an temp folder?", UiUtils.MessageButtons.YesNo);

            if (result == UiUtils.DialogResult.Yes)
            {
                IoUtils.TryDeleteIfExists(dir);
                IoUtils.DeleteIfExists(dir + ".json");
            }
        }

        public static bool IsUsingVmaf()
        {
            return Form.Av1anQualModeBox.SelectedIndex == 1;
        }
    }
}
