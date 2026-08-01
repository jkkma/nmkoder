using Avalonia.Controls;
using Avalonia.Interactivity;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.UI;
using System.IO;
using System.Linq;

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
            // Left visible rather than hidden off-Windows: the setting is saved either way and a
            // session's settings travel, so a box that vanished would silently keep its old value.
            Av1anCmdVisibleBox.IsEnabled = OS.Shell.IsWindows;
            Av1anCmdVisibleBox.Content = OS.Shell.IsWindows
                ? "Show the av1an console window"
                : "Show the av1an console window (Windows only - av1an's output goes to the log here)";
            DefaultOutDirBox.Text = Config.Get(Config.Key.DefaultOutputDir, "");
        }

        private void SaveGeneralSettings()
        {
            if (!_initialized)
                return;

            using (Config.Batch())
            {
                Config.Set(Config.Key.CmdDebugMode, CmdDebugModeBox.SelectedIndex.Clamp(0, 2).ToString());
                Config.Set(Config.Key.AutoCropSamples, AutoCropSamplesUpDown.Value.AsInt().ToString());
                Config.Set(Config.Key.DefaultKeyIntSecs, KeyIntSecsUpDown.Value.AsInt().ToString());
                Config.Set(Config.Key.UseZeroIndexedStreams, (UseZeroIndexedStreamsBox.IsChecked == true).ToString());
                Config.Set(Config.Key.mp4Faststart, (Mp4FaststartBox.IsChecked == true).ToString());
                Config.Set(Config.Key.Av1anCmdVisible, (Av1anCmdVisibleBox.IsChecked == true).ToString());
                Config.Set(Config.Key.DefaultOutputDir, (DefaultOutDirBox.Text ?? "").Trim().Trim('"'));
            }
        }

        /// <summary>
        /// Saves the default output folder, and says so if it is not there - a path typed with a typo,
        /// or on a drive that is not plugged in, otherwise looks accepted and quietly does nothing.
        /// </summary>
        private void DefaultOutDir_Changed(object sender, RoutedEventArgs e)
        {
            string dir = (DefaultOutDirBox.Text ?? "").Trim().Trim('"');
            DefaultOutDirBox.Text = dir;
            SaveGeneralSettings();

            if (dir.IsNotEmpty() && !Directory.Exists(dir))
                Logger.Log($"Note: '{dir}' does not exist, so encodes will be saved next to their source file until it does.");
        }

        private async void BrowseDefaultOutDir_Click(object sender, RoutedEventArgs e)
        {
            string[] folders = await Pickers.PickFolders(this, "Choose where to save encodes", allowMultiple: false, Pickers.Dir.Output, DefaultOutDirBox.Text);
            string path = folders.FirstOrDefault();

            if (path.IsEmpty())
                return;

            DefaultOutDirBox.Text = path;
            SaveGeneralSettings();
        }

        private void ClearDefaultOutDir_Click(object sender, RoutedEventArgs e)
        {
            DefaultOutDirBox.Text = "";
            SaveGeneralSettings();
        }

        private void GeneralSetting_Changed(object sender, RoutedEventArgs e) => SaveGeneralSettings();
        private void GeneralSetting_ValueChanged(object sender, NumericUpDownValueChangedEventArgs e) => SaveGeneralSettings();
        private void CmdDebugMode_SelectionChanged(object sender, SelectionChangedEventArgs e) => SaveGeneralSettings();


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
