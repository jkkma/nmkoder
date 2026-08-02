using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.Media;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Nmkoder.UI.Tasks
{
    /// <summary>
    /// Deinterlaces a file into a new one and stops there. The output is the deliverable: a
    /// progressive, near-lossless copy of a capture, written beside the source and left alone.
    /// <para/>
    /// That is the whole of it, and the restraint is the point. This used to load its own result into
    /// the file list, because it was the way to get QTGMC onto the AV1AN tab - which now runs QTGMC
    /// itself, as a pass of its own, without anyone having to visit a utility first. A utility that
    /// exports a file has no business rearranging the file list on the way out, so it no longer does;
    /// a tab that encodes has no business making the user go and produce an input by hand, so it no
    /// longer does either.
    /// <para/>
    /// What it is still worth doing on its own account is looking at the result. Field order and
    /// combing are precisely what goes wrong on a tape capture, and finding out after a night of AV1
    /// encoding is the expensive way to find out.
    /// </summary>
    class UtilDeinterlace
    {
        /// <summary>
        /// What this utility will run, which is its own setting and nobody else's.
        /// <para/>
        /// It used to read the Quick Convert tab's Deinterlace row, on the reasoning that the mode and
        /// the preset should be picked in one place. That is only true of someone who uses both tabs,
        /// and this one exports a file where that tab encodes one - so its answer to "what should
        /// happen to a progressive source" is not the same answer, and sharing the setting made one of
        /// the two wrong. Configured from the card, persisted across sessions.
        /// </summary>
        public static DeinterlaceRequest Settings = new DeinterlaceRequest();

        /// <summary>
        /// QTGMC outright rather than Automatic, which is the one place this deliberately differs from
        /// the encode tabs. Automatic is right on a tab that encodes whatever you give it, where doing
        /// nothing to a progressive file is the desired outcome. Here doing nothing means writing a
        /// re-encoded copy of the source for no reason - and someone who reached for a utility called
        /// Deinterlace Video has already decided their file is interlaced.
        /// </summary>
        public static DeinterlaceRequest Defaults()
        {
            return new DeinterlaceRequest { Mode = DeinterlaceMode.Qtgmc, QtgmcPreset = Qtgmc.DefaultPreset, DoubleRate = true };
        }

        public static void LoadSettings()
        {
            DeinterlaceRequest fallback = Defaults();
            int mode = Config.Get(Config.Key.UtilDeinterlaceMode, ((int)fallback.Mode).ToString()).GetInt();

            Settings = new DeinterlaceRequest
            {
                Mode = Enum.IsDefined(typeof(DeinterlaceMode), mode) ? (DeinterlaceMode)mode : fallback.Mode,
                QtgmcPreset = Config.Get(Config.Key.UtilDeinterlacePreset, fallback.QtgmcPreset),
                DoubleRate = Config.Get(Config.Key.UtilDeinterlaceDoubleRate, fallback.DoubleRate.ToString()).GetBool(),
            };

            // A preset saved by a build that spelled them differently would otherwise reach havsfunc
            // as a name it does not know, which it raises on rather than ignores.
            if (!Qtgmc.Presets.Contains(Settings.QtgmcPreset))
                Settings.QtgmcPreset = fallback.QtgmcPreset;
        }

        public static void SaveSettings()
        {
            Config.Set(Config.Key.UtilDeinterlaceMode, ((int)Settings.Mode).ToString());
            Config.Set(Config.Key.UtilDeinterlacePreset, Settings.QtgmcPreset);
            Config.Set(Config.Key.UtilDeinterlaceDoubleRate, Settings.DoubleRate.ToString());
        }

        /// <summary> The card's button doubles as the readout of what is configured, so the setting is
        /// visible without opening anything. </summary>
        public static string DescribeSettings()
        {
            if (Settings.Mode == DeinterlaceMode.Disabled)
                return "Disabled";

            string mode = DeinterlaceUi.GetLabel(Settings.Mode).Split(' ').First();
            bool qtgmcPossible = Settings.Mode == DeinterlaceMode.Qtgmc || Settings.Mode == DeinterlaceMode.Automatic;
            return qtgmcPossible ? $"{mode} · {Settings.QtgmcPreset}" : mode;
        }

        public static async Task Run()
        {
            Program.MainWin.SetWorking(true);

            try
            {
                MediaFile file = TrackList.current?.File;

                if (file == null)
                {
                    RunTask.Cancel("No input file loaded! Please load one first (File List).");
                    return;
                }

                if (file.IsDirectory)
                {
                    RunTask.Cancel($"'{file.Name}' is an image sequence. There are no fields in one, so there is nothing to deinterlace.");
                    return;
                }

                if (file.VideoStreams.Count < 1)
                {
                    RunTask.Cancel($"'{file.Name}' has no video track.");
                    return;
                }

                DeinterlaceRequest request = Settings;
                DeinterlacePlan plan = await Deinterlace.ResolveAsync(file, request);

                if (!plan.Runs)
                {
                    RunTask.Cancel(DescribeNothingToDo(file, request));
                    return;
                }

                string outPath = IoUtils.GetAvailableFilename($"{UiData.GetDefaultOutPath(file.SourcePath)}_deinterlaced.mkv");

                Logger.Log($"Deinterlacing '{file.Name.Trunc(40)}' with {plan.Describe()}, into {DeinterlacePass.DescribeOutput()}.");

                string problem = await DeinterlacePass.RunAsync(plan, file.ImportPath, outPath, "qtgmc-util", file);

                if (RunTask.canceled || RunTask.failed)
                    return;

                if (problem.IsNotEmpty())
                {
                    RunTask.Fail(problem);
                    return;
                }

                RunTask.ReportOutput(new[] { file.SourcePath }, outPath);
                Logger.Log($"Wrote '{Path.GetFileName(outPath)}'. It is progressive and near-lossless - encode it from " +
                    $"either encode tab if you want it smaller.");
            }
            catch (Exception e)
            {
                RunTask.Fail($"The deinterlace pass could not be made: {e.Message}");
                Logger.Log($"{e.StackTrace}", true, level: Logger.Level.Debug);
            }
            finally
            {
                Program.MainWin.SetWorking(false);
            }
        }

        /// <summary> Why a run was stopped before it wrote anything - which of the two reasons it is
        /// decides what the user would have to change. </summary>
        private static string DescribeNothingToDo(MediaFile file, DeinterlaceRequest request)
        {
            if (request.Mode == DeinterlaceMode.Disabled)
                return "This utility's Deinterlace mode is set to Disabled, so it has nothing to do.\n\n" +
                    "Press Configure on its card and set it to QTGMC, then run this again.";

            string scan = file.Interlacing == null ? "not interlaced" : file.Interlacing.DescribeOrder();

            return $"'{file.Name}' is {scan}, so there is nothing to deinterlace and this would only copy it.\n\n" +
                "If you know better than the check does, press Configure on this utility's card and set it to " +
                "QTGMC - that runs it whatever the source says about itself.";
        }
    }
}
