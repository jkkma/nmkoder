using Avalonia;
using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.OS;
using Nmkoder.Utils;
using Nmkoder.Views;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Nmkoder
{
    static class Program
    {
        public static string[] fileArgs = new string[0];
        public static string[] args = new string[0];

        public static bool busy;

        /// <summary> The application's main window. Set as soon as it has been constructed. </summary>
        public static MainWindow MainWin;

        private static string _version;

        /// <summary>
        /// The build's version, as "2.8.0". Read from the informational version, which is what
        /// &lt;Version&gt; in the csproj writes verbatim - the assembly version pads it to four parts,
        /// so "2.8.0.0" is what asking for that would show. A '+' suffix, which some builds append,
        /// is cut: it names a commit, not a release.
        /// </summary>
        public static string Version
        {
            get
            {
                if (_version != null)
                    return _version;

                try
                {
                    Assembly asm = Assembly.GetExecutingAssembly();
                    string info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                    _version = (info.IsNotEmpty() ? info.Split('+')[0] : asm.GetName().Version?.ToString(3)) ?? "";
                }
                catch (Exception e)
                {
                    Logger.Log($"Could not read the application version: {e.Message}", true);
                    _version = "";
                }

                return _version;
            }
        }

        [STAThread]
        static void Main(string[] cmdArgs)
        {
            Paths.Init();
            Config.Init();
            Cleanup();

            fileArgs = Environment.GetCommandLineArgs().Where(a => a.Length > 0 && a[0] != '-' && File.Exists(a)).Skip(1).ToArray();
            args = Environment.GetCommandLineArgs().Where(a => a.Length > 0 && a[0] == '-').Select(x => x.Trim().Substring(1).ToLowerInvariant()).ToArray();
            Logger.Log($"Command Line: {Environment.CommandLine}", true);
            Logger.Log($"Files: {(fileArgs.Length > 0 ? string.Join(", ", fileArgs) : "None")}", true);
            Logger.Log($"Args: {(args.Length > 0 ? string.Join(", ", args) : "None")}", true);

            // Before the UI, which is what the App SDK asks for, and paired with the Unregister
            // below so a portable copy does not leave its COM registration behind in the registry.
            WindowsToast.Register();

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(cmdArgs);

            WindowsToast.Unregister();
        }

        // Also used by the Avalonia XAML previewer/designer.
        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .LogToTrace();
        }

        public static void Cleanup()
        {
            int keepLogsDays = 4;
            int keepSessionDataDays = 4;

            try
            {
                foreach (DirectoryInfo dir in new DirectoryInfo(Paths.GetLogPath(true)).GetDirectories())
                {
                    string[] split = dir.Name.Split('-');
                    int daysOld = (DateTime.Now - new DateTime(split[0].GetInt(), split[1].GetInt(), split[2].GetInt())).Days;
                    int fileCount = dir.GetFiles("*", SearchOption.AllDirectories).Length;

                    if (daysOld > keepLogsDays || fileCount < 1) // keep logs for 4 days
                    {
                        Logger.Log($"Cleanup: Log folder {dir.Name} is {daysOld} days old and has {fileCount} files - Will Delete", true);
                        IoUtils.TryDeleteIfExists(dir.FullName);
                    }
                }

                IoUtils.DeleteContentsOfDir(Paths.GetSessionDataPath()); // Clear this session's temp files...

                foreach (DirectoryInfo dir in new DirectoryInfo(Paths.GetSessionsPath()).GetDirectories())
                {
                    string[] split = dir.Name.Split('-');
                    int daysOld = (DateTime.Now - new DateTime(split[0].GetInt(), split[1].GetInt(), split[2].GetInt())).Days;
                    int fileCount = dir.GetFiles("*", SearchOption.AllDirectories).Length;

                    if (daysOld > keepSessionDataDays || fileCount < 1) // keep temp files for 2 days
                    {
                        Logger.Log($"Cleanup: Session folder {dir.Name} is {daysOld} days old and has {fileCount} files - Will Delete", true);
                        IoUtils.TryDeleteIfExists(dir.FullName);
                    }
                }

                foreach (string file in IoUtils.GetFilesSorted(Paths.GetBinPath(), true, "*.log*"))
                    IoUtils.TryDeleteIfExists(file);

                foreach (string file in IoUtils.GetFilesSorted(Paths.GetBinPath(), true, "desktop.ini"))
                    IoUtils.TryDeleteIfExists(file);
            }
            catch (Exception e)
            {
                Logger.Log($"Cleanup Error: {e.Message}\n{e.StackTrace}");
            }

            // Off the startup path. This can be several gigabytes spread over dozens of folders,
            // and nothing below waits on it.
            Task.Run(CleanupBundleExtractions);
        }

        /// <summary>
        /// Deletes the single-file extraction folders other builds have left behind. A single-file
        /// build unpacks itself into {base}/Nmkoder/{bundle-id} before it runs - %TEMP%\.net on
        /// Windows, ~/.net elsewhere - the id is a content hash so every release gets its own, and
        /// .NET deletes none of them ever. A machine that has run a few weeks of releases would
        /// otherwise be carrying one copy per release. Measured on a user's machine before this
        /// existed: 40 folders, 5.22 GB.
        ///
        /// This is what makes shipping the app as one executable affordable rather than something
        /// that fills a disk, so it is not housekeeping that can be dropped: reaping every folder
        /// but the live one on each launch is what keeps the steady state at one, and what makes
        /// an upgrade collect its predecessor's.
        ///
        /// Every RID is bundled, but they do not extract the same thing: win-x64 sets
        /// IncludeAllContentForSelfExtract, which the App SDK demands, so its folder is the whole
        /// bundle, where the others hold the native libraries alone - 123 MB against 49 MB,
        /// measured on linux-x64 bundles of one build. Both are worth reaping; the flag's only
        /// bearing here is on how the live folder is found (see GetLiveExtractionDir).
        ///
        /// It deliberately does not ask whether *this* build is bundled before looking. A build
        /// that is not - the win-x64 releases that shipped as loose DLLs, or anyone's own
        /// non-single publish - is precisely the one with stale folders and no live one to keep.
        /// </summary>
        private static void CleanupBundleExtractions()
        {
            try
            {
                string appName = Path.GetFileNameWithoutExtension(Paths.GetExe());

                if (appName.IsEmpty())
                    return;

                string current = GetLiveExtractionDir(appName);
                string root = "";

                if (current.IsNotEmpty())
                {
                    // Reading the root off the live folder beats composing a path wherever it applies.
                    root = Path.GetDirectoryName(current);
                }
                else if (Shell.IsWindows)
                {
                    // Not running from a bundle, so there is nothing to read the path off - but the
                    // builds that left the folders were, and this is where they put them. Windows
                    // only, and not for want of knowing the path elsewhere: off Windows a folder
                    // cannot be told to be free (see IsExtractionInUse), so the sweep only runs
                    // where this process's own folder is known and every other one is a sibling of
                    // it. A build that is not bundled has no such anchor.
                    root = Path.Combine(Path.GetTempPath(), ".net", appName);
                }

                if (root.IsEmpty() || !Directory.Exists(root))
                    return;

                // IsExtractionInUse below is answered by Windows and by nothing else. Measured on
                // Linux: a .so another process has mapped opens with FileShare.None *and deletes
                // successfully*, so every folder there reads as free and the sweep would strip a
                // live instance of whatever it had not loaded yet. Off Windows the question is
                // therefore asked once, of the process list, and any second instance stands the
                // whole sweep down - its folder cannot be told apart from a dead one from here.
                if (!Shell.IsWindows && OtherInstanceRunning(appName))
                {
                    Logger.Log($"Cleanup: another {appName} is running - leaving the bundle extractions alone", true);
                    return;
                }

                long freed = 0;

                foreach (DirectoryInfo dir in new DirectoryInfo(root).GetDirectories())
                {
                    if (dir.FullName.TrimEnd(Path.DirectorySeparatorChar) == current)
                        continue;

                    if (IsExtractionInUse(dir))
                    {
                        Logger.Log($"Cleanup: bundle folder '{dir.Name}' is in use by another build - keeping it", true);
                        continue;
                    }

                    long size = IoUtils.GetDirSize(dir.FullName, true);

                    if (IoUtils.TryDeleteIfExists(dir.FullName))
                        freed += size;
                }

                if (freed > 0)
                    Logger.Log($"Cleanup: freed {FormatUtils.Bytes(freed)} of single-file extractions in '{root}'.", true);
            }
            catch (Exception e)
            {
                Logger.Log($"Cleanup Error (bundle extractions): {e.Message}", true);
            }
        }

        /// <summary>
        /// The extraction folder this process is running out of, or "" for a build that is not
        /// bundled at all.
        ///
        /// NATIVE_DLL_SEARCH_DIRECTORIES is where the host tells the runtime to look for native
        /// libraries, and for a bundled build that is the extraction folder. It is asked first
        /// because it is the only one of the two that answers on every RID: AppContext.BaseDirectory
        /// names the extraction folder only under IncludeAllContentForSelfExtract, which is the
        /// Windows build's flag alone - everywhere else BaseDirectory is the exe's own directory
        /// and nothing on it points at temp. Measured on real bundles under both settings.
        /// </summary>
        private static string GetLiveExtractionDir(string appName)
        {
            string searchDirs = AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string;

            foreach (string entry in (searchDirs ?? "").Split(Path.PathSeparator))
            {
                string dir = entry.Trim().TrimEnd(Path.DirectorySeparatorChar);

                if (IsExtractionDir(dir, appName))
                    return dir;
            }

            // The documented half of the same question, and the only one a build predating the
            // above would have. Reading BaseDirectory here is deliberate and is the exact opposite
            // of the rule Paths.GetExeDir exists to state, so do not "tidy" the two together.
            string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

            return IsExtractionDir(baseDir, appName) ? baseDir : "";
        }

        /// <summary>
        /// Whether a path looks like one of this app's extraction folders: named under a folder
        /// carrying the app's name, and not the directory the exe itself sits in.
        /// </summary>
        private static bool IsExtractionDir(string dir, string appName)
        {
            return dir.IsNotEmpty() && dir != Paths.GetExeDir() && Path.GetFileName(Path.GetDirectoryName(dir)) == appName;
        }

        /// <summary>
        /// Whether another copy of the app is running. Off Windows this is the whole of the in-use
        /// question below, since a folder cannot be tied to the process using it from here - so any
        /// second instance stands the sweep down rather than risk deleting the one it is using.
        /// Cannot-tell counts as running: that is not a moment to start deleting.
        /// </summary>
        private static bool OtherInstanceRunning(string appName)
        {
            Process[] procs = null;

            try
            {
                procs = Process.GetProcessesByName(appName);
                return procs.Any(p => p.Id != Environment.ProcessId);
            }
            catch (Exception e)
            {
                Logger.Log($"OtherInstanceRunning: could not read the process list: {e.Message}", true);
                return true;
            }
            finally
            {
                if (procs != null)
                    foreach (Process proc in procs)
                        proc.Dispose();
            }
        }

        /// <summary>
        /// Whether another Nmkoder is running out of this extraction folder. Deleting one that is
        /// in use is the way this goes wrong: the loaded files refuse to go and the rest do, which
        /// strips a live instance of everything it had not got round to loading yet. A running
        /// process holds its assemblies and native libraries open, so an exclusive open is the
        /// cheap question to ask first - and it is answered by Windows and by nothing else.
        ///
        /// Unix takes FileShare.None as an advisory flock, which the dynamic loader does not take:
        /// measured, a mapped .so opens here and deletes without complaint. The caller asks
        /// OtherInstanceRunning instead before it gets this far, so this is not the guard off
        /// Windows and must not be mistaken for one.
        /// </summary>
        private static bool IsExtractionInUse(DirectoryInfo dir)
        {
            try
            {
                foreach (FileInfo file in dir.GetFiles("*.dll", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        using (file.Open(FileMode.Open, FileAccess.Read, FileShare.None)) { }
                    }
                    catch (IOException)
                    {
                        return true;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                // Cannot even look inside it - that is not a folder to start deleting.
                Logger.Log($"IsExtractionInUse: could not read '{dir.FullName}': {e.Message}", true);
                return true;
            }

            return false;
        }
    }
}
