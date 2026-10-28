using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Ruse.App;

/// <summary>Native notification-area icon for the frameless WebView window.</summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint NimAdd = 0;
    private const uint NimDelete = 2;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint TrayCallback = 0x8000 + 1;
    private const uint WmNull = 0x0000;
    private const int WmLButtonUp = 0x0202;
    private const int WmDoubleClick = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const uint MfString = 0x00000000;
    private const uint TpmLeftAlign = 0x0000;
    private const uint TpmBottomAlign = 0x0020;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const int MenuOpen = 1;
    private const int MenuExit = 2;

    private static readonly int TaskbarCreated = RegisterWindowMessageW("TaskbarCreated");

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly HwndSourceHook _hook;
    private readonly IntPtr _icon;
    private readonly string _openText;
    private readonly string _exitText;
    private NotifyIconData _data;

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public TrayIcon(Window window, string openText, string exitText)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_hwnd)!;
        _openText = openText;
        _exitText = exitText;
        _icon = LoadIcon();
        _hook = WndProc;
        _source.AddHook(_hook);

        _data = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NifMessage | NifTip | (_icon != IntPtr.Zero ? NifIcon : 0),
            uCallbackMessage = TrayCallback,
            hIcon = _icon,
            szTip = "Ruse — Audio Router",
            szInfo = "",
            szInfoTitle = "",
        };
        AddIcon();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (TaskbarCreated != 0 && msg == TaskbarCreated)
        {
            AddIcon();
            handled = true;
            return IntPtr.Zero;
        }

        if (msg != (int)TrayCallback)
        {
            return IntPtr.Zero;
        }

        int mouseMessage = (int)(lParam.ToInt64() & 0xFFFF);
        if (mouseMessage is WmLButtonUp or WmDoubleClick)
        {
            OpenRequested?.Invoke();
            handled = true;
        }
        else if (mouseMessage == WmRButtonUp)
        {
            ShowMenu();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            AppendMenuW(menu, MfString, (UIntPtr)MenuOpen, _openText);
            AppendMenuW(menu, MfString, (UIntPtr)MenuExit, _exitText);
            GetCursorPos(out NativePoint point);
            SetForegroundWindow(_hwnd);
            int command = TrackPopupMenu(
                menu,
                TpmLeftAlign | TpmBottomAlign | TpmRightButton | TpmReturnCmd,
                point.X,
                point.Y,
                0,
                _hwnd,
                IntPtr.Zero);
            PostMessageW(_hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);

            if (command == MenuOpen)
            {
                OpenRequested?.Invoke();
            }
            else if (command == MenuExit)
            {
                ExitRequested?.Invoke();
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void AddIcon()
    {
        _data.cbSize = Marshal.SizeOf<NotifyIconData>();
        _data.hWnd = _hwnd;
        _data.uCallbackMessage = TrayCallback;
        Shell_NotifyIconW(NimAdd, ref _data);
    }

    private static IntPtr LoadIcon()
    {
        try
        {
            ExtractIconExW(Environment.ProcessPath ?? "", 0, out IntPtr large, out IntPtr small, 1);
            if (large != IntPtr.Zero)
            {
                if (small != IntPtr.Zero)
                {
                    DestroyIcon(small);
                }

                return large;
            }

            return small;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        Shell_NotifyIconW(NimDelete, ref _data);
        _source.RemoveHook(_hook);
        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconExW(
        string file, int iconIndex, out IntPtr large, out IntPtr small, uint iconCount);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr itemId, string text);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(
        IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessageW(string message);
}
