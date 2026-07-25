using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.UI;
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

        private void SelectReadBitrates(object sender, TappedEventArgs e) => SelectUtil(RunTask.TaskType.UtilReadBitrates);
        private void SelectConcat(object sender, TappedEventArgs e) => SelectUtil(RunTask.TaskType.UtilConcat);
        private void SelectBitratePlot(object sender, TappedEventArgs e) => SelectUtil(RunTask.TaskType.PlotBitrate);
        private void SelectOcr(object sender, TappedEventArgs e) => SelectUtil(RunTask.TaskType.UtilOcr);

        private async void SelectGetMetrics(object sender, TappedEventArgs e)
        {
            SelectUtil(RunTask.TaskType.UtilGetMetrics);
            await ShowMetricsConfig();
        }

        private void SelectColorData(object sender, TappedEventArgs e) => SelectUtil(RunTask.TaskType.UtilColorData);

        private void SelectUtil(RunTask.TaskType task)
        {
            _currentUtilTask = task;
            UpdatePanels();
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
                { UtilsConcatPanel, RunTask.TaskType.UtilConcat },
                { UtilsBitratePlotPanel, RunTask.TaskType.PlotBitrate },
                { UtilsOcrPanel, RunTask.TaskType.UtilOcr },
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
