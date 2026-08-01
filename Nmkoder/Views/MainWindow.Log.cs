using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.OS;
using Nmkoder.UI;
using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Nmkoder.Views
{
    partial class MainWindow
    {
        /// <summary>
        /// Whether the log should keep following new lines. Cleared when the user scrolls up and set
        /// again when they come back to the bottom - a log that yanks you back down every time a
        /// chunk finishes cannot be read while an encode is running, which is the only time anyone
        /// wants to read it.
        /// </summary>
        private bool _logFollowing = true;

        private ScrollViewer _logScroll;

        /// <summary>
        /// Wires the log box up. This runs from the constructor, where ListBox.Scroll is still null -
        /// it is a DirectProperty filled in by OnApplyTemplate - so the assignment below is only an
        /// optimistic first try and the `??=` fallbacks further down are what actually resolve it.
        /// The ScrollChanged handler does not care: it is attached to the ListBox, not the
        /// ScrollViewer, and the event bubbles up out of the template once there is one.
        /// </summary>
        private void SetUpLogBox()
        {
            LogBox.ItemsSource = Logger.Rows;
            _logScroll = LogBox.Scroll as ScrollViewer;

            // Bubbles from the template's ScrollViewer, so this reaches it without digging into the
            // template. Growth and a user scroll are told apart by which delta is non-zero: appending
            // lines moves the extent, dragging moves the offset.
            LogBox.AddHandler(ScrollViewer.ScrollChangedEvent, LogBox_ScrollChanged);

            Logger.Rows.CollectionChanged += LogRows_CollectionChanged;
            Logger.Cleared += OnLogCleared;
        }

        /// <summary>
        /// Emptying the box produces no scroll event - the offset was already where it was - so the
        /// "scrolled up" state would otherwise stick on forever over an empty log. Reachable without
        /// touching the Clear button: importing files with "clear existing" empties the log too.
        /// </summary>
        private void OnLogCleared()
        {
            _logFollowing = true;
            Logger.FollowingSuspended = false;
            LogPausedLabel.IsVisible = false;
        }

        private bool _logScrollPending;

        private void LogRows_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            // One pending re-pin at a time. A tool that logs three thousand lines in a burst would
            // otherwise queue three thousand identical scrolls, all but the last of them wasted.
            if (!_logFollowing || _logScrollPending)
                return;

            _logScrollPending = true;

            // After the list has laid the new rows out, not before them.
            Dispatcher.UIThread.Post(() =>
            {
                _logScrollPending = false;
                ScrollLogToEnd();
            }, DispatcherPriority.Background);
        }

        /// <summary>
        /// Pins the view to the last line without touching the horizontal offset. ScrollToEnd would
        /// do the vertical part and throw the horizontal part away - it scrolls to the bottom *left*
        /// corner - which on a log full of full-length ffmpeg command lines means the column you were
        /// reading jumps away every time a line arrives.
        /// <para/>
        /// The offset can still be clamped back to the left by the list itself: a virtualizing list's
        /// width is the width of the rows it has realized, so scrolling away from a very long line
        /// shrinks the extent under the offset. Nothing to be done about that short of giving up
        /// virtualization, and it only bites while following - reading a long line means scrolling
        /// up, which stops this from running at all.
        /// </summary>
        private void ScrollLogToEnd()
        {
            ScrollViewer sv = _logScroll ??= LogBox.Scroll as ScrollViewer;

            if (sv == null)
                return;

            try
            {
                double maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
                sv.Offset = new Vector(sv.Offset.X, maxY);
            }
            catch (Exception e)
            {
                Logger.Log($"Could not scroll the log: {e.Message}", true, level: Logger.Level.Debug);
            }
        }

        private void LogBox_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            ScrollViewer sv = _logScroll ??= LogBox.Scroll as ScrollViewer;

            if (sv == null)
                return;

            // Only a change the user made decides whether to keep following. Content arriving moves
            // the extent while leaving the offset alone, and reading that as "the user scrolled" would
            // unpause the moment anything was logged.
            if (e.OffsetDelta.Y == 0)
                return;

            // A line and a bit of slack, so landing one pixel short of the end still counts as being
            // at the end.
            bool atBottom = sv.Offset.Y >= sv.Extent.Height - sv.Viewport.Height - 24;

            if (atBottom == _logFollowing)
                return;

            _logFollowing = atBottom;
            Logger.FollowingSuspended = !atBottom; // Holds off trimming, which would slide the content under them
            LogPausedLabel.IsVisible = !atBottom;

            if (atBottom)
                ScrollLogToEnd();
        }

        private async void LogCopy_Click(object sender, RoutedEventArgs e) => await CopyLog();

        /// <summary>
        /// Copies the whole log. Built from the row model rather than by driving the control's own
        /// selection, which measures a thousand times slower over a few thousand lines - and which
        /// could not be driven across rows anyway, since each row selects independently. Selecting
        /// part of one line and pressing Ctrl+C is handled by the row's own SelectableTextBlock.
        /// </summary>
        private async Task CopyLog()
        {
            try
            {
                string text = Logger.GetBoxText();

                if (text.IsEmpty())
                    return;

                IClipboard clipboard = TopLevel.GetTopLevel(this)?.Clipboard;

                if (clipboard == null)
                {
                    Logger.LogWarn("Could not reach the clipboard.");
                    return;
                }

                await clipboard.SetTextAsync(text);
                SetStatus($"Copied {Logger.Rows.Count} log lines to the clipboard.", silent: true);
            }
            catch (Exception ex)
            {
                Logger.LogErr($"Could not copy the log: {ex.Message}");
            }
        }

        private async void LogSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string suggested = $"nmkoder-log-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.txt";
                string path = await Pickers.PickSavePath(this, "Save the log", suggested);

                if (path.IsEmpty())
                    return;

                await File.WriteAllTextAsync(path, Logger.GetBoxText());
                Logger.Log($"Saved the log to '{path}'.");
            }
            catch (Exception ex)
            {
                Logger.LogErr($"Could not save the log: {ex.Message}");
            }
        }

        /// <summary>
        /// Opens this session's log folder. That is where the per-tool logs live too - ffmpeg.txt,
        /// av1an.txt, mkvmerge.txt - which several failure messages point at and nothing else opened.
        /// </summary>
        private void LogFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Shell.OpenWithDefaultHandler(Paths.GetLogPath());
            }
            catch (Exception ex)
            {
                Logger.LogErr($"Could not open the log folder: {ex.Message}");
            }
        }

        private void LogClear_Click(object sender, RoutedEventArgs e)
        {
            Logger.ClearLogBox(); // The follow state is reset from the Cleared event, which every clear raises
        }
    }
}
