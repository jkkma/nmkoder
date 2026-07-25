using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Nmkoder.UI;
using System.Threading.Tasks;

namespace Nmkoder.Views
{
    /// <summary> Replacement for WinForms' MessageBox, styled like the rest of the app. </summary>
    public partial class MessageWindow : Window
    {
        private UiUtils.MessageButtons _btns = UiUtils.MessageButtons.Ok;
        private UiUtils.DialogResult _result = UiUtils.DialogResult.None;

        public MessageWindow()
        {
            InitializeComponent();
            SetupUi();
        }

        private void SetupUi()
        {
            KeyDown += OnKeyDown;
        }

        public static async Task<UiUtils.DialogResult> Show(string text, string title, UiUtils.MessageButtons btns = UiUtils.MessageButtons.Ok)
        {
            var window = new MessageWindow();
            window.Title = title;
            window.TextLabel.Text = text;
            window.SetButtons(btns);

            Window owner = UiUtils.MainWindowHandle;

            if (owner != null && owner.IsVisible)
                await window.ShowDialog(owner);
            else
                window.Show();

            return window._result;
        }

        private void SetButtons(UiUtils.MessageButtons btns)
        {
            _btns = btns;

            // Buttons are laid out right-to-left, so Btn1 is always the rightmost/default one.
            switch (btns)
            {
                case UiUtils.MessageButtons.Ok:
                    Btn1.Content = "OK";
                    Btn2.IsVisible = Btn3.IsVisible = false;
                    break;
                case UiUtils.MessageButtons.YesNo:
                    Btn1.Content = "Yes";
                    Btn2.Content = "No";
                    Btn3.IsVisible = false;
                    break;
                case UiUtils.MessageButtons.YesNoCancel:
                    Btn1.Content = "Yes";
                    Btn2.Content = "No";
                    Btn3.Content = "Cancel";
                    break;
            }
        }

        private void Btn1_Click(object sender, RoutedEventArgs e)
        {
            Done(_btns == UiUtils.MessageButtons.Ok ? UiUtils.DialogResult.Ok : UiUtils.DialogResult.Yes);
        }

        private void Btn2_Click(object sender, RoutedEventArgs e)
        {
            Done(UiUtils.DialogResult.No);
        }

        private void Btn3_Click(object sender, RoutedEventArgs e)
        {
            Done(UiUtils.DialogResult.Cancel);
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
                Done(_btns == UiUtils.MessageButtons.Ok ? UiUtils.DialogResult.Ok : UiUtils.DialogResult.Cancel);
        }

        private void Done(UiUtils.DialogResult result)
        {
            _result = result;
            Close();
        }
    }
}
