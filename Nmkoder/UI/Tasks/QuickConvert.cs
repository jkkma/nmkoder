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

                bool crf = (QualityMode)Math.Max(0, Program.MainWin.EncQualModeBox.SelectedIndex) == QualityMode.Crf;
                bool twoPass = anyVideoStreams && vCodec.SupportsTwoPass && (vCodec.ForceTwoPass || !crf);
                Dictionary<string, string> videoArgs = vCodec.DoesNotEncode ? new Dictionary<string, string>() : GetVideoArgsFromUi(!crf);

                string inFiles = TrackList.GetInputFilesString();
                // Has to happen here rather than when the file was loaded: the check reads the output
                // path through UiData.GetOutPath, which only resolves once RunningTask is set, so every
                // earlier call was comparing against an empty string and finding nothing. Without it
                // ffmpeg is handed the colliding name with -y and the existing file is overwritten.
                ValidatePath();
                string outPath = GetFfmpegOutPath(vCodec);
                string map = await TrackList.GetMapArgs(vCodec, vCodec.IsFixedFormat, vCodec.DoesNotEncode);
                string a = anyAudioStreams ? CodecUtils.GetCodec(aCodec).GetArgs(GetAudioArgsFromUi(), TrackList.current.File).Arguments : "";
                string s = CodecUtils.GetCodec(sCodec).GetArgs().Arguments;
                string meta = GetMetadataArgs();
                string miscIn = GetMiscInputArgs();
                string miscOut = GetMiscOutputArgs();
                string custIn = (Program.MainWin.CustomArgsInBox.Text ?? "").Trim();
                string custOut = (Program.MainWin.CustomArgsOutBox.Text ?? "").Trim();
                string muxing = GetMuxingArgs();

                if (twoPass && anyVideoStreams)
                {
                    CodecArgs codecArgsPass1 = vCodec.GetArgs(videoArgs, TrackList.current.File, Pass.OneOfTwo);
                    string v1 = codecArgsPass1.Arguments;
                    string vf1 = vCodec.DoesNotEncode ? "" : await GetVideoFilterArgs(vCodec, codecArgsPass1);
                    CodecArgs codecArgsPass2 = vCodec.GetArgs(videoArgs, TrackList.current.File, Pass.TwoOfTwo);
                    string v2 = codecArgsPass2.Arguments;
                    string vf2 = vCodec.DoesNotEncode ? "" : await GetVideoFilterArgs(vCodec, codecArgsPass2);

                    args = $"{miscIn} {custIn} {inFiles} {map} {v1} {vf1} {miscOut} {custOut} -an -sn -dn -f null - && ffmpeg -y -loglevel warning -stats " +
                           $"{miscIn} {custIn} {inFiles} {map} {v2} {vf2} {a} {s} {meta} {miscOut} {custOut} {muxing} {outPath.Wrap()}";
                }
                else
                {
                    CodecArgs codecArgs = vCodec.GetArgs(videoArgs, TrackList.current.File, Pass.OneOfOne);
                    string v = anyVideoStreams ? codecArgs.Arguments : "";
                    string vf = anyVideoStreams && !vCodec.DoesNotEncode ? await GetVideoFilterArgs(vCodec, codecArgs) : "";

                    args = $"{miscIn} {custIn} {inFiles} {map} {v} {vf} {a} {s} {meta} {miscOut} {custOut} {muxing} {outPath.Wrap()}";
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
                    Program.MainWin.SetWorking(false);
                    return;
                }

                args = edited;
            }

            if (RunTask.canceled) return;

            Logger.Log($"Running:\nffmpeg {args}", true, false, "ffmpeg");

            AvProcess.FfmpegSettings settings = new AvProcess.FfmpegSettings() { Args = args, LoggingMode = AvProcess.LogMode.OnlyLastLine, ProgressBar = true };
            await AvProcess.RunFfmpeg(settings);
        }

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

        private static string GetFfmpegOutPath(IEncoder c)
        {
            string uiPath = UiData.GetOutPath();

            if (!c.IsSequence)
                return uiPath;

            Directory.CreateDirectory(uiPath);
            string ext = Program.MainWin.EncVidCodecsBox.GetText().Split(' ')[0].ToLower();
            return Path.Combine(uiPath, $"%8d.{ext}");
        }
    }
}
