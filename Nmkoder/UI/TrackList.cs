using Avalonia.Media;
using Nmkoder.Data;
using Nmkoder.Data.Codecs;
using Nmkoder.Data.Streams;
using Nmkoder.Data.Ui;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.Media;
using Nmkoder.OS;
using Nmkoder.UI.Tasks;
using Nmkoder.Views;
using Nmkoder.Utils;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Stream = Nmkoder.Data.Streams.Stream;

namespace Nmkoder.UI
{
    class TrackList
    {
        /// <summary> The tracks of all loaded files. The main window's track list is bound to this. </summary>
        public static ObservableCollection<StreamListEntry> Items { get; } = new ObservableCollection<StreamListEntry>();

        public static IEnumerable<StreamListEntry> CheckedItems { get { return Items.Where(x => x.IsChecked); } }

        public static FileListEntry current;
        public static AudioConfiguration currentAudioConfig = null;

        /// <summary>
        /// Unloads the current file. <paramref name="resetSettings"/> is what "Reset on new file"
        /// hangs off, so a batch passes false: stepping to the next file in a queue is not the user
        /// loading one, and applying it there threw away the very settings the queue was configured
        /// with - the cut section among them, which left the Cut utility with nothing to cut.
        /// </summary>
        public static void ClearCurrentFile(bool clearStreamList = false, bool resetSettings = true)
        {
            MainWindow f = Program.MainWin;

            current = null;
            f.FfmpegOutputBox.Text = "";

            if (clearStreamList)
                Items.Clear();

            f.SetStreamDetails("");
            f.FormatInfoLabel.Text = "";
            f.MetadataRows.Clear();
            ThumbnailView.ClearUi();
            DeinterlaceUi.RefreshInfo(); // The readouts describe the loaded file, and there is none now
            ToneMapUi.RefreshInfo();

            if (resetSettings)
                ResetSettings();
        }

        public static void ResetSettings(bool resetAll = false, bool showMsgBox = false)
        {
            MainWindow f = Program.MainWin;

            List<string> clearedSettings = new List<string>();

            if (resetAll || ResetSettingsOnNewFile.ResetCrop)
            {
                QuickConvertUi.CurrentCrop = Av1anUi.CurrentCrop = null;
                f.EncCropModeBox.SelectedIndex = f.Av1anCropBox.SelectedIndex = 0;
                clearedSettings.Add(ResetSettingsOnNewFile.NiceNames[nameof(ResetSettingsOnNewFile.ResetCrop)]);
            }

            if (resetAll || ResetSettingsOnNewFile.ResetTrim)
            {
                // Timestamps picked against one file mean nothing against the next
                QuickConvertUi.CurrentTrim = Av1anUi.CurrentTrim = null;
                UtilCut.Cut = null;
                f.UpdateTrimBtnText();
                f.UpdateAv1anTrimBtnText();
                f.UpdateCutBtnText();
                clearedSettings.Add(ResetSettingsOnNewFile.NiceNames[nameof(ResetSettingsOnNewFile.ResetTrim)]);
            }

            if (resetAll || ResetSettingsOnNewFile.ResetDeinterlace)
            {
                // An engine picked by name deinterlaces whatever it is handed, progressive or not.
                // That is the point of picking one - it is the only way past a container flag that
                // lies about its own scan type, which is a thing tape captures do and which nothing
                // here checks for, because checking would put a frame scan in front of loading every
                // modern video. What it must not do is outlive the file it was picked for: on the
                // AV1AN tab QTGMC is a full pass over the video into a near-lossless intermediate
                // before av1an starts, so a mode left over from a tape spends hours and tens of
                // gigabytes on the next file loaded, and 47.952 fps of interpolated fields is what
                // comes out. Automatic reads the source and leaves a progressive one alone.
                //
                // Only where a *user* loaded the file. A batch clears each file with
                // resetSettings: false, so a stack of tapes keeps the engine picked for it.
                DeinterlaceUi.ResetModes();
                clearedSettings.Add(ResetSettingsOnNewFile.NiceNames[nameof(ResetSettingsOnNewFile.ResetDeinterlace)]);
            }

            if (resetAll || ResetSettingsOnNewFile.ResetToneMap)
            {
                // On by default beside the deinterlacer, and for the same test: a curve picked here
                // describes the file that was just replaced. It is the gentler of the two - the row is
                // hidden for anything that is not HDR and ToneMapUi.ModeInEffect answers Off while it is,
                // so a curve left selected cannot reach an SDR file at all - but where the next file *is*
                // also HDR it would convert it silently, and converting HDR to SDR is not undoable.
                //
                // Only where a *user* loaded the file, as above: a batch clears each one with
                // resetSettings: false, so a queue of HDR files keeps the curve picked for it.
                ToneMapUi.ResetModes();
                clearedSettings.Add(ResetSettingsOnNewFile.NiceNames[nameof(ResetSettingsOnNewFile.ResetToneMap)]);
            }

            if (resetAll || ResetSettingsOnNewFile.ResetResize)
            {
                QuickConvertUi.CurrentResize = Av1anUi.CurrentResize = new ResizeConfig();
                clearedSettings.Add(ResetSettingsOnNewFile.NiceNames[nameof(ResetSettingsOnNewFile.ResetResize)]);
            }

            if (resetAll || ResetSettingsOnNewFile.ResetBorders)
            {
                // Off by default, beside Resize rather than beside Crop: "everything I encode comes
                // out 16:9" is a preference about output, where a crop rectangle describes the file
                // that was just replaced. It is offered all the same, because a target shape picked
                // for one source is the wrong one for the next often enough - bars added to a 4:3
                // capture are bars added to the 16:9 download after it, if nobody moves the box.
                // Setting the index is what moves the configuration behind it, through the handlers.
                f.EncBordersBox.SelectedIndex = f.Av1anBordersBox.SelectedIndex = 0;
                clearedSettings.Add(ResetSettingsOnNewFile.NiceNames[nameof(ResetSettingsOnNewFile.ResetBorders)]);
            }

            if (resetAll || ResetSettingsOnNewFile.ResetFpsResample)
            {
                f.EncVidFpsBox.Text = f.Av1anFpsBox.Text = "";
                clearedSettings.Add(ResetSettingsOnNewFile.NiceNames[nameof(ResetSettingsOnNewFile.ResetFpsResample)]);
            }

            if (resetAll || ResetSettingsOnNewFile.ResetCustomFilters)
            {
                f.EncFilterRows.Clear();
                f.Av1anFilterRows.Clear();
                clearedSettings.Add(ResetSettingsOnNewFile.NiceNames[nameof(ResetSettingsOnNewFile.ResetCustomFilters)]);
            }

            // Both the crop and the resize clauses above move what the resize dropdowns' entries work out
            // to, on both tabs - and each of those refreshes its own borders readout on the way out, the
            // bars being measured against the frame the resize leaves.
            Av1anUi.RefreshResizeBox();
            QuickConvertUi.RefreshResizeBox();

            if (showMsgBox)
                UiUtils.ShowMessageBoxAsync($"The following settings have been reset:\n{string.Join(", ", clearedSettings)}.", UiUtils.MessageType.Message);
        }

        public static async Task SetAsMainFile(FileListEntry entry, bool switchToTrackList = true, bool generateThumbs = true, bool setWorking = true)
        {
            if (setWorking)
                Program.MainWin.SetWorking(true);

            MediaFile mediaFile = entry.File;

            if (mediaFile.IsDirectory)
                await mediaFile.InitializeSequence();

            int streamCount = await FfmpegUtils.GetStreamCount(mediaFile.ImportPath);
            Logger.Log($"Scanning '{mediaFile.Name}' (Streams: {streamCount})...");
            await mediaFile.Initialize();
            PrintFoundStreams(mediaFile);
            current = new FileListEntry(mediaFile);

            string titleStr = current.Title != null && current.Title.Trim().Length > 2 ? $"Title: {current.Title.Trunc(30)} - " : "";
            string br = current.File.TotalKbits > 0 ? $" - Bitrate: {FormatUtils.Bitrate(current.File.TotalKbits)}" : "";
            string dur = FormatUtils.MsToTimestamp(current.File.DurationMs);
            Program.MainWin.FormatInfoLabel.Text = $"{titleStr}Format: {current.File.Format} - Duration: {dur}{br} - Size: {FormatUtils.Bytes(current.File.Size)}";
            Items.Clear();
            // The per-track audio settings describe the file being replaced, and AudioConfiguration
            // refuses to hand them to any other one. The dropdown that points at them has to come back
            // with them: left on "Configure each track separately" over nothing, GetAudioArgsForEachStream
            // found perTrack set and the configuration null, skipped both override branches in silence,
            // and every track went out at the global spinner's bitrate and channel count - with the
            // Configure… button still on screen saying otherwise. A batch met this on every file,
            // including the first, since the queue loads each one through here.
            currentAudioConfig = null;
            Program.MainWin.EncAudConfModeBox.SelectedIndex = 0;

            QuickConvertUi.InitFile(current.File.SourcePath);
            Av1anUi.InitFile(current.File.SourcePath);
            // Off the UI thread: a file whose container says nothing about its scan type has to have a
            // few hundred frames decoded before anything is known, and loading should not wait on it.
            DeinterlaceUi.AnalyzeInBackground(current.File);
            // The same shape, and far cheaper - one ffprobe frame read rather than a few hundred decoded
            // frames - but still not something a file load should block on.
            ToneMapUi.AnalyzeInBackground(current.File);

            if (setWorking)
                Program.MainWin.SetWorking(false);

            if (generateThumbs && mediaFile.VideoStreams.Any())
                _ = Task.Run(() => ThumbnailView.GenerateThumbs(mediaFile.SourcePath)); // Generate thumbs in background
        }

        private static void PrintFoundStreams(MediaFile mediaFile)
        {
            List<string> foundTracks = new List<string>();

            if (mediaFile.VideoStreams.Count > 0) foundTracks.Add($"{mediaFile.VideoStreams.Count} video track{(mediaFile.VideoStreams.Count == 1 ? "" : "s")}");
            if (mediaFile.AudioStreams.Count > 0) foundTracks.Add($"{mediaFile.AudioStreams.Count} audio track{(mediaFile.AudioStreams.Count == 1 ? "" : "s")}");
            if (mediaFile.SubtitleStreams.Count > 0) foundTracks.Add($"{mediaFile.SubtitleStreams.Count} subtitle track{(mediaFile.SubtitleStreams.Count == 1 ? "" : "s")}");
            if (mediaFile.DataStreams.Count > 0) foundTracks.Add($"{mediaFile.DataStreams.Count} data track{(mediaFile.DataStreams.Count == 1 ? "" : "s")}");
            if (mediaFile.AttachmentStreams.Count > 0) foundTracks.Add($"{mediaFile.AttachmentStreams.Count} attachment{(mediaFile.AttachmentStreams.Count == 1 ? "" : "s")}");

            if (foundTracks.Count > 0)
                Logger.Log($"Found {string.Join(", ", foundTracks)}.");
            else
                Logger.Log($"Found no media streams in '{mediaFile.Name}'!");
        }

        public static async Task AddStreamsToList(MediaFile mediaFile, IBrush color, bool switchToList, bool silent = false, bool setWorking = true)
        {
            if (setWorking)
                Program.MainWin.SetWorking(true);

            if (!mediaFile.Initialized)
            {
                if (!silent)
                    Logger.Log($"Scanning '{mediaFile.Name}'...");

                await mediaFile.Initialize();

                if (!silent)
                    PrintFoundStreams(mediaFile);
            }

            bool alreadyHasVidStream = Items.Any(x => x.Stream.Type == Stream.StreamType.Video);

            Program.MainWin.ignoreStreamListCheck = true;

            foreach (Stream s in mediaFile.AllStreams)
            {
                try
                {
                    StreamListEntry entry = new StreamListEntry(mediaFile, s) { RowBrush = color };
                    bool check = s.Codec.ToLower().Trim() != "unknown" && !(s.Type == Stream.StreamType.Video && alreadyHasVidStream);
                    entry.SetCheckedQuiet(check);
                    Items.Add(entry);
                }
                catch (Exception e)
                {
                    Logger.Log($"Error trying to load streams into UI: {e.Message}\n{e.StackTrace}");
                }
            }

            if (setWorking)
                Program.MainWin.SetWorking(false);

            if (switchToList)
                Program.MainWin.SelectedMainTab = 1;

            Program.MainWin.ignoreStreamListCheck = false;
            Program.MainWin.OnCheckedStreamsChange();
            Program.MainWin.UpdateTrackListBtnsState();
        }

        public static async Task Refresh()
        {
            List<string> loadedPaths = FileList.Items.Select(x => x.File.ImportPath).ToList();

            foreach (StreamListEntry entry in Items.ToList())
            {
                if (entry.MediaFile == null || !loadedPaths.Contains(entry.MediaFile.ImportPath))
                    Items.Remove(entry);
            }

            await Program.MainWin.RefreshFileListUi();
        }

        public static async Task Extract(StreamListEntry entry)
        {
            if (entry == null)
                return;

            string outDir = await FfmpegExtract.ExtractAttachments(entry.MediaFile.SourcePath, entry.Stream.Index);

            if (outDir.IsEmpty()) // It said why; opening a folder that is not there would not add to it
                return;

            Shell.OpenWithDefaultHandler(outDir);
        }

        public static string GetStreamDetails(Stream stream, MediaFile mediaFile = null)
        {
            if (stream == null || mediaFile == null)
                return "";

            List<string> lines = new List<string>();
            string ext = Path.GetExtension(mediaFile.SourcePath);
            lines.Add($"Source File: {Path.GetFileNameWithoutExtension(mediaFile.SourcePath).Trunc(85 - ext.Length) + ext}");
            lines.Add($"Codec: {stream.CodecLong} ({stream.Codec})");

            if (stream.Type == Stream.StreamType.Video)
            {
                VideoStream v = (VideoStream)stream;
                lines.Add($"Title: {((v.Title.Trim().Length > 1) ? v.Title.Trunc(90) : "None")}");
                lines.Add($"Resolution and Aspect Ratio: {v.Resolution.ToStringShort()} - SAR {v.Sar.ToStringShort(":")} - DAR {v.Dar.ToStringShort(":")}");
                int bitDepth = FormatUtils.GetBitDepthFromPixelFormat(v.PixelFormat);
                lines.Add($"Color Format: {v.PixelFormat}{(bitDepth > 0 ? $" ({bitDepth}-bit)" : "")}");
                lines.Add($"Frame Rate: {v.Rate} (~{v.Rate.GetString()} FPS)");
                lines.Add($"Scan Type: {DescribeScanType(mediaFile, v)}");
            }

            else if (stream.Type == Stream.StreamType.Audio)
            {
                AudioStream a = (AudioStream)stream;
                lines.Add($"Title: {((a.Title.Trim().Length > 1) ? a.Title.Trunc(90) : "None")}");
                lines.Add($"Sample Rate: {((a.SampleRate > 1) ? $"{a.SampleRate} KHz" : "None")}");
                string chLayout = (a.Layout.Contains("(") && !a.Layout.Contains(" (") ? a.Layout.Replace("(", " (") : a.Layout);
                lines.Add($"Channels: {((a.Channels > 0) ? $"{a.Channels}" : "Unknown")} {(a.Layout.Trim().Length > 1 ? $"as {chLayout.ToTitleCase()}" : "")}");
                lines.Add($"Language: {((a.Language.Trim().Length > 1) ? $"{Aliases.GetLanguageString(a.Language)}" : "Unknown")}");
            }

            else if (stream.Type == Stream.StreamType.Subtitle)
            {
                SubtitleStream s = (SubtitleStream)stream;
                lines.Add($"Title: {((s.Title.Trim().Length > 1) ? s.Title.Trunc(90) : "None")}");
                lines.Add($"Language: {((s.Language.Trim().Length > 1) ? $"{Aliases.GetLanguageString(s.Language)}" : "Unknown")}");
                lines.Add($"Type: {((s.Bitmap) ? $"Bitmap-based" : "Text-based")}");
            }

            else if (stream.Type == Stream.StreamType.Attachment)
            {
                AttachmentStream a = (AttachmentStream)stream;
                lines.Add($"Filename: {((a.Filename.Trim().Length > 1) ? a.Filename.Trunc(90) : "None")}");
                lines.Add($"MIME Type: {((a.MimeType.Trim().Length > 1) ? a.MimeType : "Unknown")}");
            }

            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>
        /// What the track's scan type is, preferring what was measured over what the file claims - a
        /// container flag that says nothing is exactly the case the frame scan exists to settle, and
        /// showing "Unknown" beside a verdict that has already been reached would contradict the
        /// Deinterlace readout on the encode tabs.
        /// </summary>
        private static string DescribeScanType(MediaFile file, VideoStream v)
        {
            InterlaceInfo info = file.Interlacing;

            if (info != null && (v.FieldOrder == FieldOrder.Unknown || info.Scanned))
                return $"{info.DescribeOrder().ToTitleCase()}{(info.Scanned ? $" ({info.Evidence})" : "")}";

            switch (v.FieldOrder)
            {
                case FieldOrder.TopFieldFirst: return "Interlaced, Top Field First";
                case FieldOrder.BottomFieldFirst: return "Interlaced, Bottom Field First";
                case FieldOrder.Progressive: return "Progressive";
                default: return "Unknown";
            }
        }

        /// <param name="perInput"> Arguments to repeat in front of every '-i' - for the options ffmpeg
        /// reads as belonging to the input that follows them rather than to the command. The keyframe
        /// trim's own "-ss" is the only one there is, and putting it once at the head of the command
        /// applied it to the first input alone: in Muxing Mode that is one file among several, so the
        /// video started a minute in while every other file's tracks started from the top. </param>
        public static string GetInputFilesString(string perInput = "")
        {
            string prefix = perInput.IsEmpty() ? "" : $"{perInput.Trim()} ";

            if (RunTask.currentFileListMode == RunTask.FileListMode.Batch)
            {
                if (current.File.IsDirectory)
                    return $"{prefix}-safe 0 -f concat -r {current.File.InputRate} -i {Shell.WrapArg(current.File.ImportPath)}";
                else
                    return $"{prefix}-i {Shell.WrapArg(current.File.ImportPath)}";
            }

            List<string> args = new List<string>();

            foreach (FileListEntry entry in FileList.Items)
            {
                if (entry.File.IsDirectory)
                    args.Add($"{prefix}-safe 0 -f concat -r {entry.File.InputRate} -i {Shell.WrapArg(entry.File.ImportPath)}");
                else
                    args.Add($"{prefix}-i {Shell.WrapArg(entry.File.ImportPath)}");
            }

            Logger.Log($"Input Args: {string.Join(" ", args)}", true);
            return string.Join(" ", args);
        }

        /// <summary>
        /// The ticked tracks that actually reach the output, in output order - which is not simply
        /// <see cref="CheckedItems"/>. Data and attachment tracks are ticked by default and almost no
        /// container takes them: attachments are a Matroska feature, data streams a QuickTime one. Left
        /// in they fail the mux, so they are dropped and named. Refusing the encode instead would be
        /// worse - there is no way to keep them in a container that cannot hold them, so the user would
        /// only be told to untick tracks they never chose.
        /// <para/>
        /// One list, because everything that numbers the output streams has to agree with the maps.
        /// "-metadata:s:N" and "-disposition:s:N" name an *output* stream, and counting the ticked
        /// tracks instead put every title and language after a dropped track onto the wrong one.
        /// </summary>
        /// <param name="logDropped"> Whether to name what was left behind. True for the run's own maps,
        /// which happens once per encode; false for everything else asking the same question. </param>
        public static List<StreamListEntry> GetMappedStreams(bool videoOnly = false, bool logDropped = false)
        {
            Containers.Container container = QuickConvertUi.GetCurrentContainer();
            List<StreamListEntry> kept = new List<StreamListEntry>();
            List<string> dropped = new List<string>();
            // A stripped kind reaches no output stream. It used to be mapped anyway and taken out again
            // at the far end by "-vn", "-an" or "-sn", which works for the file and not for anything
            // counting output streams: with the audio stripped, every title and language the metadata
            // grid set on a track after it went one stream too late - onto the subtitles, usually.
            bool stripV = QuickConvertUi.GetCurrentCodecV() == CodecUtils.VideoCodec.StripVideo;
            bool stripA = QuickConvertUi.GetCurrentCodecA() == CodecUtils.AudioCodec.StripAudio;
            bool stripS = QuickConvertUi.GetCurrentCodecS() == CodecUtils.SubtitleCodec.StripSubs;

            foreach (StreamListEntry entry in CheckedItems)
            {
                if (videoOnly && entry.Stream.Type != Stream.StreamType.Video) // Skip all non-video streams if videoOnly == true
                    continue;

                if ((stripV && entry.Stream.Type == Stream.StreamType.Video)
                    || (stripA && entry.Stream.Type == Stream.StreamType.Audio)
                    || (stripS && entry.Stream.Type == Stream.StreamType.Subtitle))
                {
                    continue;
                }

                if (entry.Stream.Type == Stream.StreamType.Data && !Containers.CanCopyDataStream(container))
                {
                    dropped.Add("data");
                    continue;
                }

                if (entry.Stream.Type == Stream.StreamType.Attachment && !Containers.CanCopyAttachment(container))
                {
                    dropped.Add("attachment");
                    continue;
                }

                kept.Add(entry);
            }

            if (logDropped)
            {
                foreach (var kind in dropped.GroupBy(x => x))
                    Logger.Log($"{kind.Count()} {kind.Key} track{(kind.Count() == 1 ? "" : "s")} left out: {container.ToString().ToUpper()} cannot store {kind.Key} streams.");
            }

            return kept;
        }

        /// <param name="hasFilterChain"> Whether this run builds a video filtergraph, in which case the
        /// first video track is read off its "[vf]" output rather than straight out of the file.
        /// <para/>
        /// Passed in rather than worked out here, which is what it used to do. The chain is not a
        /// function of the encoder alone - GIF contributes its entire palettegen/paletteuse graph
        /// through <see cref="Data.CodecArgs.ForcedFilters"/> - and the probe was made without those, so
        /// a GIF with nothing else configured mapped the source directly past a filtergraph whose output
        /// then went nowhere. FFmpeg refuses that outright ("Filter paletteuse:default has an
        /// unconnected output"), which is to say GIF could not be produced at all. Asking here also
        /// built the whole chain a second time, autocrop probes included, only to see if it was empty. </param>
        public static string GetMapArgs(bool videoOnly, bool noVideoEncode, bool hasFilterChain)
        {
            List<string> args = new List<string>();
            bool mappedChainOutput = false;
            bool seenFirstVideoStream = false;
            // Where QTGMC's deinterlaced frames come in, when this run is using it: the first video
            // track is then read off that pipe rather than out of the file it belongs to.
            int pipeInput = QuickConvertUi.DeinterlacePipeInput;

            foreach (StreamListEntry entry in GetMappedStreams(videoOnly, logDropped: !videoOnly))
            {
                FileListEntry correspondingFileEntry = FileList.Items.FirstOrDefault(x => x.File == entry.MediaFile);
                int fileIdx = RunTask.currentFileListMode == RunTask.FileListMode.Batch || correspondingFileEntry == null
                    ? 0
                    : FileList.Items.IndexOf(correspondingFileEntry);

                bool firstVideo = entry.Stream.Type == Stream.StreamType.Video && !seenFirstVideoStream;

                if (entry.Stream.Type == Stream.StreamType.Video)
                    seenFirstVideoStream = true;

                if (hasFilterChain && !mappedChainOutput && entry.Stream.Type == Stream.StreamType.Video && !noVideoEncode)
                {
                    args.Add($"-map [vf]");
                    mappedChainOutput = true;
                    continue;
                }

                args.Add(firstVideo && pipeInput >= 0 ? $"-map {pipeInput}:v:0" : $"-map {fileIdx}:{entry.Stream.Index}");
            }

            return string.Join(" ", args);
        }

        #region Stream Selection

        public static void CheckAll(bool check)
        {
            SetChecked(x => check);
        }

        public static void InvertSelection()
        {
            SetChecked(x => !x.IsChecked);
        }

        public static void CheckTracksOfType(Stream.StreamType type)
        {
            SetChecked(x => x.Stream.Type == type);
        }

        public static void CheckFirstOfEachType()
        {
            var firstVid = Items.FirstOrDefault(x => x.Stream.Type == Stream.StreamType.Video);
            var firstAud = Items.FirstOrDefault(x => x.Stream.Type == Stream.StreamType.Audio);
            var firstSub = Items.FirstOrDefault(x => x.Stream.Type == Stream.StreamType.Subtitle);

            SetChecked(x => x == firstVid || x == firstAud || x == firstSub);
        }

        public static void CheckFirstOfEachLangOfEachType()
        {
            List<string> checkedLangs = new List<string>();

            SetChecked(entry =>
            {
                string hash = $"{entry.Stream.Type}{entry.Stream.Language}";

                if (checkedLangs.Contains(hash))
                    return false;

                checkedLangs.Add(hash);
                return true;
            });
        }

        /// <summary> Applies a checked-state predicate to every track, firing a single UI refresh. </summary>
        private static void SetChecked(Func<StreamListEntry, bool> predicate)
        {
            Program.MainWin.ignoreStreamListCheck = true;

            foreach (StreamListEntry entry in Items)
                entry.SetCheckedQuiet(predicate(entry));

            Program.MainWin.ignoreStreamListCheck = false;
            Program.MainWin.OnCheckedStreamsChange();
        }

        #endregion

        #region Sort Tracks

        public enum TrackSort { Language, Title, Codec }

        public static void SortTracks(TrackSort sort, bool reverse)
        {
            List<StreamListEntry> itemsCopy = Items.ToList();
            Program.MainWin.ignoreStreamListCheck = true;
            Items.Clear();

            foreach (Stream.StreamType streamType in itemsCopy.Select(x => x.Stream.Type).Distinct())
            {
                var items = itemsCopy.Where(x => x.Stream.Type == streamType);
                List<StreamListEntry> sorted;

                if (sort == TrackSort.Language)
                    sorted = items.OrderBy(x => x.Stream.Language).ToList();
                else if (sort == TrackSort.Title)
                    sorted = items.OrderBy(x => x.Stream.Title).ToList();
                else
                    sorted = items.OrderBy(x => x.Stream.Codec).ToList();

                if (reverse)
                    sorted.Reverse();

                foreach (StreamListEntry entry in sorted)
                    Items.Add(entry);
            }

            Program.MainWin.ignoreStreamListCheck = false;
            Program.MainWin.OnCheckedStreamsChange();
        }

        #endregion
    }
}
