using System;

namespace Nmkoder.Data
{
    /// <summary>
    /// Black bars added around the picture until the frame is a target aspect ratio - held as the
    /// ratio rather than as a frame size, for the same reason <see cref="ResizeConfig"/> is held as
    /// an intent: "16:9" means 1920x1080 for a 1920x804 film and 1440x1080 for a 1440x1080 capture,
    /// and one setting has to mean the right thing for both, and for the frame a crop and a resize
    /// leave behind rather than the one the file started with.
    /// <para/>
    /// Which bars are needed is not a setting, because it is not a choice: a picture wider than the
    /// target gets them above and below, a narrower one gets them down the sides, and one already at
    /// the target gets none and no filter at all. That is the whole of the auto-detection - a
    /// comparison of two ratios - and it is the reason this is stated as a ratio in the first place.
    /// <para/>
    /// It runs last of the geometry, after the crop and after the resize, so the bars are added to
    /// the finished picture rather than scaled along with it: a scaler run over a hard black edge
    /// rings, and bars that have been through one are neither black nor straight-edged. It also means
    /// the frame this pads is square-pixel almost everywhere - a resize and the de-squeeze that runs
    /// in its place both end in setsar=1:1 - but not quite everywhere, so the pixels' own shape is
    /// taken into account rather than assumed away. Quick Convert with no resize configured is the
    /// case that needs it: nothing there un-squeezes a DVD, ffmpeg carries its aspect flag straight
    /// through to the output, and bars measured against the stored 720x480 would be measured against
    /// a shape nobody ever sees.
    /// </summary>
    public class BorderConfig
    {
        /// <summary> The target display aspect ratio, written the way people say it. Either side at
        /// zero is no target at all, which is what an unset configuration is. </summary>
        public int RatioWidth;
        public int RatioHeight;

        /// <summary> Which dropdown entry this came from - see <see cref="BorderPresets"/>. </summary>
        public string PresetKey = "";

        public BorderConfig() { }

        public BorderConfig(int ratioWidth, int ratioHeight, string presetKey = "")
        {
            RatioWidth = ratioWidth;
            RatioHeight = ratioHeight;
            PresetKey = presetKey;
        }

        public bool IsSet { get { return RatioWidth > 0 && RatioHeight > 0; } }

        /// <summary> The target as a decimal, or 0 when nothing is set. </summary>
        public double Ratio { get { return IsSet ? (double)RatioWidth / RatioHeight : 0d; } }

        /// <summary> How the target reads - "16:9", "4:3" - taken from the ratio table so the name is
        /// written the same way it is everywhere else in the app. </summary>
        public string Label { get { return IsSet ? AspectRatio.Describe(RatioWidth, RatioHeight) : ""; } }

        #region Geometry

        /// <summary>
        /// The bars this adds to <paramref name="frame"/>, whose pixels are <paramref name="sar"/>
        /// shaped. Never null: a frame already at the target comes back as a pad that does not run.
        /// </summary>
        public BorderPad Compute(Size frame, Size sar)
        {
            if (!IsSet || frame.Width < 1 || frame.Height < 1)
                return BorderPad.None(frame);

            // A SAR of 0:1, 0:0 or anything else non-positive is ffprobe saying it does not know, and
            // is taken as square - the same reading AspectRatio.GetDisplaySize gives it.
            int sw = sar.Width > 0 && sar.Height > 0 ? sar.Width : 1;
            int sh = sar.Width > 0 && sar.Height > 0 ? sar.Height : 1;

            double target = Ratio;
            // Measured off the untouched integers rather than off a rounded display size, for the
            // reason ResizeConfig.GetAspect gives: rounding once, at the output, halves the error.
            double current = (double)frame.Width * sw / (frame.Height * (double)sh);

            if (current < target) // Taller than the target, so the bars go down the sides
            {
                // The stored width that comes out at the target once the pixels' shape is applied
                double ideal = target * frame.Height * sh / sw;
                Axis axis = PadAxis(frame.Width, ideal);
                return new BorderPad(frame, new Size(axis.Size, frame.Height), axis.Offset, 0);
            }

            if (current > target) // Wider than the target, so they go above and below
            {
                double ideal = frame.Width * (double)sw / (target * sh);
                Axis axis = PadAxis(frame.Height, ideal);
                return new BorderPad(frame, new Size(frame.Width, axis.Size), 0, axis.Offset);
            }

            return BorderPad.None(frame);
        }

        private struct Axis
        {
            public int Size;
            public int Offset;
        }

        /// <summary>
        /// One axis grown from <paramref name="input"/> towards <paramref name="ideal"/>, and where
        /// the picture then sits on it.
        /// <para/>
        /// Two roundings, and they are deliberately separate, because the frame and the offset answer
        /// to different constraints. The frame is rounded to the nearest *even* size, which is the only
        /// one it has - 4:2:0 stores one colour sample per 2x2 block of luma, so an odd frame is
        /// refused by every encoder here - and rounding it any coarser is what stops the target being
        /// reached at all. That is not hypothetical: a 4K film at 1.85:1 scales to 1920x1038, and 16:9
        /// bars on that want exactly 42 pixels. 42 is not a multiple of four, so growth rounded to one
        /// came out at 1920x1082 - two pixels past the 1920x1080 the setting names, and a frame that is
        /// no longer the ratio it was asked to be.
        /// <para/>
        /// The offset is then rounded down to even on its own, because ffmpeg's own pad filter silently
        /// moves an odd one - which would put the picture a pixel away from where this said it was. The
        /// pixel that rounding leaves over goes onto the far bar, which <see cref="BorderPad.FarBar"/>
        /// reports and the readout and the log both name. A pixel of asymmetry nobody can see is worth
        /// a frame that is the shape it claims.
        /// <para/>
        /// Nothing at all under four pixels of growth: two pixels a side is not a bar, and adding one
        /// to a source that is already the target shape to within a rounding is exactly the surprise
        /// this must not spring on a batch.
        /// </summary>
        private static Axis PadAxis(int input, double ideal)
        {
            Axis axis = new Axis { Size = input, Offset = 0 };

            // The upper bound is against the cast below, not against anything reachable: an out of
            // range double converts to an unspecified int rather than to a large one, and a frame this
            // size is refused by ResizeConfig.ExceedsFrameLimit long before it is refused here.
            if (input < 1 || double.IsNaN(ideal) || double.IsInfinity(ideal) || ideal > int.MaxValue / 2)
                return axis;

            int size = (int)Math.Round(ideal / 2d, MidpointRounding.AwayFromZero) * 2;
            int add = size - input;

            if (add < 4)
                return axis;

            axis.Size = size;
            axis.Offset = add / 2 / 2 * 2;

            return axis;
        }

        #endregion

        #region Description

        /// <summary> What is being asked for, with no source to measure it against. </summary>
        public string DescribeTarget()
        {
            return IsSet ? Label : "Disabled";
        }

        public BorderConfig Clone()
        {
            return (BorderConfig)MemberwiseClone();
        }

        #endregion
    }

    /// <summary>
    /// The bars one <see cref="BorderConfig"/> works out to for one frame: the frame it leaves and
    /// where the picture sits inside it. Resolved once and carried, so the readout, the encoder's
    /// tile count and the filter that runs cannot disagree about any of it.
    /// </summary>
    public class BorderPad
    {
        /// <summary> The frame the bars are added to. </summary>
        public Size Input { get; }

        /// <summary> The frame they leave, which is <see cref="Input"/> when none are added. </summary>
        public Size Frame { get; }

        /// <summary> Where the picture's top left corner sits in <see cref="Frame"/>. </summary>
        public int X { get; }
        public int Y { get; }

        public BorderPad(Size input, Size frame, int x, int y)
        {
            Input = input;
            Frame = frame;
            X = x;
            Y = y;
        }

        /// <summary> A pad that adds nothing, for a frame already at the target - or for no target. </summary>
        public static BorderPad None(Size input)
        {
            return new BorderPad(input, input, 0, 0);
        }

        /// <summary> Whether any bar is actually drawn. </summary>
        public bool Runs { get { return Frame != Input; } }

        /// <summary> Bars down the sides, for a picture narrower than the target. </summary>
        public bool Pillarbox { get { return Frame.Width > Input.Width; } }

        /// <summary> Bars above and below, for a picture wider than the target. </summary>
        public bool Letterbox { get { return Frame.Height > Input.Height; } }

        /// <summary> The left or top bar - the one the offset names. </summary>
        public int NearBar { get { return Pillarbox ? X : Y; } }

        /// <summary> The right or bottom bar, which is the near one except on the odd frame that
        /// could not be padded symmetrically. </summary>
        public int FarBar
        {
            get
            {
                return Pillarbox ? Frame.Width - Input.Width - X : Frame.Height - Input.Height - Y;
            }
        }

        /// <summary>
        /// The filter this contributes, or "" for nothing. Every number is worked out here rather
        /// than left as an ffmpeg expression, for the reason ResizeConfig gives: the AV1AN tab's
        /// whole chain is passed to av1an inside a quoted argument that a shell then re-parses, and
        /// that is a poor place to send arithmetic whose answer is already known.
        /// </summary>
        public string GetFilterArgs()
        {
            return Runs ? $"pad={Frame.Width}:{Frame.Height}:{X}:{Y}:color=black" : "";
        }

        /// <summary> The bars in words, for the readout and the encode log. "" when none are added. </summary>
        public string Describe()
        {
            if (!Runs)
                return "";

            string where = Pillarbox ? "down the left and right" : "along the top and bottom";
            string size = NearBar == FarBar ? $"{NearBar}px" : $"{NearBar}px and {FarBar}px";
            return $"{size} black bars {where}";
        }
    }
}
