using System.IO;
using Ruse.App.Diagnostics;

namespace Ruse.App;

internal sealed record StorageFolderChangeResult(
    bool Ok,
    string Error,
    StorageLocations? Locations,
    string? Path,
    int Copied,
    int Skipped,
    int Failed);

internal static class StorageFolderService
{
    public const string Data = "data";
    public const string Models = "models";
    public const string User = "user";

    public static bool IsKnownKind(string kind) =>
        kind is Data or Models or User;

    public static string CurrentPath(string kind) => kind switch
    {
        Data => AppSettings.Directory,
        Models => ModelStorage.Root,
        User => RuseFolders.UserRoot,
        _ => "",
    };

    public static string DefaultPath(string kind) => kind switch
    {
        Data => AppSettings.DefaultDataDirectory,
        Models => ModelStorage.DefaultRoot,
        User => RuseFolders.DefaultUserRoot,
        _ => "",
    };

    public static StorageFolderChangeResult Change(string kind, string target, bool copyExisting)
    {
        if (!IsKnownKind(kind))
        {
            return Fail("Unknown storage folder.", kind);
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            return Fail("Choose a folder first.", kind);
        }

        string full;
        try
        {
            full = Path.GetFullPath(target.Trim());
        }
        catch
        {
            return Fail("The selected folder path is not valid.", kind);
        }

        if (!Path.IsPathFullyQualified(full))
        {
            return Fail("Choose a fully qualified folder path.", kind);
        }

        if (File.Exists(full))
        {
            return Fail("The selected path is a file, not a folder.", kind);
        }

        string source = CurrentPath(kind);
        if (string.Equals(Trim(full), Trim(source), StringComparison.OrdinalIgnoreCase))
        {
            return Fail("That folder is already in use.", kind);
        }

        if (copyExisting &&
            (IsInside(full, source) || IsInside(source, full)))
        {
            return Fail("Choose a folder that is not inside the current folder or its parent.", kind);
        }

        try
        {
            Directory.CreateDirectory(full);
            string probe = Path.Combine(full, ".ruse-write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            return Fail("Could not create or write to the selected folder: " + ex.Message, kind);
        }

        int copied = 0;
        int skipped = 0;
        int failed = 0;
        if (copyExisting)
        {
            copied = CopyExisting(source, full, kind, ref skipped, ref failed);
        }

        var locations = AppSettings.SnapshotStorageLocations();
        string? stored = string.Equals(Trim(full), Trim(DefaultPath(kind)), StringComparison.OrdinalIgnoreCase)
            ? null
            : full;

        switch (kind)
        {
            case Data:
                locations.DataDirectory = stored;
                break;
            case Models:
                locations.ModelsDirectory = stored;
                break;
            case User:
                locations.UserDirectory = stored;
                break;
        }

        if (!AppSettings.SaveStorageLocations(locations, out string error))
        {
            return Fail("The folder was prepared, but the location could not be saved: " + error, kind);
        }

        return new StorageFolderChangeResult(true, "", locations, full, copied, skipped, failed);
    }

    private static StorageFolderChangeResult Fail(string message, string kind) =>
        new(false, message, null, CurrentPath(kind), 0, 0, 0);

    private static int CopyExisting(string source, string target, string kind, ref int skipped, ref int failed)
    {
        if (!Directory.Exists(source))
        {
            return 0;
        }

        int copied = 0;
        string locationsPath = Path.GetFullPath(AppSettings.StorageLocationsPath);
        bool skipPointer = kind == Data &&
            string.Equals(Trim(source), Trim(AppSettings.BootstrapDirectory), StringComparison.OrdinalIgnoreCase);

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToArray();
        }
        catch (Exception ex)
        {
            AppLog.Warning("Could not enumerate storage files: " + ex.Message);
            failed++;
            return 0;
        }

        foreach (string file in files)
        {
            try
            {
                string fullFile = Path.GetFullPath(file);
                if (skipPointer &&
                    (string.Equals(fullFile, locationsPath, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(fullFile, locationsPath + ".bak", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(fullFile, locationsPath + ".tmp", StringComparison.OrdinalIgnoreCase)))
                {
                    skipped++;
                    continue;
                }

                string relative = Path.GetRelativePath(source, file);
                string destination = Path.Combine(target, relative);
                if (File.Exists(destination))
                {
                    skipped++;
                    continue;
                }

                string? directory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
                copied++;
            }
            catch (Exception ex)
            {
                failed++;
                AppLog.Warning($"Could not copy '{file}' to the new storage folder: {ex.Message}");
            }
        }

        return copied;
    }

    private static bool IsInside(string path, string root)
    {
        string child = Trim(path) + Path.DirectorySeparatorChar;
        string parent = Trim(root) + Path.DirectorySeparatorChar;
        return child.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    private static string Trim(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
