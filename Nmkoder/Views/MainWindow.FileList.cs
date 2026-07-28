using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Nmkoder.Data.Ui;
using Nmkoder.Main;
using Nmkoder.UI;
using Nmkoder.UI.Tasks;
using System;
using System.Collections.Generic;
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

            RunTask.FileListMode oldMode = RunTask.currentFileListMode;
            RunTask.FileListMode newMode = (RunTask.FileListMode)Math.Max(0, FileListModeBox.SelectedIndex);

            if (oldMode == RunTask.FileListMode.Mux && newMode == RunTask.FileListMode.Batch)
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
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Add media files",
                AllowMultiple = true
            });

            string[] paths = files.Select(x => x.TryGetLocalPath()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();

            if (paths.Length > 0)
                await ImportFiles(paths);
        }

        private async void AddFolder_Click(object sender, RoutedEventArgs e)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Add an image sequence folder",
                AllowMultiple = true
            });

            string[] paths = folders.Select(x => x.TryGetLocalPath()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();

            if (paths.Length > 0)
                await ImportFiles(paths);
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
