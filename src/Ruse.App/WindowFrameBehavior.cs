using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Ruse.App;

/// <summary>Keeps a frameless WebView2 window inside the monitor work area.</summary>
internal static class WindowFrameBehavior
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmNcHitTest = 0x0084;
    private const int MonitorDefaultToNearest = 0x00000002;
    private const int ResizeBorder = 6;

    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;

    public static void Attach(Window window)
    {
        if (PresentationSource.FromVisual(window) is HwndSource source)
        {
            source.AddHook((hwnd, msg, wParam, lParam, ref handled) =>
                WindowFrameProc(window, hwnd, msg, wParam, lParam, ref handled));
        }
    }

    private static IntPtr WindowFrameProc(
        Window window,
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (msg == WmNcHitTest)
        {
            int border = ResizeBorder;
            if (GetCursorPos(out NativePoint point) && ScreenToClient(hwnd, ref point) &&
                GetClientRect(hwnd, out NativeRect client))
            {
                bool left = point.X < border;
                bool right = point.X >= client.Right - border;
                bool top = point.Y < border;
                bool bottom = point.Y >= client.Bottom - border;

                int hit = -1;
                if (left && top) hit = HtTopLeft;
                else if (right && top) hit = HtTopRight;
                else if (left && bottom) hit = HtBottomLeft;
                else if (right && bottom) hit = HtBottomRight;
                else if (left) hit = HtLeft;
                else if (right) hit = HtRight;
                else if (top) hit = HtTop;
                else if (bottom) hit = HtBottom;

                if (hit >= 0)
                {
                    handled = true;
                    return (IntPtr)hit;
                }
            }
        }

        if (msg != WmGetMinMaxInfo)
        {
            return IntPtr.Zero;
        }

        IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return IntPtr.Zero;
        }

        var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        limits.MaxPosition.X = info.Work.Left - info.Monitor.Left;
        limits.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
        limits.MaxSize.X = info.Work.Right - info.Work.Left;
        limits.MaxSize.Y = info.Work.Bottom - info.Work.Top;

        // The frameless chrome path can otherwise lose WPF's own minimum
        // tracking size, letting the window shrink until the UI clips.
        double dpi = GetDpiForWindow(hwnd);
        if (dpi <= 0)
        {
            dpi = 96;
        }

        if (window.MinWidth > 0 && !double.IsNaN(window.MinWidth))
        {
            limits.MinTrackSize.X = Math.Min(
                limits.MaxSize.X, (int)Math.Ceiling(window.MinWidth * dpi / 96.0));
        }

        if (window.MinHeight > 0 && !double.IsNaN(window.MinHeight))
        {
            limits.MinTrackSize.Y = Math.Min(
                limits.MaxSize.Y, (int)Math.Ceiling(window.MinHeight * dpi / 96.0));
        }

        Marshal.StructureToPtr(limits, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public int Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hwnd, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
