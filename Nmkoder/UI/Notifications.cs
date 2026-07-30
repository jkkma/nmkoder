using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using Nmkoder.IO;
using Nmkoder.OS;
using System;
using System.Threading.Tasks;

namespace Nmkoder.UI
{
    /// <summary>
    /// Toast notifications. Replaces Tulpep.NotificationWindow (WinForms-only) with Avalonia's
    /// built-in window notification manager.
    /// </summary>
    public static class Notifications
    {
        private static WindowNotificationManager _manager;

        public static void Attach(Window window)
        {
            _manager = new WindowNotificationManager(window)
            {
                Position = NotificationPosition.BottomRight,
                MaxItems = 3
            };
        }

        public static void Show(string title, string text)
        {
            void Post()
            {
                if (_manager == null)
                {
                    Logger.Log($"{title}: {text}", true);
                    return;
                }

                // TimeSpan.Zero = the toast stays until dismissed. These fire when nobody is
                // looking at the window, so one that expires on its own would be gone again by
                // the time the user comes back to see what happened.
                _manager.Show(new Notification(title, text, NotificationType.Information, TimeSpan.Zero));
            }

            if (Dispatcher.UIThread.CheckAccess())
                Post();
            else
                Dispatcher.UIThread.Post(Post);
        }

        /// <summary>
        /// Notifies only when the window is not in the foreground - whoever has the app focused is
        /// watching the log already. Shows the in-window toast and pings the OS (taskbar flash or
        /// desktop notification) so that even a minimized window gets noticed. Safe to call from
        /// any thread, which the focus check itself is not - reading IsActive is only legal on the
        /// UI thread, and task cancellations arrive from process output reader threads.
        /// </summary>
        public static void ShowIfInBackground(string title, string text)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (Program.MainWin != null && Program.MainWin.IsInFocus())
                    return;

                Show(title, text);
                Task.Run(() => OsUtils.ShowSystemNotification(title, text)); // Spawns a process on Linux/macOS - not worth blocking the UI thread for
            });
        }
    }
}
