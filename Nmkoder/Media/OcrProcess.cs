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
using System.Text;
using System.Threading.Tasks;

namespace Nmkoder.Media
{
    class OcrProcess
    {
        public static async Task RunSubtitleEdit(string args, bool hidden = false, bool trackProgress = false)
        {
            bool show = false; // Config.GetInt(Config.Key.cmdDebugMode) > 0;
            Process subEdit = OsUtils.NewProcess(!show, NmkoderProcess.ProcessType.Primary);

            subEdit.StartInfo.Arguments = Shell.BuildArguments($"{Shell.ChangeDir(GetDir())} SubtitleEdit {args}", AvProcess.StayOpen());
            Logger.Log($"cmd {subEdit.StartInfo.Arguments}", true, false);

            if (!hidden) Logger.Log("Starting OCR...", false);
            Logger.Log($"SubtitleEdit {args}", true, false, "ocr");

            if (!show)
            {
                subEdit.OutputDataReceived += (sender, outLine) => { LogOutput(outLine.Data, hidden, trackProgress, args); };
                subEdit.ErrorDataReceived += (sender, outLine) => { LogOutput(outLine.Data, hidden, trackProgress, args); };
            }

            subEdit.Start();
            subEdit.PriorityClass = ProcessPriorityClass.BelowNormal;

            if (!show)
            {
                subEdit.BeginOutputReadLine();
                subEdit.BeginErrorReadLine();
            }

            while (!subEdit.HasExited)
                await Task.Delay(100);

            if (!hidden)
                Program.MainWin?.SetProgress(0);

            if (trackProgress)
                OcrUtils.progressTracker[args] = 100; // The process is done, whatever its last progress line said
        }

        public static void LogOutput(string line, bool hidden, bool trackProg, string args)
        {
            //timeSinceLastOutput.Restart();

            if (RunTask.canceled || string.IsNullOrWhiteSpace(line))
                return;

            //lastOutputSubEdit = lastOutputSubEdit + "\n" + line;

            //bool hidden = currentLogMode == LogMode.Hidden;
            //
            //if (HideMessage(line)) // Don't print certain warnings 
            //    hidden = true;

            //bool replaceLastLine = true; //currentLogMode == LogMode.OnlyLastLine;

            //Logger.Log(line, true, replaceLastLine, "ocr");

            if (line.Contains("OCR... :"))
            {
                int percent = line.Split(':').LastOrDefault().GetInt();

                if (!hidden)
                {
                    Logger.Log($"Running Optical Character Recognition: {percent}%", false, Logger.LastUiLine.EndsWith("%"));
                    Program.MainWin?.SetProgress(percent);
                }

                if (trackProg)
                    OcrUtils.progressTracker[args] = percent;
            }

        }

        public static string GetDir()
        {
            return Path.Combine(Paths.GetBinPath(), "SE");
        }

        public static string GetCmdArg()
        {
            bool stayOpen = Config.GetInt(Config.Key.CmdDebugMode) == 2;

            if (stayOpen)
                return "/K";
            else
                return "/C";
        }
    }
}
