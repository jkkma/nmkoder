using Avalonia.Controls;
using Avalonia.Interactivity;
using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.UI;
using System;
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
            // A saved crop can be bigger than the file now loaded - the four edges outlive the file they
            // were set for - so the load clamps too, and the dialog opens showing a crop that fits.
            _reducedOnLoad = ShrinkToFit(CropLeft, CropRight, _originalDimensions.Width)
                | ShrinkToFit(CropTop, CropBot, _originalDimensions.Height);
            SetMaximums();
            UpdateResultLabel();
        }

        /// <summary> Whether the crop that came in had to be cut down to fit the file, which the readout
        /// says so nobody wonders why the numbers are not the ones they left. </summary>
        private bool _reducedOnLoad;

        /// <summary>
        /// Brings a pair of edges inside the frame *proportionally*, for the load - where neither edge
        /// has just been typed into and so neither has a claim to be the one kept. A 140/140 letterbox
        /// crop reopened against a frame too short for it stays symmetric, where taking the whole
        /// overflow off one side would silently turn it into 140/128 and move the picture.
        /// </summary>
        private bool ShrinkToFit(NumericUpDown a, NumericUpDown b, int total)
        {
            if (_originalDimensions.IsEmpty)
                return false;

            int limit = Math.Max(0, total - CropConfig.MinSide);
            int sum = a.Value.AsInt() + b.Value.AsInt();

            if (sum <= limit)
                return false;

            _ready = false;
            int keptA = (int)((long)a.Value.AsInt() * limit / sum);
            a.Value = keptA;
            b.Value = Math.Max(0, limit - keptA);
            _ready = true;
            return true;
        }

        private void SetMaximums()
        {
            if (_originalDimensions.IsEmpty)
                return;

            _ready = false;
            int maxW = Math.Max(0, _originalDimensions.Width - CropConfig.MinSide);
            int maxH = Math.Max(0, _originalDimensions.Height - CropConfig.MinSide);
            CropLeft.Maximum = Math.Max(0, maxW - CropRight.Value.AsInt());
            CropRight.Maximum = Math.Max(0, maxW - CropLeft.Value.AsInt());
            CropTop.Maximum = Math.Max(0, maxH - CropBot.Value.AsInt());
            CropBot.Maximum = Math.Max(0, maxH - CropTop.Value.AsInt());
            _ready = true;
        }

        private void CropLeftRightChanged(object sender, NumericUpDownValueChangedEventArgs e)
        {
            OppositeChanged(sender == CropLeft ? CropLeft : CropRight, sender == CropLeft ? CropRight : CropLeft, _originalDimensions.Width);
        }

        private void CropTopBotChanged(object sender, NumericUpDownValueChangedEventArgs e)
        {
            OppositeChanged(sender == CropTop ? CropTop : CropBot, sender == CropTop ? CropBot : CropTop, _originalDimensions.Height);
        }

        private void OppositeChanged(NumericUpDown changed, NumericUpDown other, int total)
        {
            if (!_ready)
                return;

            _reducedOnLoad = false; // Edited by hand now, so the note about the value that came in is spent
            ClampPair(changed, other, total);
            UpdateResultLabel();
        }

        /// <summary>
        /// Holds a pair of opposing edges to what the frame can actually give them.
        /// <para/>
        /// Each box used to be clamped against the *whole* dimension on its own, so Left 1000 and Right
        /// 1000 on a 1920 frame was something the dialog would let you confirm - and it came out as
        /// "crop=-80:1080:1000:0", which ffmpeg refuses. A pair is what has to be bounded, not an edge.
        /// <para/>
        /// The edge that just changed keeps what was typed into it and the opposite one gives way, which
        /// is the only order that lets someone type past the middle and carry on. Both the value and the
        /// maximum move: a Maximum lowered under a Value does not pull that Value down on its own, so
        /// the box would go on reading 1000 while the frame said otherwise.
        /// </summary>
        private void ClampPair(NumericUpDown changed, NumericUpDown other, int total)
        {
            if (_originalDimensions.IsEmpty)
                return;

            _ready = false;

            int limit = Math.Max(0, total - CropConfig.MinSide);
            int over = changed.Value.AsInt() + other.Value.AsInt() - limit;

            if (over > 0)
                other.Value = Math.Max(0, other.Value.AsInt() - over);

            _ready = true;
            SetMaximums();
        }

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
            int x = crop.GetX(_originalDimensions);
            int y = crop.GetY(_originalDimensions);
            // The rectangle as it will actually run, alignment included, rather than the four numbers
            // read back: an odd value is rounded to the chroma grid and saying so here is the only
            // chance to, since the encode gets the aligned one either way. Only asked once the crop
            // fits - a rectangle with nothing left in it has a size that came from the floor rather
            // than from any rounding, and calling that "rounded to even" would be an odd way to
            // describe it.
            string aligned = crop.FitsInside(_originalDimensions)
                && (x != crop.CropLeft || y != crop.CropTop
                    || w != _originalDimensions.Width - crop.CropLeft - crop.CropRight
                    || h != _originalDimensions.Height - crop.CropTop - crop.CropBot)
                ? " · rounded to even, which is the only size 4:2:0 video has" : "";

            string reduced = _reducedOnLoad ? " · reduced to fit this file, which is smaller than the one this crop was set for" : "";
            ResultLabel.Text = $"{w}x{h} at X {x}, Y {y} (source is {_originalDimensions.Width}x{_originalDimensions.Height}){aligned}{reduced}";
        }

        private CropConfig BuildCrop()
        {
            return new CropConfig(CropLeft.Value.AsInt(), CropRight.Value.AsInt(), CropTop.Value.AsInt(), CropBot.Value.AsInt());
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            _ready = false;
            CropLeft.Value = CropRight.Value = CropTop.Value = CropBot.Value = 0;
            _reducedOnLoad = false;
            _ready = true;
            // The maximums move with the values, or they stay where the cleared crop had pushed them:
            // 1000 off the left caps the right at 918, and resetting left that cap in place over a
            // frame with nothing cropped off it at all.
            SetMaximums();
            UpdateResultLabel();
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            Crop = BuildCrop();
            Close();
        }
    }
}
