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

            if (resetAll || ResetSettingsOnNewFile.ResetResize)
            {
                f.EncScaleBoxW.Text = f.EncScaleBoxH.Text = f.Av1anScaleBoxW.Text = f.Av1anScaleBoxH.Text = "";
                clearedSettings.Add(ResetSettingsOnNewFile.NiceNames[nameof(ResetSettingsOnNewFile.ResetResize)]);
            }

            if (resetAll || ResetSettingsOnNewFile.ResetFpsResample)
            {
                f.EncVidFpsBox.Text = f.Av1anFpsBox.Text = "";
                clearedSettings.Add(ResetSettingsOnNewFile.NiceNames[nameof(ResetSettingsOnNewFile.ResetFpsResample)]);
            }

            if (resetAll || ResetSettingsOnNewFile.ResetCustomInArgs)
            {
                f.CustomArgsInBox.Text = "";
                clearedSettings.Add(ResetSettingsOnNewFile.NiceNames[nameof(ResetSettingsOnNewFile.ResetCustomInArgs)]);
            }

            if (resetAll || ResetSettingsOnNewFile.ResetCustomOutArgs)
            {
                f.CustomArgsOutBox.Text = "";
                clearedSettings.Add(ResetSettingsOnNewFile.NiceNames[nameof(ResetSettingsOnNewFile.ResetCustomOutArgs)]);
            }

            if (resetAll || ResetSettingsOnNewFile.ResetCustomFilters)
            {
                f.EncFilterRows.Clear();
                f.Av1anFilterRows.Clear();
                clearedSettings.Add(ResetSettingsOnNewFile.NiceNames[nameof(ResetSettingsOnNewFile.ResetCustomFilters)]);
            }

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
            currentAudioConfig = null;

            QuickConvertUi.InitFile(current.File.SourcePath);
            Av1anUi.InitFile(current.File.SourcePath);

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

        public static string GetInputFilesString()
        {
            if (RunTask.currentFileListMode == RunTask.FileListMode.Batch)
            {
                if (current.File.IsDirectory)
                    return $"-safe 0 -f concat -r {current.File.InputRate} -i {current.File.ImportPath.Wrap()}";
                else
                    return $"-i {current.File.ImportPath.Wrap()}";
            }

            List<string> args = new List<string>();

            foreach (FileListEntry entry in FileList.Items)
            {
                if (entry.File.IsDirectory)
                    args.Add($"-safe 0 -f concat -r {entry.File.InputRate} -i {entry.File.ImportPath.Wrap()}");
                else
                    args.Add($"-i {entry.File.ImportPath.Wrap()}");
            }

            Logger.Log($"Input Args: {string.Join(" ", args)}", true);
            return string.Join(" ", args);
        }

        public static async Task<string> GetMapArgs(IEncoder enc, bool videoOnly = false, bool noVideoEncode = false, bool accountForFilterChain = true)
        {
            List<string> args = new List<string>();
            bool hasSkippedFirstVideoStream = false;
            Containers.Container container = QuickConvertUi.GetCurrentContainer();
            List<string> dropped = new List<string>();

            foreach (StreamListEntry entry in CheckedItems)
            {
                FileListEntry correspondingFileEntry = FileList.Items.FirstOrDefault(x => x.File == entry.MediaFile);
                int fileIdx = RunTask.currentFileListMode == RunTask.FileListMode.Batch || correspondingFileEntry == null
                    ? 0
                    : FileList.Items.IndexOf(correspondingFileEntry);

                if (videoOnly && entry.Stream.Type != Stream.StreamType.Video) // Skip all non-video streams if videoOnly == true
                    continue;

                // Data and attachment tracks are ticked by default, and almost no container takes them:
                // attachments are a Matroska feature, data streams a QuickTime one. Left in they fail the
                // mux, so they are dropped and named. Refusing the encode instead would be worse - there
                // is no way to keep them in a container that cannot hold them, so the user would only be
                // told to untick tracks they never chose.
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

                if (accountForFilterChain && !hasSkippedFirstVideoStream && entry.Stream.Type == Stream.StreamType.Video && !noVideoEncode)
                {
                    if (!string.IsNullOrWhiteSpace(await QuickConvertUi.GetVideoFilterArgs(enc, null, true)))
                    {
                        args.Add($"-map [vf]");
                        hasSkippedFirstVideoStream = true;
                        continue;
                    }
                }

                args.Add($"-map {fileIdx}:{entry.Stream.Index}");
            }

            foreach (var kind in dropped.GroupBy(x => x))
                Logger.Log($"{kind.Count()} {kind.Key} track{(kind.Count() == 1 ? "" : "s")} left out: {container.ToString().ToUpper()} cannot store {kind.Key} streams.");

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
