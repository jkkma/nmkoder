using System;
using System.Collections.Generic;
using System.Linq;

namespace Nmkoder.Data
{
    /// <summary>
    /// The aspect ratios a source is recognised as, so the resize tool can say what it is looking at
    /// rather than making the user work it out from two numbers.
    /// </summary>
    public class AspectRatio
    {
        /// <summary> Width over height, as a decimal. The table is landscape-only; see <see cref="Describe"/>. </summary>
        public float Value { get; }
        /// <summary> How the ratio is written - "16:9" where that is how people say it, "2.39:1" where it is not. </summary>
        public string Label { get; }
        /// <summary> What the ratio is called, or "" for the ones nobody has a name for. </summary>
        public string Name { get; }

        public AspectRatio(float value, string label, string name = "")
        {
            Value = value;
            Label = label;
            Name = name;
        }

        /// <summary>
        /// Landscape ratios only, ordered by value. A portrait source is matched on its reciprocal and
        /// the label written backwards, which covers every vertical ratio without listing any of them -
        /// and keeps the low end of the table clear, where 9:16 (0.5625) and 2:3 (0.667) would otherwise
        /// crowd against each other far more tightly than the entries here do.
        /// </summary>
        public static readonly AspectRatio[] Known =
        {
            new AspectRatio(1f, "1:1", "Square"),
            new AspectRatio(5f / 4f, "5:4", ""),
            new AspectRatio(4f / 3f, "4:3", "Standard"),
            new AspectRatio(1.43f, "1.43:1", "IMAX film"),
            new AspectRatio(3f / 2f, "3:2", "35mm photo"),
            new AspectRatio(16f / 10f, "16:10", ""),
            new AspectRatio(5f / 3f, "1.66:1", "European widescreen"),
            new AspectRatio(16f / 9f, "16:9", "Widescreen"),
            new AspectRatio(1.85f, "1.85:1", "US widescreen"),
            new AspectRatio(1.9f, "1.90:1", "IMAX Digital"),
            new AspectRatio(2f, "2:1", "Univisium"),
            new AspectRatio(2.2f, "2.20:1", "70mm"),
            new AspectRatio(64f / 27f, "21:9", "Ultrawide"),
            new AspectRatio(2.35f, "2.35:1", "CinemaScope"),
            new AspectRatio(2.39f, "2.39:1", "Anamorphic scope"),
            new AspectRatio(2.76f, "2.76:1", "Ultra Panavision"),
            new AspectRatio(32f / 9f, "32:9", "Super ultrawide"),
        };

        /// <summary>
        /// How far off a ratio may be and still be called by a name, as a fraction of the ratio itself.
        /// <para/>
        /// The table's tightest neighbours are 2.35, 21:9 (2.370) and 2.39, which sit about 0.85% apart -
        /// so a tolerance wide enough to catch real encodes would overlap them all three ways if it were
        /// read as a band. <see cref="Match"/> takes the *nearest* entry instead and only then asks
        /// whether it is close enough, which makes overlap harmless: 1920x816 (2.3529) is 0.12% from 2.35
        /// and 0.73% from 21:9, so it lands where it should either way.
        /// <para/>
        /// 1.5% is then set by what has to be caught rather than by what has to be kept apart. The
        /// loosest case that genuinely is 16:9 is 848x480 (1.7667), 0.63% low; a 2.39 master cut to
        /// 1920x800 is 2.40, 0.42% high. Both are in. A source at 1.82 - between 16:9 and 1.85, and
        /// really neither - is out of both and gets its numbers shown instead, which is the honest answer.
        /// </summary>
        public const float Tolerance = 0.015f;

        /// <summary> Anything nearer than this is written without the "≈", being a ratio rather than a rounding of one. </summary>
        private const float ExactTolerance = 0.005f;

        /// <summary> The nearest known ratio to <paramref name="value"/>, or null if none is within <see cref="Tolerance"/>. </summary>
        public static AspectRatio Match(float value)
        {
            if (value <= 0f || float.IsNaN(value) || float.IsInfinity(value))
                return null;

            AspectRatio nearest = Known.OrderBy(r => Math.Abs(r.Value - value)).First();
            return Math.Abs(nearest.Value - value) / value <= Tolerance ? nearest : null;
        }

        /// <summary>
        /// How a WxH frame's aspect ratio should be written - "16:9", "≈2.39:1", or the numbers
        /// themselves where the table has nothing near enough. Portrait sources are matched on their
        /// reciprocal and the label reversed, so 1080x1920 comes back as "9:16" off the 16:9 entry.
        /// </summary>
        public static string Describe(int width, int height, bool withName = false)
        {
            if (width <= 0 || height <= 0)
                return "";

            bool portrait = height > width;
            float value = portrait ? (float)height / width : (float)width / height;
            AspectRatio match = Match(value);

            if (match == null)
                return Unnamed(width, height);

            string label = portrait ? Reverse(match.Label) : match.Label;
            bool approx = Math.Abs(match.Value - value) / value > ExactTolerance;
            string name = withName && match.Name.Length > 0 ? $" ({match.Name})" : "";
            return $"{(approx ? "≈" : "")}{label}{name}";
        }

        /// <summary> "16:9" -> "9:16", "2.39:1" -> "1:2.39". </summary>
        private static string Reverse(string label)
        {
            string[] parts = label.Split(':');
            return parts.Length == 2 ? $"{parts[1]}:{parts[0]}" : label;
        }

        /// <summary>
        /// A ratio with no name. The reduced fraction is the better answer when it comes out small
        /// enough to read - 3:2 and 11:8 say something - but 1919:1080 says nothing at all, so past a
        /// point the decimal form is used instead.
        /// </summary>
        private static string Unnamed(int width, int height)
        {
            int gcd = Gcd(width, height);
            int w = width / gcd;
            int h = height / gcd;

            if (Math.Max(w, h) <= 64)
                return $"{w}:{h}";

            return height > width ? $"1:{((float)height / width):0.##}" : $"{((float)width / height):0.##}:1";
        }

        public static int Gcd(int a, int b)
        {
            a = Math.Abs(a);
            b = Math.Abs(b);

            while (b != 0)
            {
                int t = b;
                b = a % b;
                a = t;
            }

            return a == 0 ? 1 : a;
        }

        /// <summary>
        /// The size a frame is *displayed* at, which is the storage size only when the pixels are square.
        /// Everything the resize tool computes works from this, because the filter chain ends in
        /// setsar=1:1 and so has to produce a square-pixel frame.
        /// <para/>
        /// A SAR of 0:1, 0:0 or anything else non-positive is ffprobe saying it does not know, and is
        /// taken as square - which is what it almost always is.
        /// </summary>
        public static Size GetDisplaySize(Size storage, Size sar)
        {
            if (storage.Width <= 0 || storage.Height <= 0)
                return storage;

            if (sar.Width <= 0 || sar.Height <= 0 || sar.Width == sar.Height)
                return storage;

            // Widening rather than narrowing, always: a 720x480 DVD at SAR 8:9 could equally be called
            // 640x480 or 720x540, and the second keeps every line of luma the source actually carries.
            if (sar.Width > sar.Height)
                return new Size((int)Math.Round(storage.Width * (double)sar.Width / sar.Height), storage.Height);

            return new Size(storage.Width, (int)Math.Round(storage.Height * (double)sar.Height / sar.Width));
        }

        /// <summary> Whether a stream's pixels are non-square, and so whether correcting for them is a thing this source needs. </summary>
        public static bool IsAnamorphic(Size sar)
        {
            return sar.Width > 0 && sar.Height > 0 && sar.Width != sar.Height;
        }

        /// <summary>
        /// Whether two streams' pixels are the same shape - compared as a ratio, because 32:27 and
        /// 64:54 are one shape written two ways, and a SAR ffprobe did not know is taken as square
        /// like everywhere else here.
        /// </summary>
        public static bool SameShape(Size a, Size b)
        {
            (int aw, int ah) = OrSquare(a);
            (int bw, int bh) = OrSquare(b);
            return (long)aw * bh == (long)ah * bw;
        }

        private static (int Width, int Height) OrSquare(Size sar)
        {
            return sar.Width > 0 && sar.Height > 0 ? (sar.Width, sar.Height) : (1, 1);
        }
    }
}
