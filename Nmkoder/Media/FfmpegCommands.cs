using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.OS;
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
    class FfmpegCommands
    {
        public static string pngCompr = "-compression_level 3";

        public static int GetPadding ()
        {
            //return (Interpolate.current.ai.aiName == Implementations.flavrCuda.aiName) ? 8 : 2;     // FLAVR input needs to be divisible by 8
            // TODO: CHECK IF CODEC NEEDS mod2 etc
            return 2;
        }

        public static string GetPadFilter ()
        {
            int padPixels = GetPadding();
            return $"pad=width=ceil(iw/{padPixels})*{padPixels}:height=ceil(ih/{padPixels})*{padPixels}:color=black@0";
        }

        public static async Task ConcatVideos(string concatFile, string outPath, int looptimes = -1, bool showLog = true)
        {
            Logger.Log($"ConcatVideos('{Path.GetFileName(concatFile)}', '{outPath}', {looptimes})", true, false, "ffmpeg");

            if(showLog)
                Logger.Log($"Merging videos...", false, Logger.LastUiLine.Contains("frame"));

            IoUtils.RenameExistingFile(outPath);
            string loopStr = (looptimes > 0) ? $"-stream_loop {looptimes}" : "";
            string vfrFilename = Path.GetFileName(concatFile);
            string args = $" {loopStr} -vsync 1 -f concat -i {vfrFilename} -c copy -movflags +faststart -fflags +genpts {outPath.Wrap()}";
            FfmpegSettings settings = new FfmpegSettings() { Args = args, WorkingDir = concatFile.GetParentDir(), LoggingMode = LogMode.Hidden };
            await RunFfmpeg(settings);
        }

        public static async Task LoopVideo(string inputFile, int times, bool delSrc = false)
        {
            string pathNoExt = Path.ChangeExtension(inputFile, null);
            string ext = Path.GetExtension(inputFile);
            string loopSuffix = $"{times}x";
            string outpath = $"{pathNoExt}{loopSuffix}{ext}";
            IoUtils.RenameExistingFile(outpath);
            string args = $" -stream_loop {times} -i {inputFile.Wrap()} -c copy {outpath.Wrap()}";

            FfmpegSettings settings = new FfmpegSettings() { Args = args, LoggingMode = LogMode.Hidden };
            await RunFfmpeg(settings);

            if (delSrc)
                DeleteSource(inputFile);
        }

        public static async Task ChangeSpeed(string inputFile, float newSpeedPercent, bool delSrc = false)
        {
            string pathNoExt = Path.ChangeExtension(inputFile, null);
            string ext = Path.GetExtension(inputFile);
            float val = newSpeedPercent / 100f;
            string speedVal = (1f / val).ToString("0.0000").Replace(",", ".");
            string args = " -itsscale " + speedVal + " -i \"" + inputFile + "\" -c copy \"" + pathNoExt + "-" + newSpeedPercent + "pcSpeed" + ext + "\"";

            FfmpegSettings settings = new FfmpegSettings() { Args = args, LoggingMode = LogMode.OnlyLastLine };
            await RunFfmpeg(settings);

            if (delSrc)
                DeleteSource(inputFile);
        }

        public static async Task<long> GetDurationMs(string inputFile)
        {
            Logger.Log($"GetDuration({inputFile}) - Reading Duration using ffprobe.", true, false, "ffmpeg");
            string args = $"-select_streams v:0 -show_entries format=duration -of csv=s=x:p=0 -sexagesimal {inputFile.Wrap()}";
            FfprobeSettings settings = new FfprobeSettings() { Args = args };
            string output = await RunFfprobe(settings);

            return FormatUtils.TimestampToMs(output);
        }

        /// <summary>
        /// The rate frames leave the decoder at. ffprobe answers this question twice and the two are
        /// not the same number: <c>r_frame_rate</c> is "the lowest framerate with which all timestamps
        /// can be represented", which for a field-coded MPEG-2 stream - which is every NTSC tape
        /// capture - is the *field* rate, twice the rate whole frames actually arrive at, while
        /// <c>avg_frame_rate</c> is the frame count over the duration. Measured on a 720x480 capture:
        /// 60000/1001 against 30000/1001. Reading the first of those made a bob deinterlace declare
        /// its output at 119.88 fps, so mkvmerge stamped 59.94 fps of frames at twice that - half
        /// speed, the audio ending at 71% of the picture, and nothing anywhere saying so.
        /// <para/>
        /// <c>avg_frame_rate</c> is <c>0/0</c> whenever ffprobe cannot measure it - a stream carrying
        /// no duration, some piped inputs - which is the case <c>r_frame_rate</c> is kept behind it
        /// for. For an ordinary constant-rate file the two are equal and this picks the same number
        /// either way, so the change is confined to the sources that state a field rate.
        /// </summary>
        public static async Task<Fraction> GetFramerate(string inputFile, bool preferFfmpeg = false, int streamIndex = 0)
        {
            Logger.Log($"GetFramerate(inputFile = '{inputFile}', preferFfmpeg = {preferFfmpeg})", true, false, "ffmpeg");
            Fraction ffprobeFps = new Fraction(0, 1);
            Fraction ffmpegFps = new Fraction(0, 1);

            async Task<Fraction> ReadRate(string entry)
            {
                try
                {
                    string output = await GetVideoInfo.GetFfprobeInfoAsync(inputFile, GetVideoInfo.FfprobeMode.ShowStreams, entry, streamIndex);
                    string[] numbers = output.SplitIntoLines().First().Split('/');

                    if (numbers.Length != 2)
                        return new Fraction(0, 1);

                    int num = numbers[0].GetInt(), den = numbers[1].GetInt();
                    return den == 0 ? new Fraction(0, 1) : new Fraction(num, den);
                }
                catch (Exception e)
                {
                    Logger.Log($"GetFramerate ffprobe Error reading {entry}: {e.Message}", true, false);
                    return new Fraction(0, 1);
                }
            }

            Fraction avgFps = await ReadRate("avg_frame_rate");
            Fraction rFps = await ReadRate("r_frame_rate");
            Logger.Log($"Fractional FPS from ffprobe: avg_frame_rate {avgFps} = {avgFps.GetFloat()}, r_frame_rate {rFps} = {rFps.GetFloat()}", true, false, "ffmpeg");

            ffprobeFps = avgFps.GetFloat() > 0.01f ? avgFps : rFps;

            // Worth a line of its own rather than only the pair above: this is the one case where the
            // two disagree about what a frame is, and it is the shape that reaches every rate in the
            // app at once, so a run that later looks wrong should say here which number it was built on.
            if (avgFps.GetFloat() > 0.01f && rFps.GetFloat() > 0.01f && !MiscUtils.IsSameFrameRate(avgFps, rFps))
                Logger.Log($"'{Path.GetFileName(inputFile)}' states two frame rates - {rFps} (r_frame_rate) and {avgFps} " +
                    $"(avg_frame_rate). Using {avgFps}, the rate whole frames arrive at; a field-coded source states the field rate as the first of those.", true);

            try
            {
                string ffmpegOutput = await GetVideoInfo.GetFfmpegInfoAsync(inputFile);
                string[] entries = ffmpegOutput.Split(',');

                foreach (string entry in entries)
                {
                    if (entry.Contains(" fps") && !entry.Contains("Input "))    // Avoid reading FPS from the filename, in case filename contains "fps"
                    {
                        string num = entry.Replace(" fps", "").Trim().Replace(",", ".");
                        Logger.Log($"Float FPS from ffmpeg: {num.GetFloat()}", true, false, "ffmpeg");
                        ffmpegFps = new Fraction(num.GetFloat());
                    }
                }
            }
            catch(Exception ffmpegEx)
            {
                Logger.Log("GetFramerate ffmpeg Error: " + ffmpegEx.Message, true, false);
            }

            if (preferFfmpeg)
            {
                if (ffmpegFps.GetFloat() > 0)
                    return ffmpegFps;
                else
                    return ffprobeFps;
            }
            else
            {
                if (ffprobeFps.GetFloat() > 0)
                    return ffprobeFps;
                else
                    return ffmpegFps;
            }
        }

        public static async Task<Size> GetSize(string filePath)
        {
            Logger.Log($"GetSize('{filePath}')", true, false, "ffmpeg");
            string args = $"{filePath.GetConcStr()} -select_streams v:0 -show_entries stream=width,height -of csv=s=x:p=0 {filePath.Wrap()}";
            FfprobeSettings settings = new FfprobeSettings() { Args = args };
            string[] outputLines = (await RunFfprobe(settings)).SplitIntoLines();

            foreach (string line in outputLines)
            {
                if (!line.Contains("x") || line.Trim().Length < 3)
                    continue;

                string[] numbers = line.Split('x');
                return new Size(numbers[0].GetInt(), numbers[1].GetInt());
            }

            return new Size(0, 0);
        }

        public static async Task<int> GetFrameCountAsync(string path, bool tryPacketCount = true, bool tryFfprobe = true, bool tryFfmpeg = true, NmkoderProcess.ProcessType processType = NmkoderProcess.ProcessType.Secondary)
        {
            if (tryPacketCount)
            {
                string a = $"-select_streams v:0 -count_packets -show_entries stream=nb_read_packets -of csv=p=0 {path.Wrap()}";
                string o = await RunFfprobe(new FfprobeSettings() { Args = a, ProcessType = processType });
                string[] lines = o.SplitIntoLines().Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
                if (lines != null && lines.Length > 0 && lines.Last().GetInt() > 0) return lines.Last().GetInt();
            }

            if (tryFfprobe)
            {
                string a = $"{path.GetConcStr()} -threads 0 -select_streams v:0 -show_entries stream=nb_frames -of default=noprint_wrappers=1 {path.Wrap()}";
                string o = await RunFfprobe(new FfprobeSettings() { Args = a, ProcessType = processType });
                string[] entries = o.SplitIntoLines();

                foreach (string entry in entries)
                    if (entry.Contains("nb_frames=") && entry.GetInt() > 0)
                        return entry.GetInt();
            }

            if (tryFfmpeg)
            {
                string a = $"{path.GetConcStr()} -i {path.Wrap()} -map 0:v:0 -c copy -f null - ";
                FfmpegSettings settings = new FfmpegSettings() { Args = a, LoggingMode = LogMode.Hidden, SetBusy = true, LogLevel = "panic", ReliableOutput = true, CanCancelTask = false, ProcessType = processType };
                string[] lines = (await RunFfmpeg(settings)).SplitIntoLines();

                try
                {
                    string lastLine = lines.Last().ToLower();
                    int fr = lastLine.Substring(0, lastLine.IndexOf("fps")).GetInt();
                    if (fr > 0) return fr;
                } 
                catch { }
            }

            // A warning rather than a plain line: a frame count of zero is what makes a progress bar
            // sit at nothing for the whole encode, and it is worth being able to see why afterwards.
            Logger.LogWarn("Could not read the video's frame count - progress reporting will be less accurate.");
            return 0;
        }

        public static async Task<bool> IsEncoderCompatible(string enc)
        {
            Logger.Log($"IsEncoderCompatible('{enc}')", true, false, "ffmpeg");
            string args = $"-loglevel error -f lavfi -i color=black:s=540x540 -vframes 1 -an -c:v {enc} -f null -";
            FfmpegSettings settings = new FfmpegSettings() { Args = args, LoggingMode = LogMode.Hidden, LogLevel = "error", ReliableOutput = true, CanCancelTask = false };
            string output = await RunFfmpeg(settings);
            return !output.ToLower().Contains("error");
        }

        public static void DeleteSource(string path)
        {
            Logger.Log("[FFCmds] Deleting input file/dir: " + path, true);

            if (IoUtils.IsPathDirectory(path) && Directory.Exists(path))
                Directory.Delete(path, true);

            if (!IoUtils.IsPathDirectory(path) && File.Exists(path))
                File.Delete(path);
        }
    }
}
