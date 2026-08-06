using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.UI;
using Nmkoder.UI.Tasks;
using System.Collections.Generic;

namespace Nmkoder.Views
{
    partial class MainWindow
    {
        private RunTask.TaskType _currentUtilTask = RunTask.TaskType.None;

        public RunTask.TaskType GetUtilsTaskType()
        {
            return _currentUtilTask;
        }

        private void SelectReadBitrates(object sender, TappedEventArgs e) => SelectUtilCard(e, RunTask.TaskType.UtilReadBitrates);
        private void SelectConcat(object sender, TappedEventArgs e) => SelectUtilCard(e, RunTask.TaskType.UtilConcat);
        private void SelectBitratePlot(object sender, TappedEventArgs e) => SelectUtilCard(e, RunTask.TaskType.PlotBitrate);
        private void SelectOcr(object sender, TappedEventArgs e) => SelectUtilCard(e, RunTask.TaskType.UtilOcr);
        private void SelectDeinterlace(object sender, TappedEventArgs e) => SelectUtilCard(e, RunTask.TaskType.UtilDeinterlace);
        private void SelectFilmGrain(object sender, TappedEventArgs e) => SelectUtilCard(e, RunTask.TaskType.UtilFilmGrain);

        private async void UtilsDeinterlaceConf_Click(object sender, RoutedEventArgs e)
        {
            SelectUtil(RunTask.TaskType.UtilDeinterlace);

            // No file check: unlike Cut or Metrics, nothing here is measured against the loaded file -
            // the setting is the mode and the preset, which are just as configurable with an empty
            // list. The dialog says as much where the source line would otherwise be.
            await DeinterlaceWindow.ShowAsync();
            UpdateDeinterlaceBtnText();
        }

        private async void UtilsFilmGrainConf_Click(object sender, RoutedEventArgs e)
        {
            SelectUtil(RunTask.TaskType.UtilFilmGrain);

            // No file check, as the Deinterlace dialog does it: the operation and its grain source are
            // just as configurable with an empty list, and the dialog says so where the source line goes.
            await FilmGrainWindow.ShowAsync();
            UpdateFilmGrainBtnText();
        }

        /// <summary> The Film Grain utility's button doubles as the readout of what is configured. </summary>
        public void UpdateFilmGrainBtnText()
        {
            UtilsFilmGrainConfBtn.Content = UtilFilmGrain.DescribeSettings();
        }

        /// <summary> The Deinterlace utility's button doubles as the readout of what is configured, as
        /// the Cut utility's does. </summary>
        public void UpdateDeinterlaceBtnText()
        {
            UtilsDeinterlaceConfBtn.Content = UtilDeinterlace.DescribeSettings();
        }

        private async void SelectGetMetrics(object sender, TappedEventArgs e)
        {
            if (TapWasTheCardsButton(e))
                return;

            SelectUtil(RunTask.TaskType.UtilGetMetrics);
            await ShowMetricsConfig();
        }

        private void SelectColorData(object sender, TappedEventArgs e) => SelectUtilCard(e, RunTask.TaskType.UtilColorData);

        /// <summary> Selects the utility a card stands for, when the card itself was tapped. </summary>
        private void SelectUtilCard(TappedEventArgs e, RunTask.TaskType task)
        {
            if (TapWasTheCardsButton(e))
                return;

            SelectUtil(task);
        }

        /// <summary>
        /// Whether a card's Tapped came from the Configure button sitting inside it. Avalonia
        /// recognises the tap gesture from the pointer release whether or not the button handled it,
        /// so a click on Configure raises the button's Click and then the card's Tapped, and both
        /// handlers used to do the same work. On the two cards whose Configure opens a dialog that
        /// meant two dialogs on top of each other: the user filled in the one in front, dismissed
        /// the one behind, and the dismissal - being the last to return - wrote its own untouched
        /// value over what they had just configured. The button selects the card's utility itself,
        /// so there is nothing here the click has not already done.
        /// </summary>
        private static bool TapWasTheCardsButton(TappedEventArgs e)
        {
            return (e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) != null;
        }

        private void SelectUtil(RunTask.TaskType task)
        {
            _currentUtilTask = task;
            UpdatePanels();
            UpdateRunButtonState(); // Which utility is picked is what Run reads, and what it now says
        }

        private async void UtilsMetricsConf_Click(object sender, RoutedEventArgs e)
        {
            SelectUtil(RunTask.TaskType.UtilGetMetrics);
            await ShowMetricsConfig();
        }

        private async System.Threading.Tasks.Task ShowMetricsConfig()
        {
            if (FileList.Items.Count < 2)
            {
                Logger.Log($"You need to load at least 2 files into the file list to use this utility!");
                return;
            }

            await MetricsWindow.ShowAsync();
        }

        private async void SelectCut(object sender, TappedEventArgs e)
        {
            if (TapWasTheCardsButton(e))
                return;

            SelectUtil(RunTask.TaskType.UtilCut);

            if (UtilCut.Cut == null) // Nothing to run yet, so go straight to picking the section
                await ShowCutConfig();
        }

        private async void UtilsCutConf_Click(object sender, RoutedEventArgs e)
        {
            SelectUtil(RunTask.TaskType.UtilCut);
            await ShowCutConfig();
        }

        private async System.Threading.Tasks.Task ShowCutConfig()
        {
            if (TrackList.current == null)
            {
                Logger.Log($"You need to load a file into the file list to use this utility!");
                return;
            }

            UtilCut.Cut = await CutWindow.ShowForCut(TrackList.current.File, UtilCut.Cut);
            UpdateCutBtnText();
        }

        /// <summary> The Cut utility's button doubles as the readout of what is configured. </summary>
        public void UpdateCutBtnText()
        {
            UtilsCutConfBtn.Content = UtilCut.Cut == null ? "Configure…" : UtilCut.Cut.ToString();
        }

        private async void UtilsColorDataConf_Click(object sender, RoutedEventArgs e)
        {
            SelectUtil(RunTask.TaskType.UtilColorData);

            if (FileList.Items.Count < 1)
            {
                Logger.Log($"You need to load at least one file into the file list to use this utility!");
                return;
            }

            await ColorDataWindow.ShowAsync();
        }

        /// <summary> Highlights the selected utility card. </summary>
        private void UpdatePanels()
        {
            var panels = new Dictionary<Border, RunTask.TaskType>
            {
                { UtilsBitratesPanel, RunTask.TaskType.UtilReadBitrates },
                { UtilsMetricsPanel, RunTask.TaskType.UtilGetMetrics },
                { UtilsColorDataPanel, RunTask.TaskType.UtilColorData },
                { UtilsCutPanel, RunTask.TaskType.UtilCut },
                { UtilsConcatPanel, RunTask.TaskType.UtilConcat },
                { UtilsBitratePlotPanel, RunTask.TaskType.PlotBitrate },
                { UtilsOcrPanel, RunTask.TaskType.UtilOcr },
                { UtilsDeinterlacePanel, RunTask.TaskType.UtilDeinterlace },
                { UtilsFilmGrainPanel, RunTask.TaskType.UtilFilmGrain },
            };

            foreach (var pair in panels)
            {
                if (pair.Value == _currentUtilTask)
                    pair.Key.Classes.Add("selected");
                else
                    pair.Key.Classes.Remove("selected");
            }
        }
    }
}
