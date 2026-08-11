using Nmkoder.Extensions;
using Nmkoder.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Nmkoder.Data
{
    /// <summary>
    /// What the Tone Mapping dropdown offers: off, or a roll-off curve.
    /// <para/>
    /// The first three are ffmpeg's own <c>tonemap</c> filter's, and libplacebo has all three under the
    /// same names, so those entries mean one thing whichever backend runs. <see cref="Spline"/> is
    /// libplacebo's alone - it is the curve its <c>auto</c> selects, measured identical to it - and it
    /// is the brightest and closest to the reference of the set: 129/152 at 100 and 203 nits against
    /// hable's 115/143. There is nothing like it in the <c>tonemap</c> filter, so on a machine without
    /// a usable GPU it falls back to hable, which is the closest of the three, and the log says so.
    /// <para/>
    /// Neither tab saves this now, so nothing depends on the order - but the entries are still only
    /// ever appended, since the same enum is what both boxes are filled from and what
    /// <see cref="GetLibplaceboArgs"/> spells its curve name out of.
    /// </summary>
    public enum ToneMapMode { Off, Hable, Mobius, Reinhard, Spline }

    /// <summary>
    /// Converting an HDR source to SDR, which is a luminance operation and a gamut conversion together:
    /// a PQ or HLG curve carrying up to 10000 nits has to be squeezed into the 100 nits BT.709 describes,
    /// and BT.2020 primaries mapped into BT.709's smaller triangle.
    /// <para/>
    /// **Everything here was measured against a current BtbN master build rather than read off a wiki**,
    /// because the chain everyone copies is wrong for real HDR10 in a way that only shows on content
    /// bright enough to matter. See <see cref="AnchorNits"/> and <see cref="GetTonemapPeak"/> for the
    /// pair of numbers that decides it.
    /// <para/>
    /// **There are two backends and the machine picks.** libplacebo is the better tone-mapper and runs
    /// wherever <see cref="Media.ToneMap.GetLibplaceboProblem"/> can find a real GPU behind it; the
    /// zscale + tonemap chain below is what every other machine gets, on the CPU, needing no device and
    /// costing 0.17s against 0.07s for a plain pixel format conversion - which next to any real encode
    /// is nothing. <see cref="UI.Tasks.ToneMapUi.ResolveBackendAsync"/> settles which, once per encode,
    /// and says so in the log.
    /// </summary>
    public class ToneMapConfig
    {
        public ToneMapMode Mode = ToneMapMode.Off;

        public bool Runs { get { return Mode != ToneMapMode.Off; } }

        /// <summary>
        /// The brightest pixel a sampled scan of the file actually found, in nits, or 0 where nothing
        /// was measured. Filled by <see cref="UI.Tasks.ToneMapUi.ResolveBackendAsync"/> for the one
        /// backend that needs it - the zscale chain, whose roll-off takes its peak as a number - and
        /// left 0 for libplacebo, which measures every frame itself with peak detection.
        /// <para/>
        /// It exists because the declared metadata routinely describes a picture that is not in the
        /// file. The reported case is the ordinary shape of a UHD Blu-ray: MaxCLL 9978 and a 4000-nit
        /// mastering display over a film whose frames measure about 610 nits - so a roll-off built for
        /// the metadata spends the top two thirds of the SDR range on highlights that never come, and
        /// the whole picture sits 30-odd code values darker than the same file tone-mapped by a player
        /// that measures it. See <see cref="GetEffectivePeakNits"/> for how the two numbers combine.
        /// </summary>
        public double MeasuredPeakNits = 0;

        /// <summary>
        /// Whether libplacebo does the work instead of the zscale chain. Settled per encode by
        /// <see cref="UI.Tasks.ToneMapUi.ResolveBackendAsync"/> and never read off a control: it is a
        /// property of the machine rather than a preference, and the machine is asked once, at the
        /// start of the run, for the same reason the QTGMC probe is - a fallback decided halfway
        /// through would be a different picture in the second half of the file.
        /// </summary>
        public bool UseLibplacebo = false;

        /// <summary>
        /// Forces the zscale chain where the machine could have run libplacebo - the AV1AN row's
        /// "CPU chain (no pass)" tick. The backend is otherwise a property of the machine, and this is
        /// the one direction an override of that is offered: what the tick buys on that tab is
        /// structural - no render pass in front of av1an and no intermediate on disk, the chain running
        /// per chunk instead - and with the sampled peak scan feeding the roll-off, the picture it costs
        /// is a few code values against the GPU result. The other direction is deliberately not offered:
        /// the probe's "no" is a measurement (a software Vulkan device tone-maps at a tenth of the
        /// speed), not a preference to argue with.
        /// <para/>
        /// <see cref="UI.Tasks.ToneMapUi.ResolveBackendAsync"/> honours it by not probing at all - a
        /// probe whose answer would be discarded is a process launch for nothing - and everything
        /// downstream already branches on <see cref="UseLibplacebo"/>, which is why this is one flag
        /// rather than a second pipeline. Quick Convert has no such tick: its libplacebo is one filter
        /// in a chain it runs inline, so the pass-and-intermediate trade the tick expresses does not
        /// exist there.
        /// </summary>
        public bool ForceCpuChain = false;

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
        /// It is a global option, but position-flexible: measured, ffmpeg accepts it **after the
        /// <c>-i</c>**, which is where Quick Convert's command and <see cref="Media.ToneMapPass"/>
        /// both place it, and where the probe places it too so that what is tested is what ships.
        /// (The AV1AN tab's per-chunk chain never carries it any more - libplacebo left that chain
        /// for the pass, see <see cref="UI.Tasks.Av1anUi.GetVideoFilterArgs"/>.)
        /// </summary>
        public const string DeviceArgs = "-init_hw_device vulkan";

        /// <summary> What has to go in front of the filter arguments, or "" for a chain that does not
        /// use libplacebo. Quick Convert prepends this to its own filter argument rather than composing
        /// it itself, so it cannot forget it and the two cannot drift. </summary>
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
                // Named for what it needs rather than left to look like a fourth equal choice: it is the
                // one entry that cannot run everywhere, and a curve that quietly becomes another curve on
                // half the machines is worth one word of warning in the box itself.
                case ToneMapMode.Spline: return "Spline (GPU)";
                default: return mode.ToString();
            }
        }

        /// <summary> The curve's name as its backend spells it. They agree for the three ffmpeg has, and
        /// <see cref="ToneMapMode.Spline"/> is libplacebo's alone - see the enum for what the zscale
        /// chain does with it instead. </summary>
        private string GetCurveName(bool libplacebo)
        {
            ToneMapMode curve = !libplacebo && Mode == ToneMapMode.Spline ? ToneMapMode.Hable : Mode;
            return curve.ToString().ToLowerInvariant();
        }

        /// <summary> Whether this run asked for a curve its backend does not have, which is the one way
        /// a picked curve is not the curve that runs. Read by
        /// <see cref="UI.Tasks.ToneMapUi.ResolveBackendAsync"/>, which is where it gets said. </summary>
        public bool FallsBackToAnotherCurve { get { return Mode == ToneMapMode.Spline && !UseLibplacebo; } }

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
        /// <see cref="GetEffectivePeakNits"/> is where the nits handed in here come from.
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
        /// The sampling headroom on a measured peak: the scan reads a dozen spots of the film, so the
        /// brightest frame of all is likely brighter than the brightest frame it saw, and a peak set
        /// exactly at the sample would clip every highlight the sampling missed. Twice the sample is
        /// also where the zscale chain's shoulder behaves: measured on 610-nit content, the exact peak
        /// ran 400 nits to 252 and hard-clipped everything past 500, where libplacebo's own measured
        /// mapping puts them at 212 and 227-235 - and twice the sample lands at 220 and 235-242, within
        /// a few code values of that reference across the whole range.
        /// </summary>
        public const double MeasuredPeakHeadroom = 2.0;

        /// <summary>
        /// The peak the zscale chain's roll-off is built for, out of the two numbers this app can have
        /// about a file: what its metadata declares, and what a sampled scan of its frames measured.
        /// <para/>
        /// The measurement wins where it exists, with <see cref="MeasuredPeakHeadroom"/> on top,
        /// because the metadata describes the mastering monitor or a scan artifact more often than the
        /// picture - see <see cref="MeasuredPeakNits"/> for the reported case. The declared value still
        /// serves twice: as the cap, since a measurement cannot legitimately exceed what the file says
        /// its own ceiling is (a sampled 610 in a 4000-nit grade may have missed a 4000-nit scene, but
        /// a doubled sample past the declared peak describes frames the format says do not exist); and
        /// as the whole answer where nothing was measured, which is what every file got before the
        /// scan existed. The floor at the raw measurement is for the opposite lie - a file declaring
        /// less than its frames actually hold clips real pixels, and the frames are the authority.
        /// </summary>
        public static double GetEffectivePeakNits(VideoColorData src, double measuredNits)
        {
            double declared = ColorDataUtils.GetDeclaredPeakNits(src);

            if (measuredNits <= 0)
                return declared;

            double withHeadroom = measuredNits * MeasuredPeakHeadroom;
            double capped = declared > 0 ? Math.Min(withHeadroom, declared) : withHeadroom;

            return Math.Max(capped, measuredNits);
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
        /// shape: the file comes out tagged bt709/bt709/bt709. The HDR side data is a separate matter -
        /// see <see cref="HdrSideDataDeletes"/>, which now ends the chain.</item>
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
            string peak = pq ? $":peak={Fmt(GetTonemapPeak(GetEffectivePeakNits(src, MeasuredPeakNits)))}" : "";

            chain.Add($"zscale=transfer=linear{npl}");
            chain.Add("format=gbrpf32le");
            chain.Add("zscale=primaries=bt709");
            chain.Add($"tonemap=tonemap={GetCurveName(libplacebo: false)}{peak}");
            chain.Add("zscale=transfer=bt709:matrix=bt709:range=tv");
            chain.AddRange(HdrSideDataDeletes);

            return string.Join(",", chain);
        }

        /// <summary>
        /// The tail both chains share: the frames' HDR side data deleted, because after a tone map it
        /// describes a picture that no longer exists and some encoders will faithfully write it back
        /// out. This file used to state the zscale chain dropped it on its own, and the measurement
        /// behind that was encoder-dependent without saying so: through libsvtav1 nothing carries the
        /// side data into the file, so it *looked* dropped, where the same chain through libx265 - a
        /// wrapper that maps mastering-display and light-level frame side data straight to encoder
        /// parameters - produced an SDR BT.709 file declaring a 4000-nit mastering display and a
        /// MaxCLL of 9978. libplacebo passes the side data through just the same. The Dolby Vision
        /// entries are on the list on the same grounds, with one difference of degree: an RPU
        /// describing the reshaping of frames that have since been tone-mapped is not merely stale
        /// but actively wrong, and the x265 wrapper can write RPUs too.
        /// </summary>
        private static readonly string[] HdrSideDataDeletes =
        {
            "sidedata=mode=delete:type=MASTERING_DISPLAY_METADATA",
            "sidedata=mode=delete:type=CONTENT_LIGHT_LEVEL",
            "sidedata=mode=delete:type=DOVI_METADATA",
            "sidedata=mode=delete:type=DOVI_RPU_BUFFER",
            // The dynamic three, which the static four above left behind. HDR10+ is the one that is
            // ordinary rather than exotic - a per-scene tone-mapping curve, carried by a good deal of
            // streaming and disc content and by every file this app's own encoders can be told to write
            // one into - and a set of curves describing scene brightnesses that have since been mapped
            // away is the same staleness the mastering display was deleted for, one level more specific.
            // HDR Vivid is the same thing under another standard. The ambient viewing environment is a
            // statement about the room the grade was checked in, which an SDR BT.709 file has no use for.
            //
            // Every one of these names was read out of `ffmpeg -h filter=sidedata` on the bundled build
            // rather than guessed, because the type option takes an enum and a name it does not have
            // fails the filter graph outright - which on this chain is every tone-mapped encode, not an
            // edge case. The list it prints holds DYNAMIC_HDR_PLUS at 17, DYNAMIC_HDR_VIVID at 25 and
            // AMBIENT_VIEWING_ENVIRONMENT at 26, beside the four already here.
            //
            // What reaches an output today is narrower than this list, and that is deliberately not the
            // measure of it: Quick Convert's direct encoders take y4m, which carries no side data at
            // all, so only the ffmpeg-library encoders can leak any of it. The four above were added
            // after exactly that discovery - libsvtav1 dropped them and libx265 wrote them back out, so
            // "the chain drops it" was an encoder's behaviour mistaken for the chain's. The ffmpeg
            // underneath this app is BtbN's rolling master; a wrapper that gains passthrough next month
            // reopens the hole in a build nobody here chose.
            "sidedata=mode=delete:type=DYNAMIC_HDR_PLUS",
            "sidedata=mode=delete:type=DYNAMIC_HDR_VIVID",
            "sidedata=mode=delete:type=AMBIENT_VIEWING_ENVIRONMENT",
        };

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
        /// <c>peak_detect</c> is **on**, and it is the half of libplacebo that earns the backend its
        /// place: it measures each frame's real brightness instead of believing the file, which is the
        /// only way the truth can reach this filter at all. Measured against the current BtbN build,
        /// libplacebo with detection off reads the mastering display and **nothing else** - a MaxCLL of
        /// 610 and one of 9978 tone-map byte-identically - and there is no option to hand it a peak as
        /// a number, no filter that can write side data, and stripping the side data makes it assume
        /// the 10000-nit PQ ceiling, which is darker still. So with detection off, the ordinary UHD
        /// Blu-ray - a 4000-nit mastering display over a film whose frames top out near 600 nits - has
        /// its whole picture priced for highlights that never come: measured on such content, 100 nits
        /// came out at 129 against 139 with detection, reference white at 152 against 176, and the
        /// film's brightest pixel at a dull 186 against 235. That difference is the "output looks
        /// darker than the source in mpv" report, mpv being a peak-detecting renderer.
        /// <para/>
        /// What detection asks in exchange is a **continuous run**: its history restarts wherever the
        /// stream does, and a restart mid-scene steps the exposure - measured, a chunk boundary in a
        /// brightness ramp lands 6 code values off the continuous answer and takes ~23 frames to
        /// converge. Every libplacebo invocation this app builds is therefore a whole-file run: Quick
        /// Convert's chain is one ffmpeg over the file, and the AV1AN tab renders this chain **in
        /// front** of av1an as its own pass rather than per chunk - see <see cref="Media.ToneMapPass"/>,
        /// and the note in <see cref="UI.Tasks.Av1anUi.GetVideoFilterArgs"/> for why the per-chunk
        /// chain must never carry it.
        /// <para/>
        /// <c>setparams</c> goes in front for the reason it does in the zscale chain, and the output
        /// colour is stated on the filter itself so the frames come out tagged bt709/bt709/bt709 and
        /// limited, exactly as that chain's last zscale leaves them.
        /// <para/>
        /// <see cref="HdrSideDataDeletes"/> closes the chain, as it does the zscale one and for the
        /// reason written on it.
        /// </summary>
        private string GetLibplaceboArgs(VideoColorData src)
        {
            List<string> chain = new List<string>();
            string stated = GetSourceStatement(src);

            if (stated.IsNotEmpty())
                chain.Add(stated);

            chain.Add($"libplacebo=tonemapping={GetCurveName(libplacebo: true)}:peak_detect=1" +
                ":colorspace=bt709:color_primaries=bt709:color_trc=bt709:range=tv");

            chain.AddRange(HdrSideDataDeletes);

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
        /// nobody can otherwise see. What is named is the *declared* one, said as the ceiling it is
        /// rather than as the answer it used to be: which backend runs, and with it whether the real
        /// peak comes from per-frame detection or the sampled scan, is not settled until the encode
        /// starts, where the readout is drawn when the file loads. The encode log carries the measured
        /// number - <see cref="UI.Tasks.ToneMapUi.ResolveBackendAsync"/> is where it gets said.
        /// </summary>
        public string GetNote(VideoColorData src)
        {
            if (!ColorDataUtils.IsHdr(src))
                return "";

            string what = ColorDataUtils.DescribeHdr(src);

            if (!Runs)
                return $"This file is {what} · encoded as it is, keeping its colour";

            // What was picked, not what will run: the backend is not settled until the encode
            // starts, and the readout is drawn on file load. ResolveBackendAsync logs the swap.
            // The CPU tick is the one exception - it settles the backend at readout time, so the
            // spline substitution and the tick itself can be said here instead of only in the log.
            string curve = Mode.ToString().ToLowerInvariant();

            if (ForceCpuChain && Mode == ToneMapMode.Spline)
                curve = "spline, which the CPU chain lacks, so hable";

            string cpu = ForceCpuChain ? " · CPU chain by request: no GPU pass, no intermediate" : "";

            if (src.ColorTransfer == ColorDataUtils.TransferHlg)
                return $"{what} to SDR BT.709 · {curve} · HLG stays watchable on an SDR display, so only the top is rolled off{cpu}";

            double declared = ColorDataUtils.GetDeclaredPeakNits(src);
            string peak = declared > 0 ? $"declares {declared:0} nits" : "declares no peak";

            return $"{what} to SDR BT.709 · {curve} · {peak} · the picture's real brightness is measured at encode time{cpu}";
        }

        /// <summary> A filter argument's number. Invariant culture, because a comma decimal separator
        /// would end the filter's option list where a point continues it. </summary>
        private static string Fmt(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
