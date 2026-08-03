using System;
using System.Collections.Generic;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Nmkoder.UI
{
    /// <summary>
    /// Replaces the generated WinForms <c>Properties.Resources</c> class. Images now live in
    /// Assets/ and are embedded as Avalonia resources, loaded lazily and cached.
    /// </summary>
    public static class AppImages
    {
        private static readonly Dictionary<string, Bitmap> _cache = new Dictionary<string, Bitmap>();

        public static Bitmap Get(string fileName)
        {
            lock (_cache)
            {
                if (_cache.TryGetValue(fileName, out Bitmap cached))
                    return cached;

                try
                {
                    using System.IO.Stream stream = AssetLoader.Open(new Uri($"avares://Nmkoder/Assets/{fileName}"));
                    var bitmap = new Bitmap(stream);
                    _cache[fileName] = bitmap;
                    return bitmap;
                }
                catch (Exception e)
                {
                    IO.Logger.Log($"Failed to load asset '{fileName}': {e.Message}", true);
                    _cache[fileName] = null;
                    return null;
                }
            }
        }

        public static Bitmap Placeholder { get { return Get("baseline_image_white_48dp-4x-25pcAlphaPad.png"); } }
        public static Bitmap LoadingThumbs { get { return Get("loadingThumbsTextNew.png"); } }
    }
}
