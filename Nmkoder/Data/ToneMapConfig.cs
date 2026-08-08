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

        /// <summary>
        /// Whether libplacebo does the work instead of the zscale chain. Settled per encode by
        /// <see cref="UI.Tasks.ToneMapUi.ResolveBackendAsync"/> and never read off a control: it is a
        /// property of the machine rather than a preference, and the machine is asked once, at the
        /// start of the run, for the same reason the QTGMC probe is - a fallback decided halfway
        /// through would be a different picture in the second half of the file.
        /// </summary>
        public bool UseLibplacebo = false;

        /// <summary> Whether this config will actually put a filter in the chain for this file, which
        /// needs the file as well as the mode: the row stays on screen for an HDR file with the curve
        /// set to Off, and an SDR file has nothing to map however the row is set. </summary>
        public bool RunsOn(VideoColorData src)
        {
            return Runs && ColorDataUtils.IsHdr(src);
        }

        /// <summary>
        /// The global argument that creates the Vulkan device, which libplacebo needs and will not
        /// create for itself: without it the filter fails with "Found no suitable device, giving up"
        /// and then "Failed creating Vulkan device!" - and, measured, ffmpeg carries on and exits **0**
        /// having written nothing, which is the quiet failure shape this app has met before.
        /// <para/>
        /// It is a global option and so looks like something only the caller that owns the whole
        /// command line can place - which would rule the AV1AN tab out, av1an composing its own
        /// per-chunk ffmpeg command with this app contributing only what goes inside <c>-f</c>.
        /// Measured, that is not true: ffmpeg accepts it **after the <c>-i</c>**, which is exactly
        /// where av1an splices those arguments in, and the probe places it there too so that what is
        /// tested is what ships.
        /// </summary>
        public const string DeviceArgs = "-init_hw_device vulkan";

        /// <summary> What has to go in front of the filter arguments, or "" for a chain that does not
        /// use libplacebo. Both tabs prepend this to their own filter argument rather than composing
        /// it themselves, so neither can forget it and the two cannot drift. </summary>
        public string GetDeviceArgs(VideoColorData src)
        {
            return UseLibplacebo && RunsOn(src) ? $"{DeviceArgs} " : "";
        }

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
        /// brightest specular highlights, where assuming 10000 for everything spends the top of the
        /// output range on highlights the far commoner file does not have - 104 of 255 at 100 nits
        /// against 126.
        /// </summary>
        public const double AssumedPeakNits = 1000;

        /// <summary>
        /// zscale's nominal peak luminance: the luminance linear 1.0 stands for going into the roll-off,
        /// which is what sets how bright the mid-tones come out. **It is a constant, and that is the
        /// design** - the file's own peak goes to <see cref="GetTonemapPeak"/> instead.
        /// <para/>
        /// It used to be the file's peak divided by a headroom figure, and that is the fault this pair
        /// replaces: npl scales the *whole* signal, so a brighter declared peak darkened the picture
        /// end to end rather than only compressing its highlights. Measured on PQ patches through this
        /// chain, 100 nits came out at 112 of 255 for a grade declaring 1000 and at **71 for one
        /// declaring 4000** - the same picture at 63% of the luminance for a number that describes its
        /// highlights. Reported on an ordinary UHD Blu-ray rip, which is where a 4000-nit mastering
        /// display is the commonest thing in the world.
        /// <para/>
        /// 266.667 is what the old chain already used for a grade declaring nothing, so the file
        /// everybody has is anchored where it always was, and it is the value measured closest to
        /// libplacebo - the reference implementation - on a 1000-nit source: 126/169 at 100 and 203
        /// nits against its 129/170, where an anchor of 100 gives 173/214 and one of 150 gives 140/203.
        /// </summary>
        public const double AnchorNits = 266.667;

        /// <summary>
        /// The tonemap filter's own <c>peak</c>: where the roll-off ends, in units of
        /// <see cref="AnchorNits"/>. The curve maps it to SDR white, so this is the one number the
        /// file's declared peak is allowed to move, and everything above it clips.
        /// <para/>
        /// It has to be passed because **the filter will not read it off the file**: the same PQ ramp
        /// with and without a MaxCLL of 10000 and a 4000-nit mastering display tone-maps to
        /// byte-identical output. The reason is worth knowing, because it looks like a bug in the
        /// metadata and is not - by the time tonemap runs, the frame in front of it has been retagged
        /// <c>linear</c> by the zscale above, so the filter takes its fallback for a non-PQ transfer,
        /// which is a flat 10. That is what the chain was silently running on: a white point of ten
        /// times npl, i.e. 2.667x whatever peak had been declared.
        /// <para/>
        /// Measured through this chain, 100 and 203 nits against a declared peak of
        /// 1000/2000/4000/9999: 126/169, 115/153, 108/144, 104/139. A peak the file overstates now
        /// costs a few code values where it used to cost half the picture.
        /// <para/>
        /// Floored at 1 - a white point below the anchor - because under it this stops being a roll-off
        /// at all and starts being an exposure boost: measured, a declared 203 nits puts BT.2408
        /// reference white at 252 and a declared 100 puts 100 nits itself at 235, which is a picture
        /// blown out on the strength of a number that was probably never measured.
        /// <see cref="ColorDataUtils.GetDeclaredPeakNits"/> is where the file's answer comes from.
        /// </summary>
        public static double GetTonemapPeak(double peakNits)
        {
            return Math.Max(1, (peakNits > 0 ? peakNits : AssumedPeakNits) / AnchorNits);
        }

        /// <summary> The luminance that comes out as SDR white, which is the declared peak wherever the
        /// floor above has not taken over - so it is also the level everything clips from. </summary>
        public static double GetWhitePointNits(double peakNits)
        {
            return GetTonemapPeak(peakNits) * AnchorNits;
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
        /// see <see cref="AnchorNits"/>.</item>
        /// <item><c>format=gbrpf32le</c> because the tonemap filter takes float RGB and nothing else.</item>
        /// <item><c>zscale=primaries=bt709</c> does the gamut conversion in linear light, where it belongs,
        /// ahead of the curve rather than after it.</item>
        /// <item><c>tonemap</c> is the roll-off itself, and <c>peak</c> is where it ends - see
        /// <see cref="GetTonemapPeak"/>. <c>desat</c> is left at the filter's own default:
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
            if (!RunsOn(src))
                return "";

            if (UseLibplacebo)
                return GetLibplaceboArgs(src);

            List<string> chain = new List<string>();
            string stated = GetSourceStatement(src);

            if (stated.IsNotEmpty())
                chain.Add(stated);

            // Both halves are PQ's, and HLG keeps the filter's own defaults for both, because the two
            // curves mean different things by their top end. PQ is absolute - a code value names a
            // luminance, so the grade's peak is a real number and the roll-off can be built around it.
            // HLG is relative and was designed to be watchable on an SDR display as it stands, which is
            // measurable here: at the defaults the HLG ramp comes through close to unchanged with a
            // gentle roll-off over the top of it, where forcing PQ's exposure on it darkened every
            // mid-tone. An HLG grade carries no per-file peak to read either, so there would be nothing
            // to derive one from.
            bool pq = src.ColorTransfer == ColorDataUtils.TransferPq;
            string npl = pq ? $":npl={Fmt(AnchorNits)}" : "";
            string peak = pq ? $":peak={Fmt(GetTonemapPeak(ColorDataUtils.GetDeclaredPeakNits(src)))}" : "";

            chain.Add($"zscale=transfer=linear{npl}");
            chain.Add("format=gbrpf32le");
            chain.Add("zscale=primaries=bt709");
            chain.Add($"tonemap=tonemap={Mode.ToString().ToLowerInvariant()}{peak}");
            chain.Add("zscale=transfer=bt709:matrix=bt709:range=tv");

            return string.Join(",", chain);
        }

        /// <summary>
        /// The same job done by libplacebo, which is the better tone-mapper and is used wherever the
        /// probe finds a real GPU behind it - see <see cref="Media.ToneMap.GetLibplaceboProblem"/> for
        /// what "real" has to mean and why asking is not optional.
        /// <para/>
        /// **The curve names map straight across**, which is the whole reason the row needs no new
        /// entry: libplacebo has <c>hable</c>, <c>mobius</c> and <c>reinhard</c> under those names, so a
        /// setting means the same thing whichever backend it lands on. What differs is everything
        /// around the curve. Measured on the same PQ patches, against a file declaring a 4000-nit
        /// mastering display: 115/143 at 100 and 203 nits where the zscale chain gives 108/144, and its
        /// top lands on **235** - the nominal white of a limited-range signal - where the zscale chain
        /// runs to 247 and spends its brightest highlights in the superwhite a player clips.
        /// <para/>
        /// <c>peak_detect</c> is turned **off**, and that is the one option here worth arguing about.
        /// On it is libplacebo's headline feature: it measures each frame instead of believing the
        /// file, which is what makes a MaxCLL of 10000 harmless. Off, it uses the file's own metadata.
        /// The measurement is what settles it - with the reported file's metadata the two came out
        /// 125/154 against 129/152 for the default curve, and byte-identical for <c>hable</c> - so what
        /// it costs here is nothing much, and what it buys is determinism. **This tab runs the chain
        /// once per chunk**, in an ffmpeg av1an starts and stops around each one, so a detector whose
        /// history restarts at every chunk boundary is a detector that can hand neighbouring chunks
        /// different exposures. There is no <c>src_max</c> to pin it with in this build; off is the
        /// only way to be sure the 26 chunks of a film agree with each other.
        /// <para/>
        /// <c>setparams</c> goes in front for the reason it does in the zscale chain, and the output
        /// colour is stated on the filter itself so the frames come out tagged bt709/bt709/bt709 and
        /// limited, exactly as that chain's last zscale leaves them.
        /// </summary>
        private string GetLibplaceboArgs(VideoColorData src)
        {
            List<string> chain = new List<string>();
            string stated = GetSourceStatement(src);

            if (stated.IsNotEmpty())
                chain.Add(stated);

            chain.Add($"libplacebo=tonemapping={Mode.ToString().ToLowerInvariant()}:peak_detect=0" +
                ":colorspace=bt709:color_primaries=bt709:color_trc=bt709:range=tv");

            return string.Join(",", chain);
        }

        /// <summary>
        /// The <c>setparams</c> that tells the graph what the source is.
        /// <para/>
        /// Partial is fine for a value this app simply has no *name* for: the frame still carries it, and
        /// leaving it unstated lets the decoder supply it. **Unspecified is the exception, and it is the
        /// one case that kills the chain** - there the frame carries nothing either, so zscale is handed
        /// an unspecified matrix for the YUV to RGB step and refuses the whole graph with the very "code
        /// 3074: no path between colorspaces" this filter is here to prevent. Measured: a PQ file stating
        /// its transfer and neither its matrix nor its primaries - which is legal, and what an HEVC
        /// stream with a partial VUI or a Matroska carrying only a transfer element comes out as - fails
        /// exactly that way, and states them and it encodes.
        /// <para/>
        /// So an HDR source that says nothing is told it is BT.2100, which is not a guess: PQ and HLG are
        /// *defined* by BT.2100, and it specifies BT.2020 primaries and the non-constant-luminance BT.2020
        /// matrix alongside them. A file carrying one of those curves and disagreeing about the gamut does
        /// not exist. Only the Unspecified value is substituted, never a real one this app lacks a name
        /// for - overriding a stated smpte170m with bt2020 would be replacing the file's own answer with
        /// this one's, which is the opposite of the job.
        /// </summary>
        private static string GetSourceStatement(VideoColorData src)
        {
            List<string> parts = new List<string>();
            bool hdr = ColorDataUtils.IsHdr(src);

            int primaries = hdr && src.ColorPrimaries == ColorDataUtils.Unspecified
                ? ColorDataUtils.PrimariesBt2020 : src.ColorPrimaries;

            int matrixCoeffs = hdr && src.ColorMatrixCoeffs == ColorDataUtils.Unspecified
                ? ColorDataUtils.MatrixBt2020Ncl : src.ColorMatrixCoeffs;

            if (transferNames.TryGetValue(src.ColorTransfer, out string trc))
                parts.Add($"color_trc={trc}");

            if (primariesNames.TryGetValue(primaries, out string prim))
                parts.Add($"color_primaries={prim}");

            if (matrixNames.TryGetValue(matrixCoeffs, out string matrix))
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

            return $"{what} to SDR BT.709 · {curve} · peak {peak} · highlights above {GetWhitePointNits(declared):0} nits clip to white";
        }

        /// <summary> A filter argument's number. Invariant culture, because a comma decimal separator
        /// would end the filter's option list where a point continues it. </summary>
        private static string Fmt(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
