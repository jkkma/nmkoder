using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nmkoder.Data;
using Nmkoder.Data.Codecs;
using Nmkoder.Data.Ui;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.Media;
using Nmkoder.OS;
using Nmkoder.Views;
using static Nmkoder.UI.Tasks.QuickConvertUi;

namespace Nmkoder.UI.Tasks
{
    class QuickConvert
    {
        public enum QualityMode { Crf, TargetKbps, TargetMbytes }

        public static void Init()
        {
            QuickConvertUi.Init();
        }

        public static async Task Run()
        {
            Program.MainWin.SetWorking(true);
            SuspendResume.SetPauseButtonStyle(false);
            string args = "";
            string outPath = "";
            // Set while the arguments are built and read again after the run, so a VapourSynth script
            // that died two thirds of the way through is not mistaken for a finished encode.
            string vsLogPath = "";
            int vsRuns = 0;

            try
            {
                if (!TrackList.CheckedItems.Any())
                {
                    RunTask.Cancel($"No tracks are selected. Please select at least one track to use.");
                    Program.MainWin.SelectedMainTab = 1;
                    return;
                }

                IEncoder vCodec = CodecUtils.GetCodec(GetCurrentCodecV());
                CodecUtils.AudioCodec aCodec = GetCurrentCodecA();
                CodecUtils.SubtitleCodec sCodec = ResolveSubtitleCodec(GetCurrentCodecS(), vCodec);
                bool anyVideoStreams = TrackList.CheckedItems.Any(x => x.Stream.Type == Data.Streams.Stream.StreamType.Video);
                bool anyAudioStreams = TrackList.CheckedItems.Any(x => x.Stream.Type == Data.Streams.Stream.StreamType.Audio);
                string problem = GetContainerProblem(GetCurrentCodecV(), aCodec, sCodec);

                if (problem.IsNotEmpty())
                {
                    RunTask.Cancel(problem);
                    return;
                }

                // Before anything is built, because a crop that does not fit the frame in front of it
                // becomes "crop=-80:1080:1000:0" and ffmpeg refuses that with an error naming neither
                // the setting nor the file. The way in is rarely a typo: the four edges outlive the file
                // they were set for, and a batch does not clear them between files, so the crop measured
                // on a 1080p source is still 140 lines off a 480p one.
                string cropProblem = QuickConvertUi.GetCropProblem();

                if (cropProblem.IsNotEmpty())
                {
                    RunTask.Cancel($"'{TrackList.current.File.Name.Trunc(40)}' cannot be cropped as configured - " +
                        $"{cropProblem}.\n\nChange the crop, or switch it off for this file.");
                    return;
                }

                // The same shape of question, one step further along the chain: black bars are added
                // around whatever the resize leaves, and there are two ways that cannot be worked out
                // here. Refused rather than quietly left off, a setting picked and then dropped being
                // worse than one that stops the run and says why.
                string borderProblem = QuickConvertUi.GetBorderProblem();

                if (borderProblem.IsNotEmpty())
                {
                    RunTask.Cancel($"'{TrackList.current.File.Name.Trunc(40)}' cannot have borders added as configured - " +
                        $"{borderProblem}.\n\nChange the resize, or switch the borders off.");
                    return;
                }

                // And the frame those two settings come to between them, which FFmpeg refuses outright
                // past a certain size - the AV1AN tab has asked this since its resize dialog was written.
                string frameProblem = QuickConvertUi.GetFrameSizeProblem();

                if (frameProblem.IsNotEmpty())
                {
                    RunTask.Cancel(frameProblem);
                    return;
                }

                // And the one that cannot be worked around in the filter chain at all - see
                // GetBurnInProblem, which is where the reason is.
                string burnInProblem = QuickConvertUi.GetBurnInProblem();

                if (burnInProblem.IsNotEmpty())
                {
                    RunTask.Cancel($"'{TrackList.current.File.Name.Trunc(40)}' cannot have subtitles burnt in - {burnInProblem}");
                    return;
                }

                // The same question the AV1AN tab and the Cut utility have always asked, and for the
                // same reason: a trim outlives the file it was set for, so a batch runs one section
                // against every file in it. Where the section starts past the end of a shorter one,
                // ffmpeg seeks past everything there is and writes an empty file without complaining.
                if (QuickConvertUi.CurrentTrim != null && !QuickConvertUi.CurrentTrim.IsUnset)
                {
                    string trimProblem = UtilCut.ResolveSection(QuickConvertUi.CurrentTrim, TrackList.current.File, out long _, out long _);

                    if (trimProblem.IsNotEmpty())
                    {
                        RunTask.Cancel($"{trimProblem}\n\nChange the trim, or clear it for this file.");
                        return;
                    }
                }

                // Not read straight off the mode box: the fixed formats have no rate control and that box
                // is disabled over whatever was last picked in it, so a Target Bitrate left over from
                // H.264 had GetVideoArgsFromUi send a bitrate where GIF and JPEG read a "q". Both fell
                // back to their own default, so the palette size and the JPEG quality did nothing at all.
                bool crf = GetEffectiveQualityMode(vCodec) == QualityMode.Crf;
                bool twoPass = anyVideoStreams && vCodec.SupportsTwoPass && (vCodec.ForceTwoPass || !crf);
                Dictionary<string, string> videoArgs = vCodec.DoesNotEncode ? new Dictionary<string, string>() : GetVideoArgsFromUi(!crf);
                // What the scale boxes come out to, where that can be said - the tile count below is a
                // property of the frame being encoded rather than of the file it came from. Absent, the
                // encoders fall back on the source's own size, which is where they always looked.
                Size encodedFrame = vCodec.DoesNotEncode ? Size.Empty : GetEncodedFrameSize();

                if (!encodedFrame.IsEmpty)
                    videoArgs[CodecUtils.FrameSizeKey] = $"{encodedFrame.Width}x{encodedFrame.Height}";

                // Decided once, before anything reads it: the filter arguments and the stream maps are
                // each built more than once per run, and in Automatic mode answering the question can
                // mean decoding a few hundred frames of the source.
                await PrepareDeinterlacing(anyVideoStreams && !vCodec.DoesNotEncode, twoPass);
                string pipeIn = GetPipeInputArgs();
                vsLogPath = QuickConvertUi.CurrentDeinterlace.UsesPipe ? GetVsLogPath() : "";
                vsRuns = vsLogPath.IsEmpty() ? 0 : (twoPass ? 2 : 1);

                string miscIn = GetMiscInputArgs();
                // The input-side arguments go in front of every '-i' rather than once at the head of the
                // command: ffmpeg reads a "-ss" there as belonging to the input that follows it, so in
                // Muxing Mode a keyframe trim seeked the first file and left every other one starting
                // from the top - see TrackList.GetInputFilesString.
                string inFiles = TrackList.GetInputFilesString(miscIn);
                // Has to happen here rather than when the file was loaded: the check reads the output
                // path through UiData.GetOutPath, which only resolves once RunningTask is set, so every
                // earlier call was comparing against an empty string and finding nothing. Without it
                // ffmpeg is handed the colliding name with -y and the existing file is overwritten.
                ValidatePath();
                outPath = GetFfmpegOutPath(vCodec);

                // The encoder's arguments and the filter chain, built before the stream maps because the
                // maps have to know whether there is a filtergraph for the first video track to be read
                // out of - and that is not a question the encoder alone answers, GIF contributing its
                // whole palette graph through CodecArgs.ForcedFilters. See TrackList.GetMapArgs.
                CodecArgs codecArgs = vCodec.GetArgs(videoArgs, TrackList.current.File, twoPass ? Pass.OneOfTwo : Pass.OneOfOne);
                string v = anyVideoStreams ? codecArgs.Arguments : "";
                string vf = anyVideoStreams && !vCodec.DoesNotEncode ? await GetVideoFilterArgs(vCodec, codecArgs) : "";

                // Quiet: the second pass builds the same chain from the same controls and has nothing new
                // to say about it, so the lines that come with it - the resample, the de-squeeze - belong
                // in the log once rather than twice.
                CodecArgs codecArgsPass2 = twoPass ? vCodec.GetArgs(videoArgs, TrackList.current.File, Pass.TwoOfTwo) : null;
                string v2 = twoPass ? codecArgsPass2.Arguments : "";
                string vf2 = twoPass ? await GetVideoFilterArgs(vCodec, codecArgsPass2, quiet: true) : "";

                // Named rather than left to ffmpeg's own default, which is "ffmpeg2pass-N.log" in the
                // working directory - and that is wherever the app happens to have been launched from.
                // An install the user cannot write to failed the first pass outright, and every two-pass
                // encode that did work left a log and an x264 mbtree file sitting beside the exe. This
                // run's scratch folder is where the rest of its temporary files already go.
                string passLog = twoPass ? $"-passlogfile {Shell.WrapArg(Path.Combine(Paths.GetSessionDataPath(), "ffmpeg2pass"))}" : "";

                string map = TrackList.GetMapArgs(vCodec.IsFixedFormat, vCodec.DoesNotEncode, hasFilterChain: vf.IsNotEmpty());
                string a = anyAudioStreams ? CodecUtils.GetCodec(aCodec).GetArgs(GetAudioArgsFromUi(), TrackList.current.File).Arguments : "";
                string s = CodecUtils.GetCodec(sCodec).GetArgs().Arguments;
                string meta = GetMetadataArgs();
                string miscOut = GetMiscOutputArgs();
                string custIn = (Program.MainWin.CustomArgsInBox.Text ?? "").Trim();
                string custOut = (Program.MainWin.CustomArgsOutBox.Text ?? "").Trim();
                // Nothing for a format that writes no container. The container box is hidden for GIF,
                // JPEG and PNG and keeps whatever was last selected in it, so its muxer's private
                // options - "-movflags +faststart", Matroska's "-default_mode" - were being handed to a
                // muxer that has never heard of them.
                string muxing = vCodec.IsFixedFormat ? "" : GetMuxingArgs();

                if (twoPass)
                {
                    // Each pass needs its own VapourSynth process - a pipe feeds one reader - and each
                    // appends to the same log, which is why the check afterwards expects two finished
                    // runs rather than one.
                    string secondPipe = vsLogPath.IsEmpty() ? "" : Qtgmc.BuildVspipeCommand(GetVsScriptPath(), vsLogPath, append: true);

                    args = $"{custIn} {inFiles} {pipeIn} {map} {v} {passLog} {vf} {miscOut} {custOut} -an -sn -dn -f null - && {secondPipe}ffmpeg -y -loglevel warning -stats " +
                           $"{custIn} {inFiles} {pipeIn} {map} {v2} {passLog} {vf2} {a} {s} {meta} {miscOut} {custOut} {muxing} {Shell.WrapArg(outPath)}";
                }
                else
                {
                    args = $"{custIn} {inFiles} {pipeIn} {map} {v} {vf} {a} {s} {meta} {miscOut} {custOut} {muxing} {Shell.WrapArg(outPath)}";
                }
            }
            catch (Exception e)
            {
                RunTask.Cancel($"Error creating FFmpeg command: {e.Message}\n{e.StackTrace}");
                return;
            }

            if (Hotkeys.ShiftHeld) // Allow reviewing and editing command if shift is held
            {
                string edited = await EditCommandWindow.Show("ffmpeg", args);

                if (string.IsNullOrWhiteSpace(edited))
                {
                    RunTask.Cancel("The command was cleared in the edit window, so nothing was run.", noMsgBox: true);
                    Program.MainWin.SetWorking(false);
                    return;
                }

                args = edited;
            }

            if (RunTask.canceled) return;

            Logger.Log($"Running:\nffmpeg {args}", true, false, "ffmpeg");

            AvProcess.FfmpegSettings settings = new AvProcess.FfmpegSettings()
            {
                Args = args,
                LoggingMode = AvProcess.LogMode.OnlyLastLine,
                ProgressBar = true,
                // The exit code decides, and RunFfmpeg reports it - except where VapourSynth is
                // feeding it, since ffmpeg sees a script that died as an input that ended and its
                // words for that describe the symptom. Those runs are judged below instead.
                ReportFailure = vsRuns < 1,
                PipeFrom = vsLogPath.IsEmpty() ? "" : Qtgmc.BuildVspipeCommand(GetVsScriptPath(), vsLogPath),
                ExtraPathDirs = vsLogPath.IsEmpty() ? new string[0] : Qtgmc.GetPathDirs(),
            };

            await AvProcess.RunFfmpeg(settings);

            if (vsRuns > 0 && !RunTask.canceled)
            {
                // VapourSynth's complaint outranks ffmpeg's, and is often the only one there is: a
                // script that stops two thirds of the way through leaves ffmpeg finishing normally
                // and exiting 0 over a file that is missing the rest of the video.
                string vsProblem = Qtgmc.ReadRunProblem(vsLogPath, vsRuns);

                if (vsProblem.IsNotEmpty())
                {
                    RunTask.Fail($"The deinterlaced video was cut short.\n\n{vsProblem}");
                    return;
                }

                if (settings.Problem.IsNotEmpty())
                {
                    RunTask.Fail(settings.Problem);
                    return;
                }
            }

            if (!RunTask.canceled && !RunTask.failed && outPath.IsNotEmpty())
            {
                // Belt and braces behind the exit code: a run that somehow ends cleanly having
                // written nothing is still not a finished encode, and a batch used to count it
                // among its finished tasks.
                if (!RunTask.OutputExists(outPath))
                {
                    RunTask.Fail($"FFmpeg reported no error, but '{Path.GetFileName(outPath)}' was not written.");
                    return;
                }

                // The same inputs GetInputFilesString hands to ffmpeg: the loaded file in batch
                // mode, everything in the file list when muxing.
                IEnumerable<string> inPaths = RunTask.currentFileListMode == RunTask.FileListMode.Batch
                    ? new[] { TrackList.current.File.SourcePath }
                    : FileList.Items.Select(x => x.File.SourcePath);

                RunTask.ReportOutput(inPaths, outPath);
            }
        }

        #region Deinterlacing

        /// <summary>
        /// Settles what will deinterlace this run's video, and - when that turns out to be QTGMC -
        /// writes the VapourSynth script whose frames ffmpeg will read. Called once, before anything
        /// reads <see cref="QuickConvertUi.CurrentDeinterlace"/>, because both the filter chain and the
        /// stream maps are built several times over one run and the question behind Automatic is not a
        /// cheap one to ask twice.
        /// </summary>
        private static async Task PrepareDeinterlacing(bool encodingVideo, bool twoPass)
        {
            QuickConvertUi.CurrentDeinterlace = new DeinterlacePlan();
            QuickConvertUi.DeinterlacePipeInput = -1;

            if (!encodingVideo)
                return;

            MediaFile file = GetDeinterlaceSourceFile();

            if (file == null)
                return;

            // The verdict first, for the reason the AV1AN tab settles it here too: the request below is
            // read out of the mode box, and the scan that points that box at this file runs in the
            // background, so a batch would otherwise start the encode before it landed.
            await DeinterlaceUi.EnsureScanVerdictAsync(file);
            DeinterlaceRequest req = DeinterlaceUi.GetQuickConvertRequest();

            // A trim and QTGMC cannot both apply to the same encode. The trim is ffmpeg's - an input
            // seek, an output duration, or a frame-number filter - and none of the three reaches the
            // VapourSynth script that reads the source, so the video would arrive whole while the
            // audio arrived cut. Cutting first is the way to have both, and the Cut utility does
            // exactly that without re-encoding anything.
            if (QuickConvertUi.CurrentTrim != null && !QuickConvertUi.CurrentTrim.IsUnset)
                req.QtgmcUnavailableHere = "the trim is applied by ffmpeg and cannot reach the VapourSynth script " +
                    "that reads the source - cut the section out first with the Cut utility to run QTGMC on it";

            DeinterlacePlan plan = await Deinterlace.ResolveAsync(file, req);
            QuickConvertUi.CurrentDeinterlace = plan;

            if (!plan.Runs)
                return;

            Logger.Log($"Deinterlacing '{file.Name.Trunc(40)}' with {plan.Describe()}.");

            if (!plan.UsesPipe)
                return;

            // The pipe is added as the last '-i', so every input already on the command line keeps the
            // number the stream maps were built against.
            QuickConvertUi.DeinterlacePipeInput = RunTask.currentFileListMode == RunTask.FileListMode.Batch ? 1 : FileList.Items.Count;
            Qtgmc.WriteScript(plan, file.ImportPath, GetVsScriptPath());
            IoUtils.TryDeleteIfExists(GetVsLogPath()); // The check afterwards counts finished runs in it

            // What VapourSynth will hand over, which is what the bar has to be measured against - the
            // file's own duration is a claim, and the sources QTGMC runs on are the ones that get it
            // wrong. A trim would be the other candidate for the target, and cannot be set here: it
            // rules QTGMC out a few lines above.
            await Qtgmc.SetProgressTargetAsync(GetVsScriptPath(), file);

            if (twoPass)
                Logger.Log("Note: this is a two-pass encode, so QTGMC runs once per pass - it is the slowest part of both.");
        }

        /// <summary>
        /// The file QTGMC should read. The first ticked video track's, not necessarily the loaded one:
        /// in muxing mode the video can come from any file in the list, and reading the wrong one would
        /// deinterlace a different video than the encode is about.
        /// </summary>
        /// <summary> Delegated so the encode and the mode gate cannot pick different files - see
        /// <see cref="DeinterlaceUi.GetQuickConvertSourceFile"/>. </summary>
        private static MediaFile GetDeinterlaceSourceFile()
        {
            return DeinterlaceUi.GetQuickConvertSourceFile();
        }

        /// <summary> The extra '-i' that reads VSPipe's output, or "" when nothing is being piped.
        /// The queue size is raised because the producer is a QTGMC graph: it delivers frames in
        /// bursts, and the default queue is shallow enough for ffmpeg to complain about it. </summary>
        private static string GetPipeInputArgs()
        {
            return QuickConvertUi.DeinterlacePipeInput < 0 ? "" : "-f yuv4mpegpipe -thread_queue_size 1024 -i -";
        }

        private static string GetVsScriptPath()
        {
            return Path.Combine(Paths.GetSessionDataPath(), "qtgmc.vpy");
        }

        private static string GetVsLogPath()
        {
            return Path.Combine(Paths.GetSessionDataPath(), "qtgmc.log");
        }

        #endregion

        /// <summary>
        /// The subtitle codec to actually encode with. MP4 and MOV store no text subtitle format other
        /// than tx3g, and WebM none other than WebVTT, so copying SRT into either cannot work -
        /// converting is the only way the tracks survive, and carrying them over is what choosing to
        /// copy them asked for.
        /// </summary>
        private static CodecUtils.SubtitleCodec ResolveSubtitleCodec(CodecUtils.SubtitleCodec sCodec, IEncoder vCodec)
        {
            if (sCodec != CodecUtils.SubtitleCodec.CopySubs || vCodec.IsFixedFormat)
                return sCodec;

            Containers.Container container = GetCurrentContainer();
            List<Data.Streams.SubtitleStream> subStreams = GetCheckedSubtitleStreams();

            // Nothing to carry over, or the tracks already go in as they are and need no round trip
            // through the encoder
            if (subStreams.Count < 1 || subStreams.All(x => Containers.CanCopySubtitleCodec(container, x.Codec)))
                return sCodec;

            // Only substitute where the container leaves exactly one choice. MKV takes both SRT and
            // WebVTT, so picking one would be guessing at a preference; M4A and OGG take neither, and
            // there is nothing to substitute. Both are left to GetSubtitleProblem to explain.
            CodecUtils.SubtitleCodec[] supported = Containers.GetSupportedSubtitleCodecs(container);

            if (supported.Length != 1)
                return sCodec;

            // Image-based tracks cannot be converted to a text format either. They are left to
            // GetSubtitleProblem, which says so rather than dropping them quietly.
            Logger.Log($"{container.ToString().ToUpper()} stores no text subtitle format other than " +
                $"{GetShortName(CodecUtils.GetCodec(supported[0]))}, so the subtitles are being converted " +
                $"to it rather than copied.");

            return supported[0];
        }

        /// <summary>
        /// Describes why the chosen codecs cannot go into the chosen container, or "" if they can.
        /// ffmpeg reports these only once it is already muxing, which on a long encode means finding out
        /// at the very end, so the ones visible from the selection are caught before it starts.
        /// </summary>
        private static string GetContainerProblem(CodecUtils.VideoCodec vCodec, CodecUtils.AudioCodec aCodec, CodecUtils.SubtitleCodec sCodec)
        {
            IEncoder vEnc = CodecUtils.GetCodec(vCodec);

            // Fixed formats (GIF/PNG/JPG) write a format of their own and are mapped video-only, so the
            // container dropdown is hidden and no other stream reaches a muxer.
            if (vEnc.IsFixedFormat)
                return "";

            Containers.Container container = GetCurrentContainer();
            string problem = GetVideoProblem(vCodec, container);

            if (problem.IsNotEmpty())
                return problem;

            problem = GetAudioProblem(aCodec, container);

            return problem.IsNotEmpty() ? problem : GetSubtitleProblem(sCodec, vEnc);
        }

        private static string GetVideoProblem(CodecUtils.VideoCodec vCodec, Containers.Container container)
        {
            List<Data.Streams.Stream> streams = GetCheckedStreams(Data.Streams.Stream.StreamType.Video);

            if (vCodec == CodecUtils.VideoCodec.StripVideo || streams.Count < 1)
                return "";

            string name = container.ToString().ToUpper();

            if (vCodec == CodecUtils.VideoCodec.CopyVideo)
            {
                var unsupported = streams.Where(x => !Containers.CanCopyVideoCodec(container, x.Codec)).ToList();

                if (unsupported.Count < 1)
                    return "";

                return $"{name} cannot store {CodecNames(unsupported)} video, so it cannot be copied into it.\n\n" +
                    $"{Accepts(name, Containers.GetSupportedVideoCodecs(container).Select(x => CodecUtils.GetCodec(x)), "video")}\n\n" +
                    $"Change the container, or re-encode the video instead of copying it.";
            }

            if (Containers.GetSupportedVideoCodecs(container).Contains(vCodec))
                return "";

            return $"{name} does not support {GetShortName(CodecUtils.GetCodec(vCodec))} video.\n\n" +
                $"{Accepts(name, Containers.GetSupportedVideoCodecs(container).Select(x => CodecUtils.GetCodec(x)), "video")}";
        }

        private static string GetAudioProblem(CodecUtils.AudioCodec aCodec, Containers.Container container)
        {
            List<Data.Streams.Stream> streams = GetCheckedStreams(Data.Streams.Stream.StreamType.Audio);

            if (aCodec == CodecUtils.AudioCodec.StripAudio || streams.Count < 1)
                return "";

            string name = container.ToString().ToUpper();

            if (aCodec == CodecUtils.AudioCodec.CopyAudio)
            {
                var unsupported = streams.Where(x => !Containers.CanCopyAudioCodec(container, x.Codec)).ToList();

                if (unsupported.Count < 1)
                    return "";

                return $"{name} cannot store {CodecNames(unsupported)} audio, so it cannot be copied into it.\n\n" +
                    $"{Accepts(name, Containers.GetSupportedAudioCodecs(container).Select(x => CodecUtils.GetCodec(x)), "audio")}\n\n" +
                    $"Change the container, or re-encode the audio instead of copying it.";
            }

            if (Containers.GetSupportedAudioCodecs(container).Contains(aCodec))
                return "";

            return $"{name} does not support {GetShortName(CodecUtils.GetCodec(aCodec))} audio.\n\n" +
                $"{Accepts(name, Containers.GetSupportedAudioCodecs(container).Select(x => CodecUtils.GetCodec(x)), "audio")}";
        }

        /// <summary> "MP4 accepts H.264, H.265." - or that it takes none of that kind at all. </summary>
        private static string Accepts(string containerName, IEnumerable<IEncoder> encoders, string kind)
        {
            List<string> names = encoders.Select(GetShortName).ToList();

            if (names.Count < 1)
                return $"{containerName} holds no {kind} at all.";

            return $"{containerName} accepts {string.Join(", ", names)}.";
        }

        private static List<Data.Streams.Stream> GetCheckedStreams(Data.Streams.Stream.StreamType type)
        {
            return TrackList.CheckedItems.Where(x => x.Stream.Type == type).Select(x => x.Stream).ToList();
        }

        /// <summary> The distinct codecs of a set of streams, for naming them in a message. </summary>
        private static string CodecNames(IEnumerable<Data.Streams.Stream> streams)
        {
            return string.Join(", ", streams.Select(x => GetCodecName(x.Codec)).Distinct());
        }

        /// <summary>
        /// Describes why the selected subtitle codec cannot produce the requested output, or "" if it can.
        /// ffmpeg only reports these mismatches once it is already muxing, which on a long encode means
        /// finding out at the very end, so the ones that can be seen from the selection are caught here.
        /// </summary>
        private static string GetSubtitleProblem(CodecUtils.SubtitleCodec sCodec, IEncoder vCodec)
        {
            List<Data.Streams.SubtitleStream> subStreams = GetCheckedSubtitleStreams();

            // Fixed formats (GIF/PNG/JPG) map video only, so no subtitle reaches the muxer either way
            if (sCodec == CodecUtils.SubtitleCodec.StripSubs || subStreams.Count < 1 || vCodec.IsFixedFormat)
                return "";

            Containers.Container container = GetCurrentContainer();
            string containerName = container.ToString().ToUpper();

            if (sCodec == CodecUtils.SubtitleCodec.CopySubs)
            {
                var unsupported = subStreams.Where(x => !Containers.CanCopySubtitleCodec(container, x.Codec)).ToList();

                if (unsupported.Count < 1)
                    return "";

                string names = string.Join(", ", unsupported.Select(x => GetCodecName(x.Codec)).Distinct());
                return $"{containerName} cannot store {names} subtitles, so they cannot be copied into it.\n\n" +
                    $"{GetAcceptedSubCodecs(container)}\n\n" +
                    $"Change the container, re-encode the subtitles, or set the subtitle codec to " +
                    $"\"{CodecUtils.GetCodec(CodecUtils.SubtitleCodec.StripSubs).FriendlyName}\".";
            }

            IEncoder sEnc = CodecUtils.GetCodec(sCodec);

            if (!Containers.GetSupportedSubtitleCodecs(container).Contains(sCodec))
                return $"{containerName} does not support {GetShortName(sEnc)} subtitles.\n\n{GetAcceptedSubCodecs(container)}";

            var bitmapStreams = subStreams.Where(x => x.Bitmap).ToList();

            if (bitmapStreams.Count > 0)
            {
                string names = string.Join(", ", bitmapStreams.Select(x => GetCodecName(x.Codec)).Distinct());
                return $"{names} subtitles are image-based and cannot be encoded to {GetShortName(sEnc)}, which is text-based.\n\n" +
                    $"Copy them without re-encoding into a container that stores them (MKV), " +
                    $"or convert them to text first with the \"OCR Bitmap Subtitles\" utility.";
            }

            return "";
        }

        /// <summary> What a container takes when re-encoding, phrased for an error message. </summary>
        private static string GetAcceptedSubCodecs(Containers.Container c)
        {
            CodecUtils.SubtitleCodec[] supported = Containers.GetSupportedSubtitleCodecs(c);
            string name = c.ToString().ToUpper();

            if (supported.Length < 1)
                return $"{name} cannot hold subtitles in any format.";

            return $"{name} accepts {string.Join(" and ", supported.Select(x => GetShortName(CodecUtils.GetCodec(x))))}.";
        }

        /// <summary> The subtitle tracks that are ticked in the track list, in output order. </summary>
        private static List<Data.Streams.SubtitleStream> GetCheckedSubtitleStreams()
        {
            return TrackList.CheckedItems
                .Where(x => x.Stream.Type == Data.Streams.Stream.StreamType.Subtitle)
                .Select(x => (Data.Streams.SubtitleStream)x.Stream).ToList();
        }

        /// <summary> A track's codec for display, with a fallback for streams ffprobe gave no codec name. </summary>
        private static string GetCodecName(string codec)
        {
            string name = Aliases.GetNicerCodecName(codec ?? "").Trim();
            return name.IsEmpty() ? "unrecognized" : name;
        }

        /// <summary> An encoder's friendly name without the trailing " - For MKV" style container hint. </summary>
        private static string GetShortName(IEncoder enc)
        {
            return enc.FriendlyName.Split(" - ")[0].Trim();
        }

        /// <summary> What ffmpeg is told to write, which for a sequence is a numbering pattern inside a
        /// folder that has to exist first. Where that goes is <see cref="QuickConvertUi.GetOutputPath"/>,
        /// which the collision check reads too so the two cannot disagree. </summary>
        private static string GetFfmpegOutPath(IEncoder c)
        {
            string path = GetOutputPath(c);

            if (!c.IsSequence)
                return path;

            Directory.CreateDirectory(path);
            return Path.Combine(path, $"%8d.{GetFixedFormatExtension()}");
        }
    }
}
