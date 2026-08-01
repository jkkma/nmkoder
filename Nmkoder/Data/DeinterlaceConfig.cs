using Nmkoder.Extensions;

namespace Nmkoder.Data
{
    /// <summary> Whether a video stream's frames hold two interlaced fields, and which of them comes first. </summary>
    public enum FieldOrder { Unknown, Progressive, TopFieldFirst, BottomFieldFirst }

    /// <summary>
    /// What the Deinterlace dropdown offers. Saved by index, so entries may be appended but never
    /// reordered - a saved index would otherwise start meaning a different mode.
    /// </summary>
    public enum DeinterlaceMode { Automatic, Disabled, Qtgmc, Bwdif, Yadif }

    /// <summary>
    /// What will actually run, once Automatic has been resolved against the source and QTGMC's
    /// availability has been established.
    /// </summary>
    public enum DeinterlaceEngine { None, Qtgmc, Bwdif, Yadif }

    /// <summary>
    /// What is known about a file's scan type. Filled by <see cref="Media.InterlaceDetect"/>, once
    /// per file, and kept on the <see cref="MediaFile"/> so a batch does not re-scan.
    /// </summary>
    public class InterlaceInfo
    {
        public FieldOrder Order = FieldOrder.Unknown;

        /// <summary> Film carried in an interlaced stream by repeating fields (3:2 pulldown). Only
        /// ever set where the frame scan actually ran - the container flag cannot show it. </summary>
        public bool Telecined;

        /// <summary> Where the verdict came from, for the log line that announces it. </summary>
        public string Evidence = "";

        /// <summary> Whether frames were actually decoded and measured, as opposed to the container's
        /// own flag being taken at its word. </summary>
        public bool Scanned;

        public bool Interlaced { get { return Order == FieldOrder.TopFieldFirst || Order == FieldOrder.BottomFieldFirst; } }

        /// <summary>
        /// Whether the top field is the one shown first. Unknown counts as top: it is the common case
        /// by a wide margin, and a deinterlacer told the wrong order does not fail, it interpolates
        /// the fields in the wrong sequence and the motion judders - which is worth defaulting for
        /// rather than refusing over.
        /// </summary>
        public bool TopFieldFirst { get { return Order != FieldOrder.BottomFieldFirst; } }

        public string DescribeOrder()
        {
            switch (Order)
            {
                case FieldOrder.TopFieldFirst: return "interlaced, top field first";
                case FieldOrder.BottomFieldFirst: return "interlaced, bottom field first";
                case FieldOrder.Progressive: return "progressive";
                default: return "of unknown scan type";
            }
        }
    }

    /// <summary> What a tab's Deinterlace controls are asking for, before any of it is checked. </summary>
    public class DeinterlaceRequest
    {
        public DeinterlaceMode Mode = DeinterlaceMode.Automatic;

        /// <summary> A QTGMC preset name, as havsfunc spells them ("Medium", "Very Slow", ...). </summary>
        public string QtgmcPreset = "Medium";

        /// <summary>
        /// Whether to emit one frame per field, which is what a deinterlacer should do for genuinely
        /// interlaced video - the two fields were shot at different moments, so keeping only half of
        /// them throws away half the motion.
        /// </summary>
        public bool DoubleRate = true;

        /// <summary>
        /// Why the tab asking cannot run QTGMC at all, or "" when it can. Not the same as QTGMC being
        /// missing from the machine: this is the AV1AN tab, which drives its deinterlacing through
        /// av1an's per-chunk ffmpeg filters and has no way to put a VapourSynth script in front of them.
        /// </summary>
        public string QtgmcUnavailableHere = "";
    }

    /// <summary> The settled decision for one file: what runs, over which fields, at what rate. </summary>
    public class DeinterlacePlan
    {
        public DeinterlaceEngine Engine = DeinterlaceEngine.None;
        public bool DoubleRate;
        public bool TopFieldFirst = true;
        public string QtgmcPreset = "Medium";

        /// <summary> The file the plan was made for - QTGMC reads it directly rather than through
        /// ffmpeg, so the script has to name the same file the video map would have come from. </summary>
        public MediaFile File;

        public bool Runs { get { return Engine != DeinterlaceEngine.None; } }

        /// <summary> Whether the output carries one frame per source field, which doubles the frame
        /// rate - the fps the rest of the pipeline has to measure itself against. </summary>
        public bool DoublesFrameRate { get { return Runs && DoubleRate; } }

        /// <summary> Whether the frames arrive through a VapourSynth pipe rather than out of ffmpeg's
        /// own decoder, which changes which input the video is mapped from. </summary>
        public bool UsesPipe { get { return Engine == DeinterlaceEngine.Qtgmc; } }

        /// <summary> The ffmpeg filter that does the work, or "" for the engines that do not run in
        /// ffmpeg (QTGMC, which runs in VapourSynth, and None). </summary>
        public string GetFfmpegFilter()
        {
            // parity 0 is top field first, 1 is bottom. Named rather than left at -1 (auto), because
            // auto reads the frame's own flag and a source that carries none is then guessed at -
            // which is exactly the source this whole feature exists for.
            int parity = TopFieldFirst ? 0 : 1;

            if (Engine == DeinterlaceEngine.Bwdif)
                return $"bwdif=mode={(DoubleRate ? "send_field" : "send_frame")}:parity={parity}:deint=all";

            if (Engine == DeinterlaceEngine.Yadif)
                return $"yadif=mode={(DoubleRate ? 1 : 0)}:parity={parity}:deint=0";

            return "";
        }

        /// <summary> How the log announces what is about to happen, without the file name. </summary>
        public string Describe()
        {
            if (!Runs)
                return "not deinterlacing";

            string name = Engine == DeinterlaceEngine.Qtgmc ? $"QTGMC ({QtgmcPreset})" : Engine.ToString().ToLower();
            string fields = TopFieldFirst ? "top field first" : "bottom field first";
            string rate = DoubleRate ? "one frame per field" : "one frame per frame";
            return $"{name}, {fields}, {rate}";
        }
    }
}
