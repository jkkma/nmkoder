using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.Media;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nmkoder.Utils
{
    class ConcatUtils
    {
        public static async Task ConcatMkvMerge(List<string> paths, string outPath, bool print = true)
        {
            string parentDir = new FileInfo(paths.FirstOrDefault()).Directory.FullName;
            string parentPrefix = parentDir + Path.DirectorySeparatorChar;
            bool allInSameDir = paths.All(p => p.StartsWith(parentPrefix));

            if (allInSameDir)
                paths = paths.Select(p => p.Replace(parentPrefix, "")).ToList();

            List<string> commands = new List<string>();
            List<string> superChunkPaths = new List<string>();
            string superChunkBasePath = Directory.CreateDirectory($"{outPath}.merge.tmp").FullName;
            int superChunkIndex = 0;
            string currentCmd = GetBaseCmd(superChunkBasePath, superChunkIndex);
            bool first = true;

            for (int i = 0; i < paths.Count; i++)
            {
                if (currentCmd.Length > 7000)
                {
                    superChunkPaths.Add(Path.Combine(superChunkBasePath, $"{(superChunkIndex.ToString().PadLeft(3, '0'))}.mkv"));
                    superChunkIndex++;
                    commands.Add(currentCmd);
                    currentCmd = GetBaseCmd(superChunkBasePath, superChunkIndex);
                    first = true;
                }

                currentCmd += $" {(first ? "" : "+")}{paths[i].Wrap()}";
                first = false;

                if (i + 1 == paths.Count) // if this is the last iteration
                {
                    superChunkPaths.Add(Path.Combine(superChunkBasePath, $"{(superChunkIndex.ToString().PadLeft(3, '0'))}.mkv"));
                    commands.Add(currentCmd);
                }

                Logger.Log($"Concat: Added chunk #{i} to superchunk {superChunkIndex} - Command length is {currentCmd.Length}", true);
            }

            for (int i = 0; i < commands.Count; i++)
            {
                if (print)
                {
                    Logger.Log($"Writing chunk {i + 1}/{commands.Count}...", false, Logger.LastUiLine.Contains("Writing chunk"));
                    int percent = (((float)(i + 1) / commands.Count) * 100f).RoundToInt();
                    Program.MainWin?.SetProgress(percent);
                }

                await AvProcess.RunMkvMerge(commands[i], OS.NmkoderProcess.ProcessType.Secondary, false, allInSameDir ? parentDir : null);
            }

            await ConcatMkvMergeSingle(superChunkPaths, outPath, print);
            superChunkPaths.ForEach(x => IoUtils.TryDeleteIfExists(x));
            IoUtils.TryDeleteIfExists(superChunkBasePath);
        }

        private static string GetBaseCmd(string superChunkBasePath, int superChunkIndex)
        {
            return $" -o {Path.Combine(superChunkBasePath, $"{(superChunkIndex.ToString().PadLeft(3, '0'))}.mkv").Wrap()}";
        }

        private static async Task ConcatMkvMergeSingle(List<string> paths, string outPath, bool print)
        {
            if(paths.Count == 1)
            {
                File.Move(paths.First(), outPath);
                return;
            }

            string args = $" -o {outPath.Wrap()}";

            for (int i = 0; i < paths.Count; i++)
                args += $" {(i == 0 ? "" : "+")}{paths[i].Wrap()}";

            if (args.Length > 8000)
            {
                RunTask.Fail("The merge command is too long for the OS to accept. Move Nmkoder - or the input files - to a folder with a shorter path, or concatenate fewer files at a time.");
                return;
            }

            if (print)
                Logger.Log($"Merging...");

            await AvProcess.RunMkvMerge(args, OS.NmkoderProcess.ProcessType.Secondary, false);

            if (!File.Exists(outPath))
                RunTask.Fail($"The files were not merged - '{Path.GetFileName(outPath)}' was not written. mkvmerge's own output is in mkvmerge.txt, in the log folder.");
            else
                Logger.Log($"Saved concatenated file to {outPath}.");
        }
    }
}
