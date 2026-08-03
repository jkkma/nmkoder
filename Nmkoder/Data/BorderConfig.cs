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
        /// The growth is a multiple of four rather than of two, which is what makes the two bars come
        /// out the same width *and* both edges land on the chroma grid: 4:2:0 stores one colour
        /// sample per 2x2 block, so an odd frame is refused by every encoder here and an odd offset is
        /// silently moved by ffmpeg's own pad filter - which would put the picture a pixel away from
        /// the middle it was centred in. Half of a multiple of four is even, so both fall out of the
        /// one rounding. It costs up to two pixels of accuracy in the ratio, against an aspect ratio
        /// tolerance measured in whole percents.
        /// <para/>
        /// Rounded to the nearest rather than up, and nothing at all under four pixels: two pixels a
        /// side is not a bar, and adding one to a source that is already the target shape to within a
        /// rounding is exactly the surprise this must not spring on a batch.
        /// </summary>
        private static Axis PadAxis(int input, double ideal)
        {
            Axis axis = new Axis { Size = input, Offset = 0 };
            double gap = ideal - input;

            if (input < 1 || double.IsNaN(gap) || double.IsInfinity(gap) || gap < 4d)
                return axis;

            int add = (int)Math.Round(gap / 4d, MidpointRounding.AwayFromZero) * 4;

            if (add < 4)
                return axis;

            axis.Size = input + add;
            axis.Offset = add / 2;

            // Everything that reaches this hands over an even frame - the mod-2 pad and every size a
            // resize computes see to that - but an odd one would carry its oddness straight into the
            // result, which no encoder takes. One pixel onto the far bar is the cheapest way out, and
            // it costs only the symmetry that an odd frame could not have had in the first place.
            if (axis.Size % 2 != 0)
                axis.Size++;

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
