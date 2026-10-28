using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Ruse.App;

/// <summary>Starts a native caption drag, which also restores a maximized window.</summary>
internal static class WindowDrag
{
    private const int WmNcLButtonDown = 0x00A1;
    private const int HtCaption = 0x0002;

    public static void Begin(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        ReleaseCapture();
        SendMessage(handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
