using System.IO;

namespace Ruse.App;

internal static class DiskSpace
{
    public const long RecordingReserveBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Ensures the target volume has room for a write. Missing directories are
    /// created first so the check matches the path the writer will use.
    /// </summary>
    public static bool Ensure(string path, long requiredBytes, out string error)
    {
        error = "";
        try
        {
            string full = Path.GetFullPath(path);
            string directory = Directory.Exists(full)
                ? full
                : Path.GetDirectoryName(full) ?? "";
            if (string.IsNullOrWhiteSpace(directory))
            {
                return true;
            }

            Directory.CreateDirectory(directory);
            string? root = Path.GetPathRoot(directory);
            if (string.IsNullOrWhiteSpace(root))
            {
                return true;
            }

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                return true;
            }

            if (drive.AvailableFreeSpace < requiredBytes)
            {
                error = $"need {Format(requiredBytes)}, available {Format(drive.AvailableFreeSpace)}";
                return false;
            }
        }
        catch
        {
            // If the drive cannot be queried, let the writer surface its own
            // error rather than blocking a valid destination.
        }

        return true;
    }

    public static long PcmBytes(int sampleRate, int channels, int bitsPerSample, double seconds) =>
        (long)Math.Ceiling(
            Math.Max(0, sampleRate) *
            Math.Max(1, channels) *
            Math.Max(1, bitsPerSample) / 8.0 *
            Math.Max(0, seconds));

    public static string Format(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? $"{bytes / 1024.0 / 1024.0 / 1024.0:0.0} GB"
            : $"{Math.Max(0, bytes) / 1024.0 / 1024.0:0} MB";
}
