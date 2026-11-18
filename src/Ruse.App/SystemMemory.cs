using System.Runtime.InteropServices;

namespace Ruse.App;

internal static class SystemMemory
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    public static long AvailablePhysicalBytes
    {
        get
        {
            try
            {
                var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                return GlobalMemoryStatusEx(ref status)
                    ? (long)Math.Min(status.AvailPhys, long.MaxValue)
                    : 0;
            }
            catch
            {
                return 0;
            }
        }
    }
}
