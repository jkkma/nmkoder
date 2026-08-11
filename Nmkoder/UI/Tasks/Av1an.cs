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
            // Replaced below when the arguments come from the UI. A replayed command carries whatever
            // filters it was saved with, so nothing here may leak into it from an earlier run.
            Av1anUi.CurrentDeinterlace = new DeinterlacePlan();
            Av1anUi.CurrentToneMap = new ToneMapConfig();
            Av1anUi.CurrentGrain = null;
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
            // Snapshots that outlive the block they are read in: the parallel scene detection runs
            // after the input passes, far below, and re-reading the boxes there could disagree with
            // the command already built - the tab stays editable through every await in between.
            ChunkMethod chunkMethod = ChunkMethod.LSMASH;
            bool sceneDetection = false;
            int sceneDetectSlices = 0;
            string scDownscaleArg = "";
            string keyIntArg = "";
            // The --scenes argument this run appended, exactly as appended, so the retry below can
            // take it back out of the command with a plain string replace. Empty when none was.
            string scenesArg = "";
            // The encode's geometry, hoisted with the snapshots above for the same reason: it is
            // resolved while the arguments are built and read again by the tone-map pass far below,
            // which renders the geometry itself when frame.GeometryInPass says so. Null exactly when
            // a replayed resume skipped building arguments - the same runs that skip the passes.
            Av1anFrame frame = null;

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
                    chunkMethod = GetCurrentChunkMethod();
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

                    // Asked of the binary rather than assumed, for the same reason the two checks above
                    // ask av1an: an encoder refuses the entire command over one parameter it does not
                    // know, so a grid row naming something this build lacks fails every chunk. The help
                    // texts are read once per session, so this costs nothing after the first encode.
                    string advancedArgsProblem = await GetUnsupportedAdvancedArgsProblem(vCodec);

                    if (advancedArgsProblem.IsNotEmpty())
                    {
                        RunTask.Cancel(advancedArgsProblem);
                        return;
                    }

                    // MP4 forces the ffmpeg concatenator (mkvmerge cannot write MP4), and av1an itself
                    // warns that vpx chunks come out of that path with the wrong frame rate.
                    if (mp4 && vCodec == CodecUtils.Av1anCodec.Vpx)
                        Logger.Log("Note: VP9 in MP4 has to be concatenated by ffmpeg, which av1an warns can give the file a wrong frame rate. MKV avoids this.");

                    inPath = sourcePath = TrackList.current.File.ImportPath;
                    ValidatePath();
                    outPath = UiData.GetOutPath();
                    TrackList.current.File.ColorData = await ColorDataUtils.GetColorData(TrackList.current.File.SourcePath);
                    ToneMapUi.RefreshInfo(); // The row's own answer to "is this file HDR" comes from what was just read

                    // Asked before anything is built, because the alternative is av1an meeting "No such
                    // filter: 'zscale'" once per chunk, having already split the file into them.
                    // Read once, here, and every later use goes through the snapshot - see
                    // Av1anUi.CurrentToneMap for why the box must not be read again further down.
                    Av1anUi.CurrentToneMap = ToneMapUi.GetAv1anConfig();

                    // Which tone-mapper runs, settled before the check below because the check is about
                    // the zscale chain's filters and libplacebo names none of them. On the zscale path
                    // this also measures the file's real peak - off the whole source, which for a
                    // trimmed encode overstates the section at worst, a direction the roll-off forgives
                    // where measuring the cut copy would mean waiting for the cut to run first.
                    await ToneMapUi.ResolveBackendAsync(Av1anUi.CurrentToneMap, TrackList.current.File);
                    string toneMapProblem = await ToneMapUi.GetProblem(Av1anUi.CurrentToneMap, TrackList.current.File?.ColorData);

                    if (toneMapProblem.IsNotEmpty())
                    {
                        RunTask.Cancel(toneMapProblem);
                        return;
                    }

                    // Settled here rather than at the encode, because the encoder's arguments are built
                    // on the next line and one of the things this decides is whether they carry
                    // --fgs-table at all. The table is the user's own file: this row runs no measuring
                    // pass any more, so there is no run-produced table to name a path for.
                    GrainSynthConfig grainConfig = GrainSynthUi.GetAv1anConfig();
                    string grainTablePath = grainConfig.TablePath;

                    GrainDelivery grainDelivery = await GrainSynthUi.ResolveDeliveryAsync(grainConfig, vCodec, grainTablePath);
                    string grainSetupProblem = await GrainSynthUi.GetProblemAsync(grainConfig, grainDelivery, vCodec, grainTablePath);

                    if (grainSetupProblem.IsNotEmpty())
                    {
                        RunTask.Cancel(grainSetupProblem);
                        return;
                    }

                    Av1anUi.CurrentGrain = new GrainPlan { Config = grainConfig, Delivery = grainDelivery, TablePath = grainTablePath };

                    // Kept rather than built inline: the pixel format the color format box resolved to is
                    // needed again below, to work out who should convert to it.
                    Dictionary<string, string> videoArgs = GetVideoArgsFromUi();
                    // Pinned to the snapshot - GetVideoArgsFromUi read the boxes again, and these
                    // two decide whether the encoder emits its own quality flag at all.
                    videoArgs["qMode"] = ((int)qualMode).ToString();
                    videoArgs["q"] = ((int)quality).ToString();
                    string pixFmt = videoArgs.ContainsKey("pixFmt") ? videoArgs["pixFmt"] : "";

                    // Settled before the filters are built, and once: in Automatic mode working out
                    // whether the source is interlaced can mean decoding a few hundred frames of it,
                    // and the answer decides whether av1an's own '-f' chain carries a deinterlacer.
                    // The verdict first, because the request below is read out of the mode box and the
                    // scan that points that box at this file runs in the background: a batch starts the
                    // encode the moment the file is loaded, so without this the two race and a file
                    // needing a real scan is encoded with the previous file's engine.
                    //
                    // Whatever comes back is an ffmpeg filter running inside av1an: this tab offers no
                    // QTGMC (DeinterlaceUi.Av1anQtgmcProblem says why) so there is no pass in front and
                    // nothing here can ask for one frame per field - which av1an's chunking could not
                    // be given anyway, a filter emitting two frames for every one it takes writing
                    // twice the frames each chunk expects under the source's own rate.
                    await DeinterlaceUi.EnsureScanVerdictAsync(TrackList.current.File);
                    Av1anUi.CurrentDeinterlace = await Deinterlace.ResolveAsync(TrackList.current.File, DeinterlaceUi.GetAv1anRequest());

                    if (Av1anUi.CurrentDeinterlace.Runs)
                        Logger.Log($"Deinterlacing '{TrackList.current.File.Name.Trunc(40)}' with {Av1anUi.CurrentDeinterlace.Describe()}.");

                    // The frame the encoder will actually be handed, worked out before its arguments
                    // rather than after them: the tile count is a property of that frame and not of the
                    // file it came from, and a crop or a resize makes the two different sizes. This is
                    // also where an automatic crop gets measured, which is why the filters below are
                    // handed the answer instead of going and asking for it a second time.
                    frame = await ResolveFrameAsync();

                    // Said here as well as on the tab's readout, because this is the last point at which
                    // it can be said clearly. ffmpeg refuses a frame this large from inside av1an, one
                    // chunk at a time, as "Picture size WxH is invalid" - which names neither the resize
                    // that asked for it nor the box to change.
                    // Same reasoning as the frame limit below, one step earlier in the chain: a crop
                    // that does not fit the file in front of it produces "crop=-80:1080:1000:0", which
                    // av1an meets as an ffmpeg error per chunk. The commonest way in is a batch, where
                    // the crop set for the first file is still set for a smaller one later.
                    if (frame.CropProblem.IsNotEmpty())
                    {
                        RunTask.Cancel($"'{TrackList.current.File.Name.Trunc(40)}' cannot be cropped as configured - " +
                            $"{frame.CropProblem}.\n\nChange the crop, or switch it off for this file.");
                        return;
                    }

                    if (ResizeConfig.ExceedsFrameLimit(frame.Encoded))
                    {
                        // Named by whichever setting asked for it. The bars are additive, so they can
                        // be what carries a frame over on their own - a very tall source padded out to
                        // an ultrawide ratio - and pointing at the resize dialog would then send the
                        // user to a box that is not the one to change.
                        string culprit = frame.Border.Runs
                            ? "Pick a smaller resize target, or switch the borders off."
                            : "Pick a smaller target in the resize dialog.";

                        RunTask.Cancel($"The encode would be {frame.Encoded.Width}x{frame.Encoded.Height}, which is " +
                            $"{(double)frame.Encoded.Width * frame.Encoded.Height / 1_000_000d:0.#} megapixels - more than FFmpeg " +
                            $"will scale to, so no frame would be written.\n\n{culprit}");
                        return;
                    }

                    if (!frame.Encoded.IsEmpty)
                        videoArgs[CodecUtils.FrameSizeKey] = $"{frame.Encoded.Width}x{frame.Encoded.Height}";

                    // The encoders on this tab are *told* what colour they are encoding, as H.273
                    // integers out of MediaFile.ColorData, where Quick Convert's read it off the frames
                    // ffmpeg hands them. So a tone-map that is not accounted for here produces the worst
                    // possible outcome: SDR pixels in a file tagged PQ and BT.2020, which every player
                    // then expands again. Nothing about the picture would look wrong until it was played.
                    //
                    // Swapped for the one call rather than assigned, because the field means "the colour
                    // of this file" everywhere else - ToneMapUi.IsRowRelevant reads it to decide whether
                    // to show the row at all, and leaving BT.709 behind would make an HDR file stop
                    // looking like one the moment it had been encoded once. There is no await between the
                    // two, so nothing can observe the swap.
                    VideoColorData sourceColor = TrackList.current.File.ColorData;
                    bool toneMapping = Av1anUi.CurrentToneMap.Runs && ColorDataUtils.IsHdr(sourceColor);
                    CodecArgs codecArgs;

                    try
                    {
                        if (toneMapping)
                            TrackList.current.File.ColorData = ToneMapConfig.GetOutputColorData(sourceColor);

                        codecArgs = CodecUtils.GetCodec(vCodec).GetArgs(videoArgs, TrackList.current.File, Data.Codecs.Pass.OneOfOne);
                    }
                    finally
                    {
                        TrackList.current.File.ColorData = sourceColor;
                    }

                    // Whether the tone-map pass renders the geometry too, decided before either chain
                    // is built so the filters land in exactly one place. Folding it in is what sizes
                    // that intermediate to the *encode* instead of the source - a 4K film scaled to
                    // 1080p otherwise pays for four times the pixels the encoder ever sees, reported
                    // at tens of gigabytes for a five-minute test clip. The one thing that may not
                    // move with it is a per-chunk deinterlacer: that filter must see whole fields, and
                    // the pass runs first - so bwdif in the chain keeps the geometry per-chunk, behind
                    // it, where it always was.
                    frame.GeometryInPass = Av1anUi.ToneMapRendersInFront(sourceColor)
                        && Av1anUi.CurrentDeinterlace.GetFfmpegFilter().IsEmpty();

                    string vf = GetVideoFilterArgs(frame, sourceColor, codecArgs);
                    frame.PassGeometryFilters = Av1anUi.GetPassGeometryFilterArgs(frame);
                    // Deliberately built without the media file: that is what tells the audio arguments
                    // to come out unindexed, which is what av1an needs. Its own '-map 0' carries every
                    // audio track, and this tab has one bitrate and one channel count for all of them.
                    string ffAud = CodecUtils.GetCodec(aCodec).GetArgs(GetAudioArgsFromUi()).Arguments;
                    var form = Program.MainWin;

                    // Said before the run for the same reason the memory estimate is: on a remux
                    // carrying a dozen languages, every track getting the one setting is expensive
                    // and quiet, and nothing on this tab can select tracks.
                    string audioNote = GetMultiTrackAudioNote(TrackList.current?.File, aCodec, form.Av1anAudQualUpDown.Value.AsInt());

                    if (audioNote.IsNotEmpty())
                        Logger.Log(audioNote);
                    bool copySubs = form.CheckAv1anCopySubs.IsChecked == true;
                    List<int> bitmapSubs = GetBitmapSubtitleIndices(TrackList.current?.File);
                    string ffMux = BuildMuxArgs(copySubs, form.CheckAv1anCopyData.IsChecked == true, form.CheckAv1anCopyAttachs.IsChecked == true, mp4, webm, bitmapSubs);

                    string hbdProblem = GetHbdModeDecisionProblem(vCodec, pixFmt);

                    if (hbdProblem.IsNotEmpty())
                        Logger.Log(hbdProblem);

                    string grainProblem = GetGrainSynthProblem(vCodec, grainConfig, grainDelivery);

                    if (grainProblem.IsNotEmpty())
                        Logger.Log(grainProblem);

                    // Separate from the check above because it is a different kind of clash: nothing is
                    // overwritten and no argument loses, the grid is simply protecting grain the row has
                    // just removed. It is also the only one a content preset can cause.
                    string retentionProblem = GetGrainRetentionProblem(vCodec, grainConfig);

                    if (retentionProblem.IsNotEmpty())
                        Logger.Log(retentionProblem);

                    string tuneProblem = GetFilmGrainTuneProblem(vCodec);

                    if (tuneProblem.IsNotEmpty())
                        Logger.Log(tuneProblem);

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

                    // Said before the run rather than discovered during it. Workers is the memory axis -
                    // each one holds an encoder, a decoder and, where this tab sets any filters, the
                    // ffmpeg applying them - and a machine that cannot hold them all does not fail in a
                    // way anybody can read: see Av1anMemory for what av1an prints instead, which names
                    // neither memory nor the box to change. Everything the estimate needs is settled by
                    // here - the encoder, the frame it is handed, the source it comes from and the chain
                    // in between.
                    // With the geometry folded into the pass, the workers decode the pass's output -
                    // frames already at the encoded size - so that is the size their decode is priced
                    // at, and the residual chain (often empty now) is what the filter ffmpeg costs.
                    string memoryProblem = Av1anMemory.GetProblem(form.Av1anOptsWorkerCountUpDown.Value.AsInt(),
                        vCodec, frame.Encoded, frame.GeometryInPass ? frame.Encoded : frame.Source, vf);

                    if (memoryProblem.IsNotEmpty())
                        Logger.LogWarn(memoryProblem);

                    // Captured once and spliced in, because the parallel scene detection far below
                    // hands the same strings to its slice runs - the slices have to detect at the
                    // resolution and subdivide at the -x the encode itself will carry, and reading
                    // the boxes twice could answer two different things.
                    sceneDetection = Av1anUi.SceneDetectionEnabled;
                    sceneDetectSlices = form.Av1anOptsScDetectSlicesUpDown.Value.AsInt();
                    scDownscaleArg = GetScDownscaleHeightArg();
                    keyIntArg = CodecUtils.GetKeyIntArg(TrackList.current.File, Config.GetInt(Config.Key.DefaultKeyIntSecs), "-x ");

                    // The input is not named here. A trim has to cut its section out first, and where
                    // that copy goes is only settled once this run's temp folder is, so the '-i' is
                    // put in front of all of this below, after the cut has run.
                    args = $"-y --verbose --keep " +
                        $"{GetSplittingMethodArgs()} " +
                        $"{GetChunkGenMethod(chunkMethod)} " +
                        $"{GetConcatMethodArgs(vCodec)} " +
                        $"{GetChunkOrderArgs()} " +
                        $"{scDownscaleArg} " +
                        $"{codecArgs.Arguments} " +
                        $"{pixFmtConverter} " +
                        $"{ffFilters}" +
                        $"-a \" {ffArgs} \" " +
                        $"-w {form.Av1anOptsWorkerCountUpDown.Value.AsInt()} " +
                        $"{keyIntArg} " +
                        $"-o {outPath.Wrap()}";

                    // av1an counts the frames every finished chunk holds and compares them with the
                    // number it expects, failing the chunk when they differ - and then retrying it,
                    // three times over, before shutting the worker down and with it the run. A frame
                    // rate change is that mismatch by construction, on every chunk: writing a
                    // different number of frames than came in is the entire point of the filter. So
                    // the Frame Rate box killed any encode it was used on, hours in and after each
                    // doomed chunk had been encoded four times. --ignore-frame-mismatch is av1an's
                    // own answer to exactly this - its concat step reads the flag as "an FPS changing
                    // filter might have been applied" and stops forcing the source's rate onto the
                    // output, which is the other half of what a resampled encode needs.
                    if (frame.ResamplesFrameRate)
                    {
                        // Old enough that an av1an without it would fail on half this tab's command
                        // anyway; checked all the same, because one unrecognised flag is refused as a
                        // whole command. A help text that could not be read says nothing, so the flag
                        // goes out and an av1an that really is too old refuses it at startup.
                        if (!await AvProcess.Av1anHelpKnown() || await AvProcess.Av1anSupportsFlag("--ignore-frame-mismatch"))
                            args += " --ignore-frame-mismatch";
                        else
                            Logger.Log("Warning: this av1an has no --ignore-frame-mismatch, and the frame rate is being " +
                                "changed. av1an checks every chunk's frame count against the source's and will fail the " +
                                "encode over the difference. Leave the Frame Rate box empty, or update av1an.");
                    }

                    if (qualMode != QualityMode.Crf)
                    {
                        if (vf.Length > 3)
                            Logger.Log(GetFilteredTargetQualityNote(frame));

                        if (qualMode == QualityMode.TargetSsimu2 || qualMode == QualityMode.TargetButteraugli || qualMode == QualityMode.TargetXpsnr)
                        {
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
                            // No --vmaf-filter, deliberately. It filters the *reference* VMAF scores
                            // against, while the probe it is compared with comes off the unfiltered
                            // source - so handing it this tab's chain measures a filtered reference
                            // against an unfiltered encode. With a resize that is a sharp probe against
                            // a softened downscale-and-back-up reference, which scores far under the
                            // truth and drags the quantizer down with it; and where the chain also
                            // changes the aspect ratio - an anamorphic de-squeeze, a crop, an exact size
                            // that pads - the two frames come out different sizes after av1an's own
                            // scale to --vmaf-res and libvmaf refuses them outright ("input width must
                            // match"), minutes into a run. Both sides unfiltered is at least like for
                            // like; that the search then runs at the source's size is what the note
                            // above says out loud.
                            args += $" --target-quality {(int)quality} --vmaf-path {Paths.GetVmafPath().Wrap()} --vmaf-threads 2";
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
                tempDir = Directory.CreateDirectory(GetTempDirPath(overrideTempDir, timestamp)).FullName;
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

                    // Every pass from here on preserves frame numbering - the tone map changes pixels,
                    // never the count or the order - so a scene list detected on this file indexes the
                    // file av1an will open, frame for frame. That is what lets the detection run
                    // *alongside* the pass instead of after it: the two phases the workers cannot help
                    // with hide behind each other. The deinterlacer is no longer a reason to start the
                    // overlap later than this - it runs inside av1an now, downstream of everything
                    // here - but a bob does write one frame per field, so nothing that renumbers frames
                    // may ever be moved in front of this point.
                    VideoColorData overlapColor = TrackList.current?.File?.ColorData;
                    bool passesFollow = Av1anUi.ToneMapRendersInFront(overlapColor);
                    Task<(string scenesFile, int frames)> earlySceneTask = null;
                    string sceneDetectInput = inPath;

                    if (!resume && sceneDetection && chunkMethod == ChunkMethod.LSMASH && passesFollow)
                    {
                        Logger.Log("Scene detection runs alongside the tone-map pass - it changes pixels, not frame numbers, " +
                            "so the list detected on its input fits its output.", true);
                        earlySceneTask = Av1anSceneDetect.TryPrepareScenesFileAsync(inPath, tempDir, scDownscaleArg, keyIntArg, sceneDetectSlices);
                    }

                    // The last of the input passes, and the only one left: on whatever the trim left,
                    // and the file av1an is handed. The grain denoise that used to follow it is gone
                    // with the modes that needed it - see GrainSynthConfig.EncodeModes.
                    string toneMapped = await RenderToneMappedInput(inPath, tempDir, resume, frame);

                    if (RunTask.canceled || RunTask.failed)
                    {
                        await SettleSceneDetectionAsync(earlySceneTask);
                        DiscardUnusedTempFolder(tempDir, resume);
                        Program.MainWin.SetWorking(false);
                        return;
                    }

                    if (toneMapped.IsNotEmpty())
                        inPath = toneMapped;

                    // Scene detection is the one phase of an av1an run the workers cannot help with -
                    // it is what creates the chunks they work on - so where the pieces allow it, it is
                    // run here instead, split across parallel slices of the input, and av1an is handed
                    // the finished list via --scenes so it skips its own sequential pass. Overlapped
                    // with the render passes above where those ran, sequentially on the final input
                    // otherwise.
                    // Opportunistic by design: "" on any obstacle, and the encode runs as it always
                    // did. LSMASH only, because the list's frame numbers have to be the ones the
                    // encode's own chunking will count - see Av1anSceneDetect for the whole argument.
                    // Not on a resume: av1an's own temp folder state already carries its scenes, and a
                    // resume with new settings may have changed the trim, which would make a kept list
                    // describe frames that are no longer the ones being encoded.
                    if (!resume && sceneDetection && chunkMethod == ChunkMethod.LSMASH)
                    {
                        (string scenesFile, int detectedFrames) = earlySceneTask != null
                            ? await earlySceneTask
                            : await Av1anSceneDetect.TryPrepareScenesFileAsync(inPath, tempDir, scDownscaleArg, keyIntArg, sceneDetectSlices);

                        if (RunTask.canceled || RunTask.failed)
                        {
                            DiscardUnusedTempFolder(tempDir, resume);
                            Program.MainWin.SetWorking(false);
                            return;
                        }

                        // The tripwire for the invariant the overlap rests on. The passes preserve the
                        // count by construction, so this only ever fires on a regression in one of
                        // them - and it is a duration check rather than a packet count because the
                        // exact count costs a full read of a file that is now several hundred
                        // gigabytes, where the headers cost nothing. It catches every structural
                        // change (a doubled rate, a dropped tail, a trim); a single-frame drift slips
                        // it, and would cost av1an its final chunk rather than the run. Discarding is
                        // the safe direction: the encode falls back to av1an's own in-run detection.
                        if (scenesFile.IsNotEmpty() && inPath != sceneDetectInput &&
                            !await Av1anSceneDetect.DurationsMatchAsync(sceneDetectInput, inPath))
                        {
                            Logger.Log("The scene list was detected on an input whose duration no longer matches the prepared " +
                                "file, so it is discarded and av1an detects in-run. A render pass changed the timing, which none should.");
                            IoUtils.TryDeleteIfExists(scenesFile);
                            scenesFile = "";
                        }

                        if (scenesFile.IsNotEmpty())
                        {
                            scenesArg = $" --scenes {scenesFile.Wrap()}";
                            args += scenesArg;
                        }
                    }
                    else if (!resume && sceneDetection && sceneDetectSlices > 1)
                    {
                        // The one stand-down the method above cannot say itself, since the gate is out
                        // here. Quiet like its own: nothing is wrong, av1an simply detects in-run.
                        Logger.Log($"Parallel scene detection only runs for the LSMASH chunk method, so with {chunkMethod} av1an detects in-run.", true);
                    }

                    args = $"-i {inPath.Wrap()} {args}";
                }

                args = $"{(resume ? "-r" : "")} --temp {tempDir.Wrap()} {await GetLogFileArgs(tempDir)}{args}";
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

            // The pre-detected scene list is the one argument whose loading side could not be
            // verified against the bundled binary (there is no av1an in the session that wrote
            // this), so it is treated as revocable: an av1an that stopped without encoding a single
            // chunk, on a command that carried one, is retried once without it. That caps what a
            // wrong assumption about --scenes can ever cost at one failed startup - where an
            // unrelated startup failure costs one extra attempt that fails the same way, logged.
            if (exitCode != 0 && !RunTask.canceled && !RunTask.failed && scenesArg.IsNotEmpty() && !AnyChunkEncoded(tempDir))
            {
                Logger.LogWarn("av1an stopped before encoding anything, and this run handed it a pre-detected scene " +
                    "list - retrying once without the list in case that is what it refused. The lines above say what " +
                    "av1an itself reported.");
                args = args.Replace(scenesArg, "");
                IoUtils.TryDeleteIfExists(Av1anUi.GetScenesFilePath(tempDir));
                SaveJson(sourcePath, tempDirName, args, creationTimestamp, timestamp); // So a resume replays the command that ran
                exitCode = await AvProcess.RunAv1an(args, AvProcess.LogMode.OnlyLastLine, true);
            }

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

        /// <summary>
        /// What to say about running a target quality mode over a filtered source.
        /// <para/>
        /// av1an encodes its probes from the source rather than from the filtered frames - probe_cmd
        /// composes the probe's ffmpeg pipe with nothing in it but the probing-rate select filter, and
        /// the chunk's own source command carries no filters either - so nothing set on this tab is
        /// visible to the quality search, whichever metric it steers by.
        /// <para/>
        /// The size clause is the part with teeth, which is why it names both numbers: a resize changes
        /// how many pixels the quantizer is being spread across, so a search settled at the source's
        /// size lands somewhere else entirely at the one being written. Filters that leave the size
        /// alone skew it too - a denoise makes frames cheaper to encode - but by how much is not
        /// something that can be said from here.
        /// </summary>
        private static string GetFilteredTargetQualityNote(Av1anFrame frame)
        {
            string note = "Note: av1an encodes its target quality probes from the source, not from the filtered " +
                "frames, so the video filters set on this tab are invisible to the quality search.";

            // Only when the size change actually happens per-chunk. With the geometry folded into
            // the tone-map pass, av1an's input - and so every probe - is already the encoded frame,
            // and this clause would claim the opposite of what the fold just fixed.
            if (frame != null && frame.ChangesSize && !frame.GeometryInPass)
                note += $" The probes will be {frame.Source.Width}x{frame.Source.Height} where the encode is " +
                    $"{frame.Encoded.Width}x{frame.Encoded.Height}, so the quantizer it settles on is the one that hits " +
                    "the target at the source's size rather than at the size being written. Encoding at the source's " +
                    "size, or using CRF, is the only way to be sure of the target.";

            return note;
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

        // There is no deinterlace pass here any more, and its absence is a decision rather than an
        // omission. QTGMC cannot run inside av1an - it composes an ffmpeg command per chunk and there
        // is nowhere in one to evaluate a VapourSynth script - so this rendered the whole file through
        // it into a lossless intermediate and handed av1an that. The trade never worked out: on the
        // captures QTGMC is for it is slower than the encoder, so the serial pass plus the parallel
        // encode always cost more than Quick Convert's pipe, which overlaps the two. That tab owns
        // QTGMC now, and the Deinterlace Video utility exports a file to encode here.
        // DeinterlaceUi.Av1anQtgmcProblem is the standing statement of it; the ffmpeg deinterlacers
        // this tab still offers go into av1an's own per-chunk filter chain and need no pass at all.

        /// <summary>
        /// Renders the tone map over the whole input and returns the SDR file av1an should be given,
        /// or "" where there is no such pass - <see cref="Av1anUi.ToneMapRendersInFront"/> is the gate,
        /// and with it the statement of which encodes have one.
        /// <para/>
        /// libplacebo cannot run inside av1an, for reasons this app measured rather than assumed. Its
        /// peak detection - the only way it can learn what a file's frames are actually brighter or
        /// darker than their metadata claims - carries history, and av1an starts and stops the filter
        /// ffmpeg around every chunk: a detector restarting mid-scene lands 6 code values off the
        /// continuous answer and pumps as it converges. And with detection off it is worse than the
        /// zscale chain it replaced, because av1an's y4m pipes carry no HDR side data, so it read
        /// neither MaxCLL nor the mastering display and priced every file for the 10000-nit PQ
        /// ceiling. One continuous pass in front has neither problem, and hands av1an an SDR file its
        /// target-quality probes can actually score - the per-chunk chain is invisible to them.
        /// <para/>
        /// The zscale chain never lands here: it is stateless and runs per chunk inside av1an. It used
        /// to be pulled in front whenever a grain denoise pass followed - the grain having to be
        /// measured on the SDR frames being encoded - and there are no such passes on this tab any
        /// more, so libplacebo is the whole of the gate.
        /// </summary>
        private static async Task<string> RenderToneMappedInput(string inPath, string tempDir, bool resume, Av1anFrame frame)
        {
            ToneMapConfig config = Av1anUi.CurrentToneMap;
            VideoColorData srcColor = TrackList.current?.File?.ColorData;

            // Av1anUi.ToneMapRendersInFront is the whole gate, and it is libplacebo and nothing else.
            if (!Av1anUi.ToneMapRendersInFront(srcColor))
                return "";

            string outPath = Av1anUi.GetToneMappedInputPath(tempDir);
            MediaFile file = TrackList.current?.File;

            // Re-rendering the film to change a CRF would cost more than the encode being resumed.
            // Only ever a file this same encode wrote - a fresh run mints a temp folder from the
            // clock, so there is never one here already.
            if (resume && IoUtils.GetFilesize(outPath) > 0)
            {
                // With the geometry rendered by this pass, a kept file is only reusable if it holds
                // the frames the rebuilt command now expects - and a resume with current settings
                // re-reads the resize, the crop and the borders, so the two can disagree. The frame
                // size is the whole contract: folded, the file must already be the encoded frame;
                // unfolded, it must still be the source's, or a chain built to scale it would scale
                // it twice. One ffprobe answers it, against a kept file worth hours.
                Size expected = frame.GeometryInPass ? frame.Encoded : frame.Source;
                Size kept = await GetMediaResolutionCached.GetSizeAsync(outPath);

                if (!expected.IsEmpty && !kept.IsEmpty && kept != expected)
                {
                    Logger.Log($"The tone-mapped file this resume kept is {kept.Width}x{kept.Height} where the encode now " +
                        $"wants {expected.Width}x{expected.Height} - the geometry settings changed since it was rendered - " +
                        $"so it is rendered again.");
                    IoUtils.TryDeleteIfExists(outPath);
                }
                else
                {
                    Logger.Log($"Reusing the tone-mapped file this encode was started from ('{Path.GetFileName(outPath)}'). " +
                        $"The pass is not run again and the Tone Mapping setting is not re-read - delete that file to redo it.");
                    return outPath;
                }
            }

            // The size clause is most of what this announce is for - this intermediate is the largest
            // file this app writes, and the report that shaped it was ~40 GB of FFV1 for a five-minute
            // 4K test clip whose encode was 1080p. The codec is the measured-transparent x264, so the
            // clause states what was measured. Folded geometry that grows the frame (borders, an
            // upscale) keeps the plain sentence, which stays true.
            long encodedPx = (long)frame.Encoded.Width * frame.Encoded.Height;
            long sourcePx = (long)frame.Source.Width * frame.Source.Height;
            bool shrinks = frame.GeometryInPass && frame.ChangesSize && encodedPx < sourcePx && sourcePx > 0;
            string sizeClause = shrinks
                ? $"this is a full pass over the video, rendered straight to the {frame.Encoded.Width}x" +
                    $"{frame.Encoded.Height} the encode wants - the resize, crop and borders run in this same pass - " +
                    $"and the x264 settings were chosen by measuring what survives them: grain energy and tone " +
                    $"values come through intact."
                : "this is a full pass over the video; the x264 settings were chosen by measuring what survives them - " +
                    "grain energy and tone values come through intact, at about a tenth of the source's size.";

            Logger.Log($"Tone mapping '{file?.Name.Trunc(40)}' to SDR with libplacebo, into " +
                $"{ToneMapPass.DescribeOutput()} that av1an will then encode. Its peak detection measures the " +
                $"picture's real brightness as it goes, which needs one continuous run - inside av1an it would " +
                $"restart at every chunk - so it runs once here; {sizeClause}");

            string problem = await ToneMapPass.RunAsync(config, srcColor, inPath, outPath, file, frame.PassGeometryFilters);

            if (RunTask.canceled || RunTask.failed || problem.IsNotEmpty())
            {
                // Whatever is on disk stops partway through the video, and the reuse above would take
                // it for a finished pass on the next resume - so a stopped pass leaves nothing behind.
                IoUtils.TryDeleteIfExists(outPath);

                if (problem.IsNotEmpty() && !RunTask.canceled && !RunTask.failed)
                    RunTask.Fail($"The tone-map pass this encode needs did not finish, so av1an was not started.\n\n{problem}");

                return "";
            }

            return outPath;
        }

        /// <summary>
        /// Winds down an overlapped scene detection before a failed run returns, so its slices are not
        /// left grinding over an encode that is already dead and its scratch folder is cleaned before
        /// the temp folder goes. A cancel has already killed the slice processes - they run as
        /// Secondary - but <see cref="RunTask.Fail"/> kills nothing, so the kill here is what turns
        /// the detection's own abandon path from minutes of zombie decoding into an immediate return.
        /// Null is the common case and free: most early returns never started an overlap.
        /// </summary>
        private static async Task SettleSceneDetectionAsync(Task<(string scenesFile, int frames)> earlySceneTask)
        {
            if (earlySceneTask == null || earlySceneTask.IsCompleted)
                return;

            ProcessManager.KillSecondary();
            await earlySceneTask;
        }

        /// <summary> Where a run's temp folder is, worked out without creating it - the folder itself
        /// is made just before av1an is started. </summary>
        private static string GetTempDirPath(string overrideTempDir, string timestamp)
        {
            return Path.Combine(Paths.GetAv1anTempPath(), overrideTempDir.IsNotEmpty() ? overrideTempDir : timestamp);
        }

        // The denoise-and-measure pass that used to sit here is gone with the grain modes that
        // needed it. Measured from source rendered a lossless denoised copy of the whole film and
        // had grav1synth diff it - about 3.7 fps at 1080p, so a working day for a feature before
        // av1an was even started - and Grain table file with Denoise ticked paid the render half of
        // that for a table the user already had. Both are the Film Grain utility's Measure operation
        // and Quick Convert's one-chain hqdn3d now; see GrainSynthConfig.EncodeModes. What is left
        // on this row hands the encoder a number or a table and costs no pass at all.

        /// <summary>
        /// Whether av1an got as far as finishing a video chunk in this temp folder - the line between
        /// "refused the command at startup" and "failed partway through real work", which is what the
        /// retry-without---scenes above turns on. Sub-kilobyte files are av1an's own bookkeeping, the
        /// same reading Av1anUi.CountEncodedChunks makes. Not being able to tell counts as yes: the
        /// retry exists for a cheap failure, and rerunning hours of work is not cheap.
        /// </summary>
        private static bool AnyChunkEncoded(string tempDir)
        {
            try
            {
                string encodeDir = Path.Combine(tempDir, "encode");
                return Directory.Exists(encodeDir) && Directory.EnumerateFiles(encodeDir).Any(f => new FileInfo(f).Length >= 1024);
            }
            catch
            {
                return true;
            }
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

        /// <summary>
        /// Puts av1an's own log in this run's temp folder, next to everything else the run leaves
        /// behind. Emitted beside '--temp' rather than with the rest of the command, and for the same
        /// reason: both name a folder that only exists once this run has one, and both sit ahead of the
        /// '-i' that <see cref="SaveJson"/> starts saving from, so a resume sets them again for itself
        /// instead of inheriting the previous attempt's.
        /// <para/>
        /// Named rather than left to av1an, whose default is './logs/av1an.log' with the date appended -
        /// resolved against the working directory, which is 'bin/av1an' here. So every encode dropped a
        /// dated log beside the binary, in a folder nothing in this app knew about and nothing ever
        /// cleared. In the temp folder it lives exactly as long as the run's other state does, which is
        /// the right lifetime: <see cref="HandleTempFolder"/> keeps that folder when the encode failed,
        /// and a failed encode is when the log is worth reading.
        /// <para/>
        /// Nothing here parses it - the progress bar reads scenes.json and done.json, see
        /// <see cref="Media.Av1anOutputHandler"/> - so the exact file name is not load-bearing. Which is
        /// as well, because av1an appended its own ".log" to this value until 0.4.x and does not now.
        /// <para/>
        /// The flag is checked for all the same. It is old enough that nothing this app can drive should
        /// be without it, but av1an refuses a whole command over one argument it does not know, and that
        /// would be every encode rather than a missing log.
        /// </summary>
        private static async Task<string> GetLogFileArgs(string tempDir)
        {
            if (await AvProcess.Av1anHelpKnown() && !await AvProcess.Av1anSupportsFlag("--log-file"))
            {
                Logger.Log("Note: this av1an has no --log-file, so its own log stays wherever it puts it. " +
                    "The encode is unaffected.", true);
                return "";
            }

            return $"--log-file {Path.Combine(tempDir, "av1an.log").Wrap()} ";
        }

        /// <summary>
        /// The height to run scene detection at, or "" where there is nothing for it to say.
        /// <para/>
        /// Split Method "None" is one of those: av1an detects no scenes for it, so the flag named a
        /// resolution for a pass that never runs. The other is a file with no video track, where
        /// <see cref="GetScDownscaleHeight"/> has no height to work from and answers 0 - and 0 is not
        /// inert, since av1an only skips the downscale when the height it was given is *above* the
        /// source's. A zero goes through as scale=-2:'min(0,ih)', which ffmpeg refuses.
        /// </summary>
        private static string GetScDownscaleHeightArg()
        {
            if (!Av1anUi.SceneDetectionEnabled)
                return "";

            int height = GetScDownscaleHeight();
            return height > 0 ? $"--sc-downscale-height {height}" : "";
        }

        private static int GetScDownscaleHeight()
        {
            // current itself, not just its file - the rest of this class reads it the same way, and
            // the branch below was written to answer "no height to work from" rather than to throw.
            if (TrackList.current?.File == null || TrackList.current.File.VideoStreams.Count < 1)
                return 0;

            return GetScDownscaleHeightFor(TrackList.current.File.VideoStreams[0].Resolution.Height);
        }

        private static int GetScDownscaleHeightFor(int h)
        {
            float mult = 1f;

            // Every tier from 1080 up lands on 720 at its own threshold (1080 × 0.6667, 1440 × 0.5,
            // 2160 × 0.3333 and 4320 × 0.1667 are all 720). 2160 used to map to 900 and 4320 to
            // 1440, which made the two sources whose detection pass is slowest the only ones
            // analyzed above 720 - and that pass decodes at the source's own size regardless, so
            // the analysis height is the one part of its cost this can lower.
            if (h >= 720) mult = 0.7500f;
            if (h >= 900) mult = 0.7083f;
            if (h >= 1080) mult = 0.6667f;
            if (h >= 1440) mult = 0.5000f;
            if (h >= 2160) mult = 0.3333f;
            if (h >= 4320) mult = 0.1667f;

            // Clamped to sane values, then rounded down to even: av1an hands the height to a scale
            // filter feeding 4:2:0 detection frames, and an odd value (a 900-high source × 0.7083
            // is 637) is one ffmpeg refuses.
            return (h * mult).RoundToInt().Clamp(360, 2160) / 2 * 2;
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

                    // Only once mkvmerge has actually written the replacement. This delete was
                    // unconditional, and it is deleting av1an's encoded audio - so a run with no
                    // mkvmerge to call, which is every Linux and macOS build since bundle-tools.sh
                    // ships MKVToolNix for win-x64 alone, threw away the audio track and then failed
                    // the move into a catch that logs where nobody looks. The encode finished, the
                    // output had no sound, and nothing on screen said so. Windows is not exempt
                    // either: a full disk or a path too long lands in the same place.
                    //
                    // What is at stake on the other side is a text file naming the encoder arguments.
                    // Losing that is not worth a second's thought; losing the audio is the encode.
                    if (File.Exists(tmpOutPath))
                    {
                        File.Delete(outPath);
                        File.Move(tmpOutPath, outPath);
                    }
                    else
                    {
                        Logger.Log($"Could not attach the encode settings to the output - mkvmerge wrote nothing. The audio is untouched.", true);
                    }
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

        /// <summary> Encoder threads the whole run should add up to, as a fraction of the machine.
        /// Under one on purpose: av1an runs a decoding ffmpeg beside every worker, and scene
        /// detection and the concat step want somewhere to run too. </summary>
        private const double ThreadBudgetPerCore = 0.8;

        /// <summary> Workers is the memory axis - each one holds an encoder instance and its frames -
        /// so it is capped whatever the core count is. SVT-AV1 at preset 4 on 4K wants some GB per
        /// worker, and a machine that runs out reports it as chunks failing rather than as memory. </summary>
        private const int MaxDefaultWorkers = 32;

        /// <summary>
        /// The Workers and Threads per Worker a config that has neither yet opens on. Both come out of
        /// here together, because what has to track the machine is their <i>product</i>: workers alone
        /// says nothing about how much of the CPU is booked, and through 2.8.17 these were two unrelated
        /// constants - a computed worker count and a literal 2 - whose product landed near
        /// <see cref="ThreadBudgetPerCore"/> by coincidence, with nothing to notice if either moved.
        /// <para/>
        /// Splitting the budget rather than fixing the threads is what fixes the high core counts.
        /// <see cref="MaxDefaultWorkers"/> is a memory guard, but with the thread count pinned at 2 it
        /// was also silently a CPU cap: past 80 logical processors - the point where ceil(0.4c) first
        /// reaches 32 - nothing took up the slack the cap gave away, so a 128-core machine defaulted to
        /// booking half of itself and a 192-core one a third. Threads now takes the remainder.
        /// <para/>
        /// Rounded up rather than to nearest, because the two errors are not the same size: going a
        /// little over the budget costs some timeslicing, where going under leaves whole cores idle -
        /// at 96 cores the nearest-integer split is 2, which is the very hole this closes. The cost is
        /// that the first core counts past the cap come out slightly over the budget rather than under
        /// it (88 cores books 96 threads), which is the right way round to be wrong here: av1an's
        /// chunks are independent, so mild oversubscription is timeslicing rather than contention.
        /// <para/>
        /// The 0.4 is a double and must stay one. It was <c>0.4f</c>, multiplied against a
        /// <c>(double)</c> core count, so the widened float came out a hair above the real value and
        /// the ceiling took that as a whole extra worker on every core count where the product is
        /// exact - which is every multiple of 5. A 20-thread machine defaulted to 9 workers rather
        /// than 8, a 10-thread one to 5 rather than 4. Nothing in the arithmetic wanted that, and the
        /// cast that caused it is not needed at all: int times double is already double.
        /// </summary>
        public static (int Workers, int Threads) GetDefaultThreadPlan()
        {
            int cores = Environment.ProcessorCount;
            int workers = ((int)Math.Ceiling(cores * 0.4)).Clamp(2, MaxDefaultWorkers);
            int budget = Math.Max(2, (int)Math.Round(cores * ThreadBudgetPerCore));
            // Capped at 16 as well as floored at 2: a single encoder instance stops scaling well
            // before that, so past it the threads would be bought and not used.
            int threads = ((int)Math.Ceiling((double)budget / workers)).Clamp(2, 16);
            return (workers, threads);
        }
    }
}
