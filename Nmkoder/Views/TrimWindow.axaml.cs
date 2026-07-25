using Avalonia.Controls;
using Avalonia.Interactivity;
using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.UI;
using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Nmkoder.Views
{
    public partial class TrimWindow : Window
    {
        /// <summary> Result of the dialog. Null means "no trim". </summary>
        public TrimSettings NewTrimSettings { get; private set; }

        private long _originalDuration;
        private bool _ready;
        private bool _confirmed;

        public TrimWindow()
        {
            InitializeComponent();
        }

        public static async Task<TrimSettings> Show(long originalDurationMs, TrimSettings savedTrim)
        {
            var window = new TrimWindow();
            window._originalDuration = originalDurationMs;
            window.LoadSettings(savedTrim);

            Window owner = UiUtils.MainWindowHandle;

            if (owner != null && owner.IsVisible)
                await window.ShowDialog(owner);
            else
                window.Show();

            // Dismissing without confirming keeps whatever was configured before.
            return window._confirmed ? window.NewTrimSettings : savedTrim;
        }

        private void LoadSettings(TrimSettings loaded)
        {
            _ready = false;

            if (loaded != null)
            {
                TrimMode.SelectedIndex = (int)loaded.TrimMode;
                StartBox.Text = IsFrameMode() ? loaded.StartTime.ToString() : TrimSettings.GetTimeString(TimeSpan.FromMilliseconds(loaded.StartTime));
                EndBox.Text = IsFrameMode() ? loaded.EndTime.ToString() : TrimSettings.GetTimeString(TimeSpan.FromMilliseconds(loaded.EndTime));
            }
            else
            {
                TrimMode.SelectedIndex = 0;
                StartBox.Text = TrimSettings.GetTimeString(TimeSpan.Zero);
                EndBox.Text = TrimSettings.GetTimeString(TimeSpan.FromMilliseconds(_originalDuration > 0 ? _originalDuration : 0));
            }

            _ready = true;
            UpdateDuration();
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            LoadSettings(null);
        }

        private async void Confirm_Click(object sender, RoutedEventArgs e)
        {
            TimeSpan start = TimeSpan.Zero;
            TimeSpan end = TimeSpan.Zero;

            bool parseSuccess = IsFrameMode()
                ? Regex.IsMatch(StartBox.Text ?? "", @"^\d+$") && Regex.IsMatch(EndBox.Text ?? "", @"^\d+$")
                : TryParseTime(StartBox.Text, out start) && TryParseTime(EndBox.Text, out end);

            if (!parseSuccess)
            {
                await UiUtils.ShowMessageBox($"Invalid input.\n\n{(IsFrameMode() ? "Please enter numeric values only." : "Please use the HH:MM:SS (or HH:MM:SS.mmm) format.")}", UiUtils.MessageType.Error);
                return;
            }

            var trimSettings = new TrimSettings()
            {
                TrimMode = (TrimSettings.Mode)Math.Max(0, TrimMode.SelectedIndex),
                StartTime = IsFrameMode() ? (StartBox.Text ?? "").GetLong() : (long)start.TotalMilliseconds,
                Duration = IsFrameMode() ? (DurationBox.Text ?? "").GetLong() : (long)GetDuration(start, end).TotalMilliseconds,
                EndTime = IsFrameMode() ? (EndBox.Text ?? "").GetLong() : (long)end.TotalMilliseconds
            };

            NewTrimSettings = trimSettings.IsUnset ? null : trimSettings;
            _confirmed = true;
            Close();
        }

        private bool IsFrameMode()
        {
            return TrimMode.SelectedIndex == 2;
        }

        private static bool TryParseTime(string text, out TimeSpan ts)
        {
            text = text ?? "";

            if (text.Contains(":"))
                return TimeSpan.TryParse(text, out ts);

            ts = TimeSpan.FromSeconds(text.GetInt());
            return true;
        }

        private void Time_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateDuration();
        }

        private void UpdateDuration()
        {
            if (!_ready)
                return;

            if (!IsFrameMode()) // Time mode
            {
                if (TryParseTime(StartBox.Text, out TimeSpan start) && TryParseTime(EndBox.Text, out TimeSpan end))
                    DurationBox.Text = TrimSettings.GetTimeString(GetDuration(start, end));
                else
                    DurationBox.Text = TrimSettings.GetTimeString(TimeSpan.Zero);
            }
            else // Frame number mode
            {
                long startFrames = (StartBox.Text ?? "").GetLong();
                long endFrames = (EndBox.Text ?? "").GetLong();
                DurationBox.Text = (endFrames - startFrames).Clamp(0, long.MaxValue).ToString();
            }
        }

        private static TimeSpan GetDuration(TimeSpan start, TimeSpan end)
        {
            TimeSpan duration = end - start;
            return duration.Ticks < 0 ? TimeSpan.Zero : duration;
        }

        private void TrimMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready)
                return;

            if (IsFrameMode())
            {
                StartBox.Text = "0";
                EndBox.Text = "0";
            }
            else if (!((StartBox.Text ?? "").Contains(":") && (EndBox.Text ?? "").Contains(":")))
            {
                StartBox.Text = TrimSettings.GetTimeString(TimeSpan.Zero);
                EndBox.Text = TrimSettings.GetTimeString(TimeSpan.FromMilliseconds(_originalDuration > 0 ? _originalDuration : 0));
            }

            UpdateDuration();
        }
    }
}
