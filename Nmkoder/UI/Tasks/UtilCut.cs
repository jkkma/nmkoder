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
    /// at the closest one at or before the start point. The cut dialog says where that lands.
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

                long start = Math.Max(0, Cut.StartTime);
                // A batch runs one configured section against every file, and a shorter one can end
                // before the section does. Cutting what is there beats failing the whole queue.
                long end = file.DurationMs > 0 ? Math.Min(Cut.EndTime, file.DurationMs) : Cut.EndTime;

                if (end <= start)
                {
                    RunTask.Cancel($"'{file.Name}' is {FormatDuration(file.DurationMs)} long, which is entirely before the configured start point ({FormatDuration(start)}).");
                    return;
                }

                long durationMs = end - start;
                string ext = Path.GetExtension(file.SourcePath);
                string outPath = IoUtils.GetAvailableFilename($"{UiData.GetDefaultOutPath(file.SourcePath)}_cut{ext}");

                Logger.Log($"Cutting {FormatDuration(start)} to {FormatDuration(end)} ({FormatDuration(durationMs)}) out of {file.Name} without re-encoding.");

                // Where the copy will really begin. Saying so up front is the difference between an
                // output that looks a few seconds too long and one the user was told to expect.
                long keyframeMs = await FfmpegUtils.GetKeyframeMsAtOrBefore(file.ImportPath, start);

                if (keyframeMs >= 0 && keyframeMs < start)
                    Logger.Log($"The closest keyframe before the start point is at {FormatDuration(keyframeMs)}, so the cut begins there - {FormatDuration(start - keyframeMs)} earlier. " +
                        $"Snap the start point to a keyframe in the cut dialog to avoid this.");

                // Seeking before -i keeps this cheap - ffmpeg jumps to the keyframe instead of reading
                // its way there - and -map 0 carries every track over, not just the first of each kind.
                string args = $"-ss {FormatDuration(start)} -i {file.ImportPath.Wrap()} -t {FormatDuration(durationMs)} " +
                    $"-map 0 -c copy -avoid_negative_ts make_zero -ignore_unknown {outPath.Wrap()}";

                FfmpegOutputHandler.overrideTargetDurationMs = durationMs; // Progress is against the cut, not the source
                await AvProcess.RunFfmpeg(new AvProcess.FfmpegSettings() { Args = args, LoggingMode = AvProcess.LogMode.OnlyLastLine, ProgressBar = true });

                if (!RunTask.canceled)
                    RunTask.ReportOutput(new[] { file.SourcePath }, outPath);
            }
            catch (Exception e)
            {
                Logger.Log($"{e.Message}\n{e.StackTrace}");
            }

            Program.MainWin.SetWorking(false);
        }

        private static string FormatDuration(long ms)
        {
            return TrimSettings.GetTimeString(TimeSpan.FromMilliseconds(Math.Max(0, ms)));
        }
    }
}
