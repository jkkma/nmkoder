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
    /// Deinterlaces a capture once, into a near-lossless MKV meant to be encoded again - the first
    /// half of the way to get a tape onto the AV1AN tab.
    /// <para/>
    /// Doing it in two steps rather than one is not a workaround. av1an exists because a single
    /// encoder instance cannot saturate a many-core machine, so it runs several; QTGMC has no such
    /// problem, since VapourSynth already spreads one graph across every core. Put the two together
    /// and the filter is evaluated once for scene detection - a whole pass whose pixels are thrown
    /// away - once per chunk, and again for every probe a target-quality mode runs. Deinterlacing
    /// first pays for QTGMC exactly once, and hands av1an a progressive, seekable, frame-accurate
    /// file with its audio still attached.
    /// <para/>
    /// It also puts the result somewhere it can be looked at. Field order and combing are precisely
    /// what goes wrong on a tape capture, and finding out after a night of AV1 encoding is the
    /// expensive way to find out.
    /// </summary>
    class UtilDeinterlace
    {
        /// <summary>
        /// Near-lossless, and deliberately not lossless: FFV1 on 720x480 at one frame per field runs
        /// to tens of gigabytes an hour, where this is single digits and nothing the re-encode does
        /// can tell the difference. The preset is x264's own default, which at standard definition is
        /// far quicker than QTGMC in front of it - a slower one would buy size that the next encode
        /// throws away anyway.
        /// </summary>
        private const int Crf = 12;
        private const string Preset = "medium";

        /// <summary>
        /// What this utility will run, which is its own setting and nobody else's.
        /// <para/>
        /// It used to read the Quick Convert tab's Deinterlace row, on the reasoning that the mode and
        /// the preset should be picked in one place. That is only true of someone who uses both tabs.
        /// This utility exists *because* the AV1AN tab cannot run QTGMC, so the person reaching for it
        /// is by definition encoding somewhere else - and sending them to a tab they do not use, to
        /// change a setting that also changes what that tab does, is a worse trade than one more
        /// dialog. Configured from the card, persisted across sessions.
        /// </summary>
        public static DeinterlaceRequest Settings = new DeinterlaceRequest();

        /// <summary>
        /// QTGMC outright rather than Automatic, which is the one place this deliberately differs from
        /// the encode tabs. Automatic is right on a tab that encodes whatever you give it, where doing
        /// nothing to a progressive file is the desired outcome. Here doing nothing means writing a
        /// re-encoded copy of the source for no reason - and someone who reached for a utility called
        /// Deinterlace For Encoding has already decided their file is interlaced.
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
            string vsLogPath = "";

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
                Directory.CreateDirectory(Path.GetDirectoryName(outPath));

                Logger.Log($"Deinterlacing '{file.Name.Trunc(40)}' with {plan.Describe()}, into a near-lossless MKV " +
                    $"(x264, CRF {Crf}) for encoding afterwards.");

                string pipe = "";
                string pipeIn = "";
                string videoMap = "0:v:0";

                if (plan.UsesPipe)
                {
                    // The same shape the Quick Convert tab uses: the pipe is the last input, so the
                    // source keeps index 0 and every other track is still mapped out of it.
                    vsLogPath = Path.Combine(Paths.GetSessionDataPath(), "qtgmc-util.log");
                    IoUtils.TryDeleteIfExists(vsLogPath);
                    string script = Qtgmc.WriteScript(plan, file.ImportPath, Path.Combine(Paths.GetSessionDataPath(), "qtgmc-util.vpy"));
                    pipe = Qtgmc.BuildVspipeCommand(script, vsLogPath);
                    pipeIn = "-f yuv4mpegpipe -thread_queue_size 1024 -i -";
                    videoMap = "1:v:0";
                }

                string filter = plan.GetFfmpegFilter();
                string vf = filter.IsEmpty() ? "" : $"-vf {filter}";

                // Everything but the video is copied: this file exists to be encoded again, and
                // re-encoding its audio on the way through would cost quality for nothing. Data
                // streams are dropped because Matroska stores none.
                string args = $"-i {file.ImportPath.Wrap()} {pipeIn} -map {videoMap} -map 0:a? -map 0:s? -map 0:t? " +
                    $"-c:v libx264 -crf {Crf} -preset {Preset} {vf} -c:a copy -c:s copy -dn " +
                    $"-map_metadata 0 -map_chapters 0 {outPath.Wrap()}";

                var settings = new AvProcess.FfmpegSettings
                {
                    Args = args,
                    LoggingMode = AvProcess.LogMode.OnlyLastLine,
                    ProgressBar = true,
                    ReportFailure = vsLogPath.IsEmpty(), // Where VapourSynth feeds it, its verdict comes first
                    PipeFrom = pipe,
                    ExtraPathDirs = vsLogPath.IsEmpty() ? new string[0] : Qtgmc.GetPathDirs(),
                };

                await AvProcess.RunFfmpeg(settings);

                if (RunTask.canceled)
                    return;

                if (vsLogPath.IsNotEmpty())
                {
                    // ffmpeg reads a script that died as an input that ended, so it finishes cleanly
                    // over a file missing the rest of the video - see Qtgmc.ReadRunProblem.
                    string vsProblem = Qtgmc.ReadRunProblem(vsLogPath);

                    if (vsProblem.IsNotEmpty())
                    {
                        RunTask.Fail($"The deinterlaced video was cut short.\n\n{vsProblem}");
                        return;
                    }

                    if (settings.Problem.IsNotEmpty())
                    {
                        RunTask.Fail(settings.Problem);
                        return;
                    }
                }

                if (RunTask.failed)
                    return;

                if (!RunTask.OutputExists(outPath))
                {
                    RunTask.Fail($"FFmpeg reported no error, but '{Path.GetFileName(outPath)}' was not written.");
                    return;
                }

                RunTask.ReportOutput(new[] { file.SourcePath }, outPath);
                await LoadResult(file, outPath);
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

        /// <summary>
        /// Loads the finished file, so the encode it was made for is a tab away rather than a trip
        /// back through the file picker.
        /// <para/>
        /// Only when the list held nothing but the source. In muxing mode the file list *is* the set
        /// of inputs, so quietly adding one to a list the user has arranged would change what their
        /// next mux produces; and a batch is stepping through a queue that must not move underneath
        /// it. Both of those are told where the file is instead.
        /// </summary>
        private static async Task LoadResult(MediaFile source, string outPath)
        {
            bool listIsJustTheSource = FileList.Items.Count == 1 && FileList.Items[0].File == source;

            if (RunTask.runningBatch || !listIsJustTheSource)
            {
                Logger.Log($"Load '{Path.GetFileName(outPath)}' to encode it - it is progressive now, so the AV1AN tab " +
                    $"can chunk it with nothing in front of the encoder.");
                return;
            }

            try
            {
                await FileList.LoadFiles(new[] { outPath }, true);

                if (FileList.Items.Count > 0)
                    await TrackList.SetAsMainFile(FileList.Items.Last(), switchToTrackList: false, setWorking: false);

                Logger.Log($"'{Path.GetFileName(outPath)}' is loaded and ready for the AV1AN tab. It is progressive, " +
                    $"so scene detection and target-quality probes read it with nothing in front of them.");
            }
            catch (Exception e)
            {
                // The encode is done and on disk; failing to put it in the list is not a failed task.
                Logger.Log($"Could not load '{Path.GetFileName(outPath)}' into the file list: {e.Message}");
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
