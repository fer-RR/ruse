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
    int Failed,
    int Conflicts);

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
        var locations = AppSettings.SnapshotStorageLocations();

        string? overlap = FindOverlap(kind, full);
        if (overlap != null)
        {
            return Fail(overlap, kind);
        }

        bool sameAsSource = string.Equals(Trim(full), Trim(source), StringComparison.OrdinalIgnoreCase);
        bool targetIsDefault = string.Equals(Trim(full), Trim(DefaultPath(kind)), StringComparison.OrdinalIgnoreCase);
        bool resetFallback = sameAsSource && targetIsDefault;
        if (sameAsSource && !resetFallback)
        {
            return Fail("That folder is already in use.", kind);
        }

        if (copyExisting && !sameAsSource &&
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
        if (copyExisting && !sameAsSource)
        {
            CopyResult copy = CopyExisting(source, full, kind);
            copied = copy.Copied;
            skipped = copy.Skipped;
            failed = copy.Failed;
            if (copy.Conflicts > 0)
            {
                return new StorageFolderChangeResult(
                    false,
                    $"{copy.Conflicts} file{(copy.Conflicts == 1 ? "" : "s")} already exist in the selected folder. Choose an empty folder or turn off copying.",
                    null, full, copied, skipped, failed, copy.Conflicts);
            }

            if (copy.Failed > 0)
            {
                return new StorageFolderChangeResult(
                    false,
                    $"{copy.Failed} file{(copy.Failed == 1 ? "" : "s")} could not be copied. The storage location was not changed.",
                    null, full, copied, skipped, failed, 0);
            }
        }

        string? stored = targetIsDefault ? null : full;

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

        return new StorageFolderChangeResult(true, "", locations, full, copied, skipped, failed, 0);
    }

    private static StorageFolderChangeResult Fail(string message, string kind) =>
        new(false, message, null, CurrentPath(kind), 0, 0, 0, 0);

    private sealed record CopyResult(int Copied, int Skipped, int Failed, int Conflicts);

    private static CopyResult CopyExisting(string source, string target, string kind)
    {
        if (!Directory.Exists(source))
        {
            return new CopyResult(0, 0, 0, 0);
        }

        string locationsPath = Path.GetFullPath(AppSettings.StorageLocationsPath);
        bool skipPointer = kind == Data &&
            string.Equals(Trim(source), Trim(AppSettings.BootstrapDirectory), StringComparison.OrdinalIgnoreCase);

        string[] files;
        try
        {
            files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToArray();
        }
        catch (Exception ex)
        {
            AppLog.Warning("Could not enumerate storage files: " + ex.Message);
            return new CopyResult(0, 0, 1, 0);
        }

        int skipped = 0;
        int conflicts = 0;
        var plan = new List<(string Source, string Destination)>();
        foreach (string file in files)
        {
            string fullFile;
            try
            {
                fullFile = Path.GetFullPath(file);
            }
            catch
            {
                conflicts++;
                continue;
            }

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
            if (File.Exists(destination) || Directory.Exists(destination))
            {
                conflicts++;
                continue;
            }

            plan.Add((file, destination));
        }

        if (conflicts > 0)
        {
            return new CopyResult(0, skipped, 0, conflicts);
        }

        int copied = 0;
        int failed = 0;
        var copiedFiles = new List<string>();
        foreach (var item in plan)
        {
            try
            {
                string? directory = Path.GetDirectoryName(item.Destination);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                copiedFiles.Add(item.Destination);
                using var input = new FileStream(item.Source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var output = new FileStream(item.Destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
                copied++;
            }
            catch (Exception ex)
            {
                failed++;
                AppLog.Warning($"Could not copy '{item.Source}' to the new storage folder: {ex.Message}");
            }
        }

        if (failed > 0)
        {
            RemoveCopiedFiles(copiedFiles);
            return new CopyResult(0, skipped, failed, 0);
        }

        return new CopyResult(copied, skipped, failed, 0);
    }

    private static void RemoveCopiedFiles(IEnumerable<string> files)
    {
        foreach (string file in files)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warning($"Could not clean up the partial storage copy '{file}': {ex.Message}");
            }
        }
    }
    private static string? FindOverlap(string kind, string path)
    {
        string appRoot = Trim(AppContext.BaseDirectory);
        if (IsRootPath(path) ||
            SameOrInside(path, appRoot) ||
            SameOrInside(appRoot, path))
        {
            return "Choose a folder other than the app folder or a drive root.";
        }

        foreach (string other in new[] { Data, Models, User })
        {
            if (string.Equals(other, kind, StringComparison.Ordinal))
            {
                continue;
            }

            string root = CurrentPath(other);
            if (!string.IsNullOrWhiteSpace(root) &&
                (SameOrInside(path, root) || SameOrInside(root, path)))
            {
                return $"The selected folder overlaps the {other} storage folder.";
            }
        }

        return null;
    }

    private static bool IsRootPath(string path) =>
        string.Equals(Trim(path), Trim(Path.GetPathRoot(path) ?? path),
            StringComparison.OrdinalIgnoreCase);

    private static bool SameOrInside(string path, string root) =>
        string.Equals(Trim(path), Trim(root), StringComparison.OrdinalIgnoreCase) ||
        IsInside(path, root);

    private static bool IsInside(string path, string root)
    {
        string child = Trim(path) + Path.DirectorySeparatorChar;
        string parent = Trim(root) + Path.DirectorySeparatorChar;
        return child.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    private static string Trim(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
