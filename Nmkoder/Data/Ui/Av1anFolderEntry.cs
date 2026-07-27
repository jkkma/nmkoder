using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.UI.Tasks;
using Nmkoder.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nmkoder.Data.Ui
{
    public class Av1anFolderEntry
    {
        public Dictionary<string, string> jsonInfo { get; } = null;
        public DirectoryInfo DirInfo { get; }
        public FileInfo InputFile { get; }
        public FileInfo[] ChunkFiles { get; }
        public string InputFilename { get; } = "";
        public string TempFolderName { get; } = "";
        public string Args { get; } = "";
        public DateTime CreationDate { get; }
        public DateTime LastRunDate { get; }
        public TimeSpan TimeSinceCreation { get;  }
        public TimeSpan TimeSinceLastRun { get; }

        public Av1anFolderEntry(string path)
        {
            DirInfo = new DirectoryInfo(path);
            jsonInfo = Av1an.LoadJson(DirInfo.Name);
            ChunkFiles = IoUtils.GetFileInfosSorted(Path.Combine(path, "encode"));

            if (jsonInfo == null) return;

            InputFilename = (jsonInfo.ContainsKey("fileName") ? jsonInfo["fileName"] : DirInfo.Name).Trunc(35);

            if (jsonInfo.ContainsKey("filePath"))
                InputFile = File.Exists(jsonInfo["filePath"]) ? new FileInfo(jsonInfo["filePath"]) : null;

            CreationDate = ParseTimestamp(jsonInfo, "creationTimestamp");
            LastRunDate = ParseTimestamp(jsonInfo, "lastRunTimestamp");

            if (jsonInfo.ContainsKey("tempFolderName"))
                TempFolderName = jsonInfo["tempFolderName"];

            if (jsonInfo.ContainsKey("args"))
                Args = jsonInfo["args"];

            TimeSinceCreation = DateTime.Now - CreationDate;
            TimeSinceLastRun = DateTime.Now - LastRunDate;
        }

        /// <summary>
        /// A unix millisecond timestamp from the info json as a date, or the epoch - which the display
        /// reads as "unknown" - for anything it cannot make one out of. A resumed encode whose original
        /// json had no creation time is deliberately written as "-1" and lands here too. Throwing
        /// instead lost the whole entry, and with it the only offer to resume that encode.
        /// </summary>
        private static DateTime ParseTimestamp(Dictionary<string, string> json, string key)
        {
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, 0);

            if (json == null || !json.ContainsKey(key) || !long.TryParse(json[key], out long ms) || ms <= 0)
                return epoch;

            try
            {
                return epoch.AddMilliseconds(ms);
            }
            catch (ArgumentOutOfRangeException)
            {
                return epoch; // Far enough out of range to fall off the calendar
            }
        }

        public override string ToString()
        {
            string created = "???";

            if (CreationDate != new DateTime(1970, 1, 1, 0, 0, 0, 0))
                created = $"{(TimeSinceCreation.TotalMinutes >= 120 ? $"{TimeSinceCreation.TotalHours.RoundToInt()}h" : $"{TimeSinceCreation.TotalMinutes.RoundToInt()}m")} ago";

            string lastRun = "???";

            if (LastRunDate != new DateTime(1970, 1, 1, 0, 0, 0, 0))
                lastRun = $"{(TimeSinceLastRun.TotalMinutes >= 120 ? $"{TimeSinceLastRun.TotalHours.RoundToInt()}h" : $"{TimeSinceLastRun.TotalMinutes.RoundToInt()}m")} ago";

            string chunks = $"{ChunkFiles.Length} Chunks - {FormatUtils.Bytes(ChunkFiles.Sum(x => x.Length))}";
            return $"{InputFilename} - {chunks} - Created: {created} - Last Run: {lastRun}";
        }
    }
}
