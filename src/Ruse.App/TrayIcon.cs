using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ruse.App;

internal sealed record TrayLabels(
    string Open,
    string Exit,
    string StartRouting,
    string StopRouting,
    string MuteMics,
    string MicsMuted,
    string MuteOutputs,
    string UnmuteOutputs,
    string Mixer,
    string Timeline,
    string Recording,
    string Idle,
    string Routing);

internal readonly record struct TraySnapshot(
    bool Running,
    bool MicMuted,
    bool MasterMuted,
    bool Recording,
    double RecordingSeconds);

internal enum TrayVisualState
{
    Idle,
    Routing,
    Recording,
}

/// <summary>Native notification-area icon for the frameless WebView window.</summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;
    private const uint NiifInfo = 0x00000001;
    private const uint TrayCallback = 0x8000 + 1;
    private const uint WmNull = 0x0000;
    private const int WmLButtonUp = 0x0202;
    private const int WmDoubleClick = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const uint MfString = 0x00000000;
    private const uint MfChecked = 0x00000008;
    private const uint MfGrayed = 0x00000001;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmLeftAlign = 0x0000;
    private const uint TpmBottomAlign = 0x0020;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const int MenuOpen = 1;
    private const int MenuRouting = 2;
    private const int MenuMic = 3;
    private const int MenuMaster = 4;
    private const int MenuMixer = 5;
    private const int MenuTimeline = 6;
    private const int MenuRecording = 7;
    private const int MenuExit = 8;

    private static readonly int TaskbarCreated = RegisterWindowMessageW("TaskbarCreated");

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly HwndSourceHook _hook;
    private readonly TrayLabels _labels;
    private readonly IReadOnlyDictionary<TrayVisualState, IntPtr> _icons;
    private NotifyIconData _data;
    private TraySnapshot _snapshot;
    private TrayVisualState _visualState = TrayVisualState.Idle;
    private string _tooltip = "Ruse";

    public event Action? OpenRequested;
    public event Action? ExitRequested;
    public event Action? ToggleRoutingRequested;
    public event Action? ToggleMicRequested;
    public event Action? ToggleMasterRequested;
    public event Action? MixerRequested;
    public event Action? TimelineRequested;

    public TrayIcon(Window window, TrayLabels labels)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_hwnd)!;
        _labels = labels;
        _icons = TrayIconImages.Create();
        _hook = WndProc;
        _source.AddHook(_hook);

        _data = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NifMessage | NifTip | NifIcon,
            uCallbackMessage = TrayCallback,
            hIcon = _icons[TrayVisualState.Idle],
            szTip = _tooltip,
            szInfo = "",
            szInfoTitle = "",
        };
        AddIcon();
    }

    public void ShowNotification(string title, string text)
    {
        if (_icons.Count == 0) return;
        _data.cbSize = Marshal.SizeOf<NotifyIconData>();
        _data.hWnd = _hwnd;
        _data.uFlags = NifInfo;
        _data.szInfoTitle = Truncate(title, 63);
        _data.szInfo = Truncate(text, 255);
        _data.dwInfoFlags = NiifInfo;
        Shell_NotifyIconW(NimModify, ref _data);
    }

    public void Update(TraySnapshot snapshot)
    {
        _snapshot = snapshot;
        TrayVisualState state = snapshot.Recording
            ? TrayVisualState.Recording
            : snapshot.Running
                ? TrayVisualState.Routing
                : TrayVisualState.Idle;
        string suffix = snapshot.Recording
            ? $"{_labels.Recording} {FormatTime(snapshot.RecordingSeconds)}"
            : snapshot.Running ? _labels.Routing : _labels.Idle;
        string tooltip = $"Ruse \u2014 {suffix}";

        if (state == _visualState && tooltip == _tooltip)
        {
            return;
        }

        _visualState = state;
        _tooltip = tooltip;
        _data.cbSize = Marshal.SizeOf<NotifyIconData>();
        _data.hWnd = _hwnd;
        _data.uFlags = NifMessage | NifTip | NifIcon;
        _data.uCallbackMessage = TrayCallback;
        _data.hIcon = _icons[state];
        _data.szTip = Truncate(tooltip, 127);
        Shell_NotifyIconW(NimModify, ref _data);
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
            AppendMenuW(menu, MfString, (UIntPtr)MenuOpen, _labels.Open);
            AppendMenuW(menu, MfSeparator, UIntPtr.Zero, string.Empty);
            AppendMenuW(
                menu,
                MfString | (_snapshot.Running ? MfChecked : 0),
                (UIntPtr)MenuRouting,
                _snapshot.Running ? _labels.StopRouting : _labels.StartRouting);
            AppendMenuW(
                menu,
                MfString | (_snapshot.MicMuted ? MfChecked : 0),
                (UIntPtr)MenuMic,
                _snapshot.MicMuted ? _labels.MicsMuted : _labels.MuteMics);
            AppendMenuW(
                menu,
                MfString | (_snapshot.MasterMuted ? MfChecked : 0),
                (UIntPtr)MenuMaster,
                _snapshot.MasterMuted ? _labels.UnmuteOutputs : _labels.MuteOutputs);
            AppendMenuW(menu, MfSeparator, UIntPtr.Zero, string.Empty);
            AppendMenuW(menu, MfString, (UIntPtr)MenuMixer, _labels.Mixer);
            AppendMenuW(menu, MfString, (UIntPtr)MenuTimeline, _labels.Timeline);
            if (_snapshot.Recording)
            {
                AppendMenuW(
                    menu,
                    MfString | MfGrayed,
                    (UIntPtr)MenuRecording,
                    $"{_labels.Recording} {FormatTime(_snapshot.RecordingSeconds)}");
            }

            AppendMenuW(menu, MfSeparator, UIntPtr.Zero, string.Empty);
            AppendMenuW(menu, MfString, (UIntPtr)MenuExit, _labels.Exit);
            SetMenuDefaultItem(menu, MenuOpen, 0);

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

            switch (command)
            {
                case MenuOpen:
                    OpenRequested?.Invoke();
                    break;
                case MenuRouting:
                    ToggleRoutingRequested?.Invoke();
                    break;
                case MenuMic:
                    ToggleMicRequested?.Invoke();
                    break;
                case MenuMaster:
                    ToggleMasterRequested?.Invoke();
                    break;
                case MenuMixer:
                    MixerRequested?.Invoke();
                    break;
                case MenuTimeline:
                    TimelineRequested?.Invoke();
                    break;
                case MenuExit:
                    ExitRequested?.Invoke();
                    break;
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
        _data.uFlags = NifMessage | NifTip | NifIcon;
        _data.uCallbackMessage = TrayCallback;
        _data.hIcon = _icons[_visualState];
        _data.szTip = Truncate(_tooltip, 127);
        Shell_NotifyIconW(NimAdd, ref _data);
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length];

    private static string FormatTime(double seconds)
    {
        int total = Math.Max(0, (int)Math.Floor(seconds));
        return $"{total / 60:00}:{total % 60:00}";
    }

    public void Dispose()
    {
        Shell_NotifyIconW(NimDelete, ref _data);
        _source.RemoveHook(_hook);
        foreach (IntPtr icon in _icons.Values.Distinct())
        {
            if (icon != IntPtr.Zero)
            {
                DestroyIcon(icon);
            }
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

    [DllImport("user32.dll")]
    private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessageW(string message);
}

internal static class TrayIconImages
{
    private const int SmCxSmIcon = 49;

    public static IReadOnlyDictionary<TrayVisualState, IntPtr> Create()
    {
        int size = Math.Clamp(GetSystemMetrics(SmCxSmIcon), 16, 32);
        BitmapSource? baseIcon = LoadBaseIcon(size);
        IntPtr fallback = LoadFallbackIcon();
        var icons = new Dictionary<TrayVisualState, IntPtr>
        {
            [TrayVisualState.Idle] = SafeCreate(size, baseIcon, null, fallback),
            [TrayVisualState.Routing] = SafeCreate(size, baseIcon, Color.FromRgb(0x43, 0xE3, 0x9A), fallback),
            [TrayVisualState.Recording] = SafeCreate(size, baseIcon, Color.FromRgb(0xFF, 0x6B, 0x6B), fallback),
        };
        if (fallback != IntPtr.Zero)
        {
            DestroyIcon(fallback);
        }

        return icons;
    }

    private static IntPtr SafeCreate(int size, BitmapSource? baseIcon, Color? badge, IntPtr fallback)
    {
        try
        {
            IntPtr icon = CreateIcon(size, baseIcon, badge);
            if (icon != IntPtr.Zero)
            {
                return icon;
            }
        }
        catch
        {
        }

        return fallback == IntPtr.Zero ? IntPtr.Zero : CopyIcon(fallback);
    }

    private static IntPtr LoadFallbackIcon()
    {
        try
        {
            ExtractIconExW(Environment.ProcessPath ?? "", 0, out IntPtr large, out IntPtr small, 1);
            if (large != IntPtr.Zero)
            {
                if (small != IntPtr.Zero) DestroyIcon(small);
                return large;
            }

            return small;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static BitmapSource? LoadBaseIcon(int size)
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/ruse.ico", UriKind.Absolute);
            using Stream? stream = Application.GetResourceStream(uri)?.Stream;
            if (stream == null)
            {
                return null;
            }

            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            return decoder.Frames
                .OrderBy(frame => Math.Abs(frame.PixelWidth - size))
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static IntPtr CreateIcon(int size, BitmapSource? baseIcon, Color? badge)
    {
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            if (baseIcon != null)
            {
                context.DrawImage(baseIcon, new Rect(0, 0, size, size));
            }
            else
            {
                context.DrawRoundedRectangle(
                    new SolidColorBrush(Color.FromRgb(0x12, 0x18, 0x20)),
                    null,
                    new Rect(0, 0, size, size),
                    size * 0.2,
                    size * 0.2);
            }

            if (badge is { } color)
            {
                double radius = size * 0.27;
                double center = size - radius - 1;
                context.DrawEllipse(
                    new SolidColorBrush(Color.FromRgb(0x07, 0x0A, 0x0E)),
                    null,
                    new Point(center, center),
                    radius + Math.Max(1, size * 0.08),
                    radius + Math.Max(1, size * 0.08));
                context.DrawEllipse(
                    new SolidColorBrush(color),
                    null,
                    new Point(center, center),
                    radius,
                    radius);
            }
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[size * size * 4];
        converted.CopyPixels(pixels, size * 4, 0);
        return CreateHIcon(size, pixels);
    }

    private static IntPtr CreateHIcon(int size, byte[] pixels)
    {
        var info = new BitmapInfo
        {
            bmiHeader = new BitmapInfoHeader
            {
                biSize = Marshal.SizeOf<BitmapInfoHeader>(),
                biWidth = size,
                biHeight = -size,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
                biSizeImage = (uint)(size * size * 4),
            },
        };

        IntPtr dc = GetDC(IntPtr.Zero);
        IntPtr color = IntPtr.Zero;
        IntPtr mask = IntPtr.Zero;
        try
        {
            color = CreateDIBSection(dc, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (color == IntPtr.Zero || bits == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            Marshal.Copy(pixels, 0, bits, pixels.Length);
            int stride = ((size + 15) / 16) * 2;
            var maskBits = new byte[stride * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    if (pixels[(y * size + x) * 4 + 3] < 8)
                    {
                        maskBits[y * stride + x / 8] |= (byte)(1 << (7 - (x % 8)));
                    }
                }
            }

            IntPtr maskData = Marshal.AllocHGlobal(maskBits.Length);
            try
            {
                Marshal.Copy(maskBits, 0, maskData, maskBits.Length);
                mask = CreateBitmap(size, size, 1, 1, maskData);
            }
            finally
            {
                Marshal.FreeHGlobal(maskData);
            }
            var iconInfo = new IconInfo
            {
                fIcon = true,
                hbmMask = mask,
                hbmColor = color,
            };
            return CreateIconIndirect(ref iconInfo);
        }
        finally
        {
            if (mask != IntPtr.Zero) DeleteObject(mask);
            if (color != IntPtr.Zero) DeleteObject(color);
            if (dc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, dc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader bmiHeader;
        public uint bmiColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool fIcon;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BitmapInfo bitmapInfo, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, IntPtr bits);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref IconInfo iconInfo);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr CopyIcon(IntPtr icon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string file, int index, out IntPtr large, out IntPtr small, uint count);
}
