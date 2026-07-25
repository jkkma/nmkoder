using Avalonia.Controls;
using Avalonia.Interactivity;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.UI;

namespace Nmkoder.Views
{
    partial class MainWindow
    {
        private void LoadGeneralSettings()
        {
            CmdDebugModeBox.SelectedIndex = Config.GetInt(Config.Key.CmdDebugMode).Clamp(0, 2);
            AutoCropSamplesUpDown.SetValueClamped(Config.GetInt(Config.Key.AutoCropSamples, 10));
            KeyIntSecsUpDown.SetValueClamped(Config.GetInt(Config.Key.DefaultKeyIntSecs, 10));
            UseZeroIndexedStreamsBox.IsChecked = Config.GetBool(Config.Key.UseZeroIndexedStreams);
            Mp4FaststartBox.IsChecked = Config.GetBool(Config.Key.mp4Faststart, true);
            Av1anCmdVisibleBox.IsChecked = Config.GetBool(Config.Key.Av1anCmdVisible, true);
        }

        private void SaveGeneralSettings()
        {
            if (!_initialized)
                return;

            Config.Set(Config.Key.CmdDebugMode, CmdDebugModeBox.SelectedIndex.Clamp(0, 2).ToString());
            Config.Set(Config.Key.AutoCropSamples, AutoCropSamplesUpDown.Value.AsInt().ToString());
            Config.Set(Config.Key.DefaultKeyIntSecs, KeyIntSecsUpDown.Value.AsInt().ToString());
            Config.Set(Config.Key.UseZeroIndexedStreams, (UseZeroIndexedStreamsBox.IsChecked == true).ToString());
            Config.Set(Config.Key.mp4Faststart, (Mp4FaststartBox.IsChecked == true).ToString());
            Config.Set(Config.Key.Av1anCmdVisible, (Av1anCmdVisibleBox.IsChecked == true).ToString());
        }

        private void GeneralSetting_Changed(object sender, RoutedEventArgs e) => SaveGeneralSettings();
        private void GeneralSetting_ValueChanged(object sender, NumericUpDownValueChangedEventArgs e) => SaveGeneralSettings();
        private void CmdDebugMode_SelectionChanged(object sender, SelectionChangedEventArgs e) => SaveGeneralSettings();

        private void TaskMode_SelectionChanged(object sender, SelectionChangedEventArgs e) => SaveUiConfig();

        private async void ResetSettingsConf_Click(object sender, RoutedEventArgs e)
        {
            await ResetSettingsWindow.ShowAsync();
            UpdateResetSettingsText();
        }

        public void UpdateResetSettingsText()
        {
            SettingsToResetLabel.Text = ResetSettingsOnNewFile.GetString();
        }

        private void ResetSettingsResetAll_Click(object sender, RoutedEventArgs e)
        {
            TrackList.ResetSettings(true, true);
        }
    }
}
