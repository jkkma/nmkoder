using Nmkoder.Data;
using Nmkoder.Data.Ui;
using Nmkoder.IO;
using Nmkoder.Media;
using Nmkoder.OS;
using Nmkoder.UI;
using Nmkoder.UI.Tasks;
using Nmkoder.Utils;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Nmkoder.Main
{
    public class RunTask
    {
        public enum TaskType { Null, None, Convert, Av1an, UtilReadBitrates, UtilGetMetrics, UtilOcr, UtilColorData, UtilConcat, PlotBitrate };

        public enum FileListMode { Mux, Batch };
        public static FileListMode currentFileListMode;

        public static bool runningBatch = false;
        public static bool canceled = false;

        public static void Cancel(string reason = "", bool noMsgBox = false)
        {
            canceled = true;
            Program.MainWin.SetStatus("Canceled.");
            Program.MainWin.SetProgress(0);

            ProcessManager.KillPrimary();
            ProcessManager.KillSecondary();

            Program.MainWin.SetWorking(false);
            Logger.LogIfLastLineDoesNotContainMsg("Canceled.");

            if (!string.IsNullOrWhiteSpace(reason) && !noMsgBox)
                UiUtils.ShowMessageBoxAsync($"Canceled:\n\n{reason}", UiUtils.MessageType.Error);
        }

        public static async Task Start(TaskType batchTask = TaskType.Null)
        {
            if (batchTask == TaskType.Null)
                runningBatch = false;

            TaskType task = batchTask == TaskType.Null ? Program.MainWin.SelectedTask : batchTask;

            if (FileList.Items.Count < 1)
            {
                await UiUtils.ShowMessageBox("No input files in file list! Please add one or more files first.");
                Program.MainWin.SelectedMainTab = 0;
                return;
            }
            else
            {
                var missingFiles = FileList.Items.Select(x => x.File).Where(x => !x.CheckFiles()).Select(x => x.SourcePath).ToList();

                if (missingFiles.Any())
                {
                    await UiUtils.ShowMessageBox($"The following files have been imported but are no longer accessible:\n\n{string.Join("\n", missingFiles)}\n\n" +
                        $"Possibly they were deleted, moved, or renamed.\nPlease either restore them or remove them from the file list.", UiUtils.MessageType.Error);
                    return;
                }
            }

            bool loadedFileRequired = task == TaskType.Convert || task == TaskType.Av1an || task == TaskType.UtilReadBitrates || task == TaskType.UtilOcr;

            if (loadedFileRequired && (currentFileListMode == FileListMode.Mux && TrackList.current == null))
            {
                await UiUtils.ShowMessageBox("No input file loaded! Please load one first (File List).");
                return;
            }

            if (task == TaskType.None)
            {
                if (!RunInstantly())
                    await UiUtils.ShowMessageBox("No task selected! Please select an option (Quick Encode or one of the actions in Utilities).");

                return;
            }

            canceled = false;
            FfmpegOutputHandler.overrideTargetDurationMs = -1;
            NmkdStopwatch sw = new NmkdStopwatch();

            Program.MainWin.RunningTask = task;
            if (task == TaskType.Convert) await QuickConvert.Run();
            else if (task == TaskType.Av1an) await Av1an.Run();
            else if (task == TaskType.UtilReadBitrates) await UtilReadBitrates.Run();
            else if (task == TaskType.UtilGetMetrics) await UtilGetMetrics.Run();
            else if (task == TaskType.UtilOcr) await UtilOcr.Run();
            else if (task == TaskType.UtilColorData) await UtilColorData.Run();
            else if (task == TaskType.UtilConcat) await UtilConcat.Run();
            else if (task == TaskType.PlotBitrate) await UtilPlotBitrate.Run();
            Program.MainWin.RunningTask = TaskType.None;

            Logger.Log($"Done - Finished task in {sw}.");
            Program.MainWin.SetProgress(0);
            Program.MainWin.SetWorking(false);
        }

        public static async Task StartBatch()
        {
            canceled = false;
            TaskType batchTask = Program.MainWin.SelectedTask;

            if (batchTask == TaskType.None)
            {
                await UiUtils.ShowMessageBox("No task selected for batch processing! Please select an option (Quick Encode, AV1AN or one of the actions in Utilities).");
                return;
            }

            TrackList.ClearCurrentFile();

            List<FileListEntry> taskFileListItems = FileList.Items.ToList();

            runningBatch = true;
            int finishedTasks = 0;
            NmkdStopwatch sw = new NmkdStopwatch();

            for (int i = 0; i < taskFileListItems.Count; i++)
            {
                if (canceled)
                    break;

                FileListEntry entry = taskFileListItems[i];
                Logger.Log($"Queue: Starting task {i + 1}/{taskFileListItems.Count} for {entry.File.Name}.");
                TrackList.ClearCurrentFile();
                await TrackList.SetAsMainFile(entry, false, false); // Load file info
                await TrackList.AddStreamsToList(entry.File, entry.RowBrush, true); // Load tracks into list (readonly for user)
                await Start(batchTask); // Run task

                if (!canceled)
                    finishedTasks++;
            }

            TrackList.ClearCurrentFile(true);
            runningBatch = false;

            Logger.Log($"Queue: Completed {finishedTasks}/{taskFileListItems.Count} tasks{(canceled ? " (Canceled)" : "")}. Total time: {sw}");
        }

        public static bool RunInstantly()
        {
            return Config.GetInt("taskMode") == 1;
        }
    }
}
