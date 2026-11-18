using System;
using System.IO;

namespace Ruse.App.Audio;

/// <summary>
/// Shared guard for file paths that come from presets, sessions, templates or
/// timeline projects. Only local, fully-qualified files are trusted: relative,
/// UNC and device paths are rejected before anything tries to open them.
/// </summary>
public static class TrustedPath
{
    public static bool IsLocalFile(string? path) => TryNormalize(path, out _);

    public static string? NormalizeLocalFile(string? path) =>
        TryNormalize(path, out string full) ? full : null;

    public static bool TryNormalize(string? path, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        if (!Path.IsPathFullyQualified(path))
        {
            return false;
        }

        try
        {
            string full = Path.GetFullPath(path);
            if (string.IsNullOrWhiteSpace(full) ||
                !Path.IsPathFullyQualified(full) ||
                full.StartsWith(@"\\", StringComparison.Ordinal) ||
                full.StartsWith("//", StringComparison.Ordinal))
            {
                return false;
            }

            fullPath = full;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
