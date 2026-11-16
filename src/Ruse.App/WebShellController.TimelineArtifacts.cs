using System.IO;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    private readonly HashSet<string> _generatedTimelineFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _savedTimelineFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a WAV written by an offline timeline edit. Only files created
    /// in this session are tracked, so cleanup never touches older sessions.
    /// </summary>
    private void RegisterGeneratedTimelineFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _generatedTimelineFiles.Add(NormalizeTimelineArtifactPath(path));
    }

    /// <summary>
    /// Marks the currently generated files as referenced by a saved session or
    /// preset. They are kept even if the clip is later removed or undone.
    /// </summary>
    private void MarkGeneratedTimelineFilesSaved()
    {
        if (_timeline == null || _generatedTimelineFiles.Count == 0)
        {
            return;
        }

        HashSet<string> referenced = _timeline.ReferencedSourcePaths(includeHistory: false);
        foreach (string path in _generatedTimelineFiles)
        {
            if (referenced.Contains(path))
            {
                _savedTimelineFiles.Add(path);
            }
        }
    }

    /// <summary>
    /// Deletes generated WAVs that the live project, undo history and saved
    /// sessions no longer reference. Call after clip/track removal or undo/redo.
    /// </summary>
    private void PruneGeneratedTimelineFiles(bool includeHistory = true)
    {
        if (_timeline == null || _generatedTimelineFiles.Count == 0)
        {
            return;
        }

        HashSet<string> referenced = _timeline.ReferencedSourcePaths(includeHistory);
        foreach (string path in _generatedTimelineFiles.ToArray())
        {
            if (_savedTimelineFiles.Contains(path) || referenced.Contains(path))
            {
                continue;
            }

            _generatedTimelineFiles.Remove(path);
            TryDeleteTimelineFile(path);
        }
    }

    private static string NormalizeTimelineArtifactPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private static void TryDeleteTimelineFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Ruse.App.Diagnostics.AppLog.Warning(
                $"Could not remove the generated timeline file '{path}': {ex.Message}");
        }
    }
}
