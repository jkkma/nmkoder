using System;
using System.Collections.Generic;
using System.Linq;
using VC = Nmkoder.Data.CodecUtils.VideoCodec;
using AC = Nmkoder.Data.CodecUtils.AudioCodec;
using SC = Nmkoder.Data.CodecUtils.SubtitleCodec;
using Nmkoder.IO;
using Nmkoder.Data.Codecs;

namespace Nmkoder.Data
{
    class Containers
    {
        public enum Container { Mp4, Mkv, Webm, Mov, M4a, Ogg };

        // Both tables below were checked encoder by encoder against ffmpeg rather than reasoned about:
        // each codec was muxed into each container and the ones that came out are the ones listed. The
        // earlier lists were narrower than ffmpeg in several places, which is the direction that hurts -
        // it refuses combinations that would have worked.

        public static VC[] GetSupportedVideoCodecs (Container c)
        {
            // VP9 does go into MP4, contrary to what this used to say
            if (c == Container.Mp4)
                return new VC[] { VC.Libx264, VC.Libx265, VC.H264Nvenc, VC.H265Nvenc, VC.LibVpx, VC.LibSvtAv1, VC.LibAomAv1 };

            if (c == Container.Mkv)
                return new VC[] { VC.Libx264, VC.Libx265, VC.H264Nvenc, VC.H265Nvenc, VC.LibVpx, VC.LibSvtAv1, VC.LibAomAv1, VC.Png, VC.Jpg };

            if (c == Container.Webm)
                return new VC[] { VC.LibVpx, VC.LibSvtAv1, VC.LibAomAv1 };

            // MOV takes H.264/H.265 but not VP9 or AV1
            if (c == Container.Mov)
                return new VC[] { VC.Libx264, VC.Libx265, VC.H264Nvenc, VC.H265Nvenc };

            // .m4a goes to the ipod muxer, not the mp4 one, and that muxer's tag table is much shorter:
            // H.264 muxes, H.265 and VP9 and AV1 do not. Listing it does not put video in an audio file,
            // it only stops a working combination being called invalid.
            if (c == Container.M4a)
                return new VC[] { VC.Libx264, VC.H264Nvenc };

            return new VC[0]; // OGG holds none of the video codecs offered here
        }

        public static AC[] GetSupportedAudioCodecs(Container c)
        {
            // MP4 is the permissive one: everything offered here muxes into it
            if (c == Container.Mp4)
                return new AC[] { AC.Aac, AC.Opus, AC.Vorbis, AC.Eac3, AC.Mp3, AC.Flac };

            if (c == Container.Mkv)
                return new AC[] { AC.Aac, AC.Opus, AC.Vorbis, AC.Eac3, AC.Mp3, AC.Flac };

            if (c == Container.Webm)
                return new AC[] { AC.Opus, AC.Vorbis };

            // Opus and FLAC are the two MP4 takes and MOV does not - ffmpeg says so in as many words:
            // "opus only supported in MP4", "flac only supported in MP4".
            if (c == Container.Mov)
                return new AC[] { AC.Aac, AC.Vorbis, AC.Eac3, AC.Mp3 };

            if (c == Container.M4a)
                return new AC[] { AC.Aac }; // ipod muxer again - AAC and nothing else offered here

            if (c == Container.Ogg)
                return new AC[] { AC.Opus, AC.Vorbis, AC.Flac };

            return new AC[0];
        }

        public static SC[] GetSupportedSubtitleCodecs(Container c)
        {
            // MP4/MOV take tx3g and nothing else ffmpeg can write - notably not SRT - while Matroska
            // is the other way around and has no tag for tx3g.
            if (c == Container.Mp4 || c == Container.Mov)
                return new SC[] { SC.MovText };

            if (c == Container.Mkv)
                return new SC[] { SC.Srt, SC.WebVtt };

            if (c == Container.Webm)
                return new SC[] { SC.WebVtt };

            return new SC[0]; // M4A and OGG carry no subtitles at all
        }

        /// <summary>
        /// Whether <paramref name="c"/> can store a subtitle stream of the given ffprobe codec name
        /// as-is, i.e. whether "-c:s copy" will mux rather than fail.
        /// </summary>
        public static bool CanCopySubtitleCodec(Container c, string ffprobeCodecName)
        {
            string codec = (ffprobeCodecName ?? "").Trim().ToLower();

            if (c == Container.Mkv)
                return codec != "mov_text"; // Matroska stores every subtitle format ffmpeg writes except MP4's tx3g

            if (c == Container.Mp4 || c == Container.Mov)
                return codec == "mov_text";

            if (c == Container.Webm)
                return codec == "webvtt";

            return false; // M4A and OGG carry no subtitles at all
        }

        public static bool ContainerSupports(Container c, IEncoder enc)
        {
            string name = enc.Name;
            //Logger.Log($"ContainerSupports - Container {c}, IEncoder.Name {enc.Name} - Container supports {string.Join("/", GetSupportedVideoCodecs(c).Select(x => x.ToString()))}");

            if(enc.Type == Streams.Stream.StreamType.Video)
                return name == VC.CopyVideo.ToString() || name == VC.StripVideo.ToString() || GetSupportedVideoCodecs(c).Select(x => x.ToString()).Contains(name);

            if (enc.Type == Streams.Stream.StreamType.Audio)
                return name == AC.CopyAudio.ToString() || name == AC.StripAudio.ToString() || GetSupportedAudioCodecs(c).Select(x => x.ToString()).Contains(name);

            if (enc.Type == Streams.Stream.StreamType.Subtitle)
                return name == SC.CopySubs.ToString() || name == SC.StripSubs.ToString() || GetSupportedSubtitleCodecs(c).Select(x => x.ToString()).Contains(name);

            return false;
        }

        public static Container GetSupportedContainer(IEncoder cv, IEncoder ca, IEncoder cs)
        {
            if (ContainerSupports(Container.Mp4, cv) && ContainerSupports(Container.Mp4, ca) && ContainerSupports(Container.Mp4, cs))
                return Container.Mp4;

            if (ContainerSupports(Container.Mkv, cv) && ContainerSupports(Container.Mkv, ca) && ContainerSupports(Container.Mkv, cs))
                return Container.Mkv;

            if (ContainerSupports(Container.Webm, cv) && ContainerSupports(Container.Webm, ca) && ContainerSupports(Container.Webm, cs))
                return Container.Webm;

            if (ContainerSupports(Container.Mov, cv) && ContainerSupports(Container.Mov, ca) && ContainerSupports(Container.Mov, cs))
                return Container.Mov;

            return Container.Mkv;
        }

        public static string GetMuxingArgs(Container c)
        {
            if (c == Container.Mp4)
                return $"{(Config.GetBool(Config.Key.mp4Faststart) ? "-movflags +faststart" : "")}"; // Web Optimize

            if (c == Container.Mkv)
                return "-default_mode infer_no_subs -max_interleave_delta 0"; // -default_mode: Disable first sub track being set as default, -max_interleave_delta: Fix audio muxing problems

            if (c == Container.Webm)
                return "";

            if (c == Container.Mov)
                return "";

            return "";
        }
    }
}
