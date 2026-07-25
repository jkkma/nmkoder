using Avalonia.Controls;
using Avalonia.Interactivity;
using Nmkoder.Data.Ui;
using Nmkoder.UI;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Nmkoder.Views
{
    public partial class ResetSettingsWindow : Window
    {
        private readonly ObservableCollection<ToggleRow> _rows = new ObservableCollection<ToggleRow>();

        public ResetSettingsWindow()
        {
            InitializeComponent();
            SetupUi();
        }

        private void SetupUi()
        {
            SettingsList.ItemsSource = _rows;
            Populate();
        }

        public static async Task ShowAsync()
        {
            var window = new ResetSettingsWindow();
            Window owner = UiUtils.MainWindowHandle;

            if (owner != null && owner.IsVisible)
                await window.ShowDialog(owner);
            else
                window.Show();
        }

        private void Populate()
        {
            foreach (var prop in typeof(ResetSettingsOnNewFile).GetProperties())
            {
                if (!prop.Name.StartsWith("Reset"))
                    continue;

                _rows.Add(new ToggleRow(ResetSettingsOnNewFile.NiceNames[prop.Name], prop.Name, (bool)prop.GetValue(null, null)));
            }
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            foreach (ToggleRow row in _rows)
                typeof(ResetSettingsOnNewFile).GetProperty(row.PropertyName).SetValue(null, row.IsChecked);

            ResetSettingsOnNewFile.Save();
            Close();
        }
    }
}
