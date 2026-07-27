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
                CodecUtils.SubtitleCodec sCodec = GetCurrentCodecS();
                bool anyVideoStreams = TrackList.CheckedItems.Any(x => x.Stream.Type == Data.Streams.Stream.StreamType.Video);
                bool anyAudioStreams = TrackList.CheckedItems.Any(x => x.Stream.Type == Data.Streams.Stream.StreamType.Audio);
                string subProblem = GetSubtitleProblem(sCodec, vCodec);

                if (subProblem.IsNotEmpty())
                {
                    RunTask.Cancel(subProblem);
                    return;
                }

                bool crf = (QualityMode)Math.Max(0, Program.MainWin.EncQualModeBox.SelectedIndex) == QualityMode.Crf;
                bool twoPass = anyVideoStreams && vCodec.SupportsTwoPass && (vCodec.ForceTwoPass || !crf);
                Dictionary<string, string> videoArgs = vCodec.DoesNotEncode ? new Dictionary<string, string>() : GetVideoArgsFromUi(!crf);

                string inFiles = TrackList.GetInputFilesString();
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
        /// Describes why the selected subtitle codec cannot produce the requested output, or "" if it can.
        /// ffmpeg only reports these mismatches once it is already muxing, which on a long encode means
        /// finding out at the very end, so the ones that can be seen from the selection are caught here.
        /// </summary>
        private static string GetSubtitleProblem(CodecUtils.SubtitleCodec sCodec, IEncoder vCodec)
        {
            List<Data.Streams.SubtitleStream> subStreams = TrackList.CheckedItems
                .Where(x => x.Stream.Type == Data.Streams.Stream.StreamType.Subtitle)
                .Select(x => (Data.Streams.SubtitleStream)x.Stream).ToList();

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

                string names = string.Join(", ", unsupported.Select(x => Aliases.GetNicerCodecName(x.Codec)).Distinct());
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
                string names = string.Join(", ", bitmapStreams.Select(x => Aliases.GetNicerCodecName(x.Codec)).Distinct());
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
