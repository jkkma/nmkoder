using Avalonia.Controls;
using Avalonia.Interactivity;
using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.Media;
using Nmkoder.UI;
using Nmkoder.UI.Tasks;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Nmkoder.Views
{
    /// <summary>
    /// The Film Grain utility's settings: which of grav1synth's four jobs to run on the loaded file, and
    /// what each of them needs.
    /// <para/>
    /// Four operations on one card rather than four cards, because they are one tool and the file they act
    /// on is the same one - and because three of them take seconds, so a card each would give the
    /// Utilities tab three rows for something almost nobody does twice. The expensive one is Measure, and
    /// the dialog says how expensive before it is picked rather than after.
    /// </summary>
    public partial class FilmGrainWindow : Window
    {
        private MediaFile _file;
        private bool _ready;

        /// <summary> The three sources Apply can take, in the order the dropdown offers them. Held as
        /// <see cref="GrainSynthMode"/> values so the Grain Synthesis row and this dialog cannot come to
        /// mean different things by "a preset". </summary>
        private static readonly GrainSynthMode[] ApplySources =
            { GrainSynthMode.Table, GrainSynthMode.Preset, GrainSynthMode.PhotonNoise };

        public FilmGrainWindow()
        {
            InitializeComponent();
        }

        public static async Task ShowAsync()
        {
            var window = new FilmGrainWindow();
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
                UtilFilmGrain.Operation = UtilFilmGrain.Op.Measure;
                UtilFilmGrain.DenoiseStrength = 4;
                UtilFilmGrain.KeepDenoised = false;
                UtilFilmGrain.ApplySource = new GrainSynthConfig { Mode = GrainSynthMode.Table };
            }

            var ops = (UtilFilmGrain.Op[])Enum.GetValues(typeof(UtilFilmGrain.Op));
            OpBox.SetItems(ops.Select(o => (object)GetOpLabel(o)), Math.Max(0, Array.IndexOf(ops, UtilFilmGrain.Operation)));

            DenoiseBox.Value = UtilFilmGrain.DenoiseStrength;
            KeepDenoisedBox.IsChecked = UtilFilmGrain.KeepDenoised;

            SourceBox.SetItems(ApplySources.Select(s => (object)GetSourceLabel(s)),
                Math.Max(0, Array.IndexOf(ApplySources, UtilFilmGrain.ApplySource.Mode)));

            PresetBox.SetItems(GrainSynthConfig.Presets.Select(p => (object)p),
                Math.Max(0, Array.IndexOf(GrainSynthConfig.Presets, UtilFilmGrain.ApplySource.Preset)));

            TableBox.Text = UtilFilmGrain.ApplySource.TablePath;
            IsoBox.Value = UtilFilmGrain.ApplySource.Iso;
            ChromaBox.IsChecked = UtilFilmGrain.ApplySource.Chroma;

            _ready = true;
            ReadUi();
        }

        private static string GetOpLabel(UtilFilmGrain.Op op)
        {
            switch (op)
            {
                case UtilFilmGrain.Op.Measure: return "Measure a grain table from this file";
                case UtilFilmGrain.Op.Extract: return "Extract the grain table it already has";
                case UtilFilmGrain.Op.Apply: return "Apply grain to this file";
                default: return "Remove all grain from this file";
            }
        }

        private static string GetSourceLabel(GrainSynthMode mode)
        {
            switch (mode)
            {
                case GrainSynthMode.Preset: return "Film stock preset";
                case GrainSynthMode.PhotonNoise: return "Photon noise (ISO)";
                default: return "Grain table file";
            }
        }

        #region Handlers

        private void Op_SelectionChanged(object sender, SelectionChangedEventArgs e) => ReadUi();
        private void Source_SelectionChanged(object sender, SelectionChangedEventArgs e) => ReadUi();
        private void Option_Changed(object sender, RoutedEventArgs e) => ReadUi();
        private void Option_ValueChanged(object sender, NumericUpDownValueChangedEventArgs e) => ReadUi();

        private async void Browse_Click(object sender, RoutedEventArgs e)
        {
            string[] paths = await Pickers.PickFiles(this, "Pick a film grain table", allowMultiple: false);

            if (paths == null || paths.Length < 1 || paths[0].IsEmpty())
                return;

            TableBox.Text = paths[0];
            ReadUi();
        }

        /// <summary> Edited in place, in the shape the other Configure dialogs use: there is no Cancel, so
        /// every control writes straight through to what the utility will run. </summary>
        private void ReadUi()
        {
            if (!_ready)
                return;

            var ops = (UtilFilmGrain.Op[])Enum.GetValues(typeof(UtilFilmGrain.Op));
            UtilFilmGrain.Operation = ops[OpBox.SelectedIndex.Clamp(0, ops.Length - 1)];
            UtilFilmGrain.DenoiseStrength = DenoiseBox.Value.AsInt();
            UtilFilmGrain.KeepDenoised = KeepDenoisedBox.IsChecked == true;

            UtilFilmGrain.ApplySource = new GrainSynthConfig
            {
                Mode = ApplySources[SourceBox.SelectedIndex.Clamp(0, ApplySources.Length - 1)],
                TablePath = (TableBox.Text ?? "").Trim(),
                Preset = PresetBox.GetText(),
                Iso = IsoBox.Value.AsInt(),
                Chroma = ChromaBox.IsChecked == true,
            };

            // Hidden rather than disabled, as the encode tab's row does it: a control belonging to another
            // operation is not being ignored so much as not asked for.
            MeasureRow.IsVisible = UtilFilmGrain.Operation == UtilFilmGrain.Op.Measure;
            ApplyRow.IsVisible = UtilFilmGrain.Operation == UtilFilmGrain.Op.Apply;
            TablePanel.IsVisible = UtilFilmGrain.ApplySource.Mode == GrainSynthMode.Table;
            PresetBox.IsVisible = UtilFilmGrain.ApplySource.Mode == GrainSynthMode.Preset;
            IsoPanel.IsVisible = UtilFilmGrain.ApplySource.Mode == GrainSynthMode.PhotonNoise;

            UpdateLabels();
        }

        private void UpdateLabels()
        {
            SourceLabel.Text = DescribeSource();
            ResultLabel.Text = DescribeResult();
            WarningLabel.Text = DescribeWarning();
        }

        /// <summary> The loaded file and the one thing about it that decides whether three of the four
        /// operations can run at all, which is whether it is AV1. </summary>
        private string DescribeSource()
        {
            if (_file == null || _file.VideoStreams.Count < 1)
                return "No file is loaded. The setting is saved either way, and applied to whichever file is loaded when this runs.";

            string codec = (_file.VideoStreams.First().Codec ?? "").ToUpperInvariant();
            return $"{_file.Name.Trunc(60)} — {(codec.IsEmpty() ? "unknown codec" : codec)}";
        }

        private string DescribeResult()
        {
            switch (UtilFilmGrain.Operation)
            {
                case UtilFilmGrain.Op.Measure:
                    return $"Denoises the source with {new GrainSynthConfig { DenoiseStrength = UtilFilmGrain.DenoiseStrength }.GetDenoiseFilter()}, " +
                        $"measures what came out against the original, and writes '<name>.grain.tbl' beside it" +
                        $"{(UtilFilmGrain.KeepDenoised ? ", keeping the denoised video as '<name>_denoised.mkv'" : "")}. " +
                        $"Works on any source - it compares decoded frames, not bitstreams.";
                case UtilFilmGrain.Op.Extract:
                    return "Reads the grain table out of an AV1 file that already synthesises grain, into " +
                        "'<name>.grain.tbl'. Takes seconds; it only reads headers. A file with no grain in it has no " +
                        "table to give.";
                case UtilFilmGrain.Op.Apply:
                    return "Writes the grain into the AV1 headers of a copy, '<name>_grain', without re-encoding " +
                        "anything. The picture is untouched and the file is barely larger; every AV1 decoder " +
                        "regenerates the grain at playback.";
                default:
                    return "Strips every grain header out of a copy, '<name>_nograin', without re-encoding anything. " +
                        "What goes is the grain the decoder was adding, not anything that was coded.";
            }
        }

        /// <summary> The cost, or the thing that will stop it, for the file in front of the user. </summary>
        private string DescribeWarning()
        {
            if (!Grav1synth.IsAvailable())
                return "grav1synth is not bundled with this build and is not on your PATH, so none of these can run.";

            if (_file == null || _file.VideoStreams.Count < 1)
                return "";

            if (UtilFilmGrain.Operation != UtilFilmGrain.Op.Measure)
            {
                string codec = (_file.VideoStreams.First().Codec ?? "").ToLowerInvariant();

                return codec == "av1" ? "" : $"This file is not AV1, so this operation cannot run on it - the film " +
                    $"grain it edits lives in an AV1 bitstream. Measure works on any source.";
            }

            var v = _file.VideoStreams.First();

            if (_file.DurationMs < 1 || v.Rate.GetFloat() <= 0)
                return "Measuring reads every frame of the source twice, single-threaded, and writes a lossless " +
                    "intermediate the length of the video.";

            long frames = (long)(_file.DurationMs / 1000d * v.Rate.GetFloat());
            TimeSpan estimate = Grav1synth.EstimateDiffTime(frames, v.Resolution);

            return $"Measuring alone is about {Utils.FormatUtils.Time(estimate, allowMs: false)} for this file, " +
                $"single-threaded, plus a lossless intermediate the length of the video. The AV1AN tab's Grain " +
                $"Synthesis row does this and the encode in one run.";
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            Load(resetToDefaults: true);
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            ReadUi();
            UtilFilmGrain.SaveSettings();
            Close();
        }

        #endregion
    }
}
