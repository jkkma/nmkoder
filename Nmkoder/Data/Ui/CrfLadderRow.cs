using Nmkoder.UI.Tasks;
using Nmkoder.Utils;
using System;

namespace Nmkoder.Data.Ui
{
    /// <summary> One rung of a finished ladder, formatted for the results grid. </summary>
    public class CrfLadderRow
    {
        public string Crf { get; }
        public string Bitrate { get; }
        public string PerMinute { get; }
        public string WholeFile { get; }
        /// <summary> The projection as a share of the source file, which is the sentence most people
        /// are really after - "about a third of the original" lands where a byte count does not. Empty
        /// where the source's size is unknown, rather than showing a percentage of nothing. </summary>
        public string VsSource { get; }
        public string Score { get; }
        public string EncodeTime { get; }

        public CrfLadderRow(CrfLadder.Rung rung, CrfLadder.Result result, long sourceBytes)
        {
            long projected = rung.ProjectedBytes(result.SourceMs);

            Crf = rung.Crf.ToString();
            Bitrate = FormatUtils.Bitrate(rung.Kbps);
            PerMinute = FormatUtils.Bytes(rung.BytesPerMinute);
            WholeFile = CrfLadder.DescribeProjection(projected);
            VsSource = sourceBytes > 0 ? $"{(projected * 100d / sourceBytes).ToString("0.#")}%" : "";
            Score = rung.Scored ? UtilCrfLadder.FormatScore(rung.Score) : "-";
            EncodeTime = FormatUtils.Time(rung.EncodeTime, allowMs: false);
        }
    }
}
