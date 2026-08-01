using System.Collections.Generic;

namespace Nmkoder.Data
{
    /// <summary>
    /// The geometry of one AV1AN encode, settled ahead of its arguments rather than while its filter
    /// chain is written.
    /// <para/>
    /// It exists because two things need the size of the frame the encoder is handed, and neither had
    /// any way of asking for it. The tile count is a property of that frame and not of the file it
    /// came from, and it goes into the encoder's own arguments, which used to be built before the
    /// filters existed; and the target quality warning turns on whether the frame changed size at
    /// all. Working the size out means resolving the crop, and an automatic one costs ten ffmpeg
    /// probes and a line in the log, so it may only happen once - which is the other half of why this
    /// is carried around rather than recomputed wherever it is wanted.
    /// </summary>
    public class Av1anFrame
    {
        /// <summary> The video stream's own stored size, or empty when there is no video track. </summary>
        public Size Source;

        /// <summary> The stream's pixel aspect ratio, which the resize is measured against. </summary>
        public Size Sar;

        /// <summary> What the scale filter is handed: the source, less whatever crop is set. </summary>
        public Size ScaleInput;

        /// <summary> What the encoder is finally handed. </summary>
        public Size Encoded;

        /// <summary> The crop filters, already resolved, in the order they belong in the chain. </summary>
        public List<string> CropFilters = new List<string>();

        /// <summary> Whether a scale filter runs for the configured resize. </summary>
        public bool Resizing;

        /// <summary> Whether an anamorphic source is de-squeezed because no resize will run. </summary>
        public bool Desqueezing;

        /// <summary> Whether the mod-2 pad runs, which is the one other thing in the chain that moves the size. </summary>
        public bool Padding;

        /// <summary> Whether anything in front of the encoder changes the frame's size. </summary>
        public bool ChangesSize { get { return !Source.IsEmpty && !Encoded.IsEmpty && Encoded != Source; } }
    }
}
