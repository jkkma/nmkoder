using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using static Nmkoder.Media.AvProcess;

namespace Nmkoder.Media
{
    partial class FfmpegExtract : FfmpegCommands
    {
        public static async Task ExtractSingleFrame(string inputFile, string outputPath, int frameNum, int maxH = 2160)
        {
            bool isPng = (Path.GetExtension(outputPath).ToLower() == ".png");
            string comprArg = isPng ? pngCompr : "-q:v 1";
            string pixFmt = "-pix_fmt " + (isPng ? $"rgb24 {comprArg}" : "yuvj420p");
            Size res = await GetMediaResolutionCached.GetSizeAsync(inputFile);
            string vf = res.Height > maxH ? $"-vf scale=-1:{maxH.RoundMod(2)}" : "";
            string args = $"-i {inputFile.Wrap()} -vf \"select=eq(n\\,{frameNum})\" -vframes 1 {pixFmt} {vf} {outputPath.Wrap()}";
            FfmpegSettings settings = new FfmpegSettings() { Args = args, LoggingMode = LogMode.Hidden, CanCancelTask = false };
            await RunFfmpeg(settings);
        }

        public static async Task ExtractSingleFrameAtTime(string inputFile, string outputPath, int skipSeconds, int maxH = 2160, bool noKey = false)
        {
            NmkdStopwatch sw = new NmkdStopwatch();
            bool isPng = (Path.GetExtension(outputPath).ToLower() == ".png");
            string comprArg = isPng ? pngCompr : "-q:v 1";
            string pixFmt = "-pix_fmt " + (isPng ? $"rgb24 {comprArg}" : "yuvj420p");
            Size res = await GetMediaResolutionCached.GetSizeAsync(inputFile);
            string vf = res.Height > maxH ? $"-vf scale=-1:{maxH.RoundMod(2)}" : "";
            string noKeyArg = noKey ? "-skip_frame nokey" : "";
            string args = $"{noKeyArg} -ss {skipSeconds} -i {inputFile.Wrap()} -map 0:v -vframes 1 {pixFmt} {vf} {outputPath.Wrap()}";
            FfmpegSettings settings = new FfmpegSettings() { Args = args, LoggingMode = LogMode.Hidden, CanCancelTask = false };
            await RunFfmpeg(settings);
        }

        /// <summary>
        /// One frame at an exact position, for scrubbing a preview. Seeking before -i is accurate in
        /// current ffmpeg - it lands on the preceding keyframe and decodes forward to the wanted
        /// timestamp - and it is fast enough to drag a slider with, which output seeking is not.
        /// </summary>
        public static async Task ExtractSingleFrameAtMs(string inputFile, string outputPath, long ms, int maxH = 2160)
        {
            Size res = await GetMediaResolutionCached.GetSizeAsync(inputFile);
            string vf = res.Height > maxH ? $"-vf scale=-2:{maxH.RoundMod(2)}" : "";
            string time = (Math.Max(0, ms) / 1000d).ToString("0.###", CultureInfo.InvariantCulture);
            string args = $"-ss {time} -i {inputFile.Wrap()} -map 0:v:0 -frames:v 1 -update 1 -pix_fmt yuvj420p -q:v 2 {vf} {outputPath.Wrap()}";
            FfmpegSettings settings = new FfmpegSettings() { Args = args, LoggingMode = LogMode.Hidden, CanCancelTask = false, ProcessType = OS.NmkoderProcess.ProcessType.Background };
            await RunFfmpeg(settings);
        }

        public static async Task ExtractThumbs(string inputFile, string outputDir, int amount, int maxH = 360, string format = "jpg")
        {
            long duration = (int)Math.Floor((float)(await GetDurationMs(inputFile)) / 1000);
            int interval = (int)Math.Floor((float)duration / amount);

            Logger.Log($"Thumbnail Interval: {duration}/{amount} = {interval}", true);

            List<Task> tasks = new List<Task>();

            for (int i = 0; i < amount; i++)
            {
                int time = interval * (i + 1);
                tasks.Add(ExtractSingleFrameAtTime(inputFile, Path.Combine(outputDir, $"thumb{i + 1}-s{time}.{format}"), time, maxH, false));
            }

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Dumps a file's attachments into a folder beside it and hands back the folder, or "" when
        /// nothing came out. The empty answer is the point: the caller opens the folder, and a failed
        /// extraction used to open an empty one with nothing anywhere saying why.
        /// </summary>
        public static async Task<string> ExtractAttachments(string inputFile, int index = -1)
        {
            string outputDir = $"{inputFile} Attachments";

            try
            {
                Directory.CreateDirectory(outputDir);
            }
            catch (Exception e)
            {
                Logger.LogErr($"Could not create '{Path.GetFileName(outputDir)}': {e.Message}");
                return "";
            }

            await ExtractAttachments(inputFile, outputDir, index);

            if (IoUtils.GetFileInfosSorted(outputDir, true, "*").Length > 0)
                return outputDir;

            Logger.LogErr($"Nothing was extracted from '{Path.GetFileName(inputFile)}' - FFmpeg wrote no attachment files. The log has its output.");
            IoUtils.TryDeleteIfExists(outputDir); // An empty folder beside the source is worse than none
            return "";
        }

        public static async Task ExtractAttachments (string inputFile, string outputDir, int index = -1)
        {
            string idx = index < 0 ? ":t" : $":{index}";
            string args = $"-dump_attachment{idx} \"\" -i {inputFile.Wrap()}";
            // ffmpeg has nothing to mux here, so it ends with "Output file #0 does not contain any
            // stream" and a non-zero code even when every attachment came out. What was written is
            // the only usable answer, which is why the caller counts files rather than reading this.
            FfmpegSettings settings = new FfmpegSettings() { Args = args, WorkingDir = outputDir, LogLevel = "error", LoggingMode = LogMode.Hidden, CanCancelTask = false };
            await RunFfmpeg(settings);
        }

        //public static async Task ExtractLastFrame(string inputFile, string outputPath, Size size)
        //{
        //    if (QuickSettingsTab.trimEnabled)
        //        return;
        //
        //    if (IoUtils.IsPathDirectory(outputPath))
        //        outputPath = Path.Combine(outputPath, "last.png");
        //
        //    bool isPng = (Path.GetExtension(outputPath).ToLower() == ".png");
        //    string comprArg = isPng ? pngCompr : "";
        //    string pixFmt = "-pix_fmt " + (isPng ? $"rgb24 {comprArg}" : "yuvj420p");
        //    string sizeStr = (size.Width > 1 && size.Height > 1) ? $"-s {size.Width}x{size.Height}" : "";
        //    string trim = QuickSettingsTab.trimEnabled ? $"-ss {QuickSettingsTab.GetTrimEndMinusOne()} -to {QuickSettingsTab.trimEnd}" : "";
        //    string sseof = string.IsNullOrWhiteSpace(trim) ? "-sseof -1" : "";
        //    string args = $"{sseof} -i {inputFile.Wrap()} -update 1 {pixFmt} {sizeStr} {trim} {outputPath.Wrap()}";
        //    await RunFfmpeg(args, LogMode.Hidden, TaskType.ExtractFrames);
        //}
    }
}
