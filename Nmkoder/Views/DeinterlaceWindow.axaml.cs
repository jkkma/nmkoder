using Avalonia.Controls;
using Avalonia.Interactivity;
using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.Media;
using Nmkoder.UI;
using Nmkoder.UI.Tasks;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Nmkoder.Views
{
    /// <summary>
    /// The Deinterlace For Encoding utility's own settings.
    /// <para/>
    /// A separate dialog rather than a second reading of the Quick Convert tab's row, because the
    /// people who use this utility are the ones who do not use that tab: it exists precisely because
    /// the AV1AN tab cannot run QTGMC itself. Sending them somewhere else to change a setting that
    /// also changes what that somewhere else does was the wrong shape.
    /// <para/>
    /// The engines offered are the same five, though. This runs the same VapourSynth pipe Quick
    /// Convert does - the difference is only where the frames end up.
    /// </summary>
    public partial class DeinterlaceWindow : Window
    {
        private DeinterlaceRequest _cfg;
        private MediaFile _file;
        private bool _ready;

        public DeinterlaceWindow()
        {
            InitializeComponent();
        }

        public static async Task ShowAsync()
        {
            var window = new DeinterlaceWindow();
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
                UtilDeinterlace.Settings = UtilDeinterlace.Defaults();

            // Edited in place: this dialog has no Cancel, in the shape the other Configure dialogs
            // use, so every control writes straight through to the setting the utility will run.
            _cfg = UtilDeinterlace.Settings;

            ModeBox.SetItems(DeinterlaceUi.AllModes.Select(m => (object)DeinterlaceUi.GetLabel(m)),
                Math.Max(0, Array.IndexOf(DeinterlaceUi.AllModes, _cfg.Mode)));

            PresetBox.SetItems(Qtgmc.Presets.Select(p => (object)p),
                Math.Max(0, Array.IndexOf(Qtgmc.Presets, _cfg.QtgmcPreset)));

            DoubleRateBox.IsChecked = _cfg.DoubleRate;

            _ready = true;
            ReadUi();
        }

        #region Handlers

        private void Mode_SelectionChanged(object sender, SelectionChangedEventArgs e) => ReadUi();
        private void Preset_SelectionChanged(object sender, SelectionChangedEventArgs e) => ReadUi();
        private void Option_Changed(object sender, RoutedEventArgs e) => ReadUi();

        private void ReadUi()
        {
            if (!_ready)
                return;

            _cfg.Mode = DeinterlaceUi.AllModes[ModeBox.SelectedIndex.Clamp(0, DeinterlaceUi.AllModes.Length - 1)];
            _cfg.QtgmcPreset = PresetBox.GetText().IsEmpty() ? Qtgmc.DefaultPreset : PresetBox.GetText();
            _cfg.DoubleRate = DoubleRateBox.IsChecked == true;

            // The preset only means something where QTGMC could be what runs. Hidden rather than
            // disabled, so a row that does not apply is not a control that looks broken.
            PresetRow.IsVisible = _cfg.Mode == DeinterlaceMode.Qtgmc || _cfg.Mode == DeinterlaceMode.Automatic;
            DoubleRateBox.IsEnabled = _cfg.Mode != DeinterlaceMode.Disabled;

            UpdateLabels();
        }

        private void UpdateLabels()
        {
            SourceLabel.Text = DescribeSource();

            // The same sentence the encode tabs put under their own dropdown, from the same place, so
            // the two can never disagree about what a given setting does to a given file.
            ResultLabel.Text = Deinterlace.DescribeForUi(_file, _cfg);
        }

        private string DescribeSource()
        {
            if (_file == null || _file.VideoStreams.Count < 1)
                return "No file is loaded. The setting is saved either way, and applied to whichever file is loaded when this runs.";

            string scan = _file.Interlacing == null ? "still being checked" : _file.Interlacing.DescribeOrder();
            return $"{_file.Name.Trunc(60)} — {scan}";
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            Load(resetToDefaults: true);
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            ReadUi();
            UtilDeinterlace.SaveSettings();
            Close();
        }

        #endregion
    }
}
