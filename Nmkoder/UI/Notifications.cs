using Avalonia.Threading;
using Nmkoder.OS;
using System.Threading.Tasks;

namespace Nmkoder.UI
{
    /// <summary>
    /// End-of-run notifications, which are the operating system's own and nothing else.
    ///
    /// There used to be an Avalonia WindowNotificationManager toast alongside the OS ping, carried
    /// over from the WinForms build's Tulpep.NotificationWindow. It was drawn inside the app's own
    /// window, and the only moment it ever fired was the one where that window is minimized or
    /// buried - so it was never once seen by the person it was for. The OS notification does the
    /// job on all three platforms now, and a toast nobody can see is not a fallback for it.
    /// </summary>
    public static class Notifications
    {
        /// <summary>
        /// Notifies only when the window is not in the foreground - whoever has the app focused is
        /// watching the log already. Safe to call from any thread, which the focus check itself is
        /// not: reading IsActive is only legal on the UI thread, and task cancellations arrive from
        /// process output reader threads.
        /// </summary>
        public static void ShowIfInBackground(string title, string text)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (Program.MainWin != null && Program.MainWin.IsInFocus())
                    return;

                Task.Run(() => OsUtils.ShowSystemNotification(title, text)); // Spawns a process on Linux/macOS - not worth blocking the UI thread for
            });
        }
    }
}
