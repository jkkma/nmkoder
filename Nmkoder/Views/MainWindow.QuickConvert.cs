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
            // Stereo rather than "keep original". Nothing on this tab is restored between sessions, so
            // every default here is what someone gets every time they open it.
            EncAudChannelsBox.SelectedIndex = 2;
            EncCropModeBox.SelectedIndex = 0;
        }

        private void EncVidCodec_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized)
                return;

            QuickConvertUi.VidEncoderSelected(EncVidCodecsBox.SelectedIndex);
        }

        private void EncAudCodec_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized)
                return;

            QuickConvertUi.AudEncoderSelected(EncAudCodecBox.SelectedIndex);
        }

        private void Container_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized)
                return;

            QuickConvertUi.ValidateContainer();
        }

        #region Grain Synthesis

        // The AV1AN tab's handlers, one for one - the row is the same row, driven by the same
        // GrainSynthUi, so anything that differs between the two would be a difference in behaviour
        // rather than in wiring. Nothing here is saved; this tab restores nothing between sessions.

        /// <summary> Every spinner on the row decides what the readout says, and the strength also decides
        /// whether the Denoise box beside it can do anything - so they share one handler. </summary>
        private void EncGrainSynthStrength_ValueChanged(object sender, NumericUpDownValueChangedEventArgs e)
        {
            if (!_initialized)
                return;

            GrainSynthUi.ApplyControlVisibility();
        }

        private void EncGrainSetting_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initialized)
                return;

            GrainSynthUi.RefreshInfo();
        }

        /// <summary> Unlike the other tickboxes on this row, this one decides what else is on screen: the
        /// denoise strength beside it belongs to the denoiser, which only runs for a table when this is
        /// ticked. So it goes through the visibility pass rather than only the readout. </summary>
        private void EncGrainTableDenoise_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initialized)
                return;

            GrainSynthUi.ApplyControlVisibility();
        }

        private void EncGrainMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized)
                return;

            GrainSynthUi.ApplyControlVisibility();
        }

        private void EncGrainTable_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initialized)
                return;

            GrainSynthUi.RefreshInfo();
        }

        private async void EncGrainTableBrowse_Click(object sender, RoutedEventArgs e)
        {
            await GrainSynthUi.PickTableAsync(av1anTab: false);
        }

        #endregion

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
        /// The Quick Convert tab's opening state.
        /// <para/>
        /// **Nothing on this tab is saved between sessions**, so this restores nothing - it fills the two
        /// lists that have to hold entries before a file is ever loaded, and leaves everything else at the
        /// defaults the selected encoder has just written into these controls. Which is why it still
        /// cannot run any earlier than it does.
        /// <para/>
        /// The argument is the AV1AN Video tab's, carried across: these settings describe a *job* rather
        /// than a preference, and every way they go wrong is expensive and quiet. A resize left on 720p
        /// halves an encode nobody meant to shrink, a CRF picked for a grainy film is the wrong number for
        /// line art, a target bitrate left over from one source is meaningless against the next. Reset On
        /// New File already made that argument for Trim, Crop and Deinterlace; this carries it to the whole
        /// tab and to the boundary that is easiest to lose track of, which is a session that ended days ago.
        /// <para/>
        /// Not writing them matters as much as not reading them: a value saved and never restored is one
        /// the next person to touch this method will restore, reasonably enough, and the setting then comes
        /// back from whatever session last happened to write it. Keys from before this are still sitting in
        /// existing config files - do not wire one back up on the strength of finding it there.
        /// </summary>
        public void LoadQuickConvertSettings()
        {
            // The border list names shapes rather than sizes, so a new file changes nothing in it - but it
            // has to hold its entries, and the configuration behind it has to agree with the box, before
            // anything reads either. The box's own handler bails until _initialized, which is not set yet.
            QuickConvertUi.BorderPresetSelected(EncBordersBox.SelectedIndex);

            // Same for the resize, which is an object rather than a control's value.
            QuickConvertUi.CurrentResize = new ResizeConfig();
            QuickConvertUi.RefreshResizeBox();
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
        }

        private void EncResize_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Refilling the list raises this too, and that is not a choice: the entries are renamed for
            // every file loaded, so a batch would otherwise write the settings file once per file.
            if (!_initialized || QuickConvertUi.LoadingResizeBox)
                return;

            QuickConvertUi.ResizePresetSelected(EncResizeBox.SelectedIndex);
        }

        private async void EncResizeConf_Click(object sender, RoutedEventArgs e)
        {
            ResizeConfig resize = await ResizeWindow.Show(QuickConvertUi.GetResizeSourceSize(), QuickConvertUi.GetResizeSar(), QuickConvertUi.CurrentResize);

            if (resize != null)
            {
                // Configured by hand is the Custom entry, whichever preset it started from
                resize.PresetKey = ResizePresets.CustomKey;
                QuickConvertUi.CurrentResize = resize;
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
        }

        /// <summary> The readout under the loudness box says what it will do to the loaded file's
        /// tracks, so it is rewritten whenever the box moves. </summary>
        private void EncAudLoudness_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized)
                return;

            QuickConvertUi.UpdateLoudnessReadout();
        }

        private void EncToneMap_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized)
                return;

            ToneMapUi.RefreshInfo();
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

            var entries = await AudioStreamsWindow.Show(TrackList.current.File, EncAudQualUpDown.Value.AsInt(),
                EncAudChannelsBox.GetText().Split(' ')[0].GetInt());

            if (entries != null && entries.Count > 0)
                TrackList.currentAudioConfig = new AudioConfiguration(TrackList.current.File, entries);

            // Dismissing the dialog leaves nothing configured, so the mode has to come back with it -
            // otherwise the box reads "Configure each track separately" over no configuration at all,
            // which is the state the reset in TrackList.SetAsMainFile exists to prevent.
            if (TrackList.currentAudioConfig == null && EncAudConfModeBox.SelectedIndex == 1)
                EncAudConfModeBox.SelectedIndex = 0;

            QuickConvertUi.RefreshAudioChannelsEnabled();
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

                // Both set before the dialog opens: it is async, so control comes back here the moment it
                // is on screen, and the button beside the box has to be there when it closes. The dialog
                // refreshes the Channels row itself on the way out, where it knows whether anything was
                // actually configured.
                EncAudConfigureBtn.IsVisible = true;
                QuickConvertUi.RefreshAudioChannelsEnabled();
                EncAudConfigure_Click(null, null);
                return;
            }

            EncAudConfigureBtn.IsVisible = false;
            QuickConvertUi.RefreshAudioChannelsEnabled();
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

            if (string.IsNullOrWhiteSpace(path))
                return;

            // Only a *known* extension comes off. ChangeExtension(path, null) drops everything after the
            // last dot whatever it is, so "My.Movie.2020" was saved as "My.Movie" - the box holds a path
            // without an extension, and the container adds one back, so the year went for good.
            string ext = (Path.GetExtension(path) ?? "").TrimStart('.').ToLower();
            bool known = Enum.GetNames<Containers.Container>().Any(c => c.ToLower() == ext)
                || new[] { "gif", "png", "jpg", "jpeg" }.Contains(ext);

            OutputPathBox.Text = known ? Path.ChangeExtension(path, null) : path;
        }
    }
}
