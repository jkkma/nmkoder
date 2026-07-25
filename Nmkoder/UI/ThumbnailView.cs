using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Media;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Nmkoder.UI
{
    class ThumbnailView
    {
        private static Dictionary<string, Bitmap> currThumbs;
        private static int currThumbIndex;
        private static bool busy;

        private static Bitmap PlaceholderImg { get { return AppImages.Placeholder; } }
        private static Bitmap LoadingImg { get { return AppImages.LoadingThumbs; } }

        public static void ClearUi()
        {
            IoUtils.DeleteContentsOfDir(Paths.GetThumbsPath());
            SetUi(PlaceholderImg, "");
            busy = false;
        }

        public static void LoadUi()
        {
            IoUtils.DeleteContentsOfDir(Paths.GetThumbsPath());
            SetUi(LoadingImg, "Loading Thumbnails...");
            busy = true;
        }

        /// <summary> Thumbnail generation runs on a worker thread, so all UI writes are marshalled. </summary>
        private static void SetUi(Bitmap image, string label)
        {
            void Apply()
            {
                if (Program.MainWin == null)
                    return;

                Program.MainWin.ThumbnailBox.Source = image;
                Program.MainWin.ThumbLabel.Text = label;
            }

            if (Dispatcher.UIThread.CheckAccess())
                Apply();
            else
                Dispatcher.UIThread.Post(Apply);
        }

        public static async Task GenerateThumbs(string path)
        {
            LoadUi();
            Directory.CreateDirectory(Paths.GetThumbsPath());
            int randThumbs = 4;

            try
            {
                if (!IoUtils.IsPathDirectory(path))     // If path is video - Extract frames
                {
                    string imgPath = Path.Combine(Paths.GetThumbsPath(), $"thumb0-s0.jpg");
                    await FfmpegExtract.ExtractSingleFrame(path, imgPath, 1, 360);
                    await LoadThumbnailsOnce();

                    int duration = (int)Math.Floor((float)(await FfmpegCommands.GetDurationMs(path)) / 1000);

                    if (duration > randThumbs)   // Only generate random thumbs if duration is long enough
                    {
                        await FfmpegExtract.ExtractThumbs(path, Paths.GetThumbsPath(), randThumbs * 2);
                        FileInfo[] thumbs = IoUtils.GetFileInfosSorted(Paths.GetThumbsPath(), false, $"*.*");

                        var smallerHalf = thumbs.Skip(1).OrderBy(f => f.Length).Take(randThumbs).ToList(); // Get smaller half of thumbs

                        foreach (FileInfo f in smallerHalf) // Delete smaller thumbs to only have high-information thumbs
                            f.Delete();
                    }
                }
                else     // Path is frame folder - Copy frames
                {
                    FileInfo[] frames = IoUtils.GetFileInfosSorted(path, false, "*.*");
                    frames[0].CopyTo(Path.Combine(Paths.GetThumbsPath(), $"thumb0{frames[0].Extension}"));
                    Random rnd = new Random();
                    List<FileInfo> picks = frames.Skip(1).OrderBy(x => rnd.Next()).Take(randThumbs * 2).ToList();
                    picks = picks.OrderBy(f => f.Length).Skip(randThumbs).ToList(); // Delete smaller half of thumbs

                    int idx = 1;

                    foreach (FileInfo pick in picks)
                    {
                        pick.CopyTo(Path.Combine(Paths.GetThumbsPath(), $"thumb{idx}{pick.Extension}"));
                        idx++;
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Log($"GetThumbnails Error: {e.Message}\n{e.StackTrace}", true);
            }

            RemoveInvalidImages();

            if (IoUtils.GetAmountOfFiles(Paths.GetThumbsPath(), false, $"*.*p*") > 0)
                await LoadThumbnailsOnce();
            else
                Fail();
        }

        static void RemoveInvalidImages()
        {
            foreach (string imgFile in IoUtils.GetFilesSorted(Paths.GetThumbsPath(), false))
            {
                if (!IoUtils.CheckImageValid(imgFile))
                    IoUtils.TryDeleteIfExists(imgFile);
            }
        }

        static void Fail()
        {
            SetUi(PlaceholderImg, "Failed to extract thumbnails.");
        }

        public static async Task LoadThumbnailsOnce(string format = "*")
        {
            try
            {
                Logger.Log($"LoadThumbnailsOnce({format})", true);
                string[] files = IoUtils.GetFilesSorted(Paths.GetThumbsPath(), false, $"*.{format}");
                var loaded = new Dictionary<string, Bitmap>();

                foreach (string file in files)
                {
                    Bitmap bmp = IoUtils.GetImage(file, false);

                    if (bmp != null)
                        loaded[Path.GetFileName(file)] = bmp;
                }

                DisposeThumbs();
                currThumbs = loaded;
                currThumbIndex = currThumbs.Count > 1 ? 1 : 0;
                busy = false;
                Logger.Log($"Loaded {currThumbs.Count} thumbnail images", true);
                ShowThumb();
            }
            catch (Exception e)
            {
                Logger.Log($"LoadThumbnailsOnce Exception: {e.Message}\n{e.StackTrace}", true);
            }

            await Task.CompletedTask;
        }

        private static void DisposeThumbs()
        {
            if (currThumbs == null)
                return;

            foreach (var kvp in currThumbs)
                kvp.Value?.Dispose();

            currThumbs = null;
        }

        public static void ThumbnailClick()
        {
            if (busy || currThumbs == null || currThumbs.Count < 1)
                return;

            ShowThumb(true);
        }

        public static void ShowThumb(bool next = false)
        {
            if (currThumbs == null || currThumbs.Count < 1)
                return;

            if (next)
            {
                currThumbIndex++;

                if (currThumbIndex >= currThumbs.Count)
                    currThumbIndex = 0;
            }

            var entry = currThumbs.ElementAt(currThumbIndex);
            bool hasTime = entry.Key.Contains("-s");
            string clickHint = currThumbs.Count > 1 ? " Click for next thumbnail." : "";
            string label;

            if (hasTime)
            {
                int s = entry.Key.Split("-s")[1].GetInt();
                string time = TimeSpan.FromSeconds(s).ToString(@"hh\:mm\:ss");
                label = $"Showing Thumbnail {currThumbIndex + 1}/{currThumbs.Count} ({time}).{clickHint}";
            }
            else
            {
                label = $"Showing Thumbnail {currThumbIndex + 1}/{currThumbs.Count}.{clickHint}";
            }

            SetUi(entry.Value, label);
        }
    }
}
