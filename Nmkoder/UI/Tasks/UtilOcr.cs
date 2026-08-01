using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nmkoder.Data;
using Nmkoder.Data.Streams;
using Nmkoder.Data.Ui;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.Media;
using Nmkoder.Utils;
using Stream = Nmkoder.Data.Streams.Stream;

namespace Nmkoder.UI.Tasks
{
    class UtilOcr
    {
        public static async Task Run ()
        {
            Program.MainWin.SetWorking(true);

            // Every exit below used to leave the window in its working state, which is unrecoverable
            // without restarting - and two of them are the ordinary "nothing to do here" cases.
            try
            {
                MediaFile file = TrackList.current?.File;

                if (file == null)
                {
                    RunTask.Fail("No input file loaded! Please load one first (File List).");
                    return;
                }

                // This runs on the loaded file alone: the tracks are extracted from it and the output
                // folder is named after it. Tracks belonging to other files in the list are not in that
                // extract, and looking them up in it returns no position at all, so they are named and
                // skipped rather than taken for whichever track happens to sit where they are not.
                List<StreamListEntry> checkedSubs = TrackList.CheckedItems.Where(x => x.Stream.Type == Stream.StreamType.Subtitle).ToList();
                List<StreamListEntry> otherFiles = checkedSubs.Where(x => x.MediaFile == null || x.MediaFile.ImportPath != file.ImportPath).ToList();
                List<SubtitleStream> subStreams = checkedSubs.Except(otherFiles).Select(x => (SubtitleStream)x.Stream).ToList();

                List<SubtitleStream> streamsText = subStreams.Where(x => !x.Bitmap).ToList();
                List<SubtitleStream> streamsBitmap = subStreams.Where(x => x.Bitmap).ToList();

                if (!Directory.Exists(OcrProcess.GetDir()) || IoUtils.GetAmountOfFiles(OcrProcess.GetDir(), true, "*.exe") < 1)
                {
                    RunTask.Fail("The OCR binaries are not there. They ship with the Windows build only - this build cannot run OCR.");
                    return;
                }

                if (otherFiles.Count > 0)
                    Logger.Log($"Skipping {otherFiles.Count} checked subtitle track{(otherFiles.Count == 1 ? "" : "s")} from other files - " +
                        $"OCR runs on the loaded file only ('{file.Name.Trunc(40)}'). Load another file to convert its tracks.");

                if (streamsBitmap.Count < 1)
                {
                    // A failure rather than a quiet no-op: the user picked this utility and ticked
                    // tracks, and "finished" over having done nothing is the wrong answer.
                    RunTask.Fail("None of the selected subtitle tracks are image-based, so there is nothing to run OCR on. Tick a bitmap track (PGS, VobSub) in the Track List.");
                    return;
                }

                string outDirName = Path.GetFileNameWithoutExtension(file.ImportPath).CleanString().Trunc(50, false) + "-Subtitles";
                string outDir = Path.Combine(new FileInfo(file.ImportPath).DirectoryName, outDirName);

                Logger.Log($"Preparing to run OCR on subtitle streams {string.Join(", ", streamsBitmap.Select(x => $"#{x.Index + 1}"))}.");

                // The return value used to be discarded, so the one hard failure OcrUtils reports -
                // the tracks not coming out of the file at all - could not reach RunTask, and the
                // task ended "Done".
                if (!await OcrUtils.RunOcrOnStreams(file, streamsBitmap, outDir))
                {
                    RunTask.Fail($"OCR could not be run on '{file.Name}'. The log has the details.");
                    return;
                }

                if (streamsText.Count > 0)
                    Logger.Log($"Won't run OCR on subtitle stream{(streamsText.Count == 1 ? "" : "s")} {string.Join(", ", streamsText.Select(x => $"#{x.Index + 1}"))} as they are text-based.");
            }
            finally
            {
                Program.MainWin.SetWorking(false);
            }
        }
    }
}
