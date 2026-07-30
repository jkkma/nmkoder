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
            var mode = (QuickConvert.QualityMode)Math.Max(0, EncQualModeBox.SelectedIndex);

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
            ConfigParser.RestoreIfSaved(EncScaleBoxW);
            ConfigParser.RestoreIfSaved(EncScaleBoxH);
            ConfigParser.RestoreIfSaved(EncAudQualUpDown, allowFloat: false);
            ConfigParser.RestoreIndexIfSaved(EncAudChannelsBox);
            ConfigParser.RestoreIfSaved(EncCustomArgsIn);
            ConfigParser.RestoreIfSaved(EncCustomArgsOut);
            ConfigParser.RestoreIfSaved(EncMetaApplyGrid);
            ConfigParser.LoadFilterRows(Config.Key.EncCustomFilters, EncFilterRows);
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
                ConfigParser.SaveGuiElement(EncScaleBoxW);
                ConfigParser.SaveGuiElement(EncScaleBoxH);
                ConfigParser.SaveGuiElement(EncAudQualUpDown, ConfigParser.StringMode.Int);
                ConfigParser.SaveComboxIndex(EncAudChannelsBox);
                ConfigParser.SaveGuiElement(EncCustomArgsIn);
                ConfigParser.SaveGuiElement(EncCustomArgsOut);
                ConfigParser.SaveGuiElement(EncMetaApplyGrid);
                ConfigParser.SaveFilterRows(Config.Key.EncCustomFilters, EncFilterRows);
            }
        }

        private void EncCropMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            EncCropConfBtn.IsVisible = EncCropModeBox.GetText().ToLower().Contains("manual");
        }

        private async void EncCropConf_Click(object sender, RoutedEventArgs e)
        {
            Size res = new Size();

            if (TrackList.current != null && TrackList.current.File.VideoStreams.Count > 0)
                res = TrackList.current.File.VideoStreams[0].Resolution;

            CropConfig crop = await CropWindow.Show(res, QuickConvertUi.CurrentCrop);

            if (crop != null)
                QuickConvertUi.CurrentCrop = crop;
        }

        private async void EncTrimConf_Click(object sender, RoutedEventArgs e)
        {
            QuickConvertUi.CurrentTrim = await CutWindow.ShowForTrim(TrackList.current?.File, QuickConvertUi.CurrentTrim);
            UpdateTrimBtnText();
        }

        public void UpdateTrimBtnText()
        {
            EncTrimConfBtn.Content = QuickConvertUi.CurrentTrim == null ? "Configure…" : QuickConvertUi.CurrentTrim.ToString();
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
