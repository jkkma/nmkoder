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

        /// <summary>
        /// The frame the encoder will be handed, as far as the scale boxes can be read without asking
        /// ffmpeg - or <see cref="Size.Empty"/> where they cannot be read at all, which leaves whoever
        /// asked to fall back on the source's own size.
        /// <para/>
        /// It exists for the tile count, which belongs to the frame being encoded and not to the file
        /// it came from: four tile columns are right for a 4K source and wrong for the 1080p it is
        /// being scaled to. The AV1AN tab settles this exactly, because its resize is held as an
        /// intent; here the boxes are free text handed to ffmpeg, so only what can be worked out with
        /// certainty is worked out. A plain pair of numbers is exact, and a lone number derives the
        /// other side by ffmpeg's own '-2' arithmetic - av_rescale, which rounds to nearest and ties
        /// away from zero - so the answer is the size ffmpeg will reach, not an approximation of it.
        /// A percentage or an expression is left to the caller's fallback rather than guessed at,
        /// since a tile count worked out from the wrong size is what this is here to stop.
        /// <para/>
        /// The crop is deliberately not applied. Resolving an automatic one means ten ffmpeg probes
        /// and a visible progress bar, and this is asked once per pass ahead of the filter chain that
        /// will run them again - where the AV1AN tab could put that behind a single resolve pass and
        /// this cannot. The scale is what moves a frame across a tile threshold in any case.
        /// </summary>
        public static Size GetEncodedFrameSize()
        {
            VideoStream vs = TrackList.current?.File.VideoStreams.FirstOrDefault();

            if (vs == null)
                return Size.Empty;

            string w = (Form.EncScaleBoxW.Text ?? "").Trim().ToLower();
            string h = (Form.EncScaleBoxH.Text ?? "").Trim().ToLower();

            if (w.IsEmpty() && h.IsEmpty())
                return Size.Empty; // No scale, so only the crop could have moved it - see above

            bool plainW = w.Length > 0 && w.All(char.IsDigit) && w.GetInt() > 0;
            bool plainH = h.Length > 0 && h.All(char.IsDigit) && h.GetInt() > 0;

            if (plainW && plainH)
                return new Size(w.GetInt(), h.GetInt());

            // What the scale filter is handed, which for an anamorphic source is the de-squeezed
            // frame - the same correction GetVideoFilterArgs puts in front of the scale, so the
            // derived side is worked out against the shape ffmpeg will actually be scaling.
            Size input = AspectRatio.IsAnamorphic(vs.Sar) ? ResizeConfig.DesqueezeOnly().Compute(vs.Resolution, vs.Sar) : vs.Resolution;

            if (input.IsEmpty || input.Width < 1 || input.Height < 1)
                return Size.Empty;

            if (plainW && h.IsEmpty())
                return new Size(w.GetInt(), (int)DivideRounded(w.GetInt() * (long)input.Height, input.Width * 2L) * 2);

            if (plainH && w.IsEmpty())
                return new Size((int)DivideRounded(h.GetInt() * (long)input.Width, input.Height * 2L) * 2, h.GetInt());

            return Size.Empty; // A percentage or an ffmpeg expression - not something to guess at
        }

        /// <summary> ffmpeg's av_rescale: rounded to nearest, ties away from zero. </summary>
        private static long DivideRounded(long value, long divisor)
        {
            return divisor == 0 ? 0 : (value + divisor / 2) / divisor;
        }

        /// <summary> Why the configured crop cannot run on the loaded file, or "" when it can - the
        /// question <see cref="QuickConvert.Run"/> asks before it builds anything. The AV1AN tab answers
        /// the same one out of <see cref="Av1anFrame.CropProblem"/>, where it is settled in the pass
        /// that resolves the rest of the geometry. </summary>
        public static string GetCropProblem()
        {
            VideoStream vs = TrackList.current?.File.VideoStreams.FirstOrDefault();

            if (vs == null || CurrentCrop == null || !CurrentCrop.IsSet || !Form.EncCropModeBox.GetText().ToLower().Contains("manual"))
                return "";

            return CurrentCrop.GetProblem(vs.Resolution);
        }

        public static async Task<string> GetVideoFilterArgs(IEncoder vCodec, CodecArgs codecArgs = null, bool quiet = false)
        {
            MediaFile currFile = TrackList.current.File;
            List<string> filters = new List<string>();

            if (currFile.VideoStreams.Count < 1 || (vCodec != null && vCodec.DoesNotEncode))
                return "";

            // Deinterlacing comes before everything else, because everything else is measured against
            // a whole frame: a crop rectangle, a scale, a burnt-in subtitle. QTGMC contributes nothing
            // here - it runs in VapourSynth ahead of ffmpeg and its frames arrive already deinterlaced.
            string deinterlace = CurrentDeinterlace.GetFfmpegFilter();

            if (deinterlace.IsNotEmpty())
                filters.Add(deinterlace);

            if (codecArgs != null && codecArgs.ForcedFilters != null)
                filters.AddRange(codecArgs.ForcedFilters);

            VideoStream vs = currFile.VideoStreams.First();
            Fraction fps = GetUiFps();
            // What the frames actually arrive at, which a bob has already doubled. Compared against
            // the file's own rate instead, asking for 29.97 out of a bobbed 29.97i source would match
            // and add no filter at all, leaving the output at 59.94.
            Fraction sourceRate = Deinterlace.GetEffectiveSourceRate(vs, CurrentDeinterlace);

            if (CurrentTrim != null && !CurrentTrim.IsUnset && CurrentTrim.TrimMode == TrimSettings.Mode.FrameNumbers) // Check Filter: Frame Number Trim
                filters.Add(CurrentTrim.StartArg);

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

            string scaleW = (Form.EncScaleBoxW.Text ?? "").Trim().ToLower();
            string scaleH = (Form.EncScaleBoxH.Text ?? "").Trim().ToLower();
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
                filters.Add(FfmpegUtils.GetPadFilter(2));

            if (!string.IsNullOrWhiteSpace(scaleW) || !string.IsNullOrWhiteSpace(scaleH)) // Check Filter: Scale
            {
                // The boxes talk about the picture, but the filter they build is measured in storage
                // pixels and ends in setsar=1:1 - which is how a percentage or a lone width used to
                // turn a 4:3 DVD into a squashed 3:2 one, the way the AV1AN tab's old boxes did. An
                // anamorphic source is de-squeezed first, so the numbers measure the real shape. A
                // custom filter that sets a SAR or DAR itself is the user taking this over.
                bool desqueeze = AspectRatio.IsAnamorphic(vs.Sar)
                    && !GetCustomFilters().Any(f => f.Contains("setsar") || f.Contains("setdar"));

                if (desqueeze)
                {
                    ResizeConfig dq = ResizeConfig.DesqueezeOnly();
                    Size result = dq.Compute(scaleInput, vs.Sar);

                    if (!result.IsEmpty)
                    {
                        filters.Add(dq.GetFilterArgs(scaleInput, vs.Sar));
                        Logger.Log($"De-squeezing {scaleInput.Width}x{scaleInput.Height} ({vs.Sar.Width}:{vs.Sar.Height} pixels) to " +
                            $"{result.Width}x{result.Height} before the scale, so it measures the shape the video plays at.", quiet);
                    }
                }

                filters.Add(MiscUtils.GetScaleFilter(scaleW, scaleH));
            }

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

            // Quoted, because a chain of two or more filters is joined by semicolons and this command
            // line is handed to a shell: sh reads an unquoted ';' as the end of the command, so the
            // graph reached ffmpeg cut off at the first one - with a dangling [vf] label it then
            // refused - and the rest was run as a command of its own. Any two filters at once did it,
            // a crop with a scale among them. cmd does not split on ';', so this only ever showed on
            // Linux and macOS.
            return $"-filter_complex \"{filterChain}\"";
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
