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
            Logger.Log($"Muxing subs from input to all-subs.mkv", true, false, "ocr");
            string subsMkvPath = Path.Combine(tempDir, "subs.mkv");
            string ffArgs = $"-i {inPath.Wrap()} -map 0:s -c copy {subsMkvPath.Wrap()}";
            AvProcess.FfmpegSettings settings = new AvProcess.FfmpegSettings() { Args = ffArgs, LoggingMode = AvProcess.LogMode.Hidden };
            await AvProcess.RunFfmpeg(settings);

            // Copying the tracks into Matroska fails for formats it cannot store, mov_text among them.
            // Starting OCR tasks that have nothing to read is what used to leave the wait below spinning forever.
            if (!File.Exists(subsMkvPath))
            {
                Logger.Log($"Failed to extract the subtitle tracks from '{Path.GetFileName(inPath)}' - cannot run OCR on them.");
                return false;
            }

            Logger.Log($"starting {streams.Count} ocr tasks", true, false, "ocr");

            progressTracker.Clear(); // Entries left over from an earlier run would skew the average
            List<Task> tasks = new List<Task>();

            for (int i = 0; i < streams.Count; i++)
            {
                int iCopy = i;
                int subStreamIdx = file.SubtitleStreams.IndexOf(streams[iCopy]);

                // Not one of this file's tracks, so it is not in the extract either. Left in, it would
                // name a temp folder "-1" and ask Subtitle Edit for track number 0.
                if (subStreamIdx < 0)
                {
                    Logger.Log($"Skipping a subtitle track that does not belong to '{file.Name}'.", true, false, "ocr");
                    continue;
                }

                string srtDir = Path.Combine(tempDir, $"{subStreamIdx}");
                Directory.CreateDirectory(srtDir);
                tasks.Add(Task.Run(() => RunOcrOnSingleStream(tempDir, outDir, streams[iCopy], subStreamIdx)));
            }

            // Waiting on the tasks themselves rather than on a counter they increment as their last
            // statement: one that returns early or throws still completes the task, but never got as
            // far as the counter, so the wait could not end.
            Task allTasks = Task.WhenAll(tasks);

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

            if (!RunTask.canceled)
            {
                try
                {
                    await allTasks; // Already complete - awaited to surface what fire-and-forget swallowed
                }
                catch (Exception ex)
                {
                    Logger.Log($"An OCR task failed: {ex.Message}\n{ex.StackTrace}", true, false, "ocr");
                }
            }

            Logger.Log($"All OCR processes have finished.", false, Logger.LastUiLine.EndsWith("%"));

            return true;
        }

        public static async Task<bool> RunOcrOnSingleStream(string subTempDir, string finalOutDir, SubtitleStream ss, int subIndex)
        {
            if (IoUtils.GetAmountOfFiles(subTempDir, false, "*.mkv") < 1)
                return false;

            Logger.Log($"[Subtitle Stream {subIndex}] RunOcrOnSingleStream: Running OCR for subtitle stream", true, false, "ocr");
            string outPath = Path.Combine(finalOutDir, GetSrtName(subIndex, ss) + ".srt");
            string srtPath = Path.Combine(subTempDir, subIndex.ToString());

            string templateName = $"{ss.Language.Trim().ToLower()}.template";
            string replArg = File.Exists(Path.Combine(OcrProcess.GetDir(), templateName)) ? $"/multiplereplace:{templateName}" : "";

            await OcrProcess.RunSubtitleEdit($"/convert {Path.Combine(subTempDir, "subs.mkv")} srt /ocrengine:tesseract /track-number:{subIndex + 1} /outputfolder:{srtPath.Wrap()} {replArg}", true, true);
            Logger.Log($"[Subtitle Stream {subIndex}] RunOcrOnSingleStream: Fininshed OCR.", true, false, "ocr");
            Directory.CreateDirectory(finalOutDir);

            FileInfo[] srts = IoUtils.GetFileInfosSorted(srtPath, false, "*.srt");

            if (srts.Length < 1)
                Logger.Log($"Warning: OCR for {GetSrtName(subIndex, ss)} did not produce an output file - Possibly the track is empty.");
            else
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
                file.MoveTo(outPath);
            }
        }
    }
}
