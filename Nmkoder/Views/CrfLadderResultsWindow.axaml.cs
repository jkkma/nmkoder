using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Nmkoder.Data;
using Nmkoder.Data.Ui;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.UI;
using Nmkoder.UI.Tasks;
using Nmkoder.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nmkoder.Views
{
    /// <summary>
    /// What a finished ladder came to. A window rather than the log alone because this is a table, and
    /// a table read one line at a time between progress lines is not one - the log gets the same
    /// numbers, so nothing is lost by closing this.
    /// </summary>
    public partial class CrfLadderResultsWindow : Window
    {
        // The "highest CRF still worth using" line is drawn at CrfLadder.GoodScore(metric) - VMAF 95,
        // SSIMULACRA2 80, both the app's own target-quality anchors - and not at all for XPSNR, which
        // has no fixed ceiling. A rule of thumb rather than a threshold with anything behind it: it
        // exists because a column of scores is not an answer, and the whole point is to produce one.

        public CrfLadderResultsWindow()
        {
            InitializeComponent();
        }

        public static async Task ShowAsync(CrfLadder.Result result)
        {
            var window = new CrfLadderResultsWindow();
            window.Load(result);

            Window owner = UiUtils.MainWindowHandle;

            if (owner != null && owner.IsVisible)
                await window.ShowDialog(owner);
            else
                window.Show();
        }

        private CrfLadder.Result _result;

        private void Load(CrfLadder.Result result)
        {
            _result = result;

            long sourceBytes = result.SourceBytes;

            LadderGrid.ItemsSource = result.Rungs.Select(r => new CrfLadderRow(r, result, sourceBytes)).ToList();

            // By header rather than by x:Name: a DataGridColumn is not a Control, so a name on one in
            // the XAML generates no field to reach it by, and by index a column inserted beside it
            // would silently retitle the wrong one. Safe to key on the header the XAML sets because
            // every window is built fresh - ShowAsync constructs one per run.
            DataGridColumn score = Column("VMAF");
            DataGridColumn vsSource = Column("Of source");

            if (score != null)
            {
                score.Header = CrfLadder.MetricName(result.ScoredWith);
                score.IsVisible = result.ScoredWith != CrfLadder.Metric.None;
            }

            if (vsSource != null)
                vsSource.IsVisible = sourceBytes > 0;

            HeaderLabel.Text = $"CRF LADDER — {result.FileName.Trunc(60).ToUpperInvariant()}";
            SubtitleLabel.Text = $"{result.EncoderName}, preset {result.Preset}, {result.PixelFormat}" +
                $"{(result.ScoredWith == CrfLadder.Metric.Vmaf ? $", {result.VmafModel}" : "")} — " +
                $"{DescribeSamples(result)}";

            NoteLabel.Text = BuildNote(result);
        }

        private DataGridColumn Column(string header)
        {
            return LadderGrid.Columns.FirstOrDefault(c => (c.Header as string) == header);
        }

        private static string DescribeSamples(CrfLadder.Result result)
        {
            long sampled = result.Samples.Sum(x => x.Ms);
            string places = string.Join(", ", result.Samples.Select(x => FormatUtils.Time(x.StartMs)));

            return $"{result.Samples.Count} section{(result.Samples.Count == 1 ? "" : "s")} at {places}, " +
                $"{FormatUtils.Time(sampled)} in all ({(result.SampledFraction * 100).ToString("0.#")}% of " +
                $"{FormatUtils.Time(result.SourceMs)})";
        }

        /// <summary>
        /// The reading of the table: which rung to start from, and what the numbers under it cannot
        /// know. Both halves matter - a recommendation with no caveat is a promise this cannot keep,
        /// and a caveat with no recommendation leaves the user where they started.
        /// </summary>
        private static string BuildNote(CrfLadder.Result result)
        {
            var lines = new List<string>();
            double good = CrfLadder.GoodScore(result.ScoredWith);

            // A recommendation is drawn only where the metric has an anchor worth naming - VMAF and
            // SSIMULACRA2 do (95 and 80), XPSNR does not - so GoodScore returns 0 for the rest and the
            // table stands on its own there.
            if (good > 0)
            {
                string metric = CrfLadder.MetricName(result.ScoredWith);
                // The highest CRF - the smallest file - that still scores well, which is the way this
                // is read: quality is being spent down to the point where it stops being worth it.
                CrfLadder.Rung pick = result.Rungs.Where(r => r.Scored && r.Score >= good).OrderByDescending(r => r.Crf).FirstOrDefault();

                if (pick != null)
                    lines.Add($"CRF {pick.Crf} is the highest here that still scores above {metric} {good:0.#} — about " +
                        $"{FormatUtils.Bytes(pick.ProjectedBytes(result.SourceMs))} for the whole file. That threshold is a " +
                        $"rule of thumb for \"hard to tell from the source\", not a measurement of your eyes: look at the " +
                        $"samples before committing to a long encode.");
                else if (result.Rungs.Any(r => r.Scored))
                    lines.Add($"Nothing here reached {metric} {good:0.#}. Run it again with lower CRF values — " +
                        $"the box takes any list, and lower is better quality on every encoder offered.");
            }

            lines.Add($"The whole-file figures are the sampled bitrate carried across the source, and video only — no " +
                $"audio and no subtitles. What they cannot know is the rest of the film: this measured " +
                $"{(result.SampledFraction * 100).ToString("0.#")}% of it, so a stretch that is quieter or busier than " +
                $"what was sampled moves the answer much further than any rounding here does.");

            lines.Add("A CRF belongs to the encoder and preset that produced it. The AV1AN tab drives separate binaries, " +
                "so treat a number from here as a starting point there rather than as the same setting.");

            return string.Join("\n\n", lines);
        }

        /// <summary> The table as text, for pasting into notes beside the file it is about. </summary>
        private string BuildText()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"CRF ladder - {_result.FileName}");
            sb.AppendLine($"{_result.EncoderName}, preset {_result.Preset}, {_result.PixelFormat}");
            sb.AppendLine(DescribeSamples(_result));
            sb.AppendLine();

            string metric = _result.ScoredWith == CrfLadder.Metric.None ? "" : CrfLadder.MetricName(_result.ScoredWith);

            sb.AppendLine($"CRF\tBitrate\tPer minute\tWhole file{(metric.IsEmpty() ? "" : $"\t{metric}")}");

            foreach (CrfLadder.Rung rung in _result.Rungs)
            {
                string score = metric.IsEmpty() ? "" : $"\t{(rung.Scored ? UtilCrfLadder.FormatScore(rung.Score) : "-")}";
                sb.AppendLine($"{rung.Crf}\t{FormatUtils.Bitrate(rung.Kbps)}\t{FormatUtils.Bytes(rung.BytesPerMinute)}\t" +
                    $"{CrfLadder.DescribeProjection(rung.ProjectedBytes(_result.SourceMs))}{score}");
            }

            return sb.ToString();
        }

        private async void Copy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                IClipboard clipboard = TopLevel.GetTopLevel(this)?.Clipboard;

                if (clipboard == null)
                {
                    Logger.LogWarn("Could not reach the clipboard.");
                    return;
                }

                await clipboard.SetTextAsync(BuildText());
                Logger.Log("Copied the CRF ladder to the clipboard.");
            }
            catch (Exception ex)
            {
                Logger.LogErr($"Could not copy the results: {ex.Message}");
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
