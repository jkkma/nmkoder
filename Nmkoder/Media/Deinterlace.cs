using Nmkoder.Data;
using Nmkoder.Data.Streams;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.OS;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Nmkoder.Media
{
    /// <summary>
    /// Turns what a tab's Deinterlace controls are asking for into what will actually run against one
    /// file. Everything conditional lives here: whether the source is interlaced at all, whether
    /// QTGMC can be reached, and what to fall back to when it cannot.
    /// </summary>
    class Deinterlace
    {
        /// <summary>
        /// The settled plan for <paramref name="file"/>. Never throws and never returns null - a
        /// question it cannot answer becomes "do nothing", because deinterlacing progressive video
        /// softens it for no reason and is the worse mistake of the two.
        /// </summary>
        public static async Task<DeinterlacePlan> ResolveAsync(MediaFile file, DeinterlaceRequest req, bool quiet = false)
        {
            var plan = new DeinterlacePlan { File = file, QtgmcPreset = req.QtgmcPreset, DoubleRate = req.DoubleRate };

            try
            {
                if (file == null || file.VideoStreams.Count < 1 || req.Mode == DeinterlaceMode.Disabled)
                    return plan;

                InterlaceInfo info = await InterlaceDetect.GetAsync(file, quiet);
                plan.TopFieldFirst = info.TopFieldFirst;

                if (req.Mode == DeinterlaceMode.Automatic && !info.Interlaced)
                    return plan;

                if (req.Mode == DeinterlaceMode.Automatic && !quiet)
                {
                    Logger.Log($"'{file.Name.Trunc(40)}' is {info.DescribeOrder()} - {info.Evidence}.");

                    if (info.Telecined)
                        Logger.Log("Note: most of its frames repeat a field, which is how 24 fps film is carried in a 30 fps stream. " +
                            "Deinterlacing it works, but undoing the pulldown instead would give back the original film frames.");
                }

                DeinterlaceEngine wanted =
                    req.Mode == DeinterlaceMode.Yadif ? DeinterlaceEngine.Yadif :
                    req.Mode == DeinterlaceMode.Bwdif ? DeinterlaceEngine.Bwdif : DeinterlaceEngine.Qtgmc;

                plan.Engine = wanted == DeinterlaceEngine.Qtgmc
                    ? await ResolveQtgmc(file, req, info, quiet)
                    : wanted;

                // Only worth saying for a mode the user picked outright: Automatic has already said
                // what it found, and saying it twice is noise.
                if (plan.Runs && req.Mode != DeinterlaceMode.Automatic && !quiet && !info.Interlaced)
                    Logger.Log($"Deinterlacing '{file.Name.Trunc(40)}' as asked, although it reads as {info.DescribeOrder()}" +
                        $"{(info.Order == FieldOrder.Unknown ? " - the fields are being taken as top-first" : "")}.");
            }
            catch (Exception e)
            {
                Logger.Log($"Could not work out how to deinterlace '{file?.Name}': {e.Message}", true);
                plan.Engine = DeinterlaceEngine.None;
            }

            return plan;
        }

        /// <summary>
        /// QTGMC, or the ffmpeg deinterlacer that stands in for it, with the reason said out loud.
        /// Falling back rather than refusing is deliberate: the encode the user asked for is still
        /// worth running, and bwdif on an interlaced source is far closer to right than leaving the
        /// combing in - but silently substituting a different filter for the one that was picked
        /// would leave the quality difference unexplained.
        /// </summary>
        private static async Task<DeinterlaceEngine> ResolveQtgmc(MediaFile file, DeinterlaceRequest req, InterlaceInfo info, bool quiet)
        {
            string why = GetQtgmcProblem(file, req);

            if (why.IsEmpty() && !await Qtgmc.IsAvailableAsync())
                why = Qtgmc.UnavailableReason;

            if (why.IsEmpty())
                return DeinterlaceEngine.Qtgmc;

            if (!quiet)
                Logger.Log($"Deinterlacing with bwdif rather than QTGMC: {why}.");

            return DeinterlaceEngine.Bwdif;
        }

        /// <summary>
        /// Why this file or this tab cannot go through QTGMC, or "" when nothing is in the way. These
        /// are the conditions that can be seen without running anything; whether the machine has a
        /// working VapourSynth is a separate question, asked after these.
        /// </summary>
        private static string GetQtgmcProblem(MediaFile file, DeinterlaceRequest req)
        {
            if (req.QtgmcUnavailableHere.IsNotEmpty())
                return req.QtgmcUnavailableHere;

            // QTGMC reads the file itself through a VapourSynth source plugin. An image sequence is
            // not a file - it reaches ffmpeg as a generated concat list - and no source plugin opens
            // one of those.
            if (file.IsDirectory)
                return "image sequences cannot be read by a VapourSynth source plugin";

            string pixFmt = (file.VideoStreams.FirstOrDefault()?.PixelFormat ?? "").ToLower();

            // QTGMC's motion search runs on luma, so it rejects RGB outright - and VSPipe's y4m
            // output cannot carry it either.
            if (pixFmt.StartsWith("rgb") || pixFmt.StartsWith("bgr") || pixFmt.StartsWith("gbr"))
                return $"QTGMC does not work on RGB video, and this file is {pixFmt}";

            return "";
        }

        /// <summary>
        /// The line under the Deinterlace dropdown: what will happen to the loaded file, said in terms
        /// of that file. Built without running anything - the frame scan and the VapourSynth check are
        /// only consulted if they have already answered - so this is safe to call from a UI handler.
        /// </summary>
        public static string DescribeForUi(MediaFile file, DeinterlaceRequest req)
        {
            if (file == null || file.VideoStreams.Count < 1)
                return "Load a file to see what this will do to it.";

            if (req.Mode == DeinterlaceMode.Disabled)
                return "Interlaced sources are encoded with their combing left in.";

            InterlaceInfo info = file.Interlacing;

            if (info == null)
                return "Checking whether this file is interlaced…";

            if (req.Mode == DeinterlaceMode.Automatic && !info.Interlaced)
                return $"This file is {info.DescribeOrder()}, so nothing will be deinterlaced.";

            // One line, and it must stay one line - the label sits in a form row that would reflow
            // every time a file is loaded if it wrapped. Hence the short forms: the dropdown's
            // tooltip carries the standing explanations, this says what happens to *this* file.
            string rate = req.DoubleRate ? $" at {GetDoubledRate(file)}" : ", keeping the frame rate";
            string what = req.Mode == DeinterlaceMode.Automatic
                ? $"This file is {info.DescribeOrder()}"
                : $"Deinterlacing whatever the source is (this one is {info.DescribeOrder()})";

            DescribeEngine(file, req, out string engine, out string caveat);

            return $"{what} - {engine} will deinterlace it{rate}{caveat}." +
                (info.Telecined ? " Its fields repeat, which usually means film carried by pulldown." : "");
        }

        /// <summary> Which deinterlacer the readout should name, and whatever has to be said about
        /// that choice - a "because …" where QTGMC was asked for and cannot be had. </summary>
        private static void DescribeEngine(MediaFile file, DeinterlaceRequest req, out string engine, out string caveat)
        {
            engine = "bwdif";
            caveat = "";

            if (req.Mode == DeinterlaceMode.Yadif)
            {
                engine = "yadif";
                return;
            }

            // The tab having no way to run QTGMC is a standing fact about the tab rather than about
            // this file, so it lives in the dropdown's tooltip: it is several lines long, and this
            // one is rewritten every time a file is loaded.
            if (req.Mode == DeinterlaceMode.Bwdif || req.QtgmcUnavailableHere.IsNotEmpty())
                return;

            string why = GetQtgmcProblem(file, req);

            if (why.IsNotEmpty())
            {
                caveat = $", because {why}";
                return;
            }

            // Asked of the filesystem rather than of the probe: a machine with no VSPipe at all is a
            // definite answer, and it is the one case where nothing would ever come along to correct
            // an "if VapourSynth can run it here" - the probe is not started when there is no VSPipe.
            if (Qtgmc.GetVspipePath().IsEmpty())
            {
                caveat = Shell.IsWindows
                    ? ", because VapourSynth is not bundled with this build and VSPipe is not on your PATH"
                    : ", because VapourSynth is not installed here";
                return;
            }

            if (Qtgmc.KnownAvailability == false)
            {
                caveat = $", because {Qtgmc.UnavailableReason}";
                return;
            }

            engine = $"QTGMC ({req.QtgmcPreset})";

            if (Qtgmc.KnownAvailability == null)
                caveat = " if VapourSynth can run it here";
        }

        /// <summary> The frame rate a bob produces, for the readout - "59.94 fps" reads better than
        /// "twice the source rate", and it is the number that shows up in the output file. </summary>
        private static string GetDoubledRate(MediaFile file)
        {
            VideoStream vs = file.VideoStreams.FirstOrDefault();
            float rate = vs == null ? 0f : vs.Rate.GetFloat() * 2f;
            return rate > 0.01f ? $"{rate.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} fps" : "double the frame rate";
        }

        /// <summary> The source rate as the rest of the pipeline should measure it: a bob has already
        /// doubled it by the time any other filter sees a frame, so the Frame Rate box has to compare
        /// against that and not against what the file says. </summary>
        public static Fraction GetEffectiveSourceRate(VideoStream vs, DeinterlacePlan plan)
        {
            if (vs == null || plan == null || !plan.DoublesFrameRate)
                return vs?.Rate ?? Fraction.Zero;

            return new Fraction(vs.Rate.Numerator * 2, vs.Rate.Denominator);
        }
    }
}
