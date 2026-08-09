using Avalonia;
using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.OS;
using Nmkoder.Utils;
using Nmkoder.Views;
using System;
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
        /// Deletes the single-file extraction folders other builds have left in temp. A
        /// single-file build unpacks itself into %TEMP%\.net\Nmkoder\{bundle-id} before it runs,
        /// the id is a content hash so every release gets its own, and .NET deletes none of them
        /// ever - a machine that has run a few weeks of releases would otherwise be carrying one
        /// copy of the runtime per release. Measured on a user's machine before this existed: 40
        /// folders, 5.22 GB.
        ///
        /// This is what makes shipping the app as one executable affordable rather than something
        /// that fills a disk, so it is not housekeeping that can be dropped: reaping every folder
        /// but the live one on each launch is what keeps the steady state at one, and what makes
        /// an upgrade collect its predecessor's.
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

                string current = "";
                string root = "";

                // AppContext.BaseDirectory *is* the extraction folder when the running build is a
                // bundle, which is the one question it answers well - and the exact opposite of
                // what Paths.GetExeDir needs it never to be used for, so do not "tidy" this into
                // that. Reading the root off it beats composing a path wherever it applies.
                string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

                if (baseDir != Paths.GetExeDir() && Path.GetFileName(Path.GetDirectoryName(baseDir)) == appName)
                {
                    current = baseDir;
                    root = Path.GetDirectoryName(baseDir);
                }
                else if (Shell.IsWindows)
                {
                    // Not a bundle, so there is nothing to read the path off - but the builds that
                    // left the folders were, and this is where they put them. Windows only: the
                    // Unix layout carries a user name in the middle, which is not worth guessing at
                    // for tens of megabytes of native libraries.
                    root = Path.Combine(Path.GetTempPath(), ".net", appName);
                }

                if (root.IsEmpty() || !Directory.Exists(root))
                    return;

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
        /// Whether another Nmkoder is running out of this extraction folder. Deleting one that is
        /// in use is the way this goes wrong: the loaded files refuse to go and the rest do, which
        /// strips a live instance of everything it had not got round to loading yet. A running
        /// process holds its assemblies and native libraries open, so an exclusive open is the
        /// cheap question to ask first - and it is answered by Windows, which is where the folders
        /// are large enough to be worth deleting at all.
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
