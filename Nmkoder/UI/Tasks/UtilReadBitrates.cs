using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nmkoder.Data;
using Nmkoder.Data.Ui;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.Media;
using Nmkoder.Utils;
using Stream = Nmkoder.Data.Streams.Stream;

namespace Nmkoder.UI.Tasks
{
    class UtilReadBitrates
    {
        public static async Task Run()
        {
            Program.MainWin.SetWorking(true);
            Logger.Log("Analyzing streams... This can take a few minutes on slower hard drives.");

            int totalKbpsVid = 0;
            int totalKbpsAud = 0;
            int totalKbpsSub = 0;

            long totalBytesVid = 0;
            long totalBytesAud = 0;
            long totalBytesSub = 0;

            foreach (StreamListEntry item in TrackList.CheckedItems.ToList())
            {
                // Stop rather than skip. Nothing was being spent either way - the continue did skip
                // the probe - but a loop that keeps running to the end of a long track list after the
                // user cancelled leaves the window working for no reason.
                if (RunTask.canceled)
                    break;

                Stream s = item.Stream;
                FfmpegUtils.StreamSizeInfo info = await FfmpegUtils.GetStreamSizeBytes(TrackList.current.File.ImportPath, s.Index);
                string percent = FormatUtils.RatioFloat(info.Bytes, TrackList.current.File.Size).ToString("0.0");
                string br = info.Kbps > 1 ? FormatUtils.Bitrate(info.Kbps.RoundToInt()) : info.Kbps.ToString("0.0") + " kbps";
                Logger.Log($"Stream #{s.Index} ({s.Type}) - Bitrate: {br} - Size: {FormatUtils.Bytes(info.Bytes)} ({percent}%)");

                if (s.Type == Stream.StreamType.Video)
                {
                    totalKbpsVid += info.Kbps.RoundToInt();
                    totalBytesVid += info.Bytes;
                }

                if (s.Type == Stream.StreamType.Audio)
                {
                    totalKbpsAud += info.Kbps.RoundToInt();
                    totalBytesAud += info.Bytes;
                }
                    
                if (s.Type == Stream.StreamType.Subtitle)
                {
                    totalKbpsSub += info.Kbps.RoundToInt();
                    totalBytesSub += info.Bytes;
                }
            }

            // Totals over a list that was cut short are not this file's totals, and reading like they
            // are is worse than not printing them.
            if (RunTask.canceled)
            {
                Program.MainWin.SetWorking(false);
                return;
            }

            string totalPercentVid = FormatUtils.RatioFloat(totalBytesVid, TrackList.current.File.Size).ToString("0.0");
            string totalPercentAud = FormatUtils.RatioFloat(totalBytesAud, TrackList.current.File.Size).ToString("0.0");
            string totalPercentSub = FormatUtils.RatioFloat(totalBytesSub, TrackList.current.File.Size).ToString("0.0");

            Logger.Log($"Total Video Bitrate: {FormatUtils.Bitrate(totalKbpsVid)} - Total Video Size: {FormatUtils.Bytes(totalBytesVid)} ({totalPercentVid}%)");
            Logger.Log($"Total Audio Bitrate: {FormatUtils.Bitrate(totalKbpsAud)} - Total Audio Size: {FormatUtils.Bytes(totalBytesAud)} ({totalPercentAud}%)");
            Logger.Log($"Total Subtitle Bitrate: {FormatUtils.Bitrate(totalKbpsSub)} - Total Subtitle Size: {FormatUtils.Bytes(totalBytesSub)} ({totalPercentSub}%)");

            Program.MainWin.SetWorking(false);
        }
    }
}
