using System;
using System.IO;
using System.Text;

namespace Ruse.App;

/// <summary>
/// Replaces a text file atomically: write a sibling <c>.tmp</c> file, flush it
/// to disk, then swap it in with one <see cref="File.Replace(string, string, string, bool)"/>
/// and keep the previous contents as <c>.bak</c>. A failed write leaves the
/// original file untouched.
/// </summary>
public static class AtomicFile
{
    public static bool TryWriteAllText(string path, string contents, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "No destination path.";
            return false;
        }

        string? temp = null;
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(temp, path, path + ".bak", ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, path);
            }

            temp = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (temp != null)
            {
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                    // The stale temp file is harmless; the original is intact.
                }
            }
        }
    }
}
