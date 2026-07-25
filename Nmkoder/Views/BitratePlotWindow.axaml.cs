using Avalonia.Controls;
using Nmkoder.UI;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nmkoder.Views
{
    public partial class BitratePlotWindow : Window
    {
        public BitratePlotWindow()
        {
            InitializeComponent();
            SetupUi();
        }

        private void SetupUi()
        {
            Chart.SelectionChanged += (s, info) => InfoLabel.Text = string.IsNullOrEmpty(info) ? "Drag to select a range. Double click to reset." : info;
        }

        public static async Task Show(Dictionary<long, long> bytesPerSecond)
        {
            var window = new BitratePlotWindow();
            window.Chart.SetData(bytesPerSecond);

            Window owner = UiUtils.MainWindowHandle;

            if (owner != null && owner.IsVisible)
                await window.ShowDialog(owner);
            else
                window.Show();
        }
    }
}
