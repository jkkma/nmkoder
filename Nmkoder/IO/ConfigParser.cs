using Avalonia;
using Avalonia.Controls;
using Nmkoder.Extensions;
using System;

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
