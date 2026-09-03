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
using Nmkoder.UI.Tasks;
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

        // **Appended, never reordered, and the reason is no longer the one above.** Neither codec
        // dropdown saves its index any more - Quick Convert restores nothing - but the CRF ladder does
        // save this enum's *numeric* value (Config.Key.UtilCrfLadderEncoder, read back in
        // UtilCrfLadder.Load), so a member replaced at an existing ordinal repoints every config file
        // that already exists. Its Encoders.Contains guard does not catch that: a different member at
        // the same ordinal is still in the array.
        //
        // The Direct* five are the standalone binaries Quick Convert now drives; the Lib* five are
        // ffmpeg's own, kept because the CRF ladder deliberately runs on ffmpeg's encoders rather than
        // av1an's - see "ffmpeg's own encoders, not av1an's" in CLAUDE.md - even though Quick Convert
        // no longer offers them.
        public enum VideoCodec
        {
            CopyVideo, StripVideo, Libx264, Libx265, H264Nvenc, H265Nvenc, LibVpx, LibSvtAv1, LibAomAv1,
            Gif, Png, Jpg,
            DirectX264, DirectX265, DirectVpx, DirectSvtAv1, DirectAomAv1,
        };
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
            if (c == VideoCodec.DirectX264) return new DirectX264();
            if (c == VideoCodec.DirectX265) return new DirectX265();
            if (c == VideoCodec.DirectVpx) return new DirectVpx();
            if (c == VideoCodec.DirectSvtAv1) return new DirectSvtAv1();
            if (c == VideoCodec.DirectAomAv1) return new DirectAomAv1();
            return null;
        }

        /// <summary> The encoder as a binary this app launches itself, or null where it is one of
        /// ffmpeg's own. The one place the two kinds are told apart. </summary>
        public static IBinaryEncoder GetBinaryCodec(VideoCodec c)
        {
            return GetCodec(c) as IBinaryEncoder;
        }

        /// <summary> The key both tabs carry the flag below under, in the argument dictionary the
        /// encoder classes read. </summary>
        public const string NoPromptKey = "noPrompt";

        /// <summary>
        /// The flag that stops aomenc and vpxenc waiting for a keypress, or "" for every other encoder
        /// and for a build that turns out not to have it.
        /// <para/>
        /// **Both of them stop and ask at values their own argument rows offer.** <c>min-q</c> and
        /// <c>max-q</c> are 0-63 with a default of 63 in <c>AomAv1.json</c> and <c>Vpx.json</c> alike;
        /// set within 8 of each other the binary prints <c>Warning: Bad quantizer values…</c> followed
        /// by <c>1 encoder configuration warning(s). Continue? (y to continue)</c> and reads a byte
        /// from stdin. Measured against the 2.8.78 bundle (AV1 Encoder v3.14.1, VP9 Encoder
        /// v1.15.2-151-gd98e70839): <c>--min-q=55 --max-q=63</c> is clean and 56 asks, so the boundary
        /// is exactly the documented 8, and it is the *only* configuration warning either binary has -
        /// grepped, one <c>Continue?</c> and one <c>Bad quantizer values</c> string in each.
        /// <para/>
        /// What that costs depends on what stdin is, and the two outcomes are worth telling apart.
        /// With stdin an open pipe that never delivers, both **hang indefinitely** - measured, still
        /// blocked at 25 s. With stdin the y4m the encode actually arrives on, the read is satisfied
        /// by frame data, which is not 'y', so the encoder **exits 1 in ~130 ms having written
        /// nothing** and the encode dies at the first chunk or the first pass. That is the shape this
        /// app produces, on both tabs; the hang is what would happen if the producer ever went quiet.
        /// <para/>
        /// Guarded rather than written unconditionally, because both encoders refuse a command over an
        /// unrecognised option outright - measured, <c>Error: Unrecognized option</c>, rc=1, nothing
        /// written - so an unguarded flag would trade this bug for a worse one on any build lacking
        /// it. The lookup errs the right way by construction: an encoder that cannot be found or run
        /// gets the benefit of the doubt and the flag goes out anyway, which is the direction that
        /// fixes the encode.
        /// </summary>
        public static async Task<string> GetNoPromptArg(string toolName)
        {
            // Named rather than deduced: nothing else this app launches has this prompt, and grav1synth
            // - which has the same trap under its own -y - is not run through an encoder class.
            if (toolName != "aomenc" && toolName != "vpxenc")
                return "";

            const string flag = "--disable-warning-prompt";
            return await Media.AvProcess.ToolKnowsFlagOrIsUnknown(toolName, flag) ? flag : "";
        }

        /// <summary> The same question for the AV1AN tab, which knows its encoders as codecs. </summary>
        public static async Task<string> GetNoPromptArg(Av1anCodec c)
        {
            return await GetNoPromptArg(c == Av1anCodec.AomAv1 ? "aomenc" : c == Av1anCodec.Vpx ? "vpxenc" : "");
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

        /// <summary>
        /// A keyframe every <paramref name="intervalSeconds"/> seconds, as a frame count.
        /// <para/>
        /// <paramref name="rateOverride"/> is the rate the frames being encoded actually arrive at,
        /// which is not the source's whenever something upstream changed it - a bob deinterlacer
        /// emits one frame per field, and the Frame Rate box resamples. Measured against the source
        /// rate instead, an interlaced encode got half the GOP length it asked for, on every
        /// interlaced source rather than only the odd one: 29.97 x 10 = 300 frames, which in a 59.94
        /// fps output is five seconds. Left unsupplied - the CRF ladder, which runs no deinterlacer -
        /// the source rate is the right answer and is what it falls back to.
        /// </summary>
        public static string GetKeyIntArg(MediaFile mediaFile, int intervalSeconds, string arg = "-g ", int max = 480, Fraction rateOverride = default)
        {
            if (mediaFile == null || mediaFile.VideoStreams.Count < 1)
                return "";

            bool overridden = rateOverride.Denominator != 0 && rateOverride.GetFloat() > 0.01f;
            float rate = overridden ? rateOverride.GetFloat() : mediaFile.VideoStreams.FirstOrDefault().Rate.GetFloat();
            int keyInt = ((float)(rate * intervalSeconds)).RoundToInt();
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

            // Belt and braces behind the reset in TrackList.SetAsMainFile, which puts the dropdown back
            // to the global mode wherever the configuration behind it is cleared. The two coming apart is
            // what made this fall back to the global settings without a word.
            if (perTrack && audioConf == null)
                Logger.Log("Per-track audio settings are selected but none are configured for this file - using the Audio tab's own bitrate and channel count.", true);

            foreach (AudioStream s in checkedAudStreams)
            {
                int indexTotal = allAudStreams.IndexOf(s);
                int indexChecked = checkedAudStreams.IndexOf(s);
                int ac = GetOutputChannelCount(s, overrideChannels, perTrack ? audioConf : null, indexTotal);

                // Before the bitrate and the channel count rather than after, only so the command reads
                // in the order things happen. The loudness filter carries the channel conversion itself -
                // see LoudnessConfig.GetFilter, where '-ac' running after the filter chain is the whole
                // reason a normalized downmix used to come out several dB adrift.
                string loudness = QuickConvertUi.CurrentLoudness.GetFilter(QuickConvertUi.GetLoudnessMeasurement(indexChecked), ac);

                if (loudness.IsNotEmpty())
                    args.Add($"-filter:a:{indexChecked} {loudness.Wrap()}");

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

                        // "a:" as well as the number, the way the two lines above it write theirs. A
                        // bare ":0" is a stream specifier meaning *output stream 0*, not the first audio
                        // stream - and in any output with video that is the video. ffmpeg matched Opus's
                        // "-mapping_family 1" against the video encoder, found no such option on it,
                        // dropped it, and said so in a line nothing here reads. The audio streams never
                        // matched at all. An audio-only output is where stream 0 happens to be the
                        // audio, which is why this worked exactly where it was least interesting.
                        if (split.Length == 2)
                            args.Add($"{split[0]}:a:{indexChecked} {split[1]}");
                    }
                }
            }

            return string.Join(" ", args);
        }

        /// <summary>
        /// How many channels one track comes out with: its own per-track setting where one governs, else
        /// the Audio tab's override, else whatever the source has.
        /// <para/>
        /// Its own method because two things need the same answer and must not drift apart - the encoder
        /// arguments below, and the loudness measurement, which has to be made against the layout the
        /// track will actually be written in or the target is missed by however much the downmix moved it.
        /// </summary>
        public static int GetOutputChannelCount(AudioStream s, int overrideChannels, List<AudioConfigurationEntry> audioConf, int indexTotal)
        {
            if (audioConf != null && indexTotal >= 0 && indexTotal < audioConf.Count)
                return audioConf[indexTotal].ChannelCount;

            return overrideChannels > 0 ? overrideChannels : s.Channels;
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

        /// <summary> The key <see cref="GetEncodedFrameSize"/> reads a frame size out of, written "WIDTHxHEIGHT". </summary>
        public const string FrameSizeKey = "frameSize";

        /// <summary>
        /// The frame the encoder will actually be handed, which is the file's own size only when
        /// nothing in front of it changes the picture. A resize or a crop makes those two different
        /// things, and the arguments built from this - the tile count - belong to the frame being
        /// encoded rather than to the file it came from: four tile columns are right for a 4K source
        /// and wrong for the 720p it is being scaled down to, whatever the file says.
        /// <para/>
        /// The AV1AN tab settles its filter chain before it builds these arguments and puts the
        /// result under <see cref="FrameSizeKey"/>. Everywhere else the key is absent and the
        /// source's own size is used, as it always was.
        /// </summary>
        public static Size GetEncodedFrameSize(Dictionary<string, string> encArgs, MediaFile mediaFile)
        {
            if (encArgs != null && encArgs.TryGetValue(FrameSizeKey, out string value))
            {
                string[] parts = (value ?? "").Split('x');

                if (parts.Length == 2 && parts[0].GetInt() > 0 && parts[1].GetInt() > 0)
                    return new Size(parts[0].GetInt(), parts[1].GetInt());
            }

            return mediaFile != null && mediaFile.VideoStreams.Count > 0 ? mediaFile.VideoStreams.First().Resolution : Size.Empty;
        }

        public static string GetTilingArgs(Size resolution, string rowArg, string colArg)
        {
            if (resolution.Width < 1 || resolution.Height < 1)
                return ""; // Nothing to work a tile count out from

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
