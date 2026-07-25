using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using Nmkoder.IO;

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

                _manager.Show(new Notification(title, text, NotificationType.Information));
            }

            if (Dispatcher.UIThread.CheckAccess())
                Post();
            else
                Dispatcher.UIThread.Post(Post);
        }
    }
}
