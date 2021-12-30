﻿using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using ImageMagick;
using Nmkoder.UI.Tasks;
using Nmkoder.Main;
using Nmkoder.Data;
using Nmkoder.Data.Ui;
using Nmkoder.Properties;
using System;
using Nmkoder.UI;

namespace Nmkoder.Forms
{
    partial class MainForm
    {
        public ListView fileListBox { get { return fileList; } }
        public ComboBox fileListModeBox { get { return fileListMode; } }

        public void RefreshFileListUi ()
        {
            addTracksFromFileBtn.Visible = RunTask.currentFileListMode == RunTask.FileListMode.MultiFileInput && fileList.SelectedItems.Count > 0;
            addTracksFromFileBtn.Text = AreAnyTracksLoaded() ? "Add Tracks To List" : "Load File";
        }

        private async void fileListMode_SelectedIndexChanged(object sender, EventArgs e)
        {
            RunTask.FileListMode oldMode = RunTask.currentFileListMode;
            RunTask.FileListMode newMode = (RunTask.FileListMode)fileListMode.SelectedIndex;

            if (oldMode == RunTask.FileListMode.MultiFileInput && newMode == RunTask.FileListMode.BatchProcess)
            {
                TrackList.ClearCurrentFile();
            }

            RunTask.currentFileListMode = newMode;

            Text = $"NMKODER [{(RunTask.currentFileListMode == RunTask.FileListMode.MultiFileInput ? "MFM" : "BPM")}]";

            SaveUiConfig();
            RefreshFileListUi();

            if (oldMode == RunTask.FileListMode.BatchProcess && newMode == RunTask.FileListMode.MultiFileInput)
            {
                if (fileList.Items.Count == 1 && !AreAnyTracksLoaded())
                    await TrackList.LoadFirstFile(fileList.Items[0]);
            }
        }

        private async void addTracksFromFileBtn_Click(object sender, EventArgs e)
        {
            addTracksFromFileBtn.Enabled = false;

            foreach (ListViewItem item in fileList.SelectedItems.Cast<ListViewItem>())
            {
                if (AreAnyTracksLoaded())
                    await TrackList.AddStreamsToList(((FileListEntry)item.Tag).File, item.BackColor, true);
                else
                    await TrackList.LoadFirstFile(item);
            }

            QuickConvertUi.LoadMetadataGrid();
            addTracksFromFileBtn.Enabled = true;
        }

        private void fileList_SelectedIndexChanged(object sender = null, EventArgs e = null)
        {
            RefreshFileListUi();
        }

        private void fileListCleanBtn_Click(object sender, EventArgs e)
        {
            foreach(ListViewItem item in fileList.SelectedItems)
                fileList.Items.Remove(item);

            TrackList.Refresh();
        }

        private void fileListMoveUpBtn_Click(object sender, EventArgs e)
        {
            UiUtils.MoveListViewItem(fileList, UiUtils.MoveDirection.Up);
        }

        private void fileListMoveDownBtn_Click(object sender, EventArgs e)
        {
            UiUtils.MoveListViewItem(fileList, UiUtils.MoveDirection.Down);
        }

        private bool AreAnyTracksLoaded ()
        {
            return streamList.Items.Count > 0;
        }

        private void fileListSortBtn_Click(object sender, EventArgs e)
        {
            sortFileListContextMenu.Show(Cursor.Position);
        }

        private void fileList_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            ListViewItem item = fileList.HitTest(e.X, e.Y).Item;

            if (item != null && RunTask.currentFileListMode == RunTask.FileListMode.MultiFileInput)
                addTracksFromFileBtn_Click(null, null);
        }
    }
}
