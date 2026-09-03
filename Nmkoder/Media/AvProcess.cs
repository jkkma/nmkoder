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

            /// <summary>
            /// A command and a trailing "| " to put in front of ffmpeg, so it reads its video from
            /// another process's stdout instead of decoding it itself. That is how QTGMC's frames get
            /// in: VSPipe evaluates a VapourSynth script and ffmpeg takes the result as an input.
            /// <para/>
            /// Deliberately a prefix rather than a suffix - see <see cref="TryReadExitCode"/>: the
            /// shell reports the *last* command's status, so ffmpeg has to stay last for a failed
            /// encode to still read as one.
            /// </summary>
            public string PipeFrom { get; set; } = "";

            /// <summary> Directories to put on the process's PATH beyond the app's own bin folder -
            /// the portable VapourSynth, when something in the command line needs it. </summary>
            public string[] ExtraPathDirs { get; set; } = new string[0];

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
            /// and best-effort runs whose callers already cope with getting nothing back - and for
            /// callers that would rather report it themselves, which is what <see cref="Problem"/>
            /// is for. </summary>
            public bool ReportFailure { get; set; } = false;

            /// <summary> Why this run failed, worded as the user would be told, or "" when it did
            /// not. Written either way, so a caller holding a better explanation than ffmpeg's can
            /// choose between the two - see the VapourSynth pipe, where an input that simply ended
            /// is all ffmpeg can see of a script that died. </summary>
            public string Problem { get; set; } = "";

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
            ffmpeg.StartInfo.Arguments = Shell.BuildArguments($"{wd} {settings.PipeFrom}ffmpeg {beforeArgs} {settings.Args}", StayOpen());
            OsUtils.SetPathVar(ffmpeg, new[] { Paths.GetBinPath() }.Concat(settings.ExtraPathDirs));

            if (settings.LoggingMode != LogMode.Hidden) Logger.Log("Running FFmpeg...", false);
            Logger.Log($"{settings.PipeFrom}ffmpeg {beforeArgs} {settings.Args}", true, false, "ffmpeg");

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

            settings.Problem = GetFfmpegProblem(settings, errors);

            if (settings.ReportFailure && settings.Problem.IsNotEmpty())
                RunTask.Fail(settings.Problem);

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
        /// which does not come through here. A pipe in *front* of ffmpeg is fine for the same reason,
        /// and that is what <see cref="FfmpegSettings.PipeFrom"/> builds - but it is also why a
        /// failing VSPipe cannot be noticed here, since ffmpeg reads its early end-of-stream as the
        /// end of the video and finishes cleanly. <see cref="Qtgmc.ReadRunProblem"/> covers that.
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
        /// Why an ffmpeg run failed, or "" if it did not. The exit code is the authority; the output
        /// only supplies the explanation, and a run that exited cleanly can still have failed on one
        /// of the few messages that mean the output is worthless whatever the status said.
        /// </summary>
        private static string GetFfmpegProblem(FfmpegSettings settings, FfmpegOutputHandler.RunErrors errors)
        {
            if (RunTask.canceled) // Killing the process is what made it exit non-zero
                return "";

            string evidence = errors.Evidence;
            string suspect = errors.Suspect;

            if (evidence.IsNotEmpty())
                return $"FFmpeg did not produce a usable result:\n\n{evidence}";

            // Anything non-zero is a failure, negatives included: a Windows crash code such as
            // 0xC0000005 arrives here as a negative int, and a run that could not be judged at all
            // says so through ExitCodeKnown rather than by borrowing a value.
            if (!settings.ExitCodeKnown || settings.ExitCode == 0)
                return "";

            string why = suspect.IsNotEmpty() ? $"\n\nIt reported:\n{suspect}" : "\n\nThe log has its output.";
            return $"FFmpeg exited with code {settings.ExitCode}.{why}";
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

                        // Only a non-empty extraction: String.Replace throws outright on "", and on
                        // Linux and macOS the paths are single-quoted, so what this scan finds is
                        // whatever double-quoted value comes last - the metadata grid writes
                        // 'language=""' for a track with none, and that empty pair crashed the output
                        // reader mid-encode.
                        if (inputFilePath.Length > 0)
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

                    // As above: an empty extraction is not a path, and Replace("") throws.
                    if (outputFilePath.Length > 0)
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
                    // mkvmerge is not an encoder and does not belong in enc/, and it is the one tool
                    // here a package manager is the normal way to get - so it is worth naming the
                    // package, and worth saying the setting can be changed instead. Not for H.265,
                    // where av1an has no other way to join its chunks.
                    string advice = missing == "mkvmerge"
                        ? $"{MkvToolNixInstallAdvice()} Or pick a different Concat Method on the Av1an Options tab. H.265 can only be concatenated by mkvmerge, so for that one the install is the only way."
                        : $"Put it in '{encPath}' or install it, then try again.";

                    RunTask.Cancel($"{missing} was not found.\n\nIt is neither bundled with this build nor on your PATH. {advice}");
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
                    // av1an's own folder goes on the script's PATH as well as being its working
                    // directory. The script names the binary bare, and cmd only resolves a bare
                    // name from the current directory while NoDefaultCurrentDirectoryInExePath is
                    // unset - a documented Windows setting some hardening guides recommend, and one
                    // a parent process can hand down. With it set, every visible-console encode died
                    // at startup with "'av1an' is not recognized" (exit code 9009) while the piped
                    // mode above, which launches av1an by full path, ran fine. Measured by running
                    // the launch script with the variable set and unset.
                    string scriptPath = WriteLaunchScript(dir, new string[] { dir, vsynthPath, encPath, ffmpegPath }, args);
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

            return await ToolKnowsFlagOrIsUnknown(binary, flag);
        }

        /// <summary>
        /// The same question as <see cref="EncoderKnowsFlagOrIsUnknown"/>, asked of a binary by its own
        /// name rather than by the av1an encoder that would invoke it - which is what Quick Convert
        /// needs, launching the encoders itself and knowing them as <c>IBinaryEncoder.ToolName</c>.
        /// Same contract: false only where a help text was actually read and did not hold the flag.
        /// </summary>
        public static async Task<bool> ToolKnowsFlagOrIsUnknown(string binary, string flag)
        {
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

            // av1an joins its chunks back up with mkvmerge unless told otherwise, and for H.265 it is
            // not a preference - Av1anUi forces it, ffmpeg having no way to join raw HEVC chunks. But
            // bundle-tools.sh ships MKVToolNix for win-x64 alone, so on Linux and macOS this is
            // routinely absent, and without asking here the encode runs every chunk to completion -
            // which is the hours - and only then dies in av1an's concat step, reported as whatever
            // av1an says about it rather than as a missing package.
            //
            // Read off the command rather than the dropdown, because the dropdown is not the last
            // word on it: the MP4 override and the H.265 forcing have both already been applied by
            // the time these arguments exist, so this is what av1an is actually being told. The
            // concat flag is emitted ahead of the custom-argument box and the encoder's own quoted
            // arguments, so the first occurrence is the real one, and " -c:a " in the audio arguments
            // does not match for want of the trailing space.
            string concat = args.Contains(" -c ") ? args.Split(" -c ")[1].Trim().Split(' ').FirstOrDefault() : "";

            if (concat == "mkvmerge" && GetToolPath("mkvmerge", searchDirs).IsEmpty())
                return "mkvmerge";

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
        /// Whether a tool is present, in bin/ or on the user's PATH.
        /// <para/>
        /// Worth asking before running one, because a missing binary is not a failure any caller here
        /// can see: the command goes through a shell, which writes "command not found" to stderr and
        /// exits, so the utility finds out only by noticing the file it wanted was never written - and
        /// then says whatever it says about that instead. <c>bundle-tools.sh</c> ships MKVToolNix for
        /// win-x64 alone, so mkvmerge, mkvextract and mkvinfo are routinely absent on Linux and macOS
        /// and this is the difference between naming the missing package and reporting a mystery.
        /// </summary>
        public static bool IsToolAvailable(string name, params string[] extraDirs)
        {
            // Searched over the PATH the tool will be launched with, not the one this process holds.
            // Every runner here goes through OsUtils.SetPathVar, and on Windows that keeps bin/ and
            // C:\Windows and drops the rest - so checking the full PATH would vouch for an mkvmerge
            // installed in Program Files that the launcher then cannot resolve, leaving exactly the
            // unexplained failure this check exists to replace.
            //
            // extraDirs is for a caller that will *add* directories to that PATH itself. Quick Convert's
            // encoder binaries live in bin/av1an/enc, which nothing puts on the default PATH: asked
            // without it, this answers "missing" for a perfectly well bundled SvtAv1EncApp. Whatever a
            // caller passes here it must also pass to the runner, or the two disagree in the direction
            // that is worst - vouching for a binary the launch then cannot resolve.
            string[] roots = new[] { Paths.GetBinPath() }.Concat(extraDirs ?? new string[0]).ToArray();
            IEnumerable<string> dirs = OsUtils.GetPathVar(roots).Split(Shell.PathSeparator).Where(d => d.IsNotEmpty());
            return File.Exists(Shell.ResolveExecutable(name, dirs));
        }

        /// <summary>
        /// How to get MKVToolNix, worded once for every caller that finds it missing.
        /// <para/>
        /// Four messages said this in three different phrasings, so the day a package is renamed - or
        /// the day <c>bundle-tools.sh</c> stops shipping it for win-x64 alone - four strings in four
        /// files would have had to be found. Each caller appends its own "or do X instead" clause,
        /// which is the only part that differs between them.
        /// </summary>
        public static string MkvToolNixInstallAdvice()
        {
            return "Install MKVToolNix - 'apt install mkvtoolnix' on Linux, 'brew install mkvtoolnix' on macOS. " +
                "It ships with the Windows build.";
        }

        /// <summary>
        /// The full path of a tool, resolved over the same directories <see cref="IsToolAvailable"/>
        /// searches - so what the check vouched for is the file that runs, not merely a name the
        /// shell's PATH is trusted to agree about. That matters twice for the encoder pipes: a shell
        /// command names its tools mid-line, where the console debug modes run through UseShellExecute
        /// and <see cref="OsUtils.SetPathVar"/> therefore cannot put bin/ on the PATH at all; and a
        /// user's own PATH may hold a second copy of the same binary that the availability check never
        /// looked at. Returns the bare name when nothing is found, like
        /// <see cref="Shell.ResolveExecutable"/> - callers gate on IsToolAvailable first.
        /// </summary>
        public static string ResolveToolPath(string name, params string[] extraDirs)
        {
            string[] roots = new[] { Paths.GetBinPath() }.Concat(extraDirs ?? new string[0]).ToArray();
            IEnumerable<string> dirs = OsUtils.GetPathVar(roots).Split(Shell.PathSeparator).Where(d => d.IsNotEmpty());
            return Shell.ResolveExecutable(name, dirs);
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
