using System;

namespace Nmkoder.Data
{
    public enum ResizeMode
    {
        /// <summary> No scale filter at all - the source's own dimensions reach the encoder. </summary>
        Disabled,
        /// <summary> Largest size fitting inside a box, aspect ratio kept. This is what "1080p" means here. </summary>
        Fit,
        /// <summary> The height is the target; the width follows from the aspect ratio. </summary>
        Height,
        /// <summary> The width is the target; the height follows from the aspect ratio. </summary>
        Width,
        /// <summary> A proportion of the source. </summary>
        Percent,
        /// <summary> Exactly these dimensions, whatever the source's aspect ratio - see <see cref="ResizeFill"/>. </summary>
        Exact,
    }

    /// <summary> How <see cref="ResizeMode.Exact"/> reconciles a source whose aspect ratio is not the target's. </summary>
    public enum ResizeFill
    {
        /// <summary> Fit inside and fill the remainder with black. Nothing is lost and nothing is distorted. </summary>
        Pad,
        /// <summary> Fill the frame and cut off the overflow. Nothing is distorted; the edges are lost. </summary>
        Crop,
        /// <summary> Squash the picture into the frame. This is what the two text boxes used to do. </summary>
        Stretch,
    }

    /// <summary>
    /// A resize, held as the *intent* rather than as a pair of numbers: which target, how to round, and
    /// whether upscaling is allowed. The pixel dimensions only exist once <see cref="Compute"/> is given a
    /// source, which is what lets one setting mean the right thing for a 2.39:1 film, a 4:3 DVD and a
    /// phone video in turn - and lets the number be worked out at encode time, after the crop that runs
    /// ahead of the scale filter has settled, and separately for each file of a batch.
    /// </summary>
    public class ResizeConfig
    {
        public ResizeMode Mode { get; set; } = ResizeMode.Disabled;

        /// <summary> The box for <see cref="ResizeMode.Fit"/>, the frame for <see cref="ResizeMode.Exact"/>, the target for Width/Height. </summary>
        public int TargetWidth { get; set; } = 1920;
        public int TargetHeight { get; set; } = 1080;

        public int Percent { get; set; } = 50;
        public ResizeFill Fill { get; set; } = ResizeFill.Pad;

        /// <summary> Output dimensions are rounded to a multiple of this. 2 is the floor, not a preference - see <see cref="RoundDown"/>. </summary>
        public int Modulus { get; set; } = 2;

        /// <summary> Whether a target larger than the source is allowed to enlarge it. Off by default: enlarging invents detail and costs bitrate. </summary>
        public bool AllowUpscale { get; set; } = false;

        /// <summary> Whether a non-square-pixel source is un-squeezed to its display shape. See <see cref="GetSourceSize"/>. </summary>
        public bool CorrectAspect { get; set; } = true;

        /// <summary> The swscale flag, or "" for ffmpeg's own default (bicubic). </summary>
        public string Resampler { get; set; } = "";

        /// <summary> Which dropdown entry produced this, so the box can be put back on it next session. "" means it was configured by hand. </summary>
        public string PresetKey { get; set; } = "";

        public ResizeConfig() { }

        public static ResizeConfig FitBox(int w, int h, string presetKey)
        {
            return new ResizeConfig { Mode = ResizeMode.Fit, TargetWidth = w, TargetHeight = h, PresetKey = presetKey };
        }

        public static ResizeConfig Proportion(int percent, string presetKey)
        {
            return new ResizeConfig { Mode = ResizeMode.Percent, Percent = percent, PresetKey = presetKey };
        }

        /// <summary>
        /// A resize that changes nothing but the pixels' shape: an anamorphic source comes out
        /// de-squeezed to its display size, and a square-pixel one computes back to its own
        /// dimensions. This is what runs when no resize is configured but the frames are headed
        /// somewhere the SAR flag cannot follow - av1an's encoders, or a scale that ends in
        /// setsar=1:1 - so the shape has to be baked into the pixels to survive the trip.
        /// </summary>
        public static ResizeConfig DesqueezeOnly()
        {
            return new ResizeConfig { Mode = ResizeMode.Percent, Percent = 100 };
        }

        #region Geometry

        /// <summary>
        /// The alignment actually used: <see cref="Modulus"/> forced even, and at least two.
        /// <para/>
        /// Two is a floor rather than a default. 4:2:0 stores one chroma sample per 2x2 block of luma, so
        /// an odd dimension has half a sample at its edge, and x264, x265 and SVT-AV1 all refuse one
        /// outright. An odd modulus is corrected rather than merely floored because a *multiple* of an odd
        /// number is odd half the time - a hand-written 3 in the config file would have produced 999 - and
        /// the mod-2 padding that used to catch that is skipped whenever a resize is running.
        /// </summary>
        private int SafeModulus
        {
            get
            {
                int mod = Math.Max(2, Modulus);
                return mod % 2 == 0 ? mod : mod + 1;
            }
        }

        /// <summary>
        /// The frame the scale target is measured against: the source's *display* size, not its stored one.
        /// <para/>
        /// The two differ only for anamorphic sources - a 720x480 DVD stores 720x480 but is shown at 4:3 -
        /// and the distinction cannot be skipped, because the filter chain ends in setsar=1:1 and so has to
        /// hand the encoder a square-pixel frame. Scaling the stored 720x480 to "480p" and then declaring
        /// the pixels square is how the old two boxes turned a 4:3 DVD into a squashed 3:2 one.
        /// </summary>
        public Size GetSourceSize(Size storage, Size sar)
        {
            return CorrectAspect ? AspectRatio.GetDisplaySize(storage, sar) : storage;
        }

        /// <summary>
        /// The source's aspect ratio, taken from the untouched integers rather than from the rounded
        /// display size. A 16:9 NTSC DVD is 720x480 at SAR 32:27, which is 853⅓ pixels wide when it is
        /// square: rounding that to 853 first - an odd number, so the next rounding takes it to 852 -
        /// doubles the error and produces a width nobody asked for. Rounding happens once, at the output.
        /// </summary>
        private double GetAspect(Size storage, Size sar)
        {
            if (storage.Width <= 0 || storage.Height <= 0)
                return 0d;

            if (!CorrectAspect || !AspectRatio.IsAnamorphic(sar))
                return (double)storage.Width / storage.Height;

            return (double)storage.Width * sar.Width / (storage.Height * (double)sar.Height);
        }

        /// <summary> The output dimensions for this source, or <see cref="Size.Empty"/> when there is nothing to compute. </summary>
        public Size Compute(Size storage, Size sar)
        {
            Size src = GetSourceSize(storage, sar);
            double aspect = GetAspect(storage, sar);

            if (Mode == ResizeMode.Disabled || src.Width <= 0 || src.Height <= 0 || aspect <= 0d)
                return Size.Empty;

            int mod = SafeModulus;

            if (Mode == ResizeMode.Exact)
            {
                if (TargetWidth < 1 || TargetHeight < 1)
                    return Size.Empty;

                return new Size(RoundNearest(TargetWidth, mod), RoundNearest(TargetHeight, mod));
            }

            if (Mode == ResizeMode.Fit)
                return ComputeFit(src, aspect, mod);

            double scale = GetScale(src);

            if (Mode == ResizeMode.Width)
                return FromWidth(src.Width * scale, aspect, mod);

            if (Mode == ResizeMode.Percent)
            {
                // Anchored on the short axis so the long one is the derived one: the rounding error is
                // half a modulus either way, and half a modulus is proportionally less of a big number.
                return src.Width <= src.Height
                    ? FromWidth(src.Width * scale, aspect, mod)
                    : FromHeight(src.Height * scale, aspect, mod);
            }

            return FromHeight(src.Height * scale, aspect, mod);
        }

        private Size ComputeFit(Size src, double aspect, int mod)
        {
            if (TargetWidth < 1 || TargetHeight < 1)
                return Size.Empty;

            return FitInside(src, aspect, GetBox(src), mod, AllowUpscale);
        }

        /// <summary> The largest size of this aspect ratio that fits inside <paramref name="box"/>. </summary>
        private static Size FitInside(Size src, double aspect, Size box, int mod, bool allowUpscale)
        {
            double scale = Math.Min((double)box.Width / src.Width, (double)box.Height / src.Height);

            if (!allowUpscale)
                scale = Math.Min(scale, 1d);

            bool widthBound = (double)box.Width / src.Width <= (double)box.Height / src.Height;
            Size result = widthBound ? FromWidth(src.Width * scale, aspect, mod) : FromHeight(src.Height * scale, aspect, mod);

            // Rounding to the *nearest* multiple can land past the edge of the box, and a box that is not
            // a bound is not a box: 1080 is not a multiple of 16, so a 16:9 source aligned to 16 rounds
            // its height up to 1088. Whichever side went over is brought back to the largest multiple
            // that fits and the other re-derived from it, which keeps the ratio rather than the box edge.
            if (result.Width > box.Width)
                result = FromWidth(RoundDown(box.Width, mod), aspect, mod);

            if (result.Height > box.Height)
                result = FromHeight(RoundDown(box.Height, mod), aspect, mod);

            return result;
        }

        /// <summary>
        /// The smallest size of this aspect ratio that covers <paramref name="box"/> in both directions -
        /// what a crop-to-fill scales to before the crop takes the overflow off. The derived side is
        /// rounded *up* rather than to the nearest: a frame a pixel short of the crop about to be taken
        /// from it is an ffmpeg error, not a clamp.
        /// </summary>
        private static Size CoverBox(Size src, double aspect, Size box, int mod)
        {
            bool widthBound = (double)box.Width / src.Width >= (double)box.Height / src.Height;

            if (widthBound)
            {
                int w = RoundUp(box.Width, mod);
                return new Size(w, Math.Max(RoundUp(box.Height, mod), RoundUp(w / aspect, mod)));
            }

            int h = RoundUp(box.Height, mod);
            return new Size(Math.Max(RoundUp(box.Width, mod), RoundUp(h * aspect, mod)), h);
        }

        /// <summary>
        /// The box to fit inside, turned to match the source's orientation.
        /// <para/>
        /// Every "1080p" style target is written landscape, but a portrait source fitted literally inside
        /// 1920x1080 comes out 608x1080 - a third of the pixels, for a target every phone and every video
        /// site reads as "1080 across the short side". A square source collapses the box to its shorter
        /// side, which is the same rule stated once more.
        /// </summary>
        private Size GetBox(Size src)
        {
            Size box = new Size(TargetWidth, TargetHeight);

            if ((src.Height > src.Width) != (box.Height > box.Width))
                return new Size(box.Height, box.Width);

            return box;
        }

        /// <summary> How much the source is being multiplied by, before rounding. Not used by Fit or Exact. </summary>
        private double GetScale(Size src)
        {
            double scale;

            switch (Mode)
            {
                case ResizeMode.Height:
                    scale = (double)TargetHeight / src.Height;
                    break;
                case ResizeMode.Width:
                    scale = (double)TargetWidth / src.Width;
                    break;
                case ResizeMode.Percent:
                    // A percentage over 100 is somebody asking for an upscale in as many words, so the
                    // guard below - which is there to stop a *target* quietly enlarging a small source -
                    // has nothing to say about it.
                    return Math.Max(1, Percent) / 100d;
                default:
                    return 1d;
            }

            return AllowUpscale ? scale : Math.Min(scale, 1d);
        }

        // Rounding one dimension and then deriving the other from the aspect ratio, rather than rounding
        // both off the same scale factor: the derived one is then never more than half a modulus from the
        // ratio the source actually has, where two independent roundings can both go the same way.

        private static Size FromWidth(double width, double aspect, int mod)
        {
            int w = RoundNearest(width, mod);
            return new Size(w, RoundNearest(w / aspect, mod));
        }

        private static Size FromHeight(double height, double aspect, int mod)
        {
            int h = RoundNearest(height, mod);
            return new Size(RoundNearest(h * aspect, mod), h);
        }

        /// <summary>
        /// Rounding to a multiple of <paramref name="mod"/>, never to zero. Two is the smallest value that
        /// is ever correct: 4:2:0 stores one chroma sample per 2x2 block of luma, so an odd dimension has
        /// half a chroma sample at the edge and encoders refuse it outright. Larger values exist for
        /// encoders that would otherwise pad internally - 8 and 16 line up with AV1 and HEVC block sizes -
        /// and cost a little aspect ratio accuracy in exchange.
        /// </summary>
        public static int RoundNearest(double value, int mod)
        {
            if (mod < 1)
                mod = 1;

            return Math.Max(mod, (int)Math.Round(value / mod, MidpointRounding.AwayFromZero) * mod);
        }

        private static int RoundDown(double value, int mod)
        {
            if (mod < 1)
                mod = 1;

            return Math.Max(mod, (int)Math.Floor(value / mod) * mod);
        }

        private static int RoundUp(double value, int mod)
        {
            if (mod < 1)
                mod = 1;

            return Math.Max(mod, (int)Math.Ceiling(value / mod) * mod);
        }

        #endregion

        #region Filter

        /// <summary>
        /// Whether this actually changes the frame. A target larger than a source that may not be upscaled
        /// computes back to the source's own size, and there is no reason to run a scale filter for that -
        /// except on an anamorphic source, where reaching the same dimensions square-pixel is the entire job.
        /// </summary>
        public bool IsNoOp(Size storage, Size sar)
        {
            if (Mode == ResizeMode.Disabled)
                return true;

            if (CorrectAspect && AspectRatio.IsAnamorphic(sar))
                return false;

            Size result = Compute(storage, sar);
            return result.IsEmpty || result == storage;
        }

        /// <summary>
        /// The filter chain segment this resize contributes, or "" for nothing. Every offset is worked out
        /// as a number rather than left as an ffmpeg expression: the whole chain is passed to av1an inside
        /// a quoted argument that is in turn re-parsed by a shell, and "(ow-iw)/2" is a poor thing to send
        /// through that when the value is known here.
        /// </summary>
        public string GetFilterArgs(Size storage, Size sar)
        {
            Size src = GetSourceSize(storage, sar);

            if (Mode == ResizeMode.Disabled || src.Width <= 0 || src.Height <= 0)
                return "";

            Size outSize = Compute(storage, sar);

            if (outSize.IsEmpty)
                return "";

            if (Mode != ResizeMode.Exact || Fill == ResizeFill.Stretch)
                return $"scale={outSize.Width}:{outSize.Height}{FlagsArg()},setsar=1:1";

            int mod = SafeModulus;
            double aspect = GetAspect(storage, sar);

            if (Fill == ResizeFill.Pad)
            {
                // The box is taken literally here - no turning it to match a portrait source, the way a
                // preset's box is - because an exact size is the one mode where the user named the frame.
                Size inner = FitInside(src, aspect, outSize, mod, AllowUpscale);
                // Even offsets, so the picture starts on a chroma sample boundary rather than half way
                // into one. ffmpeg would otherwise move it itself, silently, and the preview would be a
                // pixel out from the file.
                int x = (outSize.Width - inner.Width) / 2 / 2 * 2;
                int y = (outSize.Height - inner.Height) / 2 / 2 * 2;
                return $"scale={inner.Width}:{inner.Height}{FlagsArg()},pad={outSize.Width}:{outSize.Height}:{x}:{y}:color=black,setsar=1:1";
            }

            Size cover = CoverBox(src, aspect, outSize, mod);
            int cx = (cover.Width - outSize.Width) / 2 / 2 * 2;
            int cy = (cover.Height - outSize.Height) / 2 / 2 * 2;
            return $"scale={cover.Width}:{cover.Height}{FlagsArg()},crop={outSize.Width}:{outSize.Height}:{cx}:{cy},setsar=1:1";
        }

        private string FlagsArg()
        {
            return string.IsNullOrWhiteSpace(Resampler) ? "" : $":flags={Resampler.Trim()}";
        }

        #endregion

        #region Description

        /// <summary>
        /// The clause that goes after the size and the ratio on the tab's readout - whichever of this
        /// resize's less obvious outcomes applies to this source, or "" when there is nothing to say.
        /// Only one is ever returned: they are ordered by how much the user needs to hear it.
        /// </summary>
        public string GetNote(Size storage, Size sar)
        {
            Size result = Compute(storage, sar);

            if (Mode == ResizeMode.Disabled || result.IsEmpty)
                return "";

            Size src = GetSourceSize(storage, sar);
            bool desqueezed = CorrectAspect && AspectRatio.IsAnamorphic(sar);

            if (Mode == ResizeMode.Exact && Fill == ResizeFill.Stretch)
            {
                double distortion = ((double)result.Width / result.Height) / ((double)src.Width / src.Height);
                string way = distortion > 1d ? "horizontally" : "vertically";
                double amount = (distortion > 1d ? distortion : 1d / distortion) - 1d;

                if (amount > 0.005d)
                    return $"stretched {way} by {amount * 100d:0.#}%";
            }

            if (desqueezed)
                return $"de-squeezed from {storage.Width}x{storage.Height}, whose pixels are {sar.Width}:{sar.Height}";

            if (result == storage)
                return "already this size, so it is left alone";

            // Measured against the picture rather than the frame it sits in. A letterbox scales a 640x480
            // source down to fit inside 1920x1080 and fills the rest with black; the frame is bigger than
            // the source and the picture is not, and calling that an upscale would be wrong every time.
            Size picture = Mode == ResizeMode.Exact && Fill == ResizeFill.Pad
                ? FitInside(src, GetAspect(storage, sar), result, SafeModulus, AllowUpscale)
                : result;

            // The slack is the alignment, not a fudge: a source that is not already a multiple of the
            // modulus cannot stay its own size, because there is no such size an encoder will take. 405
            // has to become 404 or 406, and calling one pixel of that an upscale would put the warning on
            // every such file. It is a proportion rather than a pixel count because the rounding lands on
            // one side and the other side is then derived from it, which multiplies it by the ratio.
            double slack = 1d + (double)SafeModulus / Math.Min(src.Width, src.Height);
            double growth = Math.Max((double)picture.Width / src.Width, (double)picture.Height / src.Height);

            if (growth > slack)
                return "larger than the source - upscaling invents no detail and costs bitrate";

            if (!AllowUpscale && WasClampedByUpscaleGuard(src))
                return "the source is smaller than the target, and upscaling is off";

            return "";
        }

        /// <summary> Whether the no-upscale guard is what decided the size, rather than the target. </summary>
        private bool WasClampedByUpscaleGuard(Size src)
        {
            switch (Mode)
            {
                case ResizeMode.Fit:
                    Size box = GetBox(src);
                    return box.Width > src.Width && box.Height > src.Height;
                case ResizeMode.Height: return TargetHeight > src.Height;
                case ResizeMode.Width: return TargetWidth > src.Width;
                default: return false;
            }
        }

        /// <summary> What was asked for, with no source to measure it against. </summary>
        public string DescribeTarget()
        {
            switch (Mode)
            {
                case ResizeMode.Fit: return $"Fit {TargetWidth}x{TargetHeight}";
                case ResizeMode.Height: return $"{TargetHeight}p";
                case ResizeMode.Width: return $"{TargetWidth}px wide";
                case ResizeMode.Percent: return $"{Percent}%";
                case ResizeMode.Exact: return $"{TargetWidth}x{TargetHeight}";
                default: return "Disabled";
            }
        }

        public ResizeConfig Clone()
        {
            return (ResizeConfig)MemberwiseClone();
        }

        #endregion
    }
}
