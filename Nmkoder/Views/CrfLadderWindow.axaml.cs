using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Nmkoder.Data;
using Nmkoder.Data.Codecs;
using Nmkoder.Data.Colors;
using Nmkoder.Extensions;
using Nmkoder.UI;
using Nmkoder.UI.Tasks;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Nmkoder.Views
{
    /// <summary>
    /// The Sample Encodes utility's own settings.
    /// <para/>
    /// The encoder, preset and colour format are here rather than read off an encode tab for the
    /// reason <see cref="DeinterlaceWindow"/> gives about its own: this utility produces a number to
    /// type into a tab, so reading a tab to produce it would be circular, and the person running a
    /// ladder is deciding what to set rather than acting on what is already set.
    /// </summary>
    public partial class CrfLadderWindow : Window
    {
        private MediaFile _file;
        private bool _ready;

        public CrfLadderWindow()
        {
            InitializeComponent();
        }

        public static async Task ShowAsync()
        {
            var window = new CrfLadderWindow();
            window.Load();

            Window owner = UiUtils.MainWindowHandle;

            if (owner != null && owner.IsVisible)
                await window.ShowDialog(owner);
            else
                window.Show();
        }

        private void Load(bool resetToDefaults = false)
        {
            _ready = false;
            _file = TrackList.current?.File;

            if (resetToDefaults)
            {
                UtilCrfLadder.Encoder = UtilCrfLadder.Encoders[0];
                UtilCrfLadder.Preset = "";
                UtilCrfLadder.ColorFormatIndex = -1;
                UtilCrfLadder.Crfs = "";
                UtilCrfLadder.SampleCount = 3;
                UtilCrfLadder.SampleSeconds = 10;
                UtilCrfLadder.Score = CrfLadder.Metric.Vmaf;
                UtilCrfLadder.VmafModel = 0;
                UtilCrfLadder.KeepSamples = false;
            }

            EncoderBox.SetItems(UtilCrfLadder.Encoders.Select(c => (object)CodecUtils.GetCodec(c).FriendlyName),
                Math.Max(0, Array.IndexOf(UtilCrfLadder.Encoders, UtilCrfLadder.Encoder)));

            SampleCountBox.Value = UtilCrfLadder.SampleCount.Clamp(1, 8);
            SampleSecsBox.Value = UtilCrfLadder.SampleSeconds.Clamp(2, 120);

            // Filled from the display order rather than the enum's numeric order - see
            // CrfLadder.MetricOrder - so SSIMULACRA2 could be added without moving VMAF's or XPSNR's
            // saved value. The selected index maps back through the same array in ReadUi.
            MetricBox.SetItems(CrfLadder.MetricOrder.Select(m => (object)CrfLadder.MetricName(m)),
                Math.Max(0, Array.IndexOf(CrfLadder.MetricOrder, UtilCrfLadder.Score)));
            VmafModelBox.SelectedIndex = UtilCrfLadder.VmafModel.Clamp(0, 2);
            KeepBox.IsChecked = UtilCrfLadder.KeepSamples;

            LoadEncoderLists();

            _ready = true;
            ReadUi();
        }

        /// <summary>
        /// The preset and colour format lists, which are the encoder's own and are refilled whenever it
        /// changes. The CRF box goes with them: its placeholder is the default for *this* encoder, and
        /// a typed value is left alone rather than being rewritten - a 22 that means something on x264
        /// is a legitimate thing to have typed before switching to x265 to compare.
        /// </summary>
        private void LoadEncoderLists()
        {
            IEncoder enc = UtilCrfLadder.GetEncoder();

            PresetBox.SetItems(enc.Presets.Select(p => (object)p),
                Math.Max(0, Array.IndexOf(enc.Presets, UtilCrfLadder.GetPreset())));

            ColorsBox.SetItems(enc.ColorFormats.Select(f => (object)PixFmtUtils.GetFormat(f).FriendlyName),
                UtilCrfLadder.GetColorFormatIndex().Clamp(0, Math.Max(0, enc.ColorFormats.Count - 1)));

            CrfBox.Text = UtilCrfLadder.Crfs;
            CrfBox.PlaceholderText = CrfLadder.Format(CrfLadder.DefaultCrfs(enc));
            PresetHint.Text = enc.PresetInfo;
        }

        #region Handlers

        private void Encoder_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready)
                return;

            // Written before the lists are refilled, because both of them are read back out of the new
            // encoder's own arrays and GetPreset/GetColorFormatIndex need to know which encoder that is.
            UtilCrfLadder.Encoder = UtilCrfLadder.Encoders[EncoderBox.SelectedIndex.Clamp(0, UtilCrfLadder.Encoders.Length - 1)];

            // The preset and colour format do not carry across: the lists are named differently per
            // encoder, so keeping an index would silently pick something else and keeping a name would
            // pick nothing. Both fall back to the new encoder's own defaults.
            UtilCrfLadder.Preset = "";
            UtilCrfLadder.ColorFormatIndex = -1;

            _ready = false;
            LoadEncoderLists();
            _ready = true;

            ReadUi();
        }

        private void Setting_SelectionChanged(object sender, SelectionChangedEventArgs e) => ReadUi();
        private void Number_ValueChanged(object sender, NumericUpDownValueChangedEventArgs e) => ReadUi();
        private void Option_Changed(object sender, RoutedEventArgs e) => ReadUi();
        private void Crf_Changed(object sender, RoutedEventArgs e) => ReadUi();

        /// <summary> So the readout answers while the box is being typed in, rather than only once the
        /// focus has moved somewhere else. </summary>
        private void Crf_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;

            // Handled, or the KeyDown bubbles to the window and the IsDefault OK button clicks - so
            // Enter would save and close the dialog rather than refresh this readout.
            e.Handled = true;
            ReadUi();
        }

        private void ReadUi()
        {
            if (!_ready)
                return;

            IEncoder enc = UtilCrfLadder.GetEncoder();

            UtilCrfLadder.Preset = PresetBox.GetText();
            UtilCrfLadder.ColorFormatIndex = ColorsBox.SelectedIndex.Clamp(0, Math.Max(0, enc.ColorFormats.Count - 1));
            UtilCrfLadder.Crfs = CrfBox.Text ?? "";
            UtilCrfLadder.SampleCount = SampleCountBox.Value.AsInt().Clamp(1, 8);
            UtilCrfLadder.SampleSeconds = SampleSecsBox.Value.AsInt().Clamp(2, 120);
            UtilCrfLadder.Score = CrfLadder.MetricOrder[MetricBox.SelectedIndex.Clamp(0, CrfLadder.MetricOrder.Length - 1)];
            UtilCrfLadder.VmafModel = VmafModelBox.SelectedIndex.Clamp(0, 2);
            UtilCrfLadder.KeepSamples = KeepBox.IsChecked == true;

            // The model only means something where VMAF is what runs. Disabled rather than hidden, the
            // row being shared with the metric box beside it.
            VmafModelBox.IsEnabled = UtilCrfLadder.Score == CrfLadder.Metric.Vmaf;

            UpdateLabels();
        }

        private void UpdateLabels()
        {
            IEncoder enc = UtilCrfLadder.GetEncoder();
            int[] crfs = UtilCrfLadder.GetCrfs();

            // Says what will run rather than what was typed, so a value outside the encoder's range or
            // a list past the cap is visible before the run rather than in the results.
            string typed = (CrfBox.Text ?? "").Trim();
            CrfHint.Text = typed.IsEmpty() ? $"{enc.QInfo}" : $"→ {CrfLadder.Format(crfs)}  ({enc.QMin}-{enc.QMax})";

            SourceLabel.Text = _file == null
                ? "No file is loaded. The settings are saved either way."
                : $"{_file.Name.Trunc(60)} — {Utils.FormatUtils.Time(_file.DurationMs)}";

            ResultLabel.Text = UtilCrfLadder.Describe(_file);
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            Load(resetToDefaults: true);
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            ReadUi();
            UtilCrfLadder.SaveSettings();
            Close();
        }

        #endregion
    }
}
