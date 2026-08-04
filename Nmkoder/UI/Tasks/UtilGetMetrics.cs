using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.Media;
using Nmkoder.OS;
using static Nmkoder.Media.AvProcess;

namespace Nmkoder.UI.Tasks
{
    class UtilGetMetrics
    {
        public static string vidLq;
        public static string vidHq;
        public static bool runVmaf = true;
        public static bool runSsim;
        public static bool runPsnr;
        public static int alignMode = 0;
        public static int vmafModel = 0;
        public static int subsample = 0;

        public static async Task Run(bool fixRate = true)
        {
            if(RunTask.currentFileListMode == RunTask.FileListMode.Batch)
            {
                RunTask.Fail("The Metrics utility only works in Muxing Mode - it compares two loaded files against each other, which is not something a batch can do per file.");
                return;
            }

            Program.MainWin.SetWorking(true);

            try
            {

                Logger.Log($"Getting metrics for {Path.GetFileName(vidLq)} compared against {Path.GetFileName(vidHq)}...");

                Fraction fps = await IoUtils.GetVideoFramerate(vidLq);
                string r = fixRate ? $"-r {(fps.GetFloat() > 0f ? fps.ToString() : "24")}" : "";
                Comparison cmp = await PrepareComparison();
                FfmpegOutputHandler.overrideTargetDurationMs = await FfmpegCommands.GetDurationMs(vidLq);

                if (runVmaf)
                {
                    Logger.Log("Calculating VMAF...");
                    string vmafPath = Paths.GetVmafPath(true, GetVmafModel());
                    string vmafFilter = $"libvmaf={vmafPath}:n_threads={Environment.ProcessorCount}:n_subsample={subsample}";
                    string args = $"{r} {vidLq.GetFfmpegInputArg()} {r} {vidHq.GetFfmpegInputArg()} -filter_complex {cmp.Graph(vmafFilter)} -f null -";
                    FfmpegSettings settings = new FfmpegSettings() { Args = args, LoggingMode = LogMode.OnlyLastLine, LogLevel = "info", ReliableOutput = true, ProgressBar = true };
                    string output = await RunFfmpeg(settings);
                    List<string> vmafLines = output.SplitIntoLines().Where(x => x.Contains("VMAF score: ")).ToList();

                    if (vmafLines.Count < 1)
                    {
                        // Not with ReplaceLastLine: the failure line would sit where the next
                        // write lands and be erased by it, which is how a metrics run that scored
                        // nothing still looked like one that had.
                        RunTask.Fail("VMAF could not be calculated. The log has FFmpeg's output.");
                    }
                    else
                    {
                        string vmafStr = vmafLines[0].Split("VMAF score: ").LastOrDefault();
                        Logger.Log($"VMAF Score: {vmafStr}", false, ReplaceLastLine());
                    }
                }

                if (runSsim)
                {
                    Logger.Log("Calculating SSIM...");
                    string select = subsample > 1 ? $"select=not(mod(n-1\\,{subsample}))" : "";
                    string args = $"{r} {vidLq.GetFfmpegInputArg()} {r} {vidHq.GetFfmpegInputArg()} -filter_complex {cmp.Graph("ssim", select)} -f null -";
                    FfmpegSettings settings = new FfmpegSettings() { Args = args, LoggingMode = LogMode.OnlyLastLine, LogLevel = "info", ReliableOutput = true, ProgressBar = true };
                    string output = await RunFfmpeg(settings);
                    List<string> ssimLines = output.SplitIntoLines().Where(x => x.Contains("] SSIM ")).ToList();

                    if (ssimLines.Count < 1)
                    {
                        // Not with ReplaceLastLine: the failure line would sit where the next
                        // write lands and be erased by it, which is how a metrics run that scored
                        // nothing still looked like one that had.
                        RunTask.Fail("SSIM could not be calculated. The log has FFmpeg's output.");
                    }
                    else
                    {
                        string scoreStr = ssimLines[0].Split(" All:").LastOrDefault();
                        Logger.Log($"SSIM Score: {scoreStr.Replace("inf", "Infinite")}", false, ReplaceLastLine());
                    }
                }

                if (runPsnr)
                {
                    Logger.Log("Calculating PSNR...");
                    string select = subsample > 1 ? $"select=not(mod(n-1\\,{subsample}))" : "";
                    string args = $"{r} {vidLq.GetFfmpegInputArg()} {r} {vidHq.GetFfmpegInputArg()} -filter_complex {cmp.Graph("psnr", select)} -f null -";
                    FfmpegSettings settings = new FfmpegSettings() { Args = args, LoggingMode = LogMode.OnlyLastLine, LogLevel = "info", ReliableOutput = true, ProgressBar = true };
                    string output = await RunFfmpeg(settings);
                    List<string> psnrLines = output.SplitIntoLines().Where(x => x.Contains("] PSNR ")).ToList();

                    if (psnrLines.Count < 1)
                    {
                        // Not with ReplaceLastLine: the failure line would sit where the next
                        // write lands and be erased by it, which is how a metrics run that scored
                        // nothing still looked like one that had.
                        RunTask.Fail("PSNR could not be calculated. The log has FFmpeg's output.");
                    }
                    else
                    {
                        string scoreStr = psnrLines[0].Split("average:").LastOrDefault().Split(' ')[0];
                        Logger.Log($"PSNR Score: {scoreStr.Replace("inf", "Infinite")}", false, ReplaceLastLine());
                    }
                }
            }
            catch(Exception e)
            {
                RunTask.Fail($"The metrics could not be calculated: {e.Message}");
                Logger.Log($"{e.StackTrace}", true, level: Logger.Level.Debug);
            }

            FfmpegOutputHandler.overrideTargetDurationMs = -1;
            Program.MainWin.SetWorking(false);
        }

        static bool ReplaceLastLine ()
        {
            return (new[] { "...", FfmpegOutputHandler.prefix }).Any(c => Logger.LastUiLine.Contains(c));
        }

        /// <summary>
        /// The two videos put into one frame of reference, which is what makes a comparison possible
        /// at all: libvmaf, ssim and psnr all refuse two inputs of different sizes.
        /// </summary>
        private class Comparison
        {
            /// <summary> Filters for the encoded video, input 0. </summary>
            public string EncFilters = "";
            /// <summary> Filters for the reference, input 1. </summary>
            public string RefFilters = "";
            /// <summary> The frame each side ends up at, for saying so when they still do not match. </summary>
            public Size EncSize;
            public Size RefSize;

            /// <summary>
            /// The whole -filter_complex value: two labelled chains, then the pair of labels the
            /// metric reads, the encode first and the reference second. That order is the one an
            /// unfiltered comparison binds in - ffmpeg attaches the unused inputs to the metric filter
            /// in the order they were opened - and it is worth keeping, because VMAF is not symmetric
            /// and scores differently with its inputs the other way up.
            /// <para/>
            /// Wrapped as one argument, because the chains are separated by semicolons and this command
            /// line is handed to a shell, which reads an unquoted ';' as the end of the command.
            /// <para/>
            /// The metric filter is taken as a parameter rather than appended by the caller, because
            /// appending is what it used to do and the quotes ended before it: libvmaf's model path is
            /// quoted at ffmpeg's level, which the *shell* does not honour, so the halves joined into
            /// one argument only while nothing after the closing quote held a space. The model sits in
            /// the app's own bin folder, so an install under "C:\Program Files\..." split the argument
            /// in two and failed the run. Through Shell.WrapArg rather than a pair of double quotes,
            /// for the reasons GetVideoFilterArgs gives.
            /// </summary>
            /// <param name="metric"> The metric filter the two labels feed - libvmaf, ssim or psnr. </param>
            /// <param name="perInput"> A filter to append to both chains alike - the frame selection
            /// SSIM and PSNR subsample with, which has to drop the same frames on both sides or it
            /// would compare each surviving frame against whatever the other input still had. </param>
            public string Graph(string metric, string perInput = "")
            {
                return Shell.WrapArg($"[0:v]{Chain(EncFilters, perInput)}[enc];[1:v]{Chain(RefFilters, perInput)}[ref];[enc][ref]{metric}");
            }

            /// <summary> A link with no filters in it is a syntax error, so an untouched input gets 'null'. </summary>
            private static string Chain(params string[] filters)
            {
                string joined = string.Join(",", filters.Where(x => x.IsNotEmpty()));
                return joined.IsEmpty() ? "null" : joined;
            }
        }

        private static async Task<Comparison> PrepareComparison()
        {
            var cmp = new Comparison();
            Size encRes = await GetMediaResolutionCached.GetSizeAsync(vidLq);
            Size refRes = await GetMediaResolutionCached.GetSizeAsync(vidHq);
            Size encSar = await FfmpegUtils.GetSampleAspectRatio(vidLq);
            Size refSar = await FfmpegUtils.GetSampleAspectRatio(vidHq);

            // Two files stored the same way need no correcting, and putting them through a scale
            // filter would only cost a generation of resampling on both sides. It is when they
            // disagree that the stored frames are the wrong thing to compare: a de-squeezed encode of
            // an anamorphic source holds the same picture in a different number of pixels, and
            // measuring one against the other means measuring both in the shape they play at.
            bool matched = encRes == refRes && AspectRatio.SameShape(encSar, refSar);
            bool desqueeze = !matched && (AspectRatio.IsAnamorphic(encSar) || AspectRatio.IsAnamorphic(refSar));

            cmp.EncSize = desqueeze ? DisplayFrame(encRes, encSar) : encRes;
            cmp.RefSize = refRes;

            if (cmp.EncSize != encRes)
                cmp.EncFilters = $"scale={cmp.EncSize.Width}:{cmp.EncSize.Height},setsar=1:1";

            if (alignMode == 1 || alignMode == 3) // Auto-Crop the reference
            {
                string crop = await FfmpegUtils.GetCurrentAutoCrop(vidHq, true);

                if (crop.IsNotEmpty())
                {
                    cmp.RefFilters = crop;
                    cmp.RefSize = FfmpegUtils.ParseCropSize(crop, cmp.RefSize);
                }
            }

            // Resizing the reference means resizing it to the *encode's* frame. Its own size was the
            // target before, which is no target at all: it left the reference exactly as it was, so
            // the mode did nothing whatsoever on its own and only ever undid the crop beside it.
            bool resizing = alignMode == 2 || alignMode == 3;
            Size target = resizing ? cmp.EncSize
                : desqueeze ? DisplayFrame(cmp.RefSize, refSar)
                : cmp.RefSize;

            if (target != cmp.RefSize)
            {
                cmp.RefFilters = string.Join(",", new[] { cmp.RefFilters, $"scale={target.Width}:{target.Height},setsar=1:1" }.Where(x => x.IsNotEmpty()));
                cmp.RefSize = target;
            }

            if (desqueeze)
                Logger.Log($"Comparing at {cmp.EncSize}: the encode is {encRes} with {Pixels(encSar)} and the reference " +
                    $"{refRes} with {Pixels(refSar)}, so both are measured in the shape they are shown at rather than as stored.");

            // Not a hard stop: this is a prediction of what ffmpeg will make of the two files, and it
            // is better to be wrong in a log line than to refuse a comparison that would have run.
            if (cmp.EncSize != cmp.RefSize)
                Logger.Log($"Warning: the encode is {cmp.EncSize} and the reference is {cmp.RefSize}, and a metric cannot be " +
                    $"taken across two different frame sizes. Set Alignment to \"Resize reference\" to scale the reference to the encode.");

            return cmp;
        }

        /// <summary>
        /// The frame a source is measured in once its pixels are square - worked out through the
        /// resize tool rather than from the display size directly, because the two disagree and the
        /// encoder is what settles it: a 720x480 DVD at 32:27 displays at 853⅓ pixels wide, which
        /// rounds to 853 as a plain measurement and to 854 as a frame an encoder will take. An
        /// encode nmkoder made of that disc is 854 wide, so measuring the source at 853 would leave
        /// the comparison a pixel apart - which is not a comparison at all.
        /// </summary>
        private static Size DisplayFrame(Size storage, Size sar)
        {
            Size frame = ResizeConfig.DesqueezeOnly().Compute(storage, sar);
            return frame.IsEmpty ? storage : frame;
        }

        /// <summary> A SAR as it is said in a message, with the unknown one named rather than printed as 0:0. </summary>
        private static string Pixels(Size sar)
        {
            return AspectRatio.IsAnamorphic(sar) ? $"{sar.Width}:{sar.Height} pixels" : "square pixels";
        }

        private static string GetVmafModel ()
        {
            if (vmafModel == 0) return "vmaf_v0.6.1";
            if (vmafModel == 1) return "vmaf_v0.6.1neg";
            if (vmafModel == 2) return "vmaf_4k_v0.6.1";
            return "";
        }
    }
}
