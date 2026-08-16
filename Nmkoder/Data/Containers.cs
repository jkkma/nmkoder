using VC = Nmkoder.Data.CodecUtils.VideoCodec;
using AC = Nmkoder.Data.CodecUtils.AudioCodec;
using SC = Nmkoder.Data.CodecUtils.SubtitleCodec;
using Nmkoder.IO;

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
            // A container holds a *codec*, so each Direct* entry sits wherever its ffmpeg counterpart
            // does: DirectX264 is H.264 however it was produced. They are listed beside them rather than
            // replacing them because the Lib* members are still reachable through the CRF ladder.

            // VP9 does go into MP4, contrary to what this used to say
            if (c == Container.Mp4)
                return new VC[] { VC.Libx264, VC.Libx265, VC.H264Nvenc, VC.H265Nvenc, VC.LibVpx, VC.LibSvtAv1, VC.LibAomAv1,
                    VC.DirectX264, VC.DirectX265, VC.DirectVpx, VC.DirectSvtAv1, VC.DirectAomAv1 };

            if (c == Container.Mkv)
                return new VC[] { VC.Libx264, VC.Libx265, VC.H264Nvenc, VC.H265Nvenc, VC.LibVpx, VC.LibSvtAv1, VC.LibAomAv1, VC.Png, VC.Jpg,
                    VC.DirectX264, VC.DirectX265, VC.DirectVpx, VC.DirectSvtAv1, VC.DirectAomAv1 };

            if (c == Container.Webm)
                return new VC[] { VC.LibVpx, VC.LibSvtAv1, VC.LibAomAv1, VC.DirectVpx, VC.DirectSvtAv1, VC.DirectAomAv1 };

            // MOV takes H.264/H.265 but not VP9 or AV1
            if (c == Container.Mov)
                return new VC[] { VC.Libx264, VC.Libx265, VC.H264Nvenc, VC.H265Nvenc, VC.DirectX264, VC.DirectX265 };

            // .m4a goes to the ipod muxer, not the mp4 one, and that muxer's tag table is much shorter:
            // H.264 muxes, H.265 and VP9 and AV1 do not. Listing it does not put video in an audio file,
            // it only stops a working combination being called invalid.
            if (c == Container.M4a)
                return new VC[] { VC.Libx264, VC.H264Nvenc, VC.DirectX264 };

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

        // The three checks below answer a different question from the tables above: not "can this
        // container hold what we are about to encode" but "can it hold what the source already is",
        // which is what copying asks. Source files carry codecs this app cannot produce - AC-3, DTS,
        // TrueHD, PCM, ProRes - so the encode tables cannot answer it.
        //
        // Where a container is narrow and well defined the codecs it takes are listed outright; where
        // it is broad the few that fail are listed instead and everything else passes. Erring towards
        // passing is deliberate: letting a bad combination through only reproduces what happens today,
        // ffmpeg refusing it, whereas blocking a good one stops work that would have succeeded.

        /// <summary> Whether "-c:a copy" will mux a source audio stream of this codec into <paramref name="c"/>. </summary>
        public static bool CanCopyAudioCodec(Container c, string ffprobeCodecName)
        {
            string codec = (ffprobeCodecName ?? "").Trim().ToLower();

            if (c == Container.Mkv)
                return true; // Matroska took every audio codec tried against it

            if (c == Container.Webm)
                return codec == "opus" || codec == "vorbis";

            if (c == Container.Ogg)
                return codec == "opus" || codec == "vorbis" || codec == "flac";

            if (c == Container.M4a)
                return codec == "aac" || codec == "ac3" || codec == "alac";

            if (c == Container.Mov)
                return !(codec == "opus" || codec == "flac" || codec == "truehd"); // ffmpeg: "opus only supported in MP4"

            return !(codec.StartsWith("wma") || codec == "truehd"); // MP4 takes the rest, including DTS and PCM
        }

        /// <summary> Whether "-c:v copy" will mux a source video stream of this codec into <paramref name="c"/>. </summary>
        public static bool CanCopyVideoCodec(Container c, string ffprobeCodecName)
        {
            string codec = (ffprobeCodecName ?? "").Trim().ToLower();

            if (c == Container.Mkv)
                return true; // Matroska took every video codec tried against it

            if (c == Container.Webm)
                return codec == "av1" || codec == "vp8" || codec == "vp9";

            if (c == Container.Ogg)
                return codec == "theora" || codec == "vp8";

            if (c == Container.M4a)
                return codec == "h264" || codec == "mpeg4"; // ipod muxer, and an audio container besides

            if (c == Container.Mov)
                return !(codec == "av1" || codec == "vp8" || codec == "vp9");

            return !(codec == "vp8" || codec == "prores" || codec == "theora" ||
                     codec.StartsWith("wmv") || codec.StartsWith("msmpeg4")); // MP4 takes the rest
        }

        // StampsUntimedPackets sat here and is gone with its one caller: it decided whether a raw
        // Annex B stream could go straight to the mux (MP4/MOV/M4A stamp unstamped packets where
        // Matroska refuses them), and both of those stampings turned out to write pts in *decode*
        // order - frames right, timestamps wrong, a dup-and-drop judder at every mini-GOP on any
        // B-frame stream. The route-vs-route measurement its doc recorded was internally consistent
        // and never compared against the source's timing, which is how it shipped. mkvmerge
        // containerises every raw stream now - see QuickConvert.BuildDirectCommand.

        /// <summary>
        /// Whether a data stream can be copied into <paramref name="c"/>. Only QuickTime takes one -
        /// its timecode track - and Matroska rejects them outright: "Only audio, video, and subtitles
        /// are supported for Matroska".
        /// </summary>
        public static bool CanCopyDataStream(Container c)
        {
            return c == Container.Mov;
        }

        /// <summary> Whether an attachment can be copied into <paramref name="c"/>. Matroska alone stores them. </summary>
        public static bool CanCopyAttachment(Container c)
        {
            return c == Container.Mkv;
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
