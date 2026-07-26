using Avalonia.Controls;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Nmkoder.Extensions
{
    /// <summary>
    /// Small helpers that restore the WinForms conveniences the codebase relied on
    /// (ComboBox.Text, Items.AddRange, ...) on top of Avalonia's controls.
    /// </summary>
    public static class AvaloniaExtensions
    {
        /// <summary> WinForms' ComboBox.Text equivalent: the display string of the selected item. </summary>
        public static string GetText(this ComboBox box)
        {
            if (box == null)
                return "";

            if (box.IsEditable && !string.IsNullOrEmpty(box.Text))
                return box.Text;

            return ItemText(box.SelectedItem);
        }

        /// <summary>
        /// Display string of a dropdown entry. Entries filled in from code are the value itself and
        /// read back as one, but entries written in the XAML arrive as ComboBoxItem, whose
        /// ToString() is its type name - so reading one of those dropdowns handed
        /// "avalonia.controls.comboboxitem" to whatever was building a command line.
        /// </summary>
        private static string ItemText(object item)
        {
            if (item is ContentControl content)
                return content.Content?.ToString() ?? "";

            return item?.ToString() ?? "";
        }

        /// <summary> Selects the first item whose string representation matches, ignoring case. </summary>
        public static bool SelectByText(this ComboBox box, string text)
        {
            if (box == null || text == null)
                return false;

            IList items = box.Items;

            for (int i = 0; i < items.Count; i++)
            {
                if (string.Equals(ItemText(items[i]), text, StringComparison.OrdinalIgnoreCase))
                {
                    box.SelectedIndex = i;
                    return true;
                }
            }

            return false;
        }

        public static void AddRange(this ItemCollection items, IEnumerable<object> values)
        {
            foreach (object value in values)
                items.Add(value);
        }

        public static void SetItems(this ComboBox box, IEnumerable<object> values, int selectIndex = -1)
        {
            box.Items.Clear();

            foreach (object value in values)
                box.Items.Add(value);

            if (selectIndex >= 0 && selectIndex < box.ItemCount)
                box.SelectedIndex = selectIndex;
        }

        public static int GetInt(this ComboBox box) => box.GetText().GetInt();
        public static float GetFloat(this ComboBox box) => box.GetText().GetFloat();
        public static int GetInt(this TextBox box) => (box.Text ?? "").GetInt();
        public static float GetFloat(this TextBox box) => (box.Text ?? "").GetFloat();

        public static int AsInt(this decimal? value) => value.HasValue ? (int)value.Value : 0;
        public static float AsFloat(this decimal? value) => value.HasValue ? (float)value.Value : 0f;

        /// <summary> Assigns Value while making sure it stays inside the control's own bounds. </summary>
        public static void SetValueClamped(this NumericUpDown upDown, decimal value)
        {
            upDown.Value = Math.Clamp(value, upDown.Minimum, upDown.Maximum);
        }

        /// <summary> Sets Minimum/Maximum in an order that never trips the control's own validation. </summary>
        public static void SetRange(this NumericUpDown upDown, decimal min, decimal max)
        {
            if (max < min)
                max = min;

            upDown.Minimum = decimal.MinValue;
            upDown.Maximum = decimal.MaxValue;
            upDown.Minimum = min;
            upDown.Maximum = max;
        }
    }
}
