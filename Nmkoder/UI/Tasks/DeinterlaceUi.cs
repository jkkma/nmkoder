using Avalonia.Threading;
using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Media;
using Nmkoder.Views;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Nmkoder.UI.Tasks
{
    /// <summary>
    /// The Deinterlace controls, which both encode tabs carry. The setting defaults to Automatic and
    /// does nothing at all to progressive video, so a tape capture comes out deinterlaced without
    /// anyone having had to know to ask - which is the whole point of it.
    /// <para/>
    /// The two tabs do not offer the same engines. Quick Convert can run QTGMC, because ffmpeg reads
    /// its frames from a VapourSynth pipe; the AV1AN tab cannot, because av1an applies filters with
    /// ffmpeg once per chunk and there is nowhere in that to put a script - see
    /// <see cref="Av1anQtgmcProblem"/>.
    /// </summary>
    class DeinterlaceUi
    {
        private static MainWindow Form { get { return Program.MainWin; } }

        /// <summary> Modes in dropdown order. Saved by index per box, so entries may be appended but
        /// not reordered. Shared with the Deinterlace For Encoding utility's own dialog, which offers
        /// the same five for the same reason - it runs the same pipe Quick Convert does. </summary>
        public static readonly DeinterlaceMode[] AllModes =
            { DeinterlaceMode.Automatic, DeinterlaceMode.Disabled, DeinterlaceMode.Qtgmc, DeinterlaceMode.Bwdif, DeinterlaceMode.Yadif };

        /// <summary> The AV1AN tab's subset. QTGMC is left out rather than offered and quietly
        /// substituted every time it is picked. </summary>
        private static readonly DeinterlaceMode[] Av1anModes =
            { DeinterlaceMode.Automatic, DeinterlaceMode.Disabled, DeinterlaceMode.Bwdif, DeinterlaceMode.Yadif };

        /// <summary>
        /// Why the AV1AN tab cannot run QTGMC. Two reasons, and the second is the one that would still
        /// bite even if the first were solved: av1an evaluates its input for scene detection, again
        /// for every chunk, and again for every probe a target-quality mode runs - so a filter chain
        /// that costs more than the encoder does would be paid for several times over.
        /// </summary>
        public const string Av1anQtgmcProblem = "av1an applies video filters with ffmpeg, once per chunk, " +
            "so a VapourSynth script cannot sit in front of them - the Deinterlace For Encoding utility runs QTGMC first";

        /// <summary> Which plugin sets a background availability check has already been started for,
        /// so hovering over a file list does not queue one per file - and so switching to a preset
        /// with different requirements still gets asked about. </summary>
        private static readonly HashSet<string> probed = new HashSet<string>();

        public static void Init()
        {
            Form.EncDeintModeBox.SetItems(AllModes.Select(m => (object)GetLabel(m)), 0);
            Form.Av1anDeintModeBox.SetItems(Av1anModes.Select(m => (object)GetLabel(m)), 0);
            Form.EncDeintPresetBox.SetItems(Qtgmc.Presets.Select(p => (object)p), Array.IndexOf(Qtgmc.Presets, Qtgmc.DefaultPreset));
        }

        public static string GetLabel(DeinterlaceMode mode)
        {
            switch (mode)
            {
                // Short on purpose: the readout under the dropdown says what each of these will
                // actually do to the loaded file, which is the part worth reading, and it only has
                // one line to do it in.
                case DeinterlaceMode.Automatic: return "Automatic";
                case DeinterlaceMode.Disabled: return "Disabled";
                case DeinterlaceMode.Qtgmc: return "QTGMC (VapourSynth)";
                case DeinterlaceMode.Bwdif: return "Bwdif (FFmpeg)";
                default: return "Yadif (FFmpeg)";
            }
        }

        public static DeinterlaceRequest GetQuickConvertRequest()
        {
            return new DeinterlaceRequest
            {
                Mode = AllModes[Form.EncDeintModeBox.SelectedIndex.Clamp(0, AllModes.Length - 1)],
                QtgmcPreset = Form.EncDeintPresetBox.GetText().IsEmpty() ? Qtgmc.DefaultPreset : Form.EncDeintPresetBox.GetText(),
                DoubleRate = Form.EncDeintDoubleRateBox.IsChecked == true,
            };
        }

        /// <summary>
        /// The AV1AN tab's request. The rate is pinned to single: av1an works out the output's frame
        /// rate from the source and hands each encoder a fixed number of frames per chunk, so a filter
        /// that emits one frame per field would write twice the frames under the source's own frame
        /// rate - a file that plays at half speed.
        /// </summary>
        public static DeinterlaceRequest GetAv1anRequest()
        {
            return new DeinterlaceRequest
            {
                Mode = Av1anModes[Form.Av1anDeintModeBox.SelectedIndex.Clamp(0, Av1anModes.Length - 1)],
                DoubleRate = false,
                QtgmcUnavailableHere = Av1anQtgmcProblem,
            };
        }

        /// <summary>
        /// Brings both tabs' readouts up to date, and shows or hides the QTGMC preset box with them.
        /// Touches no collection and starts nothing that blocks, so it is safe from any handler.
        /// </summary>
        public static void RefreshInfo()
        {
            try
            {
                MediaFile file = TrackList.current?.File;
                DeinterlaceRequest enc = GetQuickConvertRequest();
                DeinterlaceRequest av1an = GetAv1anRequest();

                bool qtgmcPossible = enc.Mode == DeinterlaceMode.Qtgmc || enc.Mode == DeinterlaceMode.Automatic;
                Form.EncDeintPresetBox.IsVisible = qtgmcPossible;
                Form.EncDeintDoubleRateBox.IsEnabled = enc.Mode != DeinterlaceMode.Disabled;

                Form.EncDeintInfoLabel.Text = Deinterlace.DescribeForUi(file, enc);
                Form.Av1anDeintInfoLabel.Text = Deinterlace.DescribeForUi(file, av1an);

                if (qtgmcPossible)
                    StartProbeIfNeeded(enc.QtgmcPreset, RefreshInfo);
            }
            catch (Exception e)
            {
                Logger.Log($"Failed to describe the deinterlace setting: {e.Message}", true);
            }
        }

        /// <summary>
        /// Asks in the background whether QTGMC can run at <paramref name="preset"/>, then calls
        /// <paramref name="onAnswered"/> on the UI thread so whatever is on screen can say so.
        /// <para/>
        /// Asked the first time something on screen depends on the answer rather than at startup: a
        /// machine with no VapourSynth pays a process launch for a setting it may never touch, and one
        /// with it pays a couple of seconds of graph building. Once per plugin set rather than once
        /// per session, because switching to Very Slow asks a question the Medium probe did not answer
        /// - it needs a denoiser plugin no other preset touches.
        /// </summary>
        public static void StartProbeIfNeeded(string preset, Action onAnswered)
        {
            // Nothing is recorded as probed until the probe is genuinely about to run. Marking it any
            // earlier means a machine with no VSPipe when the window opened never looks again, and a
            // VapourSynth installed while the app is open should not need a restart to be found.
            if (Qtgmc.GetKnownAvailability(preset) != null || Qtgmc.GetVspipePath().IsEmpty())
                return;

            if (!probed.Add(Qtgmc.NeedsNoisePlugins(preset) ? "noise" : "base"))
                return;

            _ = Task.Run(async () =>
            {
                await Qtgmc.IsAvailableAsync(preset);
                Dispatcher.UIThread.Post(() => onAnswered?.Invoke());
            });
        }

        /// <summary>
        /// Works out whether the loaded file is interlaced and updates the readouts with the answer.
        /// Off the UI thread because a file whose container says nothing about its scan type has to
        /// have a few hundred frames decoded before anything is known, and loading a file should not
        /// wait on that.
        /// </summary>
        public static void AnalyzeInBackground(MediaFile file)
        {
            if (file == null || file.VideoStreams.Count < 1 || file.Interlacing != null)
            {
                RefreshInfo();
                return;
            }

            _ = Task.Run(async () =>
            {
                await InterlaceDetect.GetAsync(file);

                // Only if it is still the file on screen: loading two files in quick succession would
                // otherwise leave the first one's verdict describing the second.
                Dispatcher.UIThread.Post(() =>
                {
                    if (TrackList.current?.File == file)
                        RefreshInfo();
                });
            });
        }
    }
}
