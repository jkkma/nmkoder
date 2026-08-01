using Nmkoder.Data;

namespace Nmkoder.Data.Streams
{
    public class VideoStream : Stream
    {
        public string PixelFormat { get; }
        public int Kbits { get; }
        public Size Resolution { get; }
        public Size Sar { get; }
        public Size Dar { get; }
        public Fraction Rate { get; }

        /// <summary> What the container says about the stream's scan type. A flag rather than a
        /// measurement: plenty of interlaced files carry none, which is what <see cref="FieldOrder.Unknown"/>
        /// means and why <see cref="Media.InterlaceDetect"/> exists to settle those cases. </summary>
        public FieldOrder FieldOrder { get; }

        public VideoStream(string language, string title, string codec, string codecLong, string pixFmt, int kbits, Size resolution, Size sar, Size dar, Fraction rate, FieldOrder fieldOrder = FieldOrder.Unknown)
        {
            base.Type = StreamType.Video;
            Codec = codec;
            CodecLong = codecLong;
            PixelFormat = pixFmt;
            Kbits = kbits;
            Resolution = resolution;
            Sar = sar;
            Dar = dar;
            Rate = rate;
            FieldOrder = fieldOrder;
            Language = language;
            Title = title;
        }

        public override string ToString()
        {
            return $"{base.ToString()} - Language: {Language} - Color Format: {PixelFormat} - Size: {Resolution.Width}x{Resolution.Height} - FPS: {Rate}";
        }
    }
}
