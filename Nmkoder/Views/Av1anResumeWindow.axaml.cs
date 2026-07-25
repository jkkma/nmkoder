using Avalonia.Controls;
using Avalonia.Interactivity;
using Nmkoder.Data;
using Nmkoder.Data.Ui;
using Nmkoder.IO;
using Nmkoder.UI;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Nmkoder.Views
{
    public partial class Av1anResumeWindow : Window
    {
        public Av1anFolderEntry ChosenEntry { get; private set; }
        public bool Resume { get; private set; }
        public bool UseSavedCommand { get; private set; }

        private readonly ObservableCollection<Av1anFolderEntry> _entries = new ObservableCollection<Av1anFolderEntry>();

        public Av1anResumeWindow()
        {
            InitializeComponent();
            SetupUi();
        }

        private void SetupUi()
        {
            FolderList.ItemsSource = _entries;
        }

        public static async Task<Av1anResumeWindow> ShowAsync()
        {
            var window = new Av1anResumeWindow();
            window.ReloadList();

            Window owner = UiUtils.MainWindowHandle;

            if (owner != null && owner.IsVisible)
                await window.ShowDialog(owner);
            else
                window.Show();

            return window;
        }

        private void ReloadList()
        {
            _entries.Clear();
            string av1anDir = Paths.GetAv1anTempPath();

            var found = new DirectoryInfo(av1anDir).GetDirectories()
                .Select(x => new Av1anFolderEntry(x.FullName))
                .OrderBy(x => x.TimeSinceLastRun.TotalMilliseconds);

            foreach (Av1anFolderEntry entry in found)
                _entries.Add(entry);

            FolderList.SelectedIndex = _entries.Count > 0 ? 0 : -1;
        }

        private void Done()
        {
            ChosenEntry = FolderList.SelectedItem as Av1anFolderEntry;
            Close();
        }

        private void ResumeSaved_Click(object sender, RoutedEventArgs e)
        {
            Resume = true;
            UseSavedCommand = true;
            Done();
        }

        private void ResumeNew_Click(object sender, RoutedEventArgs e)
        {
            Resume = true;
            UseSavedCommand = false;
            Done();
        }

        private void DeleteFull_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (FolderList.SelectedItem is not Av1anFolderEntry entry)
                    return;

                IoUtils.DeleteIfExists(entry.DirInfo.FullName);
                IoUtils.DeleteIfExists(entry.DirInfo.FullName + ".json");
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to delete av1an folder: {ex.Message}");
            }

            ReloadList();
        }

        private void DeleteChunks_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (FolderList.SelectedItem is not Av1anFolderEntry entry)
                    return;

                IoUtils.DeleteContentsOfDir(Path.Combine(entry.DirInfo.FullName, "encode"));

                string doneJsonPath = Path.Combine(entry.DirInfo.FullName, "done.json");

                if (File.Exists(doneJsonPath))
                {
                    string doneJsonContent = File.ReadAllText(doneJsonPath);

                    try
                    {
                        string before = doneJsonContent.Split("\"done\":{")[0] + "\"done\":{";
                        string after = "},\"audio_done\"" + doneJsonContent.Split("},\"audio_done\"")[1];
                        File.WriteAllText(doneJsonPath, before + after);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Warning: Failed to reset done.json progress file: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to delete av1an encode folder: {ex.Message}");
            }

            ReloadList();
        }
    }
}
