using Newtonsoft.Json.Linq;
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
            chunkFailures = 0; // Static for the same reason - a batch would otherwise carry one file's failures onto the next
            explainedChunkFailure = false;
            reportedChunkFailures = false;
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
            ReportChunkFailures(); // Called twice per run by AvProcess, which is what the guard inside is for
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

            NoteChunkFailure(line);

            if (fatal)
                RunTask.Cancel($"Error: {line}");
        }

        /// <summary> Chunks av1an has failed this run, and whether the first has been explained. </summary>
        static int chunkFailures;
        static bool explainedChunkFailure;
        static bool reportedChunkFailures;

        static readonly Regex frameMismatch = new Regex(@"FRAME MISMATCH: chunk (\d+): (\d+)/(\d+)");

        /// <summary>
        /// Says what a failed chunk means, once, in a line that survives.
        /// <para/>
        /// It has to be written by this app because av1an's own account of it says nothing a user can
        /// act on - "encoder crashed: exit code: 0", a frame count, and a source pipe reporting errno 32
        /// - and because <b>nothing about it reached the screen at all</b>. Every encode logs through
        /// <see cref="LogMode.OnlyLastLine"/>, which rewrites the last row on each progress line, and a
        /// Warning is replaceable by design (a damaged source prints scores of them and pinning each
        /// would bury the log) - so av1an's "Encoder failed (on chunk 1)" was overwritten within a
        /// fraction of a second, every time, and a run that had already lost four chunks read as a
        /// healthy one sitting at 4%. This is levelled the same way and simply not replaced: it is
        /// written once per run however many chunks fail, so it cannot bury anything either.
        /// <para/>
        /// A retried chunk is still not a failure - av1an's exit code decides that, exactly as ffmpeg's
        /// does - which is why this explains rather than cancelling.
        /// </summary>
        static void NoteChunkFailure(string line)
        {
            Match match = frameMismatch.Match(line);

            if (!match.Success)
                return;

            chunkFailures++;

            if (explainedChunkFailure)
                return;

            explainedChunkFailure = true;
            int actual = match.Groups[2].Value.GetInt();
            int expected = match.Groups[3].Value.GetInt();

            // Short and long are opposite faults and the fix for one is no use against the other. Short
            // means the frames stopped arriving: the encoder reached the end of a truncated stream,
            // finished normally and exited 0, so the only process that actually went wrong is one av1an
            // does not report the death of. Long means something in the chain wrote more frames than it
            // read, which is the case --ignore-frame-mismatch exists for and Av1an.Run already sends it
            // for - so reaching here means a filter changed the count without this app knowing.
            string explanation = actual < expected
                ? $"chunk {match.Groups[1].Value} came out {expected - actual} frames short of the {expected} it was " +
                  $"given. That is the frames stopping rather than the encoder going wrong - it reads until the stream " +
                  $"ends and exits 0 on whatever arrived - and the commonest reason is the machine running out of memory " +
                  $"and killing the process feeding it. Lower Workers on the Av1an Options tab and run it again."
                : $"chunk {match.Groups[1].Value} came out {actual - expected} frames longer than the {expected} it was " +
                  $"given, so something in the filter chain is writing more frames than it reads. Check the custom " +
                  $"filters on this tab - a filter that changes the frame count needs av1an's --ignore-frame-mismatch, " +
                  $"which is only sent for the Frame Rate box.";

            Logger.LogWarn($"AV1AN failed a chunk: {explanation} av1an retries a failed chunk a few times and then " +
                $"gives up on the whole encode, so this is worth acting on rather than waiting out.", "av1an");
        }

        /// <summary> The tally, once the run is over. Separate from the explanation above because the
        /// count is only interesting at the end - during the run it is a number that keeps changing on a
        /// row nobody is watching. </summary>
        static void ReportChunkFailures()
        {
            if (reportedChunkFailures || chunkFailures < 1)
                return;

            reportedChunkFailures = true;

            if (chunkFailures > 1)
                Logger.LogWarn($"AV1AN failed {chunkFailures} chunks during this run - see the reason above. Each one " +
                    $"was encoded and thrown away, so the run took longer than it needed to whether or not it finished.", "av1an");
        }

        /// <summary>
        /// The end of av1an's own log file, for a run whose exit code says it failed - the one place its
        /// reason survives every launch mode. With the visible console (the Windows default) this app
        /// reads no process output at all: the window carries the error and then closes itself five
        /// seconds later, so a failed encode came back as nothing but "exit code 1" and the reason
        /// scrolled away unread. The log file has no such mode - <c>Av1an.GetLogFileArgs</c> names it
        /// into the temp folder for every run - so it is read here, once, after the failure.
        /// <para/>
        /// The harvested lines also go through <see cref="NoteChunkFailure"/>, because that explanation
        /// has to fire in console mode too: a FRAME MISMATCH in the tail is the same out-of-memory shape
        /// and calls for the same advice. Its once-per-run guard makes the second sight of a line the
        /// piped mode already saw free.
        /// <para/>
        /// The tail starts at the <b>first</b> warning-or-error marker inside the last stretch of the
        /// file, not the last one: av1an gives up moments after a chunk fails for the final time, so the
        /// failure sits at the end, and taking the last marker instead would cut a FRAME MISMATCH body
        /// off the "Encoder failed" header above it.
        /// </summary>
        public static string ReadFailureTail(string tempDir)
        {
            try
            {
                if (tempDir.IsEmpty())
                    return "";

                string path = Path.Combine(tempDir, "av1an.log");

                if (!File.Exists(path))
                    return ""; // An av1an without --log-file, or one old enough to append ".log" to the name

                List<string> lines = ReadSharedText(path).SplitIntoLines()
                    .Select(x => x.TrimEnd()).Where(x => x.Trim().IsNotEmpty()).ToList();

                List<string> window = lines.Skip(Math.Max(0, lines.Count - 40)).ToList();
                string[] markers = { "ERROR", "WARN", "error:", "panicked" };
                int first = window.FindIndex(x => markers.Any(x.Contains));

                // No marked line at all still returns something: whatever av1an said last is more of an
                // answer than the bare exit code the caller is otherwise left with.
                List<string> tail = (first >= 0 ? window.Skip(first) : window.Skip(Math.Max(0, window.Count - 8))).ToList();

                foreach (string line in tail)
                    NoteChunkFailure(line);

                ReportChunkFailures();
                return string.Join("\n", tail).Trim();
            }
            catch (Exception e)
            {
                Logger.Log($"Could not read av1an's log file: {e.Message}", true);
                return "";
            }
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

            // A run handed a pre-detected list (--scenes, see Av1anSceneDetect) may leave av1an
            // never writing its own copy into the temp folder - whether it does is that binary's
            // business - and without a total the bar would sit on "Scene detection..." for the whole
            // encode. The sidecar the list came from is the fallback: same schema, same numbers,
            // merged from files the same binary wrote.
            if (!File.Exists(path))
                path = UI.Tasks.Av1anUi.GetScenesFilePath(dir);

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
