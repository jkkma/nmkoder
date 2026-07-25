using Avalonia.Controls;
using Avalonia.Interactivity;
using Nmkoder.Data;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.UI;
using Nmkoder.UI.Tasks;
using System.Linq;
using System.Threading.Tasks;

namespace Nmkoder.Views
{
    /// <summary> Picks source/target files for the color metadata transfer utility. </summary>
    public partial class ColorDataWindow : Window
    {
        private bool _batchMode;

        public ColorDataWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Shows the dialog and applies the result to <see cref="UtilColorData"/>. When
        /// <paramref name="silent"/> is set nothing is displayed and existing values are reused.
        /// </summary>
        public static async Task ShowAsync(bool silent = false)
        {
            var window = new ColorDataWindow();
            window.LoadValues();

            if (silent || window._batchMode)
            {
                if (window._batchMode && !silent)
                    Logger.Log($"In batch processing mode, this util can only be used to read the metadata! Use the Muxing Mode for transferring.");

                window.Apply();
                return;
            }

            Window owner = UiUtils.MainWindowHandle;

            if (owner != null && owner.IsVisible)
                await window.ShowDialog(owner);
            else
                window.Show();
        }

        private void LoadValues()
        {
            _batchMode = RunTask.currentFileListMode == RunTask.FileListMode.Batch;
            BatchNote.IsVisible = _batchMode;

            if (_batchMode)
            {
                if (TrackList.current != null)
                    SourceVideo.Items.Add(new FileProxy(TrackList.current.File));
            }
            else
            {
                foreach (var entry in FileList.Items)
                {
                    SourceVideo.Items.Add(new FileProxy(entry.File));
                    TargetVideo.Items.Add(new FileProxy(entry.File));
                }
            }

            CopyColorSpace.IsChecked = UtilColorData.copyColorSpace;
            CopyHdrData.IsChecked = UtilColorData.copyHdrData;

            SelectByPath(SourceVideo, UtilColorData.vidSrc);

            if (!_batchMode)
                SelectByPath(TargetVideo, UtilColorData.vidTarget);

            if (SourceVideo.SelectedIndex < 0 && SourceVideo.ItemCount > 0)
            {
                // Default guess: the biggest file is the source, the smallest is the target.
                var bySize = SourceVideo.Items.OfType<FileProxy>().OrderByDescending(x => x.File.Size).ToList();
                SourceVideo.SelectedItem = bySize.First();

                if (!_batchMode && TargetVideo.ItemCount > 1)
                    TargetVideo.SelectedItem = TargetVideo.Items.OfType<FileProxy>().OrderByDescending(x => x.File.Size).Last();
            }
        }

        private static void SelectByPath(ComboBox box, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            for (int i = 0; i < box.ItemCount; i++)
            {
                if (box.Items[i] is FileProxy proxy && proxy.File.ImportPath == path)
                {
                    box.SelectedIndex = i;
                    return;
                }
            }
        }

        private void Apply()
        {
            UtilColorData.copyColorSpace = CopyColorSpace.IsChecked == true;
            UtilColorData.copyHdrData = CopyHdrData.IsChecked == true;

            if (SourceVideo.SelectedItem is FileProxy src)
                UtilColorData.vidSrc = src.File.ImportPath;

            UtilColorData.vidTarget = TargetVideo.SelectedItem is FileProxy target ? target.File.ImportPath : null;
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            Apply();
            Close();
        }

        internal class FileProxy
        {
            public MediaFile File { get; }

            public FileProxy(MediaFile file) { File = file; }

            public override string ToString() => File.ToString();
        }
    }
}
