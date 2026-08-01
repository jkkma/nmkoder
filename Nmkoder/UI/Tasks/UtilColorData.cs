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
                await Views.ColorDataWindow.ShowAsync(silent: true); // Pick sane defaults if never configured

                if (!File.Exists(vidTarget))
                {
                    Logger.Log($"Only one file loaded - Will only print metadata for {Path.GetFileName(vidSrc)}.");
                    VideoColorData d = await ColorDataUtils.GetColorData(vidSrc);
                    Logger.Log(d.ToString());
                }
                else
                {
                    Logger.Log($"Transferring color data from {Path.GetFileName(vidSrc)} to {Path.GetFileName(vidTarget)}.");
                    VideoColorData data = await ColorDataUtils.GetColorData(vidSrc);
                    Logger.Log(data.ToString());
                    await ColorDataUtils.SetColorData(vidTarget, data);
                }
            }
            catch(Exception e)
            {
                RunTask.Fail($"The color data could not be transferred: {e.Message}");
                Logger.Log($"{e.StackTrace}", true, level: Logger.Level.Debug);
            }
            
            Program.MainWin.SetWorking(false);
        }
    }
}
