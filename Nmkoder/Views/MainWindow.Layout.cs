using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Nmkoder.Extensions;
using Nmkoder.IO;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Nmkoder.Views
{
    /// <summary>
    /// The parts of the window the user arranges rather than configures: how big it is and where,
    /// how much of it the log gets, and which tab was open. None of it belongs with the encoding
    /// settings, but all of it is just as tedious to redo on every launch.
    /// </summary>
    partial class MainWindow
    {
        /// <summary>
        /// Size and position the window would go back to if it were un-maximized. A maximized
        /// window reports the maximized frame as its own size and position, so saving those as the
        /// geometry reopens a screen-sized window with no memory of what it looked like before.
        /// </summary>
        private PixelPoint _normalPos;
        private Size _normalSize;
        private bool _normalKnown;

        /// <summary> Floor for the log's height, restoring and saving alike: a few pixels of log is
        /// a splitter the user has to go find before they can read anything. </summary>
        private const double minLogHeight = 60;

        /// <summary> Share of the window the log may take up when its saved height is restored into
        /// a window smaller than the one it was dragged in. </summary>
        private const double maxLogShare = 0.6;

        private RowDefinition LogRow => RootGrid.RowDefinitions[Grid.GetRow(LogPanel)];

        /// <summary>
        /// Runs from the constructor rather than from Opened: WindowStartupLocation is applied when
        /// the window is shown, so a position restored any later is immediately centred over.
        /// </summary>
        private void RestoreLayout()
        {
            Restore(RestoreGeometry, "size and position");
            Restore(RestoreLogHeight, "log height");
            Restore(RestoreSelectedTab, "selected tab");

            PositionChanged += (s, e) => TrackNormalBounds();
            Resized += (s, e) => TrackNormalBounds();
        }

        /// <summary> One at a time, because none of the three is worth losing either of the others
        /// to - and none of them is worth failing to start over. </summary>
        private static void Restore(Action restore, string what)
        {
            try
            {
                restore();
            }
            catch (Exception e)
            {
                Logger.Log($"Failed to restore the window's {what}: {e.Message}", true);
            }
        }

        /// <summary>
        /// The last size and position the window had while neither maximized nor minimized, which
        /// is the only state either of them means anything in.
        ///
        /// The values are read now and believed a moment later, deliberately. Win32 and macOS
        /// answer WindowState out of a field Avalonia fills in from its own state-changed callback,
        /// and Win32 raises Resized for a maximize before running that callback - so asking here
        /// would be told "Normal" while Width and Height already describe the maximized window, and
        /// the size to un-maximize back to would be lost the first time anyone maximized. Posting
        /// the decision lets the state notification go first.
        /// </summary>
        private void TrackNormalBounds()
        {
            PixelPoint pos = Position;
            var size = new Size(Width, Height);

            Dispatcher.UIThread.Post(() =>
            {
                if (WindowState != WindowState.Normal)
                    return;

                _normalPos = pos;
                _normalSize = size;
                _normalKnown = true;
            }, DispatcherPriority.Background);
        }

        private void RestoreGeometry()
        {
            // x,y,w,h,maximized - written by SaveLayout. Anything else is a config from a version
            // that stored something different, and the XAML's own size stands.
            string[] parts = (Config.Get(Config.Key.WindowGeometry) ?? "").Split(',');

            if (parts.Length != 5)
                return;

            var pos = new PixelPoint(parts[0].GetInt(), parts[1].GetInt());
            double w = parts[2].GetInt();
            double h = parts[3].GetInt();

            if (w < 1 || h < 1)
                return;

            IReadOnlyList<Screen> screens = AllScreens();
            Screen screen = ScreenAt(screens, new PixelRect(pos, PixelSize.FromSize(new Size(w, h), Scaling)));

            // Falling back to the primary monitor when the saved position is unusable: the size
            // still has to be checked against something, and that is where the window will open.
            ApplySize(w, h, screen ?? screens.FirstOrDefault(x => x.IsPrimary) ?? screens.FirstOrDefault());

            // Restoring maximized still needs the normal bounds recorded, because a session that
            // stays maximized start to finish never raises the events that would capture them and
            // would otherwise save nothing to un-maximize back to.
            _normalPos = pos;
            _normalSize = new Size(Width, Height);
            _normalKnown = true;

            if (screen == null)
                return; // Size kept, but where it goes is better left to WindowStartupLocation

            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = pos;

            if (parts[4].GetBool())
                WindowState = WindowState.Maximized;
        }

        /// <summary>
        /// Ratio between the window's own coordinates and the screen's. Deliberately DesktopScaling
        /// and pointedly not Screen.Scaling: on macOS the latter reports the backing scale factor
        /// while the window is already measured in points, which would halve every restored size on
        /// a Retina display.
        /// </summary>
        private double Scaling => DesktopScaling > 0 ? DesktopScaling : 1;

        /// <summary> The connected monitors, or none when the windowing backend cannot say: Screens
        /// throws rather than returning null before it is initialised. </summary>
        private IReadOnlyList<Screen> AllScreens()
        {
            try
            {
                return Screens?.All ?? new List<Screen>();
            }
            catch (Exception e)
            {
                Logger.Log($"Could not read the connected monitors: {e.Message}", true);
                return new List<Screen>();
            }
        }

        /// <summary>
        /// The monitor a restored window would land on, or null if it would land on none of them -
        /// a laptop undocked since the last session otherwise reopens the window on a monitor that
        /// is not there, with nothing on any screen to drag it back by.
        /// </summary>
        private static Screen ScreenAt(IReadOnlyList<Screen> screens, PixelRect window)
        {
            foreach (Screen screen in screens)
            {
                // An overlap test rather than containment, and against Bounds rather than the
                // working area, because plenty of perfectly good positions are inside neither:
                // Windows reports a window snapped to the left edge a few pixels into the negative,
                // its frame including an invisible resize border, a monitor left of the primary one
                // is at negative coordinates outright, and a window may straddle two screens or sit
                // under a taskbar docked along the top.
                PixelRect visible = screen.Bounds.Intersect(window);

                // Enough of it to see, and to grab hold of.
                if (visible.Width >= 160 && visible.Height >= 32)
                    return screen;
            }

            return null;
        }

        private void ApplySize(double w, double h, Screen screen)
        {
            if (screen != null)
            {
                // A window wider or taller than the monitor it opens on cannot be resized back by
                // dragging an edge that is off the screen.
                Size workingArea = screen.WorkingArea.Size.ToSize(Scaling);
                w = Math.Min(w, workingArea.Width);
                h = Math.Min(h, workingArea.Height);
            }

            Width = Math.Max(w, MinWidth);
            Height = Math.Max(h, MinHeight);
        }

        private void RestoreLogHeight()
        {
            double saved = Config.GetInt(Config.Key.LogHeight);

            if (saved < minLogHeight)
                return; // Nothing saved yet - the height the XAML gives the row stands

            // The window can have been restored smaller than the one the log was dragged in, and a
            // log taller than the window pushes the tabs out of it entirely.
            LogRow.Height = new GridLength(Math.Min(saved, Height * maxLogShare), GridUnitType.Pixel);
        }

        private void RestoreSelectedTab()
        {
            // Stored by name, so that the MainTab enum stays what its own comment says it is: the
            // one place the tab order lives. An index would quietly turn the order the tabs are
            // declared in into a config format, and inserting one would reopen everyone somewhere
            // else. Numbers written by an older build still parse, and anything unrecognised - a
            // tab since removed, an empty first-run value - leaves the File List selected.
            if (!Enum.TryParse(Config.Get(Config.Key.MainTab), out MainTab tab) || !Enum.IsDefined(tab))
                return;

            if (MainTabs.ItemCount > 0)
                MainTabs.SelectedIndex = ((int)tab).Clamp(0, MainTabs.ItemCount - 1);
        }

        /// <summary>
        /// Written in one go rather than a key at a time, so it is a single entry in the batch that
        /// SaveOnClose wraps around it. Deliberately not guarded on _initialized the way the
        /// settings saves are: all of this comes from the window itself rather than from controls
        /// that startup has to populate, so it is still true, and still worth keeping, after a
        /// startup that went wrong.
        /// </summary>
        public void SaveLayout()
        {
            var values = new Dictionary<string, string>();

            // Closing is late enough that every state notification has landed, so a window
            // being closed in its normal state can simply be measured. The snapshot kept by
            // TrackNormalBounds only has to cover closing while maximized or minimized.
            if (WindowState == WindowState.Normal)
            {
                _normalPos = Position;
                _normalSize = new Size(Width, Height);
                _normalKnown = true;
            }

            if (_normalKnown)
            {
                // FullScreen counts as maximized: there is no separate state to restore it to,
                // and reopening full-screen is closer to what was left behind than windowed.
                bool maximized = WindowState is WindowState.Maximized or WindowState.FullScreen;
                values[Config.Key.WindowGeometry.ToString()] =
                    $"{_normalPos.X},{_normalPos.Y},{(int)Math.Round(_normalSize.Width)},{(int)Math.Round(_normalSize.Height)},{maximized}";
            }

            // A minimized window lays out to nothing, and saving that would reopen with no log.
            if (LogRow.ActualHeight >= minLogHeight)
                values[Config.Key.LogHeight.ToString()] = ((int)Math.Round(LogRow.ActualHeight)).ToString();

            values[Config.Key.MainTab.ToString()] = ((MainTab)MainTabs.SelectedIndex).ToString();

            Config.Set(values);
        }
    }
}
