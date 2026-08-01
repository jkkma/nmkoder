using Nmkoder.Data;
using Nmkoder.Data.Ui;
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
                List<FileListEntry> fileListEntries = FileList.Items.ToList();
                List<string> paths = fileListEntries.Where(x => x.File.ImportPath == x.File.SourcePath).Select(x => x.File.ImportPath).ToList();
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
