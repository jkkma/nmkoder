using System.Collections.Generic;

namespace Nmkoder.Data.Codecs
{
    /// <summary>
    /// The content presets each encoder offers, keyed by the encoder's own name.
    /// <para/>
    /// Every value here is a deliberate departure from what the binary that will run already does.
    /// Setting a parameter to its own default only lengthens the command line, so a value that merely
    /// restates the default is left out even where a guide recommends it - the recommendation is
    /// already in force. Which makes "the default" the thing to be careful about: x265's move with the
    /// speed preset, so a value that is a departure at <c>medium</c> is not one at <c>veryslow</c>,
    /// and each of these was picked against the preset its tab defaults to.
    /// <para/>
    /// The SVT-AV1 set is the AV1AN tab's and is written for the PSY line and nothing else. Its
    /// defaults are svt-av1-hdr's, read from its source rather than its documentation, since several
    /// of them differ from both mainline SVT-AV1 and from the older psy-ex fork most of the community
    /// guides were written against. Nothing there is chosen to also work on mainline - a value whose
    /// only purpose was to make the preset half-work on a build these are not meant for would be a
    /// no-op on every build they are. Parameters mainline lacks are dropped before the encode, and
    /// parameters it merely ignores go quiet on their own.
    /// <para/>
    /// The x264 and x265 sets are Quick Convert's, and every value in them was passed to the library
    /// inside the bundled ffmpeg and observed to be accepted. Those two encoders take no such runtime
    /// check - see <c>Av1anEncoderName</c> - so an argument list written against the wrong build would
    /// go missing quietly there, which is what the measuring is for.
    /// </summary>
    public static class EncoderArgPresets
    {
        private static readonly EncoderArgPreset[] None = new EncoderArgPreset[0];

        /// <summary>
        /// The presets for an encoder, by the same name its argument JSON is filed under. Encoders
        /// without any get an empty list, and no preset row on the Advanced tab.
        /// <para/>
        /// Quick Convert's DirectSvtAv1 shares the SVT set outright: it launches the same
        /// SvtAv1EncApp the AV1AN tab drives, against the same SvtAv1.json rows, so the values carry
        /// with nothing re-measured. **The x264 and x265 sets do not carry to their Direct
        /// counterparts, and that is measured rather than forgotten.** Those values were written as
        /// library parameters, and four of them are spelled as boolean-only flags on the CLI binaries
        /// - x264 has <c>--no-dct-decimate</c> and no <c>--dct-decimate 0</c> (it refuses the option
        /// outright), and x265's <c>--sao</c>, <c>--cutree</c> and <c>--rc-grain</c> take no value at
        /// all, so <c>--sao 0</c> *enables* SAO and leaves the 0 as a stray argument x265 only warns
        /// about. The grid can only express valued rows, so a carried-over preset would quietly apply
        /// the opposite of its loudest entries - the silent half-apply these lists exist to prevent.
        /// A Direct x264/x265 set wants writing against the CLI vocabulary and measuring, not mapping.
        /// </summary>
        public static IReadOnlyList<EncoderArgPreset> For(string encoderName)
        {
            switch (encoderName)
            {
                case nameof(Video.SvtAv1): return SvtAv1Presets;
                case nameof(Video.DirectSvtAv1): return SvtAv1Presets;
                case nameof(Video.Libx264): return Libx264Presets;
                case nameof(Video.Libx265): return Libx265Presets;
                default: return None;
            }
        }

        /// <summary>
        /// What av1an calls an encoder on its own command line, which is also the key to the binary
        /// behind it - so a caller can ask that binary whether it has the parameters being set.
        /// "" for an encoder that is not to be asked.
        /// <para/>
        /// SVT-AV1 alone - under both of its names, since Quick Convert's DirectSvtAv1 launches the
        /// same binary - and that is a limit on the question rather than an oversight: asking is only
        /// sound where <c>--help</c> lists every parameter, and SvtAv1EncApp's prints its whole token
        /// table. x264's does not - it is the short list, with the rest behind <c>--longhelp</c> and
        /// <c>--fullhelp</c> - so half of X264.json would come back unsupported from a binary that has
        /// every one of them. The others are unverified, which is the same answer.
        /// </summary>
        public static string Av1anEncoderName(string encoderName)
        {
            return encoderName == nameof(Video.SvtAv1) || encoderName == nameof(Video.DirectSvtAv1) ? "svt-av1" : "";
        }

        /// <summary>
        /// x264's, reached through <c>-x264-params</c>, and read against the defaults for <c>slow</c>,
        /// which is the speed preset the Quick Convert tab opens on for this encoder. <c>trellis=2</c>
        /// was in the first of these and is not any more: it is already in force from <c>slow</c>
        /// upwards, so it only did anything for someone who had moved the box the other way - and
        /// there it partly undoes the speed-up they had just asked for.
        /// <para/>
        /// <c>chroma-qp-offset</c> below is worth reading twice. x264 applies an offset of its own when
        /// the psychovisual optimisations are on, so the effective value sits at -2 with nothing set
        /// and a typed -2 lands at -4 - measured out of the SEI the encoder writes. The row is still a
        /// departure; what it is not is the number that ends up in the stream.
        /// </summary>
        private static readonly EncoderArgPreset[] Libx264Presets =
        {
            new EncoderArgPreset("Grainy Film / Analogue Transfer",
                "35mm and 16mm scans, VHS, Hi8 and other tape captures - sources where the grain is part " +
                "of the picture rather than something to clean off. Filters and quantises so the noise " +
                "survives the loop instead of being averaged into mush, and gives the grain-heavy scenes " +
                "more of the budget.",
                new Dictionary<string, string>
                {
                    // Filters less in the in-loop deblocker, so grain and fine texture survive into the
                    // reference frames rather than being averaged away before anything can refer to them
                    { "deblock", "-2,-2" },
                    // Stops x264 discarding blocks whose coefficients it judges to be carrying nothing,
                    // which on this material is exactly the grain
                    { "dct-decimate", "0" },
                    // Raises the energy-retention bias and switches on the trellis half of it, which is
                    // what stops the encoder preferring a smooth block to a correctly noisy one
                    { "psy-rd", "1.1,0.15" },
                    // A strong AQ moves bits out of textured areas into flat ones - the opposite of what
                    // everything else here is paying for
                    { "aq-strength", "0.6" },
                    // Flattens the rate curve towards constant quality, so the hard grain-heavy shots
                    // get more of the budget than the default split gives them
                    { "qcomp", "0.70" },
                }),

            new EncoderArgPreset("Anime / Cel Animation",
                "Cel-style 2D and modern digital anime: hard line art, large flat colour fields, long held " +
                "frames and gradients that band easily. Exploits the held cels, smooths the blocking that " +
                "shows in flat fills, and puts bits where this content bands first.",
                new Dictionary<string, string>
                {
                    // Held cels make long B-frame runs almost free, nothing in them changing
                    { "bframes", "8" },
                    // A real search over where those runs should go rather than a heuristic. Worth its
                    // cost only once bframes is raised, which is why the two move together.
                    { "b-adapt", "2" },
                    // Lets the motion search point at the earlier frame that actually matches a reused
                    // cel rather than at the nearest one
                    { "ref", "8" },
                    // Smooths more, which is right on a source with no grain to protect and where
                    // blocking shows first in the flat fills
                    { "deblock", "1,1" },
                    // Adapts the strength per frame, suiting sequences that swing between busy action
                    // cuts and near-static holds
                    { "aq-mode", "2" },
                    // More bits into the flat gradients and skies that band worst on this content
                    { "aq-strength", "1.3" },
                    // 4:2:0 has already thinned the chroma, and large saturated fills are where that
                    // shows: they band in colour long before luma does
                    { "chroma-qp-offset", "-2" },
                    // Backs the energy bias off, which is what stops hard line art ringing - the same
                    // direction x264's own animation tuning moves it
                    { "psy-rd", "0.6,0.0" },
                }),
        };

        /// <summary>
        /// x265's, reached through <c>-x265-params</c>. Read against the defaults for <c>medium</c>,
        /// which is the speed preset the Quick Convert tab opens on: several of these are inert or
        /// already in force at a slower one, <c>rdoq-level</c> most of all.
        /// </summary>
        private static readonly EncoderArgPreset[] Libx265Presets =
        {
            new EncoderArgPreset("Grainy Film / 35mm Scan",
                "Scanned 35mm and grain-heavy live action at a generous CRF: keeps the film grain the " +
                "encoder's own filters are built to average away, and steadies the quantiser so it does " +
                "not visibly breathe from shot to shot.",
                new Dictionary<string, string>
                {
                    // The sample adaptive offset filter is the loudest smoother in an HEVC encode and
                    // turns grain waxy at the bitrates this material wants
                    { "sao", "0" },
                    // Loosens the in-loop filter so noise survives being reused as a reference rather
                    // than being averaged out of the loop
                    { "deblock", "-2,-2" },
                    // Pushes mode decision further towards keeping the source's own energy instead of
                    // minimising measurable error
                    { "psy-rd", "2.5" },
                    // medium runs no rate-distortion quantisation at all, so without this the next
                    // value is silently inert
                    { "rdoq-level", "1" },
                    // The more effective of the two psy dials for keeping grain alive through
                    // quantisation
                    { "psy-rdoq", "2.0" },
                    // Stops adaptive quantisation spending bits flattening the very noise the psy
                    // settings above are paying to keep
                    { "aq-strength", "0.7" },
                    // Gives the grainy, complex shots more of the budget instead of pushing every frame
                    // towards the same size
                    { "qcomp", "0.7" },
                    // cutree reads noise as unreferenced and coarsens it, which is exactly the texture
                    // this preset exists to protect
                    { "cutree", "0" },
                    // Limits how far the quantiser may move within and between frames, which is what
                    // stops grain visibly breathing
                    { "rc-grain", "1" },
                }),

            new EncoderArgPreset("Anime / Cel Animation",
                "Modern digital anime and cel-style 2D: clean line art, large flat colour fields and " +
                "gradients that band easily, with cels held across several frames. Keeps the lines sharp, " +
                "the fills clean and the held frames cheap.",
                new Dictionary<string, string>
                {
                    // Edge-aware weighting keeps a flat fill sitting next to hard line art clean, which
                    // variance-based AQ misjudges
                    { "aq-mode", "4" },
                    // Large flat colour fields and long gradients are the first thing to band here
                    { "aq-strength", "1.3" },
                    // Cel art carries no grain to defend, so a strong psy-rd only protects
                    // high-frequency energy the source does not have
                    { "psy-rd", "1.0" },
                    // As above: medium runs none, and hard lines are where choosing which coefficients
                    // survive matters most
                    { "rdoq-level", "1" },
                    // Held cels and static frames are exactly where long B-frame runs pay
                    { "bframes", "8" },
                    // Gives cutree a view long enough to see a cel held across a shot
                    { "rc-lookahead", "40" },
                    // Both planes get a finer quantiser: 4:2:0 has already thinned the chroma, and
                    // large saturated fills are where that shows
                    { "cbqpoffs", "-2" },
                    { "crqpoffs", "-2" },
                    // Keeps SAO on the I and P slices, where line-art ringing is worst, and drops it
                    // from the B-frames, where it flattens the fills
                    { "selective-sao", "2" },
                    // Lets the transform follow hard line edges more closely in intra blocks, which is
                    // where cel art's detail actually lives
                    { "tu-intra-depth", "2" },
                }),
        };

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
                    // Mode decision at full 10-bit precision. Flat cel fills and long gradients are
                    // exactly where an 8-bit search rounds away differences the 10-bit coding pass
                    // could have kept, which is the banding this preset is otherwise spending
                    // luminance-qp-bias and chroma-qm-min on. The default of 0 is not this value: it
                    // hands the choice to the encoder preset, which runs 10-bit through preset 5 and
                    // tapers off above, so this only changes the encode from preset 6 upward - and
                    // pins it for anyone who moves the preset slider to get through a season.
                    // Needs a 10-bit Color Format, which is what the tab defaults to; on an 8-bit one
                    // SVT ignores it and Av1anUi.GetHbdModeDecisionProblem says so.
                    { "hbd-mds", "1" },
                }),

            new EncoderArgPreset("Game Capture / Gameplay",
                "Raw gameplay capture at 60fps or faster, at whatever size it is being encoded to - a 4K " +
                "capture scaled down to 1080p or 720p included: sustained fast motion, hard-edged HUD " +
                "text, and synthetic skies, fog and volumetrics that band badly. Keeps the screen " +
                "content tools from misfiring on a HUD and taking a whole chunk with them.",
                new Dictionary<string, string>
                {
                    // As above - the fork's headline recommendation at this tab's CRF and preset
                    { "tune", "0" },
                    // Not the usual advice, which holds that screen content mode is what gaming footage
                    // wants. That advice is about desktop and menu recording. On rendered 3D the
                    // detector can misfire on a HUD, and its verdict is taken from the last I-frame and
                    // then carried to every picture after it - so under av1an, where a chunk begins at
                    // every scene cut, one bad call colours the whole chunk. What that call costs is
                    // more than palette coding: a screen content I-slice at preset 6 or slower turns
                    // intra block copy on, and intra block copy gates off both the deblocking filter
                    // and loop restoration for that frame - so the chunk's own anchor frame is the one
                    // that loses them. This is also the row whose worth depends on the frame being
                    // encoded rather than the file: the default of 2 runs no detection at all above
                    // 1080p, so a capture encoded at its native 4K never reaches the misfire and this
                    // value changes nothing, where the same capture scaled down to 1080p or 720p does
                    // and it does. Menu-heavy captures are the exception and want 3 instead.
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
                    // As above, and for the same banding: synthetic skies, fog and volumetrics are
                    // smooth gradients an 8-bit search cannot tell apart. This content is also the
                    // likeliest to be encoded at a fast preset - hours of 60fps capture - which is
                    // precisely where the default of 0 stops running mode decision at 10-bit.
                    { "hbd-mds", "1" },
                }),
        };
    }
}
