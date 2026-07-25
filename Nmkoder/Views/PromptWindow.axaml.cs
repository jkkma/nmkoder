using Avalonia.Controls;
using Avalonia.Interactivity;
using Nmkoder.UI;
using System.Threading.Tasks;

namespace Nmkoder.Views
{
    /// <summary> Single-line text prompt (used e.g. to ask for an image sequence's frame rate). </summary>
    public partial class PromptWindow : Window
    {
        public string EnteredText { get; private set; }

        public PromptWindow()
        {
            InitializeComponent();
        }

        public static async Task<string> Show(string title, string message, string defaultText)
        {
            var window = new PromptWindow();
            window.Title = title;
            window.MsgLabel.Text = message;
            window.InputBox.Text = defaultText;
            window.EnteredText = defaultText;

            Window owner = UiUtils.MainWindowHandle;

            if (owner != null && owner.IsVisible)
                await window.ShowDialog(owner);
            else
                window.Show();

            return window.EnteredText;
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            EnteredText = (InputBox.Text ?? "").Trim();
            Close();
        }
    }
}
