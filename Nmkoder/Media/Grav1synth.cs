using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.OS;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Nmkoder.Media
{
    /// <summary>
    /// grav1synth, which is the only thing outside an AV1 encoder that can read or write the film grain
    /// description in an AV1 bitstream.
    /// <para/>
    /// Everything here was measured against a build of the tool rather than read out of its README, which
    /// is a release behind its own source in several places. Three findings shape the code:
    /// <list type="number">
    /// <item>**Its prompts are interactive and it cannot be run without <c>-y</c>.** With the output file
    /// already there and no overwrite flag it calls <c>dialoguer::Confirm</c>, which from a process whose
    /// stdio is redirected - which is every process this app starts - fails outright with
    /// <c>Error: IO error: not a terminal</c> and exit 1. Every call built here passes <c>-y</c>.</item>
    /// <item>**Exit 0 is not success.** <c>inspect</c> on a file that carries no grain logs "No film grain
    /// headers found" and returns 0 without writing anything at all. So each call is judged by the
    /// artifact it was supposed to produce, and by the tool's own "Done, wrote…" line, rather than by its
    /// status - the same argument this project already makes about ffmpeg and <c>File.Exists</c>.</item>
    /// <item>**Its progress bar is hidden whenever stderr is not a TTY** (<c>stderr().is_tty()</c> in its
    /// main), so a redirected run prints no percentage, ever. There is nothing to parse and no point
    /// building a parser for it: <see cref="DiffAsync"/> leaves the bar indeterminate and says up front
    /// how long the run is expected to take.</item>
    /// </list>
    /// </summary>
    class Grav1synth
    {
        public const string ToolName = "grav1synth";

        /// <summary> What the tool prints on a run that produced its output file. Both the table commands
        /// and the bitstream ones end on it, with the noun changed. </summary>
        private const string DoneMarker = "Done, wrote";

        /// <summary> Whether the binary is reachable on the PATH the launcher will use - not this
        /// process's own, for the reason <see cref="AvProcess.IsToolAvailable"/> gives. </summary>
        public static bool IsAvailable()
        {
            return AvProcess.IsToolAvailable(ToolName);
        }

        /// <summary> Why a mode needing this tool cannot run without it. Named rather than left to be
        /// discovered: a missing binary goes through a shell, which writes "command not found" to a stream
        /// nothing reads, and the caller then only notices that its output was never written. </summary>
        public static string DescribeMissing()
        {
            return $"{ToolName} is not bundled with this build and is not on your PATH, so this Grain Synthesis mode " +
                $"cannot run.\n\nIt is built from source ({ToolName} publishes no binaries), which the release " +
                $"workflow does per platform - a build that skipped it says so in its own log. Encoder analysis " +
                $"needs nothing extra and works on every AV1 encoder.";
        }

        /// <summary>
        /// How fast the diff runs, in megapixels of source per second, measured on the real binary: 96
        /// frames of 320x240 in 1.11s (6.7 Mpx/s) and 48 frames of 1920x1080 in 13.05s (7.6 Mpx/s). It is
        /// single-threaded and scales with area rather than with anything else, which is what makes one
        /// number enough.
        /// <para/>
        /// The figure is worth stating out loud because of what it implies: a 100-minute 1080p film is
        /// 144,000 frames, which is **about eleven hours** of measuring before av1an is started. That is
        /// not a reason to hide the mode - it is the accurate way to get a grain table, and short sources
        /// are where most people want one - but it is every reason to say so on the row rather than let it
        /// be discovered.
        /// </summary>
        private const double DiffMegapixelsPerSecond = 7.2;

        /// <summary> How long the diff is expected to take over a given number of frames of a given size,
        /// from <see cref="DiffMegapixelsPerSecond"/>. </summary>
        public static TimeSpan EstimateDiffTime(long frames, Size frameSize)
        {
            if (frames < 1 || frameSize.Width < 1 || frameSize.Height < 1)
                return TimeSpan.Zero;

            double megapixels = (double)frameSize.Width * frameSize.Height / 1000000d * frames;
            return TimeSpan.FromSeconds(megapixels / DiffMegapixelsPerSecond);
        }

        /// <summary>
        /// Measures the grain between a source and a denoised copy of it into a table file, and returns
        /// why nothing usable came out - or "" when it did.
        /// <para/>
        /// The two files must be the same video frame for frame: this compares frame N with frame N and
        /// nothing aligns them, so a resize, a crop, a rate change or a trim on one side and not the other
        /// measures the difference between two pictures rather than the grain in one.
        /// </summary>
        public static async Task<string> DiffAsync(string sourcePath, string denoisedPath, string tablePath)
        {
            IoUtils.TryDeleteIfExists(tablePath);
            Directory.CreateDirectory(Path.GetDirectoryName(tablePath));

            string output = await Run($"diff {sourcePath.Wrap()} {denoisedPath.Wrap()} -o {tablePath.Wrap()} -y", indeterminate: true);

            if (RunTask.canceled)
                return "";

            if (!File.Exists(tablePath) || !GrainSynthConfig.LooksLikeGrainTable(tablePath))
                return $"{ToolName} did not produce a grain table.\n\n{LastLines(output)}";

            return "";
        }

        /// <summary>
        /// Writes a grain description into a finished AV1 file, from whichever of the three sources the
        /// config names. This is the delivery that needs nothing of the encoder - see
        /// <see cref="GrainDelivery.PostApply"/>.
        /// <para/>
        /// <c>--replace</c> goes out unconditionally. Without it the tool *skips* a file that already
        /// carries grain headers and still exits 0, which for a deliberate setting on an encode tab is the
        /// wrong default twice over: the encode may well have written its own grain, and a silent skip
        /// would leave the output carrying grain nobody picked.
        /// </summary>
        public static async Task<string> ApplyAsync(GrainSynthConfig config, string inPath, string outPath, string tablePath)
        {
            string source;

            switch (config.Mode)
            {
                case GrainSynthMode.Measured:
                case GrainSynthMode.Table:
                    source = $"-g {tablePath.Wrap()}";
                    break;
                case GrainSynthMode.Preset:
                    source = $"--preset {config.Preset.Wrap()}";
                    break;
                case GrainSynthMode.PhotonNoise:
                    source = $"--iso {config.Iso}{(config.Chroma ? " --chroma" : "")}";
                    break;
                default:
                    return $"{ToolName} was asked to apply grain with nothing to apply.";
            }

            IoUtils.TryDeleteIfExists(outPath);
            string output = await Run($"apply {inPath.Wrap()} -o {outPath.Wrap()} {source} --replace -y", indeterminate: true);

            if (RunTask.canceled)
                return "";

            // The tool's own success line and the file both, because neither is sufficient on its own: it
            // writes the output through an ffmpeg muxer, which creates the file before it writes a header,
            // and it reports several kinds of nothing-happened at exit 0.
            if (!output.Contains(DoneMarker) || IoUtils.GetFilesize(outPath) < 1)
                return $"{ToolName} could not write the grain into the encode.\n\n{LastLines(output)}";

            return "";
        }

        /// <summary>
        /// Reads the grain table out of an AV1 file that already carries one, and returns why it could
        /// not - or "" when it did.
        /// <para/>
        /// **A file with no grain in it is the case to get right, and it is not an error.** The tool logs
        /// "No film grain headers found--this video does not use grain synthesis", writes nothing at all,
        /// and exits 0; from here that is indistinguishable from a failure unless the message is read. So
        /// it is read, and reported as the ordinary answer it is.
        /// </summary>
        public static async Task<string> InspectAsync(string inPath, string tablePath)
        {
            IoUtils.TryDeleteIfExists(tablePath);
            Directory.CreateDirectory(Path.GetDirectoryName(tablePath));

            string output = await Run($"inspect {inPath.Wrap()} -o {tablePath.Wrap()} -y", indeterminate: true);

            if (RunTask.canceled)
                return "";

            if (output.Contains("No film grain headers"))
                return $"'{Path.GetFileName(inPath)}' carries no film grain synthesis, so there is no table in it to " +
                    $"read.\n\nA table can be measured off any source instead, with this utility's Measure operation " +
                    $"or the AV1AN tab's Grain Synthesis row.";

            if (!File.Exists(tablePath) || !GrainSynthConfig.LooksLikeGrainTable(tablePath))
                return $"{ToolName} did not produce a grain table.\n\n{LastLines(output)}";

            return "";
        }

        /// <summary> Strips every film grain header out of an AV1 file. Returns why it could not, or ""
        /// when it did. </summary>
        public static async Task<string> RemoveAsync(string inPath, string outPath)
        {
            IoUtils.TryDeleteIfExists(outPath);
            string output = await Run($"remove {inPath.Wrap()} -o {outPath.Wrap()} -y", indeterminate: true);

            if (RunTask.canceled)
                return "";

            if (!output.Contains(DoneMarker) || IoUtils.GetFilesize(outPath) < 1)
                return $"{ToolName} could not strip the grain.\n\n{LastLines(output)}";

            return "";
        }

        /// <summary>
        /// The built-in film stock tables the installed binary actually has, for the row's dropdown.
        /// <para/>
        /// Read out of the binary rather than hard-coded, because the list grew between the crates.io
        /// release and the source this project bundles from, and a name the binary does not know is
        /// refused with the whole command. Falls back to
        /// <see cref="GrainSynthConfig.FallbackPresets"/> - what the pinned build prints - wherever the
        /// tool is absent or its output cannot be read, so the dropdown is never empty.
        /// </summary>
        public static async Task LoadPresetsAsync()
        {
            try
            {
                if (!IsAvailable())
                    return;

                string[] parsed = ParsePresets(await Run("presets", indeterminate: false, log: false));

                if (parsed.Length > 0)
                    GrainSynthConfig.Presets = parsed;
            }
            catch (Exception e)
            {
                Logger.Log($"Could not read {ToolName}'s preset list: {e.Message}", true);
            }
        }

        /// <summary>
        /// The names out of <c>grav1synth presets</c>, which prints two blocks: the presets themselves,
        /// then the film stock modifiers and the presets those apply to. A modifier is a suffix on a
        /// preset's name (<c>16mm-3</c>), so the usable set is every standalone plus every format preset
        /// both bare and with each suffix.
        /// </summary>
        private static string[] ParsePresets(string output)
        {
            List<string> names = new List<string>();
            List<string> suffixes = new List<string>();
            List<string> takesSuffixes = new List<string>();
            bool inModifiers = false;

            foreach (string raw in (output ?? "").SplitIntoLines())
            {
                string line = raw.Trim();

                if (line.StartsWith("Available film stock modifiers"))
                {
                    inModifiers = true;

                    // "…modifiers (applies to 16mm, Classic35, Modern35):" - the only statement of which
                    // presets a suffix may be pinned onto.
                    int open = line.IndexOf("applies to ");

                    if (open >= 0)
                        takesSuffixes.AddRange(line.Substring(open + "applies to ".Length).TrimEnd(':', ')')
                            .Split(',').Select(s => s.Trim(' ', ')', ':')).Where(s => s.IsNotEmpty()));

                    continue;
                }

                // Every entry in both blocks is "<token>  (<description>)"; everything else - headings,
                // blank lines, the "Example:" lines - has no bracket or no token.
                int bracket = line.IndexOf('(');

                if (bracket < 1 || line.StartsWith("Example") || !line.EndsWith(")"))
                    continue;

                string token = line.Substring(0, bracket).Trim();

                if (token.IsEmpty() || token.Contains(' '))
                    continue; // A modifier's own description line ("Fujifilm Eterna 250D  (default)")

                if (inModifiers)
                    suffixes.Add(token);
                else
                    names.Add(token);
            }

            if (names.Count < 1)
                return new string[0];

            List<string> all = new List<string>();

            foreach (string name in names)
            {
                all.Add(name);

                if (takesSuffixes.Contains(name))
                    all.AddRange(suffixes.Select(s => $"{name}{s}"));
            }

            return all.ToArray();
        }

        /// <summary>
        /// Runs the tool and hands back everything it printed. Both streams are collected: its progress
        /// and its logging go to stderr, its <c>presets</c> listing to stdout, and the errors worth
        /// reporting are on stderr behind an anyhow backtrace.
        /// </summary>
        private static async Task<string> Run(string args, bool indeterminate, bool log = true)
        {
            bool show = Config.GetInt(Config.Key.CmdDebugMode) > 0;
            string output = "";

            try
            {
                Process proc = OsUtils.NewProcess(!show, NmkoderProcess.ProcessType.Primary);
                proc.StartInfo.Arguments = Shell.BuildArguments($"{ToolName} {args}", AvProcess.StayOpen());
                OsUtils.SetPathVar(proc, new[] { Paths.GetBinPath() });

                if (log)
                    Logger.Log($"{ToolName} {args}", true, false, ToolName);

                if (!show)
                {
                    proc.OutputDataReceived += (s, line) => { output += Environment.NewLine + (line.Data ?? ""); Logger.Log($"[{ToolName}] {line.Data}", true, false, ToolName); };
                    proc.ErrorDataReceived += (s, line) => { output += Environment.NewLine + (line.Data ?? ""); Logger.Log($"[{ToolName}] {line.Data}", true, false, ToolName); };
                }

                // Nothing to measure against: the tool hides its own progress bar the moment stderr is not
                // a terminal, which is always here. An indeterminate bar at least separates "still
                // working" from "finished", which for a run that can last hours is the useful half.
                if (indeterminate)
                    Program.MainWin?.SetProgress(-1);

                proc.Start();

                if (!show)
                {
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                }

                // After the readers, and allowed to miss: the listing runs ('presets', '--help') can
                // be finished before this line runs, and asking a finished process to change priority
                // throws. When the set came first, that throw also skipped the reader hookup above, so
                // a fast run's entire output was lost and the startup probe misread the tool as broken.
                try
                {
                    proc.PriorityClass = ProcessPriorityClass.BelowNormal;
                }
                catch (InvalidOperationException) { } // Already exited - there is nothing left to deprioritize

                while (!proc.HasExited)
                    await Task.Delay(50);
            }
            catch (Exception e)
            {
                Logger.Log($"Error running {ToolName}: {e.Message}", true);
                output += Environment.NewLine + e.Message;
            }
            finally
            {
                if (indeterminate)
                    Program.MainWin?.SetProgress(0);
            }

            return output;
        }

        /// <summary> The tail of a failed run, for a message box. The tool ends an error with an anyhow
        /// backtrace a dozen frames deep, which says nothing to anyone here, so the interesting line is
        /// the first one - and it is preceded by libdav1d's own banner, which is noise. </summary>
        private static string LastLines(string output)
        {
            string[] lines = (output ?? "").SplitIntoLines()
                .Select(l => l.Trim())
                .Where(l => l.IsNotEmpty() && !l.Contains("libdav1d ") && !l.StartsWith("at ") && !l.StartsWith("Stack backtrace"))
                .ToArray();

            string error = lines.FirstOrDefault(l => l.StartsWith("Error:"));

            if (error.IsNotEmpty())
                return error;

            return string.Join(Environment.NewLine, lines.Reverse().Take(4).Reverse());
        }
    }
}
