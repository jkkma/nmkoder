using Nmkoder.Extensions;
using Nmkoder.IO;
using System.Linq;
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

        /// <summary> The probe's verdict, cached for the session: "" once libplacebo has been shown to
        /// work here, the reason it will not otherwise, and null until it has been asked. A GPU does not
        /// appear halfway through a session, and the probe costs a process launch. </summary>
        private static string libplaceboProblem = null;

        /// <summary>
        /// Whether libplacebo can tone-map on this machine, or why not - the fallback to the zscale
        /// chain being silent otherwise, and a fallback nobody is told about is one nobody can fix.
        /// <para/>
        /// **Three things have to be true and the third is the one that matters.** The filter has to be
        /// in this ffmpeg, which BtbN's builds have and a distribution's may not. A Vulkan device has to
        /// come up, which libplacebo will not arrange for itself. And that device has to be a **real
        /// GPU**: measured against Mesa's lavapipe, libplacebo initialises perfectly and then takes
        /// 8.4-13.1s over 48 frames of 1080p where the zscale chain takes 0.9-1.8s and a plain pixel
        /// format conversion takes 0.27s. So a check that asks only "did it come up" passes on a
        /// software rasteriser and then costs an order of magnitude - discovered, on this tab, hours
        /// into an encode.
        /// <para/>
        /// ffmpeg's own Vulkan setup prints what it chose at verbose level - <c>Device 0 selected:
        /// llvmpipe (LLVM 20.1.2, 256 bits) (software) (0x0)</c> - and the parenthesised word is
        /// <c>VK_PHYSICAL_DEVICE_TYPE_CPU</c> spelled out. Reading that beats timing the probe: it is
        /// exact, it costs nothing, and a timing threshold would have to hold across every machine this
        /// runs on.
        /// <para/>
        /// **Positive evidence is required, in both directions.** A device line that cannot be found at
        /// all is a "no", not a shrug - the cost of wrongly falling back is the chain this app shipped
        /// with, and the cost of wrongly going ahead is the 10x above. The frame is checked too, by
        /// muxing it to <c>md5</c>: the failure this guards against exits 0 having written nothing, so
        /// an exit code proves nothing and neither would a file's existence.
        /// </summary>
        public static async Task<string> GetLibplaceboProblem()
        {
            if (libplaceboProblem != null)
                return libplaceboProblem;

            // Set before the work rather than after, as GetFilterList does: an ffmpeg that cannot be
            // run will not start answering later, and re-probing before every encode only delays it.
            libplaceboProblem = "the probe could not be run";

            string list = await GetFilterList();

            if (list.IsNotEmpty() && !list.Contains(" libplacebo "))
                return libplaceboProblem = "this FFmpeg has no 'libplacebo' filter";

            try
            {
                // The device argument sits after the '-i' on purpose: that is where the AV1AN tab's
                // filters are spliced into av1an's own per-chunk command, so probing any other shape
                // would be testing a command line this app never sends.
                var settings = new AvProcess.FfmpegSettings()
                {
                    Args = $"-f lavfi -i color=c=black:s=64x64 {Data.ToneMapConfig.DeviceArgs} " +
                        $"-vf \"setparams=color_trc=smpte2084:color_primaries=bt2020:colorspace=bt2020nc," +
                        $"libplacebo=tonemapping=hable:peak_detect=0:colorspace=bt709:color_primaries=bt709:color_trc=bt709:range=tv\" " +
                        $"-frames:v 1 -f md5 -",
                    LogLevel = "verbose",
                    ProcessType = OS.NmkoderProcess.ProcessType.Background,
                    CanCancelTask = false,
                    LoggingMode = AvProcess.LogMode.Hidden,
                };

                string output = await AvProcess.RunFfmpeg(settings);

                if (!output.Contains("MD5="))
                    return libplaceboProblem = "it could not render a frame here";

                string device = output.SplitIntoLines().LastOrDefault(l => l.Contains(" selected: ")) ?? "";

                if (device.IsEmpty())
                    return libplaceboProblem = "FFmpeg did not report which Vulkan device it chose";

                if (device.Contains("(software)"))
                    return libplaceboProblem = "the only Vulkan device here is a software renderer, which is " +
                        "several times slower than the FFmpeg chain it would replace";

                return libplaceboProblem = "";
            }
            catch (System.Exception e)
            {
                Logger.Log($"Probing libplacebo failed: {e.Message}", true, level: Logger.Level.Debug);
                return libplaceboProblem;
            }
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
