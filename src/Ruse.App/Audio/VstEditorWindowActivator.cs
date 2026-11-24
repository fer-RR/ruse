using System.Runtime.InteropServices;
using System.Text;

namespace Ruse.App.Audio;

internal static class VstEditorWindowActivator
{
    private const string EditorWindowClass = "OwnVst3EditorFrame";
    private const int SwShowNormal = 1;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpShowWindow = 0x0040;
    private const uint WmGetText = 0x000D;
    private const uint SmtoAbortIfHung = 0x0002;

    public static void Activate(string pluginName)
    {
        IntPtr match = Find(pluginName);
        if (match == IntPtr.Zero)
        {
            return;
        }

        ShowWindow(match, SwShowNormal);
        SetWindowPos(match, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
        SetForegroundWindow(match);
    }

    public static bool IsVisible(string pluginName)
    {
        IntPtr match = Find(pluginName);
        return match != IntPtr.Zero && IsWindowVisible(match);
    }

    private static IntPtr Find(string pluginName)
    {
        IntPtr match = IntPtr.Zero;
        IntPtr fallback = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            if (GetClassName(window) != EditorWindowClass)
            {
                return true;
            }

            if (fallback == IntPtr.Zero)
            {
                fallback = window;
            }

            if (!string.IsNullOrWhiteSpace(pluginName) &&
                GetWindowText(window).Contains(pluginName, StringComparison.OrdinalIgnoreCase))
            {
                match = window;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return match != IntPtr.Zero ? match : fallback;
    }

    private static string GetClassName(IntPtr window)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(window, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "";
    }

    private static string GetWindowText(IntPtr window)
    {
        var buffer = new StringBuilder(512);
        return SendMessageTimeout(
            window,
            WmGetText,
            new IntPtr(buffer.Capacity),
            buffer,
            SmtoAbortIfHung,
            50,
            out _) != IntPtr.Zero
            ? buffer.ToString()
            : "";
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder buffer, int capacity);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr window,
        uint message,
        IntPtr wParam,
        StringBuilder lParam,
        uint flags,
        uint timeout,
        out UIntPtr result);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
