using Nmkoder.Extensions;
using Nmkoder.IO;
using System.Threading.Tasks;

namespace Nmkoder.Media
{
    /// <summary>
    /// Whether the ffmpeg in front of us can tone-map at all.
    /// <para/>
    /// Worth asking rather than assuming, for the reason the rest of this app asks about av1an's flags
    /// and QTGMC's plugins: which ffmpeg runs is not something the bundler controls. The Windows and
    /// Linux archives carry BtbN's GPL build, which has zscale; macOS bundles no ffmpeg at all and leans
    /// on Homebrew's, whose formula does not necessarily include libzimg - and a user's own PATH is
    /// nobody's to control anywhere. Without zscale the chain cannot be built, and finding that out at
    /// encode time means an ffmpeg that exits on "No such filter: 'zscale'" after the run has started.
    /// </summary>
    class ToneMap
    {
        /// <summary> The filter list as ffmpeg prints it, read once per session. "" until it has been
        /// asked, and "" again for an ffmpeg that could not be found or run - which says nothing about
        /// what it supports, and so is never read as "the filter is missing". </summary>
        private static string filterList = null;

        /// <summary>
        /// Every filter the chain names, or the first missing one. zscale is the one genuinely at risk -
        /// it needs libzimg at build time, which is optional - where tonemap and setparams are built in
        /// and have been since ffmpeg 4.0. All three are asked for anyway, since the cost is a string
        /// search over a list already in hand, and a chain is only as available as its rarest filter.
        /// </summary>
        public static async Task<string> GetProblem()
        {
            string list = await GetFilterList();

            // An ffmpeg that could not be asked gets the benefit of the doubt, the way
            // AvProcess.EncoderKnowsFlagOrIsUnknown gives it to an encoder that would not answer.
            // Refusing an encode on the strength of a probe that itself failed would be worse than
            // letting ffmpeg say so in its own words.
            if (list.IsEmpty())
                return "";

            // The name with a space each side, because ffmpeg's list is columnar: " tonemap " matches the
            // tonemap row and not tonemap_opencl or tonemap_vaapi, which sit beside it.
            foreach (string filter in new[] { "zscale", "tonemap", "setparams" })
            {
                if (!list.Contains($" {filter} "))
                    return $"This copy of FFmpeg has no '{filter}' filter, so it cannot tone-map HDR to SDR. " +
                        $"{(filter == "zscale" ? "zscale needs libzimg, which the Windows and Linux builds bundled here have and some distribution and Homebrew builds do not. " : "")}" +
                        $"Set Tone Mapping back to \"{Data.ToneMapConfig.GetLabel(Data.ToneMapMode.Off)}\", or install an FFmpeg built with it.";
            }

            return "";
        }

        private static async Task<string> GetFilterList()
        {
            if (filterList != null)
                return filterList;

            // Set before the work rather than after, as AvProcess.GetToolHelp does: an ffmpeg that
            // cannot be found or run will not start answering later, and re-probing before every encode
            // only delays each one.
            filterList = "";

            try
            {
                var settings = new AvProcess.FfmpegSettings()
                {
                    Args = "-filters",
                    LogLevel = "quiet",
                    ProcessType = OS.NmkoderProcess.ProcessType.Background,
                    CanCancelTask = false,
                };

                filterList = await AvProcess.RunFfmpeg(settings);
            }
            catch (System.Exception e)
            {
                Logger.Log($"Reading FFmpeg's filter list failed: {e.Message}", true, level: Logger.Level.Debug);
            }

            return filterList;
        }
    }
}
