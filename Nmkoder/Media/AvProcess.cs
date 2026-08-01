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

            /// <summary>
            /// ffmpeg's own exit status, written by <see cref="RunFfmpeg"/> when it returns, or -1
            /// when it could not be established. Read it rather than searching the output for words
            /// that look like errors: ffmpeg says "Error submitting packet to decoder" a hundred and
            /// thirty times while encoding a damaged file perfectly, and says nothing recognisable at
            /// all for some of the failures that matter.
            /// <para/>
            /// It is the shell's status, which for both `cmd /C` and `sh -c` is the last command's -
            /// so ffmpeg's, including for the two-pass `&amp;&amp; ` chain, where a first-pass failure
            /// stops the second from running and hands its own code back.
            /// </summary>
            public int ExitCode { get; set; } = -1;

            /// <summary> Whether a non-zero exit is a failure worth reporting. False for the probes
            /// and best-effort runs whose callers already cope with getting nothing back. </summary>
            public bool ReportFailure { get; set; } = false;

            /// <summary> Whether <see cref="ExitCode"/> means anything. A run whose status could not
            /// be established leaves this false, which is not the same as exiting 0 - and cannot be
            /// expressed by a sentinel value, since -1 and other negatives are real exit codes on
            /// Windows. </summary>
            public bool ExitCodeKnown { get; set; } = false;

            /// <summary> Whether a fatal line in this run's output may stop the running task. False
            /// for the auxiliary runs - thumbnails, frame extraction, capability probes - which are
            /// not the task and have no business killing it over what they printed. </summary>
            public bool CanCancelTask { get; set; } = true;
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
            OsUtils.SetPathVar(ffmpeg, new[] { Paths.GetBinPath() });

            if (settings.LoggingMode != LogMode.Hidden) Logger.Log("Running FFmpeg...", false);
            Logger.Log($"ffmpeg {beforeArgs} {settings.Args}", true, false, "ffmpeg");

            // One per run rather than one shared: a thumbnail extraction started on a background
            // task while this encode runs would otherwise contribute its output to this run's verdict.
            var errors = new FfmpegOutputHandler.RunErrors();

            if (!show)
            {
                string[] ignore = GetIgnoreStringsFromFfmpegCmd(settings.Args);
                ffmpeg.OutputDataReceived += (sender, outLine) => { FfmpegOutputHandler.LogOutput(outLine.Data, ignore, ref processOutput, "ffmpeg", settings.LoggingMode, settings.ProgressBar, settings.CanCancelTask, errors); timeSinceLastOutput.sw.Restart(); };
                ffmpeg.ErrorDataReceived += (sender, outLine) => { FfmpegOutputHandler.LogOutput(outLine.Data, ignore, ref processOutput, "ffmpeg", settings.LoggingMode, settings.ProgressBar, settings.CanCancelTask, errors); timeSinceLastOutput.sw.Restart(); };
            }

            if (settings.SetBusy) Program.MainWin?.SetWorking(true);
            if (settings.ProgressBar) FfmpegOutputHandler.ResetProgressTracking();
            ffmpeg.Start();
            ffmpeg.PriorityClass = ProcessPriorityClass.BelowNormal;

            if (!show)
            {
                ffmpeg.BeginOutputReadLine();
                ffmpeg.BeginErrorReadLine();
            }

            while (!ffmpeg.HasExited) await Task.Delay(10);

            // HasExited only says the process is gone; the redirected readers can still have lines in
            // flight, and those last lines are exactly the ones that say why a run failed. The
            // parameterless wait is the documented way to be sure they have all been delivered.
            if (!show)
            {
                try { await ffmpeg.WaitForExitAsync(); }
                catch (Exception e) { Logger.Log($"Waiting for FFmpeg's output to drain failed: {e.Message}", true, level: Logger.Level.Debug); }
            }

            while (settings.ReliableOutput && timeSinceLastOutput.ElapsedMs < 200) await Task.Delay(50);

            settings.ExitCodeKnown = TryReadExitCode(ffmpeg, out int exitCode);
            settings.ExitCode = exitCode;

            if (settings.SetBusy) Program.MainWin?.SetWorking(false);

            if (settings.ProgressBar)
                Program.MainWin?.SetProgress(0);

            if (settings.ReportFailure)
                ReportFfmpegOutcome(settings, errors);

            return processOutput;
        }

        /// <summary>
        /// A finished process's exit status, or -1 when it cannot be trusted.
        /// <para/>
        /// The one mode where it cannot is Windows with the console debug setting on "keep open",
        /// which turns the wrapper into `cmd /K`: that shell outlives the tool and whatever it
        /// eventually exits with is its own. (In practice the wait loop above never gets past such a
        /// shell either, but a check that quietly reports someone else's status is worse than one
        /// that says it does not know.) Elsewhere `cmd /C` and `sh -c` both exit with the status of
        /// the last command they ran, which is the tool's - including across the two-pass `&amp;&amp; `
        /// chain, where a failing first pass stops the second and hands back its own code.
        /// <para/>
        /// "The last command" is why nothing run through here may end in a pipe. A trailing `| grep`,
        /// which <see cref="Shell.GrepStderr"/> builds, would make grep the last command and its
        /// status the one read - so a failed tool whose output grep happened to match would come back
        /// a success. Nothing does today: GrepStderr is only ever used by FfmpegUtils' own launcher,
        /// which does not come through here.
        /// </summary>
        private static bool TryReadExitCode(Process p, out int exitCode)
        {
            exitCode = 0;

            if (Shell.IsWindows && StayOpen())
                return false;

            try
            {
                exitCode = p.ExitCode;
                return true;
            }
            catch (Exception e)
            {
                Logger.Log($"Could not read the exit code: {e.Message}", true, level: Logger.Level.Debug);
                return false;
            }
        }

        /// <summary>
        /// Turns a bad ffmpeg run into a reported failure. The exit code is the authority; the output
        /// only supplies the explanation, and a run that exited cleanly can still be failed by one of
        /// the few messages that mean the output is worthless whatever the status said.
        /// </summary>
        private static void ReportFfmpegOutcome(FfmpegSettings settings, FfmpegOutputHandler.RunErrors errors)
        {
            if (RunTask.canceled) // Killing the process is what made it exit non-zero
                return;

            string evidence = errors.Evidence;
            string suspect = errors.Suspect;

            if (evidence.IsNotEmpty())
            {
                RunTask.Fail($"FFmpeg did not produce a usable result:\n\n{evidence}");
                return;
            }

            // Anything non-zero is a failure, negatives included: a Windows crash code such as
            // 0xC0000005 arrives here as a negative int, and a run that could not be judged at all
            // says so through ExitCodeKnown rather than by borrowing a value.
            if (!settings.ExitCodeKnown || settings.ExitCode == 0)
                return;

            string why = suspect.IsNotEmpty() ? $"\n\nIt reported:\n{suspect}" : "\n\nThe log has its output.";
            RunTask.Fail($"FFmpeg exited with code {settings.ExitCode}.{why}");
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
            OsUtils.SetPathVar(ffprobe, new[] { Paths.GetBinPath() });

            if (settings.LoggingMode != LogMode.Hidden) Logger.Log("Running FFprobe...", false);
            Logger.Log($"ffprobe -v {settings.LogLevel} {settings.Args}", true, false, "ffmpeg");

            if (!show)
            {
                string[] ignore = new string[0];
                // canCancelTask: false - a probe reads a file's metadata, and what it finds there is
                // never grounds for killing an encode that happens to be running.
                ffprobe.OutputDataReceived += (sender, outLine) => { FfmpegOutputHandler.LogOutput(outLine.Data, ignore, ref processOutput, "ffmpeg", settings.LoggingMode, false, false); timeSinceLastOutput.sw.Restart(); };
                ffprobe.ErrorDataReceived += (sender, outLine) => { FfmpegOutputHandler.LogOutput(outLine.Data, ignore, ref processOutput, "ffmpeg", settings.LoggingMode, false, false); timeSinceLastOutput.sw.Restart(); };
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

        /// <summary> Runs av1an and hands back its exit code, or -1 if it never got as far as running. </summary>
        public static async Task<int> RunAv1an(string args, LogMode logMode, bool progressBar = false)
        {
            return await RunAv1an(args, "", logMode, progressBar);
        }

        public static async Task<int> RunAv1an(string args, string workingDir, LogMode logMode, bool progressBar = false)
        {
            try
            {
                string dir = Path.Combine(Paths.GetBinPath(), "av1an");
                bool show = ShowAv1anConsole();

                string vsynthPath = Path.Combine(dir, "vsynth");
                string encPath = Path.Combine(dir, "enc");
                string ffmpegPath = Paths.GetBinPath();
                string[] toolDirs = new[] { dir, encPath, vsynthPath, ffmpegPath };

                string missing = GetMissingTool(args, toolDirs);

                if (missing.IsNotEmpty())
                {
                    RunTask.Cancel($"{missing} was not found.\n\nIt is neither bundled with this build nor on your PATH. " +
                        $"Put it in '{encPath}' or install it, then try again.");
                    return -1;
                }

                // Launched without an interpreter where possible: a command line handed to cmd or sh
                // gets %VAR%, $var and backticks in file names expanded before av1an ever sees them.
                Process av1an = OsUtils.NewProcess(!show, NmkoderProcess.ProcessType.Primary, show ? null : GetToolPath("av1an", toolDirs));
                OsUtils.SetPathVar(av1an, new[] { vsynthPath, encPath, ffmpegPath });

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
                    Av1anOutputHandler.StartProgressLoop(args); // av1an reports chunk progress through its log file, not stdout

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

                // In the visible-console mode this is the launch script's code, which it deliberately
                // carries over from av1an rather than from the command that keeps the window open.
                return TryGetExitCode(av1an);
            }
            catch (Exception e)
            {
                Logger.Log($"{e.Message}");
                return -1;
            }
            finally
            {
                Av1anOutputHandler.StopProgressLoop();
            }
        }

        /// <summary>
        /// A finished process's exit code, or 0 where the platform will not give one up - a shell-executed
        /// process does not always keep a handle to ask. Reporting failure there would mean never trusting
        /// an encode enough to clean up after it, so "cannot tell" is reported as "no complaint" and the
        /// caller is left to judge by what the run actually produced.
        /// </summary>
        private static int TryGetExitCode(Process p)
        {
            try
            {
                return p.ExitCode;
            }
            catch (Exception e)
            {
                Logger.Log($"Could not read the exit code of {p.StartInfo.FileName}: {e.Message}", true);
                return 0;
            }
        }

        /// <summary> Each tool's own --help, read once per session. "" if it could not be read at all. </summary>
        private static readonly Dictionary<string, string> helpTexts = new Dictionary<string, string>();

        /// <summary>
        /// Whether the av1an that would actually run understands a given flag.
        /// <para/>
        /// Worth asking rather than assuming, because av1an gains options between releases and rejects
        /// the entire command over one it does not know - so a flag that is merely newer than the
        /// installed binary does not degrade into being ignored, it stops the encode before it starts.
        /// Nothing pins which av1an is in use either: the bundled one, a packaged one and one the user
        /// dropped in themselves are all reachable through the same lookup.
        /// </summary>
        public static async Task<bool> Av1anSupportsFlag(string flag)
        {
            return (await GetToolHelp("av1an")).Contains(flag);
        }

        /// <summary>
        /// Whether av1an's --help has actually been read this session. A missing flag only means
        /// something when it is missing from a help text that exists - "" is an av1an that could
        /// not be found, run or waited out, which says nothing about what it supports.
        /// </summary>
        public static async Task<bool> Av1anHelpKnown()
        {
            return (await GetToolHelp("av1an")).IsNotEmpty();
        }

        /// <summary>
        /// Whether the encoder av1an would invoke for a codec understands a given flag - the same
        /// question Av1anSupportsFlag asks of av1an, asked of the encoder behind it.
        /// <para/>
        /// It has to be asked separately because which binary answers is a build-time accident. The
        /// bundle prefers svt-av1-hdr, which continues the PSY line, but falls back to mainline
        /// SVT-AV1 where no prebuilt exists - and on macOS it bundles nothing at all, leaving whatever
        /// Homebrew installed, which is mainline. A PSY-line parameter set against a mainline binary
        /// is not ignored: the encoder rejects the command and the encode never starts.
        /// <para/>
        /// Returns false only when a help text was actually read and the flag was not in it. An
        /// encoder that could not be found or run says nothing about what it supports, so it is given
        /// the benefit of the doubt rather than having its arguments stripped on a failed lookup.
        /// </summary>
        public static async Task<bool> EncoderKnowsFlagOrIsUnknown(string av1anEncoderName, string flag)
        {
            if (!av1anEncoderBinaries.TryGetValue(av1anEncoderName, out string binary))
                return true;

            string help = await GetToolHelp(binary);
            return help.IsEmpty() || help.Contains(flag);
        }

        private static async Task<string> GetToolHelp(string tool)
        {
            if (helpTexts.TryGetValue(tool, out string cached))
                return cached;

            // Set before the work, not after: a tool that cannot be found or cannot be run will not
            // start answering later, and retrying it before every encode only delays each one. The
            // timeout below is the one exception, undone where it happens: that tool may merely
            // still be being virus-scanned or paged in, and answers fine on the next attempt.
            helpTexts[tool] = "";

            try
            {
                string dir = Path.Combine(Paths.GetBinPath(), "av1an");
                string[] toolDirs = new[] { dir, Path.Combine(dir, "enc"), Path.Combine(dir, "vsynth"), Paths.GetBinPath() };
                string path = GetToolPath(tool, toolDirs);

                if (path.IsEmpty())
                    return helpTexts[tool];

                Process proc = OsUtils.NewProcess(true, NmkoderProcess.ProcessType.Background, path);
                OsUtils.SetPathVar(proc, toolDirs);
                proc.StartInfo.Arguments = "--help";
                proc.Start();

                // Both pipes are drained at once. Reading one to the end first deadlocks as soon as the
                // other fills its buffer, and av1an's help is long enough to do exactly that.
                Task<string> stdout = proc.StandardOutput.ReadToEndAsync();
                Task<string> stderr = proc.StandardError.ReadToEndAsync();
                Task both = Task.WhenAll(stdout, stderr);

                // Generous because the first launch of a freshly unpacked av1an.exe sits behind a
                // virus scan that alone can take longer than the help call itself.
                if (await Task.WhenAny(both, Task.Delay(15000)) != both)
                {
                    Logger.Log($"{tool} did not answer --help in time - trying again later.", true);
                    OsUtils.KillProcessTree(proc.Id);
                    helpTexts.Remove(tool);
                    return "";
                }

                helpTexts[tool] = $"{stdout.Result}\n{stderr.Result}";
            }
            catch (Exception e)
            {
                Logger.Log($"Could not read {tool}'s --help: {e.Message}", true);
            }

            return helpTexts[tool];
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
        /// Whether to run av1an in a console of its own rather than capturing its output.
        /// <para/>
        /// The setting only means anything on Windows, and honouring it elsewhere threw av1an's
        /// entire output away for nothing. Showing the console means UseShellExecute, which means no
        /// redirection - so <see cref="Av1anOutputHandler"/> never sees a line, and everything the app
        /// knows about the encode comes from the exit code and the chunk counts it tails out of
        /// av1an's log file. On Windows that is a fair trade: the console is right there to read. On
        /// Linux and macOS UseShellExecute opens no terminal emulator, so the output goes to the
        /// stdio this GUI process inherited - which is to say nowhere - and the launch script's
        /// "stay open for five seconds" pause is five seconds nobody spends looking at anything.
        /// <para/>
        /// So off-Windows the answer is always no, and av1an's diagnostics reach the log like every
        /// other tool's. The checkbox stays, because a Windows user's session settings travel.
        /// </summary>
        public static bool ShowAv1anConsole()
        {
            return Shell.IsWindows && Config.GetBool(Config.Key.Av1anCmdVisible, true);
        }

        /// <summary>
        /// When the av1an console is visible we launch it through a script so the window keeps
        /// the right title and stays around briefly after finishing. The script ends by exiting with
        /// av1an's own code rather than the pause command's, so the caller can still tell a finished
        /// encode from a failed one - which is the difference between keeping and deleting its chunks.
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
                    "SET AV1AN_EXIT_CODE=%ERRORLEVEL%",
                    "TIMEOUT /T 5",
                    "EXIT /B %AV1AN_EXIT_CODE%"
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
                    "av1an_exit_code=$?",
                    "sleep 5",
                    "exit $av1an_exit_code"
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
                OsUtils.SetPathVar(mkve, new[] { Paths.GetBinPath() });

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
                OsUtils.SetPathVar(mkvm, new[] { Paths.GetBinPath() });
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
                OsUtils.SetPathVar(mkvi, new[] { Paths.GetBinPath() });

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
