using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
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
            RefreshBatchNamingUi();

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

            // The loaded file has been taken out of the file list, so something else has to become the
            // loaded one. TrackList.Refresh has already removed exactly that file's streams and left
            // every other file's alone, which is why ClearCurrentFile is asked not to touch the list
            // and why the promotion below is asked the same - it used to clear it regardless, so
            // removing one file of several threw away the tracks of the ones that remained.
            if (TrackList.current != null && !FileList.Items.Any(x => x.File.Equals(TrackList.current.File)))
            {
                TrackList.ClearCurrentFile();

                if (RunTask.currentFileListMode == RunTask.FileListMode.Mux && FileList.Items.Count > 0)
                {
                    FileListEntry promoted = FileList.Items[0];
                    await TrackList.SetAsMainFile(promoted, false, clearStreamList: false);

                    // And if nothing of the promoted file's is in the list - the ordinary case, one
                    // file loaded and removed - its tracks are loaded, because everything else on
                    // screen now says that file is the loaded one. Left empty, the Track List showed
                    // nothing while the format label described the promotion, GetMappedStreams
                    // returned nothing, and the Run button still offered an encode with no -map
                    // arguments at all. Compared by path rather than by reference: MediaFile has no
                    // Equals of its own, which is what Refresh's own pruning goes by too.
                    if (!TrackList.Items.Any(x => x.MediaFile != null && x.MediaFile.ImportPath == promoted.File.ImportPath))
                        await TrackList.AddStreamsToList(promoted.File, promoted.RowBrush, false);
                }
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
            Title = $"NMKODER{(Program.Version.IsEmpty() ? "" : $" {Program.Version}")} [{(newMode == RunTask.FileListMode.Mux ? "Mux" : "Batch")}]";
            BatchNamingPanel.IsVisible = newMode == RunTask.FileListMode.Batch;
            UpdateRunButtonState(); // Run says "Run Batch: …" in one mode and "Run: …" in the other

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

        /// <summary>
        /// Loads the selected files' streams into the Track List, making the first one the main file
        /// if there is not one already.
        /// <para/>
        /// **<see cref="TrackList.SetAsMainFile"/> clears the track list, so it has to run *before*
        /// anything is added to it rather than after.** Written the other way round it added the first
        /// file's streams and then wiped them: the file whose tracks you asked for was the one file
        /// that did not appear, and with its video gone the *second* file's video stream became the
        /// checked one. Only the first iteration was ever affected, `current` being non-null from then
        /// on, which is what made it look like a selection bug rather than an ordering one.
        /// <para/>
        /// The way in is the ordinary one: dropping several files at once in Muxing Mode leaves
        /// `current` null - <see cref="FileList.HandleFiles"/> only sets a main file on the
        /// `Items.Count == 1` path - so pressing Load Tracks, or double-clicking a row, landed here
        /// with exactly the state the bug needed. The two call sites that already had this right
        /// (<see cref="ApplyFileListMode"/> and `HandleFiles`) are both main-file-then-add, which is
        /// the order to keep.
        /// </summary>
        private async void AddTracksFromFile_Click(object sender, RoutedEventArgs e)
        {
            AddTracksFromFileBtn.IsEnabled = false;

            foreach (FileListEntry entry in SelectedFileEntries)
            {
                if (TrackList.current == null)
                    await TrackList.SetAsMainFile(entry);

                await TrackList.AddStreamsToList(entry.File, entry.RowBrush, true);
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

        #region Batch output naming

        private void BatchNameTemplate_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_initialized)
                return;

            RefreshBatchNamingUi();
        }

        /// <summary>
        /// The hint beside the box, which is a worked example rather than a list of tokens: what
        /// "{name}_{codec}_{crf}" is going to call the first file in the queue answers the question
        /// the list only describes. The full list is behind the ? button.
        /// </summary>
        public void RefreshBatchNamingUi()
        {
            // A running queue owns the naming, and blanking the hint under it would only make the
            // row look broken for as long as the batch lasts.
            if (BatchNamingHintLabel == null || RunTask.runningBatch)
                return;

            BatchNamingHintLabel.Text = BatchNaming.DescribeExample(FileList.Items.FirstOrDefault()?.File, LastTaskTab);
        }

        private void BatchNamingHelp_Click(object sender, RoutedEventArgs e)
        {
            string tokens = string.Join("\n", BatchNaming.Tokens.Select(x => $"{x.Token}  -  {x.Description}"));
            UiUtils.ShowMessageBoxAsync("Batch mode names every output from this template, since one output box cannot " +
                "hold twelve names. Anything that is not a placeholder is used as written, so \"{name}-av1\" just adds " +
                "a suffix.\n\nThe folder is unaffected: outputs still go to the default output folder, or next to " +
                $"their source when none is set.\n\nPlaceholders:\n\n{tokens}\n\n" +
                "{width} and {height} come off the file itself, so they are blank in the example until the queue " +
                "reaches that file and scans it. Two files that resolve to the same name do not overwrite each " +
                "other - the second is numbered.", UiUtils.MessageType.Message);
        }

        /// <summary>
        /// Keeps the file a batch is working on visible. A queue of forty scrolls off the top within
        /// minutes, and the row indicators are only worth having if the running one can be seen.
        /// </summary>
        public void ScrollFileIntoView(FileListEntry entry)
        {
            if (entry == null)
                return;

            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    FileListBox.ScrollIntoView(entry);
                }
                catch (Exception e)
                {
                    Logger.Log($"Could not scroll the file list: {e.Message}", true);
                }
            });
        }

        #endregion

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
