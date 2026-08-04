using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.OS;
using Nmkoder.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nmkoder.Data
{
    class Paths
    {
        public static string sessionTimestamp;

        public static void Init ()
        {
            var n = DateTime.Now;
            sessionTimestamp = $"{n.Year}-{n.Month}-{n.Day}-{n.Hour}-{n.Minute}-{n.Second}-{n.Millisecond}";
        }

        public static string GetExe()
        {
            // Assembly.CodeBase is gone on modern .NET; the process path is the right answer here
            // and also works for single-file publishes.
            return Environment.ProcessPath ?? System.Reflection.Assembly.GetEntryAssembly()?.Location ?? AppContext.BaseDirectory;
        }

        /// <summary>
        /// The folder the exe actually sits in. Deliberately not AppContext.BaseDirectory: under
        /// IncludeAllContentForSelfExtract - which the Windows build has to set, because the App
        /// SDK's single-file validation refuses to build without it - that points at the bundle's
        /// extraction folder under temp rather than at the exe. Everything hanging off this would
        /// follow it there: bin/ (so no bundled ffmpeg, and every file scans as having no streams),
        /// plus settings and logs. The process path is the exe under every bundling mode.
        /// </summary>
        public static string GetExeDir()
        {
            // Not GetExe(), whose last fallback is already a directory - taking the directory name
            // of that would climb a level.
            string exe = Environment.ProcessPath ?? System.Reflection.Assembly.GetEntryAssembly()?.Location;
            string dir = exe.IsEmpty() ? AppContext.BaseDirectory : Path.GetDirectoryName(exe);
            return (dir.IsEmpty() ? AppContext.BaseDirectory : dir).TrimEnd(Path.DirectorySeparatorChar);
        }

        public static string GetWorkingDir()
        {
            return Environment.CurrentDirectory;
        }

        private static string _rootDir;

        /// <summary>
        /// Where everything the app writes - settings, logs, temp files - lives. Portable by default,
        /// i.e. next to the exe, but only when that folder can actually be written to. Installed under
        /// Program Files it cannot be, and the portable layout then loses every setting without saying
        /// so, so that case falls back to LocalApplicationData.
        /// </summary>
        public static string GetRootDir()
        {
            if (_rootDir != null)
                return _rootDir;

            string exeDir = GetExeDir();

            if (IsWritable(exeDir))
                return _rootDir = exeDir;

            // Create, not None: the default overload returns an empty string for a folder that does not
            // physically exist yet, which a fresh profile's ~/.local/share does not.
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
            string appData = localAppData.IsEmpty() ? "" : Path.Combine(localAppData, "Nmkoder");
            string temp = Path.Combine(Path.GetTempPath(), "Nmkoder");

            // Assigned before logging, because Logger writes to a path underneath this one and would
            // otherwise re-enter here with nothing resolved yet. The last candidate is taken whether or
            // not it is writable, so this always resolves to something rather than throwing.
            _rootDir = new[] { appData, temp }.Where(x => x.IsNotEmpty()).FirstOrDefault(IsWritable) ?? temp;
            Logger.Log($"'{exeDir}' is not writable - keeping settings and logs in '{_rootDir}' instead.", true);
            return _rootDir;
        }

        /// <summary>
        /// Whether files can be created in <paramref name="dir"/>. Creating the directory is not enough
        /// to tell: an existing one that denies writes creates just fine.
        /// </summary>
        private static bool IsWritable(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, $".write-test-{Guid.NewGuid():N}");

                // DeleteOnClose so a crash between creating and removing it cannot leave the probe
                // sitting next to the exe.
                using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }

                return true;
            }
            catch
            {
                return false;
            }
        }

        public static string GetLogPath(bool noSession = false)
        {
            string path = Path.Combine(GetRootDir(), "logs", (noSession ? "" : sessionTimestamp));
            Directory.CreateDirectory(path);
            return path;
        }

        public static string GetBinPath()
        {
            // Stays beside the exe even when the rest moves to LocalApplicationData: the bundled tools
            // ship there and are only ever read, so a read-only install is no problem for them.
            string path = Path.Combine(GetExeDir(), "bin");

            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception e)
            {
                Logger.Log($"Could not create '{path}': {e.Message}", true);
            }

            return path;
        }

        public static string GetDataPath()
        {
            string path = Path.Combine(GetRootDir(), "data");
            Directory.CreateDirectory(path);
            return path;
        }

        public static string GetSessionsPath()
        {
            string path = Path.Combine(GetDataPath(), "sessions");
            Directory.CreateDirectory(path);
            return path;
        }

        public static string GetSessionDataPath()
        {
            string path = Path.Combine(GetSessionsPath(), sessionTimestamp);
            Directory.CreateDirectory(path);
            return path;
        }

        public static string GetThumbsPath(bool noSession = false)
        {
            string path = Path.Combine((noSession ? GetDataPath() : GetSessionDataPath()), "thumbs");
            Directory.CreateDirectory(path);
            return path;
        }

        public static string GetFrameSeqPath(bool noSession = false)
        {
            string path = Path.Combine((noSession ? GetDataPath() : GetSessionDataPath()), "frameSequences");
            Directory.CreateDirectory(path);
            return path;
        }

        public static string GetAv1anTempPath()
        {
            string path = Path.Combine(GetDataPath(), "av1anTemp");
            Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>
        /// One of the VMAF models bundled into bin/, with forward slashes so it can go straight into a
        /// command line. Unescaped: it used to have an "escape" flag that ran it through
        /// FormatUtils.GetFilterPath, on the understanding that libvmaf's first positional option was
        /// the model - it is not, and what that produced is described in UtilGetMetrics. That caller
        /// names the model by version now and wants no path at all, leaving av1an's --vmaf-path as the
        /// only one, so the flag is gone rather than left as a branch nothing takes.
        /// </summary>
        public static string GetVmafPath(string model = "vmaf_v0.6.1")
        {
            string path = Path.Combine(GetBinPath(), $"{model}.json");

            // Forward slashes on Windows only. There a backslash is the separator and cannot be part
            // of a name, so the rewrite is free; off Windows it is legal data, and substituting it
            // would hand av1an a --vmaf-path that does not exist for anyone who unzipped the app into
            // a directory with a backslash in its name. Same split as FormatUtils.GetFilterPath.
            return Shell.IsWindows ? path.Replace(@"\", "/") : path;
        }
    }
}
