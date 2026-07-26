using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.OS;
using Nmkoder.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Nmkoder.Media
{
    class AvProcess
    {
        public static string lastTempDirAv1an;

        public enum LogMode { Visible, OnlyLastLine, Hidden }

        #region FFmpeg

        public class FfmpegSettings
        {
            public string Args { get; set; } = "";
            public string WorkingDir { get; set; } = "";
            public LogMode LoggingMode { get; set; } = LogMode.Hidden;
            public string LogLevel { get; set; } = "warning";
            public bool ReliableOutput { get; set; } = false;
            public bool SetBusy { get; set;} = false;
            public bool ProgressBar { get; set; } = false;
            public NmkoderProcess.ProcessType ProcessType { get; set; } = NmkoderProcess.ProcessType.Primary;
        }

        public static async Task<string> RunFfmpeg(FfmpegSettings settings)
        {
            bool show = Config.GetInt(Config.Key.CmdDebugMode) > 0;
            string processOutput = "";
            Process ffmpeg = OsUtils.NewProcess(!show, settings.ProcessType);
            NmkdStopwatch timeSinceLastOutput = new NmkdStopwatch();

            string beforeArgs = $"-hide_banner -stats -loglevel {settings.LogLevel} -y";

            string wd = Shell.ChangeDir(settings.WorkingDir);
            ffmpeg.StartInfo.Arguments = Shell.BuildArguments($"{wd} ffmpeg {beforeArgs} {settings.Args}", StayOpen());
            ffmpeg.StartInfo.EnvironmentVariables["PATH"] = OsUtils.GetPathVar(new[] { Paths.GetBinPath() });

            if (settings.LoggingMode != LogMode.Hidden) Logger.Log("Running FFmpeg...", false);
            Logger.Log($"ffmpeg {beforeArgs} {settings.Args}", true, false, "ffmpeg");

            if (!show)
            {
                string[] ignore = GetIgnoreStringsFromFfmpegCmd(settings.Args);
                ffmpeg.OutputDataReceived += (sender, outLine) => { FfmpegOutputHandler.LogOutput(outLine.Data, ignore, ref processOutput, "ffmpeg", settings.LoggingMode, settings.ProgressBar); timeSinceLastOutput.sw.Restart(); };
                ffmpeg.ErrorDataReceived += (sender, outLine) => { FfmpegOutputHandler.LogOutput(outLine.Data, ignore, ref processOutput, "ffmpeg", settings.LoggingMode, settings.ProgressBar); timeSinceLastOutput.sw.Restart(); };
            }

            if (settings.SetBusy) Program.MainWin?.SetWorking(true);
            ffmpeg.Start();
            ffmpeg.PriorityClass = ProcessPriorityClass.BelowNormal;

            if (!show)
            {
                ffmpeg.BeginOutputReadLine();
                ffmpeg.BeginErrorReadLine();
            }

            while (!ffmpeg.HasExited) await Task.Delay(10);
            while (settings.ReliableOutput && timeSinceLastOutput.ElapsedMs < 200) await Task.Delay(50);

            if (settings.SetBusy) Program.MainWin?.SetWorking(false);

            if (settings.ProgressBar)
                Program.MainWin?.SetProgress(0);

            return processOutput;
        }

        private static string[] GetIgnoreStringsFromFfmpegCmd(string cmd)
        {
            List<string> paths = new List<string>();

            // Extracting the input file path after "-i"
            int indexOfInputFlag = cmd.IndexOf(" -i ");
            if (indexOfInputFlag != -1)
            {
                string afterInputFlag = cmd.Substring(indexOfInputFlag + 4);
                int indexOfStartQuote = afterInputFlag.IndexOf("\"");
                if (indexOfStartQuote != -1)
                {
                    int indexOfEndQuote = afterInputFlag.IndexOf("\"", indexOfStartQuote + 1);
                    if (indexOfEndQuote != -1)
                    {
                        string inputFilePath = afterInputFlag.Substring(indexOfStartQuote + 1, indexOfEndQuote - indexOfStartQuote - 1).Trim();
                        paths.Add(inputFilePath);
                    }
                }
            }

            // Extracting the last quoted string, likely an output file path
            int lastIndexOfQuote = cmd.LastIndexOf("\"");
            if (lastIndexOfQuote > 0)
            {
                int secondLastIndexOfQuote = cmd.LastIndexOf("\"", lastIndexOfQuote - 1);
                if (secondLastIndexOfQuote != -1)
                {
                    string outputFilePath = cmd.Substring(secondLastIndexOfQuote + 1, lastIndexOfQuote - secondLastIndexOfQuote - 1).Trim();
                    paths.Add(outputFilePath);
                }
            }

            return paths.ToArray();
        }

        public class FfprobeSettings
        {
            public string Args { get; set; } = "";
            public LogMode LoggingMode { get; set; } = LogMode.Hidden;
            public string LogLevel { get; set; } = "panic";
            public bool SetBusy { get; set; } = false;
            public NmkoderProcess.ProcessType ProcessType { get; set; } = NmkoderProcess.ProcessType.Background;
        }

        public static async Task<string> RunFfprobe(FfprobeSettings settings)
        {
            bool show = Config.GetInt(Config.Key.CmdDebugMode) > 0;
            string processOutput = "";
            Process ffprobe = OsUtils.NewProcess(!show, settings.ProcessType);
            NmkdStopwatch timeSinceLastOutput = new NmkdStopwatch();

            ffprobe.StartInfo.Arguments = Shell.BuildArguments($"ffprobe -v {settings.LogLevel} {settings.Args}", StayOpen());
            ffprobe.StartInfo.EnvironmentVariables["PATH"] = OsUtils.GetPathVar(new[] { Paths.GetBinPath() });

            if (settings.LoggingMode != LogMode.Hidden) Logger.Log("Running FFprobe...", false);
            Logger.Log($"ffprobe -v {settings.LogLevel} {settings.Args}", true, false, "ffmpeg");

            if (!show)
            {
                string[] ignore = new string[0];
                ffprobe.OutputDataReceived += (sender, outLine) => { FfmpegOutputHandler.LogOutput(outLine.Data, ignore, ref processOutput, "ffmpeg", settings.LoggingMode, false); timeSinceLastOutput.sw.Restart(); };
                ffprobe.ErrorDataReceived += (sender, outLine) => { FfmpegOutputHandler.LogOutput(outLine.Data, ignore, ref processOutput, "ffmpeg", settings.LoggingMode, false); timeSinceLastOutput.sw.Restart(); };
            }

            ffprobe.Start();
            ffprobe.PriorityClass = ProcessPriorityClass.BelowNormal;

            if (!show)
            {
                ffprobe.BeginOutputReadLine();
                ffprobe.BeginErrorReadLine();
            }

            while (!ffprobe.HasExited) await Task.Delay(10);
            while (timeSinceLastOutput.ElapsedMs < 200) await Task.Delay(50);

            return processOutput;
        }

        #endregion

        #region av1an

        public static async Task RunAv1an(string args, LogMode logMode, bool progressBar = false)
        {
            await RunAv1an(args, "", logMode, progressBar);
        }

        public static async Task RunAv1an(string args, string workingDir, LogMode logMode, bool progressBar = false)
        {
            try
            {
                string dir = Path.Combine(Paths.GetBinPath(), "av1an");
                bool show = Config.GetBool(Config.Key.Av1anCmdVisible, true); // = Config.GetInt(Config.Key.cmdDebugMode) > 0;

                string vsynthPath = Path.Combine(dir, "vsynth");
                string encPath = Path.Combine(dir, "enc");
                string ffmpegPath = Paths.GetBinPath();
                string[] toolDirs = new[] { dir, encPath, vsynthPath, ffmpegPath };

                string missing = GetMissingTool(args, toolDirs);

                if (missing.IsNotEmpty())
                {
                    RunTask.Cancel($"{missing} was not found.\n\nIt is neither bundled with this build nor on your PATH. " +
                        $"Put it in '{encPath}' or install it, then try again.");
                    return;
                }

                // Launched without an interpreter where possible: a command line handed to cmd or sh
                // gets %VAR%, $var and backticks in file names expanded before av1an ever sees them.
                Process av1an = OsUtils.NewProcess(!show, NmkoderProcess.ProcessType.Primary, show ? null : GetToolPath("av1an", toolDirs));
                av1an.StartInfo.EnvironmentVariables["PATH"] = OsUtils.GetPathVar(new[] { vsynthPath, encPath, ffmpegPath });

                if (!show)
                {
                    av1an.StartInfo.WorkingDirectory = Directory.Exists(dir) ? dir : Paths.GetBinPath();
                    av1an.StartInfo.Arguments = args; // Parsed by .NET into argv, so no shell gets to touch it
                }
                else
                {
                    string scriptPath = WriteLaunchScript(dir, new string[] { vsynthPath, encPath, ffmpegPath }, args);
                    av1an.StartInfo.Arguments = Shell.BuildArguments(scriptPath.Wrap());
                }

                if (logMode != LogMode.Hidden) Logger.Log("Running av1an...", false);

                if (!show)
                {
                    av1an.OutputDataReceived += (sender, outLine) => { Av1anOutputHandler.LogOutput(outLine.Data, "av1an", logMode, progressBar); };
                    av1an.ErrorDataReceived += (sender, outLine) => { Av1anOutputHandler.LogOutput(outLine.Data, "av1an", logMode, progressBar); };
                }

                Logger.Log($"cmd {av1an.StartInfo.Arguments}", true, false, "av1an");

                if (progressBar)
                    Av1anOutputHandler.StartProgressLoop(); // av1an reports chunk progress through its log file, not stdout

                av1an.Start();
                av1an.PriorityClass = ProcessPriorityClass.BelowNormal;

                if (!show)
                {
                    av1an.BeginOutputReadLine();
                    av1an.BeginErrorReadLine();
                }

                while (!av1an.HasExited)
                    await Task.Delay(10);

                Av1anOutputHandler.StopProgressLoop(); // Before resetting the bar, so the loop can't write to it again

                if (progressBar)
                    Program.MainWin?.SetProgress(0);
            }
            catch (Exception e)
            {
                Logger.Log($"{e.Message}");
            }
            finally
            {
                Av1anOutputHandler.StopProgressLoop();
            }
        }

        /// <summary> av1an's -e values mapped to the executable it expects to find on PATH. </summary>
        private static readonly Dictionary<string, string> av1anEncoderBinaries = new Dictionary<string, string>
        {
            { "aom", "aomenc" }, { "svt-av1", "SvtAv1EncApp" }, { "vpx", "vpxenc" },
            { "x265", "x265" }, { "x264", "x264" }, { "rav1e", "rav1e" },
        };

        /// <summary>
        /// Names the first tool the command needs but cannot find, or "" if everything is present.
        /// Bundling is best-effort - av1an itself or an encoder can be missing on any platform, and
        /// finding that out from av1an's own output is considerably less clear than saying so first.
        /// </summary>
        private static string GetMissingTool(string args, string[] searchDirs)
        {
            if (GetToolPath("av1an", searchDirs).IsEmpty())
                return "av1an";

            string encoder = args.Contains(" -e ") ? args.Split(" -e ")[1].Trim().Split(' ').FirstOrDefault() : "";

            if (encoder.IsNotEmpty() && av1anEncoderBinaries.TryGetValue(encoder, out string binary) && GetToolPath(binary, searchDirs).IsEmpty())
                return binary;

            return "";
        }

        /// <summary>
        /// Full path of a bundled or installed tool, or "" if it is nowhere to be found. Shell.ResolveExecutable
        /// hands back the bare name when it finds nothing, which is what the launcher wants but tells a caller
        /// asking "is this present?" nothing - hence the File.Exists.
        /// </summary>
        private static string GetToolPath(string name, string[] searchDirs)
        {
            IEnumerable<string> dirs = searchDirs.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Shell.PathSeparator));
            string resolved = Shell.ResolveExecutable(name, dirs);
            return File.Exists(resolved) ? resolved : "";
        }

        /// <summary>
        /// When the av1an console is visible we launch it through a script so the window keeps
        /// the right title and stays around briefly after finishing.
        /// </summary>
        private static string WriteLaunchScript(string workingDir, string[] paths, string av1anArgs)
        {
            Logger.Log($"Writing launch script for: av1an {av1anArgs}", true, false, "av1an");
            char sep = Shell.PathSeparator;
            List<string> lines;
            string path;

            // Everything the interpreter should treat as data rather than as something to expand.
            // The PATH lines below are deliberately left alone - they need their own expansion.
            string safeArgs = Shell.EscapeExpansions(av1anArgs);
            string safeDir = Shell.EscapeExpansions(workingDir);

            if (Shell.IsWindows)
            {
                lines = new List<string>
                {
                    "@echo off",
                    $"CD /D {safeDir.Wrap()}",
                    $"SET PATH={string.Join(sep.ToString(), paths)}{sep}%PATH%",
                    "TITLE av1an",
                    $"av1an {safeArgs}",
                    "TIMEOUT /T 5"
                };
                path = Path.Combine(Paths.GetSessionDataPath(), "av1an.bat");
            }
            else
            {
                lines = new List<string>
                {
                    "#!/bin/sh",
                    $"cd {safeDir.Wrap()}",
                    $"export PATH=\"{string.Join(sep.ToString(), paths)}{sep}$PATH\"",
                    $"av1an {safeArgs}",
                    "sleep 5"
                };
                path = Path.Combine(Paths.GetSessionDataPath(), "av1an.sh");
            }

            File.WriteAllLines(path, lines);

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            return path;
        }

        #endregion

        #region MkvToolNix

        public static async Task<string> RunMkvExtract(string args, NmkoderProcess.ProcessType processType)
        {
            bool show = Config.GetInt(Config.Key.CmdDebugMode) > 0;
            string processOutput = "";

            try
            {
                Process mkve = OsUtils.NewProcess(!show, processType);

                mkve.StartInfo.Arguments = Shell.BuildArguments($"mkvextract {args}", StayOpen());
                mkve.StartInfo.EnvironmentVariables["PATH"] = OsUtils.GetPathVar(new[] { Paths.GetBinPath() });

                Logger.Log($"mkvextract {args}", true, false, "mkvextract");

                mkve.OutputDataReceived += (sender, outLine) => { processOutput += Environment.NewLine + outLine.Data; Logger.Log($"[mkvextract] {outLine.Data}", true, false, "mkvextract"); };
                mkve.ErrorDataReceived += (sender, outLine) => { processOutput += Environment.NewLine + outLine.Data; };

                mkve.Start();
                mkve.PriorityClass = ProcessPriorityClass.BelowNormal;
                mkve.BeginOutputReadLine();
                mkve.BeginErrorReadLine();

                while (!mkve.HasExited) await Task.Delay(10);
            }
            catch(Exception e)
            {
                Logger.Log($"Error running MkvExtract: {e.Message}");
            }

            return processOutput;
        }

        public static async Task<string> RunMkvMerge(string args, NmkoderProcess.ProcessType processType, bool log = false, string workingDir = null)
        {
            bool show = Config.GetInt(Config.Key.CmdDebugMode) > 0;
            string processOutput = "";

            try
            {
                Process mkvm = OsUtils.NewProcess(!show, processType);
                mkvm.StartInfo.Arguments = Shell.BuildArguments($"{Shell.ChangeDir(workingDir)} mkvmerge {args}", StayOpen());
                mkvm.StartInfo.EnvironmentVariables["PATH"] = OsUtils.GetPathVar(new[] { Paths.GetBinPath() });
                Logger.Log($"mkvmerge {args}", true, false, "mkvmerge");

                mkvm.OutputDataReceived += (sender, outLine) => {
                    string s = (outLine != null && outLine.Data != null) ? outLine.Data : "";
                    processOutput += Environment.NewLine + s;
                    Logger.Log($"[mkvmerge] {s}", !log || s.Trim().Length < 1, Logger.LastUiLine.Trim().EndsWith("%"), "mkvmerge");
                };

                mkvm.ErrorDataReceived += (sender, outLine) => {
                    string s = (outLine != null && outLine.Data != null) ? outLine.Data : "";
                    processOutput += Environment.NewLine + s;
                    Logger.Log($"[mkvmerge] [E] {s}", !log || s.Trim().Length < 1, false, "mkvmerge");
                };

                mkvm.Start();
                mkvm.PriorityClass = ProcessPriorityClass.BelowNormal;
                mkvm.BeginOutputReadLine();
                mkvm.BeginErrorReadLine();

                while (!mkvm.HasExited) await Task.Delay(10);
            }
            catch (Exception e)
            {
                Logger.Log($"Error running MkvMerge: {e.Message}");
            }

            return processOutput;
        }

        public static async Task<string> RunMkvInfo(string args, NmkoderProcess.ProcessType processType, bool log = false)
        {
            bool show = Config.GetInt(Config.Key.CmdDebugMode) > 0;
            string processOutput = "";
            NmkdStopwatch timeSinceLastOutput = new NmkdStopwatch();

            try
            {
                Process mkvi = OsUtils.NewProcess(!show, processType);
                mkvi.StartInfo.Arguments = Shell.BuildArguments($"mkvinfo {args}", StayOpen());
                mkvi.StartInfo.EnvironmentVariables["PATH"] = OsUtils.GetPathVar(new[] { Paths.GetBinPath() });

                Logger.Log($"mkvinfo {args}", true, false, "mkvinfo");

                mkvi.OutputDataReceived += (sender, outLine) => { processOutput += Environment.NewLine + outLine.Data; if(log) Logger.Log($"[mkvinfo] {outLine.Data}", true, false, "ocr"); timeSinceLastOutput.sw.Restart(); };
                mkvi.ErrorDataReceived += (sender, outLine) => { processOutput += Environment.NewLine + outLine.Data; timeSinceLastOutput.sw.Restart(); };

                mkvi.Start();
                mkvi.PriorityClass = ProcessPriorityClass.BelowNormal;
                mkvi.BeginOutputReadLine();
                mkvi.BeginErrorReadLine();

                while (!mkvi.HasExited) await Task.Delay(10);
                while (timeSinceLastOutput.ElapsedMs < 200) await Task.Delay(50);
            }
            catch (Exception e)
            {
                Logger.Log($"Error running MkvInfo: {e.Message}");
            }

            return processOutput;
        }

        #endregion

        public static bool StayOpen()
        {
            return Config.GetInt(Config.Key.CmdDebugMode) == 2;
        }

        public static string GetCmdArg()
        {
            return Shell.RunFlag(StayOpen());
        }
    }
}
