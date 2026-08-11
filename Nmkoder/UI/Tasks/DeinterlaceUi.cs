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
    /// The row is hidden for a file with no fields worth discussing, and on Quick Convert the setting
    /// behind it defaults to QTGMC at Very Slow - so a tape capture gets the best deinterlacer there is
    /// without anyone having had to know to ask, and a file that plainly says it is progressive never
    /// shows the control at all. <see cref="IsRowRelevant"/> decides which of those a file is, and it
    /// shows the row for anything whose fields were actually measured as well as for anything called
    /// interlaced, so a verdict a person can see is wrong is one they can still act on.
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
    /// **The two tabs no longer offer the same engines, and the difference is where QTGMC can be run
    /// without paying for it twice.** Quick Convert runs it inline, ffmpeg reading its frames from a
    /// VapourSynth pipe, so it costs one pipelined pass. av1an cannot do that at all - it applies
    /// filters with ffmpeg once per chunk and there is nowhere in that to evaluate a script - so that
    /// tab used to render the whole file through QTGMC into a lossless intermediate and hand av1an the
    /// result. On the sources QTGMC is for, that is strictly the slower shape: QTGMC is the bottleneck
    /// rather than the encoder, so the serial pass plus the parallel encode always exceeds the two
    /// pipelined, and it writes the largest temporary file this app produces to get there.
    /// <para/>
    /// So the AV1AN box offers <see cref="Av1anModes"/> - the same list without QTGMC - and
    /// <see cref="Av1anQtgmcProblem"/> is the standing reason, which Automatic there has always been
    /// resolved through. A tape that wants QTGMC goes to Quick Convert, or through the Deinterlace
    /// Video utility and then to this tab.
    /// </summary>
    class DeinterlaceUi
    {
        private static MainWindow Form { get { return Program.MainWin; } }

        /// <summary> Modes in dropdown order, as Quick Convert and the Deinterlace utility's own dialog
        /// offer them. The Quick Convert box saves its index, so entries may be appended but not
        /// reordered. </summary>
        public static readonly DeinterlaceMode[] AllModes =
            { DeinterlaceMode.Automatic, DeinterlaceMode.Disabled, DeinterlaceMode.Qtgmc, DeinterlaceMode.Bwdif, DeinterlaceMode.Yadif };

        /// <summary> What the AV1AN tab offers: <see cref="AllModes"/> without QTGMC, which that tab
        /// cannot run without turning a parallel encode into a serial pass and a lossless copy of the
        /// video - see the class summary and <see cref="Av1anQtgmcProblem"/>. Its own list rather than
        /// an index into the other, so nothing on that tab can select an engine it will not run; that
        /// box saves nothing, its whole tab starting each session at its defaults, so there is no saved
        /// index for the shorter list to disturb. </summary>
        public static readonly DeinterlaceMode[] Av1anModes =
            { DeinterlaceMode.Automatic, DeinterlaceMode.Disabled, DeinterlaceMode.Bwdif, DeinterlaceMode.Yadif };

        /// <summary>
        /// Why nothing on the AV1AN tab reaches QTGMC - the reason Automatic there resolves to bwdif,
        /// and now the reason the engine is not on that tab's list at all.
        /// <para/>
        /// av1an composes an ffmpeg command per chunk and there is nowhere in one to evaluate a
        /// VapourSynth script, so QTGMC there means rendering the whole file through it into a lossless
        /// intermediate first and encoding that. On the captures QTGMC exists for, QTGMC is the
        /// bottleneck rather than the encoder - so that shape spends the pass *and* the encode where
        /// Quick Convert's pipe overlaps them, and writes tens of gigabytes doing it. The engine is
        /// worth having; it is not worth having here.
        /// </summary>
        public const string Av1anQtgmcProblem = "av1an filters each chunk with ffmpeg, which cannot evaluate a " +
            "VapourSynth script, so QTGMC here would mean a serial pass over the whole file into a lossless " +
            "intermediate before the encode starts. Quick Convert pipes QTGMC straight into its encoder instead, " +
            "and the Deinterlace Video utility exports a deinterlaced file to encode here";

        /// <summary> Which plugin sets a background availability check has already been started for,
        /// so hovering over a file list does not queue one per file - and so switching to a preset
        /// with different requirements still gets asked about. </summary>
        private static readonly HashSet<string> probed = new HashSet<string>();

        /// <summary> The engine Quick Convert takes for a file that is interlaced - what it opens on,
        /// what Reset On New File goes back to, and what <see cref="ApplyScanVerdict"/> selects there.
        /// QTGMC because on a genuinely interlaced source the motion-compensated deinterlacer is the
        /// right answer often enough to be the one nobody has to go and pick, and on that tab it costs
        /// one pipelined pass rather than a pass of its own. </summary>
        public const DeinterlaceMode DefaultMode = DeinterlaceMode.Qtgmc;

        /// <summary> The same for the AV1AN tab, which has no QTGMC to default to. Automatic rather
        /// than Bwdif outright: the two do the same thing to an interlaced file - Automatic resolves
        /// through <see cref="Av1anQtgmcProblem"/> to bwdif - and Automatic also does nothing to a
        /// progressive one, which is the safer of the two to leave sitting in a box. </summary>
        public const DeinterlaceMode Av1anDefaultMode = DeinterlaceMode.Automatic;

        public static void Init()
        {
            int preset = Array.IndexOf(Qtgmc.Presets, Qtgmc.DefaultPreset);
            Form.EncDeintModeBox.SetItems(AllModes.Select(m => (object)GetLabel(m)), Array.IndexOf(AllModes, DefaultMode));
            Form.Av1anDeintModeBox.SetItems(Av1anModes.Select(m => (object)GetLabel(m)), Array.IndexOf(Av1anModes, Av1anDefaultMode));
            Form.EncDeintPresetBox.SetItems(Qtgmc.Presets.Select(p => (object)p), preset);
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
        /// <para/>
        /// **The loaded file's verdict has the last word, and 2.8.14 and 2.8.15 both shipped without
        /// that.** This used to write Automatic, which is safe whatever the file turns out to be -
        /// Automatic asks the verdict. Moving <see cref="DefaultMode"/> to QTGMC turned the same line
        /// into an engine picked *by name*, which runs over whatever it is handed, and nothing demoted
        /// it again for a file already measured: <see cref="AnalyzeInBackground"/> takes its early exit
        /// and <see cref="EnsureScanVerdictAsync"/> sees the verdict as already applied. So a reset
        /// landing on a progressive file - Settings' "Reset Now", or a trip through Batch mode and back -
        /// left QTGMC armed over a visible row, and Run started the whole pre-av1an pass on progressive
        /// video. Exactly the 2.8.12 failure, reached through the reset instead of through a saved
        /// setting.
        /// </summary>
        public static void ResetModes()
        {
            // Forgotten as well as overwritten: a reset is a person asking for the default back, so
            // there is no longer an earlier choice for a later interlaced file to reinstate.
            pickedQuickConvertMode = pickedAv1anMode = null;
            SetModeBoxes(DefaultMode, Av1anDefaultMode);

            // A reset is already a reset, so the preference gate inside does not apply here - what is
            // being asked for is the default, and the verdict decides what the default means for this
            // file. With no file, or none measured yet, this leaves DefaultMode standing: the row is
            // hidden until there is a verdict, and ModeInEffect reports Automatic while it is.
            ApplyScanVerdict(TrackList.current?.File, force: true);
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
        public static void ApplyScanVerdict(MediaFile file, bool force = false)
        {
            verdictAppliedTo = file;

            // Nothing to say about a file with no verdict yet - EnsureScanVerdictAsync waits for one.
            if (file?.Interlacing == null)
                return;

            bool interlaced = IsInterlaced(file);

            // An engine picked by hand is kept across an interlaced file where the reset is off - but it
            // has to be *put back*, not merely left alone, because the clause below demotes it for every
            // progressive file and this early return is what would then keep the demotion. A queue of
            // tapes with one progressive file among them lost the engine picked for it there and ran
            // every file after it on Automatic, which is the setting the whole queue was configured not
            // to use.
            if (interlaced && !force && !ResetSettingsOnNewFile.ResetDeinterlace)
            {
                SetModeBoxes(pickedQuickConvertMode, pickedAv1anMode);
                return;
            }

            // Demoting a file that is not interlaced to Automatic happens whatever the settings say, and
            // takes nothing away: Automatic does nothing to progressive video. Selecting the default for
            // one that *is* is a preference, which is why it sits behind the reset above. Per tab,
            // because the two have different defaults - Quick Convert can afford QTGMC and this is the
            // one place that difference is chosen rather than merely offered.
            SetModeBoxes(interlaced ? DefaultMode : DeinterlaceMode.Automatic,
                interlaced ? Av1anDefaultMode : DeinterlaceMode.Automatic);
        }

        /// <summary> The engine a person last chose in each tab's box, or null while nobody has. What
        /// <see cref="ApplyScanVerdict"/> reinstates for an interlaced file after a progressive one in
        /// the same queue demoted it. </summary>
        private static DeinterlaceMode? pickedQuickConvertMode, pickedAv1anMode;

        /// <summary> Set while this class writes the boxes, because their SelectionChanged fires for
        /// those writes too - and without it every demotion to Automatic would be recorded as the
        /// choice it is meant to be undoing. </summary>
        private static bool writingModeBoxes;

        /// <summary> Writes both tabs' mode boxes, leaving either alone for a null. Each is indexed
        /// against its own list, and a mode the AV1AN tab does not offer - QTGMC, arriving from a
        /// caller that only knows <see cref="DefaultMode"/> - lands on Automatic rather than on the -1
        /// <c>IndexOf</c> would hand back, which would leave the box showing nothing at all. </summary>
        private static void SetModeBoxes(DeinterlaceMode? quickConvert, DeinterlaceMode? av1an)
        {
            try
            {
                writingModeBoxes = true;

                if (quickConvert != null)
                    Form.EncDeintModeBox.SelectedIndex = Array.IndexOf(AllModes, quickConvert.Value);

                if (av1an != null)
                    Form.Av1anDeintModeBox.SelectedIndex = Math.Max(0, Array.IndexOf(Av1anModes, av1an.Value));
            }
            finally
            {
                // In a finally for the reason Av1anUi's own write guard is: left stuck on, no hand pick
                // would ever be recorded again.
                writingModeBoxes = false;
            }
        }

        /// <summary> Records a mode box being moved by a person, which is what the boxes' own
        /// SelectionChanged handlers call. This class's own writes are not that - see
        /// <see cref="writingModeBoxes"/>. </summary>
        public static void ModeBoxEdited(bool av1anTab)
        {
            if (writingModeBoxes)
                return;

            if (av1anTab)
                pickedAv1anMode = ModeOf(Form.Av1anDeintModeBox, Av1anModes);
            else
                pickedQuickConvertMode = ModeOf(Form.EncDeintModeBox, AllModes);
        }

        /// <summary> A box's selection as a mode, against whichever list fills that box - the two are
        /// different lengths, so reading one against the other would name the wrong engine. </summary>
        private static DeinterlaceMode ModeOf(ComboBox box, DeinterlaceMode[] modes)
        {
            return modes[box.SelectedIndex.Clamp(0, modes.Length - 1)];
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
        private static DeinterlaceMode ModeInEffect(ComboBox box, DeinterlaceMode[] modes, MediaFile file)
        {
            if (!IsRowRelevant(file))
                return DeinterlaceMode.Automatic;

            return ModeOf(box, modes);
        }

        /// <summary>
        /// The file Quick Convert will actually deinterlace, which in muxing mode is the one the checked
        /// video stream came from rather than whichever file the Track List happens to be showing. The
        /// two differ exactly when several files are loaded, and gating on the wrong one cuts both ways:
        /// a hidden row over a progressive Track List selection would demote an engine picked for the
        /// interlaced file being muxed in, and a row shown over an interlaced selection would carry
        /// QTGMC onto a progressive one.
        /// </summary>
        public static MediaFile GetQuickConvertSourceFile()
        {
            return TrackList.CheckedItems.FirstOrDefault(x => x.Stream.Type == Data.Streams.Stream.StreamType.Video)?.MediaFile
                ?? TrackList.current?.File;
        }

        public static DeinterlaceRequest GetQuickConvertRequest()
        {
            return new DeinterlaceRequest
            {
                Mode = ModeInEffect(Form.EncDeintModeBox, AllModes, GetQuickConvertSourceFile()),
                QtgmcPreset = Form.EncDeintPresetBox.GetText().IsEmpty() ? Qtgmc.DefaultPreset : Form.EncDeintPresetBox.GetText(),
                DoubleRate = Form.EncDeintDoubleRateBox.IsChecked == true,
            };
        }

        /// <summary>
        /// The AV1AN tab's request, which can only ever name an ffmpeg deinterlacer - QTGMC is not on
        /// <see cref="Av1anModes"/> and <see cref="Av1anQtgmcProblem"/> resolves Automatic away from it.
        /// <para/>
        /// Neither of the two settings beside the box on the other tab is asked for here, and both fall
        /// out of the same fact rather than being separately withheld. The preset is QTGMC's own speed
        /// setting. One frame per field was only ever offered where QTGMC ran as a pass in front, whose
        /// doubled rate is simply the rate of the file av1an then opens: a filter emitting one frame per
        /// field *inside* av1an writes twice the frames its chunking expects under the source's own
        /// rate, which is a file that plays at half speed.
        /// <para/>
        /// **<see cref="DeinterlaceRequest.DoubleRate"/> is therefore set to false rather than left
        /// out, and that is not tidiness.** Its default is *true* - one frame per field being the right
        /// answer for a deinterlacer that can have it - so a request that simply does not mention it
        /// asks for exactly the thing this tab must never send: measured, an omitted field here comes
        /// back as <c>bwdif=mode=send_field</c> in av1an's per-chunk chain. There used to be a line in
        /// <see cref="Av1an"/>'s run clearing it after the fact, for the case where a QTGMC pick fell
        /// back to bwdif; with QTGMC gone from the box the clearing belongs here, at the one place the
        /// request is built.
        /// </summary>
        public static DeinterlaceRequest GetAv1anRequest()
        {
            return new DeinterlaceRequest
            {
                // The AV1AN tab encodes the loaded file itself, so this one is not asked through
                // GetQuickConvertSourceFile - av1an is given TrackList.current and nothing else.
                Mode = ModeInEffect(Form.Av1anDeintModeBox, Av1anModes, TrackList.current?.File),
                QtgmcPreset = Qtgmc.DefaultPreset, // Never reached; the field is not nullable
                DoubleRate = false,                // Must be explicit - the field defaults to true
                QtgmcUnavailableHere = Av1anQtgmcProblem,
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

                Form.EncDeintInfoLabel.Text = Deinterlace.DescribeForUi(file, enc);
                Form.Av1anDeintInfoLabel.Text = Deinterlace.DescribeForUi(file, av1an);

                // Quick Convert only: the AV1AN row cannot reach QTGMC at all, so asking whether this
                // machine could run it would launch a VapourSynth probe for an answer that changes
                // nothing on that tab.
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
