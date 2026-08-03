using System;

namespace Nmkoder.Data
{
    /// <summary>
    /// How many pixels come off each side. Held as four edges rather than as a rectangle, because that
    /// is what the dialog asks for and what survives a change of source: "take 140 lines off the top and
    /// bottom" means the same thing on the next file, where "1920x800 at 0,140" does not.
    /// <para/>
    /// The rectangle it works out to is settled here and nowhere else, so the dialog's readout, the
    /// frame the resize is measured against and the filter that runs all agree. Two things are enforced
    /// in the working out, and both used to reach ffmpeg as they were typed:
    /// <para/>
    /// The result is kept inside the frame. Each edge is clamped on its own in the dialog, so nothing
    /// there stopped Left and Right adding up to more than the width - and the four edges outlive the
    /// file they were set for, which is how a batch reaches it without anyone typing anything odd: a
    /// 140px letterbox crop set on a 1080p file is 140 lines off a 480p one too. That produced
    /// "crop=-80:1080:1000:0", which ffmpeg refuses per chunk, hours into an av1an run.
    /// <para/>
    /// The result is even on both axes. 4:2:0 stores one chroma sample per 2x2 block, so an odd width
    /// or height is refused by x264, x265 and SVT-AV1 alike, and an odd offset is silently moved by
    /// ffmpeg's own crop filter - which puts the file a pixel away from what the dialog drew. The
    /// dialog steps in twos, but a typed 3 got through. The offset rounds *up* and the size rounds
    /// *down*, so alignment never re-exposes a sliver of the bar that was being removed.
    /// </summary>
    public class CropConfig
    {
        public int CropLeft;
        public int CropRight;
        public int CropTop;
        public int CropBot;

        /// <summary> The smallest frame a crop may leave, per axis. Two is the floor for the same reason
        /// the sizes are even at all - one 2x2 chroma block - rather than a judgement about what is worth
        /// encoding. Anything genuinely too small for an encoder is that encoder's to refuse, with its
        /// own message; this only stops a frame that no filter chain can produce at all. </summary>
        public const int MinSide = 2;

        public CropConfig (int left = 0, int right = 0, int top = 0, int bot = 0)
        {
            CropLeft = left;
            CropRight = right;
            CropTop = top;
            CropBot = bot;
        }

        public bool IsSet { get { return CropLeft > 0 || CropRight > 0 || CropTop > 0 || CropBot > 0; } }

        /// <summary> Whether this leaves anything to encode - false when the edges eat the whole frame,
        /// which is the case that has to be refused rather than handed to ffmpeg. </summary>
        public bool FitsInside(Size dimensions)
        {
            return dimensions.Width - CropLeft - CropRight >= MinSide
                && dimensions.Height - CropTop - CropBot >= MinSide;
        }

        /// <summary> What is being asked for against a frame that cannot take it, for the message that
        /// refuses the encode. "" when there is nothing wrong. </summary>
        public string GetProblem(Size dimensions)
        {
            if (dimensions.Width < 1 || dimensions.Height < 1 || FitsInside(dimensions))
                return "";

            int w = dimensions.Width - CropLeft - CropRight;
            int h = dimensions.Height - CropTop - CropBot;
            string axis = w < MinSide && h < MinSide ? "nothing" : (w < MinSide ? $"{w} pixels wide" : $"{h} pixels tall");

            return $"the crop takes {CropLeft}+{CropRight} off the width and {CropTop}+{CropBot} off the height of a " +
                $"{dimensions.Width}x{dimensions.Height} frame, which leaves {axis}";
        }

        /// <summary> The "w:h:x:y" the crop filter takes, aligned and clamped. Empty for a frame it
        /// cannot leave anything of - callers check <see cref="FitsInside"/> and say so first. </summary>
        public string GetFilterArgs (Size dimensions)
        {
            Size result = GetCroppedSize(dimensions);
            return $"{result.Width}:{result.Height}:{GetX(dimensions)}:{GetY(dimensions)}";
        }

        /// <summary> The frame this leaves: even on both axes, and never outside the source. </summary>
        public Size GetCroppedSize(Size dimensions)
        {
            return new Size(GetCroppedWidth(dimensions), GetCroppedHeight(dimensions));
        }

        public int GetCroppedWidth(Size dimensions)
        {
            return Fit(dimensions.Width, GetX(dimensions), CropRight);
        }

        public int GetCroppedHeight(Size dimensions)
        {
            return Fit(dimensions.Height, GetY(dimensions), CropBot);
        }

        /// <summary> The left offset, rounded up to the chroma grid: taking one pixel more off an edge is
        /// what was meant by cropping it, where rounding down leaves a line of the bar behind. </summary>
        public int GetX(Size dimensions)
        {
            return Math.Min(RoundUpEven(CropLeft), Math.Max(0, RoundDownEven(dimensions.Width - MinSide)));
        }

        public int GetY(Size dimensions)
        {
            return Math.Min(RoundUpEven(CropTop), Math.Max(0, RoundDownEven(dimensions.Height - MinSide)));
        }

        /// <summary> What is left of an axis after the offset and the far edge, rounded down to even.
        /// Floored rather than allowed to go negative, for the callers that ask before checking
        /// <see cref="FitsInside"/> - a dialog readout mid-keystroke, say. The encode does not get here:
        /// it is refused, with the numbers, by <see cref="GetProblem"/>. </summary>
        private static int Fit(int total, int offset, int farEdge)
        {
            return Math.Max(MinSide, RoundDownEven(total - offset - Math.Max(0, farEdge)));
        }

        private static int RoundUpEven(int value)
        {
            value = Math.Max(0, value);
            return value % 2 == 0 ? value : value + 1;
        }

        private static int RoundDownEven(int value)
        {
            return value - (value % 2);
        }
    }
}
