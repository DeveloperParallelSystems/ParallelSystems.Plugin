using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ParallelSystemPlugin.UI
{
    public static class WindowCentering
    {
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        /// <summary>
        /// Centers a WPF window on a native HWND (Revit main window).
        /// </summary>
        public static void CenterOnOwnerHwnd(Window win, IntPtr ownerHwnd)
        {
            if (ownerHwnd == IntPtr.Zero) return;

            // Get owner window bounds in device pixels
            if (!GetWindowRect(ownerHwnd, out var r)) return;
            double ownerPxW = r.Right - r.Left;
            double ownerPxH = r.Bottom - r.Top;

            // Convert device pixels → WPF DIPs (for DPI scaling)
            var source = PresentationSource.FromVisual(win);
            Matrix fromDevice = source != null
                ? source.CompositionTarget.TransformFromDevice
                : Matrix.Identity;

            Point ownerTopLeftDip = fromDevice.Transform(new Point(r.Left, r.Top));
            Point ownerSizeDip = fromDevice.Transform(new Point(ownerPxW, ownerPxH));

            // Make sure the window has measured itself
            win.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = double.IsNaN(win.Width) ? win.DesiredSize.Width : win.Width;
            double h = double.IsNaN(win.Height) ? win.DesiredSize.Height : win.Height;

            // Position the window in the center of the owner
            win.Left = ownerTopLeftDip.X + (ownerSizeDip.X - w) / 2.0;
            win.Top = ownerTopLeftDip.Y + (ownerSizeDip.Y - h) / 2.0;
        }
    }
}
