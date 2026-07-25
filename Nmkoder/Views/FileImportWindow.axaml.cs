using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Nmkoder.Data;
using Nmkoder.Data.Ui;
using Nmkoder.UI;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Nmkoder.Views
{
    /// <summary> Shown when files are dropped while the list already holds something. </summary>
    public partial class FileImportWindow : Window
    {
        /// <summary> Files the user confirmed for import. </summary>
        public List<string> ImportFiles { get; } = new List<string>();

        /// <summary> Whether the existing file list should be replaced instead of appended to. </summary>
        public bool Clear { get; private set; }

        private readonly ObservableCollection<FileListEntry> _entries = new ObservableCollection<FileListEntry>();

        public FileImportWindow()
        {
            InitializeComponent();
            SetupUi();
        }

        private void SetupUi()
        {
            FilesBox.ItemsSource = _entries;
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
        }

        public static async Task<FileImportWindow> Show(string[] files, bool allowClear)
        {
            var window = new FileImportWindow();
            window.Title = $"Import {files.Length} File{(files.Length != 1 ? "s" : "")}";
            window.ImportClearBtn.IsVisible = allowClear;

            if (!allowClear)
                window.ImportAppendBtn.Content = "Import";

            foreach (string file in files)
                window._entries.Add(new FileListEntry(new MediaFile(file)));

            Window owner = UiUtils.MainWindowHandle;

            if (owner != null && owner.IsVisible)
                await window.ShowDialog(owner);
            else
                window.Show();

            return window;
        }

        private void ImportClear_Click(object sender, RoutedEventArgs e)
        {
            Clear = true;
            Done();
        }

        private void ImportAppend_Click(object sender, RoutedEventArgs e)
        {
            Clear = false;
            Done();
        }

        private void Done()
        {
            ImportFiles.Clear();
            ImportFiles.AddRange(_entries.Where(x => x.IsChecked).Select(x => x.File.SourcePath));
            Close();
        }
    }
}
