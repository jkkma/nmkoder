using Avalonia;
using Avalonia.Controls;
using Newtonsoft.Json;
using Nmkoder.Data;
using Nmkoder.Data.Ui;
using Nmkoder.Extensions;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Nmkoder.IO
{
    /// <summary>
    /// Persists the value of a UI control under its <see cref="StyledElement.Name"/>.
    /// Same contract as the WinForms version, just against Avalonia controls.
    /// </summary>
    class ConfigParser
    {
        public enum StringMode { Any, Int, Float }

        public static void SaveGuiElement(TextBox textbox, StringMode stringMode = StringMode.Any)
        {
            string text = textbox.Text ?? "";

            switch (stringMode)
            {
                case StringMode.Any: Config.Set(textbox.Name, text); break;
                case StringMode.Int: Config.Set(textbox.Name, text.GetInt().ToString()); break;
                case StringMode.Float: Config.Set(textbox.Name, text.GetFloat().ToStringDot()); break;
            }
        }

        public static void SaveGuiElement(ComboBox comboBox, StringMode stringMode = StringMode.Any)
        {
            string text = comboBox.GetText();

            switch (stringMode)
            {
                case StringMode.Any: Config.Set(comboBox.Name, text); break;
                case StringMode.Int: Config.Set(comboBox.Name, text.GetInt().ToString()); break;
                case StringMode.Float: Config.Set(comboBox.Name, text.GetFloat().ToStringDot()); break;
            }
        }

        public static void SaveGuiElement(CheckBox checkbox)
        {
            Config.Set(checkbox.Name, (checkbox.IsChecked == true).ToString());
        }

        public static void SaveGuiElement(NumericUpDown upDown, StringMode stringMode = StringMode.Any)
        {
            decimal value = upDown.Value ?? 0m;

            switch (stringMode)
            {
                case StringMode.Int: Config.Set(upDown.Name, ((int)value).ToString()); break;
                default: Config.Set(upDown.Name, ((float)value).ToStringDot()); break;
            }
        }

        public static void SaveComboxIndex(ComboBox comboBox)
        {
            Config.Set(comboBox.Name, comboBox.SelectedIndex.ToString());
        }

        public static void LoadGuiElement(ComboBox comboBox, string suffix = "")
        {
            comboBox.SelectByText(Config.Get(comboBox.Name) + suffix);
        }

        public static void LoadGuiElement(TextBox textbox, string suffix = "")
        {
            textbox.Text = Config.Get(textbox.Name) + suffix;
        }

        public static void LoadGuiElement(CheckBox checkbox)
        {
            checkbox.IsChecked = Config.GetBool(checkbox.Name);
        }

        public static void LoadGuiElement(NumericUpDown upDown, bool allowFloat = true)
        {
            decimal value = allowFloat ? Convert.ToDecimal(Config.GetFloat(upDown.Name)) : Convert.ToDecimal(Config.GetInt(upDown.Name));
            upDown.Value = Math.Clamp(value, upDown.Minimum, upDown.Maximum);
        }

        public static void LoadComboxIndex(ComboBox comboBox)
        {
            int i = Config.GetInt(comboBox.Name);
            int count = comboBox.ItemCount;

            if (count < 1)
                return; // Box gets filled in later (e.g. once a file is loaded) - nothing to restore yet

            if (i >= 0 && i < count)
                comboBox.SelectedIndex = i;
            else
                Logger.Log($"LoadComboxIndex: [{comboBox.Name}] Index of {i} was loaded but there are only {count} items!", true);
        }

        #region Filter Grids

        /// <summary>
        /// The custom filter grids, whose rows are not controls and so have no name to be keyed by.
        /// Stored as a JSON array rather than one joined string because a filter is arbitrary
        /// ffmpeg syntax - commas, colons, quotes and equals signs are all ordinary characters
        /// inside one - which leaves nothing to join them with.
        /// </summary>
        public static void SaveFilterRows(Config.Key key, IEnumerable<FilterRow> rows)
        {
            // Blank rows are dropped: the grids are edited by adding an empty row and typing into
            // it, so an abandoned one is not a filter and would come back as an empty row forever.
            List<string> filters = rows.Select(x => (x.Filter ?? "").Trim()).Where(x => x.IsNotEmpty()).ToList();
            Config.Set(key, JsonConvert.SerializeObject(filters));
        }

        public static void LoadFilterRows(Config.Key key, ObservableCollection<FilterRow> rows)
        {
            rows.Clear();
            string json = Config.Get(key);

            if (json.IsEmpty())
                return;

            try
            {
                foreach (string filter in JsonConvert.DeserializeObject<List<string>>(json) ?? new List<string>())
                {
                    if (filter.IsNotEmpty())
                        rows.Add(new FilterRow(filter));
                }
            }
            catch (Exception e)
            {
                Logger.Log($"Failed to read the saved custom filters: {e.Message}", true);
            }
        }

        #endregion

        #region Resize

        /// <summary>
        /// The Quick Convert tab's resize, which is an object rather than a control's value and so has
        /// no name to be keyed by. JSON for the same reason the filter grids above are: it carries a
        /// mode, two targets, a percentage, a fill, a modulus, two flags and a resampler name, and
        /// there is no separator a joined string could use that none of those can contain.
        /// <para/>
        /// Quick Convert's alone. The AV1AN tab deliberately restores nothing across sessions, and its
        /// resize starts every launch switched off.
        /// </summary>
        public static void SaveResize(Config.Key key, ResizeConfig cfg)
        {
            Config.Set(key, cfg == null || cfg.Mode == ResizeMode.Disabled ? "" : JsonConvert.SerializeObject(cfg));
        }

        /// <summary> The saved resize, or one that is switched off - which is also what an unreadable
        /// entry comes back as, a resize nobody asked for being worse than none. </summary>
        public static ResizeConfig LoadResize(Config.Key key)
        {
            string json = Config.Get(key);

            if (json.IsEmpty())
                return new ResizeConfig();

            try
            {
                return JsonConvert.DeserializeObject<ResizeConfig>(json) ?? new ResizeConfig();
            }
            catch (Exception e)
            {
                Logger.Log($"Failed to read the saved resize: {e.Message}", true);
                return new ResizeConfig();
            }
        }

        #endregion

        #region Restore

        /// <summary>
        /// Restoring differs from loading in what it does when there is nothing to restore: it
        /// leaves the control exactly as it is, where the Load helpers above read a key that is not
        /// there as zero, empty or False and write that into the control. Whatever the control is
        /// already holding is the better answer - for the encode settings that is the default the
        /// selected encoder has just put there, and for a checkbox it is the state the XAML gives
        /// it, neither of which may be quietly turned into a nought by a config that predates the
        /// setting.
        /// </summary>
        public static void RestoreIfSaved(TextBox textbox)
        {
            if (HasSavedValue(textbox))
                LoadGuiElement(textbox);
        }

        public static void RestoreIfSaved(CheckBox checkbox)
        {
            if (HasSavedValue(checkbox))
                LoadGuiElement(checkbox);
        }

        public static void RestoreIfSaved(NumericUpDown upDown, bool allowFloat = true)
        {
            if (HasSavedValue(upDown))
                LoadGuiElement(upDown, allowFloat);
        }

        /// <summary>
        /// By text, which is what dropdowns filled from the selected encoder need: the same index
        /// is a different preset from one encoder to the next, and a saved entry the current
        /// encoder does not have simply is not found, leaving that encoder's own default selected.
        /// </summary>
        public static void RestoreIfSaved(ComboBox comboBox)
        {
            if (HasSavedValue(comboBox))
                LoadGuiElement(comboBox);
        }

        /// <summary> By index, for the dropdowns filled from a fixed list. </summary>
        public static void RestoreIndexIfSaved(ComboBox comboBox)
        {
            if (HasSavedValue(comboBox))
                LoadComboxIndex(comboBox);
        }

        /// <summary> Asked before reading rather than after: the Get helpers write a default for
        /// any key that is missing, so once one has answered the key exists either way. </summary>
        private static bool HasSavedValue(StyledElement control)
        {
            return Config.cachedValues.ContainsKey(control.Name ?? "");
        }

        #endregion
    }
}
