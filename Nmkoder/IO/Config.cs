using Newtonsoft.Json;
using Nmkoder;
using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.UI.Tasks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace Nmkoder.IO
{
    class Config
    {
        private static string configPath;
        public static Dictionary<string, string> cachedValues = new Dictionary<string, string>();

        /// <summary>
        /// Keys earlier versions wrote for settings that no longer exist. A config file written by
        /// one of those keeps carrying them otherwise, since nothing reads them to overwrite them.
        /// </summary>
        private static readonly string[] retiredKeys = { "TaskModeBox", "taskMode" };

        public static void Init()
        {
            configPath = Path.Combine(Paths.GetDataPath(), "config.json");
            IoUtils.CreateFileIfNotExists(configPath);
            Reload();
            DropRetiredKeys();
        }

        private static void DropRetiredKeys()
        {
            List<string> dropped = new List<string>();

            foreach (string key in retiredKeys)
            {
                if (cachedValues.Remove(key))
                    dropped.Add(key);
            }

            if (dropped.Count < 1)
                return;

            Logger.Log($"Config: Dropped retired {(dropped.Count == 1 ? "entry" : "entries")}: {string.Join(", ", dropped)}", true);
            WriteConfig();
        }

        public static async Task Reset(int retries = 3)
        {
            try
            {
                File.Delete(configPath);
                await Task.Delay(100);
                cachedValues.Clear();
                await Task.Delay(100);
            }
            catch (Exception e)
            {
                retries -= 1;
                Logger.Log($"Failed to reset config: {e.Message}. Retrying ({retries} attempts left).", true);

                if (retries <= 0)
                    return;

                await Task.Delay(500);
                await Reset(retries);
            }
        }

        public static void Set(Key key, string value)
        {
            Set(key.ToString(), value);
        }

        public static void Set(string str, string value)
        {
            // Setting a key to what it already holds is the common case - the UI saves a whole group of
            // controls whenever any one of them changes - and rewriting the file for it is pure cost.
            if (cachedValues.TryGetValue(str, out string current) && current == value)
                return;

            cachedValues[str] = value;
            Save();
        }

        public static void Set(Dictionary<string, string> keyValuePairs)
        {
            bool changed = false;

            foreach (KeyValuePair<string, string> entry in keyValuePairs)
            {
                if (cachedValues.TryGetValue(entry.Key, out string current) && current == entry.Value)
                    continue;

                cachedValues[entry.Key] = entry.Value;
                changed = true;
            }

            if (changed)
                Save();
        }

        private static int batchDepth;
        private static bool batchDirty;

        /// <summary>
        /// Coalesces every write made inside it into one. The UI saves controls a group at a time, so
        /// without this a single spinner tick rewrote the file once per control in the group.
        /// </summary>
        public static IDisposable Batch()
        {
            batchDepth++;
            return new BatchScope();
        }

        private sealed class BatchScope : IDisposable
        {
            private bool disposed;

            public void Dispose()
            {
                if (disposed)
                    return;

                disposed = true;
                batchDepth--;

                if (batchDepth > 0 || !batchDirty)
                    return;

                batchDirty = false;
                WriteConfig();
            }
        }

        private static void Save()
        {
            if (batchDepth > 0)
            {
                batchDirty = true;
                return;
            }

            WriteConfig();
        }

        private static async Task WriteConfig(int tries = 5)
        {
            try
            {
                File.WriteAllText(configPath, JsonConvert.SerializeObject(new SortedDictionary<string, string>(cachedValues), Formatting.Indented));
            }
            catch (Exception e)
            {
                if (tries > 0)
                {
                    Logger.Log($"Failed to write config. Retrying ({tries} tries left)", true);
                    await Task.Delay(200);
                    await WriteConfig(tries - 1);
                    return;
                }

                // Out of retries, so say so where it can be seen. Giving up quietly is how a config in a
                // folder that denies writes looked like it was saving settings that were never kept.
                Logger.Log($"Failed to save settings to '{configPath}': {e.Message}");
            }
        }

        /// <summary>
        /// Reads the file into <see cref="cachedValues"/>. Only done at startup - from then on the cache
        /// is what the app reads and what gets written back, so saving a key costs no read.
        /// </summary>
        private static void Reload()
        {
            try
            {
                Dictionary<string, string> newDict = new Dictionary<string, string>();
                Dictionary<string, string> deserializedConfig = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(configPath));

                if (deserializedConfig == null)
                    deserializedConfig = new Dictionary<string, string>();

                foreach (KeyValuePair<string, string> entry in deserializedConfig)
                    newDict.Add(entry.Key, entry.Value);

                cachedValues = newDict; // Use temp dict and only copy it back if no exception was thrown
            }
            catch (Exception e)
            {
                Logger.Log($"Failed to reload config! {e.Message}", true);
            }
        }

        // Get using fixed key
        public static string Get(Key key, string defaultVal)
        {
            WriteIfDoesntExist(key.ToString(), defaultVal);
            return Get(key);
        }

        // Get using string
        public static string Get(string key, string defaultVal)
        {
            WriteIfDoesntExist(key, defaultVal);
            return Get(key);
        }

        public static string Get(Key key, Type type = Type.String)
        {
            return Get(key.ToString(), type);
        }

        public static string Get(string key, Type type = Type.String)
        {
            string keyStr = key.ToString();

            try
            {
                if (cachedValues.ContainsKey(keyStr))
                    return cachedValues[keyStr];

                return WriteDefaultValIfExists(key.ToString(), type);
            }
            catch (Exception e)
            {
                Logger.Log($"Failed to get {keyStr.Wrap()} from config! {e.Message}", true);
            }

            return null;
        }

        #region Get Bool

        public static bool GetBool(Key key)
        {
            return Get(key, Type.Bool).GetBool();
        }

        public static bool GetBool(Key key, bool defaultVal = false)
        {
            WriteIfDoesntExist(key.ToString(), (defaultVal ? "True" : "False"));
            return Get(key, Type.Bool).GetBool();
        }

        public static bool GetBool(string key)
        {
            return Get(key, Type.Bool).GetBool();
        }

        public static bool GetBool(string key, bool defaultVal)
        {
            WriteIfDoesntExist(key.ToString(), (defaultVal ? "True" : "False"));
            return bool.Parse(Get(key, Type.Bool));
        }

        #endregion

        #region Get Int

        public static int GetInt(Key key)
        {
            return Get(key, Type.Int).GetInt();
        }

        public static int GetInt(Key key, int defaultVal)
        {
            WriteIfDoesntExist(key.ToString(), defaultVal.ToString());
            return GetInt(key);
        }

        public static int GetInt(string key)
        {
            var x = Get(key, Type.Int);
            return x.GetInt();
        }

        public static int GetInt(string key, int defaultVal)
        {
            WriteIfDoesntExist(key.ToString(), defaultVal.ToString());
            return GetInt(key);
        }

        #endregion

        #region Get Float

        public static float GetFloat(Key key)
        {
            return float.Parse(Get(key, Type.Float), CultureInfo.InvariantCulture);
        }

        public static float GetFloat(Key key, float defaultVal)
        {
            WriteIfDoesntExist(key.ToString(), defaultVal.ToStringDot());
            return float.Parse(Get(key, Type.Float), CultureInfo.InvariantCulture);
        }

        public static float GetFloat(string key)
        {
            try
            {
                return float.Parse(Get(key, Type.Float), CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0f;
            }
        }

        public static float GetFloat(string key, float defaultVal)
        {
            WriteIfDoesntExist(key.ToString(), defaultVal.ToStringDot());
            return float.Parse(Get(key, Type.Float), CultureInfo.InvariantCulture);
        }

        public static string GetFloatString (Key key)
        {
            return Get(key, Type.Float).Replace(",", ".");
        }

        public static string GetFloatString(string key)
        {
            return Get(key, Type.Float).Replace(",", ".");
        }

        #endregion

        static void WriteIfDoesntExist (string key, string val)
        {
            if (cachedValues.ContainsKey(key.ToString()))
                return;

            Set(key, val);
        }

        public enum Type { String, Int, Float, Bool }
        private static string WriteDefaultValIfExists(string keyStr, Type type)
        {
            Key key;

            try
            {
                key = (Key)Enum.Parse(typeof(Key), keyStr);
            }
            catch
            {
                return WriteDefault(keyStr, "");
            }

            if (key == Key.Av1anOptsChunkModeBox)       return WriteDefault(key, "1");
            // Scene change detection, not "None" - index 0 would otherwise become the default purely
            // because an unset key reads as zero, which is not the mode anyone wants to encode in.
            if (key == Key.Av1anOptsSplitModeBox)       return WriteDefault(key, "1");
            // Taken from the enum rather than written out, so reordering the codecs cannot leave
            // this pointing at a different one. Opus defaults to 128 kbps for stereo on its own.
            if (key == Key.Av1anAudCodecBox)            return WriteDefault(key, ((int)CodecUtils.AudioCodec.Opus).ToString());
            if (key == Key.DefaultKeyIntSecs)           return WriteDefault(key, "10");
            // Both from one call, because it is their product that has to fit the machine - see
            // Av1an.GetDefaultThreadPlan. Neither may be given a literal here.
            if (key == Key.Av1anOptsWorkerCountUpDown)  return WriteDefault(key, $"{Av1an.GetDefaultThreadPlan().Workers}");
            if (key == Key.Av1anThreadsUpDown)          return WriteDefault(key, $"{Av1an.GetDefaultThreadPlan().Threads}");
            if (key == Key.mp4Faststart)                return WriteDefault(key, "True");
            if (key == Key.metaMode)                    return WriteDefault(key, "1");

            if (type == Type.Int || type == Type.Float) return WriteDefault(key, "0");     // Write default int/float (0)
            if (type == Type.Bool)                      return WriteDefault(key, "False");     // Write default bool (False)

            return WriteDefault(key, "");
        }

        private static string WriteDefault(Key key, string def)
        {
            Set(key, def);
            return def;
        }

        private static string WriteDefault(string key, string def)
        {
            Set(key, def);
            return def;
        }

        /// <summary>
        /// Entries that back a UI control must be named exactly like that control - ConfigParser
        /// stores and looks up those values by control name, so a mismatch silently loses the default.
        /// </summary>
        public enum Key
        {
            AutoCropSamples,
            Av1anCmdVisible,
            Av1anAudCodecBox,
            Av1anCustomFilters,
            Av1anEncoderArgs,
            Av1anThreadsUpDown,
            Av1anOptsChunkModeBox,
            Av1anOptsSplitModeBox,
            Av1anOptsWorkerCountUpDown,
            CmdDebugMode,
            DefaultKeyIntSecs,
            DefaultOutputDir,
            EncCustomFilters,
            EncResize,
            LastInputDir,
            LastOutputDir,
            LogHeight,
            MainTab,
            metaMode,
            mp4Faststart,
            RecentFiles,
            ResetSettingsList,
            UseZeroIndexedStreams,
            UtilDeinterlaceDoubleRate,
            UtilDeinterlaceMode,
            UtilDeinterlacePreset,
            WindowGeometry
        }
    }
}
