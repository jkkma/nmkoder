using Nmkoder.Data;
using Nmkoder.Data.Ui;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.Media;
using Nmkoder.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nmkoder.UI.Tasks
{
    class UtilConcat
    {
        public static async Task Run()
        {
            if (RunTask.currentFileListMode == RunTask.FileListMode.Batch)
            {
                RunTask.Fail("The Concatenate utility only works in Muxing Mode - it joins the whole file list into one output, which is not something a batch can do per file.");
                return;
            }

            Program.MainWin.SetWorking(true);

            try
            {
                if (!AvProcess.IsToolAvailable("mkvmerge"))
                {
                    // Worth saying up front. Without it the run reaches mkvmerge, the shell reports
                    // "command not found" to a stream nothing here reads, and the first thing that
                    // notices is a File.Move onto a chunk that was never written - which surfaces as
                    // "Could not find file", naming a temp path the user has never heard of.
                    RunTask.Fail($"Concatenation is done with mkvmerge, which is not installed.\n\n{AvProcess.MkvToolNixInstallAdvice()}");
                    return;
                }

                List<FileListEntry> fileListEntries = FileList.Items.ToList();
                List<string> paths = fileListEntries.Where(x => x.File.ImportPath == x.File.SourcePath).Select(x => x.File.ImportPath).ToList();

                // An image sequence is imported as a generated concat file rather than as itself, so
                // it is not something mkvmerge can join. Skipping them is right; doing it without a
                // word left the count in the log disagreeing with the file list for no visible reason.
                int skipped = fileListEntries.Count - paths.Count;

                if (skipped > 0)
                    Logger.Log($"Skipping {skipped} image sequence{(skipped == 1 ? "" : "s")} - only regular files can be concatenated.");

                if (paths.Count < 1)
                {
                    RunTask.Fail("There is nothing here to concatenate. Load two or more video files into the file list - image sequences cannot be joined this way.");
                    return;
                }

                if (paths.Count < 2)
                {
                    RunTask.Fail("Concatenation joins the file list into one output, so it needs at least two files. Only one is loaded.");
                    return;
                }

                string filename = new FileInfo(paths[0]).Directory.Name + "-merge.mkv";
                string outPath = Path.Combine(new FileInfo(paths[0]).Directory.FullName, filename);
                IoUtils.TryDeleteIfExists(outPath);
                await ConcatUtils.ConcatMkvMerge(paths, outPath);
            }
            catch (Exception e)
            {
                RunTask.Fail($"The files could not be concatenated: {e.Message}");
                Logger.Log($"{e.StackTrace}", true, level: Logger.Level.Debug);
            }

            Program.MainWin.SetWorking(false);
        }
    }
}
