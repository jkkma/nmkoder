using Nmkoder.Data;
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
    class UtilColorData
    {
        public static string vidSrc;
        public static string vidTarget;
        public static bool copyColorSpace = true;
        public static bool copyHdrData = true;

        public static async Task Run()
        {
            Program.MainWin.SetWorking(true);

            try
            {
                await Views.ColorDataWindow.ShowAsync(silent: true); // Settle the source if never configured

                if (!File.Exists(vidSrc))
                {
                    RunTask.Fail("No file is loaded to read color data from. Load one into the file list first.");
                    return;
                }

                // Reading is what this does unless it has been told to write. It used to guess a
                // target as well - the smallest file in the list - so a Run nobody had configured
                // rewrote a file the user had never named, in place. A guess is a fine way to fill
                // in which file to read; it is not a way to decide which file to overwrite.
                if (!File.Exists(vidTarget))
                {
                    Logger.Log(GetReadOnlyReason());
                    VideoColorData d = await ColorDataUtils.GetColorData(vidSrc);
                    Logger.Log(d.ToString());
                }
                else
                {
                    string what = copyColorSpace && copyHdrData ? "color space and HDR data" : copyColorSpace ? "color space" : copyHdrData ? "HDR data" : "nothing";
                    Logger.Log($"Transferring {what} from {Path.GetFileName(vidSrc)} to {Path.GetFileName(vidTarget)}.");
                    VideoColorData data = await ColorDataUtils.GetColorData(vidSrc);
                    Logger.Log(data.ToString());
                    await ColorDataUtils.SetColorData(vidTarget, data, copyColorSpace, copyHdrData);
                }
            }
            catch(Exception e)
            {
                RunTask.Fail($"The color data could not be transferred: {e.Message}");
                Logger.Log($"{e.StackTrace}", true, level: Logger.Level.Debug);
            }
            
            Program.MainWin.SetWorking(false);
        }

        /// <summary> Why this run is only printing, phrased for the reason it actually is. </summary>
        private static string GetReadOnlyReason()
        {
            string printing = $"printing {Path.GetFileName(vidSrc)}'s metadata only";

            if (RunTask.currentFileListMode == RunTask.FileListMode.Batch)
                return $"Batch Processing Mode cannot transfer color data, so {printing}. Switch to Muxing Mode to transfer it.";

            if (FileList.Items.Count < 2)
                return $"Only one file is loaded, so there is nothing to transfer to - {printing}.";

            return $"No target file is picked, so nothing will be written - {printing}. " +
                $"Pick one with Configure… on this utility's card to transfer the data into it.";
        }
    }
}
