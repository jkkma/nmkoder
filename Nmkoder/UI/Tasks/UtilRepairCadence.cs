using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.Media;
using Nmkoder.Utils;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Nmkoder.UI.Tasks
{
    /// <summary>
    /// Writes a constant-rate copy of a capture that padded itself with duplicate frames, and stops
    /// there. The output is the deliverable: the same pictures at the rate they were shot, ready to
    /// be deinterlaced and encoded by anything.
    /// <para/>
    /// A utility rather than something an encode tab does on the way past, for three reasons. It
    /// costs a full extra decode - the metrics pass reads every frame before one is handed on. It
    /// helps every downstream path rather than one: ffmpeg's own <c>-fps_mode cfr</c> is
    /// timestamp-driven too, so bwdif and yadif inherit the same damage QTGMC did, and repairing the
    /// file once fixes both. And the judgement it makes is not one to make silently - deciding that a
    /// file's frame count is wrong and its container right is a claim about somebody's capture, and
    /// the opposite case exists (see <see cref="CadenceRepair.TargetFrames"/>).
    /// <para/>
    /// It has no settings. There is one right answer for a given file - the recording's length at the
    /// rate whole frames arrive at - and nothing a person could usefully turn.
    /// </summary>
    class UtilRepairCadence
    {
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
                    RunTask.Cancel($"'{file.Name}' is an image sequence, which has no cadence to repair.");
                    return;
                }

                if (file.VideoStreams.Count < 1)
                {
                    RunTask.Cancel($"'{file.Name}' has no video track.");
                    return;
                }

                int target = CadenceRepair.TargetFrames(file);

                if (target < 1)
                {
                    RunTask.Cancel($"'{file.Name}' does not report both a duration and a frame rate, so there is " +
                        "nothing to measure its frame count against.");
                    return;
                }

                Logger.Log($"Reading '{file.Name.Trunc(40)}' through VapourSynth to count its frames.");
                RunTask.ReportProgress("Counting the frames...");
                int coded = await CadenceRepair.ProbeCodedFramesAsync(file);

                if (RunTask.canceled)
                    return;

                if (coded < 1)
                {
                    RunTask.Fail("VapourSynth could not say how many frames this file has, so there is nothing to " +
                        "compare against its duration. The log has what each source plugin said about it.");
                    return;
                }

                double ratio = coded / (double)target;
                Logger.Log($"'{file.Name.Trunc(40)}' carries {coded} frames where {FormatUtils.Time(file.DurationMs)} at " +
                    $"{file.VideoStreams[0].Rate} is {target} - {ratio:0.####}x.");

                // Refused rather than run, because the output would be a re-encoded copy of the input
                // and the run says by existing that something was wrong with the file.
                if (ratio <= CadenceRepair.PaddingThreshold)
                {
                    RunTask.Cancel($"'{file.Name}' carries {coded} frames, and {FormatUtils.Time(file.DurationMs)} at " +
                        $"{file.VideoStreams[0].Rate} comes to {target} - so its frame count already matches its own " +
                        "length and there is no padding to remove.\n\n" +
                        "This utility is for a capture whose hardware repeated frames to cover timing slips, which " +
                        "shows up as a frame count well above the recording's length. Deinterlace or encode this " +
                        "file directly instead.");
                    return;
                }

                string outPath = IoUtils.GetAvailableFilename($"{UiData.GetDefaultOutPath(file.SourcePath)}_cfr.mkv");
                Logger.Log($"Repairing the cadence of '{file.Name.Trunc(40)}': dropping {coded - target} padded frames " +
                    $"of {coded}, into a near-lossless MKV that is still interlaced.");

                string problem = await CadenceRepair.RunAsync(file, outPath);

                if (RunTask.canceled || RunTask.failed)
                    return;

                if (problem.IsNotEmpty())
                {
                    RunTask.Fail(problem);
                    return;
                }

                RunTask.ReportOutput(new[] { file.SourcePath }, outPath);
                Logger.Log($"Wrote '{Path.GetFileName(outPath)}'. It is still interlaced and at the source's own rate - " +
                    "deinterlace and encode it from either tab, and the result will be the length its audio is.");
            }
            catch (Exception e)
            {
                RunTask.Fail($"The cadence repair could not be made: {e.Message}");
                Logger.Log($"{e.StackTrace}", true, level: Logger.Level.Debug);
            }
            finally
            {
                Program.MainWin.SetWorking(false);
            }
        }
    }
}
