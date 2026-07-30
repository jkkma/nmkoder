using Avalonia.Controls;
using Avalonia.Interactivity;
using Nmkoder.Data.Ui;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.UI;
using Nmkoder.UI.Tasks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Nmkoder.Views
{
    partial class MainWindow
    {
        private IEnumerable<FileListEntry> SelectedFileEntries => FileListBox.SelectedItems?.Cast<FileListEntry>().ToList() ?? new List<FileListEntry>();

        public async Task RefreshFileListUi()
        {
            int selectedCount = FileListBox.SelectedItems?.Count ?? 0;
            bool anySelected = selectedCount > 0;
            bool oneSelected = selectedCount == 1;

            FileListMoveUpBtn.IsEnabled = oneSelected;
            FileListMoveDownBtn.IsEnabled = oneSelected;
            FileListRemoveBtn.IsEnabled = anySelected;
            AddTracksFromFileBtn.IsEnabled = RunTask.currentFileListMode == RunTask.FileListMode.Mux && anySelected;

            int count = FileList.Items.Count;
            FileListEmptyHint.IsVisible = count < 1;

            FileCountLabel.Text = $"{count} file{(count != 1 ? "s" : "")} loaded. " +
                $"{(count > 1 && RunTask.currentFileListMode == RunTask.FileListMode.Mux ? "Double click any of them or use the Load Tracks button to load their tracks." : "")}";

            if (TrackList.current != null && !FileList.Items.Any(x => x.File.Equals(TrackList.current.File)))
            {
                TrackList.ClearCurrentFile();

                if (RunTask.currentFileListMode == RunTask.FileListMode.Mux && FileList.Items.Count > 0)
                    await TrackList.SetAsMainFile(FileList.Items[0], false);
            }

            QuickConvertUi.RefreshFileListRelatedOptions();
        }

        private async void FileListMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized)
                return;

            await ApplyFileListMode();
        }

        /// <summary>
        /// Brings the rest of the app in line with the mode dropdown. Separate from the handler
        /// because the saved mode is restored into the box during startup, before the handler is
        /// allowed to do anything - which left the box reading "Batch Processing Mode" while
        /// everything that asks <see cref="RunTask.currentFileListMode"/> was still told Mux, so a
        /// restored batch session muxed its whole file list into one output instead.
        /// </summary>
        public async Task ApplyFileListMode()
        {
            RunTask.FileListMode oldMode = RunTask.currentFileListMode;
            RunTask.FileListMode newMode = (RunTask.FileListMode)Math.Max(0, FileListModeBox.SelectedIndex);

            // Only when there is something loaded to unload: at startup this would otherwise reset
            // the encode settings that were just restored, over a file that was never opened.
            if (oldMode == RunTask.FileListMode.Mux && newMode == RunTask.FileListMode.Batch && (TrackList.current != null || TrackList.Items.Count > 0))
                TrackList.ClearCurrentFile(true);

            RunTask.currentFileListMode = newMode;
            Title = $"NMKODER [{(newMode == RunTask.FileListMode.Mux ? "Mux" : "Batch")}]";

            SaveUiConfig();
            await RefreshFileListUi();

            if (oldMode == RunTask.FileListMode.Batch && newMode == RunTask.FileListMode.Mux)
            {
                if (FileList.Items.Count == 1 && TrackList.Items.Count < 1)
                {
                    await TrackList.SetAsMainFile(FileList.Items[0]);
                    await TrackList.AddStreamsToList(FileList.Items[0].File, FileList.Items[0].RowBrush, true);
                }
            }
        }

        private async void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            string[] paths = await Pickers.PickFiles(this, "Add media files", allowMultiple: true);

            if (paths.Length > 0)
                await ImportFiles(paths);
        }

        private async void AddFolder_Click(object sender, RoutedEventArgs e)
        {
            string[] paths = await Pickers.PickFolders(this, "Add an image sequence folder", allowMultiple: true);

            if (paths.Length > 0)
                await ImportFiles(paths);
        }

        /// <summary>
        /// The recently loaded files, as a dropdown off the button rather than a list somewhere on
        /// the tab: it is a shortcut for getting back to a file, not something to look at.
        /// </summary>
        private void FileListRecent_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();

            foreach (string path in RecentFiles.Get())
            {
                var item = new MenuItem { Header = RecentMenuHeader(path) };
                item.Click += async (s, args) => await OpenRecent(path);
                menu.Items.Add(item);
            }

            if (menu.Items.Count > 0)
                menu.Items.Add(new Separator());

            var clear = new MenuItem { Header = "Clear List", IsEnabled = menu.Items.Count > 0 };
            clear.Click += (s, args) => { RecentFiles.Clear(); RefreshRecentFilesButton(); };
            menu.Items.Add(clear);

            menu.Open(FileListRecentBtn);
        }

        /// <summary>
        /// Whether an entry is still there is only ever asked here, off the UI thread, and only
        /// about the one entry that was clicked - a file on an unplugged drive is worth keeping in
        /// the list, and asking about all of them up front is what makes the menu hang.
        /// </summary>
        private async Task OpenRecent(string path)
        {
            if (!await Task.Run(() => RecentFiles.Exists(path)))
            {
                Logger.Log($"'{Path.GetFileName(path)}' is no longer where it was, so it has been dropped from the recent files.");
                RecentFiles.Remove(path);
                RefreshRecentFilesButton();
                return;
            }

            await ImportFiles(new[] { path });
        }

        /// <summary> Nothing to drop down to is worth saying before the click rather than after. </summary>
        public void RefreshRecentFilesButton()
        {
            FileListRecentBtn.IsEnabled = RecentFiles.Get().Count > 0;
        }

        /// <summary>
        /// File name first, since that is what is being looked for, with the folder after it
        /// because two files being told apart by their folder is the whole reason to show one.
        /// </summary>
        private static string RecentMenuHeader(string path)
        {
            string dir = Path.GetDirectoryName(path) ?? "";

            if (dir.Length > 64) // A menu as wide as the deepest path in it is unreadable
                dir = "…" + dir.Substring(dir.Length - 63);

            string header = dir.IsEmpty() ? path : $"{Path.GetFileName(path)}   ({dir})";

            // Avalonia reads an underscore in a menu header as an access key marker and swallows
            // it, and file names are full of them.
            return header.Replace("_", "__");
        }

        private async void AddTracksFromFile_Click(object sender, RoutedEventArgs e)
        {
            AddTracksFromFileBtn.IsEnabled = false;

            foreach (FileListEntry entry in SelectedFileEntries)
            {
                await TrackList.AddStreamsToList(entry.File, entry.RowBrush, true);

                if (TrackList.current == null)
                    await TrackList.SetAsMainFile(entry);
            }

            QuickConvertUi.LoadMetadataGrid();
            AddTracksFromFileBtn.IsEnabled = true;
        }

        private async void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            await RefreshFileListUi();
        }

        private void FileList_DoubleTapped(object sender, Avalonia.Input.TappedEventArgs e)
        {
            if (FileListBox.SelectedItem != null && RunTask.currentFileListMode == RunTask.FileListMode.Mux)
                AddTracksFromFile_Click(null, null);
        }

        private async void FileListRemove_Click(object sender, RoutedEventArgs e)
        {
            foreach (FileListEntry entry in SelectedFileEntries)
                FileList.Items.Remove(entry);

            await TrackList.Refresh();
        }

        private void FileListMoveUp_Click(object sender, RoutedEventArgs e)
        {
            UiUtils.MoveItem(FileList.Items, FileListBox.SelectedItem as FileListEntry, UiUtils.MoveDirection.Up);
        }

        private void FileListMoveDown_Click(object sender, RoutedEventArgs e)
        {
            UiUtils.MoveItem(FileList.Items, FileListBox.SelectedItem as FileListEntry, UiUtils.MoveDirection.Down);
        }

        private void FileListSort_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();

            void AddSort(string header, Func<IEnumerable<FileListEntry>, IEnumerable<FileListEntry>> sorter)
            {
                var item = new MenuItem { Header = header };
                item.Click += (s, args) => UiUtils.ReplaceAll(FileList.Items, sorter(FileList.Items.ToList()).ToList());
                menu.Items.Add(item);
            }

            AddSort("Name (A-Z)", x => x.OrderBy(f => f.File.SourcePath));
            AddSort("Name (Z-A)", x => x.OrderByDescending(f => f.File.SourcePath));
            AddSort("Size (Largest First)", x => x.OrderByDescending(f => f.File.Size));
            AddSort("Size (Smallest First)", x => x.OrderBy(f => f.File.Size));
            AddSort("Most Recent First", x => x.OrderByDescending(f => f.File.FileInfo?.LastWriteTime));
            AddSort("Oldest First", x => x.OrderBy(f => f.File.FileInfo?.LastWriteTime));

            menu.Open(FileListSortBtn);
        }
    }
}
