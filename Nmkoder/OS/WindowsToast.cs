using Nmkoder.IO;
using System;
using System.Runtime.CompilerServices;

#if WINDOWS
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
#endif

namespace Nmkoder.OS
{
    /// <summary>
    /// Real Windows notifications - the banner that lands in the notification centre - through the
    /// Windows App SDK. Unpackaged apps are supported: Register() writes the COM registration that
    /// lets Windows attribute the notification and reactivate the app, so there is no MSIX package,
    /// no AppUserModelID and no Start Menu shortcut anywhere in this. The older WinRT
    /// ToastNotificationManager is what needed those, which is why the taskbar flash used to be the
    /// whole of Windows' story here.
    ///
    /// Everything is best-effort and reports whether it got anywhere, because plenty can stop it
    /// that is not a bug: the App SDK runtime failing to come up, an elevated process (Microsoft
    /// does not support notifications from one), or a user who has turned notifications off.
    /// OsUtils falls back to flashing the taskbar whenever this says no.
    ///
    /// Compiled to no-ops on the plain net10.0 target framework, which is what Linux and macOS build.
    /// </summary>
    public static class WindowsToast
    {
#if WINDOWS
        private static bool _registered;

        /// <summary>
        /// Call once before the UI starts. Registering costs a registry key under HKCU that
        /// Unregister() takes back out again - worth knowing for an app that is meant to be portable.
        /// </summary>
        public static void Register()
        {
            try
            {
                RegisterCore();
                _registered = true;
            }
            catch (Exception e)
            {
                // Missing or unloadable App SDK binaries land here rather than killing startup.
                Logger.Log($"Windows notifications are unavailable, the taskbar will be flashed instead: {e.Message}", true);
            }
        }

        public static void Unregister()
        {
            if (!_registered)
                return;

            _registered = false;

            try
            {
                UnregisterCore();
            }
            catch (Exception e)
            {
                Logger.Log($"Failed to unregister Windows notifications: {e.Message}", true);
            }
        }

        /// <summary> True only when a notification was actually handed to the shell. </summary>
        public static bool TryShow(string title, string text)
        {
            if (!_registered)
                return false;

            try
            {
                return ShowCore(title, text);
            }
            catch (Exception e)
            {
                Logger.Log($"Failed to show a Windows notification: {e.Message}", true);
                return false;
            }
        }

        // The App SDK types are only touched from these three, and they are never inlined, so the
        // JIT resolving them can fail no earlier than the call itself - inside the try blocks above.
        // Inlined, a machine without a working App SDK would throw while compiling the caller, where
        // nothing is catching yet.

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RegisterCore()
        {
            // The docs are explicit that the handler goes on before Register(), not after.
            AppNotificationManager.Default.NotificationInvoked += OnInvoked;
            AppNotificationManager.Default.Register();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void UnregisterCore()
        {
            AppNotificationManager.Default.Unregister();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ShowCore(string title, string text)
        {
            AppNotification notification = new AppNotificationBuilder().AddText(title).AddText(text).BuildNotification();
            AppNotificationManager.Default.Show(notification);

            // Show() does not throw when the shell declines it - a refused notification comes back
            // with its Id still unset, and that is the only signal there is.
            return notification.Id != 0;
        }

        /// <summary>
        /// Clicking the notification brings the window back, which is the one useful thing it can
        /// do: the run it is reporting on has already finished. Raised on a background thread.
        /// </summary>
        private static void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (Program.MainWin == null)
                    return;

                // Activate() alone leaves a minimized window minimized.
                if (Program.MainWin.WindowState == WindowState.Minimized)
                    Program.MainWin.WindowState = WindowState.Normal;

                Program.MainWin.Activate();
            });
        }
#else
        public static void Register() { }

        public static void Unregister() { }

        public static bool TryShow(string title, string text) => false;
#endif
    }
}
