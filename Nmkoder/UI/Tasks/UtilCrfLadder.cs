using Nmkoder.Data;
using Nmkoder.Data.Codecs;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.Media;
using Nmkoder.OS;
using Nmkoder.Utils;
using Nmkoder.Views;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Nmkoder.UI.Tasks
{
    /// <summary>
    /// Answers "what CRF for this source" by encoding a few short sections of it at several CRFs and
    /// measuring what came out - the question a whole tab of settings cannot answer, because the right
    /// number is a property of the content and not of the encoder.
    /// <para/>
    /// A utility that produces a report and stops, like Read Bitrates and the bitrate chart beside it.
    /// Nothing here reaches either encode tab and neither tab reads any of it: the answer is a number
    /// to type into one, which is a thing a person does rather than a setting to transfer. That is also
    /// why the encoder, preset and colour format are configured here rather than read off a tab - see
    /// <see cref="UtilDeinterlace.Settings"/>, which makes the same argument for the same reason.
    /// <para/>
    /// The sampled sections are cut out losslessly and the encodes are measured against those cuts
    /// rather than against the source. That is what makes the comparison exact: a copy carries the
    /// source's own frames, so the reference and the encoder's input are the same pictures with no
    /// seeking, scaling or frame alignment anywhere between them.
    /// </summary>
    class UtilCrfLadder
    {
        /// <summary>
        /// The encoders offered. ffmpeg's own, because that is what is bundled and what can be run
        /// once per sample without av1an's chunking, scene detection and VapourSynth in the way - all
        /// of which exist to make a long encode parallel and cost more than they save on ten seconds.
        /// <para/>
        /// The AV1AN tab's SVT-AV1 is a different binary from the one here (svt-av1-hdr against the
        /// libsvtav1 compiled into ffmpeg), so a CRF read off this ladder is a starting point over
        /// there rather than the same number. <see cref="Describe"/> says so where it can be seen.
        /// </summary>
        public static readonly CodecUtils.VideoCodec[] Encoders = new[]
        {
            CodecUtils.VideoCodec.LibSvtAv1,
            CodecUtils.VideoCodec.LibAomAv1,
            CodecUtils.VideoCodec.Libx265,
            CodecUtils.VideoCodec.Libx264,
            CodecUtils.VideoCodec.LibVpx,
            CodecUtils.VideoCodec.H265Nvenc,
            CodecUtils.VideoCodec.H264Nvenc,
        };

        public static CodecUtils.VideoCodec Encoder = CodecUtils.VideoCodec.LibSvtAv1;

        /// <summary> Empty means the encoder's own default, which is what keeps this right across an
        /// encoder change: the presets are named differently for every one of them, so a string saved
        /// under x264 ("slow") is not a preset SVT-AV1 has. </summary>
        public static string Preset = "";

        /// <summary> An index into the encoder's own <see cref="IEncoder.ColorFormats"/>, or -1 for its
        /// default. Same argument as the preset - the lists differ per encoder. </summary>
        public static int ColorFormatIndex = -1;

        /// <summary> The box's text rather than the parsed values, so what was typed survives an
        /// encoder change that would clamp it. Empty means <see cref="CrfLadder.DefaultCrfs"/>. </summary>
        public static string Crfs = "";

        public static int SampleCount = 3;
        public static int SampleSeconds = 10;
        public static CrfLadder.Metric Score = CrfLadder.Metric.Vmaf;
        public static int VmafModel = 0;

        /// <summary>
        /// Whether the sample encodes are kept beside the source rather than thrown away with the
        /// session. Off by default: the point of the run is the table, and a ladder over three samples
        /// leaves a dozen short clips behind. On, they are what to actually look at - a VMAF of 94 and
        /// a look at the frames are different questions, and the second one is why somebody would want
        /// the files.
        /// </summary>
        public static bool KeepSamples = false;

        /// <summary> The last finished run, so the results window can be reopened from the card without
        /// encoding anything again. </summary>
        public static CrfLadder.Result LastResult;

        public static IEncoder GetEncoder()
        {
            return CodecUtils.GetCodec(Encoders.Contains(Encoder) ? Encoder : Encoders[0]);
        }

        /// <summary> The preset that will actually be used - the saved one where the current encoder
        /// has it, its own default where it does not. </summary>
        public static string GetPreset()
        {
            IEncoder enc = GetEncoder();

            if (enc.Presets == null || enc.Presets.Length < 1)
                return "";

            return enc.Presets.Contains(Preset) ? Preset : enc.Presets[enc.PresetDefault.Clamp(0, enc.Presets.Length - 1)];
        }

        public static int GetColorFormatIndex()
        {
            IEncoder enc = GetEncoder();

            if (enc.ColorFormats == null || enc.ColorFormats.Count < 1)
                return -1;

            return ColorFormatIndex >= 0 && ColorFormatIndex < enc.ColorFormats.Count ? ColorFormatIndex : enc.ColorFormatDefault;
        }

        public static string GetPixelFormat()
        {
            IEncoder enc = GetEncoder();
            int idx = GetColorFormatIndex();
            return idx < 0 ? "" : Data.Colors.PixFmtUtils.GetFormat(enc.ColorFormats[idx]).Name;
        }

        public static int[] GetCrfs()
        {
            return CrfLadder.ParseCrfs(Crfs, GetEncoder());
        }

        #region Settings

        public static void LoadSettings()
        {
            int enc = Config.Get(Config.Key.UtilCrfLadderEncoder, ((int)CodecUtils.VideoCodec.LibSvtAv1).ToString()).GetInt();
            Encoder = Encoders.Contains((CodecUtils.VideoCodec)enc) ? (CodecUtils.VideoCodec)enc : Encoders[0];
            Preset = Config.Get(Config.Key.UtilCrfLadderPreset, "");
            ColorFormatIndex = Config.Get(Config.Key.UtilCrfLadderColors, "-1").GetInt();
            Crfs = Config.Get(Config.Key.UtilCrfLadderCrfs, "");
            SampleCount = Config.Get(Config.Key.UtilCrfLadderSamples, "3").GetInt().Clamp(1, 8);
            SampleSeconds = Config.Get(Config.Key.UtilCrfLadderSampleSecs, "10").GetInt().Clamp(2, 120);
            KeepSamples = Config.Get(Config.Key.UtilCrfLadderKeep, "False").GetBool();
            VmafModel = Config.Get(Config.Key.UtilCrfLadderVmafModel, "0").GetInt().Clamp(0, 2);

            int metric = Config.Get(Config.Key.UtilCrfLadderMetric, ((int)CrfLadder.Metric.Vmaf).ToString()).GetInt();
            Score = Enum.IsDefined(typeof(CrfLadder.Metric), metric) ? (CrfLadder.Metric)metric : CrfLadder.Metric.Vmaf;
        }

        public static void SaveSettings()
        {
            Config.Set(Config.Key.UtilCrfLadderEncoder, ((int)Encoder).ToString());
            Config.Set(Config.Key.UtilCrfLadderPreset, Preset);
            Config.Set(Config.Key.UtilCrfLadderColors, ColorFormatIndex.ToString());
            Config.Set(Config.Key.UtilCrfLadderCrfs, Crfs);
            Config.Set(Config.Key.UtilCrfLadderSamples, SampleCount.ToString());
            Config.Set(Config.Key.UtilCrfLadderSampleSecs, SampleSeconds.ToString());
            Config.Set(Config.Key.UtilCrfLadderKeep, KeepSamples.ToString());
            Config.Set(Config.Key.UtilCrfLadderMetric, ((int)Score).ToString());
            Config.Set(Config.Key.UtilCrfLadderVmafModel, VmafModel.ToString());
        }

        /// <summary>
        /// The card's button doubles as the readout of what is configured, as Cut's, Deinterlace
        /// Video's and Film Grain's do - and it shares its row with the card's title inside a 420px
        /// card, so it is written to stay near the length of theirs. A full list of eight CRFs is
        /// three times the width of "QTGMC · Very Slow" and pushed the title off the card, so past
        /// three values it becomes a count and a range. The sampling is not in it at all: it is on the
        /// dialog and in that dialog's own readout, and it is the part nobody changes.
        /// </summary>
        public static string DescribeSettings()
        {
            int[] crfs = GetCrfs();
            string enc = GetEncoder().FriendlyName.Split('(').Last().TrimEnd(')');
            string ladder = crfs.Length <= 3 ? $"CRF {CrfLadder.Format(crfs)}" : $"{crfs.Length} CRFs, {crfs.First()}-{crfs.Last()}";
            return $"{enc} · {ladder}";
        }

        /// <summary> What the run will do, in a sentence, for the dialog's readout. </summary>
        public static string Describe(MediaFile file)
        {
            IEncoder enc = GetEncoder();
            int[] crfs = GetCrfs();
            int runs = crfs.Length * (file == null ? SampleCount : CrfLadder.PlanSamples(file.DurationMs, SampleCount, SampleSeconds).Count);

            string source = file == null
                ? "No file is loaded. The settings are saved either way, and applied to whichever file is loaded when this runs."
                : $"{file.Name.Trunc(50)} — {FormatUtils.Time(file.DurationMs)}, sampled " +
                  $"{DescribeSampling(file)}.";

            string scoring = Score == CrfLadder.Metric.None ? "no quality metric"
                : Score == CrfLadder.Metric.Vmaf ? $"VMAF ({GetVmafModelName()})"
                : CrfLadder.MetricName(Score);

            string ssimu2Note = Score == CrfLadder.Metric.Ssimulacra2
                ? " SSIMULACRA2 is scored through VapourSynth's vszip plugin, bundled on Windows only; the run stops with a " +
                  "reason if it cannot be computed here."
                : "";

            return $"{source}\n\n{runs} sample encode{(runs == 1 ? "" : "s")} with {enc.FriendlyName} at preset " +
                $"{GetPreset()}, {GetPixelFormat()}, scored with {scoring}. A CRF is only meaningful for the encoder " +
                $"and preset that produced it - the AV1AN tab drives different binaries, so treat a number from here " +
                $"as a starting point there rather than as the same setting.{ssimu2Note}";
        }

        private static string DescribeSampling(MediaFile file)
        {
            List<CrfLadder.Sample> samples = CrfLadder.PlanSamples(file.DurationMs, SampleCount, SampleSeconds);
            long total = samples.Sum(x => x.Ms);
            double share = file.DurationMs > 0 ? total * 100d / file.DurationMs : 0;
            return $"{samples.Count}×{FormatUtils.Time(samples[0].Ms)} ({share.ToString("0.#")}% of it)";
        }

        public static string GetVmafModelName()
        {
            if (VmafModel == 1) return "vmaf_v0.6.1neg";
            if (VmafModel == 2) return "vmaf_4k_v0.6.1";
            return "vmaf_v0.6.1";
        }

        #endregion

        public static async Task Run()
        {
            Program.MainWin.SetWorking(true);
            string workDir = "";

            try
            {
                MediaFile file = TrackList.current?.File;
                string problem = GetProblem(file);

                if (problem.IsNotEmpty())
                {
                    RunTask.Cancel(problem);
                    return;
                }

                // Checked before a single frame is cut or encoded, because the metric is the one thing
                // that can be impossible on this machine and it is not cheap to find out at the end: a
                // ladder with SSIMULACRA2 picked on a build with no vszip would otherwise encode the
                // whole grid and then report every rung on size alone. The other two metrics are ffmpeg
                // filters that are always present.
                if (Score == CrfLadder.Metric.Ssimulacra2 && !await Media.Ssimulacra2.IsAvailableAsync())
                {
                    RunTask.Cancel($"SSIMULACRA2 cannot be scored here.\n\n{Media.Ssimulacra2.GetUnavailableReason()}\n\n" +
                        $"Pick VMAF or XPSNR in the Sample Encodes settings, or score with nothing and read the sizes alone.");
                    return;
                }

                workDir = GetWorkDir(file);
                Directory.CreateDirectory(workDir);
                IEncoder enc = GetEncoder();
                int[] crfs = GetCrfs();

                var result = new CrfLadder.Result
                {
                    FileName = file.Name,
                    SourceMs = file.DurationMs,
                    SourceBytes = Math.Max(0, IoUtils.GetFilesize(file.SourcePath)),
                    EncoderName = enc.FriendlyName,
                    Preset = GetPreset(),
                    PixelFormat = GetPixelFormat(),
                    ScoredWith = Score,
                    VmafModel = GetVmafModelName(),
                    Samples = CrfLadder.PlanSamples(file.DurationMs, SampleCount, SampleSeconds),
                };

                if (result.Samples.Count < SampleCount)
                    Logger.Log($"'{file.Name.Trunc(40)}' is {FormatUtils.Time(file.DurationMs)} long, which does not hold " +
                        $"{SampleCount} sections of {SampleSeconds}s - taking {result.Samples.Count} instead.");

                Logger.Log($"Cutting {result.Samples.Count} sample{(result.Samples.Count == 1 ? "" : "s")} out of " +
                    $"'{file.Name.Trunc(40)}' and encoding {crfs.Length} of them at CRF {CrfLadder.Format(crfs)} with " +
                    $"{enc.FriendlyName}, preset {GetPreset()}, {GetPixelFormat()}.");

                if (!await ExtractSamples(file, result, workDir))
                    return;

                if (!await EncodeLadder(file, result, crfs, enc, workDir))
                    return;

                LastResult = result;
                Report(result);

                if (KeepSamples)
                {
                    RunTask.ReportOutput(new[] { file.SourcePath }, workDir);
                    Logger.Log($"The samples and their encodes are in '{Path.GetFileName(workDir)}', beside the source - " +
                        $"one lossless cut per section and one encode per rung, so a score can be checked against the frames.");
                }

                await CrfLadderResultsWindow.ShowAsync(result);
            }
            catch (Exception e)
            {
                RunTask.Fail($"The CRF ladder could not finish: {e.Message}");
                Logger.Log($"{e.StackTrace}", true, level: Logger.Level.Debug);
            }
            finally
            {
                // The cuts and the encodes both, unless they were asked for - a ladder over three
                // samples writes a dozen short clips, and they are scratch by default. A run that was
                // stopped or failed clears them either way: what is left of it is a partial ladder,
                // which is worth nothing and is several hundred megabytes of somebody's disk.
                if (workDir.IsNotEmpty() && (!KeepSamples || RunTask.canceled || RunTask.failed))
                    IoUtils.TryDeleteIfExists(workDir);

                FfmpegOutputHandler.overrideTargetDurationMs = -1;
                Program.MainWin.SetWorking(false);
            }
        }

        /// <summary>
        /// Where the cuts and the sample encodes are written. Beside the source when they are being
        /// kept, in the session folder when they are not - that folder goes with the session, so a run
        /// that dies partway does not leave a pile of clips behind, and a run that is being kept puts
        /// them where the source is rather than four levels down inside the app's data directory.
        /// </summary>
        private static string GetWorkDir(MediaFile file)
        {
            if (!KeepSamples)
                return Path.Combine(Paths.GetSessionDataPath(), "crf-ladder");

            string preferred = $"{UiData.GetDefaultOutPath(file.SourcePath)}_crfladder";
            string path = preferred;

            // GetAvailableFilename asks File.Exists, which is false for a directory - so a second run
            // on the same file would have written its samples into the first one's folder beside them.
            for (int i = 1; Directory.Exists(path) && i <= 9999; i++)
                path = $"{preferred}_{i}";

            return path;
        }

        /// <summary> Everything that stops a run before it encodes anything. </summary>
        private static string GetProblem(MediaFile file)
        {
            if (file == null)
                return "No input file loaded! Please load one first (File List).";

            if (file.IsDirectory)
                return $"'{file.Name}' is an image sequence. Sections cannot be cut out of one without encoding it, " +
                    $"which is the thing this measures - so there is nothing here to sample.";

            if (file.VideoStreams.Count < 1)
                return $"'{file.Name}' has no video track.";

            if (file.DurationMs <= 0)
                return $"'{file.Name}' reports no duration, so there is nowhere to place the samples. Remux it to MKV " +
                    $"first - the Concatenate utility will do that for a single file.";

            return "";
        }

        /// <summary>
        /// Cuts each planned section out losslessly and measures what came out.
        /// <para/>
        /// The measuring is the part that matters: a copy cannot start between keyframes, so the cut
        /// runs from the keyframe before the start point with the pre-roll kept on top of the length
        /// that was asked for - see <see cref="CrfLadder.Sample.Ms"/>, where the numbers are. Every
        /// per-second figure in the report divides by this, so it is read off the file rather than
        /// taken from the setting.
        /// </summary>
        private static async Task<bool> ExtractSamples(MediaFile file, CrfLadder.Result result, string workDir)
        {
            foreach (CrfLadder.Sample sample in result.Samples)
            {
                string path = Path.Combine(workDir, $"sample{sample.Index + 1}.mkv");
                Logger.Log($"Cutting sample {sample.Index + 1}/{result.Samples.Count} at {FormatUtils.Time(sample.StartMs)}...");

                // Video only. The samples are encoded video-only too, so every byte the report counts
                // is video - which is what a CRF decides and what the projection is therefore about.
                bool wrote = await UtilCut.CopySection(file.ImportPath, path, sample.StartMs, sample.StartMs + sample.Ms, "-map 0:v:0");

                if (RunTask.canceled)
                    return false;

                if (!wrote)
                {
                    RunTask.Fail($"The sample at {FormatUtils.Time(sample.StartMs)} could not be cut out of " +
                        $"'{file.Name.Trunc(40)}'. The log has FFmpeg's output.");
                    return false;
                }

                sample.Path = path;
                long ms = await FfmpegCommands.GetDurationMs(path);

                if (ms > 0)
                    sample.Ms = ms;
            }

            long sampled = result.Samples.Sum(x => x.Ms);
            Logger.Log($"Sampled {FormatUtils.Time(sampled)} in total ({(result.SampledFraction * 100).ToString("0.#")}% of the " +
                $"file). A copy starts at the keyframe before the point asked for and keeps what is between, so a section " +
                $"can come out longer than the length set - every figure below is measured against what was actually cut.");

            return true;
        }

        /// <summary> Encodes every sample at every CRF and scores each one. </summary>
        private static async Task<bool> EncodeLadder(MediaFile file, CrfLadder.Result result, int[] crfs, IEncoder enc, string workDir)
        {
            int step = 0;
            int steps = crfs.Length * result.Samples.Count;

            foreach (int crf in crfs)
            {
                var rung = new CrfLadder.Rung { Crf = crf };

                foreach (CrfLadder.Sample sample in result.Samples)
                {
                    step++;
                    string outPath = Path.Combine(workDir, $"sample{sample.Index + 1}_crf{crf}.mkv");
                    Logger.Log($"Encoding sample {sample.Index + 1}/{result.Samples.Count} at CRF {crf} ({step}/{steps})...");

                    var sw = Stopwatch.StartNew();
                    bool wrote = await EncodeSample(file, enc, crf, sample.Path, outPath);
                    sw.Stop();

                    if (RunTask.canceled)
                        return false;

                    if (!wrote)
                    {
                        // Fail the whole run on the first one rather than working through the rest:
                        // every rung is the same encoder with the same arguments, so what stopped one
                        // stops all of them, and a machine with no NVIDIA card would otherwise be told
                        // the same thing a dozen times over.
                        RunTask.Fail($"{enc.FriendlyName} wrote nothing at CRF {crf}, so the ladder was stopped before " +
                            $"the rest of it. The log has FFmpeg's output - a missing encoder or a colour format the " +
                            $"build does not support is the usual reason.");
                        return false;
                    }

                    var rungSample = new CrfLadder.RungSample
                    {
                        SampleIndex = sample.Index,
                        Bytes = IoUtils.GetFilesize(outPath),
                        Ms = sample.Ms,
                        EncodeMs = sw.ElapsedMilliseconds,
                    };

                    // Off the result rather than the static setting, so one run is governed by one
                    // choice: the dialog is reachable while this is going and a metric changed halfway
                    // would put two different measurements in one column.
                    if (result.ScoredWith != CrfLadder.Metric.None)
                    {
                        double score = await ScoreAsync(file, outPath, sample.Path, result.ScoredWith);

                        if (RunTask.canceled)
                            return false;

                        rungSample.Scored = score > double.MinValue;
                        rungSample.Score = rungSample.Scored ? score : 0;
                    }

                    rung.Samples.Add(rungSample);

                    if (!KeepSamples)
                        IoUtils.TryDeleteIfExists(outPath);
                }

                result.Rungs.Add(rung);
            }

            return true;
        }

        /// <summary>
        /// One sample at one CRF. No filter chain of any kind: the ladder measures the source as it
        /// stands, and a resize or a crop would make the answer one about a picture the encode tabs
        /// would have to be set up to reproduce.
        /// <para/>
        /// The source file is what the encoder arguments are built against rather than the cut, so the
        /// keyframe interval and the tile count are the ones a real encode of this file would get -
        /// both are worked out from the video's frame rate and frame size, which the cut shares.
        /// </summary>
        private static async Task<bool> EncodeSample(MediaFile file, IEncoder enc, int crf, string inPath, string outPath)
        {
            var encArgs = new Dictionary<string, string>
            {
                { "q", crf.ToString() },
                { "preset", GetPreset() },
                { "qMode", ((int)QuickConvert.QualityMode.Crf).ToString() },
            };

            string pixFmt = GetPixelFormat();

            if (pixFmt.IsNotEmpty())
                encArgs["pixFmt"] = pixFmt;

            CodecArgs codecArgs = enc.GetArgs(encArgs, file, Pass.OneOfOne);
            string args = $"-i {Shell.WrapArg(inPath)} {codecArgs.Arguments} -an -sn -dn {Shell.WrapArg(outPath)}";

            FfmpegOutputHandler.overrideTargetDurationMs = await FfmpegCommands.GetDurationMs(inPath);

            await AvProcess.RunFfmpeg(new AvProcess.FfmpegSettings
            {
                Args = args,
                LoggingMode = AvProcess.LogMode.OnlyLastLine,
                ProgressBar = true,
                // Reported here instead, where the CRF that failed can be named - and where the rest
                // of the ladder is called off rather than repeating the same failure per rung.
                ReportFailure = false,
            });

            FfmpegOutputHandler.overrideTargetDurationMs = -1;
            return IoUtils.GetFilesize(outPath) > 0;
        }

        /// <summary>
        /// Scores an encoded sample against the lossless cut it came from, or <see cref="double.MinValue"/>
        /// where the metric printed nothing.
        /// <para/>
        /// The two are the same frames by construction - the encode's input *is* the reference - so
        /// there is none of the scaling and cropping <see cref="UtilGetMetrics"/> has to do to put two
        /// unrelated files into one frame of reference. VMAF and XPSNR are ffmpeg filters; SSIMULACRA2
        /// goes to VapourSynth instead, since no ffmpeg build computes it.
        /// </summary>
        private static async Task<double> ScoreAsync(MediaFile file, string encPath, string refPath, CrfLadder.Metric scoreWith)
        {
            if (scoreWith == CrfLadder.Metric.Ssimulacra2)
                return await ScoreSsimulacra2Async(encPath, refPath);

            // The frame rate is forced onto both inputs: a variable-rate source keeps its timestamps
            // through a stream copy and loses them through an encode, and the ffmpeg metric filters
            // pair their inputs up by timestamp.
            Fraction rate = file.VideoStreams.FirstOrDefault()?.Rate ?? Fraction.Zero;
            string r = rate.GetFloat() > 0f ? $"-r {rate}" : "";
            string metric = scoreWith == CrfLadder.Metric.Xpsnr ? "xpsnr" : GetVmafFilter();

            string graph = Shell.WrapArg($"[0:v][1:v]{metric}");
            string args = $"{r} -i {Shell.WrapArg(encPath)} {r} -i {Shell.WrapArg(refPath)} -filter_complex {graph} -f null -";

            FfmpegOutputHandler.overrideTargetDurationMs = await FfmpegCommands.GetDurationMs(refPath);

            string output = await AvProcess.RunFfmpeg(new AvProcess.FfmpegSettings
            {
                Args = args,
                LoggingMode = AvProcess.LogMode.OnlyLastLine,
                LogLevel = "info",
                ReliableOutput = true,
                ProgressBar = true,
                ReportFailure = false,
            });

            FfmpegOutputHandler.overrideTargetDurationMs = -1;

            double score = scoreWith == CrfLadder.Metric.Xpsnr ? ParseXpsnr(output) : ParseVmaf(output);

            // Warned rather than failed: a rung with no score still carries its size, which is half of
            // what the ladder is for, and a run that got through the encodes should not be thrown away
            // over the metric.
            if (score <= double.MinValue)
                Logger.LogWarn($"No {(scoreWith == CrfLadder.Metric.Xpsnr ? "XPSNR" : "VMAF")} score came back for " +
                    $"'{Path.GetFileName(encPath)}' - that rung is reported on size alone. The log has FFmpeg's output.");

            return score;
        }

        /// <summary>
        /// SSIMULACRA2 through VapourSynth. Frame-exact by construction here - the encode is of the
        /// lossless cut - so no rate is forced: the scorer opens both files through the same LSMASH
        /// source av1an and QTGMC use and compares frame i against frame i. Its progress prints through
        /// the ordinary log rather than the progress bar, VapourSynth having no time-based readout.
        /// </summary>
        private static async Task<double> ScoreSsimulacra2Async(string encPath, string refPath)
        {
            Media.Ssimulacra2.Score result = await Media.Ssimulacra2.ScoreAsync(refPath, encPath);

            if (result.Ok)
                return result.Value;

            Logger.LogWarn($"No SSIMULACRA2 score came back for '{Path.GetFileName(encPath)}' - that rung is reported on " +
                $"size alone. {result.Problem}");
            return double.MinValue;
        }

        /// <summary>
        /// The model is named by version rather than by path, for the reasons <see cref="UtilGetMetrics"/>
        /// gives at length: libvmaf's first positional option is the log path, not the model, and a
        /// path there overwrites the bundled model file with an XML log.
        /// </summary>
        private static string GetVmafFilter()
        {
            return $"libvmaf=model='version\\={GetVmafModelName()}':n_threads={Environment.ProcessorCount}";
        }

        /// <summary> "VMAF score: 94.155506" </summary>
        private static double ParseVmaf(string output)
        {
            string line = output.SplitIntoLines().LastOrDefault(x => x.Contains("VMAF score: "));
            return ParseDouble(line?.Split("VMAF score: ").LastOrDefault());
        }

        /// <summary>
        /// "XPSNR  y: 30.7897  u: 29.9396  v: 30.8069  (minimum: 29.9396)" - the luma figure, which is
        /// the one XPSNR is quoted as. An identical pair prints "inf", which is a real answer and not a
        /// parse failure, so it comes back as positive infinity rather than as no score.
        /// </summary>
        private static double ParseXpsnr(string output)
        {
            string line = output.SplitIntoLines().LastOrDefault(x => x.Contains("XPSNR ") && x.Contains(" y: "));

            if (line == null)
                return double.MinValue;

            string value = line.Split(" y: ").LastOrDefault()?.Trim().Split(' ').FirstOrDefault();
            return value != null && value.Trim().ToLowerInvariant().StartsWith("inf") ? double.PositiveInfinity : ParseDouble(value);
        }

        private static double ParseDouble(string s)
        {
            return double.TryParse((s ?? "").Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : double.MinValue;
        }

        /// <summary> The table, in the log, because the results window is dismissed and the log is not. </summary>
        private static void Report(CrfLadder.Result result)
        {
            Logger.Log($"CRF ladder for '{result.FileName.Trunc(40)}' - {result.EncoderName}, preset {result.Preset}, " +
                $"{result.PixelFormat}, measured over {FormatUtils.Time(result.Samples.Sum(x => x.Ms))} of it:");

            foreach (CrfLadder.Rung rung in result.Rungs)
            {
                // Off the result rather than off the static setting: the dialog can be reopened while
                // this runs, and the table has to describe the run that produced it.
                string score = !rung.Scored ? "" : $" · {CrfLadder.MetricName(result.ScoredWith)} {FormatScore(rung.Score)}";

                Logger.Log($"  CRF {rung.Crf}: {FormatUtils.Bitrate(rung.Kbps)} · " +
                    $"{FormatUtils.Bytes(rung.BytesPerMinute)}/min · whole file {CrfLadder.DescribeProjection(rung.ProjectedBytes(result.SourceMs))}{score}");
            }

            Logger.Log($"The projections are the sampled bitrate carried across the whole file, and video only - no audio, " +
                $"and no subtitles. What they cannot know is the rest of the film: sampling {(result.SampledFraction * 100).ToString("0.#")}% " +
                $"of it means a quiet stretch or a busy one moves the answer far more than any rounding here does.");
        }

        public static string FormatScore(double score)
        {
            if (double.IsPositiveInfinity(score))
                return "Infinite";

            return score.ToString("0.00");
        }
    }
}
