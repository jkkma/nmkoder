using Avalonia.Controls;
using Avalonia.Interactivity;
using Nmkoder.Data.Ui;
using Nmkoder.UI;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nmkoder.Views
{
    /// <summary>
    /// The long-form help for one encoder argument, opened by right-clicking its row. The grid can
    /// only show a single clipped line, which is enough to say what a parameter is but not what it
    /// does to a picture or what to set it to - that lives here.
    /// </summary>
    public partial class EncoderArgInfoWindow : Window
    {
        /// <summary> One example setting: the value, and what choosing it gets you. </summary>
        public class ExampleEntry
        {
            public string Value { get; set; }
            public string Text { get; set; }
        }

        public EncoderArgInfoWindow()
        {
            InitializeComponent();
        }

        /// <param name="spelling">How the argument is written on the command line the encoder is
        /// given. Passed in rather than worked out here: an AV1AN argument is a standalone binary's
        /// "--flag", where Quick Convert's is an ffmpeg AVOption or an entry in one of its parameter
        /// lists, and this window is where a person goes to find out which.</param>
        public static async Task Show(EncoderArgRow row, string spelling = null)
        {
            if (row == null)
                return;

            var window = new EncoderArgInfoWindow();
            window.Load(row, spelling);

            Window owner = UiUtils.MainWindowHandle;

            if (owner != null && owner.IsVisible)
                await window.ShowDialog(owner);
            else
                window.Show();
        }

        private void Load(EncoderArgRow row, string spelling)
        {
            ArgNameLabel.Text = string.IsNullOrWhiteSpace(spelling) ? $"--{(row.Argument ?? "").TrimStart('-')}" : spelling;
            SummaryLabel.Text = row.Description ?? "";

            // Nothing has been written for every argument of every encoder, and an empty window
            // would read as a bug rather than as a gap. The one-liner is still worth repeating.
            DetailsLabel.Text = string.IsNullOrWhiteSpace(row.Details)
                ? "No extended description has been written for this argument yet. The line above is what the encoder's own documentation says about it."
                : row.Details.Replace("\\n", "\n");

            List<ExampleEntry> examples = ParseExamples(row.Examples);
            ExamplesHeader.IsVisible = examples.Count > 0;
            ExamplesList.ItemsSource = examples;
        }

        /// <summary> Splits the stored "value|explanation" lines into entries, skipping malformed ones. </summary>
        public static List<ExampleEntry> ParseExamples(string examples)
        {
            var list = new List<ExampleEntry>();

            if (string.IsNullOrWhiteSpace(examples))
                return list;

            foreach (string line in examples.Replace("\\n", "\n").Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                string[] parts = line.Split('|', 2);
                list.Add(new ExampleEntry
                {
                    Value = parts[0].Trim(),
                    Text = parts.Length > 1 ? parts[1].Trim() : "",
                });
            }

            return list;
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
