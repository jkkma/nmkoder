using Nmkoder.Extensions;

namespace Nmkoder.UI
{
    internal class UiData
    {
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
