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
        static int currentTotalFrames;
        static bool stopProgressLoop;
        public static Task currentLogReaderTask;

        /// <summary> What one pass round the loop found in av1an's temp folder. Frames are what the
        /// progress is measured in; the chunk counts ride along for the readout. </summary>
        readonly struct Av1anProgress
        {
            public readonly int Chunks;
            public readonly int Frames;
            /// <summary> The whole video's frame count, which done.json also carries - the fallback for
            /// a scenes.json that does not state it. 0 where it is not known. </summary>
            public readonly int TotalFrames;

            public Av1anProgress(int chunks, int frames, int totalFrames)
            {
                Chunks = chunks;
                Frames = frames;
                TotalFrames = totalFrames;
            }

            /// <summary> A file caught half-written. The caller keeps the counts it already had. </summary>
            public static readonly Av1anProgress Unreadable = new Av1anProgress(-1, -1, 0);
        }

        /// <summary> Starts reading progress out of the current encode's log file. Call once per av1an run. </summary>
        public static void StartProgressLoop(string av1anArgs)
        {
            stopProgressLoop = false;
            currentQueueSize = 0; // Static, so it has to be cleared or the next encode inherits this one's chunk count
            currentTotalFrames = 0;
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
        /// <para/>
        /// Progress is counted in <b>frames</b> rather than in chunks, which is what av1an itself counts
        /// and the only way the two agree. Chunks are not the same size - <c>-x</c> caps them but nothing
        /// makes them uniform - and av1an encodes them <i>longest first</i> by default, so the finished
        /// ones are the big ones and a chunk count understates the progress for most of a run, badly.
        /// Measured on a 304-chunk encode: 36 chunks done is 12% of the queue and 25% of the video, and
        /// the ETA extrapolated from the chunk count came to 37 minutes against av1an's own 12.
        /// <para/>
        /// It reads a little low, and the amount is bounded: the frames counted are those of the
        /// <i>finished</i> chunks, where av1an's own bar also counts the part-encoded frames of the
        /// chunks in flight, which it learns from each encoder's stderr and nothing writes down. That is
        /// one part-chunk per worker at any moment - a roughly constant offset rather than a growing
        /// one, so it costs the readout a few percent and the ETA very little. <c>done.json</c> is where
        /// av1an takes its own bitrate and size estimates from for the same reason.
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
            int encodedFrames = 0;
            // What was already finished when this run started. Zero for a fresh encode, and the whole
            // of the previous attempt for a resumed one - which the stopwatch beside it knows nothing
            // about, so the estimate has to extrapolate from what *this* run has encoded.
            int inheritedChunks = -1;
            int inheritedFrames = 0;

            while (Directory.Exists(dir))
            {
                try
                {
                    if (currentQueueSize == 0) // Fixed once scene detection has written it, so it is only read until then
                        ReadScenesFile(dir);

                    Av1anProgress done = GetDoneProgress(dir);

                    if (done.Chunks >= 0) // Otherwise the file was caught half-written; the counts already in hand are the better answer
                    {
                        encodedChunks = done.Chunks;
                        encodedFrames = done.Frames;

                        if (inheritedChunks < 0)
                        {
                            inheritedChunks = done.Chunks;
                            inheritedFrames = done.Frames;
                        }

                        // done.json states the total as well, and is the only one that does for a scenes
                        // file written by an av1an old enough not to have carried it.
                        if (currentTotalFrames <= 0)
                            currentTotalFrames = done.TotalFrames;
                    }

                    if (currentQueueSize > 0) // Nothing to report until scene detection has announced a queue size
                    {
                        // Frames wherever the total is known, which is everywhere av1an has written one.
                        // The chunk count is the fallback rather than the measure - see the note above.
                        bool byFrames = currentTotalFrames > 0;
                        int ratio = byFrames
                            ? FormatUtils.RatioInt(encodedFrames, currentTotalFrames)
                            : FormatUtils.RatioInt(encodedChunks, currentQueueSize);
                        Program.MainWin?.SetProgress(ratio);

                        string etaStr = "";
                        int chunksThisRun = encodedChunks - Math.Max(0, inheritedChunks);

                        // Needs more finished chunks than there are workers to extrapolate from: the
                        // first chunk out of every worker started at the same moment, so until they
                        // have all landed the elapsed time is the pipeline filling rather than a rate.
                        if (chunksThisRun > workers && encodedChunks < currentQueueSize)
                        {
                            if (!etas.ContainsKey(encodedChunks)) // Cached per chunk count so the estimate doesn't jitter within a chunk
                            {
                                float elapsedSecs = (float)sw.ElapsedMs / 1000;

                                etas[encodedChunks] = byFrames
                                    ? ((currentTotalFrames - encodedFrames) * (elapsedSecs / Math.Max(1, encodedFrames - inheritedFrames))).RoundToInt()
                                    : ((currentQueueSize - encodedChunks) * (elapsedSecs / chunksThisRun)).RoundToInt();
                            }

                            // "<" only for the chunk fallback, where it is a ceiling rather than an
                            // estimate: longest-first means the chunks left are the short ones, so
                            // seconds-per-chunk only ever falls. A frame rate has no such bias.
                            etaStr = $" ETA: {(byFrames ? "" : "<")}{FormatUtils.Time(new TimeSpan(0, 0, etas[encodedChunks]), false)}";
                        }

                        string progStr = byFrames
                            ? $"{encodedFrames}/{currentTotalFrames} frames ({ratio}%), {encodedChunks}/{currentQueueSize} chunks"
                            : $"{encodedChunks}/{currentQueueSize} chunks ({ratio}%)";

                        Logger.Log($"AV1AN is running - Encoded {progStr}.{etaStr}", false, Logger.LastUiLine.Contains("Encoded"));
                        RunTask.ReportProgress($"Encoding - {progStr}{(etaStr.IsEmpty() ? "" : $" -{etaStr}")}");
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
        /// <para/>
        /// The same file states the video's whole frame count, which is what the progress is measured
        /// against, so both are taken from the one parse - and both are taken here rather than from
        /// <c>done.json</c> because this file exists as soon as scene detection is finished, where that
        /// one carries nothing useful until the first chunk lands.
        /// </summary>
        static void ReadScenesFile(string dir)
        {
            string path = Path.Combine(dir, "scenes.json");

            if (!File.Exists(path))
                return;

            try
            {
                JObject root = JObject.Parse(ReadSharedText(path));

                foreach (string key in new[] { "split_scenes", "scenes" })
                {
                    if (root[key] is JArray scenes)
                    {
                        currentQueueSize = scenes.Count;
                        break;
                    }
                }

                currentTotalFrames = (int?)root["frames"] ?? 0;
            }
            catch (Exception e)
            {
                // Includes catching the file mid-write, which is not worth a line of its own - the next
                // pass round the loop is a second away and the count is not needed before then.
                Logger.Log($"Failed to read av1an's scene list: {e.Message}", true);
            }
        }

        /// <summary>
        /// What has finished, from the file av1an rewrites after each chunk - the same file it resumes
        /// from, so the counts survive a resume rather than restarting at zero.
        /// <para/>
        /// Its shape is <c>{"frames": 34048, "done": {"00000": {"frames": 240, "size_bytes": …}, …}}</c>:
        /// the total, then one entry per finished chunk carrying that chunk's own frame count. Summing
        /// those is how av1an works out its own bitrate and size estimates.
        /// <para/>
        /// <see cref="Av1anProgress.Unreadable"/> means the file could not be read this time round, which
        /// a file rewritten this often will occasionally be. That is not "nothing is done yet": the
        /// caller keeps the counts it already had rather than letting the bar jump back to the start.
        /// </summary>
        static Av1anProgress GetDoneProgress(string dir)
        {
            string path = Path.Combine(dir, "done.json");

            if (!File.Exists(path))
                return new Av1anProgress(0, 0, 0); // Written once the first chunk (or the audio) finishes, so its absence means none have

            try
            {
                JObject root = JObject.Parse(ReadSharedText(path));
                int total = (int?)root["frames"] ?? 0;

                if (!(root["done"] is JObject done))
                    return new Av1anProgress(0, 0, total);

                int frames = done.Properties().Sum(x => (int?)x.Value["frames"] ?? 0);
                return new Av1anProgress(done.Count, frames, total);
            }
            catch
            {
                return Av1anProgress.Unreadable;
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
