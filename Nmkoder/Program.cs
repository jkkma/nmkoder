using Avalonia;
using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.OS;
using Nmkoder.Views;
using System;
using System.IO;
using System.Linq;

namespace Nmkoder
{
    static class Program
    {
        public static string[] fileArgs = new string[0];
        public static string[] args = new string[0];

        public static bool busy;

        /// <summary> The application's main window. Set as soon as it has been constructed. </summary>
        public static MainWindow MainWin;

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
        }
    }
}
