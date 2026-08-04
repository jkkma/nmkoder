using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Nmkoder.Data;
using Nmkoder.Data.Codecs;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Data.Ui;
using Nmkoder.Media;
using Nmkoder.UI;
using Nmkoder.UI.Tasks;
using Nmkoder.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

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

            // Not saved: the encoder is a Video tab setting, and that tab opens at its defaults
            Av1anUi.VidEncoderSelected(Av1anCodecBox.SelectedIndex);
        }

        private void Av1anQualityMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized)
                return;

            ApplyAv1anQualityMode();
        }

        /// <summary>
        /// The range, step size, formatting and default value the quality spinner takes from the
        /// selected mode. It used to take the default conditionally, because a mode restored at
        /// startup had a saved value coming in behind it that the default would have overwritten;
        /// the tab restores nothing now, so picking a mode is the only thing that gets here and it
        /// always wants the mode's own number.
        /// </summary>
        private void ApplyAv1anQualityMode()
        {
            Av1an.QualityMode mode = Av1anUi.GetCurrentQualityMode();

            // Whole numbers unless the metric needs finer steps - butteraugli and XPSNR override below.
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
                // identical and higher is worse, and the useful targets sit between whole
                // numbers, hence the tenths. av1an scores it at 203 nits rather than the 80
                // the classic "under 1 is fine" lore was calibrated on, so scores read
                // higher here - av1an's own docs target 5.4 - and 4 sits between that and
                // the stricter targets seen in the wild.
                Av1anQualityUpDown.Increment = 0.1m;
                Av1anQualityUpDown.FormatString = "0.0##";
                Av1anQualityUpDown.SetRange(0.5m, 10);
                Av1anQualityUpDown.Value = 4.0m;
            }
            else if (mode == Av1an.QualityMode.TargetXpsnr)
            {
                // XPSNR is a PSNR-style decibel scale: 0 is worst and it rises with quality,
                // with no fixed ceiling. Scored as the weighted variant ((4·Y+U+V)/6),
                // the mid-30s read as good and the mid-40s as visually lossless, so 40 is
                // the counterpart of the VMAF 95 / SSIMULACRA2 80 defaults above. Half-dB
                // steps, because a whole decibel is a coarse jump on a logarithmic scale.
                Av1anQualityUpDown.Increment = 0.5m;
                Av1anQualityUpDown.FormatString = "0.0##";
                Av1anQualityUpDown.SetRange(20, 60);
                Av1anQualityUpDown.Value = 40m;
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

            Av1anUi.ValidateContainer(); // Not saved either - same tab, same reason
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

        /// <summary> Nothing to save - the Video tab restores nothing - but the Denoise box beside this
        /// one does nothing at a strength of 0, so it is enabled from here. </summary>
        private void Av1anGrainSynthStrength_ValueChanged(object sender, NumericUpDownValueChangedEventArgs e)
        {
            if (!_initialized)
                return;

            Av1anUi.ApplyGrainDenoiseEnabled();
        }

        /// <summary> The Workers box is not the plain saved spinner the rest of the tab is: it shows the
        /// count for the encoder selected, and what is saved is the count for an encoder that is not
        /// SVT-AV1. An edit here states the first, so Av1anUi works the second back out of it. </summary>
        private void Av1anWorkerCount_ValueChanged(object sender, NumericUpDownValueChangedEventArgs e)
        {
            Av1anUi.WorkerCountEdited();
            SaveConfigAv1an();
        }

        private void Av1anCrop_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Av1anCropConfBtn.IsVisible = Av1anCropBox.GetText().ToLower().Contains("manual");
            Av1anUi.RefreshResizeBox(); // A crop changes the frame the resize targets are measured against
        }

        /// <summary> The target shape is a Video tab setting, so it is not saved: the tab opens on
        /// "No borders" every session. The line under the box describes the loaded file, so picking
        /// an entry has to rewrite it. </summary>
        private void Av1anBorders_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Av1anUi.BorderPresetSelected(Av1anBordersBox.SelectedIndex);
        }

        private async void Av1anCropConf_Click(object sender, RoutedEventArgs e)
        {
            Size res = new Size();

            if (TrackList.current != null && TrackList.current.File.VideoStreams.Count > 0)
                res = TrackList.current.File.VideoStreams[0].Resolution;

            CropConfig crop = await CropWindow.Show(res, Av1anUi.CurrentCrop);

            if (crop != null)
                Av1anUi.CurrentCrop = crop;

            Av1anUi.RefreshResizeBox();
        }

        private void Av1anDeintMode_SelectionChanged(object sender, SelectionChangedEventArgs e) => Av1anDeinterlaceSetting_Changed();
        private void Av1anDeintPreset_SelectionChanged(object sender, SelectionChangedEventArgs e) => Av1anDeinterlaceSetting_Changed();
        private void Av1anDeintRate_Changed(object sender, RoutedEventArgs e) => Av1anDeinterlaceSetting_Changed();

        /// <summary> The readout under the dropdown describes the loaded file, so it is rewritten
        /// whenever any part of the setting moves - not only on the dropdown itself. Nothing is saved:
        /// the Video tab's settings last as long as the session. </summary>
        private void Av1anDeinterlaceSetting_Changed()
        {
            if (!_initialized)
                return;

            DeinterlaceUi.RefreshInfo();
        }

        private void Av1anResize_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // The handler tells a pick apart from a refill, which raises this event too
            Av1anUi.ResizePresetSelected(Av1anResizeBox.SelectedIndex);
        }

        private async void Av1anResizeConf_Click(object sender, RoutedEventArgs e)
        {
            ResizeConfig resize = await ResizeWindow.Show(Av1anUi.GetResizeSourceSize(), Av1anUi.GetResizeSar(), Av1anUi.CurrentResize);

            if (resize != null)
            {
                resize.PresetKey = ResizePresets.CustomKey;
                Av1anUi.CurrentResize = resize;
            }

            Av1anUi.UpdateResizeReadout();
        }

        private async void Av1anTrimConf_Click(object sender, RoutedEventArgs e)
        {
            Av1anUi.CurrentTrim = await CutWindow.ShowForAv1anTrim(TrackList.current?.File, Av1anUi.CurrentTrim);
            UpdateAv1anTrimBtnText();
        }

        /// <summary> Drops the configured trim, so the whole video is encoded again. The dialog cannot
        /// say this: dismissing it keeps whatever was configured, and confirming it always writes a
        /// range, so a trim picked once could not be taken back. </summary>
        private void Av1anTrimClear_Click(object sender, RoutedEventArgs e)
        {
            Av1anUi.CurrentTrim = null;
            UpdateAv1anTrimBtnText();
        }

        /// <summary> The Trim button doubles as the readout of what is configured. A range is wider
        /// than the column, so the end of one is only ever read off the tooltip. </summary>
        public void UpdateAv1anTrimBtnText()
        {
            Av1anTrimConfBtn.Content = Av1anUi.CurrentTrim == null ? "Configure…" : Av1anUi.CurrentTrim.ToString();
            ToolTip.SetTip(Av1anTrimConfBtn, Av1anUi.CurrentTrim?.ToString());
            Av1anTrimClearBtn.IsVisible = Av1anUi.CurrentTrim != null; // Nothing set is nothing to remove
        }

        /// <summary> The av1an options that outlive a session. The Video tab's encoder and container are
        /// not among them - see <see cref="LoadAv1anEncodeSettings"/> - and the audio codec below is,
        /// because it lives on the Audio &amp; Tracks tab. </summary>
        public void LoadConfigAv1an()
        {
            ConfigParser.LoadComboxIndex(Av1anAudCodecBox);
            ConfigParser.LoadComboxIndex(Av1anOptsChunkModeBox);
            ConfigParser.LoadComboxIndex(Av1anOptsSplitModeBox);
            ConfigParser.LoadComboxIndex(Av1anOptsConcatModeBox);
            ConfigParser.LoadComboxIndex(Av1anOptsChunkOrderBox);
            ConfigParser.LoadGuiElement(Av1anOptsWorkerCountUpDown, false);
            // What was restored is the count for an encoder that is not SVT-AV1 - the baseline the
            // penalty is measured from. VidEncoderSelected reduces the box from it a moment later.
            Av1anUi.LoadWorkerBaseline();
            ConfigParser.LoadGuiElement(Av1anThreadsUpDown, false);
        }

        public void SaveConfigAv1an()
        {
            if (!_initialized)
                return;

            using (Config.Batch())
            {
                ConfigParser.SaveComboxIndex(Av1anAudCodecBox);
                ConfigParser.SaveComboxIndex(Av1anOptsChunkModeBox);
                ConfigParser.SaveComboxIndex(Av1anOptsSplitModeBox);
                ConfigParser.SaveComboxIndex(Av1anOptsConcatModeBox);
                ConfigParser.SaveComboxIndex(Av1anOptsChunkOrderBox);
                // The one control here not saved as it stands. The box holds the count for the encoder
                // selected, which is two lower under SVT-AV1; storing that would have the next session
                // take the reduced number for the baseline and reduce it again, and again after that.
                Config.Set(Config.Key.Av1anOptsWorkerCountUpDown, Av1anUi.WorkerBaseline.ToString());
                ConfigParser.SaveGuiElement(Av1anThreadsUpDown, ConfigParser.StringMode.Int);
            }
        }

        /// <summary>
        /// The AV1AN settings that outlive a session, restored on top of the defaults the selected
        /// encoder has just written into these controls - which is why this cannot run any earlier
        /// than it does.
        /// <para/>
        /// Nothing on the Video tab is among them. The encoder, the container, the quality mode and its
        /// value, the preset, the colour format, grain synthesis, the frame rate, the resize, the crop,
        /// the trim and the deinterlace all start every session at their defaults - SVT-AV1 into MKV,
        /// and then whatever selecting that encoder writes into the rest. Those settings describe a job
        /// rather than a preference: they are picked for the file in front of the user and read wrong
        /// against the next one, and the ways that goes wrong are expensive and quiet. A QTGMC left
        /// armed spends hours and tens of gigabytes on a progressive source; a resize left on 720p
        /// halves a 4K encode nobody meant to shrink; a CRF picked for a grainy film is the wrong
        /// number for line art. Reset On New File already made this argument for Trim, Crop and
        /// Deinterlace - this is the same argument, carried to the whole tab and to the boundary that
        /// is even easier to lose track of than a new file, which is a session that ended days ago.
        /// <para/>
        /// The encoder is the one that had to move rather than simply stop being restored: the default
        /// was the saved value's, written by <see cref="Config"/> as SVT-AV1 for a config that had none
        /// yet, so dropping the restore alone would have started every session on the first entry of
        /// the enum, which is aomenc. <see cref="Av1anUi.Init"/> now names SVT-AV1 where the box is
        /// filled, which is the only place left that says what the tab opens as.
        /// <para/>
        /// Crop was already left out before any of this, for the same reason arrived at one setting at
        /// a time: it is per-file by design, and its rectangle is not saved either, so a restored
        /// "Manual" would be a mode with nothing behind it.
        /// </summary>
        public void LoadAv1anEncodeSettings()
        {
            // Not a restore: the entries name what each target works out to for the loaded file, so
            // the list has to be filled whether or not anything was saved. The resize itself is off.
            Av1anUi.RefreshResizeBox();
            ConfigParser.RestoreIfSaved(Av1anAudQualUpDown, allowFloat: false);
            ConfigParser.RestoreIndexIfSaved(Av1anAudChannelsBox);
            ConfigParser.RestoreIfSaved(CheckAv1anCopySubs);
            ConfigParser.RestoreIfSaved(CheckAv1anCopyData);
            ConfigParser.RestoreIfSaved(CheckAv1anCopyAttachs);
            ConfigParser.RestoreIfSaved(Av1anCustomArgsBox);
            ConfigParser.RestoreIfSaved(Av1anCustomEncArgsBox);
            ConfigParser.LoadFilterRows(Config.Key.Av1anCustomFilters, Av1anFilterRows);
        }

        public void SaveAv1anEncodeSettings()
        {
            if (!_initialized)
                return;

            // A filter typed into the grid and left without pressing Enter is still sitting in the
            // cell editor rather than in the row behind it, and closing the window is exactly when
            // that happens - the same reason SaveAv1anAdvancedArgs commits its own grids first.
            Av1anAdvancedFiltersGrid.CommitEdit();

            // The Video tab is not written either, and that is the half of it that matters: a value
            // that is saved and not restored is one the next person to touch this file will restore,
            // reasonably enough, and the setting comes back from whatever session last happened to
            // write it. Nothing on that tab is stored at all - see LoadAv1anEncodeSettings.
            using (Config.Batch())
            {
                ConfigParser.SaveGuiElement(Av1anAudQualUpDown, ConfigParser.StringMode.Int);
                ConfigParser.SaveComboxIndex(Av1anAudChannelsBox);
                ConfigParser.SaveGuiElement(CheckAv1anCopySubs);
                ConfigParser.SaveGuiElement(CheckAv1anCopyData);
                ConfigParser.SaveGuiElement(CheckAv1anCopyAttachs);
                ConfigParser.SaveGuiElement(Av1anCustomArgsBox);
                ConfigParser.SaveGuiElement(Av1anCustomEncArgsBox);
                ConfigParser.SaveFilterRows(Config.Key.Av1anCustomFilters, Av1anFilterRows);
            }
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

        /// <summary>
        /// Builds the preset row from whatever presets the selected encoder has. Encoders with none get
        /// no row at all rather than an empty one, so the tab looks exactly as it did before for them.
        /// </summary>
        public void LoadAv1anArgPresets(string encoderName)
        {
            IReadOnlyList<EncoderArgPreset> presets = EncoderArgPresets.For(encoderName);

            // The label is the panel's one fixed child; everything after it belongs to the last encoder
            while (Av1anArgPresetPanel.Children.Count > 1)
                Av1anArgPresetPanel.Children.RemoveAt(Av1anArgPresetPanel.Children.Count - 1);

            Av1anArgPresetPanel.IsVisible = presets.Count > 0;

            if (presets.Count < 1)
                return;

            foreach (EncoderArgPreset preset in presets)
            {
                Button button = new Button { Content = preset.Name };
                ToolTip.SetTip(button, $"{preset.Description}\n\nSets {preset.Values.Count} arguments and empties " +
                    $"the rest, so what the encoder gets is the preset and nothing else. Every value stays editable.");
                button.Click += async (s, e) => await ApplyAv1anArgPreset(preset);
                Av1anArgPresetPanel.Children.Add(button);
            }

            Button clear = new Button { Content = "Clear" };
            ToolTip.SetTip(clear, "Empties every argument, leaving the encoder on its own defaults.");
            clear.Click += async (s, e) => await ApplyAv1anArgPreset(null);
            Av1anArgPresetPanel.Children.Add(clear);
        }

        /// <summary>
        /// Writes a preset into the argument grid, or empties the grid when given null.
        /// <para/>
        /// Every row the preset does not name is cleared. A preset that only added to whatever was
        /// already there would encode differently depending on what had been typed before it, and there
        /// would be no way to get back to the preset as published - which is the whole point of one.
        /// <para/>
        /// That makes it destructive, so values it would overwrite are confirmed first - but only where
        /// they were typed by hand. Arriving from another preset, or from this same one, is a state the
        /// user got to by pressing one of these buttons, and re-confirming it every time would be noise.
        /// </summary>
        private async Task ApplyAv1anArgPreset(EncoderArgPreset preset)
        {
            // A cell still being edited has not written back to its row, so without this the value read
            // below would be the one from before the edit - and the edit would then survive the preset.
            foreach (DataGrid grid in _av1anArgGrids)
                grid.CommitEdit();

            Dictionary<string, string> filled = Av1anArgRows
                .Where(r => r.Argument.IsNotEmpty() && r.Value.IsNotEmpty())
                .GroupBy(r => r.Argument.Trim())
                .ToDictionary(g => g.Key, g => g.Last().Value.Trim());

            // Already exactly what was asked for - an empty grid to clear, or this very preset
            if (preset == null ? filled.Count < 1 : preset.Matches(filled))
                return;

            string encoderName = CodecUtils.GetCodec(Av1anUi.GetCurrentCodecV()).Name;
            bool handEdited = filled.Count > 0 && !EncoderArgPresets.For(encoderName).Any(p => p.Covers(filled));

            if (handEdited)
            {
                // Deliberately not "typed by hand": what is here may be mostly a preset with one value
                // changed, and counting all of it as hand-typed would overstate what is being lost.
                // What matters is only that it is not any preset, so none of it can be got back.
                bool one = filled.Count == 1;
                string msg = $"{filled.Count} argument{(one ? " is" : "s are")} set, and {(one ? "it does" : "they do")} not match a preset:\n\n" +
                    $"{string.Join(", ", filled.Select(f => $"{f.Key} {f.Value}"))}\n\n" +
                    (preset == null
                        ? $"Clearing empties {(one ? "it" : "them all")}. Continue?"
                        : $"Applying '{preset.Name}' replaces {(one ? "it" : "them all")}. Continue?");

                if (await UiUtils.ShowMessageBox(msg, "Replace the arguments already set?", UiUtils.MessageButtons.YesNo) != UiUtils.DialogResult.Yes)
                    return;
            }

            Dictionary<string, string> applicable = preset == null
                ? new Dictionary<string, string>()
                : await GetApplicablePresetValues(preset, encoderName);

            foreach (EncoderArgRow row in Av1anArgRows)
                row.Value = applicable.TryGetValue(row.Argument.Trim(), out string value) ? value : "";

            SaveAv1anAdvancedArgs();

            if (preset == null)
            {
                Logger.Log("Cleared the encoder arguments.");
                return;
            }

            // A preset naming an argument this encoder has no row for would go unset and unmentioned.
            // It should not happen - the presets are written against the same JSON the grid is built
            // from - but a setting missing from the encode is worth more than a quiet developer error.
            var missing = applicable.Keys.Where(k => !Av1anArgRows.Any(r => r.Argument.Trim() == k)).ToList();

            if (missing.Count > 0)
                Logger.Log($"The '{preset.Name}' preset could not set {string.Join(", ", missing)} - this encoder has no such argument.");

            Logger.Log($"Applied the '{preset.Name}' argument preset: {Av1anUi.BuildAdvancedArgs(Av1anArgRows)}");
        }

        /// <summary>
        /// A preset's values, less any the encoder that would run does not have a parameter for.
        /// <para/>
        /// Which binary that is comes down to how the release was built. The bundle prefers svt-av1-hdr,
        /// which continues the PSY line, but falls back to mainline SVT-AV1 where no prebuilt exists,
        /// and on macOS it bundles no encoder at all - leaving whatever Homebrew installed, which is
        /// mainline. Handing a PSY-line parameter to a mainline binary does not degrade into it being
        /// ignored: the encoder refuses the whole command, and every chunk fails.
        /// <para/>
        /// So the encoder is asked. A parameter it does not list is dropped, and said out loud, since
        /// dropping it means the preset is not doing all it says. An encoder that cannot be found or
        /// run answers nothing, and nothing is dropped on the strength of a failed lookup.
        /// </summary>
        private static async Task<Dictionary<string, string>> GetApplicablePresetValues(EncoderArgPreset preset, string encoderName)
        {
            string av1anEncoder = EncoderArgPresets.Av1anEncoderName(encoderName);

            if (av1anEncoder.IsEmpty())
                return new Dictionary<string, string>(preset.Values);

            var applicable = new Dictionary<string, string>();
            var unsupported = new List<string>();

            foreach (var value in preset.Values)
            {
                // Matched with the dashes on, so a parameter is not found inside a longer one's name
                if (await AvProcess.EncoderKnowsFlagOrIsUnknown(av1anEncoder, $"--{value.Key}"))
                    applicable[value.Key] = value.Value;
                else
                    unsupported.Add(value.Key);
            }

            // Worth naming the cause rather than the symptom. These presets are written for the PSY
            // line, and an encoder missing parameters from it is almost always mainline SVT-AV1 -
            // which is a thing to go and fix, not a build to quietly encode a lesser preset on.
            if (unsupported.Count > 0)
            {
                Logger.Log($"Left {string.Join(", ", unsupported)} out of the '{preset.Name}' preset - the encoder " +
                    $"being used does not have {(unsupported.Count == 1 ? "that parameter" : "those parameters")}, " +
                    $"and would refuse the whole command over {(unsupported.Count == 1 ? "it" : "them")}. That means " +
                    $"it is mainline SVT-AV1 rather than the PSY-line build (svt-av1-hdr) these presets " +
                    $"are written for, so the rest of the preset is doing less than it says.");
            }

            return applicable;
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
            string path = await Pickers.PickSavePath(this, "Choose output path", Av1anOutputPathBox.Text);

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
