using System.Diagnostics;
using System.IO;
using Ruse.App.Diagnostics;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    private static readonly HashSet<string> SampleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".mp3", ".flac", ".aif", ".aiff", ".m4a", ".aac", ".ogg", ".wma",
    };

    private static string SampleDirectory()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        if (string.IsNullOrWhiteSpace(root))
        {
            root = AppSettings.Directory;
        }

        return Path.Combine(root, "Ruse Samples");
    }

    private void RefreshSamples()
    {
        try
        {
            string directory = SampleDirectory();
            Directory.CreateDirectory(directory);
            _catalog.SampleDirectory = directory;
            _catalog.Samples = Directory.EnumerateFiles(directory)
                .Where(IsSampleAudio)
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .Select(path => new CatalogItemDto
                {
                    Id = path,
                    Kind = "sample",
                    Name = Path.GetFileNameWithoutExtension(path),
                    Subtitle = "Audio sample",
                    Details = path,
                    FilePath = path,
                })
                .ToList();
        }
        catch (Exception ex)
        {
            AppLog.Warning("Could not refresh samples: " + ex.Message);
            _catalog.Samples = new List<CatalogItemDto>();
        }
    }

    private static bool IsSampleAudio(string path) =>
        SampleExtensions.Contains(Path.GetExtension(path));

    private void OpenSamplesFolder()
    {
        try
        {
            string directory = SampleDirectory();
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{directory}\"",
                UseShellExecute = true,
            });
            SetStatus("Opened the samples folder.");
        }
        catch (Exception ex)
        {
            SetStatus("Could not open the samples folder: " + ex.Message, isError: true);
        }
    }

    private void ChooseSamples()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add samples",
            Filter = "Audio samples|*.wav;*.mp3;*.flac;*.aif;*.aiff;*.m4a;*.aac;*.ogg;*.wma|All files|*.*",
            CheckFileExists = true,
            Multiselect = true,
        };
        if (dialog.ShowDialog(_window) != true)
        {
            return;
        }

        ImportSamples(dialog.FileNames);
    }

    private void ImportSamplePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        ImportSamples(new[] { path });
    }

    private void ImportSamples(IEnumerable<string> paths)
    {
        string directory = SampleDirectory();
        Directory.CreateDirectory(directory);
        int imported = 0;
        int skipped = 0;
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !IsSampleAudio(path))
            {
                skipped++;
                continue;
            }

            string target = Path.Combine(directory, Path.GetFileName(path));
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }

            try
            {
                target = UniqueSamplePath(target);
                File.Copy(path, target, overwrite: false);
                imported++;
            }
            catch (Exception ex)
            {
                AppLog.Warning($"Could not import sample '{path}': {ex.Message}");
                skipped++;
            }
        }

        RefreshSamples();
        Post("catalog", _catalog);
        if (imported > 0)
        {
            SetStatus($"Added {imported} sample{(imported == 1 ? "" : "s")} to {directory}.");
        }
        else
        {
            SetStatus(skipped > 0 ? "No supported audio files were added." : "No samples were selected.");
        }
    }

    private static string UniqueSamplePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        string directory = Path.GetDirectoryName(path) ?? "";
        string stem = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        for (int suffix = 2; ; suffix++)
        {
            string candidate = Path.Combine(directory, $"{stem}-{suffix}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
