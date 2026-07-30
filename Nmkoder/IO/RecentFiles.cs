using Newtonsoft.Json;
using Nmkoder.Extensions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Nmkoder.IO
{
    /// <summary>
    /// The paths most recently loaded into the file list, newest first. Folders belong in here as
    /// well as files - an image sequence is loaded by its folder, and is exactly as tedious to go
    /// find again.
    /// </summary>
    class RecentFiles
    {
        /// <summary> Deliberately short: the list is a dropdown, and one nobody can take in at a
        /// glance saves no time over browsing for the file again. </summary>
        private const int maxEntries = 15;

        /// <summary>
        /// Two paths differing only in case are the same file on Windows and macOS and two
        /// different files on Linux, so the same list must not be deduplicated the same way on all
        /// three.
        /// </summary>
        private static StringComparison PathComparison => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        /// <summary>
        /// The list as stored. Deliberately does not check whether any of it is still there: this
        /// is called on the UI thread every time a file is imported, and one entry on a share that
        /// is no longer mounted leaves File.Exists blocking for as long as the mount takes to give
        /// up. Entries are dropped by <see cref="Remove"/> when one turns out to be gone, and only
        /// then - an unplugged external drive is exactly when the path is worth most, so a launch
        /// without it must not be what clears the list.
        /// </summary>
        public static List<string> Get()
        {
            return Load();
        }

        public static void Remove(string path)
        {
            List<string> list = Load();

            if (list.RemoveAll(x => Same(x, path)) > 0)
                Save(list);
        }

        public static void Add(IEnumerable<string> paths)
        {
            List<string> list = Load();

            // Reversed so that the first of a batch ends up at the top of the list rather than the
            // last, and a path loaded again moves back up instead of appearing twice.
            foreach (string path in paths.Where(x => x.IsNotEmpty()).Reverse())
            {
                list.RemoveAll(x => Same(x, path));
                list.Insert(0, path);
            }

            Save(list.Take(maxEntries).ToList());
        }

        public static void Clear()
        {
            Save(new List<string>());
        }

        public static bool Exists(string path)
        {
            return path.IsNotEmpty() && (File.Exists(path) || Directory.Exists(path));
        }

        private static bool Same(string a, string b)
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), PathComparison);
        }

        /// <summary>
        /// Stored as a JSON array rather than a joined string: every separator worth using is a
        /// legal character in a path on one of the platforms this ships on.
        /// </summary>
        private static List<string> Load()
        {
            string json = Config.Get(Config.Key.RecentFiles);

            if (json.IsEmpty())
                return new List<string>();

            try
            {
                return JsonConvert.DeserializeObject<List<string>>(json)?.Where(x => x.IsNotEmpty()).ToList() ?? new List<string>();
            }
            catch (Exception e)
            {
                Logger.Log($"Failed to read the recent files list: {e.Message}", true);
                return new List<string>();
            }
        }

        private static void Save(List<string> list)
        {
            Config.Set(Config.Key.RecentFiles, JsonConvert.SerializeObject(list));
        }
    }
}
