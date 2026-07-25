using Avalonia.Controls;
using Avalonia.Interactivity;
using Nmkoder.UI;
using System.Threading.Tasks;

namespace Nmkoder.Views
{
    /// <summary> Lets the user review/edit the generated ffmpeg or av1an command (hold Shift when running). </summary>
    public partial class EditCommandWindow : Window
    {
        public string Args { get; private set; }

        public EditCommandWindow()
        {
            InitializeComponent();
        }

        public static async Task<string> Show(string exeName, string command)
        {
            var window = new EditCommandWindow();
            window.Title = $"Edit {exeName} Arguments";
            window.ArgsBox.Text = command;

            Window owner = UiUtils.MainWindowHandle;

            if (owner != null && owner.IsVisible)
                await window.ShowDialog(owner);
            else
                window.Show();

            return window.Args;
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            Args = (ArgsBox.Text ?? "").Trim();
            Close();
        }
    }
}
