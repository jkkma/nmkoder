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
    /// Both tabs offer the same five engines, and they get there differently. Quick Convert runs QTGMC
    /// inline, ffmpeg reading its frames from a VapourSynth pipe; the AV1AN tab cannot do that - av1an
    /// applies filters with ffmpeg once per chunk and there is nowhere in that to put a script - so it
    /// runs QTGMC as a pass of its own first and gives av1an the progressive file that comes out. See
    /// <see cref="Av1anAutoQtgmcProblem"/> for why Automatic there does not reach for it.
    /// </summary>
    class DeinterlaceUi
    {
        private static MainWindow Form { get { return Program.MainWin; } }

        /// <summary> Modes in dropdown order, the same list everywhere the setting appears: both encode
        /// tabs and the Deinterlace utility's own dialog. The Quick Convert box saves its index, so
        /// entries may be appended but not reordered; the AV1AN box saves the mode's name instead, for
        /// the reason <see cref="RestoreAv1anMode"/> gives. </summary>
        public static readonly DeinterlaceMode[] AllModes =
            { DeinterlaceMode.Automatic, DeinterlaceMode.Disabled, DeinterlaceMode.Qtgmc, DeinterlaceMode.Bwdif, DeinterlaceMode.Yadif };

        /// <summary>
        /// Why Automatic on the AV1AN tab settles for an ffmpeg deinterlacer instead of reaching for
        /// QTGMC the way Automatic elsewhere does.
        /// <para/>
        /// QTGMC cannot run inside av1an at all, so the tab runs it beforehand, over the whole file,
        /// into a near-lossless intermediate - hours of work and tens of gigabytes on the sources this
        /// is for. That is a fine trade when it is what was asked for and a rude surprise when it is
        /// not, and Automatic's entire job is to be the setting nobody has to think about: it should
        /// clean up an interlaced source and otherwise stay out of the way. So the expensive engine is
        /// the one that has to be picked by name, and Automatic gets bwdif - which is what it has
        /// always got here.
        /// </summary>
        public const string Av1anAutoQtgmcProblem = "QTGMC cannot run inside av1an, so this tab renders it into an " +
            "intermediate file first - which Automatic will not start on its own; pick QTGMC to run that pass";

        /// <summary> Which plugin sets a background availability check has already been started for,
        /// so hovering over a file list does not queue one per file - and so switching to a preset
        /// with different requirements still gets asked about. </summary>
        private static readonly HashSet<string> probed = new HashSet<string>();

        public static void Init()
        {
            Form.EncDeintModeBox.SetItems(AllModes.Select(m => (object)GetLabel(m)), 0);
            Form.Av1anDeintModeBox.SetItems(AllModes.Select(m => (object)GetLabel(m)), 0);
            Form.EncDeintPresetBox.SetItems(Qtgmc.Presets.Select(p => (object)p), Array.IndexOf(Qtgmc.Presets, Qtgmc.DefaultPreset));
            Form.Av1anDeintPresetBox.SetItems(Qtgmc.Presets.Select(p => (object)p), Array.IndexOf(Qtgmc.Presets, Qtgmc.DefaultPreset));
        }

        /// <summary>
        /// Restores the AV1AN tab's mode, which is saved by name where every other fixed dropdown in
        /// the app saves its index.
        /// <para/>
        /// It has to be, because this list has just been reordered. QTGMC used to be missing from it,
        /// and appending it would have left the tab's own dropdown listing the engines in a different
        /// order from the identical one two tabs over; putting it where it belongs moves Bwdif and
        /// Yadif down a place, so every index saved by an older build now names the engine below the
        /// one that was picked - and one of those wrong answers starts an hours-long QTGMC pass over a
        /// setting the user thought said Bwdif. A name cannot go stale that way. The old index is read
        /// once, against the list as it stood, so nobody's setting is lost in the move.
        /// </summary>
        public static void RestoreAv1anMode()
        {
            string key = Form.Av1anDeintModeBox.Name;

            // Asked before reading, as ConfigParser's own Restore helpers do: the Get helpers write a
            // default for any key that is missing, so reading first would create the entry either way.
            if (!Config.cachedValues.ContainsKey(key))
                return;

            string saved = (Config.Get(key) ?? "").Trim();

            if (saved.IsEmpty())
                return;

            // Written by 2.8.9 and earlier: an index into { Automatic, Disabled, Bwdif, Yadif }.
            DeinterlaceMode[] oldOrder = { DeinterlaceMode.Automatic, DeinterlaceMode.Disabled, DeinterlaceMode.Bwdif, DeinterlaceMode.Yadif };
            bool wasIndex = int.TryParse(saved, out int index);
            DeinterlaceMode mode = wasIndex
                ? oldOrder[index.Clamp(0, oldOrder.Length - 1)]
                : AllModes.FirstOrDefault(m => GetLabel(m) == saved);

            int at = Array.IndexOf(AllModes, mode);

            if (at >= 0)
                Form.Av1anDeintModeBox.SelectedIndex = at;
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
        /// The AV1AN tab's request.
        /// <para/>
        /// One frame per field is only on the table for QTGMC, which runs as a pass of its own into a
        /// file av1an is then given - so the doubled rate is the rate of the source av1an measures,
        /// and everything downstream of it agrees. The ffmpeg engines run *inside* av1an, where it is
        /// forbidden: av1an works out the output's frame rate from the source and hands each encoder a
        /// fixed number of frames per chunk, so a filter emitting one frame per field would write
        /// twice the frames under the source's own rate - a file that plays at half speed.
        /// </summary>
        public static DeinterlaceRequest GetAv1anRequest()
        {
            DeinterlaceMode mode = AllModes[Form.Av1anDeintModeBox.SelectedIndex.Clamp(0, AllModes.Length - 1)];
            bool qtgmc = mode == DeinterlaceMode.Qtgmc;

            return new DeinterlaceRequest
            {
                Mode = mode,
                QtgmcPreset = Form.Av1anDeintPresetBox.GetText().IsEmpty() ? Qtgmc.DefaultPreset : Form.Av1anDeintPresetBox.GetText(),
                DoubleRate = qtgmc && Form.Av1anDeintDoubleRateBox.IsChecked == true,
                QtgmcUnavailableHere = qtgmc ? "" : Av1anAutoQtgmcProblem,
            };
        }

        /// <summary>
        /// Brings both tabs' readouts up to date, and shows or hides the QTGMC controls with them.
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

                // Only for QTGMC picked by name, because that is the only mode on this tab either
                // control means anything for: Automatic here is an ffmpeg filter, and one frame per
                // field is not something av1an's own chunking can be given.
                bool av1anQtgmc = av1an.Mode == DeinterlaceMode.Qtgmc;
                Form.Av1anDeintPresetBox.IsVisible = av1anQtgmc;
                Form.Av1anDeintDoubleRateBox.IsVisible = av1anQtgmc;

                Form.EncDeintInfoLabel.Text = Deinterlace.DescribeForUi(file, enc);
                Form.Av1anDeintInfoLabel.Text = Deinterlace.DescribeForUi(file, av1an);

                if (qtgmcPossible)
                    StartProbeIfNeeded(enc.QtgmcPreset, RefreshInfo);

                if (av1anQtgmc)
                    StartProbeIfNeeded(av1an.QtgmcPreset, RefreshInfo);
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
