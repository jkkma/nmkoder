using ImageMagick;
using System.IO;
using AvaloniaBitmap = Avalonia.Media.Imaging.Bitmap;

namespace Nmkoder.Extensions
{
    /// <summary>
    /// Bridges Magick.NET to Avalonia. The WinForms version converted MagickImage into a
    /// System.Drawing.Bitmap by poking at locked bits; going through an in-memory PNG is both
    /// portable and far simpler, and thumbnails are small enough that the encode cost is irrelevant.
    /// </summary>
    public static class MagickExtensions
    {
        public static AvaloniaBitmap ToAvaloniaBitmap(this IMagickImage<ushort> magickImg)
        {
            if (magickImg == null)
                return null;

            using var memStream = new MemoryStream();
            magickImg.Write(memStream, MagickFormat.Png);
            memStream.Position = 0;
            return new AvaloniaBitmap(memStream);
        }

        /// <summary> Loads an image file through Magick.NET, for formats Avalonia's own decoder can't read. </summary>
        public static AvaloniaBitmap LoadWithMagick(string path)
        {
            using var img = new MagickImage(path);
            return img.ToAvaloniaBitmap();
        }
    }
}
