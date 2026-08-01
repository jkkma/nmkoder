using Nmkoder.Extensions;
using Nmkoder.IO;
using System;
using System.IO;

namespace Nmkoder.UI
{
    internal class UiData
    {
        /// <summary>
        /// Where to offer to save a newly loaded file, without an extension - the container adds that.
        /// The source's own name, in the folder set as the default destination, or beside the source
        /// when no default is set. While a batch runs the name comes from its output name template
        /// instead, which is the only say the user has over it - see <see cref="BatchNaming"/>.
        /// </summary>
        public static string GetDefaultOutPath(string sourcePath)
        {
            string name = BatchNaming.ResolveName(sourcePath);
            string sourceDir = Path.GetDirectoryName(sourcePath) ?? "";
            string besideSource = sourceDir.IsEmpty() ? name : Path.Combine(sourceDir, name);
            string dir = Config.Get(Config.Key.DefaultOutputDir, "").Trim().Trim('"');

            if (dir.IsEmpty())
                return besideSource;

            // A default pointing somewhere gone - a drive not plugged in, a folder since deleted - would
            // otherwise be handed to ffmpeg, which does not create directories and fails at the last step.
            if (!Directory.Exists(dir))
            {
                Logger.Log($"Default output folder '{dir}' does not exist - saving next to the source file instead.");
                return besideSource;
            }

            try
            {
                return Path.Combine(dir, name);
            }
            catch (Exception e)
            {
                Logger.Log($"Default output folder '{dir}' cannot be used ({e.Message}) - saving next to the source file instead.");
                return besideSource;
            }
        }

        public static string GetOutPath(bool includeExtension = true)
        {
            var f = Program.MainWin;
            string outPathText = "";
            string containerText = "";

            if (f.RunningTask == Main.RunTask.TaskType.Convert)
            {
                outPathText = (f.FfmpegOutputBox.Text ?? "").Trim();
                containerText = f.FfmpegContainerBox.GetText().Trim();
            }
            else if (f.RunningTask == Main.RunTask.TaskType.Av1an)
            {
                outPathText = (f.Av1anOutputPathBox.Text ?? "").Trim();
                containerText = f.Av1anContainerBox.GetText().Trim();
            }

            if (includeExtension && containerText.IsNotEmpty())
                outPathText = $"{outPathText}.{containerText.Lower()}";

            return outPathText;
        }
    }
}
