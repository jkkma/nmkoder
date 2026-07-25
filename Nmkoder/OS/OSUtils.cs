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
        /// Enumerates direct children of a process. Windows exposes this via WMI, Linux via
        /// /proc/[pid]/task/*/children; other platforms get an empty list and the caller degrades
        /// gracefully (only used for pausing subprocesses).
        /// </summary>
        public static IEnumerable<Process> GetChildProcesses(Process process)
        {
            List<Process> children = new List<Process>();

            try
            {
                foreach (int pid in GetChildProcessIds(process.Id))
                {
                    try { children.Add(Process.GetProcessById(pid)); }
                    catch { }
                }
            }
            catch (Exception e)
            {
                Logger.Log($"GetChildProcesses Error: {e.Message}", true);
            }

            return children;
        }

        private static IEnumerable<int> GetChildProcessIds(int parentPid)
        {
            if (OperatingSystem.IsWindows())
                return GetChildProcessIdsWindows(parentPid);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return GetChildProcessIdsLinux(parentPid);

            return Enumerable.Empty<int>();
        }

        [SupportedOSPlatform("windows")]
        private static List<int> GetChildProcessIdsWindows(int parentPid)
        {
            List<int> pids = new List<int>();

            using var searcher = new System.Management.ManagementObjectSearcher($"Select ProcessID From Win32_Process Where ParentProcessID={parentPid}");

            foreach (System.Management.ManagementObject mo in searcher.Get())
                pids.Add(Convert.ToInt32(mo["ProcessID"]));

            return pids;
        }

        private static List<int> GetChildProcessIdsLinux(int parentPid)
        {
            List<int> pids = new List<int>();
            string taskDir = $"/proc/{parentPid}/task";

            if (!Directory.Exists(taskDir))
                return pids;

            foreach (string dir in Directory.GetDirectories(taskDir))
            {
                string childrenFile = Path.Combine(dir, "children");

                if (!File.Exists(childrenFile))
                    continue;

                foreach (string entry in File.ReadAllText(childrenFile).Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (int.TryParse(entry, out int pid))
                        pids.Add(pid);
                }
            }

            return pids;
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
