using Nmkoder.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nmkoder.Data
{
    public class VideoColorData
    {
        public int ColorTransfer { get; set; } = 2;
        public int ColorMatrixCoeffs { get; set; } = 2;
        public int ColorPrimaries { get; set; } = 2;
        public int ColorRange { get; set; } = 0;
        public string RedX { get; set; } = "";
        public string RedY { get; set; } = "";
        public string GreenX { get; set; } = "";
        public string GreenY { get; set; } = "";
        public string BlueX { get; set; } = "";
        public string BlueY { get; set; } = "";
        public string WhiteX { get; set; } = "";
        public string WhiteY { get; set; } = "";
        public string LumaMin { get; set; } = "";
        public string LumaMax { get; set; } = "";
        public string MaxCll { get; set; } = "";
        public string MaxFall { get; set; } = "";

        /// <summary>
        /// The Dolby Vision profile off the stream's own configuration record, or 0 for a file carrying
        /// no Dolby Vision at all - which is every ordinary HDR10 file, so 0 is the answer this asks for
        /// most of the time.
        /// <para/>
        /// It is read from a probe of its own rather than out of the frame read beside it, for the reason
        /// <see cref="ColorDataUtils.ReadDolbyVision"/> gives: the configuration record is stream side
        /// data, which <c>-show_frames</c> does not print, and the obvious repair of adding
        /// <c>-show_streams</c> to that command is the one thing that must not be done to it.
        /// </summary>
        public int DvProfile { get; set; } = 0;

        /// <summary>
        /// Which ordinary signal this file's base layer is readable as on its own, off the same record:
        /// 0 none, 1 HDR10, 2 SDR, 4 HLG. -1 where the file states nothing.
        /// <para/>
        /// **This is the field that matters rather than the profile number**, because it is the direct
        /// statement of the question anything downstream actually has: can a decoder that ignores the
        /// RPU show this picture. Profile 8.1 says 1 and profile 5 says 0, and it is 0 that
        /// <see cref="ColorDataUtils.HasUnusableBaseLayer"/> refuses a CPU tone map over.
        /// </summary>
        public int DvBlCompatId { get; set; } = -1;

        public override string ToString()
        {
            List<string> lines = new List<string>();

            try
            {
                lines.Add($"Color transfer: {ColorTransfer} ({ColorDataUtils.GetColorTransferName(ColorTransfer)})");
                lines.Add($"Colour matrix coefficients: {ColorMatrixCoeffs} ({ColorDataUtils.GetColorMatrixCoeffsName(ColorMatrixCoeffs)})");
                lines.Add($"Colour primaries: {ColorPrimaries} ({ColorDataUtils.GetColorPrimariesName(ColorPrimaries)})");
                lines.Add($"Colour range: {ColorRange} ({ColorDataUtils.GetColorRangeName(ColorRange)})");
                if (!string.IsNullOrWhiteSpace(RedX) && !string.IsNullOrWhiteSpace(RedY)) lines.Add($"Red color coordinates X/Y: {RedX}/{RedY}");
                if (!string.IsNullOrWhiteSpace(GreenX) && !string.IsNullOrWhiteSpace(GreenY)) lines.Add($"Green color coordinates X/Y: {GreenX}/{GreenY}");
                if (!string.IsNullOrWhiteSpace(BlueX) && !string.IsNullOrWhiteSpace(BlueY)) lines.Add($"Blue color coordinates X/Y: {BlueX}/{BlueY}");
                if (!string.IsNullOrWhiteSpace(WhiteX) && !string.IsNullOrWhiteSpace(WhiteY)) lines.Add($"White color coordinates X/Y: {WhiteX}/{WhiteY}");
                if (!string.IsNullOrWhiteSpace(LumaMin)) lines.Add($"Minimum luminance: {LumaMin}");
                if (!string.IsNullOrWhiteSpace(LumaMax)) lines.Add($"Maximum luminance: {LumaMax}");
                if (!string.IsNullOrWhiteSpace(MaxCll)) lines.Add($"Maximum Content Light Level: {MaxCll}");
                if (!string.IsNullOrWhiteSpace(MaxFall)) lines.Add($"Maximum Frame-Average Light Level: {MaxFall}");
                if (DvProfile > 0) lines.Add($"Dolby Vision: profile {ColorDataUtils.DescribeDolbyVisionProfile(this)}");
            }
            catch { }

            return string.Join("\n", lines);
        }
    }
}
