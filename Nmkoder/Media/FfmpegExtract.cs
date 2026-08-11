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
            string filters = await GetPreviewFilters(inputFile, maxH);
            // The rest of the chain joins the selection in a single -vf: ffmpeg takes the last -vf per
            // stream, so a second one would silently replace the frame selection whenever it kicked in.
            string vf = $"-vf \"select=eq(n\\,{frameNum}){(filters.Length > 0 ? $",{filters}" : "")}\"";
            string args = $"-i {inputFile.Wrap()} {vf} -vframes 1 {pixFmt} {outputPath.Wrap()}";
            FfmpegSettings settings = new FfmpegSettings() { Args = args, LoggingMode = LogMode.Hidden, CanCancelTask = false };
            await RunFfmpeg(settings);
        }

        public static async Task ExtractSingleFrameAtTime(string inputFile, string outputPath, int skipSeconds, int maxH = 2160, bool noKey = false)
        {
            NmkdStopwatch sw = new NmkdStopwatch();
            bool isPng = (Path.GetExtension(outputPath).ToLower() == ".png");
            string comprArg = isPng ? pngCompr : "-q:v 1";
            string pixFmt = "-pix_fmt " + (isPng ? $"rgb24 {comprArg}" : "yuvj420p");
            string filters = await GetPreviewFilters(inputFile, maxH);
            string vf = filters.Length > 0 ? $"-vf {filters.Wrap()}" : "";
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
            string filters = await GetPreviewFilters(inputFile, maxH);
            string vf = filters.Length > 0 ? $"-vf {filters.Wrap()}" : "";
            string time = (Math.Max(0, ms) / 1000d).ToString("0.###", CultureInfo.InvariantCulture);
            string args = $"-ss {time} -i {inputFile.Wrap()} -map 0:v:0 -frames:v 1 -update 1 -pix_fmt yuvj420p -q:v 2 {vf} {outputPath.Wrap()}";
            FfmpegSettings settings = new FfmpegSettings() { Args = args, LoggingMode = LogMode.Hidden, CanCancelTask = false, ProcessType = OS.NmkoderProcess.ProcessType.Background };
            await RunFfmpeg(settings);
        }

        /// <summary>
        /// The scale these previews share: height capped, and anamorphic sources de-squeezed so the
        /// image shows the shape the video plays at rather than the one it is stored at.
        /// </summary>
        private static async Task<string> GetPreviewScale(string inputFile, int maxH)
        {
            Size res = await GetMediaResolutionCached.GetSizeAsync(inputFile);
            Size sar = await FfmpegUtils.GetSampleAspectRatio(inputFile);
            return FfmpegUtils.GetPreviewScaleFilter(res, sar, maxH);
        }

        /// <summary>
        /// The whole <c>-vf</c> for a preview: the scale, then a tone map where the source is HDR.
        /// <para/>
        /// **A preview of an HDR file drawn without one is not a neutral picture of it - it is wrong,
        /// and wrong in the direction that looks like a fault in this app.** PQ code values shown as
        /// though they were BT.709 come out washed-out and grey, which is what every thumbnail, the Cut
        /// window's scrubber and the crop preview showed for exactly the files the Tone Mapping row
        /// exists for. The row itself was answering correctly the whole time; only the pictures beside
        /// it were not.
        /// <para/>
        /// It reuses <see cref="ToneMapConfig"/> rather than composing a chain of its own, and gets the
        /// CPU one: a fresh config has <see cref="ToneMapConfig.UseLibplacebo"/> false and
        /// <see cref="ToneMapConfig.MeasuredPeakNits"/> 0, so this is the zscale chain rolled off to the
        /// file's declared peak. Both defaults are right here rather than merely convenient - the GPU
        /// probe is a whole ffmpeg run and the peak scan a dozen more, which is not a thing to spend on
        /// a thumbnail, and neither buys anything at this size. Hable for the curve, being the closest
        /// of the three FFmpeg has to what the GPU path would draw.
        /// <para/>
        /// **After the scale, where the encode chain puts it before everything.** That position is a
        /// subtitle question - graphics composited into an HDR frame get dragged through the roll-off -
        /// and there are no subtitles here; what is left is this filter's cost, which is per pixel and
        /// runs through <c>gbrpf32le</c> at 12 bytes of it. Tone-mapping a 4K frame to draw 360 pixels
        /// of it is four hundred times the work for a difference measured at 3 code values out of 255.
        /// </summary>
        private static async Task<string> GetPreviewFilters(string inputFile, int maxH)
        {
            List<string> filters = new List<string>();
            string scale = await GetPreviewScale(inputFile, maxH);

            if (scale.IsNotEmpty())
                filters.Add(scale);

            try
            {
                VideoColorData color = await ColorDataUtils.GetColorDataCached(inputFile);
                string toneMap = new ToneMapConfig { Mode = ToneMapMode.Hable }.GetFilterArgs(color);

                if (toneMap.IsNotEmpty())
                    filters.Add(toneMap);
            }
            catch (Exception e)
            {
                // A preview is worth having untone-mapped, and never worth failing to draw.
                Logger.Log($"Could not tone map the preview: {e.Message}", true);
            }

            return string.Join(",", filters);
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
