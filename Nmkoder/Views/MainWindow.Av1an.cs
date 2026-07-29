using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Nmkoder.Data;
using Nmkoder.Data.Codecs;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Data.Ui;
using Nmkoder.UI;
using Nmkoder.UI.Tasks;
using Nmkoder.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Nmkoder.Views
{
    partial class MainWindow
    {
        public void InitAv1an()
        {
            Av1anAudChannelsBox.SelectedIndex = 1;
            Av1anCropBox.SelectedIndex = 0;
            Av1anOptsConcatModeBox.SelectedIndex = 0;
            Av1anOptsChunkOrderBox.SelectedIndex = 0;
        }

        private void Av1anCodec_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized)
                return;

            Av1anUi.VidEncoderSelected(Av1anCodecBox.SelectedIndex);
            SaveConfigAv1an();
        }

        private void Av1anQualityMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized)
                return;

            Av1an.QualityMode mode = Av1anUi.GetCurrentQualityMode();

            // Whole numbers unless the metric needs finer steps - butteraugli overrides below.
            Av1anQualityUpDown.Increment = 1;
            Av1anQualityUpDown.FormatString = "";

            if (mode == Av1an.QualityMode.TargetVmaf)
            {
                Av1anQualityUpDown.SetRange(10, 99);
                Av1anQualityUpDown.Value = 95;
            }
            else if (mode == Av1an.QualityMode.TargetSsimu2)
            {
                // SSIMULACRA2's anchors sit lower than VMAF's - 70 is high quality, 80
                // imperceptible side-by-side, 90 visually lossless - so 80 is the
                // counterpart of the VMAF default of 95 above.
                Av1anQualityUpDown.SetRange(30, 99);
                Av1anQualityUpDown.Value = 80;
            }
            else if (mode == Av1an.QualityMode.TargetButteraugli)
            {
                // Butteraugli runs the scale the other way: it measures distortion, so 0 is
                // identical and higher is worse, with ~1 the classic just-noticeable
                // distance. The useful targets sit between whole numbers, hence the tenths.
                // 2.0 is the counterpart of the defaults above - high quality without
                // paying for transparency.
                Av1anQualityUpDown.Increment = 0.1m;
                Av1anQualityUpDown.FormatString = "0.0##";
                Av1anQualityUpDown.SetRange(0.5m, 10);
                Av1anQualityUpDown.Value = 2.0m;
            }
            else
            {
                IEncoder enc = CodecUtils.GetCodec((CodecUtils.Av1anCodec)Math.Max(0, Av1anCodecBox.SelectedIndex));
                Av1anQualityUpDown.SetRange(enc.QMin, enc.QMax);
                Av1anQualityUpDown.SetValueClamped(enc.QDefault);
            }
        }

        private void Av1anContainer_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized)
                return;

            SaveConfigAv1an();
            Av1anUi.ValidateContainer();
        }

        private void Av1anAudCodec_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized)
                return;

            SaveConfigAv1an();
            Av1anUi.AudEncoderSelected(Av1anAudCodecBox.SelectedIndex);
        }

        private void Av1anOption_SelectionChanged(object sender, SelectionChangedEventArgs e) => SaveConfigAv1an();
        private void Av1anOption_ValueChanged(object sender, NumericUpDownValueChangedEventArgs e) => SaveConfigAv1an();

        private void Av1anCrop_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Av1anCropConfBtn.IsVisible = Av1anCropBox.GetText().ToLower().Contains("manual");
        }

        private async void Av1anCropConf_Click(object sender, RoutedEventArgs e)
        {
            Size res = new Size();

            if (TrackList.current != null && TrackList.current.File.VideoStreams.Count > 0)
                res = TrackList.current.File.VideoStreams[0].Resolution;

            CropConfig crop = await CropWindow.Show(res, Av1anUi.CurrentCrop);

            if (crop != null)
                Av1anUi.CurrentCrop = crop;
        }

        public void LoadConfigAv1an()
        {
            ConfigParser.LoadComboxIndex(Av1anContainerBox);
            ConfigParser.LoadComboxIndex(Av1anCodecBox);
            ConfigParser.LoadComboxIndex(Av1anAudCodecBox);
            ConfigParser.LoadComboxIndex(Av1anOptsChunkModeBox);
            ConfigParser.LoadComboxIndex(Av1anOptsSplitModeBox);
            ConfigParser.LoadComboxIndex(Av1anOptsConcatModeBox);
            ConfigParser.LoadComboxIndex(Av1anOptsChunkOrderBox);
            ConfigParser.LoadGuiElement(Av1anOptsWorkerCountUpDown, false);
            ConfigParser.LoadGuiElement(Av1anThreadsUpDown, false);
        }

        public void SaveConfigAv1an()
        {
            if (!_initialized)
                return;

            ConfigParser.SaveComboxIndex(Av1anContainerBox);
            ConfigParser.SaveComboxIndex(Av1anCodecBox);
            ConfigParser.SaveComboxIndex(Av1anAudCodecBox);
            ConfigParser.SaveComboxIndex(Av1anOptsChunkModeBox);
            ConfigParser.SaveComboxIndex(Av1anOptsSplitModeBox);
            ConfigParser.SaveComboxIndex(Av1anOptsConcatModeBox);
            ConfigParser.SaveComboxIndex(Av1anOptsChunkOrderBox);
            ConfigParser.SaveGuiElement(Av1anOptsWorkerCountUpDown, ConfigParser.StringMode.Int);
            ConfigParser.SaveGuiElement(Av1anThreadsUpDown, ConfigParser.StringMode.Int);
        }

        /// <summary> The category tabs' grids, kept for committing pending edits on save. </summary>
        private readonly List<DataGrid> _av1anArgGrids = new List<DataGrid>();

        /// <summary>
        /// Rebuilds the Advanced tab's category tabs from whatever is in Av1anArgRows, one tab per
        /// category in the order the encoder's JSON introduces them. Each grid shows the master
        /// list's own row objects, so edits land in Av1anArgRows and everything reading it - arg
        /// building, saving, the hbd-mds warning - is unaffected by the grouping.
        /// </summary>
        public void LoadAv1anArgCategoryTabs()
        {
            // Encoders share category names, so the open category survives an encoder switch
            string selected = (Av1anArgCategoryTabs.SelectedItem as TabItem)?.Header?.ToString();
            _av1anArgGrids.Clear();
            List<TabItem> tabs = new List<TabItem>();

            foreach (var category in Av1anArgRows.GroupBy(r => r.Category.IsEmpty() ? "Other" : r.Category))
            {
                DataGrid grid = CreateAv1anArgsGrid(category.ToList());
                _av1anArgGrids.Add(grid);
                tabs.Add(new TabItem { Header = category.Key, Content = grid });
            }

            // A missing or unparseable JSON leaves no rows at all. Without this the heading would sit
            // over a bare void; an empty grid keeps the column chrome visible, like the flat grid did.
            if (tabs.Count == 0)
                tabs.Add(new TabItem { Header = "Arguments", Content = CreateAv1anArgsGrid(new List<EncoderArgRow>()) });

            Av1anArgCategoryTabs.ItemsSource = tabs;
            int index = tabs.FindIndex(t => t.Header?.ToString() == selected);
            Av1anArgCategoryTabs.SelectedIndex = index >= 0 ? index : 0;
        }

        /// <summary> One category's grid, with the same columns the single flat grid used to have. </summary>
        private DataGrid CreateAv1anArgsGrid(List<EncoderArgRow> rows)
        {
            DataGrid grid = new DataGrid
            {
                ItemsSource = rows,
                AutoGenerateColumns = false,
                CanUserSortColumns = false,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HeadersVisibility = DataGridHeadersVisibility.Column,
            };

            // Values are nearly always a single digit, so the column is sized for one rather than
            // splitting the width evenly; the descriptions take the slack. The argument column is a
            // fixed width rather than Auto because every category tab is its own grid - each would
            // measure its own longest name, and the columns would jump when switching tabs.
            grid.Columns.Add(new DataGridTextColumn { Header = "Argument", Binding = new Binding(nameof(EncoderArgRow.Argument)), IsReadOnly = true, Width = new DataGridLength(250) });
            grid.Columns.Add(new DataGridTextColumn { Header = "Value", Binding = new Binding(nameof(EncoderArgRow.Value)), Width = new DataGridLength(110) });
            grid.Columns.Add(new DataGridTextColumn { Header = "Description, Possible Values", Binding = new Binding(nameof(EncoderArgRow.Description)), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            grid.CellEditEnded += Av1anAdvancedArg_CellEditEnded;

            // The description is one clipped line, and narrowing the window clips it further, so the
            // row carries the whole of it as a tooltip - the ranges and defaults are the point of it.
            grid.LoadingRow += (s, e) =>
            {
                if (e.Row.DataContext is EncoderArgRow row)
                    ToolTip.SetTip(e.Row, $"--{row.Argument}\n{row.Description}\n\nRight-click for details and examples.");
            };

            // Right-clicking a row opens its long-form help. Handled on the grid rather than on each
            // row: rows are recycled as the list scrolls, so per-row subscriptions would stack up.
            // The cells inherit the row's DataContext, so whatever was clicked names the argument.
            grid.ContextRequested += async (s, e) =>
            {
                e.Handled = true;

                if (e.Source is Control control && control.DataContext is EncoderArgRow row)
                    await EncoderArgInfoWindow.Show(row);
            };

            return grid;
        }

        /// <summary>
        /// Saved as each cell is committed, not only on close: selecting another encoder rebuilds
        /// the grids from that encoder's list, so anything not already stored would be gone.
        /// </summary>
        private void Av1anAdvancedArg_CellEditEnded(object sender, DataGridCellEditEndedEventArgs e)
        {
            SaveAv1anAdvancedArgs();
        }

        /// <summary>
        /// Also runs on close, where the edit has to be committed first: a cell the user is still
        /// typing in has not ended its edit, so nothing would have saved that last value.
        /// </summary>
        public void SaveAv1anAdvancedArgs()
        {
            if (!_initialized)
                return;

            foreach (DataGrid grid in _av1anArgGrids)
                grid.CommitEdit();

            Av1anUi.SaveAdvancedArgs(CodecUtils.GetCodec(Av1anUi.GetCurrentCodecV()));
        }

        private void Av1anFilterAdd_Click(object sender, RoutedEventArgs e)
        {
            Av1anFilterRows.Add(new FilterRow(""));
        }

        private void Av1anFilterRemove_Click(object sender, RoutedEventArgs e)
        {
            if (Av1anAdvancedFiltersGrid.SelectedItem is FilterRow row)
                Av1anFilterRows.Remove(row);
            else if (Av1anFilterRows.Count > 0)
                Av1anFilterRows.RemoveAt(Av1anFilterRows.Count - 1);
        }

        private async void BrowseAv1anOutput_Click(object sender, RoutedEventArgs e)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Choose output path",
                SuggestedFileName = Path.GetFileName(Av1anOutputPathBox.Text ?? "")
            });

            string path = file?.TryGetLocalPath();

            if (!string.IsNullOrWhiteSpace(path))
                Av1anOutputPathBox.Text = Path.ChangeExtension(path, null);
        }

        /// <summary>
        /// Throws away every unfinished encode's temp files. Nothing else clears them - a stopped encode
        /// keeps its folder on purpose and Cleanup never touches the directory - so without this the
        /// only way to reclaim the space is one entry at a time in the resume window, or by hand.
        /// Confirmed first, and with the size shown, because what it deletes is finished encoding work.
        /// </summary>
        private async void Av1anClearTemp_Click(object sender, RoutedEventArgs e)
        {
            var pending = Av1anUi.GetResumableEncodes();

            if (pending.Count < 1)
            {
                Logger.Log("There are no av1an temp files to clear.");
                Av1anUi.RefreshResumeButton();
                return;
            }

            long bytes = pending.Sum(x => x.ChunkFiles.Sum(f => f.Length));
            string msg = $"Delete the temporary files of {pending.Count} unfinished encode{(pending.Count == 1 ? "" : "s")}?\n\n" +
                $"That frees {FormatUtils.Bytes(bytes)} of encoded chunks. None of them can be resumed afterwards - " +
                $"each would have to start over from the beginning.";

            if (await UiUtils.ShowMessageBox(msg, "Clear all av1an temp files?", UiUtils.MessageButtons.YesNo) != UiUtils.DialogResult.Yes)
                return;

            foreach (var entry in pending)
                Av1anUi.DeleteTempFolder(entry.DirInfo.FullName);

            var left = Av1anUi.GetResumableEncodes();
            int removed = pending.Count - left.Count;
            Logger.Log($"Cleared {removed} av1an temp folder{(removed == 1 ? "" : "s")}, freeing {FormatUtils.Bytes(bytes)}." +
                (left.Count > 0 ? $" {left.Count} could not be removed - they may be in use." : ""));

            Av1anUi.RefreshResumeButton();
        }

        private async void Av1anResume_Click(object sender, RoutedEventArgs e)
        {
            Av1anResumeWindow window = await Av1anResumeWindow.ShowAsync();

            if (window.ChosenEntry == null || !window.Resume)
                return;

            // LoadJson returns an empty dict on failure, so an unusable entry shows up as a missing temp folder name
            if (window.ChosenEntry.jsonInfo == null || window.ChosenEntry.TempFolderName.IsEmpty())
            {
                Logger.Log($"Cannot resume - Failed to load info from JSON.");
                return;
            }

            if (window.ChosenEntry.InputFile == null || !File.Exists(window.ChosenEntry.InputFile.FullName))
            {
                Logger.Log($"Cannot resume - Input file doesn't seem to exist at '{window.ChosenEntry.InputFile}'.");
                return;
            }

            if (window.UseSavedCommand)
                await Av1an.RunResumeWithSavedArgs(window.ChosenEntry.TempFolderName, window.ChosenEntry.Args);
            else
                await Av1an.RunResumeWithNewArgs(window.ChosenEntry.InputFile.FullName, window.ChosenEntry.TempFolderName);
        }
    }
}
