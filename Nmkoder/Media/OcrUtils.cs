using Newtonsoft.Json;
using Nmkoder.Data;
using Nmkoder.Data.Streams;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.UI;
using Nmkoder.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nmkoder.Media
{
    class OcrUtils
    {
        public static ConcurrentDictionary<string, int> progressTracker = new ConcurrentDictionary<string, int>();

        /// <summary>
        /// Runs OCR over <paramref name="streams"/>, which have to be tracks of <paramref name="file"/>:
        /// they are located by their position among its subtitle streams, and read out of an extract
        /// taken from it. The file is passed in rather than read from the track list so that the two
        /// cannot describe different files.
        /// </summary>
        public static async Task<bool> RunOcrOnStreams(MediaFile file, List<SubtitleStream> streams, string outDir)
        {
            string inPath = file.ImportPath;
            string tempDir = Path.Combine(Paths.GetSessionDataPath(), "subs-temp");
            Directory.CreateDirectory(tempDir);
            IoUtils.DeleteContentsOfDir(tempDir);

            // Only the tracks that are about to be read. "-map 0:s" took every subtitle track the file
            // has, so one the extract's Matroska cannot hold - mov_text is the usual one - failed the
            // whole command, and with it the OCR of the bitmap tracks beside it, which were never the
            // problem and may not even have been in the same container originally.
            List<(SubtitleStream Stream, int SourceIndex)> mapped = streams
                .Select(s => (Stream: s, SourceIndex: file.SubtitleStreams.IndexOf(s)))
                .Where(x => x.SourceIndex >= 0)
                .ToList();

            // Not one of this file's tracks, so it would not be in the extract either. Left in, it
            // would name a temp folder "-1" and ask Subtitle Edit for track number 0.
            for (int i = 0; i < streams.Count - mapped.Count; i++)
                Logger.Log($"Skipping a subtitle track that does not belong to '{file.Name}'.", true, false, "ocr");

            if (mapped.Count < 1)
            {
                Logger.Log($"None of the selected subtitle tracks belong to '{file.Name}', so there is nothing to run OCR on.");
                return false;
            }

            Logger.Log($"Muxing {mapped.Count} subtitle track{(mapped.Count == 1 ? "" : "s")} from the input to subs.mkv", true, false, "ocr");
            string subsMkvPath = Path.Combine(tempDir, "subs.mkv");
            string maps = string.Join(" ", mapped.Select(x => $"-map 0:s:{x.SourceIndex}"));
            string ffArgs = $"-i {inPath.Wrap()} {maps} -c copy {subsMkvPath.Wrap()}";
            AvProcess.FfmpegSettings settings = new AvProcess.FfmpegSettings() { Args = ffArgs, LoggingMode = AvProcess.LogMode.Hidden };
            await AvProcess.RunFfmpeg(settings);

            // Copying the tracks into Matroska fails for formats it cannot store, mov_text among them.
            // Starting OCR tasks that have nothing to read is what used to leave the wait below spinning forever.
            //
            // Existence is not the test, though it was: ffmpeg creates the output before it writes the
            // header, so a codec Matroska refuses leaves a couple of hundred bytes of unreadable stub
            // behind and File.Exists says yes. Measured with a mov_text source, which wrote 291 bytes
            // that ffprobe answers "End of file" to. Counting the streams in it is the question that
            // actually distinguishes the two.
            // noCache: subs.mkv is a fixed path rewritten by every run, and the cache behind
            // GetStreamCount keys on path and size alone - so two extracts of the same length would
            // answer with the first one's streams.
            if (!File.Exists(subsMkvPath) || await FfmpegUtils.GetStreamCount(subsMkvPath, noCache: true) < mapped.Count)
            {
                Logger.Log($"Failed to extract the subtitle tracks from '{Path.GetFileName(inPath)}' - cannot run OCR on them. ffmpeg's own output is in the log.");
                return false;
            }

            Logger.Log($"starting {mapped.Count} ocr tasks", true, false, "ocr");

            progressTracker.Clear(); // Entries left over from an earlier run would skew the average
            List<Task<bool>> tasks = new List<Task<bool>>();

            for (int i = 0; i < mapped.Count; i++)
            {
                (SubtitleStream Stream, int SourceIndex) entry = mapped[i];

                // subs.mkv now holds only the mapped tracks, in the order they were mapped, so the
                // number Subtitle Edit wants is the position in that extract - not the track's index
                // in the source file, which is what names the output and the temp folder.
                int trackNumber = i + 1;
                Directory.CreateDirectory(Path.Combine(tempDir, $"{entry.SourceIndex}"));
                tasks.Add(Task.Run(() => RunOcrOnSingleStream(tempDir, outDir, entry.Stream, entry.SourceIndex, trackNumber)));
            }

            // Waiting on the tasks themselves rather than on a counter they increment as their last
            // statement: one that returns early or throws still completes the task, but never got as
            // far as the counter, so the wait could not end.
            Task<bool[]> allTasks = Task.WhenAll(tasks);

            while (!allTasks.IsCompleted)
            {
                if (RunTask.canceled)
                    break;

                if (progressTracker.Count > 0)
                {
                    int progSum = progressTracker.Select(x => x.Value).Sum();
                    int avgProg = ((float)progSum / progressTracker.Count).RoundToInt();
                    int running = progressTracker.Where(x => x.Value < 100).Count();
                    Logger.Log($"Running {running} OCR Instances - Average Progress: {avgProg}%", false, Logger.LastUiLine.EndsWith("%"));
                    Program.MainWin?.SetProgress(avgProg);
                }

                await Task.Delay(100);
            }

            // A cancelled run is not a failed one, and reporting it as one puts an error box on top of
            // the cancellation the user just asked for.
            if (RunTask.canceled)
                return true;

            try
            {
                await allTasks; // Already complete - awaited to surface what fire-and-forget swallowed
            }
            catch (Exception ex)
            {
                Logger.Log($"An OCR task failed: {ex.Message}\n{ex.StackTrace}", true, false, "ocr");
            }

            Logger.Log($"All OCR processes have finished.", false, Logger.LastUiLine.EndsWith("%"));

            // Counted per task rather than off the array WhenAll returns: that array cannot be had at
            // all once any one task has faulted, since awaiting it rethrows - so one track throwing
            // would discard the SRTs every other track had just finished writing, and report the lot
            // as a failure.
            int written = tasks.Count(t => t.IsCompletedSuccessfully && t.Result);
            int faulted = tasks.Count(t => t.IsFaulted);

            if (faulted > 0)
                Logger.LogWarn($"{faulted} of {tasks.Count} OCR task{(faulted == 1 ? "" : "s")} failed outright - the log has the error.");

            // This used to return true whatever happened, so a run where every track threw or wrote
            // nothing still ended "Done" with an empty output folder. Only the extract failing above
            // could ever reach the caller.
            if (written < 1)
                return false;

            if (written < tasks.Count)
                Logger.Log($"{tasks.Count - written} of {tasks.Count} subtitle tracks produced no output - see the lines above.");

            return true;
        }

        /// <summary> Runs one track through Subtitle Edit. True only if an SRT came out of it. </summary>
        public static async Task<bool> RunOcrOnSingleStream(string subTempDir, string finalOutDir, SubtitleStream ss, int subIndex, int trackNumber)
        {
            if (IoUtils.GetAmountOfFiles(subTempDir, false, "*.mkv") < 1)
                return false;

            Logger.Log($"[Subtitle Stream {subIndex}] RunOcrOnSingleStream: Running OCR for subtitle stream", true, false, "ocr");
            string outPath = Path.Combine(finalOutDir, GetSrtName(subIndex, ss) + ".srt");
            string srtPath = Path.Combine(subTempDir, subIndex.ToString());

            string templateName = $"{ss.Language.Trim().ToLower()}.template";
            string replArg = File.Exists(Path.Combine(OcrProcess.GetDir(), templateName)) ? $"/multiplereplace:{templateName}" : "";

            // The input path goes through a shell like every other argument here, so it is wrapped -
            // the session data folder sits under the user's profile and a space in a name is ordinary.
            string subsMkv = Path.Combine(subTempDir, "subs.mkv").Wrap();

            await OcrProcess.RunSubtitleEdit($"/convert {subsMkv} srt /ocrengine:tesseract /track-number:{trackNumber} /outputfolder:{srtPath.Wrap()} {replArg}", true, true);
            Logger.Log($"[Subtitle Stream {subIndex}] RunOcrOnSingleStream: Finished OCR.", true, false, "ocr");
            Directory.CreateDirectory(finalOutDir);

            FileInfo[] srts = IoUtils.GetFileInfosSorted(srtPath, false, "*.srt");

            if (srts.Length < 1)
            {
                Logger.Log($"Warning: OCR for {GetSrtName(subIndex, ss)} did not produce an output file - Possibly the track is empty.");
                return false;
            }

            PostprocessAndMove(srts[0], subIndex, ss, outPath);
            return true;
        }

        static string GetSrtName(int subIndex, SubtitleStream ss)
        {
            string title = ss.Title.CleanString().Trunc(30, false).Trim();
            string lang = ss.Language.CleanString().ToUpper().Trunc(3, false).Trim();
            return $"SubtitleTrack{subIndex}-Index{ss.Index}{(title.Length > 0 ? $"-{title}" : "")}{(lang.Length > 0 ? $"-{lang}" : "")}";
        }

        static void PostprocessAndMove(FileInfo file, int subIndex, SubtitleStream ss, string outPath)
        {
            string templateName = $"{ss.Language.Trim().ToLower()}.repl.json";
            string templateFile = Path.Combine(OcrProcess.GetDir(), templateName);

            if (File.Exists(templateFile))
            {
                Logger.Log($"[Subtitle Stream {subIndex}] PostprocessAndMove: There is a multi-replace file, applying it.", true, false, "ocr");
                Dictionary<string, string> findAndReplacePairs = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(templateFile));

                string srtText = File.ReadAllText(file.FullName);

                foreach (KeyValuePair<string, string> pair in findAndReplacePairs)
                    srtText = srtText.Replace(pair.Key, pair.Value);

                File.WriteAllText(outPath, srtText);
            }
            else
            {
                // MoveTo refuses an existing destination, which is exactly what a second run over the
                // same file meets - and the throw went to the catch that logs and carries on, so the
                // track quietly produced nothing while the task still reported success.
                File.Move(file.FullName, outPath, overwrite: true);
            }
        }
    }
}
