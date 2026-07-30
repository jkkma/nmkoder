using System.Collections.Generic;

namespace Nmkoder.Data.Codecs
{
    /// <summary> The content presets each encoder offers, keyed by the encoder's own name. </summary>
    public static class EncoderArgPresets
    {
        private static readonly EncoderArgPreset[] None = new EncoderArgPreset[0];

        /// <summary>
        /// The presets for an encoder, by the same name its argument JSON is filed under. Encoders
        /// without any get an empty list, and no preset row on the Advanced tab.
        /// </summary>
        public static IReadOnlyList<EncoderArgPreset> For(string encoderName)
        {
            return encoderName == nameof(Video.SvtAv1) ? SvtAv1Presets : None;
        }

        private static readonly EncoderArgPreset[] SvtAv1Presets = new EncoderArgPreset[0];
    }
}
