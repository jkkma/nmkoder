using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static Nmkoder.Media.AvProcess;

namespace Nmkoder.Media
{
    class Av1anOutputHandler
    {
        static int currentQueueSize;
        static bool stopProgressLoop;
        public static Task currentLogReaderTask;

        /// <summary> Starts reading progress out of the current encode's log file. Call once per av1an run. </summary>
        public static void StartProgressLoop()
        {
            stopProgressLoop = false;
            currentQueueSize = 0; // Static, so it has to be cleared or the next encode inherits this one's chunk count
            currentLogReaderTask = Task.Run(() => ParseProgressLoop());
        }

        public static void StopProgressLoop()
        {
            stopProgressLoop = true;
        }

        /// <summary> Sleeps in small steps so the loop notices the encode ending. False = stop looping. </summary>
        static async Task<bool> KeepWaiting(int ms = 1000)
        {
            for (int i = ms / 10; i > 0; i--)
            {
                if (!Program.busy || stopProgressLoop)
                    return false;

                await Task.Delay(10);
            }

            return true;
        }

        public static void LogOutput(string line, string logFilename, LogMode logMode, bool showProgressBar)
        {
            if (RunTask.canceled || string.IsNullOrWhiteSpace(line))
                return;

            bool hidden = logMode == LogMode.Hidden;

            if (HideMessage(line)) // Don't print certain warnings 
                hidden = true;

            bool replaceLastLine = logMode == LogMode.OnlyLastLine;

            Logger.Log(line, hidden, replaceLastLine, "av1an");

            if (line.Contains("Could not open file"))
            {
                RunTask.Cancel($"Error: {line}");
                return;
            }
        }

        public static async Task ParseProgressLoop()
        {
            int workers = Config.GetInt(Config.Key.Av1anOptsWorkerCountUpDown);
            string dir = AvProcess.lastTempDirAv1an;

            if (dir.IsEmpty())
                return; // No temp folder to watch (e.g. av1an failed before one was created)

            string logFile = Path.Combine(dir, "log.log");

            if (!await KeepWaiting(3000)) return;

            while (!File.Exists(logFile))
                if (!await KeepWaiting()) return;

            NmkdStopwatch sw = new NmkdStopwatch();

            Dictionary<int, int> etas = new Dictionary<int, int>();

            while (File.Exists(logFile))
            {
                try
                {
                    string contents;

                    using (var stream = File.Open(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        contents = new StreamReader(stream).ReadToEnd();

                    string[] logLines = contents.SplitIntoLines();
                    int encodedChunks = logLines.Where(x => x.Contains("Done: ")).Count();

                    if (currentQueueSize == 0)
                    {
                        string[] sc = logLines.Where(x => x.Contains("SC: Now at ")).ToArray();
                        currentQueueSize = sc.Length > 0 ? sc[0].Split("SC: Now at ")[1].Split(' ')[0].GetInt() : 0;
                    }

                    if (currentQueueSize > 0) // Nothing to report until scene detection has announced a queue size
                    {
                        int ratio = FormatUtils.RatioInt(encodedChunks, currentQueueSize);
                        Program.MainWin?.SetProgress(ratio);

                        string etaStr = "";

                        if (encodedChunks > workers && encodedChunks < currentQueueSize) // Needs finished chunks to extrapolate from
                        {
                            if (!etas.ContainsKey(encodedChunks)) // Cached per chunk count so the estimate doesn't jitter within a chunk
                            {
                                float secsPerChunk = ((float)sw.ElapsedMs / 1000) / encodedChunks;
                                etas[encodedChunks] = ((currentQueueSize - encodedChunks) * secsPerChunk).RoundToInt();
                            }

                            etaStr = $" ETA: <{FormatUtils.Time(new TimeSpan(0, 0, etas[encodedChunks]), false)}";
                        }

                        Logger.Log($"AV1AN is running - Encoded {encodedChunks}/{currentQueueSize} chunks ({ratio}%).{etaStr}", false, Logger.LastUiLine.Contains("Encoded"));
                    }
                }
                catch (Exception e)
                {
                    Logger.Log($"Failed to get av1an progress from log file: {e.Message}\n{e.StackTrace}", true);
                }

                if (!await KeepWaiting()) return;
            }
        }

        static bool HideMessage(string msg)
        {
            string[] hiddenMsgs = new string[] { };

            foreach (string str in hiddenMsgs)
                if (msg.MatchesWildcard($"*{str}*"))
                    return true;

            return false;
        }
    }
}
