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
