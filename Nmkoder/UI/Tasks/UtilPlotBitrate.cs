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
                Program.MainWin.SetWorking(false);
                await BitratePlotWindow.Show(seconds);
            }
            catch (Exception e)
            {
                RunTask.Fail($"The bitrate chart could not be produced: {e.Message}");
                Logger.Log($"{e.StackTrace}", true, level: Logger.Level.Debug);
            }
        }
    }
}
