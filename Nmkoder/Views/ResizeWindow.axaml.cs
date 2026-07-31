using Avalonia.Controls;
using Avalonia.Interactivity;
using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Nmkoder.Views
{
    public partial class ResizeWindow : Window
    {
        /// <summary> The configured resize, or null when the dialog was dismissed without confirming. </summary>
        public ResizeConfig Resize { get; private set; }

        /// <summary> The order the mode dropdown lists them in, so index maps to value. Disabled is not offered:
        /// turning the resize off is what the dropdown on the tab itself is for. </summary>
        private static readonly ResizeMode[] Modes =
            { ResizeMode.Fit, ResizeMode.Height, ResizeMode.Width, ResizeMode.Percent, ResizeMode.Exact };

        /// <summary> swscale's own names, paired with what to call them. "" is ffmpeg's default. </summary>
        private static readonly List<KeyValuePair<string, string>> Resamplers = new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>("", "Bicubic (default)"),
            new KeyValuePair<string, string>("lanczos", "Lanczos"),
            new KeyValuePair<string, string>("spline", "Spline"),
            new KeyValuePair<string, string>("bilinear", "Bilinear"),
            new KeyValuePair<string, string>("area", "Area"),
            new KeyValuePair<string, string>("neighbor", "Nearest neighbor"),
        };

        private static readonly int[] Moduli = { 2, 4, 8, 16 };

        private Size _storage;
        private Size _sar;
        private ResizeConfig _cfg;
        private bool _ready;

        public ResizeWindow()
        {
            InitializeComponent();
        }

        public static async Task<ResizeConfig> Show(Size resolution, Size sar, ResizeConfig saved)
        {
            var window = new ResizeWindow();
            window.Load(resolution, sar, saved);

            Window owner = UiUtils.MainWindowHandle;

            if (owner != null && owner.IsVisible)
                await window.ShowDialog(owner);
            else
                window.Show();

            return window.Resize;
        }

        private void Load(Size resolution, Size sar, ResizeConfig saved)
        {
            _ready = false;
            _storage = resolution;
            _sar = sar;
            NoVidPanel.IsVisible = resolution.IsEmpty;

            // Whatever is already configured, or - for a resize being set up for the first time - the
            // source's own size in a mode that leaves it alone, so the numbers start somewhere real.
            _cfg = saved?.Clone() ?? DefaultFor(resolution, sar);
            _cfg.Mode = _cfg.Mode == ResizeMode.Disabled ? ResizeMode.Fit : _cfg.Mode;

            ModeBox.SetItems(new[]
            {
                "Fit inside a box (keeps the aspect ratio)",
                "Set the height (width follows)",
                "Set the width (height follows)",
                "Scale by a percentage",
                "Exact size",
            }.Select(x => (object)x), Math.Max(0, Array.IndexOf(Modes, _cfg.Mode)));

            ResamplerBox.SetItems(Resamplers.Select(x => (object)x.Value),
                Math.Max(0, Resamplers.FindIndex(x => x.Key == (_cfg.Resampler ?? ""))));

            ModulusBox.SelectedIndex = Math.Max(0, Array.IndexOf(Moduli, _cfg.Modulus));
            FillBox.SelectedIndex = (int)_cfg.Fill;
            UpscaleBox.IsChecked = _cfg.AllowUpscale;
            AnamorphicBox.IsChecked = _cfg.CorrectAspect;

            // Nothing to correct on a square-pixel source, and offering the switch anyway invites the
            // reading that it does something to one.
            AnamorphicBox.IsEnabled = AspectRatio.IsAnamorphic(sar);

            TargetW.SetValueClamped(_cfg.TargetWidth);
            TargetH.SetValueClamped(_cfg.TargetHeight);
            TargetSingle.SetValueClamped(_cfg.Mode == ResizeMode.Width ? _cfg.TargetWidth : _cfg.TargetHeight);
            TargetPercent.SetValueClamped(_cfg.Percent);

            _ready = true;
            ApplyMode();
        }

        /// <summary> The starting point for a resize with nothing saved behind it: the source's own display size. </summary>
        private static ResizeConfig DefaultFor(Size resolution, Size sar)
        {
            var cfg = new ResizeConfig { Mode = ResizeMode.Fit };
            Size display = AspectRatio.GetDisplaySize(resolution, sar);

            if (display.Width > 0 && display.Height > 0)
            {
                cfg.TargetWidth = ResizeConfig.RoundNearest(display.Width, 2);
                cfg.TargetHeight = ResizeConfig.RoundNearest(display.Height, 2);
            }

            return cfg;
        }

        #region Handlers

        private void Mode_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyMode();
        private void Fill_SelectionChanged(object sender, SelectionChangedEventArgs e) => ReadUi();
        private void Modulus_SelectionChanged(object sender, SelectionChangedEventArgs e) => ReadUi();
        private void Resampler_SelectionChanged(object sender, SelectionChangedEventArgs e) => ReadUi();
        private void Option_Changed(object sender, RoutedEventArgs e) => ReadUi();
        private void Target_ValueChanged(object sender, NumericUpDownValueChangedEventArgs e) => ReadUi();

        /// <summary> Shows the rows the selected mode has a use for, then re-reads everything. </summary>
        private void ApplyMode()
        {
            if (!_ready)
                return;

            ResizeMode mode = Modes[ModeBox.SelectedIndex.Clamp(0, Modes.Length - 1)];

            SizeRow.IsVisible = mode == ResizeMode.Fit || mode == ResizeMode.Exact;
            SingleRow.IsVisible = mode == ResizeMode.Height || mode == ResizeMode.Width;
            PercentRow.IsVisible = mode == ResizeMode.Percent;
            FillRow.IsVisible = mode == ResizeMode.Exact;

            SizeRowLabel.Text = mode == ResizeMode.Exact ? "Exact size" : "Fit inside";
            SingleRowLabel.Text = mode == ResizeMode.Width ? "Width" : "Height";

            // Upscaling is what a percentage over 100 asks for outright, so the switch has no say there.
            UpscaleBox.IsEnabled = mode != ResizeMode.Percent;

            ReadUi();
        }

        /// <summary> Pulls the controls into the config and refreshes the readout. </summary>
        private void ReadUi()
        {
            if (!_ready)
                return;

            _cfg.Mode = Modes[ModeBox.SelectedIndex.Clamp(0, Modes.Length - 1)];
            _cfg.Fill = (ResizeFill)FillBox.SelectedIndex.Clamp(0, 2);
            _cfg.Modulus = Moduli[ModulusBox.SelectedIndex.Clamp(0, Moduli.Length - 1)];
            _cfg.Resampler = Resamplers[ResamplerBox.SelectedIndex.Clamp(0, Resamplers.Count - 1)].Key;
            _cfg.AllowUpscale = UpscaleBox.IsChecked == true;
            _cfg.CorrectAspect = AnamorphicBox.IsChecked == true;
            _cfg.Percent = TargetPercent.Value.AsInt();

            if (_cfg.Mode == ResizeMode.Height)
                _cfg.TargetHeight = TargetSingle.Value.AsInt();
            else if (_cfg.Mode == ResizeMode.Width)
                _cfg.TargetWidth = TargetSingle.Value.AsInt();
            else
            {
                _cfg.TargetWidth = TargetW.Value.AsInt();
                _cfg.TargetHeight = TargetH.Value.AsInt();
            }

            // Configured by hand is no longer any of the tab's presets, whichever one it started from.
            _cfg.PresetKey = "";

            UpdateLabels();
        }

        private void UpdateLabels()
        {
            SourceLabel.Text = DescribeSource();
            ResultLabel.Text = DescribeResult();
        }

        private string DescribeSource()
        {
            if (_storage.IsEmpty)
                return "No video track loaded.";

            Size display = AspectRatio.GetDisplaySize(_storage, _sar);
            string ratio = AspectRatio.Describe(display.Width, display.Height, withName: true);

            if (!AspectRatio.IsAnamorphic(_sar))
                return $"{_storage.Width}x{_storage.Height} — {ratio}";

            // Worth spelling out, because it is the case where the stored numbers are not the shape.
            return $"{_storage.Width}x{_storage.Height} stored, shown as {display.Width}x{display.Height} " +
                   $"— {ratio}, with {_sar.Width}:{_sar.Height} pixels";
        }

        private string DescribeResult()
        {
            if (_storage.IsEmpty)
                return $"{_cfg.DescribeTarget()} — the size is worked out per file when the encode starts.";

            Size result = _cfg.Compute(_storage, _sar);

            if (result.IsEmpty)
                return "-";

            // The same clause the tab's readout carries, from the same place, so the two never disagree
            string line = $"{result.Width}x{result.Height} — {AspectRatio.Describe(result.Width, result.Height, withName: true)}";
            string note = _cfg.GetNote(_storage, _sar);
            return note.Length > 0 ? $"{line}, {note}" : line;
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            Load(_storage, _sar, null);
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            ReadUi();
            Resize = _cfg;
            Close();
        }

        #endregion
    }
}
