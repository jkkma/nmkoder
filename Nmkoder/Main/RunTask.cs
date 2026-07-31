using Nmkoder.Data;
using Nmkoder.Data.Ui;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Media;
using Nmkoder.OS;
using Nmkoder.UI;
using Nmkoder.UI.Tasks;
using Nmkoder.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Nmkoder.Main
{
    public class RunTask
    {
        public enum TaskType { Null, None, Convert, Av1an, UtilReadBitrates, UtilGetMetrics, UtilOcr, UtilColorData, UtilConcat, UtilCut, PlotBitrate };

        public enum FileListMode { Mux, Batch };
        public static FileListMode currentFileListMode;

        public static bool runningBatch = false;
        public static bool canceled = false;
        /// <summary> Set when the user pressed Stop, as against a task stopping itself over a bad setting or an error. </summary>
        public static bool canceledManually = false;
        /// <summary> Set when a task ran but did not produce its output, as against being canceled - av1an
        /// exiting nonzero sets nothing else, and used to be indistinguishable from success. </summary>
        public static bool failed = false;

        /// <summary>
        /// Set when Stop is pressed while a batch runs, and cleared only once the queue is over.
        /// <para/>
        /// It exists because <see cref="canceled"/> cannot carry this: every task clears that on its
        /// way in, so Stop pressed while the queue sat between two files was wiped by the very next
        /// <see cref="ResetOutcome"/> and the following file encoded anyway. Checking it before each
        /// file is not enough on its own either - the press can land during the checks - so
        /// ResetOutcome reinstates <see cref="canceled"/> from this rather than clearing it blindly.
        /// </summary>
        public static bool batchCanceled = false;

        /// <summary> Why the running task stopped, for the batch's end-of-queue summary. A batch
        /// shows no error boxes - twelve files would mean twelve of them - so this is where the
        /// reason goes instead. </summary>
        public static string lastFailReason = "";

        /// <summary> "File 3/12 (name.mkv) - " while a batch runs, so every progress line says where
        /// the queue stands; empty otherwise. </summary>
        static string batchProgressPrefix = "";

        /// <summary> Size summary of the last finished encode, e.g. "video.mkv: 2.1 GB → 780 MB (-63%)";
        /// empty for tasks that write no output file. </summary>
        static string lastOutputSummary = "";

        /// <summary> Input/output bytes accumulated over a batch, for the end-of-batch total. </summary>
        static long batchBytesIn, batchBytesOut;

        /// <summary> Clears the per-task outcome state before a run. A batch-level cancel deliberately
        /// survives it - see <see cref="batchCanceled"/>. </summary>
        public static void ResetOutcome()
        {
            // Outside a queue there is no batch cancel to honour, and leaving a stopped batch's flag
            // standing would start the next single task already canceled.
            if (!runningBatch)
                batchCanceled = false;

            canceled = canceledManually = batchCanceled;
            failed = false;
            lastOutputSummary = lastFailReason = "";
        }

        /// <summary>
        /// Marks the running task as having failed on its own, and says why. Distinct from
        /// <see cref="Cancel"/>: nothing is killed and no error box is raised, because the caller has
        /// already decided there is nothing to run. In a batch the reason lands on the file's row and
        /// in the end-of-queue summary.
        /// </summary>
        public static void Fail(string reason)
        {
            failed = true;
            lastFailReason = reason;
            Logger.Log(reason);
        }

        /// <summary>
        /// Post-encode size summary: says what was written and what that did to the size, in the log,
        /// the end-of-task status line and the completion toast. Call once a task's output exists.
        /// </summary>
        public static void ReportOutput(IEnumerable<string> inPaths, string outPath)
        {
            try
            {
                if (Path.GetFileName(outPath).Contains('%')) // An ffmpeg sequence pattern - measure the folder it fills
                    outPath = Path.GetDirectoryName(outPath);

                long outBytes = Directory.Exists(outPath) ? IoUtils.GetDirSize(outPath, true) : IoUtils.GetFilesize(outPath);

                if (outBytes <= 0)
                    return; // Nothing was written - the failure paths have their own reporting

                long inBytes = inPaths.Distinct().Sum(x => Directory.Exists(x) ? IoUtils.GetDirSize(x, true) : Math.Max(0, IoUtils.GetFilesize(x)));
                string summary = inBytes > 0 ? SizeDelta(inBytes, outBytes) : FormatUtils.Bytes(outBytes);

                if (inBytes > 0)
                {
                    batchBytesIn += inBytes;
                    batchBytesOut += outBytes;
                }

                lastOutputSummary = $"{Path.GetFileName(outPath)}: {summary}";
                Logger.Log($"Output: {lastOutputSummary}");
            }
            catch (Exception e)
            {
                Logger.Log($"Failed to summarize the output size: {e.Message}", true);
            }
        }

        /// <summary>
        /// Whether a task actually wrote what it said it would. An encoder that died has no other way
        /// of saying so here - ffmpeg's exit code is not carried back - and a run that wrote nothing
        /// counted as finished, which in a batch meant the tally said twelve of twelve.
        /// </summary>
        public static bool OutputExists(string outPath)
        {
            try
            {
                if (outPath.IsEmpty())
                    return false;

                if (Path.GetFileName(outPath).Contains('%')) // An ffmpeg sequence pattern - measure the folder it fills
                    outPath = Path.GetDirectoryName(outPath);

                return Directory.Exists(outPath) ? IoUtils.GetDirSize(outPath, true) > 0 : IoUtils.GetFilesize(outPath) > 0;
            }
            catch (Exception e)
            {
                Logger.Log($"Could not check the output file: {e.Message}", true);
                return true; // Not knowing is not the same as knowing it failed
            }
        }

        /// <summary> "2.1 GB → 780 MB (-63%)" </summary>
        static string SizeDelta(long inBytes, long outBytes)
        {
            int percent = (((double)(outBytes - inBytes) / inBytes) * 100).RoundToInt();
            return $"{FormatUtils.Bytes(inBytes)} → {FormatUtils.Bytes(outBytes)} ({(percent > 0 ? "+" : "")}{percent}%)";
        }

        /// <summary> Live progress line for the footer status label, from whichever parser has the
        /// numbers (ffmpeg stats, av1an's chunk log). Prefixed with the batch position when one runs. </summary>
        public static void ReportProgress(string text)
        {
            Program.MainWin?.SetStatus($"{batchProgressPrefix}{text}", silent: true);
        }

        /// <summary> Mirrors the footer's "Shutdown when done" checkbox as a plain bool, so it can be
        /// read from any thread. Deliberately never persisted: an armed shutdown must not outlive the
        /// session that armed it. </summary>
        public static bool shutdownWhenDone;

        /// <summary>
        /// Shuts the machine down 60 seconds after a run ended, if the checkbox is armed. Counts down
        /// in the status label; unticking the box aborts, as does starting another task. Canceled runs
        /// never shut down - a cancellation either has the user at the machine (Stop) or raises a
        /// modal error box that would sit unread on a dead screen (and block the abort checkbox).
        /// <para/>
        /// <paramref name="aborted"/> is that judgement, made by the caller rather than read off
        /// <see cref="canceled"/> here: at the end of a batch that flag describes the last file, and a
        /// queue of twelve that lost one of them has still finished, with nobody at the machine and no
        /// modal on the screen.
        /// </summary>
        internal static async Task ShutdownWhenDoneCountdown(bool aborted = false)
        {
            if (!shutdownWhenDone || aborted)
                return;

            Logger.Log("Shutting down in 60 seconds - untick 'Shutdown when done' to abort.");

            for (int secondsLeft = 60; secondsLeft > 0; secondsLeft--)
            {
                if (!shutdownWhenDone || Program.busy) // Unticked, or another task was started
                {
                    Logger.Log("Shutdown aborted.");
                    Program.MainWin?.SetStatus("Shutdown aborted.", silent: true);
                    return;
                }

                Program.MainWin?.SetStatus($"Shutting down in {secondsLeft}s - untick 'Shutdown when done' to abort.", silent: true);
                await Task.Delay(1000);
            }

            Logger.Log("Shutting down now.");
            OsUtils.Shutdown();
        }

        public static void Cancel(string reason = "", bool noMsgBox = false)
        {
            canceled = true;
            lastFailReason = reason;

            // Stop is the one cancellation a batch does not survive, and it has to outlive the task
            // state that is about to be cleared for the next file.
            if (runningBatch && canceledManually)
                batchCanceled = true;

            // A file stopping itself does not end the queue any more, so saying "Canceled" over the
            // whole window would be describing the wrong thing.
            bool continuingBatch = runningBatch && !canceledManually;
            Program.MainWin.SetStatus(continuingBatch ? $"{batchProgressPrefix}Failed - continuing with the next file." : "Canceled.");
            Program.MainWin.SetProgress(0);

            ProcessManager.KillPrimary();
            ProcessManager.KillSecondary();

            Program.MainWin.SetWorking(false);
            Logger.LogIfLastLineDoesNotContainMsg(continuingBatch ? "Continuing with the next file." : "Canceled.");

            // A task stopping itself is news; the user pressing Stop is not. Neither is worth a
            // notification per file - the batch sends one of its own when the queue ends.
            if (!canceledManually && !runningBatch)
                Notifications.ShowIfInBackground($"{GetTaskName(Program.MainWin.RunningTask)} canceled", reason.IsEmpty() ? "The log has the details." : reason.Trunc(200));

            // Likewise the error box: twelve files that all fail the same settings check would mean
            // twelve of them, each one waiting on a click before the queue could go on.
            if (!string.IsNullOrWhiteSpace(reason) && !noMsgBox && !runningBatch)
                UiUtils.ShowMessageBoxAsync($"Canceled:\n\n{reason}", UiUtils.MessageType.Error);
        }

        public static async Task Start(TaskType batchTask = TaskType.Null)
        {
            bool inBatch = batchTask != TaskType.Null;

            if (!inBatch)
                runningBatch = false;

            TaskType task = inBatch ? batchTask : Program.MainWin.SelectedTask;

            // Ahead of the checks below rather than after them. Each of those used to return without
            // marking anything, so in a batch the file was counted as finished; they now call Fail,
            // and this is what would otherwise clear that again. It also reinstates a Stop that
            // landed while the queue sat between two files.
            ResetOutcome();

            if (inBatch && batchCanceled)
                return;

            if (FileList.Items.Count < 1)
            {
                if (inBatch)
                {
                    Fail("The file list is empty.");
                    return;
                }

                await UiUtils.ShowMessageBox("No input files in file list! Please add one or more files first.");
                Program.MainWin.SelectedMainTab = 0;
                return;
            }

            // A batch only cares about the file it is encoding; a mux reads the whole list, so it is
            // the whole list that has to still be there. Checking all of them in a batch meant one
            // missing file raised the same modal on every one of the twelve.
            var missingFiles = (inBatch
                    ? (TrackList.current == null ? Enumerable.Empty<MediaFile>() : new[] { TrackList.current.File })
                    : FileList.Items.Select(x => x.File))
                .Where(x => !x.CheckFiles()).Select(x => x.SourcePath).ToList();

            if (missingFiles.Any())
            {
                if (inBatch)
                {
                    Fail($"'{Path.GetFileName(missingFiles[0])}' is no longer accessible - it may have been deleted, moved or renamed.");
                    return;
                }

                await UiUtils.ShowMessageBox($"The following files have been imported but are no longer accessible:\n\n{string.Join("\n", missingFiles)}\n\n" +
                    $"Possibly they were deleted, moved, or renamed.\nPlease either restore them or remove them from the file list.", UiUtils.MessageType.Error);
                return;
            }

            bool loadedFileRequired = task == TaskType.Convert || task == TaskType.Av1an || task == TaskType.UtilReadBitrates || task == TaskType.UtilOcr || task == TaskType.UtilCut;

            if (loadedFileRequired && TrackList.current == null && (inBatch || currentFileListMode == FileListMode.Mux))
            {
                if (inBatch)
                {
                    Fail("The file could not be loaded - the log has the details.");
                    return;
                }

                await UiUtils.ShowMessageBox("No input file loaded! Please load one first (File List).");
                return;
            }

            if (task == TaskType.None)
            {
                if (inBatch)
                {
                    Fail("No task is selected.");
                    return;
                }

                await UiUtils.ShowMessageBox("No task selected! Please select an option (Quick Encode or one of the actions in Utilities).");
                return;
            }

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
            else if (task == TaskType.UtilCut) await UtilCut.Run();
            else if (task == TaskType.PlotBitrate) await UtilPlotBitrate.Run();
            Program.MainWin.RunningTask = TaskType.None;

            Logger.Log(canceled || failed ? $"Stopped after {sw}." : $"Done - Finished task in {sw}.");
            Program.MainWin.SetProgress(0);
            Program.MainWin.SetWorking(false);

            if (!runningBatch)
            {
                NotifyTaskEnd(task, sw);
                _ = ShutdownWhenDoneCountdown(canceled);
            }
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

            string outNote = lastOutputSummary.IsEmpty() ? "" : $" - {lastOutputSummary}";
            Program.MainWin?.SetStatus(failed ? "Did not finish - the log has the details." : $"Done{outNote} - finished in {sw}.", silent: true);

            if (failed)
                Notifications.ShowIfInBackground($"{GetTaskName(task)} failed", "The task did not finish. The log has the details.");
            else
                Notifications.ShowIfInBackground($"{GetTaskName(task)} finished", lastOutputSummary.IsEmpty() ? $"Completed after {sw}." : $"{lastOutputSummary} - completed after {sw}.");
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
                case TaskType.UtilCut: return "Lossless cut";
                case TaskType.PlotBitrate: return "Bitrate chart";
                default: return "Task";
            }
        }

        public static async Task StartBatch()
        {
            canceled = canceledManually = batchCanceled = false;
            TaskType batchTask = Program.MainWin.SelectedTask;

            if (batchTask == TaskType.None)
            {
                await UiUtils.ShowMessageBox("No task selected for batch processing! Please select an option (Quick Encode, AV1AN or one of the actions in Utilities).");
                return;
            }

            if (FileList.Items.Count < 1)
            {
                await UiUtils.ShowMessageBox("No input files in file list! Please add one or more files first.");
                Program.MainWin.SelectedMainTab = 0;
                return;
            }

            TrackList.ClearCurrentFile(resetSettings: false);

            List<FileListEntry> taskFileListItems = FileList.Items.ToList();

            runningBatch = true;
            batchBytesIn = batchBytesOut = 0;
            NmkdStopwatch sw = new NmkdStopwatch();

            foreach (FileListEntry entry in taskFileListItems)
                entry.SetBatchStatus(BatchStatus.Queued);

            // Held for the whole queue rather than per file: every task clears the working state on
            // its way out, and between two files that put the Run button back, took Stop away, and
            // left a second batch one click from starting. SetWorking ignores the clears while
            // runningBatch is set, so this is the one that has to be undone below.
            Program.MainWin.SetWorking(true);

            try
            {
                for (int i = 0; i < taskFileListItems.Count; i++)
                {
                    FileListEntry entry = taskFileListItems[i];

                    // Only Stop ends the queue. A file that stopped itself - a bad output path, an
                    // argument av1an would not take, an encoder that crashed - used to take the other
                    // eleven with it, which is the last thing an overnight batch should do.
                    if (batchCanceled)
                    {
                        entry.SetBatchStatus(BatchStatus.Skipped, "The batch was stopped before this file ran.");
                        continue;
                    }

                    // Ahead of the scan below, not just inside Start: the previous file's cancel is
                    // still set here, and the output handlers go quiet while it is.
                    ResetOutcome();
                    Logger.Log($"Queue: Starting task {i + 1}/{taskFileListItems.Count} for {entry.File.Name}.");
                    batchProgressPrefix = $"File {i + 1}/{taskFileListItems.Count} ({entry.File.Name}) - ";
                    entry.SetBatchStatus(BatchStatus.Running);
                    Program.MainWin.ScrollFileIntoView(entry);
                    // Which file the naming template is resolving for, since neither the file being
                    // loaded nor the task being run is knowable from the output box alone.
                    BatchNaming.SetContext(batchTask, i + 1, taskFileListItems.Count);
                    TrackList.ClearCurrentFile(resetSettings: false);
                    // Neither of these switches to the Track List tab: the queue is watched from
                    // whichever tab the user left it on, and the list is read-only here anyway.
                    await TrackList.SetAsMainFile(entry, false, false); // Load file info
                    await TrackList.AddStreamsToList(entry.File, entry.RowBrush, false); // Load tracks into list (readonly for user)

                    await Start(batchTask); // Run task

                    // A run that failed on its own (av1an exiting nonzero, a bad output path) does not
                    // set canceled, and used to be counted as finished here.
                    if (batchCanceled)
                        entry.SetBatchStatus(BatchStatus.Canceled, "Stopped by the user.");
                    else if (canceled || failed)
                        entry.SetBatchStatus(BatchStatus.Failed, lastFailReason.IsEmpty() ? "The log has the details." : lastFailReason);
                    else
                        entry.SetBatchStatus(BatchStatus.Done, lastOutputSummary);
                }
            }
            finally
            {
                BatchNaming.ClearContext();
                TrackList.ClearCurrentFile(true, resetSettings: false);
                runningBatch = false;
                batchProgressPrefix = "";
                Program.MainWin.SetWorking(false);
            }

            ReportBatchEnd(taskFileListItems, sw);
        }

        /// <summary>
        /// What became of every file in the queue. A batch that runs overnight is read afterwards,
        /// not watched, so the per-file outcome has to survive somewhere - the rows keep it, and this
        /// puts the same thing in the log where it can be copied out.
        /// </summary>
        private static void ReportBatchEnd(List<FileListEntry> entries, NmkdStopwatch sw)
        {
            int done = entries.Count(x => x.Status == BatchStatus.Done);
            int failedCount = entries.Count(x => x.Status == BatchStatus.Failed);
            int skipped = entries.Count(x => x.Status == BatchStatus.Skipped || x.Status == BatchStatus.Canceled);

            string totalSizes = batchBytesIn > 0 && batchBytesOut > 0 ? $" Total size: {SizeDelta(batchBytesIn, batchBytesOut)}." : "";
            string counts = $"{done}/{entries.Count} finished" +
                $"{(failedCount > 0 ? $", {failedCount} failed" : "")}" +
                $"{(skipped > 0 ? $", {skipped} not run" : "")}";

            Logger.Log($"Queue: {counts}{(batchCanceled ? " (Stopped)" : "")}. Total time: {sw}.{totalSizes}");

            foreach (FileListEntry entry in entries)
            {
                // The reason lives on the note rather than the status text, and a settings check's
                // reason is a paragraph with blank lines in it - which in a queue of forty would be
                // the log's whole tail.
                string note = OneLine(entry.StatusNote);
                Logger.Log($"  {entry.StatusGlyph} {entry.File.Name}: {entry.Status}{(note.IsEmpty() ? "" : $" - {note}")}");
            }

            string status = $"Batch done - {counts} in {sw}.{totalSizes}";
            Program.MainWin?.SetStatus(batchCanceled ? $"Batch stopped - {counts}." : status, silent: true);

            // One notification for the queue rather than one per file, and it says how many did not
            // make it, because a batch left running is exactly the case where nobody saw the log.
            if (!batchCanceled)
            {
                Notifications.ShowIfInBackground(failedCount > 0 ? "Batch finished with failures" : "Batch finished",
                    $"{counts}. Total time: {sw}.{totalSizes}");
                _ = ShutdownWhenDoneCountdown();
            }
        }

        /// <summary> A multi-line reason squeezed onto the one line a summary entry gets. </summary>
        private static string OneLine(string text)
        {
            return string.Join(" ", (text ?? "").Split('\r', '\n').Select(x => x.Trim()).Where(x => x.IsNotEmpty())).Trunc(200);
        }
    }
}
