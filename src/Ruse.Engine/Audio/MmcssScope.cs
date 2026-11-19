using System.Runtime.InteropServices;

namespace Ruse.App.Audio;

internal enum AvrtPriority
{
    Low = -1,
    Normal = 0,
    High = 1,
    Critical = 2,
}

/// <summary>
/// Places the current thread in the Windows MMCSS "Pro Audio" class for the
/// lifetime of the scope. Audio threads get a protected CPU share, which keeps
/// small buffers stable under load. The scope is a no-op when AVRT is absent.
/// </summary>
internal sealed class MmcssScope : IDisposable
{
    private IntPtr _handle;

    private MmcssScope(IntPtr handle) => _handle = handle;

    public static MmcssScope Enter(string taskName = "Pro Audio", AvrtPriority priority = AvrtPriority.High)
    {
        uint taskIndex = 0;
        try
        {
            IntPtr handle = AvSetMmThreadCharacteristicsW(taskName, ref taskIndex);
            if (handle != IntPtr.Zero)
            {
                try
                {
                    AvSetMmThreadPriority(handle, (int)priority);
                }
                catch
                {
                }
            }

            return new MmcssScope(handle);
        }
        catch
        {
            return new MmcssScope(IntPtr.Zero);
        }
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            AvRevertMmThreadCharacteristics(_handle);
        }
        catch
        {
        }

        _handle = IntPtr.Zero;
    }

    [DllImport("avrt.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll")]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

    [DllImport("avrt.dll")]
    private static extern bool AvSetMmThreadPriority(IntPtr handle, int priority);
}
