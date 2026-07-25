using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Nmkoder.Data;
using Nmkoder.Data.Codecs;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Data.Ui;
using Nmkoder.UI;
using Nmkoder.UI.Tasks;
using Nmkoder.Utils;
using System;
using System.IO;

namespace Nmkoder.Views
{
    partial class MainWindow
    {
        public void InitAv1an()
        {
            Av1anAudChannelsBox.SelectedIndex = 1;
            Av1anCropBox.SelectedIndex = 0;
            Av1anOptsConcatModeBox.SelectedIndex = 0;
            Av1anOptsChunkOrderBox.SelectedIndex = 0;
        }

        private void Av1anCodec_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized)
                return;

            Av1anUi.VidEncoderSelected(Av1anCodecBox.SelectedIndex);
            SaveConfigAv1an();
        }

        private void Av1anQualityMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized)
                return;

            if (Av1anUi.IsUsingVmaf())
            {
                Av1anQualityUpDown.SetRange(10, 99);
                Av1anQualityUpDown.Value = 95;
            }
            else
            {
                IEncoder enc = CodecUtils.GetCodec((CodecUtils.Av1anCodec)Math.Max(0, Av1anCodecBox.SelectedIndex));
                Av1anQualityUpDown.SetRange(enc.QMin, enc.QMax);
                Av1anQualityUpDown.SetValueClamped(enc.QDefault);
            }
        }

        private void Av1anContainer_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized)
                return;

            SaveConfigAv1an();
            Av1anUi.ValidateContainer();
        }

        private void Av1anAudCodec_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized)
                return;

            SaveConfigAv1an();
            Av1anUi.AudEncoderSelected(Av1anAudCodecBox.SelectedIndex);
        }

        private void Av1anOption_SelectionChanged(object sender, SelectionChangedEventArgs e) => SaveConfigAv1an();
        private void Av1anOption_ValueChanged(object sender, NumericUpDownValueChangedEventArgs e) => SaveConfigAv1an();

        private void Av1anCrop_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Av1anCropConfBtn.IsVisible = Av1anCropBox.GetText().ToLower().Contains("manual");
        }

        private async void Av1anCropConf_Click(object sender, RoutedEventArgs e)
        {
            Size res = new Size();

            if (TrackList.current != null && TrackList.current.File.VideoStreams.Count > 0)
                res = TrackList.current.File.VideoStreams[0].Resolution;

            CropConfig crop = await CropWindow.Show(res, Av1anUi.CurrentCrop);

            if (crop != null)
                Av1anUi.CurrentCrop = crop;
        }

        public void LoadConfigAv1an()
        {
            ConfigParser.LoadComboxIndex(Av1anContainerBox);
            ConfigParser.LoadComboxIndex(Av1anCodecBox);
            ConfigParser.LoadComboxIndex(Av1anAudCodecBox);
            ConfigParser.LoadComboxIndex(Av1anOptsChunkModeBox);
            ConfigParser.LoadComboxIndex(Av1anOptsConcatModeBox);
            ConfigParser.LoadComboxIndex(Av1anOptsChunkOrderBox);
            ConfigParser.LoadGuiElement(Av1anOptsWorkerCountUpDown, false);
            ConfigParser.LoadGuiElement(Av1anThreadsUpDown, false);
        }

        public void SaveConfigAv1an()
        {
            if (!_initialized)
                return;

            ConfigParser.SaveComboxIndex(Av1anContainerBox);
            ConfigParser.SaveComboxIndex(Av1anCodecBox);
            ConfigParser.SaveComboxIndex(Av1anAudCodecBox);
            ConfigParser.SaveComboxIndex(Av1anOptsChunkModeBox);
            ConfigParser.SaveComboxIndex(Av1anOptsConcatModeBox);
            ConfigParser.SaveComboxIndex(Av1anOptsChunkOrderBox);
            ConfigParser.SaveGuiElement(Av1anOptsWorkerCountUpDown, ConfigParser.StringMode.Int);
            ConfigParser.SaveGuiElement(Av1anThreadsUpDown, ConfigParser.StringMode.Int);
        }

        private void Av1anFilterAdd_Click(object sender, RoutedEventArgs e)
        {
            Av1anFilterRows.Add(new FilterRow(""));
        }

        private void Av1anFilterRemove_Click(object sender, RoutedEventArgs e)
        {
            if (Av1anAdvancedFiltersGrid.SelectedItem is FilterRow row)
                Av1anFilterRows.Remove(row);
            else if (Av1anFilterRows.Count > 0)
                Av1anFilterRows.RemoveAt(Av1anFilterRows.Count - 1);
        }

        private async void BrowseAv1anOutput_Click(object sender, RoutedEventArgs e)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Choose output path",
                SuggestedFileName = Path.GetFileName(Av1anOutputPathBox.Text ?? "")
            });

            string path = file?.TryGetLocalPath();

            if (!string.IsNullOrWhiteSpace(path))
                Av1anOutputPathBox.Text = Path.ChangeExtension(path, null);
        }

        private async void Av1anResume_Click(object sender, RoutedEventArgs e)
        {
            Av1anResumeWindow window = await Av1anResumeWindow.ShowAsync();

            if (window.ChosenEntry == null || !window.Resume)
                return;

            if (window.ChosenEntry.jsonInfo == null)
            {
                Logger.Log($"Cannot resume - Failed to load info from JSON.");
                return;
            }

            if (window.ChosenEntry.InputFile == null || !File.Exists(window.ChosenEntry.InputFile.FullName))
            {
                Logger.Log($"Cannot resume - Input file doesn't seem to exist at '{window.ChosenEntry.InputFile}'.");
                return;
            }

            if (window.UseSavedCommand)
                await Av1an.RunResumeWithSavedArgs(window.ChosenEntry.TempFolderName, window.ChosenEntry.Args);
            else
                await Av1an.RunResumeWithNewArgs(window.ChosenEntry.InputFile.FullName, window.ChosenEntry.TempFolderName);
        }
    }
}
