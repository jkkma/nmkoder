using Nmkoder.Extensions;
using System;
using System.Globalization;

namespace Nmkoder.Data
{
    /// <summary>
    /// What the Loudness dropdown offers. Saved by index, so entries may be appended but never
    /// reordered - a saved index would otherwise start meaning a different target.
    /// </summary>
    public enum LoudnessTarget { Off, Lufs14, Lufs16, Lufs23 }

    /// <summary>
    /// What ffmpeg's first pass measured about one audio track. Every field is a number
    /// <c>loudnorm</c> printed about the track and hands straight back to itself on the second pass;
    /// none of them are this app's to interpret.
    /// </summary>
    public class LoudnessMeasurement
    {
        public double InputI, InputTp, InputLra, InputThresh, TargetOffset;

        /// <summary> The loudness range as measured, which the second pass's own LRA target is taken
        /// from - see <see cref="LoudnessConfig.GetFilter"/>. </summary>
        public double Lra { get { return InputLra; } }
    }

    /// <summary>
    /// EBU R128 loudness normalization, as a target and the ffmpeg filter that reaches it.
    /// <para/>
    /// **This is two-pass, and that is not an optimisation - it is the whole feature.** ffmpeg's
    /// <c>loudnorm</c> run in one pass normalizes *dynamically*, riding the gain as the programme goes,
    /// and measured against a source whose quiet passage sits 26 dB under its loud one it brought the
    /// two to within **1.3 dB of each other** - the quiet passage lifted by nearly 30 dB - while still
    /// reporting that it had hit the target. The same source through the two-pass path came out with all
    /// 26 dB intact. Both land on the requested LUFS, so nothing about the number gives the difference
    /// away; the first is simply a compressor nobody asked for.
    /// <para/>
    /// The first pass measures, the second is the encode itself with those measurements handed back in
    /// and <c>linear=true</c>, which asks for one flat gain over the whole track.
    /// </summary>
    public class LoudnessConfig
    {
        public LoudnessTarget Target = LoudnessTarget.Off;

        public bool Runs { get { return Target != LoudnessTarget.Off; } }

        /// <summary> The integrated loudness each target asks for, in LUFS. </summary>
        public double TargetLufs
        {
            get
            {
                switch (Target)
                {
                    case LoudnessTarget.Lufs14: return -14;
                    case LoudnessTarget.Lufs16: return -16;
                    case LoudnessTarget.Lufs23: return -23;
                    default: return 0;
                }
            }
        }

        /// <summary>
        /// The true-peak ceiling, in dBTP. -1.0 for all three targets: it is what EBU R128 specifies,
        /// and the streaming services that publish a figure ask for -1 or lower. It is deliberately not
        /// ffmpeg's own default of -2, which costs a dB of headroom nobody asked for - but it is the one
        /// number here that can force the encode out of linear mode, so it is not set tighter either.
        /// </summary>
        public const double TruePeakDb = -1.0;

        public static readonly LoudnessTarget[] AllTargets = (LoudnessTarget[])Enum.GetValues(typeof(LoudnessTarget));

        /// <summary> What the dropdown shows. The number leads, because LUFS is what the platforms
        /// publish and what anyone arriving with a requirement already has in hand. </summary>
        public static string GetLabel(LoudnessTarget target)
        {
            switch (target)
            {
                case LoudnessTarget.Off: return "No normalization";
                case LoudnessTarget.Lufs14: return "-14 LUFS (YouTube, Spotify)";
                case LoudnessTarget.Lufs16: return "-16 LUFS (podcast, mobile)";
                case LoudnessTarget.Lufs23: return "-23 LUFS (EBU R128 broadcast)";
                default: return target.ToString();
            }
        }

        /// <summary>
        /// Whether the gain this track needs fits under the true-peak ceiling.
        /// <para/>
        /// **This is a necessary condition for a flat gain, not a sufficient one, and the difference is
        /// deliberate.** A gain that would push the peak past <see cref="TruePeakDb"/> cannot be applied
        /// flat, so ffmpeg rides it instead - that much is certain, and it is the one case worth warning
        /// about, since it is where the dynamics get touched. The reverse does not follow: measured, two
        /// sources whose gain fitted comfortably still came out dynamic, both of them perfectly
        /// stationary noise measuring an LRA of exactly 0.00, so there is at least one more condition
        /// inside loudnorm than this app can see. Real programme material has never been observed to hit
        /// it. Nothing here claims which mode ffmpeg picked - only what would rule the flat one out.
        /// <para/>
        /// The other thing that would rule it out is an LRA target under the track's own loudness range,
        /// and <see cref="GetFilter"/> takes that off the table by deriving the target from the
        /// measurement: ffmpeg's own default of 7 would otherwise force dynamic mode on any film mix,
        /// which routinely runs 10 to 25 LU.
        /// </summary>
        public bool GainFitsUnderTruePeak(LoudnessMeasurement m)
        {
            return m != null && m.InputTp + (TargetLufs - m.InputI) <= TruePeakDb;
        }

        /// <summary> How much flatter this track has to be made, in dB. Negative means quieter. </summary>
        public double GetGainDb(LoudnessMeasurement m)
        {
            return m == null ? 0 : TargetLufs - m.InputI;
        }

        /// <summary>
        /// The <c>-filter:a:N</c> value for one track: the channel conversion, then loudnorm carrying
        /// the first pass's numbers back in.
        /// <para/>
        /// **The channel conversion has to be in here, ahead of loudnorm, and that is the trap this
        /// feature turns on.** The app's own channel control is <c>-ac:a:N</c>, which ffmpeg applies
        /// *after* the filter chain - so loudnorm normalizes the source layout and the downmix then moves
        /// the level out from under it. Measured on a 5.1 source asked for -16 LUFS: -23.67 came out, 7.7
        /// dB adrift, silently. With the conversion inside the chain the same source lands on -16.01, and
        /// the true-peak ceiling then applies to the signal that is actually written rather than to one
        /// that gets mixed down afterwards.
        /// <para/>
        /// <paramref name="channels"/> of 0 means "keep the source's layout", where no conversion is
        /// needed and none is emitted - loudnorm already sees what the encoder will get.
        /// </summary>
        public string GetFilter(LoudnessMeasurement m, int channels)
        {
            if (!Runs || m == null)
                return "";

            string layout = GetChannelLayoutName(channels);
            string convert = layout.IsEmpty() ? "" : $"aformat=channel_layouts={layout},";

            // Taken from the measurement rather than configured, so the loudness range can never be the
            // reason this comes out dynamic - see WillBeLinear. Rounded up because the comparison is
            // against the measured value and a target below it is what selects dynamic; clamped into the
            // range the option accepts.
            double lra = Math.Min(50, Math.Max(1, Math.Ceiling(m.InputLra) + 1));

            return $"{convert}loudnorm=I={N(TargetLufs)}:TP={N(TruePeakDb)}:LRA={N(lra)}" +
                $":measured_I={N(m.InputI)}:measured_TP={N(m.InputTp)}:measured_LRA={N(m.InputLra)}" +
                $":measured_thresh={N(m.InputThresh)}:offset={N(m.TargetOffset)}:linear=true";
        }

        /// <summary> The filter for the *measuring* pass, which is the same chain without the numbers it
        /// does not have yet. The channel conversion is in it for the reason above: the measurement has
        /// to describe the signal that will be written, not the one that was read. </summary>
        public string GetMeasureFilter(int channels)
        {
            string layout = GetChannelLayoutName(channels);
            string convert = layout.IsEmpty() ? "" : $"aformat=channel_layouts={layout},";
            return $"{convert}loudnorm=I={N(TargetLufs)}:TP={N(TruePeakDb)}:LRA=7:print_format=json";
        }

        /// <summary> The names ffmpeg's <c>aformat</c> takes for the channel counts this app's dropdown
        /// offers, all four confirmed against the binary. "" for 0, which means keep what the source has,
        /// and for any count with no name here - where converting would be guessing at a layout. </summary>
        private static string GetChannelLayoutName(int channels)
        {
            switch (channels)
            {
                case 1: return "mono";
                case 2: return "stereo";
                case 6: return "5.1";
                case 8: return "7.1";
                default: return "";
            }
        }

        /// <summary> Invariant culture, because a filter written with a comma for a decimal point is one
        /// ffmpeg reads as the end of the filter. </summary>
        private static string N(double value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}
