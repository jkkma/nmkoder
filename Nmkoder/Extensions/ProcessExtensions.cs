using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Nmkoder.Extensions
{
    /// <summary>
    /// Suspending/resuming a running encode. The original implementation walked the process'
    /// threads via OpenThread/SuspendThread, which is Windows-only; on Unix we send SIGSTOP/SIGCONT
    /// to the process instead, which is both simpler and more reliable.
    /// </summary>
    public static class ProcessExtensions
    {
        private const int SIGSTOP = 19;
        private const int SIGCONT = 18;

        [Flags]
        private enum ThreadAccess : int
        {
            SUSPEND_RESUME = 0x0002,
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenThread(ThreadAccess dwDesiredAccess, bool bInheritHandle, uint dwThreadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SuspendThread(IntPtr hThread);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int ResumeThread(IntPtr hThread);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("libc", SetLastError = true, EntryPoint = "kill")]
        private static extern int PosixKill(int pid, int sig);

        public static void Suspend(this Process process)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                SetWindowsThreadsSuspended(process, true);
            else
                PosixKill(process.Id, SIGSTOP);
        }

        public static void Resume(this Process process)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                SetWindowsThreadsSuspended(process, false);
            else
                PosixKill(process.Id, SIGCONT);
        }

        private static void SetWindowsThreadsSuspended(Process process, bool suspend)
        {
            foreach (ProcessThread thread in process.Threads)
            {
                IntPtr handle = OpenThread(ThreadAccess.SUSPEND_RESUME, false, (uint)thread.Id);

                if (handle == IntPtr.Zero)
                    break;

                try
                {
                    if (suspend)
                        SuspendThread(handle);
                    else
                        ResumeThread(handle);
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
        }
    }
}
