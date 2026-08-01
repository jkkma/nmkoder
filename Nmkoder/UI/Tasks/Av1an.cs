using System;
using System.Collections.Generic;
using System.Globalization;
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
        public enum QualityMode { Crf, TargetVmaf, TargetSsimu2, TargetButteraugli, TargetXpsnr }
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
            RunTask.ResetOutcome();
            Program.MainWin.RunningTask = RunTask.TaskType.Av1an;
            RunTask.ReportProgress("Running: AV1AN encode...");
            NmkdStopwatch sw = new NmkdStopwatch();

            try
            {
                await Run(true, overrideTempDir, overrideArgs);
                RunTask.NotifyTaskEnd(RunTask.TaskType.Av1an, sw);
            }
            catch (Exception e)
            {
                // Resuming does not go through RunTask.Start, so it does not get Start's guard either -
                // and this is started as a fire-and-forget task, where an escaping exception is
                // swallowed by the runtime and reports nothing at all.
                RunTask.Fail($"The encode could not be resumed: {e.Message}");
                Logger.Log($"{e}", true, level: Logger.Level.Debug);
            }
            finally
            {
                Program.MainWin.RunningTask = RunTask.TaskType.None;
                Program.MainWin.SetProgress(0);
                Program.MainWin.SetWorking(false);
            }

            // After the finally: the countdown aborts itself while the app still counts as busy
            _ = RunTask.ShutdownWhenDoneCountdown(RunTask.canceled);
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
            // The file the user loaded, which a trim replaces inPath with a cut copy of. Kept apart
            // because resuming with new settings reloads whatever the saved info names, and that has
            // to be the source: re-cutting from the source is right where re-cutting a cut is not.
            string sourcePath = "";
            string outPath = "";
            string tempDir = "";
            string tempDirName = "";
            string creationTimestamp = "";
            string timestamp = ((long)(DateTime.Now - new DateTime(1970, 1, 1)).TotalMilliseconds).ToString();
            // Set only when the arguments are built from the UI: replaying saved arguments means running
            // exactly what was saved, and may not even have a file loaded to take the subtitles from.
            List<int> subsToAddAfter = new List<int>();

            try
            {
                if (overrideArgs.IsEmpty())
                {
                    Logger.Log($"Preparing encoding arguments...");
                    CodecUtils.Av1anCodec vCodec = GetCurrentCodecV();
                    CodecUtils.AudioCodec aCodec = GetCurrentCodecA();
                    bool mp4 = IsMp4Output();
                    bool webm = IsWebmOutput();
                    // Snapshotted once, because the tab stays editable through the awaits below
                    // (the color probe, the auto-crop scan, av1an's --help) - reading the boxes
                    // again later could validate one mode's settings and emit another's.
                    QualityMode qualMode = GetCurrentQualityMode();
                    ChunkMethod chunkMethod = GetCurrentChunkMethod();
                    decimal quality = Program.MainWin.Av1anQualityUpDown.Value ?? 0;

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

                    // IVF holds one raw video stream and nothing else - no audio, no subtitles, and only
                    // VP8, VP9 or AV1. MKV and WebM are neither, so what it wrote would carry their name
                    // over a file that is not one, missing every track that is not video and with nothing
                    // having said so. MP4 is exempt only because it already overrides the dropdown below.
                    if (!mp4 && IsUsingIvfConcat())
                    {
                        RunTask.Cancel("The IVF concatenator writes a raw video stream, not an MKV or WebM file, " +
                            "and it holds no audio or subtitles.\n\n" +
                            "Choose MKVMerge or FFmpeg as the concatenation method.");
                        return;
                    }

                    // The check above only covers re-encoding. Copying is a separate question - it turns
                    // on what the source already is, and a track the container cannot hold fails the
                    // final mux just the same, having by then encoded the whole video for nothing.
                    string audioProblem = GetCopiedAudioProblem(aCodec, GetCurrentContainer(), TrackList.current?.File);

                    if (audioProblem.IsNotEmpty())
                    {
                        RunTask.Cancel(audioProblem);
                        return;
                    }

                    if (qualMode == QualityMode.TargetSsimu2 || qualMode == QualityMode.TargetButteraugli || qualMode == QualityMode.TargetXpsnr)
                    {
                        string metric = GetTargetMetricName(qualMode);

                        // The bundle parks Vship outside the autoload folder, and what belongs in it
                        // on this machine is decided here, right before av1an looks: av1an snapshots
                        // its plugin list once at startup, and both these modes change scoring
                        // backend when Vship is present. XPSNR is left out - ffmpeg scores it, so
                        // there is no reason to pay a GPU probe for it.
                        if (qualMode == QualityMode.TargetSsimu2 || qualMode == QualityMode.TargetButteraugli)
                            await VshipStager.Reconcile();

                        // av1an's releases to date (through 0.5.2, and unfixed upstream as of July
                        // 2026) invoke the julek plugin's scoring function as "butteraugli", but the
                        // plugin registers it as "Butteraugli", and VapourSynth's lookup is case-
                        // sensitive - so on the bundled CPU path every probe fails, minutes in, after
                        // scene detection and the first chunks have already encoded. Vship registers
                        // the exact name av1an calls, so it works, and the reconcile above has just
                        // staged it wherever this machine's GPU passes its check - so its absence
                        // here means the machine cannot run it. Only worth stopping on where the
                        // portable plugin folder exists to be inspected: a system VapourSynth keeps
                        // its plugins wherever it likes, so there nothing is known and the run
                        // proceeds on a warning instead. Checked ahead of the chunk method - this
                        // verdict is terminal for the mode, so it should not come out only after a
                        // chunk method was dutifully corrected for it. Remove this guard when an
                        // av1an release fixes the invoke (compare_butteraugli in vapoursynth.rs).
                        if (qualMode == QualityMode.TargetButteraugli)
                        {
                            bool? vship = HasVshipInPortablePlugins();

                            if (vship == false)
                            {
                                RunTask.Cancel(GetButteraugliBlockedMessage());
                                return;
                            }

                            if (vship == null)
                                Logger.Log("Note: av1an calls the CPU Butteraugli plugin (julek) by the wrong " +
                                    "function name, so probing fails unless the Vship plugin is installed.");
                        }

                        // av1an scores SSIMULACRA2 and Butteraugli probes through VapourSynth, so it
                        // insists on a chunk method that decodes through it and refuses the pairing at
                        // startup. XPSNR is exempt: at the probing rate this tab uses, av1an scores it
                        // with ffmpeg's xpsnr filter, which works with every chunk method - and an
                        // ffmpeg too old to have the filter is likewise refused at startup, before any
                        // encoding work has happened.
                        if (qualMode != QualityMode.TargetXpsnr && !IsVapourSynthChunkMethod(chunkMethod))
                        {
                            RunTask.Cancel($"Target {metric} scores its probes through VapourSynth, so av1an " +
                                "requires the BestSource, LSMASH or FFMS2 chunk method.\n\n" +
                                "Pick one of those as the Chunk Method, or a different quality mode.");
                            return;
                        }

                        // Added in av1an 0.5.0, with every metric this tab offers - xpsnr included -
                        // there from the start. An older binary refuses the entire command over
                        // the unknown flag, so a help text that lacks it is worth stopping on.
                        // A help text that could not be read at all is not: stopping on that
                        // grounded up-to-date binaries whose first launch was still being
                        // virus-scanned, over a flag they knew. When nothing is known the flag
                        // is passed, and an av1an that really is too old refuses it at startup,
                        // before any encoding work has happened.
                        if (await AvProcess.Av1anHelpKnown() && !await AvProcess.Av1anSupportsFlag("--target-metric"))
                        {
                            RunTask.Cancel($"This av1an has no --target-metric option (added in av1an 0.5.0), " +
                                $"so it cannot target {metric}.\n\nUpdate av1an, or pick a different quality mode.");
                            return;
                        }
                    }

                    // MP4 forces the ffmpeg concatenator (mkvmerge cannot write MP4), and av1an itself
                    // warns that vpx chunks come out of that path with the wrong frame rate.
                    if (mp4 && vCodec == CodecUtils.Av1anCodec.Vpx)
                        Logger.Log("Note: VP9 in MP4 has to be concatenated by ffmpeg, which av1an warns can give the file a wrong frame rate. MKV avoids this.");

                    inPath = sourcePath = TrackList.current.File.ImportPath;
                    ValidatePath();
                    outPath = UiData.GetOutPath();
                    TrackList.current.File.ColorData = await ColorDataUtils.GetColorData(TrackList.current.File.SourcePath);
                    // Kept rather than built inline: the pixel format the color format box resolved to is
                    // needed again below, to work out who should convert to it.
                    Dictionary<string, string> videoArgs = GetVideoArgsFromUi();
                    // Pinned to the snapshot - GetVideoArgsFromUi read the boxes again, and these
                    // two decide whether the encoder emits its own quality flag at all.
                    videoArgs["qMode"] = ((int)qualMode).ToString();
                    videoArgs["q"] = ((int)quality).ToString();
                    string pixFmt = videoArgs.ContainsKey("pixFmt") ? videoArgs["pixFmt"] : "";
                    CodecArgs codecArgs = CodecUtils.GetCodec(vCodec).GetArgs(videoArgs, TrackList.current.File, Data.Codecs.Pass.OneOfOne);
                    string vf = await GetVideoFilterArgs(codecArgs);
                    // Deliberately built without the media file: that is what tells the audio arguments
                    // to come out unindexed, which is what av1an needs. Its own '-map 0' carries every
                    // audio track, and this tab has one bitrate and one channel count for all of them.
                    string ffAud = CodecUtils.GetCodec(aCodec).GetArgs(GetAudioArgsFromUi()).Arguments;
                    var form = Program.MainWin;
                    bool copySubs = form.CheckAv1anCopySubs.IsChecked == true;
                    List<int> bitmapSubs = GetBitmapSubtitleIndices(TrackList.current?.File);
                    string ffMux = BuildMuxArgs(copySubs, form.CheckAv1anCopyData.IsChecked == true, form.CheckAv1anCopyAttachs.IsChecked == true, mp4, webm, bitmapSubs);

                    string hbdProblem = GetHbdModeDecisionProblem(vCodec, pixFmt);

                    if (hbdProblem.IsNotEmpty())
                        Logger.Log(hbdProblem);

                    if (form.CheckAv1anCopyData.IsChecked == true && (TrackList.current?.File.DataStreams.Count ?? 0) > 0)
                        Logger.Log("Note: data streams are being left out. av1an muxes through an intermediate Matroska file, which stores none, so they cannot be carried either way.");

                    if (copySubs && mp4)
                        subsToAddAfter = GetTextSubtitleIndices(TrackList.current?.File);

                    if (copySubs && mp4 && bitmapSubs.Count > 0)
                        Logger.Log($"Note: MP4 stores no image-based subtitles, so {bitmapSubs.Count} " +
                            $"track{(bitmapSubs.Count == 1 ? " is" : "s are")} being left out. Use MKV to keep them.");
                    else if (copySubs && webm && bitmapSubs.Count > 0)
                        Logger.Log($"Note: WebM only holds text subtitles, so {bitmapSubs.Count} image-based " +
                            $"track{(bitmapSubs.Count == 1 ? " is" : "s are")} being left out. Use MKV to keep them.");

                    if (RunTask.canceled) return;

                    string ffArgs = $"{ffAud} {ffMux}";
                    string ffFilters = vf.IsNotEmpty() ? $"-f \" {vf} \" " : ""; // Omit rather than pass av1an a blank filter string
                    string pixFmtConverter = await GetPixelFormatConverterArgs(pixFmt, ffFilters.IsNotEmpty(), chunkMethod);

                    // The input is not named here. A trim has to cut its section out first, and where
                    // that copy goes is only settled once this run's temp folder is, so the '-i' is
                    // put in front of all of this below, after the cut has run.
                    args = $"-y --verbose --keep " +
                        $"{GetSplittingMethodArgs()} " +
                        $"{GetChunkGenMethod(chunkMethod)} " +
                        $"{GetConcatMethodArgs(vCodec)} " +
                        $"{GetChunkOrderArgs()} " +
                        $"--sc-downscale-height {GetScDownscaleHeight()} " +
                        $"{(form.Av1anCustomArgsBox.Text ?? "").Trim()} " +
                        $"{codecArgs.Arguments} " +
                        $"{pixFmtConverter} " +
                        $"{ffFilters}" +
                        $"-a \" {ffArgs} \" " +
                        $"-w {form.Av1anOptsWorkerCountUpDown.Value.AsInt()} " +
                        $"{CodecUtils.GetKeyIntArg(TrackList.current.File, Config.GetInt(Config.Key.DefaultKeyIntSecs), "-x ")} " +
                        $"-o {outPath.Wrap()}";

                    if (qualMode != QualityMode.Crf)
                    {
                        if (qualMode == QualityMode.TargetSsimu2 || qualMode == QualityMode.TargetButteraugli || qualMode == QualityMode.TargetXpsnr)
                        {
                            // These metrics are scored outside libvmaf - SSIMULACRA2 and Butteraugli
                            // through VapourSynth (vszip, the julek plugin or vship), XPSNR by
                            // ffmpeg's xpsnr filter - so none of the --vmaf-* flags apply, including
                            // --vmaf-filter, which is how the VMAF branch shows the probes its
                            // filtered frames. There is no equivalent here: probes are compared
                            // against the unfiltered source, so any filter that visibly alters the
                            // frames skews the search.
                            if (vf.Length > 3)
                                Logger.Log("Note: video filters are not applied when scoring " +
                                    $"{GetTargetMetricName(qualMode)} probes, " +
                                    "so any filter that visibly changes the frames will skew the target quality search.");

                            // The INF norm rather than the 3-norm, because av1an only scores the
                            // 3-norm through the GPU plugin (Vship), while INF is also meant to work
                            // on the bundled CPU plugin (julek) - meant to, because av1an's releases
                            // to date call julek by the wrong name (see the guard above), so INF too
                            // needs Vship until that is fixed. XPSNR goes out as the weighted variant -
                            // the (4·Y+U+V)/6 plane aggregation the metric's authors define for
                            // video - where plain xpsnr takes the single worst plane instead.
                            string metricName = qualMode == QualityMode.TargetSsimu2 ? "ssimulacra2"
                                : qualMode == QualityMode.TargetButteraugli ? "butteraugli-inf" : "xpsnr-weighted";
                            // Butteraugli measures distortion - 0 is identical, and the useful
                            // targets sit between whole numbers - so it keeps its decimals, as does
                            // XPSNR, a decibel scale where half a dB is a real step; the 0-100
                            // metrics stay the whole numbers they always were.
                            string target = qualMode == QualityMode.TargetButteraugli || qualMode == QualityMode.TargetXpsnr
                                ? quality.ToString("0.0##", CultureInfo.InvariantCulture)
                                : ((int)quality).ToString();
                            args += $" --target-metric {metricName} --target-quality {target}";
                        }
                        else
                        {
                            string filters = vf.Length > 3 ? $"--vmaf-filter \" {vf.Split("-vf ").LastOrDefault()} \"" : "";
                            args += $" --target-quality {(int)quality} --vmaf-path {Paths.GetVmafPath(false).Wrap()} {filters} --vmaf-threads 2";
                        }
                    }
                }
                else
                {
                    inPath = ParseQuotedArg(overrideArgs, "-i");
                    outPath = ParseQuotedArg(overrideArgs, "-o");
                    args = overrideArgs;

                    // The replayed command's '-i' may be a trimmed copy rather than the file the user
                    // loaded, and the saved info already names that file - so it is read back rather
                    // than rewritten from the command being replayed.
                    Dictionary<string, string> savedInfo = LoadJson(overrideTempDir);
                    sourcePath = savedInfo.ContainsKey("filePath") ? savedInfo["filePath"] : inPath;

                    if (inPath.IsEmpty() || outPath.IsEmpty())
                    {
                        RunTask.Fail($"Cannot resume - the saved command names no {(inPath.IsEmpty() ? "input" : "output")} file.");
                        Program.MainWin.SetWorking(false);
                        return;
                    }

                    // A replayed command picks its scoring backend the same way a fresh one does -
                    // at av1an startup, from whatever sits in vs-plugins - so the folder has to be
                    // reconciled for it too, and a Butteraugli resume that would only rediscover
                    // av1an's julek name mismatch minutes in is stopped up front like a fresh run.
                    if (args.Contains("--target-metric ssimulacra2") || args.Contains("--target-metric butteraugli"))
                    {
                        await VshipStager.Reconcile();

                        if (args.Contains("--target-metric butteraugli") && HasVshipInPortablePlugins() == false)
                        {
                            RunTask.Cancel(GetButteraugliBlockedMessage());
                            return;
                        }
                    }
                }

                if (outPath == inPath)
                {
                    RunTask.Fail($"Output path can't be the same as the input path!");
                    Program.MainWin.SetWorking(false);
                    return;
                }

                if (Path.GetExtension(outPath).IsEmpty()) // GetExtension returns an empty string, never null
                {
                    RunTask.Fail($"Output path must have a valid file extension!");
                    Program.MainWin.SetWorking(false);
                    return;
                }

                tempDirName = overrideTempDir.IsNotEmpty() ? overrideTempDir : timestamp;
                tempDir = Directory.CreateDirectory(Path.Combine(Paths.GetAv1anTempPath(), tempDirName)).FullName;
                AvProcess.lastTempDirAv1an = tempDir;

                if (overrideArgs.IsEmpty()) // A replayed command already names the input it wants
                {
                    string trimmed = await CutTrimmedInput(inPath, tempDir);

                    if (RunTask.canceled)
                    {
                        DiscardUnusedTempFolder(tempDir, resume);
                        Program.MainWin.SetWorking(false);
                        return;
                    }

                    if (trimmed.IsNotEmpty())
                        inPath = trimmed;

                    args = $"-i {inPath.Wrap()} {args}";
                }

                args = $"{(resume ? "-r" : "")} --temp {tempDir.Wrap()} {args}";
                creationTimestamp = (resume ? (LoadJson(overrideTempDir).ContainsKey("creationTimestamp") ? LoadJson(overrideTempDir)["creationTimestamp"] : "-1") : timestamp);
            }
            catch (Exception e)
            {
                RunTask.Fail($"Error creating av1an command: {e.Message}");
                Logger.Log($"{e.StackTrace}", true);
                DiscardUnusedTempFolder(tempDir, resume);
                Program.MainWin.SetWorking(false);
                return;
            }

            if (Hotkeys.ShiftHeld) // Allow reviewing and editing command if shift is held
            {
                string edited = await EditCommandWindow.Show("av1an", args);

                if (string.IsNullOrWhiteSpace(edited))
                {
                    // Backing out of the edit window is the user's decision, so no error box - but it
                    // is not a finished encode either, and returning silently had a batch mark the
                    // file Done with nothing written.
                    RunTask.Cancel("The command was cleared in the edit window, so nothing was run.", noMsgBox: true);
                    DiscardUnusedTempFolder(tempDir, resume);
                    Program.MainWin.SetWorking(false);
                    return;
                }

                args = edited;
            }

            // Written only now, so that what a resume replays is the command that actually ran. Saving
            // it before the edit window meant holding Shift to fix a command left the broken one on disk.
            SaveJson(sourcePath, tempDirName, args, creationTimestamp, timestamp);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            }
            catch (Exception e)
            {
                RunTask.Fail($"Failed to create output folder: {e.Message}");
                DiscardUnusedTempFolder(tempDir, resume);
                Program.MainWin.SetWorking(false);
                return;
            }

            Logger.Log($"Running:\nav1an {args}", true, false, "av1an");

            if (!resume) // A resumed encode finds audio.mkv already written, attachment and all
                _ = Task.Run(() => CreateAttachmentMkv(args, tempDir));

            int exitCode = await AvProcess.RunAv1an(args, AvProcess.LogMode.OnlyLastLine, true);

            if (subsToAddAfter.Count > 0 && !RunTask.canceled)
                await AddSubtitlesToMp4(inPath, outPath, subsToAddAfter);

            Program.MainWin.SetWorking(false);

            // Both halves matter. av1an failing on its own - a crashed encoder, a full disk, an argument
            // it would not take - sets nothing on RunTask and used to be indistinguishable from success,
            // so the finished chunks were deleted at exactly the moment they were worth the most.
            bool succeeded = exitCode == 0 && !RunTask.canceled && IoUtils.GetFilesize(outPath) > 0;

            if (!succeeded && !RunTask.canceled)
            {
                RunTask.Fail($"av1an did not finish{(exitCode != 0 ? $" (exit code {exitCode})" : $" - '{Path.GetFileName(outPath)}' was not written")}.");
            }

            if (succeeded)
                RunTask.ReportOutput(new[] { inPath }, outPath);

            await HandleTempFolder(tempDir, succeeded, RunTask.canceledManually);
            RefreshResumeButton(); // This run either added a resumable folder or cleared one
        }

        /// <summary>
        /// Whether Vship sits in the portable VapourSynth plugin folder, or null when there is no
        /// such folder to look in - a system VapourSynth (Linux, macOS, or a custom install) loads
        /// plugins from wherever it likes, and their presence cannot be cheaply known from here.
        /// Only ".dll" files count, because only those autoload: a probe's parked ".bak" carries
        /// the same name and would otherwise vouch for a plugin VapourSynth will not be loading.
        /// </summary>
        private static bool? HasVshipInPortablePlugins()
        {
            try
            {
                string dir = Path.Combine(Paths.GetBinPath(), "av1an", "vsynth", "vs-plugins");

                if (!Directory.Exists(dir))
                    return null;

                return Directory.EnumerateFiles(dir)
                    .Select(f => Path.GetFileName(f).ToLower())
                    .Any(f => f.EndsWith(".dll") && f.Contains("vship"));
            }
            catch (Exception e)
            {
                Logger.Log($"Could not inspect the VapourSynth plugin folder: {e.Message}", true);
                return null;
            }
        }

        /// <summary>
        /// Why a Butteraugli run without Vship is being stopped, with the part that varies said
        /// precisely: no Vship installed at all, this machine failing the bundled build's GPU
        /// check, or the check going unanswered - blaming the GPU in all three alike would tell
        /// a user with a capable card that their hardware flunked a check that never ran.
        /// </summary>
        private static string GetButteraugliBlockedMessage()
        {
            string intro = "Target Butteraugli cannot work like this: av1an calls the CPU scoring plugin " +
                "(julek) by the wrong function name in every release to date, ";

            if (!VshipStager.HasParkedBuilds())
                return intro + "and no Vship plugin is installed.\n\nPick Target SSIMULACRA2 or Target " +
                    "XPSNR instead, or install the GPU plugin Vship (NVIDIA/AMD) into bin/av1an/vsynth/vs-plugins.";

            if (VshipStager.SessionVerdict == false)
                return intro + "and no GPU in this machine passes the bundled Vship plugin's check - it " +
                    "needs a supported NVIDIA or AMD GPU with current drivers.\n\n" +
                    "Pick Target SSIMULACRA2 or Target XPSNR instead.";

            return intro + "and the bundled Vship plugin's GPU check could not get an answer just now.\n\n" +
                "Pick Target SSIMULACRA2 or Target XPSNR instead, or try again in a moment.";
        }

        /// <summary> The metric a target quality mode steers by, as it is named in messages. </summary>
        private static string GetTargetMetricName(QualityMode mode)
        {
            return mode == QualityMode.TargetSsimu2 ? "SSIMULACRA2"
                : mode == QualityMode.TargetButteraugli ? "Butteraugli"
                : mode == QualityMode.TargetXpsnr ? "XPSNR" : "VMAF";
        }

        /// <summary>
        /// Cuts the configured section out of the input and returns the copy av1an should be given,
        /// or "" when the tab has no trim configured and the whole file is to be encoded.
        /// <para/>
        /// av1an has no trim of its own, and the ffmpeg arguments it does take are no substitute: it
        /// applies them to each chunk it cuts rather than to the source once, so a '-ss' handed to it
        /// would be applied as many times as there are chunks. Encoding part of a video therefore
        /// means giving av1an a file that is only that part. The copy is a stream copy - seconds of
        /// work and the section's own size on disk - and, being a copy, it can only begin at a
        /// keyframe, which is why the cut dialog puts the start point on one before handing it over.
        /// </summary>
        private static async Task<string> CutTrimmedInput(string inPath, string tempDir)
        {
            TrimSettings trim = Av1anUi.CurrentTrim;
            MediaFile file = TrackList.current?.File;

            if (trim == null || trim.IsUnset || file == null)
                return "";

            string problem = UtilCut.ResolveSection(trim, file, out long start, out long end);

            if (problem.IsNotEmpty())
            {
                RunTask.Cancel(problem);
                return "";
            }

            string outPath = Av1anUi.GetTrimmedInputPath(tempDir, Path.GetExtension(inPath));

            Logger.Log($"Cutting {UtilCut.FormatDuration(start)} to {UtilCut.FormatDuration(end)} ({UtilCut.FormatDuration(end - start)}) out of " +
                $"{file.Name} first - av1an has no trim of its own, so it is given a copy of just that section to encode.");

            if (await UtilCut.CopySection(inPath, outPath, start, end))
                return outPath;

            // Nothing to add if the run has already been reported - which the trim's own ffmpeg call
            // can do if it fails on its way out.
            if (!RunTask.canceled && !RunTask.failed)
                RunTask.Cancel($"Could not cut the section to encode out of '{file.Name}'. The log has the details.");

            return "";
        }

        /// <summary>
        /// Removes a temp folder that was created for a run which then never started. Only ever the
        /// folder minted for this run: the one a resume points at holds the chunks being resumed from,
        /// and backing out of a resume is not a reason to throw those away.
        /// </summary>
        private static void DiscardUnusedTempFolder(string tempDir, bool resume)
        {
            if (resume || tempDir.IsEmpty())
                return;

            DeleteTempFolder(tempDir);
            AvProcess.lastTempDirAv1an = "";
        }

        /// <summary>
        /// The value of a quoted argument such as -i or -o, read back out of a command line.
        /// <para/>
        /// Reading to the end of the string instead only worked while the flag was last, and -o is not:
        /// the target quality options are appended after it. A resumed VMAF encode therefore took its
        /// output path to be the path plus every flag that followed, and died creating a folder for it.
        /// </summary>
        private static string ParseQuotedArg(string args, string flag)
        {
            string token = $"{flag} \"";

            for (int at = args.IndexOf(token); at >= 0; at = args.IndexOf(token, at + 1))
            {
                if (at > 0 && !char.IsWhiteSpace(args[at - 1])) // The tail of a longer flag, not this one
                    continue;

                int start = at + token.Length;
                int end = args.IndexOf('"', start);

                if (end >= 0)
                    return args.Substring(start, end - start).Trim();
            }

            return "";
        }

        /// <summary>
        /// Copies the source's text subtitle tracks into a finished MP4 as tx3g. av1an cannot carry them
        /// through its own pipeline - it muxes an intermediate audio.mkv first, and no subtitle codec is
        /// legal in both Matroska and MP4 - so adding them afterwards is the only way they get in.
        /// Nothing is re-encoded: video and audio are copied across untouched.
        /// </summary>
        private static async Task AddSubtitlesToMp4(string sourcePath, string outPath, List<int> textSubIndices)
        {
            if (!File.Exists(outPath) || !File.Exists(sourcePath))
            {
                Logger.Log($"Not adding subtitles: the {(File.Exists(outPath) ? "source" : "output")} file is missing.", true);
                return;
            }

            string tempPath = IoUtils.FilenameSuffix(outPath, ".subs");

            try
            {
                Logger.Log($"Adding {textSubIndices.Count} subtitle track{(textSubIndices.Count == 1 ? "" : "s")} to the finished MP4...");

                // Named one by one rather than '-map 1:s': the image-based tracks cannot become tx3g and
                // would fail the mux, and they were reported as left out when the encode started.
                string subMaps = string.Join(" ", textSubIndices.Select(i => $"-map 1:s:{i}"));
                string faststart = Config.GetBool(Config.Key.mp4Faststart) ? "-movflags +faststart" : "";
                string args = $"-i {outPath.Wrap()} -i {sourcePath.Wrap()} -map 0 {subMaps} " +
                    $"-c copy -c:s mov_text {faststart} {tempPath.Wrap()}";

                Logger.Log($"Running:\nffmpeg {args}", true, false, "ffmpeg");
                await AvProcess.RunFfmpeg(new AvProcess.FfmpegSettings() { Args = args, LoggingMode = AvProcess.LogMode.OnlyLastLine });

                // The encode that just finished is worth far more than the subtitles, so it is only
                // replaced once ffmpeg has actually written something in its place.
                if (!File.Exists(tempPath) || new FileInfo(tempPath).Length < 1)
                {
                    Logger.Log("Could not add the subtitles - the encode itself is unaffected.");
                    IoUtils.TryDeleteIfExists(tempPath);
                    return;
                }

                File.Delete(outPath);
                File.Move(tempPath, outPath);
                Logger.Log("Added the subtitles to the finished MP4.");
            }
            catch (Exception e)
            {
                Logger.Log($"Failed to add subtitles to the output: {e.Message}");
                Logger.Log($"{e.StackTrace}", true);
                IoUtils.TryDeleteIfExists(tempPath);
            }
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

                // Everything from -i onwards: the resume flag and the temp folder are set again by
                // whoever resumes, so saving this run's would point the next one at the wrong place.
                int inputAt = args.IndexOf(" -i ");

                if (inputAt < 0)
                {
                    Logger.Log($"Not saving resume info: the command names no input file.", true);
                    return;
                }

                info.Add("fileName", Path.GetFileName(inputFilePath));
                info.Add("filePath", inputFilePath);
                info.Add("tempFolderName", tempFolderName);
                info.Add("args", args.Substring(inputAt + 1).Trim());
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
