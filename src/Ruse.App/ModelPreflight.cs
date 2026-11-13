using System.IO;

namespace Ruse.App;

internal static class ModelPreflight
{
    public static void Check(string directory, double sizeMb, double ramMultiplier = 2.0)
    {
        long downloadBytes = (long)Math.Ceiling(Math.Max(0, sizeMb) * 1024 * 1024);
        long required = downloadBytes + (64L * 1024 * 1024);

        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(directory));
            if (!string.IsNullOrWhiteSpace(root))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady && drive.AvailableFreeSpace < required)
                {
                    throw new IOException(
                        $"Not enough disk space for this model: need {Format(required)}, " +
                        $"available {Format(drive.AvailableFreeSpace)}.");
                }
            }
        }
        catch (IOException)
        {
            throw;
        }
        catch
        {
        }

        long memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (memory > 0 && downloadBytes * Math.Max(1, ramMultiplier) > memory * 0.85)
        {
            throw new InvalidOperationException(
                $"Not enough memory for this model: need about {Format(downloadBytes * 2)}, " +
                $"available {Format(memory)}.");
        }
    }

    private static string Format(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? $"{bytes / 1024.0 / 1024.0 / 1024.0:0.0} GB"
            : $"{bytes / 1024.0 / 1024.0:0} MB";
}
