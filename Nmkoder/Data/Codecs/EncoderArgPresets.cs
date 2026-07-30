using System.Collections.Generic;

namespace Nmkoder.Data.Codecs
{
    /// <summary>
    /// The content presets each encoder offers, keyed by the encoder's own name.
    /// <para/>
    /// Every value here is a deliberate departure from what the bundled binary already does. Setting a
    /// parameter to its own default only lengthens the command line, so a value that merely restates
    /// the default is left out even where a guide recommends it - the recommendation is already in
    /// force. The defaults these were written against are svt-av1-hdr's, read from its source rather
    /// than its documentation, since several of them differ from both mainline SVT-AV1 and from the
    /// older psy-ex fork most of the community guides were written against.
    /// <para/>
    /// They are written for the PSY line and nothing else. Nothing here is chosen to also work on
    /// mainline SVT-AV1 - a value whose only purpose was to make the preset half-work there would be
    /// a no-op on every build these are actually meant for. What mainline does with them is therefore
    /// not a consideration: parameters it lacks are dropped before the encode, and parameters it
    /// merely ignores (it ships quantisation matrices and variance boost off, where the PSY line has
    /// both on) go quiet on their own.
    /// </summary>
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

        /// <summary>
        /// What av1an calls an encoder on its own command line, which is also the key to the binary
        /// behind it - so a preset can ask that binary whether it has the parameters being set.
        /// "" for an encoder whose presets do not need asking.
        /// </summary>
        public static string Av1anEncoderName(string encoderName)
        {
            return encoderName == nameof(Video.SvtAv1) ? "svt-av1" : "";
        }

        private static readonly EncoderArgPreset[] SvtAv1Presets =
        {
            new EncoderArgPreset("Anime / Cel Animation",
                "Modern digital anime and cel-style 2D: clean line art, big flat colour fields, and " +
                "gradients that band easily. Keeps the lines sharp, deblocks more accurately, and spends " +
                "extra bits on the dark scenes that band worst.",
                new Dictionary<string, string>
                {
                    // The bundled fork leads with this at exactly the CRF and preset this tab defaults
                    // to, and psy-ex made it the default outright for animation. Contested: the JET
                    // anime guide keeps the default of 1, which scores better on SSIMULACRA2. The one
                    // setting here picked for how it looks rather than for how it measures.
                    { "tune", "0" },
                    // "Strength 1 tends to be best for simple, untextured, or smooth animation" -
                    // SVT-AV1's own Variance Boost appendix, describing this content exactly.
                    { "variance-boost-strength", "1" },
                    // The most explicitly anime-targeted setting published anywhere: psy-ex's README
                    // has a section headed "Anime Encoding (Minimal Blur)" whose whole content is this
                    // value. 4 leaves only the restoration filter backing off on noisy frames, so CDEF
                    // keeps cleaning up the ringing that hard line art produces.
                    { "noise-adaptive-filtering", "4" },
                    // Recommended for preset 4 and faster, which is what this tab defaults to
                    { "enable-dlf", "2" },
                    // The value in the JET anime guide's own published base parameter string
                    { "luminance-qp-bias", "25" },
                    // psy-ex shipped 10 as its default, and the JET guide asks for a chroma floor above
                    // the luma one: 4:2:0 has already thinned the chroma, and the encoder then picks
                    // matrices that thin it further, which is what bleeds big flat saturated fills.
                    { "chroma-qm-min", "10" },
                    // Extra references for base-layer frames, which pay off unusually well where the
                    // same cel is held across several frames. From psy-ex's own anime command line.
                    { "enable-overlays", "1" },
                }),

            new EncoderArgPreset("Game Capture / Gameplay",
                "Raw gameplay capture at 1080p and up, 60fps or faster: sustained fast motion, hard-edged " +
                "HUD text, and synthetic skies, fog and volumetrics that band badly. Keeps the screen " +
                "content tools from misfiring on a HUD and taking a whole chunk with them.",
                new Dictionary<string, string>
                {
                    // As above - the fork's headline recommendation at this tab's CRF and preset
                    { "tune", "0" },
                    // Not the usual advice, which holds that screen content mode is what gaming footage
                    // wants. That advice is about desktop and menu recording. On rendered 3D the
                    // detector can misfire on a HUD, and its verdict is taken from the last I-frame and
                    // then carried to every picture after it - so under av1an, where a chunk begins at
                    // every scene cut, one bad call colours the whole chunk. Menu-heavy captures are
                    // the exception and want 3 instead.
                    { "scm", "0" },
                    // Boosts a superblock once part of it is low contrast rather than nearly all of it,
                    // which is what fog, haze and god-rays look like. Inside the documented 4-7 band.
                    { "variance-octile", "4" },
                    // Dark caves, night scenes and dim interiors are where capture bands first. The low
                    // end of the documented range, because unlike anime this content is not mostly dark.
                    { "luminance-qp-bias", "10" },
                    // Blocking shows first on large synthetic gradients, which this content is full of
                    { "enable-dlf", "2" },
                    // Saturated HUD colour and coloured UI text on 4:2:0, same reasoning as above
                    { "chroma-qm-min", "10" },
                    // Puts back detail that temporal filtering takes out, which shows more at 60fps
                    { "enable-overlays", "1" },
                }),
        };
    }
}
