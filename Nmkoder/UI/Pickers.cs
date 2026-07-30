using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Nmkoder.Extensions;
using Nmkoder.IO;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Nmkoder.UI
{
    /// <summary>
    /// The file and folder dialogs, wrapped so each one opens in the folder the last one of its
    /// kind was left in. Avalonia only takes a starting folder as an <see cref="IStorageFolder"/>,
    /// which has to be resolved asynchronously and comes back null for a path that has since gone
    /// away, so leaving that to the call sites is how none of them ended up doing it.
    /// </summary>
    class Pickers
    {
        /// <summary>
        /// Which of the two remembered folders a dialog belongs to. Sources are browsed for in one
        /// place and encodes are written to another, so sharing one memory between them would mean
        /// every save dialog throws away where the input files live, and every open dialog throws
        /// away where the output goes.
        /// </summary>
        public enum Dir { Input, Output }

        public static async Task<string[]> PickFiles(TopLevel owner, string title, bool allowMultiple)
        {
            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = allowMultiple,
                SuggestedStartLocation = await StartLocation(owner, Dir.Input)
            });

            string[] paths = LocalPaths(files);

            if (paths.Length > 0)
                Remember(Dir.Input, ParentDir(paths[0]));

            return paths;
        }

        public static async Task<string[]> PickFolders(TopLevel owner, string title, bool allowMultiple, Dir dir = Dir.Input, string preferred = null)
        {
            var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = allowMultiple,
                SuggestedStartLocation = await StartLocation(owner, dir, preferred)
            });

            string[] paths = LocalPaths(folders);

            if (paths.Length > 0)
                // An input folder is one of a set - the next image sequence sits beside the one
                // just picked rather than inside it - while an output folder is the destination
                // itself, and the next thing written there goes in it, not next to it.
                Remember(dir, dir == Dir.Input ? ParentDir(paths[0]) : paths[0]);

            return paths;
        }

        /// <summary>
        /// A path to write to. <paramref name="currentPath"/> is whatever the output box already
        /// holds - the dialog opens beside it, because a path already pointing somewhere is a
        /// better answer than any folder a previous dialog happened to be left in.
        /// </summary>
        public static async Task<string> PickSavePath(TopLevel owner, string title, string currentPath)
        {
            var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = Path.GetFileName(currentPath ?? ""),
                SuggestedStartLocation = await StartLocation(owner, Dir.Output, ParentDir(currentPath))
            });

            string path = file?.TryGetLocalPath();

            if (path.IsEmpty())
                return null;

            Remember(Dir.Output, ParentDir(path));
            return path;
        }

        private static string[] LocalPaths(IReadOnlyList<IStorageItem> items)
        {
            return (items ?? new IStorageItem[0]).Select(x => x.TryGetLocalPath()).Where(x => x.IsNotEmpty()).ToArray();
        }

        private static void Remember(Dir dir, string dirPath)
        {
            if (dirPath.IsEmpty())
                return;

            Config.Set(dir == Dir.Input ? Config.Key.LastInputDir : Config.Key.LastOutputDir, dirPath);
        }

        private static async Task<IStorageFolder> StartLocation(TopLevel owner, Dir dir, string preferred = null)
        {
            // Collected here rather than inside the probe below: the candidates read the file list,
            // which belongs to the UI thread.
            string[] candidates = Candidates(dir, preferred).ToArray();

            // Probed off it, though. A candidate left over from a share that is no longer mounted
            // leaves Directory.Exists blocking until the mount gives up, and this sits between the
            // user clicking a browse button and the dialog appearing.
            string dirPath = await Task.Run(() => candidates.FirstOrDefault(Usable));

            if (dirPath == null)
                return null; // Nothing to suggest, so the platform picks - which is the old behaviour

            try
            {
                return await owner.StorageProvider.TryGetFolderFromPathAsync(dirPath);
            }
            catch (Exception e)
            {
                Logger.Log($"Could not open the file dialog in '{dirPath}': {e.Message}", true);
                return null;
            }
        }

        /// <summary> Rooted as well as present: a relative path neither throws here nor resolves,
        /// it just turns into something UNC-shaped that the dialog then opens nowhere at. </summary>
        private static bool Usable(string dir)
        {
            return dir.IsNotEmpty() && Path.IsPathRooted(dir) && Directory.Exists(dir);
        }

        /// <summary> Folders to try, best first. </summary>
        private static IEnumerable<string> Candidates(Dir dir, string preferred)
        {
            yield return preferred;

            if (dir == Dir.Output)
            {
                // Ahead of the remembered folder on purpose: the default output directory is a
                // setting the user went and configured, where the other is only an inference from
                // the last time they browsed.
                yield return Config.Get(Config.Key.DefaultOutputDir);
                yield return Config.Get(Config.Key.LastOutputDir);
            }
            else
            {
                yield return Config.Get(Config.Key.LastInputDir);
                // Nothing has been browsed for yet, but files dragged into the window say just as
                // well which folder the user is working out of.
                yield return ParentDir(FileList.Items.FirstOrDefault()?.File?.SourcePath);
            }
        }

        /// <summary> The containing folder of a path, or an empty string for anything that does not
        /// have one. Never throws - these paths come from config files and text boxes. </summary>
        private static string ParentDir(string path)
        {
            if (path.IsEmpty())
                return "";

            try
            {
                return Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path.Trim().Trim('"'))) ?? "";
            }
            catch
            {
                return "";
            }
        }
    }
}
