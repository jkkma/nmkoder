using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.UI;
using Nmkoder.UI.Tasks;
using Nmkoder.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using static Nmkoder.Media.AvProcess;

namespace Nmkoder.Media
{
    class FfmpegOutputHandler
    {
        public static readonly string prefix = "[ffmpeg]";
        public static long overrideTargetDurationMs = -1;

        /// <summary> The percentage for "running, and nobody can say how far along" - which is a real
        /// answer rather than a failure to have one, and which the bar shows by running indeterminate
        /// rather than by picking a number. </summary>
        public const int Unknown = -1;

        /// <summary> Wall time of the encode being tracked, for the ETA. </summary>
        static NmkdStopwatch encodeSw = new NmkdStopwatch();
        static int lastProgressPercent = -1;

        /// <summary> Whether this run has already been found to be running past the length its
        /// source claims to be, so the explanation is said once rather than three times a second. </summary>
        static bool pastTargetDuration = false;

        /// <summary> Called before each progress-tracked ffmpeg run, so the ETA never extrapolates
        /// from a previous run's elapsed time. </summary>
        public static void ResetProgressTracking()
        {
            encodeSw.sw.Restart();
            lastProgressPercent = -1;
            pastTargetDuration = false;
        }

        /// <summary>
        /// How far past a target duration an encode can land while the target is still the right
        /// one. Not zero: a muxer pads the last frame, a filter graph flushes one more, and a target
        /// worked out from a frame count and a frame rate is rounded to begin with - all of which put
        /// the final <c>time=</c> a frame or two beyond the number it was measured against.
        /// <para/>
        /// Anything past this is not overshoot, it is the target being wrong, and the difference
        /// matters because the two call for opposite behaviour: overshoot is rounded away, while a
        /// wrong target has to stop being reported as a percentage.
        /// </summary>
        public static long TargetToleranceMs(long durationMs)
        {
            return Math.Max(2000, durationMs / 100);
        }

        /// <summary>
        /// What one ffmpeg run's output said about how it was going. Deliberately per-run rather than
        /// static: thumbnail extraction runs on a background task while an encode is in progress
        /// (TrackList.SetAsMainFile starts it), so a shared field would let the thumbnail's output
        /// explain the encode's exit code - and a shared reset would wipe the encode's evidence the
        /// moment a thumbnail started.
        /// </summary>
        public class RunErrors
        {
            /// <summary> A line proving the run cannot have produced what was asked for, even if
            /// ffmpeg went on to exit 0. The first such line is kept. </summary>
            public string Evidence = "";

            /// <summary> The most recent line that reads like trouble. Never acted on by itself - it
            /// is only the explanation offered when the exit code says the run failed. </summary>
            public string Suspect = "";
        }

        /// <param name="canCancelTask">Whether a fatal line may stop the running task. False for the
        /// probes, which share this handler and have no business cancelling an encode over what they
        /// found while reading a file's metadata.</param>
        /// <param name="errors">Where this run's evidence accumulates, or null for the runs whose
        /// outcome nobody checks.</param>
        public static void LogOutput(string line, string[] ignoreStrings, ref string appendStr, string logFilename, LogMode logMode, bool showProgressBar, bool canCancelTask = true, RunErrors errors = null)
        {
            if (RunTask.canceled || string.IsNullOrWhiteSpace(line) || line.Trim().Length < 1)
                return;

            bool hidden = logMode == LogMode.Hidden;

            if (HideMessage(line)) // Don't print certain warnings
                hidden = true;

            bool replaceLastLine = logMode == LogMode.OnlyLastLine;

            if (line.Contains("time=") && (line.StartsWith("frame=") || line.StartsWith("size=")))
                line = FormatUtils.BeautifyFfmpegStats(line);

            string lineWithoutPath = RemoveStringsFromLine(line, ignoreStrings);
            string fatal = GetFatalProblem(lineWithoutPath, line);
            bool evidence = fatal.IsEmpty() && IsFailureEvidence(lineWithoutPath);
            bool suspect = !evidence && fatal.IsEmpty() && LooksLikeTrouble(lineWithoutPath);

            appendStr += Environment.NewLine + line;
            Logger.Log($"{prefix} {line}", hidden, replaceLastLine, logFilename,
                fatal.IsNotEmpty() || evidence ? Logger.Level.Error : suspect ? Logger.Level.Warning : Logger.Level.Info);

            if (!hidden && showProgressBar && line.Contains("Time:"))
            {
                Regex timeRegex = new Regex("(?<=Time:).*(?= )");
                UpdateFfmpegProgress(timeRegex.Match(line).Value, line);
            }

            if (errors != null && evidence && errors.Evidence.IsEmpty())
                errors.Evidence = line.Trim();

            if (errors != null && suspect)
                errors.Suspect = line.Trim();

            if (fatal.IsNotEmpty() && canCancelTask)
                RunTask.Cancel(fatal);
        }

        /// <summary>
        /// Why this line means the encode is over, or "" if it does not.
        /// <para/>
        /// Every entry here is a message ffmpeg only prints when it is about to give up, so stopping
        /// on it saves the user the rest of a doomed encode. What is deliberately absent is the
        /// catch-all that used to sit at the end of this list - any line containing "Error ",
        /// "Unable to ", "Could not open file" or "Failed " hard-cancelled the task. Encoding a
        /// lightly damaged transport stream prints "Error submitting packet to decoder" a hundred and
        /// thirty times and then exits 0 having written a complete file, so that rule killed the
        /// encode about a sixth of the way in; a track titled "Unable to Find My Way Home" killed it
        /// before it started, because only the input and output paths are stripped from the line
        /// before matching. Those lines are now <see cref="LooksLikeTrouble"/>: remembered as the
        /// explanation for a bad exit code, and ignored when the run turns out fine.
        /// </summary>
        private static string GetFatalProblem(string lineWithoutPath, string line)
        {
            if (lineWithoutPath.Contains("No NVENC capable devices found") || lineWithoutPath.MatchesWildcard("*nvcuda.dll*"))
                return $"Error: {line}\n\nMake sure you have an NVENC-capable Nvidia GPU.";

            if (lineWithoutPath.Contains("not currently supported in container") || lineWithoutPath.Contains("Unsupported codec id"))
                return $"Error: {line}\n\nIt looks like you are trying to copy a stream into a container that doesn't support this codec.";

            if (lineWithoutPath.Contains("Subtitle encoding currently only possible from text to text or bitmap to bitmap"))
                return $"Error: {line}\n\nYou cannot encode image-based subtitles into text-based subtitles. Please use the Copy Subtitles option instead, with a compatible container.";

            if (lineWithoutPath.Contains("Only VP8 or VP9 or AV1 video and Vorbis or Opus audio and WebVTT subtitles are supported for WebM"))
                return $"Error: {line}\n\nIt looks like you are trying to copy an unsupported stream into WEBM!";

            if (lineWithoutPath.MatchesWildcard("*codec*not supported*"))
                return $"Error: {line}\n\nTry using a different codec.";

            if (lineWithoutPath.Contains("GIF muxer supports only a single video GIF stream"))
                return $"Error: {line}\n\nYou tried to mux a non-GIF stream into a GIF file.";

            if (lineWithoutPath.Contains("Width and height of input videos must be same"))
                return $"Error: {line}";

            if (lineWithoutPath.Contains("Unknown pixel format"))
                return $"Error: {line}";

            if (lineWithoutPath.Contains("exactly one stream"))
                return $"Error: {line}\n\nYou cannot mux multiple tracks into this container.";

            return "";
        }

        /// <summary>
        /// Lines that prove the result is not what was asked for, on runs ffmpeg nonetheless ends
        /// with status 0 - so the exit code cannot catch them and nothing else would.
        /// <para/>
        /// "Output file is empty" is what a trim starting past the end of the video produces: a
        /// couple of hundred bytes of container with no frames in it, which every size check in the
        /// app happily counts as a finished encode. The demuxer pair is the concat case - one part of
        /// a joined-up input missing means the output silently ends early. Neither stops the run:
        /// ffmpeg is already finishing by the time they appear, and the point is that the task is
        /// reported failed rather than done.
        /// <para/>
        /// The null muxer is excluded, and that exclusion is the whole reason this is a method rather
        /// than a list. A two-pass encode's first pass is `-f null -`, and VP9's first pass genuinely
        /// muxes nothing - measured on ffmpeg 6.1.1, it prints
        /// "[out#0/null @ ...] Output file is empty, nothing was encoded" and exits 0, having done
        /// exactly what was asked. Without this, every two-pass VP9 encode would be reported failed.
        /// Get Metrics and cropdetect write to the same null muxer for the same reason.
        /// </summary>
        private static bool IsFailureEvidence(string line)
        {
            if (line.Contains("Output file is empty, nothing was encoded"))
                return !line.Contains("/null");

            return line.Contains("Error during demuxing")
                || line.Contains("Error retrieving a packet from demuxer");
        }

        /// <summary>
        /// The old catch-all, demoted. A line matching this is worth quoting when the run turns out
        /// to have failed, and worth nothing at all when it has not.
        /// </summary>
        private static bool LooksLikeTrouble(string line)
        {
            // Deliberately not "Invalid ": a damaged transport stream prints
            // "Invalid data found when processing input" scores of times per encode, and since this
            // tier keeps the *last* match, that noise would displace the line that actually explains
            // the failure.
            return new[] { "Error ", "Unable to ", "Could not open file", "Failed ", "No such file" }
                .Any(x => line.Contains(x));
        }

        private static string RemoveStringsFromLine (string s, string[] ignoreStrings)
        {
            foreach(string ign in ignoreStrings)
                s = s.Replace(ign, "");

            return s;
        }

        static void UpdateFfmpegProgress(string ffmpegTime, string statsLine)
        {
            try
            {
                if (TrackList.current == null && overrideTargetDurationMs < 0)
                    return;

                long durationMs = 0;
                
                if(overrideTargetDurationMs > 0)
                {
                    durationMs = overrideTargetDurationMs;
                }
                else if(QuickConvertUi.CurrentTrim != null && !QuickConvertUi.CurrentTrim.IsUnset)
                {
                    if(QuickConvertUi.CurrentTrim.TrimMode == Nmkoder.Data.TrimSettings.Mode.FrameNumbers)
                    {
                        if (TrackList.current != null && TrackList.current.File.VideoStreams.Count > 0)
                            durationMs = 1000 * ((double)QuickConvertUi.CurrentTrim.Duration * (1f / TrackList.current.File.VideoStreams[0].Rate.GetFloat())).RoundToLong();
                    }
                    else
                    {
                        durationMs = QuickConvertUi.CurrentTrim.Duration;
                    }
                    
                }
                else if (TrackList.current != null)
                {
                    durationMs = TrackList.current.File.DurationMs;
                }

                if (durationMs < 1)
                {
                    Program.MainWin?.SetProgress(0);
                    return;
                }

                long currentMs = FormatUtils.TimestampToMs(ffmpegTime);

                // Past the end of the video the target duration describes, by more than an encode
                // overshoots - which proves the target wrong, since an encode cannot write more of a
                // file than there is. What is being encoded is fine; what was measured against is
                // not, and a bar sitting on 100% for the rest of an hour says the opposite.
                bool overrun = currentMs > durationMs + TargetToleranceMs(durationMs);

                if (overrun)
                    NoteOverrun(durationMs);

                int progress = overrun ? Unknown : (((double)currentMs / (double)durationMs) * (double)100).RoundToInt();
                Program.MainWin?.SetProgress(progress);
                RunTask.ReportProgress(BuildProgressLine(progress, currentMs, durationMs, statsLine));
            }
            catch (Exception e)
            {
                Logger.Log($"Failed to get ffmpeg progress: {e.Message}", true);
            }
        }

        /// <summary>
        /// Says once, in the log, that the run has outlasted the length everything here is measured
        /// against - because the bar going indeterminate is a symptom, and this is the cause.
        /// <para/>
        /// Usually the duration is wrong. It comes out of the container, and a container can be wrong
        /// about it: an analogue capture is timestamped by whatever wrote it and ffprobe reads the
        /// duration straight back out of those timestamps, so a capture that stalled or restarted its
        /// clock leaves a file reporting an hour and holding considerably more. Said as the usual
        /// reason rather than the only one, because a mux whose longest track is not the loaded
        /// file's lands here too, and neither is a fault in the run.
        /// </summary>
        static void NoteOverrun(long durationMs)
        {
            if (pastTargetDuration)
                return;

            pastTargetDuration = true;
            Logger.Log($"This run has passed {FormatUtils.Time(durationMs)}, which is all the loaded file says it is - " +
                $"so the progress bar cannot say how far along it is, and shows that rather than sitting at 100%. " +
                $"Usually that means the file's own duration is wrong, which a tape capture's often is. The run itself is fine.");
        }

        /// <summary> Footer status line: percentage and ETA, plus the speed figures ffmpeg reported.
        /// The same numbers already scroll by in the log; this keeps the current ones in one place.
        /// <para/>
        /// A <see cref="Unknown"/> progress replaces the percentage and the ETA - both of which are
        /// derived from a target that has just been proved wrong - with the two figures that are
        /// still true: how much video has been written, and how much the source said there was. </summary>
        static string BuildProgressLine(int progress, long currentMs, long durationMs, string statsLine)
        {
            // The second pass of a two-pass encode restarts at zero within the same run - the only
            // thing that distinguishes it from a progress update is the direction. Skipped while the
            // progress is unknown, so the pass that overran leaves the pass after it a real baseline.
            if (progress >= 0)
            {
                if (progress < lastProgressPercent - 10)
                    encodeSw.sw.Restart();

                lastProgressPercent = progress;
            }

            // Progress-tracked ffmpeg runs are not all encodes - Get Metrics scores files this way too.
            RunTask.TaskType task = Program.MainWin != null ? Program.MainWin.RunningTask : RunTask.TaskType.None;
            string action = task == RunTask.TaskType.Convert || task == RunTask.TaskType.Av1an ? "Encoding" : "Processing";

            string state = progress < 0
                ? $"{FormatUtils.Time(currentMs)} in, past the {FormatUtils.Time(durationMs)} this file claims"
                : $"{progress.Clamp(0, 100)}%";

            List<string> parts = new List<string> { $"{action} - {state}" };

            string fps = Regex.Match(statsLine, @"(?<=FPS: )[\d\.]+").Value;
            string speed = Regex.Match(statsLine, @"(?<=Relative Speed: )[\d\.]+x").Value;

            if (fps.IsNotEmpty() && fps.GetFloat() > 0)
                parts.Add($"FPS: {fps}");

            if (speed.IsNotEmpty())
                parts.Add($"Speed: {speed}");

            if (progress >= 1 && progress < 100)
                parts.Add($"ETA: {FormatUtils.Time(TimeSpan.FromMilliseconds(encodeSw.ElapsedMs * (100 - progress) / progress), false)}");

            return string.Join(" - ", parts);
        }

        static bool HideMessage(string msg)
        {
            string[] hiddenMsgs = new string[] { 
                "can produce invalid output", 
                "pixel format", 
                "provided invalid", 
                "Non-monotonous", 
                "not enough frames to estimate rate", 
                "invalid dropping", 
                "message repeated", 
                "missing, emulating",
                "Consider increasing the value"
            };

            foreach (string str in hiddenMsgs)
                if (msg.MatchesWildcard($"*{str}*"))
                    return true;

            return false;
        }
    }
}
