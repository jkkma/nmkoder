using Avalonia.Controls;
using Avalonia.Data;
using Nmkoder.Data;
using Nmkoder.Data.Codecs;
using Nmkoder.Data.Ui;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Media;
using Nmkoder.UI;
using Nmkoder.UI.Tasks;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Nmkoder.Views
{
    /// <summary>
    /// The Advanced tab's encoder-argument section, which both encode tabs now have. It was the AV1AN
    /// tab's alone and is shared rather than copied: the grouping into category tabs, the preset row
    /// and its confirmation, the right-click help and the save-as-you-type are one behaviour, and two
    /// copies of it would drift the moment either tab was touched.
    /// <para/>
    /// What the two tabs do *not* share is anything about the arguments themselves. Each section
    /// carries its own rows, its own encoder, its own config key and its own spelling, because an
    /// AV1AN argument is a standalone binary's CLI parameter and a Quick Convert one is whatever the
    /// ffmpeg wrapper accepts - see <see cref="FfmpegEncoderArgs"/>, where that difference lives.
    /// </summary>
    partial class MainWindow
    {
        /// <summary> One tab's argument section: the controls it draws into, and the four questions
        /// that are answered differently for each tab. </summary>
        internal class ArgSection
        {
            /// <summary> The master list. Each category tab's grid shows a slice of it, so an edit in
            /// any of them lands here and everything reading it is unaffected by the grouping. </summary>
            public ObservableCollection<EncoderArgRow> Rows;
            public StackPanel PresetPanel;
            public TabControl CategoryTabs;

            /// <summary> The encoder the tab has selected, asked rather than passed: a preset is
            /// applied long after the section was built, and by then the box may have moved. </summary>
            public Func<IEncoder> Encoder;

            /// <summary> Persists the values - called as each cell is committed, since selecting
            /// another encoder rebuilds the rows and anything not already stored would be gone. </summary>
            public Action Save;

            /// <summary> How one argument is written on the command line, for the tooltip and the
            /// right-click window. </summary>
            public Func<string, string> Spell;

            /// <summary> What the filled-in rows come to, for the line logged when a preset lands. </summary>
            public Func<IEnumerable<EncoderArgRow>, string> Describe;

            /// <summary> The category tabs' grids, kept for committing pending edits on save. </summary>
            public readonly List<DataGrid> Grids = new List<DataGrid>();
        }

        internal ArgSection Av1anArgs { get; private set; }
        internal ArgSection EncArgs { get; private set; }

        /// <summary> Built once, before anything can select an encoder. </summary>
        private void SetUpArgSections()
        {
            Av1anArgs = new ArgSection
            {
                Rows = Av1anArgRows,
                PresetPanel = Av1anArgPresetPanel,
                CategoryTabs = Av1anArgCategoryTabs,
                Encoder = () => CodecUtils.GetCodec(Av1anUi.GetCurrentCodecV()),
                Save = SaveAv1anAdvancedArgs,
                Spell = a => $"--{(a ?? "").TrimStart('-')}",
                Describe = EncoderArgs.BuildCli,
            };

            EncArgs = new ArgSection
            {
                Rows = EncArgRows,
                PresetPanel = EncArgPresetPanel,
                CategoryTabs = EncArgCategoryTabs,
                Encoder = () => CodecUtils.GetCodec(QuickConvertUi.GetCurrentCodecV()),
                Save = SaveEncAdvancedArgs,
                Spell = a => FfmpegEncoderArgs.Spell(CodecUtils.GetCodec(QuickConvertUi.GetCurrentCodecV()).Name, a),
                Describe = rows => FfmpegEncoderArgs.Render(CodecUtils.GetCodec(QuickConvertUi.GetCurrentCodecV()).Name,
                    EncoderArgs.BuildPairs(rows)),
            };
        }

        /// <summary>
        /// Rebuilds a section's category tabs from whatever is in its rows, one tab per category in
        /// the order the encoder's JSON introduces them.
        /// </summary>
        internal void LoadArgCategoryTabs(ArgSection section)
        {
            // Encoders share category names, so the open category survives an encoder switch
            string selected = (section.CategoryTabs.SelectedItem as TabItem)?.Header?.ToString();
            section.Grids.Clear();
            List<TabItem> tabs = new List<TabItem>();

            foreach (var category in section.Rows.GroupBy(r => r.Category.IsEmpty() ? "Other" : r.Category))
            {
                DataGrid grid = CreateArgsGrid(section, category.ToList());
                section.Grids.Add(grid);
                tabs.Add(new TabItem { Header = category.Key, Content = grid });
            }

            // A missing or unparseable JSON leaves no rows at all, and so does an encoder that has no
            // parameters worth offering - a stream copy, or an image sequence. Without this the
            // heading would sit over a bare void; an empty grid keeps the column chrome visible.
            if (tabs.Count == 0)
                tabs.Add(new TabItem { Header = "Arguments", Content = CreateArgsGrid(section, new List<EncoderArgRow>()) });

            section.CategoryTabs.ItemsSource = tabs;
            int index = tabs.FindIndex(t => t.Header?.ToString() == selected);
            section.CategoryTabs.SelectedIndex = index >= 0 ? index : 0;
        }

        /// <summary>
        /// Builds the preset row from whatever presets the selected encoder has. Encoders with none get
        /// no row at all rather than an empty one, so the tab looks exactly as it did before for them.
        /// </summary>
        internal void LoadArgPresets(ArgSection section, string encoderName)
        {
            IReadOnlyList<EncoderArgPreset> presets = EncoderArgPresets.For(encoderName);

            // The label is the panel's one fixed child; everything after it belongs to the last encoder
            while (section.PresetPanel.Children.Count > 1)
                section.PresetPanel.Children.RemoveAt(section.PresetPanel.Children.Count - 1);

            section.PresetPanel.IsVisible = presets.Count > 0;

            if (presets.Count < 1)
                return;

            foreach (EncoderArgPreset preset in presets)
            {
                Button button = new Button { Content = preset.Name };
                ToolTip.SetTip(button, $"{preset.Description}\n\nSets {preset.Values.Count} arguments and empties " +
                    $"the rest, so what the encoder gets is the preset and nothing else. Every value stays editable.");
                button.Click += async (s, e) => await ApplyArgPreset(section, preset);
                section.PresetPanel.Children.Add(button);
            }

            Button clear = new Button { Content = "Clear" };
            ToolTip.SetTip(clear, "Empties every argument, leaving the encoder on its own defaults.");
            clear.Click += async (s, e) => await ApplyArgPreset(section, null);
            section.PresetPanel.Children.Add(clear);
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
        private async Task ApplyArgPreset(ArgSection section, EncoderArgPreset preset)
        {
            // A cell still being edited has not written back to its row, so without this the value read
            // below would be the one from before the edit - and the edit would then survive the preset.
            foreach (DataGrid grid in section.Grids)
                grid.CommitEdit();

            Dictionary<string, string> filled = EncoderArgs.Filled(section.Rows);

            // Already exactly what was asked for - an empty grid to clear, or this very preset
            if (preset == null ? filled.Count < 1 : preset.Matches(filled))
                return;

            string encoderName = section.Encoder().Name;
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

            foreach (EncoderArgRow row in section.Rows)
                row.Value = applicable.TryGetValue(row.Argument.Trim(), out string value) ? value : "";

            section.Save();

            if (preset == null)
            {
                Logger.Log("Cleared the encoder arguments.");
                return;
            }

            // A preset naming an argument this encoder has no row for would go unset and unmentioned.
            // It should not happen - the presets are written against the same JSON the grid is built
            // from - but a setting missing from the encode is worth more than a quiet developer error.
            var missing = applicable.Keys.Where(k => !section.Rows.Any(r => r.Argument.Trim() == k)).ToList();

            if (missing.Count > 0)
                Logger.Log($"The '{preset.Name}' preset could not set {string.Join(", ", missing)} - this encoder has no such argument.");

            Logger.Log($"Applied the '{preset.Name}' argument preset: {section.Describe(section.Rows)}");
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
        /// <para/>
        /// Only the standalone binaries are asked, which is what
        /// <see cref="EncoderArgPresets.Av1anEncoderName"/> returning "" for everything else settles.
        /// Quick Convert's encoders are libraries inside ffmpeg with no <c>--help</c> to read, and they
        /// are the ones the shipped list was written against in the first place.
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
        private DataGrid CreateArgsGrid(ArgSection section, List<EncoderArgRow> rows)
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
            grid.CellEditEnded += (s, e) => section.Save();

            // No per-row tooltip. It repeated the description the row is already showing, and it was
            // drawn over the rows underneath the pointer - so moving down the list covered the very
            // thing being read, on a grid whose whole job is to be scanned. The full text is a
            // right-click away, which is what the heading above the grid says.

            // Right-clicking a row opens its long-form help. Handled on the grid rather than on each
            // row: rows are recycled as the list scrolls, so per-row subscriptions would stack up.
            // The cells inherit the row's DataContext, so whatever was clicked names the argument.
            grid.ContextRequested += async (s, e) =>
            {
                e.Handled = true;

                if (e.Source is Control control && control.DataContext is EncoderArgRow row)
                    await EncoderArgInfoWindow.Show(row, section.Spell(row.Argument));
            };

            return grid;
        }

        /// <summary>
        /// Also runs on close, where the edit has to be committed first: a cell the user is still
        /// typing in has not ended its edit, so nothing would have saved that last value.
        /// </summary>
        public void SaveAv1anAdvancedArgs()
        {
            if (!_initialized)
                return;

            foreach (DataGrid grid in Av1anArgs.Grids)
                grid.CommitEdit();

            EncoderArgs.Save(Av1anArgRows, CodecUtils.GetCodec(Av1anUi.GetCurrentCodecV()), Config.Key.Av1anEncoderArgs);
        }

        /// <summary> As above, for Quick Convert. Kept apart because the values are: the two tabs' lists
        /// name different things even where the encoder behind them has the same name. </summary>
        public void SaveEncAdvancedArgs()
        {
            if (!_initialized)
                return;

            foreach (DataGrid grid in EncArgs.Grids)
                grid.CommitEdit();

            EncoderArgs.Save(EncArgRows, CodecUtils.GetCodec(QuickConvertUi.GetCurrentCodecV()), Config.Key.EncEncoderArgs);
        }
    }
}
