using Avalonia.Media;
using Nmkoder.Data;
using Nmkoder.Data.Ui;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.UI.Tasks;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Nmkoder.UI
{
    class FileList
    {
        /// <summary>
        /// The input files. The main window's list is bound to this collection, so mutating it here
        /// updates the UI - this replaces the old "poke ListViewItems into the ListView" approach.
        /// </summary>
        public static ObservableCollection<FileListEntry> Items { get; } = new ObservableCollection<FileListEntry>();

        public static List<MediaFile> currentFiles = new List<MediaFile>();

        /// <summary> Neutral grey, matching the palette's disabled/chrome tone. </summary>
        private static readonly IBrush FirstRowBrush = new SolidColorBrush(Color.FromRgb(0x6D, 0x6F, 0x78));

        /// <summary>
        /// The stripe that tags a file in the list, and its tracks in the Track List. It carries the
        /// palette's pastel weight - the same saturation and lightness as the accent - with the hue
        /// advanced by the golden angle per file, so any number of files stays distinguishable
        /// without any of them landing outside the palette.
        /// </summary>
        private static IBrush GetRowBrush(int index)
        {
            if (index == 0)
                return FirstRowBrush; // The first file is the muxing target, so it stays neutral

            double hue = (150.0 + index * 137.508) % 360.0;
            return new SolidColorBrush(new HslColor(1.0, hue, 0.49, 0.65).ToRgb());
        }

        public static async Task LoadFiles(string[] paths, bool clearExisting)
        {
            if (clearExisting)
                Items.Clear();

            foreach (string file in paths)
            {
                MediaFile mediaFile = await MediaFile.CreateAsync(file); // Create MediaFile without initializing
                FileListEntry entry = new FileListEntry(mediaFile) { RowBrush = GetRowBrush(Items.Count) };
                Items.Add(entry);
            }

            await Program.MainWin.RefreshFileListUi();
        }

        public static async Task HandleFiles(string[] paths, bool clearExisting)
        {
            if (paths == null || paths.Length == 0)
                return;

            Media.GetFrameCountCached.ClearCache();
            Media.GetMediaResolutionCached.ClearCache();
            Media.GetVideoInfo.ClearCache();

            if (clearExisting)
            {
                ThumbnailView.ClearUi();
                TrackList.ClearCurrentFile();
                Logger.ClearLogBox();
            }

            Program.MainWin.SelectedMainTab = 0;

            // Every way a file gets in - the buttons, drag & drop, the command line - ends up here,
            // so this is the one place that sees all of them and none of them twice.
            RecentFiles.Add(paths);
            Program.MainWin.RefreshRecentFilesButton();

            Logger.Log($"Added {paths.Length} file{((paths.Length == 1) ? "" : "s")} to list.");
            await LoadFiles(paths, clearExisting);

            if (RunTask.currentFileListMode == RunTask.FileListMode.Mux)
            {
                if (Items.Count == 1)
                {
                    await TrackList.SetAsMainFile(Items[0]);
                    await TrackList.AddStreamsToList(Items[0].File, Items[0].RowBrush, true);
                }
                else
                {
                    QuickConvertUi.InitFile();
                }
            }
        }
    }
}
