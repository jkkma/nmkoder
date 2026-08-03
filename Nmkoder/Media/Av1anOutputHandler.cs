using Newtonsoft.Json.Linq;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        public static void StartProgressLoop(string av1anArgs)
        {
            stopProgressLoop = false;
            currentQueueSize = 0; // Static, so it has to be cleared or the next encode inherits this one's chunk count
            int workers = ParseWorkerCount(av1anArgs);
            currentLogReaderTask = Task.Run(() => ParseProgressLoop(workers));
        }

        /// <summary>
        /// The worker count the command actually asked for. Read back out of the arguments rather than
        /// from the config, which only holds what the AV1AN tab last saved - a resumed encode runs a
        /// saved command that may well have been built with a different number.
        /// </summary>
        static int ParseWorkerCount(string args)
        {
            string[] parts = (args ?? "").Split(' ');

            for (int i = 0; i < parts.Length - 1; i++)
                if (parts[i] == "-w" || parts[i] == "--workers")
                    return parts[i + 1].GetInt();

            return 0;
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
            bool fatal = line.Contains("Could not open file");

            // Levelled the way the ffmpeg handler is, and for the same reason: OnlyLastLine rewrites
            // the last row on every line av1an prints, so anything that mattered was overwritten
            // within a fraction of a second. An Error line is neither replaced nor replaces.
            Logger.Log(line, hidden, replaceLastLine, "av1an",
                fatal ? Logger.Level.Error : LooksLikeTrouble(line) ? Logger.Level.Warning : Logger.Level.Info);

            if (fatal)
                RunTask.Cancel($"Error: {line}");
        }

        /// <summary>
        /// Lines worth colouring amber. Only a hint - av1an's own exit code decides whether the
        /// encode failed, exactly as ffmpeg's does, and a chunk that retries is not a failure.
        /// </summary>
        private static bool LooksLikeTrouble(string line)
        {
            return new[] { "ERROR", "error:", "Error:", "panicked", "thread '", "warning:", "WARN" }
                .Any(x => line.Contains(x));
        }

        /// <summary>
        /// Watches an av1an run and reports its progress, out of the two JSON files av1an keeps in the
        /// temp folder: <c>scenes.json</c>, written once scene detection is done, and <c>done.json</c>,
        /// rewritten after every finished chunk.
        /// <para/>
        /// It used to read av1an's own log file instead, and that had stopped working entirely - which
        /// is why the files are read rather than any log line. Two things went at once. av1an's
        /// <c>--log-file</c> stopped defaulting to <c>{temp}/log.log</c> after 0.4.x and now defaults to
        /// <c>./logs/av1an.log</c> with the date appended, relative to the working directory - so the
        /// file this waited for is never created and the loop sat in that wait for the whole encode,
        /// reporting nothing at all. And the two lines it looked for, "SC: Now at " and "Done: ", are
        /// from an av1an older still: nothing since 0.4.0 emits either, a finished chunk being
        /// "finished chunk 00001: …" for several years now.
        /// <para/>
        /// The files are the stabler thing to read anyway. Their location is not a default that can move
        /// - it is the folder this app names itself with <c>--temp</c> - and their contents are what
        /// av1an resumes from, so they cannot quietly stop being written the way a log line can.
        /// </summary>
        public static async Task ParseProgressLoop(int workers)
        {
            string dir = AvProcess.lastTempDirAv1an;

            if (dir.IsEmpty())
                return; // No temp folder to watch (e.g. av1an failed before one was created)

            if (!await KeepWaiting(3000)) return;

            NmkdStopwatch sw = new NmkdStopwatch();

            Dictionary<int, int> etas = new Dictionary<int, int>();
            int encodedChunks = 0;
            // What was already finished when this run started. Zero for a fresh encode, and the whole
            // of the previous attempt for a resumed one - which the stopwatch beside it knows nothing
            // about, so the estimate has to extrapolate from the chunks *this* run has encoded.
            int inheritedChunks = -1;

            while (Directory.Exists(dir))
            {
                try
                {
                    if (currentQueueSize == 0) // Fixed once scene detection has written it, so it is only read until then
                        currentQueueSize = GetQueueSizeFromScenesFile(dir);

                    int done = GetDoneChunkCount(dir);

                    if (done >= 0) // -1 is a file caught half-written, where the count already in hand is the better answer
                    {
                        encodedChunks = done;

                        if (inheritedChunks < 0)
                            inheritedChunks = done;
                    }

                    if (currentQueueSize > 0) // Nothing to report until scene detection has announced a queue size
                    {
                        int ratio = FormatUtils.RatioInt(encodedChunks, currentQueueSize);
                        Program.MainWin?.SetProgress(ratio);

                        string etaStr = "";
                        int chunksThisRun = encodedChunks - Math.Max(0, inheritedChunks);

                        if (chunksThisRun > workers && encodedChunks < currentQueueSize) // Needs finished chunks to extrapolate from
                        {
                            if (!etas.ContainsKey(encodedChunks)) // Cached per chunk count so the estimate doesn't jitter within a chunk
                            {
                                float secsPerChunk = ((float)sw.ElapsedMs / 1000) / chunksThisRun;
                                etas[encodedChunks] = ((currentQueueSize - encodedChunks) * secsPerChunk).RoundToInt();
                            }

                            etaStr = $" ETA: <{FormatUtils.Time(new TimeSpan(0, 0, etas[encodedChunks]), false)}";
                        }

                        Logger.Log($"AV1AN is running - Encoded {encodedChunks}/{currentQueueSize} chunks ({ratio}%).{etaStr}", false, Logger.LastUiLine.Contains("Encoded"));
                        RunTask.ReportProgress($"Encoding - {encodedChunks}/{currentQueueSize} chunks ({ratio}%){(etaStr.IsEmpty() ? "" : $" -{etaStr}")}");
                    }
                    else
                    {
                        RunTask.ReportProgress("Scene detection..."); // The chunk queue only exists once av1an is done splitting
                    }
                }
                catch (Exception e)
                {
                    Logger.Log($"Failed to get av1an progress from its temp folder: {e.Message}\n{e.StackTrace}", true);
                }

                if (!await KeepWaiting()) return;
            }
        }

        /// <summary>
        /// How many chunks the video was split into, read from the scene list av1an writes into the temp
        /// folder before the first chunk starts. A resumed encode reuses that list rather than redoing
        /// the detection, so this is the only thing that answers for one at all.
        /// <para/>
        /// The array to count is <c>split_scenes</c> and not <c>scenes</c>. Those are the same list until
        /// <c>-x</c> subdivides the long scenes, which this app always asks for, and it is the subdivided
        /// one the chunk queue is built from. Both are in the file, so counting every <c>"start_frame"</c>
        /// in it - which is what this did - came to <c>scenes + split_scenes</c>: at least double the real
        /// chunk count, and more wherever the extra splits actually did something. The progress bar was
        /// measured against that, so a finished encode stopped somewhere under half.
        /// <para/>
        /// <c>scenes</c> is still read as a fallback, for a scenes file written before av1an grew the
        /// second array - av1an reads those itself, and a resume is exactly where one turns up.
        /// </summary>
        static int GetQueueSizeFromScenesFile(string dir)
        {
            string path = Path.Combine(dir, "scenes.json");

            if (!File.Exists(path))
                return 0;

            try
            {
                JObject root = JObject.Parse(ReadSharedText(path));

                foreach (string key in new[] { "split_scenes", "scenes" })
                    if (root[key] is JArray scenes)
                        return scenes.Count;

                return 0;
            }
            catch (Exception e)
            {
                // Includes catching the file mid-write, which is not worth a line of its own - the next
                // pass round the loop is a second away and the count is not needed before then.
                Logger.Log($"Failed to read av1an's scene list: {e.Message}", true);
                return 0;
            }
        }

        /// <summary>
        /// How many chunks have finished, from the file av1an rewrites after each one - the same file it
        /// resumes from, so the count survives a resume rather than restarting at zero.
        /// <para/>
        /// -1 means the file could not be read this time round, which a file rewritten this often will
        /// occasionally be. That is not "nothing is done yet": the caller keeps the count it already had
        /// rather than letting the bar jump back to the start for a second.
        /// </summary>
        static int GetDoneChunkCount(string dir)
        {
            string path = Path.Combine(dir, "done.json");

            if (!File.Exists(path))
                return 0; // Written once the first chunk (or the audio) finishes, so its absence means none have

            try
            {
                return JObject.Parse(ReadSharedText(path))["done"] is JObject done ? done.Count : 0;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary> av1an is writing these files while this reads them, so the share mode has to allow
        /// it - File.ReadAllText opens without FileShare.Write and fails outright on Windows for as long
        /// as av1an holds the file. </summary>
        static string ReadSharedText(string path)
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return new StreamReader(stream).ReadToEnd();
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
