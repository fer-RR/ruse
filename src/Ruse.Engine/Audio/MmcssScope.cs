using System.Runtime.InteropServices;

namespace Ruse.App.Audio;

/// <summary>
/// Places the current thread in the Windows MMCSS "Pro Audio" class for the
/// lifetime of the scope. Audio threads get a protected CPU share, which keeps
/// small buffers stable under load. The scope is a no-op when AVRT is absent.
/// </summary>
internal sealed class MmcssScope : IDisposable
{
    private IntPtr _handle;

    private MmcssScope(IntPtr handle) => _handle = handle;

    public static MmcssScope Enter(string taskName = "Pro Audio")
    {
        uint taskIndex = 0;
        try
        {
            IntPtr handle = AvSetMmThreadCharacteristicsW(taskName, ref taskIndex);
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
}
