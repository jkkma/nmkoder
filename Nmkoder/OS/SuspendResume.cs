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

        public static void Reset()
        {
            SetRunning(false);
            SetPauseButtonStyle(false);
        }

        public static void SetRunning(bool running)
        {
            isRunning = running;
            Program.MainWin?.SetPauseButtonVisible(running);
        }

        public static void SuspendIfRunning()
        {
            if (!frozen)
                SuspendProcs(true);
        }

        public static void ResumeIfPaused()
        {
            if (frozen)
                SuspendProcs(false);
        }

        public static void SuspendProcs(bool freeze, bool excludeCmd = false)
        {
            if (ProcessManager.RunningSubProcesses.Count < 1)
                return;

            Logger.Log($"{(freeze ? "Suspending" : "Resuming")} processes!", true);

            if (freeze)
            {
                List<Process> procs = new List<Process>();

                foreach (Process parent in new List<Process>(ProcessManager.RunningSubProcesses.Select(x => x.Process))) // We MUST clone the list here since it might get modifed!
                    procs.AddRange(OsUtils.GetChildProcesses(parent));

                frozen = true;
                SetPauseButtonStyle(true);

                foreach (Process process in procs)
                {
                    if (process == null || process.HasExited)
                        continue;

                    if (excludeCmd && (process.ProcessName == "conhost" || process.ProcessName == "cmd" || process.ProcessName == "sh"))
                        continue;

                    if (process.ProcessName == "av1an")
                        Logger.Log($"Warning: Pausing {process.ProcessName} might not work correctly. Use at your own risk.");

                    Logger.Log($"Suspending {process.ProcessName}", true);

                    process.Suspend();
                    suspendedProcesses.Add(process);
                }
            }
            else
            {
                frozen = false;
                SetPauseButtonStyle(false);

                foreach (Process process in new List<Process>(suspendedProcesses))   // We MUST clone the list here since we modify it in the loop!
                {
                    if (process == null || process.HasExited)
                        continue;

                    Logger.Log($"Resuming {process.ProcessName}", true);

                    process.Resume();
                    suspendedProcesses.Remove(process);
                }
            }
        }

        public static void SetPauseButtonStyle(bool paused)
        {
            Program.MainWin?.SetPauseButtonPaused(paused);
        }
    }
}
