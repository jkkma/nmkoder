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
    /// grav1synth on its own, for the parts of a grain workflow that are not an encode.
    /// <para/>
    /// The AV1AN tab's Grain Synthesis row does the whole thing in one run - denoise, measure, encode the
    /// clean picture, deliver the table - and for almost everybody that is the right shape and this card
    /// is unnecessary. What it is for is the same work taken apart: measuring a table off a source without
    /// committing to the encode that will use it, reading the table back out of an encode somebody else
    /// made, putting grain onto a finished file, or taking it off one.
    /// <para/>
    /// A utility that writes a file and stops, like Cut and Deinterlace Video beside it. Nothing here
    /// touches the encode tabs' settings and nothing is loaded back into the file list.
    /// </summary>
    class UtilFilmGrain
    {
        /// <summary> What the card will do. </summary>
        public enum Op
        {
            /// <summary> Denoise the source, diff the two, write the table. The expensive one. </summary>
            Measure,

            /// <summary> Read the table out of an AV1 file that already carries grain. Seconds. </summary>
            Extract,

            /// <summary> Write grain into a finished AV1 file - from a table, a film stock preset or an
            /// ISO - without re-encoding anything. </summary>
            Apply,

            /// <summary> Strip every grain header out of an AV1 file. </summary>
            Remove,
        }

        public static Op Operation = Op.Measure;

        /// <summary> Measure's denoise strength, the same number and the same pass the AV1AN tab's
        /// Measured mode uses - see <see cref="GrainSynthConfig.GetDenoiseFilter"/>. </summary>
        public static int DenoiseStrength = 4;

        /// <summary>
        /// Whether Measure keeps the denoised video beside the table.
        /// <para/>
        /// Off by default because it is lossless FFV1 and therefore several times the size of the source,
        /// and the table is what the operation is for. On, it is the other half of a hand-run pipeline:
        /// the file to encode, with the table to put back afterwards. Somebody splitting the work up by
        /// hand is exactly who is here, so it is offered rather than assumed either way.
        /// </summary>
        public static bool KeepDenoised = false;

        /// <summary>
        /// Where Apply's grain comes from - a table, a preset or an ISO. A <see cref="GrainSynthConfig"/>
        /// rather than three fields of this class's own, because <see cref="Grav1synth.ApplyAsync"/>
        /// already reads one and two vocabularies for one question is how they drift apart.
        /// </summary>
        public static GrainSynthConfig ApplySource = new GrainSynthConfig { Mode = GrainSynthMode.Table };

        public static void LoadSettings()
        {
            int op = Config.Get(Config.Key.UtilFilmGrainOp, ((int)Op.Measure).ToString()).GetInt();
            Operation = Enum.IsDefined(typeof(Op), op) ? (Op)op : Op.Measure;
            DenoiseStrength = Config.Get(Config.Key.UtilFilmGrainDenoise, "4").GetInt().Clamp(1, 16);
            KeepDenoised = Config.Get(Config.Key.UtilFilmGrainKeepDenoised, "False").GetBool();

            int source = Config.Get(Config.Key.UtilFilmGrainApplySource, ((int)GrainSynthMode.Table).ToString()).GetInt();
            ApplySource = new GrainSynthConfig
            {
                Mode = Enum.IsDefined(typeof(GrainSynthMode), source) ? (GrainSynthMode)source : GrainSynthMode.Table,
                TablePath = Config.Get(Config.Key.UtilFilmGrainTable, ""),
                Preset = Config.Get(Config.Key.UtilFilmGrainPreset, ""),
                Iso = Config.Get(Config.Key.UtilFilmGrainIso, "400").GetInt(),
                Chroma = Config.Get(Config.Key.UtilFilmGrainChroma, "False").GetBool(),
            };

            // Only the three that name a grain source mean anything here; a saved Off or Encoder - which
            // this dialog cannot produce, but an older config might - would leave Apply with nothing.
            if (ApplySource.Mode != GrainSynthMode.Table && ApplySource.Mode != GrainSynthMode.Preset &&
                ApplySource.Mode != GrainSynthMode.PhotonNoise)
                ApplySource.Mode = GrainSynthMode.Table;
        }

        public static void SaveSettings()
        {
            Config.Set(Config.Key.UtilFilmGrainOp, ((int)Operation).ToString());
            Config.Set(Config.Key.UtilFilmGrainDenoise, DenoiseStrength.ToString());
            Config.Set(Config.Key.UtilFilmGrainKeepDenoised, KeepDenoised.ToString());
            Config.Set(Config.Key.UtilFilmGrainApplySource, ((int)ApplySource.Mode).ToString());
            Config.Set(Config.Key.UtilFilmGrainTable, ApplySource.TablePath);
            Config.Set(Config.Key.UtilFilmGrainPreset, ApplySource.Preset);
            Config.Set(Config.Key.UtilFilmGrainIso, ApplySource.Iso.ToString());
            Config.Set(Config.Key.UtilFilmGrainChroma, ApplySource.Chroma.ToString());
        }

        /// <summary> The card's button doubles as the readout of what is configured, as Cut's and
        /// Deinterlace Video's do. </summary>
        public static string DescribeSettings()
        {
            switch (Operation)
            {
                case Op.Measure: return $"Measure · denoise {DenoiseStrength}{(KeepDenoised ? " · keep video" : "")}";
                case Op.Extract: return "Extract table";
                case Op.Remove: return "Remove grain";
                default: return $"Apply · {DescribeApplySource()}";
            }
        }

        private static string DescribeApplySource()
        {
            switch (ApplySource.Mode)
            {
                case GrainSynthMode.Preset: return ApplySource.Preset.IsEmpty() ? "no preset picked" : ApplySource.Preset;
                case GrainSynthMode.PhotonNoise: return $"ISO {ApplySource.Iso}";
                default: return ApplySource.TablePath.IsEmpty() ? "no table picked" : Path.GetFileName(ApplySource.TablePath);
            }
        }

        /// <summary> Whether an operation reads the AV1 bitstream rather than decoded frames, and so needs
        /// an AV1 file rather than any file ffmpeg can open. Measure is the odd one out: it diffs decoded
        /// frames, so it measures grain off a ProRes master or a DVD rip just as well. </summary>
        private static bool NeedsAv1(Op op)
        {
            return op != Op.Measure;
        }

        public static async Task Run()
        {
            Program.MainWin.SetWorking(true);

            try
            {
                MediaFile file = TrackList.current?.File;
                string problem = GetProblem(file);

                if (problem.IsNotEmpty())
                {
                    RunTask.Cancel(problem);
                    return;
                }

                switch (Operation)
                {
                    case Op.Measure: await RunMeasure(file); break;
                    case Op.Extract: await RunExtract(file); break;
                    case Op.Apply: await RunApply(file); break;
                    case Op.Remove: await RunRemove(file); break;
                }
            }
            catch (Exception e)
            {
                RunTask.Fail($"The film grain utility could not finish: {e.Message}");
                Logger.Log($"{e.StackTrace}", true, level: Logger.Level.Debug);
            }
            finally
            {
                Program.MainWin.SetWorking(false);
            }
        }

        /// <summary> Everything that stops a run before it writes anything, checked in one place so the
        /// four operations below can get on with their own work. </summary>
        private static string GetProblem(MediaFile file)
        {
            if (file == null)
                return "No input file loaded! Please load one first (File List).";

            if (file.IsDirectory)
                return $"'{file.Name}' is an image sequence, which carries no bitstream to read grain out of.";

            if (file.VideoStreams.Count < 1)
                return $"'{file.Name}' has no video track.";

            if (!Grav1synth.IsAvailable())
                return Grav1synth.DescribeMissing();

            if (NeedsAv1(Operation))
            {
                string codec = (file.VideoStreams.First().Codec ?? "").ToLowerInvariant();

                if (codec != "av1")
                    return $"'{file.Name}' is {(codec.IsEmpty() ? "not AV1" : codec.ToUpperInvariant())}, and this " +
                        $"operation edits the film grain inside an AV1 bitstream - there is nowhere in another codec " +
                        $"for it to be.\n\nMeasure works on any source: it compares decoded frames, so it can read the " +
                        $"grain off this file and write a table for an AV1 encode of it.";
            }

            if (Operation == Op.Apply)
                return ApplySource.GetProblem();

            return "";
        }

        /// <summary>
        /// Denoises the source and measures the difference into a grain table beside it.
        /// <para/>
        /// The same two passes the AV1AN tab runs, which is the point - a table measured here is the table
        /// that tab would have measured, and it goes straight into its Grain table file mode. What is
        /// different is only that nothing is encoded afterwards, so the denoised copy is scratch unless it
        /// is asked for.
        /// </summary>
        private static async Task RunMeasure(MediaFile file)
        {
            var config = new GrainSynthConfig { Mode = GrainSynthMode.Measured, DenoiseStrength = DenoiseStrength };
            string outBase = UiData.GetDefaultOutPath(file.SourcePath);
            string tablePath = IoUtils.GetAvailableFilename($"{outBase}.grain.tbl");

            // Beside the table when it is being kept, in the session folder when it is not - that folder
            // is cleared with the session, so a run that dies partway does not leave a lossless copy of
            // somebody's film on their disk forever.
            string denoisedPath = KeepDenoised
                ? IoUtils.GetAvailableFilename($"{outBase}_denoised.mkv")
                : Path.Combine(Paths.GetSessionDataPath(), "grain-measure.mkv");

            long frames = (long)(file.DurationMs / 1000d * file.VideoStreams.First().Rate.GetFloat());
            TimeSpan estimate = Grav1synth.EstimateDiffTime(frames, file.VideoStreams.First().Resolution);

            Logger.Log($"Denoising '{file.Name.Trunc(40)}' with {config.GetDenoiseFilter()} into " +
                $"{DenoisePass.DescribeOutput()}, then measuring the difference with grav1synth. The measuring alone " +
                $"is about {Utils.FormatUtils.Time(estimate, allowMs: false)} for this file, and it prints no progress " +
                $"of its own.");

            string problem = await DenoisePass.RunAsync(config, file.ImportPath, denoisedPath);

            if (RunTask.canceled || RunTask.failed)
                return;

            if (problem.IsEmpty())
                problem = await Grav1synth.DiffAsync(file.ImportPath, denoisedPath, tablePath);

            if (!KeepDenoised)
                IoUtils.TryDeleteIfExists(denoisedPath);

            if (RunTask.canceled || RunTask.failed)
                return;

            if (problem.IsNotEmpty())
            {
                RunTask.Fail(problem);
                return;
            }

            RunTask.ReportOutput(new[] { file.SourcePath }, tablePath);
            Logger.Log($"Wrote '{Path.GetFileName(tablePath)}'" +
                $"{(KeepDenoised ? $" and '{Path.GetFileName(denoisedPath)}'" : "")}. Point the AV1AN tab's Grain " +
                $"Synthesis row at the table with Grain table file, and tick its Denoise at the same strength - or " +
                $"use Measured from source there and skip this step entirely.");
        }

        private static async Task RunExtract(MediaFile file)
        {
            string tablePath = IoUtils.GetAvailableFilename($"{UiData.GetDefaultOutPath(file.SourcePath)}.grain.tbl");
            Logger.Log($"Reading the grain table out of '{file.Name.Trunc(40)}'.");

            string problem = await Grav1synth.InspectAsync(file.ImportPath, tablePath);

            if (RunTask.canceled)
                return;

            if (problem.IsNotEmpty())
            {
                RunTask.Fail(problem);
                return;
            }

            RunTask.ReportOutput(new[] { file.SourcePath }, tablePath);
            Logger.Log($"Wrote '{Path.GetFileName(tablePath)}'. It describes the grain this encode already " +
                $"synthesises, so it can be applied to another encode of the same source.");
        }

        private static async Task RunApply(MediaFile file)
        {
            string ext = Path.GetExtension(file.SourcePath);
            string outPath = IoUtils.GetAvailableFilename($"{UiData.GetDefaultOutPath(file.SourcePath)}_grain{ext}");

            Logger.Log($"Writing {DescribeApplySource()} into '{file.Name.Trunc(40)}'. This rewrites the AV1 headers " +
                $"and remuxes; nothing is re-encoded, and the picture is untouched.");

            string problem = await Grav1synth.ApplyAsync(ApplySource, file.ImportPath, outPath, ApplySource.TablePath);
            await Finish(file, outPath, problem, "The grain is described in the headers, so the file is barely larger " +
                "than it was and every AV1 decoder regenerates the grain at playback.");
        }

        private static async Task RunRemove(MediaFile file)
        {
            string ext = Path.GetExtension(file.SourcePath);
            string outPath = IoUtils.GetAvailableFilename($"{UiData.GetDefaultOutPath(file.SourcePath)}_nograin{ext}");

            Logger.Log($"Stripping the film grain synthesis out of '{file.Name.Trunc(40)}'.");

            string problem = await Grav1synth.RemoveAsync(file.ImportPath, outPath);
            await Finish(file, outPath, problem, "The picture is exactly as it was coded - what is gone is the grain " +
                "the decoder was adding on top of it.");
        }

        /// <summary> What the two bitstream-rewriting operations do afterwards, which is the same thing:
        /// report the output, or take the half-written file away with the failure. </summary>
        private static async Task Finish(MediaFile file, string outPath, string problem, string note)
        {
            if (RunTask.canceled)
            {
                IoUtils.TryDeleteIfExists(outPath);
                return;
            }

            if (problem.IsNotEmpty())
            {
                IoUtils.TryDeleteIfExists(outPath);
                RunTask.Fail(problem);
                return;
            }

            RunTask.ReportOutput(new[] { file.SourcePath }, outPath);

            // Named rather than left to be found: grav1synth carries video, audio, subtitles and chapters
            // through its remux and drops attachments, which on an anime release is the subtitle fonts.
            string lost = file.AttachmentStreams.Count > 0
                ? $" Note: its {file.AttachmentStreams.Count} attachment{(file.AttachmentStreams.Count == 1 ? "" : "s")} " +
                    $"(fonts, cover art) are not carried through - grav1synth remuxes video, audio, subtitles and " +
                    $"chapters only."
                : "";

            Logger.Log($"Wrote '{Path.GetFileName(outPath)}'. {note}{lost}");
            await Task.CompletedTask;
        }
    }
}
