using Nmkoder.Extensions;
using Nmkoder.IO;
using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
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
                    // peak_detect on, because the shipped chains run it on - what is tested is what
                    // ships. On this one black frame it converges instantly and proves the detector's
                    // compute shaders run on this device, which a bare conversion would not.
                    Args = $"-f lavfi -i color=c=black:s=64x64 {Data.ToneMapConfig.DeviceArgs} " +
                        $"-vf \"setparams=color_trc=smpte2084:color_primaries=bt2020:colorspace=bt2020nc," +
                        $"libplacebo=tonemapping=hable:peak_detect=1:colorspace=bt709:color_primaries=bt709:color_trc=bt709:range=tv\" " +
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

        /// <summary>
        /// How many spots of the file the peak scan decodes, spread evenly through it, and how many
        /// frames it reads at each. A dozen points is the autocrop's ten with a little on top. The miss
        /// risk of sampling - the brightest scene falling between points - is what
        /// <see cref="Data.ToneMapConfig.MeasuredPeakHeadroom"/> is priced for.
        /// <para/>
        /// **The cost is the seeks, not the frames, and it is minutes rather than the seconds this
        /// used to claim.** Measured on 120 s of 4K: 90-96 s at a 10 s GOP against 23-27 s for the same
        /// content at a 1 s GOP, where the whole scan only ever decodes 60 frames. Each point pays a
        /// fresh process, a seek, and then `accurate_seek`'s decode-and-discard from the preceding
        /// keyframe - up to a whole GOP - so the bill tracks the GOP length, and batching the points
        /// into one process saves nothing (measured: a 60-input concat was 8.4 s at n=12 and 13.7 s at
        /// n=20, so the seek is the cost).
        /// <para/>
        /// **Raising either number is not the way to find more peaks, and a whole-file keyframe pass is
        /// not either.** Both were measured and both were rejected - see
        /// <see cref="MeasurePeakNitsAsync"/> for what was tried and what happened.
        /// </summary>
        private const int PeakScanPoints = 12, PeakScanFramesPerPoint = 5;

        /// <summary>
        /// Reads the brightest pixel a sampled scan of the file actually contains, in nits, or 0 where
        /// it could not be measured - which every caller treats as "fall back to the declared metadata",
        /// so a failed scan costs the old behaviour and never the encode.
        /// <para/>
        /// This exists because a PQ file's declared peak routinely describes something other than the
        /// picture: the mastering monitor's ceiling, or a scan artifact like a MaxCLL two nits under
        /// the format maximum. PQ is absolute - a code value *is* a luminance - so the honest number
        /// can simply be read off the frames: <c>signalstats</c> reports each frame's maximum luma
        /// code, and the PQ curve turns the largest one back into nits. It is the same answer a
        /// peak-detecting player computes per frame, taken once, deterministically, for the one
        /// backend that needs its peak as a static number.
        /// <para/>
        /// PQ only. HLG is relative by design and the zscale chain passes no peak for it - see the
        /// note in <see cref="Data.ToneMapConfig.GetFilterArgs"/>.
        /// <para/>
        /// The maximum is a single-pixel maximum, so film grain and speculars set it rather than the
        /// scene's perceptual level. That is the right way round to be wrong for a roll-off: it can
        /// only overstate the content, which softens the curve, where a percentile that understated it
        /// would clip real pixels.
        /// <para/>
        /// **Two replacements were measured and both are worse. Do not reach for either.**
        /// <list type="number">
        /// <item><b>More or denser points.</b> 24x5, 48x5, 12x15 and 60x1 cost 2.3-4.6x the wall clock
        /// on a 4K file, for the reason on <see cref="PeakScanPoints"/> - every extra point is another
        /// seek. 12x15 finds nothing at all that 12x5 does not: more frames at the same twelve places
        /// is the least useful direction, the failure being placement rather than depth.</item>
        /// <item><b>A whole-file keyframe pass</b> - one ffmpeg, <c>-skip_frame nokey</c>, no seeking.
        /// This looks unanswerable and is not. It read the true peak on six fixtures at 15-34x the
        /// speed, and **that result was an artifact of how the fixtures were built**: each changed
        /// brightness with a hard cut, so the encoder's scene detector had already put an IDR on the
        /// bright frame - flash.mkv carries keyframes at exactly t=100.000 and t=102.000, the burst's
        /// own boundaries. The pass was reading the encoder's marks, not finding peaks. Rebuilt with
        /// the event inside a GOP it fails as badly as anything: a 2 s burst under a fixed GOP reads
        /// 91.2 nits against a true 1494.6, which is the full 84-code-value clip-to-white failure; and
        /// a brightness rise *within a take* - no cut, x265's own default scene detection, the ordinary
        /// sunrise or lamp or explosion - reads 528.9 against 1526.2. No cut, no keyframe, missed peak,
        /// whatever the content is. Its speed also inverts with GOP length, being 1.4-2.5x **slower**
        /// than this scan on a 480 s 4K file at a 1 s GOP and projecting to ~12 minutes on a feature,
        /// where this scan stays at 20-35 s however long the film is.</item>
        /// </list>
        /// <para/>
        /// **And a better maximum is not a better picture, which is the deeper reason neither was
        /// worth it.** Measured against libplacebo's own per-frame detection over a whole file: on
        /// content with an outlier, hitting the true peak is ~14 code values *darker* than missing it
        /// across 95% of the runtime, because a static roll-off built for a two-second event prices
        /// the entire film for it. libplacebo does not face this trade - it re-exposes per scene - and
        /// the zscale chain cannot. So the room left here is in what
        /// <see cref="Data.ToneMapConfig.GetEffectivePeakNits"/> does with the number (a percentile
        /// rather than a maximum), not in finding the maximum more reliably. That was not measured;
        /// it is the next thing to try, and it is not a change to this method.
        /// </summary>
        public static async Task<double> MeasurePeakNitsAsync(string path)
        {
            try
            {
                var probe = new AvProcess.FfprobeSettings()
                {
                    Args = $"-select_streams v:0 -show_entries stream=pix_fmt,color_range:format=duration -of default=noprint_wrappers=1 {path.Wrap()}",
                    LogLevel = "quiet",
                };

                string info = await AvProcess.RunFfprobe(probe);
                string pixFmt = ReadProbeValue(info, "pix_fmt");
                string range = ReadProbeValue(info, "color_range");
                double duration = ReadProbeValue(info, "duration").GetFloat();

                int bitDepth = GetBitDepth(pixFmt);

                if (bitDepth < 8)
                    return 0;

                // Full range only where the file says so; limited is what unspecified video is.
                bool fullRange = range.Trim().ToLowerInvariant() == "pc" || range.Trim().ToLowerInvariant() == "full";

                int maxCode = -1;
                int points = duration > 1 ? PeakScanPoints : 1;
                int frames = duration > 1 ? PeakScanFramesPerPoint : PeakScanPoints * PeakScanFramesPerPoint;

                for (int i = 0; i < points; i++)
                {
                    if (Main.RunTask.canceled)
                        return 0;

                    double t = duration > 1 ? duration * (i + 0.5d) / points : 0;

                    var settings = new AvProcess.FfmpegSettings()
                    {
                        Args = $"-ss {t.ToString("0.##", CultureInfo.InvariantCulture)} -i {path.Wrap()} -map 0:v:0 -an -sn " +
                            $"-frames:v {frames} -vf signalstats,metadata=mode=print -f null -",
                        LogLevel = "info", // metadata=print speaks at info level, and quieter drops it
                        ProcessType = OS.NmkoderProcess.ProcessType.Background,
                        CanCancelTask = false,
                        LoggingMode = AvProcess.LogMode.Hidden,
                    };

                    string output = await AvProcess.RunFfmpeg(settings);

                    foreach (Match m in Regex.Matches(output, @"signalstats\.YMAX=(\d+)"))
                        maxCode = Math.Max(maxCode, m.Groups[1].Value.GetInt());
                }

                if (maxCode < 0)
                    return 0;

                return PqCodeToNits(maxCode, bitDepth, fullRange);
            }
            catch (Exception e)
            {
                Logger.Log($"Measuring the file's peak brightness failed: {e.Message}", true, level: Logger.Level.Debug);
                return 0;
            }
        }

        private static string ReadProbeValue(string probeOutput, string key)
        {
            return probeOutput.SplitIntoLines().FirstOrDefault(l => l.StartsWith($"{key}="))?.Split('=').Last() ?? "";
        }

        /// <summary> The bit depth a decoded pixel format's values are in, read off the name ffprobe
        /// prints - "yuv420p10le" is 10, "yuv420p" is 8 - because that is the scale signalstats reports
        /// its maxima in. 0 for a name that carries no recognisable depth, which callers treat as
        /// unmeasurable rather than guessing one. </summary>
        private static int GetBitDepth(string pixFmt)
        {
            Match m = Regex.Match(pixFmt ?? "", @"p(\d{2})(?:le|be)?$");

            if (m.Success)
                return m.Groups[1].Value.GetInt();

            return pixFmt.IsNotEmpty() ? 8 : 0;
        }

        /// <summary>
        /// One luma code value back into the nits the PQ curve says it stands for - ST 2084's EOTF,
        /// with the code first normalised out of the file's own range: limited puts black at 64 and
        /// white at 940 in 10-bit terms, scaled by 2^(depth-10) for other depths, where full range is
        /// the plain 0..2^depth-1.
        /// </summary>
        public static double PqCodeToNits(int code, int bitDepth, bool fullRange)
        {
            double e;

            if (fullRange)
            {
                e = code / (Math.Pow(2, bitDepth) - 1);
            }
            else
            {
                double scale = Math.Pow(2, bitDepth - 10);
                e = (code - 64 * scale) / (876 * scale);
            }

            e = Math.Clamp(e, 0, 1);

            // ST 2084 constants, as the spec writes them
            const double m1 = 2610d / 16384d, m2 = 2523d / 4096d * 128d;
            const double c1 = 3424d / 4096d, c2 = 2413d / 4096d * 32d, c3 = 2392d / 4096d * 32d;

            double p = Math.Pow(e, 1d / m2);
            double num = Math.Max(p - c1, 0);
            double den = c2 - c3 * p;

            return 10000d * Math.Pow(num / den, 1d / m1);
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
