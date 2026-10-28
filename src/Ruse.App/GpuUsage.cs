using System.Runtime.InteropServices;

namespace Ruse.App;

/// <summary>
/// Best-effort system GPU utilisation from the Windows "GPU Engine" counters.
/// The counters are optional, so every failure quietly disables the metric.
/// </summary>
internal static class GpuUsage
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhMoreData = 0x800007D2;
    private const string CounterPath = @"\GPU Engine(*)\Utilization Percentage";

    private static IntPtr _query;
    private static IntPtr _counter;
    private static bool _unavailable;
    private static long _lastSampleTick;
    private static double? _lastValue;

    public static double? Sample()
    {
        long now = Environment.TickCount64;
        if (now - _lastSampleTick < 1000)
        {
            return _lastValue;
        }

        _lastSampleTick = now;
        if (_unavailable)
        {
            return null;
        }

        try
        {
            _lastValue = Read();
            return _lastValue;
        }
        catch
        {
            _unavailable = true;
            _lastValue = null;
            return null;
        }
    }

    private static double? Read()
    {
        if (_query == IntPtr.Zero && PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0)
        {
            _unavailable = true;
            return null;
        }

        if (_counter == IntPtr.Zero &&
            PdhAddEnglishCounterW(_query, CounterPath, IntPtr.Zero, out _counter) != 0)
        {
            _unavailable = true;
            return null;
        }

        if (PdhCollectQueryData(_query) != 0)
        {
            return _lastValue;
        }

        uint size = 0;
        uint count;
        uint result = PdhGetFormattedCounterArrayW(
            _counter, PdhFmtDouble, ref size, out count, IntPtr.Zero);
        if (result != PdhMoreData || size == 0)
        {
            return _lastValue;
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            result = PdhGetFormattedCounterArrayW(
                _counter, PdhFmtDouble, ref size, out count, buffer);
            if (result != 0)
            {
                return _lastValue;
            }

            int itemSize = Marshal.SizeOf<CounterValueItem>();
            double busiest = 0;
            for (int index = 0; index < count; index++)
            {
                var item = Marshal.PtrToStructure<CounterValueItem>(
                    IntPtr.Add(buffer, index * itemSize));
                if (item.Value.Status == 0 && double.IsFinite(item.Value.DoubleValue))
                {
                    busiest = Math.Max(busiest, item.Value.DoubleValue);
                }
            }

            return Math.Clamp(busiest, 0, 100);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValue
    {
        public uint Status;
        public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValueItem
    {
        public IntPtr Name;
        public CounterValue Value;
    }

    [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(
        IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(
        IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);
}
