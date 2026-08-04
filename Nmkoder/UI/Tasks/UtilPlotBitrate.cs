using Nmkoder.Data;
using Nmkoder.Data.Ui;
using Nmkoder.Views;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nmkoder.UI.Tasks
{
    class UtilPlotBitrate
    {
        public static async Task Run()
        {
            if (RunTask.currentFileListMode == RunTask.FileListMode.Batch)
            {
                RunTask.Fail("The Bitrate Chart utility opens a chart window for the file it analysed, so it only runs in Muxing Mode - a queue would raise one window per file.");
                return;
            }

            Program.MainWin.SetWorking(true);

            try
            {
                string path = TrackList.current.File.ImportPath;
                List<BitratePlottingUtils.Frame> frameList = await BitratePlottingUtils.GetFrameInfos(path, true);
                var seconds = BitratePlottingUtils.GetBytesPerSecond(frameList);

                // A window drawing "No data" does not say whether the probe failed or the file is
                // empty, and it is the same picture either way.
                if (seconds.Count < 1)
                {
                    RunTask.Fail("No frames could be read from this file, so there is no bitrate to chart. The log has ffprobe's own output.");
                    return;
                }

                Program.MainWin.SetWorking(false);
                await BitratePlotWindow.Show(seconds);
            }
            catch (Exception e)
            {
                RunTask.Fail($"The bitrate chart could not be produced: {e.Message}");
                Logger.Log($"{e.StackTrace}", true, level: Logger.Level.Debug);
            }
            finally
            {
                // The only SetWorking(false) sat inside the try, after the analysis, so the early
                // returns and the throw path left this method without clearing it. RunTask.Start
                // clears it after every task regardless, so nothing was actually stuck - this is here
                // so the method stands on its own rather than on its caller. It is idempotent, so the
                // happy path clearing it early to show the chart still stands.
                Program.MainWin.SetWorking(false);
            }
        }
    }
}
