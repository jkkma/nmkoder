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

        /// <summary> What the scale filter leaves - the resize, or the de-squeeze that runs in its
        /// place, or the mod-2 pad, or none of them. This is the picture the border bars go around,
        /// and so the size the resize is reported as having produced. </summary>
        public Size Scaled;

        /// <summary> What the encoder is finally handed: <see cref="Scaled"/> plus any border bars. </summary>
        public Size Encoded;

        /// <summary> The crop filters, already resolved, in the order they belong in the chain. </summary>
        public List<string> CropFilters = new List<string>();

        /// <summary> Why the configured crop cannot be applied to this file, or "" when it can. Carried
        /// rather than thrown so the run refuses with a sentence naming the file and the numbers, where
        /// a crop bigger than the frame otherwise reaches av1an and fails one chunk at a time. </summary>
        public string CropProblem = "";

        /// <summary> Whether a scale filter runs for the configured resize. </summary>
        public bool Resizing;

        /// <summary> Whether an anamorphic source is de-squeezed because no resize will run. </summary>
        public bool Desqueezing;

        /// <summary> Whether the mod-2 pad runs. Not to be confused with <see cref="Border"/>, the
        /// other pad in the chain: this one sits *above* the scale and exists to stop an odd source
        /// reaching an encoder that will not take one, where the bars go on last of all. </summary>
        public bool Padding;

        /// <summary>
        /// The black bars added to reach a target aspect ratio, already resolved against the frame
        /// the resize leaves. Never null; <see cref="BorderPad.Runs"/> is false where none are added.
        /// <para/>
        /// Last of the geometry, so <see cref="Encoded"/> is its output rather than the resize's -
        /// which is what the tile count is worked out from, a pillarboxed 4:3 capture being 1920
        /// pixels across where the picture in it is 1440.
        /// </summary>
        public BorderPad Border = BorderPad.None(Size.Empty);

        /// <summary>
        /// Whether the geometry above - the crop, the mod-2 pad, the resize or de-squeeze, and the
        /// borders - is rendered by the tone-map pass in front of av1an instead of by the per-chunk
        /// filter chain. Set by Av1an.Run exactly when that pass runs and no per-chunk deinterlacer
        /// sits ahead of the geometry (a deinterlacer must see whole fields, and the pass runs first).
        /// <para/>
        /// What it buys is the intermediate's size: written at the source's frame it carries pixels
        /// the encoder never sees - a 4K film scaled to 1080p costs four times the disk it needs to,
        /// reported at tens of gigabytes for a five-minute test clip. The frames that come out are the
        /// same either way: the pass renders the exact filters, in the exact order, that the per-chunk
        /// chain would have run on its output.
        /// </summary>
        public bool GeometryInPass;

        /// <summary> Those geometry filters as one chain for the pass to append, "" where
        /// <see cref="GeometryInPass"/> is off or nothing changes the frame. Built once, beside the
        /// per-chunk chain, so the two cannot disagree about what runs where. </summary>
        public string PassGeometryFilters = "";

        /// <summary>
        /// The frame rate filter the chain carries, or "" where the encode keeps the source's rate.
        /// <para/>
        /// Not geometry, but settled in the same pass and for the same reason: av1an has to be told
        /// about it before the command is built, because a filter that writes a different number of
        /// frames than it read is the one thing av1an refuses outright. See where this is read.
        /// </summary>
        public string FpsFilter = "";

        /// <summary> Whether the encode writes a different number of frames than the source has. </summary>
        public bool ResamplesFrameRate { get { return FpsFilter.Length > 0; } }

        /// <summary> Whether anything in front of the encoder changes the frame's size. </summary>
        public bool ChangesSize { get { return !Source.IsEmpty && !Encoded.IsEmpty && Encoded != Source; } }
    }
}
