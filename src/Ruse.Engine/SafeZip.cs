using System;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace Ruse.App;

public static class SafeZip
{
    public static void ExtractToDirectory(string archivePath, string destination, CancellationToken token)
    {
        Directory.CreateDirectory(destination);
        string root = Path.GetFullPath(destination);
        string rootPrefix = root + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(archivePath);
        var buffer = new byte[81920];
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            string key = entry.FullName.Replace('\\', '/');
            if (key.StartsWith('/') || key.Split('/').Contains(".."))
            {
                continue;
            }

            string path = Path.GetFullPath(Path.Combine(root, key));
            if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var input = entry.Open();
            using var output = File.Create(path);
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
            }
        }
    }
}

