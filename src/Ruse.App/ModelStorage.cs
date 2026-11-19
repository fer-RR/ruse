using System;
using System.IO;
using System.Linq;
using Ruse.App.Diagnostics;

namespace Ruse.App;

/// <summary>
/// Large model files live under LocalAppData. Older builds stored them under
/// Roaming AppData, so move or merge the entries once at startup.
/// </summary>
internal static class ModelStorage
{
    public static string Root
    {
        get
        {
            if (AppSettings.OverrideDirectory != null)
            {
                return Path.Combine(AppSettings.OverrideDirectory, "models");
            }

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(local)
                ? Path.Combine(AppSettings.Directory, "models")
                : Path.Combine(local, "Ruse", "models");
        }
    }

    private static string LegacyRoot => Path.Combine(AppSettings.Directory, "models");

    public static void Ensure()
    {
        string root;
        string legacy;
        try
        {
            root = Path.GetFullPath(Root);
            legacy = Path.GetFullPath(LegacyRoot);
            Directory.CreateDirectory(root);
        }
        catch (Exception ex)
        {
            AppLog.Warning("Could not create the model folder: " + ex.Message);
            return;
        }

        if (string.Equals(root, legacy, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(legacy))
        {
            return;
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(legacy))
        {
            MoveEntry(entry, Path.Combine(root, Path.GetFileName(entry)));
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(legacy).Any())
            {
                Directory.Delete(legacy);
            }
        }
        catch
        {
        }
    }

    private static void MoveEntry(string source, string destination)
    {
        if (Directory.Exists(source) && Directory.Exists(destination))
        {
            foreach (string child in Directory.EnumerateFileSystemEntries(source))
            {
                MoveEntry(child, Path.Combine(destination, Path.GetFileName(child)));
            }

            try
            {
                if (!Directory.EnumerateFileSystemEntries(source).Any())
                {
                    Directory.Delete(source);
                }
            }
            catch
            {
            }

            return;
        }

        if (File.Exists(destination) || Directory.Exists(destination))
        {
            return;
        }

        try
        {
            if (Directory.Exists(source))
            {
                Directory.Move(source, destination);
            }
            else
            {
                File.Move(source, destination);
            }
        }
        catch (Exception ex)
        {
            try
            {
                if (Directory.Exists(source))
                {
                    CopyDirectory(source, destination);
                    Directory.Delete(source, recursive: true);
                }
                else
                {
                    File.Copy(source, destination, overwrite: false);
                    File.Delete(source);
                }
            }
            catch (Exception copyError)
            {
                AppLog.Warning(
                    $"Could not move model path '{source}' to LocalAppData: {ex.Message}; copy failed: {copyError.Message}");
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        }

        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
