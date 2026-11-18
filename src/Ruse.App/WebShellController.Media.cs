using System.IO;
using System.Threading;
using System.Text.Json;
using Microsoft.Win32;
using Ruse.App.Audio;
using Ruse.App.Diagnostics;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".mp3", ".flac", ".aif", ".aiff", ".m4a", ".wma", ".ogg",
    };

    private static readonly EnumerationOptions MediaEnumeration = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        MaxRecursionDepth = 5,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
    };

    private void EnsureDefaultMediaFolders()
    {
        _settings.MediaFolders ??= new List<string>();
        var defaults = new List<string> { RuseFolders.Samples, RuseFolders.Recordings };
        if (Directory.Exists(RuseFolders.LegacySamples)) defaults.Add(RuseFolders.LegacySamples);
        if (Directory.Exists(RuseFolders.LegacyRecordings)) defaults.Add(RuseFolders.LegacyRecordings);


        foreach (string path in defaults)
        {
            if (_settings.MediaFolders.Any(item =>
                    string.Equals(item, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _settings.MediaFolders.Add(path);

        }

    }


    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, float[]> MediaWaveformCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> MediaWaveformPending = new();
    private static readonly SemaphoreSlim MediaWaveformGate = new(2);

    private async void RefreshMediaLibrary()
    {
        string[] folders = NormalizedMediaFolders();
        MediaListDto list;
        try
        {
            list = await Task.Run(() => BuildMediaList(folders));
        }
        catch (Exception ex)
        {
            list = new MediaListDto { Error = ex.Message };
        }

        Post("media-list", list);
    }

    private async void LoadMediaWaveform(string path)
    {
        if (!TrustedPath.TryNormalize(path, out string source) ||
            !File.Exists(source) ||
            !IsInsideMediaFolder(source))
        {
            return;
        }

        var info = new FileInfo(source);
        string key = $"{source}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        if (MediaWaveformCache.TryGetValue(key, out float[]? cached))
        {
            Post("media-waveform", new { path = source, peaks = cached });
            return;
        }

        if (!MediaWaveformPending.TryAdd(key, 0)) return;
        try
        {
            await MediaWaveformGate.WaitAsync();
            float[] peaks;
            try
            {
                peaks = await Task.Run(() => AudioFilePeaks.Read(
                    source, 64, 15.0, CancellationToken.None));
            }
            finally
            {
                MediaWaveformGate.Release();
            }

            if (MediaWaveformCache.Count > 1000) MediaWaveformCache.Clear();
            MediaWaveformCache[key] = peaks;
            Post("media-waveform", new { path = source, peaks });
        }
        catch
        {
            Post("media-waveform", new { path = source, peaks = Array.Empty<float>() });
        }
        finally
        {
            MediaWaveformPending.TryRemove(key, out _);
        }
    }

    private MediaListDto BuildMediaList(IReadOnlyList<string> folders)
    {
        var result = new MediaListDto();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in folders)
        {
            bool exists = Directory.Exists(folder);
            result.Folders.Add(new MediaFolderDto
            {
                Path = folder,
                Name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } name
                    ? name
                    : folder,
                Exists = exists,
            });

            if (!exists)
            {
                continue;
            }

            try
            {
                foreach (string path in Directory.EnumerateFiles(folder, "*", MediaEnumeration))
                {
                    if (result.Files.Count >= 5000) break;
                    if (!MediaExtensions.Contains(Path.GetExtension(path))) continue;
                    if (!seen.Add(path)) continue;
                    var info = new FileInfo(path);
                    if (!info.Exists) continue;
                    result.Files.Add(new MediaFileDto
                    {
                        Path = info.FullName,
                        Name = info.Name,
                        Folder = folder,
                        Extension = info.Extension.TrimStart('.').ToUpperInvariant(),
                        SizeBytes = info.Length,
                        ModifiedUtc = info.LastWriteTimeUtc,
                    });
                }
            }
            catch
            {
                // A folder can disappear or become inaccessible between scans.
            }
        }

        result.Files.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private string[] NormalizedMediaFolders()
    {
        var result = new List<string>();
        foreach (string? folder in _settings.MediaFolders ?? new List<string>())
        {
            if (!TrustedPath.TryNormalize(folder, out string full) ||
                result.Any(item => string.Equals(item, full, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            result.Add(full);
        }

        return result.ToArray();
    }

    private void AddMediaFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Add media folder",
            Multiselect = false,
        };

        if (dialog.ShowDialog(_window) != true ||
            !TrustedPath.TryNormalize(dialog.FolderName, out string folder) ||
            !Directory.Exists(folder))
        {
            return;
        }

        _settings.MediaFolders ??= new List<string>();
        if (!_settings.MediaFolders.Any(item => string.Equals(item, folder, StringComparison.OrdinalIgnoreCase)))
        {
            _settings.MediaFolders.Add(folder);
            _settings.Save();
        }

        SetStatus($"Added media folder: {folder}");
        RefreshMediaLibrary();
    }

    private void RemoveMediaFolder(string path)
    {
        if (!TrustedPath.TryNormalize(path, out string folder)) return;
        _settings.MediaFolders ??= new List<string>();
        int removed = _settings.MediaFolders.RemoveAll(item =>
            string.Equals(item, folder, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            _settings.Save();
            SetStatus($"Removed media folder: {folder}");
            RefreshMediaLibrary();
        }
    }

    private void ReorderMediaFolders(BridgeMessage message)
    {
        if (_settings.MediaFolders == null ||
            !message.Payload.TryGetProperty("paths", out JsonElement paths) ||
            paths.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var requested = new List<string>();
        foreach (JsonElement item in paths.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String &&
                TrustedPath.TryNormalize(item.GetString(), out string full))
            {
                requested.Add(full);
            }
        }

        var reordered = new List<string>();
        foreach (string path in requested)
        {
            string? original = _settings.MediaFolders.FirstOrDefault(item =>
                string.Equals(TrustedPath.NormalizeLocalFile(item), path, StringComparison.OrdinalIgnoreCase));
            if (original != null && !reordered.Contains(original, StringComparer.OrdinalIgnoreCase))
            {
                reordered.Add(original);
            }
        }

        foreach (string path in _settings.MediaFolders)
        {
            if (!reordered.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                reordered.Add(path);
            }
        }

        _settings.MediaFolders = reordered;
        _settings.Save();
        RefreshMediaLibrary();
    }

    private void OpenMediaFolder(string path)
    {
        if (!TrustedPath.TryNormalize(path, out string folder) || !Directory.Exists(folder)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{folder}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open the media folder: {ex.Message}", isError: true);
        }
    }

    private void RevealMediaFile(string path)
    {
        if (!TrustedPath.TryNormalize(path, out string full) || !File.Exists(full)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{full}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus($"Could not reveal the file: {ex.Message}", isError: true);
        }
    }

    private void AddMediaFileToTimeline(string path)
    {
        if (!TrustedPath.TryNormalize(path, out string full) || !File.Exists(full) || !IsInsideMediaFolder(full))
        {
            return;
        }

        EnsureTimeline();
        var track = _timeline?.Project.Tracks.FirstOrDefault() ?? AddTimelineTrack();
        if (track == null) return;
        AddTimelineClipPath(track.Id, full, Math.Max(0, _timeline?.PlayheadSample ?? 0));
    }

    private void RenameMediaFile(string path, string name)
    {
        if (!TrustedPath.TryNormalize(path, out string source) || !File.Exists(source) ||
            !IsInsideMediaFolder(source))
        {
            SetStatus("The media file is outside the project media folders.", isError: true);
            return;
        }

        string trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0 || trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            trimmed.Contains(Path.DirectorySeparatorChar) || trimmed.Contains(Path.AltDirectorySeparatorChar))
        {
            SetStatus("Enter a valid file name.", isError: true);
            return;
        }

        if (Path.GetExtension(trimmed).Length == 0)
        {
            trimmed += Path.GetExtension(source);
        }

        string directory = Path.GetDirectoryName(source) ?? "";
        string destination = Path.Combine(directory, trimmed);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (File.Exists(destination))
        {
            SetStatus("A file with that name already exists.", isError: true);
            return;
        }

        try
        {
            File.Move(source, destination);
        }
        catch (Exception ex)
        {
            SetStatus($"Could not rename the file: {ex.Message}", isError: true);
            return;
        }

        foreach (var entry in _nodes.Values)
        {
            if (entry.Source is { Kind: SourceKind.File } node &&
                string.Equals(node.FilePath, source, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    node.SetFile(destination, node.FileLoop);
                    PublishSourceState(entry.Id);
                }
                catch (Exception ex)
                {
                    AppLog.Warning($"Could not switch renamed file source '{entry.Id}': {ex.Message}");
                }
            }
        }

        _timeline?.RenameSourcePath(source, destination);
        if (_timeline != null)
        {
            PublishTimeline(false);
        }

        PublishGraph();
        SetStatus($"Renamed to {trimmed}.");
        RefreshMediaLibrary();
    }

    private bool IsInsideMediaFolder(string path)
    {
        foreach (string folder in NormalizedMediaFolders())
        {
            string relative = Path.GetRelativePath(folder, path);
            if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            {
                return true;
            }
        }

        return false;
    }
}
