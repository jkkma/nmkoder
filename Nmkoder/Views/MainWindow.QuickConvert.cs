using Avalonia.Controls;
using Avalonia.Interactivity;
using Nmkoder.Data;
using Nmkoder.Data.Codecs;
using Nmkoder.Data.Streams;
using Nmkoder.Data.Ui;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.UI;
using Nmkoder.UI.Tasks;
using System;
using System.IO;
using System.Linq;
using Stream = Nmkoder.Data.Streams.Stream;

namespace Nmkoder.Views
{
    partial class MainWindow
    {
        public void InitQuickConvert()
        {
            EncAudChannelsBox.SelectedIndex = 0;
            EncCropModeBox.SelectedIndex = 0;
        }

        private void EncVidCodec_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized)
                return;

            QuickConvertUi.VidEncoderSelected(EncVidCodecsBox.SelectedIndex);
            SaveUiConfig();
        }

        private void EncAudCodec_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized)
                return;

            QuickConvertUi.AudEncoderSelected(EncAudCodecBox.SelectedIndex);
            SaveUiConfig();
        }

        private void Container_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized)
                return;

            QuickConvertUi.ValidateContainer();
            SaveUiConfig();
        }

        private void EncQualityMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized)
                return;

            ApplyEncQualityMode(useModeDefault: true);
        }

        /// <summary>
        /// The range, step size and formatting the quality spinner takes from the selected mode.
        /// The mode's own default value comes with them when the user picks a mode, but not when a
        /// saved mode is being restored at startup: there the saved value follows immediately
        /// behind, and would only be overwritten on its way in.
        /// </summary>
        private void ApplyEncQualityMode(bool useModeDefault)
        {
            // Through the same accessor the encoder's own spinner range and the run itself read, so a
            // fixed format is treated as CRF here too rather than as whatever its disabled box shows
            var mode = QuickConvertUi.GetEffectiveQualityMode();

            if (mode == QuickConvert.QualityMode.TargetKbps)
            {
                EncVidQualityBox.FormatString = "0";
                EncVidQualityBox.SetRange(10, 100000);

                if (useModeDefault)
                    EncVidQualityBox.Value = 1500;
            }
            else if (mode == QuickConvert.QualityMode.TargetMbytes)
            {
                EncVidQualityBox.FormatString = "0.0";
                EncVidQualityBox.SetRange(0, 8192);

                if (useModeDefault)
                    EncVidQualityBox.Value = 50;
            }
            else
            {
                IEncoder enc = CodecUtils.GetCodec((CodecUtils.VideoCodec)Math.Max(0, EncVidCodecsBox.SelectedIndex));
                EncVidQualityBox.FormatString = "0";
                EncVidQualityBox.SetRange(enc.QMin.Clamp(0, int.MaxValue), enc.QMax.Clamp(0, int.MaxValue));

                if (useModeDefault)
                    EncVidQualityBox.SetValueClamped(enc.QDefault.Clamp(0, int.MaxValue));
            }
        }

        /// <summary>
        /// The Quick Convert encode settings, restored on top of the defaults the selected encoder
        /// has just written into these controls - which is why this cannot run any earlier than it
        /// does. Everything the loaded file decides is left out: subtitle burn-in and the metadata
        /// sources list that file's own tracks, and crop is per-file by design, being one of the
        /// settings Reset On New File clears and having a rectangle that is not saved either.
        /// </summary>
        public void LoadQuickConvertSettings()
        {
            ConfigParser.RestoreIndexIfSaved(EncQualModeBox);
            ApplyEncQualityMode(useModeDefault: false); // The restored mode decides the range the value below is clamped into
            ConfigParser.RestoreIfSaved(EncVidQualityBox);
            ConfigParser.RestoreIfSaved(EncVidPresetBox);
            ConfigParser.RestoreIfSaved(EncVidColorsBox);
            ConfigParser.RestoreIfSaved(EncVidFpsBox);
            ConfigParser.RestoreIndexIfSaved(EncDeintModeBox);
            ConfigParser.RestoreIfSaved(EncDeintPresetBox);
            ConfigParser.RestoreIfSaved(EncDeintDoubleRateBox);
            ConfigParser.RestoreIfSaved(EncAudQualUpDown, allowFloat: false);
            ConfigParser.RestoreIndexIfSaved(EncAudChannelsBox);
            ConfigParser.RestoreIfSaved(EncCustomArgsIn);
            ConfigParser.RestoreIfSaved(EncCustomArgsOut);
            ConfigParser.RestoreIfSaved(EncMetaApplyGrid);
            ConfigParser.LoadFilterRows(Config.Key.EncCustomFilters, EncFilterRows);

            // Restored like the resize beside it, and for the same reason: "everything I encode comes
            // out 16:9" is a preference about output rather than a fact about the file that happens to
            // be loaded. The selection has to be pushed back into the config object by hand - the box's
            // own handler bails until _initialized, which is not set yet here.
            ConfigParser.RestoreIndexIfSaved(EncBordersBox);
            QuickConvertUi.BorderPresetSelected(EncBordersBox.SelectedIndex);

            // The resize is an object rather than a control's value, so it is read straight into the
            // configuration and the dropdown is then filled from it. Filling has to happen whether or
            // not anything was saved: the list has to hold its entries before a file is ever loaded.
            QuickConvertUi.CurrentResize = ConfigParser.LoadResize(Config.Key.EncResize);
            QuickConvertUi.RefreshResizeBox();
        }

        public void SaveQuickConvertSettings()
        {
            if (!_initialized)
                return;

            // A filter typed into the grid and left without pressing Enter is still sitting in the
            // cell editor rather than in the row behind it, and closing the window is exactly when
            // that happens.
            EncAdvancedFiltersGrid.CommitEdit();

            using (Config.Batch())
            {
                ConfigParser.SaveComboxIndex(EncQualModeBox);
                ConfigParser.SaveGuiElement(EncVidQualityBox);
                ConfigParser.SaveGuiElement(EncVidPresetBox);
                ConfigParser.SaveGuiElement(EncVidColorsBox);
                ConfigParser.SaveGuiElement(EncVidFpsBox);
                ConfigParser.SaveResize(Config.Key.EncResize, QuickConvertUi.CurrentResize);
                ConfigParser.SaveComboxIndex(EncDeintModeBox);
                ConfigParser.SaveGuiElement(EncDeintPresetBox);
                ConfigParser.SaveGuiElement(EncDeintDoubleRateBox);
                ConfigParser.SaveGuiElement(EncAudQualUpDown, ConfigParser.StringMode.Int);
                ConfigParser.SaveComboxIndex(EncAudChannelsBox);
                ConfigParser.SaveGuiElement(EncCustomArgsIn);
                ConfigParser.SaveGuiElement(EncCustomArgsOut);
                ConfigParser.SaveGuiElement(EncMetaApplyGrid);
                ConfigParser.SaveComboxIndex(EncBordersBox);
                ConfigParser.SaveFilterRows(Config.Key.EncCustomFilters, EncFilterRows);
            }
        }

        private void EncCropMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            EncCropConfBtn.IsVisible = EncCropModeBox.GetText().ToLower().Contains("manual");
            // A crop changes the frame the resize targets are measured against, and the shape the bars
            // are picked by - the second of those is refreshed on the way out of the first.
            QuickConvertUi.RefreshResizeBox();
        }

        private void EncBorders_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized)
                return;

            QuickConvertUi.BorderPresetSelected(EncBordersBox.SelectedIndex);
            SaveQuickConvertSettings();
        }

        private void EncResize_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Refilling the list raises this too, and that is not a choice: the entries are renamed for
            // every file loaded, so a batch would otherwise write the settings file once per file.
            if (!_initialized || QuickConvertUi.LoadingResizeBox)
                return;

            QuickConvertUi.ResizePresetSelected(EncResizeBox.SelectedIndex);
            SaveQuickConvertSettings();
        }

        private async void EncResizeConf_Click(object sender, RoutedEventArgs e)
        {
            ResizeConfig resize = await ResizeWindow.Show(QuickConvertUi.GetResizeSourceSize(), QuickConvertUi.GetResizeSar(), QuickConvertUi.CurrentResize);

            if (resize != null)
            {
                // Configured by hand is the Custom entry, whichever preset it started from
                resize.PresetKey = ResizePresets.CustomKey;
                QuickConvertUi.CurrentResize = resize;
                SaveQuickConvertSettings();
            }

            QuickConvertUi.UpdateResizeReadout();
        }

        private void EncDeintMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            DeinterlaceUi.ModeBoxEdited(av1anTab: false); // Remembered, so a progressive file in a queue cannot take it away
            DeinterlaceSetting_Changed();
        }
        private void EncDeintPreset_SelectionChanged(object sender, SelectionChangedEventArgs e) => DeinterlaceSetting_Changed();
        private void EncDeintRate_Changed(object sender, RoutedEventArgs e) => DeinterlaceSetting_Changed();

        /// <summary> The readout under the dropdown describes the loaded file, so it is rewritten
        /// whenever any part of the setting moves - not only on the dropdown itself. </summary>
        private void DeinterlaceSetting_Changed()
        {
            if (!_initialized)
                return;

            DeinterlaceUi.RefreshInfo();
            SaveQuickConvertSettings();
        }

        private async void EncCropConf_Click(object sender, RoutedEventArgs e)
        {
            // The video the crop will actually run on, which in Muxing Mode is not the file the Track
            // List is showing - the dialog's own bounds are measured against it
            Size res = QuickConvertUi.GetVideoSourceStream()?.Resolution ?? new Size();

            CropConfig crop = await CropWindow.Show(res, QuickConvertUi.CurrentCrop);

            if (crop != null)
                QuickConvertUi.CurrentCrop = crop;

            // The rectangle just moved, so what every resize target comes out to moved with it
            QuickConvertUi.RefreshResizeBox();
        }

        private async void EncTrimConf_Click(object sender, RoutedEventArgs e)
        {
            QuickConvertUi.CurrentTrim = await CutWindow.ShowForTrim(TrackList.current?.File, QuickConvertUi.CurrentTrim);
            UpdateTrimBtnText();
        }

        /// <summary> Drops the configured trim, so the whole video is encoded again. The dialog cannot
        /// say this: dismissing it keeps whatever was configured, and confirming it always writes a
        /// range, so a trim picked once could not be taken back. </summary>
        private void EncTrimClear_Click(object sender, RoutedEventArgs e)
        {
            QuickConvertUi.CurrentTrim = null;
            UpdateTrimBtnText();
        }

        /// <summary> The Trim button doubles as the readout of what is configured. A range is wider
        /// than the column, so the end of one is only ever read off the tooltip. </summary>
        public void UpdateTrimBtnText()
        {
            EncTrimConfBtn.Content = QuickConvertUi.CurrentTrim == null ? "Configure…" : QuickConvertUi.CurrentTrim.ToString();
            ToolTip.SetTip(EncTrimConfBtn, QuickConvertUi.CurrentTrim?.ToString());
            EncTrimClearBtn.IsVisible = QuickConvertUi.CurrentTrim != null; // Nothing set is nothing to remove
        }

        private async void EncAudConfigure_Click(object sender, RoutedEventArgs e)
        {
            if (TrackList.current == null)
                return;

            var entries = await AudioStreamsWindow.Show(TrackList.current.File, EncAudQualUpDown.Value.AsInt());

            if (entries != null && entries.Count > 0)
                TrackList.currentAudioConfig = new AudioConfiguration(TrackList.current.File, entries);
        }

        private async void EncAudConfMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized)
                return;

            int i = EncAudConfModeBox.SelectedIndex;

            if (i == 1)
            {
                bool currentNull = TrackList.current == null;
                bool noEnc = !EncAudQualUpDown.IsEnabled;
                bool noAudTracks = !TrackList.Items.Any(x => x.Stream.Type == Stream.StreamType.Audio);

                if (currentNull || noAudTracks || noEnc)
                {
                    if (currentNull)
                        await UiUtils.ShowMessageBox("Please load a file first in order to configure its audio tracks.", UiUtils.MessageType.Error);
                    else if (noAudTracks)
                        await UiUtils.ShowMessageBox("This is only available if you have at least one audio track in the track list.", UiUtils.MessageType.Error);
                    else
                        await UiUtils.ShowMessageBox("The selected audio encoder does not support custom bitrates.", UiUtils.MessageType.Error);

                    EncAudConfModeBox.SelectedIndex = 0;
                    return;
                }

                EncAudConfigure_Click(null, null);
            }

            EncAudConfigureBtn.IsVisible = i == 1;
        }

        private void EncFilterAdd_Click(object sender, RoutedEventArgs e)
        {
            EncFilterRows.Add(new FilterRow(""));
        }

        private void EncFilterRemove_Click(object sender, RoutedEventArgs e)
        {
            if (EncAdvancedFiltersGrid.SelectedItem is FilterRow row)
                EncFilterRows.Remove(row);
            else if (EncFilterRows.Count > 0)
                EncFilterRows.RemoveAt(EncFilterRows.Count - 1);
        }

        private async void BrowseFfmpegOutput_Click(object sender, RoutedEventArgs e)
        {
            string path = await Pickers.PickSavePath(this, "Choose output path", OutputPathBox.Text);

            if (!string.IsNullOrWhiteSpace(path))
                OutputPathBox.Text = Path.ChangeExtension(path, null);
        }
    }
}
