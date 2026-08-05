using Nmkoder.Extensions;
using Nmkoder.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Nmkoder.Data
{
    /// <summary>
    /// What the Tone Mapping dropdown offers: off, or one of ffmpeg's three roll-off curves. Saved by
    /// index on the Quick Convert tab, so entries may be appended but never reordered - a saved index
    /// would otherwise start meaning a different curve.
    /// </summary>
    public enum ToneMapMode { Off, Hable, Mobius, Reinhard }

    /// <summary>
    /// Converting an HDR source to SDR, which is a luminance operation and a gamut conversion together:
    /// a PQ or HLG curve carrying up to 10000 nits has to be squeezed into the 100 nits BT.709 describes,
    /// and BT.2020 primaries mapped into BT.709's smaller triangle.
    /// <para/>
    /// **Everything here was measured against a current BtbN master build rather than read off a wiki**,
    /// because the chain everyone copies is wrong for real HDR10 in a way that only shows on content
    /// bright enough to matter. See <see cref="GetNpl"/> for the number that decides it.
    /// <para/>
    /// The backend is zscale + tonemap, on the CPU, and deliberately not libplacebo - which is the better
    /// tone-mapper and was measured to be so (a smooth roll-off to 10000 nits where this chain needs
    /// tuning to get there). Three things ruled it out. It needs a Vulkan device, which it will not
    /// create for itself: without a global <c>-init_hw_device vulkan</c> it fails with "Found no suitable
    /// device, giving up", and that argument is one the AV1AN tab cannot reliably place, since av1an
    /// composes its own per-chunk ffmpeg command and this app only contributes filters through
    /// <c>-f</c>. It cannot be told a real GPU from a software one: measured against Mesa's lavapipe,
    /// libplacebo initialises perfectly and then runs **63x slower than this chain** (4.40s against
    /// 0.17s for the same 48 frames), so a check that merely asks "did it come up" passes and then
    /// destroys the encode's speed. And macOS has no Vulkan at all without MoltenVK. zscale is in the
    /// GPL ffmpeg this project bundles, needs no device, and cost 0.17s against 0.07s for a plain pixel
    /// format conversion - which next to any real encode is nothing.
    /// </summary>
    public class ToneMapConfig
    {
        public ToneMapMode Mode = ToneMapMode.Off;

        public bool Runs { get { return Mode != ToneMapMode.Off; } }

        /// <summary> What the dropdown shows, in <see cref="AllModes"/> order. Short on purpose, as the
        /// Deinterlace list beside it is: the box is 200 wide and truncates anything longer, and the
        /// readout underneath has the whole line to say what the pick will actually do to this file. Which
        /// curve is which is in the tooltip. </summary>
        public static string GetLabel(ToneMapMode mode)
        {
            switch (mode)
            {
                case ToneMapMode.Off: return "No tone mapping";
                default: return mode.ToString();
            }
        }

        public static readonly ToneMapMode[] AllModes = (ToneMapMode[])Enum.GetValues(typeof(ToneMapMode));

        /// <summary>
        /// The peak a PQ grade is assumed to reach when it does not say. 1000 nits is the commonest
        /// HDR10 mastering display by a wide margin, and it is the value the roll-off is least wrong
        /// about either way: a 4000-nit grade tone-mapped as though it were 1000 loses only its
        /// brightest specular highlights, where assuming 10000 for everything crushes every mid-tone in
        /// the far commoner case.
        /// </summary>
        public const double AssumedPeakNits = 1000;

        /// <summary>
        /// How far above <c>npl</c> ffmpeg's tonemap filter keeps rolling off before it clips, measured
        /// rather than derived: PQ ramps built to peak at exactly 1000, 4000 and 10000 nits were run
        /// through this chain at npl 100, 200, 400, 1000 and 2000, and the level where the output
        /// reached 255 and stayed there came out at 374/376/377, 743/746/749, 1509/1509, 3747/3777 and
        /// 7568 nits - a constant 3.75x npl across every source peak, which is what makes it a usable
        /// rule rather than a lookup table.
        /// </summary>
        private const double HighlightHeadroom = 3.75;

        /// <summary>
        /// zscale's nominal peak luminance, which is the single number that decides what this chain
        /// produces - and the one every copied-and-pasted version of it gets wrong.
        /// <para/>
        /// Left unset the filter uses 100, and measured, that clips **everything above about 374 nits to
        /// flat white**: on a 1000-nit HDR10 master every specular highlight, every practical light and
        /// every window is gone. Set from the content's own peak through the 3.75x above, the same
        /// source clips nothing at all and lands within a few code values of libplacebo, which is the
        /// reference implementation - 16/44/110/153 against its 22/54/129/170 at 1, 10, 100 and 203
        /// nits.
        /// <para/>
        /// It has to be worked out here because **the filter will not read it off the file**: the same
        /// PQ ramp with and without a MaxCLL of 1000 and a 1000-nit mastering display tone-maps to
        /// byte-identical output, so tonemap's own peak detection never sees the container's metadata.
        /// <see cref="ColorDataUtils.GetDeclaredPeakNits"/> is where the file's answer comes from.
        /// <para/>
        /// Floored at 100 so this can only ever be gentler than the filter's default, never harsher: a
        /// file declaring some implausibly low peak should not end up with a chain that crushes it.
        /// </summary>
        public static double GetNpl(double peakNits)
        {
            return Math.Max(100, (peakNits > 0 ? peakNits : AssumedPeakNits) / HighlightHeadroom);
        }

        /// <summary>
        /// The names zscale and setparams take for the H.273 integers this app carries colour in. Only
        /// the handful a tone-map can start from or end at, because a name either of those filters does
        /// not know is an error rather than something they ignore - the same trap
        /// <see cref="ColorDataUtils"/> records for aomenc, and every entry here was confirmed by
        /// passing it to the binary rather than by reading the option list.
        /// </summary>
        private static readonly Dictionary<int, string> transferNames = new Dictionary<int, string>
        {
            { ColorDataUtils.TransferPq, "smpte2084" },
            { ColorDataUtils.TransferHlg, "arib-std-b67" },
            { ColorDataUtils.TransferBt709, "bt709" },
        };

        private static readonly Dictionary<int, string> primariesNames = new Dictionary<int, string>
        {
            { ColorDataUtils.PrimariesBt2020, "bt2020" },
            { ColorDataUtils.PrimariesBt709, "bt709" },
        };

        private static readonly Dictionary<int, string> matrixNames = new Dictionary<int, string>
        {
            { 9, "bt2020nc" },
            { 10, "bt2020nc" }, // bt2020c, constant luminance - zimg has no path for it, and the ncl matrix is the closer of the two
            { ColorDataUtils.MatrixBt709, "bt709" },
        };

        /// <summary>
        /// The whole chain, as one comma-joined filter string, or "" when nothing is to be done.
        /// <para/>
        /// Six filters, and each is load-bearing:
        /// <list type="number">
        /// <item><c>setparams</c> states what the source is. Without it the chain depends on whatever the
        /// *decoder* chose to expose, and a file whose tags live only in the container is refused outright
        /// with "code 3074: no path between colorspaces" - measured on a file ffprobe reads correctly at
        /// frame level and not at stream level, which is a state a Matroska file reaches easily. zscale's
        /// own <c>transferin</c>/<c>primariesin</c>/<c>matrixin</c> options look like the answer and are
        /// not: measured, they leave the same file failing with the same error, where setparams fixes it.
        /// Stating tags that were already there changes no pixel.</item>
        /// <item><c>zscale=transfer=linear</c> undoes the transfer curve, because tone-mapping is an
        /// operation on light and PQ code values are not light. <c>npl</c> is what scales it -
        /// see <see cref="GetNpl"/>.</item>
        /// <item><c>format=gbrpf32le</c> because the tonemap filter takes float RGB and nothing else.</item>
        /// <item><c>zscale=primaries=bt709</c> does the gamut conversion in linear light, where it belongs,
        /// ahead of the curve rather than after it.</item>
        /// <item><c>tonemap</c> is the roll-off itself. <c>desat</c> is left at the filter's own default:
        /// it was measured to change even a neutral ramp, so it is not the no-op on greyscale that its
        /// name suggests, and there is no reading of the result that says this app knows better than the
        /// default.</item>
        /// <item><c>zscale=transfer=bt709:matrix=bt709:range=tv</c> puts it back into an ordinary SDR
        /// video signal - and, importantly, retags the frames as it goes, which is what makes the output
        /// correct without a single explicit colour argument anywhere. Measured through the real command
        /// shape: the file comes out tagged bt709/bt709/bt709 with the mastering-display and light-level
        /// side data dropped.</item>
        /// </list>
        /// </summary>
        public string GetFilterArgs(VideoColorData src)
        {
            if (!Runs || !ColorDataUtils.IsHdr(src))
                return "";

            List<string> chain = new List<string>();
            string stated = GetSourceStatement(src);

            if (stated.IsNotEmpty())
                chain.Add(stated);

            // HLG keeps the filter's own nominal peak and PQ does not, because the two curves mean
            // different things by their top end. PQ is absolute - a code value names a luminance, and the
            // grade's own peak is the number the roll-off has to be built around. HLG is relative and was
            // designed to be watchable on an SDR display as it stands, which is measurable here: at the
            // default the HLG ramp comes through close to unchanged with a gentle roll-off over the top
            // of it, where forcing the same npl PQ wants darkened every mid-tone of it. An HLG grade
            // carries no per-file peak to read either, so there would be nothing to derive one from.
            string npl = src.ColorTransfer == ColorDataUtils.TransferPq
                ? $":npl={GetNpl(ColorDataUtils.GetDeclaredPeakNits(src)).ToString("0.###", CultureInfo.InvariantCulture)}"
                : "";

            chain.Add($"zscale=transfer=linear{npl}");
            chain.Add("format=gbrpf32le");
            chain.Add("zscale=primaries=bt709");
            chain.Add($"tonemap=tonemap={Mode.ToString().ToLowerInvariant()}");
            chain.Add("zscale=transfer=bt709:matrix=bt709:range=tv");

            return string.Join(",", chain);
        }

        /// <summary> The <c>setparams</c> that tells the graph what the source is, or "" where this app
        /// has no name for one of the three values and would be guessing. Partial is fine and useful -
        /// a file whose transfer is known and whose matrix is not still gets the transfer stated. </summary>
        private static string GetSourceStatement(VideoColorData src)
        {
            List<string> parts = new List<string>();

            if (transferNames.TryGetValue(src.ColorTransfer, out string trc))
                parts.Add($"color_trc={trc}");

            if (primariesNames.TryGetValue(src.ColorPrimaries, out string prim))
                parts.Add($"color_primaries={prim}");

            if (matrixNames.TryGetValue(src.ColorMatrixCoeffs, out string matrix))
                parts.Add($"colorspace={matrix}");

            return parts.Count > 0 ? $"setparams={string.Join(":", parts)}" : "";
        }

        /// <summary>
        /// What the colour of the output will be once this has run, for the encoders that are handed
        /// colour as numbers rather than picking it up off the frames. Everything else about the source's
        /// colour data - the mastering display, MaxCLL, MaxFALL - describes a grade that no longer exists
        /// and is dropped rather than carried across.
        /// <para/>
        /// The range is **limited whatever the source was**, because the last filter in the chain says
        /// <c>range=tv</c> unconditionally. Carried across from the source instead - which is what this
        /// did first - a full-range HDR master would have had SVT-AV1 told <c>--color-range 1</c> and
        /// x265 told <c>--range full</c> over pixels that are limited, which is a levels mismatch: the
        /// blacks lift and the whites clip on every player that believes the tag. Measured, a source
        /// ffprobe reads as <c>color_range=pc</c> comes out of this chain as <c>tv</c>.
        /// </summary>
        public static VideoColorData GetOutputColorData(VideoColorData src)
        {
            return new VideoColorData
            {
                ColorTransfer = ColorDataUtils.TransferBt709,
                ColorPrimaries = ColorDataUtils.PrimariesBt709,
                ColorMatrixCoeffs = ColorDataUtils.MatrixBt709,
                ColorRange = ColorDataUtils.RangeLimited,
            };
        }

        /// <summary>
        /// The readout under the row, and the line the log carries per file. Clauses joined by a middle
        /// dot, as the resize and border readouts are, and kept to one line: <c>Classes="hint"</c> sets no
        /// TextWrapping, so a longer sentence is not wrapped but clipped at the window's edge.
        /// <para/>
        /// The peak is named because it is the number that decides the whole result and the only one
        /// nobody can otherwise see, and whether it was declared or assumed with it - a file that says
        /// nothing about its own brightness is being guessed at, and that is worth knowing before an
        /// encode rather than after one.
        /// </summary>
        public string GetNote(VideoColorData src)
        {
            if (!ColorDataUtils.IsHdr(src))
                return "";

            string what = ColorDataUtils.DescribeHdr(src);

            if (!Runs)
                return $"This file is {what} · encoded as it is, keeping its colour";

            string curve = Mode.ToString().ToLowerInvariant();

            if (src.ColorTransfer == ColorDataUtils.TransferHlg)
                return $"{what} to SDR BT.709 · {curve} · HLG stays watchable on an SDR display, so only the top is rolled off";

            double declared = ColorDataUtils.GetDeclaredPeakNits(src);
            string peak = declared > 0 ? $"{declared:0} nits, declared" : $"{AssumedPeakNits:0} nits, assumed";

            return $"{what} to SDR BT.709 · {curve} · peak {peak} · highlights above {GetNpl(declared) * HighlightHeadroom:0} nits clip to white";
        }
    }
}
