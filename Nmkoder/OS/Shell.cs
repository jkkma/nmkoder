using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Nmkoder.OS
{
    /// <summary>
    /// Abstracts away the platform's command interpreter. The original code hardcoded
    /// "cmd.exe /C ..." everywhere; on Linux/macOS we need "/bin/sh -c ..." instead.
    /// </summary>
    public static class Shell
    {
        public static bool IsWindows { get { return RuntimeInformation.IsOSPlatform(OSPlatform.Windows); } }

        /// <summary> Executable used to run a command line. </summary>
        public static string Interpreter { get { return IsWindows ? "cmd.exe" : "/bin/sh"; } }

        /// <summary> Flag that tells the interpreter that the next argument is the command to run. </summary>
        public static string RunFlag(bool stayOpen = false)
        {
            if (IsWindows)
                return stayOpen ? "/K" : "/C";

            return "-c";
        }

        /// <summary> Directory separator used inside shell commands. </summary>
        public static char PathSeparator { get { return IsWindows ? ';' : ':'; } }

        /// <summary> Builds a "change directory" prefix for a chained shell command. </summary>
        public static string ChangeDir(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir))
                return "";

            return IsWindows ? $"cd /D \"{dir}\" &" : $"cd \"{dir}\" &&";
        }

        /// <summary> Path of the platform's null device, for -f null targets and output redirection. </summary>
        public static string NullDevice { get { return IsWindows ? "NUL" : "/dev/null"; } }

        /// <summary>
        /// Pipes stderr into a line filter that keeps lines containing any of the given literals
        /// (findstr on Windows, grep -F elsewhere).
        /// </summary>
        public static string GrepStderr(params string[] literals)
        {
            if (IsWindows)
                return $"2>&1 1>{NullDevice} | findstr /L /C:\"{string.Join("\" /C:\"", literals)}\"";

            string patterns = string.Join(" ", literals.Select(l => $"-e \"{l}\""));
            return $"2>&1 1>{NullDevice} | grep -F {patterns}";
        }

        /// <summary>
        /// Wraps a command so it is executed by the platform interpreter.
        /// On POSIX the whole command must be a single argument, so it gets quoted.
        /// </summary>
        public static string BuildArguments(string command, bool stayOpen = false)
        {
            if (IsWindows)
                return $"{RunFlag(stayOpen)} {command}";

            return $"{RunFlag(stayOpen)} \"{command.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
        }

        /// <summary> Opens a file, folder or URL with the OS default handler. </summary>
        public static void OpenWithDefaultHandler(string target)
        {
            try
            {
                if (IsWindows)
                {
                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    Process.Start("open", new[] { target });
                }
                else
                {
                    Process.Start("xdg-open", new[] { target });
                }
            }
            catch (Exception e)
            {
                IO.Logger.Log($"Failed to open '{target}': {e.Message}", true);
            }
        }

        /// <summary> Finds an executable in the given directories, falling back to PATH. </summary>
        public static string ResolveExecutable(string name, IEnumerable<string> searchDirs)
        {
            string[] exts = IsWindows ? new[] { ".exe", ".bat", ".cmd", "" } : new[] { "", ".sh" };

            foreach (string dir in searchDirs.Where(d => !string.IsNullOrWhiteSpace(d)))
            {
                foreach (string ext in exts)
                {
                    string candidate = Path.Combine(dir, name + ext);

                    if (File.Exists(candidate))
                        return candidate;
                }
            }

            return name; // Let the OS resolve it through PATH
        }
    }
}
