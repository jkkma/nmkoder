using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.Media;
using Nmkoder.OS;
using Nmkoder.Utils;
using Nmkoder.Views;
using static Nmkoder.UI.Tasks.Av1anUi;

namespace Nmkoder.UI.Tasks
{
    class Av1an
    {
        public enum QualityMode { Crf, TargetVmaf }
        // DGDecNV is deliberately absent: it needs a proprietary decoder that is not bundled and
        // cannot be, so offering it only produced an option that always failed.
        public enum ChunkMethod { BestSource, LSMASH, FFMS2, Segment, Hybrid, Select }

        public static void Init()
        {
            Av1anUi.Init();
        }

        public static async Task RunResumeWithSavedArgs(string overrideTempDir = "", string overrideArgs = "")
        {
            await RunResume(overrideTempDir, overrideArgs);
        }

        public static async Task RunResumeWithNewArgs(string sourceFile, string overrideTempDir = "")
        {
            if (TrackList.current == null || TrackList.current.File.ImportPath != sourceFile)
            {
                Logger.Log($"You first need to load the input file that was used for this encode to resume with new settings!");
                await FileList.LoadFiles(new string[1] { sourceFile }, true); // Add input file
                await TrackList.SetAsMainFile(FileList.Items[0]); // Load file
            }

            await RunResume(overrideTempDir, "");
        }

        /// <summary>
        /// Resuming skips RunTask.Start, so the task state it normally sets up (and tears down)
        /// has to be applied here - without it the output path resolves empty, a previous
        /// cancelation still aborts the run, and the UI stays stuck in its working state.
        /// </summary>
        private static async Task RunResume(string overrideTempDir, string overrideArgs)
        {
            RunTask.canceled = false;
            Program.MainWin.RunningTask = RunTask.TaskType.Av1an;

            try
            {
                await Run(true, overrideTempDir, overrideArgs);
            }
            finally
            {
                Program.MainWin.RunningTask = RunTask.TaskType.None;
                Program.MainWin.SetProgress(0);
                Program.MainWin.SetWorking(false);
            }
        }

        public static async Task Run(bool resume = false, string overrideTempDir = "", string overrideArgs = "")
        {
            if (overrideTempDir.IsEmpty() && (TrackList.current == null || TrackList.current.File.IsDirectory))
            {
                RunTask.Cancel(TrackList.current == null ? "No input file loaded!" : "Av1an cannot use image sequence inputs!");
                return;
            }

            Program.MainWin.SetWorking(true);
            string args = "";
            string inPath = "";
            string outPath = "";
            string tempDir = "";
            string timestamp = ((long)(DateTime.Now - new DateTime(1970, 1, 1)).TotalMilliseconds).ToString();

            try
            {
                if (overrideArgs.IsEmpty())
                {
                    Logger.Log($"Preparing encoding arguments...");
                    CodecUtils.Av1anCodec vCodec = GetCurrentCodecV();
                    CodecUtils.AudioCodec aCodec = GetCurrentCodecA();
                    bool mp4 = IsMp4Output();
                    bool webm = IsWebmOutput();

                    // av1an insists on mkvmerge to stitch x265 back together, and mkvmerge cannot
                    // write MP4 - so the two cannot be had at once, and saying which is better than
                    // letting av1an refuse the command after the settings have been forgotten.
                    if (mp4 && vCodec == CodecUtils.Av1anCodec.X265)
                    {
                        RunTask.Cancel("av1an needs mkvmerge to concatenate H.265, and mkvmerge cannot write MP4.\n\n" +
                            "Choose MKV as the container, or a different video encoder.");
                        return;
                    }

                    // WebM is a narrow subset of Matroska. What it refuses, it refuses in the muxing
                    // step at the very end, so the encode would be thrown away hours after the choice
                    // that doomed it was made.
                    if (webm && (vCodec == CodecUtils.Av1anCodec.X264 || vCodec == CodecUtils.Av1anCodec.X265))
                    {
                        RunTask.Cancel("WebM can only hold VP9 or AV1 video, not H.264 or H.265.\n\n" +
                            "Choose MKV or MP4 as the container, or a different video encoder.");
                        return;
                    }

                    if (webm && (aCodec == CodecUtils.AudioCodec.Aac || aCodec == CodecUtils.AudioCodec.Eac3 ||
                                 aCodec == CodecUtils.AudioCodec.Mp3 || aCodec == CodecUtils.AudioCodec.Flac))
                    {
                        RunTask.Cancel("WebM can only hold Opus or Vorbis audio.\n\n" +
                            "Choose one of those, or a different container.");
                        return;
                    }

                    // MP4 forces the ffmpeg concatenator (mkvmerge cannot write MP4), and av1an itself
                    // warns that vpx chunks come out of that path with the wrong frame rate.
                    if (mp4 && vCodec == CodecUtils.Av1anCodec.Vpx)
                        Logger.Log("Note: VP9 in MP4 has to be concatenated by ffmpeg, which av1an warns can give the file a wrong frame rate. MKV avoids this.");

                    inPath = TrackList.current.File.ImportPath;
                    ValidatePath();
                    outPath = UiData.GetOutPath();
                    TrackList.current.File.ColorData = await ColorDataUtils.GetColorData(TrackList.current.File.SourcePath);
                    CodecArgs codecArgs = CodecUtils.GetCodec(vCodec).GetArgs(GetVideoArgsFromUi(), TrackList.current.File, Data.Codecs.Pass.OneOfOne);
                    string vf = await GetVideoFilterArgs(codecArgs);
                    string ffAud = CodecUtils.GetCodec(aCodec).GetArgs(GetAudioArgsFromUi()).Arguments;
                    var form = Program.MainWin;
                    bool copySubs = form.CheckAv1anCopySubs.IsChecked == true;
                    string ffMux = BuildMuxArgs(copySubs, form.CheckAv1anCopyData.IsChecked == true, form.CheckAv1anCopyAttachs.IsChecked == true, mp4, webm);

                    if (copySubs && mp4)
                        Logger.Log("Note: av1an cannot carry subtitles into MP4, so they are being left out. Use MKV to keep them.");

                    if (RunTask.canceled) return;

                    string ffArgs = $"{ffAud} {ffMux}";
                    string ffFilters = vf.IsNotEmpty() ? $"-f \" {vf} \" " : ""; // Omit rather than pass av1an a blank filter string

                    args = $"-i {inPath.Wrap()} -y --verbose --keep " +
                        $"{GetSplittingMethodArgs()} " +
                        $"{GetChunkGenMethod()} " +
                        $"{GetConcatMethodArgs()} " +
                        $"{GetChunkOrderArgs()} " +
                        $"--sc-downscale-height {GetScDownscaleHeight()} " +
                        $"{(form.Av1anCustomArgsBox.Text ?? "").Trim()} " +
                        $"{codecArgs.Arguments} " +
                        $"{ffFilters}" +
                        $"-a \" {ffArgs} \" " +
                        $"-w {form.Av1anOptsWorkerCountUpDown.Value.AsInt()} " +
                        $"{CodecUtils.GetKeyIntArg(TrackList.current.File, Config.GetInt(Config.Key.DefaultKeyIntSecs), "-x ")} " +
                        $"-o {outPath.Wrap()}";

                    if (IsUsingVmaf())
                    {
                        int q = form.Av1anQualityUpDown.Value.AsInt();
                        string filters = vf.Length > 3 ? $"--vmaf-filter \" {vf.Split("-vf ").LastOrDefault()} \"" : "";
                        args += $" --target-quality {q} --vmaf-path {Paths.GetVmafPath(false).Wrap()} {filters} --vmaf-threads 2";
                    }
                }
                else
                {
                    inPath = overrideArgs.Split("-i \"")[1].Split("\"")[0].Trim();
                    outPath = overrideArgs.Split(" -o \"").Last().Remove("\"").Trim();
                    args = overrideArgs;
                }

                if (outPath == inPath)
                {
                    Logger.Log($"Output path can't be the same as the input path!");
                    return;
                }

                if (Path.GetExtension(outPath).IsEmpty()) // GetExtension returns an empty string, never null
                {
                    Logger.Log($"Output path must have a valid file extension!");
                    return;
                }

                string tempDirName = overrideTempDir.IsNotEmpty() ? overrideTempDir : timestamp;
                tempDir = Directory.CreateDirectory(Path.Combine(Paths.GetAv1anTempPath(), tempDirName)).FullName;
                AvProcess.lastTempDirAv1an = tempDir;

                args = $"{(resume ? "-r" : "")} --temp {tempDir.Wrap()} {args}";

                string creationTimestamp = (resume ? (LoadJson(overrideTempDir).ContainsKey("creationTimestamp") ? LoadJson(overrideTempDir)["creationTimestamp"] : "-1") : timestamp);
                SaveJson(inPath, tempDirName, args, creationTimestamp, timestamp);
            }
            catch (Exception e)
            {
                Logger.Log($"Error creating av1an command: {e.Message}\n{e.StackTrace}");
                return;
            }

            if (Hotkeys.ShiftHeld) // Allow reviewing and editing command if shift is held
            {
                string edited = await EditCommandWindow.Show("av1an", args);

                if (string.IsNullOrWhiteSpace(edited))
                {
                    Program.MainWin.SetWorking(false);
                    return;
                }

                args = edited;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            }
            catch (Exception e)
            {
                Logger.Log($"Failed to create output folder: {e.Message}");
                Program.MainWin.SetWorking(false);
                return;
            }

            Logger.Log($"Running:\nav1an {args}", true, false, "av1an");

            _ = Task.Run(() => CreateAttachmentMkv(args, tempDir));
            await AvProcess.RunAv1an(args, AvProcess.LogMode.OnlyLastLine, true);

            Program.MainWin.SetWorking(false);
            await AskDeleteTempFolder(tempDir);
        }

        private static int GetScDownscaleHeight()
        {
            if (TrackList.current.File == null || TrackList.current.File.VideoStreams.Count < 1)
                return 0;

            int h = TrackList.current.File.VideoStreams[0].Resolution.Height;
            float mult = 1f;

            if (h >= 720) mult = 0.7500f;
            if (h >= 900) mult = 0.7083f;
            if (h >= 1080) mult = 0.6667f;
            if (h >= 1440) mult = 0.5000f;
            if (h >= 2160) mult = 0.4166f;
            if (h >= 4320) mult = 0.3333f;

            return (h * mult).RoundToInt().Clamp(360, 2160); // Apply multiplicator but clamp to sane values
        }

        private static void SaveJson(string inputFilePath, string tempFolderName, string args, string creationTimestamp, string lastRunTimestamp)
        {
            try
            {
                string jsonPath = Path.Combine(Paths.GetAv1anTempPath(), $"{tempFolderName}.json");
                Dictionary<string, string> info = new Dictionary<string, string>();

                info.Add("fileName", Path.GetFileName(inputFilePath));
                info.Add("filePath", inputFilePath);
                info.Add("tempFolderName", tempFolderName);
                info.Add("args", "-i " + args.Split(" -i ")[1]);
                info.Add("creationTimestamp", creationTimestamp);
                info.Add("lastRunTimestamp", lastRunTimestamp);

                File.WriteAllText(jsonPath, JsonConvert.SerializeObject(info, Formatting.Indented));
            }
            catch (Exception e)
            {
                Logger.Log($"Failed to write nmkoder av1an info json! {e.Message}", true);
            }
        }

        public static Dictionary<string, string> LoadJson(string tempFolderName)
        {
            try
            {
                string jsonPath = Path.Combine(Paths.GetAv1anTempPath(), $"{tempFolderName}.json");
                return JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(jsonPath));
            }
            catch (Exception e)
            {
                Logger.Log($"Failed to load nmkoder av1an info json! {e.Message}", true);
                return new Dictionary<string, string>();
            }
        }

        public static async Task CreateAttachmentMkv(string args, string tempFolder)
        {
            if (!args.MatchesWildcard("* -o \"*.mkv\"*")) return; // Only do this if output is MKV

            NmkdStopwatch sw = new NmkdStopwatch();

            while (!IsAv1anRunning()) // Give up rather than poll forever if av1an never comes up
            {
                if (RunTask.canceled || sw.ElapsedMs > 30000) return;
                await Task.Delay(200);
            }

            while (!IsAudioDone(tempFolder)) // The encode ending without an audio track means it is never coming
            {
                if (RunTask.canceled || !IsAv1anRunning()) return;
                await Task.Delay(500);
            }

            await Task.Delay(500);

            try
            {
                string encoder = TextBetween(args, " -e ", " ");
                string encoderArgs = TextBetween(args, "-v \" ", " \"");

                if (encoder.IsEmpty()) return; // Not a command we can describe, so there is nothing worth attaching

                string txtPath = Path.Combine(Paths.GetSessionDataPath(), $"av1an-{DateTime.Now.ToString("MM-dd-yyyy-HH-mm-ss")}.txt");
                List<string> lines = new List<string> { "Encoder:", encoder, "", "Args:", encoderArgs };
                File.WriteAllLines(txtPath, lines);
                string outPath = Path.Combine(tempFolder, "audio.mkv");

                if (File.Exists(outPath)) // Add attachment to existing audio.mkv
                {
                    string tmpOutPath = IoUtils.FilenameSuffix(outPath, ".tmp");
                    string cmd = $"-o {tmpOutPath.Wrap()} --attachment-mime-type text/plain --attach-file {txtPath.Wrap()} {outPath.Wrap()}";
                    await AvProcess.RunMkvMerge(cmd, NmkoderProcess.ProcessType.Background);
                    File.Delete(outPath);
                    File.Move(tmpOutPath, outPath);
                }
                else // Create an empty audio.mkv with just the attachment in it
                {
                    string cmd = $"-o {outPath.Wrap()} --attachment-mime-type text/plain --attach-file {txtPath.Wrap()}";
                    await AvProcess.RunMkvMerge(cmd, NmkoderProcess.ProcessType.Background);
                }

                IoUtils.TryDeleteIfExists(txtPath);
            }
            catch (Exception ex)
            {
                Logger.Log($"CreateAttachmentMkv Error: {ex.Message}\n{ex.StackTrace}", true);
            }
        }

        /// <summary> Text between two markers, or "" if either is absent - custom args can leave them out. </summary>
        private static string TextBetween(string str, string start, string end)
        {
            int from = str.IndexOf(start);

            if (from < 0)
                return "";

            from += start.Length;
            int to = str.IndexOf(end, from);
            return to < 0 ? "" : str.Substring(from, to - from).Trim();
        }

        private static bool IsAudioDone(string tempFolder)
        {
            string doneJsonPath = Path.Combine(tempFolder, "done.json");

            if (!Directory.Exists(tempFolder) || !File.Exists(doneJsonPath)) return false;

            try
            {
                using var stream = File.Open(doneJsonPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                string contents = new StreamReader(stream).ReadToEnd();
                return contents.Contains("\"audio_done\":true");
            }
            catch (Exception ex)
            {
                Logger.Log($"IsAudioDone Error: {ex.Message}", true);
                return false;
            }
        }

        private static bool IsAv1anRunning()
        {
            return ProcessManager.RunningSubProcesses.Any(x => x.Type == NmkoderProcess.ProcessType.Primary && x.Process.StartInfo.Arguments.Contains("av1an"));
        }

        public static int GetDefaultWorkerCount()
        {
            return ((int)Math.Ceiling((double)Environment.ProcessorCount * 0.4f)).Clamp(2, 32);
        }
    }
}
