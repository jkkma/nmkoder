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
    }
}
