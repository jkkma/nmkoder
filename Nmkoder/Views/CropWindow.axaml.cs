using Avalonia.Controls;
using Avalonia.Interactivity;
using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.UI;
using System.Threading.Tasks;

namespace Nmkoder.Views
{
    public partial class CropWindow : Window
    {
        /// <summary> The configured crop, or null when the dialog was dismissed without confirming. </summary>
        public CropConfig Crop { get; private set; }

        private Size _originalDimensions;
        private bool _ready;

        public CropWindow()
        {
            InitializeComponent();
        }

        public static async Task<CropConfig> Show(Size resolution, CropConfig savedCrop)
        {
            var window = new CropWindow();
            window.Load(resolution, savedCrop);

            Window owner = UiUtils.MainWindowHandle;

            if (owner != null && owner.IsVisible)
                await window.ShowDialog(owner);
            else
                window.Show();

            return window.Crop;
        }

        private void Load(Size resolution, CropConfig savedCrop)
        {
            _ready = false;
            _originalDimensions = resolution;
            NoVidPanel.IsVisible = resolution.IsEmpty;

            // With no video loaded we can't clamp against real dimensions, so allow anything sane.
            int maxWidth = resolution.IsEmpty ? 16384 : resolution.Width;
            int maxHeight = resolution.IsEmpty ? 16384 : resolution.Height;

            CropLeft.SetRange(0, maxWidth);
            CropRight.SetRange(0, maxWidth);
            CropTop.SetRange(0, maxHeight);
            CropBot.SetRange(0, maxHeight);

            CropLeft.Value = savedCrop?.CropLeft ?? 0;
            CropRight.Value = savedCrop?.CropRight ?? 0;
            CropTop.Value = savedCrop?.CropTop ?? 0;
            CropBot.Value = savedCrop?.CropBot ?? 0;

            _ready = true;
            UpdateResultLabel();
        }

        private void CropLeftRightChanged(object sender, NumericUpDownValueChangedEventArgs e) => UpdateResultLabel();
        private void CropTopBotChanged(object sender, NumericUpDownValueChangedEventArgs e) => UpdateResultLabel();

        private void UpdateResultLabel()
        {
            if (!_ready)
                return;

            if (_originalDimensions.IsEmpty)
            {
                ResultLabel.Text = $"Crop L/R/T/B: {CropLeft.Value.AsInt()}/{CropRight.Value.AsInt()}/{CropTop.Value.AsInt()}/{CropBot.Value.AsInt()}";
                return;
            }

            CropConfig crop = BuildCrop();
            int w = crop.GetCroppedWidth(_originalDimensions);
            int h = crop.GetCroppedHeight(_originalDimensions);
            ResultLabel.Text = $"{w}x{h} at X {crop.CropLeft}, Y {crop.CropTop} (source is {_originalDimensions.Width}x{_originalDimensions.Height})";
        }

        private CropConfig BuildCrop()
        {
            return new CropConfig(CropLeft.Value.AsInt(), CropRight.Value.AsInt(), CropTop.Value.AsInt(), CropBot.Value.AsInt());
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            _ready = false;
            CropLeft.Value = CropRight.Value = CropTop.Value = CropBot.Value = 0;
            _ready = true;
            UpdateResultLabel();
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            Crop = BuildCrop();
            Close();
        }
    }
}
