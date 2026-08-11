using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Media;
using Nmkoder.UI;
using Nmkoder.Utils;
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Nmkoder.Views
{
    /// <summary>
    /// Visual in/out point editor, in the shape LosslessCut made familiar: the frame at the playhead
    /// is on screen while the section is picked, so the points are chosen by looking at the video
    /// rather than by guessing timestamps and running the encode to find out.
    ///
    /// The same dialog configures the trim an encode applies, the trim the AV1AN tab applies and
    /// the standalone lossless cut utility - they differ in what happens to the section afterwards,
    /// not in how it is picked.
    /// </summary>
    public partial class CutWindow : Window
    {
        public enum Purpose { Trim, Av1anTrim, LosslessCut }

        /// <summary> The configured range. Null means "no cut". </summary>
        public TrimSettings Result { get; private set; }

        private Purpose _purpose;
        private bool _videoIsCopied;
        private string _videoPath = "";
        private long _durationMs;
        private double _fps = 30d;
        private long _startMs, _endMs, _positionMs;

        private bool _ready;
        private bool _confirmed;
        private bool _closed;

        // Preview frames are extracted one at a time: a drag asks for a position, and whichever
        // position it has reached when the previous frame is done is the one extracted next.
        private string _previewDir = "";
        private int _previewCounter;
        private long _previewWanted = -1, _previewShown = -1;
        private bool _previewBusy;
        private Bitmap _previewBitmap, _previousBitmap;

        // Keyframe lookup, throttled the same way, plus a delay so dragging does not probe per pixel.
        private long _kfWanted = -1, _kfDone = long.MinValue, _kfResultMs = -1;
        private bool _kfBusy;

        /// <summary> The dialog currently open, if any. Two of these at once cannot be told apart
        /// afterwards: both write the same setting on their way out, so the one closed last wins and
        /// a window the user never touched could overwrite the one they filled in. </summary>
        private static CutWindow _open;

        public CutWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Configures the trim that the Quick Encode tab applies while encoding.
        /// <para/>
        /// <paramref name="videoIsCopied"/> is what decides whether that section is copied out of the
        /// source or re-encoded, and it changes this dialog into the shape the other two purposes
        /// already have - see <see cref="SectionIsCopied"/>, which is the one statement of what follows
        /// from it.
        /// </summary>
        public static async Task<TrimSettings> ShowForTrim(MediaFile file, TrimSettings saved, bool videoIsCopied)
        {
            return await Show(Purpose.Trim, file, saved, videoIsCopied);
        }

        /// <summary> Configures the section the AV1AN tab encodes. Always a copy: av1an has no trim of
        /// its own, so the section is cut out of the source before av1an is run on it. </summary>
        public static async Task<TrimSettings> ShowForAv1anTrim(MediaFile file, TrimSettings saved)
        {
            return await Show(Purpose.Av1anTrim, file, saved, true);
        }

        /// <summary> Configures the section the lossless cut utility copies out. </summary>
        public static async Task<TrimSettings> ShowForCut(MediaFile file, TrimSettings saved)
        {
            return await Show(Purpose.LosslessCut, file, saved, true);
        }

        private static async Task<TrimSettings> Show(Purpose purpose, MediaFile file, TrimSettings saved, bool videoIsCopied)
        {
            if (_open != null) // Already picking a section - a second copy of this answers nothing
            {
                _open.Activate();
                return saved;
            }

            var window = new CutWindow();
            window.Load(purpose, file, saved, videoIsCopied);
            _open = window;

            try
            {
                Window owner = UiUtils.MainWindowHandle;

                if (owner != null && owner.IsVisible)
                    await window.ShowDialog(owner);
                else
                    window.Show();
            }
            finally
            {
                _open = null;
            }

            // Dismissing without confirming keeps whatever was configured before.
            return window._confirmed ? window.Result : saved;
        }

        private void Load(Purpose purpose, MediaFile file, TrimSettings saved, bool videoIsCopied)
        {
            _ready = false;
            _purpose = purpose;
            _videoIsCopied = videoIsCopied;
            _durationMs = file?.DurationMs ?? 0;

            var videoStream = file?.VideoStreams.FirstOrDefault();

            if (videoStream != null && videoStream.Rate.GetFloat() > 0f)
                _fps = videoStream.Rate.GetFloat();

            // An image sequence folder has no seekable video to scrub, so it gets the fields only.
            if (file != null && !file.IsDirectory)
                _videoPath = file.ImportPath.IsNotEmpty() ? file.ImportPath : file.SourcePath;

            // The three modes are offered where the section is re-encoded and nowhere else: a copy
            // begins at a keyframe whatever it was asked for, so there is nothing to choose between -
            // and the two exact modes are worse than nothing there, seeking the output rather than the
            // input, which over a copy lands on the *next* keyframe and drops the frames in between.
            bool modes = purpose == Purpose.Trim && !videoIsCopied;
            Title = purpose == Purpose.LosslessCut ? "Cut Video" : "Configure Trim";
            ModeLabel.IsVisible = ModeBox.IsVisible = modes;
            ModeBox.SelectedIndex = modes ? (int)(saved?.TrimMode ?? TrimSettings.Mode.TimeKeyframe) : 0;

            HintLabel.Text = purpose == Purpose.LosslessCut
                ? "The section between the two points is copied into a new file without re-encoding, which takes seconds rather than as long as an encode. A copy can only begin at a keyframe, so the start point is moved back to the closest one on its own. Press Run to cut."
                : purpose == Purpose.Av1anTrim
                ? "av1an has no trim of its own, so the section is first copied out of the source without re-encoding and av1an is run on that copy. A copy can only begin at a keyframe, so the start point is moved back to the closest one on its own."
                : videoIsCopied
                ? "The video is being copied rather than re-encoded, so the section is cut out of the source as it is. A copy can only begin at a keyframe, so the start point is moved back to the closest one on its own - pick a video codec to encode with if you need the section to start exactly where it is set."
                : "Times use the HH:MM:SS or HH:MM:SS.mmm format. In frame mode, enter plain frame numbers. The section outside the start and end point is dropped while encoding.";

            // Snapping the start point is the button's whole job, and where it happens on its own
            // there is nothing left to press.
            SnapBtn.IsVisible = !KeyframeSnapAutomatic;

            LoadRange(saved);

            PositionSlider.Maximum = Math.Max(1, _durationMs);
            PositionSlider.IsEnabled = _durationMs > 0;

            if (_videoPath.IsEmpty())
                PreviewLabel.Text = _durationMs > 0 ? "No preview for this input." : "No file loaded - the points below are still applied.";

            _ready = true;

            SetPosition(_startMs);
            RequestKeyframeNote();
        }

        private void LoadRange(TrimSettings saved)
        {
            if (saved != null && !saved.IsUnset)
            {
                bool frames = saved.TrimMode == TrimSettings.Mode.FrameNumbers;
                _startMs = frames ? FramesToMs(saved.StartTime) : saved.StartTime;
                _endMs = frames ? FramesToMs(saved.EndTime) : saved.EndTime;
            }
            else
            {
                _startMs = 0;
                _endMs = _durationMs;
            }

            WriteFields();
        }

        #region Playhead

        private void Position_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (!_ready)
                return;

            _positionMs = ((long)e.NewValue).Clamp(0, Math.Max(0, _durationMs));
            UpdatePositionLabel();
            UpdateBars();
            RequestPreview();
        }

        /// <summary> Moves the playhead, and the scrubber with it, without the slider's own handler
        /// running a second round of the same updates. </summary>
        private void SetPosition(long ms)
        {
            _positionMs = ms.Clamp(0, Math.Max(0, _durationMs));

            bool ready = _ready;
            _ready = false;
            PositionSlider.Value = _positionMs;
            _ready = ready;

            UpdatePositionLabel();
            UpdateBars();
            RequestPreview();
        }

        private void NudgeMs_Click(object sender, RoutedEventArgs e)
        {
            SetPosition(_positionMs + ((sender as Button)?.Tag as string ?? "").GetLong());
        }

        private void NudgeFrames_Click(object sender, RoutedEventArgs e)
        {
            long frames = ((sender as Button)?.Tag as string ?? "").GetLong();
            SetPosition(_positionMs + (long)Math.Round(frames * 1000d / _fps));
        }

        private void JumpToStart_Click(object sender, RoutedEventArgs e) => SetPosition(_startMs);
        private void JumpToEnd_Click(object sender, RoutedEventArgs e) => SetPosition(_endMs);

        private void UpdatePositionLabel()
        {
            string frame = _fps > 0 ? $" (frame {MsToFrames(_positionMs)})" : "";
            PositionLabel.Text = $"{TrimSettings.GetTimeString(TimeSpan.FromMilliseconds(_positionMs))}{frame}";
        }

        #endregion

        #region Range

        private void SetStart_Click(object sender, RoutedEventArgs e)
        {
            _startMs = _positionMs;

            if (_endMs <= _startMs) // A start behind the end point would leave nothing to keep
                _endMs = Math.Max(_durationMs, _startMs);

            WriteFields();
            UpdateBars();
            RequestKeyframeNote();
        }

        private void SetEnd_Click(object sender, RoutedEventArgs e)
        {
            _endMs = _positionMs;

            if (_startMs >= _endMs)
                _startMs = 0;

            WriteFields();
            UpdateBars();
            RequestKeyframeNote();
        }

        private void Range_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_ready)
                return;

            if (TryReadFields(out long start, out long end))
            {
                _startMs = start;
                _endMs = end;
                UpdateBars();
                RequestKeyframeNote();
            }

            UpdateDurationField();
        }

        private void Mode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready)
                return;

            WriteFields(); // Same range, written as frame numbers or as timestamps
            RequestKeyframeNote();
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            _startMs = 0;
            _endMs = _durationMs;
            WriteFields();
            SetPosition(0);
            RequestKeyframeNote();
        }

        private async void Confirm_Click(object sender, RoutedEventArgs e)
        {
            if (_closed)
                return;

            if (!TryReadFields(out long start, out long end))
            {
                await UiUtils.ShowMessageBox($"Invalid input.\n\n{(IsFrameMode() ? "Please enter numeric values only." : "Please use the HH:MM:SS (or HH:MM:SS.mmm) format.")}", UiUtils.MessageType.Error);
                return;
            }

            // A range that runs backwards used to be confirmed and then thrown away: BuildResult calls a
            // zero-or-negative length unset and returns null, so the dialog closed, the setting was
            // cleared, and the button went back to reading "Configure…" with nothing said. End equal to
            // start is left alone - that is the empty range, and clearing is a fair reading of it.
            if (end < start)
            {
                await UiUtils.ShowMessageBox($"The end point is before the start point.\n\n" +
                    $"{(IsFrameMode() ? $"Frame {MsToFrames(end)} comes before frame {MsToFrames(start)}" : $"{TrimSettings.GetTimeString(TimeSpan.FromMilliseconds(end))} comes before {TrimSettings.GetTimeString(TimeSpan.FromMilliseconds(start))}")}.",
                    UiUtils.MessageType.Error);
                return;
            }

            _startMs = start;
            _endMs = end;

            await SnapStartBeforeClosing();

            if (_closed) // Confirmed twice while the probe above ran - the first one already closed
                return;

            Result = BuildResult();
            _confirmed = true;
            Close();
        }

        private TrimSettings BuildResult()
        {
            // A copied section is the keyframe mode whatever the box was last left on: that mode is the
            // input-side seek, and it is the only one a copy can carry out. See SectionIsCopied.
            TrimSettings.Mode mode = SectionIsCopied ? TrimSettings.Mode.TimeKeyframe : (TrimSettings.Mode)Math.Max(0, ModeBox.SelectedIndex);
            bool frames = mode == TrimSettings.Mode.FrameNumbers;

            long start = frames ? MsToFrames(_startMs) : _startMs;
            long end = frames ? MsToFrames(_endMs) : _endMs;

            var settings = new TrimSettings()
            {
                TrimMode = mode,
                StartTime = start,
                EndTime = end,
                Duration = Math.Max(0, end - start)
            };

            return settings.IsUnset ? null : settings;
        }

        private bool IsFrameMode()
        {
            return ModeBox.IsVisible && ModeBox.SelectedIndex == 2;
        }

        private long MsToFrames(long ms) => (long)Math.Round(ms / 1000d * _fps);
        private long FramesToMs(long frames) => (long)Math.Round(frames * 1000d / _fps);

        private string FormatValue(long ms)
        {
            return IsFrameMode() ? MsToFrames(ms).ToString() : TrimSettings.GetTimeString(TimeSpan.FromMilliseconds(ms));
        }

        /// <summary> Writes the current range into the boxes in whichever unit the mode uses. </summary>
        private void WriteFields()
        {
            bool ready = _ready;
            _ready = false;
            StartBox.Text = FormatValue(_startMs);
            EndBox.Text = FormatValue(_endMs);
            _ready = ready;

            UpdateDurationField();
        }

        /// <summary> Reads both boxes back as milliseconds. False means one of them does not parse,
        /// in which case the range is left as it was. </summary>
        private bool TryReadFields(out long startMs, out long endMs)
        {
            startMs = _startMs;
            endMs = _endMs;

            if (IsFrameMode())
            {
                if (!Regex.IsMatch(StartBox.Text ?? "", @"^\d+$") || !Regex.IsMatch(EndBox.Text ?? "", @"^\d+$"))
                    return false;

                startMs = FramesToMs((StartBox.Text ?? "").GetLong());
                endMs = FramesToMs((EndBox.Text ?? "").GetLong());
                return true;
            }

            if (!TryParseTime(StartBox.Text, out TimeSpan start) || !TryParseTime(EndBox.Text, out TimeSpan end))
                return false;

            startMs = (long)start.TotalMilliseconds;
            endMs = (long)end.TotalMilliseconds;
            return true;
        }

        private static bool TryParseTime(string text, out TimeSpan ts)
        {
            text = text ?? "";

            if (text.Contains(":"))
                return TimeSpan.TryParse(text, out ts);

            ts = TimeSpan.FromSeconds(text.GetInt());
            return true;
        }

        private void UpdateDurationField()
        {
            DurationBox.Text = IsFrameMode()
                ? Math.Max(0, MsToFrames(_endMs) - MsToFrames(_startMs)).ToString()
                : TrimSettings.GetTimeString(TimeSpan.FromMilliseconds(Math.Max(0, _endMs - _startMs)));
        }

        /// <summary> Redraws the kept section and the playhead marker. Both are star-weighted grid
        /// columns, so they follow the window's width without any pixel arithmetic. </summary>
        private void UpdateBars()
        {
            double duration = Math.Max(1, _durationMs);
            double start = Math.Clamp(_startMs / duration, 0, 1);
            double end = Math.Clamp(_endMs / duration, start, 1);
            double position = Math.Clamp(_positionMs / duration, 0, 1);

            RangeBar.ColumnDefinitions[0].Width = new GridLength(start, GridUnitType.Star);
            RangeBar.ColumnDefinitions[1].Width = new GridLength(Math.Max(0.001, end - start), GridUnitType.Star);
            RangeBar.ColumnDefinitions[2].Width = new GridLength(Math.Max(0, 1 - end), GridUnitType.Star);

            PlayheadBar.ColumnDefinitions[0].Width = new GridLength(position, GridUnitType.Star);
            PlayheadBar.ColumnDefinitions[2].Width = new GridLength(Math.Max(0, 1 - position), GridUnitType.Star);
        }

        #endregion

        #region Preview

        private void RequestPreview()
        {
            if (_videoPath.IsEmpty() || _closed)
                return;

            _previewWanted = _positionMs;

            if (_previewBusy)
                return;

            _previewBusy = true;
            _ = RunPreviewLoop();
        }

        private async Task RunPreviewLoop()
        {
            try
            {
                if (_previewDir.IsEmpty())
                {
                    _previewDir = Path.Combine(Paths.GetSessionDataPath(), "cutPreview");
                    Directory.CreateDirectory(_previewDir);
                }

                while (!_closed && _previewWanted != _previewShown)
                {
                    long wanted = _previewWanted;

                    if (_previewBitmap == null)
                        PreviewLabel.Text = "Loading preview…";

                    // Two filenames in rotation: the one on screen is never the one being written.
                    string path = Path.Combine(_previewDir, $"preview{_previewCounter++ % 2}.jpg");
                    IoUtils.TryDeleteIfExists(path);

                    // Seeking to the very last moment of a file decodes no frame at all.
                    await FfmpegExtract.ExtractSingleFrameAtMs(_videoPath, path, wanted.Clamp(0, Math.Max(0, _durationMs - 100)), 480);

                    if (_closed)
                        return;

                    _previewShown = wanted;
                    ShowPreviewFrame(path);
                }
            }
            catch (Exception e)
            {
                Logger.Log($"Cut preview failed: {e.Message}", true);
                PreviewLabel.Text = "Could not extract a preview frame.";
            }
            finally
            {
                _previewBusy = false;
            }
        }

        private void ShowPreviewFrame(string path)
        {
            if (!File.Exists(path))
            {
                PreviewLabel.Text = "No frame at this position.";
                return;
            }

            try
            {
                Bitmap bitmap;

                using (var stream = File.OpenRead(path)) // Decoded up front, so nothing holds the file we overwrite next
                    bitmap = new Bitmap(stream);

                PreviewImage.Source = bitmap;
                PreviewLabel.Text = "";

                // Kept one generation behind: the frame just replaced can still be in the middle of
                // being drawn, and disposing it out from under the renderer is not survivable.
                _previousBitmap?.Dispose();
                _previousBitmap = _previewBitmap;
                _previewBitmap = bitmap;
            }
            catch (Exception e)
            {
                Logger.Log($"Failed to load the preview frame: {e.Message}", true);
            }
        }

        #endregion

        #region Keyframes

        /// <summary>
        /// Whether this section is copied out of the source rather than re-encoded, which is the one
        /// thing everything below turns on. The AV1AN trim and the standalone cut always are; the Quick
        /// Encode trim is whenever its video codec is the copy.
        /// <para/>
        /// It is the codec that decides this and not the tab, which is what a round of measurement
        /// against the bundled FFmpeg settled. An input-side seek in front of a *re-encode* is exact:
        /// FFmpeg's default accurate_seek seeks to the keyframe and then decodes and discards up to the
        /// point, so a section asked for at frame 30 of a source with keyframes every 48 begins on
        /// frame 30 - measured, through MKV/H.264 and MP4/HEVC alike, and identical to what the exact
        /// mode's output-side seek produces. The same seek in front of a *copy* begins on frame 0, the
        /// keyframe at or before, there being no decode to discard with.
        /// </summary>
        private bool SectionIsCopied
        {
            get { return _purpose != Purpose.Trim || _videoIsCopied; }
        }

        /// <summary> A stream copy can only begin at a keyframe, so where the closest one sits is
        /// worth showing - but only when the section is actually copied. A re-encode starts exactly
        /// where it was told to, in every one of the three modes, so a keyframe note there would
        /// describe a cut that is not the one produced. </summary>
        private bool KeyframeSnapRelevant
        {
            get { return _videoPath.IsNotEmpty() && SectionIsCopied; }
        }

        /// <summary> Whether the start point moves onto that keyframe on its own instead of the move
        /// being offered as a button. Every section that ends in a stream copy does it - the AV1AN
        /// trim, whose section is cut out before av1an ever sees it, the standalone cut, and now a
        /// Quick Encode trim whose video codec is the copy - because declining it there buys nothing:
        /// the copy begins at the keyframe whatever this field says, so refusing the move only leaves
        /// the dialog describing a section that is not the one produced. Snapping does not change a
        /// frame of what comes out. It makes the range shown here the range really copied, run-up and
        /// all, which is also the duration the copy's own progress is measured against.
        ///
        /// A re-encoded section is the opposite case and must never be snapped: it begins exactly where
        /// it was set, so moving the start point back to a keyframe would put video the user did not
        /// ask for at the front of the output - up to a whole GOP of it, silently. That is why this
        /// follows <see cref="SectionIsCopied"/> rather than the tab the dialog was opened from. </summary>
        private bool KeyframeSnapAutomatic
        {
            get { return SectionIsCopied; }
        }

        private void RequestKeyframeNote()
        {
            if (!KeyframeSnapRelevant || _closed)
            {
                KeyframeNote.IsVisible = false;
                SnapBtn.IsEnabled = false;
                return;
            }

            KeyframeNote.IsVisible = true;
            _kfWanted = _startMs;

            if (_kfBusy)
                return;

            _kfBusy = true;
            _ = RunKeyframeLoop();
        }

        private async Task RunKeyframeLoop()
        {
            try
            {
                while (!_closed && _kfWanted != _kfDone)
                {
                    long wanted = _kfWanted;
                    KeyframeNote.Text = "Looking for the closest keyframe…";
                    SnapBtn.IsEnabled = false;

                    await Task.Delay(350); // Let a drag come to rest before probing

                    if (_closed)
                        return;

                    if (_kfWanted != wanted)
                        continue;

                    long keyframeMs = await FfmpegUtils.GetKeyframeMsAtOrBefore(_videoPath, wanted);

                    if (_closed)
                        return;

                    if (_kfWanted != wanted) // Moved on while the probe ran - that answer is stale
                        continue;

                    _kfDone = wanted;
                    _kfResultMs = keyframeMs;

                    // A snap answers its own question - the start point now sits on that keyframe -
                    // so the loop is told the new point is resolved rather than asking the file again
                    // to be told what it just did.
                    if (TryAutoSnap(keyframeMs))
                        _kfWanted = _kfDone = _startMs;
                    else
                        WriteKeyframeNote(wanted, keyframeMs);
                }
            }
            catch (Exception e)
            {
                Logger.Log($"Keyframe lookup failed: {e.Message}", true);
                KeyframeNote.Text = "";
            }
            finally
            {
                _kfBusy = false;
            }
        }

        private void WriteKeyframeNote(long startMs, long keyframeMs)
        {
            if (keyframeMs < 0)
            {
                KeyframeNote.Text = "Could not read this file's keyframes, so the cut may begin slightly before the start point.";
                SnapBtn.IsEnabled = false;
                return;
            }

            long offset = startMs - keyframeMs;

            if (offset <= 0)
            {
                KeyframeNote.Text = "The start point is on a keyframe, so the cut begins exactly there.";
                SnapBtn.IsEnabled = false;
                return;
            }

            string keyframe = TrimSettings.GetTimeString(TimeSpan.FromMilliseconds(keyframeMs));

            // Automatic and still off the keyframe is the one case the snap is held back for: the
            // start point is mid-edit, so this says what is about to happen rather than offering it.
            KeyframeNote.Text = KeyframeSnapAutomatic
                ? $"The closest keyframe at or before the start point is at {keyframe}, and the start point moves back to it once this field is done being edited."
                : $"The closest keyframe at or before the start point is at {keyframe}, so a copy begins {(offset / 1000d).ToString("0.##")}s earlier than the start point.";

            SnapBtn.IsEnabled = !KeyframeSnapAutomatic;
        }

        /// <summary>
        /// Moves the start point onto <paramref name="keyframeMs"/> where the purpose snaps on its
        /// own, and says whether it did. The end point stays where it is, so the section grows by
        /// however far back the keyframe sits rather than sliding: the frames asked for are all still
        /// in it, with the run-up the copy has to include in front of them.
        /// <para/>
        /// The playhead follows, the same as it does off the button, so the preview is showing the
        /// frame the encode really opens on rather than the one that was picked a moment ago. The
        /// cost is that nudging a frame past a keyframe and setting the start point there lands back
        /// on the keyframe - which is the honest answer, since a copy cannot begin anywhere else.
        /// </summary>
        private bool TryAutoSnap(long keyframeMs)
        {
            if (!KeyframeSnapAutomatic || !KeyframeSnapRelevant || keyframeMs < 0 || keyframeMs >= _startMs)
                return false;

            // Rewriting the box under the caret would swallow the digits still coming, so a start
            // point being typed is snapped when it is finished instead of between two keystrokes.
            if (StartBox.IsFocused)
                return false;

            long offset = _startMs - keyframeMs;
            _startMs = keyframeMs;
            WriteFields();
            UpdateBars();
            SetPosition(_startMs);

            KeyframeNote.Text = $"The start point was moved back to the keyframe at {TrimSettings.GetTimeString(TimeSpan.FromMilliseconds(keyframeMs))} " +
                $"({(offset / 1000d).ToString("0.##")}s earlier), because a copy can only begin at one.";
            SnapBtn.IsEnabled = false;
            return true;
        }

        /// <summary> Applies a snap the box's own focus held back, now that it has lost it. </summary>
        private void StartBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!_ready || _kfBusy) // Busy means a probe is already on its way to doing this
                return;

            if (_kfDone != _startMs) // No answer for this point yet
                RequestKeyframeNote();
            else if (TryAutoSnap(_kfResultMs))
                _kfWanted = _kfDone = _startMs;
        }

        /// <summary>
        /// Snaps a start point that never got the chance - typed into the box and confirmed before the
        /// probe behind it had run, or while the box still held focus. The dialog closes on the range
        /// that is really encoded whichever route the start point took to get here.
        /// </summary>
        private async Task SnapStartBeforeClosing()
        {
            if (!KeyframeSnapAutomatic || !KeyframeSnapRelevant)
                return;

            if (_kfDone != _startMs) // Never asked about this point, or asked about a different one
            {
                KeyframeNote.Text = "Looking for the closest keyframe…";
                _kfResultMs = await FfmpegUtils.GetKeyframeMsAtOrBefore(_videoPath, _startMs);
                _kfDone = _startMs;
            }

            if (_kfResultMs >= 0 && _kfResultMs < _startMs)
                _startMs = _kfResultMs;
        }

        private void SnapToKeyframe_Click(object sender, RoutedEventArgs e)
        {
            if (_kfResultMs < 0)
                return;

            _startMs = _kfResultMs;
            WriteFields();
            UpdateBars();
            SetPosition(_startMs);
            RequestKeyframeNote();
        }

        #endregion

        protected override void OnClosed(EventArgs e)
        {
            _closed = true;
            PreviewImage.Source = null;
            _previewBitmap?.Dispose();
            _previousBitmap?.Dispose();
            _previewBitmap = _previousBitmap = null;

            if (_previewDir.IsNotEmpty())
                IoUtils.DeleteContentsOfDir(_previewDir);

            base.OnClosed(e);
        }
    }
}
