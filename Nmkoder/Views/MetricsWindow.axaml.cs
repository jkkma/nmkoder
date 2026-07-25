using Avalonia.Controls;
using Avalonia.Interactivity;
using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.UI;
using Nmkoder.UI.Tasks;
using System.Linq;
using System.Threading.Tasks;

namespace Nmkoder.Views
{
    /// <summary>
    /// Configures a VMAF/SSIM/PSNR comparison. Writes straight back into <see cref="UtilGetMetrics"/>,
    /// which is what the WinForms version did via MainForm.SetMetricsVarsFromForm.
    /// </summary>
    public partial class MetricsWindow : Window
    {
        public MetricsWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Shows the dialog and applies the result. When <paramref name="silent"/> is set the dialog
        /// isn't displayed at all and the previously configured values are simply re-validated -
        /// used when a task runs without prompting.
        /// </summary>
        public static async Task ShowAsync(bool silent = false)
        {
            var window = new MetricsWindow();
            window.LoadValues();

            if (silent)
            {
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
            foreach (FileListEntryProxy proxy in FileList.Items.Select(x => new FileListEntryProxy(x.File)))
            {
                EncodedVideo.Items.Add(proxy);
                ReferenceVideo.Items.Add(proxy);
            }

            Subsample.SetItems(Enumerable.Range(1, 32).Select(i => (object)(i == 1 ? "1 (Every frame)" : $"{i} (Every {i}th frame)")));

            Vmaf.IsChecked = UtilGetMetrics.runVmaf;
            Ssim.IsChecked = UtilGetMetrics.runSsim;
            Psnr.IsChecked = UtilGetMetrics.runPsnr;
            Align.SelectedIndex = UtilGetMetrics.alignMode.Clamp(0, 3);
            Subsample.SelectedIndex = (UtilGetMetrics.subsample - 1).Clamp(0, 31);
            VmafMdl.SelectedIndex = UtilGetMetrics.vmafModel.Clamp(0, 2);

            SelectByPath(EncodedVideo, UtilGetMetrics.vidLq);
            SelectByPath(ReferenceVideo, UtilGetMetrics.vidHq);

            if (EncodedVideo.SelectedIndex < 0 || ReferenceVideo.SelectedIndex < 0)
            {
                // Default guess: the smallest file is the encode, the biggest is the reference.
                var bySize = EncodedVideo.Items.OfType<FileListEntryProxy>().OrderByDescending(x => x.File.Size).ToList();

                if (bySize.Count > 0)
                {
                    EncodedVideo.SelectedItem = bySize.Last();
                    ReferenceVideo.SelectedItem = bySize.First();
                }
            }
        }

        private static void SelectByPath(ComboBox box, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            for (int i = 0; i < box.ItemCount; i++)
            {
                if (box.Items[i] is FileListEntryProxy proxy && proxy.File.ImportPath == path)
                {
                    box.SelectedIndex = i;
                    return;
                }
            }
        }

        private void Apply()
        {
            UtilGetMetrics.subsample = Subsample.SelectedIndex + 1;
            UtilGetMetrics.alignMode = Align.SelectedIndex.Clamp(0, 3);
            UtilGetMetrics.vmafModel = VmafMdl.SelectedIndex.Clamp(0, 2);
            UtilGetMetrics.runVmaf = Vmaf.IsChecked == true;
            UtilGetMetrics.runSsim = Ssim.IsChecked == true;
            UtilGetMetrics.runPsnr = Psnr.IsChecked == true;

            if (EncodedVideo.SelectedItem is FileListEntryProxy lq)
                UtilGetMetrics.vidLq = lq.File.ImportPath;

            if (ReferenceVideo.SelectedItem is FileListEntryProxy hq)
                UtilGetMetrics.vidHq = hq.File.ImportPath;
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            Apply();
            Close();
        }

        /// <summary> Wraps a MediaFile so combo boxes show a friendly name. </summary>
        internal class FileListEntryProxy
        {
            public MediaFile File { get; }

            public FileListEntryProxy(MediaFile file) { File = file; }

            public override string ToString() => File.ToString();
        }
    }
}
