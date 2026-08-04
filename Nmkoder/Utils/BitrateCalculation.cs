using Nmkoder.Data;
using Nmkoder.Data.Codecs;
using Nmkoder.Data.Streams;
using Nmkoder.Data.Ui;
using Nmkoder.Extensions;
using Nmkoder.Views;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.UI;
using Nmkoder.UI.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nmkoder.Utils
{
    class BitrateCalculation
    {
        public static int GetTargetSizeKbps(IEncoder aCodec, bool silent = false)
        {
            MainWindow form = Program.MainWin;

            string audArgs = CodecUtils.GetAudioArgsForEachStream(TrackList.current.File, form.EncAudQualUpDown.Value.AsInt(), form.EncAudChannelsBox.GetText().Split(' ')[0].GetInt());

            List<int> audioBitrates = audArgs.Split("-b:a:").Where(x => x.Contains("k ")).Select(x => x.Split(' ')[1].GetInt()).ToList(); //aud ? ((int)form.encAudQualUpDown.Value * 1024) * audioTracks : 0;
            int audioBps = audioBitrates.Select(x => x * 1024).Sum();

            double durationSecs = GetEncodedDurationMs(TrackList.current.File) / (double)1000;
            float targetMbytes = form.EncVidQualityBox.Value.AsFloat();
            long targetBits = (long)Math.Round(targetMbytes * 8 * 1024 * 1024);

            if (durationSecs <= 0d)
            {
                RunTask.Cancel($"Target Filesize Mode:\n\nThe length of '{TrackList.current.File.Name}' is not known, so there is no way to " +
                    $"work out what bitrate reaches {targetMbytes} megabytes.\n\nUse CRF or Target Bitrate for this file.");
                return -1;
            }

            int targetVidBitrate = (int)Math.Floor(targetBits / durationSecs) - audioBps; // Round down since undershooting is better than overshooting here

            string brTotal = (((float)targetVidBitrate + audioBps) / 1024).ToString("0.0");
            string brVid = ((float)targetVidBitrate / 1024).ToString("0");
            string brAud = ((float)audioBps / 1024).ToString("0");

            if (targetVidBitrate < 0)
            {
                RunTask.Cancel($"Target Filesize Mode:\n\nNo bitrate left for video ({brVid}k) after {audioBitrates.Count} audio tracks ({string.Join(" + ", audioBitrates.Select(x => $"{x}k"))} = {brAud}k)." +
                    $"\n\nUse a lower audio bitrate or fewer/no audio tracks.");
                return -1;
            }

            if (!silent)
                Logger.Log($"Target Filesize Mode: Using bitrate of {brTotal} kbps ({brVid}k Video, {brAud}k Audio) over {durationSecs.ToString("0.0")} seconds to hit {targetMbytes} megabytes.");

            return ((float)targetVidBitrate / 1024).RoundToInt();
        }

        /// <summary>
        /// How much of the file this encode actually writes, in milliseconds: the whole of it, or the
        /// section the Trim leaves.
        /// <para/>
        /// The file's own duration was used regardless, which is the one number that is wrong whenever
        /// a trim is set - and wrong by however much of the file was cut away, not by a rounding. A 100
        /// MB target on a two hour source trimmed to five minutes spent the two hour bitrate on five
        /// minutes of video and wrote a file twenty-odd times the size that was asked for. The failure
        /// is silent both ways round: nothing here or in ffmpeg compares the result against the target.
        /// </summary>
        private static long GetEncodedDurationMs(MediaFile file)
        {
            TrimSettings trim = QuickConvertUi.CurrentTrim;

            if (trim == null || trim.IsUnset)
                return file.DurationMs;

            // Narrowed to the file the same way the run itself narrows it, so the two cannot disagree
            // about what is being encoded. A section lying entirely past the end of the file has already
            // stopped the run by this point - QuickConvert.Run checks it before it builds anything - so
            // the fallback here is for a file whose duration is unknown rather than for a bad trim.
            if (UtilCut.ResolveSection(trim, file, out long startMs, out long endMs).IsNotEmpty())
                return file.DurationMs;

            return Math.Max(0, endMs - startMs);
        }
    }
}
