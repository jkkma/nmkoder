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
using Stream = Nmkoder.Data.Streams.Stream;

namespace Nmkoder.UI.Tasks
{
    partial class QuickConvertUi : QuickConvert
    {
        private static MainWindow Form { get { return Program.MainWin; } }

        public static CropConfig CurrentCrop;
        public static TrimSettings CurrentTrim;

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

        public static void RefreshFileListRelatedOptions()
        {
            RefreshSubtitleBurnInBox();
            RefreshMetadataAndChapterOptions();
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
            Form.EncScaleBoxW.IsEnabled = Form.EncScaleBoxH.IsEnabled = !enc.DoesNotEncode;
            Form.EncCropModeBox.IsEnabled = !enc.DoesNotEncode;
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
            if (Form.EncQualModeBox.SelectedIndex == 0)
            {
                Form.EncVidQualityBox.SetRange(enc.QMin, enc.QMax > 0 ? enc.QMax : 100);

                if (enc.QDefault >= 0)
                    Form.EncVidQualityBox.SetValueClamped(enc.QDefault);
            }
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
        /// Steps the output filename aside when something is already there, rather than letting ffmpeg
        /// overwrite it - it is always run with -y. Only does anything once RunningTask is set, since
        /// that is what makes UiData.GetOutPath resolve to a real path.
        /// </summary>
        public static void ValidatePath()
        {
            if (TrackList.current == null || !File.Exists(UiData.GetOutPath()))
                return;

            string taken = UiData.GetOutPath();
            Form.FfmpegOutputBox.Text = Path.ChangeExtension(IoUtils.GetAvailableFilename(taken), null);
            Logger.Log($"'{Path.GetFileName(taken)}' already exists - saving as '{Path.GetFileName(UiData.GetOutPath())}' instead.");
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

            Form.EncSubBurnBox.SetItems(items, 0);
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
            Form.EncMetaCopySource.SetItems(items, select);
            Form.EncMetaChapterSource.SetItems(items, select);
        }

        public static void LoadMetadataGrid()
        {
            FileListEntry curr = TrackList.current;

            if (RunTask.runningBatch || curr == null)
                return;

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
            List<StreamListEntry> checkedEntries = TrackList.CheckedItems.ToList();

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
                        argsAttachmentData += $"-map_metadata:s:t {i}:s:t";
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

            if (Form.EncMetaApplyGrid.IsChecked == true)
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

        public static void InitAdvFilterGrid()
        {
            Form.EncFilterRows.Clear();
        }

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

        public static string GetMiscInputArgs()
        {
            List<string> args = new List<string>();

            if (CurrentTrim != null && !CurrentTrim.IsUnset && CurrentTrim.TrimMode == TrimSettings.Mode.TimeKeyframe)
                args.Add(CurrentTrim.StartArg);

            return string.Join(" ", args);
        }

        public static string GetMiscOutputArgs()
        {
            List<string> args = new List<string>();

            if (CurrentTrim != null && !CurrentTrim.IsUnset)
            {
                if (CurrentTrim.TrimMode == TrimSettings.Mode.TimeExact)
                    args.Add(CurrentTrim.StartArg);

                args.Add(CurrentTrim.DurationArg);
            }

            return string.Join(" ", args);
        }

        public static async Task<string> GetVideoFilterArgs(IEncoder vCodec, CodecArgs codecArgs = null, bool quiet = false)
        {
            MediaFile currFile = TrackList.current.File;
            List<string> filters = new List<string>();

            if (codecArgs != null && codecArgs.ForcedFilters != null)
                filters.AddRange(codecArgs.ForcedFilters);

            if (currFile.VideoStreams.Count < 1 || (vCodec != null && vCodec.DoesNotEncode))
                return "";

            VideoStream vs = currFile.VideoStreams.First();
            Fraction fps = GetUiFps();

            if (CurrentTrim != null && !CurrentTrim.IsUnset && CurrentTrim.TrimMode == TrimSettings.Mode.FrameNumbers) // Check Filter: Frame Number Trim
                filters.Add(CurrentTrim.StartArg);

            if (fps.GetFloat() > 0.01f && vs.Rate.GetFloat() != fps.GetFloat()) // Check Filter: Framerate Resampling
                filters.Add($"fps=fps={fps}");

            int subIndex = GetBurnInSubtitleIndex(currFile, quiet); // Check Filter: Subtitle Burn-In

            if (subIndex >= 0)
            {
                bool bitmapSubs = currFile.SubtitleStreams[subIndex].Bitmap;

                if (bitmapSubs)
                {
                    // Read as a filtergraph input, so it has to name where the file sits among the '-i'
                    // arguments rather than assuming it is the first of them. Being an input of its own
                    // it is seeked along with the video, so it needs no timestamp correction.
                    filters.Add($"[{GetInputFileIndex(currFile)}:s:{subIndex}]overlay=shortest=1");
                }
                else
                {
                    string subFilePath = FormatUtils.GetFilterPath(currFile.ImportPath);
                    string burnIn = $"subtitles={subFilePath}:si={subIndex}";

                    // This filter re-reads the source and picks its lines by frame timestamp. Seeking
                    // the input restarts those timestamps at zero, so left alone it renders from the
                    // top of the file: the wrong lines, or past the last cue no lines at all. Put the
                    // frames back on the source's clock to render, then take them off again.
                    float seekOffset = GetInputSeekOffsetSecs();

                    if (seekOffset > 0f)
                        burnIn = $"setpts=PTS+{seekOffset.ToStringDot()}/TB,{burnIn},setpts=PTS-{seekOffset.ToStringDot()}/TB";

                    filters.Add(burnIn);
                }
            }

            if ((vs.Resolution.Width % 2 != 0) || (vs.Resolution.Height % 2 != 0)) // Check Filter: Pad for mod2
                filters.Add(FfmpegUtils.GetPadFilter(2));

            string scaleW = (Form.EncScaleBoxW.Text ?? "").Trim().ToLower();
            string scaleH = (Form.EncScaleBoxH.Text ?? "").Trim().ToLower();
            string cropMode = Form.EncCropModeBox.GetText().ToLower();

            if (cropMode.Contains("manual") && CurrentCrop != null) // Check Filter: Manual Crop
                filters.Add($"crop={CurrentCrop.GetFilterArgs(vs.Resolution)}");

            if (cropMode.Contains("auto")) // Check Filter: Autocrop
                filters.Add(await FfmpegUtils.GetCurrentAutoCrop(currFile.ImportPath, quiet));

            if (!string.IsNullOrWhiteSpace(scaleW) || !string.IsNullOrWhiteSpace(scaleH)) // Check Filter: Scale
                filters.Add(MiscUtils.GetScaleFilter(scaleW, scaleH));

            filters.AddRange(GetCustomFilters());

            filters = filters.Where(x => x.Trim().Length > 2).ToList(); // Strip empty filters

            if (filters.Count < 1)
                return "";

            string mapArgs = await TrackList.GetMapArgs(vCodec, true, false, false);
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

            return $"-filter_complex {filterChain}";
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
