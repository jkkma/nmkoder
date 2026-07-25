using System;

namespace Nmkoder.Data
{
    /// <summary>
    /// Cross-platform replacement for System.Drawing's Size, which is Windows-only on modern .NET.
    /// Only ever used to carry pixel dimensions / aspect ratios around.
    /// </summary>
    public struct Size : IEquatable<Size>
    {
        public static readonly Size Empty = new Size(0, 0);

        public int Width { get; set; }
        public int Height { get; set; }

        public bool IsEmpty { get { return Width == 0 && Height == 0; } }

        public Size(int width, int height)
        {
            Width = width;
            Height = height;
        }

        public bool Equals(Size other) => Width == other.Width && Height == other.Height;
        public override bool Equals(object obj) => obj is Size s && Equals(s);
        public override int GetHashCode() => HashCode.Combine(Width, Height);

        public static bool operator ==(Size a, Size b) => a.Equals(b);
        public static bool operator !=(Size a, Size b) => !a.Equals(b);

        public override string ToString() => $"{Width}x{Height}";
    }
}
