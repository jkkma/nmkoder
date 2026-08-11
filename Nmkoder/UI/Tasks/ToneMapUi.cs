using Avalonia.Controls;
using Avalonia.Threading;
using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Media;
using Nmkoder.Utils;
using Nmkoder.Views;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Nmkoder.UI.Tasks
{
    /// <summary>
    /// The Tone Mapping controls, which both encode tabs carry.
    /// <para/>
    /// Shaped after the Deinterlace row and for the same reason: the setting only means anything for one
    /// kind of source, so the row appears for that kind and is not on screen at all otherwise. A file
    /// whose transfer curve is PQ or HLG gets the control; every ordinary BT.709 download never sees it.
    /// <para/>
    /// **What it does not share with that row is a default that does anything.** Deinterlacing an
    /// interlaced file is what almost everyone wants and the row opens armed; tone-mapping an HDR file is
    /// a deliberate choice, because the other thing people load an HDR source for is to re-encode it as
    /// HDR - which is most of what the AV1AN tab's 10-bit AV1 encoding is for. So this opens on
    /// <see cref="ToneMapMode.Off"/> and the row's whole job at that point is to say the file is HDR and
    /// that it will stay that way. Nothing here ever selects a curve on the user's behalf.
    /// <para/>
    /// <see cref="ModeInEffect"/> reports Off whenever the row is off screen, which is what makes hiding
    /// it safe rather than merely tidy - a curve left selected behind a hidden row would otherwise
    /// convert a file nobody was looking at.
    /// </summary>
    class ToneMapUi
    {
        private static MainWindow Form { get { return Program.MainWin; } }

        /// <summary> What both tabs open on, what Reset On New File goes back to. Off, for the reason in
        /// the class summary: the conversion is destructive and irreversible, and wanting it is not the
        /// same as having loaded a file it could apply to. </summary>
        public const ToneMapMode DefaultMode = ToneMapMode.Off;

        public static void Init()
        {
            int mode = Array.IndexOf(ToneMapConfig.AllModes, DefaultMode);
            Form.EncToneMapModeBox.SetItems(ToneMapConfig.AllModes.Select(m => (object)ToneMapConfig.GetLabel(m)), mode);
            Form.Av1anToneMapModeBox.SetItems(ToneMapConfig.AllModes.Select(m => (object)ToneMapConfig.GetLabel(m)), mode);
        }

        /// <summary> Both tabs back to <see cref="DefaultMode"/>, for Reset On New File. </summary>
        public static void ResetModes()
        {
            Form.EncToneMapModeBox.SelectedIndex = Array.IndexOf(ToneMapConfig.AllModes, DefaultMode);
            Form.Av1anToneMapModeBox.SelectedIndex = Array.IndexOf(ToneMapConfig.AllModes, DefaultMode);
        }

        /// <summary>
        /// Whether the loaded file is one this row has anything to say about, which is exactly "does its
        /// transfer curve say HDR".
        /// <para/>
        /// False while the answer is not in yet - <see cref="AnalyzeInBackground"/> reads the colour off
        /// the file on a background task and calls <see cref="RefreshInfo"/> when it lands - and false for
        /// a file with no video in it, which in Muxing Mode is an ordinary thing for the Track List to be
        /// showing.
        /// </summary>
        public static bool IsRowRelevant(MediaFile file)
        {
            return file != null && file.VideoStreams.Count >= 1 && ColorDataUtils.IsHdr(file.ColorData);
        }

        /// <summary>
        /// The mode a box is actually asking for: what it says while the row is on screen, and Off while
        /// it is not. See the class summary - a curve is applied to whatever it is handed, so a box left
        /// on one behind a hidden row is the one way this setting could reach a file silently.
        /// </summary>
        private static ToneMapMode ModeInEffect(ComboBox box, MediaFile file)
        {
            if (!IsRowRelevant(file))
                return ToneMapMode.Off;

            return ToneMapConfig.AllModes[box.SelectedIndex.Clamp(0, ToneMapConfig.AllModes.Length - 1)];
        }

        /// <summary>
        /// Quick Convert's setting, read against the file the video will actually come out of - which in
        /// Muxing Mode is the file the checked video stream belongs to rather than whichever one the
        /// Track List is showing. Shared with the deinterlacer so the two cannot pick different files.
        /// <para/>
        /// Off for a stream copy, whatever the box says, because a stream copy builds no filter chain at
        /// all - <see cref="QuickConvertUi.GetVideoFilterArgs"/> returns "" for one before it reads
        /// anything. The box is disabled for it too, so this is mostly about the readout: without it the
        /// row would go on describing a conversion that is not going to happen.
        /// </summary>
        public static ToneMapConfig GetQuickConvertConfig()
        {
            if (CodecUtils.GetCodec(QuickConvertUi.GetCurrentCodecV()).DoesNotEncode)
                return new ToneMapConfig { Mode = ToneMapMode.Off };

            return new ToneMapConfig { Mode = ModeInEffect(Form.EncToneMapModeBox, DeinterlaceUi.GetQuickConvertSourceFile()) };
        }

        /// <summary> The AV1AN tab's setting. That tab encodes the loaded file and nothing else, so
        /// there is no source-file question to ask here. </summary>
        public static ToneMapConfig GetAv1anConfig()
        {
            return new ToneMapConfig { Mode = ModeInEffect(Form.Av1anToneMapModeBox, TrackList.current?.File) };
        }

        /// <summary> The colour of the file Quick Convert reads its video from, for the chain builder -
        /// null where nothing has been measured, which reads as "not HDR" everywhere. </summary>
        public static VideoColorData GetQuickConvertColorData()
        {
            return DeinterlaceUi.GetQuickConvertSourceFile()?.ColorData;
        }

        /// <summary> Brings both readouts up to date and shows or hides the rows with them. Touches no
        /// collection and starts nothing that blocks, so it is safe from any handler - which matters,
        /// because the answer it works from arrives on a background probe. </summary>
        public static void RefreshInfo()
        {
            try
            {
                MediaFile file = TrackList.current?.File;

                // Both halves of each row, label included. Hidden rather than disabled, as the
                // Deinterlace row is: an SDR file has no question here to answer.
                bool encRelevant = IsRowRelevant(DeinterlaceUi.GetQuickConvertSourceFile());
                Form.EncToneMapLabel.IsVisible = Form.EncToneMapPanel.IsVisible = encRelevant;

                bool av1anRelevant = IsRowRelevant(file);
                Form.Av1anToneMapLabel.IsVisible = Form.Av1anToneMapPanel.IsVisible = av1anRelevant;

                Form.EncToneMapInfoLabel.Text = GetQuickConvertConfig().GetNote(GetQuickConvertColorData());
                Form.Av1anToneMapInfoLabel.Text = GetAv1anConfig().GetNote(file?.ColorData);
            }
            catch (Exception e)
            {
                Logger.Log($"Failed to describe the tone mapping setting: {e.Message}", true);
            }
        }

        /// <summary>
        /// Reads a file's colour data, off the UI thread, then refreshes the rows with the answer.
        /// <para/>
        /// Fire-and-forget so that loading a file does not wait on it. It is one ffprobe frame read and -
        /// for Matroska, where the container carries colour the bitstream may not - one mkvinfo, which is
        /// far cheaper than the interlace scan beside it but is still two process launches per file.
        /// <para/>
        /// Cached on the <see cref="MediaFile"/>, so a trip round the file list costs nothing and a batch
        /// does not re-read. The AV1AN tab used to be the only thing that ever filled this in, at encode
        /// time, which is why Quick Convert had no colour data at all.
        /// </summary>
        public static void AnalyzeInBackground(MediaFile file)
        {
            // Both files, because in Muxing Mode they are not the same one and each tab reads a
            // different one: the AV1AN tab encodes the loaded file, while Quick Convert takes its video
            // from whichever file the checked video stream belongs to. Probing only the loaded one left
            // the row unable to appear for the ordinary shape of a mux - a video file muxed with an audio
            // file, with the audio file selected in the Track List - so an HDR source could not be
            // tone-mapped from the UI at all, however plainly it said it was HDR.
            foreach (MediaFile f in new[] { file, DeinterlaceUi.GetQuickConvertSourceFile() }.Distinct())
                Probe(f);

            RefreshInfo();
        }

        private static void Probe(MediaFile file)
        {
            if (file == null || file.VideoStreams.Count < 1 || file.ColorData != null || !probing.Add(file))
                return;

            _ = Task.Run(async () =>
            {
                VideoColorData data = await ColorDataUtils.GetColorData(file.ImportPath);

                Dispatcher.UIThread.Post(() =>
                {
                    file.ColorData = data;
                    probing.Remove(file);

                    // Only if it is still a file something on screen is describing: loading two files in
                    // quick succession would otherwise leave the first one's colour on the second's row.
                    if (TrackList.current?.File == file || DeinterlaceUi.GetQuickConvertSourceFile() == file)
                        RefreshInfo();
                });
            });
        }

        /// <summary> Files with a probe already in flight, so checking a stream or reselecting a file
        /// cannot queue a second ffprobe for an answer that is already on its way. </summary>
        private static readonly HashSet<MediaFile> probing = new HashSet<MediaFile>();

        /// <summary>
        /// Settles a file's colour and points the rows at it, for a caller about to read them.
        /// <para/>
        /// <see cref="AnalyzeInBackground"/> is fire-and-forget, and a batch starts each encode the moment
        /// its file is loaded - so without this the two race, exactly as the deinterlace verdict and its
        /// encode did in 2.8.14. Losing that race here is quiet in the worst way: the row would report
        /// itself irrelevant, <see cref="ModeInEffect"/> would answer Off, and an HDR file would go
        /// through untouched with nothing anywhere saying why.
        /// </summary>
        public static async Task EnsureColorDataAsync(MediaFile file)
        {
            if (file == null || file.VideoStreams.Count < 1 || file.ColorData != null)
                return;

            file.ColorData = await ColorDataUtils.GetColorData(file.ImportPath);
            RefreshInfo();
        }

        /// <summary>
        /// Why this tone-map cannot run, or "" when it can. Asked before the encode starts by both tabs,
        /// because the alternative is an ffmpeg that exits on "No such filter" once the run is under way -
        /// and on the AV1AN tab, once per chunk.
        /// </summary>
        public static async Task<string> GetProblem(ToneMapConfig config, VideoColorData src)
        {
            if (config == null || !config.RunsOn(src))
                return "";

            // A profile 5 source has no ordinary picture under its RPU, so the CPU chain - which does no
            // reshaping whatever - cannot produce the right colours from one. That is a property of the
            // bitstream rather than a guess about a tool, which is what earns a refusal here beside the
            // crop and frame-size ones rather than a warning: the output would be the magenta-and-green
            // picture, over a whole encode, looking exactly like a broken app.
            //
            // libplacebo is the other half and is deliberately not refused: it applies an RPU where its
            // build carries libdovi, so the same file may well come out right on the GPU path. Whether
            // the bundled build does carry it is **not measured** - no web session has a Dolby Vision
            // file to ask with - so that path warns instead, in ResolveBackendAsync, and the run goes
            // ahead. Refusing there on the strength of an unmeasured guess would take away a conversion
            // that probably works.
            if (ColorDataUtils.HasUnusableBaseLayer(src) && !config.UseLibplacebo)
            {
                return $"this file is Dolby Vision profile {ColorDataUtils.DescribeDolbyVisionProfile(src)}, whose picture only exists " +
                    $"once its dynamic metadata has been applied - and this machine is tone mapping on the CPU, which cannot apply it. " +
                    $"The colours would come out wrong rather than merely different. Encode it as it is to keep the file intact, or " +
                    $"convert it to a profile with an ordinary HDR10 layer underneath (dovi_tool's mode 2) and load that instead";
            }

            // Only the zscale chain's filters are worth refusing over. Where libplacebo is doing the
            // work, zscale and tonemap are not in the command at all - which is the case an ffmpeg
            // built without libzimg lands in, and it used to be told it could not tone-map by a check
            // that was asking about filters its chain would never name.
            if (config.UseLibplacebo)
                return "";

            return await ToneMap.GetProblem();
        }

        /// <summary>
        /// Settles which backend this encode tone-maps with, measures what that backend needs measured,
        /// and says so. Called by both tabs before anything is built, for the reason
        /// <see cref="ToneMapConfig.UseLibplacebo"/> gives: the answer is a property of the machine, and
        /// one answer has to hold for the whole run.
        /// <para/>
        /// It is stated in the log either way rather than only on the fallback. Which tone-mapper
        /// produced a file is the first thing anybody comparing two encodes needs to know, and on this
        /// tab the fallback is invisible from the outside - the picture simply differs a little from
        /// the one the same settings gave on another machine.
        /// <para/>
        /// The peak scan runs here, and only for the zscale chain. libplacebo measures every frame
        /// itself - that is the whole reason it is preferred - where the zscale chain takes its peak as
        /// one static number, and the declared metadata that number used to come from routinely
        /// describes the mastering monitor rather than the picture: the reported case declared MaxCLL
        /// 9978 and a 4000-nit display over frames that measure about 610 nits, which put the whole
        /// film 30-odd code values darker than a player that measures. A PQ file's frames are the
        /// authority on their own brightness, so they are read - a sampled scan, seconds long, logged
        /// with both numbers so an encode can be compared against the metadata it ignored.
        /// </summary>
        public static async Task ResolveBackendAsync(ToneMapConfig config, MediaFile file)
        {
            VideoColorData src = file?.ColorData;

            if (config == null || !config.RunsOn(src))
                return;

            string problem = await ToneMap.GetLibplaceboProblem();
            config.UseLibplacebo = problem.IsEmpty();

            if (config.UseLibplacebo)
            {
                Logger.Log("Tone mapping with libplacebo on the GPU, measuring the picture's real brightness as it goes.");
                LogDolbyVision(src, libplacebo: true);
                return;
            }

            LogDolbyVision(src, libplacebo: false);

            Logger.Log($"Tone mapping with FFmpeg's own zscale chain - libplacebo is not usable here ({problem}).");

            // The one setting on this row that cannot survive the fallback, and the readout cannot say
            // so: it is drawn when the file loads, where this is not known until the encode starts.
            if (config.FallsBackToAnotherCurve)
                Logger.LogWarn($"The {ToneMapConfig.GetLabel(ToneMapMode.Spline)} curve is libplacebo's own, so this encode " +
                    $"uses {ToneMapConfig.GetLabel(ToneMapMode.Hable)} instead - the closest of the curves FFmpeg has. " +
                    $"It is a little darker in the mid-tones and holds a little less back at the top.");

            if (src.ColorTransfer != ColorDataUtils.TransferPq)
                return; // HLG is relative and the chain passes no peak for it - there is nothing to measure

            Logger.Log("Measuring the picture's peak brightness (a sampled scan, a few seconds)...");
            config.MeasuredPeakNits = await ToneMap.MeasurePeakNitsAsync(file.SourcePath);

            double declared = ColorDataUtils.GetDeclaredPeakNits(src);
            string declaredNote = declared > 0 ? $"{declared:0} nits" : "nothing";

            if (config.MeasuredPeakNits > 0)
            {
                double effective = ToneMapConfig.GetEffectivePeakNits(src, config.MeasuredPeakNits);
                Logger.Log($"The brightest sampled pixel is ~{config.MeasuredPeakNits:0} nits, where the metadata declares " +
                    $"{declaredNote} - the roll-off is built for {effective:0} nits, and highlights above " +
                    $"{ToneMapConfig.GetWhitePointNits(effective):0} nits clip to white.");
            }
            else
            {
                Logger.Log($"The picture's own peak could not be measured, so the declared metadata answers: " +
                    $"{(declared > 0 ? $"{declaredNote}, with highlights above {ToneMapConfig.GetWhitePointNits(declared):0} nits clipping to white" : $"nothing declared, so {ToneMapConfig.AssumedPeakNits:0} nits is assumed")}.");
            }
        }

        /// <summary>
        /// What becomes of a Dolby Vision layer on the backend that was just picked, said once per run
        /// and only for a file that has one.
        /// <para/>
        /// It is worth a line because nothing else can carry it. The readout is drawn when the file
        /// loads, where the backend is not known; the output carries no trace either way, the dynamic
        /// metadata being deleted from an SDR file by the chain's own side-data tail; and the two
        /// backends genuinely differ here, where for every other property of this row they differ only
        /// by a few code values.
        /// <para/>
        /// **The GPU line is the one to be careful about, and it says "where this build carries libdovi"
        /// rather than promising.** libplacebo applies an RPU only when it was built against libdovi,
        /// and which way BtbN's ffmpeg is built is not something any session here could measure - a
        /// Dolby Vision file is not a thing that can be synthesised from a colour bar. So it is written
        /// as the conditional it is. The base layer is what comes out otherwise, which for every profile
        /// but 5 is the graded HDR10 or HLG picture and perfectly good; profile 5 is the one where that
        /// is not true, and <see cref="GetProblem"/> is what stands in front of it.
        /// </summary>
        private static void LogDolbyVision(VideoColorData src, bool libplacebo)
        {
            if (!ColorDataUtils.HasDolbyVision(src))
                return;

            string profile = ColorDataUtils.DescribeDolbyVisionProfile(src);

            if (!libplacebo)
            {
                Logger.Log($"This file carries Dolby Vision (profile {profile}), which FFmpeg's own chain does not read - " +
                    $"the {(ColorDataUtils.HasUnusableBaseLayer(src) ? "picture underneath it" : "HDR10 base layer")} is what gets mapped, " +
                    $"and the dynamic metadata is dropped with the rest of the HDR data.");
                return;
            }

            Logger.Log($"This file carries Dolby Vision (profile {profile}); libplacebo applies its dynamic metadata where this " +
                $"FFmpeg was built with libdovi, and maps the base layer where it was not. Either way the output is SDR " +
                $"BT.709 with no Dolby Vision data left in it.");
        }
    }
}
