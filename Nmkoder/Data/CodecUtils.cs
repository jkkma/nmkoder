using Nmkoder.Data.Codecs;
using Nmkoder.Data.Codecs.Audio;
using Nmkoder.Data.Codecs.Subs;
using Nmkoder.Data.Codecs.Video;
using Nmkoder.Data.Streams;
using Nmkoder.Data.Ui;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.UI;
using Nmkoder.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nmkoder.Data
{
    class CodecUtils
    {
        //public enum CodecType { Video, AnimImage, Image, Audio }

        // Appended rather than ordered by codec: the dropdown is built straight from this enum and
        // the selected index is what gets saved, so inserting anywhere else repoints saved settings.
        public enum Av1anCodec { AomAv1, SvtAv1, Vpx, X265, X264 };
        public enum VideoCodec { CopyVideo, StripVideo, Libx264, Libx265, H264Nvenc, H265Nvenc, LibVpx, LibSvtAv1, LibAomAv1, Gif, Png, Jpg };
        public enum AudioCodec { CopyAudio, StripAudio, Aac, Opus, Vorbis, Eac3, Mp3, Flac };
        public enum SubtitleCodec { CopySubs, StripSubs, MovText, Srt, WebVtt };

        public static IEncoder GetCodec(VideoCodec c)
        {
            if (c == VideoCodec.StripVideo) return new StripVideo();
            if (c == VideoCodec.CopyVideo) return new CopyVideo();
            if (c == VideoCodec.Libx264) return new Libx264();
            if (c == VideoCodec.H264Nvenc) return new H264Nvenc();
            if (c == VideoCodec.Libx265) return new Libx265();
            if (c == VideoCodec.H265Nvenc) return new H265Nvenc();
            if (c == VideoCodec.LibVpx) return new LibVpx();
            if (c == VideoCodec.LibSvtAv1) return new LibSvtAv1();
            if (c == VideoCodec.LibAomAv1) return new LibAomAv1();
            if (c == VideoCodec.Gif) return new Gif();
            if (c == VideoCodec.Png) return new Png();
            if (c == VideoCodec.Jpg) return new Jpg();
            return null;
        }

        public static IEncoder GetCodec(Av1anCodec c)
        {
            if (c == Av1anCodec.AomAv1) return new AomAv1();
            if (c == Av1anCodec.SvtAv1) return new SvtAv1();
            if (c == Av1anCodec.Vpx) return new Vpx();
            if (c == Av1anCodec.X265) return new X265();
            if (c == Av1anCodec.X264) return new X264();
            return null;
        }

        public static IEncoder GetCodec(AudioCodec c)
        {
            if (c == AudioCodec.StripAudio) return new StripAudio();
            if (c == AudioCodec.CopyAudio) return new CopyAudio();
            if (c == AudioCodec.Aac) return new Aac();
            if (c == AudioCodec.Opus) return new Opus();
            if (c == AudioCodec.Vorbis) return new Vorbis();
            if (c == AudioCodec.Eac3) return new Eac3();
            if (c == AudioCodec.Mp3) return new Mp3();
            if (c == AudioCodec.Flac) return new Flac();
            return null;
        }

        public static IEncoder GetCodec(SubtitleCodec c)
        {
            if (c == SubtitleCodec.StripSubs) return new StripSubs();
            if (c == SubtitleCodec.CopySubs) return new CopySubs();
            if (c == SubtitleCodec.MovText) return new MovText();
            if (c == SubtitleCodec.Srt) return new Srt();
            if (c == SubtitleCodec.WebVtt) return new WebVtt();
            return null;
        }

        public static string GetKeyIntArg(MediaFile mediaFile, int intervalSeconds, string arg = "-g ", int max = 480)
        {
            if (mediaFile == null || mediaFile.VideoStreams.Count < 1)
                return "";

            int keyInt = ((float)(mediaFile?.VideoStreams.FirstOrDefault().Rate.GetFloat() * intervalSeconds)).RoundToInt();
            return $"{arg}{keyInt.Clamp(12, max)}";
        }

        /// <summary>
        /// The audio settings as ffmpeg arguments, one set per ticked track.
        /// <para/>
        /// Callers with no file in hand get a single unindexed set that applies to every audio stream
        /// instead. That is the AV1AN tab, which has no per-track settings and whose arguments av1an
        /// applies to everything its own '-map 0' picks up: numbering them by ticked track there put
        /// each track's settings on whichever stream happened to share its position, and dropped a
        /// track from the numbering without dropping it from the output.
        /// </summary>
        public static string GetAudioArgsForEachStream(MediaFile mf, int baseBitrate, int overrideChannels, List<string> extraArgs = null)
        {
            if (mf == null)
                return GetAudioArgsForAllStreams(baseBitrate, overrideChannels, extraArgs);

            List<string> args = new List<string>();

            List<AudioStream> allAudStreams = TrackList.Items.Where(x => x.Stream.Type == Stream.StreamType.Audio).Select(x => (AudioStream)x.Stream).ToList();
            List<AudioStream> checkedAudStreams = TrackList.CheckedItems.Where(x => x.Stream.Type == Stream.StreamType.Audio).Select(x => (AudioStream)x.Stream).ToList();

            List<AudioConfigurationEntry> audioConf = TrackList.currentAudioConfig != null ? TrackList.currentAudioConfig.GetConfig(mf) : null;
            bool perTrack = Program.MainWin.EncAudConfModeBox.SelectedIndex == 1;

            foreach (AudioStream s in checkedAudStreams)
            {
                int indexTotal = allAudStreams.IndexOf(s);
                int indexChecked = checkedAudStreams.IndexOf(s);
                int ac = overrideChannels > 0 ? overrideChannels : s.Channels;

                if (perTrack && audioConf != null && indexTotal >= 0 && indexTotal < audioConf.Count)
                    ac = audioConf[indexTotal].ChannelCount;

                if (baseBitrate > 0)
                {
                        int kbps = (baseBitrate * MiscUtils.GetAudioBitrateMultiplier(ac)).RoundToInt();

                    if (perTrack && audioConf != null && indexTotal >= 0 && indexTotal < audioConf.Count)
                        kbps = audioConf[indexTotal].BitrateKbps;

                    args.Add($"-b:a:{indexChecked} {kbps}k");
                }

                args.Add($"-ac:a:{indexChecked} {ac}");

                if (extraArgs != null)
                {
                    foreach (var arg in extraArgs)
                    {
                        string[] split = arg.Split(' ');

                        if (split.Length == 2)
                            args.Add($"{split[0]}:{indexChecked} {split[1]}");
                    }
                }
            }

            return string.Join(" ", args);
        }

        /// <summary>
        /// The same audio settings for every stream, without the stream specifiers that would tie them
        /// to particular tracks.
        /// <para/>
        /// A channel count of zero is "keep what the source has". There is no per-stream count to put in
        /// its place here, so the count goes unsaid - which leaves every track its own layout - and the
        /// bitrate is used as written rather than scaled to a number of channels nobody chose.
        /// </summary>
        private static string GetAudioArgsForAllStreams(int baseBitrate, int overrideChannels, List<string> extraArgs = null)
        {
            List<string> args = new List<string>();

            if (baseBitrate > 0)
            {
                float mult = overrideChannels > 0 ? MiscUtils.GetAudioBitrateMultiplier(overrideChannels) : 1f;
                args.Add($"-b:a {(baseBitrate * mult).RoundToInt()}k");
            }

            if (overrideChannels > 0)
                args.Add($"-ac {overrideChannels}");

            if (extraArgs != null)
                args.AddRange(extraArgs.Where(x => !string.IsNullOrWhiteSpace(x)));

            return string.Join(" ", args);
        }

        public static string GetTilingArgs(Size resolution, string rowArg, string colArg)
        {
            int cols = 0;
            if (resolution.Width >= 1920) cols = 1;
            if (resolution.Width >= 3840) cols = 2;
            if (resolution.Width >= 7680) cols = 3;

            int rows = 0;
            if (resolution.Height >= 1600) rows = 1;
            if (resolution.Height >= 3200) rows = 2;
            if (resolution.Height >= 6400) rows = 3;

            Logger.Log($"GetTilingArgs: Video resolution is {resolution.Width}x{resolution.Height} - Using 2^{cols} columns, 2^{rows} rows (=> {Math.Pow(2, cols)}x{Math.Pow(2, rows)} = {Math.Pow(2, cols) * Math.Pow(2, rows)} Tiles)", true);
            return $"{rowArg}{rows} {colArg}{cols}";
        }
    }
}
