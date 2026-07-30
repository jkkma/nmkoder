using Nmkoder.Extensions;
using Nmkoder.IO;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nmkoder.OS
{
    class ProcessManager
    {
        static List<NmkoderProcess> subProcesses = new List<NmkoderProcess>();
        public static List<NmkoderProcess> AllSubProcesses { get { return GetStartedSubProcesses(); } }
        public static List<NmkoderProcess> RunningSubProcesses { get { return GetRunningSubProcesses(); } }
        public static List<NmkoderProcess> ExitedSubProcesses { get { return GetExitedSubProcesses(); } }

        public static void RegisterProcess(NmkoderProcess p)
        {
            subProcesses.Add(p);
        }

        public static List<NmkoderProcess> GetRunningSubProcesses ()
        {
            List<NmkoderProcess> running = new List<NmkoderProcess>();

            foreach(NmkoderProcess p in new List<NmkoderProcess>(subProcesses))
            {
                try
                {
                    if (!p.Process.HasExited)
                        running.Add(p);
                }
                catch { }
            }

            return running;
        }

        public static List<NmkoderProcess> GetExitedSubProcesses()
        {
            List<NmkoderProcess> running = new List<NmkoderProcess>();

            foreach (NmkoderProcess p in new List<NmkoderProcess>(subProcesses))
            {
                try
                {
                    if (p.Process.HasExited)
                        running.Add(p);
                }
                catch { }
            }

            return running;
        }

        public static List<NmkoderProcess> GetStartedSubProcesses()
        {
            List<NmkoderProcess> running = new List<NmkoderProcess>();

            foreach (NmkoderProcess p in new List<NmkoderProcess>(subProcesses))
            {
                try
                {
                    running.Add(p);
                }
                catch { }
            }

            return running;
        }

        public static void ClearExitedProcesses ()
        {
            subProcesses = new List<NmkoderProcess>(subProcesses).Where(x => !x.Process.HasExited).ToList();
        }

        public static void Kill (List<NmkoderProcess> list)
        {
            if (list.Count < 1)
                return;

            Logger.Log($"ProcMan: Killing {list.Count} subprocesses ({string.Join(", ", list.Select(x => Describe(x)))})", true);

            foreach(NmkoderProcess np in list)
            {
                Logger.Log($"ProcMan: Killing {Describe(np)} ({np.Type})...", true);

                try
                {
                    OsUtils.KillProcessTree(np.Process.Id);
                    Logger.Log($"ProcMan: Killed process tree for {Describe(np, withArgs: true)}", true);
                }
                catch(Exception e)
                {
                    Logger.Log($"ProcMan: Failed to kill process tree for {Describe(np, withArgs: true)}: {e.Message}", true);
                }
            }
        }

        /// <summary>
        /// What a process is, for the log. Reading StartInfo throws once the process object has
        /// been disposed, and every one of these reads used to sit outside the try below - so one
        /// finished subprocess in the list could throw on its way into a log line and leave every
        /// other process in it running, which is the whole of what this class exists to prevent.
        /// </summary>
        private static string Describe(NmkoderProcess np, bool withArgs = false)
        {
            try
            {
                ProcessStartInfo info = np?.Process?.StartInfo;

                if (info == null)
                    return "unknown process";

                return withArgs ? $"{info.FileName} {info.Arguments.Trunc(150)}" : info.FileName;
            }
            catch
            {
                return "unknown process";
            }
        }

        public static void KillAll()
        {
            Kill(RunningSubProcesses);
        }

        public static void KillPrimary ()
        {
            Kill(RunningSubProcesses.Where(x => x.Type == NmkoderProcess.ProcessType.Primary).ToList());
        }

        public static void KillSecondary()
        {
            Kill(RunningSubProcesses.Where(x => x.Type == NmkoderProcess.ProcessType.Secondary).ToList());
        }

        public static void KillBackground()
        {
            Kill(RunningSubProcesses.Where(x => x.Type == NmkoderProcess.ProcessType.Background).ToList());
        }
    }
}
