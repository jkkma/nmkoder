using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.Media;
using System;
using System.IO;
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

                if (await CopySection(file.ImportPath, outPath, start, end) && !RunTask.canceled)
                    RunTask.ReportOutput(new[] { file.SourcePath }, outPath);
            }
            catch (Exception e)
            {
                Logger.Log($"{e.Message}\n{e.StackTrace}");
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
            startMs = Math.Max(0, section.StartTime);
            endMs = file.DurationMs > 0 ? Math.Min(section.EndTime, file.DurationMs) : section.EndTime;

            return endMs > startMs ? ""
                : $"'{file.Name}' is {FormatDuration(file.DurationMs)} long, which is entirely before the configured start point ({FormatDuration(startMs)}).";
        }

        /// <summary>
        /// Copies the section between two points into outPath without re-encoding it, and says
        /// whether anything came out. Shared with the AV1AN tab, whose trim works by cutting the
        /// section out before av1an is ever started on it.
        /// </summary>
        public static async Task<bool> CopySection(string inPath, string outPath, long startMs, long endMs)
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
                $"-map 0 -c copy -avoid_negative_ts make_zero -ignore_unknown {outPath.Wrap()}";

            FfmpegOutputHandler.overrideTargetDurationMs = durationMs; // Progress is against the cut, not the source
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
