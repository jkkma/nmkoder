using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.Media;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Nmkoder.UI.Tasks
{
    /// <summary>
    /// Copies the section between two points into a new file without re-encoding it - the same job
    /// LosslessCut does, and the reason to do it here rather than through the Quick Encode tab's trim
    /// is speed: nothing is decoded, so a cut runs at disk speed and the video comes out untouched.
    ///
    /// The price of not re-encoding is that a copy can only begin at a keyframe, so the output starts
    /// at the closest one at or before the start point. The cut dialog puts the start point on one
    /// before it gets here, so the section configured is the section that comes out.
    /// </summary>
    class UtilCut
    {
        /// <summary> The configured section. Null until the user has been through the dialog. </summary>
        public static TrimSettings Cut;

        public static async Task Run()
        {
            Program.MainWin.SetWorking(true);

            try
            {
                MediaFile file = TrackList.current?.File;

                if (file == null)
                {
                    RunTask.Cancel("No input file loaded! Please load one first (File List).");
                    return;
                }

                if (file.IsDirectory)
                {
                    RunTask.Cancel($"'{file.Name}' is an image sequence, which cannot be cut without encoding it. Use the Quick Encode tab's trim instead.");
                    return;
                }

                if (Cut == null || Cut.IsUnset)
                {
                    RunTask.Cancel("No section to cut has been configured yet. Press Configure on the Cut Video utility to pick one.");
                    return;
                }

                string problem = ResolveSection(Cut, file, out long start, out long end);

                if (problem.IsNotEmpty())
                {
                    RunTask.Cancel(problem);
                    return;
                }

                string ext = Path.GetExtension(file.SourcePath);
                string outPath = IoUtils.GetAvailableFilename($"{UiData.GetDefaultOutPath(file.SourcePath)}_cut{ext}");

                Logger.Log($"Cutting {FormatDuration(start)} to {FormatDuration(end)} ({FormatDuration(end - start)}) out of {file.Name} without re-encoding.");

                // The else branch is the point: a cut that wrote nothing used to produce no message
                // of any kind, and the task then reported itself finished. The cancel guard has to
                // stay on the success branch too - a stopped cut leaves a partial file behind, and
                // reporting its size would announce "4.0 GB -> 480 MB (-88%)" over a truncated clip.
                bool wrote = await CopySection(file.ImportPath, outPath, start, end);

                if (RunTask.canceled)
                    return;

                if (wrote)
                    RunTask.ReportOutput(new[] { file.SourcePath }, outPath);
                else if (!RunTask.failed)
                    RunTask.Fail($"Nothing was written to '{Path.GetFileName(outPath)}'. The log has FFmpeg's output.");
            }
            catch (Exception e)
            {
                RunTask.Fail($"The cut could not be made: {e.Message}");
                Logger.Log($"{e.StackTrace}", true, level: Logger.Level.Debug);
            }

            Program.MainWin.SetWorking(false);
        }

        /// <summary>
        /// Narrows a configured section to what a file actually holds, and returns why there is
        /// nothing left to cut when there is not. A batch runs one configured section against every
        /// file, and a shorter one can end before the section does - cutting what is there beats
        /// failing the whole queue.
        /// </summary>
        public static string ResolveSection(TrimSettings section, MediaFile file, out long startMs, out long endMs)
        {
            // Through the ms accessors rather than off the fields: in frame mode those hold frame
            // numbers, and comparing a frame count against a duration in milliseconds compares nothing.
            Fraction rate = file.VideoStreams.FirstOrDefault()?.Rate ?? Fraction.Zero;
            startMs = Math.Max(0, section.GetStartMs(rate));
            endMs = file.DurationMs > 0 ? Math.Min(section.GetEndMs(rate), file.DurationMs) : section.GetEndMs(rate);

            return endMs > startMs ? ""
                : $"'{file.Name}' is {FormatDuration(file.DurationMs)} long, which is entirely before the configured start point ({FormatDuration(startMs)}).";
        }

        /// <summary>
        /// Copies the section between two points into outPath without re-encoding it, and says
        /// whether anything came out. Shared with the AV1AN tab, whose trim works by cutting the
        /// section out before av1an is ever started on it.
        /// </summary>
        /// <param name="map"> Which streams to carry over. Everything by default, which is what a cut
        /// and a trim both want; the CRF ladder asks for the first video track alone, so that the bytes
        /// it measures are video and its samples are not carrying an audio track nothing scores. </param>
        public static async Task<bool> CopySection(string inPath, string outPath, long startMs, long endMs, string map = "-map 0")
        {
            long durationMs = endMs - startMs;

            // Where the copy will really begin. Saying so up front is the difference between an
            // output that looks a few seconds too long and one the user was told to expect.
            long keyframeMs = await FfmpegUtils.GetKeyframeMsAtOrBefore(inPath, startMs);

            // The dialog snaps the start point onto a keyframe, so this is the batch case: one
            // configured section run against a file whose keyframes sit somewhere else.
            if (keyframeMs >= 0 && keyframeMs < startMs)
                Logger.Log($"The closest keyframe before the start point is at {FormatDuration(keyframeMs)}, so the cut begins there - {FormatDuration(startMs - keyframeMs)} earlier. " +
                    $"This file's keyframes do not line up with the configured start point, and a copy cannot begin between them.");

            // Seeking before -i keeps this cheap - ffmpeg jumps to the keyframe instead of reading
            // its way there - and -map 0 carries every track over, not just the first of each kind.
            string args = $"-ss {FormatDuration(startMs)} -i {inPath.Wrap()} -t {FormatDuration(durationMs)} " +
                $"{map} -c copy -avoid_negative_ts make_zero -ignore_unknown {outPath.Wrap()}";

            FfmpegOutputHandler.overrideTargetDurationMs = durationMs; // Progress is against the cut, not the source
            // Deliberately not ReportFailure: this is shared with the AV1AN tab, whose trim runs it
            // as a preparatory step and reports the failure itself. Reporting here as well produced
            // two error boxes, and the second, vaguer reason overwrote the first.
            await AvProcess.RunFfmpeg(new AvProcess.FfmpegSettings() { Args = args, LoggingMode = AvProcess.LogMode.OnlyLastLine, ProgressBar = true });
            FfmpegOutputHandler.overrideTargetDurationMs = -1; // Whatever runs next is not this cut

            return IoUtils.GetFilesize(outPath) > 0;
        }

        public static string FormatDuration(long ms)
        {
            return TrimSettings.GetTimeString(TimeSpan.FromMilliseconds(Math.Max(0, ms)));
        }
    }
}
