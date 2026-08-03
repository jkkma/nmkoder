using Avalonia.Controls;
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
    /// The Deinterlace controls, which both encode tabs carry.
    /// <para/>
    /// The row is hidden for a file with no fields worth discussing, and the setting behind it defaults
    /// to QTGMC at Very Slow - so a tape capture gets the best deinterlacer there is without anyone
    /// having had to know to ask, and a file that plainly says it is progressive never shows the
    /// control at all. <see cref="IsRowRelevant"/> decides which of those a file is, and it shows the
    /// row for anything whose fields were actually measured as well as for anything called interlaced,
    /// so a verdict a person can see is wrong is one they can still act on.
    /// <para/>
    /// Two things keep that from arming an expensive filter on video that does not need it, because a
    /// default of QTGMC is an engine picked *by name* and <see cref="Media.Deinterlace.ResolveAsync"/>
    /// runs one of those over whatever it is handed without consulting the verdict.
    /// <see cref="ApplyScanVerdict"/> puts the mode on Automatic for any file not called interlaced, so
    /// a row that appears over progressive video appears switched off; and <see cref="ModeInEffect"/>
    /// reports Automatic whenever the row is not on screen at all, whatever the box behind it says.
    /// <para/>
    /// What none of it covers is a container that lies outright. A file flagged progressive is believed
    /// rather than scanned, so it never reaches the measurement that would show the row, and forcing an
    /// engine by name was how that used to be overruled. The Deinterlace Video utility takes no notice
    /// of any of this and deinterlaces what it is given, so that is where such a file goes.
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
        /// entries may be appended but not reordered. The AV1AN box saves nothing at all - its whole
        /// tab starts each session at its defaults - which is what retired the name-versus-index
        /// migration this list used to need there. </summary>
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

        /// <summary> The engine both tabs take for a file that is interlaced - what they open on, what
        /// Reset On New File goes back to, and what <see cref="ApplyScanVerdict"/> selects. QTGMC
        /// because on a genuinely interlaced source the motion-compensated deinterlacer is the right
        /// answer often enough to be the one nobody has to go and pick. It is not the cheap answer: on
        /// the AV1AN tab it is a whole extra pass into a near-lossless intermediate before av1an
        /// starts, which is why nothing but a measured "interlaced" selects it. </summary>
        public const DeinterlaceMode DefaultMode = DeinterlaceMode.Qtgmc;

        public static void Init()
        {
            int mode = Array.IndexOf(AllModes, DefaultMode);
            int preset = Array.IndexOf(Qtgmc.Presets, Qtgmc.DefaultPreset);
            Form.EncDeintModeBox.SetItems(AllModes.Select(m => (object)GetLabel(m)), mode);
            Form.Av1anDeintModeBox.SetItems(AllModes.Select(m => (object)GetLabel(m)), mode);
            Form.EncDeintPresetBox.SetItems(Qtgmc.Presets.Select(p => (object)p), preset);
            Form.Av1anDeintPresetBox.SetItems(Qtgmc.Presets.Select(p => (object)p), preset);
        }

        /// <summary>
        /// Both tabs' modes back to <see cref="DefaultMode"/>, for Reset On New File.
        /// <para/>
        /// Only the mode. The preset and the field doubling say *how* to deinterlace, which is a
        /// preference and survives the file it was set over; the mode says *whether* to, which is a
        /// fact about a file that has just been replaced.
        /// <para/>
        /// The index is looked up rather than written as a literal because this list has been reordered
        /// once already - QTGMC went into the middle of it - and a literal here would have moved with
        /// it in silence, resetting to whatever ended up in that position.
        /// </summary>
        public static void ResetModes()
        {
            int mode = Array.IndexOf(AllModes, DefaultMode);
            Form.EncDeintModeBox.SelectedIndex = mode;
            Form.Av1anDeintModeBox.SelectedIndex = mode;
        }

        /// <summary>
        /// Whether the loaded file is one the Deinterlace row has anything to say about - which is the
        /// question behind both the row's visibility and what the two tabs report while it is hidden.
        /// <para/>
        /// Two things make it true, and the second is there to leave a way to disagree. A file the
        /// verdict calls interlaced obviously needs the row. A file whose fields were actually
        /// *measured* gets it too, whatever the measurement said, because that measurement is the part
        /// of the verdict that can be wrong: <see cref="InterlaceDetect"/> only decodes frames for a
        /// file whose container says nothing about its scan type, and a capture that scored just under
        /// the line is exactly the case where a person can see combing the counters missed.
        /// <para/>
        /// The row being *there* is not the row being armed - see <see cref="ApplyScanVerdict"/>, which
        /// puts the mode on Automatic for anything not called interlaced, so a scanned-progressive file
        /// shows a control that is doing nothing until somebody picks an engine in it.
        /// <para/>
        /// False for no file, for a file whose scan type is not settled yet - until that lands there is
        /// nothing to show a control for, and <see cref="AnalyzeInBackground"/> calls
        /// <see cref="RefreshInfo"/> when it does - and for a container that states progressive, which
        /// is believed rather than measured and so never reaches the scan at all. That last one is the
        /// case this does not cover: a container that lies outright still has to go to the Deinterlace
        /// Video utility, which deinterlaces whatever it is given.
        /// </summary>
        public static bool IsRowRelevant(MediaFile file)
        {
            if (file == null || file.VideoStreams.Count < 1 || file.Interlacing == null)
                return false;

            return file.Interlacing.Interlaced || file.Interlacing.Scanned;
        }

        /// <summary> The file both boxes were last pointed at by a verdict, so the same one is not
        /// applied twice - the second time would be over a selection the user had made since. </summary>
        private static MediaFile verdictAppliedTo;

        /// <summary>
        /// Points both tabs at the engine a freshly measured file wants, and does two different things
        /// depending on which way the verdict went.
        /// <para/>
        /// A file that is **not** interlaced goes to Automatic whatever the boxes said, and that half is
        /// not optional. A scan runs on any file whose container says nothing, most of which are
        /// progressive, and the row appears for all of them - so a box still reading QTGMC there would
        /// force a deinterlace on progressive video, which is the thing hiding the row exists to
        /// prevent. Automatic does nothing to it, so nothing is taken away by demoting to it.
        /// <para/>
        /// A file that **is** interlaced gets <see cref="DefaultMode"/> only where Reset On New File is
        /// set to clear the deinterlace mode - which is on by default. That half is a preference, not a
        /// safety measure, and treating it as one is what shipped in 2.8.14: a user who had turned that
        /// reset off to keep bwdif for a queue of tapes had every file of it moved back to QTGMC, which
        /// on the AV1AN tab is an hours-long pass each. With the reset off, an engine picked by hand now
        /// survives the next file, which is what turning it off means.
        /// </summary>
        public static void ApplyScanVerdict(MediaFile file)
        {
            verdictAppliedTo = file;

            // Nothing to say about a file with no verdict yet - EnsureScanVerdictAsync waits for one.
            if (file?.Interlacing == null)
                return;

            if (IsInterlaced(file) && !ResetSettingsOnNewFile.ResetDeinterlace)
                return;

            int mode = Array.IndexOf(AllModes, IsInterlaced(file) ? DefaultMode : DeinterlaceMode.Automatic);
            Form.EncDeintModeBox.SelectedIndex = mode;
            Form.Av1anDeintModeBox.SelectedIndex = mode;
        }

        /// <summary>
        /// Settles the scan verdict and points the boxes at it, for a caller that is about to read them.
        /// <para/>
        /// <see cref="AnalyzeInBackground"/> is deliberately fire-and-forget, so that loading a file does
        /// not wait on a few hundred frames being decoded - but a batch starts each encode the moment the
        /// file is loaded, and 2.8.14 shipped the two racing. Whichever landed first decided the engine:
        /// a file with a container flag answered fast enough for its verdict to win, while one needing a
        /// real scan had its encode read the *previous* file's mode. Asking here costs one await of an
        /// answer the run is about to wait for anyway, since <see cref="Media.Deinterlace.ResolveAsync"/>
        /// waits for the same verdict a moment later.
        /// </summary>
        public static async Task EnsureScanVerdictAsync(MediaFile file)
        {
            if (file == null || file.VideoStreams.Count < 1 || verdictAppliedTo == file)
                return;

            await InterlaceDetect.GetAsync(file, quiet: true);
            ApplyScanVerdict(file);
            RefreshInfo();
        }

        private static bool IsInterlaced(MediaFile file)
        {
            return file?.Interlacing != null && file.Interlacing.Interlaced;
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

        /// <summary>
        /// The mode a box is actually asking for: what it says while the row is on screen, and
        /// Automatic while it is not.
        /// <para/>
        /// This is what makes hiding the row safe rather than merely tidy. A mode picked by name is
        /// applied to whatever it is handed - that is the point of naming one - so a box left on QTGMC
        /// behind a hidden row would deinterlace progressive video, which is both wrong and impossible
        /// to see. Automatic asks the scan verdict first and does nothing when the answer is
        /// progressive, so it is the one mode that is safe to fall back to without knowing anything:
        /// on a file whose scan type has not been measured yet it still cleans up a source that turns
        /// out to be interlaced, because <see cref="Media.Deinterlace.ResolveAsync"/> waits for that
        /// answer itself.
        /// </summary>
        private static DeinterlaceMode ModeInEffect(ComboBox box)
        {
            if (!IsRowRelevant(TrackList.current?.File))
                return DeinterlaceMode.Automatic;

            return AllModes[box.SelectedIndex.Clamp(0, AllModes.Length - 1)];
        }

        public static DeinterlaceRequest GetQuickConvertRequest()
        {
            return new DeinterlaceRequest
            {
                Mode = ModeInEffect(Form.EncDeintModeBox),
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
            DeinterlaceMode mode = ModeInEffect(Form.Av1anDeintModeBox);
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
        /// Brings both tabs' readouts up to date, and shows or hides the row and the QTGMC controls
        /// with them. Touches no collection and starts nothing that blocks, so it is safe from any
        /// handler - which matters, because the answer it works from arrives on a background scan.
        /// </summary>
        public static void RefreshInfo()
        {
            try
            {
                MediaFile file = TrackList.current?.File;
                DeinterlaceRequest enc = GetQuickConvertRequest();
                DeinterlaceRequest av1an = GetAv1anRequest();

                // Both halves of each row, label included, so nothing is left behind pointing at a
                // missing control. Hidden rather than disabled: a greyed-out row still asks the user
                // to work out why it is greyed out, where a file with no fields in it has no question
                // to answer in the first place.
                bool relevant = IsRowRelevant(file);
                Form.EncDeintLabel.IsVisible = Form.EncDeintPanel.IsVisible = relevant;
                Form.Av1anDeintLabel.IsVisible = Form.Av1anDeintPanel.IsVisible = relevant;

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
        /// Works out whether the loaded file is interlaced, then points the row at the right engine and
        /// updates the readouts with the answer. Off the UI thread because a file whose container says
        /// nothing about its scan type has to have a few hundred frames decoded before anything is
        /// known, and loading a file should not wait on that.
        /// <para/>
        /// A file that has been through this before takes the early exit and keeps whatever mode is
        /// selected: the verdict is applied where it is *measured*, not every time a file is looked at,
        /// which is what stops a trip round the file list undoing an engine picked by hand.
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
                // otherwise leave the first one's verdict describing the second - and would point the
                // row at an engine chosen for a file nobody is looking at any more.
                Dispatcher.UIThread.Post(() =>
                {
                    if (TrackList.current?.File != file)
                        return;

                    ApplyScanVerdict(file);
                    RefreshInfo(); // Setting the mode raises this too; called plainly for the case where it does not change
                });
            });
        }
    }
}
