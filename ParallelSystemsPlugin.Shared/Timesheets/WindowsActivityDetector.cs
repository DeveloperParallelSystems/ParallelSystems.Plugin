using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ParallelSystemsPlugin.Timesheets
{
    internal static class WindowsActivityDetector
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct LastInputInfo
        {
            public uint Size;
            public uint Time;
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LastInputInfo info);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        public static bool IsRevitForeground()
        {
            try
            {
                var window = GetForegroundWindow();
                if (window == IntPtr.Zero) return false;

                uint processId;
                GetWindowThreadProcessId(window, out processId);
                return processId == (uint)Process.GetCurrentProcess().Id;
            }
            catch
            {
                return false;
            }
        }

        public static TimeSpan GetSystemIdleTime()
        {
            try
            {
                var info = new LastInputInfo { Size = (uint)Marshal.SizeOf(typeof(LastInputInfo)) };
                if (!GetLastInputInfo(ref info)) return TimeSpan.MaxValue;

                // Environment.TickCount wraps roughly every 24.9 days. Unsigned subtraction handles the wrap.
                var current = unchecked((uint)Environment.TickCount);
                var elapsed = unchecked(current - info.Time);
                return TimeSpan.FromMilliseconds(elapsed);
            }
            catch
            {
                return TimeSpan.MaxValue;
            }
        }
    }
}
