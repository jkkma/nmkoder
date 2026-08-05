using Nmkoder.IO;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Nmkoder.UI
{
    class ResetSettingsOnNewFile
    {
        public static bool ResetTrim { get; set; }
        public static bool ResetFpsResample { get; set; }
        public static bool ResetResize { get; set; }
        public static bool ResetCrop { get; set; }
        public static bool ResetBorders { get; set; }
        public static bool ResetDeinterlace { get; set; }
        public static bool ResetToneMap { get; set; }
        public static bool ResetCustomFilters { get; set; }

        public static Dictionary<string, string> NiceNames
        {
            get
            {
                Dictionary<string, string> d = new Dictionary<string, string>();
                d.Add(nameof(ResetTrim), "Trim");
                d.Add(nameof(ResetFpsResample), "Frame Rate");
                d.Add(nameof(ResetResize), "Resize");
                d.Add(nameof(ResetCrop), "Crop");
                d.Add(nameof(ResetBorders), "Borders");
                d.Add(nameof(ResetDeinterlace), "Deinterlace");
                d.Add(nameof(ResetToneMap), "Tone Mapping");
                d.Add(nameof(ResetCustomFilters), "Custom Filters");
                return d;
            }
        }

        public static void ResetAll()
        {
            ResetTrim = false;
            ResetFpsResample = false;
            ResetResize = false;
            ResetCrop = false;
            ResetBorders = false;
            ResetDeinterlace = false;
            ResetToneMap = false;
            ResetCustomFilters = false;
        }

        public static string GetString()
        {
            List<string> list = new List<string>();

            if (ResetTrim) list.Add(NiceNames[nameof(ResetTrim)]);
            if (ResetFpsResample) list.Add(NiceNames[nameof(ResetFpsResample)]);
            if (ResetResize) list.Add(NiceNames[nameof(ResetResize)]);
            if (ResetCrop) list.Add(NiceNames[nameof(ResetCrop)]);
            if (ResetBorders) list.Add(NiceNames[nameof(ResetBorders)]);
            if (ResetDeinterlace) list.Add(NiceNames[nameof(ResetDeinterlace)]);
            if (ResetToneMap) list.Add(NiceNames[nameof(ResetToneMap)]);
            if (ResetCustomFilters) list.Add(NiceNames[nameof(ResetCustomFilters)]);

            if (list.Count > 0)
                return string.Join(", ", list.Select(x => ShortenName(x)));
            else
                return "None";
        }

        public static string ShortenName(string s)
        {
            return s.Replace("Custom Filters", "Filters").Replace("Frame Rate", "FPS");
        }

        public static void Save()
        {
            List<string> list = new List<string>();

            foreach (var prop in typeof(ResetSettingsOnNewFile).GetProperties())
            {
                if (!prop.Name.StartsWith("Reset"))
                    continue;

                list.Add($"{prop.Name}={(bool)prop.GetValue(null, null)}");
            }

            Config.Set(Config.Key.ResetSettingsList, string.Join(",", list));
        }

        /// <summary> The ones that start out on. Every other setting here starts off: these four are
        /// the ones whose value is about the file that was just replaced rather than about how the
        /// user likes to encode, so carrying them to the next file is always wrong. </summary>
        private static readonly string[] onByDefault = { nameof(ResetTrim), nameof(ResetCrop), nameof(ResetDeinterlace), nameof(ResetToneMap) };

        /// <summary>
        /// Restores the list, defaulting anything it does not name.
        /// <para/>
        /// Which is a first run and an older list alike, and deliberately does not distinguish them: a
        /// setting added after a list was written is missing from that list in exactly the way it is
        /// missing on a first run, so defaulting only on a first run is how a new default reaches
        /// nobody who already has the app - everyone it was written for included. A setting the user
        /// turned off is saved as False, which names it, which is what keeps it off.
        /// </summary>
        public static void Load()
        {
            // Asked before reading, as ConfigParser's own Restore helpers do: the Get helpers write a
            // default for any key that is missing, so reading first would create the entry either way.
            string data = Config.cachedValues.ContainsKey(Config.Key.ResetSettingsList.ToString())
                ? Config.Get(Config.Key.ResetSettingsList) : "";
            HashSet<string> named = new HashSet<string>();

            foreach (string prop in data.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string propName = prop.Split('=')[0];
                    bool propVal = bool.Parse(prop.Split('=')[1]);
                    typeof(ResetSettingsOnNewFile).GetProperty(propName).SetValue(null, propVal);
                    named.Add(propName);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to set saved ResetSettingsOnNewFile property: {ex.Message}", true);
                }
            }

            string[] missing = onByDefault.Where(x => !named.Contains(x)).ToArray();

            foreach (string propName in missing)
                typeof(ResetSettingsOnNewFile).GetProperty(propName).SetValue(null, true);

            // Written straight back, so the list on disk names every setting there is and this
            // defaults each of them exactly once - turning one off again then sticks.
            if (missing.Length > 0)
                Save();
        }
    }
}
