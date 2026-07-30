using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace Nmkoder.OS
{
    class OsUtils
    {
        public static bool IsUserAdministrator()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    return IsWindowsAdministrator();

                return PosixGetEuid() == 0;
            }
            catch (Exception e)
            {
                Logger.Log("IsUserAdministrator() Error: " + e.Message);
                return false;
            }
        }

        [SupportedOSPlatform("windows")]
        private static bool IsWindowsAdministrator()
        {
            using WindowsIdentity user = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(user).IsInRole(WindowsBuiltInRole.Administrator);
        }

        [DllImport("libc", EntryPoint = "geteuid")]
        private static extern uint PosixGetEuid();

        public static Process SetStartInfo(Process proc, bool hidden, string filename = null)
        {
            proc.StartInfo.UseShellExecute = !hidden;
            proc.StartInfo.RedirectStandardOutput = hidden;
            proc.StartInfo.RedirectStandardError = hidden;
            proc.StartInfo.CreateNoWindow = hidden;
            proc.StartInfo.FileName = string.IsNullOrWhiteSpace(filename) ? Shell.Interpreter : filename;
            return proc;
        }

        public static bool IsProcessHidden(Process proc)
        {
            bool defaultVal = true;

            try
            {
                if (proc == null)
                {
                    Logger.Log($"IsProcessHidden was called but proc is null, defaulting to {defaultVal}", true);
                    return defaultVal;
                }

                if (proc.HasExited)
                {
                    Logger.Log($"IsProcessHidden was called but proc has already exited, defaulting to {defaultVal}", true);
                    return defaultVal;
                }

                ProcessStartInfo si = proc.StartInfo;
                return !si.UseShellExecute && si.CreateNoWindow;
            }
            catch (Exception e)
            {
                Logger.Log($"IsProcessHidden errored, defaulting to {defaultVal}: {e.Message}", true);
                return defaultVal;
            }
        }

        public static Process NewProcess(bool hidden, NmkoderProcess.ProcessType type, string filename = null)
        {
            NmkoderProcess p = new NmkoderProcess(new Process(), type);
            ProcessManager.RegisterProcess(p);
            return SetStartInfo(p.Process, hidden, filename);
        }

        /// <summary>
        /// Kills a process and everything it spawned. .NET has had a portable implementation of this
        /// since .NET 5, so the WMI-based Win32_Process recursion the original used is no longer needed.
        /// </summary>
        public static void KillProcessTree(int pid)
        {
            try
            {
                using Process proc = Process.GetProcessById(pid);

                if (!proc.HasExited)
                    proc.Kill(entireProcessTree: true);
            }
            catch (ArgumentException)
            {
                // Process already exited.
            }
            catch (InvalidOperationException)
            {
                // Process already exited.
            }
        }

        public static string GetCmdArg()
        {
            return Shell.RunFlag(Config.GetInt(Config.Key.CmdDebugMode) == 2);
        }

        public static bool ShowHiddenCmd()
        {
            return Config.GetInt(Config.Key.CmdDebugMode) > 0;
        }

        /// <summary>
        /// Used to decide whether av1an can use aggressive temp file IO. Detecting the physical media
        /// type portably isn't possible, so assume SSD - which is the safe default the old code also
        /// fell back to whenever detection failed.
        /// </summary>
        public static bool DriveIsSSD(string path)
        {
            return true;
        }

        public static bool HasNonAsciiChars(string str)
        {
            return Encoding.UTF8.GetByteCount(str) != str.Length;
        }

        public static int GetFreeRamMb()
        {
            try
            {
                GCMemoryInfo info = GC.GetGCMemoryInfo();

                if (info.TotalAvailableMemoryBytes > 0)
                    return (int)((info.TotalAvailableMemoryBytes - info.MemoryLoadBytes) / 1048576);
            }
            catch { }

            return 1000;
        }

        public static string TryGetOs()
        {
            try
            {
                return $"{RuntimeInformation.OSDescription} | {RuntimeInformation.OSArchitecture}";
            }
            catch (Exception e)
            {
                Logger.Log("TryGetOs Error: " + e.Message, true);
                return "";
            }
        }

        /// <summary>
        /// Returns the given processes together with every descendant they have spawned, parents
        /// before their children, resolved from one snapshot of the system process table. The tasks
        /// run through a shell interpreter, so the interesting processes are grandchildren and deeper
        /// (av1an's worker pipelines). Platforms without a snapshot implementation get just the roots
        /// back and the caller degrades gracefully (only used for pausing subprocesses).
        /// </summary>
        public static List<Process> GetProcessTree(IEnumerable<Process> roots)
        {
            List<Process> tree = roots.Where(x => x != null).ToList();

            try
            {
                Dictionary<int, List<int>> childPids = GetProcessSnapshot();
                HashSet<int> seen = new HashSet<int>(tree.Select(x => x.Id));
                Queue<int> queue = new Queue<int>(seen);

                while (queue.Count > 0)
                {
                    if (!childPids.TryGetValue(queue.Dequeue(), out List<int> children))
                        continue;

                    foreach (int pid in children)
                    {
                        if (!seen.Add(pid))
                            continue;

                        queue.Enqueue(pid); // A process that exited since the snapshot can still have live children listed under it

                        try { tree.Add(Process.GetProcessById(pid)); }
                        catch { } // Exited between the snapshot and now
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Log($"GetProcessTree Error: {e.Message}", true);
            }

            return tree;
        }

        /// <summary> Maps each parent PID to the PIDs of its direct children, from one snapshot. </summary>
        private static Dictionary<int, List<int>> GetProcessSnapshot()
        {
            if (OperatingSystem.IsWindows())
                return GetProcessSnapshotWindows();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return GetProcessSnapshotLinux();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return GetProcessSnapshotMacos();

            return new Dictionary<int, List<int>>();
        }

        [SupportedOSPlatform("windows")]
        private static Dictionary<int, List<int>> GetProcessSnapshotWindows()
        {
            Dictionary<int, List<int>> map = new Dictionary<int, List<int>>();

            using var searcher = new System.Management.ManagementObjectSearcher("Select ProcessID, ParentProcessID From Win32_Process");

            foreach (System.Management.ManagementObject mo in searcher.Get())
                AddToSnapshot(map, Convert.ToInt32(mo["ParentProcessID"]), Convert.ToInt32(mo["ProcessID"]));

            return map;
        }

        private static Dictionary<int, List<int>> GetProcessSnapshotLinux()
        {
            Dictionary<int, List<int>> map = new Dictionary<int, List<int>>();

            foreach (string dir in Directory.GetDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), out int pid))
                    continue;

                string stat;

                try { stat = File.ReadAllText(Path.Combine(dir, "stat")); }
                catch { continue; } // Exited mid-scan

                // The fields come after the parenthesized command name, which can itself contain parentheses
                int nameEnd = stat.LastIndexOf(')');

                if (nameEnd < 0)
                    continue;

                string[] fields = stat.Substring(nameEnd + 1).Trim().Split(' '); // [0] = state, [1] = ppid

                if (fields.Length >= 2 && int.TryParse(fields[1], out int ppid))
                    AddToSnapshot(map, ppid, pid);
            }

            return map;
        }

        private static Dictionary<int, List<int>> GetProcessSnapshotMacos()
        {
            Dictionary<int, List<int>> map = new Dictionary<int, List<int>>();

            // No /proc on macOS, so ask ps. Deliberately a bare Process rather than NewProcess:
            // this must not be registered as a subprocess, or pausing would try to pause it.
            using Process ps = new Process();
            ps.StartInfo = new ProcessStartInfo("ps", "-axo pid=,ppid=") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            ps.Start();
            string output = ps.StandardOutput.ReadToEnd();
            ps.WaitForExit(3000);

            foreach (string line in output.SplitIntoLines())
            {
                string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                if (fields.Length >= 2 && int.TryParse(fields[0], out int pid) && int.TryParse(fields[1], out int ppid))
                    AddToSnapshot(map, ppid, pid);
            }

            return map;
        }

        private static void AddToSnapshot(Dictionary<int, List<int>> map, int parentPid, int childPid)
        {
            if (!map.TryGetValue(parentPid, out List<int> children))
                map[parentPid] = children = new List<int>();

            children.Add(childPid);
        }

        public static async Task<string> GetOutputAsync(Process process, bool onlyLastLine = false)
        {
            Logger.Log($"Getting output for {process.StartInfo.FileName} {process.StartInfo.Arguments}", true);
            NmkdStopwatch sw = new NmkdStopwatch();

            Stopwatch timeSinceLastOutput = new Stopwatch();
            timeSinceLastOutput.Restart();

            string output = "";

            process.OutputDataReceived += (object sender, DataReceivedEventArgs e) => output += $"{e.Data}\n";
            process.ErrorDataReceived += (object sender, DataReceivedEventArgs e) => output += $"{e.Data}\n";
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            while (!process.HasExited) await Task.Delay(50);
            while (timeSinceLastOutput.ElapsedMilliseconds < 100) await Task.Delay(50);
            output = output.Trim('\r', '\n');

            Logger.Log($"Output (after {sw}):  {output.Replace("\r", " / ").Replace("\n", " / ").Trunc(250)}", true);

            if (onlyLastLine)
                output = output.SplitIntoLines().LastOrDefault();

            return output;
        }

        public static void Shutdown()
        {
            Process proc = NewProcess(true, NmkoderProcess.ProcessType.Secondary);
            proc.StartInfo.Arguments = Shell.BuildArguments(Shell.IsWindows ? "shutdown -s -t 0" : "shutdown -h now");
            proc.Start();
        }

        public static void ShowNotification(string title, string text)
        {
            UI.Notifications.Show(title, text);
        }

        public static void ShowNotificationIfInBackground(string title, string text)
        {
            if (Program.MainWin != null && Program.MainWin.IsInFocus())
                return;

            ShowNotification(title, text);
        }

        public static string GetPathVar(string additionalPath = null)
        {
            return GetPathVar(new[] { additionalPath });
        }

        /// <summary>
        /// Points a process at Nmkoder's bundled tools through PATH. Assigning environment variables
        /// is only legal when the process is not started through the shell - doing it anyway makes
        /// Start() throw "The Process object must have the UseShellExecute property set to false in
        /// order to use environment variables", which is why this has to be asked rather than assumed.
        /// A process that does run through the shell gets its PATH from the launch script instead.
        /// </summary>
        public static void SetPathVar(Process proc, IEnumerable<string> additionalPaths)
        {
            if (proc.StartInfo.UseShellExecute)
                return;

            proc.StartInfo.EnvironmentVariables["PATH"] = GetPathVar(additionalPaths);
        }

        /// <summary>
        /// Builds a PATH that puts Nmkoder's own bin folder first. On Windows the original also
        /// stripped everything but C:\Windows to avoid stray ffmpeg builds shadowing the bundled one;
        /// that heuristic is kept for Windows and the full PATH is preserved elsewhere.
        /// </summary>
        public static string GetPathVar(IEnumerable<string> additionalPaths)
        {
            char sep = Shell.PathSeparator;
            string[] paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(sep);
            List<string> newPaths = new List<string>();

            if (additionalPaths != null)
                newPaths.AddRange(additionalPaths.Where(p => p.IsNotEmpty()));

            if (Shell.IsWindows)
                newPaths.AddRange(paths.Where(x => x.Lower().Replace("\\", "/").StartsWith("c:/windows")));
            else
                newPaths.AddRange(paths.Where(x => x.IsNotEmpty()));

            return string.Join(sep.ToString(), newPaths) + sep;
        }
    }
}
