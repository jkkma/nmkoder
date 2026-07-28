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

        public static async Task LoadFiles(string[] paths, bool clearExisting)
        {
            if (clearExisting)
                Items.Clear();

            Random r = new Random();

            foreach (string file in paths)
            {
                MediaFile mediaFile = await MediaFile.CreateAsync(file); // Create MediaFile without initializing
                FileListEntry entry = new FileListEntry(mediaFile);
                entry.RowBrush = Items.Count == 0
                    ? new SolidColorBrush(Color.FromRgb(64, 64, 64))
                    : new SolidColorBrush(Color.FromRgb((byte)r.Next(16, 128), (byte)r.Next(16, 128), (byte)r.Next(16, 128)));
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
