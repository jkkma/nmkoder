using Nmkoder.Extensions;
using Nmkoder.IO;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Nmkoder.OS
{
    class SuspendResume
    {
        public static bool frozen;
        static List<Process> suspendedProcesses = new List<Process>();
        public static bool isRunning;

        /// <summary> A Pause press runs on a worker thread while a task can end - and Reset - from an
        /// output reader thread, so every state transition goes through this lock. </summary>
        static readonly object stateLock = new object();

        /// <summary> Called whenever a task ends, however it ends - finished, failed or canceled.
        /// Nothing may stay frozen once the task it belongs to is gone. </summary>
        public static void Reset()
        {
            lock (stateLock)
            {
                if (frozen)
                    Resume();
            }

            SetRunning(false);
        }

        public static void SetRunning(bool running)
        {
            isRunning = running;
            Program.MainWin?.SetPauseButtonVisible(running);
        }

        public static void SuspendIfRunning()
        {
            lock (stateLock)
            {
                if (!frozen)
                    Suspend();
            }
        }

        public static void ResumeIfPaused()
        {
            lock (stateLock)
            {
                if (frozen)
                    Resume();
            }
        }

        /// <summary> One press of the Pause button. Runs off the UI thread (the Windows process
        /// snapshot is a WMI query), and the lock keeps rapid presses in click order. </summary>
        public static void TogglePause()
        {
            lock (stateLock)
            {
                if (frozen)
                    Resume();
                else
                    Suspend();
            }
        }

        static void Suspend()
        {
            List<Process> roots = ProcessManager.RunningSubProcesses.Select(x => x.Process).ToList();

            if (roots.Count < 1)
                return;

            frozen = true;
            SetPauseButtonStyle(true);

            // A parent that is not yet suspended can spawn a child between the snapshot and its own
            // suspension - av1an starts a new worker pipeline whenever a chunk finishes - so sweep
            // again until a pass finds nothing new. Once every spawner is frozen, nothing appears.
            for (int pass = 0; pass < 3; pass++)
            {
                int suspended = 0;

                foreach (Process process in OsUtils.GetProcessTree(roots))
                {
                    try
                    {
                        if (process.HasExited || suspendedProcesses.Any(x => x.Id == process.Id))
                            continue;

                        // conhost only renders the console window and spawns nothing; freezing it
                        // would hang the window the user is watching in console debug mode.
                        if (process.ProcessName == "conhost")
                            continue;

                        Logger.Log($"Suspending {process.ProcessName}", true);
                        process.Suspend();
                        suspendedProcesses.Add(process);
                        suspended++;
                    }
                    catch (Exception e)
                    {
                        Logger.Log($"Failed to suspend a process: {e.Message}", true);
                    }
                }

                if (suspended < 1)
                    break;
            }

            Logger.Log($"Paused - froze {suspendedProcesses.Count} processes. Speed and ETA readings will be off after resuming, since paused time still counts as elapsed.");
        }

        static void Resume()
        {
            frozen = false;
            SetPauseButtonStyle(false);
            int resumed = 0;

            foreach (Process process in suspendedProcesses)
            {
                try
                {
                    if (process == null || process.HasExited)
                        continue;

                    Logger.Log($"Resuming {process.ProcessName}", true);
                    process.Resume();
                    resumed++;
                }
                catch (Exception e)
                {
                    Logger.Log($"Failed to resume a process: {e.Message}", true);
                }
            }

            suspendedProcesses.Clear();

            // Reset() also lands here to clear the state after the processes were killed; only an
            // actual thaw is worth a line in the log.
            if (resumed > 0)
                Logger.Log("Resumed.");
        }

        public static void SetPauseButtonStyle(bool paused)
        {
            Program.MainWin?.SetPauseButtonPaused(paused);
        }
    }
}
