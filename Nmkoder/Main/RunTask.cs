using Nmkoder.Data;
using Nmkoder.Data.Ui;
using Nmkoder.Extensions;
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
        /// <summary> Set when the user pressed Stop, as against a task stopping itself over a bad setting or an error. </summary>
        public static bool canceledManually = false;
        /// <summary> Set when a task ran but did not produce its output, as against being canceled - av1an
        /// exiting nonzero sets nothing else, and used to be indistinguishable from success. </summary>
        public static bool failed = false;

        /// <summary> "File 3/12 (name.mkv) - " while a batch runs, so every progress line says where
        /// the queue stands; empty otherwise. </summary>
        static string batchProgressPrefix = "";

        /// <summary> Live progress line for the footer status label, from whichever parser has the
        /// numbers (ffmpeg stats, av1an's chunk log). Prefixed with the batch position when one runs. </summary>
        public static void ReportProgress(string text)
        {
            Program.MainWin?.SetStatus($"{batchProgressPrefix}{text}", silent: true);
        }

        public static void Cancel(string reason = "", bool noMsgBox = false)
        {
            canceled = true;
            Program.MainWin.SetStatus("Canceled.");
            Program.MainWin.SetProgress(0);

            ProcessManager.KillPrimary();
            ProcessManager.KillSecondary();

            Program.MainWin.SetWorking(false);
            Logger.LogIfLastLineDoesNotContainMsg("Canceled.");

            // A task stopping itself is news; the user pressing Stop is not.
            if (!canceledManually)
                Notifications.ShowIfInBackground($"{GetTaskName(Program.MainWin.RunningTask)} canceled", reason.IsEmpty() ? "The log has the details." : reason.Trunc(200));

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
                await UiUtils.ShowMessageBox("No task selected! Please select an option (Quick Encode or one of the actions in Utilities).");
                return;
            }

            canceled = canceledManually = failed = false;
            FfmpegOutputHandler.overrideTargetDurationMs = -1;
            NmkdStopwatch sw = new NmkdStopwatch();

            Program.MainWin.RunningTask = task;
            ReportProgress($"Running: {GetTaskName(task)}..."); // Overwritten as soon as a parser has real numbers
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

            if (!runningBatch)
                NotifyTaskEnd(task, sw);
        }

        /// <summary>
        /// Final status line and completion toast for a task that ran to its end. A long encode is
        /// usually left running in the background, where the "Done" log line reaches nobody.
        /// Cancellations set their status and notify from Cancel() instead, where the reason is at hand.
        /// </summary>
        internal static void NotifyTaskEnd(TaskType task, NmkdStopwatch sw)
        {
            if (canceled)
                return;

            Program.MainWin?.SetStatus(failed ? "Did not finish - the log has the details." : $"Done - finished in {sw}.", silent: true);

            if (failed)
                Notifications.ShowIfInBackground($"{GetTaskName(task)} failed", "The task did not finish. The log has the details.");
            else
                Notifications.ShowIfInBackground($"{GetTaskName(task)} finished", $"Completed after {sw}.");
        }

        /// <summary> How a task announces itself in a notification title. </summary>
        private static string GetTaskName(TaskType task)
        {
            switch (task)
            {
                case TaskType.Convert: return "Encode";
                case TaskType.Av1an: return "AV1AN encode";
                case TaskType.UtilReadBitrates: return "Bitrate reading";
                case TaskType.UtilGetMetrics: return "Metrics calculation";
                case TaskType.UtilOcr: return "Subtitle OCR";
                case TaskType.UtilColorData: return "Color data transfer";
                case TaskType.UtilConcat: return "Concatenation";
                case TaskType.PlotBitrate: return "Bitrate chart";
                default: return "Task";
            }
        }

        public static async Task StartBatch()
        {
            canceled = canceledManually = false;
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
                batchProgressPrefix = $"File {i + 1}/{taskFileListItems.Count} ({entry.File.Name}) - ";
                TrackList.ClearCurrentFile();
                await TrackList.SetAsMainFile(entry, false, false); // Load file info
                await TrackList.AddStreamsToList(entry.File, entry.RowBrush, true); // Load tracks into list (readonly for user)
                await Start(batchTask); // Run task

                // A run that failed on its own (av1an exiting nonzero, a bad output path) does not
                // set canceled, and used to be counted as finished here.
                if (!canceled && !failed)
                    finishedTasks++;
            }

            TrackList.ClearCurrentFile(true);
            runningBatch = false;
            batchProgressPrefix = "";

            Logger.Log($"Queue: Completed {finishedTasks}/{taskFileListItems.Count} tasks{(canceled ? " (Canceled)" : "")}. Total time: {sw}");

            // A canceled batch already notified from Cancel(), naming the reason.
            if (!canceled)
            {
                Program.MainWin?.SetStatus($"Batch done - completed {finishedTasks}/{taskFileListItems.Count} tasks in {sw}.", silent: true);
                Notifications.ShowIfInBackground("Batch finished", $"Completed {finishedTasks} of {taskFileListItems.Count} tasks. Total time: {sw}.");
            }
        }
    }
}
